using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.IO;

internal static class Program
{
    private static int Main()
    {
        PanelBoundsMatchReference();
        BottomRailMatchesReferenceKo();
        RailArcsHaveCorrectGeometry();
        HolesAndFiducialsMatchReferencePositions();
        RoundedRectangleDetectsR3();
        SharpRectangleDetectsZero();
        Console.WriteLine("EdgeRails tests passed.");
        return 0;
    }

    private static void PanelBoundsMatchReference()
    {
        var board = Board(0, 10, 146, 72);
        var o = new EdgeRailOptions { HorizontalRailMm = 10, VerticalRailMm = 0, HoleSizeMm = 2, FiducialSizeMm = 1 };
        EdgeRailPlan plan = EdgeRailsGenerator.Generate(board, 3.0, new EdgeRailContour(), o);
        Check(Near(plan.PanelBounds.MinX, 0) && Near(plan.PanelBounds.MaxX, 146), "panel X");
        Check(Near(plan.PanelBounds.MinY, 0) && Near(plan.PanelBounds.MaxY, 82), "panel Y");
    }

    internal static EdgeRailBounds Board(double minX, double minY, double maxX, double maxY)
        => new EdgeRailBounds { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
    internal static bool Near(double a, double b) => System.Math.Abs(a - b) < 0.0001;
    internal static void Check(bool value, string name) { if (!value) throw new InvalidDataException("Test failed: " + name); }

    private static void BottomRailMatchesReferenceKo()
    {
        EdgeRailPlan plan = EdgeRailsGenerator.Generate(Program.Board(0, 10, 146, 72), 3.0, new EdgeRailContour(),
            new EdgeRailOptions { HorizontalRailMm = 10, VerticalRailMm = 0 });
        // Collect line endpoints (ignore arcs for this ordering check).
        var pts = new List<double[]>();
        foreach (EdgeRailSegment s in plan.RailSegments)
            if (s.Kind == EdgeRailSegmentKind.Line) { pts.Add(new[] { s.Start.X, s.Start.Y }); pts.Add(new[] { s.End.X, s.End.Y }); }
        HasPoint(pts, 0, 1.5);   // panel bottom-left outer arc tangent
        HasPoint(pts, 0, 9);     // side edge top (1 mm clearance below board edge y=10)
        HasPoint(pts, 3, 9);     // L-step jog (board R3)
        HasPoint(pts, 3, 10);    // board bottom-edge tangent point
        HasPoint(pts, 143, 10);
        HasPoint(pts, 143, 9);
        HasPoint(pts, 146, 9);
        HasPoint(pts, 146, 1.5);
        HasPoint(pts, 1.5, 0);
        HasPoint(pts, 144.5, 0);
        int arcs = 0; foreach (EdgeRailSegment s in plan.RailSegments) if (s.Kind == EdgeRailSegmentKind.Arc) arcs++;
        Check(arcs == 4, "4 R1.5 outer arcs across bottom+top strips, got " + arcs); // 2 bottom + 2 top
    }

    private static void HasPoint(List<double[]> pts, double x, double y)
    {
        foreach (double[] p in pts) if (Near(p[0], x) && Near(p[1], y)) return;
        throw new InvalidDataException("Missing expected rail point (" + x + ", " + y + ")");
    }

    // Pins the exact R1.5 outer-corner arc geometry for the default horizontal-rail case.
    // Every arc must carry an explicit Center + Start + End (no nulls), both tangent points
    // must lie on the R1.5 circle, and each corner's Center/Start/End/Clockwise must match.
    // This fails on the old generator (Start was null; the left-corner Center was a tangent).
    private static void RailArcsHaveCorrectGeometry()
    {
        EdgeRailPlan plan = EdgeRailsGenerator.Generate(Program.Board(0, 10, 146, 72), 3.0, new EdgeRailContour(),
            new EdgeRailOptions { HorizontalRailMm = 10, VerticalRailMm = 0 });
        var arcs = new List<EdgeRailSegment>();
        foreach (EdgeRailSegment s in plan.RailSegments) if (s.Kind == EdgeRailSegmentKind.Arc) arcs.Add(s);
        Check(arcs.Count == 4, "4 arcs, got " + arcs.Count);
        foreach (EdgeRailSegment a in arcs)
        {
            Check(a.Center != null && a.Start != null && a.End != null, "arc has Center/Start/End");
            Check(Near(a.Center.DistanceTo(a.Start), 1.5), "arc Start radius 1.5, got " + a.Center.DistanceTo(a.Start));
            Check(Near(a.Center.DistanceTo(a.End), 1.5), "arc End radius 1.5, got " + a.Center.DistanceTo(a.End));
        }
        const double R = 1.5;
        double bx0 = 0, bx1 = 146, py0 = 0, py1 = 82; // board X span + panel Y span for this case
        // Bottom strip (outer edge at panel MinY): clockwise corners.
        HasArc(arcs, bx1 - R, py0 + R, bx1, py0 + R, bx1 - R, py0, true);   // bottom-right
        HasArc(arcs, bx0 + R, py0 + R, bx0 + R, py0, bx0, py0 + R, true);   // bottom-left
        // Top strip (outer edge at panel MaxY): counter-clockwise corners.
        HasArc(arcs, bx1 - R, py1 - R, bx1, py1 - R, bx1 - R, py1, false);  // top-right
        HasArc(arcs, bx0 + R, py1 - R, bx0 + R, py1, bx0, py1 - R, false);  // top-left
    }

    private static void HasArc(List<EdgeRailSegment> arcs, double cx, double cy, double sx, double sy, double ex, double ey, bool cw)
    {
        foreach (EdgeRailSegment a in arcs)
            if (Near(a.Center.X, cx) && Near(a.Center.Y, cy)
                && Near(a.Start.X, sx) && Near(a.Start.Y, sy)
                && Near(a.End.X, ex) && Near(a.End.Y, ey)
                && a.Clockwise == cw) return;
        throw new InvalidDataException("Missing expected arc C(" + cx + "," + cy + ") S(" + sx + "," + sy + ") E(" + ex + "," + ey + ") cw=" + cw);
    }

    private static void HolesAndFiducialsMatchReferencePositions()
    {
        EdgeRailPlan plan = EdgeRailsGenerator.Generate(Program.Board(0, 10, 146, 72), 3.0, new EdgeRailContour(),
            new EdgeRailOptions { HorizontalRailMm = 10, VerticalRailMm = 0, HoleSizeMm = 2, FiducialSizeMm = 1 });
        // 4 holes: (7,5)(139,5)(7,77)(134,77)  — top-right shifted +5 mm inboard.
        Check(plan.Holes.Count == 4, "4 holes, got " + plan.Holes.Count);
        Contains(plan.Holes.ConvertAll(h => h.Center), 7, 5);
        Contains(plan.Holes.ConvertAll(h => h.Center), 139, 5);
        Contains(plan.Holes.ConvertAll(h => h.Center), 7, 77);
        Contains(plan.Holes.ConvertAll(h => h.Center), 134, 77);
        // 8 fiducials (4 top + 4 bottom, stacked): (12,5)(134,5)(12,77)(129,77), each on both layers.
        Check(plan.Fiducials.Count == 8, "8 fiducials, got " + plan.Fiducials.Count);
        Contains(plan.Fiducials.ConvertAll(f => f.Center), 12, 5);
        Contains(plan.Fiducials.ConvertAll(f => f.Center), 134, 5);
        Contains(plan.Fiducials.ConvertAll(f => f.Center), 12, 77);
        Contains(plan.Fiducials.ConvertAll(f => f.Center), 129, 77);
        Check(plan.Fiducials.TrueForAll(f => Near(f.CopperDiameterMm, 1.0)), "fid copper 1 mm");
        Check(plan.Fiducials.TrueForAll(f => Near(f.MaskOpeningMm, 2.0)), "fid mask 2 mm");
        int top = 0, bot = 0; foreach (EdgeRailFiducial f in plan.Fiducials) if (f.Side == EdgeRailSide.Top) top++; else bot++;
        Check(top == 4 && bot == 4, "4 top + 4 bottom");
    }

    private static void Contains(List<EdgeRailPoint> pts, double x, double y)
    { foreach (EdgeRailPoint p in pts) if (Near(p.X, x) && Near(p.Y, y)) return; throw new InvalidDataException("Missing (" + x + "," + y + ")"); }

    private static void RoundedRectangleDetectsR3()
    {
        var c = new EdgeRailContour();
        c.Bounds = Program.Board(0, 0, 146, 62);
        // Approximate an R3 rounded rectangle: straight edges + a few arc points near each corner.
        AddPt(c, 3, 0); AddPt(c, 143, 0); AddPt(c, 146, 3); AddPt(c, 146, 59); AddPt(c, 143, 62); AddPt(c, 3, 62); AddPt(c, 0, 59); AddPt(c, 0, 3);
        double r = EdgeRailContourAnalyzer.DetectCornerRadius(c);
        Check(Near(r, 3.0), "detected R3, got " + r);
    }
    private static void SharpRectangleDetectsZero()
    {
        var c = new EdgeRailContour();
        c.Bounds = Program.Board(0, 0, 100, 80);
        AddPt(c, 0, 0); AddPt(c, 100, 0); AddPt(c, 100, 80); AddPt(c, 0, 80);
        Check(Near(EdgeRailContourAnalyzer.DetectCornerRadius(c), 0), "sharp -> 0");
    }
    private static void AddPt(EdgeRailContour c, double x, double y) { c.Points.Add(new EdgeRailPoint(x, y)); c.Bounds.Add(new EdgeRailPoint(x, y)); }
}

namespace EasyEDA_Loader { internal static class EasyEDALoaderModule { internal static void Trace(string m) { Console.Error.WriteLine(m); } } }
