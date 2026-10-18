using System;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    internal static class TronstolE1ArcBounds
    {
        // Altium arcs run counterclockwise from start to end, including across zero.
        public static bool TryGetBounds(
            int centerX, int centerY, int radius, double startAngle, double endAngle,
            out int left, out int bottom, out int right, out int top)
        {
            left = bottom = right = top = 0;
            if (radius <= 0 || double.IsNaN(startAngle) || double.IsInfinity(startAngle)
                || double.IsNaN(endAngle) || double.IsInfinity(endAngle))
                return false;

            double start = Normalize(startAngle);
            double sweep = Normalize(Normalize(endAngle) - start);
            if (sweep == 0.0 || Math.Abs(endAngle - startAngle) >= 360.0)
                sweep = 360.0;

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            double[] angles = { start, Normalize(endAngle), 0, 90, 180, 270 };
            for (int index = 0; index < angles.Length; index++)
            {
                double angle = angles[index];
                if (index >= 2 && Normalize(angle - start) > sweep + 1e-9)
                    continue;
                double radians = angle * Math.PI / 180.0;
                double x = centerX + radius * Math.Cos(radians);
                double y = centerY + radius * Math.Sin(radians);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            if (minX < int.MinValue || minY < int.MinValue || maxX > int.MaxValue || maxY > int.MaxValue)
                return false;
            left = (int)Math.Floor(minX);
            bottom = (int)Math.Floor(minY);
            right = (int)Math.Ceiling(maxX);
            top = (int)Math.Ceiling(maxY);
            return true;
        }

        private static double Normalize(double angle)
        {
            double result = angle % 360.0;
            return result < 0.0 ? result + 360.0 : result;
        }
    }
}
