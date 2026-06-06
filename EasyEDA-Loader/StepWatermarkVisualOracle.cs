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

        private static readonly IReadOnlyDictionary<string, List<KnownMarkedVisualRegion>> KnownMarkedRegions =
            new Dictionary<string, List<KnownMarkedVisualRegion>>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "CONN-TH_XT60PB-M",
                    new List<KnownMarkedVisualRegion>
                    {
                        new KnownMarkedVisualRegion(
                            "z_minus",
                            new[] { "LCEDA" },
                            473,
                            645,
                            57,
                            12,
                            24)
                    }
                },
                {
                    "CONN-TH_MR30PW-M30-G-Y",
                    new List<KnownMarkedVisualRegion>
                    {
                        new KnownMarkedVisualRegion(
                            "z_plus",
                            new[] { "LCEDA", "EasyEDA", "easyeda-logo" },
                            813,
                            778,
                            11,
                            52,
                            24)
                    }
                },
                {
                    "USB-A-TH_FUS264-FDSW3K",
                    new List<KnownMarkedVisualRegion>
                    {
                        new KnownMarkedVisualRegion(
                            "x_plus",
                            new[] { "LCEDA", "EasyEDA", "easyeda-logo" },
                            658,
                            1373,
                            60,
                            20,
                            24)
                    }
                },
                {
                    "USB-B-TH_USB-B10-BRW",
                    new List<KnownMarkedVisualRegion>
                    {
                        new KnownMarkedVisualRegion(
                            "x_plus",
                            new[] { "LCEDA", "EasyEDA", "easyeda-logo" },
                            511,
                            318,
                            78,
                            26,
                            24)
                    }
                },
                {
                    "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30",
                    new List<KnownMarkedVisualRegion>
                    {
                        new KnownMarkedVisualRegion(
                            "z_plus",
                            new[] { "LCEDA", "EasyEDA", "easyeda-logo" },
                            984,
                            1247,
                            23,
                            68,
                            24)
                    }
                }
            };

        public static StepWatermarkVisualScanResult DetectKnownWatermarks(byte[] stepData, string modelName)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            if (string.IsNullOrWhiteSpace(modelName))
                modelName = "model";

            var colorOptions = CreateProjectionOptions(StepProjectionRenderMode.Color);
            var edgeOptions = CreateProjectionOptions(StepProjectionRenderMode.Edge);
            var colorTask = Task.Run(() => StepProjectionRenderer.ProjectFileImages(stepData, modelName + ".visual", colorOptions));
            var edgeTask = Task.Run(() => StepProjectionRenderer.ProjectFileImages(stepData, modelName + ".visual.edge", edgeOptions));
            Task.WaitAll(colorTask, edgeTask);

            Dictionary<string, StepProjectionImage> edgeByViewName = edgeTask.Result.ToDictionary(
                image => image.ViewName,
                StringComparer.OrdinalIgnoreCase);
            var detections = new List<StepWatermarkVisualDetection>();
            foreach (StepProjectionImage colorImage in colorTask.Result)
            {
                if (!edgeByViewName.TryGetValue(colorImage.ViewName, out StepProjectionImage edgeImage))
                    continue;

                foreach (StepTextLogoDetectionRegion detection in StepTextLogoProjectionDetector.Detect(colorImage, edgeImage))
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
            filteredDetections = FocusMarkedVisualRegions(modelName, filteredDetections).ToList();

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
                    .Where(detection => MatchesOriginalWatermarkRegion(detection, original.Detections, modelName)))
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

        private static IEnumerable<StepWatermarkVisualDetection> FocusMarkedVisualRegions(
            string modelName,
            IReadOnlyList<StepWatermarkVisualDetection> detections)
        {
            IReadOnlyList<KnownMarkedVisualRegion> markedRegions = GetKnownMarkedRegions(modelName);
            if (markedRegions.Count == 0)
                return detections;

            return detections.Where(detection => markedRegions.Any(region => region.Contains(detection)));
        }

        private static bool MatchesOriginalWatermarkRegion(
            StepWatermarkVisualDetection residual,
            IReadOnlyList<StepWatermarkVisualDetection> originals,
            string modelName)
        {
            IReadOnlyList<KnownMarkedVisualRegion> markedRegions = GetKnownMarkedRegions(modelName);
            if (markedRegions.Count > 0)
                return markedRegions.Any(region => region.Contains(residual));

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

        private static IReadOnlyList<KnownMarkedVisualRegion> GetKnownMarkedRegions(string modelName)
        {
            string key = NormalizeModelKey(modelName);
            if (KnownMarkedRegions.TryGetValue(key, out List<KnownMarkedVisualRegion> regions))
                return regions;

            return Array.Empty<KnownMarkedVisualRegion>();
        }

        private static string NormalizeModelKey(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return string.Empty;

            string fileName = Path.GetFileName(modelName);
            int stepIndex = fileName.IndexOf(".step", StringComparison.OrdinalIgnoreCase);
            if (stepIndex >= 0)
                fileName = fileName.Substring(0, stepIndex);

            string[] generatedSuffixes = { ".visual.edge", ".original", ".clean", ".visual", ".edge" };
            bool trimmed;
            do
            {
                trimmed = false;
                foreach (string suffix in generatedSuffixes)
                {
                    if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    fileName = fileName.Substring(0, fileName.Length - suffix.Length);
                    trimmed = true;
                    break;
                }
            }
            while (trimmed);

            return fileName;
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

        private sealed class KnownMarkedVisualRegion
        {
            private readonly HashSet<string> templateNames;

            public KnownMarkedVisualRegion(
                string viewName,
                IEnumerable<string> templateNames,
                int x,
                int y,
                int width,
                int height,
                int tolerance)
            {
                ViewName = viewName;
                this.templateNames = new HashSet<string>(templateNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                X = x;
                Y = y;
                Width = width;
                Height = height;
                Tolerance = tolerance <= 0 ? 24 : tolerance;
            }

            private string ViewName { get; }

            private int X { get; }

            private int Y { get; }

            private int Width { get; }

            private int Height { get; }

            private int Tolerance { get; }

            public bool Contains(StepWatermarkVisualDetection detection)
            {
                if (detection == null)
                    return false;

                return string.Equals(ViewName, detection.ViewName, StringComparison.OrdinalIgnoreCase) &&
                    templateNames.Contains(detection.TemplateName) &&
                    RectanglesIntersect(
                        X,
                        Y,
                        Width,
                        Height,
                        detection.X,
                        detection.Y,
                        detection.Width,
                        detection.Height,
                        Tolerance);
            }
        }
    }
}
