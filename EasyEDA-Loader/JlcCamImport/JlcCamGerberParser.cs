using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace EasyEDA_Loader
{
    internal static class JlcCamGerberParser
    {
        // Production copper layers can legitimately contain more than 300k commands.
        // Keep a ceiling to protect Altium while accepting the supplied JLC package.
        private const int MaxCommands = 500000;
        private static readonly Regex ApertureRegex = new Regex(@"^ADD(?<code>\d+)(?<shape>[A-Z]),(?<values>[0-9.+\-Xx]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CoordinateRegex = new Regex(@"(?<key>[XYIJ])(?<value>[+\-]?\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static JlcCamGerberFile Parse(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Gerber file was not found.", path);
            return Parse(File.ReadAllText(path), path);
        }

        public static JlcCamGerberFile Parse(string text, string path)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            // JLC top/bottom copper commonly contains large AM definitions for ordinary
            // component pads.  Rail fiducials use circular apertures; retain those and
            // skip macro bodies rather than rejecting the entire copper layer.
            text = Regex.Replace(text, @"(?is)%AM.*?%", string.Empty);
            var result = new JlcCamGerberFile { Path = path };
            var apertures = new Dictionary<int, JlcCamAperture>();
            bool inches = false, unitsSpecified = false, trailingZero = false, absolute = true, clockwise = false, inRegion = false, darkPolarity = true;
            int xInt = 2, xDec = 4, yInt = 2, yDec = 4, currentAperture = -1, operation = 2, interpolation = 1, depth = 0;
            JlcCamPoint current = new JlcCamPoint(0, 0);
            int commandCount = 0;
            foreach (string raw in Tokenize(text))
            {
                if (++commandCount > MaxCommands) throw new InvalidDataException("Gerber command limit (" + MaxCommands + ") exceeded in " + path + ".");
                string command = raw.Trim().ToUpperInvariant();
                if (command.Length == 0 || command == "M02") continue;
                if (command.StartsWith("G04")) { int parsed; if (TryDepth(command, out parsed)) depth = parsed; continue; }
                if (command.StartsWith("FS"))
                {
                    if (command.IndexOf('I') >= 0) throw new InvalidDataException("Incremental Gerber coordinates are unsupported: " + path);
                    absolute = command.IndexOf('A') >= 0;
                    trailingZero = command.IndexOf('T') >= 0;
                    Match format = Regex.Match(command, @"X(\d)(\d)Y(\d)(\d)");
                    if (!format.Success) throw new InvalidDataException("Invalid FS format command: " + command + " in " + path);
                    xInt = int.Parse(format.Groups[1].Value); xDec = int.Parse(format.Groups[2].Value);
                    yInt = int.Parse(format.Groups[3].Value); yDec = int.Parse(format.Groups[4].Value); continue;
                }
                if (command == "MOIN") { inches = true; unitsSpecified = true; result.Units = "inch"; continue; }
                if (command == "MOMM") { inches = false; unitsSpecified = true; result.Units = "mm"; continue; }
                if (command.StartsWith("ADD")) { AddAperture(command, apertures, inches, path); continue; }
                if (command.StartsWith("SR")) throw new InvalidDataException("Unsupported Gerber construct '" + command.Substring(0, Math.Min(3, command.Length)) + "' in " + path);
                if (command.StartsWith("G36")) { inRegion = true; continue; }
                if (command.StartsWith("G37")) { inRegion = false; continue; }
                if (command.StartsWith("G01") || command.StartsWith("G1")) { interpolation = 1; command = command.StartsWith("G01") ? command.Substring(3) : command.Substring(2); if (command.Length == 0) continue; }
                if (command.StartsWith("G02") || command.StartsWith("G2")) { interpolation = 2; clockwise = true; command = command.StartsWith("G02") ? command.Substring(3) : command.Substring(2); if (command.Length == 0) continue; }
                if (command.StartsWith("G03") || command.StartsWith("G3")) { interpolation = 3; clockwise = false; command = command.StartsWith("G03") ? command.Substring(3) : command.Substring(2); if (command.Length == 0) continue; }
                if (command == "G74" || command == "G75") continue;
                if (command == "LPD") { darkPolarity = true; continue; }
                if (command == "LPC") { darkPolarity = false; continue; }
                if (command.StartsWith("D") && int.TryParse(command.Substring(1), out int selected) && selected >= 10) { currentAperture = selected; continue; }
                if (!command.Contains("X") && !command.Contains("Y") && !command.Contains("I") && !command.Contains("J")) continue;
                if (inRegion) continue;
                if (!unitsSpecified) throw new InvalidDataException("Gerber units are missing before coordinate data: " + path);
                if (!absolute) throw new InvalidDataException("Incremental Gerber coordinates are unsupported: " + path);
                int suffixD = ExtractOperation(command, operation); if (suffixD >= 0) operation = suffixD;
                double x = current.X, y = current.Y, i = 0, j = 0; bool hasI = false, hasJ = false;
                foreach (Match match in CoordinateRegex.Matches(command))
                {
                    char key = match.Groups["key"].Value[0];
                    double value = ParseCoordinate(match.Groups["value"].Value, key == 'X' || key == 'I' ? xInt : yInt, key == 'X' || key == 'I' ? xDec : yDec, trailingZero, inches);
                    if (key == 'X') x = value; else if (key == 'Y') y = value; else if (key == 'I') { i = value; hasI = true; } else { j = value; hasJ = true; }
                }
                var target = new JlcCamPoint(x, y);
                if (operation == 3)
                {
                    if (!apertures.TryGetValue(currentAperture, out JlcCamAperture aperture)) throw new InvalidDataException("Flash has no selected aperture in " + path);
                    if (darkPolarity) result.Flashes.Add(new JlcCamFlash { Center = target, Aperture = aperture, Depth = depth, SourceFile = path }); current = target; continue;
                }
                if (operation == 1)
                {
                    JlcCamSegment segment = new JlcCamSegment { Kind = interpolation == 1 ? JlcCamSegmentKind.Line : JlcCamSegmentKind.Arc, Start = current, End = target, Depth = depth, Clockwise = clockwise };
                    if (segment.Kind == JlcCamSegmentKind.Arc)
                    {
                        if (!hasI && !hasJ) throw new InvalidDataException("Arc missing I/J centre offset in " + path);
                        segment.Center = new JlcCamPoint(current.X + i, current.Y + j);
                    }
                    result.Segments.Add(segment);
                }
                current = target;
            }
            if (!unitsSpecified) throw new InvalidDataException("Gerber units were not specified: " + path);
            return result;
        }

        private static IEnumerable<string> Tokenize(string text)
        {
            foreach (string item in Regex.Split(text.Replace("\r", "").Replace("\n", ""), "[\\*%]")) yield return item;
        }
        private static void AddAperture(string command, Dictionary<int, JlcCamAperture> apertures, bool inches, string path)
        {
            Match match = ApertureRegex.Match(command);
            // Macro apertures are intentionally non-circular and cannot be mistaken
            // for the circular rail fiducials that this importer recognizes.
            if (!match.Success && Regex.IsMatch(command, @"^ADD\d+A[A-Z0-9_.$+-]*$", RegexOptions.CultureInvariant))
            {
                Match macro = Regex.Match(command, @"^ADD(\d+)A", RegexOptions.CultureInvariant);
                apertures[int.Parse(macro.Groups[1].Value, CultureInfo.InvariantCulture)] = new JlcCamAperture { Code = int.Parse(macro.Groups[1].Value, CultureInfo.InvariantCulture), Shape = "A" };
                return;
            }
            if (!match.Success) throw new InvalidDataException("Unsupported aperture definition '" + command + "' in " + path);
            string[] values = match.Groups["values"].Value.Split(new[] { 'X', 'x' });
            double x = double.Parse(values[0], CultureInfo.InvariantCulture) * (inches ? 25.4 : 1.0);
            double y = values.Length > 1 ? double.Parse(values[1], CultureInfo.InvariantCulture) * (inches ? 25.4 : 1.0) : x;
            apertures[int.Parse(match.Groups["code"].Value, CultureInfo.InvariantCulture)] = new JlcCamAperture { Code = int.Parse(match.Groups["code"].Value, CultureInfo.InvariantCulture), Shape = match.Groups["shape"].Value, XSize = x, YSize = y };
        }
        private static int ExtractOperation(string command, int current) { Match match = Regex.Match(command, @"D0?([123])$"); return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : current; }
        private static double ParseCoordinate(string value, int whole, int decimals, bool trailingZero, bool inches)
        {
            double number;
            if (value.IndexOf('.') >= 0) number = double.Parse(value, CultureInfo.InvariantCulture);
            else
            {
                bool negative = value.StartsWith("-", StringComparison.Ordinal); string digits = value.TrimStart('+', '-');
                if (digits.Length > whole + decimals) throw new InvalidDataException("Gerber coordinate exceeds FS precision: " + value);
                // Leading-zero omission restores omitted most-significant zeros on the
                // left; trailing-zero omission restores fractional zeros on the right.
                if (trailingZero) digits = digits.PadRight(whole + decimals, '0'); else digits = digits.PadLeft(whole + decimals, '0');
                number = long.Parse(digits, CultureInfo.InvariantCulture) / Math.Pow(10, decimals); if (negative) number = -number;
            }
            if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > 100000) throw new InvalidDataException("Invalid Gerber coordinate: " + value);
            return inches ? number * 25.4 : number;
        }
        private static bool TryDepth(string command, out int depth) { Match m = Regex.Match(command, @"DEPTH\s+(\d+)"); return int.TryParse(m.Success ? m.Groups[1].Value : "", out depth); }
    }
}
