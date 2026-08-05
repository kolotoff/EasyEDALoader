using System.Collections.Generic;

namespace EasyEDA_Loader
{
    internal enum EdgeRailSegmentKind { Line, Arc }
    internal enum EdgeRailSide { Top, Bottom }

    internal sealed class EdgeRailPoint
    {
        public double X;
        public double Y;
        public EdgeRailPoint(double x, double y) { X = x; Y = y; }
        public double DistanceTo(EdgeRailPoint o) { double dx = X - o.X, dy = Y - o.Y; return System.Math.Sqrt(dx * dx + dy * dy); }
    }

    internal sealed class EdgeRailBounds
    {
        public double MinX = double.PositiveInfinity;
        public double MinY = double.PositiveInfinity;
        public double MaxX = double.NegativeInfinity;
        public double MaxY = double.NegativeInfinity;
        public bool IsEmpty => double.IsInfinity(MinX);
        public double Width => IsEmpty ? 0 : MaxX - MinX;
        public double Height => IsEmpty ? 0 : MaxY - MinY;
        public void Add(EdgeRailPoint p) { if (p == null) return; MinX = System.Math.Min(MinX, p.X); MinY = System.Math.Min(MinY, p.Y); MaxX = System.Math.Max(MaxX, p.X); MaxY = System.Math.Max(MaxY, p.Y); }
    }

    internal sealed class EdgeRailSegment
    {
        public EdgeRailSegmentKind Kind;
        public EdgeRailPoint Start;
        public EdgeRailPoint End;
        public EdgeRailPoint Center;   // arcs only
        public bool Clockwise;         // arcs only
    }

    internal sealed class EdgeRailContour
    {
        public List<EdgeRailPoint> Points = new List<EdgeRailPoint>();   // ordered outer-contour points, absolute mm
        public EdgeRailBounds Bounds = new EdgeRailBounds();
    }

    internal sealed class EdgeRailOptions
    {
        public double HorizontalRailMm = 10.0; // top & bottom
        public double VerticalRailMm = 0.0;    // left & right
        public bool CloseCornerRectangles = true;
        public double HoleSizeMm = 2.0;
        public double FiducialSizeMm = 1.0;
    }

    internal sealed class EdgeRailHole { public EdgeRailPoint Center; public double DiameterMm; }
    internal sealed class EdgeRailFiducial { public EdgeRailPoint Center; public EdgeRailSide Side; public double CopperDiameterMm; public double MaskOpeningMm; }

    internal sealed class EdgeRailPlan
    {
        public List<EdgeRailSegment> RailSegments = new List<EdgeRailSegment>();
        public List<EdgeRailSegment> BoardOutlineSegments = new List<EdgeRailSegment>();
        public List<EdgeRailHole> Holes = new List<EdgeRailHole>();
        public List<EdgeRailFiducial> Fiducials = new List<EdgeRailFiducial>();
        public EdgeRailBounds BoardBounds = new EdgeRailBounds();
        public EdgeRailBounds PanelBounds = new EdgeRailBounds();
        public double BoardCornerRMm;
        public List<string> Diagnostics = new List<string>();
    }
}
