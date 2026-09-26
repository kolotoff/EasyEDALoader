# Code review — 2026-09-26

Reviewed the application and helper-project structure, import and export entry points, filesystem and HTTP boundaries, asynchronous work, layout mapping, Gerber parsing, STEP rendering, and existing regression coverage. Fixes are included in this change.

## Findings fixed

| Priority | Finding and effect | Fix |
| --- | --- | --- |
| P1 | Cache keys `.` and `..` resolved to parent directories; clearing a component cache could remove unrelated cached data. | Normalize Windows directory/device names and independently check the resolved deletion path. |
| P1 | Layout mappings could reuse one destination in multiple groups, moving it repeatedly and copying conflicting routing. | Reject every group involved in a destination collision, source-component reuse, and reuse of another selected anchor. |
| P1 | Closing the layout dialog with its window close button left an AI request active; its result could still reach board application. | Cancel work on close, check cancellation after the response, and prevent model refresh from replacing an active duplication token. |
| P1 | F3D process execution waited for exit without reading either redirected pipe, allowing output to deadlock the renderer. | Drain both pipes concurrently, bound execution, and terminate a timed-out renderer. |
| P1 | Gerber coordinate-free operations were skipped, region coordinates did not update the current point, and G74 arcs were interpreted as signed-offset G75 arcs. Clear-polarity tracks were also treated as positive rails. | Preserve operation/current-point state, resolve single-quadrant centres geometrically, exclude clear tracks, and stop at M02. |
| P2 | Thumbnail loading prefixed an already absolute URL with `https:`, producing an invalid URI. | Resolve absolute, protocol-relative, and relative HTTP(S) URLs consistently. |
| P2 | Search/product-info requests ignored caller cancellation. Missing optional descriptions also hid available symbols, footprints, and models. | Forward cancellation to HTTP, preserve cancellation exceptions, and handle absent descriptions. Reuse the HTTP client, enable matching decompression support, and dispose request/response resources. |
| P2 | Malformed or unusable downloaded component JSON was written before validation. | Deserialize and validate before updating the cache; a valid retry can still populate it. |
| P2 | Nested YG ZIP extraction omitted aggregate byte and entry limits; RAR extraction trusted declared byte counts. | Preflight nested ZIP limits and enforce actual streamed byte limits for both archive formats. |
| P2 | Layout validation threw on scalar/null groups and silently ignored extra source keys or omitted targets. | Validate JSON shapes and exact source keys, reject duplicate JSON properties, and report missing targets. |
| P2 | An idle command-pipe client could hold the sole listener indefinitely; shutdown did not cancel its read. | Apply a read timeout and cancellation, and capture the listener token before scheduling. |
| P2 | Edge-rail numeric fields accepted `NaN`, allowing non-finite dimensions into geometry generation. | Require finite parsed values. |
| P2 | Two existing test helpers failed independently of production behavior: multiline awaits were rejected and an OCCT reflection call used an old signature. | Scan the complete await statement and pass the optional OCCT argument correctly. |

The G74 fixture follows the unsigned-offset example in the [Ucamco Gerber specification](https://www.ucamco.com/files/downloads/file_en/416/the-gerber-layer-format-specification-revision-2021-02_en.pdf).

## Validation

- Release solution build passed. All other C# project entry points were also built successfully, including Standalone, SVG OutJob, OCCT, F3D, marker, and comparison tools. Existing platform/package warnings remain.
- Added `Test/Core/Core.Tests.csproj` to the solution: **16 tests passed**. Eight of the initial nine cases failed before the corresponding fixes.
- Expanded JLCCAM tests passed, including modal operations, region state, clear polarity, G74 centres, nested ZIP entry limits, and actual extracted-byte limits.
- Existing edge-rail and Tronstol pick-and-place tests passed.
- Full STEP regression passed: **17 original models cleaned and 17 validated models compared**.
- Focused STEP checks passed: metadata, symbol rules, library-save policy, async import, footprint placement/layers, PCB-library actions, Ulanzi plugin contracts, model cache, and OCCT overlap cleanup.
- Generated one top-side board SVG through the actual SVG writer and parsed its XML; verified its namespace, millimetre dimensions, and outline stroke. The fixture is written to `Test/Core/bin/Release/net8.0-windows/board-review.svg`.
- PowerShell build/install scripts and the two first-party Ulanzi JavaScript files passed syntax checks. `git diff --check` passed.

The first full STEP run found a missing generated `Test/StepCleaner/Data/Projection` folder. It was regenerated from the existing originals using the command documented in `StepCleaner/README.md`, then the complete suite passed. Accepted `Validated` models and marker annotations were not changed.

## Coverage limits

Live Altium board/library editing, undo behavior, OutJob execution, and 3D camera restoration were not exercised. HTTP regression tests use local fake responses; live EasyEDA/Ollama services were not required. The archive regression covers ZIP extraction and the shared bounded-copy path; no new RAR fixture was added. Third-party SDK binaries and vendored Ulanzi SDK code were outside the source review.

Existing untracked `PNP/` files and planning documents were left untouched.

## Reproduce the main checks

```powershell
dotnet build EasyEDA-Loader/EasyEDA-Loader.sln -c Release -v:q -clp:ErrorsOnly
dotnet run --project Test/Core/Core.Tests.csproj -c Release --no-build
dotnet run --project Test/JlcCamImport/JlcCamImport.Tests.csproj -c Release
dotnet run --project Test/EdgeRails/EdgeRails.Tests.csproj -c Release
dotnet run --project Test/TronstolE1Pnp/TronstolE1Pnp.Tests.csproj -c Release --no-build
dotnet run --project Test/StepCleaner/StepCleaner.Tests.csproj -c Release --no-build
```
