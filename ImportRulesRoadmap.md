# EasyEDALoader Import Rules Roadmap

## Current State

The importer is being aligned with the Altium MCP schematic and footprint rules read from the adjacent `altium-mcp` documentation. The current implementation direction includes:

- Footprint 3D bodies placed on Mechanical Layer 1.
- 3D/body projection and assembly text on Mechanical Layer 2.
- Courtyard/body extents on Mechanical Layer 3.
- No pin-1 indication in the courtyard.
- Generated schematic symbols using GOST-style defaults where supported.
- Left/right-only schematic pins.
- Schematic pin placement on a 2.5 mm grid.
- Fixed 5 mm schematic pin length.
- Library document saving is explicit through the import dialog.

There is also pre-existing uncommitted spline-related work in `EasyEDA-Loader/StepProjectionRenderer.cs`. Treat that as separate from the import-rule roadmap and avoid mixing it with footprint or schematic rule changes unless a projection-specific task explicitly requires it.

## Footprint Roadmap

Continue hardening footprint import behavior around the GOST footprint rules:

- Keep EasyEDA-to-Altium layer mapping aligned with the target library convention:
  - Mechanical Layer 1: exact 3D body/model.
  - Mechanical Layer 2: 2D projection, `.Designator`, and `.Comment`.
  - Mechanical Layer 3: courtyard/body extents and center mark.
  - Top/Bottom Overlay: silkscreen only, with metric line widths.
- Ensure every pad, mounting pad, and mounting hole has a unique name.
- Prefer rounded-rectangle SMD pads.
- Generate courtyard geometry for imported footprints.
- Generate or preserve 3D body projection on Mechanical Layer 2.
- Ensure `.Designator` and `.Comment` are present on Mechanical Layer 2 when appropriate.
- Do not add pin-1 indication to the courtyard.
- Do not auto-save PcbLib or SchLib documents unless the user explicitly requests saving.
- Add future Altium-side visual QA using MCP primitive dumps and screenshots.

## Schematic Roadmap

Continue hardening schematic-symbol import behavior around the GOST schematic rules:

- Keep GOST font and body styling for generated schematic primitives.
- Keep symbol geometry on the fixed 2.5 mm grid.
- Keep generated pins on left and right sides only.
- Preserve fixed 5 mm pin length.
- Add datasheet-aware functional grouping for generated symbols.
- Add family-specific designator conventions, such as `XP?`, `XS?`, `X?`, `DD?`, and `DA?`, based on target library style.
- Add family-specific pin placement rules for connectors, USB, MCU/MPU, RF active parts, op-amps, interfaces, and isolation parts.

## Validation Plan

Use this validation sequence for importer changes:

- Build `EasyEDA-Loader.csproj`.
- Import representative connector, IC, USB, and QFN/BGA-style parts into test libraries.
- Inspect generated PcbLib and SchLib primitives through Altium/MCP.
- Confirm 3D bodies are on Mechanical Layer 1.
- Confirm projection and `.Designator`/`.Comment` are on Mechanical Layer 2.
- Confirm courtyard/body extents are on Mechanical Layer 3.
- Confirm the courtyard has no pin-1 indication.
- Confirm schematic pins stay on left/right sides, on a 2.5 mm grid, with 5 mm pin length.

## Open Follow-Ups

- Resolve the full-solution `Standalone`/`net48` compatibility issue separately.
- Review STEP projection accuracy against real Altium output.
- Add MCP-based regression checks for generated footprint primitives.
- Add MCP-based visual or primitive checks for generated schematic symbols.

## Assumptions

- Root Markdown means `D:\Develop\Altium\Ai\EasyEDALoader\ImportRulesRoadmap.md`.
- Full Roadmap means documentation only, not additional importer code changes.
- Existing code changes remain untouched while saving this plan file.
