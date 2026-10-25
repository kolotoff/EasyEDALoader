using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    internal sealed class ContourViaOptions
    {
        public double DiameterMm = 0.4;
        public double HoleMm = 0.2;
        public double ClearanceMm = 0.3;
        public double PitchMm = 2.0;
        public string NetName = "";

        public void Validate()
        {
            if (!double.IsFinite(DiameterMm) || DiameterMm < 0.001 || DiameterMm > 100)
                throw new ArgumentException("Via diameter must be between 0.001 and 100 mm.");
            if (!double.IsFinite(HoleMm) || HoleMm < 0.001 || HoleMm >= DiameterMm)
                throw new ArgumentException("Drill diameter must be positive and smaller than the via diameter.");
            if (!double.IsFinite(ClearanceMm) || ClearanceMm < 0 || ClearanceMm > 100)
                throw new ArgumentException("Keepout clearance must be between 0 and 100 mm.");
            if (!double.IsFinite(PitchMm) || PitchMm < DiameterMm || PitchMm > 1000)
                throw new ArgumentException("Pitch must be at least the via diameter and no more than 1000 mm.");
        }
    }

    internal readonly struct ContourViaPoint
    {
        public readonly double X, Y;
        public ContourViaPoint(double x, double y) { X = x; Y = y; }
    }

    internal sealed class ContourViaSegment
    {
        public bool IsArc;
        // Line endpoints, or arc centre and radius/angles (Altium CCW degrees).
        public double X1, Y1, X2, Y2, Radius, StartAngle, EndAngle;
    }

    internal static class ContourViaGeometry
    {
        // Match altium-mcp: choose the offset closest to the keepout bounds centre,
        // start each primitive at distance zero, and deduplicate at 0.001 mm.
        public static List<ContourViaPoint> Generate(IReadOnlyList<ContourViaSegment> segments,
            double centerX, double centerY, ContourViaOptions options)
        {
            options.Validate();
            var result = new List<ContourViaPoint>();
            var keys = new HashSet<(long, long)>();
            double offset = options.ClearanceMm + options.DiameterMm / 2;
            foreach (var s in segments)
            {
                double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1;
                double sweep = s.EndAngle - s.StartAngle;
                if (sweep < 0) sweep += 360;
                if (sweep <= 0.000001) sweep = 360;
                double length = s.IsArc ? s.Radius * sweep * Math.PI / 180 : Math.Sqrt(dx * dx + dy * dy);
                if (length <= 0.000001) continue;
                double nx = -dy / length, ny = dx / length;
                double mx = (s.X1 + s.X2) / 2, my = (s.Y1 + s.Y2) / 2;
                if (!s.IsArc && Distance2(mx - nx * offset, my - ny * offset, centerX, centerY)
                    < Distance2(mx + nx * offset, my + ny * offset, centerX, centerY))
                { nx = -nx; ny = -ny; }
                double steps = Math.Floor(length / options.PitchMm);
                if (steps > 100000 || result.Count + steps > 100000)
                    throw new ArgumentException("The contour would create more than 100,000 vias. Increase the pitch.");
                for (int i = 0; i <= (int)steps; i++)
                {
                    double x, y;
                    if (s.IsArc)
                    {
                        double angle = (s.StartAngle + i * options.PitchMm / length * sweep) * Math.PI / 180;
                        double cos = Math.Cos(angle), sin = Math.Sin(angle);
                        double radius = s.Radius + offset, alternate = s.Radius - offset;
                        if (Distance2(s.X1 + cos * alternate, s.Y1 + sin * alternate, centerX, centerY)
                            < Distance2(s.X1 + cos * radius, s.Y1 + sin * radius, centerX, centerY)) radius = alternate;
                        if (radius <= 0) throw new ArgumentException("Via offset exceeds a keepout arc radius. Reduce clearance or via diameter.");
                        x = s.X1 + cos * radius; y = s.Y1 + sin * radius;
                    }
                    else
                    {
                        x = s.X1 + dx / length * i * options.PitchMm + nx * offset;
                        y = s.Y1 + dy / length * i * options.PitchMm + ny * offset;
                    }
                    if (keys.Add(((long)Math.Round(x * 1000), (long)Math.Round(y * 1000))))
                        result.Add(new ContourViaPoint(x, y));
                }
            }
            return result;
        }

        private static double Distance2(double x, double y, double cx, double cy)
            => (x - cx) * (x - cx) + (y - cy) * (y - cy);
    }
}
