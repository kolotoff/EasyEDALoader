using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace EasyEDA_Loader
{
    internal sealed class StepSilhouetteImageRenderOptions
    {
        public int ImageSizePixels { get; set; } = 1600;
        public int PaddingPixels { get; set; } = 90;
        public bool DrawGrid { get; set; } = true;
        public bool DrawAxes { get; set; } = true;
        public string Title { get; set; }
    }

    internal static class StepSilhouetteImageRenderer
    {
        private const double BoundsPaddingRatio = 0.06;
        private const double ArcSampleStepDegrees = 2.0;
        private static readonly SKColor BackgroundColor = SKColors.White;
        private static readonly SKColor GridColor = new SKColor(232, 232, 232);
        private static readonly SKColor AxisColor = new SKColor(176, 176, 176);
        private static readonly SKColor PrimitiveColor = SKColors.Black;
        private static readonly SKColor TextColor = SKColors.Black;

        public static void SaveProjectionPng(
            byte[] stepData,
            StepSilhouettePlacement placement,
            string outputPath,
            StepSilhouetteImageRenderOptions options = null)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(stepData, placement);
            SavePng(primitives, outputPath, options);
        }

        public static byte[] RenderProjectionPng(
            byte[] stepData,
            StepSilhouettePlacement placement,
            StepSilhouetteImageRenderOptions options = null)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(stepData, placement);
            return RenderPng(primitives, options);
        }

        public static void SavePng(
            IReadOnlyList<StepSilhouettePrimitive> primitives,
            string outputPath,
            StepSilhouetteImageRenderOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            string fullPath = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(fullPath, RenderPng(primitives, options));
        }

        public static byte[] RenderPng(
            IReadOnlyList<StepSilhouettePrimitive> primitives,
            StepSilhouetteImageRenderOptions options = null)
        {
            if (primitives == null)
                throw new ArgumentNullException(nameof(primitives));

            options = NormalizeOptions(options);
            int size = options.ImageSizePixels;
            int padding = options.PaddingPixels;
            ProjectionBounds bounds = MeasureBounds(primitives);
            ProjectionTransform transform = ProjectionTransform.Create(bounds, size, padding);

            using (var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))
            using (var canvas = new SKCanvas(bitmap))
            using (var gridPaint = new SKPaint())
            using (var axisPaint = new SKPaint())
            using (var primitivePaint = new SKPaint())
            using (var textPaint = new SKPaint())
            using (var textFont = new SKFont())
            {
                canvas.Clear(BackgroundColor);

                gridPaint.Color = GridColor;
                gridPaint.StrokeWidth = 1.0f;
                gridPaint.IsAntialias = true;
                gridPaint.Style = SKPaintStyle.Stroke;

                axisPaint.Color = AxisColor;
                axisPaint.StrokeWidth = 1.5f;
                axisPaint.IsAntialias = true;
                axisPaint.Style = SKPaintStyle.Stroke;

                primitivePaint.Color = PrimitiveColor;
                primitivePaint.StrokeWidth = Math.Max(2.0f, size / 420.0f);
                primitivePaint.StrokeCap = SKStrokeCap.Round;
                primitivePaint.StrokeJoin = SKStrokeJoin.Round;
                primitivePaint.IsAntialias = true;
                primitivePaint.Style = SKPaintStyle.Stroke;

                textPaint.Color = TextColor;
                textPaint.IsAntialias = true;
                textFont.Size = Math.Max(16.0f, size / 58.0f);

                if (options.DrawGrid)
                    DrawGrid(canvas, transform, bounds, gridPaint);

                if (options.DrawAxes)
                    DrawAxes(canvas, transform, bounds, axisPaint);

                foreach (StepSilhouettePrimitive primitive in primitives)
                    DrawPrimitive(canvas, transform, primitive, primitivePaint);

                DrawHeader(canvas, primitives, options.Title, textFont, textPaint, size);

                using (SKImage image = SKImage.FromBitmap(bitmap))
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 95))
                    return data.ToArray();
            }
        }

        private static StepSilhouetteImageRenderOptions NormalizeOptions(StepSilhouetteImageRenderOptions options)
        {
            options = options ?? new StepSilhouetteImageRenderOptions();
            if (options.ImageSizePixels < 200)
                options.ImageSizePixels = 200;
            if (options.PaddingPixels < 0)
                options.PaddingPixels = 0;
            if (options.PaddingPixels > options.ImageSizePixels / 3)
                options.PaddingPixels = options.ImageSizePixels / 3;
            return options;
        }

        private static void DrawGrid(SKCanvas canvas, ProjectionTransform transform, ProjectionBounds bounds, SKPaint paint)
        {
            double step = NiceGridStep(Math.Max(bounds.Width, bounds.Height) / 12.0);
            if (step <= 0.0)
                return;

            double firstX = Math.Ceiling(bounds.Left / step) * step;
            for (double x = firstX; x <= bounds.Right; x += step)
                canvas.DrawLine(transform.X(x), transform.Y(bounds.Bottom), transform.X(x), transform.Y(bounds.Top), paint);

            double firstY = Math.Ceiling(bounds.Bottom / step) * step;
            for (double y = firstY; y <= bounds.Top; y += step)
                canvas.DrawLine(transform.X(bounds.Left), transform.Y(y), transform.X(bounds.Right), transform.Y(y), paint);
        }

        private static void DrawAxes(SKCanvas canvas, ProjectionTransform transform, ProjectionBounds bounds, SKPaint paint)
        {
            if (bounds.Left <= 0.0 && bounds.Right >= 0.0)
                canvas.DrawLine(transform.X(0.0), transform.Y(bounds.Bottom), transform.X(0.0), transform.Y(bounds.Top), paint);

            if (bounds.Bottom <= 0.0 && bounds.Top >= 0.0)
                canvas.DrawLine(transform.X(bounds.Left), transform.Y(0.0), transform.X(bounds.Right), transform.Y(0.0), paint);
        }

        private static void DrawPrimitive(
            SKCanvas canvas,
            ProjectionTransform transform,
            StepSilhouettePrimitive primitive,
            SKPaint paint)
        {
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
            {
                canvas.DrawLine(
                    transform.X(primitive.X1),
                    transform.Y(primitive.Y1),
                    transform.X(primitive.X2),
                    transform.Y(primitive.Y2),
                    paint);
                return;
            }

            using (var path = new SKPath())
            {
                bool first = true;
                foreach (ProjectionPoint point in SampleArc(primitive))
                {
                    float x = transform.X(point.X);
                    float y = transform.Y(point.Y);
                    if (first)
                    {
                        path.MoveTo(x, y);
                        first = false;
                    }
                    else
                    {
                        path.LineTo(x, y);
                    }
                }

                if (!first)
                    canvas.DrawPath(path, paint);
            }
        }

        private static void DrawHeader(
            SKCanvas canvas,
            IReadOnlyList<StepSilhouettePrimitive> primitives,
            string title,
            SKFont font,
            SKPaint paint,
            int imageSizePixels)
        {
            int lines = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Line);
            int arcs = primitives.Count - lines;
            string stats =
                lines.ToString(CultureInfo.InvariantCulture) +
                " lines, " +
                arcs.ToString(CultureInfo.InvariantCulture) +
                " arcs";
            string text = string.IsNullOrWhiteSpace(title) ? stats : title + " - " + stats;

            canvas.DrawText(
                text,
                Math.Max(12.0f, imageSizePixels / 80.0f),
                Math.Max(28.0f, imageSizePixels / 38.0f),
                SKTextAlign.Left,
                font,
                paint);
        }

        private static ProjectionBounds MeasureBounds(IReadOnlyList<StepSilhouettePrimitive> primitives)
        {
            var bounds = new ProjectionBounds();
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    bounds.Include(primitive.X1, primitive.Y1);
                    bounds.Include(primitive.X2, primitive.Y2);
                }
                else
                {
                    foreach (ProjectionPoint point in SampleArc(primitive))
                        bounds.Include(point.X, point.Y);
                }
            }

            if (!bounds.HasPoint)
            {
                bounds.Include(-5.0, -5.0);
                bounds.Include(5.0, 5.0);
            }

            double pad = Math.Max(bounds.Width, bounds.Height) * BoundsPaddingRatio;
            if (pad <= 0.0)
                pad = 1.0;

            bounds.Left -= pad;
            bounds.Right += pad;
            bounds.Bottom -= pad;
            bounds.Top += pad;

            if (bounds.Width <= 0.0)
            {
                bounds.Left -= 1.0;
                bounds.Right += 1.0;
            }

            if (bounds.Height <= 0.0)
            {
                bounds.Bottom -= 1.0;
                bounds.Top += 1.0;
            }

            return bounds;
        }

        private static IEnumerable<ProjectionPoint> SampleArc(StepSilhouettePrimitive primitive)
        {
            double sweep = NormalizeSweep(primitive.StartAngle, primitive.EndAngle);
            int steps = Math.Max(2, (int)Math.Ceiling(sweep / ArcSampleStepDegrees));
            for (int index = 0; index <= steps; index++)
            {
                double angle = primitive.StartAngle + sweep * index / steps;
                double radians = angle * Math.PI / 180.0;
                yield return new ProjectionPoint(
                    primitive.CenterX + Math.Cos(radians) * primitive.Radius,
                    primitive.CenterY + Math.Sin(radians) * primitive.Radius);
            }
        }

        private static double NormalizeSweep(double startAngle, double endAngle)
        {
            double sweep = endAngle - startAngle;
            while (sweep < 0.0)
                sweep += 360.0;
            while (sweep > 360.0)
                sweep -= 360.0;
            if (Math.Abs(sweep) < 1e-9 && Math.Abs(endAngle - startAngle) > 1e-9)
                return 360.0;
            return sweep;
        }

        private static double NiceGridStep(double value)
        {
            if (value <= 0.0)
                return 1.0;

            double exponent = Math.Pow(10.0, Math.Floor(Math.Log10(value)));
            double normalized = value / exponent;
            double nice;
            if (normalized <= 1.0)
                nice = 1.0;
            else if (normalized <= 2.0)
                nice = 2.0;
            else if (normalized <= 5.0)
                nice = 5.0;
            else
                nice = 10.0;

            return nice * exponent;
        }

        private sealed class ProjectionBounds
        {
            public bool HasPoint { get; private set; }
            public double Left { get; set; }
            public double Bottom { get; set; }
            public double Right { get; set; }
            public double Top { get; set; }
            public double Width => Right - Left;
            public double Height => Top - Bottom;

            public void Include(double x, double y)
            {
                if (!HasPoint)
                {
                    Left = x;
                    Right = x;
                    Bottom = y;
                    Top = y;
                    HasPoint = true;
                    return;
                }

                if (x < Left)
                    Left = x;
                if (x > Right)
                    Right = x;
                if (y < Bottom)
                    Bottom = y;
                if (y > Top)
                    Top = y;
            }
        }

        private sealed class ProjectionTransform
        {
            private readonly int _imageSizePixels;
            private readonly int _paddingPixels;
            private readonly double _scale;
            private readonly ProjectionBounds _bounds;

            private ProjectionTransform(ProjectionBounds bounds, int imageSizePixels, int paddingPixels)
            {
                _bounds = bounds;
                _imageSizePixels = imageSizePixels;
                _paddingPixels = paddingPixels;
                double drawable = Math.Max(1.0, imageSizePixels - paddingPixels * 2.0);
                _scale = drawable / Math.Max(bounds.Width, bounds.Height);
            }

            public static ProjectionTransform Create(ProjectionBounds bounds, int imageSizePixels, int paddingPixels)
            {
                return new ProjectionTransform(bounds, imageSizePixels, paddingPixels);
            }

            public float X(double x)
            {
                double drawingWidth = _bounds.Width * _scale;
                double offset = (_imageSizePixels - drawingWidth) / 2.0;
                return (float)(offset + (x - _bounds.Left) * _scale);
            }

            public float Y(double y)
            {
                double drawingHeight = _bounds.Height * _scale;
                double offset = (_imageSizePixels - drawingHeight) / 2.0;
                return (float)(_imageSizePixels - offset - (y - _bounds.Bottom) * _scale);
            }
        }

        private readonly struct ProjectionPoint
        {
            public ProjectionPoint(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }
    }
}
