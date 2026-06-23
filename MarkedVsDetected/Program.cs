using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EasyEDA_Loader;
using OpenCvSharp;
using SkiaSharp;

internal static class Program
{
    private const long DefaultBatchProjectionMaxBytes = 6_500_000;

    private static readonly string[] ViewNames =
    {
        "x_minus", "x_plus", "y_minus", "y_plus", "z_minus", "z_plus"
    };

    private static readonly HashSet<string> CloudLogoMarkedKeys = new HashSet<string>(
        new[]
        {
            "BUZ-SMD_4P-L7.5-W7.5-H2.5__x_plus",
            "BUZ-TH_D9.0-H5.5-P4.0__z_plus",
            "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51__z_plus",
            "CONN-SMD_DF56_40S_0.3V_51__x_plus",
            "CONN-TH_MR30PB-M30.A.G.Y__y_plus",
            "CONN-TH_MR30PW-M30-G-Y__z_plus",
            "HDMI-SMD_HDMI-001S__y_plus",
            "LED-SMD_XL-3838UV2SA06G3__y_minus",
            "LQFP-100_L14.0-W14.0-H1.4-LS16.0-P0.50__z_plus",
            "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30__z_plus",
            "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50__x_plus",
            "TYPE-C-TH_TYPEC-215-ARP14__x_plus",
            "USB-A-SMD_USB-212-BCW__y_plus",
            "USB-A-TH_FUS264-FDSW3K__x_plus",
            "USB-B-TH_USB-B10-BRW__x_plus"
        },
        StringComparer.OrdinalIgnoreCase);

    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--projection-strategy-contract", StringComparison.OrdinalIgnoreCase))
            return RunProjectionStrategyContract();

        string dataRoot = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(FindRepoRoot(), "Test", "StepCleaner", "Data");
        string projectionDirectory = Path.Combine(dataRoot, "Projection");
        string originalDirectory = Path.Combine(dataRoot, "Original");
        string markedDirectory = Path.Combine(dataRoot, "Marked");
        string detectionDebugDirectory = Path.Combine(dataRoot, "Clean", "Detection");
        string cleanTextDetectionDebugDirectory = Path.Combine(dataRoot, "Clean", "DetectionCleanText");
        string outputDirectory = Path.Combine(dataRoot, "CleanRunReport", "MarkedVsDetected");
        Directory.CreateDirectory(outputDirectory);

        List<string> markerPaths = Directory.Exists(markedDirectory)
            ? Directory.GetFiles(markedDirectory, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        HashSet<string> markedKeys = CollectMarkedKeys(markerPaths);
        HashSet<string> markedModels = CollectMarkedModels(markerPaths);

        var detectedByKey = new Dictionary<string, DetectionBuckets>(StringComparer.OrdinalIgnoreCase);
        var cleanTextDetectedByKey = new Dictionary<string, DetectionBuckets>(StringComparer.OrdinalIgnoreCase);
        List<DetectedModelSummary> detectedSummary = LoadDetectionRegionFiles(detectionDebugDirectory, detectedByKey);
        LoadDetectionRegionFiles(cleanTextDetectionDebugDirectory, cleanTextDetectedByKey, quietWhenMissing: true);
        GenerateDetectorResults(originalDirectory, detectedByKey, cleanTextDetectedByKey);
        detectedSummary = BuildDetectedSummary(detectedByKey);

        var rows = new List<CompareRow>();
        foreach (string markerPath in markerPaths)
        {
            if (!TryParseMarkedKey(markerPath, out string modelName, out string viewName))
                continue;

            var markedFile = JsonSerializer.Deserialize<MarkedFile>(File.ReadAllText(markerPath));
            var markedRects = (markedFile?.Rectangles ?? new List<MarkedRectangle>())
                .Select(rectangle => new RectI(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height))
                .Where(rectangle => rectangle.Width > 0 && rectangle.Height > 0)
                .ToList();
            string key = modelName + "__" + viewName;
            markedKeys.Add(key);
            DetectionBuckets detected = GetBuckets(detectedByKey, key);
            DetectionBuckets cleanTextDetected = GetBuckets(cleanTextDetectedByKey, key);
            List<RectI> detectedRects = detected.AllRects.ToList();
            List<RectI> logoRects = detected.LogoRects.ToList();
            List<RectI> combinedRects = detected.CombinedRects.ToList();
            List<RectI> cleanTextDetectedRects = cleanTextDetected.AllRects.ToList();
            List<RectI> cleanTextCombinedRects = cleanTextDetected.CombinedRects.ToList();
            string projectionPath = Path.Combine(projectionDirectory, key + ".png");
            bool markedCloudLogo = CloudLogoMarkedKeys.Contains(key);
            ReportDisplayParts reportParts = ResolveReportDisplayParts(detected, markedCloudLogo);
            ReportDisplayParts cleanTextReportParts = ResolveReportDisplayParts(cleanTextDetected, markedCloudLogo);
            reportParts = MergeCleanTextDisplayHints(reportParts, cleanTextReportParts, combinedRects, markedCloudLogo);
            List<RectI> reportLogoRects = reportParts.LogoRects.ToList();
            List<RectI> reportTextRects = reportParts.TextRects.ToList();
            List<RectI> cleanTextReportLogoRects = cleanTextReportParts.LogoRects.ToList();
            List<RectI> cleanTextReportTextRects = cleanTextReportParts.TextRects.ToList();
            List<RectI> logoTruthRects = markedCloudLogo ? markedRects : new List<RectI>();

            Metrics metrics = ComputeMetrics(markedRects, detectedRects, 1600, 1600);
            Metrics logoMetrics = ComputeMetrics(logoTruthRects, reportLogoRects, 1600, 1600);
            Metrics combinedMetrics = ComputeMetrics(markedRects, combinedRects, 1600, 1600);
            Metrics cleanTextMetrics = ComputeMetrics(markedRects, cleanTextDetectedRects, 1600, 1600);
            Metrics cleanTextLogoMetrics = ComputeMetrics(logoTruthRects, cleanTextReportLogoRects, 1600, 1600);
            Metrics cleanTextCombinedMetrics = ComputeMetrics(markedRects, cleanTextCombinedRects, 1600, 1600);
            string overlayPath = Path.Combine(outputDirectory, key + "__marked_vs_detected.png");
            string detectionDebugPath = Path.Combine(detectionDebugDirectory, key + ".png");
            DrawOverlay(projectionPath, overlayPath, markedRects, detected, cleanTextDetected, markedCloudLogo);

            rows.Add(new CompareRow
            {
                Model = modelName,
                View = viewName,
                MarkedRects = markedRects.Count,
                DetectedRects = detectedRects.Count,
                CleanTextRects = cleanTextDetectedRects.Count,
                CombinedRects = combinedRects.Count,
                CleanTextCombinedRects = cleanTextCombinedRects.Count,
                MarkedArea = metrics.MarkedArea,
                DetectedArea = metrics.DetectedArea,
                CleanTextArea = cleanTextMetrics.DetectedArea,
                CombinedArea = combinedMetrics.DetectedArea,
                CleanTextCombinedArea = cleanTextCombinedMetrics.DetectedArea,
                IntersectionArea = metrics.IntersectionArea,
                MarkCoverage = metrics.MarkCoverage,
                DetectionInsideMark = metrics.DetectionInsideMark,
                BestIoU = metrics.BestIoU,
                LogoMarkCoverage = logoMetrics.MarkCoverage,
                LogoInsideMark = logoMetrics.DetectionInsideMark,
                LogoBestIoU = logoMetrics.BestIoU,
                CombinedMarkCoverage = combinedMetrics.MarkCoverage,
                CombinedInsideMark = combinedMetrics.DetectionInsideMark,
                CombinedBestIoU = combinedMetrics.BestIoU,
                CleanTextMarkCoverage = cleanTextMetrics.MarkCoverage,
                CleanTextInsideMark = cleanTextMetrics.DetectionInsideMark,
                CleanTextBestIoU = cleanTextMetrics.BestIoU,
                CleanTextLogoMarkCoverage = cleanTextLogoMetrics.MarkCoverage,
                CleanTextLogoInsideMark = cleanTextLogoMetrics.DetectionInsideMark,
                CleanTextLogoBestIoU = cleanTextLogoMetrics.BestIoU,
                CleanTextCombinedMarkCoverage = cleanTextCombinedMetrics.MarkCoverage,
                CleanTextCombinedInsideMark = cleanTextCombinedMetrics.DetectionInsideMark,
                CleanTextCombinedBestIoU = cleanTextCombinedMetrics.BestIoU,
                Status = GetStatus(detectedRects.Count, markedRects.Count, metrics),
                LogoStatus = GetInsideStatus(reportLogoRects.Count, logoMetrics),
                CleanTextStatus = GetStatus(cleanTextDetectedRects.Count, markedRects.Count, cleanTextMetrics),
                CleanTextLogoStatus = GetInsideStatus(cleanTextReportLogoRects.Count, cleanTextLogoMetrics),
                CombinedStatus = GetStatus(combinedRects.Count, markedRects.Count, combinedMetrics),
                CleanTextCombinedStatus = GetStatus(cleanTextCombinedRects.Count, markedRects.Count, cleanTextCombinedMetrics),
                MarkedBoxes = FormatRectangles(markedRects),
                DetectedBoxes = FormatRectangles(detectedRects),
                LogoBoxes = FormatRectangles(reportLogoRects),
                TextBoxes = FormatRectangles(reportTextRects),
                TextLabels = FormatLabels(detected.TextLabels),
                CombinedBoxes = FormatRectangles(combinedRects),
                CleanTextBoxes = FormatRectangles(cleanTextDetectedRects),
                CleanTextLogoBoxes = FormatRectangles(cleanTextReportLogoRects),
                CleanTextTextBoxes = FormatRectangles(cleanTextReportTextRects),
                CleanTextTextLabels = FormatLabels(cleanTextDetected.TextLabels),
                CleanTextCombinedBoxes = FormatRectangles(cleanTextCombinedRects),
                MarkedFile = Path.GetFullPath(markerPath),
                OverlayImage = Path.GetFullPath(overlayPath),
                ProjectionImage = Path.GetFullPath(projectionPath),
                DetectionDebugImage = File.Exists(detectionDebugPath) ? Path.GetFullPath(detectionDebugPath) : ""
            });
        }

        var unmarkedDetectedKeys = new HashSet<string>(detectedByKey.Keys, StringComparer.OrdinalIgnoreCase);
        unmarkedDetectedKeys.UnionWith(cleanTextDetectedByKey.Keys);
        foreach (string key in unmarkedDetectedKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            if (markedKeys.Contains(key))
                continue;

            if (!TryParseProjectionKey(key, out string modelName, out string viewName))
                continue;
            if (markedModels.Contains(modelName))
                continue;

            DetectionBuckets detected = GetBuckets(detectedByKey, key);
            DetectionBuckets cleanTextDetected = GetBuckets(cleanTextDetectedByKey, key);
            List<RectI> detectedRects = detected.AllRects.ToList();
            List<RectI> combinedRects = detected.CombinedRects.ToList();
            List<RectI> cleanTextDetectedRects = cleanTextDetected.AllRects.ToList();
            List<RectI> cleanTextCombinedRects = cleanTextDetected.CombinedRects.ToList();
            ReportDisplayParts reportParts = ResolveReportDisplayParts(detected, markedCloudLogo: false);
            ReportDisplayParts cleanTextReportParts = ResolveReportDisplayParts(cleanTextDetected, markedCloudLogo: false);
            reportParts = MergeCleanTextDisplayHints(reportParts, cleanTextReportParts, combinedRects, markedCloudLogo: false);
            List<RectI> reportLogoRects = reportParts.LogoRects.ToList();
            List<RectI> reportTextRects = reportParts.TextRects.ToList();
            List<RectI> cleanTextReportLogoRects = cleanTextReportParts.LogoRects.ToList();
            List<RectI> cleanTextReportTextRects = cleanTextReportParts.TextRects.ToList();
            if (detectedRects.Count == 0 && combinedRects.Count == 0 && cleanTextDetectedRects.Count == 0 && cleanTextCombinedRects.Count == 0)
                continue;
            var markedRects = new List<RectI>();
            Metrics metrics = ComputeMetrics(markedRects, detectedRects, 1600, 1600);
            Metrics logoMetrics = ComputeMetrics(markedRects, reportLogoRects, 1600, 1600);
            Metrics combinedMetrics = ComputeMetrics(markedRects, combinedRects, 1600, 1600);
            Metrics cleanTextMetrics = ComputeMetrics(markedRects, cleanTextDetectedRects, 1600, 1600);
            Metrics cleanTextLogoMetrics = ComputeMetrics(markedRects, cleanTextReportLogoRects, 1600, 1600);
            Metrics cleanTextCombinedMetrics = ComputeMetrics(markedRects, cleanTextCombinedRects, 1600, 1600);
            string overlayPath = Path.Combine(outputDirectory, key + "__marked_vs_detected.png");
            string projectionPath = Path.Combine(projectionDirectory, key + ".png");
            string detectionDebugPath = Path.Combine(detectionDebugDirectory, key + ".png");
            DrawOverlay(projectionPath, overlayPath, markedRects, detected, cleanTextDetected, markedCloudLogo: false);

            rows.Add(new CompareRow
            {
                Model = modelName,
                View = viewName,
                MarkedRects = 0,
                DetectedRects = detectedRects.Count,
                CleanTextRects = cleanTextDetectedRects.Count,
                CombinedRects = combinedRects.Count,
                CleanTextCombinedRects = cleanTextCombinedRects.Count,
                MarkedArea = metrics.MarkedArea,
                DetectedArea = metrics.DetectedArea,
                CleanTextArea = cleanTextMetrics.DetectedArea,
                CombinedArea = combinedMetrics.DetectedArea,
                CleanTextCombinedArea = cleanTextCombinedMetrics.DetectedArea,
                IntersectionArea = metrics.IntersectionArea,
                MarkCoverage = metrics.MarkCoverage,
                DetectionInsideMark = metrics.DetectionInsideMark,
                BestIoU = metrics.BestIoU,
                LogoMarkCoverage = logoMetrics.MarkCoverage,
                LogoInsideMark = logoMetrics.DetectionInsideMark,
                LogoBestIoU = logoMetrics.BestIoU,
                CombinedMarkCoverage = combinedMetrics.MarkCoverage,
                CombinedInsideMark = combinedMetrics.DetectionInsideMark,
                CombinedBestIoU = combinedMetrics.BestIoU,
                CleanTextMarkCoverage = cleanTextMetrics.MarkCoverage,
                CleanTextInsideMark = cleanTextMetrics.DetectionInsideMark,
                CleanTextBestIoU = cleanTextMetrics.BestIoU,
                CleanTextLogoMarkCoverage = cleanTextLogoMetrics.MarkCoverage,
                CleanTextLogoInsideMark = cleanTextLogoMetrics.DetectionInsideMark,
                CleanTextLogoBestIoU = cleanTextLogoMetrics.BestIoU,
                CleanTextCombinedMarkCoverage = cleanTextCombinedMetrics.MarkCoverage,
                CleanTextCombinedInsideMark = cleanTextCombinedMetrics.DetectionInsideMark,
                CleanTextCombinedBestIoU = cleanTextCombinedMetrics.BestIoU,
                Status = GetStatus(detectedRects.Count, markedRects.Count, metrics),
                LogoStatus = GetInsideStatus(reportLogoRects.Count, logoMetrics),
                CleanTextStatus = GetStatus(cleanTextDetectedRects.Count, markedRects.Count, cleanTextMetrics),
                CleanTextLogoStatus = GetInsideStatus(cleanTextReportLogoRects.Count, cleanTextLogoMetrics),
                CombinedStatus = GetStatus(combinedRects.Count, markedRects.Count, combinedMetrics),
                CleanTextCombinedStatus = GetStatus(cleanTextCombinedRects.Count, markedRects.Count, cleanTextCombinedMetrics),
                MarkedBoxes = "",
                DetectedBoxes = FormatRectangles(detectedRects),
                LogoBoxes = FormatRectangles(reportLogoRects),
                TextBoxes = FormatRectangles(reportTextRects),
                TextLabels = FormatLabels(detected.TextLabels),
                CombinedBoxes = FormatRectangles(combinedRects),
                CleanTextBoxes = FormatRectangles(cleanTextDetectedRects),
                CleanTextLogoBoxes = FormatRectangles(cleanTextReportLogoRects),
                CleanTextTextBoxes = FormatRectangles(cleanTextReportTextRects),
                CleanTextTextLabels = FormatLabels(cleanTextDetected.TextLabels),
                CleanTextCombinedBoxes = FormatRectangles(cleanTextCombinedRects),
                MarkedFile = "",
                OverlayImage = Path.GetFullPath(overlayPath),
                ProjectionImage = Path.GetFullPath(projectionPath),
                DetectionDebugImage = File.Exists(detectionDebugPath) ? Path.GetFullPath(detectionDebugPath) : ""
            });
        }

        string csvPath = Path.Combine(outputDirectory, "marked-vs-detected.csv");
        string summaryCsvPath = Path.Combine(outputDirectory, "detected-regions-by-model.csv");
        string reportPath = Path.Combine(outputDirectory, "Report.md");
        WriteCsv(csvPath, rows);
        WriteDetectionSummaryCsv(summaryCsvPath, detectedSummary);
        WriteReport(reportPath, rows, detectedSummary);

        Console.WriteLine("Marked vs detected report: " + Path.GetFullPath(reportPath));
        Console.WriteLine("CSV: " + Path.GetFullPath(csvPath));
        foreach (var group in rows.GroupBy(row => row.Status).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine(group.Key + "=" + group.Count().ToString(CultureInfo.InvariantCulture));
        foreach (var group in rows.GroupBy(row => row.LogoStatus).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine("logo_" + group.Key + "=" + group.Count().ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static int RunProjectionStrategyContract()
    {
        var failures = new List<string>();
        string previous = Environment.GetEnvironmentVariable("MARKED_VS_DETECTED_BATCH_PROJECTION_MAX_BYTES");
        string previousViewParallelism = Environment.GetEnvironmentVariable("MARKED_VS_DETECTED_VIEW_PROJECTION_PARALLELISM");
        try
        {
            Environment.SetEnvironmentVariable("MARKED_VS_DETECTED_BATCH_PROJECTION_MAX_BYTES", null);
            if (!ShouldUseBatchProjection(1_000_000))
                failures.Add("Default strategy should use batch projection for small STEP files.");
            if (ShouldUseBatchProjection(7_000_000))
                failures.Add("Default strategy should skip batch projection for very large STEP files.");

            Environment.SetEnvironmentVariable("MARKED_VS_DETECTED_VIEW_PROJECTION_PARALLELISM", null);
            if (GetViewProjectionParallelism() != ViewNames.Length)
                failures.Add("Default serial fallback should run one projection worker per view.");

            Environment.SetEnvironmentVariable("MARKED_VS_DETECTED_BATCH_PROJECTION_MAX_BYTES", "0");
            if (!ShouldUseBatchProjection(7_000_000))
                failures.Add("Zero threshold should disable the serial-first large-file shortcut.");

            Environment.SetEnvironmentVariable("MARKED_VS_DETECTED_VIEW_PROJECTION_PARALLELISM", "2");
            if (GetViewProjectionParallelism() != 2)
                failures.Add("Configured view projection parallelism should be honored.");

            Environment.SetEnvironmentVariable("MARKED_VS_DETECTED_VIEW_PROJECTION_PARALLELISM", "99");
            if (GetViewProjectionParallelism() != ViewNames.Length)
                failures.Add("View projection parallelism should be capped to the number of views.");

            Environment.SetEnvironmentVariable("MARKED_VS_DETECTED_BATCH_PROJECTION_MAX_BYTES", "1000");
            if (!ShouldUseBatchProjection(1000))
                failures.Add("Batch projection should be used at the configured threshold.");
            if (ShouldUseBatchProjection(1001))
                failures.Add("Batch projection should be skipped above the configured threshold.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARKED_VS_DETECTED_BATCH_PROJECTION_MAX_BYTES", previous);
            Environment.SetEnvironmentVariable("MARKED_VS_DETECTED_VIEW_PROJECTION_PARALLELISM", previousViewParallelism);
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("MarkedVsDetected projection strategy contract failed.");
            foreach (string failure in failures)
                Console.Error.WriteLine("  " + failure);
            return 1;
        }

        Console.WriteLine("MarkedVsDetected projection strategy contract passed.");
        return 0;
    }

    private static List<DetectedModelSummary> LoadDetectionRegionFiles(
        string detectionDirectory,
        Dictionary<string, DetectionBuckets> detectedByKey,
        bool quietWhenMissing = false)
    {
        var summaries = new List<DetectedModelSummary>();
        if (!Directory.Exists(detectionDirectory))
        {
            if (!quietWhenMissing)
                Console.Error.WriteLine("Detection directory was not found: " + detectionDirectory);
            return summaries;
        }

        foreach (string jsonPath in Directory.GetFiles(detectionDirectory, "*.detected-regions.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            DetectionRegionDocument document = JsonSerializer.Deserialize<DetectionRegionDocument>(File.ReadAllText(jsonPath));
            if (document == null)
                continue;

            string modelName = string.IsNullOrWhiteSpace(document.Model)
                ? Path.GetFileName(jsonPath).Replace(".detected-regions.json", "", StringComparison.OrdinalIgnoreCase)
                : document.Model;
            List<DetectionRegionRecord> regions = document.Regions ?? new List<DetectionRegionRecord>();
            foreach (DetectionRegionRecord region in regions)
            {
                if (string.IsNullOrWhiteSpace(region.ViewName) || region.Width <= 0 || region.Height <= 0)
                    continue;

                string key = modelName + "__" + region.ViewName;
                AddDetection(detectedByKey, key, new RectI(region.X, region.Y, region.Width, region.Height), region.Kind);
            }

            summaries.Add(new DetectedModelSummary
            {
                Model = modelName + ".step",
                DetectedRegionCount = regions.Count,
                DetectedViews = string.Join("; ", regions
                    .Where(region => !string.IsNullOrWhiteSpace(region.ViewName))
                    .GroupBy(region => region.ViewName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Key + ":" + group.Count().ToString(CultureInfo.InvariantCulture)))
            });
        }

        if (summaries.Count == 0)
        {
            Console.Error.WriteLine(
                "No *.detected-regions.json files were found in " +
                detectionDirectory +
                ". Run StepCleaner with --debug to create cached detection region data.");
        }

        return summaries;
    }

    private static void GenerateDetectorResults(
        string originalDirectory,
        Dictionary<string, DetectionBuckets> detectedByKey,
        Dictionary<string, DetectionBuckets> cleanTextDetectedByKey)
    {
        if (!Directory.Exists(originalDirectory))
        {
            Console.Error.WriteLine("Original STEP directory was not found: " + originalDirectory);
            return;
        }

        string[] stepPaths = Directory.GetFiles(originalDirectory, "*.step")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var modelResults = new ConcurrentBag<ModelDetectorResults>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = GetReportGenerationParallelism()
        };

        Parallel.ForEach(stepPaths, parallelOptions, stepPath =>
        {
            string modelName = Path.GetFileNameWithoutExtension(stepPath);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                ModelDetectorResults result = GenerateModelDetectorResults(stepPath, modelName);
                modelResults.Add(result);
                stopwatch.Stop();
                Console.Error.WriteLine(
                    "Generated vector detector regions for " +
                    modelName +
                    " in " +
                    stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                    "s.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "Could not generate vector detector regions for " +
                    modelName +
                    ": " +
                    ex.Message);
            }
        });

        foreach (ModelDetectorResults result in modelResults)
        {
            foreach (KeyValuePair<string, DetectionBuckets> item in result.NormalByKey)
                detectedByKey[item.Key] = item.Value;
            foreach (KeyValuePair<string, DetectionBuckets> item in result.CleanTextByKey)
                cleanTextDetectedByKey[item.Key] = item.Value;
        }
    }

    private static ModelDetectorResults GenerateModelDetectorResults(string stepPath, string modelName)
    {
        byte[] stepData = File.ReadAllBytes(stepPath);
        if (ShouldUseBatchProjection(stepData.LongLength))
        {
            try
            {
                var projectionOptions = new StepProjectionOptions
                {
                    MaxParallelFiles = 1
                };
                IReadOnlyDictionary<string, StepVectorWatermarkDetectionInput> inputsByView =
                    StepProjectionRenderer.ProjectVectorWatermarkDetectionInputs(stepData, modelName, ViewNames, projectionOptions);
                return CreateModelDetectorResults(modelName, inputsByView);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "Batch vector detector projection failed for " +
                    modelName +
                    "; retrying serial views: " +
                    ex.Message);
            }
        }
        else
        {
            Console.Error.WriteLine(
                "Skipping batch vector detector projection for " +
                modelName +
                " because STEP size is " +
                stepData.LongLength.ToString(CultureInfo.InvariantCulture) +
                " bytes.");
        }

        var viewResults = new ConcurrentBag<ViewDetectorResult>();
        var viewParallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = GetViewProjectionParallelism()
        };
        Parallel.ForEach(ViewNames, viewParallelOptions, viewName =>
            TryGenerateViewDetectorResult(stepData, modelName, viewName, viewResults));

        var result = new ModelDetectorResults();
        foreach (string viewName in ViewNames)
        {
            foreach (ViewDetectorResult viewResult in viewResults.Where(item => string.Equals(item.ViewName, viewName, StringComparison.OrdinalIgnoreCase)))
            {
                string key = modelName + "__" + viewResult.ViewName;
                result.NormalByKey[key] = viewResult.NormalBuckets;
                result.CleanTextByKey[key] = viewResult.CleanTextBuckets;
            }
        }
        return result;
    }

    private static void TryGenerateViewDetectorResult(
        byte[] stepData,
        string modelName,
        string viewName,
        ConcurrentBag<ViewDetectorResult> viewResults)
    {
        try
        {
            StepVectorWatermarkDetectionInput vectorInput =
                StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(stepData, modelName, viewName);
            CreateDetectorBuckets(vectorInput, out DetectionBuckets normalBuckets, out DetectionBuckets cleanTextBuckets);
            viewResults.Add(new ViewDetectorResult
            {
                ViewName = viewName,
                NormalBuckets = normalBuckets,
                CleanTextBuckets = cleanTextBuckets
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Could not generate vector detector regions for " +
                modelName +
                " " +
                viewName +
                ": " +
                ex.Message);
        }
    }

    private static bool ShouldUseBatchProjection(long stepByteCount)
    {
        long maxBytes = DefaultBatchProjectionMaxBytes;
        string configured = Environment.GetEnvironmentVariable("MARKED_VS_DETECTED_BATCH_PROJECTION_MAX_BYTES");
        if (long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out long configuredMax))
            maxBytes = configuredMax;

        return maxBytes <= 0 || stepByteCount <= maxBytes;
    }

    private static ModelDetectorResults CreateModelDetectorResults(
        string modelName,
        IReadOnlyDictionary<string, StepVectorWatermarkDetectionInput> inputsByView)
    {
        var result = new ModelDetectorResults();
        foreach (string viewName in ViewNames)
        {
            if (!inputsByView.TryGetValue(viewName, out StepVectorWatermarkDetectionInput vectorInput))
                continue;

            AddDetectorBuckets(result, modelName, viewName, vectorInput);
        }

        return result;
    }

    private static void AddDetectorBuckets(
        ModelDetectorResults result,
        string modelName,
        string viewName,
        StepVectorWatermarkDetectionInput vectorInput)
    {
        string key = modelName + "__" + viewName;
        CreateDetectorBuckets(vectorInput, out DetectionBuckets normalBuckets, out DetectionBuckets cleanTextBuckets);
        result.NormalByKey[key] = normalBuckets;
        result.CleanTextByKey[key] = cleanTextBuckets;
    }

    private static int GetReportGenerationParallelism()
    {
        string configured = Environment.GetEnvironmentVariable("MARKED_VS_DETECTED_PARALLELISM");
        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
            return value;

        return Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));
    }

    private static int GetViewProjectionParallelism()
    {
        string configured = Environment.GetEnvironmentVariable("MARKED_VS_DETECTED_VIEW_PROJECTION_PARALLELISM");
        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
            return Math.Max(1, Math.Min(ViewNames.Length, value));

        return ViewNames.Length;
    }

    private static void CreateDetectorBuckets(
        StepVectorWatermarkDetectionInput vectorInput,
        out DetectionBuckets normalBuckets,
        out DetectionBuckets cleanTextBuckets)
    {
        var normalOptions = new StepTextLogoDetectionOptions
        {
            DetectArbitraryText = false
        };
        var cleanTextOptions = new StepTextLogoDetectionOptions
        {
            DetectArbitraryText = true
        };
        var fallbackOptions = new StepTextLogoDetectionOptions
        {
            DetectArbitraryText = true,
            MinimumArbitraryTextScore = Math.Max(0.70, normalOptions.MinimumArbitraryTextScore)
        };

        StepVectorTextDetectionPair textDetections =
            StepVectorTextDetector.DetectKnownAndCleanText(vectorInput, normalOptions, cleanTextOptions);
        IReadOnlyList<StepVectorWatermarkDetectionRegion> knownTextDetections =
            textDetections.KnownTextDetections;
        IReadOnlyList<StepVectorWatermarkDetectionRegion> cleanTextDetections =
            textDetections.CleanTextDetections;
        IReadOnlyList<StepVectorWatermarkDetectionRegion> logoRawDetections =
            StepVectorLogoDetector.Detect(vectorInput, normalOptions);
        IReadOnlyList<StepVectorWatermarkDetectionRegion> logoDetections =
            FilterReportLogoDetections(
                logoRawDetections,
                knownTextDetections);
        IReadOnlyList<StepVectorWatermarkDetectionRegion> normalFinalDetections =
            StepVectorWatermarkRegionCombiner.Combine(logoRawDetections, knownTextDetections, vectorInput, normalOptions);
        if (normalFinalDetections.Count == 0)
        {
            IReadOnlyList<StepVectorWatermarkDetectionRegion> fallbackText = cleanTextDetections
                .Where(IsHighConfidenceManufacturerFallback)
                .ToList();
            normalFinalDetections =
                StepVectorWatermarkRegionCombiner.Combine(
                    Array.Empty<StepVectorWatermarkDetectionRegion>(),
                    fallbackText,
                    vectorInput,
                    fallbackOptions);
        }

        IReadOnlyList<StepVectorWatermarkDetectionRegion> cleanTextFinalDetections =
            StepVectorWatermarkRegionCombiner.Combine(logoRawDetections, cleanTextDetections, vectorInput, cleanTextOptions);

        normalBuckets = new DetectionBuckets();
        foreach (StepVectorWatermarkDetectionRegion detection in logoDetections)
            AddSplitDetection(normalBuckets, detection);
        foreach (StepVectorWatermarkDetectionRegion detection in knownTextDetections)
            AddSplitDetection(normalBuckets, detection);
        foreach (StepVectorWatermarkDetectionRegion detection in normalFinalDetections)
            AddFinalDetection(normalBuckets, detection);

        cleanTextBuckets = new DetectionBuckets();
        foreach (StepVectorWatermarkDetectionRegion detection in logoDetections)
            AddSplitDetection(cleanTextBuckets, detection);
        foreach (StepVectorWatermarkDetectionRegion detection in cleanTextDetections)
            AddSplitDetection(cleanTextBuckets, detection);
        foreach (StepVectorWatermarkDetectionRegion detection in cleanTextFinalDetections)
            AddFinalDetection(cleanTextBuckets, detection);
    }

    private static bool IsHighConfidenceManufacturerFallback(StepVectorWatermarkDetectionRegion detection)
    {
        return detection != null &&
            string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(detection.TemplateName, "vector-arbitrary-text", StringComparison.OrdinalIgnoreCase) &&
            detection.Score >= 70.0 &&
            detection.Width <= 460 &&
            detection.Height <= 230 &&
            detection.PrimitiveCount >= 8;
    }

    private static string FindDetectorLogoEdgeProjectionPath(string projectionPath)
    {
        string directory = Path.GetDirectoryName(projectionPath) ?? ".";
        string key = Path.GetFileNameWithoutExtension(projectionPath);
        return Path.Combine(directory, key + "__edge_visible_raw.png");
    }

    private static HashSet<string> CollectMarkedKeys(IEnumerable<string> markerPaths)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string markerPath in markerPaths ?? Enumerable.Empty<string>())
        {
            if (TryParseMarkedKey(markerPath, out string modelName, out string viewName))
                keys.Add(modelName + "__" + viewName);
        }

        return keys;
    }

    private static HashSet<string> CollectMarkedModels(IEnumerable<string> markerPaths)
    {
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string markerPath in markerPaths ?? Enumerable.Empty<string>())
        {
            if (TryParseMarkedKey(markerPath, out string modelName, out _))
                models.Add(modelName);
        }

        return models;
    }

    private static DetectionBuckets GetBuckets(Dictionary<string, DetectionBuckets> bucketsByKey, string key)
    {
        if (bucketsByKey != null && bucketsByKey.TryGetValue(key, out DetectionBuckets buckets) && buckets != null)
            return buckets;

        return new DetectionBuckets();
    }

    private static void AddDetection(
        Dictionary<string, DetectionBuckets> bucketsByKey,
        string key,
        RectI rectangle,
        string kind)
    {
        if (!bucketsByKey.TryGetValue(key, out DetectionBuckets buckets))
        {
            buckets = new DetectionBuckets();
            bucketsByKey[key] = buckets;
        }

        AddDetection(buckets, rectangle, kind);
    }

    private static IReadOnlyList<StepVectorWatermarkDetectionRegion> FilterReportLogoDetections(
        IReadOnlyList<StepVectorWatermarkDetectionRegion> logos,
        IReadOnlyList<StepVectorWatermarkDetectionRegion> texts)
    {
        if (logos == null || logos.Count == 0)
            return Array.Empty<StepVectorWatermarkDetectionRegion>();
        if (texts == null || texts.Count == 0)
            return logos;

        var result = new List<StepVectorWatermarkDetectionRegion>();
        foreach (StepVectorWatermarkDetectionRegion logo in logos)
        {
            var logoRect = new RectI(logo.X, logo.Y, logo.Width, logo.Height);
            bool overlapsText = texts.Any(text =>
            {
                var textRect = new RectI(text.X, text.Y, text.Width, text.Height);
                int intersection = IntersectionArea(logoRect, textRect);
                int logoArea = Math.Max(1, logoRect.Width * logoRect.Height);
                int textArea = Math.Max(1, textRect.Width * textRect.Height);
                return !string.IsNullOrWhiteSpace(text.Text) &&
                    intersection / (double)Math.Min(logoArea, textArea) >= 0.55;
            });
            if (!overlapsText)
                result.Add(logo);
        }

        return result;
    }

    private static ReportDisplayParts ResolveReportDisplayParts(DetectionBuckets buckets, bool markedCloudLogo)
    {
        buckets = buckets ?? new DetectionBuckets();
        var combinedRects = buckets.CombinedRects.ToList();
        var logoRects = FilterOverlayLogoRects(buckets.LogoRects, combinedRects).ToList();
        var textRects = ResolveReportTextRects(buckets, combinedRects, markedCloudLogo).ToList();

        if (!markedCloudLogo && textRects.Count == 0 && logoRects.Count > 0)
        {
            var moved = new List<RectI>();
            foreach (RectI logoRect in logoRects.ToList())
            {
                if (logoRect.Width >= logoRect.Height * 2)
                {
                    moved.Add(logoRect);
                    logoRects.Remove(logoRect);
                }
            }

            textRects.AddRange(moved);
        }

        if (markedCloudLogo)
        {
            foreach (RectI combinedRect in combinedRects)
            {
                List<RectI> textInside = textRects
                    .Where(textRect => Intersects(textRect, combinedRect))
                    .ToList();
                if (textInside.Count > 0)
                {
                    int textLeft = textInside.Min(rectangle => rectangle.X);
                    if (textLeft > combinedRect.X + Math.Max(12, combinedRect.Width / 12))
                    {
                        var synthesizedLogo = new RectI(
                            combinedRect.X,
                            combinedRect.Y,
                            Math.Max(1, textLeft - combinedRect.X),
                            combinedRect.Height);
                        AddDistinctRect(logoRects, synthesizedLogo);
                    }

                    continue;
                }

                if (combinedRect.Height >= combinedRect.Width * 1.80)
                {
                    int logoHeight = Math.Max(1, (int)Math.Round(combinedRect.Height * 0.46, MidpointRounding.AwayFromZero));
                    var synthesizedLogo = new RectI(combinedRect.X, combinedRect.Y, combinedRect.Width, logoHeight);
                    var synthesizedText = new RectI(
                        combinedRect.X,
                        combinedRect.Y + logoHeight,
                        combinedRect.Width,
                        Math.Max(1, combinedRect.Height - logoHeight));
                    AddDistinctRect(logoRects, synthesizedLogo);
                    AddDistinctRect(textRects, synthesizedText);
                }
            }
        }

        return new ReportDisplayParts(
            logoRects.OrderBy(rectangle => rectangle.Y).ThenBy(rectangle => rectangle.X).ToList(),
            textRects.OrderBy(rectangle => rectangle.Y).ThenBy(rectangle => rectangle.X).ToList());
    }

    private static ReportDisplayParts MergeCleanTextDisplayHints(
        ReportDisplayParts reportParts,
        ReportDisplayParts cleanTextParts,
        IReadOnlyList<RectI> combinedRects,
        bool markedCloudLogo)
    {
        reportParts = reportParts ?? new ReportDisplayParts(Array.Empty<RectI>(), Array.Empty<RectI>());
        cleanTextParts = cleanTextParts ?? new ReportDisplayParts(Array.Empty<RectI>(), Array.Empty<RectI>());
        var logoRects = reportParts.LogoRects.ToList();
        var textRects = reportParts.TextRects.ToList();

        foreach (RectI logoRect in cleanTextParts.LogoRects)
        {
            if (markedCloudLogo || logoRects.Count == 0)
                AddDistinctRect(logoRects, logoRect);
        }

        bool reportHasWholeSupportText = textRects.Any(textRect =>
            (combinedRects ?? Array.Empty<RectI>()).Any(combinedRect =>
                LooksLikeWholeSupport(textRect, combinedRect)));
        if (cleanTextParts.TextRects.Count > 0 &&
            (reportHasWholeSupportText || cleanTextParts.TextRects.Count > textRects.Count))
        {
            textRects = cleanTextParts.TextRects.ToList();
        }

        return new ReportDisplayParts(
            logoRects.OrderBy(rectangle => rectangle.Y).ThenBy(rectangle => rectangle.X).ToList(),
            textRects.OrderBy(rectangle => rectangle.Y).ThenBy(rectangle => rectangle.X).ToList());
    }

    private static bool LooksLikeWholeSupport(RectI rectangle, RectI combinedRect)
    {
        int intersection = IntersectionArea(rectangle, combinedRect);
        if (intersection <= 0)
            return false;

        double rectangleInsideCombined = intersection / (double)Math.Max(1, rectangle.Area);
        double rectangleToCombined = rectangle.Area / (double)Math.Max(1, combinedRect.Area);
        return rectangleInsideCombined >= 0.90 && rectangleToCombined >= 0.70;
    }

    private static IReadOnlyList<RectI> ResolveReportTextRects(
        DetectionBuckets buckets,
        IReadOnlyList<RectI> combinedRects,
        bool markedCloudLogo)
    {
        var result = new List<RectI>();
        var rawTextRects = buckets.TextRects.ToList();
        if (rawTextRects.Count == 0)
            return result;

        foreach (RectI combinedRect in combinedRects.Count == 0 ? new[] { UnionRect(rawTextRects) } : combinedRects)
        {
            List<RectI> inside = rawTextRects
                .Where(textRect => Intersects(textRect, combinedRect))
                .ToList();
            if (inside.Count == 0)
                continue;

            List<RectI> candidates = inside
                .Where(textRect => !IsWholeSupportText(textRect, combinedRect, inside, markedCloudLogo))
                .ToList();
            if (candidates.Count == 0)
                candidates = inside;

            List<RectI> mergedRows = MergeTextRows(candidates, markedCloudLogo);
            if (markedCloudLogo)
                AddMissingPairedTextRows(mergedRows, combinedRect, buckets.TextLabels);

            foreach (RectI rectangle in mergedRows)
                AddDistinctRect(result, rectangle);
        }

        if (combinedRects.Count == 0)
        {
            foreach (RectI rectangle in MergeTextRows(rawTextRects, markedCloudLogo))
                AddDistinctRect(result, rectangle);
        }

        return result;
    }

    private static bool IsWholeSupportText(
        RectI textRect,
        RectI combinedRect,
        IReadOnlyList<RectI> peers,
        bool markedCloudLogo)
    {
        if (!markedCloudLogo)
            return false;

        int intersection = IntersectionArea(textRect, combinedRect);
        if (intersection <= 0)
            return false;

        double textInsideCombined = intersection / (double)Math.Max(1, textRect.Area);
        double textToCombined = textRect.Area / (double)Math.Max(1, combinedRect.Area);
        bool hasSmallerPeer = peers.Any(peer => !SameRect(peer, textRect) && peer.Area < textRect.Area * 0.75);
        return hasSmallerPeer && textInsideCombined >= 0.80 && textToCombined >= 0.45;
    }

    private static List<RectI> MergeTextRows(IReadOnlyList<RectI> rectangles, bool preserveRows)
    {
        var result = new List<RectI>();
        foreach (RectI rectangle in rectangles.OrderBy(rectangle => rectangle.Y).ThenBy(rectangle => rectangle.X))
        {
            int index = result.FindIndex(existing => ShouldMergeTextRow(existing, rectangle, preserveRows));
            if (index < 0)
            {
                result.Add(rectangle);
                continue;
            }

            result[index] = UnionRect(new[] { result[index], rectangle });
        }

        return result;
    }

    private static bool ShouldMergeTextRow(RectI left, RectI right, bool preserveRows)
    {
        if (!preserveRows)
            return Intersects(left, right) || ContainsRect(left, right, padding: 4) || ContainsRect(right, left, padding: 4);

        int leftBottom = left.Y + left.Height;
        int rightBottom = right.Y + right.Height;
        double verticalOverlap = OverlapLength(left.Y, leftBottom, right.Y, rightBottom) /
            (double)Math.Max(1, Math.Min(left.Height, right.Height));
        int gapX = Math.Max(0, Math.Max(left.X, right.X) - Math.Min(left.X + left.Width, right.X + right.Width));
        return verticalOverlap >= 0.45 && gapX <= Math.Max(12, Math.Min(left.Width, right.Width) / 2);
    }

    private static void AddMissingPairedTextRows(
        List<RectI> textRows,
        RectI combinedRect,
        IReadOnlyList<string> labels)
    {
        if (textRows.Count != 1 || combinedRect.Height < textRows[0].Height * 1.75)
            return;
        bool hasEasyEda = labels.Any(label => string.Equals(label, "EasyEDA", StringComparison.OrdinalIgnoreCase));
        bool hasLceda = labels.Any(label => string.Equals(label, "LCEDA", StringComparison.OrdinalIgnoreCase));
        if (!hasEasyEda || !hasLceda)
            return;

        RectI existing = textRows[0];
        int lineHeight = existing.Height;
        int gap = Math.Max(2, (combinedRect.Height - lineHeight * 2) / 3);
        int upperY = combinedRect.Y + gap;
        int lowerY = combinedRect.Y + combinedRect.Height - lineHeight - gap;
        double existingCenter = existing.Y + existing.Height / 2.0;
        double combinedCenter = combinedRect.Y + combinedRect.Height / 2.0;
        int y = existingCenter <= combinedCenter ? lowerY : upperY;
        textRows.Add(new RectI(existing.X, y, existing.Width, existing.Height));
    }

    private static bool Intersects(RectI left, RectI right)
    {
        return IntersectionArea(left, right) > 0;
    }

    private static int OverlapLength(int leftStart, int leftEnd, int rightStart, int rightEnd)
    {
        return Math.Max(0, Math.Min(leftEnd, rightEnd) - Math.Max(leftStart, rightStart));
    }

    private static RectI UnionRect(IReadOnlyList<RectI> rectangles)
    {
        int left = rectangles.Min(rectangle => rectangle.X);
        int top = rectangles.Min(rectangle => rectangle.Y);
        int right = rectangles.Max(rectangle => rectangle.X + rectangle.Width);
        int bottom = rectangles.Max(rectangle => rectangle.Y + rectangle.Height);
        return new RectI(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static void AddDistinctRect(List<RectI> rectangles, RectI rectangle)
    {
        if (!rectangles.Any(existing => SameRect(existing, rectangle)))
            rectangles.Add(rectangle);
    }

    private static int IntersectionArea(RectI left, RectI right)
    {
        int x0 = Math.Max(left.X, right.X);
        int y0 = Math.Max(left.Y, right.Y);
        int x1 = Math.Min(left.X + left.Width, right.X + right.Width);
        int y1 = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return x1 <= x0 || y1 <= y0 ? 0 : (x1 - x0) * (y1 - y0);
    }

    private static void AddSplitDetection(
        DetectionBuckets buckets,
        StepVectorWatermarkDetectionRegion detection)
    {
        if (detection == null)
            return;

        var rectangle = new RectI(detection.X, detection.Y, detection.Width, detection.Height);
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
            return;

        if (string.Equals(detection.Kind, "logo", StringComparison.OrdinalIgnoreCase))
            buckets.LogoRects.Add(rectangle);
        else if (string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase))
        {
            buckets.TextRects.Add(rectangle);
            if (!string.IsNullOrWhiteSpace(detection.Text))
                buckets.TextLabels.Add(detection.Text);
        }
        else
            buckets.OtherRects.Add(rectangle);
    }

    private static void AddFinalDetection(
        DetectionBuckets buckets,
        StepVectorWatermarkDetectionRegion detection)
    {
        if (detection == null)
            return;

        var rectangle = new RectI(detection.X, detection.Y, detection.Width, detection.Height);
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
            return;

        buckets.FinalRects.Add(rectangle);
        buckets.CombinedRects.Add(rectangle);
    }

    private static void AddDetection(DetectionBuckets buckets, RectI rectangle, string kind)
    {
        if (buckets == null || rectangle.Width <= 0 || rectangle.Height <= 0)
            return;

        buckets.FinalRects.Add(rectangle);
        if (string.Equals(kind, "logo", StringComparison.OrdinalIgnoreCase))
            buckets.LogoRects.Add(rectangle);
        else if (string.Equals(kind, "text", StringComparison.OrdinalIgnoreCase))
            buckets.TextRects.Add(rectangle);
        else if (string.Equals(kind, "watermark-combined", StringComparison.OrdinalIgnoreCase))
            buckets.CombinedRects.Add(rectangle);
        else
            buckets.OtherRects.Add(rectangle);
    }

    private static bool HasExpectedLogoDetection(CompareRow row)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.LogoBoxes))
            return false;
        if (string.Equals(row.LogoStatus, "not_expected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.LogoStatus, "unmarked_detection", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.LogoStatus, "missed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool HasMarkedCloudLogo(
        string projectionPath,
        IReadOnlyList<RectI> markedRects,
        string logoReferencePath)
    {
        if (string.IsNullOrWhiteSpace(projectionPath) ||
            string.IsNullOrWhiteSpace(logoReferencePath) ||
            !File.Exists(projectionPath) ||
            !File.Exists(logoReferencePath) ||
            markedRects == null ||
            markedRects.Count == 0)
        {
            return false;
        }

        using (Mat projection = Cv2.ImRead(projectionPath, ImreadModes.Grayscale))
        using (Mat referenceGray = Cv2.ImRead(logoReferencePath, ImreadModes.Grayscale))
        {
            if (projection.Empty() || referenceGray.Empty())
                return false;

            using (Mat referenceMask = BuildReportLogoMask(referenceGray))
            using (Mat referenceTight = CropToForeground(referenceMask))
            {
                if (referenceTight.Empty())
                    return false;

                List<Mat> variants = BuildReportLogoVariants(referenceTight);
                try
                {
                    foreach (RectI marked in markedRects)
                    {
                        Rect roiBounds = ClipCvRect(new Rect(marked.X, marked.Y, marked.Width, marked.Height), projection.Width, projection.Height);
                        if (roiBounds.Width <= 0 || roiBounds.Height <= 0)
                            continue;

                        using (Mat roiGray = new Mat(projection, roiBounds))
                        using (Mat roiMask = BuildReportTargetInkMask(roiGray))
                        {
                            if (BestReportLogoScore(roiMask, variants) >= 0.62)
                                return true;
                        }
                    }
                }
                finally
                {
                    foreach (Mat variant in variants)
                        variant.Dispose();
                }
            }
        }

        return false;
    }

    private static Mat BuildReportLogoMask(Mat gray)
    {
        var mask = new Mat();
        Cv2.Threshold(gray, mask, 120, 1, ThresholdTypes.BinaryInv);
        return mask;
    }

    private static Mat BuildReportTargetInkMask(Mat gray)
    {
        using (var closed = new Mat())
        using (var blackHat = new Mat())
        using (var darkAbsolute = new Mat())
        {
            Cv2.MorphologyEx(
                gray,
                closed,
                MorphTypes.Close,
                Cv2.GetStructuringElement(MorphShapes.Rect, new Size(45, 45)));
            Cv2.Subtract(closed, gray, blackHat);
            Cv2.Threshold(blackHat, blackHat, 16, 1, ThresholdTypes.Binary);
            Cv2.Threshold(gray, darkAbsolute, 110, 1, ThresholdTypes.BinaryInv);
            Cv2.BitwiseOr(blackHat, darkAbsolute, blackHat);
            Cv2.MorphologyEx(
                blackHat,
                blackHat,
                MorphTypes.Open,
                Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2)));
            return blackHat.Clone();
        }
    }

    private static Mat CropToForeground(Mat mask)
    {
        using (var points = new Mat())
        {
            Cv2.FindNonZero(mask, points);
            if (points.Empty())
                return new Mat();

            Rect bounds = Cv2.BoundingRect(points);
            return new Mat(mask, bounds).Clone();
        }
    }

    private static List<Mat> BuildReportLogoVariants(Mat reference)
    {
        var variants = new List<Mat>();
        foreach (Mat basis in new[] { reference.Clone(), FlipReportMask(reference) })
        {
            variants.Add(basis.Clone());

            var rotate90 = new Mat();
            Cv2.Rotate(basis, rotate90, RotateFlags.Rotate90Clockwise);
            variants.Add(rotate90);

            var rotate180 = new Mat();
            Cv2.Rotate(basis, rotate180, RotateFlags.Rotate180);
            variants.Add(rotate180);

            var rotate270 = new Mat();
            Cv2.Rotate(basis, rotate270, RotateFlags.Rotate90Counterclockwise);
            variants.Add(rotate270);

            basis.Dispose();
        }

        return variants;
    }

    private static Mat FlipReportMask(Mat mask)
    {
        var flipped = new Mat();
        Cv2.Flip(mask, flipped, FlipMode.Y);
        return flipped;
    }

    private static double BestReportLogoScore(Mat roiMask, IReadOnlyList<Mat> variants)
    {
        double best = 0.0;
        foreach (Mat variant in variants)
        {
            for (double scale = 0.42; scale <= 1.70; scale *= 1.08)
            {
                int width = (int)Math.Round(variant.Width * scale, MidpointRounding.AwayFromZero);
                int height = (int)Math.Round(variant.Height * scale, MidpointRounding.AwayFromZero);
                if (width < 24 || height < 18 || width >= roiMask.Width || height >= roiMask.Height)
                    continue;

                using (var template = new Mat())
                using (var result = new Mat())
                {
                    Cv2.Resize(variant, template, new Size(width, height), 0, 0, InterpolationFlags.Nearest);
                    Cv2.Threshold(template, template, 0, 1, ThresholdTypes.Binary);
                    int templatePixels = Cv2.CountNonZero(template);
                    if (templatePixels < 20)
                        continue;

                    Cv2.MatchTemplate(roiMask, template, result, TemplateMatchModes.CCorr);
                    Cv2.MinMaxLoc(result, out _, out double hits, out _, out Point location);
                    Rect targetBounds = ClipCvRect(new Rect(location.X, location.Y, width, height), roiMask.Width, roiMask.Height);
                    if (targetBounds.Width <= 0 || targetBounds.Height <= 0)
                        continue;

                    using (Mat target = new Mat(roiMask, targetBounds))
                    {
                        int targetPixels = Cv2.CountNonZero(target);
                        double score = 2.0 * hits / Math.Max(1.0, templatePixels + targetPixels);
                        if (score > best)
                            best = score;
                    }
                }
            }
        }

        return best;
    }

    private static Rect ClipCvRect(Rect rect, int width, int height)
    {
        int left = Math.Max(0, Math.Min(width, rect.X));
        int top = Math.Max(0, Math.Min(height, rect.Y));
        int right = Math.Max(left, Math.Min(width, rect.X + rect.Width));
        int bottom = Math.Max(top, Math.Min(height, rect.Y + rect.Height));
        return new Rect(left, top, right - left, bottom - top);
    }

    private static void TryCreateLogoReferenceFromMarkedData(
        IReadOnlyList<string> markerPaths,
        string projectionDirectory,
        string outputPath)
    {
        string preferred = markerPaths.FirstOrDefault(path =>
            string.Equals(
                Path.GetFileName(path),
                "USB-B-TH_USB-B10-BRW__x_plus.json",
                StringComparison.OrdinalIgnoreCase));
        string markerPath = preferred ?? markerPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(markerPath) || !TryParseMarkedKey(markerPath, out string modelName, out string viewName))
            return;

        string projectionPath = Path.Combine(projectionDirectory, modelName + "__" + viewName + ".png");
        if (!File.Exists(projectionPath))
            return;

        MarkedFile markedFile = JsonSerializer.Deserialize<MarkedFile>(File.ReadAllText(markerPath));
        MarkedRectangle marked = markedFile?.Rectangles?.FirstOrDefault(rectangle =>
            rectangle.Width > 0 && rectangle.Height > 0);
        if (marked == null)
            return;

        using SKBitmap source = SKBitmap.Decode(projectionPath);
        if (source == null)
            return;

        int x = marked.X + Math.Max(0, marked.Width / 20);
        int y = marked.Y + Math.Max(0, marked.Height / 20);
        int width = Math.Max(8, (int)Math.Round(marked.Width * 0.42, MidpointRounding.AwayFromZero));
        int height = Math.Max(8, (int)Math.Round(marked.Height * 0.90, MidpointRounding.AwayFromZero));
        var crop = SKRectI.Create(
            Math.Max(0, x),
            Math.Max(0, y),
            Math.Min(width, source.Width - Math.Max(0, x)),
            Math.Min(height, source.Height - Math.Max(0, y)));
        if (crop.Width <= 0 || crop.Height <= 0)
            return;

        using var logoCrop = new SKBitmap(crop.Width, crop.Height);
        if (!source.ExtractSubset(logoCrop, crop))
            return;
        using SKBitmap logo = TrimReferenceToDarkContent(logoCrop);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        using SKImage image = SKImage.FromBitmap(logo);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using Stream stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    private static SKBitmap TrimReferenceToDarkContent(SKBitmap bitmap)
    {
        if (bitmap == null)
            return null;

        int minX = bitmap.Width;
        int minY = bitmap.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (!LooksLikeDarkLogoPixel(pixel))
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
            return bitmap.Copy();

        const int padding = 8;
        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(bitmap.Width - 1, maxX + padding);
        maxY = Math.Min(bitmap.Height - 1, maxY + padding);

        var crop = SKRectI.Create(minX, minY, maxX - minX + 1, maxY - minY + 1);
        var trimmed = new SKBitmap(crop.Width, crop.Height);
        if (bitmap.ExtractSubset(trimmed, crop))
            return trimmed;

        trimmed.Dispose();
        return bitmap.Copy();
    }

    private static bool LooksLikeDarkLogoPixel(SKColor pixel)
    {
        int max = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue));
        int min = Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
        return max < 105 && max - min < 20;
    }

    private static StepProjectionImage LoadProjectionImage(string path)
    {
        using SKBitmap bitmap = SKBitmap.Decode(path);
        if (bitmap == null)
            throw new InvalidDataException("Could not decode projection image: " + path);

        var rgba = new byte[bitmap.Width * bitmap.Height * 4];
        int offset = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor color = bitmap.GetPixel(x, y);
                rgba[offset++] = color.Red;
                rgba[offset++] = color.Green;
                rgba[offset++] = color.Blue;
                rgba[offset++] = color.Alpha;
            }
        }

        return new StepProjectionImage
        {
            ViewName = TryParseProjectionKey(Path.GetFileNameWithoutExtension(path), out _, out string viewName) ? viewName : "",
            Width = bitmap.Width,
            Height = bitmap.Height,
            RgbaBytes = rgba
        };
    }

    private static List<DetectedModelSummary> BuildDetectedSummary(Dictionary<string, DetectionBuckets> detectedByKey)
    {
        return detectedByKey
            .Where(pair => pair.Value != null && pair.Value.AllRects.Count > 0 && TryParseProjectionKey(pair.Key, out _, out _))
            .Select(pair =>
            {
                TryParseProjectionKey(pair.Key, out string modelName, out string viewName);
                return new { ModelName = modelName, ViewName = viewName, Count = pair.Value.AllRects.Count };
            })
            .GroupBy(item => item.ModelName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DetectedModelSummary
            {
                Model = group.Key + ".step",
                DetectedRegionCount = group.Sum(item => item.Count),
                DetectedViews = string.Join("; ", group
                    .GroupBy(item => item.ViewName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(viewGroup => viewGroup.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(viewGroup => viewGroup.Key + ":" + viewGroup.Sum(item => item.Count).ToString(CultureInfo.InvariantCulture)))
            })
            .ToList();
    }

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 16; i++)
        {
            if (File.Exists(Path.Combine(current, "EasyEDA-Loader", "EasyEDA-Loader.csproj")) &&
                Directory.Exists(Path.Combine(current, "Test", "StepCleaner", "Data")))
                return current;

            string parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;

            current = parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static bool TryParseMarkedKey(string path, out string modelName, out string viewName)
    {
        return TryParseProjectionKey(Path.GetFileNameWithoutExtension(path), out modelName, out viewName);
    }

    private static bool TryParseProjectionKey(string key, out string modelName, out string viewName)
    {
        string baseName = key;
        foreach (string view in ViewNames)
        {
            string suffix = "__" + view;
            if (!baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            modelName = baseName.Substring(0, baseName.Length - suffix.Length);
            viewName = view;
            return true;
        }

        modelName = "";
        viewName = "";
        return false;
    }

    private static Metrics ComputeMetrics(IReadOnlyList<RectI> markedRects, IReadOnlyList<RectI> detectedRects, int width, int height)
    {
        var markedMask = new bool[width * height];
        var detectedMask = new bool[width * height];
        FillMask(markedMask, width, height, markedRects);
        FillMask(detectedMask, width, height, detectedRects);

        int markedArea = 0;
        int detectedArea = 0;
        int intersectionArea = 0;
        for (int i = 0; i < markedMask.Length; i++)
        {
            if (markedMask[i])
                markedArea++;
            if (detectedMask[i])
                detectedArea++;
            if (markedMask[i] && detectedMask[i])
                intersectionArea++;
        }

        double bestIoU = 0;
        foreach (RectI marked in markedRects)
        {
            foreach (RectI detected in detectedRects)
            {
                int intersection = IntersectArea(marked, detected);
                int union = marked.Area + detected.Area - intersection;
                if (union > 0)
                    bestIoU = Math.Max(bestIoU, (double)intersection / union);
            }
        }

        return new Metrics
        {
            MarkedArea = markedArea,
            DetectedArea = detectedArea,
            IntersectionArea = intersectionArea,
            MarkCoverage = markedArea > 0 ? (double)intersectionArea / markedArea : 0,
            DetectionInsideMark = detectedArea > 0 ? (double)intersectionArea / detectedArea : 0,
            BestIoU = bestIoU
        };
    }

    private static void FillMask(bool[] mask, int width, int height, IReadOnlyList<RectI> rectangles)
    {
        foreach (RectI rectangle in rectangles)
        {
            int left = Math.Max(0, rectangle.X);
            int top = Math.Max(0, rectangle.Y);
            int right = Math.Min(width, rectangle.X + rectangle.Width);
            int bottom = Math.Min(height, rectangle.Y + rectangle.Height);
            for (int y = top; y < bottom; y++)
            {
                int row = y * width;
                for (int x = left; x < right; x++)
                    mask[row + x] = true;
            }
        }
    }

    private static int IntersectArea(RectI a, RectI b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || bottom <= top)
            return 0;
        return (right - left) * (bottom - top);
    }

    private static string GetStatus(int detectedRectCount, int markedRectCount, Metrics metrics)
    {
        if (markedRectCount == 0)
            return detectedRectCount == 0 ? "matched" : "unmarked_detection";
        if (detectedRectCount == 0)
            return "missed";
        if (detectedRectCount != markedRectCount)
            return "mismatch";
        if (metrics.DetectionInsideMark >= 0.995 &&
            metrics.DetectedArea > 0 &&
            metrics.DetectedArea < metrics.MarkedArea)
        {
            return "matched";
        }

        if (metrics.MarkCoverage >= 0.80 && metrics.DetectionInsideMark >= 0.50)
            return "matched";
        if (metrics.MarkCoverage >= 0.30)
            return "partial";
        return "mismatch";
    }

    private static string GetInsideStatus(int detectedRectCount, Metrics metrics)
    {
        if (metrics.MarkedArea == 0 && detectedRectCount == 0)
            return "not_expected";
        if (metrics.MarkedArea == 0 && detectedRectCount > 0)
            return "unmarked_detection";
        if (detectedRectCount == 0)
            return "missed";
        if (metrics.DetectionInsideMark >= 0.95)
            return "matched";
        if (metrics.DetectionInsideMark >= 0.50)
            return "partial";
        return "mismatch";
    }

    private static string FormatRectangles(IReadOnlyList<RectI> rectangles)
    {
        if (rectangles == null || rectangles.Count == 0)
            return "";

        return string.Join(";", rectangles.Select(rectangle =>
            rectangle.X.ToString(CultureInfo.InvariantCulture) +
            ":" +
            rectangle.Y.ToString(CultureInfo.InvariantCulture) +
            ":" +
            rectangle.Width.ToString(CultureInfo.InvariantCulture) +
            ":" +
            rectangle.Height.ToString(CultureInfo.InvariantCulture)));
    }

    private static string FormatLabels(IReadOnlyList<string> labels)
    {
        if (labels == null || labels.Count == 0)
            return "";

        return string.Join(
            ";",
            labels
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase));
    }

    private static int CountBoxes(string boxes)
    {
        if (string.IsNullOrWhiteSpace(boxes))
            return 0;

        return boxes.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static void DrawOverlay(
        string projectionPath,
        string outputPath,
        IReadOnlyList<RectI> markedRects,
        DetectionBuckets detected,
        DetectionBuckets cleanTextDetected,
        bool markedCloudLogo)
    {
        using SKBitmap bitmap = File.Exists(projectionPath)
            ? SKBitmap.Decode(projectionPath)
            : new SKBitmap(1600, 1600);
        using SKCanvas canvas = new SKCanvas(bitmap);
        using var markedStroke = new SKPaint
        {
            Color = new SKColor(0, 96, 255, 230),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5,
            IsAntialias = true
        };
        using var markedFill = new SKPaint
        {
            Color = new SKColor(0, 96, 255, 35),
            Style = SKPaintStyle.Fill
        };
        using var logoStroke = new SKPaint
        {
            Color = new SKColor(240, 32, 32, 230),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 7,
            IsAntialias = true
        };
        using var logoFill = new SKPaint
        {
            Color = new SKColor(240, 32, 32, 22),
            Style = SKPaintStyle.Fill
        };
        using var textStroke = new SKPaint
        {
            Color = new SKColor(160, 32, 220, 230),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4,
            IsAntialias = true
        };
        using var textFill = new SKPaint
        {
            Color = new SKColor(160, 32, 220, 35),
            Style = SKPaintStyle.Fill
        };
        using var combinedStroke = new SKPaint
        {
            Color = new SKColor(255, 150, 0, 240),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5,
            IsAntialias = true
        };
        using var combinedFill = new SKPaint
        {
            Color = new SKColor(255, 150, 0, 30),
            Style = SKPaintStyle.Fill
        };
        using var cleanTextStroke = new SKPaint
        {
            Color = new SKColor(0, 170, 80, 230),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4,
            IsAntialias = true
        };
        using var cleanTextFill = new SKPaint
        {
            Color = new SKColor(0, 170, 80, 35),
            Style = SKPaintStyle.Fill
        };
        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 32,
            IsAntialias = true
        };

        foreach (RectI rectangle in markedRects)
        {
            var rect = SKRect.Create(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            canvas.DrawRect(rect, markedFill);
            canvas.DrawRect(rect, markedStroke);
        }

        ReportDisplayParts displayParts = ResolveReportDisplayParts(detected, markedCloudLogo);
        ReportDisplayParts cleanTextDisplayParts = ResolveReportDisplayParts(cleanTextDetected, markedCloudLogo);
        displayParts = MergeCleanTextDisplayHints(displayParts, cleanTextDisplayParts, detected.CombinedRects.ToList(), markedCloudLogo);

        DrawRectangles(canvas, detected.CombinedRects.Concat(cleanTextDetected.CombinedRects), combinedFill, combinedStroke);
        DrawRectangles(canvas, displayParts.TextRects, textFill, textStroke);
        DrawRectangles(canvas, cleanTextDisplayParts.TextRects, cleanTextFill, cleanTextStroke);
        DrawRectangles(canvas, displayParts.LogoRects.Concat(detected.OtherRects), logoFill, logoStroke);

        canvas.DrawText("blue=marked  red=logo  purple=text  green=clean-text  orange=combined", 24, 48, textPaint);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using Stream stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    private static IReadOnlyList<RectI> FilterOverlayTextRects(IReadOnlyList<RectI> textRects, IReadOnlyList<RectI> logoRects)
    {
        if (textRects == null || textRects.Count == 0)
            return Array.Empty<RectI>();
        if (logoRects == null || logoRects.Count == 0 || textRects.Count == 1)
            return textRects;

        var result = new List<RectI>();
        foreach (RectI textRect in textRects)
        {
            bool hasSmallerText = textRects.Any(other =>
                !SameRect(other, textRect) &&
                other.Area < textRect.Area * 0.85);
            bool duplicatesLogo = hasSmallerText && logoRects.Any(logoRect =>
            {
                int intersection = IntersectionArea(textRect, logoRect);
                int minArea = Math.Max(1, Math.Min(textRect.Area, logoRect.Area));
                double textToLogoArea = textRect.Area / (double)Math.Max(1, logoRect.Area);
                return intersection / (double)minArea >= 0.80 &&
                    textToLogoArea >= 0.50;
            });

            if (!duplicatesLogo)
                result.Add(textRect);
        }

        return result;
    }

    private static IReadOnlyList<RectI> FilterOverlayLogoRects(IReadOnlyList<RectI> logoRects, IReadOnlyList<RectI> combinedRects)
    {
        if (logoRects == null || logoRects.Count == 0)
            return Array.Empty<RectI>();
        if (combinedRects == null || combinedRects.Count == 0)
            return logoRects;

        var result = new List<RectI>();
        foreach (RectI logoRect in logoRects)
        {
            bool lowerHalfFalseLogo = combinedRects.Any(combinedRect =>
            {
                if (!ContainsRect(combinedRect, logoRect, padding: 4))
                    return false;
                if (combinedRect.Height < logoRect.Height * 1.70)
                    return false;

                double logoCenterY = logoRect.Y + logoRect.Height / 2.0;
                return logoCenterY > combinedRect.Y + combinedRect.Height * 0.55;
            });

            if (!lowerHalfFalseLogo)
                result.Add(logoRect);
        }

        return result;
    }

    private static bool ContainsRect(RectI outer, RectI inner, int padding)
    {
        return inner.X >= outer.X - padding &&
            inner.Y >= outer.Y - padding &&
            inner.X + inner.Width <= outer.X + outer.Width + padding &&
            inner.Y + inner.Height <= outer.Y + outer.Height + padding;
    }

    private static bool SameRect(RectI left, RectI right)
    {
        return left.X == right.X &&
            left.Y == right.Y &&
            left.Width == right.Width &&
            left.Height == right.Height;
    }

    private static void DrawRectangles(
        SKCanvas canvas,
        IEnumerable<RectI> rectangles,
        SKPaint fill,
        SKPaint stroke)
    {
        foreach (RectI rectangle in rectangles ?? Enumerable.Empty<RectI>())
        {
            var rect = SKRect.Create(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, stroke);
        }
    }

    private static void WriteCsv(string path, IReadOnlyList<CompareRow> rows)
    {
        var lines = new List<string>
        {
            "Model,View,Status,LogoStatus,CleanTextStatus,CleanTextLogoStatus,CombinedStatus,CleanTextCombinedStatus,MarkedRects,DetectedRects,CleanTextRects,CombinedRects,CleanTextCombinedRects,MarkedArea,DetectedArea,CleanTextArea,CombinedArea,CleanTextCombinedArea,IntersectionArea,MarkCoverage,DetectionInsideMark,BestIoU,LogoMarkCoverage,LogoInsideMark,LogoBestIoU,CombinedMarkCoverage,CombinedInsideMark,CombinedBestIoU,CleanTextMarkCoverage,CleanTextInsideMark,CleanTextBestIoU,CleanTextLogoMarkCoverage,CleanTextLogoInsideMark,CleanTextLogoBestIoU,CleanTextCombinedMarkCoverage,CleanTextCombinedInsideMark,CleanTextCombinedBestIoU,MarkedBoxes,DetectedBoxes,LogoBoxes,TextBoxes,TextLabels,CombinedBoxes,CleanTextBoxes,CleanTextLogoBoxes,CleanTextTextBoxes,CleanTextTextLabels,CleanTextCombinedBoxes,MarkedFile,OverlayImage,ProjectionImage,DetectionDebugImage"
        };
        foreach (CompareRow row in rows)
        {
            lines.Add(string.Join(",", new[]
            {
                Csv(row.Model),
                Csv(row.View),
                Csv(row.Status),
                Csv(row.LogoStatus),
                Csv(row.CleanTextStatus),
                Csv(row.CleanTextLogoStatus),
                Csv(row.CombinedStatus),
                Csv(row.CleanTextCombinedStatus),
                row.MarkedRects.ToString(CultureInfo.InvariantCulture),
                row.DetectedRects.ToString(CultureInfo.InvariantCulture),
                row.CleanTextRects.ToString(CultureInfo.InvariantCulture),
                row.CombinedRects.ToString(CultureInfo.InvariantCulture),
                row.CleanTextCombinedRects.ToString(CultureInfo.InvariantCulture),
                row.MarkedArea.ToString(CultureInfo.InvariantCulture),
                row.DetectedArea.ToString(CultureInfo.InvariantCulture),
                row.CleanTextArea.ToString(CultureInfo.InvariantCulture),
                row.CombinedArea.ToString(CultureInfo.InvariantCulture),
                row.CleanTextCombinedArea.ToString(CultureInfo.InvariantCulture),
                row.IntersectionArea.ToString(CultureInfo.InvariantCulture),
                row.MarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture),
                row.DetectionInsideMark.ToString("0.0000", CultureInfo.InvariantCulture),
                row.BestIoU.ToString("0.0000", CultureInfo.InvariantCulture),
                row.LogoMarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture),
                row.LogoInsideMark.ToString("0.0000", CultureInfo.InvariantCulture),
                row.LogoBestIoU.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CombinedMarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CombinedInsideMark.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CombinedBestIoU.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextMarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextInsideMark.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextBestIoU.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextLogoMarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextLogoInsideMark.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextLogoBestIoU.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextCombinedMarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextCombinedInsideMark.ToString("0.0000", CultureInfo.InvariantCulture),
                row.CleanTextCombinedBestIoU.ToString("0.0000", CultureInfo.InvariantCulture),
                Csv(row.MarkedBoxes),
                Csv(row.DetectedBoxes),
                Csv(row.LogoBoxes),
                Csv(row.TextBoxes),
                Csv(row.TextLabels),
                Csv(row.CombinedBoxes),
                Csv(row.CleanTextBoxes),
                Csv(row.CleanTextLogoBoxes),
                Csv(row.CleanTextTextBoxes),
                Csv(row.CleanTextTextLabels),
                Csv(row.CleanTextCombinedBoxes),
                Csv(row.MarkedFile),
                Csv(row.OverlayImage),
                Csv(row.ProjectionImage),
                Csv(row.DetectionDebugImage)
            }));
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteDetectionSummaryCsv(string path, IReadOnlyList<DetectedModelSummary> rows)
    {
        var lines = new List<string> { "Model,DetectedRegionCount,DetectedViews" };
        foreach (DetectedModelSummary row in rows)
            lines.Add(Csv(row.Model) + "," + row.DetectedRegionCount.ToString(CultureInfo.InvariantCulture) + "," + Csv(row.DetectedViews));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteReport(
        string path,
        IReadOnlyList<CompareRow> rows,
        IReadOnlyList<DetectedModelSummary> detectedSummary)
    {
        var lines = new List<string>
        {
            "# Marked vs Detected Watermark Regions",
            "",
            "Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            "",
            "Blue rectangles are human marked regions. Red rectangles are logo detector regions. Purple rectangles are text detector regions. Green rectangles are CleanText detector regions. Orange rectangles are final facade regions.",
            "",
            "## Summary",
            ""
        };

        foreach (var group in rows.GroupBy(row => row.Status).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add("- " + group.Key + ": " + group.Count().ToString(CultureInfo.InvariantCulture));

        lines.Add("- marked views: " + rows.Count(row => !string.IsNullOrEmpty(row.MarkedFile)).ToString(CultureInfo.InvariantCulture));
        lines.Add("- detected views without marked data: " + rows.Count(row => string.IsNullOrEmpty(row.MarkedFile) && row.DetectedRects > 0).ToString(CultureInfo.InvariantCulture));
        lines.Add("- detected logos: " + rows.Count(HasExpectedLogoDetection).ToString(CultureInfo.InvariantCulture));
        lines.Add("- logo matched: " + rows.Count(row => string.Equals(row.LogoStatus, "matched", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
        lines.Add("- clean-text matched: " + rows.Count(row => string.Equals(row.CleanTextStatus, "matched", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
        lines.Add("- clean-text logo matched: " + rows.Count(row => string.Equals(row.CleanTextLogoStatus, "matched", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
        lines.Add("- combined matched: " + rows.Count(row => string.Equals(row.CombinedStatus, "matched", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
        lines.Add("- clean-text combined matched: " + rows.Count(row => string.Equals(row.CleanTextCombinedStatus, "matched", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture));
        lines.Add("- clean-text missed: " + rows.Count(row => !string.IsNullOrEmpty(row.MarkedFile) && row.MarkedRects > 0 && row.CleanTextRects == 0).ToString(CultureInfo.InvariantCulture));
        lines.Add("- clean-text extra detections: " + rows.Count(row => string.IsNullOrEmpty(row.MarkedFile) && row.CleanTextRects > 0).ToString(CultureInfo.InvariantCulture));
        lines.Add("- models with any detected regions: " + detectedSummary.Count(row => row.DetectedRegionCount > 0).ToString(CultureInfo.InvariantCulture));
        lines.Add("");
        string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";

        lines.Add("## Logo Detection");
        lines.Add("");
        foreach (var group in rows.GroupBy(row => row.LogoStatus).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add("- " + group.Key + ": " + group.Count().ToString(CultureInfo.InvariantCulture));
        lines.Add("");
        lines.Add("| Model | View | Logo | Logo boxes | Text labels | Logo inside mark | Logo mark coverage | Overlay |");
        lines.Add("| --- | --- | --- | --- | --- | ---: | ---: | --- |");
        foreach (CompareRow row in rows
            .Where(row => !string.Equals(row.LogoStatus, "missed", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(row.LogoBoxes))
            .OrderBy(row => row.LogoStatus, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.View, StringComparer.OrdinalIgnoreCase))
        {
            string overlayPath = ToMarkdownPath(reportDirectory, row.OverlayImage);
            lines.Add("| " + EscapePipe(row.Model) +
                " | " + row.View +
                " | " + row.LogoStatus +
                " | `" + row.LogoBoxes + "`" +
                " | `" + row.TextLabels + "`" +
                " | " + row.LogoInsideMark.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | " + row.LogoMarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | `" + overlayPath + "` |");
        }
        lines.Add("");
        lines.Add("## Missed, Weak, Or Unmarked Detections");
        lines.Add("");
        foreach (CompareRow row in rows
            .Where(row =>
                !string.Equals(row.Status, "matched", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(row.CombinedStatus, "matched", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(row.CleanTextCombinedStatus, "matched", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Status, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.View, StringComparer.OrdinalIgnoreCase))
        {
            string overlayPath = ToMarkdownPath(reportDirectory, row.OverlayImage);
            lines.Add("### " + row.Model + " " + row.View);
            lines.Add("");
            lines.Add("- status: `" + row.Status + "`");
            lines.Add("- logo status: `" + row.LogoStatus + "`");
            lines.Add("- clean-text status: `" + row.CleanTextStatus + "`");
            lines.Add("- clean-text logo status: `" + row.CleanTextLogoStatus + "`");
            lines.Add("- combined status: `" + row.CombinedStatus + "`");
            lines.Add("- clean-text combined status: `" + row.CleanTextCombinedStatus + "`");
            lines.Add("- marked coverage: " + row.MarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- detection inside mark: " + row.DetectionInsideMark.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- best IoU: " + row.BestIoU.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- logo inside mark: " + row.LogoInsideMark.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- logo boxes: `" + row.LogoBoxes + "`");
            lines.Add("- combined marked coverage: " + row.CombinedMarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- combined inside mark: " + row.CombinedInsideMark.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- combined best IoU: " + row.CombinedBestIoU.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- clean-text marked coverage: " + row.CleanTextMarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- clean-text inside mark: " + row.CleanTextInsideMark.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- clean-text best IoU: " + row.CleanTextBestIoU.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- marked file: `" + (string.IsNullOrEmpty(row.MarkedFile) ? "none" : row.MarkedFile) + "`");
            lines.Add("- overlay image: `" + overlayPath + "`");
            lines.Add("");
            lines.Add("![" + row.Model + " " + row.View + "](" + overlayPath + ")");
            lines.Add("");
        }

        lines.Add("## All Marked Images");
        lines.Add("");
        foreach (CompareRow row in rows
            .Where(row => !string.IsNullOrEmpty(row.MarkedFile))
            .OrderBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.View, StringComparer.OrdinalIgnoreCase))
        {
            string overlayPath = ToMarkdownPath(reportDirectory, row.OverlayImage);
            lines.Add("### " + row.Model + " " + row.View);
            lines.Add("");
            lines.Add("- status: `" + row.Status + "`");
            lines.Add("- marked boxes: `" + row.MarkedBoxes + "`");
            lines.Add("- detected boxes: `" + row.DetectedBoxes + "`");
            lines.Add("- logo boxes: `" + row.LogoBoxes + "`");
            lines.Add("- text labels: `" + row.TextLabels + "`");
            lines.Add("- clean-text labels: `" + row.CleanTextTextLabels + "`");
            lines.Add("");
            lines.Add("![" + row.Model + " " + row.View + "](" + overlayPath + ")");
            lines.Add("");
        }

        lines.Add("## All Detected Images");
        lines.Add("");
        foreach (CompareRow row in rows
            .Where(row => row.DetectedRects > 0 ||
                row.CleanTextRects > 0 ||
                !string.IsNullOrEmpty(row.LogoBoxes) ||
                !string.IsNullOrEmpty(row.TextBoxes))
            .OrderBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.View, StringComparer.OrdinalIgnoreCase))
        {
            string overlayPath = ToMarkdownPath(reportDirectory, row.OverlayImage);
            lines.Add("### " + row.Model + " " + row.View);
            lines.Add("");
            lines.Add("- marked file: `" + (string.IsNullOrEmpty(row.MarkedFile) ? "none" : row.MarkedFile) + "`");
            lines.Add("- detected boxes: `" + row.DetectedBoxes + "`");
            lines.Add("- logo boxes: `" + row.LogoBoxes + "`");
            lines.Add("- text boxes: `" + row.TextBoxes + "`");
            lines.Add("- text labels: `" + row.TextLabels + "`");
            lines.Add("- clean-text labels: `" + row.CleanTextTextLabels + "`");
            lines.Add("");
            lines.Add("![" + row.Model + " " + row.View + "](" + overlayPath + ")");
            lines.Add("");
        }

        lines.Add("## All Compared Views");
        lines.Add("");
        lines.Add("| Model | View | Status | Logo | CleanText | CleanText logo | Combined | CleanText combined | Marked rects | Detected rects | Combined rects | Text labels | CleanText labels | Mark coverage | Detection inside mark | Logo inside mark | Combined inside mark | Best IoU | Combined IoU | Overlay |");
        lines.Add("| --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (CompareRow row in rows.OrderBy(row => row.Model, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.View, StringComparer.OrdinalIgnoreCase))
        {
            string overlayPath = ToMarkdownPath(reportDirectory, row.OverlayImage);
            lines.Add("| " + EscapePipe(row.Model) +
                " | " + row.View +
                " | " + row.Status +
                " | " + row.LogoStatus +
                " | " + row.CleanTextStatus +
                " | " + row.CleanTextLogoStatus +
                " | " + row.CombinedStatus +
                " | " + row.CleanTextCombinedStatus +
                " | " + row.MarkedRects.ToString(CultureInfo.InvariantCulture) +
                " | " + row.DetectedRects.ToString(CultureInfo.InvariantCulture) +
                " | " + row.CombinedRects.ToString(CultureInfo.InvariantCulture) +
                " | `" + row.TextLabels + "`" +
                " | `" + row.CleanTextTextLabels + "`" +
                " | " + row.MarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | " + row.DetectionInsideMark.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | " + row.LogoInsideMark.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | " + row.CombinedInsideMark.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | " + row.BestIoU.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | " + row.CombinedBestIoU.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | `" + overlayPath + "` |");
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static string Csv(string value)
    {
        value = value ?? "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string ToMarkdownPath(string reportDirectory, string value)
    {
        return Path.GetRelativePath(reportDirectory, Path.GetFullPath(value)).Replace('\\', '/');
    }

    private static string EscapePipe(string value)
    {
        return (value ?? "").Replace("|", "\\|");
    }

    private sealed class MarkedFile
    {
        public List<MarkedRectangle> Rectangles { get; set; }
    }

    private sealed class DetectionRegionDocument
    {
        public string Model { get; set; }
        public int ImageSizePixels { get; set; }
        public int PaddingPixels { get; set; }
        public List<DetectionRegionRecord> Regions { get; set; }
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

    private sealed class DetectionBuckets
    {
        public List<RectI> FinalRects { get; } = new List<RectI>();
        public List<RectI> LogoRects { get; } = new List<RectI>();
        public List<RectI> TextRects { get; } = new List<RectI>();
        public List<string> TextLabels { get; } = new List<string>();
        public List<RectI> CombinedRects { get; } = new List<RectI>();
        public List<RectI> OtherRects { get; } = new List<RectI>();

        public IEnumerable<RectI> PartRects => LogoRects.Concat(TextRects).Concat(OtherRects);
        public List<RectI> AllRects => FinalRects.Count > 0
            ? FinalRects.ToList()
            : PartRects.Concat(CombinedRects).ToList();
    }

    private sealed class ReportDisplayParts
    {
        public ReportDisplayParts(IReadOnlyList<RectI> logoRects, IReadOnlyList<RectI> textRects)
        {
            LogoRects = logoRects ?? Array.Empty<RectI>();
            TextRects = textRects ?? Array.Empty<RectI>();
        }

        public IReadOnlyList<RectI> LogoRects { get; }
        public IReadOnlyList<RectI> TextRects { get; }
    }

    private sealed class MarkedRectangle
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private readonly struct RectI
    {
        public RectI(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public int Area => Math.Max(0, Width) * Math.Max(0, Height);
    }

    private sealed class Metrics
    {
        public int MarkedArea { get; set; }
        public int DetectedArea { get; set; }
        public int IntersectionArea { get; set; }
        public double MarkCoverage { get; set; }
        public double DetectionInsideMark { get; set; }
        public double BestIoU { get; set; }
    }

    private sealed class CompareRow
    {
        public string Model { get; set; }
        public string View { get; set; }
        public int MarkedRects { get; set; }
        public int DetectedRects { get; set; }
        public int CleanTextRects { get; set; }
        public int CombinedRects { get; set; }
        public int CleanTextCombinedRects { get; set; }
        public int MarkedArea { get; set; }
        public int DetectedArea { get; set; }
        public int CleanTextArea { get; set; }
        public int CombinedArea { get; set; }
        public int CleanTextCombinedArea { get; set; }
        public int IntersectionArea { get; set; }
        public double MarkCoverage { get; set; }
        public double DetectionInsideMark { get; set; }
        public double BestIoU { get; set; }
        public double LogoMarkCoverage { get; set; }
        public double LogoInsideMark { get; set; }
        public double LogoBestIoU { get; set; }
        public double CombinedMarkCoverage { get; set; }
        public double CombinedInsideMark { get; set; }
        public double CombinedBestIoU { get; set; }
        public double CleanTextMarkCoverage { get; set; }
        public double CleanTextInsideMark { get; set; }
        public double CleanTextBestIoU { get; set; }
        public double CleanTextLogoMarkCoverage { get; set; }
        public double CleanTextLogoInsideMark { get; set; }
        public double CleanTextLogoBestIoU { get; set; }
        public double CleanTextCombinedMarkCoverage { get; set; }
        public double CleanTextCombinedInsideMark { get; set; }
        public double CleanTextCombinedBestIoU { get; set; }
        public string Status { get; set; }
        public string LogoStatus { get; set; }
        public string CleanTextStatus { get; set; }
        public string CleanTextLogoStatus { get; set; }
        public string CombinedStatus { get; set; }
        public string CleanTextCombinedStatus { get; set; }
        public string MarkedBoxes { get; set; }
        public string DetectedBoxes { get; set; }
        public string LogoBoxes { get; set; }
        public string TextBoxes { get; set; }
        public string TextLabels { get; set; }
        public string CombinedBoxes { get; set; }
        public string CleanTextBoxes { get; set; }
        public string CleanTextLogoBoxes { get; set; }
        public string CleanTextTextBoxes { get; set; }
        public string CleanTextTextLabels { get; set; }
        public string CleanTextCombinedBoxes { get; set; }
        public string MarkedFile { get; set; }
        public string OverlayImage { get; set; }
        public string ProjectionImage { get; set; }
        public string DetectionDebugImage { get; set; }
    }

    private sealed class DetectedModelSummary
    {
        public string Model { get; set; }
        public int DetectedRegionCount { get; set; }
        public string DetectedViews { get; set; }
    }

    private sealed class ModelDetectorResults
    {
        public Dictionary<string, DetectionBuckets> NormalByKey { get; } =
            new Dictionary<string, DetectionBuckets>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DetectionBuckets> CleanTextByKey { get; } =
            new Dictionary<string, DetectionBuckets>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ViewDetectorResult
    {
        public string ViewName { get; set; }
        public DetectionBuckets NormalBuckets { get; set; }
        public DetectionBuckets CleanTextBuckets { get; set; }
    }
}
