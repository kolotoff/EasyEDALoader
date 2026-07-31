# Import JLCCAM Plan

## Planning status

- [x] Inspect the current EasyEDALoader PCB command/menu wiring.
- [x] Inspect existing WPF preview, table, file/folder picker, clipboard, and modal-dialog patterns.
- [x] Inspect the real JLC `ok`/`YG` production package and its Gerber headers.
- [x] Research JLC production-file layer meanings and standard rail-feature dimensions.
- [x] Research the Gerber commands needed for a production-safe parser.
- [x] Inspect the installed Agile 26 Altium SDK interfaces for PCB primitives, masks, layers, transactions, and redraw.
- [x] Define the parsing, coordinate-mapping, preview, import, validation, and test architecture.
- [x] Write this implementation plan.
- [x] Review the plan against the requested action name, UI fields, import semantics, and no-implementation constraint.
- [x] Revise the plan for the `JlcCamImport` code folder, `JlcCam*` naming, and separate archive/folder submenu actions.
- [x] Implemented the scoped JLCCAM command, parser/analyzer, review UI, and PCB importer.

During implementation, check an item only after its code and focused tests are
complete. Do not mark a whole phase complete in advance.

## Goal and user workflow

Add this submenu only to the PCB editor's existing menu:

```text
Tools -> EasyEDA -> Import JLCCAM -> Import archive
                                    -> Import folder
```

The command must be available only when a normal PCB document is active. It
must not be added to the schematic or PCB library menus.

Workflow:

1. Run one of:
   - `Tools -> EasyEDA -> Import JLCCAM -> Import archive`; or
   - `Tools -> EasyEDA -> Import JLCCAM -> Import folder`.
2. The selected action immediately opens the matching stock Windows picker:
   - `Import archive` opens a file picker filtered to JLC production `.rar`
     archives;
   - `Import folder` opens a folder picker for an already extracted production
     folder containing `ok` and `YG`.
3. EasyEDALoader analyzes the package without changing the PCB.
4. Show a modal `Import JLCCAM` review dialog containing:
   - a board/panel preview in millimetres;
   - the original customer-board outline and JLC edge rails;
   - tooling holes and top/bottom fiducials;
   - a tooling-hole table;
   - a fiducial table with one row per physical copper side;
   - analysis/mapping warnings;
   - `Copy report to clipboard`;
   - three checked-by-default options: `Import edge rails`, `Import fiducials`,
     and `Import edge holes`;
   - `Import` and `Cancel` buttons.
5. `Cancel`, closing the window, or a parse/validation error leaves the PCB
   unchanged.
6. `Import` creates only the checked categories in one undoable PCB operation,
   redraws the board, and leaves it modified but unsaved.

Windows does not provide one reliable stock picker that selects both files and
folders. Do not create a special source chooser. Put two actions under the
`Import JLCCAM` submenu and have each action open the appropriate stock picker
directly.

## Scope and non-goals

- Do not implement this plan until explicitly requested.
- Do not create or switch branches for this work.
- Do not commit or push unless explicitly requested.
- Do not save the PCB automatically.
- Do not change Altium's actual board shape. Edge-rail geometry is imported as
  0.1 mm keepout primitives only.
- Do not use JLCCAM/JLCDFM, Pascal scripts, MCP, or an external process at
  runtime.
- Do not import ordinary board drills or ordinary board fiducials. Only features
  outside the matched customer-board outline and on JLC-added rail areas are in
  scope.
- Do not silently guess a coordinate transform, feature class, side, or nominal
  dimension when the evidence is ambiguous.

## Research findings

### JLC production package

JLC's official production-file guide says the downloaded archive contains the
original customer data in `YG` and the engineer-produced data in `ok`. It also
defines the relevant extensionless layers: `tl`/`bl` are top/bottom copper,
`ts`/`bs` are top/bottom solder mask, `drl` is drill, and `ko` is outline:

- <https://jlcpcb.com/help/article/how-to-confirm-the-production-file>

The sample package confirms:

- `ok/ko` contains JLC comments `DEPTH 2` for the customer-board outline and
  `DEPTH 1` for the final panel/rail geometry;
- the production files use inch units with six decimal places;
- the original `.GKO` uses millimetres and contains Altium `Board_Origin`
  metadata;
- the sample transform is a pure translation:

```text
original X = panel X + 68.000 mm
original Y = panel Y + 86.300 mm
```

- the raw CAM apertures are 2.05 mm for the rail drills and 1.05 mm for the
  fiducial copper, while the intended nominal JLC rail features are 2.00 mm and
  1.00 mm.

JLC's rail-design guidance recommends 2 mm tooling holes and 1 mm fiducials:

- <https://jlcpcb.com/help/article/specifications-for-adding-process-edges-and-positioning-holes>

JLC also documents 0.05 mm compensation for non-plated drilling. This supports
the sample's 2.05 mm CAM drill versus 2.00 mm nominal hole, but compensation is
still recorded as an inference in the report rather than hidden:

- <https://www.jlc.com/portal/q1i11642.html>

The prior standalone extractor is useful as a sample oracle, but must not be
copied unchanged. It is line-oriented, assumes no rotation/mirroring, requires
fiducials on both copper sides, combines both sides into one row, and reports
CAM apertures rather than a separately tracked nominal dimension.

### Gerber parsing

Use the current Ucamco Gerber specification as the parsing contract. The
relevant commands include `FS`, `MO`, `AD`, `D01`, `D02`, `D03`, `G01`, `G02`,
`G03`, `G74`, `G75`, polarity, and `M02`:

- <https://www.ucamco.com/en/gerber/downloads>

Do not parse by physical line. Tokenize Gerber commands by `*` and extended
command delimiters so valid files with multiple commands per line work.

### Altium integration

The existing repository already supplies the command and UI patterns:

- `EasyEDA-Loader/EasyEDA-Loader.ins` declares commands.
- `EasyEDA-Loader/EasyEDA-Loader.rcs` inserts commands into the PCB `EasyEDA`
  menu.
- `EasyEDA-Loader/EasyEDALoader.cs` registers namespaced and unnamespaced
  handlers and owns modal-dialog lifetime/owner setup.
- `LayoutDuplicatorDialog.xaml` is the closest focused WPF modal pattern.
- `CanvasZoomPanHelper.cs` supplies fit, zoom, pan, and right-click reset.
- `ShapeExportErrorForm.cs` demonstrates clipboard copy and error handling.
- `EEPCB.GetCurrentPcbBoard(...)` resolves the active PCB robustly.
- `LayoutDuplicationApply.cs` demonstrates board transactions, object
  creation/addition, and redraw.

The checked-in Agile 26 SDK assemblies expose all required native interfaces.
Reflection of `Altium.SDK.Interfaces.dll` and `Altium.SDK.dll` confirmed:

- `IPCB_ServerInterface.GetCurrentPCBBoard`;
- `PCBObjectFactory`, `IPCB_Board.AddPCBObject`, and board redraw/update;
- `IPCB_Primitive.BeginModify` / `EndModify`;
- `TLayerConstant.eKeepOutLayer`, `eTopLayer`, `eBottomLayer`, and
  `eMultiLayer`;
- `IPCB_Primitive.SetState_IsKeepout`;
- pad name, hole type/size, plating, top/mid/bottom sizes, rotation, and layer;
- top/bottom paste enable flags;
- manual/no-mask/rule mask modes;
- separate top/bottom solder-mask expansions and expansion-from-hole-edge;
- `PCBServer.PreProcess()` / `PostProcess()`.

Altium's official documentation confirms that mask opening diameter is the pad
or hole diameter plus twice the radial expansion, and that per-pad overrides
and separate top/bottom expansions are supported:

- <https://www.altium.com/documentation/altium-designer/pcb/design-rule-types/mask>
- <https://www.altium.com/documentation/altium-dxp-developer/system-api?version=20.0>

No additional Altium decompilation is currently necessary. If live testing
shows behavior that differs from the checked-in SDK surface, inspect the exact
installed Altium assembly implementation before adding reflection fallbacks.

## Proposed files and responsibilities

Store all new production code in `EasyEDA-Loader/JlcCamImport`. Every new C#
file and code class for this feature must use the `JlcCam*` casing. Create
focused files instead of growing `EasyEDALoader.cs` or `EEPCB.cs`:

- `EasyEDA-Loader/JlcCamImport/JlcCamImportModels.cs`
  - immutable millimetre-domain models for points, line/arc segments, contours,
    layer flashes, holes, fiducials, transforms, diagnostics, and the analysis
    session;
  - keep CAM size, inferred nominal size, mask opening, layer, confidence, and
    source evidence as separate fields.
- `EasyEDA-Loader/JlcCamImport/JlcCamSource.cs`
  - shared archive/folder source abstraction used after the menu action has
    already selected a source;
  - safe RAR extraction;
  - case-insensitive `ok`/`YG` discovery;
  - temporary-directory ownership and cleanup;
  - original outline discovery inside folders or nested ZIP files.
- `EasyEDA-Loader/JlcCamImport/JlcCamGerberParser.cs`
  - tokenizer and the deliberately limited but explicit Gerber state machine;
  - output geometry only, with no WPF or Altium dependencies.
- `EasyEDA-Loader/JlcCamImport/JlcCamAnalyzer.cs`
  - package validation, depth classification, coordinate transform, rail-area
    calculation, hole/fiducial detection, mask matching, side classification,
    nominal-size resolution, sorting, and diagnostics.
- `EasyEDA-Loader/JlcCamImport/JlcCamReportBuilder.cs`
  - invariant-culture, millimetre-only session report used both by the UI and
    clipboard.
- `EasyEDA-Loader/JlcCamImport/JlcCamPreviewRenderer.cs`
  - WPF-only preview rendering from analyzed models.
- `EasyEDA-Loader/JlcCamImport/JlcCamImportDialog.xaml`
- `EasyEDA-Loader/JlcCamImport/JlcCamImportDialog.xaml.cs`
  - review tables, checkboxes, report copy, Import/Cancel, and busy/error state.
- `EasyEDA-Loader/JlcCamImport/JlcCamPcbImporter.cs`
  - preflight, unique names, native PCB primitive construction, grouped apply,
    rollback-on-error, dirty flag, and redraw.
- `EasyEDA-Loader/JlcCamImport/JlcCamPcbAdapter.cs`
  - narrow wrapper over Altium SDK calls so importer decisions can be unit
    tested without Altium running.

Add a pinned `SharpCompress` package reference for in-process RAR reading. At
planning time, `0.50.1` supports .NET 8 and RAR and is MIT licensed:

- <https://www.nuget.org/packages/SharpCompress>

Re-check the selected version and its advisories at implementation time. Do not
depend on a separately installed `7z.exe`, Python, JLCCAM, or shell extraction.

## Source handling and archive safety

- [x] Add `Import archive` using the stock `OpenFileDialog`, filtered to
  `*.rar`, and `Import folder` using the stock `FolderBrowserDialog`. Do not add
  a custom source chooser.
- [ ] Remember the last successful archive directory and extracted-folder
  directory under separate settings keys in the existing EasyEDALoader local
  settings area.
- [x] Accept `.rar` case-insensitively and an extracted directory.
- [x] Extract archives to
  `%TEMP%/EasyEDA-Loader/JLCCAM/<random-guid>`; never extract beside the source
  archive or into the repository.
- [ ] Reject encrypted, corrupt, multipart-with-missing-parts, or unsupported
  archives with a clear error before opening the review dialog.
- [ ] Prevent Zip Slip/path traversal: reject absolute paths, drive-qualified
  paths, `..` traversal, links/reparse targets, and any resolved path outside
  the owned temporary root.
- [ ] Apply defensive entry-count, single-entry-size, and total-uncompressed-
  size limits. Make the limits constants and include them in the error text.
- [x] Locate exactly one package root with sibling `ok` and `YG` directories,
  case-insensitively. Reject zero or multiple candidates rather than choosing
  arbitrarily.
- [ ] Support an original Gerber folder directly under `YG` or inside its ZIP.
  Prefer explicit outline names (`.GKO`, then `.GM1`/`.GML`) and reject multiple
  equally plausible outlines.
- [ ] Dispose all streams before preview opens and delete the owned temporary
  root when the analysis/dialog session ends. Log cleanup failures without
  deleting any broader parent directory.

## Gerber parser contract

- [x] Parse `FS` leading/trailing-zero suppression and X/Y integer/decimal
  widths independently.
- [x] Parse `MO` millimetre/inch units and convert into `double` millimetres at
  the parser boundary. No downstream model may store Altium coordinates or
  inches.
- [ ] Parse standard circular/rectangular/obround aperture definitions needed
  for flashes and drill representations. Preserve raw aperture dimensions.
- [x] Implement modal X/Y coordinates, aperture selection, `D01` draw, `D02`
  move, and `D03` flash.
- [ ] Preserve `G01` lines and `G02`/`G03` arcs, including I/J centre offsets
  and single/multi-quadrant modes. Reject an ambiguous arc rather than
  flattening it incorrectly.
- [ ] Track image polarity sufficiently to reject unsupported negative/complex
  outline constructions. The importer must not mistake clear flashes or
  regions for rail geometry.
- [x] Parse JLC `G04 DEPTH n` comments as metadata but exclude ordinary comments
  from coordinate parsing.
- [ ] Ignore `ko` flashes when constructing outline paths; only D01 line/arc
  geometry contributes to edge-rail keepouts.
- [ ] Detect unsupported aperture macros, step-repeat constructs, region usage,
  or incremental-coordinate forms when they affect required geometry and show
  the exact file/command in the error.
- [ ] Put hard limits on commands, apertures, segments, and flashes so a bad
  production file cannot allocate unbounded memory or freeze Altium.

## Coordinate mapping

The UI, tables, report, preview, and created PCB primitives all use original
Gerber coordinates in millimetres. `Board_Origin` is reported as metadata only;
do not subtract it from feature coordinates before import.

- [ ] Extract all closed customer-outline contours from production `ko`
  `DEPTH 2` and final panel/rail geometry from `DEPTH 1`.
- [ ] Parse the original outline from `YG` in its own coordinate system.
- [ ] Match original and production customer-outline geometry using the eight
  orthogonal rigid-transform candidates: rotations 0/90/180/270 degrees, with
  and without mirroring, plus translation.
- [ ] Score candidates using contour count, total bounds, segment/arc lengths,
  transformed vertices, and point-to-contour error; do not rely on bounding-box
  size alone.
- [ ] Require one unique candidate within a documented tolerance. Include the
  raw fitted error and chosen transform in the report.
- [ ] Snap only tiny CAM unit-conversion noise (for example the sample's roughly
  1-2 micrometre inch-export error) after the transform is proven. Never round
  general geometry to a coarse grid.
- [ ] Apply the same proven transform to rail paths, holes, fiducials, and mask
  flashes. When mirroring, also reverse arc handedness correctly.
- [ ] Detect repeated/step-and-repeat customer-board instances. If they create
  more than one valid mapping to the original outline, show the analysis but
  disable Import with an explicit ambiguity message; do not choose a panel
  instance silently in the first implementation.
- [ ] Compare the active Altium board outline/bounds with the original outline
  before enabling Import. A clear mismatch must block import; a sub-tolerance
  mismatch is a warning recorded in the report.

For the known sample, automated tests and manual verification must reproduce:

```text
Original X = panel X + 68.000 mm
Original Y = panel Y + 86.300 mm
Original board: X 68.000..214.000, Y 96.300..158.300 mm
Final panel:    X 68.000..213.998, Y 86.300..168.298 mm
```

## Feature detection and dimensions

### Rail areas

- [ ] Build rail areas from the difference between the transformed `DEPTH 1`
  final-panel geometry and `DEPTH 2` customer-board area.
- [ ] Classify a feature as rail-only only when its centre is outside the
  customer-board area and inside/on the final-panel rail area within tolerance.
- [ ] Preserve every D01 line/arc in the `DEPTH 1` rail geometry for preview and
  optional keepout import, including rail-to-board boundary lines authored by
  JLCCAM.

### Edge holes

- [ ] Detect circular `drl` flashes in rail areas and exclude all customer-board
  drills.
- [ ] Require no meaningful copper annulus at the same location. Record top and
  bottom mask openings independently, even when the UI presents one combined
  value because they are equal.
- [ ] Treat the raw `drl` aperture as CAM tool size and infer nominal NPTH size
  separately. For recognized JLC rail holes, apply the documented 0.05 mm NPTH
  compensation only when the result lands on a plausible manufacturing grid
  and agrees with the 2.00 mm JLC standard. Otherwise mark nominal size
  unverified and disable that row/category from import rather than guessing.
- [ ] Sort deterministically by transformed Y, then X; allocate visible row
  numbers from that order.

Hole table columns, all in mm:

```text
# | X | Y | Nominal hole diameter | Top mask opening | Bottom mask opening | Status
```

When both mask openings are equal, the UI may visually group them under one
`Solder-mask opening` header, but the model/report must keep both values.

### Fiducials

- [ ] Detect candidates independently on `tl` and `bl`; never require a match
  on the opposite copper side.
- [ ] A candidate must be a circular copper flash in a rail area, have a
  same-side circular solder-mask opening at the same location, and have no
  drill at that location.
- [ ] De-duplicate only within the same side. A top and bottom fiducial at the
  same X/Y are two table/import rows.
- [ ] Use `Top` and `Bottom` side values from actual layer evidence, not position
  or symmetry.
- [ ] Track raw CAM copper size separately from nominal size. Recognize the JLC
  1.00 mm standard only when the CAM evidence matches the sample/standard
  compensation pattern within tolerance. Mark any other inferred value and
  confidence explicitly; do not silently subtract 0.05 mm from arbitrary
  copper apertures.
- [ ] Allow different top and bottom mask openings at the same X/Y.
- [ ] Reject or flag ambiguous nearby flashes, non-circular pads, missing masks,
  multiple matching masks, or candidates outside configured plausible size
  limits.

Fiducial table columns, all dimensions in mm:

```text
# | X | Y | Layer | Nominal copper diameter | Solder-mask opening | Status
```

## Review dialog and preview

- [ ] Create a resizable WPF modal owned by the Altium main window, matching the
  owner/lifetime guard used for `LayoutDuplicatorDialog`.
- [ ] Show source path/package name, detected transform, analysis status, and
  millimetre units prominently.
- [ ] Render with an equal X/Y scale and Y axis pointing upward. Reuse or extend
  `CanvasZoomPanHelper` for wheel zoom, left-drag pan, fit-on-load, and
  right-click fit.
- [ ] Preview legend:
  - original customer-board outline: dark neutral line;
  - final panel/edge-rail shape: blue keepout-width line;
  - edge holes: hollow circles/crosshairs;
  - top fiducials: warm colour;
  - bottom fiducials: cool colour;
  - coincident top+bottom fiducials: both colours remain distinguishable.
- [ ] Draw arcs as arcs, not coarse polylines. Include hover tooltips with row
  number, type, layer, X/Y, nominal size, raw CAM size, and mask opening.
- [ ] Bind the three checked-by-default import checkboxes to preview visibility
  as well as import selection so the user sees exactly what will be added.
- [ ] Use read-only `DataGrid`s with invariant numeric formatting and explicit
  `mm` column headers/tooltips. Do not inherit Altium's current display unit.
- [ ] Disable `Import` when parsing/mapping is ambiguous, no option is selected,
  a selected category contains unverified required dimensions, or the active
  board no longer matches the captured board/session.
- [ ] Disable controls and show progress while archive extraction or analysis is
  active; run file parsing off the UI thread but marshal only completed immutable
  models back to WPF.
- [ ] `Cancel` and window close dispose the source session and make no PCB edits.

## Clipboard report

- [ ] Build one report from the immutable analysis session and, after an import,
  append the import result. The dialog and clipboard must use exactly the same
  report builder.
- [ ] Include source, package structure, original outline source, Gerber units,
  transform formula/matrix, fit error, board/panel bounds, Board_Origin
  metadata, warnings, raw-versus-nominal rules, both feature tables, selected
  checkboxes, imported/skipped counts, names, and errors.
- [ ] Format all values in invariant-culture millimetres with enough precision
  to preserve CAM data (normally 3-6 decimals).
- [ ] Use WPF `Clipboard.SetText` on the UI/STA thread with a short bounded retry
  for a temporarily busy clipboard. Show `Copied` feedback or a selectable error.
- [ ] The known sample report must list four holes and eight fiducial rows when
  all four physical positions are present on both top and bottom copper.

## Native PCB import

Perform a complete preflight before `PCBServer.PreProcess()`:

- active document is still the captured `IPCB_Board`;
- board/original outline still matches;
- transform and all selected dimensions are verified;
- all coordinates and sizes are finite and within sane limits;
- all required layers exist;
- every primitive specification can be constructed;
- existing `PanelHole*`/`PanelFiducial*` objects are indexed.

### Edge rails

- [ ] Transform the analyzed `DEPTH 1` rail line/arc paths into original Gerber
  coordinates.
- [ ] Create `IPCB_Track` and `IPCB_Arc` primitives on
  `TLayerConstant.eKeepOutLayer` with exactly 0.1 mm width.
- [ ] Set the keepout state explicitly and preserve line/arc endpoints and arc
  direction. Do not alter the PCB board-outline object.

### Edge holes

- [ ] Create free simple-mode `IPCB_Pad4` objects on `eMultiLayer`.
- [ ] Use a round hole with nominal diameter, `Plated = false`, and top/mid/bottom
  pad diameters equal to the hole diameter so there is no copper annulus.
- [ ] Name holes `PanelHole1`, `PanelHole2`, ... in deterministic table order,
  choosing the next free numeric suffix when names already exist.
- [ ] Set solder-mask expansion from the hole edge. Use separate manual top and
  bottom radial expansions calculated as:

```text
expansion = (mask opening diameter - nominal hole diameter) / 2
```

- [ ] If a side has no mask opening, represent that side as no opening/tented
  rather than inheriting the board rule.
- [ ] Disable top and bottom paste generation.

### Fiducials

- [ ] Create one free simple-mode round SMD `IPCB_Pad4` per table row on exactly
  `eTopLayer` or `eBottomLayer`.
- [ ] Use nominal copper diameter, no hole, and no net.
- [ ] Name sequentially `PanelFiducial1`, `PanelFiducial2`, ... in table order.
  Coincident top and bottom fiducials receive separate names and pads.
- [ ] Disable paste on both sides so fiducials do not create stencil apertures.
- [ ] Apply a manual solder-mask expansion only on the fiducial's actual side:

```text
expansion = (mask opening diameter - nominal copper diameter) / 2
```

- [ ] Ensure the opposite side does not acquire a copper pad or unintended mask
  opening. Verify this in live Altium and generated Gerbers, not only by SDK
  property reads.

### Transactions, duplicate safety, and result

- [ ] Treat an existing same-category object with matching position, layer, and
  geometry as already imported and skip it with a report entry. Do not create
  exact duplicates on repeated imports.
- [ ] If an existing `PanelHole*`/`PanelFiducial*` name belongs to different
  geometry, keep it and allocate a new free suffix; never overwrite unrelated
  PCB data.
- [ ] Wrap all additions in one `PCBServer.PreProcess()` / `PostProcess()` undo
  operation. Use begin/end modify notifications as required by the live SDK.
- [ ] If adding any primitive fails, remove only primitives added by this
  invocation before closing the transaction, then report failure. Never leave a
  knowingly partial import.
- [ ] On success, mark the current PCB document modified, perform a full redraw,
  update the report, and close with a concise imported/skipped summary.
- [ ] Never call save APIs.

## Command and menu wiring checklist

- [x] Add `EasyEDAImportJlcCamArchive` and `EasyEDAImportJlcCamFolder` to
  `EasyEDA-Loader.ins`.
- [x] Register namespaced and unnamespaced forms in
  `EasyEDALoader.InitializeCommands()`:
  - `EasyEDAImportJlcCamArchive`;
  - `EasyEDA-Loader:EasyEDAImportJlcCamArchive`;
  - `EasyEDAImportJlcCamFolder`;
  - `EasyEDA-Loader:EasyEDAImportJlcCamFolder`.
- [x] Add an `Import &JLCCAM` tree under the PCB editor's existing `EasyEDA`
  menu in `EasyEDA-Loader.rcs`.
- [ ] Add child actions captioned `Import &archive` and `Import &folder`, each
  with a clear description and its corresponding process launcher.
- [ ] Place the submenu near `Loader`, separated from unrelated
  layer-switch/export commands.
- [ ] The archive handler resolves the active PCB through the command view and
  opens `OpenFileDialog` directly; the folder handler does the same with
  `FolderBrowserDialog`. Both pass the selected source to one shared
  `JlcCamImport` analysis/dialog workflow.
- [ ] Reuse the existing modal guard so a second EasyEDALoader modal command
  cannot open concurrently.

## Automated testing

Create a focused test project such as:

```text
Test/JlcCamImport/JlcCamImport.Tests.csproj
```

Link the parser/analyzer/report model files into it so most behavior is tested
without loading Altium assemblies or starting Altium. Use small synthetic
Gerbers committed as fixtures; do not add the user's 34 MB production files or
RAR archive to git.

- [ ] Parser tests: mm/inch, leading/trailing zeros, independent X/Y formats,
  modal coordinates, commands sharing a line, aperture selection, D01/D02/D03,
  lines, clockwise/counter-clockwise arcs, quadrant modes, and depth comments.
- [ ] Rejection tests: unsupported macro/region/step-repeat when relevant,
  malformed coordinates, missing units/format, impossible arcs, excessive
  commands, and non-finite/out-of-range geometry.
- [ ] Transform tests: translation, all four rotations, mirrored candidates,
  arc handedness, unique-fit tolerance, CAM noise snapping, ambiguous repeated
  instances, and mismatched outlines.
- [ ] Hole tests: rail-only filtering, board-drill exclusion, no-copper check,
  top/bottom mask differences, 2.05-to-2.00 nominal evidence, and unverified
  size blocking.
- [ ] Fiducial tests: top-only, bottom-only, both sides at one position, mask
  matching per side, drill exclusion, nearby decoys, 1.05-to-1.00 nominal
  evidence, and deterministic ordering.
- [ ] Source tests: case-insensitive layout, nested wrapper folder, outline in
  YG ZIP, multiple package rejection, corrupt/encrypted RAR errors, traversal
  rejection, and cleanup limited to the owned temp directory.
- [ ] Report tests: invariant millimetres, transform, confidence/warnings,
  separate top/bottom rows, raw and nominal sizes, and selected import options.
- [ ] Importer tests through a fake adapter: keepout width/layer, NPTH no copper
  annulus/plating, top/bottom fiducial sides, paste disabled, mask expansion
  math, unique names, repeated-import skip, rollback, single transaction, dirty
  flag, redraw, and no-save policy.
- [ ] Static command tests in the existing regression lane: `.ins`, `.rcs`, all
  four command registrations, PCB-only `Import JLCCAM` submenu, both stock
  picker actions, modal guard, `JlcCam*` naming, `JlcCamImport` production
  folder containment, and no external JLCCAM/Python/Pascal/MCP execution.
- [ ] Build and run:

```powershell
dotnet run --project Test\JlcCamImport\JlcCamImport.Tests.csproj
dotnet run --project Test\StepCleaner\StepCleaner.Tests.csproj -- --pcblib-actions
dotnet build EasyEDA-Loader\EasyEDA-Loader.csproj
```

## Manual Altium verification

- [ ] Install a local debug build only when implementation is requested and the
  user authorizes the normal install/test workflow.
- [ ] Open the intended original PCB and run the command with an extracted
  production folder.
- [ ] Verify preview geometry, axes, equal scaling, zoom/pan/fit, rail arcs,
  four holes, and separate top/bottom fiducials.
- [ ] Verify the known original-Gerber coordinates:

```text
Holes:     (75.000,91.300), (207.000,91.300),
           (75.000,163.300), (202.000,163.300)
Fiducials: (80.000,91.300), (202.000,91.300),
           (80.000,163.300), (197.000,163.300), per present side
```

- [ ] Verify nominal hole/copper sizes are 2.00/1.00 mm and sample mask openings
  are 2.35/2.00 mm while the report retains raw 2.05/1.05 mm CAM evidence.
- [ ] Copy the report and paste it into a text editor; verify all units and
  tables.
- [ ] Test each import checkbox alone and all three together.
- [ ] Inspect created objects in PCB List/Properties: layers, names, dimensions,
  plating, copper annulus, paste state, and per-side mask expansion.
- [ ] Generate Gerber/drill outputs and compare rail outline, NPTH holes, top
  masks, bottom masks, top fiducials, and bottom fiducials with JLC `ok` layers.
- [ ] Verify Cancel makes no change, Import makes exactly one undo step, Undo
  removes all objects from that invocation, Redo restores them, and the PCB is
  not saved automatically.
- [ ] Run Import twice and verify exact objects are skipped rather than doubled.
- [ ] Verify an unrelated PCB, ambiguous repeated panel, malformed archive, and
  one-sided fiducial fixtures fail safely with actionable messages.

## Recommended implementation order

1. [ ] Add models, Gerber tokenizer/parser, and parser fixtures.
2. [ ] Add package/folder discovery and safe in-process RAR support.
3. [ ] Add outline/depth extraction and rigid-transform matching.
4. [ ] Add independent hole, mask, and per-side fiducial analysis.
5. [ ] Add nominal-size resolver, diagnostics, and report builder.
6. [ ] Add the two stock-picker menu actions and read-only review dialog/tables.
7. [ ] Add preview renderer and checkbox-driven visibility.
8. [ ] Add the testable PCB adapter and full preflight.
9. [ ] Add edge-rail keepout import.
10. [ ] Add NPTH edge-hole import with per-side mask settings.
11. [ ] Add top/bottom fiducial import with paste disabled.
12. [ ] Add duplicate safety, rollback, one-step undo, dirty flag, and redraw.
13. [ ] Wire `.ins`, `.rcs`, the submenu, and all four command registrations.
14. [ ] Run automated tests/build and update documentation.
15. [ ] Perform the manual Altium/Gerber verification matrix.

## Acceptance criteria

- [ ] `Tools -> EasyEDA -> Import JLCCAM` appears only in the PCB editor and
  contains exactly `Import archive` and `Import folder` actions.
- [ ] `Import archive` opens the stock RAR file picker directly and `Import
  folder` opens the stock folder picker directly; no custom source chooser is
  introduced.
- [ ] All new production code is under `EasyEDA-Loader/JlcCamImport`, and its
  C# files/classes use the `JlcCam*` casing.
- [ ] Both a JLC RAR and its extracted folder produce the same analysis.
- [ ] Preview and all tables use original Gerber coordinates and millimetres.
- [ ] Top-only, bottom-only, and two-sided fiducials remain distinct and import
  to the correct copper/mask side.
- [ ] The report is copyable and includes raw CAM evidence, nominal dimensions,
  masks, transform, warnings, and import results.
- [ ] All three import checkboxes default to selected and control both preview
  and apply behavior.
- [ ] Rails are 0.1 mm keepout tracks/arcs; holes are named unplated no-copper
  through-hole pads; fiducials are named no-paste SMD pads with exact side and
  mask opening.
- [ ] Import is fully prevalidated, undoable in one step, duplicate-safe, and
  never saves the PCB.
- [ ] Ambiguous transforms/features/dimensions block unsafe import with a useful
  diagnostic instead of being guessed.
