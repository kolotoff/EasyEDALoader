# STEP Vector 2D Projection Summary And Fix Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` if this plan is implemented later. This file is an investigation summary and implementation plan only; it intentionally does not change renderer code.

**Goal:** Replace the failed raster/heuristic `EdgeVisibleRaw` experiments with a first-class vector 2D projection pipeline for STEP models, then rasterize that vector result only as a preview/export consumer.

**Architecture:** Generate canonical per-view vector primitives from OCCT hidden-line removal, store them in a typed JSON contract, and make PNG rendering consume that vector contract. Keep watermark/logo detection separate from projection geometry; do not copy pixels between renderers and do not use image-space filters to decide CAD visibility.

**Tech Stack:** C#/.NET 8, `Occt.NET 7.9.0`, existing `StepOcctHlr` helper, optional native OCCT helper only if managed wrapper inspection proves insufficient, SkiaSharp only for rasterizing vector output.

---

## Chats And Artifacts Reviewed

- `Fix STEP edge rendering orig` (`019ecbeb-0ac9-76a3-b330-48b4bdf2e116`)
  - Mesh renderer attempt was implemented, judged worse, rolled back.
  - Current code was restored to `05da449^` and committed as `c024f3ca Restore pre-05da449 visible raw renderer`.
- `Fix STEP edge rendering` (`019e9e08-22b6-7020-8b7e-acb130014ece`)
  - Real-HLR and semantic/hybrid attempts were implemented and visually rejected.
  - Direct public `HLRBRep_Algo` constructors in `Occt.NET` were observed to hang; the working code fell back to the high-level `HlrBRepAlgo` wrapper.
- `Check OpenCascade SWG projection` (`019ebc94-623a-7553-9370-c2b5ed99651b`)
  - Confirmed the correct CAD concept: OCCT HLR produces 2D projected edge shapes that can be converted to SVG/vector output.
- Existing plan files:
  - `docs/superpowers/plans/2026-06-06-stepcleaner-visible-raw-occt-edges.md`
  - `docs/superpowers/plans/2026-06-12-stepcleaner-real-occt-hlr-extraction.md`
  - `docs/superpowers/plans/2026-06-12-stepcleaner-single-renderer-mesh.md`

## Current State

- `HEAD` is `4006f09 Route visible raw projection through vector HLR`.
- Committed vector-helper work:
  - `622fee5 Add vector STEP projection debug output`
  - `0eda2ca Validate vector HLR line endpoints`
  - `4006f09 Route visible raw projection through vector HLR`
- `EasyEDA-Loader/StepProjectionRenderer.cs` routes `EdgeVisibleRaw` through vector OCCT HLR output, not through the local STEP parser depth-tested raw renderer.
- `StepOcctHlr` exposes `--vector-views` plus optional SVG debug output.
- `StepOcctHlr/OcctVectorHiddenLineExtractor.cs` uses the high-level `HlrBRepAlgo` wrapper and currently parses helper output into vector primitives while preserving visibility/category metadata.
- `Test/StepCleaner/Program.cs` has a `--vector-projection-contract` harness and `--visible-raw-silhouette-edge-projection` guard aligned to the vector architecture.
- Current uncommitted work:
  - expands the vector validation set to MR30, HDMI, SOT-223, and USB-B;
  - strengthens SVG debug validation with XML parsing and raster-reference rejection;
  - removes the dead `RenderVisibleRawEdgeProjectionImage(...)` method while keeping shared depth-buffer helpers used by color rendering.

## Plan State

- [x] Task 1: Lock the failure with tests.
  - Added `--vector-projection-contract`.
  - Replaced old active-path assertions with vector-route assertions.
- [x] Task 2: Add helper CLI without changing preview rendering.
  - Added `--vector-views`.
  - Added `VectorProjectionResultDto` output and engine marker.
- [x] Task 3: Implement managed vector extraction first.
  - Added managed vector extraction through `HlrBRepAlgo`.
  - Preserved primitive `Kind`, `Visibility`, `Category`, `SourceIndex`, points, and arc data.
  - Fixed line endpoint validation to reject parser-created long diagonal artifacts.
- [x] Task 4: Probe whether a native helper is required.
  - Managed `Occt.NET 7.9.0` exposes direct edge/curve traversal APIs, but the scratch runtime probe hung in this environment before returning useful typed HLR edges. Do not introduce a native helper yet.
  - Implemented the managed BREP-adapter fallback instead: type-7 degree-1 records remain lines, higher-degree type-7 records become explicit `bspline`/`rational-bspline` polyline fallback primitives, and non-circular conics become explicit `ellipse` polyline fallback primitives.
  - Follow-up fix: type-7 B-spline records are now parsed as two-line BREP records, including knot/multiplicity pairs, and sampled with de Boor evaluation over the edge parameter range instead of drawing control polygons.
  - Fixed debug SVG output so JSON `arc` primitives emit real SVG `A` commands instead of sampled `L` segment paths.
  - Validation result: HDMI `y_plus` now has 324 `bspline` polylines, 103 `rational-bspline` polylines, 359 lines, and 1 arc; short line fragments under 0.2 mm dropped from 375 to 106.
  - Follow-up validation result: USB-B `x_minus` and SOT-223 `z_minus` now have zero B-spline fallback primitives with fewer than 24 sampled points; their small text/logo marks render as evaluated curves instead of jagged control polygons.
  - Follow-up orientation fix: all six standard vector views now apply the readable horizontal mirror policy through the shared OCCT BREP point/vector transform, so JSON, SVG, and PNG debug output use the same corrected coordinates. Real-fixture checks cover USB-B `x_minus`, USB-B `z_plus`, SOT-223 `z_plus`, and SOT-223 `z_minus`; a policy check covers `x_plus`, `y_plus`, and `y_minus` until asymmetric readable-marking fixtures are added for those views.
- [x] Task 5: Add vector raster consumer.
  - Routed `EdgeVisibleRaw` through vector projection in both in-memory and file-output paths.
  - PNG rendering consumes vector primitives.
- [x] Task 6: Generate SVG debug output.
  - `--vector-svg-dir` writes one SVG per requested view.
  - Debug output is generated under `.codex-temp/vector-projection-debug/`.
- [x] Task 7: Validation set.
  - Added four-fixture validation for MR30, HDMI, SOT-223, and USB-B.
  - Added bounds/primitive checks for known collapse/detached-line cases.
  - Added mirror-sensitive vector fallback cluster checks for USB-B `x_minus`, USB-B `z_plus`, SOT-223 `z_plus`, and SOT-223 `z_minus`, plus a six-view mirror policy guard, so readable text/logo marks cannot regress to mirrored output.
  - Added XML-level SVG validation that rejects embedded raster image references.
  - Status: implemented and extended with orientation regression coverage; not yet committed.
- [x] Task 8: Final cleanup.
  - Removed the dead legacy `RenderVisibleRawEdgeProjectionImage(...)` method.
  - Kept shared depth-tested drawing helpers because normal color rendering still uses them.
  - Status: implemented but not yet committed.

## Why The Attempts Failed

1. **Old/pre-`05da449` local renderer is not a CAD HLR renderer.**
   - It projects reconstructed STEP topology (`ProjectionModel`) and approximates visibility with one depth plane per face.
   - This is fragile for curved, trimmed, reused, and complex connector geometry.
   - It can look better for text/logo strokes because it draws many raw loops, but it is not a correct visible projection.

2. **Routing to existing OCCT visible primitives fixed the wrong layer.**
   - `StepSilhouetteProjection.GenerateVisibleRawViews(...)` gives 2D visible primitives, but they come through a lossy ASCII BREP parsing layer.
   - The active data model has only `Line` and `Arc` with 2D coordinates. It has no source shape, visibility category, original edge identity, curve type, hidden/visible distinction, or depth/sample metadata.
   - Once the vector source is reduced to a flat 2D stroke list, later code can only apply heuristics.

3. **Semantic marking overlays mixed two unrelated concerns.**
   - Text/logo cleanup and CAD projection are different problems.
   - Attempts to suppress/redraw markings by detecting image regions or copying pixels produced broken text, missing body geometry, and view-dependent artifacts.
   - The correct fix is to project geometry vectors first. Marking cleanup can consume the vectors later; it should not decide visibility for the projection renderer.

4. **The "real OCCT HLR" attempt was not actually a reliable direct API path.**
   - The planned `HLRBRep_Algo` + `HLRBRep_HLRToShape` route matches OCCT docs, but public `Occt.NET` constructors were observed to hang.
   - Implementation therefore reused `HlrBRepAlgo`, so it did not remove the wrapper/API limitation.
   - It still emitted 2D primitives through the same insufficient contract.

5. **The mesh renderer turned visibility into raster heuristics again.**
   - `TriangulationHelper.GetTriangulation(...)` made mesh export practical after `BRepMesh` paths hung, but using mesh coverage to filter 2D HLR strokes created alignment/threshold problems.
   - The result was visually worse: missed geometry, stray lines outside the body, broken logo/text.
   - This was correctly rolled back.

## Important OCCT Facts

- OCCT HLR is the right algorithm family. The official modeling guide says `HLRBRep_Algo` works on the exact shape, while `HLRBRep_PolyAlgo` works on a polyhedral simplification; exact HLR gives exact projected results, while poly HLR gives polygonal segments.
- `HLRBRep_HLRToShape` is the intended extraction layer. Its output includes visible sharp, smooth, sewn, and outline edges; docs state the result is composed of 2D edges in the projection plane.
- Therefore the fix should produce vector edges from HLR output. Raster PNGs should be a downstream representation, never the source of truth.

References:
- https://dev.opencascade.org/doc/overview/html/occt_user_guides__modeling_algos.html#occt_modalg_11
- https://dev.opencascade.org/doc/refman/html/class_h_l_r_b_rep___h_l_r_to_shape.html

## Proposed Fix

Build a new vector contract and pipeline:

```text
STEP bytes/file
  -> StepOcctHlr vector projection helper
  -> StepVectorProjectionResult JSON
  -> consumers:
       1. PNG preview renderer
       2. SVG/DXF/debug export
       3. watermark/text detection review tools
```

The important shift is that `EdgeVisibleRaw` should no longer be "a special PNG renderer". It should request a vector projection for one or more views and render that projection.

## File Structure

- Create `StepOcctHlr/VectorProjectionDto.cs`
  - Defines canonical vector projection JSON.
- Create `StepOcctHlr/OcctVectorHiddenLineExtractor.cs`
  - Owns HLR extraction and vector primitive conversion.
- Modify `StepOcctHlr/Program.cs`
  - Adds `--vector-views` and optional `--vector-format json|svg`.
- Modify `EasyEDA-Loader/StepVectorProjection.cs`
  - Managed wrapper that invokes `StepOcctHlr --vector-views`.
- Modify `EasyEDA-Loader/StepProjectionRenderer.cs`
  - Routes `EdgeVisibleRaw` through vector projection, then rasterizes vectors.
- Modify `Test/StepCleaner/Program.cs`
  - Replaces old source assertions with vector-contract and behavioral checks.
- Optional later fallback: create a native OCCT helper only if the managed wrapper cannot expose enough topology/curve data without BREP text parsing.

## Vector Contract

Add a DTO contract like this:

```csharp
internal sealed class VectorProjectionResultDto
{
    public bool Success { get; set; }
    public string Error { get; set; }
    public string Engine { get; set; }
    public List<VectorProjectionViewDto> Views { get; set; } = new List<VectorProjectionViewDto>();
}

internal sealed class VectorProjectionViewDto
{
    public string Name { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; }
    public VectorBoundsDto Bounds { get; set; }
    public List<VectorProjectionPrimitiveDto> Primitives { get; set; } = new List<VectorProjectionPrimitiveDto>();
}

internal sealed class VectorProjectionPrimitiveDto
{
    public string Kind { get; set; }          // line, circle, arc, ellipse, bspline, polyline
    public string Visibility { get; set; }    // visible or hidden; default visible for preview
    public string Category { get; set; }      // sharp, smooth, sewn, outline, iso
    public int SourceIndex { get; set; }      // stable per helper run when available
    public double[] Points { get; set; }      // flattened 2D control/sample points
    public double[] Knots { get; set; }       // for splines when available
    public double[] Weights { get; set; }     // for rational splines when available
    public double Radius { get; set; }
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }
}
```

Rules:
- Do not downconvert everything to line/arc unless the curve is truly line/arc.
- For unsupported exact curve types, emit `polyline` with a declared tolerance and keep the original `Kind` in metadata.
- Include categories before dedupe. Dedupe must not erase category/source information.
- Use the vector bounds returned by the helper to frame the raster image. Do not recompute placement from a different STEP parser.

## Implementation Tasks

### Task 1: Lock The Current Failure With Tests

- Add a new command in `Test/StepCleaner/Program.cs`, for example `--vector-projection-contract`.
- It must invoke the helper on original models:
  - `Test/StepCleaner/Data/Original/CONN-TH_MR30PW-M30-G-Y.step`
  - `Test/StepCleaner/Data/Original/HDMI-SMD_HDMI-001S.step`
- Assert:
  - six views are returned;
  - each view has non-empty vector primitives;
  - every primitive has `Kind`, `Visibility`, and `Category`;
  - `CONN y_plus` has a projected horizontal span close to the older reference span, not the broken 476 px span from the failed real-HLR run;
  - `HDMI y_minus`/logo-bearing view has vector content in the known marked region.
- Update the old `--visible-raw-silhouette-edge-projection` source guard so it rejects `RenderVisibleRawEdgeProjectionImage(...)` as the active path.

### Task 2: Add Helper CLI Without Changing Preview Rendering

- Add `--vector-views x_plus,x_minus,...` to `StepOcctHlr/Program.cs`.
- Keep existing `--views` unchanged for old callers during this task.
- Output `VectorProjectionResultDto`.
- Add `Engine = "occt-hlr-vector-managed"` for the first implementation.
- Run this task red/green against only the helper contract test.

### Task 3: Implement Managed Vector Extraction First

- In `OcctVectorHiddenLineExtractor`, use the working `HlrBRepAlgo` wrapper initially because public `HLRBRep_Algo` construction was observed to hang.
- Extract category compounds separately:
  - visible sharp
  - visible smooth
  - visible sewn
  - visible outline
  - optional hidden categories for diagnostics, not preview default
- Replace ASCII BREP parsing only if wrapper APIs allow direct traversal of returned edges/curves.
- If direct curve traversal is not available, keep ASCII BREP parsing temporarily but make it a named adapter: `BrepTextVectorPrimitiveReader`. This makes the technical debt explicit and isolated.
- The adapter must preserve category and support `polyline` fallback rather than silently dropping unsupported curves.

### Task 4: Probe Whether A Native Helper Is Required

This is a decision gate, not a speculative rewrite.

- Probe `Occt.NET` for direct edge/curve traversal APIs on returned HLR compounds.
- If managed traversal can return enough exact curve data, continue managed.
- If the only reliable path is ASCII BREP text, decide whether that is acceptable.
- If unacceptable, create a separate native OCCT helper only after confirming headers/libs are available through an installed OCCT SDK or vcpkg. The current `Occt.NET` NuGet package includes OCCT DLLs, but inspection did not show headers/import libraries.

### Task 5: Add Vector Raster Consumer

- Add `EasyEDA-Loader/StepVectorProjection.cs`.
- Add `RenderVectorProjectionImage(...)` in `StepProjectionRenderer.cs`.
- Route `EdgeVisibleRaw` to:

```csharp
if (options.RenderMode == StepProjectionRenderMode.EdgeVisibleRaw)
    return ProjectFileVectorProjectionImages(stepData, selectedViews, options);
```

- Rasterizer responsibilities:
  - transform helper vector bounds to image coordinates;
  - draw exact lines/arcs where possible;
  - draw spline/polyline primitives from DTO points;
  - draw optional diagnostics by category color only when requested;
  - never use pixel-copy overlays.

### Task 6: Generate SVG Debug Output

- Add an optional CLI path that writes one SVG per requested view from the same vector DTO.
- Use SVG as the visual proof for "proper vector projection"; PNG comparison alone is not sufficient.
- Output under `.codex-temp/vector-projection-debug/`.

### Task 7: Validation Set

Generate six views from `Original`, not `Validated`, for:

- `CONN-TH_MR30PW-M30-G-Y`
- `HDMI-SMD_HDMI-001S`
- one simple package, e.g. `SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30`
- one USB connector with marked text, e.g. `USB-B-TH_USB-B10-BRW`

Acceptance:
- no blank views;
- no model-span collapse like the failed `CONN y_plus` case;
- no long detached lines outside body bounds;
- no pixel-copy source functions remain in active `EdgeVisibleRaw`;
- SVG output contains vector primitives, not embedded PNGs.

### Task 8: Final Cleanup

- Remove or quarantine old `RenderVisibleRawEdgeProjectionImage(...)` once the vector path is active.
- Keep the old path only under an explicit diagnostic mode if needed, never under `EdgeVisibleRaw`.
- Update tests so they validate vector behavior, not source-string presence of the old renderer.
- Leave generated `.codex-temp` output untracked.

## Risks

- `Occt.NET` public direct HLR constructors were observed to hang. Do not reattempt that path without a timeout-guarded probe.
- ASCII BREP parsing may remain necessary in managed mode. If so, isolate it and treat it as a vector adapter, not as the projection architecture.
- Text/logo appearance cannot be judged independently from projection correctness. First make vector projection correct; then handle watermark cleanup/detection on top of vector data.
- Native OCCT helper may require adding a real OCCT SDK dependency because the current NuGet inspection found DLLs but not headers/libs.

## Recommended Next Step

Commit the current Task 7/8 validation and cleanup slice after review. Then decide whether Task 4 needs deeper managed-API/native-helper probing; do not start a native helper unless a new validation fixture proves the managed vector path is insufficient.

## Current Detection Follow-Up, 2026-06-18

- [x] Add vector-only detector quality contract for the user-reported cases:
  `BUZ-TH_D9.0-H5.5-P4.0 z_plus`,
  `CONN-TH_MR30PW-M30-G-Y z_plus`,
  `SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50 x_plus`,
  `LED-SMD_XL-3838UV2SA06G3 y_minus`,
  `USB-C-SMD_TYPE-C-6PIN-2MD-073 z_minus`,
  and `HDMI-SMD_HDMI-001S z_plus`.
- [x] Reject false logo matches with a vector chamfer-distance guard.
- [x] Expand CleanText combined regions only from detected vector support primitives, without marked-data input to runtime detector code.
- [x] Keep normal detection parity unchanged while allowing CleanText arbitrary/manufacturer text regions to include all relevant strokes.
- [x] Speed up `MarkedVsDetected` report generation by batching all six vector views per model, sharing normal/CleanText detector work, processing models in parallel, and retrying failed batch projections with serial per-view fallback.
- [x] Generate `MarkedVsDetected` report with all marked images and all detected images.
- [x] Verify detector count target: `detected logos: 15`, `logo_matched=15`, `matched=26`.
- [x] Fix report bucketing so final text-only, logo-only, and logo+text facade regions all appear in combined/final report columns: `combined matched: 26`, `clean-text combined matched: 26`.
- [x] Run focused verification:
  `--vector-detection-quality-contract`,
  `--vector-detection-report-contract`,
  `--marked-vector-detection-parity`,
  and `--marked-vector-detection-parity-clean-text`.

Report timing after optimization: about 4 minutes on the current machine, compared with about 60 minutes before batching/parallelization.
