# STEP Watermark Cleaner

`StepCleaner` removes the EasyEDA/LCEDA STEP watermark without a CAD kernel.
For project test data, cleanup is projection-guided: generated side-view
metadata and user-authored marker JSON rectangles define the only regions where
geometry may be edited. Geometry outside marked rectangles is not selected by
the cleaner.

This does not run a boolean operation. It keeps the surrounding BREP intact and
only rewrites the watermark geometry needed to flatten the surface, which makes
the result safer for Altium's STEP importer and portable to other EasyEDA/JLCPCB
models that use the same watermark style.

Embedded watermark topology is merged into the detected host plane by default.
The cleaner removes only `FACE_BOUND` cut loops from that host face and always
preserves `FACE_OUTER_BOUND`, because STEP files do not guarantee the outer bound
is listed first.
The cleaner still uses local geometry rules inside each marked rectangle. It can
remove thin standalone watermark solids, flatten styled relief faces back onto
the detected host plane, and remove marked `FACE_BOUND` text loops plus their
adjacent shallow sidewalls. `FACE_OUTER_BOUND` is preserved.

## Usage

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- <input.step> [output.step]
dotnet run --project StepCleaner\StepCleaner.csproj -- <input-directory> [output-directory]
```

If `output.step` is omitted, the tool writes `<input>.clean.step` next to the
input file.

Project test-data rule:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- Test\StepCleaner\Data\Original Test\StepCleaner\Data\Clean
```

Original test models are read from `Test\StepCleaner\Data\Original`; cleaned
models are written to `Test\StepCleaner\Data\Clean`. If the input directory is
named `Original`, the output directory defaults to the sibling `Clean`.
When sibling `Marked` and `Projection` folders exist, the cleaner loads
`Marked\<model>__*.json` and the matching projection metadata automatically and
runs in marked-region-only mode.

## Regression test rule

Run the cleaner regression test with:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj
```

The test treats `Test\StepCleaner\Data\Original` and
`Test\StepCleaner\Data\Validated` as read-only data. It cleans every STEP model
from `Original` into the ignored `Clean` folder, then requires every generated
clean model to have a matching golden file in `Validated`. If a generated clean
model is missing from `Validated`, the test treats it as not fully cleaned and
asks the reviewer to view the generated file before accepting it. Matching files
are byte-compared against their `Validated` golden files.

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
JSON sidecars to `Test\StepCleaner\Data\Marked`. See
`StepCleaner\ProjectionCleanupPlan.md` for the rule that the cleaner may only
edit geometry inside marked rectangles.

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
