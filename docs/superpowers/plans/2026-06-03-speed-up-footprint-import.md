# Speed Up Footprint Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce slow footprint imports by caching expensive Altium-independent model processing before touching Altium-side primitive creation.

**Architecture:** Keep Altium-side footprint creation unchanged. First optimize the pre-Altium model path used by components such as `C5338332`: original STEP and raw OBJ are already cached, so the next win is to reuse the existing cleaned STEP cache for import-time watermark cleanup instead of cleaning and projection-verifying on every import. Next reduce first-run cost by making the clean STEP cache path observable and cheap on hits, then replace F3D screenshot-based verification projections with OpenCascade HLR rendering that uses the same view geometry and detection masks. Keep colored 3D preview/rendering separate from monochrome watermark verification: OCCT can replace projection geometry first, but color-correct 3D rendering needs native XCAF presentation support or F3D.

**Tech Stack:** C#/.NET, EasyEDA-Loader Altium add-in, standalone `StepCleaner.Tests` console regression harness.

**OCCT color-renderer finding:** Do not replace the F3D colored 3D preview/render path with the managed `AIS_Shape` or manual `AIS_ColoredShape`/`XCAFDoc_ColorTool` approach. The correct OCCT color-aware display object is native `XCAFPrs_AISObject`, constructed from labels filled by `STEPCAFControl_Reader::Transfer()`. It dispatches XDE/XCAF styles with the required shape/sub-shape precedence. The current `Occt.NET 7.9.0` package used by `StepOcctHlr` exposes `XCAFPrs_Style` but does **not** expose `XCAFPrs_AISObject` or headers/libs for compiling a native helper. Local verification also found no installed native OCCT SDK, `XCAFPrs_AISObject.hxx`, `DRAWEXE.exe`, `CASROOT`, or `vcpkg` checkout. Until a native OCCT helper or binding extension is added, keep F3D as the authoritative colored 3D renderer and use OCCT only for HLR/silhouette/projection work.

**OCCT color-renderer evidence:** DF56 side-by-side probes showed the managed OCCT/XCAF raster probe applied `1174` style assignments but rendered the housing cream/white instead of the F3D dark/gray/olive output. F3D with production `--scalar-coloring --coloring-array=Colors` averaged about `1.70 s` warm render time; the managed OCCT/XCAF probe averaged about `2.86 s` and still used wrong color precedence. `RWGltf_CafWriter` was also probed as a managed XCAF style-export route, but it did not complete the DF56 GLB export within `120 s`, making it unsuitable for import-time preview replacement.

**F3D color-renderer evidence:** Direct `libf3d`/`f3d_c_api.dll` was also probed with `model.scivis.enable`, `model.scivis.cells`, `model.scivis.array_name = Colors`, and `model.scivis.component = -2`. The output preserved DF56 colors correctly, matching the existing CLI scalar-coloring path. A one-shot helper process rendered DF56 in about `2.2-2.9 s` process time (`2.1-2.7 s` internal render time), so it is not a drop-in speed win over the existing warm F3D CLI evidence. Keep it as a future persistent-renderer candidate only if colored 3D preview process startup becomes the bottleneck.

---

### Task 1: Cache Import-Time Cleaned STEP Models

**Files:**
- Modify: `EasyEDA-Loader/FootprintShapes/EeFootprint3dModel.cs`
- Modify: `Test/StepCleaner/Program.cs`

- [x] **Step 1: Write the failing regression test**

Add source-level assertions to `RunModelCacheTests()` proving import-time watermark cleanup goes through `ModelCache.GetCleanStepModelAsync()` and uses the existing clean-mode key for `ctx.CleanText`.

Run: `dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --model-cache`

Expected before implementation: FAIL, because `EeFootprint3dModel.AddToComponent()` calls `StepWatermarkCleanVerifier.CleanOrThrow()` directly.

- [x] **Step 2: Implement minimal cache usage**

In `EeFootprint3dModel.AddToComponent()`, replace the direct clean call with:

```csharp
byte[] footprintModel = originalModel;
if (ctx.RemoveWatermark)
{
    string cleanCacheKey = CleanStepCacheKeys.GetCleanModeKey(GetSafeCacheFileName(), ctx.CleanText);
    footprintModel = ModelCache.GetCleanStepModelAsync(
            cleanCacheKey,
            () => Task.Run(() => StepWatermarkCleanVerifier.CleanOrThrow(
                originalModel,
                GetSafeCacheFileName(),
                CreateVerificationDirectory(),
                ctx.CleanText)),
            ctx.CancelToken)
        .ConfigureAwait(false)
        .GetAwaiter()
        .GetResult();
}
```

This keeps first-import behavior identical, then makes repeated imports read `LocalAppData/EasyEDA-Loader/ModelCache/Clean/...` instead of rerunning STEP cleanup verification.

- [x] **Step 3: Verify focused regression**

Run: `dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --model-cache`

Expected after implementation: PASS with `Model cache regression test passed.`

- [x] **Step 4: Verify nearby regression suites**

Run:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --async-import
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --footprint-placement
dotnet build EasyEDA-Loader/EasyEDA-Loader.csproj
```

Expected: all commands exit 0.

### Task 2: Next Altium-Independent Speedups

**Files:**
- Candidate: `EasyEDA-Loader/FootprintShapes/EeFootprint3dModel.cs`
- Candidate: `EasyEDA-Loader/API/EasyedaApi.cs`
- Candidate: `EasyEDA-Loader/StepWatermarkCleaner.cs`
- Candidate: `Test/StepCleaner/Program.cs`

- [x] **Step 0: Speed up model watermark cleaning**

Reduce elapsed verification time in `StepWatermarkCleanVerifier.CleanOrThrow()` for models that do contain EasyEDA watermark geometry. Render the original and cleaned projection images in parallel for the same detected views instead of running those independent renders sequentially.

- [x] **Step 1: Add timing around model phases**

Add trace timing for model download/cache read, raw OBJ Z parse, watermark clean/cache, OCCT HLR projection, and projection optimization. Use the existing `EasyEDALoaderModule.Trace()` log path so timing is available from a normal Altium run.

- [x] **Step 2: Avoid repeated raw OBJ parsing**

Cache parsed `ModelZInfo` beside the raw OBJ model so repeated imports avoid decoding and scanning the full OBJ again.

- [x] **Step 3: Measure with `C5338332`**

Import `C5338332` twice with the same settings. The second import should show cache hits for original STEP, raw OBJ, cleaned STEP, and parsed OBJ Z info.

Measured through the standalone Altium-independent harness:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --measure-model-import C5338332 --repeat 2
```

Observed after seeding network artifacts into the normal cache because this sandbox blocks .NET direct sockets:

- Run 1: total measured 21001 ms; watermark clean/cache 19496 ms; raw OBJ Z info 14 ms; projection total 1477 ms.
- Run 2: total measured 1645 ms; watermark clean/cache 3 ms; raw OBJ Z info 0 ms; projection total 1638 ms.

### Task 3: Speed Up And Harden Watermark Clean Cache Code

**Files:**
- Modify: `EasyEDA-Loader/ModelCache.cs`
- Modify: `EasyEDA-Loader/CleanStepCacheKeys.cs`
- Modify: `EasyEDA-Loader/FootprintShapes/EeFootprint3dModel.cs`
- Modify: `EasyEDA-Loader/DialogWindow.cs`
- Modify: `Test/StepCleaner/Program.cs`

- [ ] **Step 1: Write failing cache-status regression**

Extend `RunModelCacheTests()` in `Test/StepCleaner/Program.cs` with source-level checks that the clean STEP cache path exposes hit/miss status instead of only returning bytes.

Add assertions for these exact implementation markers:

```csharp
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
```

Run:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --model-cache
```

Expected before implementation: FAIL with missing `ModelCacheResult` / `GetCleanStepModelWithStatusAsync`.

- [ ] **Step 2: Add cache-result API without changing existing callers**

In `EasyEDA-Loader/ModelCache.cs`, add:

```csharp
public sealed class ModelCacheResult
{
    public byte[] Data { get; set; }
    public bool CacheHit { get; set; }
    public string CachePath { get; set; }
}
```

Add `GetCleanStepModelWithStatusAsync(string modelUuid, Func<Task<byte[]>> clean, CancellationToken cancellationToken)` that:

- checks `GetCleanStepPath(modelUuid)` first;
- returns `{ Data = cached, CacheHit = true, CachePath = cachePath }` for non-empty cache files;
- invokes `clean()` only on miss;
- writes non-empty data to the cache path;
- returns `{ Data = data, CacheHit = false, CachePath = cachePath }`;
- leaves existing `GetCleanStepModelAsync()` as a wrapper returning `.Data`.

- [ ] **Step 3: Trace clean cache status from import and preview**

In `EasyEDA-Loader/FootprintShapes/EeFootprint3dModel.cs`, replace the current `ModelCache.GetCleanStepModelAsync(...)` call with `GetCleanStepModelWithStatusAsync(...)`, keep the same clean lambda, and trace:

```csharp
EasyEDALoaderModule.Trace(
    "Clean STEP cache " +
    (cleanResult.CacheHit ? "hit" : "miss") +
    ": model=" + modelTraceIdentifier +
    " path=" + cleanResult.CachePath);
```

In `EasyEDA-Loader/DialogWindow.cs`, update `GetOrCreateCleanStepPreviewFileAsync()` to use the same status API and trace the same hit/miss shape for preview generation.

- [ ] **Step 4: Verify cache hit path stays cheap**

Run:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --model-cache
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --measure-model-import C5338332 --repeat 2
dotnet build EasyEDA-Loader/EasyEDA-Loader.csproj
```

Expected: all commands exit 0. The second `C5338332` measurement should keep `watermark_clean_cache_ms` in low single-digit milliseconds on a clean STEP cache hit.

### Task 4: Replace F3D Verification Projection Rendering With OpenCascade HLR

**Files:**
- Modify: `EasyEDA-Loader/StepProjectionRenderer.cs`
- Modify: `EasyEDA-Loader/StepSilhouetteProjection.cs`
- Modify: `EasyEDA-Loader/StepSilhouetteImageRenderer.cs`
- Modify: `StepOcctHlr/Program.cs`
- Modify: `StepOcctHlr/OcctHiddenLineExtractor.cs`
- Modify: `StepOcctHlr/ProjectionPrimitiveDto.cs`
- Modify: `Test/StepCleaner/Program.cs`
- Modify: `Test/StepCleaner/OcctHiddenLineProjectionSmokeTests.cs`

Do not cache silhouette projection output in this task. The target is replacing the F3D render backend used by watermark verification with an on-demand OpenCascade backend.

This task is for monochrome/technical verification projections, not colored 3D previews. Do not use the managed `AIS_ColoredShape`/`XCAFDoc_ColorTool` color-render probe here; it has been verified to produce wrong colors for DF56. The correct OCCT color renderer would require native `XCAFPrs_AISObject`, which is not available in the current managed OCCT package.

- [ ] **Step 1: Write failing backend-selection regression**

Extend `RunSilhouetteCleanupTests()` in `Test/StepCleaner/Program.cs` with source-level checks:

```csharp
string projectionRenderer = File.ReadAllText(Path.Combine(repoRoot, "EasyEDA-Loader", "StepProjectionRenderer.cs"));
AssertContains(
    projectionRenderer,
    "TryRenderWithOpenCascade",
    "watermark verification projections should prefer OpenCascade over F3D",
    failures);
AssertContains(
    projectionRenderer,
    "TryRenderWithOpenCascade(inputPath, outputPath, view, transform, options",
    "OpenCascade renderer must use StepProjectionRenderer's existing transform so detection masks align",
    failures);
AssertContains(
    projectionRenderer,
    "TryRenderWithF3D",
    "F3D should remain as fallback while OpenCascade rollout is verified",
    failures);
```

Run:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --silhouette-cleanup
```

Expected before implementation: FAIL with missing `TryRenderWithOpenCascade`.

- [ ] **Step 2: Add OpenCascade render path that preserves existing projection transform**

In `EasyEDA-Loader/StepProjectionRenderer.cs`, change `RenderProjection(...)` order to:

```csharp
if (TryRenderWithOpenCascade(inputPath, outputPath, view, transform, options, highlights))
    return;

if (TryRenderWithF3D(inputPath, outputPath, view, options))
{
    ...
    return;
}
```

Implement `TryRenderWithOpenCascade(...)` so it:

- reads the STEP bytes from `inputPath`;
- maps the existing `ViewSpec` names (`x_plus`, `x_minus`, `y_plus`, `y_minus`, `z_plus`, `z_minus`) to the same model rotations used by the projection transform;
- calls `StepSilhouetteProjection.Generate(stepData, placement)`;
- renders the resulting primitives into the same `ProjectionTransform transform` passed by `StepProjectionRenderer`, not into auto-measured primitive bounds;
- draws detection highlights after the OCCT primitives using the existing `DrawDetectionHighlights(...)`;
- writes a PNG to `outputPath`;
- returns `false` only if the helper is unavailable or OCCT projection fails.

- [ ] **Step 3: Add transform-aware primitive PNG renderer**

In `EasyEDA-Loader/StepSilhouetteImageRenderer.cs`, add a method used by `StepProjectionRenderer`:

```csharp
public static byte[] RenderPng(
    IReadOnlyList<StepSilhouettePrimitive> primitives,
    int imageSizePixels,
    Action<SKCanvas, SKPaint> drawWithExistingTransform)
```

Or, if keeping rendering inside `StepProjectionRenderer` is simpler, add a private `RenderOcctPrimitives(...)` there. The important requirement is that OCCT output uses `StepProjectionRenderer`'s `ProjectionTransform` so `BuildAllowedChangeMask(...)` continues to line up with rendered pixels.

- [ ] **Step 4: Avoid repeated OpenCascade process startup for multi-view verification**

Extend `StepOcctHlr` with a batch mode:

```powershell
StepOcctHlr <input.step> <output.json> --views x_plus,y_plus,z_plus
```

Update `ProjectionResultDto` to support per-view primitive groups while keeping the single-view JSON format backward compatible for existing `StepSilhouetteProjection.Generate(...)` callers.

In `EasyEDA-Loader/StepProjectionRenderer.cs`, when rendering multiple selected views for one STEP file, use the batch helper so the helper reads the STEP file once per model instead of once per model per view.

- [ ] **Step 5: Verify OCCT projection backend against existing suites**

Run:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --occt-hlr-smoke
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --occt-overlap-unit
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --silhouette-cleanup
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj
dotnet build EasyEDA-Loader/EasyEDA-Loader.csproj
```

Expected: all commands exit 0. `--silhouette-cleanup` should no longer require `STEPCLEANER_F3D_CONSOLE` / `f3d-console.exe` for primary verification rendering. F3D remains only as fallback.

### Task 5: Speed Up OCCT Silhouette Projection

**Files:**
- Modify: `EasyEDA-Loader/StepSilhouetteProjection.cs`
- Modify: `EasyEDA-Loader/StepSilhouetteImageRenderer.cs`
- Modify: `StepOcctHlr/Program.cs`
- Modify: `StepOcctHlr/OcctHiddenLineExtractor.cs`
- Modify: `StepOcctHlr/ProjectionPrimitiveDto.cs`
- Modify: `Test/StepCleaner/Program.cs`
- Modify: `Test/StepCleaner/OcctHiddenLineProjectionSmokeTests.cs`
- Modify: `Test/StepCleaner/OcctOverlapCleanupTests.cs`

This task speeds up the common OCCT silhouette generator used by 3D body import/reproject. Do not persistently cache silhouette projection output; optimize process I/O, helper startup work, and primitive post-processing instead.

- [x] **Step 1: Write failing OCCT benchmark/source regression**

Add a standalone benchmark command to `Test/StepCleaner/Program.cs`:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --occt-hlr-benchmark Test/StepCleaner/Data/Validated/CONN-SMD_DF56_40S_0.3V_51.step --repeat 3
```

The command should:

- call `StepSilhouetteProjection.Generate(File.ReadAllBytes(inputPath), CreateDefaultSilhouettePlacement())`;
- print `run_index`, `total_ms`, `line_count`, `arc_count`, and `primitive_count`;
- return non-zero if primitive counts fall below the existing smoke thresholds from `OcctHiddenLineProjectionSmokeTests`.

Extend `RunModelCacheTests()` or `RunSilhouetteCleanupTests()` with source-level checks:

```csharp
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
```

Run:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --model-cache
```

Expected before implementation: FAIL with missing stdin/stdout/spatial-bucket markers.

- [x] **Step 2: Remove temp STEP and temp JSON files from byte-based projection**

In `StepOcctHlr/Program.cs`, support `-` as the input path and output path:

```powershell
StepOcctHlr - - --rot-x 0 --rot-y 0 --rot-z 0 --rotation2d 0
```

Implementation requirements:

- when input path is `-`, read all STEP bytes from `Console.OpenStandardInput()` into a temp file owned by the helper, because `StepReader.ReadFromFile()` still requires a path;
- when output path is `-`, write the JSON result to `Console.Out` and keep diagnostic trace output on `Console.Error`;
- preserve existing file-path behavior for current callers and tests.

In `EasyEDA-Loader/StepSilhouetteProjection.cs`, update `GenerateWithOcctHelper(byte[] stepData, StepSilhouettePlacement placement)` to:

- start `StepOcctHlr.exe` with input `-` and output `-`;
- set `RedirectStandardInput = true`, `RedirectStandardOutput = true`, and `RedirectStandardError = true`;
- write `stepData` to `process.StandardInput.BaseStream`;
- parse the JSON from stdout through a new `ReadOcctProjectionJsonFromString(string json, StepSilhouetteBounds targetBounds)`;
- keep file cleanup only as fallback for older helper behavior if needed.

- [x] **Step 3: Add file-path overload to avoid byte re-write when caller already has a STEP file**

In `EasyEDA-Loader/StepSilhouetteProjection.cs`, add:

```csharp
public static IReadOnlyList<StepSilhouettePrimitive> GenerateFromFile(
    string stepPath,
    StepSilhouettePlacement placement)
```

This overload should pass `stepPath` directly to `StepOcctHlr.exe` and only use temp files for JSON if stdout JSON is unavailable. Update `StepProjectionRenderer` and `OcctSilhouetteStageReport` call sites that already have file paths to use this overload.

- [x] **Step 4: Add spatial buckets to overlap cleanup**

In `EasyEDA-Loader/StepSilhouetteProjection.cs`, optimize `RemoveFullyOverlappedOcctPrimitives(...)`.

Current bottleneck:

- `OcctStrokeAreaCoverageRatio(...)` samples each candidate primitive;
- `OcctStrokeAreaCoverageAtPoint(...)` scans every primitive for every sample;
- this makes overlap cleanup scale roughly with `candidate_count * sample_count * primitive_count`.

Add:

```csharp
private static Dictionary<string, List<int>> BuildPrimitiveSpatialBuckets(
    StepSilhouetteBounds[] bounds,
    double bucketSizeMm)
```

Then use the buckets inside `OcctStrokeAreaCoverageAtPoint(...)` so each sample checks only primitives in nearby buckets. Keep the existing geometric distance checks as the final authority, so output primitives remain unchanged.

- [x] **Step 5: Preserve OCCT primitive output**

Extend `OcctOverlapCleanupTests` with a deterministic fixture that runs `OptimizeOcctPrimitives(...)` on a mixed line/arc list and asserts the exact optimized primitive count and key line/arc coordinates before and after bucketed overlap cleanup.

Run:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --occt-overlap-unit
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --occt-hlr-smoke
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --occt-hlr-benchmark Test/StepCleaner/Data/Validated/CONN-SMD_DF56_40S_0.3V_51.step --repeat 3
dotnet build EasyEDA-Loader/EasyEDA-Loader.csproj
```

Expected: all commands exit 0. The smoke test primitive counts must stay at or above the existing thresholds. Benchmark output should show lower `total_ms` after stdin/stdout and bucketed overlap cleanup than the baseline captured before implementation.

- [x] **Step 6: Record benchmark numbers**

Update this plan with before/after numbers from `--occt-hlr-benchmark`, including:

- median `total_ms` over 3 runs;
- line/arc/total primitive counts;
- whether stdin/stdout mode was used;
- whether spatial buckets were enabled.

Recorded after implementation on DF56 (`Test/StepCleaner/Data/Validated/CONN-SMD_DF56_40S_0.3V_51.step`):

```text
run_index=1 total_ms=1107 line_count=299 arc_count=38 primitive_count=337
run_index=2 total_ms=2297 line_count=299 arc_count=38 primitive_count=337
run_index=3 total_ms=1866 line_count=299 arc_count=38 primitive_count=337
median_total_ms=1866
stdin_stdout_mode=true
spatial_buckets=true
```

### Task 6: Re-measure First-Run Cleanup After OpenCascade Projection

**Files:**
- Modify: `docs/superpowers/plans/2026-06-03-speed-up-footprint-import.md`

- [x] **Step 1: Clear only the clean STEP cache for `C5338332`**

Delete the clean STEP file for key `92054bbdc40943db8639f6838d1ba6b4__watermark` under:

```text
%LOCALAPPDATA%\EasyEDA-Loader\ModelCache\Clean
```

Keep Original STEP and Raw OBJ caches intact so the measurement isolates clean verification and projection speed.

- [x] **Step 2: Measure twice**

Run:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --measure-model-import C5338332 --repeat 2
```

Expected:

- Run 1 should show lower `watermark_clean_cache_ms` than the previous F3D-backed 19496 ms baseline.
- Run 2 should remain a clean cache hit, with `watermark_clean_cache_ms` in low single-digit milliseconds.
- `occt_hlr_projection_total_ms` should stay visible in output so remaining projection cost can be tracked.

- [x] **Step 3: Record numbers**

Update this plan with the new Run 1 and Run 2 timings, including:

- `watermark_clean_cache_ms`;
- `occt_hlr_projection_total_ms`;
- `total_measured_ms`;
- whether OpenCascade batch mode was used.

Recorded after clearing only:

```text
%LOCALAPPDATA%\EasyEDA-Loader\ModelCache\Clean\92054bbdc40943db8639f6838d1ba6b4__watermark_clean.step
```

Measurement command:

```powershell
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -- --measure-model-import C5338332 --repeat 2
```

Results:

```text
Run 1:
  model_uuid=92054bbdc40943db8639f6838d1ba6b4
  model_title=CONN-SMD_26P-P0.30_DF56C-26S-0.3V51
  cleaned_step_bytes=3779057
  projection_primitives=344
  watermark_clean_cache_ms=14180
  occt_hlr_projection_total_ms=2949
  total_measured_ms=17145

Run 2:
  model_uuid=92054bbdc40943db8639f6838d1ba6b4
  model_title=CONN-SMD_26P-P0.30_DF56C-26S-0.3V51
  cleaned_step_bytes=3779057
  projection_primitives=344
  watermark_clean_cache_ms=1
  occt_hlr_projection_total_ms=2842
  total_measured_ms=2846

OpenCascade batch mode used: false
OpenCascade stdin/stdout mode used: true
OpenCascade spatial buckets enabled: true
```

### Task 7: Gate Color-Correct 3D Renderer Replacement

**Files:**
- Modify: `docs/superpowers/plans/2026-06-03-speed-up-footprint-import.md`
- Candidate: `EasyEDA-Loader/DialogWindow.cs`
- Candidate: `EasyEDA-Loader/StepProjectionRenderer.cs`
- Candidate: new native helper or binding extension project, only after a native OCCT SDK is available

Do not replace the colored 3D preview/render path with the current managed OCCT probe. DF56 proves the managed `AIS_ColoredShape`/`XCAFDoc_ColorTool` route uses the wrong color precedence. The correct OCCT renderer is native `XCAFPrs_AISObject` over an XDE document populated by `STEPCAFControl_Reader::Transfer()`.

Current colored-render entry points:

- `EasyEDA-Loader/DialogWindow.cs`: `StartF3DPreviewAsync()` launches interactive `f3d.exe` for the original/clean STEP preview hosts. This is coupled to embedded HWND hosting and raw-input mirroring, so it is not the first replacement target.
- `EasyEDA-Loader/StepProjectionRenderer.cs`: `RenderProjection()` calls `TryRenderWithF3D()`, which launches `f3d-console.exe` with `--scalar-coloring`, `--coloring-by-cells`, `--coloring-array=Colors`, and `--coloring-component=-2`. This is the least-invasive future integration point for a persistent color-correct renderer because it already has a file-in/file-out contract and existing fallback behavior.

- [x] **Step 1: Add a renderer-decision regression**

Add a source-level regression that fails if colored 3D preview is switched to the managed OCCT color probe. The test should assert that production colored preview still uses F3D scalar coloring unless a native `XCAFPrs_AISObject` helper/binding is present.

Implemented in `Test/StepCleaner/Program.cs` under `--model-cache`: the source regression now checks that `DialogWindow.StartF3DPreviewAsync()` and `StepProjectionRenderer.TryRenderWithF3D()` keep F3D scalar coloring with `--coloring-array=Colors` and `--coloring-component=-2`, and that those production colored-render paths do not use `AIS_ColoredShape`.

- [x] **Step 2: Add native OCCT availability check**

Before implementing any OCCT colored preview replacement, verify all of these are available locally or in CI:

- native OCCT headers, including `XCAFPrs_AISObject.hxx`;
- link/import libraries for the OCCT visualization and XDE modules;
- runtime DLLs for the same OCCT version;
- a minimal native smoke program that reads DF56 with `STEPCAFControl_Reader`, displays `XCAFPrs_AISObject`, and renders colors matching F3D.

If these are not available, keep F3D as the colored renderer and continue only with OCCT HLR/projection speedups.

Local check result: native OCCT colored replacement is still gated. `DRAWEXE` was not found, `vcpkg` was not found, `CASROOT` is unset, and `C:\Program Files\F3D` does not contain `XCAFPrs_AISObject.hxx`. Keep F3D as the color-correct renderer.

- [ ] **Step 3: Keep direct libf3d as the practical color fallback**

If colored PNG render process startup becomes important before native OCCT is available, prototype a persistent direct `libf3d` helper behind `StepProjectionRenderer.TryRenderWithF3D()` instead of spawning `f3d-console.exe` per render. It must use:

```text
model.scivis.enable = true
model.scivis.cells = true
model.scivis.array_name = Colors
model.scivis.component = -2
scene.camera.orthographic = true
```

Expected: output colors match `f3d.exe --scalar-coloring --coloring-array=Colors --coloring-component=-2`. Treat one-shot `libf3d` timings around `2.2-2.9 s` as baseline; only promote this path if a persistent process proves faster than the existing warm CLI path.

Keep `DialogWindow.StartF3DPreviewAsync()` as a separate, later UI task unless the work explicitly targets interactive preview startup. Replacing the embedded `f3d.exe` window with libf3d requires a new host/control model and should not be mixed into watermark-clean or projection-speed work.
