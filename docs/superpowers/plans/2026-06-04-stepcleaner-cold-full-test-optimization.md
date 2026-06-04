# StepCleaner Cold Full Test Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Reduce the cold no-cache full `Test\StepCleaner\StepCleaner.Tests.csproj` runtime by attacking the measured projection bottlenecks first.

**Architecture:** Keep F3D as the color-correct renderer, but extend the new libf3d helper so one STEP load can render any requested view subset, not only all six sides. Then let `StepProjectionRenderer` use that batch path for highlighted detection/debug projections and add an explicit render-only verification mode that can skip expensive STEP geometry parsing when metadata and geometry counts are not needed.

**Tech Stack:** C#/.NET 8, `f3d_c_api.dll`, `StepF3DRender`, `StepProjectionRenderer`, `StepCleaner.Tests`, PowerShell timing harness.

---

## Baseline From Cold Run

Command used after clearing generated `Test\StepCleaner\Data` outputs:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Result:

```text
full_test_wall_ms=163976
detection_debug_images=44049 ms
clean_projection_render_ms=28238 ms
post_clean_detection_ms=2870 ms
post_clean_detection_region_projection_ms=3098 ms
original_detection_side_projection_render_ms=39597 ms
original_vs_clean_projection_compare_ms=2921 ms
validated_projection_render_ms=28742 ms
clean_vs_validated_projection_compare_ms=2887 ms
failed_projection_report_ms=3 ms
total_measured_projection_verification_ms=108356 ms
unmeasured remainder=11571 ms
```

Priority order:

1. `detection_debug_images` + `original_detection_side_projection_render_ms`: both still render selected/highlighted views through per-view paths.
2. `clean_projection_render_ms` + `validated_projection_render_ms`: both render full six-side sets but still parse/build STEP geometry before external rendering.
3. Directory-level parallelism: useful only after single-model repeated-load waste is removed.

## Final Measurement Notes

Best cold run after enabling render-only F3D batch paths and scoped `MaxParallelFiles=2`:

```text
full_test_wall_ms=155753
detection_debug_images=50605 ms
clean_projection_render_ms=24670 ms
post_clean_detection_ms=4075 ms
post_clean_detection_region_projection_ms=4384 ms
original_detection_side_projection_render_ms=31740 ms
original_vs_clean_projection_compare_ms=4023 ms
validated_projection_render_ms=20872 ms
clean_vs_validated_projection_compare_ms=2973 ms
total_measured_projection_verification_ms=92740 ms
```

Final no-args regression run after safety fixes for duplicate F3D retries and duplicate basenames:

```text
full_test_wall_ms=196269
detection_debug_images=57222 ms
clean_projection_render_ms=18508 ms
post_clean_detection_ms=10984 ms
post_clean_detection_region_projection_ms=11870 ms
original_detection_side_projection_render_ms=26421 ms
original_vs_clean_projection_compare_ms=12673 ms
validated_projection_render_ms=20262 ms
clean_vs_validated_projection_compare_ms=5958 ms
total_measured_projection_verification_ms=106678 ms
```

Interpretation: the pure projection render buckets improved, especially clean/original/validated renders. End-to-end wall time is not stable yet because detection debug image generation, post-clean detection, and image comparison still dominate or vary run-to-run. The next optimization pass should focus on those stages before increasing F3D parallelism further.

---

## File Structure

- Modify: `StepF3DRender/Program.cs`
  - Add `--views x_plus,y_plus,...` support.
  - Reuse one F3D engine and one scene load for the requested subset.
  - Keep default `--six-sides` behavior unchanged.

- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`
  - Allow `TryRenderWithF3DLibraryBatch` to render any non-empty view subset.
  - Use the batch renderer inside `ProjectDetectionFile` before falling back to per-view `RenderProjection`.
  - Draw existing detection highlights after batch-rendered PNGs.
  - Add an explicit render-only option that attempts external rendering before parsing STEP geometry.

- Modify: `Test/StepCleaner/Program.cs`
  - Add source guards for `--views`, subset batch rendering, highlighted batch rendering, and render-only external projection.
  - Set render-only mode in `CreateVerificationProjectionOptions()`.
  - Keep the existing full-test timing output and add a single wall-time line for easier before/after comparison.

- Modify: `StepCleaner/StepCleaner.csproj`
  - Only if needed for new linked helper code; currently no planned change after the previous compile include fix.

- Optional Modify: `docs/superpowers/plans/2026-06-03-speed-up-footprint-import.md`
  - Append final measured timing deltas after implementation is verified.

---

### Task 1: Add Guards For The Next Optimization Surface

**Files:**
- Modify: `Test/StepCleaner/Program.cs`

- [x] **Step 1: Write failing source guards**

In `RunModelCacheTests()`, after the existing F3D library helper assertions, add these guards:

```csharp
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
    "full_test_wall_ms",
    "full StepCleaner regression timing should print total wall time for before/after comparisons",
    failures);
```

- [x] **Step 2: Run guard test and verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: FAIL with messages for missing `--views`, highlighted batch use, subset support, render-only option, and `full_test_wall_ms`.

- [x] **Step 3: Commit the failing guards**

```powershell
git add Test\StepCleaner\Program.cs
git commit -m "Add guards for cold full-test projection optimizations"
```

---

### Task 2: Add `--views` To The Libf3d Helper

**Files:**
- Modify: `StepF3DRender/Program.cs`

- [x] **Step 1: Extend request shape**

In `RenderRequest`, add:

```csharp
public IReadOnlyList<string> ViewNames { get; set; }
```

- [x] **Step 2: Parse optional `--views`**

In `ParseArguments`, initialize and parse:

```csharp
List<string> viewNames = Views.Select(view => view.Name).ToList();

for (int i = 3; i < args.Length; i++)
{
    if (IsOption(args[i], "--size") && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out sizePixels))
            throw new ArgumentException("Invalid --size value.");
        continue;
    }

    if (IsOption(args[i], "--views") && i + 1 < args.Length)
    {
        viewNames = ParseViewNames(args[++i]);
        continue;
    }

    throw new ArgumentException("Unknown argument: " + args[i]);
}
```

Add:

```csharp
private static List<string> ParseViewNames(string value)
{
    if (string.IsNullOrWhiteSpace(value))
        throw new ArgumentException("--views requires at least one view name.");

    var result = new List<string>();
    foreach (string rawName in value.Split(','))
    {
        string name = rawName.Trim();
        if (name.Length == 0)
            continue;

        if (!Views.Any(view => string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Unknown view name in --views: " + name);

        if (!result.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            result.Add(name);
    }

    if (result.Count == 0)
        throw new ArgumentException("--views requires at least one view name.");

    return result;
}
```

Set the request:

```csharp
return new RenderRequest
{
    InputPath = Path.GetFullPath(inputPath),
    OutputDirectory = Path.GetFullPath(outputDirectory),
    SizePixels = sizePixels,
    ViewNames = viewNames
};
```

- [x] **Step 3: Render only requested views**

In `RenderSixSides`, replace:

```csharp
foreach (ViewSpec view in Views)
```

with:

```csharp
foreach (ViewSpec view in Views.Where(view =>
    request.ViewNames.Any(name => string.Equals(name, view.Name, StringComparison.OrdinalIgnoreCase))))
```

- [x] **Step 4: Update usage**

Replace the usage text with:

```csharp
Console.Error.WriteLine("Usage: StepF3DRender --six-sides <input.step> <output-directory> [--size pixels] [--views x_plus,y_plus,z_plus]");
```

- [x] **Step 5: Build helper**

Run:

```powershell
dotnet build StepF3DRender\StepF3DRender.csproj
```

Expected: PASS.

- [x] **Step 6: Verify subset command**

Run:

```powershell
$out = Join-Path (Get-Location) 'Test\StepCleaner\Data\F3DSubsetSmoke'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null
& '.\StepF3DRender\bin\Debug\net8.0-windows7.0\win-x64\StepF3DRender.exe' --six-sides '.\Test\StepCleaner\Data\Clean\USB-C-SMD_TYPE-C-6PIN-2MD-073.step' $out --size 1000 --views x_plus,z_plus
Get-ChildItem -LiteralPath $out -Filter *.png | Select-Object Name,Length
```

Expected: exactly `2` PNGs, both nonempty, and output contains `six_side_f3d_library_ms=`.

- [x] **Step 7: Run source guard**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: still FAIL, but no longer failing the `--views` guard.

- [x] **Step 8: Commit helper subset support**

```powershell
git add StepF3DRender\Program.cs
git commit -m "Support subset views in libf3d batch renderer"
```

---

### Task 3: Use Libf3d Batch Rendering For Highlighted Detection Projections

**Files:**
- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`

- [x] **Step 1: Remove six-view-only restriction**

In `TryRenderWithF3DLibraryBatch`, replace:

```csharp
if (views == null || views.Count != Views.Length)
    return false;
foreach (ViewSpec view in Views)
{
    if (!views.Any(candidate => string.Equals(candidate.Name, view.Name, StringComparison.OrdinalIgnoreCase)) ||
        !outputPathsByView.ContainsKey(view.Name))
    {
        return false;
    }
}
```

with:

```csharp
if (views == null || views.Count == 0)
    return false;
foreach (ViewSpec view in views)
{
    if (!Views.Any(candidate => string.Equals(candidate.Name, view.Name, StringComparison.OrdinalIgnoreCase)) ||
        !outputPathsByView.ContainsKey(view.Name))
    {
        return false;
    }
}
```

- [x] **Step 2: Pass `--views` to helper**

After adding `--size`, add:

```csharp
startInfo.ArgumentList.Add("--views");
startInfo.ArgumentList.Add(string.Join(",", views.Select(view => view.Name)));
```

- [x] **Step 3: Batch-render detection projection base images**

In `ProjectDetectionFile`, after `renderedWithOpenCascadeBatch`, add:

```csharp
bool renderedWithF3DLibraryBatch =
    !renderedWithOpenCascadeBatch &&
    TryRenderWithF3DLibraryBatch(
        inputPath,
        selectedDetectionViews,
        outputPathsByView,
        options);
```

Then update the loop:

```csharp
if (!renderedWithOpenCascadeBatch && !renderedWithF3DLibraryBatch)
{
    RenderProjection(inputPath, drawingModel, view, transform, outputPath, options, highlightsByView[view.Name]);
}
else if (renderedWithF3DLibraryBatch && highlightsByView.TryGetValue(view.Name, out IReadOnlyList<ProjectionHighlight> highlights) && highlights.Count > 0)
{
    var renderedImage = RgbaImage.LoadPng(outputPath);
    DrawDetectionHighlights(renderedImage, view, transform, highlights);
    renderedImage.SavePng(outputPath);
}
```

- [x] **Step 4: Build production project**

Run:

```powershell
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
```

Expected: PASS.

- [x] **Step 5: Run guard test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: still FAIL only for render-only option and wall-time timing guard.

- [x] **Step 6: Measure targeted highlighted path**

Clear generated debug images and run full test once:

```powershell
$data = Join-Path (Get-Location) 'Test\StepCleaner\Data'
Remove-Item -LiteralPath (Join-Path $data 'Clean') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $data 'OriginalCleanCompareProjection') -Recurse -Force -ErrorAction SilentlyContinue
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: PASS. `detection_debug_images` and `original_detection_side_projection_render_ms` should be materially lower than baseline `44049 ms` and `39597 ms`.

- [x] **Step 7: Commit highlighted batch rendering**

```powershell
git add EasyEDA-Loader\StepProjectionRenderer.cs Test\StepCleaner\Data\full_no_cache_timing.log
git reset Test\StepCleaner\Data\full_no_cache_timing.log
git commit -m "Batch highlighted projections through libf3d"
```

---

### Task 4: Add Render-Only External Projection Mode

**Files:**
- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`
- Modify: `Test/StepCleaner/Program.cs`

- [x] **Step 1: Add explicit option**

In `StepProjectionOptions`, add:

```csharp
public bool SkipGeometryModelForExternalRender { get; set; }
```

- [x] **Step 2: Preserve option in clone paths**

Where `StepProjectionOptions` is cloned, add:

```csharp
SkipGeometryModelForExternalRender = options.SkipGeometryModelForExternalRender
```

Specifically update:

```csharp
private static StepProjectionOptions CloneSingleViewOptions(...)
private static StepProjectionOptions CreateProjectionOptionsForViews(...)
```

- [x] **Step 3: Add a fast external-render branch at the start of `ProjectFile`**

After normalizing options and creating `outputDirectory`, before reading/parsing STEP bytes, add:

```csharp
string modelName = Path.GetFileNameWithoutExtension(inputPath);
IReadOnlyList<ViewSpec> selectedViews = GetSelectedViews(options);
Dictionary<string, string> outputPathsByView = selectedViews.ToDictionary(
    view => view.Name,
    view => Path.Combine(outputDirectory, modelName + "__" + view.Name + ".png"),
    StringComparer.OrdinalIgnoreCase);

if (options.SkipGeometryModelForExternalRender &&
    !options.WriteMetadata &&
    TryRenderWithF3DLibraryBatch(inputPath, selectedViews, outputPathsByView, options))
{
    return new StepProjectionReport
    {
        InputPath = inputPath,
        FaceCount = 0,
        EdgeCount = 0,
        OutputFiles = selectedViews.Select(view => outputPathsByView[view.Name]).ToList()
    };
}
```

Then remove the duplicate later declarations of `modelName`, `selectedViews`, and `outputPathsByView`; keep `transformsByView` creation after `drawingModel` exists.

- [x] **Step 4: Enable the option in verification projections**

In `CreateVerificationProjectionOptions()`, return:

```csharp
return new StepProjectionOptions
{
    ImageSizePixels = VerificationProjectionImageSizePixels,
    PaddingPixels = VerificationProjectionPaddingPixels,
    WriteMetadata = false,
    SkipGeometryModelForExternalRender = true
};
```

- [x] **Step 5: Build and run guard**

Run:

```powershell
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: FAIL only for `full_test_wall_ms` if Task 5 is not done yet.

- [x] **Step 6: Measure clean/validated projection improvement**

Clear generated outputs:

```powershell
$data = Join-Path (Get-Location) 'Test\StepCleaner\Data'
foreach ($name in @('Clean','CleanProjection','OriginalCleanCompareProjection','ValidatedProjection','FailedProjectionReport')) {
    Remove-Item -LiteralPath (Join-Path $data $name) -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath (Join-Path $data 'FailedProjectionReport.md') -Force -ErrorAction SilentlyContinue
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: PASS. `clean_projection_render_ms` and `validated_projection_render_ms` should be lower than baseline `28238 ms` and `28742 ms`.

- [x] **Step 7: Commit render-only projection mode**

```powershell
git add EasyEDA-Loader\StepProjectionRenderer.cs Test\StepCleaner\Program.cs
git commit -m "Skip geometry parsing for external verification renders"
```

---

### Task 5: Print Full-Test Wall Time From The Harness

**Files:**
- Modify: `Test/StepCleaner/Program.cs`

- [x] **Step 1: Add wall stopwatch**

At the start of the no-args `try` block in `Main`, add:

```csharp
Stopwatch fullTestStopwatch = Stopwatch.StartNew();
```

Before returning success, add:

```csharp
fullTestStopwatch.Stop();
Console.WriteLine("full_test_wall_ms=" + fullTestStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
```

Before returning failure after printing failures, add:

```csharp
fullTestStopwatch.Stop();
Console.WriteLine("full_test_wall_ms=" + fullTestStopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
```

- [x] **Step 2: Build and run guard**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: PASS.

- [x] **Step 3: Commit timing output**

```powershell
git add Test\StepCleaner\Program.cs
git commit -m "Print full StepCleaner test wall timing"
```

---

### Task 6: Evaluate Bounded Directory-Level Parallel Projection

**Files:**
- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`
- Modify: `Test/StepCleaner/Program.cs`

- [x] **Step 1: Add option for parallel file projection**

In `StepProjectionOptions`, add:

```csharp
public int MaxParallelFiles { get; set; } = 1;
```

- [x] **Step 2: Preserve option in clone paths**

Add to every `StepProjectionOptions` clone:

```csharp
MaxParallelFiles = options.MaxParallelFiles
```

- [x] **Step 3: Add bounded parallel branch in `ProjectDirectory`**

Replace the sequential loop:

```csharp
var reports = new List<StepProjectionReport>();
foreach (string inputFile in GetStepFiles(inputDirectory))
    reports.Add(ProjectFile(inputFile, outputDirectory, options));

return reports;
```

with:

```csharp
var inputFiles = GetStepFiles(inputDirectory);
if (options.MaxParallelFiles <= 1 || inputFiles.Count <= 1)
{
    var reports = new List<StepProjectionReport>();
    foreach (string inputFile in inputFiles)
        reports.Add(ProjectFile(inputFile, outputDirectory, options));
    return reports;
}

int degree = Math.Max(1, Math.Min(options.MaxParallelFiles, inputFiles.Count));
var results = new StepProjectionReport[inputFiles.Count];
Parallel.For(
    0,
    inputFiles.Count,
    new ParallelOptions { MaxDegreeOfParallelism = degree },
    index => results[index] = ProjectFile(inputFiles[index], outputDirectory, options));
return results.ToList();
```

Add `using System.Threading.Tasks;` if not already present.

- [x] **Step 4: Enable conservative parallelism in the full-test verification options**

In `CreateVerificationProjectionOptions()`, add:

```csharp
MaxParallelFiles = 2
```

Use `2` first because F3D is GPU/OpenGL-backed and too much parallelism may become slower or unstable.

- [x] **Step 5: Benchmark `1` versus `2`**

Run the cold full test twice, changing only `MaxParallelFiles`.

Expected:

```text
MaxParallelFiles=1: pass, record full_test_wall_ms and projection stages
MaxParallelFiles=2: pass, record full_test_wall_ms and projection stages
```

Keep `MaxParallelFiles=2` only as a scoped full-test verification setting if it passes cold and improves the measured projection verification total without increasing full-test wall time. Do not make it the library default. If detection debug image generation regresses, leave that for a separate targeted optimization instead of increasing parallelism further.

- [x] **Step 6: Commit only if faster**

If faster:

```powershell
git add EasyEDA-Loader\StepProjectionRenderer.cs Test\StepCleaner\Program.cs
git commit -m "Parallelize verification projection directories"
```

If not faster:

```powershell
git restore EasyEDA-Loader\StepProjectionRenderer.cs Test\StepCleaner\Program.cs
```

---

### Task 7: Final Cold Full-Test Verification And Timing Report

**Files:**
- No planned code changes.

- [x] **Step 1: Clear generated outputs**

Run:

```powershell
$data = Join-Path (Get-Location) 'Test\StepCleaner\Data'
$targets = @(
    'Clean',
    'CleanProjection',
    'OriginalCleanCompareProjection',
    'ValidatedProjection',
    'FailedProjectionReport',
    'CleanText',
    'CleanTextProjection',
    'F3DSubsetSmoke'
)
foreach ($name in $targets) {
    Remove-Item -LiteralPath (Join-Path $data $name) -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath (Join-Path $data 'FailedProjectionReport.md') -Force -ErrorAction SilentlyContinue
```

- [x] **Step 2: Run full cold test and capture log**

Run:

```powershell
$log = Join-Path (Get-Location) 'Test\StepCleaner\Data\full_no_cache_timing_after_projection_optimization.log'
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj 2>&1 | Tee-Object -FilePath $log
```

Expected: PASS.

- [x] **Step 3: Extract timing lines**

Run:

```powershell
Select-String -Path Test\StepCleaner\Data\full_no_cache_timing_after_projection_optimization.log -Pattern 'Detection debug images|Projection verification timings|_ms=|full_test_wall_ms|STEP cleaner regression test passed|Post-clean projection verification|Projection comparison'
```

Expected: output includes all timing stages and `STEP cleaner regression test passed`.

- [x] **Step 4: Compare against baseline**

Create this manual summary in the commit message or final response:

```text
baseline full_test_wall_ms=163976
after full_test_wall_ms=<measured>
baseline detection_debug_images=44049
after detection_debug_images=<measured>
baseline original_detection_side_projection_render_ms=39597
after original_detection_side_projection_render_ms=<measured>
baseline clean_projection_render_ms=28238
after clean_projection_render_ms=<measured>
baseline validated_projection_render_ms=28742
after validated_projection_render_ms=<measured>
```

- [x] **Step 5: Commit final timing note if docs are updated**

If updating docs:

```powershell
git add docs\superpowers\plans\2026-06-04-stepcleaner-cold-full-test-optimization.md
git commit -m "Document cold StepCleaner optimization results"
```

---

## Next Optimization Pass

Measured signal after `7d33dc5`:

```text
Best run:
  full_test_wall_ms=155753
  detection_debug_images=50605 ms
  post_clean_detection_ms=4075 ms
  post_clean_detection_region_projection_ms=4384 ms
  original_vs_clean_projection_compare_ms=4023 ms
  clean_vs_validated_projection_compare_ms=2973 ms

Final post-safety run:
  full_test_wall_ms=196269
  detection_debug_images=57222 ms
  post_clean_detection_ms=10984 ms
  post_clean_detection_region_projection_ms=11870 ms
  original_vs_clean_projection_compare_ms=12673 ms
  clean_vs_validated_projection_compare_ms=5958 ms
```

Conclusion: pure projection rendering improved, but the next wall-time wins are in repeated detection/parsing, detection-region projection, and PNG comparison. Keep `StepF3DRender.exe` as the color-correct helper until native OCCT color rendering is correct. Do not add silhouette projection output caching.

### Task 8: First Priority - Replace Internal File Round-Trips With In-Memory Data

**Files:**
- Modify: `Test/StepCleaner/Program.cs`
- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`
- Modify: `EasyEDA-Loader/StepWatermarkCleanVerifier.cs`
- Modify: `EasyEDA-Loader/StepSilhouetteProjection.cs`
- Modify: `StepF3DRender/Program.cs`

**Goal:** internal verification/import code should pass STEP bytes and raw rendered image buffers in memory. Saving STEP files, JSON metadata, and PNG files should remain available for explicit command-line tools, test artifacts, reports, and user-facing debug output only.

- [x] **Step 1: Add source guards for in-memory internal APIs**

In `RunModelCacheTests()`, add guards:

```csharp
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
    "F3D library batch rendering should avoid saving and reloading image files for internal callers",
    failures);
AssertContains(
    stepSilhouetteProjection,
    "Generate(cleanedStep,",
    "internal OCCT HLR projection should accept cleaned STEP bytes instead of requiring a saved STEP file",
    failures);
AssertContains(
    stepWatermarkCleanVerifier,
    "ProjectFileImages(",
    "watermark verification should compare in-memory raw projection images before writing report artifacts",
    failures);
```

- [x] **Step 2: Run guard test and verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: FAIL with the new in-memory projection guards.

- [x] **Step 3: Add an in-memory projection result type**

In `StepProjectionRenderer.cs`, add:

```csharp
public sealed class StepProjectionImage
{
    public string ViewName { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public byte[] RgbaBytes { get; internal set; }
}
```

Keep the file-writing `ProjectFile()` API as a wrapper for CLI/test artifact paths.

- [x] **Step 4: Add `ProjectFileImages()`**

Add:

```csharp
public static IReadOnlyList<StepProjectionImage> ProjectFileImages(
    byte[] stepData,
    string modelName,
    StepProjectionOptions options = null)
{
    if (stepData == null)
        throw new ArgumentNullException(nameof(stepData));
    if (string.IsNullOrWhiteSpace(modelName))
        modelName = "model";

    options = NormalizeOptions(options);
    IReadOnlyList<ViewSpec> selectedViews = GetSelectedViews(options);

    if (TryRenderWithF3DLibraryBatchToRawImages(stepData, modelName, selectedViews, options, out IReadOnlyList<StepProjectionImage> f3dImages))
        return f3dImages;

    string stepText = Encoding.Latin1.GetString(stepData);
    StepModel model = StepModel.Parse(stepText);
    model.BuildIndexes();
    var drawingModel = ProjectionModel.Build(model);

    var result = new List<StepProjectionImage>();
    foreach (ViewSpec view in selectedViews)
    {
        ProjectionTransform transform = ProjectionTransform.Create(drawingModel.Bounds, view, options);
        RgbaImage image = RenderProjectionImage(drawingModel, view, transform, options);
        result.Add(image.ToProjectionImage(view.Name));
    }

    return result;
}
```

Add raw conversion helpers beside PNG edge helpers:

```csharp
internal StepProjectionImage ToProjectionImage(string viewName);
internal static RgbaImage FromProjectionImage(StepProjectionImage image);
```

- [x] **Step 5: Add `ProjectDetectionFileImages()`**

Add an in-memory variant of `ProjectDetectionFile()`:

```csharp
public static IReadOnlyList<StepProjectionImage> ProjectDetectionFileImages(
    byte[] stepData,
    string modelName,
    StepWatermarkDetectionReport detectionReport,
    StepProjectionOptions options = null,
    IReadOnlyList<StepWatermarkMarkedRegion> markedRegions = null)
```

It should:

1. Parse/build the model once from `stepData`.
2. Build detection highlights and selected detection views exactly like `ProjectDetectionFile()`.
3. Try `TryRenderWithF3DLibraryBatchToRawImages(...)`.
4. Overlay highlights on `RgbaImage.FromProjectionImage(image)` and return updated raw image buffers.
5. Fall back to `RenderProjectionImage(...).ToProjectionImage(...)` without writing a temp PNG.

- [x] **Step 6: Make file APIs wrappers**

Change `ProjectFile(string inputPath, string outputDirectory, ...)` to:

1. Read `byte[] stepData = File.ReadAllBytes(inputPath)`.
2. Call `ProjectFileImages(stepData, Path.GetFileNameWithoutExtension(inputPath), options)`.
3. Save each returned raw image as PNG to `outputDirectory`.
4. Write metadata only when `options.WriteMetadata` is true.

Change `ProjectDetectionFile(...)` to:

1. Read bytes.
2. Call `ProjectDetectionFileImages(...)`.
3. Save returned raw images as PNGs to the requested debug/test output directory.

- [x] **Step 7: Add an in-memory F3D helper mode**

In `StepF3DRender/Program.cs`, add a command mode:

```text
StepF3DRender --six-sides-stdout <input.step|-> <model-name> [--size pixels] [--views x_plus,y_plus]
```

Rules:

- `-` means read STEP bytes from stdin into a temp stream inside the helper process only if libf3d cannot load from memory directly.
- Return JSON to stdout:

```json
{
  "views": [
    { "name": "x_plus", "width": 1000, "height": 1000, "channelCount": 4, "channelType": 0, "channelTypeSize": 1, "rawBase64": "..." }
  ],
  "elapsedMs": 1234
}
```

- Do not write PNG files in this mode.
- Keep the current `--six-sides` file-output mode for command-line/manual/test artifact requests.

- [x] **Step 8: Add `TryRenderWithF3DLibraryBatchToRawImages()`**

In `StepProjectionRenderer.cs`, add:

```csharp
private static bool TryRenderWithF3DLibraryBatchToRawImages(
    byte[] stepData,
    string modelName,
    IReadOnlyList<ViewSpec> views,
    StepProjectionOptions options,
    out IReadOnlyList<StepProjectionImage> images)
```

It should invoke:

```text
StepF3DRender --six-sides-stdout - <model-name> --size <pixels> --views <names>
```

Write `stepData` to stdin, parse stdout JSON, decode `rawBase64`, validate dimensions/channel layout for every requested view, convert to RGBA when needed, and return raw image buffers. The existing file-output `TryRenderWithF3DLibraryBatch()` stays as the CLI/test artifact fallback.

- [x] **Step 9: Move watermark verification to memory-first projection**

In `StepWatermarkCleanVerifier.VerifyPostCleanOutput()`, replace file-output projection calls:

```csharp
StepProjectionRenderer.ProjectFile(originalPath, originalProjectionDirectory, renderOptions)
StepProjectionRenderer.ProjectFile(cleanPath, cleanProjectionDirectory, renderOptions)
```

with memory-first projection of `originalStep` and `cleanStep` bytes. Only write files if a visual failure report needs side-by-side artifacts.

- [x] **Step 10: Move import OCCT HLR to bytes-first input**

In import/measurement paths, prefer:

```csharp
StepSilhouetteProjection.Generate(cleanedStep, placement)
```

over:

```csharp
StepSilhouetteProjection.GenerateFromFile(cleanedStepPath, placement)
```

Only write the cleaned STEP file when Altium needs a 3D body file attachment or when a command-line/test artifact explicitly requests it. If Altium still needs the file, write once at the edge and keep internal verification/projection from rereading it.

- [x] **Step 11: Verify**

Run:

```powershell
dotnet build StepF3DRender\StepF3DRender.csproj
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
dotnet build StepCleaner\StepCleaner.csproj
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --measure-model-import C5338332 --repeat 3
```

Expected:

- all builds pass;
- guard test passes;
- measurement still prints `watermark_clean_cache_ms`, `occt_hlr_projection_total_ms`, and `projection_primitives`;
- internal paths no longer save/load projection PNGs except explicit test/CLI/debug artifact paths, and import HLR projection no longer reloads the already-available STEP body bytes.

Verified after raw row-origin fix:

```text
StepF3DRender build: PASS
EasyEDA-Loader build: PASS
StepCleaner build: PASS
model-cache guard: PASS
raw F3D stdout smoke: x_plus 64x64, channelCount=3, rawBytes=12288
full StepCleaner run: PASS, full_test_wall_ms=272594
C5338332 warm import measurement:
  watermark_clean_cache_ms=2,2,1
  occt_hlr_projection_total_ms=2389,2058,1992
  projection_primitives=344
```

- [x] **Step 12: Commit**

Run:

```powershell
git add EasyEDA-Loader\StepProjectionRenderer.cs EasyEDA-Loader\StepWatermarkCleanVerifier.cs EasyEDA-Loader\StepSilhouetteProjection.cs StepF3DRender\Program.cs Test\StepCleaner\Program.cs
git commit -m "Use in-memory projection data for internal verification"
```

### Task 9: Add A Shared Full-Test Detection Cache

**Files:**
- Modify: `Test/StepCleaner/Program.cs`

- [x] **Step 1: Add a failing guard**

In `RunModelCacheTests()`, add:

```csharp
AssertContains(
    stepCleanerProgram,
    "FullTestDetectionCache",
    "full StepCleaner regression should reuse original-model detection reports between debug image generation and post-clean verification",
    failures);
```

- [x] **Step 2: Run guard test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: FAIL with the new `FullTestDetectionCache` guard.

Verified red:

```text
Model cache regression test failed.
  full StepCleaner regression should reuse original-model detection reports between debug image generation and post-clean verification: missing 'FullTestDetectionCache'.
```

- [x] **Step 3: Introduce the cache**

Add this private helper class near `ProjectionVerificationTimings`:

```csharp
private sealed class FullTestDetectionCache
{
    private readonly Dictionary<string, StepWatermarkDetectionReport> _reportsByFileName =
        new Dictionary<string, StepWatermarkDetectionReport>(StringComparer.OrdinalIgnoreCase);

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
}
```

- [x] **Step 4: Create one cache in `Main`**

After `var projectionTimings = new ProjectionVerificationTimings();`, add:

```csharp
var detectionCache = new FullTestDetectionCache();
```

Pass `detectionCache` into:

```csharp
VerifyDetectionDebugImages(..., detectionCache, failures);
VerifyPostCleanProjections(..., detectionCache, projectionTimings, ...);
```

- [x] **Step 5: Use the cache in detection debug**

Change the `VerifyDetectionDebugImages` signature to include:

```csharp
FullTestDetectionCache detectionCache,
```

Replace:

```csharp
var detectionReport = StepWatermarkCleaner.Detect(File.ReadAllBytes(originalFile), new StepWatermarkCleanerOptions());
```

with:

```csharp
var detectionReport = detectionCache.GetReport(originalFile);
```

- [x] **Step 6: Use the cache in post-clean verification**

Change the `VerifyPostCleanProjections` signature to include:

```csharp
FullTestDetectionCache detectionCache,
```

Replace:

```csharp
var detectionReport = projectionTimings.Measure(
    "post_clean_detection_ms",
    () => StepWatermarkCleaner.Detect(File.ReadAllBytes(originalFile), new StepWatermarkCleanerOptions()));
```

with:

```csharp
var detectionReport = projectionTimings.Measure(
    "post_clean_detection_ms",
    () => detectionCache.GetReport(originalFile));
```

Expected: `post_clean_detection_ms` should drop sharply when detection debug already regenerated the same original model in the same run.

- [x] **Step 7: Verify**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: both PASS. Record `detection_debug_images`, `post_clean_detection_ms`, and `full_test_wall_ms`.

Verified:

```text
model-cache guard: PASS
Detection debug images: marked=20, generated=20, regenerated models=14, cached models=2, elapsed=72687 ms
post_clean_detection_ms=615 ms
full_test_wall_ms=179388
```

### Task 10: Cache Projected Detection Regions In The Full-Test Harness

**Files:**
- Modify: `Test/StepCleaner/Program.cs`
- Optional Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`

- [x] **Step 1: Extend the guard**

In `RunModelCacheTests()`, add:

```csharp
AssertContains(
    stepCleanerProgram,
    "GetDetectionRegions(",
    "full StepCleaner regression should reuse projected detection regions for original-vs-clean and clean-vs-validated comparisons",
    failures);
```

- [x] **Step 2: Extend `FullTestDetectionCache`**

Add region storage:

```csharp
private readonly Dictionary<string, IReadOnlyList<StepProjectionDetectionRegion>> _regionsByKey =
    new Dictionary<string, IReadOnlyList<StepProjectionDetectionRegion>>(StringComparer.OrdinalIgnoreCase);

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
        regions = StepProjectionRenderer.ProjectDetectionRegions(
            originalFile,
            GetReport(originalFile),
            projectionOptions).ToList();
        _regionsByKey[key] = regions;
    }

    return regions;
}
```

- [x] **Step 3: Use cached regions in `VerifyPostCleanProjections`**

Replace:

```csharp
var detectionRegions = projectionTimings.Measure(
    "post_clean_detection_region_projection_ms",
    () => StepProjectionRenderer.ProjectDetectionRegions(
        originalFile,
        detectionReport,
        projectionOptions)
    .ToList());
```

with:

```csharp
var detectionRegions = projectionTimings.Measure(
    "post_clean_detection_region_projection_ms",
    () => detectionCache.GetDetectionRegions(originalFile, projectionOptions).ToList());
```

- [x] **Step 4: Keep debug-image region filtering separate**

Do not reuse marked-region-filtered debug results for post-clean verification. `VerifyDetectionDebugImages()` uses marker sidecars, while post-clean verification must use unmarked automatic detection regions.

- [x] **Step 5: Verify**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: PASS. Record `post_clean_detection_region_projection_ms` and `original_detection_side_projection_render_ms`.

Verified:

```text
model-cache guard: PASS
full StepCleaner run: PASS
post_clean_detection_region_projection_ms=10492 ms
original_detection_side_projection_render_ms=71333 ms
full_test_wall_ms=318114
```

Note: this cache is now available through `FullTestDetectionCache.GetDetectionRegions(...)`, but the current harness only projects each unmarked original detection region set once per run. No standalone wall-time win is expected until a later comparison stage reuses the same keyed regions.

### Task 11: Add Fine-Grained Detection Debug Timings

**Files:**
- Modify: `Test/StepCleaner/Program.cs`

- [x] **Step 1: Add stage timing fields**

Inside `VerifyDetectionDebugImages()`, add local counters:

```csharp
long loadMarkedRegionsMs = 0;
long cacheCheckMs = 0;
long detectMs = 0;
long projectDetectionFileMs = 0;
```

- [x] **Step 2: Time each expensive per-model operation**

Use `Stopwatch.StartNew()` around:

```csharp
StepWatermarkCleaner.LoadMarkedRegionsForStepFile(...)
IsDetectionDebugImageCacheFresh(...)
detectionCache.GetReport(originalFile)
StepProjectionRenderer.ProjectDetectionFile(...)
```

Add elapsed milliseconds to the counters after each call.

- [x] **Step 3: Print detail lines**

After the existing `Detection debug images:` line, print:

```csharp
Console.WriteLine("  detection_debug_load_marked_regions_ms=" + loadMarkedRegionsMs.ToString(CultureInfo.InvariantCulture) + " ms");
Console.WriteLine("  detection_debug_cache_check_ms=" + cacheCheckMs.ToString(CultureInfo.InvariantCulture) + " ms");
Console.WriteLine("  detection_debug_detect_ms=" + detectMs.ToString(CultureInfo.InvariantCulture) + " ms");
Console.WriteLine("  detection_debug_project_file_ms=" + projectDetectionFileMs.ToString(CultureInfo.InvariantCulture) + " ms");
```

- [x] **Step 4: Add guard**

In `RunModelCacheTests()`, add:

```csharp
AssertContains(
    stepCleanerProgram,
    "detection_debug_project_file_ms",
    "detection debug image generation should expose detailed timing for the remaining bottleneck",
    failures);
```

- [x] **Step 5: Verify**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: PASS and the full run prints all four detail lines. Use this output to choose the next implementation task.

Verified:

```text
model-cache guard: PASS
full StepCleaner run: PASS
Detection debug images: marked=20, generated=20, regenerated models=14, cached models=2, elapsed=28559 ms
  detection_debug_load_marked_regions_ms=0 ms
  detection_debug_cache_check_ms=2 ms
  detection_debug_detect_ms=2379 ms
  detection_debug_project_file_ms=26149 ms
full_test_wall_ms=111969
```

Next target from this timing: `ProjectDetectionFile` dominates detection debug image generation.

### Task 12: Add A Fast Path For Identical Clean/Validated Projection PNGs

**Files:**
- Modify: `Test/StepCleaner/Program.cs`

- [ ] **Step 1: Add a guard**

In `RunModelCacheTests()`, add:

```csharp
AssertContains(
    stepCleanerProgram,
    "FilesEqualByLengthAndBytes",
    "clean-vs-validated projection comparison should skip PNG decode when files are byte-identical",
    failures);
```

- [ ] **Step 2: Add a byte equality helper**

Add near `ProjectionPixelsEqual()`:

```csharp
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
```

- [ ] **Step 3: Use the fast path**

At the start of `ProjectionPixelsEqual()`, add:

```csharp
if (FilesEqualByLengthAndBytes(cleanProjectionPath, validatedProjectionPath))
    return true;
```

Do not add this shortcut to `VerifyPostCleanProjectionImage()`, because original-vs-clean still needs `VerifyCleanedRegionFlatness()` even when outside-region pixels do not change.

- [ ] **Step 4: Verify**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: PASS. Compare `clean_vs_validated_projection_compare_ms` before and after.

### Task 13: Add Fast Pixel Buffer Comparison For Remaining PNG Compares

**Files:**
- Modify: `Test/StepCleaner/Program.cs`

- [ ] **Step 1: Add a guard**

In `RunModelCacheTests()`, add:

```csharp
AssertContains(
    stepCleanerProgram,
    "CopyBitmapPixelsToInt32Rows",
    "projection comparison should avoid per-pixel SKBitmap.GetPixel calls in hot loops",
    failures);
```

- [ ] **Step 2: Add a row-copy helper**

Add:

```csharp
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
        System.Runtime.InteropServices.Marshal.Copy(row, pixels, y * bitmap.Width, bitmap.Width);
    }

    return pixels;
}
```

- [ ] **Step 3: Use copied buffers in `ProjectionPixelsEqual()`**

After decoding and validating image dimensions, replace nested `GetPixel()` calls with:

```csharp
int[] cleanPixels = CopyBitmapPixelsToInt32Rows(cleanImage);
int[] validatedPixels = CopyBitmapPixelsToInt32Rows(validatedImage);
for (int i = 0; i < cleanPixels.Length; i++)
{
    if (cleanPixels[i] != validatedPixels[i])
        return false;
}

return true;
```

If tolerance must be preserved, add a small `PixelsDifferent(int left, int right, int tolerance)` overload that extracts RGBA bytes from the packed integer and matches the existing `PixelsDifferent(SKColor, SKColor, int)` semantics.

- [ ] **Step 4: Use copied buffers in `VerifyPostCleanProjectionImage()`**

Only do this after `ProjectionPixelsEqual()` is green. Replace the hot nested `GetPixel()` loop with indexed buffers while preserving `allowedMask`, `ProjectionDifferenceTolerance`, and `VerifyCleanedRegionFlatness()` behavior.

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: PASS. Compare `original_vs_clean_projection_compare_ms` and `clean_vs_validated_projection_compare_ms`.

### Task 14: Add A Dedicated C5338332 Import Optimization Baseline

**Files:**
- Modify: `docs/superpowers/plans/2026-06-04-stepcleaner-cold-full-test-optimization.md`
- Optional Modify: `Test/StepCleaner/Program.cs`

- [ ] **Step 1: Run the warm-cache import measurement**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --measure-model-import C5338332 --repeat 3
```

Expected: PASS. Capture:

```text
component_lookup_ms
model_download_cache_read_ms
raw_obj_download_cache_read_ms
watermark_clean_cache_ms
raw_obj_z_info_ms
occt_hlr_projection_total_ms
projection_primitives
```

- [ ] **Step 2: Run a clean-cache-miss measurement**

Remove only the C5338332 clean STEP cache file for the chosen clean mode, then run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --measure-model-import C5338332 --repeat 1
```

Expected: PASS. Capture all `watermark_clean_detail_*_ms` lines.

- [ ] **Step 3: Add missing detail timing if output is not enough**

If `watermark_clean_cache_ms` is still too coarse, add timing around these existing stages in `StepWatermarkCleaner.cs`:

```text
BuildCleanupContext
DetectAutomaticWatermarks
CleanWithAutomaticDetection
BuildPublicDetectionReport
RemoveInactiveDefinitions
```

Expected: the measurement command prints enough detail to select one concrete edit optimization before touching OCCT HLR.

- [ ] **Step 4: Record the baseline in this plan**

Add a small table under this task:

```text
C5338332 import baseline:
  run_date=<date>
  cache_state=<warm|clean-cache-miss>
  watermark_clean_cache_ms=<measured>
  occt_hlr_projection_total_ms=<measured>
  raw_obj_z_info_ms=<measured>
  projection_primitives=<measured>
```

### Task 15: Optimize Only The Measured C5338332 Winner

**Files:**
- Modify based on Task 14 result:
  - `EasyEDA-Loader/StepWatermarkCleaner.cs` for `watermark_clean_cache_ms`
  - `EasyEDA-Loader/StepSilhouetteProjection.cs` and `StepOcctHlr/*` for `occt_hlr_projection_total_ms`
  - `EasyEDA-Loader/ModelZInfoCache.cs` for `raw_obj_z_info_ms`

- [ ] **Step 1: Choose exactly one next target**

Use this rule:

```text
If watermark_clean_cache_ms is largest: optimize StepWatermarkCleaner first.
If occt_hlr_projection_total_ms is largest: optimize OCCT HLR projection first.
If raw_obj_z_info_ms is largest on warm cache: optimize ModelZInfoCache/raw OBJ parsing first.
```

- [ ] **Step 2: Add a guard before editing**

Add a source guard in `RunModelCacheTests()` for the selected optimization. Examples:

```csharp
AssertContains(
    stepWatermarkCleaner,
    "SelectedConcreteOptimizationName",
    "watermark cleaner should use the selected measured optimization",
    failures);
```

or:

```csharp
AssertContains(
    stepSilhouetteProjection,
    "SelectedConcreteOptimizationName",
    "OCCT HLR projection should use the selected measured optimization",
    failures);
```

- [ ] **Step 3: Implement the smallest measured optimization**

Do not batch unrelated edits. One commit should target one measured phase.

- [ ] **Step 4: Verify with C5338332 and full guards**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --measure-model-import C5338332 --repeat 3
```

Expected: guard test PASS and the chosen phase improves versus the Task 14 baseline.

- [ ] **Step 5: Commit**

Run:

```powershell
git add <changed files>
git commit -m "Optimize measured C5338332 import bottleneck"
```

### Task 16: Replace F3D Stdout/Base64 Bridge With Shared Library Calls

**Files:**
- Create: `StepF3DRenderLib/StepF3DRenderLib.csproj`
- Create: `StepF3DRenderLib/F3DProjectionRenderer.cs`
- Modify: `StepF3DRender/Program.cs`
- Modify: `StepF3DRender/StepF3DRender.csproj`
- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`
- Modify: `EasyEDA-Loader/EasyEDA-Loader.csproj`
- Modify: `StepCleaner/StepCleaner.csproj`
- Modify: `Test/StepCleaner/Program.cs`
- Modify: `Test/StepCleaner/StepCleaner.Tests.csproj`

- [x] **Step 1: Add source guards**

`RunModelCacheTests()` now checks that:

- `StepF3DRenderLib` contains `f3d_scene_add_buffer`;
- `StepProjectionRenderer` calls `F3DProjectionRenderer.RenderRawImages`;
- `StepProjectionRenderer` no longer contains `--six-sides-stdout` or `rawBase64`;
- the CLI uses `F3DProjectionRenderer.RenderPngFilesFromFile`;
- `EasyEDA-Loader`, `StepCleaner`, and `StepCleaner.Tests` reference `StepF3DRenderLib.csproj`.

Red run:

```text
Model cache regression test failed.
  F3D shared renderer should load STEP bytes directly through libf3d without a temp STEP file: missing 'f3d_scene_add_buffer'.
  internal colored projection rendering should call the shared F3D renderer library instead of a helper process: missing 'F3DProjectionRenderer.RenderRawImages'.
  internal colored projection rendering should not send raw images through stdout: found '--six-sides-stdout'.
  internal colored projection rendering should not base64-expand raw image buffers: found 'rawBase64'.
```

- [x] **Step 2: Move F3D native rendering into a shared library**

`StepF3DRenderLib.F3DProjectionRenderer` now exposes:

```csharp
public static IReadOnlyList<F3DRenderedImage> RenderRawImages(
    byte[] stepData,
    int sizePixels,
    IReadOnlyList<string> viewNames)

public static IReadOnlyList<F3DRenderedFile> RenderPngFilesFromFile(
    string inputPath,
    string outputDirectory,
    int sizePixels,
    IReadOnlyList<string> viewNames)
```

The raw path pins caller STEP bytes and loads them with:

```csharp
f3d_scene_add_buffer(scene, pinnedStepData.AddrOfPinnedObject(), (UIntPtr)stepData.Length)
```

- [x] **Step 3: Replace the executable with a thin CLI wrapper**

`StepF3DRender --six-sides <input.step> <output-directory> [--size pixels] [--views ...]` now only parses arguments and calls `F3DProjectionRenderer.RenderPngFilesFromFile(...)`.

The old `--six-sides-stdout` JSON/base64 mode was removed from internal production usage.

- [x] **Step 4: Switch `StepProjectionRenderer` to in-process F3D**

`TryRenderWithF3DLibraryBatchToRawImages(...)` now calls `F3DProjectionRenderer.RenderRawImages(...)` directly and converts the returned raw F3D image buffers to top-down RGBA.

`TryRenderWithF3DLibraryBatch(...)` now calls `F3DProjectionRenderer.RenderPngFilesFromFile(...)` directly for explicit file-output render requests.

- [x] **Step 5: Serialize libf3d entry points**

Full harness initially crashed with native heap corruption:

```text
exit_code=-1073740940
```

Root cause: the old helper process isolated F3D native state per render, while the shared library allowed parallel calls from post-clean verification tasks. `F3DProjectionRenderer` now uses a single `NativeRenderLock` around libf3d render operations.

- [x] **Step 6: Verify**

Commands:

```powershell
dotnet build Test\StepCleaner\StepCleaner.Tests.csproj
dotnet run --no-build --project Test\StepCleaner\StepCleaner.Tests.csproj -- --f3d-buffer-smoke Test\StepCleaner\Data\Original\HDMI-SMD_HDMI-001S.step
dotnet run --project StepF3DRender\StepF3DRender.csproj -- --six-sides Test\StepCleaner\Data\Original\HDMI-SMD_HDMI-001S.step <temp-dir> --size 128 --views x_plus,z_plus
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Result:

```text
f3d_buffer_smoke=PASS
view=x_plus
size=256x256
raw_rgba_bytes=262144

CLI smoke: generated x_plus and z_plus PNG files, six_side_f3d_library_ms=3252

model-cache guard: PASS

full StepCleaner run: PASS
full_test_wall_ms=312357
```

Note: this removes internal stdout/base64 expansion and avoids temp STEP files for F3D raw internal rendering. Because libf3d is serialized for native stability, this is an architecture cleanup and prerequisite for future in-process optimization, not a measured wall-time win yet.

---

## Self-Review

Spec coverage:

- The plan targets the cold no-cache full-test bottlenecks shown by the measured run.
- The largest stages are covered first: detection/debug selected views, original selected side projection, clean/validated full projection renders.
- The plan preserves F3D as the color renderer and does not reintroduce OCCT color rendering.
- Bounded parallelism is explicitly experimental and only kept if measured faster.
- The next pass targets the latest noisy stages: repeated detection, projected detection regions, PNG comparison, and the standalone C5338332 import measurement.
- The plan explicitly avoids silhouette projection output caching.

Placeholder scan:

- No `TBD`, `TODO`, or undefined implementation steps are present.
- Each code-changing task includes exact files, snippets, commands, and expected results.

Type consistency:

- `StepProjectionOptions.SkipGeometryModelForExternalRender` is introduced before use.
- `StepProjectionOptions.MaxParallelFiles` is introduced before use.
- `TryRenderWithF3DLibraryBatch` remains the central production integration point.
- `FullTestDetectionCache` is introduced before use in detection debug and post-clean verification tasks.
