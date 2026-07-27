# Shape SVG Export

The PCB and PCB footprint library menus include `EasyEDA -> Export shape` with:

- `All components`
- `Selected component`

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
