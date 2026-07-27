# Shape SVG Export

The PCB and PCB footprint library menus include `EasyEDA -> Export shape` with:

- `All components`
- `Selected component`
- `All from selected libraries`

`All from selected libraries` first selects one or more `.PcbLib` files, then
selects the shared SVG target folder. Libraries opened by the command are
processed and closed one at a time. A library that was already open in Altium
is reused and left open. The source-library folder and SVG target folder are
remembered across Altium restarts; all shape-export commands share the same
target-folder setting. A failure in one footprint or library is collected while
the remaining footprints and libraries continue. Collected errors are shown
once after processing finishes in a selectable, read-only error list with a
`Copy to clipboard` button. Selected libraries are loaded directly into the PCB
server and unloaded after export without opening Altium documents, so they are
not added to `File > Recent Documents`.

The exporter writes SVG files from Mechanical 2 shape primitives. Coordinates are relative to the footprint or component origin.

## Diagnostics

Runtime diagnostics and export trace output are disabled by default. Normal exports should create only SVG files and no `EasyEDA-ShapeExport-Diagnostics-*.txt` file.

To enable diagnostics, create this file:

```text
%LOCALAPPDATA%\EasyEDA-Loader\shape-export-diagnostics.txt
```

Put one of these values in the file:

```text
true
```

Accepted enabled values are `1`, `true`, `yes`, `on`, and `enabled`.

When enabled, each export writes a diagnostic file into the selected export folder:

```text
EasyEDA-ShapeExport-Diagnostics-YYYYMMDD-HHMMSS.txt
```

To disable diagnostics again, delete `shape-export-diagnostics.txt` or set its contents to any other value, such as:

```text
false
```
