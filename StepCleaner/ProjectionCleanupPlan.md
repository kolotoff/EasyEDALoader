# Projection-Guided STEP Watermark Cleanup Plan

The heuristic-only cleaner is not the final rule. It can miss watermark faces and
can touch geometry that only happens to look like a watermark. Marked projection
regions are training/reference data for designing the common detector, but the
regression test and normal cleanup must run without loading marked data.

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

1. Stage 1: find watermark geometry from the STEP model without marked JSON.
   The detector may use the marked set only as offline training/reference data.
2. Stage 1 is pattern-gated. It must project thin standalone solids, shallow
   candidate faces, and host-loop bounds onto their local side/host plane,
   cluster them, and keep only clusters that match one of the known EasyEDA
   watermark patterns: `LCEDA`, `EasyEDA`, or the cloud/key logo. Digit-like or
   unrelated symbol clusters are not valid watermark patterns.
3. Projection orientation must be stable per side; text on `x/y/z` side views
   should not be mirrored by the renderer. If a view-axis mapping changes, stale
   marker JSON/projection metadata must be treated as incompatible reference
   data rather than as a runtime constraint.
4. Stage 1 returns detected thin solids, embedded/relief faces, coplanar faces,
   and host-face inner loops only inside accepted pattern regions. It does not
   edit the model.
5. Stage 2 consumes only the stage 1 detection result, flattens selected faces to
   the nearest local host plane, and removes selected host loops/topology.
6. Normal cleanup and regression tests must not load `Marked`; they use
   automatic stage 1 detection only.
6. Write cleaned output only to `Clean`; never rewrite `Original`, `Marked`, or
   `Validated`.
