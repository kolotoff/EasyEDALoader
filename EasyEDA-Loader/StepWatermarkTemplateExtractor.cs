using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SkiaSharp;

namespace EasyEDA_Loader
{
    public static class StepWatermarkTemplateExtractor
    {
        private const byte EdgeThreshold = 80;

        public static IReadOnlyList<StepWatermarkTemplate> ExtractFromMarkedData(
            string projectionDirectory,
            string markedDirectory,
            IReadOnlyList<StepWatermarkTemplateSource> sources)
        {
            if (string.IsNullOrWhiteSpace(projectionDirectory))
                throw new ArgumentException("Projection directory is required.", nameof(projectionDirectory));
            if (string.IsNullOrWhiteSpace(markedDirectory))
                throw new ArgumentException("Marked directory is required.", nameof(markedDirectory));
            if (sources == null)
                throw new ArgumentNullException(nameof(sources));

            var templates = new List<StepWatermarkTemplate>();
            foreach (StepWatermarkTemplateSource source in sources)
                templates.Add(ExtractTemplate(projectionDirectory, markedDirectory, source));

            return templates;
        }

        private static StepWatermarkTemplate ExtractTemplate(
            string projectionDirectory,
            string markedDirectory,
            StepWatermarkTemplateSource source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(source.TemplateName))
                throw new InvalidDataException("Template source is missing TemplateName.");
            if (string.IsNullOrWhiteSpace(source.MarkedFileName))
                throw new InvalidDataException("Template source " + source.TemplateName + " is missing MarkedFileName.");
            if (string.IsNullOrWhiteSpace(source.ProjectionFileName))
                throw new InvalidDataException("Template source " + source.TemplateName + " is missing ProjectionFileName.");

            string markerPath = Path.Combine(markedDirectory, source.MarkedFileName);
            string projectionPath = Path.Combine(projectionDirectory, source.ProjectionFileName);
            if (!File.Exists(markerPath))
                throw new FileNotFoundException("Marked rectangle JSON was not found.", markerPath);
            if (!File.Exists(projectionPath))
                throw new FileNotFoundException("Edge projection PNG was not found.", projectionPath);

            MarkedRectangle rectangle = ReadRectangle(markerPath, source.RectangleIndex);
            using (SKBitmap image = SKBitmap.Decode(projectionPath))
            {
                if (image == null)
                    throw new InvalidDataException("Could not decode edge projection PNG: " + projectionPath);

                int left = Clamp(rectangle.X, 0, image.Width - 1);
                int top = Clamp(rectangle.Y, 0, image.Height - 1);
                int right = Clamp(rectangle.X + rectangle.Width - 1, 0, image.Width - 1);
                int bottom = Clamp(rectangle.Y + rectangle.Height - 1, 0, image.Height - 1);
                var points = new List<StepWatermarkTemplatePoint>();
                int minX = int.MaxValue;
                int minY = int.MaxValue;
                int maxX = int.MinValue;
                int maxY = int.MinValue;

                for (int y = top; y <= bottom; y++)
                {
                    for (int x = left; x <= right; x++)
                    {
                        if (!IsDarkEdge(image.GetPixel(x, y)))
                            continue;

                        int localX = x - left;
                        int localY = y - top;
                        points.Add(new StepWatermarkTemplatePoint(localX, localY));
                        minX = Math.Min(minX, localX);
                        minY = Math.Min(minY, localY);
                        maxX = Math.Max(maxX, localX);
                        maxY = Math.Max(maxY, localY);
                    }
                }

                if (points.Count == 0)
                    throw new InvalidDataException("Template source " + source.TemplateName + " did not contain dark edge pixels.");

                var trimmed = points
                    .Select(point => new StepWatermarkTemplatePoint(point.X - minX, point.Y - minY))
                    .ToList();

                return new StepWatermarkTemplate
                {
                    Name = source.TemplateName,
                    Kind = string.IsNullOrWhiteSpace(source.Kind) ? "unknown" : source.Kind,
                    Text = source.Text ?? string.Empty,
                    Width = maxX - minX + 1,
                    Height = maxY - minY + 1,
                    EdgePoints = trimmed
                };
            }
        }

        private static MarkedRectangle ReadRectangle(string markerPath, int rectangleIndex)
        {
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(markerPath)))
            {
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("Rectangles", out JsonElement rectangles) ||
                    rectangles.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Marked JSON has no Rectangles array: " + markerPath);
                }

                if (rectangleIndex < 0 || rectangleIndex >= rectangles.GetArrayLength())
                {
                    throw new InvalidDataException(
                        "Marked rectangle index " +
                        rectangleIndex.ToString(CultureInfo.InvariantCulture) +
                        " is out of range for " +
                        markerPath);
                }

                JsonElement rectangle = rectangles.EnumerateArray().Skip(rectangleIndex).First();
                return new MarkedRectangle
                {
                    X = rectangle.GetProperty("X").GetInt32(),
                    Y = rectangle.GetProperty("Y").GetInt32(),
                    Width = rectangle.GetProperty("Width").GetInt32(),
                    Height = rectangle.GetProperty("Height").GetInt32()
                };
            }
        }

        private static bool IsDarkEdge(SKColor color)
        {
            return color.Alpha > 0 &&
                color.Red <= EdgeThreshold &&
                color.Green <= EdgeThreshold &&
                color.Blue <= EdgeThreshold;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private sealed class MarkedRectangle
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }
    }
}
