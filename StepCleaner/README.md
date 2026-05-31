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
compatible `Marked` sidecars exist for the same view, debug overlays on that
view are filtered to regions that fit inside those marked rectangles. Stale
sidecars whose stored projection axes no longer match the renderer are ignored,
and detected views without a compatible marker are still emitted for review.

## Regression test rule

Run the cleaner regression test with:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

The test treats `Test\StepCleaner\Data\Original` and
`Test\StepCleaner\Data\Validated` as read-only data. It cleans every STEP model
from `Original` into the ignored `Clean` folder using automatic cleanup only;
marked-region JSON is intentionally not loaded by the regression test. The test
then requires every generated clean model to have a matching golden file in
`Validated`. If a generated clean model is missing from `Validated`, the test
treats it as not fully cleaned and asks the reviewer to view the generated file
before accepting it. Matching files are byte-compared against their `Validated`
golden files.

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
training/reference data for improving automatic detection; they are not runtime
input for the regression test.

Projection PNG rendering uses the installed F3D command-line renderer when
available. F3D loads STEP through its OpenCascade/OCCT reader, so projection
images are generated from a real STEP tessellation instead of the fallback
in-process STEP sampler. The F3D render path uses STEP cell colors and disables
mesh edge drawing so generated projections do not show tessellation triangles.
SkiaSharp is still used for PNG overlay work such as debug detection
rectangles.

## Integration

The reusable implementation lives in
`EasyEDA-Loader\StepWatermarkCleaner.cs`. Later, the model import path can clean
downloaded STEP bytes before writing the temporary file:

```csharp
byte[] step = StepWatermarkCleaner.Clean(modelTask.Result);
File.WriteAllBytes(temp, step);
```

Use the byte-based API for integration. It parses STEP syntax through a
byte-preserving Latin-1 view so non-ASCII metadata from CAD exporters is not
corrupted while ASCII STEP entities are edited.
