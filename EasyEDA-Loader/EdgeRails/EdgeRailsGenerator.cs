using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    internal static class EdgeRailsGenerator
    {
        internal const double PanelCornerRadiusMm = 1.5;     // outer panel corner radius
        internal const double CornerClearanceMm = 1.0;       // L-step clearance from the board edge
        internal const double HoleSideInsetMm = 7.0;
        internal const double FiducialSideInsetMm = 12.0;
        internal const double PolarizationExtraInsetMm = 5.0; // added at the top-right corner only
        internal const double FiducialMaskOpeningPadMm = 1.0; // mask = copper + this

        public static EdgeRailPlan Generate(EdgeRailBounds boardBounds, double boardCornerRMm, EdgeRailContour contour, EdgeRailOptions options)
        {
            var plan = new EdgeRailPlan { BoardBounds = boardBounds, BoardCornerRMm = boardCornerRMm };
            if (boardBounds == null || boardBounds.IsEmpty) { plan.Diagnostics.Add("ERROR: board bounds are empty."); return plan; }
            ComputePanelBounds(plan, options);
            BuildBoardOutline(plan, contour);
            BuildRails(plan, options);     // Task 2
            BuildHoles(plan, options);     // Task 3
            BuildFiducials(plan, options); // Task 3
            return plan;
        }

        private static void ComputePanelBounds(EdgeRailPlan plan, EdgeRailOptions o)
        {
            EdgeRailBounds b = plan.BoardBounds;
            plan.PanelBounds.MinX = b.MinX - o.VerticalRailMm;
            plan.PanelBounds.MaxX = b.MaxX + o.VerticalRailMm;
            plan.PanelBounds.MinY = b.MinY - o.HorizontalRailMm;
            plan.PanelBounds.MaxY = b.MaxY + o.HorizontalRailMm;
        }

        private static void BuildBoardOutline(EdgeRailPlan plan, EdgeRailContour contour)
        {
            if (contour == null || contour.Points.Count < 2) return;
            for (int i = 0; i < contour.Points.Count - 1; i++)
                plan.BoardOutlineSegments.Add(new EdgeRailSegment { Kind = EdgeRailSegmentKind.Line, Start = contour.Points[i], End = contour.Points[i + 1] });
        }

        // Implemented in later tasks:
        private static void BuildRails(EdgeRailPlan plan, EdgeRailOptions o)
        {
            if (o.HorizontalRailMm > 0) { AddHorizontalStrip(plan, bottom: true, o); AddHorizontalStrip(plan, bottom: false, o); }
            if (o.VerticalRailMm > 0) { AddVerticalStrip(plan, left: true, o); AddVerticalStrip(plan, left: false, o); }
        }

        private static void AddHorizontalStrip(EdgeRailPlan plan, bool bottom, EdgeRailOptions o)
        {
            EdgeRailBounds b = plan.BoardBounds, p = plan.PanelBounds;
            double R = PanelCornerRadiusMm, Rb = plan.BoardCornerRMm, clr = CornerClearanceMm;
            double edgeY = bottom ? b.MinY : b.MaxY;          // board edge this strip faces
            double outerY = bottom ? p.MinY : p.MaxY;         // panel outer edge
            int sgn = bottom ? 1 : -1;                        // +1: inward is +Y (bottom strip); -1: top strip
            // 1. side edges (from outer arc tangent up to the L-step level / board corner if sharp)
            plan.RailSegments.Add(Line(b.MinX, outerY + sgn * R, b.MinX, edgeY - sgn * clr));
            plan.RailSegments.Add(Line(b.MaxX, edgeY - sgn * clr, b.MaxX, outerY + sgn * R));
            if (Rb > 0.001)
            {
                // 2 & 4. L-steps at both corners
                plan.RailSegments.Add(Line(b.MinX, edgeY - sgn * clr, b.MinX + Rb, edgeY - sgn * clr));
                plan.RailSegments.Add(Line(b.MinX + Rb, edgeY - sgn * clr, b.MinX + Rb, edgeY));
                plan.RailSegments.Add(Line(b.MaxX - Rb, edgeY, b.MaxX - Rb, edgeY - sgn * clr));
                plan.RailSegments.Add(Line(b.MaxX - Rb, edgeY - sgn * clr, b.MaxX, edgeY - sgn * clr));
            }
            else
            {
                plan.RailSegments.Add(Line(b.MinX, edgeY - sgn * clr, b.MinX, edgeY));
                plan.RailSegments.Add(Line(b.MaxX, edgeY, b.MaxX, edgeY - sgn * clr));
            }
            // 6-8. outer edge with two R1.5 corners. Each arc carries its true Center and both
            // tangent points explicitly: Start = side-edge tangent, End = outer-edge tangent.
            // The left-corner Center is inset by +sgn*R (previously it held a tangent point).
            plan.RailSegments.Add(Arc(b.MaxX - R, outerY + sgn * R, b.MaxX, outerY + sgn * R, b.MaxX - R, outerY, bottom ? SweepCW : SweepCCW));
            plan.RailSegments.Add(Line(b.MaxX - R, outerY, b.MinX + R, outerY));
            plan.RailSegments.Add(Arc(b.MinX + R, outerY + sgn * R, b.MinX + R, outerY, b.MinX, outerY + sgn * R, bottom ? SweepCW : SweepCCW));
        }

        private static void AddVerticalStrip(EdgeRailPlan plan, bool left, EdgeRailOptions o)
        {
            // 90° rotation of AddHorizontalStrip: swap X/Y, board edge = b.MinX/b.MaxX, outer = p.MinX/p.MaxX,
            // L-step jogs along Y by Rb, side edges run vertically. Same R1.5 outer corners.
            EdgeRailBounds b = plan.BoardBounds, p = plan.PanelBounds;
            double R = PanelCornerRadiusMm, Rb = plan.BoardCornerRMm, clr = CornerClearanceMm;
            double edgeX = left ? b.MinX : b.MaxX, outerX = left ? p.MinX : p.MaxX;
            int sgn = left ? 1 : -1;
            plan.RailSegments.Add(Line(outerX + sgn * R, b.MinY, edgeX - sgn * clr, b.MinY));
            plan.RailSegments.Add(Line(edgeX - sgn * clr, b.MaxY, outerX + sgn * R, b.MaxY));
            if (Rb > 0.001)
            {
                plan.RailSegments.Add(Line(edgeX - sgn * clr, b.MinY, edgeX - sgn * clr, b.MinY + Rb));
                plan.RailSegments.Add(Line(edgeX - sgn * clr, b.MinY + Rb, edgeX, b.MinY + Rb));
                plan.RailSegments.Add(Line(edgeX, b.MaxY - Rb, edgeX - sgn * clr, b.MaxY - Rb));
                plan.RailSegments.Add(Line(edgeX - sgn * clr, b.MaxY - Rb, edgeX - sgn * clr, b.MaxY));
            }
            plan.RailSegments.Add(Arc(outerX + sgn * R, b.MaxY - R, outerX + sgn * R, b.MaxY, outerX, b.MaxY - R, left ? SweepCCW : SweepCW));
            plan.RailSegments.Add(Line(outerX, b.MaxY - R, outerX, b.MinY + R));
            plan.RailSegments.Add(Arc(outerX + sgn * R, b.MinY + R, outerX, b.MinY + R, outerX + sgn * R, b.MinY, left ? SweepCCW : SweepCW));
        }

        private const bool SweepCW = true, SweepCCW = false;
        private static EdgeRailSegment Line(double x1, double y1, double x2, double y2)
            => new EdgeRailSegment { Kind = EdgeRailSegmentKind.Line, Start = new EdgeRailPoint(x1, y1), End = new EdgeRailPoint(x2, y2) };
        private static EdgeRailSegment Arc(double cx, double cy, double sx, double sy, double ex, double ey, bool clockwise)
            => new EdgeRailSegment { Kind = EdgeRailSegmentKind.Arc, Center = new EdgeRailPoint(cx, cy), Start = new EdgeRailPoint(sx, sy), End = new EdgeRailPoint(ex, ey), Clockwise = clockwise };
        private static void BuildHoles(EdgeRailPlan plan, EdgeRailOptions o)
        {
            foreach (EdgeRailPoint pt in FeatureCenters(plan, o, HoleSideInsetMm))
                if (InRail(plan, pt)) plan.Holes.Add(new EdgeRailHole { Center = pt, DiameterMm = o.HoleSizeMm });
                else plan.Diagnostics.Add("Skipped a tooling hole outside the rail at " + pt.X + "," + pt.Y + ".");
        }

        private static void BuildFiducials(EdgeRailPlan plan, EdgeRailOptions o)
        {
            double copper = o.FiducialSizeMm, mask = copper + FiducialMaskOpeningPadMm;
            foreach (EdgeRailPoint pt in FeatureCenters(plan, o, FiducialSideInsetMm))
            {
                if (!InRail(plan, pt)) { plan.Diagnostics.Add("Skipped a fiducial outside the rail at " + pt.X + "," + pt.Y + "."); continue; }
                plan.Fiducials.Add(new EdgeRailFiducial { Center = pt, Side = EdgeRailSide.Top, CopperDiameterMm = copper, MaskOpeningMm = mask });
                plan.Fiducials.Add(new EdgeRailFiducial { Center = pt, Side = EdgeRailSide.Bottom, CopperDiameterMm = copper, MaskOpeningMm = mask });
            }
        }

        private static bool InRail(EdgeRailPlan plan, EdgeRailPoint pt)
        {
            EdgeRailBounds p = plan.PanelBounds;
            return pt.X > p.MinX - 0.01 && pt.X < p.MaxX + 0.01 && pt.Y > p.MinY - 0.01 && pt.Y < p.MaxY + 0.01;
        }

        private static List<EdgeRailPoint> FeatureCenters(EdgeRailPlan plan, EdgeRailOptions o, double inset)
        {
            EdgeRailBounds b = plan.BoardBounds, p = plan.PanelBounds;
            var pts = new List<EdgeRailPoint>();
            if (o.HorizontalRailMm > 0)
            {
                double bottomY = p.MinY + o.HorizontalRailMm / 2.0;
                double topY = p.MaxY - o.HorizontalRailMm / 2.0;
                pts.Add(new EdgeRailPoint(b.MinX + inset, bottomY));                                                  // BL
                pts.Add(new EdgeRailPoint(b.MaxX - inset, bottomY));                                                  // BR
                pts.Add(new EdgeRailPoint(b.MinX + inset, topY));                                                     // TL
                pts.Add(new EdgeRailPoint(b.MaxX - inset - PolarizationExtraInsetMm, topY));                          // TR
            }
            else if (o.VerticalRailMm > 0)
            {
                double leftX = p.MinX + o.VerticalRailMm / 2.0;
                double rightX = p.MaxX - o.VerticalRailMm / 2.0;
                pts.Add(new EdgeRailPoint(leftX, b.MinY + inset));                                                    // BL
                pts.Add(new EdgeRailPoint(leftX, b.MaxY - inset));                                                    // TL
                pts.Add(new EdgeRailPoint(rightX, b.MinY + inset));                                                   // BR
                pts.Add(new EdgeRailPoint(rightX, b.MaxY - inset - PolarizationExtraInsetMm));                        // TR
            }
            return pts;
        }
    }
}
