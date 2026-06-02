# STEP Watermark Cleaner

`StepCleaner` removes the EasyEDA/LCEDA STEP watermark without a CAD kernel.
For project test data, cleanup is automatic and pattern-gated: stage 1 detects
only known EasyEDA watermark patterns (`LCEDA`, `EasyEDA`, or the cloud/key
logo) from the STEP geometry, and stage 2 edits only the geometry returned by
that detection. User-authored marker JSON is reference/debug data and is not
loaded by normal cleanup or tests.

This does not run a boolean operation. It keeps the surrounding BREP intact and
only rewrites the watermark geometry needed to flatten the surface, which makes
the result safer for Altium's STEP importer and portable to other EasyEDA/JLCPCB
models that use the same watermark style.

Embedded watermark topology is merged into the detected host plane by default.
The cleaner removes only `FACE_BOUND` cut loops from that host face and always
preserves `FACE_OUTER_BOUND`, because STEP files do not guarantee the outer bound
is listed first.
The cleaner uses local projection geometry inside each detected pattern region.
It can remove thin standalone watermark solids, flatten styled relief faces back
onto the detected host plane, and remove detected `FACE_BOUND` text loops plus
their adjacent shallow sidewalls. `FACE_OUTER_BOUND` is preserved.

## Usage

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- <input.step> [output.step] [--debug]
dotnet run --project StepCleaner\StepCleaner.csproj -- <input-directory> [output-directory] [--debug]
dotnet run --project StepCleaner\StepCleaner.csproj -- detect <input.step|input-directory> [--debug]
```

Add `--clean-text` to cleanup commands to additionally remove detected raised
or cut text-string markings. Text cleanup is conservative and string-gated: it
targets clustered text markings and avoids isolated graphics or separated
single-character marks.

If `output.step` is omitted, the tool writes `<input>.clean.step` next to the
input file.

Project test-data rule:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- Test\StepCleaner\Data\Original Test\StepCleaner\Data\Clean
dotnet run --project StepCleaner\StepCleaner.csproj -- detect Test\StepCleaner\Data\Original --debug
```

Original test models are read from `Test\StepCleaner\Data\Original`; cleaned
models are written to `Test\StepCleaner\Data\Clean`. If the input directory is
named `Original`, the output directory defaults to the sibling `Clean`.
The cleaner runs automatic stage 1 detection first, then stage 2 removal from
the detected geometry. Marked JSON is not loaded by normal cleaning or by the
regression test.

Add `--debug` to either cleanup or `detect` to write detected-side PNG
projections with red detected-region overlays into `Clean\Detection`. When
compatible non-empty `Marked` sidecars exist for a model, debug output is
marker-view driven: generated PNG names must match the marked model side names,
detected geometry is matched to those marked rectangles by model-space overlap,
and the whole marked rectangle is drawn for review. Empty marker sidecars are
ignored. Stale sidecars whose stored projection axes no longer match the
renderer are ignored.

## Regression test rule

Run the cleaner regression test with:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

The test treats `Test\StepCleaner\Data\Original` and
`Test\StepCleaner\Data\Validated` as read-only data. It cleans every STEP model
from `Original` into the ignored `Clean` folder using automatic cleanup only;
marked-region JSON is not loaded for cleanup. The test also regenerates
`Clean\Detection` debug PNGs and checks that generated detection images have the
same count and side names as non-empty compatible marker sidecars. This debug
image check is a review aid for detector coverage and does not drive cleanup.

The test then requires every generated clean model to have a matching accepted
model in `Validated`. If a generated clean model is missing from `Validated`,
the test treats it as not fully cleaned and asks the reviewer to view the
generated file before accepting it. Matching models are compared only through
their generated six-side projection PNGs in `CleanProjection` and
`ValidatedProjection`; STEP file bytes are not compared.

Current cleanup notes:

- `LED-SMD_XL-3838UV2SA06G3.step` is considered cleaned; the front and bottom
  watermarks are removed and the gold pads are preserved.
- `USB-A-TH_FUS264-FDSW3K.step` is considered cleaned; the watermark cut is
  flattened/removed in projection review.
- `SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50.step` is considered cleaned; the raised top watermark is flattened/removed in projection review.

## Projection marking workflow

Generate six side-view PNGs and matching JSON pixel-to-model mapping files with:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- project Test\StepCleaner\Data\Original
```

When the input directory is named `Original`, the projection output defaults to
the sibling `Projection` folder. `Projection` is generated and ignored by git.
Use the marker GUI to create sidecar JSON rectangles in sibling `Marked` without
editing PNG files:

```powershell
dotnet run --project StepProjectionMarker\StepProjectionMarker.csproj
```

The marker defaults to `Test\StepCleaner\Data\Projection`; it writes matching
JSON sidecars to `Test\StepCleaner\Data\Marked`. These rectangles are
training/reference data for improving automatic detection. Normal cleanup and
STEP golden comparison do not use them, but the regression test does use
non-empty compatible marker sidecars to verify generated detection-debug PNG
count and side names.

Projection PNG rendering uses the installed F3D command-line renderer when
available. F3D loads STEP through its OpenCascade/OCCT reader, so projection
images are generated from a real STEP tessellation instead of the fallback
in-process STEP sampler. The F3D render path uses STEP cell colors and disables
mesh edge drawing so generated projections do not show tessellation triangles.
SkiaSharp is still used for PNG overlay work such as debug detection
rectangles.

## Integration

The reusable implementation lives in
`EasyEDA-Loader\StepWatermarkCleaner.cs`. The model import path can clean
downloaded STEP bytes before writing the temporary file:

```csharp
byte[] step = StepWatermarkCleaner.Clean(modelTask.Result);
File.WriteAllBytes(temp, step);
```

Use the byte-based API for integration. It parses STEP syntax through a
byte-preserving Latin-1 view so non-ASCII metadata from CAD exporters is not
corrupted while ASCII STEP entities are edited.

The EasyEDA loader dialog enables this by default with the `Remove Watermark`
checkbox. The optional `Clean text` checkbox additionally removes detected
raised or cut text-string markings from the cleaned STEP and refreshes the clean
preview when toggled. When enabled, the downloaded original STEP is kept in the
local model cache and the cleaned STEP bytes are used for the footprint 3D body.
The cleaned model is also checked against the detected cleanup region before it
is added to the footprint. If that verification fails, cleanup returns an error
and writes a Markdown report with side-by-side projection PNGs under the local
`EasyEDA-Loader\StepCleanerReports` folder.
