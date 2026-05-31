# Projection-Guided STEP Watermark Cleanup Plan

The heuristic-only cleaner is not the final rule. It can miss watermark faces and
can touch geometry that only happens to look like a watermark. The next cleanup
algorithm must be driven by explicit marked projection regions.

## Data Rule

- `Test\StepCleaner\Data\Original` is read-only source data.
- `Test\StepCleaner\Data\Projection` is generated output from the projection tool.
- `Test\StepCleaner\Data\Marked` is user-authored annotation data. Marker JSON
  filenames should match the generated projection PNG base names.
- `Test\StepCleaner\Data\Validated` is read-only accepted STEP output.
- Do not modify `Original` or `Validated` from tools or tests.

## Marking Workflow

Generate six side projections for every original STEP model:

```powershell
dotnet run --project StepCleaner\StepCleaner.csproj -- project Test\StepCleaner\Data\Original
```

For every model, the tool writes these six views to `Projection`:

- `x_plus`
- `x_minus`
- `y_plus`
- `y_minus`
- `z_plus`
- `z_minus`

Each PNG has a matching projection JSON file with the model-axis mapping and
pixels-per-model-unit scale. Mark watermark regions with the graphical marker
tool; it overlays red rectangles in the UI and saves rectangle coordinates as
JSON sidecars in `Marked` without modifying PNG files:

```powershell
dotnet run --project StepProjectionMarker\StepProjectionMarker.csproj -- Test\StepCleaner\Data\Projection Test\StepCleaner\Data\Marked
```

Use the file list or Previous/Next buttons to switch images. Draw rectangles
with the mouse, save with Ctrl+S or Save, undo with Ctrl+Z, and redo with Ctrl+Y.

## Cleanup Algorithm Rule

The common cleaner should:

1. Load marked rectangles from `Marked` JSON sidecars and their matching
   projection JSON files.
2. Convert each rectangle to a model-space projection region on the matching
   view axis.
3. Select only STEP faces, thin solids, host loops, and shallow adjacent faces
   whose projected bounds lie inside a marked region for that model.
4. Flatten those selected faces to the nearest local host plane and recolor them
   to the local host style.
5. Reject any candidate outside marked rectangles, even if it has EasyEDA-like
   color, text-like size, or shallow relief geometry.
6. Write cleaned output only to `Clean`; never rewrite `Original`, `Marked`, or
   `Validated`.

This makes visual annotation the boundary of trust. Geometry heuristics may
rank candidates inside a marked rectangle, but they must not authorize edits
outside the marked rectangle.
