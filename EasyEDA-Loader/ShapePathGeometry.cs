using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace EasyEDA_Loader
{
    internal sealed class ShapeContour
    {
        public List<EePoint> Points { get; } = new List<EePoint>();
        public bool IsClosed { get; set; }
        public bool IsHole { get; set; }
    }

    internal static class ShapePathGeometry
    {
        public static PathGeometry Parse(string path)
        {
            var geometry = PathGeometry.CreateFromGeometry(Geometry.Parse(path));
            geometry.Freeze();
            return geometry;
        }

        public static List<ShapeContour> Flatten(Geometry geometry, bool outline = false)
        {
            var flat = (outline ? geometry.GetOutlinedPathGeometry() : PathGeometry.CreateFromGeometry(geometry))
                .GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
            var contours = new List<ShapeContour>();
            foreach (var figure in flat.Figures)
            {
                var contour = new ShapeContour { IsClosed = figure.IsClosed };
                Add(contour, figure.StartPoint);
                foreach (var segment in figure.Segments)
                {
                    if (segment is PolyLineSegment polyline)
                        foreach (var point in polyline.Points) Add(contour, point);
                    else if (segment is LineSegment line)
                        Add(contour, line.Point);
                }
                if (contour.Points.Count > 1)
                {
                    if (contour.IsClosed && contour.Points.Count > 2 &&
                        contour.Points[0].X == contour.Points[contour.Points.Count - 1].X &&
                        contour.Points[0].Y == contour.Points[contour.Points.Count - 1].Y)
                        contour.Points.RemoveAt(contour.Points.Count - 1);
                    contours.Add(contour);
                }
            }
            if (outline)
            {
                foreach (var contour in contours)
                {
                    var point = contour.Points[0];
                    contour.IsHole = contours.Count(other => other != contour && Contains(other.Points, point)) % 2 == 1;
                }
            }
            return contours;
        }

        public static IEnumerable<IReadOnlyList<ShapeContour>> SplitRegions(IReadOnlyList<ShapeContour> contours)
        {
            var outers = contours.Where(contour => !contour.IsHole).ToList();
            foreach (var outer in outers)
            {
                var region = new List<ShapeContour> { outer };
                foreach (var hole in contours.Where(contour => contour.IsHole))
                {
                    var owner = outers.Where(candidate => Contains(candidate.Points, hole.Points[0]))
                        .OrderBy(candidate => Math.Abs(Area(candidate.Points))).FirstOrDefault();
                    if (owner == outer) region.Add(hole);
                }
                yield return region;
            }
        }

        private static double Area(IReadOnlyList<EePoint> points)
        {
            double area = 0;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                area += points[j].X * points[i].Y - points[i].X * points[j].Y;
            return area / 2;
        }

        private static void Add(ShapeContour contour, Point point)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new FormatException("Non-finite path coordinate.");
            contour.Points.Add(new EePoint { X = point.X, Y = point.Y });
        }

        private static bool Contains(IReadOnlyList<EePoint> polygon, EePoint point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }
    }
}
