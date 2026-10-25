using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyEDA_Loader
{
    internal sealed class SymbolGraphic
    {
        public List<EePoint> Points { get; } = new List<EePoint>();
        public bool IsClosed { get; set; }
        public bool IsFilled { get; set; }
    }

    internal static class SymbolGraphics
    {
        public static IReadOnlyList<SymbolGraphic> Build(IReadOnlyList<EeSymbolShape> shapes, AltiumSymbolRectangle target)
        {
            var graphics = new List<SymbolGraphic>();
            foreach (var shape in shapes)
            {
                string path;
                string fill;
                if (shape is EeSymbolPolyline line)
                {
                    path = "M" + line.Points + (shape is EeSymbolPolygon ? " Z" : "");
                    fill = line.FillColor;
                }
                else if (shape is EeSymbolPath svg)
                {
                    path = svg.Paths;
                    fill = svg.FillColor;
                }
                else continue;

                bool filled = !string.IsNullOrWhiteSpace(fill) && !string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase) && !string.Equals(fill, "transparent", StringComparison.OrdinalIgnoreCase);
                var contours = ShapePathGeometry.Flatten(ShapePathGeometry.Parse(path), filled);
                bool hasHoles = contours.Any(contour => contour.IsHole);
                foreach (var contour in contours)
                {
                    var graphic = new SymbolGraphic { IsClosed = contour.IsClosed || filled, IsFilled = filled && !hasHoles };
                    graphic.Points.AddRange(contour.Points);
                    graphics.Add(graphic);
                }
            }
            var points = graphics.SelectMany(graphic => graphic.Points).ToList();
            if (points.Count == 0) return graphics;
            double left = points.Min(point => point.X), right = points.Max(point => point.X);
            double top = points.Min(point => point.Y), bottom = points.Max(point => point.Y);
            // Keep graphics between the pin-label lanes at either side of the GOST body.
            double horizontalMargin = target.Width * 0.30;
            double verticalMargin = Math.Min(target.Width, target.Height) * 0.15;
            double width = Math.Max(right - left, 1e-9), height = Math.Max(bottom - top, 1e-9);
            double scale = Math.Min((target.Width - 2 * horizontalMargin) / width, (target.Height - 2 * verticalMargin) / height);
            foreach (var point in points)
            {
                point.X = target.X1 + target.Width / 2 + (point.X - (left + right) / 2) * scale;
                point.Y = target.Y1 + target.Height / 2 + (point.Y - (top + bottom) / 2) * scale;
            }
            return graphics;
        }
    }
}
