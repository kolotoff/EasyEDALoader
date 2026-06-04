# Speed Up Footprint Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce slow footprint imports by caching expensive Altium-independent model processing before touching Altium-side primitive creation.

**Architecture:** Keep Altium-side footprint creation unchanged. First optimize the pre-Altium model path used by components such as `C5338332`: original STEP and raw OBJ are already cached, so the next win is to reuse the existing cleaned STEP cache for import-time watermark cleanup instead of cleaning and projection-verifying on every import.

**Tech Stack:** C#/.NET, EasyEDA-Loader Altium add-in, standalone `StepCleaner.Tests` console regression harness.

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
