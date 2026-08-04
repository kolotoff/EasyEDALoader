# Motivations / Inspirations

Sourcing parts from JLCPCB can be a bit of a pain when you don't have the footprint, the symbol, or the model. 

If you're a user of KiCAD you're in luck as there's a nice project [easyeda2kicad.py](https://github.com/uPesy/easyeda2kicad.py) which will offline convert and pull models for you, and there's another script which integrates this script into KiCAD.

If you're an Altium user, you're stuck either running this script and importing via KiCAD import, or exporting the Altium files from EasyEDA and copying them manually into a footprint.

I used easyeda2kicad as a reference for this project as well as AtliumLibraryLoader script for reference on Altium APIs to manipulate adding parts to libraries

# Usage

Using the extension is pretty straight forward once it is installed, there will be a new Menu option `EasyEDA Loader` in Altium's menus, this will open a Modal Dialog which prompts the LCSC Part number e.g. "C2040". Tick which part you want to add after searching and press `Import to temp`, `Add footprint`, or `Add symbol`.

![Dialog](/Assets/Loader.PNG)

`Import to temp` will automatically create `EasyEDA.pcblib` and `EasyEDA.schlib` if they don't already exist in `Documents/AltiumEE`, create the footprint, download the 3d model, create the symbol, add part info, map the footprint to the symbol, then place the component into the active schematic at the bottom left. `Add footprint` imports only the footprint into the active PCB library, and `Add symbol` imports only the schematic symbol into the active schematic library.

## JLCCAM production-panel import

In the PCB editor, use either of these actions:

`Tools` -> `EasyEDA` -> `Import JLCCAM` -> `Import archive`

`Tools` -> `EasyEDA` -> `Import JLCCAM` -> `Import folder`

`Import archive` selects a JLC production `.rar`; `Import folder` selects an
already extracted production folder. Both open a review dialog before changing
the board. The dialog previews the original board outline and panel rails,
lists edge-hole and per-side fiducial coordinates in millimetres, and can copy
the session report to the clipboard.

All three import categories are selected by default. On import, rail geometry
is added as 0.1 mm keepout tracks/arcs, edge holes are added as unplated
no-copper `PanelHole*` pads, and fiducials are added as side-specific no-paste
`PanelFiducial*` SMD pads.

## Tronstol E1 pick-and-place output

The extension adds a Tronstol E1 pick-and-place CSV generator to Altium
OutJobs. In an OutJob, choose:

`Assembly Outputs` -> `Generate Tronstol E1 PNP`

Select the source `.PcbDoc` in the same way as for Altium's generic
pick-and-place output. The default output filename is
`<PCB name> Tronstol E1 PNP.csv`.

The generated comma-delimited CSV uses a dot as the decimal separator,
millimetres for coordinates relative to the PCB coordinate origin, and the
following columns:

```text
"Designator","PartNumber","Footprint","Manufacturer","Description","Mid X","Mid Y","Layer","Rotation","Carrier","ReelPitch"
```

- `PartNumber` is read from the component's schematic `PartNumber` parameter;
  it is not copied from the component comment.
- `Manufacturer` is read from the component's schematic `Manufacturer`
  parameter.
- `Carrier` is read from the component's schematic `Carrier` parameter.
- `ReelPitch` is read from the component's schematic `ReelPitch` parameter.
- `Description` is selected from normalized schematic text. If normalized
  `PartNumber` does not contain the normalized component comment
  case-insensitively, the comment is exported as `Description`; otherwise the
  component `Description` parameter/property is exported.
- Text values are normalized before processing and before CSV output: tabs and
  line breaks are converted to spaces, repeated spaces are collapsed to one
  space, and leading/trailing whitespace is removed.
- Top-side components are labelled `Top`; bottom-side components are labelled
  `Bottom`.
- Bottom-side components are transformed as if the PCB is flipped around its X
  axis while keeping the board's left side on the left. X stays unchanged, Y is
  mirrored around the board outline's Y span (`Y' = BoardBottom + BoardTop - Y`,
  using coordinates relative to the PCB coordinate origin), and rotation is
  mirrored to `360 - rotation`.
- Components whose type is `No BOM` are always omitted.
- When panel fiducial export is enabled, board pads named
  `PanelFiducial<number>` are added before component rows. They are ordered by
  number, renamed to `Fiducial<number>`, exported with `PanelFiducial` in the
  `PartNumber` column, and use a footprint value based on pad shape and X size
  such as `Round 2.00mm`. Fiducial rotation is always exported as `0.0`; bottom
  fiducials are labelled `Bottom` and use the same X-axis flip coordinate rule
  as bottom-side components.
- When board dimension export is enabled, four rows are added below panel
  fiducials and above component rows: `PCB_Size1` and `PCB_BTLC1` for `Top`,
  followed by `PCB_Size2` and `PCB_BTLC2` for `Bottom`. `PCB_Size1` and
  `PCB_Size2` export `Board dimensions` in the `PartNumber` column, `PCB_Size`
  in the `Footprint` column, board width in `Mid X`, board height in `Mid Y`,
  and rotation `0.0`. `PCB_BTLC1` and `PCB_BTLC2` export `Board bottom left
  corner` in the `PartNumber` column, `PCB_BTLC` in the `Footprint` column, the
  board bottom-left X coordinate relative to the PCB coordinate origin in
  `Mid X`, the board bottom-left Y coordinate relative to the PCB coordinate
  origin in `Mid Y`, and rotation `0.0`. Bottom board-dimension rows are
  labelled `Bottom` but keep the same numeric size and corner values because
  they are board metadata, not component placements. If board bounds or the PCB
  coordinate origin cannot be read, generation stops with an error instead of
  emitting placeholder zero values.
- When edge rail export is enabled, an `EdgeRail` row is added in the same PCB
  metadata block. It exports `Edge rails size` in the `PartNumber` column,
  `EdgeRail` in the `Footprint` column, the left rail width in `Mid X`, the
  bottom rail width in `Mid Y`, layer `Top`, and rotation `0.0`. Rail size is
  calculated from the Keep-Out Layer outer envelope compared with the board
  outline bounds. The Keep-Out envelope is read from Keep-Out Layer track and
  arc primitives; when Altium does not expose their typed track/arc coordinate
  interfaces, the generator falls back to each primitive's bounding rectangle.
  Absent rails smaller than 0.05 mm are exported as `0.0000`. If Keep-Out Layer
  bounds cannot be read, generation stops with an error.
- Rows are grouped by the original schematic `PartNumber` value before output
  cleanup. Components inside each group are sorted by designator using natural
  order (`R2` before `R10`), and the groups are ordered by the first designator
  in each group.

The output's configuration dialog provides the following options. They are all
enabled by default:

- Remove a trailing `_BGA` from footprint names.
- Remove a trailing ` BGA` from footprint names.
- Skip components whose comment is `NF`.
- Skip components whose comment is `DNP` (surrounding whitespace is ignored).
- Skip components whose `SolderingType` parameter is `Manual`.
- Skip components whose `SolderingType` parameter is `Wave`.
- Export panel fiducials from board pads named `PanelFiducial<number>`.
- Export board dimensions and board bottom-left corner rows.
- Export edge rails size from the Keep-Out Layer.
- Remove a trailing footprint name from `PartNumber`, unless `PartNumber`
  consists only of that footprint name. For example,
  `100nF 50V ±10% X7R C0603` becomes `100nF 50V ±10% X7R`.

# Runtime Dependencies

## Experimental local AI layout duplication

The local AI PCB layout duplicator implementation is included in the source,
but its `Tools -> EasyEDA -> Duplicate layout` menu entry is temporarily
disabled. The command is not currently exposed as a normal user-facing feature
while its capture, mapping, and apply workflow is being validated.

## F3D for STEP preview

The interactive 3D preview in the loader dialog uses [F3D](https://f3d.app/) to
render the cached STEP file directly. The loader uses F3D's in-process
`f3d_c_api.dll` renderer for the side-by-side original/clean STEP preview, so
the preview stays inside the dialog instead of launching or embedding external
`f3d.exe` windows. F3D provides the OpenCascade/OCCT STEP reader used for proper
STEP tessellation and display; the preview does not use the EasyEDA OBJ model.

Install F3D on Windows with WinGet:

```powershell
winget install -e --id f3d-app.f3d
```

Or download and run the Windows x64 installer from:

```text
https://github.com/f3d-app/f3d/releases
```

The default installer path for the preview library is:

```text
C:\Program Files\F3D\bin\f3d_c_api.dll
```

The extension looks for `f3d_c_api.dll` in that default location. If F3D is
installed somewhere else, set `STEPCLEANER_F3D_LIB` to the full path of
`f3d_c_api.dll` and restart Altium so the extension can read the updated
environment:

```powershell
[Environment]::SetEnvironmentVariable(
    "STEPCLEANER_F3D_LIB",
    "D:\Tools\F3D\bin\f3d_c_api.dll",
    "User")
```

If F3D is missing, footprint import can still download and attach STEP models,
but the right-side interactive STEP preview will not be available.

## Ulanzi Studio plugin

This repository includes a Windows-only Ulanzi Studio plugin that lets a Ulanzi
Dial call EasyEDALoader commands through a local named-pipe bridge inside the
Altium extension.

Install it with:

```powershell
.\BuildAndInstall-UlanziStudio.ps1
```

The installer closes a running Ulanzi Studio/UlanziDeck process, installs the
plugin, then starts the app again so the plugin list is rescanned. Pass
`-NoRestart` if you want to leave Ulanzi Studio running.

The installer searches common Ulanzi Studio plugin folders, including the
current Windows user folders:

```text
%APPDATA%\Ulanzi\UlanziDeck\Plugins
%APPDATA%\Ulanzi\UlanziDeck\System\Plugins
```

It also checks LocalAppData, ProgramData, Documents, and installed Ulanzi
application folders.
If needed, pass the plugin folder explicitly:

```powershell
.\BuildAndInstall-UlanziStudio.ps1 -UlanziPluginRoot "C:\Path\To\UlanziStudio\plugins"
```

After installation, assign either the combined `EasyEDA Loader Dial` action or
one of the separate actions, such as `Next Signal Layer`, `Top Signal Layer`,
`Switch to Selected Primitive Layer`, or `Reproject 3D`. The separate actions are intended to behave like
`System > Hotkey`: each action entry maps to one EasyEDALoader command and can
be assigned independently. The manifest intentionally does not filter by device
model, because some Ulanzi Studio builds hide filtered plugins when a different
Deck/Dial model is the currently connected device.

Command mapping:

- Dial clockwise: switch to the next displayed signal layer.
- Dial counter-clockwise: switch to the previous displayed signal layer.
- Hold and rotate clockwise: switch to the bottom signal layer.
- Hold and rotate counter-clockwise: switch to the top signal layer.
- Keypad/run action: open the EasyEDA Loader dialog.
- `Switch to Selected Primitive Layer`: switch to the currently selected PCB primitive's layer.

Altium window must be active before the bridge executes a command. If another
application is focused, the bridge rejects the request with `altium-not-active`
instead of changing the Altium document from the background. Dial press is not
bound to any command.

If Ulanzi Studio shows `EasyEDALoader bridge pipe was not found` or a raw
`ENOENT \\.\pipe\EasyEDA-Loader.CommandBridge` error, the Altium extension that
hosts the pipe is not loaded. Close Altium and run:

```powershell
.\BuildAndInstall-Altium.ps1
```

Then keep Altium running with an Altium window active before triggering the
Ulanzi action.

### Altium `MSVCP140.dll` compatibility

The in-process F3D preview also needs a compatible Visual C++ runtime. Some
Altium installs ship an old app-local `MSVCP140.dll`; because Windows reuses
already-loaded DLLs by filename inside `X2.EXE`, F3D can fail to initialize even
when `f3d_c_api.dll` is present.

`BuildAndInstall-Altium.ps1` checks the version of Altium's app-local
`MSVCP140.dll` against the version bundled with F3D. If Altium's copy is older,
the script backs it up and installs the F3D-compatible copy before launching
Altium. The backup is stored beside `X2.EXE` in:

```text
<Altium exe folder>\EasyEDA-Loader-MsvcBackup\MSVCP140.dll.<yyyyMMdd-HHmmss>.bak
```

`BuildAndInstall-Altium.ps1` auto-detects the Altium profile and executable.
If the detected executable has an older app-local runtime, the backup folder is:

```text
<Altium exe folder>\EasyEDA-Loader-MsvcBackup\
```

# Comparisons

Left EasyEDA, Right Altium after import

## Symbol

![Comparison of EasyEDA Symbol](/Assets/Compare-Symbol.png)

## Footprint

![Comparison of EasyEDA Footprint](/Assets/Compare-Footprint.png)

## 3D Model

![Comparison of EasyEDA 3D](/Assets/Compare-3D.png)

## Part Info

![Comparison of EasyEDA Part Info](/Assets/PartInfo-EEL.PNG)

# Building

You shouldn't need anything special to build, just .NET 4.8, Language v8.0, and probably assembly references to Altium's internal libraries.

The following Assembly references were made and can be found in 

```
C:\Program Files\Altium\AD24\System
C:\Program Files\Altium\AD24\System\DotNet\DevExpress.Wpf
```

```
Altium.Controls
Altium.Controls.Skins
Altium.SDK
Altium.SDK.Interfaces
DevExpress.Data.v22.1
DevExpress.Mvvm.v22.1
DevExpress.Printing.v22.1.Core
DevExpress.Utils.v22.1
DevExpress.Xpf.Core.v22.1
DevExpress.Xpf.Grid.v22.1.Core
DevExpress.Xpf.Grid.v22.1
```

# Standalone

The standalone version is a simple WPF app that draws the primitives to a Canvas and was mainly used to validate without having to repeatedly re-launch Altium. Unfortunately doesn't load the step file, but will load the raw obj model, you can also use it to manually save the step or obj.

# Installation

Copy to your Offline Setup Altium Designer Extensions directory so that it can be installed from Extensions

Or extract contents to for example:

`C:\ProgramData\Altium\Altium Designer {08BC8A67-180A-4240-B39B-AF5998437998}\Extensions\EasyEDA-Loader`

And register it in your `ExtensionsRegistry.xml` with contents near the bottom.

For Altium Designer Agile 26, make sure the `PlatformVersions` values match the
installed platform build. A known working Agile 26 profile uses:

```
<DXP BuildNumber="1.0.16.61"/>
<EDP BuildNumber="10.0.16.61"/>
```

If these are left at the wrong build, Altium can show `EasyEDA-Loader` as
incompatible or fail to load it from the extension manager.

```
 <Item HRID="EasyEDA-Loader" Guid="8035C261-E5FE-403B-A9B5-9ABFFB6E0EF5">
    <Path>C:\ProgramData\Altium\Altium Designer {08BC8A67-180A-4240-B39B-AF5998437998}\Extensions\EasyEDA-Loader</Path>
    <Status>0</Status>
    <VaultGuid></VaultGuid>
    <CreatedBy>Altium, Inc.</CreatedBy>
    <CategoryGuid>793A1F67-0B22-4E01-A5DE-3176A1E8C60D</CategoryGuid>
    <CategoryName></CategoryName>
    <ReadMe></ReadMe>
    <Help></Help>
    <Requirements></Requirements>
    <Title>EasyEDA-Loader</Title>
    <ShortDescription>EasyEDA-Loader</ShortDescription>
    <LongDescription>EasyEDA-Loader</LongDescription>
    <SmallImage></SmallImage>
    <LargeImage></LargeImage>
    <Version>1.0.0.0</Version>
    <VersionGuid>7042BC82-F870-462D-86AF-B158AC75C490</VersionGuid>
    <ReleasedDate>45495.4140277778</ReleasedDate>
    <ReleaseNotes></ReleaseNotes>
    <DateInstalled>45838.7675816088</DateInstalled>
    <PlatformVersions>
      <DXP BuildNumber="1.0.16.61"/>
      <EDP BuildNumber="10.0.16.61"/>
      <MaxDXP BuildNumber="0.0.0.0"/>
      <MaxEDP BuildNumber="0.0.0.0"/>
    </PlatformVersions>
  </Item>
```

## Known Issues
The 3D model is not places *quite* right, something is still different from the reported translation and the actual. See [EeFootprint3dModel](/EasyEDA-Loader/FootprintShapes/EeFootprint3dModel.cs) for more information and how and where it retrieves model info from.
