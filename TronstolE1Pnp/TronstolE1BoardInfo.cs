using System.Collections.Generic;
using System.Globalization;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    internal static class TronstolE1BoardInfo
    {
        public static IEnumerable<TronstolE1Placement> CreateEdgeRails(
            double leftRailMillimeters,
            double bottomRailMillimeters,
            double topRailMillimeters)
        {
            // E1 filters the CSV by layer before initializing fiducials.
            // Flipping around X brings the original top rail to the bottom.
            yield return CreatePlacement(
                "EdgeRail", "Edge rails size", "EdgeRail",
                leftRailMillimeters, bottomRailMillimeters, false, 5);
            yield return CreatePlacement(
                "EdgeRail", "Edge rails size", "EdgeRail",
                leftRailMillimeters, topRailMillimeters, true, 6);
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
