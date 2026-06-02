using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace StepCleaner.Tests
{
    internal static class Program
    {
        private const int ProjectionDifferenceTolerance = 6;
        private const int AllowedDetectionRegionPaddingPixels = 10;
        private const double MaxOutsideDetectionRegionChangeRatio = 0.005;
        private const int VerificationProjectionImageSizePixels = 1000;
        private const int VerificationProjectionPaddingPixels = 50;
        private const int FlatnessEdgeThreshold = 28;
        private const double MaxCleanedRegionEdgeRatio = 0.035;
        private const double MaxRetainedRegionEdgeRatio = 0.45;

        private static int Main(string[] args)
        {
            if (args.Length > 0)
                return RunCommand(args);

            try
            {
                string dataRoot = FindDataRoot();
                string originalDirectory = Path.Combine(dataRoot, "Original");
                string cleanDirectory = Path.Combine(dataRoot, "Clean");
                string validatedDirectory = Path.Combine(dataRoot, "Validated");
                string markedDirectory = Path.Combine(dataRoot, "Marked");
                string projectionDirectory = Path.Combine(dataRoot, "Projection");
                string originalCleanCompareProjectionDirectory = Path.Combine(dataRoot, "OriginalCleanCompareProjection");
                string cleanProjectionDirectory = Path.Combine(dataRoot, "CleanProjection");
                string validatedProjectionDirectory = Path.Combine(dataRoot, "ValidatedProjection");
                string failedProjectionReportPath = Path.Combine(dataRoot, "FailedProjectionReport.md");
                string failedProjectionReportDirectory = Path.Combine(dataRoot, "FailedProjectionReport");
                string detectionDirectory = Path.Combine(cleanDirectory, "Detection");

                Directory.CreateDirectory(cleanDirectory);

                var originalFiles = GetStepFiles(originalDirectory);
                var validatedFiles = GetStepFiles(validatedDirectory);
                var validatedByName = validatedFiles.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
                var originalBaseNames = new HashSet<string>(
                    originalFiles.Select(file => Path.GetFileNameWithoutExtension(file)),
                    StringComparer.OrdinalIgnoreCase);
                var generatedCleanByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var detectionViewNamesByFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var postCleanFaultFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var failures = new List<string>();
                var visualFailures = new List<ProjectionVisualFailure>();
                var projectionTimings = new ProjectionVerificationTimings();

                if (originalFiles.Count == 0)
                    failures.Add("No STEP files were found in Original.");

                if (validatedFiles.Count == 0)
                    failures.Add("No STEP files were found in Validated.");

                VerifyCleanupIgnoresMarkedOptions(originalFiles, failures);

                VerifyDetectionDebugImages(
                    originalFiles,
                    originalBaseNames,
                    projectionDirectory,
                    markedDirectory,
                    detectionDirectory,
                    failures);

                foreach (string originalFile in originalFiles)
                {
                    string fileName = Path.GetFileName(originalFile);
                    string outputFile = Path.Combine(cleanDirectory, fileName);
                    byte[] cleanedStep = StepWatermarkCleaner.Clean(File.ReadAllBytes(originalFile), new StepWatermarkCleanerOptions());
                    File.WriteAllBytes(outputFile, cleanedStep);
                    generatedCleanByName[fileName] = outputFile;

                    Console.WriteLine("Cleaned " + fileName);
                    if (!validatedByName.TryGetValue(fileName, out string validatedFile))
                    {
                        failures.Add(
                            "Clean output is missing from Validated, so it is treated as not fully cleaned. " +
                            "Please view the generated clean model before accepting it: " +
                            outputFile);
                        continue;
                    }
                }

                foreach (string note in GetCleanupNotes())
                    Console.WriteLine("Cleanup note: " + note);

                foreach (string validatedFile in validatedFiles)
                {
                    string fileName = Path.GetFileName(validatedFile);
                    if (!generatedCleanByName.ContainsKey(fileName))
                        failures.Add("Validated file has no matching Original model or generated Clean output: " + fileName);
                }

                var verificationProjectionOptions = CreateVerificationProjectionOptions();
                ClearProjectionFiles(cleanProjectionDirectory, generatedCleanByName.Keys);
                projectionTimings.Measure(
                    "clean_projection_render_ms",
                    () => StepProjectionRenderer.ProjectDirectory(cleanDirectory, cleanProjectionDirectory, verificationProjectionOptions));

                VerifyPostCleanProjections(
                    originalFiles,
                    generatedCleanByName,
                    originalCleanCompareProjectionDirectory,
                    cleanProjectionDirectory,
                    verificationProjectionOptions,
                    detectionViewNamesByFileName,
                    projectionTimings,
                    postCleanFaultFileNames,
                    failures,
                    visualFailures);

                CompareCleanAndValidatedProjections(
                    generatedCleanByName,
                    validatedByName,
                    validatedDirectory,
                    cleanProjectionDirectory,
                    validatedProjectionDirectory,
                    verificationProjectionOptions,
                    detectionViewNamesByFileName,
                    projectionTimings,
                    postCleanFaultFileNames,
                    failures,
                    visualFailures);

                projectionTimings.Measure(
                    "failed_projection_report_ms",
                    () => WriteFailedProjectionReport(
                        failedProjectionReportPath,
                        failedProjectionReportDirectory,
                        visualFailures));
                if (visualFailures.Count > 0)
                    Console.WriteLine("Failed projection report: " + failedProjectionReportPath);
                projectionTimings.WriteToConsole();

                if (failures.Count > 0)
                {
                    Console.Error.WriteLine("STEP cleaner regression test failed.");
                    foreach (string failure in failures)
                        Console.Error.WriteLine("  " + failure);

                    return 1;
                }

                Console.WriteLine(
                    "STEP cleaner regression test passed. Cleaned " +
                    originalFiles.Count.ToString(CultureInfo.InvariantCulture) +
                    " original file(s), compared " +
                    validatedFiles.Count.ToString(CultureInfo.InvariantCulture) +
                    " validated file(s).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("STEP cleaner regression test failed: " + ex.Message);
                return 1;
            }
        }

        private static int RunCommand(string[] args)
        {
            if (IsOption(args[0], "--metadata"))
                return RunMetadataTests();

            if (IsOption(args[0], "--symbol-rules"))
                return RunSymbolRuleTests();

            if (IsOption(args[0], "--async-import"))
                return RunAsyncImportTests();

            if (IsOption(args[0], "--silhouette"))
                return SaveSilhouetteProjectionImage(args);

            Console.Error.WriteLine("Unknown command: " + args[0]);
            Console.Error.WriteLine("Usage: StepCleaner.Tests --metadata");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --symbol-rules");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --async-import");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette <input.step> <output.png> [--rotx deg] [--roty deg] [--rotz deg] [--rotation2d deg] [--size pixels] [--padding pixels] [--no-grid] [--no-axes]");
            return 2;
        }

        private static int RunAsyncImportTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();

            AssertAwaitsUseConfigureAwaitFalse(
                Path.Combine(repoRoot, "EasyEDA-Loader", "ModelCache.cs"),
                "ModelCache",
                failures);
            AssertAwaitsUseConfigureAwaitFalse(
                Path.Combine(repoRoot, "EasyEDA-Loader", "API", "EasyedaApi.cs"),
                "EasyedaApi",
                failures);
            AssertNoBlockingTaskWaits(
                Path.Combine(repoRoot, "EasyEDA-Loader", "FootprintShapes", "EeFootprint3dModel.cs"),
                "EeFootprint3dModel",
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Async footprint import regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Async footprint import regression test passed.");
            return 0;
        }

        private static void AssertAwaitsUseConfigureAwaitFalse(string filePath, string label, List<string> failures)
        {
            string[] lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.Contains("await "))
                    continue;

                if (!line.Contains("ConfigureAwait(false)"))
                    failures.Add(label + " line " + (i + 1).ToString(CultureInfo.InvariantCulture) + " awaits without ConfigureAwait(false): " + line);
            }
        }

        private static void AssertNoBlockingTaskWaits(string filePath, string label, List<string> failures)
        {
            string[] lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Contains(".Wait()") || line.Contains(".Result"))
                    failures.Add(label + " line " + (i + 1).ToString(CultureInfo.InvariantCulture) + " blocks synchronously on a Task: " + line);
            }
        }

        private static string FindRepoRoot()
        {
            string directory = AppContext.BaseDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (Directory.Exists(Path.Combine(directory, ".git")) &&
                    Directory.Exists(Path.Combine(directory, "EasyEDA-Loader")))
                    return directory;

                directory = Directory.GetParent(directory)?.FullName;
            }

            directory = Directory.GetCurrentDirectory();
            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (Directory.Exists(Path.Combine(directory, ".git")) &&
                    Directory.Exists(Path.Combine(directory, "EasyEDA-Loader")))
                    return directory;

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new DirectoryNotFoundException("Could not locate repository root.");
        }

        private static int RunMetadataTests()
        {
            var failures = new List<string>();

            string synthesizedDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "MR30PB-M30.A.G.Y",
                componentDescription: "MR30PB-M30.A.G.Y",
                packageTitle: "MR30PB-M30.A.G.Y",
                packageName: "CONN-TH_MR30PB-M30.A.G.Y",
                partNumber: "MR30PB-M30.A.G.Y",
                mounting: "through-hole");

            AssertEqual(
                "CONN-TH package, through-hole",
                synthesizedDescription,
                "part-number-only candidates should be ignored when selecting the footprint description",
                failures);

            string usefulDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "Amass MR30PB connector, 3 position plug, through-hole",
                componentDescription: "MR30PB-M30.A.G.Y",
                packageTitle: "MR30PB-M30.A.G.Y",
                packageName: "CONN-TH_MR30PB-M30.A.G.Y",
                partNumber: "MR30PB-M30.A.G.Y",
                mounting: "through-hole");

            AssertEqual(
                "Amass MR30PB connector, 3 position plug, through-hole",
                usefulDescription,
                "descriptive product text should be kept",
                failures);

            string richDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "",
                componentDescription: "",
                packageTitle: "CONN-TH_MR30PB-M30.A.G.Y",
                packageName: "CONN-TH_MR30PB-M30.A.G.Y",
                partNumber: "MR30PB-M30.A.G.Y",
                mounting: "through-hole",
                parameters: new Dictionary<string, string>
                {
                    { "Manufacturer", "AMASS(艾迈斯)" },
                    { "Manufacturer Part", "MR30PB-M30.A.G.Y" },
                    { "LCSC Part Name", "AMASS(艾迈斯)三芯动力电池马达e电调航模插头连接器 PCB板立式插头公头 金 黄MR30PB-M30.A.G.Y" }
                },
                geometry: CreateMr30FootprintGeometry());

            AssertEqual(
                "AMASS MR30PB, 3-position vertical through-hole male PCB power plug connector, 3.5 mm pitch, 11.9 x 5.3 mm body",
                richDescription,
                "C30170185-style metadata should synthesize a detailed footprint description",
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Metadata regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Metadata regression test passed.");
            return 0;
        }

        private static int RunSymbolRuleTests()
        {
            var failures = new List<string>();

            AssertNear(98.4251968503937, SymbolImportRules.GostGridMil, 0.0000001, "GOST schematic grid must be exactly 2.5 mm in mils", failures);
            AssertNear(196.850393700787, SymbolImportRules.GostPinPitchMil, 0.0000001, "generated schematic pins must advance by 5.0 mm rows", failures);
            AssertNear(196.850393700787, SymbolImportRules.GostPinLengthMil, 0.0000001, "generated schematic pin length must be 5.0 mm", failures);

            AssertEqual("Pin 1", SymbolImportRules.FormatPinName("1", "1", true), "numeric connector pins should have readable Pin N names", failures);
            AssertEqual("Pin 12", SymbolImportRules.FormatPinName("", "12", true), "numeric connector pin names should be normalized even when the designator is absent", failures);
            AssertEqual("D+", SymbolImportRules.FormatPinName("2", "D+", true), "non-numeric connector pin names should keep electrical meaning", failures);
            AssertEqual("1", SymbolImportRules.FormatPinName("1", "1", false), "numeric IC pin names should not be rewritten as connector contacts", failures);

            AssertEqual("XS?", SymbolImportRules.SelectDesignator("J?", "USB4110", "USB Type-C receptacle", "USB-C-SMD"), "USB receptacles should use the socket designator family", failures);
            AssertEqual("XP?", SymbolImportRules.SelectDesignator("J?", "DF40C-10DP-0.4V", "board-to-board header", "CONN-SMD"), "headers should use the plug/header designator family", failures);
            AssertEqual("DD?", SymbolImportRules.SelectDesignator("U?", "CH334P", "USB hub controller", "SOP-16"), "IC source U? designators should map to GOST DD?", failures);

            AssertEqual("Разъём USB", SymbolImportRules.SelectValueType("XS?", "USB4110", "USB Type-C receptacle", "USB-C-SMD"), "USB connector value type should use the USB connector vocabulary", failures);
            AssertEqual("Разъём", SymbolImportRules.SelectValueType("XP?", "DF40C", "mezzanine header", "CONN-SMD"), "generic connectors should use connector value type", failures);
            AssertEqual("Микросхема", SymbolImportRules.SelectValueType("DD?", "CH334P", "USB hub controller", "SOP-16"), "ICs should use microcircuit value type", failures);

            AssertEqual("not custom", SymbolImportRules.IsCustomParameter("Footprint") ? "custom" : "not custom", "footprint model name must not be added as a custom parameter", failures);
            AssertEqual("not custom", SymbolImportRules.IsCustomParameter("FootprintLibrary") ? "custom" : "not custom", "footprint library link must not be added as a custom parameter", failures);
            AssertEqual("not custom", SymbolImportRules.IsCustomParameter("Package") ? "custom" : "not custom", "package must not be added as a custom parameter", failures);
            AssertEqual("not custom", SymbolImportRules.IsCustomParameter("mounting") ? "custom" : "not custom", "mounting must not be added as a custom parameter", failures);
            AssertEqual("custom", SymbolImportRules.IsCustomParameter("Manufacturer") ? "custom" : "not custom", "manufacturer remains a custom GOST parameter", failures);

            AssertEqual(
                "BM08B-GHS-TBT",
                SymbolImportRules.SelectDesignItemId(
                    manufacturerPart: "BM08B-GHS-TBT",
                    symbolName: "EasyEDA Generic Name",
                    componentTitle: "C123456",
                    searchResultName: "C123456",
                    searchPart: "C123456",
                    lcscNumber: "C123456",
                    szlcscNumber: "C123456"),
                "design item ID should prefer manufacturer part number over EasyEDA or LCSC identifiers",
                failures);

            AssertEqual(
                "USB4110-GF-A",
                SymbolImportRules.SelectDesignItemId(
                    manufacturerPart: "",
                    symbolName: "USB4110-GF-A",
                    componentTitle: "C999999",
                    searchResultName: "C999999",
                    searchPart: "C999999",
                    lcscNumber: "C999999",
                    szlcscNumber: "C999999"),
                "design item ID should fall back to EasyEDA manufacturer-style symbol name before LCSC identifiers",
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Symbol rule regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Symbol rule regression test passed.");
            return 0;
        }

        private static FootprintDescriptionGeometry CreateMr30FootprintGeometry()
        {
            return new FootprintDescriptionGeometry
            {
                PositionCount = 3,
                PitchMm = 3.5,
                BodyWidthMm = 11.9,
                BodyHeightMm = 5.3
            };
        }

        private static void AssertEqual(string expected, string actual, string message, List<string> failures)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                failures.Add(message + ": expected '" + expected + "', got '" + actual + "'.");
        }

        private static void AssertNear(double expected, double actual, double tolerance, string message, List<string> failures)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                failures.Add(
                    message +
                    ": expected '" +
                    expected.ToString(CultureInfo.InvariantCulture) +
                    "', got '" +
                    actual.ToString(CultureInfo.InvariantCulture) +
                    "'.");
            }
        }

        private static int SaveSilhouetteProjectionImage(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette <input.step> <output.png> [--rotx deg] [--roty deg] [--rotz deg] [--rotation2d deg] [--size pixels] [--padding pixels] [--no-grid] [--no-axes]");
                return 2;
            }

            string inputPath = args[1];
            string outputPath = args[2];
            double rotX = 0.0;
            double rotY = 0.0;
            double rotZ = 0.0;
            double rotation2D = 0.0;
            int imageSizePixels = 1600;
            int paddingPixels = 90;
            bool drawGrid = true;
            bool drawAxes = true;

            for (int index = 3; index < args.Length; index++)
            {
                string option = args[index];
                if (IsOption(option, "--no-grid"))
                {
                    drawGrid = false;
                    continue;
                }

                if (IsOption(option, "--no-axes"))
                {
                    drawAxes = false;
                    continue;
                }

                if (IsOption(option, "--rotx"))
                {
                    if (!TryReadDoubleOption(args, ref index, option, out rotX))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--roty"))
                {
                    if (!TryReadDoubleOption(args, ref index, option, out rotY))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--rotz"))
                {
                    if (!TryReadDoubleOption(args, ref index, option, out rotZ))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--rotation2d"))
                {
                    if (!TryReadDoubleOption(args, ref index, option, out rotation2D))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--size"))
                {
                    if (!TryReadIntOption(args, ref index, option, out imageSizePixels))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--padding"))
                {
                    if (!TryReadIntOption(args, ref index, option, out paddingPixels))
                        return 2;
                    continue;
                }

                Console.Error.WriteLine("Unknown silhouette option: " + option);
                return 2;
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + inputPath);
                return 2;
            }

            byte[] stepData = File.ReadAllBytes(inputPath);
            var placement = new StepSilhouettePlacement
            {
                TargetBounds = new StepSilhouetteBounds
                {
                    Left = -0.5,
                    Bottom = -0.5,
                    Right = 0.5,
                    Top = 0.5
                },
                RotX = rotX,
                RotY = rotY,
                RotZ = rotZ,
                Rotation2D = rotation2D
            };

            IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(stepData, placement);
            var renderOptions = new StepSilhouetteImageRenderOptions
            {
                ImageSizePixels = imageSizePixels,
                PaddingPixels = paddingPixels,
                DrawGrid = drawGrid,
                DrawAxes = drawAxes,
                Title = Path.GetFileName(inputPath)
            };
            StepSilhouetteImageRenderer.SavePng(primitives, outputPath, renderOptions);

            int lineCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Line);
            int arcCount = primitives.Count - lineCount;
            Console.WriteLine("Silhouette image written: " + Path.GetFullPath(outputPath));
            Console.WriteLine(
                "Primitives: " +
                lineCount.ToString(CultureInfo.InvariantCulture) +
                " line(s), " +
                arcCount.ToString(CultureInfo.InvariantCulture) +
                " arc(s), " +
                primitives.Count.ToString(CultureInfo.InvariantCulture) +
                " total.");
            return 0;
        }

        private static bool TryReadDoubleOption(string[] args, ref int index, string option, out double value)
        {
            value = 0.0;
            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine("Missing value for " + option);
                return false;
            }

            index++;
            if (!double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                Console.Error.WriteLine("Invalid numeric value for " + option + ": " + args[index]);
                return false;
            }

            return true;
        }

        private static bool TryReadIntOption(string[] args, ref int index, string option, out int value)
        {
            value = 0;
            if (index + 1 >= args.Length)
            {
                Console.Error.WriteLine("Missing value for " + option);
                return false;
            }

            index++;
            if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                Console.Error.WriteLine("Invalid integer value for " + option + ": " + args[index]);
                return false;
            }

            return true;
        }

        private static bool IsOption(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static void VerifyPostCleanProjections(
            List<string> originalFiles,
            Dictionary<string, string> generatedCleanByName,
            string originalProjectionDirectory,
            string cleanProjectionDirectory,
            StepProjectionOptions projectionOptions,
            Dictionary<string, List<string>> detectionViewNamesByFileName,
            ProjectionVerificationTimings projectionTimings,
            HashSet<string> postCleanFaultFileNames,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            var matchedFileNames = generatedCleanByName.Keys
                .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matchedFileNames.Count == 0)
                return;

            ClearProjectionFiles(originalProjectionDirectory, matchedFileNames);

            var originalByName = originalFiles.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            int comparedImages = 0;
            int checkedRegions = 0;

            foreach (string fileName in matchedFileNames)
            {
                if (!originalByName.TryGetValue(fileName, out string originalFile))
                {
                    failures.Add("Generated clean file has no matching Original model: " + fileName);
                    continue;
                }

                var detectionReport = projectionTimings.Measure(
                    "post_clean_detection_ms",
                    () => StepWatermarkCleaner.Detect(File.ReadAllBytes(originalFile), new StepWatermarkCleanerOptions()));
                var detectionRegions = projectionTimings.Measure(
                    "post_clean_detection_region_projection_ms",
                    () => StepProjectionRenderer.ProjectDetectionRegions(
                        originalFile,
                        detectionReport,
                        projectionOptions)
                    .ToList());

                string modelName = Path.GetFileNameWithoutExtension(fileName);
                var detectedViewNames = detectionRegions
                    .Select(region => region.ViewName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                detectionViewNamesByFileName[fileName] = detectedViewNames;
                if (detectedViewNames.Count == 0)
                    continue;

                var renderOptions = CreateProjectionOptionsForViews(detectedViewNames, projectionOptions);
                projectionTimings.Measure(
                    "original_detection_side_projection_render_ms",
                    () => StepProjectionRenderer.ProjectFile(originalFile, originalProjectionDirectory, renderOptions));

                foreach (string viewName in detectedViewNames)
                {
                    string originalProjectionPath = Path.Combine(originalProjectionDirectory, modelName + "__" + viewName + ".png");
                    string cleanProjectionPath = Path.Combine(cleanProjectionDirectory, modelName + "__" + viewName + ".png");

                    if (!File.Exists(originalProjectionPath))
                    {
                        failures.Add("Original projection is missing: " + originalProjectionPath);
                        continue;
                    }

                    if (!File.Exists(cleanProjectionPath))
                    {
                        failures.Add("Clean projection is missing: " + cleanProjectionPath);
                        continue;
                    }

                    var viewRegions = detectionRegions
                        .Where(region => string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    projectionTimings.Measure(
                        "original_vs_clean_projection_compare_ms",
                        () => VerifyPostCleanProjectionImage(
                            fileName,
                            viewName,
                            originalProjectionPath,
                            cleanProjectionPath,
                            viewRegions,
                            postCleanFaultFileNames,
                            failures,
                            visualFailures));

                    comparedImages++;
                    checkedRegions += viewRegions.Count;
                }
            }

            Console.WriteLine(
                "Post-clean projection verification: models=" +
                matchedFileNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", images=" +
                comparedImages.ToString(CultureInfo.InvariantCulture) +
                ", detected regions=" +
                checkedRegions.ToString(CultureInfo.InvariantCulture));
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

        private static void VerifyPostCleanProjectionImage(
            string fileName,
            string viewName,
            string originalProjectionPath,
            string cleanProjectionPath,
            IReadOnlyList<StepProjectionDetectionRegion> detectionRegions,
            HashSet<string> postCleanFaultFileNames,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            using (var originalImage = SKBitmap.Decode(originalProjectionPath))
            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            {
                if (originalImage == null || cleanImage == null)
                {
                    failures.Add(fileName + " has an unreadable original or clean projection on " + viewName + ".");
                    return;
                }

                if (originalImage.Width != cleanImage.Width || originalImage.Height != cleanImage.Height)
                {
                    failures.Add(fileName + " original and clean projections have different sizes on " + viewName + ".");
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
                    postCleanFaultFileNames.Add(fileName);
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
                    failures.Add(
                        message);
                    visualFailures.Add(new ProjectionVisualFailure
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
                    VerifyCleanedRegionFlatness(fileName, viewName, originalImage, cleanImage, region, postCleanFaultFileNames, failures);
            }
        }

        private static int GetAllowedOutsideRegionChanges(int imageWidth, int imageHeight)
        {
            return Math.Max(
                1,
                (int)Math.Round(
                    imageWidth * imageHeight * MaxOutsideDetectionRegionChangeRatio,
                    MidpointRounding.AwayFromZero));
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

        private static void VerifyCleanedRegionFlatness(
            string fileName,
            string viewName,
            SKBitmap originalImage,
            SKBitmap cleanImage,
            StepProjectionDetectionRegion region,
            HashSet<string> postCleanFaultFileNames,
            List<string> failures)
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

            postCleanFaultFileNames.Add(fileName);
            failures.Add(
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
                ".");
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

        private static bool IsBackgroundLike(SKColor color)
        {
            return color.Red >= 245 && color.Green >= 245 && color.Blue >= 245;
        }

        private static void VerifyCleanupIgnoresMarkedOptions(List<string> originalFiles, List<string> failures)
        {
            if (originalFiles.Count == 0)
                return;

            byte[] originalStep = File.ReadAllBytes(originalFiles[0]);
            byte[] automaticClean = StepWatermarkCleaner.Clean(originalStep, new StepWatermarkCleanerOptions());
            var markedOptions = new StepWatermarkCleanerOptions
            {
                UseMarkedRegionsOnly = true
            };
            markedOptions.MarkedRegions.Add(new StepWatermarkMarkedRegion
            {
                ViewName = "z_plus",
                UAxis = 0,
                USign = 1,
                VAxis = 1,
                VSign = 1,
                DepthAxis = 2,
                DepthSign = 1,
                ModelUMin = -1000.0,
                ModelUMax = 1000.0,
                ModelVMin = -1000.0,
                ModelVMax = 1000.0,
                ScalePixelsPerModelUnit = 1.0,
                ImageWidth = 2000,
                ImageHeight = 2000,
                RectangleX = 0,
                RectangleY = 0,
                RectangleWidth = 2000,
                RectangleHeight = 2000
            });

            byte[] markedClean = StepWatermarkCleaner.Clean(originalStep, markedOptions);
            if (!automaticClean.SequenceEqual(markedClean))
                failures.Add("Clean output changed when marker-only options were supplied; cleanup must use automatic detection only.");
        }

        private static void VerifyDetectionDebugImages(
            List<string> originalFiles,
            HashSet<string> originalBaseNames,
            string projectionDirectory,
            string markedDirectory,
            string detectionDirectory,
            List<string> failures)
        {
            if (!Directory.Exists(markedDirectory))
            {
                failures.Add("Marked directory was not found: " + markedDirectory);
                return;
            }

            if (!Directory.Exists(projectionDirectory))
            {
                failures.Add("Projection directory was not found: " + projectionDirectory);
                return;
            }

            Directory.CreateDirectory(detectionDirectory);

            Stopwatch stopwatch = Stopwatch.StartNew();
            var expectedNames = GetMarkedDetectionImageNames(markedDirectory, originalBaseNames);
            var expectedSet = new HashSet<string>(expectedNames, StringComparer.OrdinalIgnoreCase);
            foreach (string staleImage in Directory.GetFiles(detectionDirectory, "*.png"))
            {
                if (!expectedSet.Contains(Path.GetFileName(staleImage)))
                    File.Delete(staleImage);
            }

            int regeneratedModels = 0;
            int cachedModels = 0;
            foreach (string originalFile in originalFiles)
            {
                var markedRegions = StepWatermarkCleaner.LoadMarkedRegionsForStepFile(
                    originalFile,
                    projectionDirectory,
                    markedDirectory);
                if (IsDetectionDebugImageCacheFresh(originalFile, markedRegions, detectionDirectory))
                {
                    cachedModels++;
                    continue;
                }

                var detectionReport = StepWatermarkCleaner.Detect(File.ReadAllBytes(originalFile), new StepWatermarkCleanerOptions());

                StepProjectionRenderer.ProjectDetectionFile(
                    originalFile,
                    detectionDirectory,
                    detectionReport,
                    new StepProjectionOptions
                    {
                        WriteMetadata = false
                    },
                    markedRegions);
                regeneratedModels++;
            }

            var actualNames = Directory.GetFiles(detectionDirectory, "*.png")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            stopwatch.Stop();
            Console.WriteLine(
                "Detection debug images: marked=" +
                expectedNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", generated=" +
                actualNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", regenerated models=" +
                regeneratedModels.ToString(CultureInfo.InvariantCulture) +
                ", cached models=" +
                cachedModels.ToString(CultureInfo.InvariantCulture) +
                ", elapsed=" +
                stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " ms");

            if (actualNames.Count != expectedNames.Count)
            {
                failures.Add(
                    "Detection debug image count differs from Marked sidecars: marked=" +
                    expectedNames.Count.ToString(CultureInfo.InvariantCulture) +
                    ", generated=" +
                    actualNames.Count.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            var actualSet = new HashSet<string>(actualNames, StringComparer.OrdinalIgnoreCase);

            foreach (string expectedName in expectedNames)
            {
                if (!actualSet.Contains(expectedName))
                    failures.Add("Detection debug image is missing for marked side: " + expectedName);
            }

            foreach (string actualName in actualNames)
            {
                if (!expectedSet.Contains(actualName))
                    failures.Add("Detection debug image has no matching marked side: " + actualName);
            }
        }

        private static bool IsDetectionDebugImageCacheFresh(
            string originalFile,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions,
            string detectionDirectory)
        {
            if (markedRegions == null || markedRegions.Count == 0)
                return true;

            DateTime latestInputWriteTimeUtc = GetLatestDetectionDebugInputWriteTimeUtc(originalFile, markedRegions);
            foreach (StepWatermarkMarkedRegion region in markedRegions)
            {
                string markerPath = region.SourceMarkerPath;
                if (string.IsNullOrEmpty(markerPath))
                    return false;

                string outputPath = Path.Combine(
                    detectionDirectory,
                    Path.GetFileNameWithoutExtension(markerPath) + ".png");
                if (!File.Exists(outputPath))
                    return false;

                if (File.GetLastWriteTimeUtc(outputPath) < latestInputWriteTimeUtc)
                    return false;
            }

            return true;
        }

        private static DateTime GetLatestDetectionDebugInputWriteTimeUtc(
            string originalFile,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions)
        {
            DateTime latest = File.GetLastWriteTimeUtc(originalFile);
            AddLatestWriteTimeUtc(ref latest, typeof(Program).Assembly.Location);
            AddLatestWriteTimeUtc(ref latest, typeof(StepWatermarkCleaner).Assembly.Location);
            AddLatestWriteTimeUtc(ref latest, typeof(StepProjectionRenderer).Assembly.Location);

            foreach (StepWatermarkMarkedRegion region in markedRegions)
            {
                AddLatestWriteTimeUtc(ref latest, region.SourceMarkerPath);
                AddLatestWriteTimeUtc(ref latest, region.SourceProjectionPath);
            }

            return latest;
        }

        private static void AddLatestWriteTimeUtc(ref DateTime latest, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            DateTime writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime > latest)
                latest = writeTime;
        }

        private static List<string> GetMarkedDetectionImageNames(string markedDirectory, HashSet<string> originalBaseNames)
        {
            var result = new List<string>();
            foreach (string modelName in originalBaseNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                string stepFileName = modelName + ".step";
                var markedRegions = StepWatermarkCleaner.LoadMarkedRegionsForStepFile(
                    stepFileName,
                    Path.Combine(Path.GetDirectoryName(markedDirectory) ?? string.Empty, "Projection"),
                    markedDirectory);

                foreach (var region in markedRegions)
                {
                    string markerPath = region.SourceMarkerPath;
                    if (string.IsNullOrEmpty(markerPath))
                        continue;

                    result.Add(Path.GetFileNameWithoutExtension(markerPath) + ".png");
                }
            }

            result = result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static void CompareCleanAndValidatedProjections(
            Dictionary<string, string> generatedCleanByName,
            Dictionary<string, string> validatedByName,
            string validatedDirectory,
            string cleanProjectionDirectory,
            string validatedProjectionDirectory,
            StepProjectionOptions projectionOptions,
            Dictionary<string, List<string>> detectionViewNamesByFileName,
            ProjectionVerificationTimings projectionTimings,
            HashSet<string> postCleanFaultFileNames,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            var matchedFileNames = generatedCleanByName.Keys
                .Where(fileName => validatedByName.ContainsKey(fileName))
                .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matchedFileNames.Count == 0)
                return;

            ClearProjectionFiles(validatedProjectionDirectory, matchedFileNames);

            projectionTimings.Measure(
                "validated_projection_render_ms",
                () => StepProjectionRenderer.ProjectDirectory(validatedDirectory, validatedProjectionDirectory, projectionOptions));

            int comparedImages = 0;
            int ignoredValidatedDifferences = 0;
            foreach (string fileName in matchedFileNames)
            {
                string modelName = Path.GetFileNameWithoutExtension(fileName);
                if (!detectionViewNamesByFileName.TryGetValue(fileName, out List<string> detectionViewNames) ||
                    detectionViewNames.Count == 0)
                    continue;

                foreach (string viewName in detectionViewNames)
                {
                    string cleanProjectionPath = Path.Combine(cleanProjectionDirectory, modelName + "__" + viewName + ".png");
                    string validatedProjectionPath = Path.Combine(validatedProjectionDirectory, modelName + "__" + viewName + ".png");

                    if (!File.Exists(cleanProjectionPath))
                    {
                        failures.Add("Clean projection is missing: " + cleanProjectionPath);
                        continue;
                    }

                    if (!File.Exists(validatedProjectionPath))
                    {
                        failures.Add("Validated projection is missing: " + validatedProjectionPath);
                        continue;
                    }

                    bool projectionsEqual = projectionTimings.Measure(
                        "clean_vs_validated_projection_compare_ms",
                        () => ProjectionPixelsEqual(cleanProjectionPath, validatedProjectionPath));
                    if (!projectionsEqual)
                    {
                        if (!postCleanFaultFileNames.Contains(fileName))
                        {
                            ignoredValidatedDifferences++;
                            continue;
                        }

                        string message =
                            fileName +
                            " differs from Validated projection on " +
                            viewName +
                            ": " +
                            cleanProjectionPath +
                            " vs " +
                            validatedProjectionPath;
                        failures.Add(message);
                        visualFailures.Add(new ProjectionVisualFailure
                        {
                            Category = "Clean vs Validated",
                            FileName = fileName,
                            ViewName = viewName,
                            Message = message,
                            LeftLabel = "Clean",
                            LeftImagePath = cleanProjectionPath,
                            RightLabel = "Validated",
                            RightImagePath = validatedProjectionPath
                        });
                    }

                    comparedImages++;
                }
            }

            Console.WriteLine(
                "Projection comparison: models=" +
                matchedFileNames.Count.ToString(CultureInfo.InvariantCulture) +
                ", images=" +
                comparedImages.ToString(CultureInfo.InvariantCulture) +
                ", ignored non-post-clean diffs=" +
                ignoredValidatedDifferences.ToString(CultureInfo.InvariantCulture));
        }

        private static bool ProjectionPixelsEqual(string cleanProjectionPath, string validatedProjectionPath)
        {
            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            using (var validatedImage = SKBitmap.Decode(validatedProjectionPath))
            {
                if (cleanImage == null || validatedImage == null)
                    return false;

                if (cleanImage.Width != validatedImage.Width || cleanImage.Height != validatedImage.Height)
                    return false;

                for (int y = 0; y < cleanImage.Height; y++)
                {
                    for (int x = 0; x < cleanImage.Width; x++)
                    {
                        if (cleanImage.GetPixel(x, y) != validatedImage.GetPixel(x, y))
                            return false;
                    }
                }

                return true;
            }
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

        private static void ClearProjectionFiles(string projectionDirectory, IEnumerable<string> stepFileNames)
        {
            Directory.CreateDirectory(projectionDirectory);
            foreach (string fileName in stepFileNames)
            {
                string modelName = Path.GetFileNameWithoutExtension(fileName);
                foreach (string projectionFile in Directory.GetFiles(projectionDirectory, modelName + "__*.png"))
                    File.Delete(projectionFile);

                foreach (string projectionFile in Directory.GetFiles(projectionDirectory, modelName + "__*.json"))
                    File.Delete(projectionFile);
            }
        }

        private static IReadOnlyList<string> GetCleanupNotes()
        {
            return new[]
            {
                "LED-SMD_XL-3838UV2SA06G3.step cleaned output should be reviewed as cleaned.",
                "USB-A-TH_FUS264-FDSW3K.step cleaned output should be reviewed as cleaned.",
                "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50.step cleaned output should be reviewed as cleaned."
            };
        }

        private static string FindDataRoot()
        {
            var roots = new List<string>
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory
            };

            foreach (string root in roots)
            {
                string current = Path.GetFullPath(root);
                for (int i = 0; i < 12; i++)
                {
                    string directData = Path.Combine(current, "Data");
                    if (IsDataRoot(directData))
                        return directData;

                    string repoData = Path.Combine(current, "Test", "StepCleaner", "Data");
                    if (IsDataRoot(repoData))
                        return repoData;

                    string parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                        break;

                    current = parent;
                }
            }

            throw new DirectoryNotFoundException("Could not find Test\\StepCleaner\\Data.");
        }

        private static bool IsDataRoot(string path)
        {
            return Directory.Exists(Path.Combine(path, "Original"))
                && Directory.Exists(Path.Combine(path, "Validated"));
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

        private sealed class ProjectionVerificationTimings
        {
            private readonly Dictionary<string, long> _elapsedByStage = new Dictionary<string, long>(StringComparer.Ordinal);
            private readonly List<string> _stageOrder = new List<string>();

            public void Measure(string stageName, Action action)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    action();
                }
                finally
                {
                    stopwatch.Stop();
                    Add(stageName, stopwatch.ElapsedMilliseconds);
                }
            }

            public T Measure<T>(string stageName, Func<T> action)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    return action();
                }
                finally
                {
                    stopwatch.Stop();
                    Add(stageName, stopwatch.ElapsedMilliseconds);
                }
            }

            public void WriteToConsole()
            {
                Console.WriteLine("Projection verification timings:");
                long total = 0;
                foreach (string stageName in _stageOrder)
                {
                    long elapsed = _elapsedByStage[stageName];
                    total += elapsed;
                    Console.WriteLine("  " + stageName + "=" + elapsed.ToString(CultureInfo.InvariantCulture) + " ms");
                }

                Console.WriteLine("  total_measured_projection_verification_ms=" + total.ToString(CultureInfo.InvariantCulture) + " ms");
            }

            private void Add(string stageName, long elapsedMilliseconds)
            {
                if (!_elapsedByStage.ContainsKey(stageName))
                {
                    _elapsedByStage.Add(stageName, 0);
                    _stageOrder.Add(stageName);
                }

                _elapsedByStage[stageName] += elapsedMilliseconds;
            }
        }

        private static List<string> GetStepFiles(string directory)
        {
            if (!Directory.Exists(directory))
                return new List<string>();

            var result = new List<string>();
            foreach (string file in Directory.GetFiles(directory))
            {
                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
                    result.Add(file);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}
