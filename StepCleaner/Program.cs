using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace StepCleaner
{
    internal static class Program
    {
        private const int PostCleanVerificationFailedExitCode = 4;
        private const int ProjectionDifferenceTolerance = 6;
        private const int AllowedDetectionRegionPaddingPixels = 16;
        private const double MaxOutsideDetectionRegionChangeRatio = 0.01;
        private const int VerificationProjectionImageSizePixels = 1000;
        private const int VerificationProjectionPaddingPixels = 50;
        private const int FlatnessEdgeThreshold = 28;
        private const double MinOriginalRegionEdgeRatioForFlatness = 0.08;
        private const double MaxCleanedRegionEdgeRatio = 0.035;
        private const double MaxRetainedRegionEdgeRatio = 0.45;
        private const double MaxRetainedTextLogoEdgePixelRatio = 0.35;
        private const int MinOriginalTextLogoEdgePixels = 8;

        private static int Main(string[] args)
        {
            var arguments = new List<string>(args);
            bool writeDetectionDebug = RemoveDetectionDebugFlag(arguments);
            bool cleanText = RemoveCleanTextFlag(arguments);

            if (arguments.Count > 0 && IsProjectionCommand(arguments[0]))
                return Project(arguments.ToArray());

            if (arguments.Count > 0 && IsDetectionCommand(arguments[0]))
                return Detect(arguments.ToArray(), writeDetectionDebug);

            if (arguments.Count > 0 && IsRemovedGeometryCommand(arguments[0]))
                return ExportRemovedGeometry(arguments.ToArray(), cleanText);

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

                string removedGeometryPath = GetDefaultRemovedGeometryOutputPathForCleanOutput(outputPath);
                string verificationDirectory = GetDefaultPostCleanVerificationOutputPath(inputPath, outputPath);
                PostCleanVerificationResult verification = VerifyPostCleanOutput(
                    inputPath,
                    outputPath,
                    report.DetectionReport,
                    verificationDirectory);

                Console.WriteLine("STEP watermark cleanup complete");
                Console.WriteLine("Input:  " + Path.GetFullPath(inputPath));
                Console.WriteLine("Output: " + Path.GetFullPath(outputPath));
                Console.WriteLine("Removed geometry: " + Path.GetFullPath(removedGeometryPath));
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
                            PrintDetectionDetails(report);
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
                    PrintDetectionDetails(singleReport);
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

        private static int ExportRemovedGeometry(string[] args, bool cleanText)
        {
            if (args.Length < 2 || args.Length > 3 || IsHelp(args[1]))
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
                    string outputDirectory = args.Length == 3 ? args[2] : GetDefaultRemovedGeometryOutputPath(inputPath);
                    Directory.CreateDirectory(outputDirectory);
                    var inputFiles = GetStepFiles(inputPath);
                    if (inputFiles.Count == 0)
                    {
                        Console.Error.WriteLine("No STEP files were found in: " + inputPath);
                        return 2;
                    }

                    Console.WriteLine("STEP removed-geometry export");
                    Console.WriteLine("Input directory:  " + Path.GetFullPath(inputPath));
                    Console.WriteLine("Output directory: " + Path.GetFullPath(outputDirectory));
                    Console.WriteLine("Files: " + inputFiles.Count.ToString(CultureInfo.InvariantCulture));
                    int written = 0;
                    foreach (string inputFile in inputFiles)
                    {
                        string outputFile = Path.Combine(
                            outputDirectory,
                            Path.GetFileNameWithoutExtension(inputFile) + ".removed" + Path.GetExtension(inputFile));
                        var report = BuildRemovedGeometryFile(inputFile, outputFile, cleanText);
                        if (!string.IsNullOrEmpty(report.RemovedGeometryStep))
                            written++;

                        Console.WriteLine(
                            Path.GetFileName(inputFile) +
                            ": removedGeometry=" +
                            (string.IsNullOrEmpty(report.RemovedGeometryStep) ? "none" : Path.GetFullPath(outputFile)));
                    }

                    Console.WriteLine("Written removed-geometry files: " + written.ToString(CultureInfo.InvariantCulture));
                    return 0;
                }

                string outputPath = args.Length == 3 ? args[2] : GetDefaultRemovedGeometryOutputPath(inputPath);
                var singleReport = BuildRemovedGeometryFile(inputPath, outputPath, cleanText);
                Console.WriteLine("STEP removed-geometry export complete");
                Console.WriteLine("Input:  " + Path.GetFullPath(inputPath));
                Console.WriteLine("Output: " + Path.GetFullPath(outputPath));
                Console.WriteLine("Removed geometry: " + (string.IsNullOrEmpty(singleReport.RemovedGeometryStep) ? "none" : "written"));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP removed-geometry export failed: " + ex.Message);
                return 3;
            }
        }

        private static int Project(string[] args)
        {
            var arguments = new List<string>(args);
            bool edgeMode = RemoveProjectionEdgeFlag(arguments);
            args = arguments.ToArray();

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
                var projectionOptions = new StepProjectionOptions
                {
                    RenderMode = edgeMode ? StepProjectionRenderMode.Edge : StepProjectionRenderMode.Color
                };

                if (Directory.Exists(inputPath))
                {
                    var reports = StepProjectionRenderer.ProjectDirectory(inputPath, outputDirectory, projectionOptions);
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

                var singleReport = StepProjectionRenderer.ProjectFile(inputPath, outputDirectory, projectionOptions);
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
            Console.WriteLine("Removed geometry directory: " + Path.GetFullPath(GetDefaultRemovedGeometryDirectoryForCleanOutput(outputDirectory)));
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
            var residualTopology = StepWatermarkCleaner.FindResidualCleanupTopology(
                Encoding.Latin1.GetString(File.ReadAllBytes(inputPath)),
                Encoding.Latin1.GetString(File.ReadAllBytes(outputPath)),
                detectionReport,
                new StepWatermarkCleanerOptions());
            foreach (string failure in residualTopology.Failures)
                result.Failures.Add(failure);
            bool verifyRetainedEdgeDetail = residualTopology.Failures.Count > 0;

            var verifiedDetectionReport = StepWatermarkCleaner.CreateVerifiedCleanupDetectionReport(detectionReport);
            var detectionRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    inputPath,
                    verifiedDetectionReport,
                    projectionOptions)
                .ToList();

            string[] detectedViewNames = detectionRegions
                .Select(region => region.ViewName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (detectedViewNames.Length == 0)
            {
                result.Failures.Add(
                    Path.GetFileName(inputPath) +
                    " has no detected watermark cleanup regions; post-clean verification cannot prove the watermark was removed.");
                WriteFailedProjectionReport(result.ReportPath, result.ReportDirectory, result.Failures, result.VisualFailures);
                return;
            }

            var renderOptions = CreateProjectionOptionsForViews(detectedViewNames, projectionOptions);
            var edgeRenderOptions = CreateProjectionOptionsForViews(detectedViewNames, projectionOptions);
            edgeRenderOptions.RenderMode = StepProjectionRenderMode.Edge;
            StepProjectionRenderer.ProjectFile(inputPath, originalProjectionDirectory, renderOptions);
            StepProjectionRenderer.ProjectFile(outputPath, cleanProjectionDirectory, renderOptions);
            StepProjectionRenderer.ProjectFile(inputPath, originalProjectionDirectory, edgeRenderOptions);
            StepProjectionRenderer.ProjectFile(outputPath, cleanProjectionDirectory, edgeRenderOptions);

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

                if (verifyRetainedEdgeDetail)
                {
                    string originalEdgeProjectionPath = Path.Combine(originalProjectionDirectory, inputModelName + "__" + viewName + "__edge.png");
                    string cleanEdgeProjectionPath = Path.Combine(cleanProjectionDirectory, outputModelName + "__" + viewName + "__edge.png");
                    VerifyPostCleanEdgeProjectionImage(
                        Path.GetFileName(inputPath),
                        viewName,
                        originalEdgeProjectionPath,
                        cleanEdgeProjectionPath,
                        viewRegions,
                        result);
                }
            }

            WriteFailedProjectionReport(result.ReportPath, result.ReportDirectory, result.Failures, result.VisualFailures);
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

        private static void VerifyPostCleanEdgeProjectionImage(
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
                    result.Failures.Add(fileName + " has an unreadable original or clean edge projection on " + viewName + ".");
                    return;
                }

                if (originalImage.Width != cleanImage.Width || originalImage.Height != cleanImage.Height)
                {
                    result.Failures.Add(fileName + " original and clean edge projections have different sizes on " + viewName + ".");
                    return;
                }

                VerifyTextLogoEdgeRegions(
                    fileName,
                    viewName,
                    originalImage,
                    cleanImage,
                    detectionRegions,
                    result,
                    originalProjectionPath,
                    cleanProjectionPath);
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
                if (originalEdgeRatio < MinOriginalRegionEdgeRatioForFlatness)
                    return;

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

        private static void VerifyTextLogoEdgeRegions(
            string fileName,
            string viewName,
            SKBitmap originalImage,
            SKBitmap cleanImage,
            IReadOnlyList<StepProjectionDetectionRegion> detectionRegions,
            PostCleanVerificationResult result,
            string originalProjectionPath,
            string cleanProjectionPath)
        {
            foreach (StepProjectionDetectionRegion region in detectionRegions)
            {
                int left = Math.Max(0, region.RectangleX);
                int top = Math.Max(0, region.RectangleY);
                int right = Math.Min(cleanImage.Width - 1, region.RectangleX + region.RectangleWidth - 1);
                int bottom = Math.Min(cleanImage.Height - 1, region.RectangleY + region.RectangleHeight - 1);
                if (right < left || bottom < top)
                    continue;

                int originalEdgePixels = CountForegroundPixels(originalImage, left, top, right, bottom);
                if (originalEdgePixels < MinOriginalTextLogoEdgePixels)
                    continue;

                int cleanEdgePixels = CountForegroundPixels(cleanImage, left, top, right, bottom);
                if (cleanEdgePixels <= originalEdgePixels * MaxRetainedTextLogoEdgePixelRatio)
                    continue;

                string message =
                    fileName +
                    " retains text/logo edge detail on " +
                    viewName +
                    ": cleanEdgePixels=" +
                    cleanEdgePixels.ToString(CultureInfo.InvariantCulture) +
                    ", originalEdgePixels=" +
                    originalEdgePixels.ToString(CultureInfo.InvariantCulture) +
                    ".";
                result.Failures.Add(message);
                result.VisualFailures.Add(new ProjectionVisualFailure
                {
                    Category = "Original vs Clean: retained text/logo edge detail",
                    FileName = fileName,
                    ViewName = viewName,
                    Message = message,
                    LeftLabel = "Original edge",
                    LeftImagePath = originalProjectionPath,
                    RightLabel = "Clean edge",
                    RightImagePath = cleanProjectionPath
                });
            }
        }

        private static int CountForegroundPixels(SKBitmap image, int left, int top, int right, int bottom)
        {
            int count = 0;
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    if (!IsBackgroundLike(image.GetPixel(x, y)))
                        count++;
                }
            }

            return count;
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
                WriteMetadata = template.WriteMetadata,
                RenderMode = template.RenderMode
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
            List<string> failures,
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

            if (failures.Count > 0)
            {
                lines.Add("Projection verification failures without comparison images: " + failures.Count.ToString(CultureInfo.InvariantCulture));
                lines.Add(string.Empty);
                foreach (string failure in failures)
                    lines.Add("- " + failure);
                lines.Add(string.Empty);
            }

            if (visualFailures.Count == 0)
            {
                if (failures.Count == 0)
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
            WriteRemovedGeometryForCleanOutput(outputPath, report);
            return report;
        }

        private static string WriteRemovedGeometryForCleanOutput(string outputPath, StepWatermarkCleanerReport report)
        {
            string removedGeometryPath = GetDefaultRemovedGeometryOutputPathForCleanOutput(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(removedGeometryPath)) ?? string.Empty);
            byte[] removedGeometryBytes = string.IsNullOrEmpty(report.RemovedGeometryStep)
                ? Array.Empty<byte>()
                : System.Text.Encoding.Latin1.GetBytes(report.RemovedGeometryStep);
            File.WriteAllBytes(removedGeometryPath, removedGeometryBytes);
            return removedGeometryPath;
        }

        private static StepWatermarkCleanerReport BuildRemovedGeometryFile(string inputPath, string outputPath, bool cleanText)
        {
            byte[] stepBytes = File.ReadAllBytes(inputPath);
            string stepText = System.Text.Encoding.Latin1.GetString(stepBytes);
            var report = StepWatermarkCleaner.CleanWithReport(
                stepText,
                new StepWatermarkCleanerOptions
                {
                    CleanText = cleanText
                });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? string.Empty);
            byte[] outputBytes = string.IsNullOrEmpty(report.RemovedGeometryStep)
                ? Array.Empty<byte>()
                : System.Text.Encoding.Latin1.GetBytes(report.RemovedGeometryStep);
            File.WriteAllBytes(outputPath, outputBytes);
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

            WriteDetectionRegionJson(inputPath, outputDirectory, detectionReport);
        }

        private static void WriteDetectionRegionJson(
            string inputPath,
            string outputDirectory,
            StepWatermarkDetectionReport detectionReport)
        {
            var options = new StepProjectionOptions
            {
                ImageSizePixels = 1600,
                PaddingPixels = 80
            };
            IReadOnlyList<StepProjectionDetectionRegion> regions =
                StepProjectionRenderer.ProjectDetectionRegions(inputPath, detectionReport, options);
            string modelName = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(outputDirectory, modelName + ".detected-regions.json");
            var document = new DetectionRegionDocument
            {
                Model = modelName,
                ImageSizePixels = options.ImageSizePixels,
                PaddingPixels = options.PaddingPixels,
                Regions = regions
                    .OrderBy(region => region.ViewName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(region => region.RectangleY)
                    .ThenBy(region => region.RectangleX)
                    .Select(region => new DetectionRegionRecord
                    {
                        ViewName = region.ViewName,
                        X = region.RectangleX,
                        Y = region.RectangleY,
                        Width = region.RectangleWidth,
                        Height = region.RectangleHeight,
                        EntityId = region.EntityId,
                        Kind = region.Kind
                    })
                    .ToList()
            };
            string json = JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(outputPath, json, Encoding.UTF8);
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

        private static void PrintDetectionDetails(StepWatermarkDetectionReport report)
        {
            if (report.HostLoops == null || report.HostLoops.Count == 0)
                goto PrintRegions;

            Console.WriteLine("Detected host loops:");
            foreach (var loop in report.HostLoops.OrderBy(loop => loop.HostFaceId).ThenBy(loop => loop.BoundId))
            {
                Console.WriteLine(
                    "  hostFace=#" +
                    loop.HostFaceId.ToString(CultureInfo.InvariantCulture) +
                    " bound=#" +
                    loop.BoundId.ToString(CultureInfo.InvariantCulture) +
                    " axis=" +
                    loop.ProjectionAxis);
            }

        PrintRegions:
            if (report.Regions == null || report.Regions.Count == 0)
                return;

            Console.WriteLine("Detection regions:");
            foreach (var region in report.Regions
                .OrderBy(region => region.ViewName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(region => region.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(region => region.EntityId))
            {
                Console.WriteLine(
                    "  view=" +
                    region.ViewName +
                    " kind=" +
                    region.Kind +
                    " entity=#" +
                    region.EntityId.ToString(CultureInfo.InvariantCulture));
            }
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

        private static string GetDefaultRemovedGeometryOutputPath(string inputPath)
        {
            if (Directory.Exists(inputPath))
            {
                string fullInput = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parent = Path.GetDirectoryName(fullInput) ?? fullInput;
                string name = Path.GetFileName(fullInput);
                return string.Equals(name, "Original", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(parent, "RemovedGeometry")
                    : Path.Combine(fullInput, "RemovedGeometry");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;
            string parentDirectory = Path.GetDirectoryName(directory) ?? directory;
            string outputDirectory = string.Equals(Path.GetFileName(directory), "Original", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(parentDirectory, "RemovedGeometry")
                : directory;
            return Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(inputPath) + ".removed" + Path.GetExtension(inputPath));
        }

        private static string GetDefaultRemovedGeometryOutputPathForCleanOutput(string outputPath)
        {
            string fullOutputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(fullOutputPath) ?? string.Empty;
            string removedGeometryDirectory = GetDefaultRemovedGeometryDirectoryForCleanOutput(outputDirectory);
            return Path.Combine(
                removedGeometryDirectory,
                Path.GetFileNameWithoutExtension(fullOutputPath) + ".removed" + Path.GetExtension(fullOutputPath));
        }

        private static string GetDefaultRemovedGeometryDirectoryForCleanOutput(string outputDirectory)
        {
            string fullOutputDirectory = Path.GetFullPath(outputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(fullOutputDirectory) ?? fullOutputDirectory;
            string name = Path.GetFileName(fullOutputDirectory);
            return string.Equals(name, "Clean", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(parent, "RemovedGeometry")
                : Path.Combine(fullOutputDirectory, "RemovedGeometry");
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

        private static bool IsRemovedGeometryCommand(string arg)
        {
            return string.Equals(arg, "removed-geometry", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "removed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "removedgeometry", StringComparison.OrdinalIgnoreCase);
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

        private static bool RemoveProjectionEdgeFlag(List<string> args)
        {
            bool found = false;
            for (int i = args.Count - 1; i >= 0; i--)
            {
                if (!IsProjectionEdgeFlag(args[i]))
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

        private static bool IsProjectionEdgeFlag(string arg)
        {
            return string.Equals(arg, "--edge", StringComparison.OrdinalIgnoreCase);
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

        private sealed class DetectionRegionDocument
        {
            public string Model { get; set; }
            public int ImageSizePixels { get; set; }
            public int PaddingPixels { get; set; }
            public List<DetectionRegionRecord> Regions { get; set; } = new List<DetectionRegionRecord>();
        }

        private sealed class DetectionRegionRecord
        {
            public string ViewName { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int EntityId { get; set; }
            public string Kind { get; set; }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  StepCleaner <input.step> [output.step] [--debug] [--clean-text]");
            Console.WriteLine("  StepCleaner <input-directory> [output-directory] [--debug] [--clean-text]");
            Console.WriteLine("  StepCleaner detect <input.step|input-directory> [--debug]");
            Console.WriteLine("  StepCleaner removed-geometry <input.step|input-directory> [output.step|output-directory] [--clean-text]");
            Console.WriteLine("  StepCleaner project <input.step|input-directory> [projection-directory] [--edge]");
            Console.WriteLine();
            Console.WriteLine("When output.step is omitted, the cleaner writes <input>.clean.step next to the input file.");
            Console.WriteLine("When input-directory is named Original and output-directory is omitted, the cleaner writes to sibling Clean.");
            Console.WriteLine("The detect command runs automatic stage 1 detection only; marked JSON is not loaded.");
            Console.WriteLine("The removed-geometry command writes diagnostic STEP files containing the geometry selected for removal.");
            Console.WriteLine("The --debug option writes detected watermark region projection PNG files to Clean\\Detection.");
            Console.WriteLine("The --clean-text option additionally removes detected raised or cut text-string geometry.");
            Console.WriteLine("Cleanup returns " + PostCleanVerificationFailedExitCode.ToString(CultureInfo.InvariantCulture) + " when post-clean projection verification fails; failed comparison images are written to PostCleanVerification.");
            Console.WriteLine("The project command writes six PNG side projections and JSON mapping files; --edge writes aligned edge-only projections with __edge file suffixes.");
        }
    }
}
