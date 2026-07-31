using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyEDA_Loader
{
    internal sealed class JlcCamPoint
    {
        public JlcCamPoint(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }
        public double DistanceTo(JlcCamPoint other) { return Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y)); }
        public override string ToString() { return X.ToString("0.######", CultureInfo.InvariantCulture) + ", " + Y.ToString("0.######", CultureInfo.InvariantCulture); }
    }

    internal enum JlcCamSide { Top, Bottom }
    internal enum JlcCamSegmentKind { Line, Arc }

    internal sealed class JlcCamAperture
    {
        public int Code { get; set; }
        public string Shape { get; set; }
        public double XSize { get; set; }
        public double YSize { get; set; }
        public bool IsCircular { get { return string.Equals(Shape, "C", StringComparison.OrdinalIgnoreCase); } }
    }

    internal sealed class JlcCamSegment
    {
        public JlcCamSegmentKind Kind { get; set; }
        public JlcCamPoint Start { get; set; }
        public JlcCamPoint End { get; set; }
        public JlcCamPoint Center { get; set; }
        public bool Clockwise { get; set; }
        public int Depth { get; set; }
    }

    internal sealed class JlcCamFlash
    {
        public JlcCamPoint Center { get; set; }
        public JlcCamAperture Aperture { get; set; }
        public int Depth { get; set; }
        public string SourceFile { get; set; }
    }

    internal sealed class JlcCamGerberFile
    {
        public string Path { get; set; }
        public string Units { get; set; }
        public List<JlcCamSegment> Segments { get; } = new List<JlcCamSegment>();
        public List<JlcCamFlash> Flashes { get; } = new List<JlcCamFlash>();
        public List<string> Warnings { get; } = new List<string>();
    }

    internal sealed class JlcCamBounds
    {
        public double MinX { get; set; } = double.PositiveInfinity;
        public double MinY { get; set; } = double.PositiveInfinity;
        public double MaxX { get; set; } = double.NegativeInfinity;
        public double MaxY { get; set; } = double.NegativeInfinity;
        public bool IsEmpty { get { return double.IsInfinity(MinX); } }
        public double Width { get { return IsEmpty ? 0 : MaxX - MinX; } }
        public double Height { get { return IsEmpty ? 0 : MaxY - MinY; } }
        public void Add(JlcCamPoint point)
        {
            if (point == null) return;
            MinX = Math.Min(MinX, point.X); MinY = Math.Min(MinY, point.Y);
            MaxX = Math.Max(MaxX, point.X); MaxY = Math.Max(MaxY, point.Y);
        }
        public bool Contains(JlcCamPoint point, double tolerance)
        {
            return !IsEmpty && point.X >= MinX - tolerance && point.X <= MaxX + tolerance && point.Y >= MinY - tolerance && point.Y <= MaxY + tolerance;
        }
        public override string ToString() { return string.Format(CultureInfo.InvariantCulture, "X {0:0.###}..{1:0.###}, Y {2:0.###}..{3:0.###} mm", MinX, MaxX, MinY, MaxY); }
    }

    internal sealed class JlcCamTransform
    {
        public static readonly JlcCamTransform Identity = new JlcCamTransform();
        public int Rotation { get; set; }
        public bool Mirrored { get; set; }
        public double TranslateX { get; set; }
        public double TranslateY { get; set; }
        public double FitErrorMm { get; set; }
        public JlcCamPoint Apply(JlcCamPoint input)
        {
            if (input == null) return null;
            double x = input.X, y = input.Y;
            if (Mirrored) x = -x;
            switch (((Rotation % 360) + 360) % 360)
            {
                case 90: return new JlcCamPoint(-y + TranslateX, x + TranslateY);
                case 180: return new JlcCamPoint(-x + TranslateX, -y + TranslateY);
                case 270: return new JlcCamPoint(y + TranslateX, -x + TranslateY);
                default: return new JlcCamPoint(x + TranslateX, y + TranslateY);
            }
        }
        public override string ToString() { return string.Format(CultureInfo.InvariantCulture, "rotation {0}°, mirror {1}, X' = … + {2:0.00}, Y' = … + {3:0.00} mm", Rotation, Mirrored ? "yes" : "no", TranslateX, TranslateY); }
    }

    internal sealed class JlcCamHole
    {
        public int Number { get; set; }
        public JlcCamPoint Center { get; set; }
        public double CamDiameterMm { get; set; }
        public double NominalDiameterMm { get; set; }
        public double? TopMaskOpeningMm { get; set; }
        public double? BottomMaskOpeningMm { get; set; }
        public bool Verified { get; set; }
        public string Status { get; set; }
    }

    internal sealed class JlcCamFiducial
    {
        public int Number { get; set; }
        public JlcCamPoint Center { get; set; }
        public JlcCamSide Side { get; set; }
        public double CamDiameterMm { get; set; }
        public double NominalDiameterMm { get; set; }
        public double MaskOpeningMm { get; set; }
        public bool Verified { get; set; }
        public string Status { get; set; }
    }

    internal sealed class JlcCamAnalysisSession : IDisposable
    {
        public string SourcePath { get; set; }
        public string PackageRoot { get; set; }
        public string OriginalOutlinePath { get; set; }
        public string TemporaryRoot { get; set; }
        public JlcCamTransform Transform { get; set; } = JlcCamTransform.Identity;
        public JlcCamBounds OriginalBounds { get; set; } = new JlcCamBounds();
        public JlcCamBounds PanelBounds { get; set; } = new JlcCamBounds();
        public List<JlcCamSegment> OriginalOutline { get; } = new List<JlcCamSegment>();
        public List<JlcCamSegment> RailSegments { get; } = new List<JlcCamSegment>();
        public List<JlcCamHole> Holes { get; } = new List<JlcCamHole>();
        public List<JlcCamFiducial> Fiducials { get; } = new List<JlcCamFiducial>();
        public List<string> Diagnostics { get; } = new List<string>();
        public bool CanImport { get; set; }
        public void Dispose() { JlcCamSource.CleanupTemporaryRoot(TemporaryRoot); TemporaryRoot = null; }
    }

    internal sealed class JlcCamImportOptions
    {
        public bool ImportRails { get; set; } = true;
        public bool ImportFiducials { get; set; } = true;
        public bool ImportHoles { get; set; } = true;
    }

    internal sealed class JlcCamImportResult
    {
        public int RailsImported { get; set; }
        public int HolesImported { get; set; }
        public int FiducialsImported { get; set; }
        public int Skipped { get; set; }
        public List<string> Messages { get; } = new List<string>();
    }
}
