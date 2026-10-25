# Upstream import proposals

Checked on 2026-10-25. Import the small API compatibility fix first, then consider two missing layer names. Most of upstream's AD25 fix already has an equivalent in this fork; a full merge would mix those fixes with an unsuitable runtime and dependency change.

## Comparison baseline

- Local `main`: `35e0fdf7d1fb0a820176e44aa4818b6ac6ea25ab`.
- Original repository `main`: [`1ade34da9c59ce53d27199452c4f7df9184d4789`](https://github.com/expired6978/EasyEDALoader/commit/1ade34da9c59ce53d27199452c4f7df9184d4789), dated 2026-08-10.
- Common ancestor: `1c93e7ed6bfee0cf4c21c55d87308f236435d6aa`, dated 2026-04-07.
- Git ancestry counts: 171 local-only commits and 5 upstream-only commits, including 2 upstream merge commits. These counts do not measure missing functionality.
- Fetched review refs: `upstream-review/main`, `upstream-review/pr-8`, and `upstream-review/pr-9`. No remote configuration was added.

## Recommended imports

### 1. API compatibility

Port [c5794727](https://github.com/expired6978/EasyEDALoader/commit/c5794727a066e2005aab32cb22946bd6db4f64b8) into the current implementation:

- Add an escaped `keyword` query parameter in [EasyedaApi.cs](../../EasyEDA-Loader/API/EasyedaApi.cs), retaining cancellation, shared HTTP transport, resource disposal, and current response handling. The current request sends only the `codes` JSON body. Upstream reports that the service ignores that body and requires the query parameter.
- Change `Utime` from `int` to `int?` in [FootprintData.cs](../../EasyEDA-Loader/FootprintData.cs) and [SymbolData.cs](../../EasyEDA-Loader/SymbolData.cs). Both remain non-nullable here. Upstream identifies empty timestamps, including part C5360828, as a deserialization failure.

This is the highest-priority, smallest import: three production files. Verify the actual outgoing query, including escaping, and deserialize numeric, null, empty, and missing timestamps. Existing Core search checks use fake responses and do not verify the required query parameter.

### 2. Missing layer names

Selectively adapt [971f957b](https://github.com/expired6978/EasyEDALoader/commit/971f957bcecbd066b3cc7c873f640c4ed64b23f7): support `BoardOutLine` as an alias for `BoardOutline`, and handle `BottomAssembly` according to this fork's assembly-layer policy.

[FootprintLayerMap.cs](../../EasyEDA-Loader/FootprintLayerMap.cs) already normalizes Document, ComponentShapeLayer, ComponentMarkingLayer, LeadShapeLayer, and ComponentPolarityLayer. [EEPCB.cs](../../EasyEDA-Loader/EEPCB.cs) already maps bottom solder mask correctly. The two names above currently pass normalization and reach an unsupported-layer exception.

Keep the existing M1/M2/M3 assignments and optional LCSC mechanical-layer filtering. Upstream's M7/M8/M9/M10/M11/M13/M15/M16 assignments differ from this fork's conventions. Verify both spellings, bottom assembly, and enabled/disabled optional-layer behavior with focused mapping checks.

### 3. Persistent primitive diagnostics

Consider the `ImportLog` idea from [971f957b](https://github.com/expired6978/EasyEDALoader/commit/971f957bcecbd066b3cc7c873f640c4ed64b23f7) as a smaller follow-up. Upstream appends exceptions and shows a failed-primitive count. This fork already displays footprint warnings through its progress dialog in [EasyEDALoader.cs](../../EasyEDA-Loader/EasyEDALoader.cs), so the useful addition is durable exception detail and a per-import warning summary. Integrate with the existing progress flow; do not replace it with upstream's older import routine.

## Changes already covered or unsuitable for direct import

| Upstream change | Local finding and disposition |
| --- | --- |
| Invariant numeric parsing in the AD25 fix | Covered by the shared invariant parsing helpers in Shapes.cs and their callers. No remaining bare double.Parse calls were found in the affected footprint/symbol shape directories. |
| CreateText Y-coordinate correction | Already uses the component's Y location. |
| Bottom solder-mask correction and common mechanical aliases | Already covered, with intentionally different mechanical-layer assignments. |
| SDK-style project migration | Already SDK-style, targeting net8.0-windows with DevExpress 24.1. Upstream targets net6.0-windows and DevExpress 22.1. Retain the current project; any AD25 build variant needs separate compatibility work. |
| Build/package/deploy suite from [e81f621e](https://github.com/expired6978/EasyEDALoader/commit/e81f621eddcef40692005a89e5b085de650e9a95) | Optional packaging design reference. The local installer already handles extra output generators, OCCT/F3D helpers, native dependencies, and registry backups. Upstream's original package selection is insufficient for this fork. |

The latest upstream [v1.0.1 release](https://github.com/expired6978/EasyEDALoader/releases/tag/v1.0.1) predates the AD25 fix. [Issue 10](https://github.com/expired6978/EasyEDALoader/issues/10) reports its archive omits `_Shared.ps1`; any adapted distribution must include its helper scripts. [PR 7](https://github.com/expired6978/EasyEDALoader/pull/7) also discusses runtime compatibility, and [issue 5](https://github.com/expired6978/EasyEDALoader/issues/5) remains open for AD26 loading/menu trouble. These are reasons to verify a runtime variant separately.

## Optional features from open pull requests

These changes are not part of upstream main.

| Candidate | Proposal for this fork |
| --- | --- |
| [PR 9](https://github.com/expired6978/EasyEDALoader/pull/9): multipart symbols | Most promising feature extension: the local ComponentInfo has no subparts model. Adapt subpart deserialization and Altium owner-part selection to the existing GOST symbol layout. Test a multipart device plus a normal single-part device. |
| PR 9: SOLIDREGION | Useful because the local footprint converter explicitly returns null for this primitive. Implement with a real path parser and explicit cutout handling. The proposed parser only handles simple space-separated M/L/Z paths and skips cutouts. |
| PR 9: pad paste/solder expansion | Useful optional metadata, but guard absent and empty trailing fields and preserve rule-driven expansion when unspecified. The PR directly indexes fields 17/18 and always enables manual expansion. |
| PR 9: schematic graphics | Evaluate separately against the deliberate generated GOST rectangle. The PR maps graphics into its generated bounds; irregular pin arrangements need verification. Default Comment and 3D-body layer handling already exist locally. |
| [PR 8](https://github.com/expired6978/EasyEDALoader/pull/8): top/bottom pin offsets | Not directly applicable to the current two-column GOST LayoutPins implementation. Retain as a reference if four-sided layouts are introduced. |
| PRs 8/9: zero model XY origin | Defer. This fork resolves SVGNODE origins, product offsets, and body placement through FootprintModelPlacement. Zeroing the origin would bypass existing behavior. |
| [PR 3](https://github.com/expired6978/EasyEDALoader/pull/3): LCSC browser, datasheets, project libraries | Defer wholesale import; evaluate individual UI features if wanted. Its broad project and dependency changes are unrelated to the immediate compatibility fixes. |

PR 9 explicitly says it was tested in CircuitStudio and remains untested in Altium Designer. Its features need selective implementation and live validation before being treated as supported.

## Scope and validation

This is a source/history comparison and an import proposal. No production code was changed or imported, no build or runtime tests were run, and Altium was not installed, restarted, or edited. Live EasyEDA behavior was not independently reproduced; API failure details above come from the upstream fix. Existing unrelated untracked files were left untouched.
