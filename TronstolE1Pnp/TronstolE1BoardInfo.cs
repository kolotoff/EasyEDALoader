using System;
using System.Collections.Generic;
using System.Globalization;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    internal static class TronstolE1BoardInfo
    {
        public static IEnumerable<TronstolE1Placement> CreateEdgeRails(
            double topRailMillimeters,
            double bottomRailMillimeters,
            double leftRailMillimeters,
            double rightRailMillimeters)
        {
            ValidateRailWidth(topRailMillimeters, nameof(topRailMillimeters));
            ValidateRailWidth(bottomRailMillimeters, nameof(bottomRailMillimeters));
            ValidateRailWidth(leftRailMillimeters, nameof(leftRailMillimeters));
            ValidateRailWidth(rightRailMillimeters, nameof(rightRailMillimeters));

            yield return CreatePlacement(
                "EdgeRailTopBottom", "Top and bottom edge rail widths", string.Empty,
                topRailMillimeters, bottomRailMillimeters, false, 5);
            yield return CreatePlacement(
                "EdgeRailLeftRight", "Left and right edge rail widths", string.Empty,
                leftRailMillimeters, rightRailMillimeters, false, 6);
        }

        private static void ValidateRailWidth(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                throw new ArgumentOutOfRangeException(
                    parameterName, value, "Edge rail width must be finite and nonnegative (mm).");
        }

        public static TronstolE1Placement CreatePlacement(
            string designator,
            string partNumber,
            string footprint,
            double xMillimeters,
            double yMillimeters,
            bool isBottom,
            int order,
            double rotationValue = 0.0)
        {
            return new TronstolE1Placement
            {
                Designator = designator,
                OriginalPartNumber = partNumber,
                PartNumber = partNumber,
                Manufacturer = string.Empty,
                Description = string.Empty,
                Footprint = footprint,
                Carrier = string.Empty,
                ReelPitch = string.Empty,
                CenterXMillimeters = xMillimeters,
                CenterYMillimeters = yMillimeters,
                IsBottom = isBottom,
                RotationDegrees = rotationValue,
                RotationText = rotationValue == 0.0
                    ? "0.0"
                    : rotationValue.ToString("0.####", CultureInfo.InvariantCulture),
                IsBoardInfo = true,
                BoardInfoOrder = order,
                DisableBottomTransform = true
            };
        }
    }
}
