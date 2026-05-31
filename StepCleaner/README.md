# STEP Watermark Cleaner

`StepCleaner` removes the EasyEDA/LCEDA STEP watermark without a CAD kernel or
manual picking. EasyEDA commonly exports the logo/text as 0.001 mm neutral
white relief geometry on dark plastic bodies. The cleaner parses the STEP graph,
removes thin standalone watermark solids from the BREP shape representation,
then collapses embedded watermark faces back onto the detected dark host plane.

This does not run a boolean operation. It keeps the surrounding BREP intact and
only rewrites the watermark geometry needed to flatten the surface, which makes
the result safer for Altium's STEP importer and portable to other EasyEDA/JLCPCB
models that use the same watermark style.

Embedded watermark topology is merged into the detected host plane by default.
The cleaner removes only `FACE_BOUND` cut loops from that host face and always
preserves `FACE_OUTER_BOUND`, because STEP files do not guarantee the outer bound
is listed first.
Medium-neutral bodies are supported as host surfaces too, so grey metal shells
with same-colour LCEDA cuts can be cleaned without requiring white watermark
styling.
After a host face is confirmed, the cleaner follows the removed inner-loop
edges to adjacent shallow faces in the same solid. This removes residual text
sidewalls and caps that remain visible even after the main surface is flat.

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
