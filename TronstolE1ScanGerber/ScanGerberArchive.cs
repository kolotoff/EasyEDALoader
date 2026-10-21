using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyEDA_Loader.TronstolE1ScanGerber
{
    internal static class ScanGerberArchive
    {
        internal const long MaxFileBytes = 32L * 1024 * 1024;
        internal const long MaxArchiveBytes = 256L * 1024 * 1024;
        internal const string EmptyGerber = "G04 Empty layer*\n%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.01*%\nM02*\n";
        private static readonly string[] LayerExtensions = { ".gtl", ".gbl", ".gts", ".gbs", ".gto", ".gbo" };

        public static void Create(string staging, string outputFile)
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string[] gerbers = Directory.GetFiles(Path.Combine(staging, "gerber"), "*", SearchOption.AllDirectories);
            foreach (string extension in LayerExtensions)
            {
                string[] matches = gerbers.Where(p => string.Equals(Path.GetExtension(p), extension, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException("Expected exactly one " + extension + " layer; found " + matches.Length + ".");
                files.Add("board" + extension, NormalizeGerber(ReadAscii(matches[0]), extension));
            }
            files.Add("board.gko", NormalizeGerber(ReadAscii(Path.Combine(staging, "outline.gko")), ".gko"));

            // Native NC Drill also produces reports and tool lists. Only M48 programs
            // belong in the archive. Never include both combined and split copies.
            var drills = new List<KeyValuePair<string, string>>();
            foreach (string path in Directory.GetFiles(Path.Combine(staging, "drill"), "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(path);
                if (!ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".drl", StringComparison.OrdinalIgnoreCase)) continue;
                string text = ReadAscii(path);
                if (text.TrimStart().StartsWith("M48", StringComparison.Ordinal))
                    drills.Add(new KeyValuePair<string, string>(path, NormalizeDrill(text)));
                else if (ext.Equals(".drl", StringComparison.OrdinalIgnoreCase) && text.Contains("%FS"))
                    drills.Add(new KeyValuePair<string, string>(path, NormalizeGerber(text, ".drl")));
            }
            if (drills.Count == 1) files.Add("board.drl", drills[0].Value);
            else if (drills.Count == 2)
            {
                var slots = drills.Where(d => Path.GetFileNameWithoutExtension(d.Key).IndexOf("SlotHoles", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                var round = drills.Except(slots).ToArray();
                if (slots.Length != 1 || round.Length != 1
                    || Regex.IsMatch(round[0].Value, @"(?m)^(?:.*G85|M15|G0[123])"))
                    throw new InvalidDataException("Ambiguous drill outputs; expected complementary round-hole and slot-hole programs.");
                files.Add("board-RoundHoles.drl", round[0].Value);
                files.Add("board-SlotHoles.drl", slots[0].Value);
            }
            else throw new InvalidDataException("Expected one combined drill program or a round/slot pair; found " + drills.Count + ".");

            long total = 0;
            foreach (var file in files)
            {
                int size = Encoding.ASCII.GetByteCount(file.Value);
                if (size > MaxFileBytes) throw new InvalidDataException(file.Key + " exceeds the 32 MiB file limit.");
                total += size;
            }
            if (total > MaxArchiveBytes) throw new InvalidDataException("Scan export exceeds the 256 MiB uncompressed limit.");
            string absoluteOutput = Path.GetFullPath(outputFile);
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutput));
            string temporary = absoluteOutput + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                    foreach (var file in files)
                    {
                        ZipArchiveEntry entry = zip.CreateEntry(file.Key, CompressionLevel.Optimal);
                        using (var writer = new StreamWriter(entry.Open(), Encoding.ASCII)) writer.Write(file.Value);
                    }
                if (new FileInfo(temporary).Length > MaxArchiveBytes)
                    throw new InvalidDataException("Scan ZIP exceeds the 256 MiB archive limit.");
                File.Move(temporary, absoluteOutput, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string ReadAscii(string path)
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException(path + " exceeds the 32 MiB file limit.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Any(b => b > 127 || b == 0)) throw new InvalidDataException("Fabrication files must be ASCII: " + path);
            return Encoding.ASCII.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        internal static string NormalizeGerber(string text, string extension)
        {
            Match format = Regex.Match(text, @"%FSLAX(?<x>[1-6][1-6])Y(?<y>[1-6][1-6])\*%");
            if (!format.Success || format.Groups["x"].Value != format.Groups["y"].Value)
                throw new InvalidDataException("Gerber must use explicit, equal X/Y absolute leading-zero formats: " + extension);
            if (!Regex.IsMatch(text, @"%MO(?:MM|IN)\*%") || !Regex.IsMatch(text, @"M02\*\s*$"))
                throw new InvalidDataException("Gerber units or M02 termination missing: " + extension);
            if (!text.Contains("%ADD"))
            {
                if (Regex.IsMatch(text, @"D0?[13]\*"))
                    throw new InvalidDataException("Gerber aperture definitions missing: " + extension);
                // Native Altium can omit the aperture table on an empty legend.
                // Keep a fully specified empty layer in the Scan archive.
                text = text.Insert(text.IndexOf("M02*", StringComparison.Ordinal), "%ADD10C,0.01*%\n");
            }

            // RS-274X has no X2 identity attributes. Do not permit a native exporter
            // change to silently override our unambiguous filenames with other roles.
            if (Regex.IsMatch(text, @"%TF\.(?:FileFunction|ProjectId),"))
                throw new InvalidDataException("Unexpected X2 file identity in RS-274X export: " + extension);
            // Altium emits legacy identity transforms. Remove only exact identities;
            // a nonidentity transform or repeat must never reach Scan unflattened.
            text = Regex.Replace(text, @"%(?:ASAXBY|OFA[+\-]?0(?:\.0*)?B[+\-]?0(?:\.0*)?|SFA1(?:\.0*)?B1(?:\.0*)?|MIA0B0|IR0(?:\.0*)?|IPPOS|SR)\*%\s*", "");
            string commands = Regex.Replace(text, @"%AM[^%]*%", "", RegexOptions.Singleline);
            foreach (Match block in Regex.Matches(commands, @"%([^%]*)%"))
                foreach (string command in block.Groups[1].Value.Split('*'))
                {
                    string c = command.Trim();
                    if (c.Length == 0) continue;
                    if (!Regex.IsMatch(c, @"^(?:FS|MO|ADD|LP[DC]$|LN|IN|TA\.|TD$|TO\.|TF\.)"))
                        throw new InvalidDataException("Unsupported or unflattened Gerber command '" + c + "' in " + extension);
                }
            if (Regex.IsMatch(text, @"(?:^|\*)\s*G91(?:\*|$)"))
                throw new InvalidDataException("Incremental Gerber coordinates are unsupported: " + extension);
            return text;
        }

        internal static string NormalizeDrill(string text)
        {
            if (!Regex.IsMatch(text, @"\AM48\s*\n") || !Regex.IsMatch(text, @"(?m)^METRIC,(?:LZ|TZ)(?:,[0.]+)?\s*$")
                || !Regex.IsMatch(text, @"(?m)^M30\s*$"))
                throw new InvalidDataException("NC Drill must declare metric units, LZ/TZ zero format and M30 termination.");
            Match format = Regex.Match(text, @";FILE_FORMAT=(\d):(\d)");
            if (format.Success && format.Value != ";FILE_FORMAT=4:6")
                throw new InvalidDataException("NC Drill exporter did not use the requested 4:6 format.");
            if (!format.Success) text = text.Insert(text.IndexOf('\n') + 1, ";FILE_FORMAT=4:6\n");
            if (!Regex.IsMatch(text, @"(?m)^T\d+(?:[FS][0-9.]+)*C[0-9.]+(?:[FS][0-9.]+)*\s*$"))
            {
                if (Regex.IsMatch(text, @"(?m)^[^;\n]*[XY][+\-]?\d"))
                    throw new InvalidDataException("NC Drill tool diameters are missing.");
                return EmptyGerber;
            }
            if (Regex.IsMatch(text, @"(?m)^(?:G91|M25|M01|M02|R\d)"))
                throw new InvalidDataException("Incremental or repeated NC Drill geometry must be flattened.");
            return text;
        }
    }
}
