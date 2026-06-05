# XT60 LCEDA Step Cleaner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the STEP cleaner report silent no-op watermark misses, clean the bottom geometric `LCEDA` watermark in `CONN-TH_XT60PB-M.step`, and avoid exporting real component geometry as removed watermark geometry.

**Architecture:** Keep reusable cleanup logic in `EasyEDA-Loader\StepWatermarkCleaner.cs` and keep CLI/app verification behavior aligned between `StepCleaner\Program.cs` and `EasyEDA-Loader\StepWatermarkCleanVerifier.cs`. Detection may report multiple visual boxes, but editing must merge clustered watermark regions into a 3D cleanup volume, derive host/top/bottom coordinates on the watermark axis, and refuse to touch geometry outside that volume. Add focused regression coverage in the existing console test harness before changing production code.

**Tech Stack:** C#/.NET 8 test harness, .NET Framework-linked loader source, SkiaSharp/F3D projection verification.

---

### Task 1: Focused Red Tests For Silent XT60 Miss

**Files:**
- Modify: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Add a new `--xt60-lceda` command**

Add a command branch near the existing command dispatch:

```csharp
if (IsOption(args[0], "--xt60-lceda"))
    return RunXt60LcedaWatermarkTests();
```

- [ ] **Step 2: Add a focused failing test method**

Add `RunXt60LcedaWatermarkTests()` that:

```csharp
string inputPath = Path.Combine(FindDataRoot(), "Original", "CONN-TH_XT60PB-M.step");
byte[] original = File.ReadAllBytes(inputPath);
StepWatermarkCleanerReport report = StepWatermarkCleaner.CleanWithReport(
    Encoding.Latin1.GetString(original),
    new StepWatermarkCleanerOptions());
byte[] cleaned = Encoding.Latin1.GetBytes(report.CleanedStep);

AssertEqual("not equal", cleaned.SequenceEqual(original) ? "equal" : "not equal",
    "XT60 bottom LCEDA cleanup should change the STEP output", failures);
AssertContains(
    string.Join(",", report.DetectionReport.Regions.Select(region => region.ViewName)),
    "z_minus",
    "XT60 bottom LCEDA detection should report a z_minus cleanup region",
    failures);
```

- [ ] **Step 3: Run the red test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --xt60-lceda
```

Expected: FAIL because current cleanup is byte-identical and `Regions` is empty.

### Task 2: Report Zero-Region Verification As A Failure

**Files:**
- Modify: `EasyEDA-Loader\StepWatermarkCleanVerifier.cs`
- Modify: `StepCleaner\Program.cs`
- Modify: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Add test coverage to `--xt60-lceda`**

Call `StepWatermarkCleanVerifier.CleanOrThrowWithReport(...)` for XT60 into an ignored temp directory. Before the fix, assert that it should throw instead of silently passing with no regions.

- [ ] **Step 2: Add a shared failure message**

When post-clean verification receives zero projected detection regions after cleanup was requested, add a failure like:

```text
<model> has no detected watermark cleanup regions; post-clean verification cannot prove the watermark was removed.
```

- [ ] **Step 3: Apply the same rule in CLI and app verifier**

In both verification implementations, replace the zero-view early-success path with a failure entry and report write. CLI should return the existing post-clean verification failed exit code.

- [ ] **Step 4: Run the red/green test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --xt60-lceda
```

Expected after Task 2 only: verifier no longer silently passes, but XT60 cleanup still fails because detection/cleanup is not implemented.

### Task 3: Detect Geometric LCEDA Host Loops

**Files:**
- Modify: `EasyEDA-Loader\StepWatermarkCleaner.cs`
- Modify: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Add detector assertions**

Keep `--xt60-lceda` asserting `DetectionReport.HostLoopCount > 0`, `Regions` contains `z_minus`, and output differs from original.

- [ ] **Step 2: Extend host-loop candidate detection**

Allow known-pattern host-loop clusters on colored host faces when the loops are small, interior, and pattern-shaped. The XT60 watermark is encoded as inner `FACE_BOUND` loops on bottom host face `#5285`, including compact text loop bounds around `z=-8`.

- [ ] **Step 3: Preserve guardrails**

Keep existing small-mark, interior-boundary, clustered known-pattern, and host-face shape gates. Do not globally remove `HasProtectedNonWatermarkColor`; add a narrowly-scoped geometric host-loop path for same-color engraved/cut text.

- [ ] **Step 4: Run focused test**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --xt60-lceda
```

Expected: PASS with nonzero host-loop detection and changed clean output.

### Task 4: Post-Clean Regression Coverage

**Files:**
- Modify: `Test\StepCleaner\Program.cs`
- Optionally create/update generated ignored `Test\StepCleaner\Data\Clean*` artifacts only during verification

- [ ] **Step 1: Tighten full post-clean checks**

In `VerifyPostCleanProjections`, fail or explicitly record a post-clean fault for models that are expected to be cleaned but have zero detected views. Use the focused XT60 test as the hard regression so the full suite can remain compatible with fixtures that lack accepted `Validated` outputs.

- [ ] **Step 2: Add cleanup notes**

Add a cleanup note for `CONN-TH_XT60PB-M.step` describing the bottom geometric LCEDA watermark.

- [ ] **Step 3: Run focused and broad checks**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --xt60-lceda
dotnet run --project StepCleaner\StepCleaner.csproj -- Test\StepCleaner\Data\Original\CONN-TH_XT60PB-M.step Test\StepCleaner\Data\Clean\CONN-TH_XT60PB-M.step --debug
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

Expected: focused and CLI checks pass. Full suite may require existing golden/validated data review if XT60 is intentionally not yet accepted; report exact status.

### Task 5: Box-Limited Cleanup Volume And Removed-Geometry Guardrails

**Files:**
- Modify: `EasyEDA-Loader\StepWatermarkCleaner.cs`
- Modify: `Test\StepCleaner\Program.cs`

- [x] **Step 1: Add red tests for user-reported over-removal**

Extend `RunRemovedGeometryExportTests()` with the five user-reported fixtures:

```csharp
var overRemovalFixtures = new[]
{
    "CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step",
    "HDMI-SMD_HDMI-001S.step",
    "SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step",
    "TYPE-C-TH_TYPEC-215-ARP14.step",
    "USB-A-SMD_USB-212-BCW.step"
};
```

For each, clean and assert removed geometry is absent or small enough to be only watermark topology. The current multi-megabyte `.removed.step` outputs should fail this test before the cleaner guard is added.

- [x] **Step 2: Add red XT60 volume test**

Strengthen `RunXt60LcedaWatermarkTests()` to assert the cleaner creates one merged cleanup volume for the bottom `LCEDA` cluster and that flattened/removed faces stay inside that volume. The test can use diagnostics until a public report field is added:

```csharp
AssertContains(cleanerDiagnostics, "Automatic cleanup volumes: 1", "XT60 LCEDA should be edited as one merged bounded volume", failures);
AssertContains(cleanerDiagnostics, "Edited geometry outside cleanup volumes: 0", "XT60 LCEDA cleanup must not edit outside its bounded volume", failures);
```

- [x] **Step 3: Add cleanup-volume data model**

In `EasyEDA-Loader\StepWatermarkCleaner.cs`, add an internal `AutomaticCleanupVolume` with:

```csharp
public int OwnerId;
public int HostFaceId;
public int Axis;
public double HostCoordinate;
public double MinCoordinate;
public double MaxCoordinate;
public Bounds Bounds;
public Bounds HostBounds;
```

Build it from merged `AutomaticWatermarkRegion` clusters. The projected bounds are the union of all detected boxes in the cluster plus existing projection padding. The depth bounds are the nearest host coordinate plus the nearest opposite shallow/top/bottom watermark coordinate discovered from candidate face bounds. Support both cut and bump watermarks by allowing the second coordinate to be on either side of the host coordinate.

- [x] **Step 4: Use volumes for editing**

Replace broad `FlattenAllGeometryInsideAutomaticRegions(...)` selection with volume-limited selection:

```csharp
if (!ProjectedBoundsInside(faceBounds.Value, volume.Bounds, volume.Axis, options.HostPlaneProjectionPadding))
    continue;

if (!BoundsWithinCleanupDepth(faceBounds.Value, volume, options))
    continue;
```

Only flatten points whose projected X/Y are inside the volume and whose depth coordinate is between `MinCoordinate` and `MaxCoordinate`. Do not edit host/body geometry outside the box. Continue removing only matching host inner bounds inside the projected volume.

- [x] **Step 5: Keep reporting and diagnostics**

Keep `StepWatermarkDetectionReport.Regions` compatible with the current projection overlay, but add diagnostics for volume count and outside-volume edit count. Removed geometry should contain only solids/faces/host loops that are selected by those volumes.

- [x] **Step 6: Run red/green checks**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --xt60-lceda
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --removed-geometry
```

Expected after implementation: both pass; before implementation, at least the strengthened assertions fail.

### Task 6: Verification Projection Upgrade

**Files:**
- Modify: `EasyEDA-Loader\StepWatermarkCleanVerifier.cs`
- Modify: `StepCleaner\Program.cs`
- Modify: `Test\StepCleaner\Program.cs`

- [ ] **Step 1: Add six-side residual scan**

After detection-region verification, generate six cleaned projections for known-watermark fixtures and scan for residual watermark-like line detail inside the merged cleanup volume. This catches oblique/wireframe-visible same-color seams that ordinary color projection can miss.

- [ ] **Step 2: Prefer stronger wire/edge signal**

If the existing renderer cannot show residual seams reliably, add a wireframe/edge projection option or reuse the hidden-line/OCCT projection path for post-clean residual checks. Keep this as a verifier-only signal; it must not drive cleanup selection.

- [ ] **Step 3: Run focused check**

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --xt60-lceda
```

Expected: XT60 fails on residual edge detail before the geometry box fix and passes after it.

### Task 7: Review And No Commit

**Files:**
- Review all modified files

- [ ] **Step 1: Dispatch subagent review**

Ask one subagent to review spec compliance and another to review code quality for the final diff.

- [ ] **Step 2: Run final verification**

Run fresh focused verification after any review fixes.

- [ ] **Step 3: Stop without committing**

Leave changes unstaged/uncommitted unless the user asks for a commit.
