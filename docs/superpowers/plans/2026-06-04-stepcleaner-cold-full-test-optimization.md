# StepCleaner Cold Full Test Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

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

- [ ] **Step 1: Write failing source guards**

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

- [ ] **Step 2: Run guard test and verify it fails**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: FAIL with messages for missing `--views`, highlighted batch use, subset support, render-only option, and `full_test_wall_ms`.

- [ ] **Step 3: Commit the failing guards**

```powershell
git add Test\StepCleaner\Program.cs
git commit -m "Add guards for cold full-test projection optimizations"
```

---

### Task 2: Add `--views` To The Libf3d Helper

**Files:**
- Modify: `StepF3DRender/Program.cs`

- [ ] **Step 1: Extend request shape**

In `RenderRequest`, add:

```csharp
public IReadOnlyList<string> ViewNames { get; set; }
```

- [ ] **Step 2: Parse optional `--views`**

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

- [ ] **Step 3: Render only requested views**

In `RenderSixSides`, replace:

```csharp
foreach (ViewSpec view in Views)
```

with:

```csharp
foreach (ViewSpec view in Views.Where(view =>
    request.ViewNames.Any(name => string.Equals(name, view.Name, StringComparison.OrdinalIgnoreCase))))
```

- [ ] **Step 4: Update usage**

Replace the usage text with:

```csharp
Console.Error.WriteLine("Usage: StepF3DRender --six-sides <input.step> <output-directory> [--size pixels] [--views x_plus,y_plus,z_plus]");
```

- [ ] **Step 5: Build helper**

Run:

```powershell
dotnet build StepF3DRender\StepF3DRender.csproj
```

Expected: PASS.

- [ ] **Step 6: Verify subset command**

Run:

```powershell
$out = Join-Path (Get-Location) 'Test\StepCleaner\Data\F3DSubsetSmoke'
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null
& '.\StepF3DRender\bin\Debug\net8.0-windows7.0\win-x64\StepF3DRender.exe' --six-sides '.\Test\StepCleaner\Data\Clean\USB-C-SMD_TYPE-C-6PIN-2MD-073.step' $out --size 1000 --views x_plus,z_plus
Get-ChildItem -LiteralPath $out -Filter *.png | Select-Object Name,Length
```

Expected: exactly `2` PNGs, both nonempty, and output contains `six_side_f3d_library_ms=`.

- [ ] **Step 7: Run source guard**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: still FAIL, but no longer failing the `--views` guard.

- [ ] **Step 8: Commit helper subset support**

```powershell
git add StepF3DRender\Program.cs
git commit -m "Support subset views in libf3d batch renderer"
```

---

### Task 3: Use Libf3d Batch Rendering For Highlighted Detection Projections

**Files:**
- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`

- [ ] **Step 1: Remove six-view-only restriction**

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

- [ ] **Step 2: Pass `--views` to helper**

After adding `--size`, add:

```csharp
startInfo.ArgumentList.Add("--views");
startInfo.ArgumentList.Add(string.Join(",", views.Select(view => view.Name)));
```

- [ ] **Step 3: Batch-render detection projection base images**

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

- [ ] **Step 4: Build production project**

Run:

```powershell
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
```

Expected: PASS.

- [ ] **Step 5: Run guard test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: still FAIL only for render-only option and wall-time timing guard.

- [ ] **Step 6: Measure targeted highlighted path**

Clear generated debug images and run full test once:

```powershell
$data = Join-Path (Get-Location) 'Test\StepCleaner\Data'
Remove-Item -LiteralPath (Join-Path $data 'Clean') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $data 'OriginalCleanCompareProjection') -Recurse -Force -ErrorAction SilentlyContinue
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: PASS. `detection_debug_images` and `original_detection_side_projection_render_ms` should be materially lower than baseline `44049 ms` and `39597 ms`.

- [ ] **Step 7: Commit highlighted batch rendering**

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

- [ ] **Step 1: Add explicit option**

In `StepProjectionOptions`, add:

```csharp
public bool SkipGeometryModelForExternalRender { get; set; }
```

- [ ] **Step 2: Preserve option in clone paths**

Where `StepProjectionOptions` is cloned, add:

```csharp
SkipGeometryModelForExternalRender = options.SkipGeometryModelForExternalRender
```

Specifically update:

```csharp
private static StepProjectionOptions CloneSingleViewOptions(...)
private static StepProjectionOptions CreateProjectionOptionsForViews(...)
```

- [ ] **Step 3: Add a fast external-render branch at the start of `ProjectFile`**

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

- [ ] **Step 4: Enable the option in verification projections**

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

- [ ] **Step 5: Build and run guard**

Run:

```powershell
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: FAIL only for `full_test_wall_ms` if Task 5 is not done yet.

- [ ] **Step 6: Measure clean/validated projection improvement**

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

- [ ] **Step 7: Commit render-only projection mode**

```powershell
git add EasyEDA-Loader\StepProjectionRenderer.cs Test\StepCleaner\Program.cs
git commit -m "Skip geometry parsing for external verification renders"
```

---

### Task 5: Print Full-Test Wall Time From The Harness

**Files:**
- Modify: `Test/StepCleaner/Program.cs`

- [ ] **Step 1: Add wall stopwatch**

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

- [ ] **Step 2: Build and run guard**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --model-cache
```

Expected: PASS.

- [ ] **Step 3: Commit timing output**

```powershell
git add Test\StepCleaner\Program.cs
git commit -m "Print full StepCleaner test wall timing"
```

---

### Task 6: Evaluate Bounded Directory-Level Parallel Projection

**Files:**
- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`
- Modify: `Test/StepCleaner/Program.cs`

- [ ] **Step 1: Add option for parallel file projection**

In `StepProjectionOptions`, add:

```csharp
public int MaxParallelFiles { get; set; } = 1;
```

- [ ] **Step 2: Preserve option in clone paths**

Add to every `StepProjectionOptions` clone:

```csharp
MaxParallelFiles = options.MaxParallelFiles
```

- [ ] **Step 3: Add bounded parallel branch in `ProjectDirectory`**

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

- [ ] **Step 4: Enable conservative parallelism in the full-test verification options**

In `CreateVerificationProjectionOptions()`, add:

```csharp
MaxParallelFiles = 2
```

Use `2` first because F3D is GPU/OpenGL-backed and too much parallelism may become slower or unstable.

- [ ] **Step 5: Benchmark `1` versus `2`**

Run the cold full test twice, changing only `MaxParallelFiles`.

Expected:

```text
MaxParallelFiles=1: pass, record full_test_wall_ms and projection stages
MaxParallelFiles=2: pass, record full_test_wall_ms and projection stages
```

Keep `MaxParallelFiles=2` only as a scoped full-test verification setting if it passes cold and improves the measured projection verification total without increasing full-test wall time. Do not make it the library default. If detection debug image generation regresses, leave that for a separate targeted optimization instead of increasing parallelism further.

- [ ] **Step 6: Commit only if faster**

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

- [ ] **Step 1: Clear generated outputs**

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

- [ ] **Step 2: Run full cold test and capture log**

Run:

```powershell
$log = Join-Path (Get-Location) 'Test\StepCleaner\Data\full_no_cache_timing_after_projection_optimization.log'
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj 2>&1 | Tee-Object -FilePath $log
```

Expected: PASS.

- [ ] **Step 3: Extract timing lines**

Run:

```powershell
Select-String -Path Test\StepCleaner\Data\full_no_cache_timing_after_projection_optimization.log -Pattern 'Detection debug images|Projection verification timings|_ms=|full_test_wall_ms|STEP cleaner regression test passed|Post-clean projection verification|Projection comparison'
```

Expected: output includes all timing stages and `STEP cleaner regression test passed`.

- [ ] **Step 4: Compare against baseline**

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

- [ ] **Step 5: Commit final timing note if docs are updated**

If updating docs:

```powershell
git add docs\superpowers\plans\2026-06-04-stepcleaner-cold-full-test-optimization.md
git commit -m "Document cold StepCleaner optimization results"
```

---

## Self-Review

Spec coverage:

- The plan targets the cold no-cache full-test bottlenecks shown by the measured run.
- The largest stages are covered first: detection/debug selected views, original selected side projection, clean/validated full projection renders.
- The plan preserves F3D as the color renderer and does not reintroduce OCCT color rendering.
- Bounded parallelism is explicitly experimental and only kept if measured faster.

Placeholder scan:

- No `TBD`, `TODO`, or undefined implementation steps are present.
- Each code-changing task includes exact files, snippets, commands, and expected results.

Type consistency:

- `StepProjectionOptions.SkipGeometryModelForExternalRender` is introduced before use.
- `StepProjectionOptions.MaxParallelFiles` is introduced before use.
- `TryRenderWithF3DLibraryBatch` remains the central production integration point.
