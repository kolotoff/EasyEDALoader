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
- The initial checks did not exercise live Altium import. Subsequent native
  verification is recorded below.

The user could not identify the previously affected part and authorized choosing
reference cases. The reproduced coordinate-frame defect is fixed; this does not
establish that every source model displacement should be removed.

## Live Altium verification

Verified on Altium Designer Agile Enterprise 26.10.1 using isolated, unsaved
LiveVerification PCB and schematic libraries. The current Release build was
installed before testing; the original loader session settings were backed up.

- C1325540 imported through the actual loader dialog with four pads and one
  Mechanical 1 model body, without import warnings. Native body bounds were
  X -3.34001 to +3.66 mm and Y -2.49995 to +2.50005 mm: center
  (+0.159995, +0.00005) mm, matching the source SVGNODE offset within 0.00001 mm.
- The new layer checkbox was off by default. A synthetic OFF/ON pair produced
  respectively 6/7 M3 tracks and 0/1 M2 tracks, confirming the alternate outline
  and bottom-assembly geometry are independently opt-in.
- The synthetic copper region retained its native hole contour. Altium's group
  iterator returned the same object address twice; unique-object inspection
  confirmed one region with one hole.
- An explicit pad override had paste -0.127 mm and solder 0 mm, with native
  manual cache modes. An unspecified pad retained native rule cache modes and
  a 0.1016 mm rule-derived solder expansion.
- A synthetic multipart symbol imported as two parts, with four pins owned by
  each part, two body rectangles, and three-vertex polygon graphics on both
  parts. Native inspection confirmed owner-part IDs 1 and 2.

The visual check exposed graphic overlap with pin labels. A regression reproduced
the overlap before the correction. Graphic fitting now reserves the left and
right pin-label lanes, and all 28 Core checks pass.

The corrected build was installed and the multipart import repeated in a fresh
Altium session. Native inspection again confirmed two parts with four pins each,
and visual inspection confirmed that the filled graphic clears the pin labels.
The installed loader SHA256 matches the Release build:
`633B779929DC540172AC27E81D5C6115408C37BD42FBBB6FA054AE2500C0825F`.
The original loader session settings were restored. Scratch libraries remain
unsaved for inspection; no original design files were saved or modified.

These native imports used cached source data and synthetic fixtures; they do not
establish live EasyEDA HTTP service availability.
