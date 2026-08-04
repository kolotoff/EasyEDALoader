using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    public sealed class TronstolE1Placement
    {
        public string Designator { get; set; }
        public string OriginalPartNumber { get; set; }
        public string PartNumber { get; set; }
        public string Manufacturer { get; set; }
        public string Description { get; set; }
        public string Footprint { get; set; }
        public string Carrier { get; set; }
        public double CenterXMillimeters { get; set; }
        public double CenterYMillimeters { get; set; }
        public bool IsBottom { get; set; }
        public double RotationDegrees { get; set; }
        public string RotationText { get; set; }
        public bool IsPanelFiducial { get; set; }
        public int PanelFiducialNumber { get; set; }
        public bool IsBoardInfo { get; set; }
        public int BoardInfoOrder { get; set; }
        public bool DisableBottomTransform { get; set; }
        public bool HasBottomMirrorAxisY { get; set; }
        public double BottomMirrorAxisYMillimeters { get; set; }
    }

    public static class TronstolE1Csv
    {
        public const string Header =
            "\"Designator\",\"PartNumber\",\"Footprint\",\"Manufacturer\",\"Description\",\"Mid X\",\"Mid Y\",\"Layer\",\"Rotation\",\"Carrier\"";

        public static void Write(TextWriter writer, IEnumerable<TronstolE1Placement> placements)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (placements == null)
                throw new ArgumentNullException(nameof(placements));

            writer.WriteLine(Header);
            foreach (TronstolE1Placement placement in OrderPlacements(placements))
            {
                bool transformBottom = placement.IsBottom && !placement.DisableBottomTransform;
                double outputX = placement.CenterXMillimeters;
                double outputY = placement.CenterYMillimeters;
                if (transformBottom)
                {
                    if (!placement.HasBottomMirrorAxisY)
                    {
                        throw new InvalidOperationException(
                            "Bottom placement transform requires board mirror axis.");
                    }

                    outputY = (2.0 * placement.BottomMirrorAxisYMillimeters)
                        - placement.CenterYMillimeters;
                }
                string rotation = placement.RotationText
                    ?? FormatRotation(
                        transformBottom
                            ? FlipRotationViaXAxis(placement.RotationDegrees)
                            : placement.RotationDegrees);

                writer.Write(Quote(placement.Designator));
                writer.Write(',');
                writer.Write(Quote(placement.PartNumber));
                writer.Write(',');
                writer.Write(Quote(placement.Footprint));
                writer.Write(',');
                writer.Write(Quote(placement.Manufacturer));
                writer.Write(',');
                writer.Write(Quote(placement.Description));
                writer.Write(',');
                writer.Write(Quote(FormatCoordinate(outputX)));
                writer.Write(',');
                writer.Write(Quote(FormatCoordinate(outputY)));
                writer.Write(',');
                writer.Write(Quote(placement.IsBottom ? "Bottom" : "Top"));
                writer.Write(',');
                writer.Write(Quote(rotation));
                writer.Write(',');
                writer.WriteLine(Quote(placement.Carrier));
            }
        }

        public static string Render(IEnumerable<TronstolE1Placement> placements)
        {
            var builder = new StringBuilder();
            using (var writer = new StringWriter(builder, CultureInfo.InvariantCulture))
                Write(writer, placements);
            return builder.ToString();
        }

        private static IEnumerable<TronstolE1Placement> OrderPlacements(
            IEnumerable<TronstolE1Placement> placements)
        {
            List<TronstolE1Placement> rows = placements
                .Where(placement => placement != null)
                .ToList();

            IEnumerable<TronstolE1Placement> panelFiducials = rows
                .Where(placement => placement.IsPanelFiducial)
                .OrderBy(placement => placement.PanelFiducialNumber)
                .ThenBy(
                    placement => placement.Designator ?? string.Empty,
                    NaturalDesignatorComparer.Instance);

            IEnumerable<TronstolE1Placement> boardInfo = rows
                .Where(placement => placement.IsBoardInfo)
                .OrderBy(placement => placement.BoardInfoOrder)
                .ThenBy(
                    placement => placement.Designator ?? string.Empty,
                    NaturalDesignatorComparer.Instance);

            IEnumerable<TronstolE1Placement> components = rows
                .Where(placement => !placement.IsPanelFiducial && !placement.IsBoardInfo)
                .GroupBy(
                    placement => TronstolE1Text.Normalize(
                        placement.OriginalPartNumber ?? placement.PartNumber),
                    StringComparer.Ordinal)
                .Select(group => new
                {
                    PartNumber = group.Key,
                    Rows = group
                        .OrderBy(
                            placement => placement.Designator ?? string.Empty,
                            NaturalDesignatorComparer.Instance)
                        .ToList()
                })
                .OrderBy(
                    group => group.Rows[0].Designator ?? string.Empty,
                    NaturalDesignatorComparer.Instance)
                .ThenBy(group => group.PartNumber, StringComparer.Ordinal)
                .SelectMany(group => group.Rows);

            return panelFiducials.Concat(boardInfo).Concat(components);
        }

        private static string FormatCoordinate(double value)
        {
            if (value == 0.0)
                value = 0.0;
            return value.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        private static string FormatRotation(double value)
        {
            if (value == 0.0)
                value = 0.0;
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static double FlipRotationViaXAxis(double value)
        {
            double result = 360.0 - value;
            result %= 360.0;
            if (result < 0.0)
                result += 360.0;
            if (result == 360.0)
                result = 0.0;
            return result;
        }

        private static string Quote(string value)
        {
            return "\"" + TronstolE1Text.Normalize(value).Replace("\"", "\"\"") + "\"";
        }

        private sealed class NaturalDesignatorComparer : IComparer<string>
        {
            public static readonly NaturalDesignatorComparer Instance =
                new NaturalDesignatorComparer();

            public int Compare(string left, string right)
            {
                left = left ?? string.Empty;
                right = right ?? string.Empty;

                int leftIndex = 0;
                int rightIndex = 0;
                while (leftIndex < left.Length && rightIndex < right.Length)
                {
                    bool leftIsDigit = char.IsDigit(left[leftIndex]);
                    bool rightIsDigit = char.IsDigit(right[rightIndex]);
                    if (leftIsDigit && rightIsDigit)
                    {
                        int leftStart = leftIndex;
                        int rightStart = rightIndex;
                        while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                            leftIndex++;
                        while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                            rightIndex++;

                        int leftSignificant = leftStart;
                        int rightSignificant = rightStart;
                        while (leftSignificant < leftIndex && left[leftSignificant] == '0')
                            leftSignificant++;
                        while (rightSignificant < rightIndex && right[rightSignificant] == '0')
                            rightSignificant++;

                        int leftDigits = leftIndex - leftSignificant;
                        int rightDigits = rightIndex - rightSignificant;
                        int digitCountComparison = leftDigits.CompareTo(rightDigits);
                        if (digitCountComparison != 0)
                            return digitCountComparison;

                        int digitComparison = string.Compare(
                            left,
                            leftSignificant,
                            right,
                            rightSignificant,
                            leftDigits,
                            StringComparison.Ordinal);
                        if (digitComparison != 0)
                            return digitComparison;

                        int runLengthComparison =
                            (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
                        if (runLengthComparison != 0)
                            return runLengthComparison;
                    }
                    else
                    {
                        int characterComparison = char.ToUpperInvariant(left[leftIndex])
                            .CompareTo(char.ToUpperInvariant(right[rightIndex]));
                        if (characterComparison != 0)
                            return characterComparison;

                        leftIndex++;
                        rightIndex++;
                    }
                }

                int lengthComparison = left.Length.CompareTo(right.Length);
                return lengthComparison != 0
                    ? lengthComparison
                    : string.Compare(left, right, StringComparison.Ordinal);
            }
        }
    }
}
