using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EasyEDA_Loader
{
    internal static class JlcCamAnalyzer
    {
        private const double PositionToleranceMm = 0.08;
        private const double FitToleranceMm = 0.02;

        public static JlcCamAnalysisSession Analyze(JlcCamAnalysisSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            string ok = JlcCamSource.GetChildDirectory(session.PackageRoot, "ok");
            string outlinePath = JlcCamSource.FindOriginalOutline(session);
            string ko = RequiredLayer(ok, "ko");
            string drl = RequiredLayer(ok, "drl");
            JlcCamGerberFile original = JlcCamGerberParser.Parse(outlinePath);
            JlcCamGerberFile panel = JlcCamGerberParser.Parse(ko);
            JlcCamGerberFile drills = JlcCamGerberParser.Parse(drl);
            JlcCamGerberFile topCopper = ParseOptional(JlcCamSource.FindLayer(ok, "tl"));
            JlcCamGerberFile bottomCopper = ParseOptional(JlcCamSource.FindLayer(ok, "bl"));
            JlcCamGerberFile topMask = ParseOptional(JlcCamSource.FindLayer(ok, "ts"));
            JlcCamGerberFile bottomMask = ParseOptional(JlcCamSource.FindLayer(ok, "bs"));

            session.OriginalOutline.AddRange(original.Segments);
            JlcCamBounds originalBounds = Bounds(original.Segments.SelectMany(s => new[] { s.Start, s.End }));
            JlcCamBounds customerBounds = Bounds(panel.Segments.Where(s => s.Depth == 2).SelectMany(s => new[] { s.Start, s.End }));
            if (originalBounds.IsEmpty || customerBounds.IsEmpty) throw new InvalidDataException("Original outline or production ko DEPTH 2 outline has no line/arc geometry.");
            session.Transform = FitOrthogonalTransform(customerBounds, originalBounds);
            session.OriginalBounds = originalBounds;
            session.PanelBounds = TransformBounds(Bounds(panel.Segments.Where(s => s.Depth == 1).SelectMany(s => new[] { s.Start, s.End })), session.Transform);
            if (session.PanelBounds.IsEmpty) throw new InvalidDataException("Production ko has no DEPTH 1 panel geometry.");

            foreach (JlcCamSegment rail in panel.Segments.Where(s => s.Depth == 1)) session.RailSegments.Add(TransformSegment(rail, session.Transform));
            AnalyzeHoles(session, drills, topCopper, bottomCopper, topMask, bottomMask);
            AnalyzeFiducials(session, topCopper, topMask, JlcCamSide.Top, drills);
            AnalyzeFiducials(session, bottomCopper, bottomMask, JlcCamSide.Bottom, drills);
            session.Holes.Sort((a, b) => a.Center.Y != b.Center.Y ? a.Center.Y.CompareTo(b.Center.Y) : a.Center.X.CompareTo(b.Center.X));
            double centerX = (session.OriginalBounds.MinX + session.OriginalBounds.MaxX) / 2.0;
            double centerY = (session.OriginalBounds.MinY + session.OriginalBounds.MaxY) / 2.0;
            session.Fiducials.Sort((a, b) => CompareFiducials(a, b, centerX, centerY));
            for (int i = 0; i < session.Holes.Count; i++) session.Holes[i].Number = i + 1;
            for (int i = 0; i < session.Fiducials.Count; i++) session.Fiducials[i].Number = i + 1;
            if (session.Holes.Count == 0 && session.Fiducials.Count == 0) session.Diagnostics.Add("No rail-only edge holes or fiducials were found.");
            session.CanImport = session.Transform.FitErrorMm <= FitToleranceMm && session.Diagnostics.All(d => !d.StartsWith("ERROR:", StringComparison.Ordinal));
            return session;
        }

        private static void AnalyzeHoles(JlcCamAnalysisSession session, JlcCamGerberFile drills, JlcCamGerberFile topCopper, JlcCamGerberFile bottomCopper, JlcCamGerberFile topMask, JlcCamGerberFile bottomMask)
        {
            foreach (JlcCamFlash source in drills.Flashes.Where(f => f.Aperture != null && f.Aperture.IsCircular))
            {
                JlcCamPoint point = session.Transform.Apply(source.Center);
                if (!IsRailOnly(session, point) || HasFlashAt(topCopper, session.Transform, point) || HasFlashAt(bottomCopper, session.Transform, point)) continue;
                double raw = source.Aperture.XSize;
                bool verified = Near(raw, 2.05, 0.06);
                JlcCamHole hole = new JlcCamHole
                {
                    Center = point, CamDiameterMm = raw, NominalDiameterMm = verified ? 2.0 : raw,
                    TopMaskOpeningMm = FindCircularFlashDiameter(topMask, session.Transform, point),
                    BottomMaskOpeningMm = FindCircularFlashDiameter(bottomMask, session.Transform, point),
                    Verified = verified, Status = verified ? "Verified JLC 2.00 mm nominal (2.05 mm CAM drill)" : "Unverified CAM drill size"
                };
                session.Holes.Add(hole);
                if (!verified) session.Diagnostics.Add("ERROR: edge-hole at " + point + " has unverified nominal diameter " + raw.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " mm.");
            }
        }

        private static void AnalyzeFiducials(JlcCamAnalysisSession session, JlcCamGerberFile copper, JlcCamGerberFile mask, JlcCamSide side, JlcCamGerberFile drills)
        {
            if (copper == null || mask == null) return;
            foreach (JlcCamFlash source in copper.Flashes.Where(f => f.Aperture != null && f.Aperture.IsCircular))
            {
                JlcCamPoint point = session.Transform.Apply(source.Center);
                if (!IsRailOnly(session, point) || HasFlashAt(drills, session.Transform, point)) continue;
                double? maskSize = FindCircularFlashDiameter(mask, session.Transform, point); if (!maskSize.HasValue) continue;
                double raw = source.Aperture.XSize; bool verified = Near(raw, 1.05, 0.06);
                var fiducial = new JlcCamFiducial { Center = point, Side = side, CamDiameterMm = raw, NominalDiameterMm = verified ? 1.0 : raw, MaskOpeningMm = maskSize.Value, Verified = verified, Status = verified ? "Verified JLC 1.00 mm nominal (1.05 mm CAM copper)" : "Unverified CAM copper size" };
                if (!session.Fiducials.Any(x => x.Side == side && x.Center.DistanceTo(point) <= PositionToleranceMm)) session.Fiducials.Add(fiducial);
                if (!verified) session.Diagnostics.Add("ERROR: " + side + " fiducial at " + point + " has unverified nominal diameter.");
            }
        }

        private static bool IsRailOnly(JlcCamAnalysisSession session, JlcCamPoint point) { return session.PanelBounds.Contains(point, PositionToleranceMm) && !session.OriginalBounds.Contains(point, -PositionToleranceMm); }
        private static bool HasFlashAt(JlcCamGerberFile file, JlcCamTransform transform, JlcCamPoint point) { return FindCircularFlashDiameter(file, transform, point).HasValue; }
        private static double? FindCircularFlashDiameter(JlcCamGerberFile file, JlcCamTransform transform, JlcCamPoint point)
        {
            if (file == null) return null;
            JlcCamFlash match = file.Flashes.Where(f => f.Aperture != null && f.Aperture.IsCircular).FirstOrDefault(f => transform.Apply(f.Center).DistanceTo(point) <= PositionToleranceMm);
            return match?.Aperture.XSize;
        }
        private static JlcCamGerberFile ParseOptional(string path) { return string.IsNullOrWhiteSpace(path) ? null : JlcCamGerberParser.Parse(path); }
        private static string RequiredLayer(string root, string layer) { return JlcCamSource.FindLayer(root, layer) ?? throw new InvalidDataException("JLCCAM production package is missing ok/" + layer + "."); }
        private static bool Near(double value, double expected, double tolerance) { return Math.Abs(value - expected) <= tolerance; }
        private static int CompareFiducials(JlcCamFiducial a, JlcCamFiducial b, double centerX, double centerY)
        {
            int side = a.Side.CompareTo(b.Side); if (side != 0) return side; // Top rows, then Bottom rows.
            int oa = ClockwiseCorner(a.Center, centerX, centerY), ob = ClockwiseCorner(b.Center, centerX, centerY);
            if (oa != ob) return oa.CompareTo(ob);
            int y = a.Center.Y.CompareTo(b.Center.Y); return y != 0 ? y : a.Center.X.CompareTo(b.Center.X);
        }
        private static int ClockwiseCorner(JlcCamPoint point, double centerX, double centerY)
        {
            bool left = point.X <= centerX, bottom = point.Y <= centerY;
            if (left && bottom) return 0; // bottom-left
            if (left) return 1;            // top-left
            if (!bottom) return 2;         // top-right
            return 3;                      // bottom-right
        }
        private static JlcCamBounds Bounds(IEnumerable<JlcCamPoint> points) { var b = new JlcCamBounds(); foreach (JlcCamPoint p in points) b.Add(p); return b; }
        private static JlcCamBounds TransformBounds(JlcCamBounds bounds, JlcCamTransform transform)
        {
            if (bounds.IsEmpty) return bounds; return Bounds(new[] { transform.Apply(new JlcCamPoint(bounds.MinX, bounds.MinY)), transform.Apply(new JlcCamPoint(bounds.MinX, bounds.MaxY)), transform.Apply(new JlcCamPoint(bounds.MaxX, bounds.MinY)), transform.Apply(new JlcCamPoint(bounds.MaxX, bounds.MaxY)) });
        }
        private static JlcCamSegment TransformSegment(JlcCamSegment input, JlcCamTransform transform)
        {
            return new JlcCamSegment { Kind = input.Kind, Start = transform.Apply(input.Start), End = transform.Apply(input.End), Center = transform.Apply(input.Center), Clockwise = transform.Mirrored ? !input.Clockwise : input.Clockwise, Depth = input.Depth };
        }
        private static JlcCamTransform FitOrthogonalTransform(JlcCamBounds source, JlcCamBounds target)
        {
            var candidates = new List<JlcCamTransform>();
            foreach (bool mirror in new[] { false, true }) foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                var raw = new JlcCamTransform { Mirrored = mirror, Rotation = rotation };
                JlcCamBounds oriented = TransformBounds(source, raw);
                var candidate = new JlcCamTransform { Mirrored = mirror, Rotation = rotation, TranslateX = target.MinX - oriented.MinX, TranslateY = target.MinY - oriented.MinY };
                JlcCamBounds mapped = TransformBounds(source, candidate);
                candidate.FitErrorMm = Math.Max(Math.Max(Math.Abs(mapped.Width - target.Width), Math.Abs(mapped.Height - target.Height)), Math.Max(Math.Abs(mapped.MaxX - target.MaxX), Math.Abs(mapped.MaxY - target.MaxY)));
                candidates.Add(candidate);
            }
            JlcCamTransform best = candidates.OrderBy(c => c.FitErrorMm).First();
            if (best.FitErrorMm > FitToleranceMm) throw new InvalidDataException("Could not establish a safe panel-to-original Gerber transform; fitted error is " + best.FitErrorMm.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " mm. Production bounds: " + source + "; original bounds: " + target + ".");
            return best;
        }
    }
}
