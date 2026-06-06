using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            if (string.IsNullOrWhiteSpace(modelName))
                modelName = "model";

            var colorOptions = CreateProjectionOptions(StepProjectionRenderMode.Color);
            var edgeOptions = CreateProjectionOptions(StepProjectionRenderMode.Edge);
            var logoEdgeOptions = CreateProjectionOptions(StepProjectionRenderMode.EdgeVisibleRaw);
            var colorTask = Task.Run(() => StepProjectionRenderer.ProjectFileImages(stepData, modelName + ".visual", colorOptions));
            var edgeTask = Task.Run(() => StepProjectionRenderer.ProjectFileImages(stepData, modelName + ".visual.edge", edgeOptions));
            var logoEdgeTask = Task.Run(() => StepProjectionRenderer.ProjectFileImages(stepData, modelName + ".visual.logo-edge", logoEdgeOptions));
            Task.WaitAll(colorTask, edgeTask, logoEdgeTask);

            Dictionary<string, StepProjectionImage> edgeByViewName = edgeTask.Result.ToDictionary(
                image => image.ViewName,
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, StepProjectionImage> logoEdgeByViewName = logoEdgeTask.Result.ToDictionary(
                image => image.ViewName,
                StringComparer.OrdinalIgnoreCase);
            var detections = new List<StepWatermarkVisualDetection>();
            foreach (StepProjectionImage colorImage in colorTask.Result)
            {
                if (!edgeByViewName.TryGetValue(colorImage.ViewName, out StepProjectionImage edgeImage))
                    continue;
                logoEdgeByViewName.TryGetValue(colorImage.ViewName, out StepProjectionImage logoEdgeImage);

                foreach (StepTextLogoDetectionRegion detection in StepTextLogoProjectionDetector.Detect(
                    colorImage,
                    edgeImage,
                    logoEdgeImage,
                    new StepTextLogoDetectionOptions { DetectArbitraryText = false }))
                {
                    if (!IsKnownWatermarkDetection(detection))
                        continue;

                    detections.Add(new StepWatermarkVisualDetection
                    {
                        ViewName = colorImage.ViewName,
                        TemplateName = detection.TemplateName,
                        Kind = detection.Kind,
                        Text = detection.Text,
                        X = detection.X,
                        Y = detection.Y,
                        Width = detection.Width,
                        Height = detection.Height,
                        Score = detection.Score,
                        ChamferDistance = detection.ChamferDistance,
                        EdgePixelCount = detection.EdgePixelCount
                    });
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
            StepWatermarkVisualScanResult original = DetectKnownWatermarks(originalStep, modelName + ".original");
            StepWatermarkVisualScanResult clean = DetectKnownWatermarks(cleanStep, modelName + ".clean");
            var failures = new List<string>();

            if (original.Detections.Count == 0)
            {
                failures.Add(modelName + " original model has no known text/logo watermark detections; visual cleanup cannot be verified.");
            }
            else
            {
                foreach (StepWatermarkVisualDetection residual in clean.Detections
                    .Where(detection => detection.Score >= MinimumResidualScore)
                    .Where(detection => MatchesOriginalWatermarkRegion(detection, original.Detections)))
                {
                    failures.Add(modelName + " retains known watermark visual template " + residual.Describe() + ".");
                }
            }

            return new StepWatermarkVisualResidualResult
            {
                OriginalDetections = original.Detections,
                ResidualDetections = clean.Detections
                    .Where(detection => detection.Score >= MinimumResidualScore)
                    .ToList(),
                Failures = failures
            };
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

        private static bool IsKnownWatermarkDetection(StepTextLogoDetectionRegion detection)
        {
            if (detection == null || string.IsNullOrWhiteSpace(detection.TemplateName))
                return false;

            return string.Equals(detection.TemplateName, "LCEDA", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detection.TemplateName, "EasyEDA", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detection.TemplateName, "easyeda-logo", StringComparison.OrdinalIgnoreCase);
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
