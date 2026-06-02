using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace StepCleaner
{
    internal static class Program
    {
        private const int PostCleanVerificationFailedExitCode = 4;
        private const int ProjectionDifferenceTolerance = 6;
        private const int AllowedDetectionRegionPaddingPixels = 10;
        private const double MaxOutsideDetectionRegionChangeRatio = 0.005;
        private const int VerificationProjectionImageSizePixels = 1000;
        private const int VerificationProjectionPaddingPixels = 50;

        private static int Main(string[] args)
        {
            var arguments = new List<string>(args);
            bool writeDetectionDebug = RemoveDetectionDebugFlag(arguments);
            bool cleanText = RemoveCleanTextFlag(arguments);

            if (arguments.Count > 0 && IsProjectionCommand(arguments[0]))
                return Project(arguments.ToArray());

            if (arguments.Count > 0 && IsDetectionCommand(arguments[0]))
                return Detect(arguments.ToArray(), writeDetectionDebug);

            if (arguments.Count < 1 || arguments.Count > 2 || IsHelp(arguments[0]))
            {
                PrintUsage();
                return arguments.Count == 0 ? 1 : 0;
            }

            string inputPath = arguments[0];
            string outputPath = arguments.Count == 2 ? arguments[1] : GetDefaultOutputPath(inputPath);

            if (Directory.Exists(inputPath))
                return CleanDirectory(inputPath, outputPath, writeDetectionDebug, cleanText);

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file was not found: " + inputPath);
                return 2;
            }

            try
            {
                var report = CleanFile(inputPath, outputPath, cleanText);
                if (writeDetectionDebug)
                    WriteDetectionDebug(inputPath, GetDefaultDetectionDebugOutputPath(inputPath, outputPath), report.DetectionReport);

                string verificationDirectory = GetDefaultPostCleanVerificationOutputPath(inputPath, outputPath);
                PostCleanVerificationResult verification = VerifyPostCleanOutput(
                    inputPath,
                    outputPath,
                    report.DetectionReport,
                    verificationDirectory);

                Console.WriteLine("STEP watermark cleanup complete");
                Console.WriteLine("Input:  " + Path.GetFullPath(inputPath));
                Console.WriteLine("Output: " + Path.GetFullPath(outputPath));
                if (writeDetectionDebug)
                    Console.WriteLine("Detection debug: " + Path.GetFullPath(GetDefaultDetectionDebugOutputPath(inputPath, outputPath)));
                Console.WriteLine("Solids: " + report.SolidCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Styled faces: " + report.StyledFaceCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Candidates: " + report.CandidateFaceCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Removed solids: " + report.RemovedSolidCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Flattened faces: " + report.FlattenedFaceCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Flattened points: " + report.FlattenedPointCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Recolored faces: " + report.RecoloredFaceCount.ToString(CultureInfo.InvariantCulture));

                foreach (string diagnostic in report.Diagnostics)
                    Console.WriteLine(diagnostic);

                PrintPostCleanVerificationResult(verification);
                if (!verification.Passed)
                    return PostCleanVerificationFailedExitCode;

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP watermark cleanup failed: " + ex.Message);
                return 3;
            }
        }

        private static int Detect(string[] args, bool writeDetectionDebug)
        {
            if (args.Length != 2 || IsHelp(args[1]))
            {
                PrintUsage();
                return args.Length < 2 ? 1 : 0;
            }

            string inputPath = args[1];
            if (!Directory.Exists(inputPath) && !File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file or directory was not found: " + inputPath);
                return 2;
            }

            try
            {
                if (Directory.Exists(inputPath))
                {
                    var inputFiles = GetStepFiles(inputPath);
                    if (inputFiles.Count == 0)
                    {
                        Console.Error.WriteLine("No STEP files were found in: " + inputPath);
                        return 2;
                    }

                    Console.WriteLine("STEP watermark detection");
                    Console.WriteLine("Input directory: " + Path.GetFullPath(inputPath));
                    Console.WriteLine("Files: " + inputFiles.Count.ToString(CultureInfo.InvariantCulture));
                    string debugDirectory = GetDefaultDetectionDebugOutputPath(inputPath, GetDefaultOutputPath(inputPath));
                    if (writeDetectionDebug)
                        Console.WriteLine("Detection debug: " + Path.GetFullPath(debugDirectory));

                    foreach (string inputFile in inputFiles)
                    {
                        var report = StepWatermarkCleaner.Detect(File.ReadAllBytes(inputFile));
                        PrintDetection(Path.GetFileName(inputFile), report);
                        if (writeDetectionDebug)
                            WriteDetectionDebug(inputFile, debugDirectory, report);
                    }

                    return 0;
                }

                Console.WriteLine("STEP watermark detection");
                Console.WriteLine("Input: " + Path.GetFullPath(inputPath));
                string debugDirectoryForFile = GetDefaultDetectionDebugOutputPath(inputPath, GetDefaultOutputPath(inputPath));
                if (writeDetectionDebug)
                    Console.WriteLine("Detection debug: " + Path.GetFullPath(debugDirectoryForFile));

                var singleReport = StepWatermarkCleaner.Detect(File.ReadAllBytes(inputPath));
                PrintDetection(Path.GetFileName(inputPath), singleReport);
                if (writeDetectionDebug)
                    WriteDetectionDebug(inputPath, debugDirectoryForFile, singleReport);

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP watermark detection failed: " + ex.Message);
                return 3;
            }
        }

        private static int Project(string[] args)
        {
            if (args.Length < 2 || args.Length > 3 || IsHelp(args[1]))
            {
                PrintUsage();
                return args.Length < 2 ? 1 : 0;
            }

            string inputPath = args[1];
            string outputDirectory = args.Length == 3 ? args[2] : GetDefaultProjectionOutputPath(inputPath);

            if (!Directory.Exists(inputPath) && !File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file or directory was not found: " + inputPath);
                return 2;
            }

            try
            {
                if (Directory.Exists(inputPath))
                {
                    var reports = StepProjectionRenderer.ProjectDirectory(inputPath, outputDirectory);
                    if (reports.Count == 0)
                    {
                        Console.Error.WriteLine("No STEP files were found in: " + inputPath);
                        return 2;
                    }

                    Console.WriteLine("STEP six-side projection complete");
                    Console.WriteLine("Input directory:      " + Path.GetFullPath(inputPath));
                    Console.WriteLine("Projection directory: " + Path.GetFullPath(outputDirectory));
                    Console.WriteLine("Files: " + reports.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (var report in reports)
                    {
                        Console.WriteLine(
                            Path.GetFileName(report.InputPath) +
                            ": faces=" + report.FaceCount.ToString(CultureInfo.InvariantCulture) +
                            ", edges=" + report.EdgeCount.ToString(CultureInfo.InvariantCulture) +
                            ", outputs=" + report.OutputFiles.Count.ToString(CultureInfo.InvariantCulture));
                    }

                    return 0;
                }

                var singleReport = StepProjectionRenderer.ProjectFile(inputPath, outputDirectory);
                Console.WriteLine("STEP six-side projection complete");
                Console.WriteLine("Input:                " + Path.GetFullPath(inputPath));
                Console.WriteLine("Projection directory: " + Path.GetFullPath(outputDirectory));
                Console.WriteLine("Faces: " + singleReport.FaceCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Edges: " + singleReport.EdgeCount.ToString(CultureInfo.InvariantCulture));
                foreach (string outputFile in singleReport.OutputFiles)
                    Console.WriteLine("Output: " + Path.GetFullPath(outputFile));

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP six-side projection failed: " + ex.Message);
                return 3;
            }
        }

        private static int CleanDirectory(string inputDirectory, string outputDirectory, bool writeDetectionDebug, bool cleanText)
        {
            Directory.CreateDirectory(outputDirectory);

            var inputFiles = GetStepFiles(inputDirectory);
            if (inputFiles.Count == 0)
            {
                Console.Error.WriteLine("No STEP files were found in: " + inputDirectory);
                return 2;
            }

            Console.WriteLine("STEP watermark batch cleanup");
            Console.WriteLine("Input directory:  " + Path.GetFullPath(inputDirectory));
            Console.WriteLine("Output directory: " + Path.GetFullPath(outputDirectory));
            string debugDirectory = Path.Combine(outputDirectory, "Detection");
            if (writeDetectionDebug)
                Console.WriteLine("Detection debug: " + Path.GetFullPath(debugDirectory));
            Console.WriteLine("Files: " + inputFiles.Count.ToString(CultureInfo.InvariantCulture));

            int totalRemovedSolids = 0;
            int totalFlattenedFaces = 0;
            int totalFlattenedPoints = 0;
            int totalRecoloredFaces = 0;
            var verification = new PostCleanVerificationResult
            {
                ReportPath = Path.Combine(GetDefaultPostCleanVerificationOutputPath(inputDirectory, outputDirectory), "FailedProjectionReport.md"),
                ReportDirectory = Path.Combine(GetDefaultPostCleanVerificationOutputPath(inputDirectory, outputDirectory), "FailedProjectionReport")
            };

            try
            {
                foreach (string inputFile in inputFiles)
                {
                    string outputFile = Path.Combine(outputDirectory, Path.GetFileName(inputFile));
                    var report = CleanFile(inputFile, outputFile, cleanText);
                    if (writeDetectionDebug)
                        WriteDetectionDebug(inputFile, debugDirectory, report.DetectionReport);

                    VerifyPostCleanOutput(
                        inputFile,
                        outputFile,
                        report.DetectionReport,
                        GetDefaultPostCleanVerificationOutputPath(inputDirectory, outputDirectory),
                        verification);

                    totalRemovedSolids += report.RemovedSolidCount;
                    totalFlattenedFaces += report.FlattenedFaceCount;
                    totalFlattenedPoints += report.FlattenedPointCount;
                    totalRecoloredFaces += report.RecoloredFaceCount;

                    Console.WriteLine(
                        Path.GetFileName(inputFile) +
                        ": removedSolids=" + report.RemovedSolidCount.ToString(CultureInfo.InvariantCulture) +
                        ", flattenedFaces=" + report.FlattenedFaceCount.ToString(CultureInfo.InvariantCulture) +
                        ", flattenedPoints=" + report.FlattenedPointCount.ToString(CultureInfo.InvariantCulture) +
                        ", recoloredFaces=" + report.RecoloredFaceCount.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP watermark batch cleanup failed: " + ex.Message);
                return 3;
            }

            Console.WriteLine("Batch cleanup complete");
            Console.WriteLine("Total removed solids: " + totalRemovedSolids.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Total flattened faces: " + totalFlattenedFaces.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Total flattened points: " + totalFlattenedPoints.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Total recolored faces: " + totalRecoloredFaces.ToString(CultureInfo.InvariantCulture));
            PrintPostCleanVerificationResult(verification);
            return verification.Passed ? 0 : PostCleanVerificationFailedExitCode;
        }

        private static PostCleanVerificationResult VerifyPostCleanOutput(
            string inputPath,
            string outputPath,
            StepWatermarkDetectionReport detectionReport,
            string verificationDirectory)
        {
            var result = new PostCleanVerificationResult
            {
                ReportPath = Path.Combine(verificationDirectory, "FailedProjectionReport.md"),
                ReportDirectory = Path.Combine(verificationDirectory, "FailedProjectionReport")
            };
            VerifyPostCleanOutput(inputPath, outputPath, detectionReport, verificationDirectory, result);
            return result;
        }

        private static void VerifyPostCleanOutput(
            string inputPath,
            string outputPath,
            StepWatermarkDetectionReport detectionReport,
            string verificationDirectory,
            PostCleanVerificationResult result)
        {
            Directory.CreateDirectory(verificationDirectory);
            string originalProjectionDirectory = Path.Combine(verificationDirectory, "OriginalProjection");
            string cleanProjectionDirectory = Path.Combine(verificationDirectory, "CleanProjection");
            Directory.CreateDirectory(originalProjectionDirectory);
            Directory.CreateDirectory(cleanProjectionDirectory);

            var projectionOptions = CreateVerificationProjectionOptions();
            var detectionRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    inputPath,
                    detectionReport,
                    projectionOptions)
                .ToList();

            string[] detectedViewNames = detectionRegions
                .Select(region => region.ViewName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (detectedViewNames.Length == 0)
            {
                WriteFailedProjectionReport(result.ReportPath, result.ReportDirectory, result.VisualFailures);
                return;
            }

            var renderOptions = CreateProjectionOptionsForViews(detectedViewNames, projectionOptions);
            StepProjectionRenderer.ProjectFile(inputPath, originalProjectionDirectory, renderOptions);
            StepProjectionRenderer.ProjectFile(outputPath, cleanProjectionDirectory, renderOptions);

            string inputModelName = Path.GetFileNameWithoutExtension(inputPath);
            string outputModelName = Path.GetFileNameWithoutExtension(outputPath);
            foreach (string viewName in detectedViewNames)
            {
                string originalProjectionPath = Path.Combine(originalProjectionDirectory, inputModelName + "__" + viewName + ".png");
                string cleanProjectionPath = Path.Combine(cleanProjectionDirectory, outputModelName + "__" + viewName + ".png");
                var viewRegions = detectionRegions
                    .Where(region => string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                VerifyPostCleanProjectionImage(
                    Path.GetFileName(inputPath),
                    viewName,
                    originalProjectionPath,
                    cleanProjectionPath,
                    viewRegions,
                    result);
            }

            WriteFailedProjectionReport(result.ReportPath, result.ReportDirectory, result.VisualFailures);
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
                if (changedOutsideRegion <= allowedOutsideRegionChanges)
                    return;

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

        private static void PrintPostCleanVerificationResult(PostCleanVerificationResult verification)
        {
            Console.WriteLine("Post-clean verification: " + (verification.Passed ? "passed" : "failed"));
            if (!string.IsNullOrEmpty(verification.ReportPath))
                Console.WriteLine("Post-clean verification report: " + Path.GetFullPath(verification.ReportPath));

            foreach (string failure in verification.Failures)
                Console.Error.WriteLine("Post-clean verification fault: " + failure);
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

        private static StepWatermarkCleanerReport CleanFile(string inputPath, string outputPath, bool cleanText)
        {
            byte[] stepBytes = File.ReadAllBytes(inputPath);
            string stepText = System.Text.Encoding.Latin1.GetString(stepBytes);

            var report = StepWatermarkCleaner.CleanWithReport(
                stepText,
                new StepWatermarkCleanerOptions
                {
                    CleanText = cleanText
                });
            File.WriteAllBytes(outputPath, System.Text.Encoding.Latin1.GetBytes(report.CleanedStep));
            return report;
        }

        private static void WriteDetectionDebug(
            string inputPath,
            string outputDirectory,
            StepWatermarkDetectionReport detectionReport)
        {
            var markedRegions = StepWatermarkCleaner.LoadMarkedRegionsForStepFile(
                inputPath,
                GetDefaultProjectionOutputPath(inputPath),
                GetDefaultMarkedDirectory(inputPath));

            StepProjectionRenderer.ProjectDetectionFile(
                inputPath,
                outputDirectory,
                detectionReport,
                new StepProjectionOptions
                {
                    WriteMetadata = false
                },
                markedRegions);
        }

        private static void PrintDetection(string label, StepWatermarkDetectionReport report)
        {
            Console.WriteLine(
                label +
                ": solids=" + report.SolidCount.ToString(CultureInfo.InvariantCulture) +
                ", styledFaces=" + report.StyledFaceCount.ToString(CultureInfo.InvariantCulture) +
                ", removableSolids=" + report.RemovableSolidCount.ToString(CultureInfo.InvariantCulture) +
                ", embeddedFaces=" + report.EmbeddedFaceCount.ToString(CultureInfo.InvariantCulture) +
                ", coplanarFaces=" + report.CoplanarFaceCount.ToString(CultureInfo.InvariantCulture) +
                ", hostLoopCandidates=" + report.HostLoopCandidateCount.ToString(CultureInfo.InvariantCulture) +
                ", hostLoops=" + report.HostLoopCount.ToString(CultureInfo.InvariantCulture));
        }

        private static string GetDefaultOutputPath(string inputPath)
        {
            if (Directory.Exists(inputPath))
            {
                string fullInput = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(fullInput) ?? fullInput;
                string name = Path.GetFileName(fullInput);
                return string.Equals(name, "Original", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(parent, "Clean")
                    : Path.Combine(fullInput, "Clean");
            }

            return Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty,
                Path.GetFileNameWithoutExtension(inputPath) + ".clean" + Path.GetExtension(inputPath));
        }

        private static string GetDefaultDetectionDebugOutputPath(string inputPath, string outputPath)
        {
            if (Directory.Exists(inputPath))
                return Path.Combine(outputPath, "Detection");

            string fullInputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
            string inputParent = Path.GetDirectoryName(fullInputDirectory) ?? fullInputDirectory;
            if (string.Equals(Path.GetFileName(fullInputDirectory), "Original", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(inputParent, "Clean", "Detection");

            return Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? string.Empty,
                "Detection");
        }

        private static string GetDefaultPostCleanVerificationOutputPath(string inputPath, string outputPath)
        {
            if (Directory.Exists(inputPath))
                return Path.Combine(outputPath, "PostCleanVerification");

            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? string.Empty;
            return Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(outputPath) + ".PostCleanVerification");
        }

        private static string GetDefaultProjectionOutputPath(string inputPath)
        {
            if (Directory.Exists(inputPath))
            {
                string fullInput = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string inputParent = Path.GetDirectoryName(fullInput) ?? fullInput;
                string name = Path.GetFileName(fullInput);
                return string.Equals(name, "Original", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(inputParent, "Projection")
                    : Path.Combine(fullInput, "Projection");
            }

            string fullInputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
            string fileParent = Path.GetDirectoryName(fullInputDirectory) ?? fullInputDirectory;
            return string.Equals(Path.GetFileName(fullInputDirectory), "Original", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(fileParent, "Projection")
                : Path.Combine(fullInputDirectory, "Projection");
        }

        private static string GetDefaultMarkedDirectory(string inputPath)
        {
            if (Directory.Exists(inputPath))
            {
                string fullInput = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(fullInput) ?? fullInput;
                string name = Path.GetFileName(fullInput);
                return string.Equals(name, "Original", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(parent, "Marked")
                    : Path.Combine(fullInput, "Marked");
            }

            string fullInputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
            string directoryParent = Path.GetDirectoryName(fullInputDirectory) ?? fullInputDirectory;
            return string.Equals(Path.GetFileName(fullInputDirectory), "Original", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(directoryParent, "Marked")
                : Path.Combine(fullInputDirectory, "Marked");
        }

        private static List<string> GetStepFiles(string inputDirectory)
        {
            var result = new List<string>();
            foreach (string file in Directory.GetFiles(inputDirectory))
            {
                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
                    result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool IsHelp(string arg)
        {
            return string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProjectionCommand(string arg)
        {
            return string.Equals(arg, "project", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "projection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "projections", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDetectionCommand(string arg)
        {
            return string.Equals(arg, "detect", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "detection", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RemoveDetectionDebugFlag(List<string> args)
        {
            bool found = false;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (!IsDetectionDebugFlag(args[i]))
                    continue;

                args.RemoveAt(i);
                found = true;
            }

            return found;
        }

        private static bool RemoveCleanTextFlag(List<string> args)
        {
            bool found = false;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (!IsCleanTextFlag(args[i]))
                    continue;

                args.RemoveAt(i);
                found = true;
            }

            return found;
        }

        private static bool IsDetectionDebugFlag(string arg)
        {
            return string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-d", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--debug-detection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--detection-debug", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCleanTextFlag(string arg)
        {
            return string.Equals(arg, "--clean-text", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--text", StringComparison.OrdinalIgnoreCase);
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

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  StepCleaner <input.step> [output.step] [--debug] [--clean-text]");
            Console.WriteLine("  StepCleaner <input-directory> [output-directory] [--debug] [--clean-text]");
            Console.WriteLine("  StepCleaner detect <input.step|input-directory> [--debug]");
            Console.WriteLine("  StepCleaner project <input.step|input-directory> [projection-directory]");
            Console.WriteLine();
            Console.WriteLine("When output.step is omitted, the cleaner writes <input>.clean.step next to the input file.");
            Console.WriteLine("When input-directory is named Original and output-directory is omitted, the cleaner writes to sibling Clean.");
            Console.WriteLine("The detect command runs automatic stage 1 detection only; marked JSON is not loaded.");
            Console.WriteLine("The --debug option writes detected watermark region projection PNG files to Clean\\Detection.");
            Console.WriteLine("The --clean-text option additionally removes detected raised or cut text-string geometry.");
            Console.WriteLine("Cleanup returns " + PostCleanVerificationFailedExitCode.ToString(CultureInfo.InvariantCulture) + " when post-clean projection verification fails; failed comparison images are written to PostCleanVerification.");
            Console.WriteLine("The project command writes six PNG side projections and JSON mapping files; when the input directory is named Original, the projection directory defaults to sibling Projection.");
        }
    }
}
