using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyEDA_Loader
{
    public enum StepVectorWatermarkPrimitiveKind
    {
        Line,
        Arc,
        Polyline
    }

    public sealed class StepVectorWatermarkPoint
    {
        public StepVectorWatermarkPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    public sealed class StepVectorWatermarkBounds
    {
        public double Left { get; internal set; }
        public double Bottom { get; internal set; }
        public double Right { get; internal set; }
        public double Top { get; internal set; }
        public double Width => Math.Abs(Right - Left);
        public double Height => Math.Abs(Top - Bottom);
    }

    public sealed class StepVectorWatermarkImageMapping
    {
        public int ImageWidth { get; internal set; }
        public int ImageHeight { get; internal set; }
        public int PaddingPixels { get; internal set; }
        public double UMin { get; internal set; }
        public double UMax { get; internal set; }
        public double VMin { get; internal set; }
        public double VMax { get; internal set; }
        public double Scale { get; internal set; }

        public double ProjectX(double u)
        {
            return PaddingPixels + (u - UMin) * Scale;
        }

        public double ProjectY(double v)
        {
            return ImageHeight - PaddingPixels - (v - VMin) * Scale;
        }
    }

    public sealed class StepVectorWatermarkPrimitive
    {
        public StepVectorWatermarkPrimitiveKind Kind { get; internal set; }
        public string Visibility { get; internal set; }
        public string Category { get; internal set; }
        public int SourceIndex { get; internal set; }
        public string OriginalKind { get; internal set; }
        public int? FaceId { get; internal set; }
        public int? BoundId { get; internal set; }
        public int? EdgeCurveId { get; internal set; }
        public double CenterX { get; internal set; }
        public double CenterY { get; internal set; }
        public double Radius { get; internal set; }
        public double StartAngle { get; internal set; }
        public double EndAngle { get; internal set; }
        public IReadOnlyList<StepVectorWatermarkPoint> SampledPoints { get; internal set; } =
            Array.Empty<StepVectorWatermarkPoint>();
        public IReadOnlyList<StepVectorWatermarkPoint> SampledImagePoints { get; internal set; } =
            Array.Empty<StepVectorWatermarkPoint>();
        public StepVectorWatermarkBounds Bounds { get; internal set; }
        public StepVectorWatermarkBounds ImageBounds { get; internal set; }
    }

    public sealed class StepVectorWatermarkDetectionInput
    {
        public string ModelName { get; internal set; }
        public string ViewName { get; internal set; }
        public int ImageWidth { get; internal set; }
        public int ImageHeight { get; internal set; }
        public StepVectorWatermarkImageMapping ImageMapping { get; internal set; }
        public IReadOnlyList<StepVectorWatermarkPrimitive> Primitives { get; internal set; } =
            Array.Empty<StepVectorWatermarkPrimitive>();
    }

    public sealed class StepVectorWatermarkDetectionRegion
    {
        public string TemplateName { get; internal set; }
        public string Kind { get; internal set; }
        public string Text { get; internal set; }
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int OrientationDegrees { get; internal set; }
        public int LogoOrientationDegrees { get; internal set; }
        public int TextOrientationDegrees { get; internal set; }
        public double Score { get; internal set; }
        public double ChamferDistance { get; internal set; }
        public int PrimitiveCount { get; internal set; }
        public IReadOnlyList<int> PrimitiveSourceIndices { get; internal set; } =
            Array.Empty<int>();
    }

    public static class StepVectorWatermarkProjectionDetector
    {
        public static IReadOnlyList<StepVectorWatermarkDetectionRegion> Detect(
            StepVectorWatermarkDetectionInput input,
            StepTextLogoDetectionOptions options)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            options = options ?? new StepTextLogoDetectionOptions();
            IReadOnlyList<StepVectorWatermarkDetectionRegion> logoDetections =
                StepVectorLogoDetector.Detect(input, options);
            IReadOnlyList<StepVectorWatermarkDetectionRegion> textDetections =
                StepVectorTextDetector.Detect(input, options);

            IReadOnlyList<StepVectorWatermarkDetectionRegion> combined =
                StepVectorWatermarkRegionCombiner.Combine(
                logoDetections,
                textDetections,
                input,
                options);
            if (combined.Count > 0 || options.DetectArbitraryText)
                return combined;

            var fallbackOptions = new StepTextLogoDetectionOptions
            {
                DetectArbitraryText = true,
                MinimumRegionWidth = options.MinimumRegionWidth,
                MinimumRegionHeight = options.MinimumRegionHeight,
                MinimumEdgePixels = options.MinimumEdgePixels,
                MinimumKnownTemplateScore = options.MinimumKnownTemplateScore,
                MinimumArbitraryTextScore = Math.Max(0.70, options.MinimumArbitraryTextScore),
                MaximumRegionExpansionRatio = options.MaximumRegionExpansionRatio,
                IncludeCombinedWatermarkRegion = options.IncludeCombinedWatermarkRegion
            };
            IReadOnlyList<StepVectorWatermarkDetectionRegion> fallbackText =
                StepVectorTextDetector.Detect(input, fallbackOptions)
                    .Where(IsHighConfidenceManufacturerFallback)
                    .ToList();
            return StepVectorWatermarkRegionCombiner.Combine(
                Array.Empty<StepVectorWatermarkDetectionRegion>(),
                fallbackText,
                input,
                fallbackOptions);
        }

        private static bool IsHighConfidenceManufacturerFallback(
            StepVectorWatermarkDetectionRegion detection)
        {
            return detection != null &&
                string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(detection.TemplateName, "vector-arbitrary-text", StringComparison.OrdinalIgnoreCase) &&
                detection.Score >= 70.0 &&
                detection.Width <= 460 &&
                detection.Height <= 230 &&
                detection.PrimitiveCount >= 8;
        }
    }
}
