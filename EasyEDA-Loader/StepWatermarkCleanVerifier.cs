using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    public sealed class StepWatermarkCleanFailedException : Exception
    {
        public StepWatermarkCleanFailedException(string message, string reportPath, IReadOnlyList<string> failures)
            : base(message)
        {
            ReportPath = reportPath;
            Failures = failures ?? Array.Empty<string>();
        }

        public string ReportPath { get; }
        public IReadOnlyList<string> Failures { get; }
    }

    public sealed class StepWatermarkCleanVerifierResult
    {
        public byte[] CleanStep { get; internal set; }
        public StepWatermarkCleanerReport CleanReport { get; internal set; }
    }

    public static class StepWatermarkCleanVerifier
    {
        private const int ProjectionDifferenceTolerance = 6;
        private const int AllowedDetectionRegionPaddingPixels = 10;
        private const double MaxOutsideDetectionRegionChangeRatio = 0.005;
        private const int VerificationProjectionImageSizePixels = 1000;
        private const int VerificationProjectionPaddingPixels = 50;
        private const int FlatnessEdgeThreshold = 28;
        private const double MaxCleanedRegionEdgeRatio = 0.035;
        private const double MaxRetainedRegionEdgeRatio = 0.45;

        public static byte[] CleanOrThrow(byte[] originalStep, string modelName, string verificationDirectory, bool cleanText = false)
        {
            return CleanOrThrowWithReport(originalStep, modelName, verificationDirectory, cleanText).CleanStep;
        }

        public static StepWatermarkCleanVerifierResult CleanOrThrowWithReport(byte[] originalStep, string modelName, string verificationDirectory, bool cleanText = false)
        {
            if (originalStep == null)
                throw new ArgumentNullException(nameof(originalStep));
            if (string.IsNullOrWhiteSpace(modelName))
                modelName = "model";
            if (string.IsNullOrWhiteSpace(verificationDirectory))
                throw new ArgumentException("Verification directory is required.", nameof(verificationDirectory));

            Directory.CreateDirectory(verificationDirectory);

            var cleanReport = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalStep),
                new StepWatermarkCleanerOptions
                {
                    CleanText = cleanText
                });
            byte[] cleanStep = Encoding.Latin1.GetBytes(cleanReport.CleanedStep);

            string safeModelName = SanitizeFileName(Path.GetFileNameWithoutExtension(modelName));
            string originalPath = Path.Combine(verificationDirectory, safeModelName + ".original.step");
            string cleanPath = Path.Combine(verificationDirectory, safeModelName + ".clean.step");
            File.WriteAllBytes(originalPath, originalStep);
            File.WriteAllBytes(cleanPath, cleanStep);

            var verification = VerifyPostCleanOutput(originalPath, cleanPath, cleanReport.DetectionReport, verificationDirectory);
            if (!verification.Passed)
            {
                string message = "STEP watermark cleanup failed post-clean projection verification. Report: " +
                    Path.GetFullPath(verification.ReportPath);
                throw new StepWatermarkCleanFailedException(message, verification.ReportPath, verification.Failures);
            }

            return new StepWatermarkCleanVerifierResult
            {
                CleanStep = cleanStep,
                CleanReport = cleanReport
            };
        }

        private static PostCleanVerificationResult VerifyPostCleanOutput(
            string originalPath,
            string cleanPath,
            StepWatermarkDetectionReport detectionReport,
            string verificationDirectory)
        {
            var result = new PostCleanVerificationResult
            {
                ReportPath = Path.Combine(verificationDirectory, "FailedProjectionReport.md"),
                ReportDirectory = Path.Combine(verificationDirectory, "FailedProjectionReport")
            };

            Directory.CreateDirectory(verificationDirectory);
            string originalProjectionDirectory = Path.Combine(verificationDirectory, "OriginalProjection");
            string cleanProjectionDirectory = Path.Combine(verificationDirectory, "CleanProjection");
            Directory.CreateDirectory(originalProjectionDirectory);
            Directory.CreateDirectory(cleanProjectionDirectory);

            var projectionOptions = CreateVerificationProjectionOptions();
            var detectionRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    originalPath,
                    detectionReport,
                    projectionOptions)
                .ToList();

            string[] detectedViewNames = detectionRegions
                .Select(region => region.ViewName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (detectedViewNames.Length > 0)
            {
                var renderOptions = CreateProjectionOptionsForViews(detectedViewNames, projectionOptions);
                var originalProjectionTask = Task.Run(() => StepProjectionRenderer.ProjectFile(originalPath, originalProjectionDirectory, renderOptions));
                var cleanProjectionTask = Task.Run(() => StepProjectionRenderer.ProjectFile(cleanPath, cleanProjectionDirectory, renderOptions));
                Task.WaitAll(originalProjectionTask, cleanProjectionTask);

                string originalModelName = Path.GetFileNameWithoutExtension(originalPath);
                string cleanModelName = Path.GetFileNameWithoutExtension(cleanPath);
                foreach (string viewName in detectedViewNames)
                {
                    string originalProjectionPath = Path.Combine(originalProjectionDirectory, originalModelName + "__" + viewName + ".png");
                    string cleanProjectionPath = Path.Combine(cleanProjectionDirectory, cleanModelName + "__" + viewName + ".png");
                    var viewRegions = detectionRegions
                        .Where(region => string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    VerifyPostCleanProjectionImage(
                        Path.GetFileName(originalPath),
                        viewName,
                        originalProjectionPath,
                        cleanProjectionPath,
                        viewRegions,
                        result);
                }
            }

            WriteFailedProjectionReport(result.ReportPath, result.ReportDirectory, result.VisualFailures);
            return result;
        }

        private static void VerifyPostCleanProjectionImage(
            string fileName,
            string viewName,
            string originalProjectionPath,
            string cleanProjectionPath,
            IReadOnlyList<StepProjectionDetectionRegion> detectionRegions,
            PostCleanVerificationResult result)
        {
            using (var originalImage = SKBitmap.Decode(originalProjectionPath))
            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            {
                if (originalImage == null || cleanImage == null)
                {
                    result.Failures.Add(fileName + " has an unreadable original or clean projection on " + viewName + ".");
                    return;
                }

                if (originalImage.Width != cleanImage.Width || originalImage.Height != cleanImage.Height)
                {
                    result.Failures.Add(fileName + " original and clean projections have different sizes on " + viewName + ".");
                    return;
                }

                bool[] allowedMask = BuildAllowedChangeMask(
                    originalImage.Width,
                    originalImage.Height,
                    detectionRegions,
                    AllowedDetectionRegionPaddingPixels);

                int changedOutsideRegion = 0;
                int firstOutsideX = -1;
                int firstOutsideY = -1;

                for (int y = 0; y < originalImage.Height; y++)
                {
                    int row = y * originalImage.Width;
                    for (int x = 0; x < originalImage.Width; x++)
                    {
                        if (!PixelsDifferent(originalImage.GetPixel(x, y), cleanImage.GetPixel(x, y), ProjectionDifferenceTolerance))
                            continue;

                        if (allowedMask[row + x])
                            continue;

                        changedOutsideRegion++;
                        if (firstOutsideX < 0)
                        {
                            firstOutsideX = x;
                            firstOutsideY = y;
                        }
                    }
                }

                int allowedOutsideRegionChanges = GetAllowedOutsideRegionChanges(originalImage.Width, originalImage.Height);
                if (changedOutsideRegion > allowedOutsideRegionChanges)
                {
                    string message =
                        fileName +
                        " changed outside detected cleanup region on " +
                        viewName +
                        ": pixels=" +
                        changedOutsideRegion.ToString(CultureInfo.InvariantCulture) +
                        ", allowed=" +
                        allowedOutsideRegionChanges.ToString(CultureInfo.InvariantCulture) +
                        ", first=(" +
                        firstOutsideX.ToString(CultureInfo.InvariantCulture) +
                        "," +
                        firstOutsideY.ToString(CultureInfo.InvariantCulture) +
                        ").";
                    result.Failures.Add(message);
                    result.VisualFailures.Add(new ProjectionVisualFailure
                    {
                        Category = "Original vs Clean: outside detected region",
                        FileName = fileName,
                        ViewName = viewName,
                        Message = message,
                        LeftLabel = "Original",
                        LeftImagePath = originalProjectionPath,
                        RightLabel = "Clean",
                        RightImagePath = cleanProjectionPath
                    });
                }

                foreach (StepProjectionDetectionRegion region in detectionRegions)
                    VerifyCleanedRegionFlatness(
                        fileName,
                        viewName,
                        originalImage,
                        cleanImage,
                        originalProjectionPath,
                        cleanProjectionPath,
                        region,
                        result);
            }
        }

        private static void VerifyCleanedRegionFlatness(
            string fileName,
            string viewName,
            SKBitmap originalImage,
            SKBitmap cleanImage,
            string originalProjectionPath,
            string cleanProjectionPath,
            StepProjectionDetectionRegion region,
            PostCleanVerificationResult result)
        {
            int left = Math.Max(0, region.RectangleX);
            int top = Math.Max(0, region.RectangleY);
            int right = Math.Min(cleanImage.Width - 1, region.RectangleX + region.RectangleWidth - 1);
            int bottom = Math.Min(cleanImage.Height - 1, region.RectangleY + region.RectangleHeight - 1);
            int width = right - left + 1;
            int height = bottom - top + 1;
            if (width < 16 || height < 16 || width * height < 500)
                return;

            double originalEdgeRatio = MeasureRegionEdgeRatio(originalImage, left, top, right, bottom);
            double cleanEdgeRatio = MeasureRegionEdgeRatio(cleanImage, left, top, right, bottom);
            if (cleanEdgeRatio <= MaxCleanedRegionEdgeRatio ||
                cleanEdgeRatio <= originalEdgeRatio * MaxRetainedRegionEdgeRatio)
                return;

            string message =
                fileName +
                " cleaned region still has non-flat visual detail on " +
                viewName +
                " at [" +
                left.ToString(CultureInfo.InvariantCulture) +
                "," +
                top.ToString(CultureInfo.InvariantCulture) +
                " " +
                width.ToString(CultureInfo.InvariantCulture) +
                "x" +
                height.ToString(CultureInfo.InvariantCulture) +
                "]: clean edge ratio=" +
                cleanEdgeRatio.ToString("0.0000", CultureInfo.InvariantCulture) +
                ", original edge ratio=" +
                originalEdgeRatio.ToString("0.0000", CultureInfo.InvariantCulture) +
                ".";
            result.Failures.Add(message);
            result.VisualFailures.Add(new ProjectionVisualFailure
            {
                Category = "Original vs Clean: non-flat cleaned region",
                FileName = fileName,
                ViewName = viewName,
                Message = message,
                LeftLabel = "Original",
                LeftImagePath = originalProjectionPath,
                RightLabel = "Clean",
                RightImagePath = cleanProjectionPath
            });
        }

        private static double MeasureRegionEdgeRatio(SKBitmap image, int left, int top, int right, int bottom)
        {
            int highContrastEdges = 0;
            int sampledEdges = 0;

            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    SKColor current = image.GetPixel(x, y);
                    if (IsBackgroundLike(current))
                        continue;

                    if (x < right)
                    {
                        SKColor next = image.GetPixel(x + 1, y);
                        if (!IsBackgroundLike(next))
                        {
                            sampledEdges++;
                            if (ColorDistance(current, next) > FlatnessEdgeThreshold)
                                highContrastEdges++;
                        }
                    }

                    if (y < bottom)
                    {
                        SKColor next = image.GetPixel(x, y + 1);
                        if (!IsBackgroundLike(next))
                        {
                            sampledEdges++;
                            if (ColorDistance(current, next) > FlatnessEdgeThreshold)
                                highContrastEdges++;
                        }
                    }
                }
            }

            if (sampledEdges == 0)
                return 0.0;

            return (double)highContrastEdges / sampledEdges;
        }

        private static bool IsBackgroundLike(SKColor color)
        {
            return color.Red >= 245 && color.Green >= 245 && color.Blue >= 245;
        }

        private static StepProjectionOptions CreateVerificationProjectionOptions()
        {
            return new StepProjectionOptions
            {
                ImageSizePixels = VerificationProjectionImageSizePixels,
                PaddingPixels = VerificationProjectionPaddingPixels,
                WriteMetadata = false
            };
        }

        private static StepProjectionOptions CreateProjectionOptionsForViews(
            IReadOnlyList<string> viewNames,
            StepProjectionOptions template)
        {
            var options = new StepProjectionOptions
            {
                ImageSizePixels = template.ImageSizePixels,
                PaddingPixels = template.PaddingPixels,
                WriteMetadata = template.WriteMetadata
            };

            foreach (string viewName in viewNames)
                options.ViewNames.Add(viewName);

            return options;
        }

        private static bool[] BuildAllowedChangeMask(
            int imageWidth,
            int imageHeight,
            IReadOnlyList<StepProjectionDetectionRegion> detectionRegions,
            int paddingPixels)
        {
            var mask = new bool[imageWidth * imageHeight];
            foreach (StepProjectionDetectionRegion region in detectionRegions)
            {
                int left = Math.Max(0, region.RectangleX - paddingPixels);
                int top = Math.Max(0, region.RectangleY - paddingPixels);
                int right = Math.Min(imageWidth - 1, region.RectangleX + region.RectangleWidth - 1 + paddingPixels);
                int bottom = Math.Min(imageHeight - 1, region.RectangleY + region.RectangleHeight - 1 + paddingPixels);
                if (right < left || bottom < top)
                    continue;

                for (int y = top; y <= bottom; y++)
                {
                    int row = y * imageWidth;
                    for (int x = left; x <= right; x++)
                        mask[row + x] = true;
                }
            }

            return mask;
        }

        private static int GetAllowedOutsideRegionChanges(int imageWidth, int imageHeight)
        {
            return Math.Max(
                1,
                (int)Math.Round(
                    imageWidth * imageHeight * MaxOutsideDetectionRegionChangeRatio,
                    MidpointRounding.AwayFromZero));
        }

        private static bool PixelsDifferent(SKColor left, SKColor right, int tolerance)
        {
            return ColorDistance(left, right) > tolerance ||
                Math.Abs(left.Alpha - right.Alpha) > tolerance;
        }

        private static int ColorDistance(SKColor left, SKColor right)
        {
            int red = Math.Abs(left.Red - right.Red);
            int green = Math.Abs(left.Green - right.Green);
            int blue = Math.Abs(left.Blue - right.Blue);
            return Math.Max(red, Math.Max(green, blue));
        }

        private static void WriteFailedProjectionReport(
            string reportPath,
            string reportDirectory,
            List<ProjectionVisualFailure> visualFailures)
        {
            Directory.CreateDirectory(reportDirectory);
            foreach (string staleImage in Directory.GetFiles(reportDirectory, "*.png"))
                File.Delete(staleImage);

            var lines = new List<string>
            {
                "# StepCleaner Failed Projection Report",
                string.Empty,
                "Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                string.Empty
            };

            if (visualFailures.Count == 0)
            {
                lines.Add("No failed projections.");
                File.WriteAllLines(reportPath, lines, Encoding.UTF8);
                return;
            }

            lines.Add("Failed projections: " + visualFailures.Count.ToString(CultureInfo.InvariantCulture));
            lines.Add(string.Empty);

            int index = 1;
            foreach (ProjectionVisualFailure failure in visualFailures)
            {
                string imageName = BuildReportImageName(index, failure);
                string outputPath = Path.Combine(reportDirectory, imageName);
                bool wroteImage = TryWriteSideBySideProjectionImage(failure, outputPath);

                lines.Add("## " + failure.Category);
                lines.Add(string.Empty);
                lines.Add("- Model: `" + failure.FileName + "`");
                lines.Add("- View: `" + failure.ViewName + "`");
                lines.Add("- Detail: " + failure.Message);
                lines.Add(string.Empty);

                if (wroteImage)
                    lines.Add("![" + failure.FileName + " " + failure.ViewName + "](" + BuildMarkdownImagePath(reportPath, outputPath) + ")");
                else
                    lines.Add("Could not create side-by-side image for this projection.");

                lines.Add(string.Empty);
                index++;
            }

            File.WriteAllLines(reportPath, lines, Encoding.UTF8);
        }

        private static bool TryWriteSideBySideProjectionImage(ProjectionVisualFailure failure, string outputPath)
        {
            using (var leftImage = SKBitmap.Decode(failure.LeftImagePath))
            using (var rightImage = SKBitmap.Decode(failure.RightImagePath))
            {
                if (leftImage == null || rightImage == null)
                    return false;

                const int panelSize = 720;
                const int labelHeight = 46;
                const int gutter = 18;
                const int margin = 18;

                int outputWidth = panelSize * 2 + gutter + margin * 2;
                int outputHeight = panelSize + labelHeight + margin * 2;

                using (var output = new SKBitmap(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
                using (var canvas = new SKCanvas(output))
                using (var textPaint = new SKPaint())
                using (var labelFont = new SKFont())
                using (var backgroundPaint = new SKPaint())
                using (var framePaint = new SKPaint())
                {
                    canvas.Clear(new SKColor(250, 250, 250));

                    backgroundPaint.Color = SKColors.White;
                    backgroundPaint.Style = SKPaintStyle.Fill;
                    framePaint.Color = new SKColor(205, 205, 205);
                    framePaint.Style = SKPaintStyle.Stroke;
                    framePaint.StrokeWidth = 2;
                    textPaint.Color = new SKColor(35, 35, 35);
                    textPaint.IsAntialias = true;
                    labelFont.Size = 24;

                    var leftRect = new SKRect(margin, margin + labelHeight, margin + panelSize, margin + labelHeight + panelSize);
                    var rightRect = new SKRect(leftRect.Right + gutter, leftRect.Top, leftRect.Right + gutter + panelSize, leftRect.Bottom);

                    DrawProjectionPanel(canvas, leftImage, leftRect, backgroundPaint, framePaint);
                    DrawProjectionPanel(canvas, rightImage, rightRect, backgroundPaint, framePaint);

                    canvas.DrawText(failure.LeftLabel, leftRect.Left, margin + 30, SKTextAlign.Left, labelFont, textPaint);
                    canvas.DrawText(failure.RightLabel, rightRect.Left, margin + 30, SKTextAlign.Left, labelFont, textPaint);

                    using (SKImage image = SKImage.FromBitmap(output))
                    using (SKData data = image.Encode(SKEncodedImageFormat.Png, 95))
                    using (Stream stream = File.Create(outputPath))
                        data.SaveTo(stream);
                }
            }

            return true;
        }

        private static void DrawProjectionPanel(
            SKCanvas canvas,
            SKBitmap image,
            SKRect target,
            SKPaint backgroundPaint,
            SKPaint framePaint)
        {
            canvas.DrawRect(target, backgroundPaint);

            float scale = Math.Min(target.Width / image.Width, target.Height / image.Height);
            float width = image.Width * scale;
            float height = image.Height * scale;
            var imageRect = new SKRect(
                target.Left + (target.Width - width) / 2.0f,
                target.Top + (target.Height - height) / 2.0f,
                target.Left + (target.Width + width) / 2.0f,
                target.Top + (target.Height + height) / 2.0f);

            canvas.DrawBitmap(image, imageRect);
            canvas.DrawRect(target, framePaint);
        }

        private static string BuildReportImageName(int index, ProjectionVisualFailure failure)
        {
            return index.ToString("000", CultureInfo.InvariantCulture) +
                "_" +
                SanitizeFileName(Path.GetFileNameWithoutExtension(failure.FileName)) +
                "__" +
                SanitizeFileName(failure.ViewName) +
                "__" +
                SanitizeFileName(failure.Category) +
                ".png";
        }

        private static string BuildMarkdownImagePath(string reportPath, string imagePath)
        {
            string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? string.Empty;
            return Path.GetRelativePath(reportDirectory, Path.GetFullPath(imagePath)).Replace('\\', '/');
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "model";

            var invalidCharacters = new HashSet<char>(Path.GetInvalidFileNameChars());
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (invalidCharacters.Contains(character) || char.IsWhiteSpace(character) || character == ':')
                    builder.Append('_');
                else
                    builder.Append(character);
            }

            return builder.ToString();
        }

        private sealed class PostCleanVerificationResult
        {
            public string ReportPath { get; set; }
            public string ReportDirectory { get; set; }
            public List<string> Failures { get; } = new List<string>();
            public List<ProjectionVisualFailure> VisualFailures { get; } = new List<ProjectionVisualFailure>();
            public bool Passed => Failures.Count == 0;
        }

        private sealed class ProjectionVisualFailure
        {
            public string Category { get; set; }
            public string FileName { get; set; }
            public string ViewName { get; set; }
            public string Message { get; set; }
            public string LeftLabel { get; set; }
            public string LeftImagePath { get; set; }
            public string RightLabel { get; set; }
            public string RightImagePath { get; set; }
        }
    }
}
