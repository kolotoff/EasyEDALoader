# Fast Local AI Layout Duplication Plan

## Goal

Add a native PCB editor command to EasyEDALoader:

```text
Tools -> EasyEDA -> Duplicate layout
```

The command duplicates a selected source layout block to one or more equivalent
target blocks on the same PCB. It must copy component placement, rotation, side,
and selected routing when routing primitives are selected. If no routing
primitives are selected, it must do placement/orientation/layer copy only.

The local AI model may choose component-to-component mappings. All PCB edits
must remain deterministic EasyEDALoader C# code and must be undoable through
Altium undo/redo. The command must not save the PCB.

Hard implementation constraint:

- Do not use PAS/DelphiScript/AltiumScript for this feature at runtime.
- Do not shell out to MCP, invoke `RunProcess` script commands, or copy the MCP
  Pascal implementation into EasyEDALoader.
- Implement selection capture, component transforms, routing replication, net
  translation, undo transactions, and board redraw directly in C# through the
  Altium SDK interfaces already used by EasyEDALoader.
- The reason is speed: native C# code should avoid script bridge overhead and
  keep long operations inside one fast in-process command.

## Current References

### EasyEDALoader integration points

Existing command plumbing:

- `EasyEDA-Loader/EasyEDA-Loader.ins`
  - declares Altium extension commands.
- `EasyEDA-Loader/EasyEDA-Loader.rcs`
  - binds commands into editor menus.
- `EasyEDA-Loader/EasyEDALoader.cs`
  - registers handlers through `RegisterCommand(...)`.
- `EasyEDA-Loader/EEPCB.cs`
  - owns heavier PCB/PcbLib helper logic and active-board access.

Existing UI/progress patterns:

- `DialogWindow.xaml` / `DialogWindow.cs` already use WPF progress panels with
  job descriptions and determinate/indeterminate progress bars.
- Reuse the same style, but create a focused layout duplicator dialog instead
  of extending the EasyEDA component search/import dialog.

### altium-mcp layout duplicator behavior reference

Reference repo:

```text
D:\Develop\Altium\Ai\altium-mcp
```

Useful behavior references:

- `server/main.py`
  - `layout_duplicator`
  - `layout_duplicator_apply`
  - `layout_duplicator_apply_groups`
- `server/AltiumScript/pcb_layout_duplicator.pas`
  - selected routing duplication
  - source/destination component transforms
  - primitive net translation

Important behavior to preserve:

- Multi-target routing copy must be one grouped operation, not repeated
  single-target operations.
- The grouped MCP wrapper repeats the source designator list and flattens all
  destination groups so the board-side apply code replicates selected routing
  once per target group.
- Supported selected routing primitives include tracks, arcs, vias, polygons,
  regions, and fills.
- Source/destination mapping should be validated before any board edits.

EasyEDALoader must not depend on MCP or PAS/DelphiScript at runtime. The MCP
repo is only a behavior reference for the native C# implementation.

## User Workflow

1. User selects source components in the active PCB editor.
2. User may optionally also select routing primitives belonging to that source
   layout block.
3. User runs:

```text
Tools -> EasyEDA -> Duplicate layout
```

4. The handler checks the active document is a PCB and at least one PCB
   component is selected.
5. If no PCB component is selected, do not open the dialog. Show an error such
   as:

```text
Select source PCB components before running Duplicate layout.
```

6. Open a layout duplicator dialog.
7. Left side of the dialog lists selected source components:
   designator, part number, comment/description fallback, footprint, layer, X,
   Y, rotation.
8. User selects one source anchor component from that list.
9. After anchor selection, right side target table is populated with all board
   components that match the anchor by part number and footprint, excluding the
   selected source anchor. If part number is missing, use comment/description as
   fallback matching data.
10. Every target row has a checkbox, selected by default.
11. Target table columns:
    selected, designator, part number, comment/description, footprint, layer,
    X, Y, rotation.
12. Dialog has an AI model combobox and an optional `Use schematic matching`
    checkbox. The checkbox is on by default; users can turn it off for a
    PCB-only flow.
13. User clicks Duplicate.
14. EasyEDALoader gathers target candidate data, asks the selected local model
    for mapping, validates the response, then applies deterministic edits.
15. Board is redrawn and left dirty/modified, but not saved.

## Dialog Design

Create:

- `EasyEDA-Loader/LayoutDuplicatorDialog.xaml`
- `EasyEDA-Loader/LayoutDuplicatorDialog.xaml.cs`
- `EasyEDA-Loader/LayoutDuplicatorViewModels.cs`

Dialog layout:

- Top row:
  - model combobox
  - optional schematic matching checkbox
  - model status text
  - refresh models button
- Main body:
  - left table: selected source components
  - right table: target anchor candidates
- Bottom row:
  - progress bar with current job description
  - Duplicate button
  - Cancel button

Source table behavior:

- Single-selection table.
- The selected source component is the source anchor.
- Columns: designator, part number, comment/description, footprint, layer, X, Y,
  rotation.

Target table behavior:

- Rows are equivalent target anchors, not individual mapped support
  components.
- Candidate rule for first implementation:
  - same part number as source anchor where available;
  - same comment/description only as fallback when part number is missing;
  - same footprint;
  - not the selected source anchor;
  - exclude all selected source components for safety.
- Optional schematic matching:
  - when enabled, capture schematic hints through native C# `SCHServer` /
    workspace APIs, not PAS/DelphiScript/AltiumScript;
  - schematic data is not required; if no schematic/project context is
    available, keep the PCB-only candidate list and ordering;
  - use schematic sheet/channel/net-role hints to rank target anchors and
    destination candidates before asking the model;
  - include only structured schematic hints in the AI prompt; deterministic
    validation still rejects incompatible part/footprint mappings.
- Every row has a checkbox defaulting to selected.
- User may deselect targets before applying.

Progress behavior:

- Every potentially long operation must show a job description:
  - scanning selected PCB components;
  - scanning board target candidates;
  - reading schematic matching hints;
  - checking Ollama;
  - loading or warming model;
  - building mapping prompt;
  - waiting for AI mapping;
  - validating mapping;
  - applying placement;
  - copying routing;
  - redrawing board.
- Use indeterminate progress for operations with unknown duration, especially
  Ollama load/pull and AI inference.
- Use determinate progress when iterating target groups.

## Local AI / Ollama Policy

Use Ollama first.

Default model:

```text
qwen3.5:9b
```

Fallback model:

```text
qwen2.5-coder:7b-instruct
```

Dialog model list:

- Query `GET http://localhost:11434/api/tags`.
- Include installed models from Ollama.
- Query Ollama's loaded/running model list, for example `GET
  http://localhost:11434/api/ps`, and mark currently loaded models in the
  combobox.
- If default/fallback models are not installed, still show them as available
  actions with status `not installed`.
- Persist the last used model in the EasyEDALoader local settings/session data.
- Initial combobox selection precedence:
  - if the last used model is currently loaded in Ollama, select it;
  - otherwise select the first currently loaded Ollama model that is suitable
    for layout mapping;
  - otherwise select the last used model if it is installed or known;
  - otherwise select `qwen3.5:9b`.

Model load/warm behavior:

- Prefer Ollama to be warm before the user presses Duplicate.
- On dialog open, start a non-blocking warm-up for the selected model only when
  it is already installed.
- Do not pull a missing model automatically.
- If the selected model is missing, show a confirmation prompt before any
  `ollama pull` operation. If the user declines, leave the PCB unchanged and let
  the user choose another installed model.
- Warm with a small deterministic chat request and `keep_alive`, for example:

```json
{
  "model": "qwen3.5:9b",
  "stream": false,
  "think": false,
  "keep_alive": "30m",
  "messages": [
    { "role": "user", "content": "Return only JSON: {\"ok\":true}" }
  ],
  "format": "json",
  "options": {
    "temperature": 0,
    "num_predict": 16,
    "num_ctx": 8192
  }
}
```

Mapping request defaults:

```json
{
  "stream": false,
  "format": "json",
  "think": false,
  "keep_alive": "30m",
  "options": {
    "temperature": 0,
    "num_predict": 512,
    "num_ctx": 8192
  }
}
```

Implementation targets:

- `EasyEDA-Loader/OllamaLayoutMappingClient.cs`
  - list models;
  - list currently loaded/running models;
  - warm/load selected model;
  - pull model only after explicit user confirmation;
  - request JSON mapping;
  - parse errors and timeout reporting.
- Use `HttpClient` and `Newtonsoft.Json`, matching existing repo patterns.

## Prompt Contract

The AI output is mapping only. It must not return coordinates, rotations,
routing geometry, net assignments, or edit commands.

For each selected target anchor, EasyEDALoader sends:

- source anchor;
- all selected source components;
- selected source routing summary;
- the target anchor;
- nearby/equivalent destination component candidates;
- component metadata:
  - designator;
  - part number;
  - comment/description;
  - footprint;
  - layer;
  - X/Y;
  - rotation;
  - pad names and net names where cheap to gather.

Instruction pattern:

```text
Return only JSON.
The map object MUST have exactly these source designator keys:
DD15, HL11, C139, R51, R87.
Each value MUST be one bare destination designator from the destination list.
Do not invent designators.
Do not return coordinates or edit commands.
If any required source cannot be mapped, put it in ambiguous and omit that key.
```

Expected response:

```json
{
  "groups": [
    {
      "target_anchor": "DD16",
      "map": {
        "DD15": "DD16",
        "HL11": "HL12",
        "C139": "C140",
        "R51": "R85",
        "R87": "R89"
      },
      "confidence": 1.0,
      "ambiguous": []
    }
  ]
}
```

## Validation Rules

Before modifying the PCB, validate:

- JSON parses successfully.
- `groups` exists.
- Every checked target anchor has at most one returned group.
- Every returned `target_anchor` exists in the checked target table.
- Every required selected source component appears exactly once in each
  group's `map`.
- Every mapped destination designator exists on the board.
- Destination designators are unique within a group.
- Source anchor maps to the selected target anchor.
- Source and destination footprint match exactly.
- Source and destination part number match where available.
- Source and destination comment/description match only when part number is
  missing.
- Source and destination layer compatibility is valid or explicitly handled by
  the transform.
- Pad-name compatibility is valid where pad data is available.
- Any non-empty `ambiguous` list causes that target group to be skipped.

If all checked target groups fail validation:

- show an error dialog;
- do not modify the PCB.

If some target groups validate and some fail:

- apply only valid groups;
- show a completion summary listing skipped targets and reasons.

## Deterministic Apply Rules

All rules in this section must be implemented in C#. Do not implement apply
logic as a PAS script, generated script, MCP command, or script bridge call.

Create:

- `EasyEDA-Loader/LayoutDuplicationModels.cs`
- `EasyEDA-Loader/LayoutDuplicationCapture.cs`
- `EasyEDA-Loader/LayoutDuplicationMapper.cs`
- `EasyEDA-Loader/LayoutDuplicationApply.cs`

Capture rules:

- Capture selected source components from the active PCB.
- Capture selected routing primitives separately.
- Determine placement from component origin, rotation, and layer.
- Determine source-relative transforms from selected source anchor.
- Capture target anchor candidates from all board components.
- Capture destination candidate pools for each target anchor using:
  - exact footprint match;
  - exact part number match where available;
  - exact comment/description fallback when part number is missing;
  - spatial proximity to the target anchor;
  - pad/net role data where cheap.

Placement apply:

- Use source anchor to target anchor transform.
- Use C# Altium SDK object reads/writes for component origin, rotation, layer,
  and side changes.
- For each mapped source/destination component:
  - compute destination X/Y from source-relative offset;
  - compute destination rotation from source-relative rotation;
  - set destination layer/side to mirror source relation to the anchor;
  - preserve target component identity/designator.

Routing apply:

- If selected source routing primitive count is zero, skip routing copy.
- If routing is selected, replicate all selected tracks, arcs, vias, polygons,
  regions, and fills once for each valid target group.
- Create copied primitives in C# using Altium SDK object factories or SDK
  replicate APIs available from EasyEDALoader; do not delegate primitive copy to
  PAS/DelphiScript.
- Apply all checked/valid target groups in one grouped transaction so routing
  copies are duplicated per target group.
- Translate primitive nets using mapped component pad nets where possible.
- If a primitive net cannot be translated, leave it unassigned or keep the
  safest board-compatible value and report the issue in the summary.

Undo/no-save rules:

- Wrap the apply phase in Altium PCB edit transaction calls:
  - `PCBServer.PreProcess()`;
  - robot begin/end modify messages around modified/created objects;
  - `PCBServer.PostProcess()`.
- Do not call save APIs.
- Mark/redraw the board after successful edits.
- Preserve Altium undo/redo behavior for all created and modified objects.

## Command and Menu Wiring

Add command:

```text
EasyEDADuplicateLayout
```

Files:

- `EasyEDA-Loader/EasyEDA-Loader.ins`
  - declare `EasyEDADuplicateLayout`.
- `EasyEDA-Loader/EasyEDA-Loader.rcs`
  - add menu item caption `Duplicate layout` under the existing PCB editor
    `Tools -> EasyEDA` menu.
- `EasyEDA-Loader/EasyEDALoader.cs`
  - register both:
    - `EasyEDADuplicateLayout`
    - `EasyEDA-Loader:EasyEDADuplicateLayout`
  - handler:
    - validate active PCB;
    - validate at least one selected component;
    - capture selection summary;
    - open `LayoutDuplicatorDialog`.

Place the item under:

```text
Tools -> EasyEDA -> Duplicate layout
```

## Testing Plan

Static regression lane:

```powershell
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --pcblib-actions
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
```

Add focused checks in `Test/StepCleaner/Program.cs`:

- `.ins` declares `EasyEDADuplicateLayout`.
- `.rcs` contains the PCB menu link and `Duplicate layout` caption.
- `EasyEDALoader.cs` registers both command IDs.
- command handler checks selected components before opening the dialog.
- no implementation path calls save APIs.
- no implementation path invokes PAS/DelphiScript/AltiumScript, MCP, or script
  bridge commands for layout duplication.
- layout duplication apply files use native C# Altium SDK calls for capture,
  transforms, primitive creation, net assignment, transaction, and redraw.
- Ollama defaults include:
  - `qwen3.5:9b`;
  - `qwen2.5-coder:7b-instruct`;
  - `temperature: 0`;
  - `think: false`;
  - `keep_alive`.
- model combobox selection prefers the loaded last-used model, then another
  loaded suitable model, then last used, then `qwen3.5:9b`.
- missing models are pulled only after explicit user confirmation.
- validation rejects:
  - missing source keys;
  - invented destination designators;
  - duplicate destination designators;
  - footprint mismatch;
  - non-empty ambiguous list.
- routing apply code supports:
  - track;
  - arc;
  - via;
  - polygon;
  - region;
  - fill.
- grouped apply is used when more than one target is checked.

Manual Altium verification:

1. Open a PCB.
2. Select only source components.
3. Run `Tools -> EasyEDA -> Duplicate layout`.
4. Verify dialog opens and source table contains selected components.
5. Select an anchor.
6. Verify target table lists matching anchors with X/Y and checked boxes.
7. Duplicate with no selected routing.
8. Verify only component placement/orientation/layer changes.
9. Undo and redo in Altium.
10. Select source components plus routing primitives.
11. Duplicate to multiple checked targets.
12. Verify routing primitives are copied to each target, not moved between
    targets.
13. Verify board is dirty but not saved automatically.

## Implementation Order

1. Add model classes and capture helpers with unit/static tests.
2. Add validation-only mapping parser tests.
3. Add Ollama client and model warm/list behavior.
4. Add dialog UI with source table, target table, model combobox, progress, and
   cancellation.
5. Add deterministic placement apply for one target group.
6. Add grouped multi-target placement apply.
7. Add selected routing capture and grouped routing copy.
8. Add net translation.
9. Wire command/menu.
10. Run static regression and build.
11. Hand live Altium verification to the user unless install/test in Altium is
    explicitly requested.

## Resolved Decisions

1. Add `Duplicate layout` under the existing PCB editor `Tools -> EasyEDA`
   menu. Do not rename the menu to `EasyLoader`.
2. Exclude all selected source components from target anchor candidates for
   safety.
3. Do not pull missing Ollama models automatically. Ask the user before any
   model download.
4. In the model combobox, prefer the currently loaded Ollama model when
   available. If no suitable model is loaded, use the last used model; otherwise
   fall back to `qwen3.5:9b`.
