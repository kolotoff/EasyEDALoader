# Vector Watermark Detection Research And Plan

Date: 2026-06-16

This file records the detection-only plan for watermark regions after the vector 2D projection renderer work. It intentionally avoids code changes, generated test data, git staging, and commits.

## Goal

Create a vector-based watermark detector that does not use the F3D color renderer as algorithm input. It must split logo detection from text detection, detect arbitrary manufacturer text when `CleanText=true`, and report a combined watermark region for logo plus labels such as `EasyEDA` or `LCEDA`.

The first acceptance target is parity with user-marked truth:

- detected region count equals marked region count;
- each detected region is fully inside a matching marked rectangle;
- each detected region is smaller than the matching marked rectangle;
- marked data is used only by tests, reports, and visual verification, not by detector code.

Logo and text can be rotated independently to any right-angle orientation: `0`, `90`, `180`, or `270` degrees.

## Current Findings

- The current `StepTextLogoProjectionDetector` still consumes raster `StepProjectionImage` inputs.
- `StepWatermarkCleaner` creates color, optimized edge, and visible raw edge projections before detection.
- `MarkedVsDetected` currently creates a logo reference from marked data inside the report harness. That is acceptable only as historical report scaffolding; the new detector must not depend on marked-derived templates.
- `MarkedVsDetected` did not finish in this workspace when run over the full data set. A smaller `--text-logo-detection` run finished and showed that detections exist, but boxes drift against old raster expectations.
- Existing marked-parity tests already encode the right verification shape: same count and detected boxes inside marked boxes.

## Architecture

Build a vector-first detection layer:

```text
STEP
  -> StepOcctHlr vector projection
  -> view vector primitives
  -> vector candidate clustering
  -> split logo detector
  -> split text detector
  -> combined watermark region builder
  -> MarkedVsDetected visualization/report
```

Raster PNGs remain useful for human overlays, but they must not drive detection.

## Detector Units

### Vector Input Contract

Add an EasyEDA-side vector detection model that preserves enough information from `StepOcctHlr`:

- view name;
- image mapping from model/vector coordinates to report pixels;
- primitive kind: line, arc, polyline;
- category and visibility when available;
- sampled stroke points;
- primitive bounds.

This contract should be independent of marked rectangles.

### Logo Detector

The logo detector should:

- use a built-in static EasyEDA cloud-logo vector or point template;
- build stroke clusters from compact vector primitives;
- normalize candidate clusters into each tested orientation: `0`, `90`, `180`, `270`;
- optionally test mirrored variants only to compensate projection mirroring, not as a substitute for rotation handling;
- score candidates by chamfer-like point distance, stroke overlap, compactness, and template aspect;
- reject dense mechanical clusters, long connector contacts, regular pin arrays, and whole-body outlines;
- output `kind=logo` with the tight candidate bounds, not the larger marked-region bounds.

### Text Detector

The text detector should:

- detect known `EasyEDA` and `LCEDA` labels separately from arbitrary text;
- when `CleanText=true`, detect manufacturer text without requiring a known string template;
- normalize every candidate cluster through `0`, `90`, `180`, and `270` degree frames before text scoring;
- score string-like geometry by component count, stroke density, baseline/band structure, spacing consistency, and elongated string shape;
- reject pin/contact arrays using orientation-aware checks so vertical text is not misclassified as a pin row;
- output `kind=text` for the tight text bounds.

### Combined Region Builder

The combiner should:

- consume split `logo` and `text` detections;
- merge nearby logo/text detections into one `kind=watermark-combined` region;
- allow logo and text to have different right-angle orientations;
- combine `EasyEDA`, `LCEDA`, and manufacturer labels with the logo when spatially anchored;
- keep combined bounds tight and smaller than the corresponding marked rectangle.

## Implementation Plan

## Task Ledger

- [x] Task 1: Add vector detection fixtures and failing tests
- [x] Task 2: Introduce vector detection data model
- [x] Task 3: Add split vector logo detector
- [x] Task 4: Add split vector text detector
- [x] Task 5: Add combined watermark region builder
- [x] Task 6: Wire cleaner and MarkedVsDetected to vector detector
- [x] Task 7: Verification

### Task 1: Add Vector Detection Fixtures And Failing Tests

Files:

- Modify: `Test/StepCleaner/Program.cs`
- Modify: `Test/StepCleaner/StepCleaner.Tests.csproj` only if new linked files are needed later

Steps:

- Add a vector-only marked parity command, for example `--marked-vector-detection-parity`.
- It should iterate existing `Test/StepCleaner/Data/Marked/*.json`.
- It should render/load vector projections from original STEP files.
- It should call only the new vector detector entry point.
- It should assert:
  - detection count equals marked rectangle count;
  - each detection is inside at least one marked rectangle;
  - each detection area is smaller than the matched marked rectangle area.
- Add a clean-text variant, for example `--marked-vector-detection-parity-clean-text`.
- Do not pass marked rectangles into detector code.

Expected initial result: fail because the vector detector entry point does not exist.

### Task 2: Introduce Vector Detection Data Model

Files:

- Create: `EasyEDA-Loader/StepVectorWatermarkDetectionInput.cs`
- Modify: `EasyEDA-Loader/EasyEDA-Loader.csproj`
- Modify: `StepCleaner/StepCleaner.csproj`
- Modify: `MarkedVsDetected/MarkedVsDetected.csproj`
- Modify: `Test/StepCleaner/StepCleaner.Tests.csproj`

Steps:

- Define view-level vector detection input and primitive records.
- Preserve vector-to-image mapping so reports still use 1600 px marked coordinates.
- Add a small adapter from existing `StepOcctHlr` vector output to the detection input.
- Keep the adapter free of marked/test data.

### Task 3: Add Split Vector Logo Detector

Files:

- Create: `EasyEDA-Loader/StepVectorLogoDetector.cs`
- Modify: project files that link EasyEDA detector sources

Steps:

- Build compact stroke clusters from vector primitives.
- Add a built-in logo template source that does not read marked data.
- For each cluster, score template fit at `0`, `90`, `180`, and `270` degrees.
- Return only `kind=logo` detections.
- Keep bounds tight to actual candidate strokes.

### Task 4: Add Split Vector Text Detector

Files:

- Create: `EasyEDA-Loader/StepVectorTextDetector.cs`
- Modify: project files that link EasyEDA detector sources

Steps:

- Build text-like stroke clusters from vector primitives.
- Detect known text templates independently of logo matching.
- When `CleanText=true`, score arbitrary manufacturer text with orientation-normalized geometry.
- Evaluate each candidate at `0`, `90`, `180`, and `270` degrees.
- Return only `kind=text` detections.

### Task 5: Add Combined Watermark Region Builder

Files:

- Create: `EasyEDA-Loader/StepVectorWatermarkRegionCombiner.cs`
- Create or modify: `EasyEDA-Loader/StepVectorWatermarkProjectionDetector.cs`

Steps:

- Add a public detector facade that runs logo and text detection independently.
- Merge related logo/text detections into `kind=watermark-combined`.
- Allow logo and text orientations to differ.
- Limit output count to the selected high-confidence combined regions needed for cleanup/report parity.

### Task 6: Wire Cleaner And MarkedVsDetected To Vector Detector

Files:

- Modify: `EasyEDA-Loader/StepWatermarkCleaner.cs`
- Modify: `MarkedVsDetected/Program.cs`

Steps:

- Replace raster detector usage in cleanup promotion with vector detector usage.
- Keep raster projection generation only for debug overlays.
- Update `MarkedVsDetected` to use the vector detector and to stop creating runtime detector templates from marked rectangles.
- Keep marked rectangles as truth-only data for report metrics and overlays.

### Task 7: Verification

Run:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --marked-vector-detection-parity
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --marked-vector-detection-parity-clean-text
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --text-logo-detection
dotnet run --project MarkedVsDetected\MarkedVsDetected.csproj -- Test\StepCleaner\Data
```

Expected final result:

- marked vector parity passes for normal mode;
- marked vector parity passes for `CleanText=true`;
- text/logo detection accepts right-angle rotated logo and text labels;
- MarkedVsDetected report shows the same number of detected regions as marked regions;
- detected and combined regions are smaller than marked rectangles;
- no generated data is staged or committed.

Verification status after implementation:

- `--marked-vector-detection-parity`: passed.
- `--marked-vector-detection-parity-clean-text`: passed.
- `MarkedVsDetected`: generated report with `matched=26`, `MarkedRects=25`, `DetectedRects=25`, and detected area smaller than marked area.
- `--text-logo-detection`: still fails in the legacy raster detector path with pre-existing bound drift; this task did not change the raster detector.

## Non-Goals

- Do not improve F3D color rendering.
- Do not tune against marked rectangles inside detector code.
- Do not add generated projection images, reports, or temp files to git.
- Do not commit without explicit user request.
