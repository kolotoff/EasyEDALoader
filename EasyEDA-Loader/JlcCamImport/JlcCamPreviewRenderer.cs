using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EasyEDA_Loader
{
    internal static class JlcCamPreviewRenderer
    {
        private const double Scale = 5.0;
        public static void Render(Canvas canvas, JlcCamAnalysisSession session, JlcCamImportOptions options)
        {
            canvas.Children.Clear(); if (session == null) return;
            JlcCamBounds bounds = session.PanelBounds.IsEmpty ? session.OriginalBounds : session.PanelBounds;
            double ox = -bounds.MinX * Scale + 20, oy = bounds.MaxY * Scale + 20;
            foreach (JlcCamSegment segment in session.OriginalOutline) AddSegment(canvas, segment, ox, oy, Brushes.LightGray, 1.5);
            if (options.ImportRails) foreach (JlcCamSegment segment in session.RailSegments) AddSegment(canvas, segment, ox, oy, Brushes.DodgerBlue, 2);
            if (options.ImportHoles) foreach (JlcCamHole hole in session.Holes) AddCircle(canvas, hole.Center, hole.NominalDiameterMm, ox, oy, Brushes.LightCyan, "Edge hole #" + hole.Number + "\n" + hole.Center + " mm\nNominal " + hole.NominalDiameterMm.ToString("0.###") + " mm");
            if (options.ImportFiducials) foreach (JlcCamFiducial fiducial in session.Fiducials) AddCircle(canvas, fiducial.Center, fiducial.NominalDiameterMm, ox, oy, fiducial.Side == JlcCamSide.Top ? Brushes.OrangeRed : Brushes.MediumSlateBlue, fiducial.Side + " fiducial #" + fiducial.Number + "\n" + fiducial.Center + " mm\nNominal " + fiducial.NominalDiameterMm.ToString("0.###") + " mm");
            canvas.Width = Math.Max(200, bounds.Width * Scale + 40); canvas.Height = Math.Max(200, bounds.Height * Scale + 40);
        }
        private static void AddSegment(Canvas canvas, JlcCamSegment segment, double ox, double oy, Brush colour, double thickness)
        {
            // The preview uses a high-level Path so WPF keeps circular rails smooth at every zoom.
            if (segment.Kind == JlcCamSegmentKind.Arc && segment.Center != null)
            {
                double radius = segment.Center.DistanceTo(segment.Start) * Scale;
                var figure = new PathFigure { StartPoint = P(segment.Start, ox, oy) };
                figure.Segments.Add(new ArcSegment(P(segment.End, ox, oy), new System.Windows.Size(radius, radius), 0, false, segment.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
                var geometry = new PathGeometry(); geometry.Figures.Add(figure);
                canvas.Children.Add(new Path { Data = geometry, Stroke = colour, StrokeThickness = thickness, ToolTip = "JLCCAM rail arc" }); return;
            }
            canvas.Children.Add(new Line { X1 = segment.Start.X * Scale + ox, Y1 = -segment.Start.Y * Scale + oy, X2 = segment.End.X * Scale + ox, Y2 = -segment.End.Y * Scale + oy, Stroke = colour, StrokeThickness = thickness, ToolTip = "JLCCAM rail" });
        }
        private static void AddCircle(Canvas canvas, JlcCamPoint point, double diameter, double ox, double oy, Brush colour, string tooltip)
        {
            double d = Math.Max(5, diameter * Scale); var circle = new Ellipse { Width = d, Height = d, Stroke = colour, StrokeThickness = 2, Fill = Brushes.Transparent, ToolTip = tooltip };
            Canvas.SetLeft(circle, point.X * Scale + ox - d / 2); Canvas.SetTop(circle, -point.Y * Scale + oy - d / 2); canvas.Children.Add(circle);
        }
        private static System.Windows.Point P(JlcCamPoint p, double ox, double oy) { return new System.Windows.Point(p.X * Scale + ox, -p.Y * Scale + oy); }
    }
}
