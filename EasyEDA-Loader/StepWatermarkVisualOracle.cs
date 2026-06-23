using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyEDA_Loader
{
    public sealed class StepWatermarkVisualDetection
    {
        public string ViewName { get; internal set; }
        public string TemplateName { get; internal set; }
        public string Kind { get; internal set; }
        public string Text { get; internal set; }
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public double Score { get; internal set; }
        public double ChamferDistance { get; internal set; }
        public int EdgePixelCount { get; internal set; }

        public string Describe()
        {
            return
                ViewName +
                ":" +
                TemplateName +
                " @ " +
                X.ToString(CultureInfo.InvariantCulture) +
                "," +
                Y.ToString(CultureInfo.InvariantCulture) +
                " " +
                Width.ToString(CultureInfo.InvariantCulture) +
                "x" +
                Height.ToString(CultureInfo.InvariantCulture) +
                " score=" +
                Score.ToString("G4", CultureInfo.InvariantCulture) +
                " chamfer=" +
                ChamferDistance.ToString("G4", CultureInfo.InvariantCulture) +
                " edges=" +
                EdgePixelCount.ToString(CultureInfo.InvariantCulture);
        }
    }

    public sealed class StepWatermarkVisualScanResult
    {
        public IReadOnlyList<StepWatermarkVisualDetection> Detections { get; internal set; } =
            Array.Empty<StepWatermarkVisualDetection>();

        public bool HasKnownWatermark => Detections.Count > 0;
    }

    public sealed class StepWatermarkVisualResidualResult
    {
        public IReadOnlyList<StepWatermarkVisualDetection> OriginalDetections { get; internal set; } =
            Array.Empty<StepWatermarkVisualDetection>();

        public IReadOnlyList<StepWatermarkVisualDetection> ResidualDetections { get; internal set; } =
            Array.Empty<StepWatermarkVisualDetection>();

        public IReadOnlyList<string> Failures { get; internal set; } = Array.Empty<string>();

        public bool Passed => Failures.Count == 0;
    }

    public static class StepWatermarkVisualOracle
    {
        private const double MinimumOriginalScore = 7.5;
        private const double MinimumResidualScore = 7.5;

        public static StepWatermarkVisualScanResult DetectKnownWatermarks(byte[] stepData, string modelName)
        {
            return DetectKnownWatermarks(stepData, modelName, StepProjectionRenderer.ViewNames);
        }

        public static StepWatermarkVisualScanResult DetectKnownWatermarks(
            byte[] stepData,
            string modelName,
            IReadOnlyCollection<string> viewNames)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            if (string.IsNullOrWhiteSpace(modelName))
                modelName = "model";
            if (viewNames == null || viewNames.Count == 0)
                viewNames = StepProjectionRenderer.ViewNames;

            IReadOnlyDictionary<string, StepVectorWatermarkDetectionInput> inputsByView =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInputs(
                    stepData,
                    modelName + ".visual",
                    viewNames);

            var detections = new List<StepWatermarkVisualDetection>();
            foreach (KeyValuePair<string, StepVectorWatermarkDetectionInput> inputByView in inputsByView)
            {
                IReadOnlyList<StepVectorWatermarkDetectionRegion> vectorDetections =
                    StepVectorWatermarkProjectionDetector.Detect(
                        inputByView.Value,
                        new StepTextLogoDetectionOptions { DetectArbitraryText = false });
                foreach (StepVectorWatermarkDetectionRegion vectorDetection in vectorDetections)
                {
                    StepWatermarkVisualDetection detection = ToVisualDetection(inputByView.Key, vectorDetection);
                    if (!IsKnownWatermarkDetection(detection))
                        continue;

                    detections.Add(detection);
                }
            }

            List<StepWatermarkVisualDetection> filteredDetections = detections
                .Where(detection => detection.Score >= MinimumOriginalScore)
                .OrderBy(detection => detection.ViewName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(detection => detection.Score)
                .ToList();

            return new StepWatermarkVisualScanResult
            {
                Detections = filteredDetections
            };
        }

        public static StepWatermarkVisualResidualResult VerifyKnownWatermarkRemoved(
            byte[] originalStep,
            byte[] cleanStep,
            string modelName)
        {
            return VerifyKnownWatermarkRemoved(originalStep, cleanStep, modelName, StepProjectionRenderer.ViewNames);
        }

        public static StepWatermarkVisualResidualResult VerifyKnownWatermarkRemoved(
            byte[] originalStep,
            byte[] cleanStep,
            string modelName,
            IReadOnlyCollection<string> viewNames)
        {
            StepWatermarkVisualScanResult original = DetectKnownWatermarks(originalStep, modelName + ".original", viewNames);
            return VerifyKnownWatermarkRemoved(original.Detections, cleanStep, modelName, viewNames);
        }

        public static StepWatermarkVisualResidualResult VerifyKnownWatermarkRemoved(
            IReadOnlyList<StepWatermarkVisualDetection> originalDetections,
            byte[] cleanStep,
            string modelName,
            IReadOnlyCollection<string> viewNames)
        {
            originalDetections = originalDetections ?? Array.Empty<StepWatermarkVisualDetection>();
            StepWatermarkVisualScanResult clean = DetectKnownWatermarks(cleanStep, modelName + ".clean", viewNames);
            var failures = new List<string>();

            if (originalDetections.Count == 0)
            {
                failures.Add(modelName + " original model has no known text/logo watermark detections; visual cleanup cannot be verified.");
            }
            else
            {
                foreach (StepWatermarkVisualDetection residual in clean.Detections
                    .Where(detection => detection.Score >= MinimumResidualScore)
                    .Where(detection => MatchesOriginalWatermarkRegion(detection, originalDetections)))
                {
                    failures.Add(modelName + " retains known watermark visual template " + residual.Describe() + ".");
                }
            }

            return new StepWatermarkVisualResidualResult
            {
                OriginalDetections = originalDetections,
                ResidualDetections = clean.Detections
                    .Where(detection => detection.Score >= MinimumResidualScore)
                    .ToList(),
                Failures = failures
            };
        }

        public static IReadOnlyList<StepWatermarkVisualDetection> CreateOriginalDetections(
            StepWatermarkDetectionReport detectionReport,
            IReadOnlyCollection<string> viewNames)
        {
            if (detectionReport?.Regions == null || detectionReport.Regions.Count == 0)
                return Array.Empty<StepWatermarkVisualDetection>();

            HashSet<string> selectedViewNames = viewNames == null || viewNames.Count == 0
                ? null
                : new HashSet<string>(viewNames, StringComparer.OrdinalIgnoreCase);
            var detections = new List<StepWatermarkVisualDetection>();
            foreach (StepWatermarkRegionDetection region in detectionReport.Regions)
            {
                if (region == null ||
                    !region.RectangleX.HasValue ||
                    !region.RectangleY.HasValue ||
                    !region.RectangleWidth.HasValue ||
                    !region.RectangleHeight.HasValue ||
                    string.IsNullOrWhiteSpace(region.ViewName) ||
                    selectedViewNames != null && !selectedViewNames.Contains(region.ViewName))
                {
                    continue;
                }

                var detection = new StepWatermarkVisualDetection
                {
                    ViewName = region.ViewName,
                    TemplateName = region.TemplateName,
                    Kind = region.Kind,
                    Text = region.Text,
                    X = region.RectangleX.Value,
                    Y = region.RectangleY.Value,
                    Width = region.RectangleWidth.Value,
                    Height = region.RectangleHeight.Value,
                    Score = region.Score,
                    ChamferDistance = region.ChamferDistance,
                    EdgePixelCount = region.EdgePixelCount
                };
                if (IsKnownWatermarkDetection(detection))
                    detections.Add(detection);
            }

            return detections
                .OrderBy(detection => detection.ViewName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(detection => detection.Score)
                .ToList();
        }

        public static StepProjectionOptions CreateProjectionOptions(StepProjectionRenderMode renderMode)
        {
            var options = new StepProjectionOptions
            {
                ImageSizePixels = 1600,
                PaddingPixels = 80,
                WriteMetadata = false,
                RenderMode = renderMode
            };

            foreach (string viewName in StepProjectionRenderer.ViewNames)
                options.ViewNames.Add(viewName);

            return options;
        }

        private static StepWatermarkVisualDetection ToVisualDetection(
            string viewName,
            StepVectorWatermarkDetectionRegion detection)
        {
            return new StepWatermarkVisualDetection
            {
                ViewName = viewName,
                TemplateName = detection.TemplateName,
                Kind = detection.Kind,
                Text = detection.Text,
                X = detection.X,
                Y = detection.Y,
                Width = detection.Width,
                Height = detection.Height,
                Score = detection.Score,
                ChamferDistance = detection.ChamferDistance,
                EdgePixelCount = detection.PrimitiveCount
            };
        }

        private static bool IsKnownWatermarkDetection(StepWatermarkVisualDetection detection)
        {
            if (detection == null)
                return false;

            if (string.Equals(detection.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrWhiteSpace(detection.TemplateName))
                return false;

            string templateName = detection.TemplateName;
            return templateName.IndexOf("LCEDA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                templateName.IndexOf("EasyEDA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                templateName.IndexOf("easyeda-logo", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesOriginalWatermarkRegion(
            StepWatermarkVisualDetection residual,
            IReadOnlyList<StepWatermarkVisualDetection> originals)
        {
            return originals.Any(original =>
                string.Equals(original.ViewName, residual.ViewName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(original.TemplateName, residual.TemplateName, StringComparison.OrdinalIgnoreCase) &&
                RectanglesIntersect(
                    original.X,
                    original.Y,
                    original.Width,
                    original.Height,
                    residual.X,
                    residual.Y,
                    residual.Width,
                    residual.Height,
                    padding: 36));
        }

        private static bool RectanglesIntersect(
            int leftX,
            int leftY,
            int leftWidth,
            int leftHeight,
            int rightX,
            int rightY,
            int rightWidth,
            int rightHeight,
            int padding)
        {
            int leftRight = leftX + leftWidth - 1;
            int leftBottom = leftY + leftHeight - 1;
            int rightRight = rightX + rightWidth - 1;
            int rightBottom = rightY + rightHeight - 1;
            return leftX <= rightRight + padding &&
                leftRight + padding >= rightX &&
                leftY <= rightBottom + padding &&
                leftBottom + padding >= rightY;
        }
    }
}
