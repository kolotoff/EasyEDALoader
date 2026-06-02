# Motivations / Inspirations

Sourcing parts from JLCPCB can be a bit of a pain when you don't have the footprint, the symbol, or the model. 

If you're a user of KiCAD you're in luck as there's a nice project [easyeda2kicad.py](https://github.com/uPesy/easyeda2kicad.py) which will offline convert and pull models for you, and there's another script which integrates this script into KiCAD.

If you're an Altium user, you're stuck either running this script and importing via KiCAD import, or exporting the Altium files from EasyEDA and copying them manually into a footprint.

I used easyeda2kicad as a reference for this project as well as AtliumLibraryLoader script for reference on Altium APIs to manipulate adding parts to libraries

# Usage

Using the extension is pretty straight forward once it is installed, there will be a new Menu option `EasyEDA Loader` in Altium's menus, this will open a Modal Dialog which prompts the LCSC Part number e.g. "C2040". Tick which part you want to add after searching and press `Import to temp`, `Add footprint`, or `Add symbol`.

![Dialog](/Assets/Loader.PNG)

`Import to temp` will automatically create `EasyEDA.pcblib` and `EasyEDA.schlib` if they don't already exist in `Documents/AltiumEE`, create the footprint, download the 3d model, create the symbol, add part info, map the footprint to the symbol, then place the component into the active schematic at the bottom left. `Add footprint` imports only the footprint into the active PCB library, and `Add symbol` imports only the schematic symbol into the active schematic library.

# Runtime Dependencies

## F3D for STEP preview

The interactive 3D preview in the loader dialog uses [F3D](https://f3d.app/) to
render the cached STEP file directly. F3D provides the OpenCascade/OCCT STEP
reader used for proper STEP tessellation and display; the preview does not use
the EasyEDA OBJ model.

Install F3D on Windows with WinGet:

```powershell
winget install -e --id f3d-app.f3d
```

Or download and run the Windows x64 installer from:

```text
https://github.com/f3d-app/f3d/releases
```

The default installer path is:

```text
C:\Program Files\F3D\bin\f3d.exe
```

The extension looks for F3D in that default location. If F3D is installed
somewhere else, set `STEPCLEANER_F3D` to the full path of `f3d.exe` and restart
Altium so the extension can read the updated environment:

```powershell
[Environment]::SetEnvironmentVariable(
    "STEPCLEANER_F3D",
    "D:\Tools\F3D\bin\f3d.exe",
    "User")
```

If F3D is missing, footprint import can still download and attach STEP models,
but the right-side interactive STEP preview will not be available.

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
