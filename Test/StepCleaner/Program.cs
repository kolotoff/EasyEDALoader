using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using StepF3DRenderLib;

namespace StepCleaner.Tests
{
    internal static class Program
    {
        private const int ProjectionDifferenceTolerance = 6;
        private const int AllowedDetectionRegionPaddingPixels = 16;
        private const double MaxOutsideDetectionRegionChangeRatio = 0.01;
        private const int VerificationProjectionImageSizePixels = 1000;
        private const int VerificationProjectionPaddingPixels = 50;
        private const int FlatnessEdgeThreshold = 28;
        private const double MinOriginalRegionEdgeRatioForFlatness = 0.08;
        private const double MaxCleanedRegionEdgeRatio = 0.035;
        private const double MaxRetainedRegionEdgeRatio = 0.45;
        private const string DefaultMeasurePartNumber = "C5338332";
        private const int OcctHlrBenchmarkMinimumExpectedLines = 290;
        private const int OcctHlrBenchmarkMinimumExpectedArcs = 20;

        private static int Main(string[] args)
        {
            if (args.Length > 0)
                return RunCommand(args);

            try
            {
                Stopwatch fullTestStopwatch = Stopwatch.StartNew();
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
                var detectionCache = new FullTestDetectionCache();

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
                    detectionCache,
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
                    detectionCache,
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

                    fullTestStopwatch.Stop();
                    Console.WriteLine("full_test_wall_ms=" + fullTestStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                    return 1;
                }

                Console.WriteLine(
                    "STEP cleaner regression test passed. Cleaned " +
                    originalFiles.Count.ToString(CultureInfo.InvariantCulture) +
                    " original file(s), compared " +
                    validatedFiles.Count.ToString(CultureInfo.InvariantCulture) +
                    " validated file(s).");
                fullTestStopwatch.Stop();
                Console.WriteLine("full_test_wall_ms=" + fullTestStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
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

            if (IsOption(args[0], "--import-save-policy"))
                return RunImportSavePolicyTests();

            if (IsOption(args[0], "--async-import"))
                return RunAsyncImportTests();

            if (IsOption(args[0], "--footprint-placement"))
                return RunFootprintPlacementTests();

            if (IsOption(args[0], "--footprint-layers"))
                return RunFootprintLayerTests();

            if (IsOption(args[0], "--pcblib-actions"))
                return RunPcbLibActionTests();

            if (IsOption(args[0], "--model-cache"))
                return RunModelCacheTests();

            if (IsOption(args[0], "--measure-model-import"))
                return RunModelImportMeasurement(args);

            if (IsOption(args[0], "--silhouette-cleanup"))
                return RunSilhouetteCleanupTests();

            if (IsOption(args[0], "--occt-hlr-smoke"))
                return OcctHiddenLineProjectionSmokeTests.Run();

            if (IsOption(args[0], "--occt-hlr-benchmark"))
                return RunOcctHlrBenchmark(args);

            if (IsOption(args[0], "--occt-overlap-unit"))
                return OcctOverlapCleanupTests.Run();

            if (IsOption(args[0], "--occt-stage-report"))
                return OcctSilhouetteStageReport.Run(args);

            if (IsOption(args[0], "--f3d-buffer-smoke"))
                return RunF3DBufferSmoke(args);

            if (IsOption(args[0], "--f3d-preview-smoke"))
                return RunF3DPreviewSmoke(args);

            if (IsOption(args[0], "--f3d-no-ambient-occlusion"))
                return RunF3DNoAmbientOcclusionTests();

            if (IsOption(args[0], "--clean-text"))
                return RunCleanTextTests();

            if (IsOption(args[0], "--xt60-lceda"))
                return RunXt60LcedaWatermarkTests();

            if (IsOption(args[0], "--projection-edge-mode"))
                return RunProjectionEdgeModeTests();

            if (IsOption(args[0], "--raw-silhouette-edge-projection"))
                return RunRawSilhouetteEdgeProjectionTests();
            if (IsOption(args[0], "--visible-raw-silhouette-edge-projection"))
                return RunVisibleRawSilhouetteEdgeProjectionTests();
            if (IsOption(args[0], "--edge-preview"))
                return SaveEdgePreview(args);

            if (IsOption(args[0], "--watermark-template-library"))
                return RunWatermarkTemplateLibraryTests();

            if (IsOption(args[0], "--text-logo-detection"))
                return RunTextLogoDetectionTests();

            if (IsOption(args[0], "--marked-detection-parity"))
                return RunMarkedDetectionParityTests(false);

            if (IsOption(args[0], "--marked-detection-parity-clean-text"))
                return RunMarkedDetectionParityTests(true);

            if (IsOption(args[0], "--text-logo-cleanup-promotion"))
                return RunTextLogoCleanupPromotionTests();

            if (IsOption(args[0], "--text-logo-negative-classifier"))
                return RunTextLogoNegativeClassifierTests();

            if (IsOption(args[0], "--text-logo-verifier"))
                return RunTextLogoVerifierTests();

            if (IsOption(args[0], "--text-logo-visual-residuals"))
                return RunTextLogoVisualResidualTests(args);

            if (IsOption(args[0], "--removed-geometry"))
                return RunRemovedGeometryExportTests();

            if (IsOption(args[0], "--removed-geometry-roi-locality"))
                return RunRemovedGeometryRoiLocalityTests();

            if (IsOption(args[0], "--silhouette"))
                return SaveSilhouetteProjectionImage(args);

            if (IsOption(args[0], "--silhouette-dump"))
                return SaveSilhouettePrimitiveDump(args);

            Console.Error.WriteLine("Unknown command: " + args[0]);
            Console.Error.WriteLine("Usage: StepCleaner.Tests --metadata");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --symbol-rules");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --import-save-policy");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --async-import");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --footprint-placement");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --footprint-layers");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --pcblib-actions");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --model-cache");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --measure-model-import [part-number] [--repeat count] [--clean-text]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette-cleanup");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-hlr-smoke");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-hlr-benchmark <input.step> [--repeat count]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-overlap-unit");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-stage-report [output-dir]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-buffer-smoke <input.step>");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-preview-smoke <input.step> [output.png]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-no-ambient-occlusion");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --clean-text");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --xt60-lceda");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --projection-edge-mode");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --raw-silhouette-edge-projection");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --watermark-template-library");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-detection");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --marked-detection-parity");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --marked-detection-parity-clean-text");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-cleanup-promotion");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-negative-classifier");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-verifier");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --text-logo-visual-residuals [fixture-name]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --removed-geometry");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --removed-geometry-roi-locality");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette <input.step> <output.png> [--rotx deg] [--roty deg] [--rotz deg] [--rotation2d deg] [--size pixels] [--padding pixels] [--no-grid] [--no-axes]");
            Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette-dump <input.step> <output.csv>");
            return 2;
        }

        private static int RunModelCacheTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();

            AssertEqual(
                "model-uuid__watermark",
                CleanStepCacheKeys.GetCleanModeKey("model-uuid", false),
                "watermark-only clean cache key should use the existing suffix",
                failures);

            AssertEqual(
                "model-uuid__watermark_text",
                CleanStepCacheKeys.GetCleanModeKey("model-uuid", true),
                "clean-text cache key should use the existing suffix",
                failures);

            var expectedKeys = new[]
            {
                "model-uuid__watermark",
                "model-uuid__watermark_text"
            };
            var actualKeys = CleanStepCacheKeys.GetCleanModeKeys("model-uuid").ToArray();
            for (int i = 0; i < expectedKeys.Length; i++)
            {
                string actual = i < actualKeys.Length ? actualKeys[i] : "";
                AssertEqual(expectedKeys[i], actual, "regenerate should target every cleaned STEP cache variant", failures);
            }
            if (actualKeys.Length != expectedKeys.Length)
            {
                failures.Add(
                    "regenerate should target exactly " +
                    expectedKeys.Length.ToString(CultureInfo.InvariantCulture) +
                    " cleaned STEP cache variants, got " +
                    actualKeys.Length.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            string footprint3dModel = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "FootprintShapes", "EeFootprint3dModel.cs"));
            string modelCache = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "ModelCache.cs"));
            string easyEdaLoader = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDALoader.cs"));
            string dialogWindow = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "DialogWindow.cs"));
            string dialogWindowXaml = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "DialogWindow.xaml"));
            string canvasZoomPanHelper = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "CanvasZoomPanHelper.cs"));
            string standaloneProject = File.ReadAllText(Path.Combine(repoRoot, "Standalone", "Standalone.csproj"));
            string standaloneMainWindow = File.ReadAllText(Path.Combine(repoRoot, "Standalone", "MainWindow.xaml.cs"));
            string stepWatermarkCleaner = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepWatermarkCleaner.cs"));
            string stepWatermarkCleanVerifier = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepWatermarkCleanVerifier.cs"));
            string stepProjectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
            string stepSilhouetteProjection = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepSilhouetteProjection.cs"));
            string occtHiddenLineExtractor = File.ReadAllText(Path.Combine(repoRoot, "StepOcctHlr", "OcctHiddenLineExtractor.cs"));
            string buildAndInstallScript = File.ReadAllText(Path.Combine(repoRoot, "BuildAndInstall-Altium.ps1"));
            string stepF3DRenderProgramPath = Path.Combine(repoRoot, "StepF3DRender", "Program.cs");
            string stepF3DRenderProgram = File.Exists(stepF3DRenderProgramPath)
                ? File.ReadAllText(stepF3DRenderProgramPath)
                : string.Empty;
            string stepF3DRenderLibPath = Path.Combine(repoRoot, "StepF3DRenderLib", "F3DProjectionRenderer.cs");
            string stepF3DRenderLib = File.Exists(stepF3DRenderLibPath)
                ? File.ReadAllText(stepF3DRenderLibPath)
                : string.Empty;
            string easyEdaProject = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDA-Loader.csproj"));
            string stepCleanerProject = File.ReadAllText(Path.Combine(repoRoot, "StepCleaner", "StepCleaner.csproj"));
            string stepCleanerTestsProject = File.ReadAllText(Path.Combine(repoRoot, "Test", "StepCleaner", "StepCleaner.Tests.csproj"));
            string stepCleanerProgram = File.ReadAllText(Path.Combine(repoRoot, "Test", "StepCleaner", "Program.cs"));
            AssertContains(
                footprint3dModel,
                "ModelCache.GetCleanStepModelWithStatusAsync",
                "footprint import should reuse the cleaned STEP model cache instead of cleaning every import",
                failures);
            AssertContains(
                modelCache,
                "public sealed class ModelCacheResult",
                "model cache should expose whether clean STEP data came from cache",
                failures);
            AssertContains(
                footprint3dModel,
                "GetCleanStepModelWithStatusAsync",
                "footprint import should trace clean STEP cache hit/miss status",
                failures);
            AssertContains(
                dialogWindow,
                "GetCleanStepModelWithStatusAsync",
                "preview clean STEP generation should share the same cache-status path",
                failures);
            AssertContains(
                modelCache,
                "GetSearchProductInfoAsync",
                "EasyEDA product search results should be cached under the local loader cache",
                failures);
            AssertContains(
                modelCache,
                "GetComponentJsonAsync",
                "EasyEDA component JSON should be cached under the local loader cache",
                failures);
            AssertContains(
                modelCache,
                "GetJsonObjectFromStringAsync",
                "EasyEDA component JSON should cache the raw server response instead of reserializing converter-backed objects",
                failures);
            AssertContains(
                modelCache,
                "IsUsableComponentRoot",
                "EasyEDA component JSON cache should reject stale files that no longer deserialize into preview-ready component data",
                failures);
            AssertContains(
                modelCache,
                "api.GetComponentJsonStringAsync(lcscId, cancellationToken)",
                "EasyEDA component JSON cache should download raw JSON so converter WriteJson methods cannot break preview loading",
                failures);
            AssertContains(
                File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "API", "EasyedaApi.cs")),
                "GetComponentJsonStringAsync",
                "EasyEDA API should expose raw component JSON for cache writes",
                failures);
            AssertContains(
                modelCache,
                "GetPngImageAsync",
                "EasyEDA thumbnail image data should be cached under the local loader cache",
                failures);
            AssertContains(
                modelCache,
                "GetProjectionPreviewPngAsync",
                "bottom model projection preview PNGs should be cached by original model and render size",
                failures);
            AssertContains(
                modelCache,
                "GetModelCacheRoot",
                "model cache should expose the cached 3D model root folder for Explorer",
                failures);
            AssertContains(
                modelCache,
                "DeleteSelectedComponentCache",
                "the dialog should be able to remove all cache files for the selected component",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetSearchProductInfoAsync",
                "search should use cached EasyEDA product data instead of always posting to the remote server",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetComponentJsonAsync",
                "preview and import should use cached EasyEDA component JSON instead of always fetching remotely",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetPngImageAsync",
                "thumbnail preview should use cached EasyEDA image bytes",
                failures);
            AssertContains(
                dialogWindow,
                "GetSelectedComponentCacheKey",
                "dialog clean/projection cache actions should be scoped to the selected component row",
                failures);
            AssertContains(
                dialogWindow,
                "WarmSelectedComponentCacheAsync",
                "form load and row selection preview should warm missing selected-component cache elements",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetRawObjModelAsync(Api, modelInfo.Uuid",
                "selected-component cache warm-up should store the raw OBJ model cache when missing",
                failures);
            AssertContains(
                dialogWindow,
                "ModelZInfoCache.GetOrCreateAsync",
                "selected-component cache warm-up should store the derived raw OBJ ZInfo cache when missing",
                failures);
            AssertContains(
                dialogWindow,
                "await interactivePreviewTask.ConfigureAwait(false)",
                "selected-component clean STEP warm-up should wait for the active preview to avoid cold-cache clean races",
                failures);
            AssertContains(
                dialogWindow,
                "CleanStepCacheKeys.GetCleanModeKeys(selectedComponentCacheKey)",
                "selected-component cache warm-up should store every clean STEP cache variant when missing",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetSearchProductInfoAsync(Api, partViewModel.PartInfo.Part",
                "selected-component cache warm-up should store search metadata when restored session skipped a live search",
                failures);
            AssertContains(
                footprint3dModel,
                "CleanStepModelFastWithReport",
                "footprint import clean-cache misses should skip projection verification and use the fast cleaner report path",
                failures);
            AssertContains(
                dialogWindow,
                "CleanStepModelFastWithReport",
                "preview clean STEP cache misses should skip projection verification and use the fast cleaner report path",
                failures);
            AssertContains(
                footprint3dModel,
                "ModelCache.GetComponentModelCacheKey(ctx.CachePartNumber, GetSafeCacheFileName())",
                "footprint import should scope clean STEP cache outputs to the selected component plus model",
                failures);
            AssertContains(
                easyEdaLoader,
                "CachePartNumber = selection.PartInfo?.Part",
                "footprint import should pass the stable selected LCSC row part number into cache keys",
                failures);
            AssertDoesNotContain(
                footprint3dModel,
                "? StepWatermarkCleanVerifier.CleanOrThrow",
                "footprint import should not call watermark cleanup directly inside the import branch",
                failures);
            AssertContains(
                easyEdaLoader,
                "ModelImportTrace.MeasureAsync(\"model_download_cache_read\"",
                "model STEP download/cache read should be timed from the normal import prefetch path",
                failures);
            AssertContains(
                easyEdaLoader,
                "ModelImportTrace.MeasureAsync(\"raw_obj_download_cache_read\"",
                "raw OBJ download/cache read should be timed from the normal import prefetch path",
                failures);
            AssertContains(
                footprint3dModel,
                "ModelImportTrace.Measure(\"watermark_clean_cache\"",
                "watermark clean/cache phase should be timed during 3D model import",
                failures);
            AssertContains(
                footprint3dModel,
                "ModelImportTrace.MeasureAsync(\"raw_obj_z_info\"",
                "raw OBJ Z info parse/cache phase should be timed during 3D model import",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "ModelImportTrace.Measure(\"occt_hlr_projection\"",
                "OCCT HLR helper projection phase should be timed",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "ModelImportTrace.Measure(\"projection_optimization\"",
                "projection parse/place/optimization phase should be timed",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "UseStandardInputForStepData",
                "OCCT silhouette projection should avoid writing a temp STEP file when the caller has bytes",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "ReadOcctProjectionJsonFromString",
                "OCCT silhouette projection should parse helper JSON from stdout without a temp JSON file",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "BuildPrimitiveSpatialBuckets",
                "OCCT overlap cleanup should use spatial buckets instead of scanning every primitive for every sample",
                failures);
            AssertContains(
                stepSilhouetteProjection,
                "Dictionary<long, List<int>> spatialBuckets",
                "OCCT overlap cleanup spatial buckets should use numeric keys instead of allocating strings for every sample",
                failures);
            AssertContains(
                occtHiddenLineExtractor,
                "TryReadFirstTwoShapeReferences",
                "OCCT HLR edge parsing should avoid regex work in the BREP edge hot loop",
                failures);
            AssertContains(
                occtHiddenLineExtractor,
                "IsIdentityModelRotation(options)",
                "OCCT HLR helper should skip BRepBuilderAPI_Transform when model rotation is identity",
                failures);
            AssertContains(
                dialogWindow,
                "CreatePreviewSession(",
                "interactive colored STEP preview should create an in-process libf3d preview session",
                failures);
            AssertContains(
                dialogWindow,
                "CreatePreviewSession(previewStepData",
                "interactive colored STEP preview should create a single active libf3d preview session from the selected clean/original STEP bytes",
                failures);
            AssertContains(
                dialogWindow,
                "CreatePreviewSession(previewStepData, cameraSnapshot)",
                "interactive colored STEP preview should restore the F3D camera when toggling clean/original STEP data",
                failures);
            AssertContains(
                dialogWindow,
                "DrainF3DPreviewInteractions()",
                "interactive colored STEP preview should apply queued wheel/mouse input before capturing camera for clean/original toggles",
                failures);
            AssertContains(
                dialogWindow,
                "if (_currentModel != null)",
                "clean option changes should refresh only the current 3D preview instead of reloading the whole component",
                failures);
            AssertContains(
                dialogWindow,
                "CoalesceF3DPreviewInteraction",
                "interactive colored STEP preview should coalesce drag mouse moves before rendering",
                failures);
            AssertContains(
                dialogWindow,
                "ScheduleF3DPreviewRender",
                "interactive colored STEP preview should throttle rendering without canceling every mouse move",
                failures);
            AssertContains(
                dialogWindow,
                "int maxEdge = isInteractive ? 960 : 1920",
                "interactive colored STEP preview should keep enough resolution while rotating and use high-resolution idle frames",
                failures);
            AssertContains(
                dialogWindow,
                "RequestF3DPreviewIdleRender",
                "interactive 3D preview should replace the low-resolution interaction frame with a high-resolution idle render",
                failures);
            AssertContains(
                dialogWindow,
                "isInteractive && _f3dPreviewDragStart == null",
                "interactive 3D preview should schedule the high-resolution refresh after drag or wheel input settles",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Width=\"1600\"",
                "dialog should be wide enough for a 25 percent 3D preview column",
                failures);
            AssertContains(
                dialogWindowXaml,
                "<ColumnDefinition Width=\"37.5*\"/>",
                "dialog should allocate 37.5 percent of the main content width to the interactive 3D preview",
                failures);
            AssertContains(
                dialogWindowXaml,
                "x:Name=\"modelProjectionViewport\"",
                "middle-column projection preview should render inside a full-column viewport",
                failures);
            AssertContains(
                dialogWindowXaml,
                "x:Name=\"f3dPreviewStatusTextBlock\"",
                "interactive STEP preview should show a visible status or failure message when rendering cannot start",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Background=\"#FAFAFA\"",
                "middle-column projection preview should use a light viewport background so unfilled sides do not appear as black gutters",
                failures);
            AssertDoesNotContain(
                dialogWindowXaml,
                "Background=\"Black\"",
                "symbol and footprint preview surfaces should not be black because EasyEDA primitives can render dark-on-dark and appear blank",
                failures);
            AssertContains(
                dialogWindowXaml,
                "modelProjectionImage",
                "middle-column projection preview should keep the projection image control",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Width=\"{Binding ActualWidth, ElementName=modelProjectionViewport}\"",
                "middle-column projection preview should display at the exact column viewport width",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Height=\"{Binding ActualHeight, ElementName=modelProjectionViewport}\"",
                "middle-column projection preview should display at the exact row viewport height",
                failures);
            AssertContains(
                dialogWindowXaml,
                "VerticalAlignment=\"Top\"",
                "middle-column projection preview should preserve its rendered height instead of vertically centering a shifted crop",
                failures);
            AssertContains(
                dialogWindowXaml,
                "Stretch=\"Uniform\"",
                "middle-column projection and interactive 3D previews should fit their columns without crop-induced shifting",
                failures);
            AssertContains(
                dialogWindow,
                "GetModelProjectionPreviewImageSizePixels",
                "middle-column projection preview should render the source bitmap at the column width",
                failures);
            AssertContains(
                dialogWindow,
                "modelProjectionViewport.ActualWidth",
                "middle-column projection preview should size projection rendering from the current column width",
                failures);
            AssertContains(
                dialogWindow,
                "GetModelProjectionPreviewImageSizePixels(out int imageWidthPixels, out int imageHeightPixels)",
                "middle-column projection preview should render a rectangular bitmap matching the preview viewport",
                failures);
            AssertContains(
                dialogWindow,
                "modelProjectionViewport.ActualHeight",
                "middle-column projection preview should size projection rendering from the current row height",
                failures);
            AssertContains(
                dialogWindow,
                "ImageWidthPixels = imageWidthPixels",
                "middle-column projection preview should pass the column width to the projection renderer",
                failures);
            AssertContains(
                dialogWindow,
                "ImageHeightPixels = imageHeightPixels",
                "middle-column projection preview should pass the row height to the projection renderer",
                failures);
            AssertContains(
                dialogWindow,
                "SetF3DPreviewStatus(\"3D preview failed: \" + ex.Message",
                "interactive STEP preview failures should be visible in the dialog instead of trace-only",
                failures);
            AssertContains(
                dialogWindow,
                "Clean STEP preview failed; showing original STEP",
                "interactive STEP preview should fall back to the original STEP when clean STEP generation fails",
                failures);
            AssertContains(
                dialogWindow,
                "byte[] previewStepData = stepData;",
                "interactive STEP preview should keep original STEP data available as the fallback preview model",
                failures);
            AssertContains(
                dialogWindow,
                "SetF3DPreviewStatus(\"3D preview render failed: \" + ex.Message",
                "interactive STEP render failures should be visible in the dialog instead of trace-only",
                failures);
            AssertContains(
                dialogWindow,
                "DrawSymbolPreviewSafely",
                "symbol preview drawing failures should not abort footprint and 3D preview loading",
                failures);
            AssertContains(
                dialogWindow,
                "DrawFootprintPreviewSafely",
                "footprint preview drawing failures should not abort 3D preview loading",
                failures);
            AssertContains(
                dialogWindow,
                "LoadPreviewComponentJsonAsync",
                "preview loading should retry component JSON by the row part number and display name before giving up",
                failures);
            AssertContains(
                dialogWindow,
                "GetPreviewPartCandidates",
                "preview loading should derive stable fallback identifiers from the selected result row",
                failures);
            AssertContains(
                dialogWindow,
                "ShowMissingPreviewData",
                "preview loading should show visible pane messages when EasyEDA returns no drawable component data",
                failures);
            AssertContains(
                dialogWindow,
                "No component preview data was returned",
                "blank component JSON responses should be visible in the preview panes",
                failures);
            AssertContains(
                dialogWindow,
                "No footprint preview data was returned",
                "parts without footprint payloads should show a visible footprint preview message",
                failures);
            AssertContains(
                dialogWindow,
                "No 3D model preview data was returned",
                "parts without usable 3D payloads should show a visible original STEP preview message",
                failures);
            AssertContains(
                dialogWindow,
                "if (symbolCanvas.Children.Count == 0)",
                "symbol preview should show a visible message when a non-null symbol payload draws no primitives",
                failures);
            AssertContains(
                dialogWindow,
                "if (footprintCanvas.Children.Count == 0)",
                "footprint preview should show a visible message when a non-null footprint payload draws no primitives",
                failures);
            AssertContains(
                dialogWindow,
                "BitmapSource bitmapSource = CreateF3DBitmapSource(renderedImage);",
                "interactive STEP preview should inspect renderer bitmap creation before clearing failure status",
                failures);
            AssertContains(
                dialogWindow,
                "3D preview failed: renderer returned no image.",
                "interactive STEP preview should show a visible message when F3D returns an empty image",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "public int ImageWidthPixels",
                "STEP projection options should support non-square preview render width",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "public int ImageHeightPixels",
                "STEP projection options should support non-square preview render height",
                failures);
            AssertDoesNotContain(
                dialogWindowXaml,
                "Stretch=\"UniformToFill\"",
                "projection and interactive 3D previews should not crop their bitmaps while fitting to the column",
                failures);
            AssertContains(
                dialogWindowXaml,
                "x:Name=\"f3dModelViewport\"",
                "interactive 3D preview should render against the full preview column viewport",
                failures);
            AssertContains(
                dialogWindowXaml,
                "RenderOptions.BitmapScalingMode=\"HighQuality\"",
                "interactive 3D preview should use high-quality WPF bitmap scaling",
                failures);
            AssertContains(
                dialogWindow,
                "f3dModelViewport.ActualWidth",
                "interactive 3D preview should size F3D renders from the full preview column viewport width",
                failures);
            AssertContains(
                dialogWindow,
                "Task<byte[]> stepDataTask = ModelCache.GetStepModelAsync",
                "3D model preview should start a shared STEP load as soon as the model UUID is known",
                failures);
            AssertContains(
                dialogWindow,
                "interactivePreviewTask = ShowInteractiveModelPreviewAsync(_currentModel, stepDataTask",
                "interactive 3D preview should start from the shared STEP task independently of other preview data",
                failures);
            AssertContains(
                dialogWindow,
                "ShowModelProjectionPreviewAsync(_currentModel,",
                "2D model projection should render from an explicit original-model projection path",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetProjectionPreviewPngAsync",
                "2D model projection should cache rendered preview PNGs",
                failures);
            AssertContains(
                dialogWindow,
                "await originalStepDataTask.ConfigureAwait(false)",
                "2D model projection cache misses should reuse the already-started original STEP task",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetStepModelAsync(Api, modelInfo.Uuid",
                "2D model projection should always read the original STEP model, independent of clean STEP preview state",
                failures);
            AssertAppearsBefore(
                dialogWindow,
                "interactivePreviewTask = ShowInteractiveModelPreviewAsync(_currentModel, stepDataTask",
                "DrawFootprintPreviewSafely",
                "interactive 3D preview should begin before footprint drawing runs through the safe UI preview path",
                failures);
            AssertAppearsBefore(
                dialogWindow,
                "interactivePreviewTask = ShowInteractiveModelPreviewAsync(_currentModel, stepDataTask",
                "ShowModelProjectionPreviewAsync(_currentModel,",
                "interactive 3D preview should begin before 2D projection rendering",
                failures);
            AssertContains(
                dialogWindowXaml,
                "removeCacheButton",
                "dialog should expose a Remove cache button near model cache actions",
                failures);
            AssertContains(
                dialogWindowXaml,
                "openStepCacheButton",
                "dialog should expose an Open STEP Cache button near model cache actions",
                failures);
            AssertContains(
                dialogWindow,
                "RemoveCacheButton_Click",
                "Remove cache button should be wired to selected-component cache cleanup",
                failures);
            AssertContains(
                dialogWindow,
                "OpenStepCacheButton_Click",
                "Open STEP Cache button should be wired to an Explorer folder opener",
                failures);
            AssertContains(
                dialogWindow,
                "ModelCache.GetModelCacheRoot()",
                "Open STEP Cache should open the cached 3D model folder",
                failures);
            AssertContains(
                dialogWindow,
                "previewCts = new CancellationTokenSource();\n\n                await LoadPreviewAsync(partViewModel, previewCts.Token);\n\n                CompleteCriticalOperation(\"Selected component cache removed.\", true);",
                "Remove cache should reload fresh selected-component data into cache and preview after deletion",
                failures);
            AssertContains(
                dialogWindow,
                "IsCurrentPreviewForSelectedComponent",
                "model cache buttons should only act when the loaded preview belongs to the selected grid row",
                failures);
            AssertContains(
                dialogWindow,
                "HasSelectedComponentForCache(out _)",
                "Remove cache should be enabled for the selected part even when preview/model loading failed",
                failures);
            AssertContains(
                dialogWindow,
                "HasSelectedComponentForCache(out PartInfoViewModel partViewModel)",
                "Remove cache should act on the selected part instead of requiring a completed preview",
                failures);
            AssertContains(
                dialogWindowXaml,
                "toggleOperationLogButton",
                "operation log panel should expose a button to hide and show the log",
                failures);
            AssertContains(
                dialogWindow,
                "ToggleOperationLogButton_Click",
                "operation log hide/show button should be wired to code-behind",
                failures);
            AssertContains(
                dialogWindow,
                "operationLogTextBox.Visibility",
                "operation log hide/show button should collapse the log text box",
                failures);
            AssertContains(
                standaloneProject,
                "<TargetFramework>net8.0-windows</TargetFramework>",
                "standalone app should target the same Windows runtime family as the shared loader project",
                failures);
            AssertContains(
                standaloneMainWindow,
                "GetZInfoFromOrigin(ctx)",
                "standalone app should use the current raw OBJ Z-info API instead of the removed Z-offset helper",
                failures);
            AssertContains(
                standaloneMainWindow,
                "ModelCache.GetSearchProductInfoAsync",
                "standalone search should use the same cached EasyEDA product lookup as the dialog",
                failures);
            AssertContains(
                standaloneMainWindow,
                "ModelCache.GetComponentJsonAsync",
                "standalone preview loading should use cached component JSON instead of direct live API calls",
                failures);
            AssertContains(
                standaloneMainWindow,
                "LoadSelectedPartAsync",
                "standalone should load preview for the selected search result instead of requiring row double-click",
                failures);
            AssertContains(
                standaloneMainWindow,
                "_previewLoadVersion",
                "standalone preview loads should ignore stale async work from previous row selections",
                failures);
            AssertContains(
                standaloneMainWindow,
                "Interlocked.Increment(ref _previewLoadVersion)",
                "standalone preview loads should version each row load before awaiting remote/cache data",
                failures);
            AssertContains(
                standaloneMainWindow,
                "SearchBox.SelectedItem is EasyedaApi.PartInfo",
                "standalone Load button should prefer the selected row part over raw search text like gd32f103",
                failures);
            AssertContains(
                standaloneMainWindow,
                "DrawSymbolPreviewSafely",
                "standalone should isolate symbol drawing failures from footprint and 3D preview loading",
                failures);
            AssertContains(
                standaloneMainWindow,
                "DrawFootprintPreviewSafely",
                "standalone should isolate footprint drawing failures from 3D preview loading",
                failures);
            AssertContains(
                standaloneMainWindow,
                "No component preview data was returned",
                "standalone should show an in-pane message instead of crashing on missing component JSON",
                failures);
            AssertContains(
                canvasZoomPanHelper,
                "GetElementBounds",
                "canvas zoom helper should calculate primitive geometry bounds instead of relying only on ActualWidth",
                failures);
            AssertContains(
                canvasZoomPanHelper,
                "if (child is Line line)",
                "canvas zoom helper should include WPF Line coordinates when fitting symbol and footprint previews",
                failures);
            AssertContains(
                canvasZoomPanHelper,
                "shape.RenderedGeometry.Bounds",
                "canvas zoom helper should include Path and Shape rendered geometry bounds when fitting previews",
                failures);
            AssertContains(
                dialogWindow,
                "RenderInteractivePreviewImage",
                "interactive colored STEP preview should render one active image instead of rendering original and clean windows every frame",
                failures);
            AssertContains(
                dialogWindow,
                "QueueF3DPreviewRender",
                "interactive colored STEP preview should queue renders for the active preview image",
                failures);
            AssertContains(
                dialogWindowXaml,
                "f3dModelImage",
                "interactive colored STEP preview should host the selected original/clean model in one WPF Image",
                failures);
            AssertDoesNotContain(
                dialogWindowXaml,
                "f3dOriginalModelImage",
                "interactive colored STEP preview should not keep a second original-model preview image",
                failures);
            AssertDoesNotContain(
                dialogWindowXaml,
                "f3dCleanModelImage",
                "interactive colored STEP preview should not keep a second clean-model preview image",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "AIS_ColoredShape",
                "interactive colored STEP preview should not use the managed OCCT color probe with wrong DF56 colors",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "SetParent(",
                "interactive colored STEP preview should not reparent external f3d.exe windows",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "WaitForMainWindowHandleAsync",
                "interactive colored STEP preview should not wait for an external f3d.exe main window",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "RegisterRawInputDevices",
                "dual STEP preview sync should not use global raw-input mirroring",
                failures);
            AssertDoesNotContain(
                dialogWindow,
                "MirrorF3DRawMouseInput",
                "dual STEP preview sync should not mirror Win32 mouse messages between external windows",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "F3DPreviewCameraState",
                "F3D library helper should expose a shared preview camera state",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "CreatePreviewSession",
                "F3D library helper should expose a persistent in-process preview session",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "CreatePreviewSession(byte[] stepData)",
                "F3D library helper should expose a single-model preview session for faster dialog previews",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "RenderInteractivePreviewImage",
                "F3D library helper should render a single active preview image without a paired clean/original engine",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "GetCameraSnapshot",
                "F3D library helper should expose absolute camera capture for clean/original preview toggles",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "ApplyCameraSnapshot",
                "F3D library helper should restore an absolute camera capture on a new preview session",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "OrthographicZoomFactor",
                "F3D library helper should preserve orthographic zoom because F3D stores it as parallel scale, not view angle",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "PreviewMouseWheelZoomFactor",
                "F3D library helper should track wheel zoom factor for camera restore in orthographic mode",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "_pendingCameraSnapshot",
                "F3D library helper should defer camera restore until the preview window has its final render size",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "ApplyPendingCameraSnapshot",
                "F3D library helper should apply saved zoom after PreparePreviewWindows sets the sized viewport",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_camera_azimuth",
                "F3D library helper should apply preview orbit through libf3d camera APIs",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_camera_pan",
                "F3D library helper should apply preview pan through libf3d camera APIs",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_camera_zoom",
                "F3D library helper should apply preview zoom through libf3d camera APIs",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_engine_get_interactor",
                "interactive colored STEP preview should use F3D's native interactor for mouse behavior",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_interactor_trigger_mouse_position",
                "interactive colored STEP preview should forward mouse movement to F3D's native interactor",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_interactor_trigger_mouse_wheel",
                "interactive colored STEP preview should forward mouse wheel input to F3D's native interactor",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "TryRenderWithF3D",
                "colored projection PNG rendering should keep F3D as the color-correct renderer until native XCAFPrs_AISObject support exists",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "TryRenderWithF3DLibraryBatch",
                "colored projection PNG rendering should try the single-load F3D library batch helper before f3d-console fallback",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_scene_add",
                "F3D library helper should load the STEP scene through f3d_c_api instead of f3d-console",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "f3d_scene_add_buffer",
                "F3D shared renderer should load STEP bytes directly through libf3d without a temp STEP file",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "F3DProjectionRenderer.RenderRawImages",
                "internal colored projection rendering should call the shared F3D renderer library instead of a helper process",
                failures);
            AssertDoesNotContain(
                stepProjectionRenderer,
                "--six-sides-stdout",
                "internal colored projection rendering should not send raw images through stdout",
                failures);
            AssertDoesNotContain(
                stepProjectionRenderer,
                "rawBase64",
                "internal colored projection rendering should not base64-expand raw image buffers",
                failures);
            AssertContains(
                stepF3DRenderProgram,
                "F3DProjectionRenderer.RenderPngFilesFromFile",
                "StepF3DRender executable should be a tiny CLI wrapper over the shared renderer",
                failures);
            AssertContains(
                easyEdaProject,
                "StepF3DRenderLib.csproj",
                "EasyEDA-Loader should reference the shared F3D renderer library for in-process projections",
                failures);
            AssertContains(
                stepCleanerProject,
                "StepF3DRenderLib.csproj",
                "StepCleaner should reference the shared F3D renderer library used by linked projection code",
                failures);
            AssertContains(
                stepCleanerTestsProject,
                "StepF3DRenderLib.csproj",
                "StepCleaner tests should reference the shared F3D renderer library used by linked projection code",
                failures);
            AssertContains(
                stepF3DRenderProgram,
                "--six-sides",
                "F3D library helper should expose a six-side render command",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "model.scivis.array_name",
                "F3D library helper should preserve the STEP Colors scalar array",
                failures);
            AssertContains(
                stepF3DRenderProgram,
                "--views",
                "F3D library helper should render selected side subsets from one STEP load",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "TryRenderWithF3DLibraryBatch(inputPath, selectedDetectionViews",
                "highlighted detection projections should try the single-load F3D library batch helper",
                failures);
            AssertDoesNotContain(
                stepProjectionRenderer,
                "views.Count != Views.Length",
                "F3D library batch renderer should not be limited to exactly six views",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "SkipGeometryModelForExternalRender",
                "verification projection rendering should be able to skip STEP geometry parsing when external rendering succeeds",
                failures);
            AssertContains(
                stepCleanerProgram,
                "FullTest" + "DetectionCache",
                "full StepCleaner regression should reuse original-model detection reports between debug image generation and post-clean verification",
                failures);
            AssertContains(
                stepCleanerProgram,
                "GetDetection" + "Regions(",
                "full StepCleaner regression should reuse projected detection regions for original-vs-clean and clean-vs-validated comparisons",
                failures);
            AssertContains(
                stepCleanerProgram,
                "detection_debug_project" + "_file_ms",
                "detection debug image generation should expose detailed timing for the remaining bottleneck",
                failures);
            AssertContains(
                stepCleanerProgram,
                "FilesEqualByLength" + "AndBytes",
                "clean-vs-validated projection comparison should skip PNG decode when files are byte-identical",
                failures);
            AssertContains(
                stepCleanerProgram,
                "CopyBitmapPixels" + "ToInt32Rows",
                "projection comparison should avoid per-pixel SKBitmap.GetPixel calls in hot loops",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "ProjectFileImages(",
                "internal projection rendering should expose an in-memory raw image API",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "ProjectDetectionFileImages(",
                "internal detection projection rendering should expose an in-memory highlighted raw image API",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "TryRenderWithF3DLibraryBatchToRawImages",
                "F3D library batch rendering should avoid saving/loading image files or PNG encoding for internal callers",
                failures);
            AssertContains(
                stepCleanerProgram,
                "Generate(cleanedStep,",
                "measurement OCCT HLR projection should use cleaned STEP bytes instead of requiring a saved STEP file",
                failures);
            AssertContains(
                footprint3dModel,
                "StepSilhouetteProjection.Generate(",
                "footprint import OCCT HLR projection should use in-memory STEP bytes after the Altium body file is written",
                failures);
            AssertContains(
                stepWatermarkCleanVerifier,
                "ProjectFileImages(",
                "watermark verification should compare raw in-memory projection images before writing report artifacts",
                failures);
            AssertContains(
                stepCleanerProgram,
                "full_test_wall_ms",
                "full StepCleaner regression timing should print total wall time for before/after comparisons",
                failures);
            AssertContains(
                buildAndInstallScript,
                "StepF3DRender\\StepF3DRender.csproj",
                "Altium install script should build the F3D library render helper",
                failures);
            AssertContains(
                buildAndInstallScript,
                "StepF3DRender.exe",
                "Altium install script should copy the F3D library render helper",
                failures);
            AssertContains(
                buildAndInstallScript,
                "STEPCLEANER_F3D_LIB",
                "Altium install script should honor the configured native F3D library path",
                failures);
            AssertContains(
                buildAndInstallScript,
                "f3d_c_api.dll",
                "Altium install script should package the native libf3d C API for internal previews",
                failures);
            AssertContains(
                buildAndInstallScript,
                "F3D\\bin",
                "Altium install script should install native F3D runtime DLLs under the extension F3D bin folder",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Installed F3D native library",
                "Altium install script should report the installed native F3D library path",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Install-F3DCompatibleMsvcRuntime",
                "Altium install script should update Altium's app-local MSVCP140.dll when it is too old for in-process F3D",
                failures);
            AssertDoesNotContain(
                buildAndInstallScript,
                "[string]$AltiumProfile = \"",
                "Altium install script should deduce the profile automatically instead of hard-coding a default parameter",
                failures);
            AssertDoesNotContain(
                buildAndInstallScript,
                "[string]$AltiumExe = \"",
                "Altium install script should deduce the executable automatically instead of hard-coding a default parameter",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Resolve-AltiumProfile",
                "Altium install script should resolve the installed Altium profile automatically",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Resolve-AltiumExecutable",
                "Altium install script should resolve the installed Altium executable automatically",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Resolve-AltiumInstallation",
                "Altium install script should correlate the profile and executable before installing",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Get-AltiumRegistryInstallations",
                "Altium install script should discover Altium installs from Windows uninstall registry entries",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Get-AltiumProfileCandidates $uniqueId",
                "Altium install script should correlate ProgramData profiles to the installer GUID",
                failures);
            AssertContains(
                buildAndInstallScript,
                "*_Security",
                "Altium install script should ignore Altium profile security folders",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Multiple Altium installations were detected",
                "Altium install script should fail clearly instead of guessing when multiple installs match",
                failures);
            AssertContains(
                buildAndInstallScript,
                "EasyEDA-Loader-MsvcBackup",
                "Altium install script should back up Altium's MSVCP140.dll before installing the F3D-compatible runtime",
                failures);
            AssertContains(
                buildAndInstallScript,
                "Updated Altium MSVCP140.dll for in-process F3D",
                "Altium install script should report MSVC runtime updates needed by in-process F3D",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "Path.Combine(baseDirectory, \"F3D\", \"bin\", \"f3d_c_api.dll\")",
                "F3D renderer should load the native library bundled by the Altium install script",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "typeof(F3DProjectionRenderer).Assembly.Location",
                "F3D renderer should locate the bundled native library beside StepF3DRenderLib when Altium is the host process",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs",
                "F3D renderer should isolate native dependency resolution from Altium's process directory",
                failures);
            AssertContains(
                stepF3DRenderLib,
                "Loaded MSVCP140.dll=",
                "F3D renderer should diagnose Altium's already-loaded MSVCP140.dll when native initialization fails",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "--scalar-coloring",
                "colored projection PNG rendering should preserve F3D scalar-coloring",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "--coloring-array=Colors",
                "colored projection PNG rendering should use the STEP Colors array",
                failures);
            AssertContains(
                stepProjectionRenderer,
                "--coloring-component=-2",
                "colored projection PNG rendering should use F3D RGB scalar components",
                failures);
            AssertDoesNotContain(
                stepProjectionRenderer,
                "AIS_ColoredShape",
                "colored projection PNG rendering should not use the managed OCCT color probe with wrong DF56 colors",
                failures);
            AssertContains(
                stepCleanerProgram,
                "DefaultMeasure" + "PartNumber = \"C5338332\"",
                "standalone model measurement should default to the C5338332 example",
                failures);
            AssertContains(
                stepCleanerProgram,
                "RunModelImport" + "Measurement(args)",
                "test harness should expose a standalone model import measurement command",
                failures);
            AssertContains(
                stepCleanerProgram,
                "GetComponent" + "JsonPath(partNumber)",
                "measurement command should cache component JSON lookup for repeatable measurements",
                failures);
            AssertContains(
                stepCleanerProgram,
                "--measure-" + "model-import",
                "usage should document the standalone model import measurement command",
                failures);
            AssertContains(
                stepCleanerProgram,
                "--occt-hlr-" + "benchmark",
                "usage should document the OCCT HLR benchmark command",
                failures);
            AssertContains(
                stepCleanerProgram,
                "RunOcctHlr" + "Benchmark(args)",
                "test harness should expose a standalone OCCT HLR benchmark command",
                failures);
            AssertContains(
                stepCleanerProgram,
                "CleanStepModelFastWithReport",
                "measurement command should exercise the same fast watermark clean-cache miss path as import",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "public sealed class StepWatermarkCleanerTiming",
                "watermark cleaner should expose detailed detection/edit timing entries",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "RemoveReferencesFromCommaList(definition, styledItemIds)",
                "watermark cleaner should remove styled-item references in bulk instead of one regex scan per removed style",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "BuildPlanarHostCandidatesByAxis",
                "watermark cleaner should cache planar host candidates per solid instead of rebuilding them for each watermark candidate",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "ownerInfo.PlanarHostCandidatesByAxis",
                "host-plane selection should use prebuilt planar host candidates from SolidInfo",
                failures);
            AssertContains(
                stepWatermarkCleaner,
                "facePointsByFace",
                "watermark cleaner should cache face point ids while building face components",
                failures);
            AssertContains(
                stepWatermarkCleanVerifier,
                "CleanOrThrowWithReport",
                "verifier should expose clean report timings for cache-miss measurement",
                failures);
            AssertContains(
                stepCleanerProgram,
                "watermark_clean_detail_",
                "measurement command should print detailed watermark detection/edit timings on clean cache misses",
                failures);
            AssertContains(
                stepCleanerProgram,
                "CleanOrThrowWithReport",
                "measurement command should capture cleaner report timings from the verifier miss path",
                failures);
            AssertContains(
                stepCleanerProgram,
                "ModelZInfo" + "Cache.GetOrCreateAsync",
                "measurement command should exercise cached raw OBJ Z info parsing",
                failures);
            AssertContains(
                stepCleanerProgram,
                "StepSilhouette" + "Projection.Generate(cleanedStep",
                "measurement command should project from cleaned STEP bytes instead of reloading a saved file",
                failures);

            string zInfoCacheDirectory = Path.Combine(Path.GetTempPath(), "EasyEDALoaderZInfo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(zInfoCacheDirectory);
            try
            {
                string zInfoCachePath = Path.Combine(zInfoCacheDirectory, "model.zinfo");
                int rawObjLoadCount = 0;
                Func<byte[]> rawObjLoader = () =>
                {
                    rawObjLoadCount++;
                    return Encoding.UTF8.GetBytes(
                        "v 0 0 -1.25" + Environment.NewLine +
                        "v 1 0 2.75" + Environment.NewLine);
                };

                ModelZInfo firstZInfo = ModelZInfoCache.GetOrCreate(zInfoCachePath, rawObjLoader);
                ModelZInfo secondZInfo = ModelZInfoCache.GetOrCreate(
                    zInfoCachePath,
                    () =>
                    {
                        rawObjLoadCount++;
                        return Encoding.UTF8.GetBytes(
                            "v 0 0 -9" + Environment.NewLine +
                            "v 1 0 9" + Environment.NewLine);
                    });

                AssertNear(1.25, firstZInfo.OffsetFromOrigin, 0.00001, "raw OBJ Z info should keep the lowest vertex offset", failures);
                AssertNear(4.0, firstZInfo.Height, 0.00001, "raw OBJ Z info should keep model height", failures);
                AssertNear(firstZInfo.OffsetFromOrigin, secondZInfo.OffsetFromOrigin, 0.00001, "cached Z info should preserve the parsed offset", failures);
                AssertNear(firstZInfo.Height, secondZInfo.Height, 0.00001, "cached Z info should preserve the parsed height", failures);
                AssertEqual("1", rawObjLoadCount.ToString(CultureInfo.InvariantCulture), "cached Z info should avoid reparsing raw OBJ on repeated imports", failures);
            }
            finally
            {
                try
                {
                    Directory.Delete(zInfoCacheDirectory, true);
                }
                catch
                {
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Model cache regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Model cache regression test passed.");
            return 0;
        }

        private static int RunXt60LcedaWatermarkTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("XT60 LCEDA regression test failed.");
                Console.Error.WriteLine("  Missing original fixture: " + inputPath);
                return 1;
            }

            byte[] originalStep = File.ReadAllBytes(inputPath);
            var report = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalStep),
                new StepWatermarkCleanerOptions());
            byte[] cleanedStep = Encoding.Latin1.GetBytes(report.CleanedStep);
            string detectedViews = string.Join(
                ",",
                report.DetectionReport.Regions
                    .Select(region => region.ViewName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase));

            AssertEqual(
                "not equal",
                cleanedStep.SequenceEqual(originalStep) ? "equal" : "not equal",
                "XT60 bottom LCEDA cleanup should change the STEP output",
                failures);
            AssertContains(
                detectedViews,
                "z_minus",
                "XT60 bottom LCEDA detection should report a z_minus cleanup region",
                failures);
            if (report.DetectionReport.HostLoopCount <= 0)
                failures.Add("XT60 bottom LCEDA cleanup should detect host-face watermark loops.");
            string cleanerDiagnostics = string.Join(Environment.NewLine, report.Diagnostics);
            AssertContains(
                cleanerDiagnostics,
                "Automatic host-face watermark loops: 5",
                "XT60 bottom LCEDA cleanup should use the automatic host-loop detector",
                failures);
            AssertContains(
                cleanerDiagnostics,
                "Removed host-face inner loops: 5",
                "XT60 bottom LCEDA cleanup should remove only the five LCEDA inner loops",
                failures);
            int automaticCleanupVolumeCount = GetDiagnosticInt(report.Diagnostics, "Automatic cleanup volumes");
            if (automaticCleanupVolumeCount <= 0)
            {
                failures.Add(
                    "XT60 bottom LCEDA cleanup should use at least one bounded cleanup volume, got " +
                    automaticCleanupVolumeCount.ToString(CultureInfo.InvariantCulture) +
                    ". Diagnostics: " +
                    cleanerDiagnostics);
            }
            AssertContains(
                cleanerDiagnostics,
                "Edited geometry outside cleanup volumes: 0",
                "XT60 bottom LCEDA cleanup must not edit geometry outside its bounded cleanup volume",
                failures);
            var residualTopology = StepWatermarkCleaner.FindResidualCleanupTopology(
                Encoding.Latin1.GetString(originalStep),
                report.CleanedStep,
                report.DetectionReport,
                new StepWatermarkCleanerOptions());
            if (residualTopology.Failures.Count > 0)
            {
                foreach (string failure in residualTopology.Failures)
                    failures.Add("XT60 residual topology: " + failure);
            }
            AssertDoesNotContain(
                report.CleanedStep,
                "#1469 = ADVANCED_FACE",
                "XT60 cleanup should remove coplanar standalone LCEDA residue face #1469",
                failures);
            AssertDoesNotContain(
                report.CleanedStep,
                "#7432 = ADVANCED_FACE",
                "XT60 cleanup should remove coplanar standalone LCEDA residue face #7432",
                failures);

            string verifierDirectory = Path.Combine(dataRoot, "Clean", "XT60Verifier");
            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);
            }
            catch
            {
            }

            bool verifierThrew = false;
            try
            {
                StepWatermarkCleanVerifier.CleanOrThrowWithReport(
                    originalStep,
                    "CONN-TH_XT60PB-M.step",
                    verifierDirectory);
            }
            catch (StepWatermarkCleanFailedException)
            {
                verifierThrew = true;
            }

            if (report.DetectionReport.Regions.Count == 0 && !verifierThrew)
                failures.Add("XT60 verifier silently passed even though automatic detection reported zero cleanup regions.");

            if (report.DetectionReport.Regions.Count > 0 && verifierThrew)
                failures.Add("XT60 verifier failed after automatic detection reported cleanup regions.");

            VerifyXt60OriginalStillFailsResidualTopology(dataRoot, originalStep, report.DetectionReport, failures);
            VerifyNoRegionCleanupFailsWithReport(dataRoot, failures);
            VerifyCandidateOnlyCleanupFailsWithReport(dataRoot, failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("XT60 LCEDA regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("XT60 LCEDA regression test passed.");
            return 0;
        }

        private static void VerifyXt60OriginalStillFailsResidualTopology(
            string dataRoot,
            byte[] originalStep,
            StepWatermarkDetectionReport detectionReport,
            List<string> failures)
        {
            string verifierDirectory = Path.Combine(dataRoot, "Clean", "XT60OriginalVerifier");
            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);
            }
            catch
            {
            }

            var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
                "VerifyPostCleanOutput",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[]
                {
                    typeof(byte[]),
                    typeof(byte[]),
                    typeof(string),
                    typeof(StepWatermarkDetectionReport),
                    typeof(string)
                },
                null);
            if (verifyMethod == null)
            {
                failures.Add("Could not find verifier post-clean method for XT60 residual-topology regression test.");
                return;
            }

            object verification = verifyMethod.Invoke(
                null,
                new object[]
                {
                    originalStep,
                    originalStep,
                    "xt60-original",
                    detectionReport,
                    verifierDirectory
                });
            bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
            string reportPath = (string)verification.GetType().GetProperty("ReportPath").GetValue(verification);
            var verificationFailures = ((IEnumerable<string>)verification.GetType().GetProperty("Failures").GetValue(verification)).ToList();
            if (passed)
                failures.Add("Verifier should fail when XT60 LCEDA residual geometry remains in the cleaned output.");

            if (!verificationFailures.Any(failure =>
                failure.Contains("Residual cleanup face", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("remains on host face", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add("XT60 residual-topology verifier did not report surviving LCEDA topology.");
            }

            if (!File.Exists(reportPath))
            {
                failures.Add("Verifier XT60 residual-topology failure report is missing: " + reportPath);
                return;
            }
        }

        private static void VerifyNoRegionCleanupFailsWithReport(string dataRoot, List<string> failures)
        {
            string verifierDirectory = Path.Combine(dataRoot, "Clean", "NoRegionVerifier");
            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);
            }
            catch
            {
            }

            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            byte[] originalStep = File.ReadAllBytes(inputPath);
            var emptyDetectionReport = new StepWatermarkDetectionReport
            {
                RemovableSolidIds = Array.Empty<int>(),
                EmbeddedFaceIds = Array.Empty<int>(),
                CoplanarFaceIds = Array.Empty<int>(),
                HostLoops = Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = Array.Empty<StepWatermarkRegionDetection>(),
                Diagnostics = Array.Empty<string>()
            };
            var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
                "VerifyPostCleanOutput",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[]
                {
                    typeof(byte[]),
                    typeof(byte[]),
                    typeof(string),
                    typeof(StepWatermarkDetectionReport),
                    typeof(string)
                },
                null);
            if (verifyMethod == null)
            {
                failures.Add("Could not find verifier post-clean method for no-region regression test.");
                return;
            }

            object verification = verifyMethod.Invoke(
                null,
                new object[]
                {
                    originalStep,
                    originalStep,
                    "no-region",
                    emptyDetectionReport,
                    verifierDirectory
                });
            bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
            string reportPath = (string)verification.GetType().GetProperty("ReportPath").GetValue(verification);
            if (passed)
                failures.Add("Verifier should fail when no detected cleanup regions are available.");

            if (!File.Exists(reportPath))
            {
                failures.Add("Verifier no-region failure report is missing: " + reportPath);
                return;
            }

            string reportText = File.ReadAllText(reportPath);
            AssertContains(
                reportText,
                "Projection verification failures without comparison images",
                "Verifier no-region failure report should describe non-visual failures",
                failures);
            AssertDoesNotContain(
                reportText,
                "No failed projections.",
                "Verifier no-region failure report should not claim there were no failed projections",
                failures);
        }

        private static void VerifyCandidateOnlyCleanupFailsWithReport(string dataRoot, List<string> failures)
        {
            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            byte[] originalStep = File.ReadAllBytes(inputPath);
            var candidateOnlyDetectionReport = new StepWatermarkDetectionReport
            {
                RemovableSolidIds = Array.Empty<int>(),
                EmbeddedFaceIds = Array.Empty<int>(),
                CoplanarFaceIds = Array.Empty<int>(),
                HostLoops = Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = new[]
                {
                    new StepWatermarkRegionDetection
                    {
                        EntityId = 1,
                        Kind = "solid-candidate",
                        ViewName = "z_plus"
                    }
                },
                Diagnostics = Array.Empty<string>()
            };

            string verifierDirectory = Path.Combine(dataRoot, "Clean", "CandidateOnlyVerifier");
            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);
            }
            catch
            {
            }

            var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
                "VerifyPostCleanOutput",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[]
                {
                    typeof(byte[]),
                    typeof(byte[]),
                    typeof(string),
                    typeof(StepWatermarkDetectionReport),
                    typeof(string)
                },
                null);
            if (verifyMethod == null)
            {
                failures.Add("Could not find verifier post-clean method for candidate-only regression test.");
                return;
            }

            object verification = verifyMethod.Invoke(
                null,
                new object[]
                {
                    originalStep,
                    originalStep,
                    "candidate-only",
                    candidateOnlyDetectionReport,
                    verifierDirectory
                });
            bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
            var verificationFailures = ((IEnumerable<string>)verification.GetType().GetProperty("Failures").GetValue(verification)).ToList();
            if (passed)
                failures.Add("Verifier should fail when only candidate detection overlays exist and no cleanup geometry was edited.");

            if (!verificationFailures.Any(failure =>
                failure.Contains("no verified cleanup regions", StringComparison.OrdinalIgnoreCase) ||
                failure.Contains("cannot prove", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add("Candidate-only verifier failure should explain that no verified cleanup regions were available.");
            }
        }

        private static int RunRemovedGeometryExportTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "BUZ-SMD_4P-L7.5-W7.5-H2.5.step");
            string outputDirectory = Path.Combine(dataRoot, "RemovedGeometry");
            string outputPath = Path.Combine(outputDirectory, "BUZ-SMD_4P-L7.5-W7.5-H2.5.removed.step");
            string cleanPath = Path.Combine(dataRoot, "Clean", "BUZ-SMD_4P-L7.5-W7.5-H2.5.removed-test-clean.step");
            string automaticRemovedPath = Path.Combine(dataRoot, "RemovedGeometry", "BUZ-SMD_4P-L7.5-W7.5-H2.5.removed-test-clean.removed.step");
            string cleanProjectionDirectory = Path.Combine(dataRoot, "RemovedGeometryProjection");
            string verifierDirectory = Path.Combine(dataRoot, "Clean", "BUZSideLogoVerifier");

            Directory.CreateDirectory(outputDirectory);
            byte[] originalStep = File.ReadAllBytes(inputPath);
            var report = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalStep),
                new StepWatermarkCleanerOptions());
            if (string.IsNullOrWhiteSpace(report.RemovedGeometryStep))
            {
                failures.Add("Removed-geometry export should produce a non-empty diagnostic STEP file.");
            }
            else
            {
                File.WriteAllBytes(outputPath, Encoding.Latin1.GetBytes(report.RemovedGeometryStep));
                if (!File.Exists(outputPath))
                    failures.Add("Removed-geometry STEP file was not written: " + outputPath);
                else if (new FileInfo(outputPath).Length <= 0)
                    failures.Add("Removed-geometry STEP file is empty: " + outputPath);
            }

            File.WriteAllBytes(cleanPath, Encoding.Latin1.GetBytes(report.CleanedStep));
            if (File.Exists(automaticRemovedPath))
                File.Delete(automaticRemovedPath);

            RunStepCleanerCleanup(inputPath, cleanPath, failures);

            if (!File.Exists(automaticRemovedPath))
            {
                failures.Add("Normal cleanup should write a removed-geometry STEP next to the generated clean output: " + automaticRemovedPath);
            }
            else if (new FileInfo(automaticRemovedPath).Length <= 0)
            {
                failures.Add("Normal cleanup removed-geometry STEP is empty: " + automaticRemovedPath);
            }

            var projectionOptions = CreateVerificationProjectionOptions();
            projectionOptions.ViewNames.Clear();
            projectionOptions.ViewNames.Add("x_plus");
            StepProjectionRenderer.ProjectFile(cleanPath, cleanProjectionDirectory, projectionOptions);
            string cleanProjectionPath = Path.Combine(
                cleanProjectionDirectory,
                Path.GetFileNameWithoutExtension(cleanPath) + "__x_plus.png");
            var sideRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    originalStep,
                    Path.GetFileName(inputPath),
                    report.DetectionReport,
                    projectionOptions)
                .Where(region => string.Equals(region.ViewName, "x_plus", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(region => region.RectangleWidth * region.RectangleHeight)
                .ToList();
            if (sideRegions.Count == 0)
            {
                failures.Add("BUZ side logo detection should produce an x_plus cleanup region.");
            }
            else
            {
                using (var cleanProjection = SKBitmap.Decode(cleanProjectionPath))
                {
                    int brightPixels = sideRegions.Sum(region => CountBrightPixels(cleanProjection, region, threshold: 115));
                    brightPixels += CountBrightPixels(cleanProjection, 250, 380, 430, 520, threshold: 115);
                    double logoEdgeRatio = MeasureRegionEdgeRatio(cleanProjection, 250, 380, 430, 520);
                    if (brightPixels > 50)
                    {
                        failures.Add(
                            "BUZ side logo cleanup should remove bright logo pixels from x_plus; remaining bright pixels=" +
                            brightPixels.ToString(CultureInfo.InvariantCulture) +
                            ".");
                    }

                    if (logoEdgeRatio > 0.002)
                    {
                        failures.Add(
                            "BUZ side logo cleanup should flatten logo outline geometry on x_plus; edge ratio=" +
                            logoEdgeRatio.ToString("G4", CultureInfo.InvariantCulture) +
                            ".");
                    }
                }
            }

            try
            {
                if (Directory.Exists(verifierDirectory))
                    Directory.Delete(verifierDirectory, true);

                StepWatermarkCleanVerifier.CleanOrThrowWithReport(
                    originalStep,
                    Path.GetFileName(inputPath),
                    verifierDirectory);
            }
            catch (StepWatermarkCleanFailedException ex)
            {
                failures.Add("BUZ side logo cleanup should pass post-clean verification, but failed: " + string.Join("; ", ex.Failures));
            }

            VerifyRemovedGeometryDoesNotExportLargeNonWatermarkModels(dataRoot, failures);
            VerifyTextLogoPromotedRemovedGeometryIsNotEmpty(dataRoot, failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Removed-geometry export test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Removed-geometry export test passed.");
            Console.WriteLine("Removed geometry: " + outputPath);
            return 0;
        }

        private static int RunTextLogoCleanupPromotionTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var fixtureNames = new[]
            {
                "CONN-TH_MR30PW-M30-G-Y.step",
                "USB-A-TH_FUS264-FDSW3K.step",
                "USB-B-TH_USB-B10-BRW.step",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "CONN-TH_XT60PB-M.step"
            };

            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(originalDirectory, fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing text/logo cleanup promotion fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                var verifiedReport = StepWatermarkCleaner.CreateVerifiedCleanupDetectionReport(report.DetectionReport);
                int templateTextLogoDetectionCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo detections");
                int templateTextLogoCandidateCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo cleanup candidates");
                int templateTextLogoCleanupRegionCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo cleanup regions");

                if (verifiedReport.Regions.Count <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should promote topology-confirmed text/logo projection detections to verified cleanup regions. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (templateTextLogoDetectionCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should run runtime template text/logo projection detection during normal cleanup. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (templateTextLogoCandidateCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should find topology-confirmed text/logo cleanup candidates. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (templateTextLogoCleanupRegionCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should promote template text/logo detections to cleanup regions, got " +
                        templateTextLogoCleanupRegionCount.ToString(CultureInfo.InvariantCulture) +
                        ". Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (BytesEqual(originalStep, Encoding.Latin1.GetBytes(report.CleanedStep)))
                {
                    failures.Add(
                        fixtureName +
                        " should not silently pass cleanup as a no-op after text/logo promotion. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }

                if (report.FlattenedFaceCount <= 0 && report.FlattenedPointCount <= 0 && report.RemovedSolidCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should report actual cleanup edits after text/logo promotion. Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo cleanup promotion test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo cleanup promotion test passed.");
            return 0;
        }

        private static int RunTextLogoNegativeClassifierTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string fixtureName = "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step";
            string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing text/logo negative classifier fixture: " + inputPath);
            }
            else
            {
                string originalStep = Encoding.Latin1.GetString(File.ReadAllBytes(inputPath));
                var report = StepWatermarkCleaner.CleanWithReport(
                    originalStep,
                    new StepWatermarkCleanerOptions());
                string diagnostics = string.Join(" | ", report.Diagnostics ?? Array.Empty<string>());
                var originalEntities = ParseStepEntityDefinitions(originalStep);
                var cleanedEntities = ParseStepEntityDefinitions(report.CleanedStep);
                var removedEntities = ParseStepEntityDefinitions(report.RemovedGeometryStep ?? string.Empty);
                int[] protectedFaceIds = { 14214, 26383, 34754 };

                foreach (int protectedFaceId in protectedFaceIds)
                {
                    string protectedFace = "#" + protectedFaceId.ToString(CultureInfo.InvariantCulture) + " = ADVANCED_FACE";
                    AssertContains(
                        report.CleanedStep,
                        protectedFace,
                        "CONN-SMD text/logo negative classifier should preserve protected contact face " + protectedFace,
                        failures);
                    AssertDoesNotContain(
                        report.RemovedGeometryStep ?? string.Empty,
                        protectedFace,
                        "CONN-SMD text/logo negative classifier should not export protected contact face " + protectedFace + " as removed geometry",
                        failures);
                    VerifyProtectedFaceEntityClosurePreserved(
                        protectedFaceId,
                        originalEntities,
                        cleanedEntities,
                        removedEntities,
                        failures);
                }

                int templateTextLogoFaceCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo faces");
                int templateTextLogoProtectedRejectCount = GetDiagnosticInt(report.Diagnostics, "Template text/logo protected rejects");
                if (templateTextLogoProtectedRejectCount <= 0)
                {
                    failures.Add(
                        fixtureName +
                        " should explicitly reject protected contact geometry during template text/logo promotion. Diagnostics: " +
                        diagnostics);
                }

                if (templateTextLogoFaceCount != 0)
                {
                    failures.Add(
                        fixtureName +
                        " should not classify protected contact faces as template text/logo faces, got " +
                        templateTextLogoFaceCount.ToString(CultureInfo.InvariantCulture) +
                        " face(s). Diagnostics: " +
                        diagnostics);
                }

                if (report.DetectionReport.Regions.Any(region =>
                    region.EntityId == 14214 ||
                    region.EntityId == 26383 ||
                    region.EntityId == 34754))
                {
                    failures.Add(
                        fixtureName +
                        " detection regions should not reference protected contact faces #14214, #26383, or #34754. Diagnostics: " +
                        diagnostics);
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo negative classifier test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo negative classifier test passed.");
            return 0;
        }

        private static int RunTextLogoVerifierTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string fixtureName = "USB-B-TH_USB-B10-BRW.step";
            string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing text/logo verifier fixture: " + inputPath);
            }
            else
            {
                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());

                string verifierDirectory = Path.Combine(dataRoot, "Clean", "TextLogoVerifierNoOp");
                try
                {
                    if (Directory.Exists(verifierDirectory))
                        Directory.Delete(verifierDirectory, true);
                }
                catch
                {
                }

                var verifyMethod = typeof(StepWatermarkCleanVerifier).GetMethod(
                    "VerifyPostCleanOutput",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null,
                    new[]
                    {
                        typeof(byte[]),
                        typeof(byte[]),
                        typeof(string),
                        typeof(StepWatermarkDetectionReport),
                        typeof(string)
                    },
                    null);
                if (verifyMethod == null)
                {
                    failures.Add("Could not find verifier post-clean method for text/logo verifier regression test.");
                }
                else
                {
                    object verification = verifyMethod.Invoke(
                        null,
                        new object[]
                        {
                            originalStep,
                            originalStep,
                            "usb-b-noop",
                            report.DetectionReport,
                            verifierDirectory
                        });
                    bool passed = (bool)verification.GetType().GetProperty("Passed").GetValue(verification);
                    string reportPath = (string)verification.GetType().GetProperty("ReportPath").GetValue(verification);
                    var verificationFailures = ((IEnumerable<string>)verification.GetType().GetProperty("Failures").GetValue(verification)).ToList();

                    if (passed)
                        failures.Add("Verifier should fail when original STEP is supplied as clean output for a detected text/logo watermark.");

                    if (!verificationFailures.Any(failure =>
                        failure.Contains("retains known watermark visual template", StringComparison.OrdinalIgnoreCase) ||
                        failure.Contains("retains text/logo edge detail", StringComparison.OrdinalIgnoreCase)))
                    {
                        failures.Add("Text/logo verifier failure should report retained visual template or edge detail inside the detected cleanup region.");
                    }

                    if (!File.Exists(reportPath))
                    {
                        failures.Add("Text/logo verifier failure report is missing: " + reportPath);
                    }
                    else
                    {
                        string reportText = File.ReadAllText(reportPath);
                        AssertContains(
                            reportText,
                            "Projection verification failures without comparison images",
                            "Text/logo verifier report should include non-visual failures.",
                            failures);
                    }
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo verifier test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo verifier test passed.");
            return 0;
        }

        private static int RunTextLogoVisualResidualTests(string[] args)
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            List<string> fixtureNames = new[]
            {
                "USB-B-TH_USB-B10-BRW.step",
                "USB-A-TH_FUS264-FDSW3K.step",
                "CONN-TH_MR30PW-M30-G-Y.step",
                "CONN-SMD_DF56_40S_0.3V_51.step",
                "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "CONN-TH_XT60PB-M.step"
            }.ToList();

            if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            {
                string filter = args[1];
                fixtureNames = fixtureNames
                    .Where(fixtureName =>
                        string.Equals(fixtureName, filter, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(fixtureName), filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (fixtureNames.Count == 0)
                {
                    Console.Error.WriteLine("Text/logo visual residual test fixture was not found: " + filter);
                    return 2;
                }
            }

            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(originalDirectory, fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing text/logo visual residual fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                byte[] cleanedStep = Encoding.Latin1.GetBytes(report.CleanedStep);
                StepWatermarkVisualResidualResult visual = StepWatermarkVisualOracle.VerifyKnownWatermarkRemoved(
                    originalStep,
                    cleanedStep,
                    fixtureName);

                foreach (string failure in visual.Failures)
                    failures.Add(failure + " Diagnostics: " + string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo visual residual test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Text/logo visual residual test passed.");
            return 0;
        }

        private static int RunRemovedGeometryRoiLocalityTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            var fixtureNames = new[]
            {
                "USB-B-TH_USB-B10-BRW.step",
                "CONN-TH_MR30PW-M30-G-Y.step"
            };

            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(originalDirectory, fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing removed-geometry ROI locality fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                VerifyRemovedGeometryFacesStayInsideVisualRois(
                    fixtureName,
                    originalStep,
                    report,
                    failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Removed-geometry ROI locality test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Removed-geometry ROI locality test passed.");
            return 0;
        }

        private static void VerifyRemovedGeometryFacesStayInsideVisualRois(
            string fixtureName,
            byte[] originalStep,
            StepWatermarkCleanerReport report,
            List<string> failures)
        {
            StepWatermarkVisualScanResult originalVisual = StepWatermarkVisualOracle.DetectKnownWatermarks(
                originalStep,
                fixtureName + ".original");
            if (originalVisual.Detections.Count == 0)
            {
                failures.Add(fixtureName + " original model has no visual watermark ROI detections for removed-geometry locality.");
                return;
            }

            if (string.IsNullOrWhiteSpace(report.RemovedGeometryStep))
            {
                failures.Add(fixtureName + " should export removed geometry for detected text/logo watermark cleanup.");
                return;
            }

            List<int> removedFaceIds = GetActiveAdvancedFaceIds(report.RemovedGeometryStep).ToList();
            if (removedFaceIds.Count == 0)
            {
                failures.Add(fixtureName + " removed geometry contains no ADVANCED_FACE entities.");
                return;
            }

            var removedFaceReport = new StepWatermarkDetectionReport
            {
                RemovableSolidIds = Array.Empty<int>(),
                EmbeddedFaceIds = removedFaceIds,
                CoplanarFaceIds = Array.Empty<int>(),
                HostLoops = Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = removedFaceIds
                    .SelectMany(faceId => StepProjectionRenderer.ViewNames.Select(viewName => new StepWatermarkRegionDetection
                    {
                        EntityId = faceId,
                        Kind = "face",
                        ViewName = viewName
                    }))
                    .ToList(),
                Diagnostics = Array.Empty<string>()
            };
            IReadOnlyList<StepProjectionDetectionRegion> removedFaceRegions = StepProjectionRenderer.ProjectDetectionRegions(
                originalStep,
                fixtureName,
                removedFaceReport,
                StepWatermarkVisualOracle.CreateProjectionOptions(StepProjectionRenderMode.Color));
            var projectedFaceIds = new HashSet<int>(removedFaceRegions.Select(region => region.EntityId));
            foreach (int faceId in removedFaceIds)
            {
                if (!projectedFaceIds.Contains(faceId))
                    failures.Add(fixtureName + " removed face #" + faceId.ToString(CultureInfo.InvariantCulture) + " could not be projected onto the original model.");
            }

            foreach (var faceGroup in removedFaceRegions.GroupBy(region => region.EntityId))
            {
                bool overlapsWatermark = faceGroup.Any(removedRegion =>
                    originalVisual.Detections.Any(detection =>
                        string.Equals(detection.ViewName, removedRegion.ViewName, StringComparison.OrdinalIgnoreCase) &&
                        RectanglesIntersect(
                            removedRegion.RectangleX,
                            removedRegion.RectangleY,
                            removedRegion.RectangleWidth,
                            removedRegion.RectangleHeight,
                            detection.X,
                            detection.Y,
                            detection.Width,
                            detection.Height,
                            padding: 36)));
                if (!overlapsWatermark)
                {
                    string projectedRegions = string.Join(
                        "; ",
                        faceGroup.Select(region =>
                            region.ViewName +
                            " " +
                            region.RectangleX.ToString(CultureInfo.InvariantCulture) +
                            "," +
                            region.RectangleY.ToString(CultureInfo.InvariantCulture) +
                            " " +
                            region.RectangleWidth.ToString(CultureInfo.InvariantCulture) +
                            "x" +
                            region.RectangleHeight.ToString(CultureInfo.InvariantCulture)));
                    failures.Add(
                        fixtureName +
                        " removed face #" +
                        faceGroup.Key.ToString(CultureInfo.InvariantCulture) +
                        " projects outside all known watermark visual ROIs. Face projections: " +
                        projectedRegions +
                        ". Visual ROIs: " +
                        string.Join("; ", originalVisual.Detections.Select(detection => detection.Describe())) +
                        ". Diagnostics: " +
                        string.Join(" | ", report.Diagnostics ?? Array.Empty<string>()));
                }
            }
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

        private static void VerifyRemovedGeometryDoesNotExportLargeNonWatermarkModels(string dataRoot, List<string> failures)
        {
            var fixtureNames = new[]
            {
                "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step",
                "HDMI-SMD_HDMI-001S.step",
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "TYPE-C-TH_TYPEC-215-ARP14.step",
                "USB-A-SMD_USB-212-BCW.step"
            };

            const int maxRemovedGeometryBytes = 1700000;
            foreach (string fixtureName in fixtureNames)
            {
                string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing removed-geometry over-removal fixture: " + inputPath);
                    continue;
                }

                byte[] originalStep = File.ReadAllBytes(inputPath);
                var report = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions());
                if (report.DetectionReport.RemovableSolidCount != 0)
                {
                    failures.Add(
                        Path.GetFileNameWithoutExtension(fixtureName) +
                        " should not classify component solids as removable watermark solids; detected " +
                        report.DetectionReport.RemovableSolidCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int removedByteCount = string.IsNullOrEmpty(report.RemovedGeometryStep)
                    ? 0
                    : Encoding.Latin1.GetByteCount(report.RemovedGeometryStep);

                if (string.Equals(fixtureName, "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step", StringComparison.OrdinalIgnoreCase))
                {
                    AssertContains(
                        report.CleanedStep,
                        "#14214 = ADVANCED_FACE",
                        "CONN-SMD cleanup should preserve original gold contact face #14214",
                        failures);
                    AssertContains(
                        report.CleanedStep,
                        "#26383 = ADVANCED_FACE",
                        "CONN-SMD cleanup should preserve original gold contact face #26383",
                        failures);
                    AssertContains(
                        report.CleanedStep,
                        "#34754 = ADVANCED_FACE",
                        "CONN-SMD cleanup should preserve original gold contact face #34754",
                        failures);
                    AssertDoesNotContain(
                        report.RemovedGeometryStep ?? string.Empty,
                        "#14214 = ADVANCED_FACE",
                        "CONN-SMD removed geometry should not contain original gold contact face #14214",
                        failures);
                    AssertDoesNotContain(
                        report.RemovedGeometryStep ?? string.Empty,
                        "#26383 = ADVANCED_FACE",
                        "CONN-SMD removed geometry should not contain original gold contact face #26383",
                        failures);
                    AssertDoesNotContain(
                        report.RemovedGeometryStep ?? string.Empty,
                        "#34754 = ADVANCED_FACE",
                        "CONN-SMD removed geometry should not contain original gold contact face #34754",
                        failures);
                }

                if (removedByteCount > maxRemovedGeometryBytes)
                {
                    failures.Add(
                        Path.GetFileNameWithoutExtension(fixtureName) +
                        " removed-geometry export is too large for a watermark-only diagnostic: " +
                        removedByteCount.ToString(CultureInfo.InvariantCulture) +
                        " bytes.");
                }
            }
        }

        private static void VerifyTextLogoPromotedRemovedGeometryIsNotEmpty(string dataRoot, List<string> failures)
        {
            string fixtureName = "CONN-TH_MR30PW-M30-G-Y.step";
            string inputPath = Path.Combine(dataRoot, "Original", fixtureName);
            if (!File.Exists(inputPath))
            {
                failures.Add("Missing text/logo removed-geometry fixture: " + inputPath);
                return;
            }

            byte[] originalStep = File.ReadAllBytes(inputPath);
            var report = StepWatermarkCleaner.CleanWithReport(
                Encoding.Latin1.GetString(originalStep),
                new StepWatermarkCleanerOptions());
            if (report.FlattenedFaceCount <= 0 && report.FlattenedPointCount <= 0 && report.RemovedSolidCount <= 0)
            {
                failures.Add(fixtureName + " should perform text/logo cleanup before removed-geometry export.");
                return;
            }

            if (string.IsNullOrEmpty(report.RemovedGeometryStep))
                failures.Add(fixtureName + " should export non-empty removed geometry for promoted text/logo cleanup.");
        }

        private static int CountBrightPixels(SKBitmap image, StepProjectionDetectionRegion region, byte threshold)
        {
            return CountBrightPixels(
                image,
                region.RectangleX,
                region.RectangleY,
                region.RectangleX + region.RectangleWidth - 1,
                region.RectangleY + region.RectangleHeight - 1,
                threshold);
        }

        private static int CountBrightPixels(SKBitmap image, int rectangleLeft, int rectangleTop, int rectangleRight, int rectangleBottom, byte threshold)
        {
            int left = Math.Max(0, rectangleLeft);
            int top = Math.Max(0, rectangleTop);
            int right = Math.Min(image.Width - 1, rectangleRight);
            int bottom = Math.Min(image.Height - 1, rectangleBottom);
            int count = 0;
            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    SKColor color = image.GetPixel(x, y);
                    if (color.Red >= threshold && color.Green >= threshold && color.Blue >= threshold)
                        count++;
                }
            }

            return count;
        }

        private static void RunStepCleanerCleanup(string inputPath, string outputPath, List<string> failures)
        {
            string repoRoot = FindRepoRoot();
            var processStart = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            processStart.ArgumentList.Add("run");
            processStart.ArgumentList.Add("--project");
            processStart.ArgumentList.Add(Path.Combine(repoRoot, "StepCleaner", "StepCleaner.csproj"));
            processStart.ArgumentList.Add("--");
            processStart.ArgumentList.Add(inputPath);
            processStart.ArgumentList.Add(outputPath);

            using (Process process = Process.Start(processStart))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    failures.Add(
                        "Normal StepCleaner cleanup failed with exit code " +
                        process.ExitCode.ToString(CultureInfo.InvariantCulture) +
                        ". stdout=" +
                        stdout +
                        " stderr=" +
                        stderr);
                }
            }
        }

        private static int RunCleanTextTests()
        {
            var failures = new List<string>();
            var visualFailures = new List<ProjectionVisualFailure>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string cachedOriginalProjectionDirectory = Path.Combine(dataRoot, "OriginalCleanCompareProjection");
            string cleanTextDirectory = Path.Combine(dataRoot, "CleanText");
            string cleanTextProjectionDirectory = Path.Combine(dataRoot, "CleanTextProjection");
            var originalFiles = GetStepFiles(originalDirectory);
            var expectedTextCleanFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                "USB-B-TH_USB-B10-BRW.step"
            };

            if (originalFiles.Count == 0)
                failures.Add("No STEP files were found in Original.");

            foreach (string originalFile in originalFiles)
            {
                byte[] originalStep = File.ReadAllBytes(originalFile);
                byte[] watermarkOnly = StepWatermarkCleaner.Clean(
                    originalStep,
                    new StepWatermarkCleanerOptions());
                var textCleanReport = StepWatermarkCleaner.CleanWithReport(
                    Encoding.Latin1.GetString(originalStep),
                    new StepWatermarkCleanerOptions
                    {
                        CleanText = true
                    });
                byte[] textCleaned = Encoding.Latin1.GetBytes(textCleanReport.CleanedStep);

                bool changedByTextCleaning = !BytesEqual(watermarkOnly, textCleaned);
                bool shouldChange = expectedTextCleanFileNames.Contains(Path.GetFileName(originalFile));

                if (changedByTextCleaning != shouldChange)
                {
                    failures.Add(
                        Path.GetFileName(originalFile) +
                        (shouldChange
                            ? " should be additionally cleaned by CleanText."
                            : " should not be changed by CleanText.") +
                        " Diagnostics: " +
                        string.Join(" | ", textCleanReport.Diagnostics ?? Array.Empty<string>()));
                }

                if (shouldChange && changedByTextCleaning)
                {
                    int templateTextDetectionCount = GetDiagnosticInt(textCleanReport.Diagnostics, "Template text detections");
                    int templateTextCandidateCount = GetDiagnosticInt(textCleanReport.Diagnostics, "Template text cleanup candidates");
                    int templateTextFaceCount = GetDiagnosticInt(textCleanReport.Diagnostics, "Template text faces");
                    if (templateTextDetectionCount <= 0 || templateTextCandidateCount <= 0)
                    {
                        failures.Add(
                            Path.GetFileName(originalFile) +
                            " CleanText should report template-backed detections and cleanup candidates, got detections=" +
                            templateTextDetectionCount.ToString(CultureInfo.InvariantCulture) +
                            ", candidates=" +
                            templateTextCandidateCount.ToString(CultureInfo.InvariantCulture) +
                            ". Diagnostics: " +
                            string.Join(" | ", textCleanReport.Diagnostics ?? Array.Empty<string>()));
                    }

                    if (templateTextFaceCount <= 0)
                    {
                        failures.Add(
                            Path.GetFileName(originalFile) +
                            " CleanText should report accepted template-backed text faces, got " +
                            templateTextFaceCount.ToString(CultureInfo.InvariantCulture) +
                            ". Diagnostics: " +
                            string.Join(" | ", textCleanReport.Diagnostics ?? Array.Empty<string>()));
                    }

                    Directory.CreateDirectory(cleanTextDirectory);
                    string textCleanOutputPath = Path.Combine(cleanTextDirectory, Path.GetFileName(originalFile));
                    File.WriteAllBytes(textCleanOutputPath, textCleaned);
                    VerifyCleanTextPostProcessProjectionUsesCachedOriginal(
                        originalFile,
                        textCleanOutputPath,
                        textCleanReport.DetectionReport,
                        cachedOriginalProjectionDirectory,
                        cleanTextProjectionDirectory,
                        failures,
                        visualFailures);
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Clean text regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Clean text regression test passed.");
            return 0;
        }

        private static int GetDiagnosticInt(IReadOnlyList<string> diagnostics, string label)
        {
            if (diagnostics == null)
                return 0;

            string prefix = label + ":";
            foreach (string diagnostic in diagnostics)
            {
                if (string.IsNullOrWhiteSpace(diagnostic) ||
                    !diagnostic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = diagnostic.Substring(prefix.Length).Trim();
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                    return result;
            }

            return 0;
        }

        private static int RunModelImportMeasurement(string[] args)
        {
            string partNumber = DefaultMeasurePartNumber;
            int repeatCount = 2;
            bool cleanText = false;

            int index = 1;
            if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                partNumber = args[index];
                index++;
            }

            for (; index < args.Length; index++)
            {
                string option = args[index];
                if (IsOption(option, "--repeat"))
                {
                    if (!TryReadIntOption(args, ref index, option, out repeatCount))
                        return 2;
                    continue;
                }

                if (IsOption(option, "--clean-text"))
                {
                    cleanText = true;
                    continue;
                }

                Console.Error.WriteLine("Unknown measurement option: " + option);
                return 2;
            }

            if (string.IsNullOrWhiteSpace(partNumber))
            {
                Console.Error.WriteLine("Part number is required.");
                return 2;
            }

            if (repeatCount < 1)
            {
                Console.Error.WriteLine("--repeat must be at least 1.");
                return 2;
            }

            try
            {
                return RunModelImportMeasurementAsync(partNumber, repeatCount, cleanText)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Model import measurement failed: " + ex.Message);
                return 1;
            }
        }

        private static int RunF3DBufferSmoke(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-buffer-smoke <input.step>");
                return 1;
            }

            string inputPath = args[1];
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file was not found: " + inputPath);
                return 2;
            }

            try
            {
                var options = new StepProjectionOptions
                {
                    ImageSizePixels = 256,
                    PaddingPixels = 16,
                    WriteMetadata = false,
                    SkipGeometryModelForExternalRender = true
                };
                options.ViewNames.Add("x_plus");

                IReadOnlyList<StepProjectionImage> images = StepProjectionRenderer.ProjectFileImages(
                    File.ReadAllBytes(inputPath),
                    Path.GetFileNameWithoutExtension(inputPath),
                    options);
                if (images.Count != 1)
                {
                    Console.Error.WriteLine("Expected one rendered image, got " + images.Count.ToString(CultureInfo.InvariantCulture) + ".");
                    return 3;
                }

                StepProjectionImage image = images[0];
                Console.WriteLine("f3d_buffer_smoke=PASS");
                Console.WriteLine("view=" + image.ViewName);
                Console.WriteLine("size=" + image.Width.ToString(CultureInfo.InvariantCulture) + "x" + image.Height.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("raw_rgba_bytes=" + image.RgbaBytes.Length.ToString(CultureInfo.InvariantCulture));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("F3D buffer smoke failed: " + ex.Message);
                return 4;
            }
        }

        private static int RunF3DPreviewSmoke(string[] args)
        {
            if (args.Length < 2 || args.Length > 4)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --f3d-preview-smoke <input.step> [output.png] [--cross-thread]");
                return 1;
            }

            string inputPath = args[1];
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("Input STEP file was not found: " + inputPath);
                return 2;
            }

            try
            {
                byte[] stepData = File.ReadAllBytes(inputPath);
                bool crossThread = args.Any(arg => IsOption(arg, "--cross-thread"));
                string outputPath = args
                    .Skip(2)
                    .FirstOrDefault(arg => !IsOption(arg, "--cross-thread"));
                F3DRenderedImage image = crossThread
                    ? RenderPreviewSmokeCrossThread(stepData)
                    : RenderPreviewSmokeSameThread(stepData);
                if (image == null)
                {
                    Console.Error.WriteLine("Preview render did not return an image.");
                    return 1;
                }

                byte[] rgba = ConvertRawF3DImageToRgba(image);
                int nonWhitePixels = CountNonWhitePixels(rgba);
                Console.WriteLine("f3d_preview_smoke=" + (nonWhitePixels > 0 ? "PASS" : "FAIL"));
                Console.WriteLine("cross_thread=" + crossThread.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("size=" + image.Width.ToString(CultureInfo.InvariantCulture) + "x" + image.Height.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("channels=" + image.ChannelCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("channel_type=" + image.ChannelType.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("channel_type_size=" + image.ChannelTypeSize.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("non_white_pixels=" + nonWhitePixels.ToString(CultureInfo.InvariantCulture));

                if (!string.IsNullOrWhiteSpace(outputPath))
                    SaveRgbaPng(rgba, image.Width, image.Height, outputPath);

                return nonWhitePixels > 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("F3D preview smoke failed: " + ex.Message);
                return 1;
            }
        }

        private static int RunProjectionEdgeModeTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            byte[] stepData = File.ReadAllBytes(inputPath);

            var colorOptions = new StepProjectionOptions
            {
                ImageSizePixels = 512,
                PaddingPixels = 32,
                RenderMode = StepProjectionRenderMode.Color
            };
            colorOptions.ViewNames.Add("z_minus");

            var edgeOptions = new StepProjectionOptions
            {
                ImageSizePixels = colorOptions.ImageSizePixels,
                PaddingPixels = colorOptions.PaddingPixels,
                RenderMode = StepProjectionRenderMode.Edge
            };
            edgeOptions.ViewNames.Add("z_minus");

            StepProjectionImage colorImage = StepProjectionRenderer.ProjectFileImages(
                stepData,
                Path.GetFileNameWithoutExtension(inputPath),
                colorOptions)[0];
            StepProjectionImage edgeImage = StepProjectionRenderer.ProjectFileImages(
                stepData,
                Path.GetFileNameWithoutExtension(inputPath),
                edgeOptions)[0];

            if (colorImage.Width != edgeImage.Width || colorImage.Height != edgeImage.Height)
            {
                failures.Add(
                    "Edge projection should use the same dimensions as color projection: color=" +
                    colorImage.Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    colorImage.Height.ToString(CultureInfo.InvariantCulture) +
                    ", edge=" +
                    edgeImage.Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    edgeImage.Height.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            const int minDarkPixels = 32;
            int darkPixels = CountDarkPixels(edgeImage.RgbaBytes, threshold: 48);
            if (darkPixels < minDarkPixels)
            {
                failures.Add(
                    "Edge projection should contain dark edge pixels in the z_minus view: expected at least " +
                    minDarkPixels.ToString(CultureInfo.InvariantCulture) +
                    ", got " +
                    darkPixels.ToString(CultureInfo.InvariantCulture) +
                    ".");
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Projection edge-mode test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Projection edge-mode test passed.");
            Console.WriteLine("edge_dark_pixels=" + darkPixels.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("projection_size=" + edgeImage.Width.ToString(CultureInfo.InvariantCulture) + "x" + edgeImage.Height.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunRawSilhouetteEdgeProjectionTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string projectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
            string silhouetteProjection = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepSilhouetteProjection.cs"));
            if (!projectionRenderer.Contains("StepSilhouetteProjection.GenerateViews", StringComparison.Ordinal))
                failures.Add("StepProjectionRenderer edge mode should use optimized OCCT silhouette views.");
            if (projectionRenderer.Contains("StepSilhouetteProjection.GenerateRawViews", StringComparison.Ordinal))
                failures.Add("StepProjectionRenderer edge mode must not use raw OCCT silhouette views.");
            if (!silhouetteProjection.Contains("GenerateViews(", StringComparison.Ordinal))
                failures.Add("StepSilhouetteProjection should expose optimized byte-array view generation.");

            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "CONN-TH_XT60PB-M.step");
            byte[] stepData = File.ReadAllBytes(inputPath);
            StepProjectionImage edgeImage = ProjectSingleTestView(
                stepData,
                "CONN-TH_XT60PB-M.optimized-edge",
                "z_minus",
                StepProjectionRenderMode.Edge);

            int darkPixels = CountDarkPixels(edgeImage.RgbaBytes, 48);
            if (darkPixels < 1000)
                failures.Add("Optimized silhouette edge image should contain visible HLR edge pixels, got " + darkPixels.ToString(CultureInfo.InvariantCulture) + ".");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Optimized silhouette edge projection test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Optimized silhouette edge projection test passed.");
            Console.WriteLine("edge_dark_pixels=" + darkPixels.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("projection_size=" + edgeImage.Width.ToString(CultureInfo.InvariantCulture) + "x" + edgeImage.Height.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunVisibleRawSilhouetteEdgeProjectionTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string projectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
            if (!projectionRenderer.Contains("RenderVisibleRawEdgeProjectionImage", StringComparison.Ordinal))
                failures.Add("StepProjectionRenderer should expose visible raw depth-tested edge projection.");
            if (!projectionRenderer.Contains("StepProjectionRenderMode.EdgeVisibleRaw", StringComparison.Ordinal))
                failures.Add("StepProjectionRenderer should expose the EdgeVisibleRaw render mode.");
            if (!projectionRenderer.Contains("DrawLoop(image, loop.Points, transform, view, depthPlane, zBuffer", StringComparison.Ordinal))
                failures.Add("Visible raw edge projection should draw raw loops through the depth-tested path.");

            string dataRoot = FindDataRoot();
            string inputPath = Path.Combine(dataRoot, "Original", "USB-B-TH_USB-B10-BRW.step");
            string markedPath = Path.Combine(dataRoot, "Marked", "USB-B-TH_USB-B10-BRW__x_plus.json");
            byte[] stepData = File.ReadAllBytes(inputPath);
            StepProjectionImage edgeImage = ProjectSingleTestView(
                stepData,
                "USB-B-TH_USB-B10-BRW.visible-raw-edge",
                "x_plus",
                StepProjectionRenderMode.EdgeVisibleRaw);

            MarkedRectI marked = ReadMarkedRectangles(markedPath).First();
            int markedDarkPixels = CountDarkPixels(edgeImage, marked, 48);
            if (markedDarkPixels < 250)
            {
                failures.Add(
                    "Visible raw silhouette edge image should preserve watermark strokes inside marked region, got " +
                    markedDarkPixels.ToString(CultureInfo.InvariantCulture) +
                    " dark pixels.");
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Visible raw silhouette edge projection test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Visible raw silhouette edge projection test passed.");
            Console.WriteLine("marked_dark_pixels=" + markedDarkPixels.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("projection_size=" + edgeImage.Width.ToString(CultureInfo.InvariantCulture) + "x" + edgeImage.Height.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int SaveEdgePreview(string[] args)
        {
            if (args.Length < 4 || args.Length > 5)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --edge-preview <input.step> <view> <output.png> [--visible-raw]");
                return 2;
            }

            string inputPath = args[1];
            string viewName = args[2];
            string outputPath = args[3];
            StepProjectionRenderMode renderMode = args.Length == 5 && IsOption(args[4], "--visible-raw")
                ? StepProjectionRenderMode.EdgeVisibleRaw
                : StepProjectionRenderMode.Edge;
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + inputPath);
                return 2;
            }

            StepProjectionImage edgeImage = ProjectSingleTestView(
                File.ReadAllBytes(inputPath),
                Path.GetFileNameWithoutExtension(inputPath),
                viewName,
                renderMode);
            edgeImage.SavePng(outputPath);
            Console.WriteLine("Edge preview written: " + Path.GetFullPath(outputPath));
            Console.WriteLine("projection_size=" + edgeImage.Width.ToString(CultureInfo.InvariantCulture) + "x" + edgeImage.Height.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunWatermarkTemplateLibraryTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string projectionDirectory = Path.Combine(dataRoot, "Projection");
            string markedDirectory = Path.Combine(dataRoot, "Marked");
            string sourcesPath = Path.Combine(dataRoot, "WatermarkTemplateSources.json");

            List<StepWatermarkTemplateSource> sources = ReadJsonList<StepWatermarkTemplateSource>(sourcesPath);
            IReadOnlyList<StepWatermarkTemplate> templates = StepWatermarkTemplateExtractor.ExtractFromMarkedData(
                projectionDirectory,
                markedDirectory,
                sources);
            var templatesByName = templates.ToDictionary(template => template.Name, StringComparer.OrdinalIgnoreCase);

            foreach (string requiredName in new[] { "LCEDA", "EasyEDA", "easyeda-logo" })
            {
                if (!templatesByName.TryGetValue(requiredName, out StepWatermarkTemplate template))
                {
                    failures.Add("Extracted watermark template is missing: " + requiredName);
                    continue;
                }

                if (template.Width <= 8 || template.Height <= 8)
                {
                    failures.Add(
                        "Extracted watermark template " +
                        requiredName +
                        " should be larger than 8x8, got " +
                        template.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        template.Height.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int edgePointCount = template.EdgePoints == null ? 0 : template.EdgePoints.Count;
                if (edgePointCount < 24)
                {
                    failures.Add(
                        "Extracted watermark template " +
                        requiredName +
                        " should contain enough edge points, got " +
                        edgePointCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }

            VerifyKnownRuntimeTemplatesMatchExtractedTemplates(templatesByName, failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Watermark template library test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Watermark template library test passed.");
            foreach (StepWatermarkTemplate template in templates.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    "template=" +
                    template.Name +
                    ", size=" +
                    template.Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    template.Height.ToString(CultureInfo.InvariantCulture) +
                    ", edge_points=" +
                    template.EdgePoints.Count.ToString(CultureInfo.InvariantCulture));
            }

            return 0;
        }

        private static int RunTextLogoDetectionTests()
        {
            var failures = new List<string>();
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string expectedPath = Path.Combine(dataRoot, "TextLogoDetectionExpected.json");
            List<TextLogoDetectionExpectation> expectations = ReadJsonList<TextLogoDetectionExpectation>(expectedPath);

            foreach (TextLogoDetectionExpectation expectation in expectations)
            {
                string inputPath = Path.Combine(originalDirectory, expectation.FileName);
                if (!File.Exists(inputPath))
                {
                    failures.Add("Missing original fixture for text/logo detection: " + inputPath);
                    continue;
                }

                byte[] stepData = File.ReadAllBytes(inputPath);
                StepProjectionImage colorImage = ProjectSingleTestView(
                    stepData,
                    Path.GetFileNameWithoutExtension(inputPath),
                    expectation.ViewName,
                    StepProjectionRenderMode.Color);
                StepProjectionImage edgeImage = ProjectSingleTestView(
                    stepData,
                    Path.GetFileNameWithoutExtension(inputPath),
                    expectation.ViewName,
                    StepProjectionRenderMode.Edge);
                StepProjectionImage logoEdgeImage = ProjectSingleTestView(
                    stepData,
                    Path.GetFileNameWithoutExtension(inputPath),
                    expectation.ViewName,
                    StepProjectionRenderMode.EdgeVisibleRaw);
                IReadOnlyList<StepTextLogoDetectionRegion> detections = StepTextLogoProjectionDetector.Detect(
                    colorImage,
                    edgeImage,
                    logoEdgeImage,
                    new StepTextLogoDetectionOptions());
                StepTextLogoDetectionRegion requiredDetection = detections
                    .OrderByDescending(detection => detection.Score)
                    .FirstOrDefault(detection => string.Equals(detection.TemplateName, expectation.RequiredTemplate, StringComparison.OrdinalIgnoreCase));

                if (requiredDetection == null)
                {
                    failures.Add(
                        expectation.FileName +
                        " " +
                        expectation.ViewName +
                        " should detect known template " +
                        expectation.RequiredTemplate +
                        ", got [" +
                        string.Join(",", detections.Select(detection => detection.TemplateName).Distinct(StringComparer.OrdinalIgnoreCase)) +
                        "].");
                    continue;
                }

                Console.WriteLine(
                    "detected=" +
                    expectation.FileName +
                    " " +
                    expectation.ViewName +
                    " template=" +
                    requiredDetection.TemplateName +
                    " bounds=" +
                    requiredDetection.X.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    requiredDetection.Y.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    requiredDetection.Width.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    requiredDetection.Height.ToString(CultureInfo.InvariantCulture) +
                    " score=" +
                    requiredDetection.Score.ToString("0.000", CultureInfo.InvariantCulture) +
                    " chamfer=" +
                    requiredDetection.ChamferDistance.ToString("0.000", CultureInfo.InvariantCulture) +
                    " edges=" +
                    requiredDetection.EdgePixelCount.ToString(CultureInfo.InvariantCulture));

                if (requiredDetection.Width <= 8 || requiredDetection.Height <= 8)
                {
                    failures.Add(
                        expectation.FileName +
                        " " +
                        expectation.ViewName +
                        " detection bounds are too small: " +
                        requiredDetection.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        requiredDetection.Height.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                if (requiredDetection.EdgePixelCount < 24)
                {
                    failures.Add(
                        expectation.FileName +
                        " " +
                        expectation.ViewName +
                        " detection should have enough edge pixels, got " +
                        requiredDetection.EdgePixelCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                VerifyTextLogoDetectionExpectation(expectation, requiredDetection, detections, failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Text/logo detection test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);

                return 1;
            }

            Console.WriteLine("Text/logo detection test passed.");
            return 0;
        }

        private static StepProjectionImage ProjectSingleTestView(
            byte[] stepData,
            string modelName,
            string viewName,
            StepProjectionRenderMode renderMode)
        {
            var options = new StepProjectionOptions
            {
                ImageSizePixels = 1600,
                PaddingPixels = 80,
                WriteMetadata = false,
                RenderMode = renderMode
            };
            options.ViewNames.Add(viewName);
            return StepProjectionRenderer.ProjectFileImages(stepData, modelName, options)[0];
        }

        private static void VerifyKnownRuntimeTemplatesMatchExtractedTemplates(
            Dictionary<string, StepWatermarkTemplate> extractedTemplatesByName,
            List<string> failures)
        {
            var knownTemplatesByName = StepWatermarkTemplateLibrary.GetKnownTemplates()
                .ToDictionary(template => template.Name, StringComparer.OrdinalIgnoreCase);

            foreach (string requiredName in new[] { "LCEDA", "EasyEDA", "easyeda-logo" })
            {
                if (!knownTemplatesByName.TryGetValue(requiredName, out StepWatermarkTemplate knownTemplate))
                {
                    failures.Add("Committed runtime watermark template is missing: " + requiredName);
                    continue;
                }

                if (!extractedTemplatesByName.TryGetValue(requiredName, out StepWatermarkTemplate extractedTemplate))
                    continue;

                if (knownTemplate.Width <= 8 || knownTemplate.Height <= 8)
                {
                    failures.Add(
                        "Committed runtime watermark template " +
                        requiredName +
                        " should be larger than 8x8, got " +
                        knownTemplate.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        knownTemplate.Height.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int knownEdgePointCount = knownTemplate.EdgePoints == null ? 0 : knownTemplate.EdgePoints.Count;
                if (knownEdgePointCount < 24)
                {
                    failures.Add(
                        "Committed runtime watermark template " +
                        requiredName +
                        " should contain enough edge points, got " +
                        knownEdgePointCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                    continue;
                }

                double extractedAspect = extractedTemplate.Width / (double)Math.Max(1, extractedTemplate.Height);
                double knownAspect = knownTemplate.Width / (double)Math.Max(1, knownTemplate.Height);
                double aspectDelta = Math.Abs(extractedAspect - knownAspect) / Math.Max(0.001, extractedAspect);
                if (aspectDelta > 0.08)
                {
                    failures.Add(
                        "Committed runtime watermark template " +
                        requiredName +
                        " dimensions are not close to the extracted source aspect: extracted=" +
                        extractedTemplate.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        extractedTemplate.Height.ToString(CultureInfo.InvariantCulture) +
                        ", known=" +
                        knownTemplate.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        knownTemplate.Height.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int overlapCount = CountNormalizedTemplateOverlap(extractedTemplate, knownTemplate);
                int minimumOverlap = Math.Max(24, (int)Math.Round(knownEdgePointCount * 0.80, MidpointRounding.AwayFromZero));
                if (overlapCount < minimumOverlap)
                {
                    failures.Add(
                        "Committed runtime watermark template " +
                        requiredName +
                        " does not overlap enough normalized extracted edge points: overlap=" +
                        overlapCount.ToString(CultureInfo.InvariantCulture) +
                        ", required=" +
                        minimumOverlap.ToString(CultureInfo.InvariantCulture) +
                        ", known_points=" +
                        knownEdgePointCount.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }
        }

        private static int CountNormalizedTemplateOverlap(
            StepWatermarkTemplate extractedTemplate,
            StepWatermarkTemplate knownTemplate)
        {
            if (extractedTemplate.EdgePoints == null || knownTemplate.EdgePoints == null)
                return 0;

            var extractedNormalized = new HashSet<int>();
            foreach (StepWatermarkTemplatePoint point in extractedTemplate.EdgePoints)
            {
                int x = extractedTemplate.Width <= 1
                    ? 0
                    : (int)Math.Round(point.X * (knownTemplate.Width - 1) / (double)(extractedTemplate.Width - 1), MidpointRounding.AwayFromZero);
                int y = extractedTemplate.Height <= 1
                    ? 0
                    : (int)Math.Round(point.Y * (knownTemplate.Height - 1) / (double)(extractedTemplate.Height - 1), MidpointRounding.AwayFromZero);
                x = Math.Max(0, Math.Min(knownTemplate.Width - 1, x));
                y = Math.Max(0, Math.Min(knownTemplate.Height - 1, y));
                extractedNormalized.Add(y * knownTemplate.Width + x);
            }

            int overlapCount = 0;
            foreach (StepWatermarkTemplatePoint point in knownTemplate.EdgePoints)
            {
                if (extractedNormalized.Contains(point.Y * knownTemplate.Width + point.X))
                    overlapCount++;
            }

            return overlapCount;
        }

        private static void VerifyTextLogoDetectionExpectation(
            TextLogoDetectionExpectation expectation,
            StepTextLogoDetectionRegion requiredDetection,
            IReadOnlyList<StepTextLogoDetectionRegion> detections,
            List<string> failures)
        {
            int boundsTolerance = expectation.BoundsTolerance <= 0 ? 24 : expectation.BoundsTolerance;
            if (expectation.ExpectedWidth > 0 && expectation.ExpectedHeight > 0)
            {
                int expectedCenterX = expectation.ExpectedX + expectation.ExpectedWidth / 2;
                int expectedCenterY = expectation.ExpectedY + expectation.ExpectedHeight / 2;
                int actualCenterX = requiredDetection.X + requiredDetection.Width / 2;
                int actualCenterY = requiredDetection.Y + requiredDetection.Height / 2;
                if (Math.Abs(actualCenterX - expectedCenterX) > boundsTolerance ||
                    Math.Abs(actualCenterY - expectedCenterY) > boundsTolerance ||
                    Math.Abs(requiredDetection.Width - expectation.ExpectedWidth) > boundsTolerance ||
                    Math.Abs(requiredDetection.Height - expectation.ExpectedHeight) > boundsTolerance)
                {
                    failures.Add(
                        expectation.FileName +
                        " " +
                        expectation.ViewName +
                        " " +
                        expectation.RequiredTemplate +
                        " detection bounds drifted: expected approximately " +
                        FormatBounds(expectation.ExpectedX, expectation.ExpectedY, expectation.ExpectedWidth, expectation.ExpectedHeight) +
                        ", got " +
                        FormatBounds(requiredDetection.X, requiredDetection.Y, requiredDetection.Width, requiredDetection.Height) +
                        ", tolerance=" +
                        boundsTolerance.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }

            if (expectation.MinScore > 0.0 && requiredDetection.Score < expectation.MinScore)
            {
                failures.Add(
                    expectation.FileName +
                    " " +
                    expectation.ViewName +
                    " " +
                    expectation.RequiredTemplate +
                    " detection score is too low: expected at least " +
                    expectation.MinScore.ToString("0.000", CultureInfo.InvariantCulture) +
                    ", got " +
                    requiredDetection.Score.ToString("0.000", CultureInfo.InvariantCulture) +
                    ".");
            }

            if (expectation.MaxChamferDistance > 0.0 && requiredDetection.ChamferDistance > expectation.MaxChamferDistance)
            {
                failures.Add(
                    expectation.FileName +
                    " " +
                    expectation.ViewName +
                    " " +
                    expectation.RequiredTemplate +
                    " chamfer distance is too high: expected at most " +
                    expectation.MaxChamferDistance.ToString("0.000", CultureInfo.InvariantCulture) +
                    ", got " +
                    requiredDetection.ChamferDistance.ToString("0.000", CultureInfo.InvariantCulture) +
                    ".");
            }

            double maxUnexpectedHighScore = expectation.MaxUnexpectedHighScore <= 0.0
                ? Math.Max(10.0, requiredDetection.Score * 0.50)
                : expectation.MaxUnexpectedHighScore;
            IEnumerable<string> expectedTemplateNames = expectation.ExpectedTemplates == null || expectation.ExpectedTemplates.Count == 0
                ? new[] { expectation.RequiredTemplate }
                : expectation.ExpectedTemplates;
            var expectedTemplates = new HashSet<string>(
                expectedTemplateNames.Where(template => !string.IsNullOrWhiteSpace(template)),
                StringComparer.OrdinalIgnoreCase);

            foreach (StepTextLogoDetectionRegion detection in detections)
            {
                if (expectedTemplates.Contains(detection.TemplateName))
                    continue;
                if (detection.Score < maxUnexpectedHighScore)
                    continue;

                failures.Add(
                    expectation.FileName +
                    " " +
                    expectation.ViewName +
                    " returned unexpected high-score template " +
                    detection.TemplateName +
                    " score=" +
                    detection.Score.ToString("0.000", CultureInfo.InvariantCulture) +
                    ", threshold=" +
                    maxUnexpectedHighScore.ToString("0.000", CultureInfo.InvariantCulture) +
                    ".");
            }
        }

        private static int RunMarkedDetectionParityTests(bool cleanText)
        {
            string dataRoot = FindDataRoot();
            string originalDirectory = Path.Combine(dataRoot, "Original");
            string markedDirectory = Path.Combine(dataRoot, "Marked");
            var failures = new List<string>();

            foreach (string markerPath in Directory.GetFiles(markedDirectory, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryParseMarkedModelAndView(markerPath, out string modelName, out string viewName))
                    continue;

                List<MarkedRectI> markedRects = ReadMarkedRectangles(markerPath);
                if (markedRects.Count == 0)
                    continue;

                string stepPath = Path.Combine(originalDirectory, modelName + ".step");
                if (!File.Exists(stepPath))
                {
                    failures.Add("Missing original STEP for marked parity: " + stepPath);
                    continue;
                }

                byte[] stepData = File.ReadAllBytes(stepPath);
                StepProjectionImage colorImage = ProjectSingleTestView(stepData, modelName, viewName, StepProjectionRenderMode.Color);
                StepProjectionImage edgeImage = ProjectSingleTestView(stepData, modelName, viewName, StepProjectionRenderMode.Edge);
                StepProjectionImage logoEdgeImage = ProjectSingleTestView(stepData, modelName, viewName, StepProjectionRenderMode.EdgeVisibleRaw);
                IReadOnlyList<StepTextLogoDetectionRegion> detections = StepTextLogoProjectionDetector.Detect(
                    colorImage,
                    edgeImage,
                    logoEdgeImage,
                    new StepTextLogoDetectionOptions { DetectArbitraryText = cleanText });

                AssertMarkedParity(modelName, viewName, markedRects, detections, failures);
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Marked detection parity failed. cleanText=" + cleanText.ToString(CultureInfo.InvariantCulture));
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Marked detection parity passed. cleanText=" + cleanText.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static bool TryParseMarkedModelAndView(string markerPath, out string modelName, out string viewName)
        {
            string name = Path.GetFileNameWithoutExtension(markerPath);
            foreach (string candidateViewName in StepProjectionRenderer.ViewNames.OrderByDescending(value => value.Length))
            {
                string suffix = "__" + candidateViewName;
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                modelName = name.Substring(0, name.Length - suffix.Length);
                viewName = candidateViewName;
                return true;
            }

            modelName = string.Empty;
            viewName = string.Empty;
            return false;
        }

        private static List<MarkedRectI> ReadMarkedRectangles(string markerPath)
        {
            MarkedRegionFile document = JsonSerializer.Deserialize<MarkedRegionFile>(File.ReadAllText(markerPath));
            return (document?.Rectangles ?? new List<MarkedRectangle>())
                .Where(rectangle => rectangle.Width > 0 && rectangle.Height > 0)
                .Select(rectangle => new MarkedRectI(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height))
                .ToList();
        }

        private static void AssertMarkedParity(
            string modelName,
            string viewName,
            IReadOnlyList<MarkedRectI> markedRects,
            IReadOnlyList<StepTextLogoDetectionRegion> detections,
            List<string> failures)
        {
            if (detections.Count != markedRects.Count)
            {
                failures.Add(
                    modelName +
                    " " +
                    viewName +
                    " expected " +
                    markedRects.Count.ToString(CultureInfo.InvariantCulture) +
                    " detections from marked data, got " +
                    detections.Count.ToString(CultureInfo.InvariantCulture) +
                    ".");
                return;
            }

            var detectedRects = detections
                .Select(detection => new MarkedRectI(detection.X, detection.Y, detection.Width, detection.Height))
                .ToList();
            foreach (MarkedRectI detected in detectedRects)
            {
                MarkedRectI best = markedRects
                    .OrderByDescending(marked => IntersectArea(marked, detected))
                    .First();
                if (!IsInsideMarkedRectangle(detected, best))
                    failures.Add(modelName + " " + viewName + " detection " + detected + " is not fully inside marked rectangle " + best + ".");
                if (detected.Area >= best.Area)
                    failures.Add(modelName + " " + viewName + " detection " + detected + " should be smaller than marked rectangle " + best + ".");
            }
        }

        private static bool IsInsideMarkedRectangle(MarkedRectI inner, MarkedRectI outer)
        {
            return inner.X >= outer.X &&
                inner.Y >= outer.Y &&
                inner.X + inner.Width <= outer.X + outer.Width &&
                inner.Y + inner.Height <= outer.Y + outer.Height;
        }

        private static int IntersectArea(MarkedRectI left, MarkedRectI right)
        {
            int x0 = Math.Max(left.X, right.X);
            int y0 = Math.Max(left.Y, right.Y);
            int x1 = Math.Min(left.X + left.Width, right.X + right.Width);
            int y1 = Math.Min(left.Y + left.Height, right.Y + right.Height);
            if (x1 <= x0 || y1 <= y0)
                return 0;
            return (x1 - x0) * (y1 - y0);
        }

        private static string FormatBounds(int x, int y, int width, int height)
        {
            return x.ToString(CultureInfo.InvariantCulture) +
                "," +
                y.ToString(CultureInfo.InvariantCulture) +
                "," +
                width.ToString(CultureInfo.InvariantCulture) +
                "," +
                height.ToString(CultureInfo.InvariantCulture);
        }

        private static List<T> ReadJsonList<T>(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Required test fixture was not found.", path);

            List<T> result = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path));
            if (result == null)
                throw new InvalidDataException("Could not read JSON fixture: " + path);

            return result;
        }

        private static F3DRenderedImage RenderPreviewSmokeSameThread(byte[] stepData)
        {
            using (F3DProjectionRenderer.F3DPreviewSession session =
                F3DProjectionRenderer.CreatePreviewSession(stepData))
            {
                return session.RenderInteractivePreviewImage(
                    320,
                    240,
                    new F3DPreviewCameraState());
            }
        }

        private static F3DRenderedImage RenderPreviewSmokeCrossThread(byte[] stepData)
        {
            F3DProjectionRenderer.F3DPreviewSession session = Task.Run(
                () => F3DProjectionRenderer.CreatePreviewSession(stepData))
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            try
            {
                return Task.Run(
                    () => session.RenderInteractivePreviewImage(
                        320,
                        240,
                        new F3DPreviewCameraState()))
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                session.Dispose();
            }
        }

        private static int RunF3DNoAmbientOcclusionTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string projectionRendererPath = Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs");
            string f3dRendererPath = Path.Combine(repoRoot, "StepF3DRenderLib", "F3DProjectionRenderer.cs");
            string projectionRenderer = File.ReadAllText(projectionRendererPath);
            string f3dRenderer = File.ReadAllText(f3dRendererPath);

            if (projectionRenderer.Contains("\"--ambient-occlusion\"", StringComparison.Ordinal))
                failures.Add("External f3d-console render path must not pass --ambient-occlusion.");
            if (f3dRenderer.Contains("\"render.effect.ambient_occlusion\", 1", StringComparison.Ordinal))
                failures.Add("In-process F3D render path must not enable render.effect.ambient_occlusion.");

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("F3D no ambient occlusion test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("F3D no ambient occlusion test passed.");
            return 0;
        }

        private static byte[] ConvertRawF3DImageToRgba(F3DRenderedImage image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (image.RawBytes == null)
                throw new InvalidDataException("F3D preview raw bytes are missing.");
            if (image.Width <= 0 || image.Height <= 0 || image.ChannelCount <= 0)
                throw new InvalidDataException("F3D preview image shape is invalid.");
            if (image.ChannelType != 0 || image.ChannelTypeSize != 1)
                throw new InvalidDataException("F3D preview image uses an unsupported channel type.");

            int pixelCount = checked(image.Width * image.Height);
            if (image.RawBytes.Length < pixelCount * image.ChannelCount)
                throw new InvalidDataException("F3D preview image data is incomplete.");

            var rgba = new byte[pixelCount * 4];
            for (int y = 0; y < image.Height; y++)
            {
                int sourceRow = (image.Height - 1 - y) * image.Width * image.ChannelCount;
                int targetRow = y * image.Width * 4;
                for (int x = 0; x < image.Width; x++)
                {
                    int source = sourceRow + x * image.ChannelCount;
                    int target = targetRow + x * 4;
                    if (image.ChannelCount == 1)
                    {
                        byte value = image.RawBytes[source];
                        rgba[target] = value;
                        rgba[target + 1] = value;
                        rgba[target + 2] = value;
                        rgba[target + 3] = 255;
                        continue;
                    }

                    rgba[target] = image.RawBytes[source];
                    rgba[target + 1] = image.RawBytes[source + 1];
                    rgba[target + 2] = image.RawBytes[source + 2];
                    rgba[target + 3] = image.ChannelCount >= 4 ? image.RawBytes[source + 3] : (byte)255;
                }
            }

            return rgba;
        }

        private static int CountNonWhitePixels(byte[] rgba)
        {
            int count = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                if (rgba[i] < 245 || rgba[i + 1] < 245 || rgba[i + 2] < 245)
                    count++;
            }

            return count;
        }

        private static int CountDarkPixels(byte[] rgba, byte threshold)
        {
            int count = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                if (rgba[i + 3] > 0 && rgba[i] <= threshold && rgba[i + 1] <= threshold && rgba[i + 2] <= threshold)
                    count++;
            }

            return count;
        }

        private static int CountDarkPixels(StepProjectionImage image, MarkedRectI rectangle, byte threshold)
        {
            int count = 0;
            int left = Math.Max(0, rectangle.X);
            int top = Math.Max(0, rectangle.Y);
            int right = Math.Min(image.Width, rectangle.X + rectangle.Width);
            int bottom = Math.Min(image.Height, rectangle.Y + rectangle.Height);
            for (int y = top; y < bottom; y++)
            {
                int row = y * image.Width * 4;
                for (int x = left; x < right; x++)
                {
                    int offset = row + x * 4;
                    if (image.RgbaBytes[offset + 3] > 0 &&
                        image.RgbaBytes[offset] <= threshold &&
                        image.RgbaBytes[offset + 1] <= threshold &&
                        image.RgbaBytes[offset + 2] <= threshold)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static void SaveRgbaPng(byte[] rgba, int width, int height, string outputPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using (var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul))
            {
                Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
                using (SKImage image = SKImage.FromBitmap(bitmap))
                using (SKData data = image.Encode(SKEncodedImageFormat.Png, 95))
                {
                    File.WriteAllBytes(outputPath, data.ToArray());
                }
            }
        }

        private static async System.Threading.Tasks.Task<int> RunModelImportMeasurementAsync(
            string partNumber,
            int repeatCount,
            bool cleanText)
        {
            using (var httpClient = new HttpClient())
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) EasyEDA-Loader-StepCleaner/1.0");

                Console.WriteLine("Model import measurement: part=" + partNumber + ", repeat=" + repeatCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("Clean text: " + cleanText.ToString(CultureInfo.InvariantCulture));

                for (int repeatIndex = 1; repeatIndex <= repeatCount; repeatIndex++)
                {
                    var timings = new ModelImportMeasurementTimings();
                    Console.WriteLine("Run " + repeatIndex.ToString(CultureInfo.InvariantCulture) + "/" + repeatCount.ToString(CultureInfo.InvariantCulture));

                    MeasuredModelInfo model = await timings.MeasureAsync(
                        "component_lookup",
                        () => LoadMeasuredModelInfoAsync(httpClient, partNumber, cancellation.Token)).ConfigureAwait(false);

                    Console.WriteLine("  model_uuid=" + model.Uuid);
                    Console.WriteLine("  model_title=" + model.Title);

                    byte[] originalStep = await timings.MeasureAsync(
                        "model_download_cache_read",
                        () => GetOrDownloadBytesAsync(
                            GetOriginalStepPath(model.Uuid),
                            () => DownloadBytesAsync(httpClient, "https://modules.easyeda.com/qAxj6KHrDKw4blvCG8QJPs7Y/" + model.Uuid, cancellation.Token),
                            cancellation.Token)).ConfigureAwait(false);

                    byte[] rawObj = await timings.MeasureAsync(
                        "raw_obj_download_cache_read",
                        () => GetOrDownloadBytesAsync(
                            GetRawObjPath(model.Uuid),
                            () => DownloadBytesAsync(httpClient, "https://modules.easyeda.com/3dmodel/" + model.Uuid, cancellation.Token),
                            cancellation.Token)).ConfigureAwait(false);

                    string cleanCacheKey = CleanStepCacheKeys.GetCleanModeKey(model.Uuid, cleanText);
                    string cleanedStepPath = GetCleanStepPath(cleanCacheKey);
                    StepWatermarkCleanVerifierResult cleanMissResult = null;
                    byte[] cleanedStep = await timings.MeasureAsync(
                        "watermark_clean_cache",
                        () => GetOrDownloadBytesAsync(
                            cleanedStepPath,
                            () => System.Threading.Tasks.Task.Run(
                                () =>
                                {
                                    cleanMissResult = StepWatermarkCleanVerifier.CleanStepModelFastWithReport(
                                        originalStep,
                                        cleanText);
                                    return cleanMissResult.CleanStep;
                                },
                                cancellation.Token),
                            cancellation.Token)).ConfigureAwait(false);

                    ModelZInfo zInfo = await timings.MeasureAsync(
                        "raw_obj_z_info",
                        () => ModelZInfoCache.GetOrCreateAsync(
                            model.Uuid,
                            () => System.Threading.Tasks.Task.FromResult(rawObj),
                            cancellation.Token)).ConfigureAwait(false);

                    IReadOnlyList<StepSilhouettePrimitive> projectionPrimitives = timings.Measure(
                        "occt_hlr_projection_total",
                        () => StepSilhouetteProjection.Generate(cleanedStep, CreateMeasurementProjectionPlacement(model)));

                    Console.WriteLine("  original_step_bytes=" + originalStep.Length.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("  raw_obj_bytes=" + rawObj.Length.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("  cleaned_step_bytes=" + cleanedStep.Length.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine("  z_offset_mm=" + zInfo.OffsetFromOrigin.ToString("R", CultureInfo.InvariantCulture));
                    Console.WriteLine("  model_height_mm=" + zInfo.Height.ToString("R", CultureInfo.InvariantCulture));
                    Console.WriteLine("  projection_primitives=" + projectionPrimitives.Count.ToString(CultureInfo.InvariantCulture));
                    PrintCleanerTimings(cleanMissResult?.CleanReport, "  ");
                    timings.WriteToConsole("  ");
                }
            }

            return 0;
        }

        private static void PrintCleanerTimings(StepWatermarkCleanerReport report, string prefix)
        {
            if (report?.Timings == null || report.Timings.Count == 0)
                return;

            foreach (StepWatermarkCleanerTiming timing in report.Timings)
            {
                if (timing == null || string.IsNullOrWhiteSpace(timing.Name))
                    continue;

                Console.WriteLine(
                    prefix +
                    "watermark_clean_detail_" +
                    timing.Name +
                    "_ms=" +
                    timing.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static async System.Threading.Tasks.Task<MeasuredModelInfo> LoadMeasuredModelInfoAsync(
            HttpClient httpClient,
            string partNumber,
            CancellationToken cancellationToken)
        {
            string url = "https://easyeda.com/api/products/" + Uri.EscapeDataString(partNumber) + "/components?version=6.4.19.5";
            string json = await GetOrDownloadTextAsync(
                GetComponentJsonPath(partNumber),
                () => httpClient.GetStringAsync(url, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                JsonElement packageData = root
                    .GetProperty("result")
                    .GetProperty("packageDetail")
                    .GetProperty("dataStr");
                JsonElement shapes = packageData.GetProperty("shape");
                foreach (JsonElement shape in shapes.EnumerateArray())
                {
                    string rawShape = shape.GetString();
                    if (rawShape != null && rawShape.StartsWith("SVGNODE~", StringComparison.Ordinal))
                        return ParseMeasuredModelInfo(rawShape);
                }
            }

            throw new InvalidDataException("Component does not contain a 3D SVGNODE model: " + partNumber);
        }

        private static MeasuredModelInfo ParseMeasuredModelInfo(string rawShape)
        {
            int separator = rawShape.IndexOf('~');
            if (separator < 0 || separator + 1 >= rawShape.Length)
                throw new InvalidDataException("Invalid SVGNODE model shape.");

            using (JsonDocument document = JsonDocument.Parse(rawShape.Substring(separator + 1)))
            {
                JsonElement attrs = document.RootElement.GetProperty("attrs");
                string[] rotationParts = attrs.GetProperty("c_rotation").GetString().Split(',');
                return new MeasuredModelInfo
                {
                    Uuid = attrs.GetProperty("uuid").GetString(),
                    Title = attrs.GetProperty("title").GetString(),
                    WidthMm = ConvertEasyEdaUnitToMm(ParseInvariantDouble(attrs.GetProperty("c_width").GetString())),
                    HeightMm = ConvertEasyEdaUnitToMm(ParseInvariantDouble(attrs.GetProperty("c_height").GetString())),
                    RotX = rotationParts.Length > 0 ? ParseInvariantDouble(rotationParts[0]) : 0.0,
                    RotY = rotationParts.Length > 1 ? ParseInvariantDouble(rotationParts[1]) : 0.0,
                    RotZ = rotationParts.Length > 2 ? ParseInvariantDouble(rotationParts[2]) : 0.0
                };
            }
        }

        private static async Task<byte[]> DownloadBytesAsync(
            HttpClient httpClient,
            string url,
            CancellationToken cancellationToken)
        {
            using (HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> GetOrDownloadBytesAsync(
            string cachePath,
            Func<Task<byte[]>> download,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(cachePath))
            {
                byte[] cached = File.ReadAllBytes(cachePath);
                if (cached.Length > 0)
                    return cached;
            }

            byte[] data = await download().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (data != null && data.Length > 0)
            {
                string directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(cachePath, data);
            }

            return data;
        }

        private static async Task<string> GetOrDownloadTextAsync(
            string cachePath,
            Func<Task<string>> download,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(cachePath))
            {
                string cached = File.ReadAllText(cachePath, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(cached))
                    return cached;
            }

            string data = await download().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(data))
            {
                string directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(cachePath, data, Encoding.UTF8);
            }

            return data;
        }

        private static StepSilhouettePlacement CreateMeasurementProjectionPlacement(MeasuredModelInfo model)
        {
            FootprintModelRotation modelRotation = FootprintModelPlacement.ResolveAltiumModelRotationDeg(
                model.RotX,
                model.RotY,
                model.RotZ);
            FootprintModelRotation projectionRotation = FootprintModelPlacement.ResolveProjectionModelRotationDeg(modelRotation);
            double halfWidth = Math.Max(model.WidthMm, 1.0) / 2.0;
            double halfHeight = Math.Max(model.HeightMm, 1.0) / 2.0;
            return new StepSilhouettePlacement
            {
                TargetBounds = new StepSilhouetteBounds
                {
                    Left = -halfWidth,
                    Bottom = -halfHeight,
                    Right = halfWidth,
                    Top = halfHeight
                },
                RotX = projectionRotation.X,
                RotY = projectionRotation.Y,
                RotZ = projectionRotation.Z,
                Rotation2D = FootprintModelPlacement.ProjectionPlacementRotationDeg()
            };
        }

        private static string CreateMeasurementVerificationDirectory(string partNumber, string modelUuid)
        {
            string reportName =
                GetSafeFileName(partNumber) +
                "_" +
                GetSafeFileName(modelUuid) +
                "_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string reportDirectory = Path.Combine(GetLocalDataRoot(), "StepCleanerReports", reportName);
            Directory.CreateDirectory(reportDirectory);
            return reportDirectory;
        }

        private static string GetOriginalStepPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Original"), GetSafeFileName(modelUuid) + ".step");
        }

        private static string GetRawObjPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Raw"), GetSafeFileName(modelUuid) + ".obj");
        }

        private static string GetCleanStepPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Clean"), GetSafeFileName(modelUuid) + "_clean.step");
        }

        private static string GetComponentJsonPath(string partNumber)
        {
            return Path.Combine(GetLocalDataRoot(), "ComponentCache", GetSafeFileName(partNumber) + ".json");
        }

        private static string GetModelCacheDirectory(string kind)
        {
            return Path.Combine(GetLocalDataRoot(), "ModelCache", kind);
        }

        private static string GetLocalDataRoot()
        {
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localApplicationData)
                ? Path.Combine(Path.GetTempPath(), "EasyEDA-Loader")
                : Path.Combine(localApplicationData, "EasyEDA-Loader");
        }

        private static string GetSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                value = Guid.NewGuid().ToString("N");

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                value = value.Replace(invalidChar, '_');

            return value;
        }

        private static double ParseInvariantDouble(string value)
        {
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static double ConvertEasyEdaUnitToMm(double value)
        {
            return value * 10.0 * 0.0254;
        }

        private sealed class MeasuredModelInfo
        {
            public string Uuid { get; set; }
            public string Title { get; set; }
            public double WidthMm { get; set; }
            public double HeightMm { get; set; }
            public double RotX { get; set; }
            public double RotY { get; set; }
            public double RotZ { get; set; }
        }

        private sealed class ModelImportMeasurementTimings
        {
            private readonly List<Tuple<string, long>> stages = new List<Tuple<string, long>>();

            public async Task<T> MeasureAsync<T>(string stageName, Func<Task<T>> action)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    return await action().ConfigureAwait(false);
                }
                finally
                {
                    stopwatch.Stop();
                    stages.Add(Tuple.Create(stageName, stopwatch.ElapsedMilliseconds));
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
                    stages.Add(Tuple.Create(stageName, stopwatch.ElapsedMilliseconds));
                }
            }

            public void WriteToConsole(string prefix)
            {
                long total = 0;
                foreach (Tuple<string, long> stage in stages)
                {
                    total += stage.Item2;
                    Console.WriteLine(
                        prefix +
                        stage.Item1 +
                        "_ms=" +
                        stage.Item2.ToString(CultureInfo.InvariantCulture));
                }

                Console.WriteLine(prefix + "total_measured_ms=" + total.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void VerifyCleanTextPostProcessProjectionUsesCachedOriginal(
            string originalFile,
            string cleanTextFile,
            StepWatermarkDetectionReport detectionReport,
            string cachedOriginalProjectionDirectory,
            string cleanTextProjectionDirectory,
            List<string> failures,
            List<ProjectionVisualFailure> visualFailures)
        {
            var projectionOptions = CreateVerificationProjectionOptions();
            var detectionRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    originalFile,
                    detectionReport,
                    projectionOptions)
                .ToList();

            var detectedViewNames = detectionRegions
                .Select(region => region.ViewName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (detectedViewNames.Count == 0)
            {
                failures.Add(Path.GetFileName(originalFile) + " CleanText did not produce projection verification regions.");
                return;
            }

            string originalModelName = Path.GetFileNameWithoutExtension(originalFile);
            string generatedOriginalProjectionDirectory = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(cleanTextProjectionDirectory)) ?? string.Empty,
                "CleanTextOriginalProjection");
            bool needsGeneratedOriginalProjection = detectedViewNames.Any(viewName =>
                !File.Exists(Path.Combine(cachedOriginalProjectionDirectory, originalModelName + "__" + viewName + ".png")));
            if (needsGeneratedOriginalProjection)
            {
                ClearProjectionFiles(generatedOriginalProjectionDirectory, new[] { Path.GetFileName(originalFile) });
                StepProjectionRenderer.ProjectFile(
                    originalFile,
                    generatedOriginalProjectionDirectory,
                    CreateProjectionOptionsForViews(detectedViewNames, projectionOptions));
            }

            ClearProjectionFiles(cleanTextProjectionDirectory, new[] { Path.GetFileName(cleanTextFile) });
            StepProjectionRenderer.ProjectFile(
                cleanTextFile,
                cleanTextProjectionDirectory,
                CreateProjectionOptionsForViews(detectedViewNames, projectionOptions));

            string cleanTextModelName = Path.GetFileNameWithoutExtension(cleanTextFile);
            foreach (string viewName in detectedViewNames)
            {
                string cachedOriginalProjectionPath = Path.Combine(cachedOriginalProjectionDirectory, originalModelName + "__" + viewName + ".png");
                string generatedOriginalProjectionPath = Path.Combine(generatedOriginalProjectionDirectory, originalModelName + "__" + viewName + ".png");
                string originalProjectionPath = File.Exists(cachedOriginalProjectionPath)
                    ? cachedOriginalProjectionPath
                    : generatedOriginalProjectionPath;
                string cleanTextProjectionPath = Path.Combine(cleanTextProjectionDirectory, cleanTextModelName + "__" + viewName + ".png");

                if (!File.Exists(originalProjectionPath))
                {
                    failures.Add("Original projection is missing for CleanText verification: " + originalProjectionPath);
                    continue;
                }

                if (!File.Exists(cleanTextProjectionPath))
                {
                    failures.Add("CleanText projection is missing: " + cleanTextProjectionPath);
                    continue;
                }

                var viewRegions = detectionRegions
                    .Where(region => string.Equals(region.ViewName, viewName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                VerifyPostCleanProjectionImage(
                    Path.GetFileName(originalFile),
                    viewName,
                    originalProjectionPath,
                    cleanTextProjectionPath,
                    viewRegions,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    failures,
                    visualFailures);

                if (string.Equals(viewName, "z_plus", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Path.GetFileName(originalFile),
                        "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                        StringComparison.OrdinalIgnoreCase))
                {
                    VerifyProjectionRegionPreserved(
                        Path.GetFileName(originalFile),
                        viewName,
                        "pin-one marker",
                        originalProjectionPath,
                        cleanTextProjectionPath,
                        x: 318,
                        y: 128,
                        width: 68,
                        height: 68,
                        maxChangedPixels: 12,
                        failures);
                }
            }
        }

        private static void VerifyProjectionRegionPreserved(
            string fileName,
            string viewName,
            string regionName,
            string originalProjectionPath,
            string cleanProjectionPath,
            int x,
            int y,
            int width,
            int height,
            int maxChangedPixels,
            List<string> failures)
        {
            using (var originalImage = SKBitmap.Decode(originalProjectionPath))
            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            {
                if (originalImage == null || cleanImage == null)
                {
                    failures.Add(fileName + " has an unreadable " + regionName + " projection on " + viewName + ".");
                    return;
                }

                int xEnd = Math.Min(x + width, Math.Min(originalImage.Width, cleanImage.Width));
                int yEnd = Math.Min(y + height, Math.Min(originalImage.Height, cleanImage.Height));
                int changedPixels = 0;
                for (int row = Math.Max(y, 0); row < yEnd; row++)
                {
                    for (int col = Math.Max(x, 0); col < xEnd; col++)
                    {
                        if (PixelsDifferent(originalImage.GetPixel(col, row), cleanImage.GetPixel(col, row), ProjectionDifferenceTolerance))
                            changedPixels++;
                    }
                }

                if (changedPixels > maxChangedPixels)
                {
                    failures.Add(
                        fileName +
                        " changed preserved " +
                        regionName +
                        " on " +
                        viewName +
                        ": pixels=" +
                        changedPixels.ToString(CultureInfo.InvariantCulture) +
                        ", allowed=" +
                        maxChangedPixels.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }
        }

        private static int RunSilhouetteCleanupTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string verifier = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepWatermarkCleanVerifier.cs"));
            string projectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
            AssertContains(
                verifier,
                "Task.WaitAll(originalProjectionTask, cleanProjectionTask)",
                "watermark cleanup verification should render original and cleaned projections in parallel",
                failures);
            AssertContains(
                projectionRenderer,
                "TryRenderWithOpenCascade",
                "watermark verification projections should keep an experimental OpenCascade render backend",
                failures);
            AssertContains(
                projectionRenderer,
                "IsOpenCascadeVerificationRendererEnabled",
                "OpenCascade PNG verification rendering should stay gated until visual equivalence is proven",
                failures);
            AssertContains(
                projectionRenderer,
                "TryRenderWithOpenCascade(inputPath, outputPath, view, transform, options",
                "OpenCascade renderer must use StepProjectionRenderer's existing transform so detection masks align",
                failures);
            AssertContains(
                projectionRenderer,
                "TryRenderWithOpenCascadeBatch",
                "multi-view watermark verification projections should avoid repeated OpenCascade process startup",
                failures);
            AssertContains(
                projectionRenderer,
                "GenerateViewsFromFile",
                "multi-view OpenCascade rendering should request all selected views from one helper process",
                failures);
            AssertContains(
                projectionRenderer,
                "TryRenderWithF3D",
                "F3D should remain as fallback while OpenCascade rollout is verified",
                failures);

            string dataRoot = FindDataRoot();
            string sot223Path = Path.Combine(dataRoot, "Original", "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step");
            if (!File.Exists(sot223Path))
                failures.Add("SOT-223 cleanup fixture is missing: " + sot223Path);

            if (failures.Count == 0)
            {
                byte[] originalStep = File.ReadAllBytes(sot223Path);
                byte[] cleanedStep = StepWatermarkCleaner.Clean(originalStep, new StepWatermarkCleanerOptions());
                StepSilhouettePlacement placement = CreateDefaultSilhouettePlacement();

                IReadOnlyList<StepSilhouettePrimitive> originalPrimitives = StepSilhouetteProjection.Generate(originalStep, placement);
                IReadOnlyList<StepSilhouettePrimitive> cleanedPrimitives = StepSilhouetteProjection.Generate(cleanedStep, placement);

                if (cleanedPrimitives.Count >= originalPrimitives.Count)
                {
                    failures.Add(
                        "cleaned SOT-223 silhouette should drop inactive watermark topology: original=" +
                        originalPrimitives.Count.ToString(CultureInfo.InvariantCulture) +
                        ", cleaned=" +
                        cleanedPrimitives.Count.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Silhouette cleanup regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Silhouette cleanup regression test passed.");
            return 0;
        }

        private static int RunImportSavePolicyTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();

            AssertEqual(
                "False",
                ImportLibrarySavePolicy.SaveLibrariesAfterImport ? "True" : "False",
                "schematic/PCB library import must leave libraries unsaved for manual review",
                failures);
            AssertNoForbiddenLibrarySaveCalls(
                Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDALoader.cs"),
                "EasyEDALoader",
                failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Import save policy regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Import save policy regression test passed.");
            return 0;
        }

        private static int RunFootprintPlacementTests()
        {
            var failures = new List<string>();

            var lm317ImportedBounds = new StepSilhouetteBounds
            {
                Left = -945.30976,
                Bottom = 657.15,
                Right = -938.20032,
                Top = 663.65
            };

            FootprintModelMove move = FootprintModelPlacement.CalculateCenteringMoveMm(lm317ImportedBounds, 0.0, 0.0);
            AssertNear(941.75504, move.XMm, 0.00001, "LM317 body should move from imported STEP bounds to footprint center on X", failures);
            AssertNear(-660.4, move.YMm, 0.00001, "LM317 body should move from imported STEP bounds to footprint center on Y", failures);
            AssertNear(0.0, lm317ImportedBounds.CenterX + move.XMm, 0.00001, "LM317 centered body X should match target center", failures);
            AssertNear(0.0, lm317ImportedBounds.CenterY + move.YMm, 0.00001, "LM317 centered body Y should match target center", failures);

            FootprintModelMove shiftedTargetMove = FootprintModelPlacement.CalculateCenteringMoveMm(lm317ImportedBounds, 2.5, -1.25);
            AssertNear(2.5, lm317ImportedBounds.CenterX + shiftedTargetMove.XMm, 0.00001, "body centering should support non-zero EasyEDA model X", failures);
            AssertNear(-1.25, lm317ImportedBounds.CenterY + shiftedTargetMove.YMm, 0.00001, "body centering should support non-zero EasyEDA model Y", failures);

            AssertNear(0.0, FootprintModelPlacement.ProjectionPlacementRotationDeg(0.0), 0.00001, "projection placement should not apply model Z rotation a second time", failures);
            AssertNear(355.0, FootprintModelPlacement.ProjectionPlacementRotationDeg(-5.0), 0.00001, "projection correction rotation should normalize negative angles", failures);
            AssertNear(180.0, FootprintModelPlacement.ProjectionPlacementRotationDeg(), 0.00001, "Altium projection placement should rotate imported silhouettes 180 degrees", failures);

            FootprintModelRotation c5334147Rotation = FootprintModelPlacement.ResolveAltiumModelRotationDeg(0.0, 0.0, 0.0);
            AssertNear(0.0, c5334147Rotation.X, 0.00001, "C5334147 EasyEDA zero model rotation should preserve X rotation", failures);
            AssertNear(0.0, c5334147Rotation.Y, 0.00001, "C5334147 EasyEDA zero model rotation should preserve Y rotation", failures);
            AssertNear(0.0, c5334147Rotation.Z, 0.00001, "C5334147 EasyEDA zero model rotation should preserve Z rotation", failures);

            FootprintModelRotation c5334147ProjectionRotation = FootprintModelPlacement.ResolveProjectionModelRotationDeg(c5334147Rotation);
            AssertNear(0.0, c5334147ProjectionRotation.X, 0.00001, "C5334147 projection should preserve X rotation", failures);
            AssertNear(0.0, c5334147ProjectionRotation.Y, 0.00001, "C5334147 projection should preserve Y rotation", failures);
            AssertNear(0.0, c5334147ProjectionRotation.Z, 0.00001, "C5334147 projection should preserve Z rotation", failures);
            AssertNear(180.0, FootprintModelPlacement.ProjectionPlacementRotationDeg(180.0), 0.00001, "C5334147 projection placement should rotate the silhouette 180 degrees", failures);

            FootprintModelMove c5334147FootprintOrigin = FootprintModelPlacement.ResolveModelCenterMm(
                0.0,
                0.0,
                -0.004826,
                -0.193294);
            AssertNear(-0.004826, c5334147FootprintOrigin.XMm, 0.00001, "C5334147 zero product transform should preserve footprint 3D origin X", failures);
            AssertNear(-0.193294, c5334147FootprintOrigin.YMm, 0.00001, "C5334147 zero product transform should preserve footprint 3D origin Y", failures);

            FootprintModelMove c5338332FootprintOrigin = FootprintModelPlacement.ResolveModelCenterMm(
                0.0,
                0.0000254,
                0.0,
                0.1599184);
            AssertNear(0.0, c5338332FootprintOrigin.XMm, 0.00001, "C5338332 near-zero product transform should preserve footprint 3D origin X", failures);
            AssertNear(0.1599184, c5338332FootprintOrigin.YMm, 0.00001, "C5338332 near-zero product transform should preserve footprint 3D origin Y", failures);

            FootprintModelMove explicitModelCenter = FootprintModelPlacement.ResolveModelCenterMm(1.2, -0.4, -0.004826, -0.193294);
            AssertNear(1.2, explicitModelCenter.XMm, 0.00001, "explicit non-zero model X offset must be preserved", failures);
            AssertNear(-0.4, explicitModelCenter.YMm, 0.00001, "explicit non-zero model Y offset must be preserved", failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Footprint placement regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Footprint placement regression test passed.");
            return 0;
        }

        private static int RunFootprintLayerTests()
        {
            var failures = new List<string>();

            AssertEqual("TopAssembly", FootprintLayerMap.NormalizeLayerName("ComponentShapeLayer"), "component shape should import as assembly documentation", failures);
            AssertEqual("TopAssembly", FootprintLayerMap.NormalizeLayerName("ComponentMarkingLayer"), "component marking should import as assembly documentation", failures);
            AssertEqual("TopAssembly", FootprintLayerMap.NormalizeLayerName("ComponentPolarityLayer"), "component polarity should import as assembly documentation", failures);
            AssertEqual("Mechanical", FootprintLayerMap.NormalizeLayerName("LeadShapeLayer"), "lead shape should import as non-production mechanical documentation", failures);
            AssertEqual("Mechanical", FootprintLayerMap.NormalizeLayerName("Document"), "document layer should import as non-production mechanical documentation", failures);
            AssertEqual("TopSilkLayer", FootprintLayerMap.NormalizeLayerName("TopSilkLayer"), "existing EasyEDA layer names should remain unchanged", failures);

            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("ComponentShapeLayer", false), "component shape should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("ComponentMarkingLayer", false), "component marking should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("ComponentPolarityLayer", false), "component polarity should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("LeadShapeLayer", false), "lead shape should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual(null, FootprintLayerMap.NormalizeLayerName("Document", false), "document layer should be skipped when LCSC mechanical layers are disabled", failures);
            AssertEqual("TopSilkLayer", FootprintLayerMap.NormalizeLayerName("TopSilkLayer", false), "existing EasyEDA layer names should remain available when LCSC mechanical layers are disabled", failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("Footprint layer regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("Footprint layer regression test passed.");
            return 0;
        }

        private static int RunPcbLibActionTests()
        {
            var failures = new List<string>();
            string repoRoot = FindRepoRoot();
            string ins = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDA-Loader.ins"));
            string rcs = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDA-Loader.rcs"));
            string module = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EasyEDALoader.cs"));
            string eePcb = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "EEPCB.cs"));
            string footprintData = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "FootprintData.cs"));
            string footprint3dModel = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "FootprintShapes", "EeFootprint3dModel.cs"));

            AssertContains(ins, "Command  Name = 'EasyEDAReproject3D'", "PcbLib action command must be declared in the INS file", failures);
            AssertContains(ins, "Command  Name = 'EasyEDAAlign3DModel'", "PcbLib alignment command must be declared in the INS file", failures);

            AssertContains(rcs, "Caption='&EasyEDA'", "PcbLib menu should expose an EasyEDA submenu", failures);
            AssertContains(rcs, "Caption='&Loader...'", "Loader command should move under the EasyEDA submenu", failures);
            AssertContains(rcs, "Caption='&Reproject 3D'", "Reproject 3D command should be available in the PcbLib EasyEDA submenu", failures);
            AssertContains(rcs, "Caption='&Align 3D model'", "Align 3D model command should be available in the PcbLib EasyEDA submenu", failures);

            AssertContains(module, "RegisterCommand(\"EasyEDAReproject3D\"", "module must register the Reproject 3D command", failures);
            AssertContains(module, "RegisterCommand(\"EasyEDAAlign3DModel\"", "module must register the Align 3D model command", failures);
            AssertContains(module, "ReprojectActiveFootprint3D", "Reproject command should dispatch to an active-footprint handler", failures);
            AssertContains(module, "AlignActiveFootprint3DModel", "Align command should dispatch to an active-footprint handler", failures);

            AssertContains(eePcb, "ClearMechanical2Projection", "Reproject 3D must clear Mechanical Layer 2 before regenerating projection", failures);
            AssertContains(eePcb, "SyncPcbLibComponentFromBoard(component)", "Reproject 3D cleanup must sync the active PcbLib footprint from its board view before enumerating old primitives", failures);
            AssertContains(eePcb, "TransferAllPrimitivesBackFromBoard", "Reproject 3D cleanup must call the PcbLib transfer-back API so visible board-view primitives are enumerable", failures);
            AssertContains(eePcb, "SyncPcbLibComponentToBoard(component)", "Reproject 3D must push regenerated projection primitives back to the active PcbLib board view", failures);
            AssertContains(eePcb, "private static void SyncPcbLibComponentToBoard", "Whole-footprint PcbLib component-to-board sync must stay scoped to reproject and not normal import", failures);
            AssertContains(eePcb, "SetImportedComponentBodyLayer(stepModel)", "Footprint import must place imported 3D model bodies on Mechanical Layer 1", failures);
            AssertContains(eePcb, "SetImportedComponentBodyLayer(body)", "3D body model-origin updates must restore Mechanical Layer 1 after SetState_FromModel", failures);
            AssertContains(eePcb, "V7_Layer.MechanicalLayer(1)", "Imported 3D bodies must use Altium's V7 mechanical layer index 1 helper like altium-mcp", failures);
            AssertDoesNotContain(eePcb, "TryInvoke(target, \"SetState_Layer\", layerNumber)", "Imported 3D bodies must not pass V7 layer number 1 into the legacy Layer setter", failures);
            AssertContains(footprint3dModel, "EEPCB.SetImportedComponentBodyLayer(body)", "Footprint import must restore Mechanical Layer 1 after adding the 3D body to the PcbLib", failures);
            AssertContains(footprint3dModel, "Add3dBodyProjection(c, projectionPrimitives, true)", "Footprint import must add generated Mechanical 2 projection primitives directly to both component and board view", failures);
            AssertContains(footprintData, "AddAssemblyTexts(c, ctx.HasAssemblyDesignatorText, ctx.HasAssemblyCommentText, ctx.Box.Height, ctx.ProjectionPrimitives, true)", "Footprint import must add generated Mechanical 2 assembly texts directly to both component and board view", failures);
            AssertDoesNotContain(footprintData, "SyncPcbLibComponentToBoard(c)", "Footprint import must not transfer all component primitives onto the board because it can disturb pad locations", failures);
            AssertContains(eePcb, "TransferAllPrimitivesOntoBoard", "Reproject 3D must call the PcbLib transfer-onto-board API after regenerating component primitives", failures);
            AssertContains(eePcb, "AddProjectionPrimitive(c, pcbPrimitive, addToBoardView)", "Generated Mechanical 2 projection primitives must use the common import/reproject ownership helper", failures);
            AssertContains(eePcb, "AddToPcbLibComponent(c, primitive)", "Default reprojected Mechanical 2 projection primitives must be added to the footprint component, not duplicated through the board helper", failures);
            AssertContains(eePcb, "EnumerateFilteredComponentProjectionPrimitives", "Reproject 3D cleanup must use filtered PcbLib iterators so old Mechanical 2 projection tracks are visible", failures);
            AssertContains(eePcb, "GetCurrentPcbLibraryBoard", "Reproject 3D cleanup must also scan the active PcbLib board for free Mechanical 2 projection primitives", failures);
            AssertContains(eePcb, "AddDistinctBoard(boards, GetCurrentPcbLibraryBoard())", "Reproject 3D cleanup must remove primitives from the active PcbLib board, not only the component board", failures);
            AssertContains(eePcb, "ClearMechanical2ByEditorCommand", "Reproject 3D cleanup must use PcbLib editor select/delete when SDK primitive enumeration hides visible Mechanical 2 objects", failures);
            AssertContains(eePcb, "LaunchPcbCommand(\"PCB:DeSelect\", \"Scope=All\")", "Reproject 3D cleanup must clear editor selection before selecting Mechanical 2 objects", failures);
            AssertContains(eePcb, "LaunchPcbCommand(\"PCB:Select\", \"Scope=Layer\")", "Reproject 3D cleanup must select all objects on Mechanical 2 through the PCB editor command", failures);
            AssertContains(eePcb, "LaunchPcbCommand(\"PCB:DeleteObjects\", \"Object=FOCUSED\")", "Reproject 3D cleanup must use the same DeleteObjects mode as the working altium-mcp PcbLib editor cleanup", failures);
            AssertContains(eePcb, "DXP.Utils.RunCommand", "Reproject 3D cleanup must send PCB editor commands through the view-less SDK command helper when CurrentView is unavailable", failures);
            AssertContains(eePcb, "MessageRouterSendCommandToModule", "Reproject 3D cleanup should retain message router fallback when a document view is available", failures);
            AssertContains(eePcb, "new V7_Layer(TLayerConstant.eMechanical2).Number()", "Reproject 3D cleanup must filter by V7 mechanical layer number, not raw TLayerConstant enum value", failures);
            AssertContains(eePcb, "CreatePcbLayerSet(TLayerConstant.eMechanical2)", "Reproject 3D cleanup must use Altium's typed IPCB_LayerSet factory for Mechanical 2 when available", failures);
            AssertContains(eePcb, "AddFilter_IPCB_LayerSet", "Reproject 3D cleanup must apply typed IPCB_LayerSet filters like AltiumScript LayerSet helpers", failures);
            AssertContains(eePcb, "AddFilter_ObjectSet", "Reproject 3D cleanup must filter iterators by projection object types", failures);
            AssertContains(eePcb, "AddFilter_LayerSet", "Reproject 3D cleanup must filter iterators by Mechanical 2", failures);
            AssertContains(eePcb, "ReprojectComponentBodySilhouette", "Reproject 3D must regenerate silhouette primitives from a 3D body", failures);
            AssertContains(eePcb, "Rotation2D = FootprintModelPlacement.ProjectionPlacementRotationDeg()", "Reproject 3D must apply the common Altium 180-degree projection placement correction", failures);
            AssertContains(footprint3dModel, "Rotation2D = FootprintModelPlacement.ProjectionPlacementRotationDeg()", "3D model import must apply the common Altium 180-degree projection placement correction", failures);
            AssertContains(footprint3dModel, "StepSilhouetteProjection.Generate(", "3D model import should project from in-memory STEP bytes instead of reloading the already-written body file", failures);
            AssertContains(footprint3dModel, "footprintModel,", "3D model import should pass the already-loaded STEP bytes to silhouette projection", failures);
            AssertContains(module, "ReprojectComponentBodySilhouette(component, out removedCount)", "Reproject 3D must only clear Mechanical 2 after projection generation succeeds", failures);
            AssertContains(eePcb, "BeginPcbPrimitiveModify(component)", "Reproject 3D must open a footprint primitive modify transaction for undo", failures);
            AssertContains(eePcb, "EndPcbPrimitiveModify(component, modifying, changed)", "Reproject 3D must close or cancel the footprint primitive modify transaction for undo", failures);
            AssertContains(eePcb, "SaveModelToFile failed for 3D body projection, trying model fallback", "Reproject 3D must continue to model export fallback when SaveModelToFile is unsupported", failures);
            AssertContains(eePcb, "AlignComponentBodiesToPads", "Align 3D model must align bodies using pad bounds", failures);
            AssertContains(eePcb, "body.BeginModify()", "Align 3D model must open a primitive modify transaction for undo", failures);
            AssertContains(eePcb, "body.EndModify()", "Align 3D model must close a changed primitive modify transaction for undo", failures);
            AssertContains(eePcb, "body.CancelModify()", "Align 3D model must cancel the primitive modify transaction when no change was applied", failures);
            AssertContains(eePcb, "TranslateComponentBodyModelOriginMm(body, move.XMm, move.YMm)", "Align 3D model must attempt body model-origin translation", failures);
            AssertDoesNotContain(eePcb, "body.MoveByXY", "Align 3D model must never move an already-owned component body with primitive MoveByXY in PcbLib", failures);
            AssertContains(eePcb, "FindCurrentPcbLibComponentFallback", "PcbLib commands must find the active footprint even when CurrentComponent is not populated", failures);

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("PcbLib action regression test failed.");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            Console.WriteLine("PcbLib action regression test passed.");
            return 0;
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

        private static void AssertNoForbiddenLibrarySaveCalls(string filePath, string label, List<string> failures)
        {
            string[] lines = File.ReadAllLines(filePath);
            string[] forbiddenTokens =
            {
                "SaveDocument",
                "SaveObject",
                "WorkspaceManager:SaveObject",
                "CloseDocument"
            };

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                foreach (string forbiddenToken in forbiddenTokens)
                {
                    if (line.IndexOf(forbiddenToken, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        failures.Add(
                            label +
                            " line " +
                            (i + 1).ToString(CultureInfo.InvariantCulture) +
                            " contains forbidden import-time library persistence call: " +
                            line);
                    }
                }
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

            string inferredConnectorDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "CONN-SMD_DF56_40S_0.3V_51",
                componentDescription: "DF56-40S-0.3V(51)",
                packageTitle: "CONN-SMD_DF56_40S_0.3V_51",
                packageName: "CONN-SMD_DF56_40S_0.3V_51",
                partNumber: "CONN-SMD_DF56_40S_0.3V_51",
                mounting: "SMT",
                parameters: new Dictionary<string, string>(),
                geometry: new FootprintDescriptionGeometry
                {
                    PositionCount = 40,
                    PitchMm = 0.3,
                    BodyWidthMm = 6.2,
                    BodyHeightMm = 4.8
                });

            AssertEqual(
                "HRS DF56, 40-position vertical SMT female connector, 0.3 mm pitch, 6.2 x 4.8 mm body",
                inferredConnectorDescription,
                "connector footprint descriptions should be synthesized from package identity and geometry without manufacturer metadata",
                failures);

            string catalogConnectorDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "CONN-SMD_DF56_40S_0.3V_51",
                componentDescription: "CONN-SMD_DF56_40S_0.3V_51",
                packageTitle: "CONN-SMD_DF56_40S_0.3V_51",
                packageName: "CONN-SMD_DF56_40S_0.3V_51",
                partNumber: "C5334147",
                mounting: "SMT",
                parameters: new Dictionary<string, string>(),
                geometry: new FootprintDescriptionGeometry
                {
                    PositionCount = 40,
                    PitchMm = 0.3,
                    BodyWidthMm = 6.2,
                    BodyHeightMm = 4.8
                });

            AssertEqual(
                "HRS DF56, 40-position vertical SMT female connector, 0.3 mm pitch, 6.2 x 4.8 mm body",
                catalogConnectorDescription,
                "connector footprint descriptions should ignore LCSC catalog IDs and use inferred manufacturer identity",
                failures);

            string parameterBlobConnectorDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "Number of Pins:40P Pitch:0.6mm Mounting Type:Surface Mount,Vertical Number of Rows:2 Connection Type:Slot Type Butting Contact Material:Phosphor bronze Contact Plating:Tin",
                componentDescription: "Number of Pins:40P Pitch:0.6mm Mounting Type:Surface Mount,Vertical Number of Rows:2 Connection Type:Slot Type Butting Contact Material:Phosphor bronze Contact Plating:Tin",
                packageTitle: "CONN-SMD_DF56_40S_0.3V_51",
                packageName: "CONN-SMD_DF56_40S_0.3V_51",
                partNumber: "DF56C-40S-0.3V(51)",
                mounting: "SMT",
                parameters: new Dictionary<string, string>(),
                geometry: new FootprintDescriptionGeometry
                {
                    PositionCount = 40,
                    PitchMm = 0.3,
                    BodyWidthMm = 6.2,
                    BodyHeightMm = 4.8
                });

            AssertEqual(
                "HRS DF56C, 40-position vertical SMT female connector, 0.3 mm pitch, 6.2 x 4.8 mm body",
                parameterBlobConnectorDescription,
                "connector footprint descriptions should synthesize rich text instead of keeping EasyEDA parameter blobs",
                failures);

            string genericPackageDescription = FootprintMetadataSelector.SelectDescription(
                productDescription: "LM317",
                componentDescription: "SOT-223",
                packageTitle: "SOT-223",
                packageName: "SOT-223",
                partNumber: "LM317",
                mounting: "SMT",
                parameters: new Dictionary<string, string>(),
                geometry: new FootprintDescriptionGeometry
                {
                    PositionCount = 3,
                    PitchMm = 2.3,
                    BodyWidthMm = 6.5,
                    BodyHeightMm = 3.5
                });

            AssertEqual(
                "SOT-223 package, 3-pad SMT footprint, 2.3 mm pitch, 6.5 x 3.5 mm body",
                genericPackageDescription,
                "generic footprint descriptions should use package, pad count, mounting, and body geometry",
                failures);

            AssertEqual(
                "SOT-223",
                FootprintMetadataSelector.SelectName("SOT-223", "LM317"),
                "generic IC footprint names should prefer the standard package over the part number",
                failures);

            AssertEqual(
                "MR30PB-M30.A.G.Y",
                FootprintMetadataSelector.SelectName("CONN-TH_MR30PB-M30.A.G.Y", "MR30PB-M30.A.G.Y"),
                "part-number-specific connector footprint names should keep the manufacturer part number",
                failures);

            AssertEqual(
                "DF56C-30S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51", "DF56C-30S-0.3V(51)"),
                "part-number-specific connector footprint names should handle package suffix notation variants",
                failures);

            AssertEqual(
                "DF56C-26S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_26P-P0.30_DF56C-26S-0.3V51", "DF56C-26S-0.3V(51)"),
                "part-number-specific connector footprint names should handle omitted suffix separators",
                failures);

            AssertEqual(
                "DF56-40S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_DF56_40S_0.3V_51", "DF56-40S-0.3V(51)"),
                "part-number-specific connector footprint names should handle underscore-separated package names",
                failures);

            AssertEqual(
                "DF56C-40S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_DF56_40S_0.3V_51", "DF56C-40S-0.3V(51)"),
                "part-number-specific connector footprint names should prefer manufacturer part numbers even when package family omits a variant letter",
                failures);

            AssertEqual(
                "DF56C-26S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_26P-P0.30_DF56C-26S-0.3V51", "CONN-SMD_26P-P0.30_DF56C-26S-0.3V51"),
                "part-number-specific connector footprint names should be inferred when import passes the package as the part number",
                failures);

            AssertEqual(
                "DF56-40S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_DF56_40S_0.3V_51", "CONN-SMD_DF56_40S_0.3V_51"),
                "underscore-separated connector footprint names should be inferred when import passes the package as the part number",
                failures);

            AssertEqual(
                "DF56-40S-0.3V(51)",
                FootprintMetadataSelector.SelectName("CONN-SMD_DF56_40S_0.3V_51", "C5334147"),
                "underscore-separated connector footprint names should be inferred when import passes an LCSC catalog ID",
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
                "3-position vertical through-hole male PCB power plug connector, 3.5 mm pitch",
                SymbolImportRules.SelectSymbolDescription(
                    productDescription: "",
                    componentDescription: "",
                    packageTitle: "MR30PB-M30.A.G.Y",
                    packageName: "CONN-TH_MR30PB-M30.A.G.Y",
                    partNumber: "MR30PB-M30.A.G.Y",
                    mounting: "through-hole",
                    parameters: new Dictionary<string, string>
                    {
                        { "Manufacturer", "AMASS(艾迈斯)" },
                        { "Manufacturer Part", "MR30PB-M30.A.G.Y" },
                        { "LCSC Part Name", "AMASS(艾迈斯)三芯动力电池马达e电调航模插头连接器 PCB板立式插头公头 金 黄MR30PB-M30.A.G.Y" }
                    },
                    geometry: CreateMr30FootprintGeometry()),
                "schematic description should synthesize key selection facts without manufacturer or part-number-only text",
                failures);

            AssertEqual(
                "BM08B-GHS-TBT",
                SymbolImportRules.SelectLibraryComment("BM08B-GHS-TBT"),
                "symbol comment should equal design item ID instead of Altium default *",
                failures);

            AssertEqual(
                "X?",
                SymbolImportRules.SelectVisibleDesignator("X?"),
                "visible schematic designator should use the selected GOST prefix instead of Altium default *",
                failures);

            AssertEqual(
                "BM08B-GHS-TBT",
                SymbolImportRules.SelectDesignItemId(
                    manufacturerPart: "",
                    symbolName: "*",
                    componentTitle: "C123456",
                    searchResultName: "C123456",
                    searchPart: "BM08B-GHS-TBT",
                    lcscNumber: "C123456",
                    szlcscNumber: ""),
                "design item ID must skip EasyEDA placeholder * and catalog IDs before falling back to manufacturer-style search part",
                failures);

            AssertEqual(
                "DD?",
                SymbolImportRules.SelectDesignator("*", "STM32F103C8T6", "", "LQFP-48"),
                "placeholder source designator must not become the visible schematic designator",
                failures);

            AssertEqual(
                "",
                SymbolImportRules.SelectLibraryComment("*"),
                "symbol comment selector must reject EasyEDA placeholder *",
                failures);

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

        private static void AssertContains(string text, string expectedSubstring, string message, List<string> failures)
        {
            if (text == null || text.IndexOf(expectedSubstring, StringComparison.Ordinal) < 0)
                failures.Add(message + ": missing '" + expectedSubstring + "'.");
        }

        private static void AssertDoesNotContain(string text, string unexpectedSubstring, string message, List<string> failures)
        {
            if (text != null && text.IndexOf(unexpectedSubstring, StringComparison.Ordinal) >= 0)
                failures.Add(message + ": found '" + unexpectedSubstring + "'.");
        }

        private static void AssertAppearsBefore(
            string text,
            string earlierSubstring,
            string laterSubstring,
            string message,
            List<string> failures)
        {
            int earlierIndex = text?.IndexOf(earlierSubstring, StringComparison.Ordinal) ?? -1;
            int laterIndex = text?.IndexOf(laterSubstring, StringComparison.Ordinal) ?? -1;
            if (earlierIndex < 0 || laterIndex < 0 || earlierIndex >= laterIndex)
            {
                failures.Add(
                    message +
                    ": expected '" +
                    earlierSubstring +
                    "' before '" +
                    laterSubstring +
                    "'.");
            }
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
            var placement = CreateDefaultSilhouettePlacement();
            placement.RotX = rotX;
            placement.RotY = rotY;
            placement.RotZ = rotZ;
            placement.Rotation2D = rotation2D;

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

        private static int SaveSilhouettePrimitiveDump(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --silhouette-dump <input.step> <output.csv>");
                return 2;
            }

            string inputPath = args[1];
            string outputPath = args[2];
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + inputPath);
                return 2;
            }

            IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(
                File.ReadAllBytes(inputPath),
                CreateDefaultSilhouettePlacement());
            using (var writer = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                writer.WriteLine("index,kind,x1,y1,x2,y2,centerX,centerY,radius,startAngle,endAngle");
                for (int index = 0; index < primitives.Count; index++)
                {
                    StepSilhouettePrimitive primitive = primitives[index];
                    writer.Write(index.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.Kind.ToString());
                    writer.Write(",");
                    writer.Write(primitive.X1.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.Y1.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.X2.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.Y2.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.CenterX.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.CenterY.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.Radius.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.Write(primitive.StartAngle.ToString(CultureInfo.InvariantCulture));
                    writer.Write(",");
                    writer.WriteLine(primitive.EndAngle.ToString(CultureInfo.InvariantCulture));
                }
            }

            Console.WriteLine("Silhouette primitive dump written: " + Path.GetFullPath(outputPath));
            Console.WriteLine("Primitives: " + primitives.Count.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private static int RunOcctHlrBenchmark(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-hlr-benchmark <input.step> [--repeat count]");
                return 2;
            }

            string inputPath = args[1];
            int repeatCount = 1;
            for (int index = 2; index < args.Length; index++)
            {
                string option = args[index];
                if (IsOption(option, "--repeat"))
                {
                    if (!TryReadIntOption(args, ref index, option, out repeatCount))
                        return 2;
                    repeatCount = Math.Max(1, repeatCount);
                    continue;
                }

                Console.Error.WriteLine("Unknown OCCT HLR benchmark option: " + option);
                return 2;
            }

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine("STEP file does not exist: " + inputPath);
                return 2;
            }

            byte[] stepData = File.ReadAllBytes(inputPath);
            bool failed = false;
            for (int runIndex = 1; runIndex <= repeatCount; runIndex++)
            {
                var stopwatch = Stopwatch.StartNew();
                IReadOnlyList<StepSilhouettePrimitive> primitives = StepSilhouetteProjection.Generate(
                    stepData,
                    CreateDefaultSilhouettePlacement());
                stopwatch.Stop();

                int lineCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Line);
                int arcCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Arc);
                Console.WriteLine(
                    "run_index=" + runIndex.ToString(CultureInfo.InvariantCulture) +
                    " total_ms=" + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    " line_count=" + lineCount.ToString(CultureInfo.InvariantCulture) +
                    " arc_count=" + arcCount.ToString(CultureInfo.InvariantCulture) +
                    " primitive_count=" + primitives.Count.ToString(CultureInfo.InvariantCulture));

                if (lineCount < OcctHlrBenchmarkMinimumExpectedLines ||
                    arcCount < OcctHlrBenchmarkMinimumExpectedArcs)
                {
                    failed = true;
                    Console.Error.WriteLine(
                        "OCCT HLR benchmark primitive count below smoke threshold: " +
                        lineCount.ToString(CultureInfo.InvariantCulture) +
                        " line(s), " +
                        arcCount.ToString(CultureInfo.InvariantCulture) +
                        " arc(s), " +
                        primitives.Count.ToString(CultureInfo.InvariantCulture) +
                        " total.");
                }
            }

            return failed ? 1 : 0;
        }

        private static StepSilhouettePlacement CreateDefaultSilhouettePlacement()
        {
            return new StepSilhouettePlacement
            {
                TargetBounds = new StepSilhouetteBounds
                {
                    Left = -0.5,
                    Bottom = -0.5,
                    Right = 0.5,
                    Top = 0.5
                },
                RotX = 0.0,
                RotY = 0.0,
                RotZ = 0.0,
                Rotation2D = 0.0
            };
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

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
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
            FullTestDetectionCache detectionCache,
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

                projectionTimings.Measure(
                    "post_clean_detection_ms",
                    () => detectionCache.GetReport(originalFile));
                var detectionRegions = projectionTimings.Measure(
                    "post_clean_detection_region_projection_ms",
                    () => detectionCache.GetDetectionRegions(originalFile, projectionOptions).ToList());

                string modelName = Path.GetFileNameWithoutExtension(fileName);
                var detectedViewNames = detectionRegions
                    .Select(region => region.ViewName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(viewName => viewName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                detectionViewNamesByFileName[fileName] = detectedViewNames;
                if (detectedViewNames.Count == 0)
                {
                    if (RequiresAutomaticWatermarkCleanup(fileName))
                    {
                        postCleanFaultFileNames.Add(fileName);
                        failures.Add(fileName + " has no detected watermark cleanup views for a known watermark fixture.");
                    }

                    continue;
                }

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
                WriteMetadata = false,
                SkipGeometryModelForExternalRender = true,
                MaxParallelFiles = 2
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
                SkipGeometryModelForExternalRender = template.SkipGeometryModelForExternalRender,
                MaxParallelFiles = template.MaxParallelFiles,
                RenderMode = template.RenderMode
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
                int[] originalPixels = CopyBitmapPixelsToInt32Rows(originalImage);
                int[] cleanPixels = CopyBitmapPixelsToInt32Rows(cleanImage);

                for (int y = 0; y < originalImage.Height; y++)
                {
                    int row = y * originalImage.Width;
                    for (int x = 0; x < originalImage.Width; x++)
                    {
                        int pixelIndex = row + x;
                        if (!PixelsDifferent(originalPixels[pixelIndex], cleanPixels[pixelIndex], ProjectionDifferenceTolerance))
                            continue;

                        if (allowedMask[pixelIndex])
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
            if (originalEdgeRatio < MinOriginalRegionEdgeRatioForFlatness)
                return;

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

        private static bool PixelsDifferent(int left, int right, int tolerance)
        {
            int color0 = Math.Abs((left & 0xFF) - (right & 0xFF));
            int color1 = Math.Abs(((left >> 8) & 0xFF) - ((right >> 8) & 0xFF));
            int color2 = Math.Abs(((left >> 16) & 0xFF) - ((right >> 16) & 0xFF));
            int alpha = Math.Abs(((left >> 24) & 0xFF) - ((right >> 24) & 0xFF));
            return Math.Max(color0, Math.Max(color1, color2)) > tolerance ||
                alpha > tolerance;
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
            FullTestDetectionCache detectionCache,
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
            long loadMarkedRegionsMs = 0;
            long cacheCheckMs = 0;
            long detectMs = 0;
            long projectDetectionFileMs = 0;
            foreach (string originalFile in originalFiles)
            {
                Stopwatch stageStopwatch = Stopwatch.StartNew();
                var markedRegions = StepWatermarkCleaner.LoadMarkedRegionsForStepFile(
                    originalFile,
                    projectionDirectory,
                    markedDirectory);
                stageStopwatch.Stop();
                loadMarkedRegionsMs += stageStopwatch.ElapsedMilliseconds;

                stageStopwatch = Stopwatch.StartNew();
                if (IsDetectionDebugImageCacheFresh(originalFile, markedRegions, detectionDirectory))
                {
                    stageStopwatch.Stop();
                    cacheCheckMs += stageStopwatch.ElapsedMilliseconds;
                    cachedModels++;
                    continue;
                }
                stageStopwatch.Stop();
                cacheCheckMs += stageStopwatch.ElapsedMilliseconds;

                stageStopwatch = Stopwatch.StartNew();
                var detectionReport = detectionCache.GetReport(originalFile);
                stageStopwatch.Stop();
                detectMs += stageStopwatch.ElapsedMilliseconds;

                stageStopwatch = Stopwatch.StartNew();
                StepProjectionRenderer.ProjectDetectionFile(
                    originalFile,
                    detectionDirectory,
                    detectionReport,
                    new StepProjectionOptions
                    {
                        WriteMetadata = false
                    },
                    markedRegions);
                stageStopwatch.Stop();
                projectDetectionFileMs += stageStopwatch.ElapsedMilliseconds;
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
            Console.WriteLine("  detection_debug_load_marked_regions_ms=" + loadMarkedRegionsMs.ToString(CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine("  detection_debug_cache_check_ms=" + cacheCheckMs.ToString(CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine("  detection_debug_detect_ms=" + detectMs.ToString(CultureInfo.InvariantCulture) + " ms");
            Console.WriteLine("  detection_debug_project_file_ms=" + projectDetectionFileMs.ToString(CultureInfo.InvariantCulture) + " ms");

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
                        postCleanFaultFileNames.Add(fileName);
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
                comparedImages.ToString(CultureInfo.InvariantCulture));
        }

        private static bool ProjectionPixelsEqual(string cleanProjectionPath, string validatedProjectionPath)
        {
            if (FilesEqualByLengthAndBytes(cleanProjectionPath, validatedProjectionPath))
                return true;

            using (var cleanImage = SKBitmap.Decode(cleanProjectionPath))
            using (var validatedImage = SKBitmap.Decode(validatedProjectionPath))
            {
                if (cleanImage == null || validatedImage == null)
                    return false;

                if (cleanImage.Width != validatedImage.Width || cleanImage.Height != validatedImage.Height)
                    return false;

                int[] cleanPixels = CopyBitmapPixelsToInt32Rows(cleanImage);
                int[] validatedPixels = CopyBitmapPixelsToInt32Rows(validatedImage);
                for (int i = 0; i < cleanPixels.Length; i++)
                {
                    if (cleanPixels[i] != validatedPixels[i])
                        return false;
                }

                return true;
            }
        }

        private static int[] CopyBitmapPixelsToInt32Rows(SKBitmap bitmap)
        {
            if (bitmap == null)
                throw new ArgumentNullException(nameof(bitmap));

            int[] pixels = new int[bitmap.Width * bitmap.Height];
            IntPtr source = bitmap.GetPixels();
            if (source == IntPtr.Zero)
                return pixels;

            int bytesPerPixel = bitmap.BytesPerPixel;
            if (bytesPerPixel != 4)
                return pixels;

            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr row = IntPtr.Add(source, y * bitmap.RowBytes);
                Marshal.Copy(row, pixels, y * bitmap.Width, bitmap.Width);
            }

            return pixels;
        }

        private static bool FilesEqualByLengthAndBytes(string leftPath, string rightPath)
        {
            var leftInfo = new FileInfo(leftPath);
            var rightInfo = new FileInfo(rightPath);
            if (leftInfo.Length != rightInfo.Length)
                return false;

            byte[] leftBytes = File.ReadAllBytes(leftPath);
            byte[] rightBytes = File.ReadAllBytes(rightPath);
            return leftBytes.SequenceEqual(rightBytes);
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

        private static IReadOnlyList<CleanupExpectation> GetCleanupExpectations()
        {
            return new[]
            {
                new CleanupExpectation
                {
                    FileName = "CONN-TH_XT60PB-M.step",
                    Note = "CONN-TH_XT60PB-M.step bottom geometric LCEDA watermark should be removed from the z_minus view."
                },
                new CleanupExpectation
                {
                    FileName = "LED-SMD_XL-3838UV2SA06G3.step",
                    Note = "LED-SMD_XL-3838UV2SA06G3.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "USB-A-TH_FUS264-FDSW3K.step",
                    Note = "USB-A-TH_FUS264-FDSW3K.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "CONN-TH_MR30PW-M30-G-Y.step",
                    Note = "CONN-TH_MR30PW-M30-G-Y.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "USB-B-TH_USB-B10-BRW.step",
                    Note = "USB-B-TH_USB-B10-BRW.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
                    Note = "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step cleaned output should be reviewed as cleaned."
                },
                new CleanupExpectation
                {
                    FileName = "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50.step",
                    Note = "SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50.step cleaned output should be reviewed as cleaned."
                }
            };
        }

        private static IReadOnlyList<string> GetCleanupNotes()
        {
            return GetCleanupExpectations()
                .Select(expectation => expectation.Note)
                .ToList();
        }

        private static bool RequiresAutomaticWatermarkCleanup(string fileName)
        {
            string actualFileName = Path.GetFileName(fileName);
            return GetCleanupExpectations()
                .Any(expectation => string.Equals(expectation.FileName, actualFileName, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class CleanupExpectation
        {
            public string FileName { get; set; }
            public string Note { get; set; }
        }

        private sealed class TextLogoDetectionExpectation
        {
            public string FileName { get; set; }
            public string ViewName { get; set; }
            public string RequiredTemplate { get; set; }
            public List<string> ExpectedTemplates { get; set; }
            public int ExpectedX { get; set; }
            public int ExpectedY { get; set; }
            public int ExpectedWidth { get; set; }
            public int ExpectedHeight { get; set; }
            public int BoundsTolerance { get; set; }
            public double MinScore { get; set; }
            public double MaxChamferDistance { get; set; }
            public double MaxUnexpectedHighScore { get; set; }
        }

        private sealed class MarkedRegionFile
        {
            public List<MarkedRectangle> Rectangles { get; set; }
        }

        private sealed class MarkedRectangle
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private readonly struct MarkedRectI
        {
            public MarkedRectI(int x, int y, int width, int height)
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

            public override string ToString()
            {
                return "[" +
                    X.ToString(CultureInfo.InvariantCulture) +
                    "," +
                    Y.ToString(CultureInfo.InvariantCulture) +
                    " " +
                    Width.ToString(CultureInfo.InvariantCulture) +
                    "x" +
                    Height.ToString(CultureInfo.InvariantCulture) +
                    "]";
            }
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

        private sealed class FullTestDetectionCache
        {
            private readonly Dictionary<string, StepWatermarkDetectionReport> _reportsByFileName =
                new Dictionary<string, StepWatermarkDetectionReport>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, IReadOnlyList<StepProjectionDetectionRegion>> _regionsByKey =
                new Dictionary<string, IReadOnlyList<StepProjectionDetectionRegion>>(StringComparer.OrdinalIgnoreCase);

            public StepWatermarkDetectionReport GetReport(string originalFile)
            {
                string fileName = Path.GetFileName(originalFile);
                if (!_reportsByFileName.TryGetValue(fileName, out StepWatermarkDetectionReport report))
                {
                    report = StepWatermarkCleaner.Detect(
                        File.ReadAllBytes(originalFile),
                        new StepWatermarkCleanerOptions());
                    _reportsByFileName[fileName] = report;
                }

                return report;
            }

            public IReadOnlyList<StepProjectionDetectionRegion> GetDetectionRegions(
                string originalFile,
                StepProjectionOptions projectionOptions)
            {
                string key =
                    Path.GetFileName(originalFile) +
                    "|" +
                    projectionOptions.ImageSizePixels.ToString(CultureInfo.InvariantCulture) +
                    "|" +
                    projectionOptions.PaddingPixels.ToString(CultureInfo.InvariantCulture) +
                    "|" +
                    string.Join(",", projectionOptions.ViewNames);

                if (!_regionsByKey.TryGetValue(key, out IReadOnlyList<StepProjectionDetectionRegion> regions))
                {
                    var verifiedReport = StepWatermarkCleaner.CreateVerifiedCleanupDetectionReport(GetReport(originalFile));
                    regions = StepProjectionRenderer.ProjectDetectionRegions(
                            originalFile,
                            verifiedReport,
                            projectionOptions)
                        .ToList();
                    _regionsByKey[key] = regions;
                }

                return regions;
            }
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

        private static Dictionary<int, string> ParseStepEntityDefinitions(string stepText)
        {
            var result = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(stepText))
                return result;

            foreach (Match match in Regex.Matches(
                stepText,
                @"#(\d+)\s*=\s*(.*?);",
                RegexOptions.Singleline | RegexOptions.CultureInvariant))
            {
                int id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                result[id] = NormalizeStepEntityDefinition(match.Groups[2].Value);
            }

            return result;
        }

        private static IEnumerable<int> GetAdvancedFaceIds(string stepText)
        {
            foreach (var kvp in ParseStepEntityDefinitions(stepText))
            {
                if (kvp.Value.StartsWith("ADVANCED_FACE", StringComparison.OrdinalIgnoreCase))
                    yield return kvp.Key;
            }
        }

        private static IEnumerable<int> GetActiveAdvancedFaceIds(string stepText)
        {
            Dictionary<int, string> entities = ParseStepEntityDefinitions(stepText);
            var referencedFaceIds = new HashSet<int>();
            foreach (string definition in entities.Values)
            {
                if (!definition.StartsWith("CLOSED_SHELL", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (Match match in Regex.Matches(definition, @"#(\d+)", RegexOptions.CultureInvariant))
                {
                    int referencedId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (entities.TryGetValue(referencedId, out string referencedDefinition) &&
                        referencedDefinition.StartsWith("ADVANCED_FACE", StringComparison.OrdinalIgnoreCase))
                    {
                        referencedFaceIds.Add(referencedId);
                    }
                }
            }

            return referencedFaceIds.OrderBy(id => id).ToList();
        }

        private static void VerifyProtectedFaceEntityClosurePreserved(
            int faceId,
            IReadOnlyDictionary<int, string> originalEntities,
            IReadOnlyDictionary<int, string> cleanedEntities,
            IReadOnlyDictionary<int, string> removedEntities,
            List<string> failures)
        {
            HashSet<int> closure = BuildStepReferenceClosure(faceId, originalEntities);
            if (closure.Count == 0)
            {
                failures.Add("Could not build protected face closure for #" + faceId.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }

            foreach (int entityId in closure.OrderBy(id => id))
            {
                if (!originalEntities.TryGetValue(entityId, out string originalDefinition))
                    continue;

                if (!cleanedEntities.TryGetValue(entityId, out string cleanedDefinition))
                {
                    failures.Add(
                        "Protected contact face #" +
                        faceId.ToString(CultureInfo.InvariantCulture) +
                        " lost referenced entity #" +
                        entityId.ToString(CultureInfo.InvariantCulture) +
                        " in cleaned STEP.");
                    continue;
                }

                if (!string.Equals(originalDefinition, cleanedDefinition, StringComparison.Ordinal))
                {
                    failures.Add(
                        "Protected contact face #" +
                        faceId.ToString(CultureInfo.InvariantCulture) +
                        " referenced entity #" +
                        entityId.ToString(CultureInfo.InvariantCulture) +
                        " changed in cleaned STEP.");
                }

                if (removedEntities.ContainsKey(entityId))
                {
                    failures.Add(
                        "Protected contact face #" +
                        faceId.ToString(CultureInfo.InvariantCulture) +
                        " referenced entity #" +
                        entityId.ToString(CultureInfo.InvariantCulture) +
                        " was exported as removed geometry.");
                }
            }
        }

        private static HashSet<int> BuildStepReferenceClosure(
            int rootId,
            IReadOnlyDictionary<int, string> entities)
        {
            var result = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(rootId);

            while (pending.Count > 0)
            {
                int entityId = pending.Pop();
                if (!result.Add(entityId))
                    continue;

                if (!entities.TryGetValue(entityId, out string definition))
                    continue;

                foreach (Match match in Regex.Matches(
                    definition,
                    @"#(\d+)",
                    RegexOptions.CultureInvariant))
                {
                    int referencedId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (!result.Contains(referencedId))
                        pending.Push(referencedId);
                }
            }

            foreach (KeyValuePair<int, string> entity in entities)
            {
                if (entity.Key == rootId || result.Contains(entity.Key))
                    continue;

                if (Regex.IsMatch(
                    entity.Value,
                    @"^STYLED_ITEM\s*\(",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                    Regex.IsMatch(
                        entity.Value,
                        @"#" + rootId.ToString(CultureInfo.InvariantCulture) + @"(?!\d)",
                        RegexOptions.CultureInvariant))
                {
                    result.Add(entity.Key);
                }
            }

            return result;
        }

        private static string NormalizeStepEntityDefinition(string definition)
        {
            return Regex.Replace(
                    definition ?? string.Empty,
                    @"\s+",
                    " ",
                    RegexOptions.CultureInvariant)
                .Trim();
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
