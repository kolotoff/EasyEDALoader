using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    internal static class EdgeRailContourAnalyzer
    {
        private const double TolMm = 0.05;

        public static double DetectCornerRadius(EdgeRailContour contour)
        {
            if (contour == null || contour.Bounds == null || contour.Bounds.IsEmpty || contour.Points.Count < 4) return 0;
            EdgeRailBounds b = contour.Bounds;
            var samples = new List<double>();
            // Bottom edge: straight part is inset by Rb at both ends.
            Collect(samples, MinXOnEdge(contour, b.MinY, b.MinX, b.MaxX, vertical: false) - b.MinX);
            Collect(samples, b.MaxX - MaxXOnEdge(contour, b.MinY, b.MinX, b.MaxX, vertical: false));
            // Top edge
            Collect(samples, MinXOnEdge(contour, b.MaxY, b.MinX, b.MaxX, vertical: false) - b.MinX);
            Collect(samples, b.MaxX - MaxXOnEdge(contour, b.MaxY, b.MinX, b.MaxX, vertical: false));
            // Left/right edges (inset along Y)
            Collect(samples, MinXOnEdge(contour, b.MinX, b.MinY, b.MaxY, vertical: true) - b.MinY);
            Collect(samples, b.MaxY - MaxXOnEdge(contour, b.MinX, b.MinY, b.MaxY, vertical: true));
            Collect(samples, MinXOnEdge(contour, b.MaxX, b.MinY, b.MaxY, vertical: true) - b.MinY);
            Collect(samples, b.MaxY - MaxXOnEdge(contour, b.MaxX, b.MinY, b.MaxY, vertical: true));
            if (samples.Count == 0) return 0;
            samples.Sort();
            double median = samples[samples.Count / 2];
            // Discard as "no rounding" if samples disagree widely or are ~0.
            return (median > 0.2 && median < 50.0) ? median : 0;
        }

        private static void Collect(List<double> samples, double value) { if (value >= -0.5 && value <= 50.0) samples.Add(value); }

        // Smallest coordinate (X for horizontal edges, Y for vertical) among points lying on the edge within tolerance.
        private static double MinXOnEdge(EdgeRailContour c, double edgeValue, double lo, double hi, bool vertical)
        {
            double best = double.PositiveInfinity;
            foreach (EdgeRailPoint p in c.Points)
            {
                double key = vertical ? p.X : p.Y;     // which axis identifies the edge
                double val = vertical ? p.Y : p.X;     // which axis we measure along
                if (System.Math.Abs(key - edgeValue) <= TolMm) best = System.Math.Min(best, val);
            }
            return double.IsInfinity(best) ? lo : best;
        }
        private static double MaxXOnEdge(EdgeRailContour c, double edgeValue, double lo, double hi, bool vertical)
        {
            double best = double.NegativeInfinity;
            foreach (EdgeRailPoint p in c.Points)
            {
                double key = vertical ? p.X : p.Y;
                double val = vertical ? p.Y : p.X;
                if (System.Math.Abs(key - edgeValue) <= TolMm) best = System.Math.Max(best, val);
            }
            return double.IsNegativeInfinity(best) ? hi : best;
        }
    }
}
