using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EasyEDA_Loader
{
    internal static class EdgeRailsPreviewRenderer
    {
        private const double Scale = 5.0;

        public static void Render(Canvas canvas, EdgeRailPlan plan)
        {
            canvas.Children.Clear();
            if (plan == null) return;
            EdgeRailBounds bounds = plan.PanelBounds.IsEmpty ? plan.BoardBounds : plan.PanelBounds;
            if (bounds == null || bounds.IsEmpty) return;
            double ox = -bounds.MinX * Scale + 20, oy = bounds.MaxY * Scale + 20;
            foreach (EdgeRailSegment segment in plan.BoardOutlineSegments) AddSegment(canvas, segment, ox, oy, Brushes.LightGray, 1.5);
            foreach (EdgeRailSegment segment in plan.RailSegments) AddSegment(canvas, segment, ox, oy, Brushes.DodgerBlue, 2);
            foreach (EdgeRailHole hole in plan.Holes) AddCircle(canvas, hole.Center, hole.DiameterMm, ox, oy, Brushes.LightCyan, "Panel hole\n" + hole.Center.X.ToString("0.###") + ", " + hole.Center.Y.ToString("0.###") + " mm\nØ " + hole.DiameterMm.ToString("0.###") + " mm");
            foreach (EdgeRailFiducial fiducial in plan.Fiducials) AddCircle(canvas, fiducial.Center, fiducial.CopperDiameterMm, ox, oy, fiducial.Side == EdgeRailSide.Top ? Brushes.OrangeRed : Brushes.MediumSlateBlue, fiducial.Side + " fiducial\n" + fiducial.Center.X.ToString("0.###") + ", " + fiducial.Center.Y.ToString("0.###") + " mm\nØ " + fiducial.CopperDiameterMm.ToString("0.###") + " mm");
            canvas.Width = Math.Max(200, bounds.Width * Scale + 40);
            canvas.Height = Math.Max(200, bounds.Height * Scale + 40);
        }

        private static void AddSegment(Canvas canvas, EdgeRailSegment segment, double ox, double oy, Brush colour, double thickness)
        {
            if (segment.Kind == EdgeRailSegmentKind.Arc && segment.Center != null && segment.Start != null && segment.End != null)
            {
                // Start/End are the two corner tangents and Center is the true centre (set by the generator).
                double radius = segment.Center.DistanceTo(segment.Start) * Scale;
                var figure = new PathFigure { StartPoint = P(segment.Start, ox, oy) };
                figure.Segments.Add(new ArcSegment(P(segment.End, ox, oy), new System.Windows.Size(radius, radius), 0, false, segment.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
                var geometry = new PathGeometry(); geometry.Figures.Add(figure);
                canvas.Children.Add(new Path { Data = geometry, Stroke = colour, StrokeThickness = thickness, ToolTip = "Edge rail arc" });
                return;
            }
            if (segment.Start == null || segment.End == null) return;
            canvas.Children.Add(new Line { X1 = segment.Start.X * Scale + ox, Y1 = -segment.Start.Y * Scale + oy, X2 = segment.End.X * Scale + ox, Y2 = -segment.End.Y * Scale + oy, Stroke = colour, StrokeThickness = thickness, ToolTip = "Edge rail" });
        }

        private static void AddCircle(Canvas canvas, EdgeRailPoint point, double diameter, double ox, double oy, Brush colour, string tooltip)
        {
            double d = Math.Max(5, diameter * Scale);
            var circle = new Ellipse { Width = d, Height = d, Stroke = colour, StrokeThickness = 2, Fill = Brushes.Transparent, ToolTip = tooltip };
            Canvas.SetLeft(circle, point.X * Scale + ox - d / 2);
            Canvas.SetTop(circle, -point.Y * Scale + oy - d / 2);
            canvas.Children.Add(circle);
        }

        private static System.Windows.Point P(EdgeRailPoint p, double ox, double oy) { return new System.Windows.Point(p.X * Scale + ox, -p.Y * Scale + oy); }
    }
}
