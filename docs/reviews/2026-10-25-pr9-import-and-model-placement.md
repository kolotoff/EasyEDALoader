# PR 9 import and model placement

The compatibility batch was committed as `27185ebc`: API search keyword, nullable
timestamps, persistent exception diagnostics, and default-disabled support for
BoardOutLine and BottomAssembly through a separate checkbox.

This follow-up adapts [upstream PR 9](https://github.com/expired6978/EasyEDALoader/pull/9)
at `c12e26bbcba366260b67578b881e4532010e056c` to the existing importer.

## Imported features

- SOLIDREGION parsing, preview and PCB regions. The SVG path parser handles
  relative coordinates, commas, multiple contours and curves. Cutout geometry
  is subtracted on the same layer; geometric polygon contours retain hole flags.
  Existing mechanical-layer options apply to regions.
- Optional paste and solder mask expansion values. Absent and empty fields keep
  rule-driven behavior; explicit zero and negative values are preserved. Parsing
  is invariant and rejects non-finite overrides.
- Multipart symbol deserialization and creation. Gates receive separate Altium
  owner-part IDs and share one footprint assignment. The current component is
  selected before drawing; current part returns to 1 afterward. Preview uses the
  first usable gate.
- Polyline, polygon and SVG path graphics in preview and schematic import. The
  current GOST pin layout and fonts remain. Graphic proportions are preserved
  while fitting the generated body. Open paths stay open and filled polygons use
  1-based Altium vertex indices. Compound schematic graphics with holes use
  outlines instead of incorrectly filling the holes.

Default Comment handling and Mechanical 1 assignment for imported 3D bodies
already existed and were retained. The upstream .NET 6 project and older import
routine were not substituted for the current .NET 8 implementation.

## PR 8 and the remaining model offset

[PR 8](https://github.com/expired6978/EasyEDALoader/pull/8) at
`4271d68e3a956e8033b110a752e59e5a03ceca52` proposes zeroing all model XY origins.
Cached EasyEDA source geometry contradicts that as a universal placement rule:

| Cached part | Source model center relative to footprint head, mm |
| --- | --- |
| C1325540 | X +0.1599946, Y +0.0000508 |
| C2982928 | X 0, Y +1.500997 |
| C7529392 | X -0.0000762, Y +1.1500 |

These values come from the cached component payloads' head, SVGNODE c_origin,
and projected child outline. The child outline centers agree with c_origin;
their dimensions also match the original STEP/OBJ extents measured by the OCCT
helper. These local cache measurements are reference evidence, not portable test
fixtures. Legitimate source displacement and explicit product offsets remain.

A separate defect was reproduced through the actual EEPCB bounds path: artwork
is inserted relative to the component position, but model bounds subtracted the
board origin. With component position (100,50) mm and board origin (90,40) mm, a
body centered on the component incorrectly measured as (10,10) mm. Primitive
bounds now use the component frame. Model fallback insertion likewise uses the
component position. This also keeps pad alignment and reprojection in the same
coordinate frame.

The regression uses dispatched Altium interfaces and exercises real bounds and
body-creation code. It failed with center (10,10) before the fix and passes with
center (0,0), including fallback insertion with a nonzero local model target.
PR 8's top/bottom pin offset hunk does not apply to this fork's two-column GOST
pin layout.

## Validation

- Release plugin and Standalone builds passed.
- All 27 Core checks passed, covering optional expansions, cutout topology, curved paths,
  multipart data, schematic graphics, region interop, owner-part assignment,
  polygon vertex indexing and model coordinate frames, alongside existing
  regressions.
- Cached C1325540, C2982928 and C7529392 payloads parsed successfully through the
  updated loader, including respectively 6, 14 and 14 nonempty solid regions.
- Focused placement, layer, symbol-rule, save-policy and asynchronous import
  checks passed.
- The full STEP regression passed: 17 original models cleaned and 17 validated
  models compared. PCB-library action checks also passed.
- A pre-existing multiline source assertion in the cache regression depended on
  LF-only line endings. Its comparison now normalizes Windows CRLF line endings.
  The cache regression passed after that correction.
- Live Altium import was not exercised. The connected application had no active
  PCB library; no installation, restart or document changes were performed.

The user could not identify the previously affected part and authorized choosing
reference cases. The reproduced coordinate-frame defect is fixed; this does not
establish that every source model displacement should be removed.
