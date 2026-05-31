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

Embedded watermark topology is preserved by default. Removing those faces from
the shell can also remove or invalidate the real host face in some STEP
importers, so the default path flattens and recolors the relief while leaving the
body face topology in place.

## Usage

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- <input.step> [output.step]
```

If `output.step` is omitted, the tool writes `<input>.clean.step` next to the
input file.

## Integration

The reusable implementation lives in
`EasyEDA-Loader\StepWatermarkCleaner.cs`. Later, the model import path can clean
downloaded STEP bytes before writing the temporary file:

```csharp
byte[] step = StepWatermarkCleaner.Clean(modelTask.Result);
File.WriteAllBytes(temp, step);
```
