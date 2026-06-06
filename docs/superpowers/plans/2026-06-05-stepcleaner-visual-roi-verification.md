# StepCleaner Visual ROI Verification And ROI-Limited Cleanup Plan

## Goal

Make StepCleaner fail when visible `LCEDA`, `EasyEDA`, or EasyEDA-logo watermark remnants remain, and fail when `RemovedGeometry` contains non-watermark geometry. Fix this in a common way by tying detection, cleanup, verification, and removed-geometry export to the same six-side visual watermark ROIs.

Do not update `Test/StepCleaner/Data/Validated` unless explicitly requested. Do not add generated `Clean` or `RemovedGeometry` files to git.

## Why Current Tests Passed

The tests passed because they check indirect signals instead of the actual user-visible failure:

- Post-clean verification only runs the edge-retention projection check when residual topology failures exist. If the cleaner removes some topology but leaves a visible black/white watermark imprint or logo edge pattern, the visual check can be skipped.
- Projection verification is limited to detector-reported regions. If the detector misses a side, polarity, logo, or partial text region, that unreported area is never checked.
- Text/logo cleanup tests currently assert counters and non-no-op behavior, not that known watermark templates disappear from the cleaned six-side projections.
- Removed-geometry tests check coarse properties such as existence, size, and basic protected-face conditions. They do not prove that all exported removed geometry lies inside a detected watermark ROI.
- `Validated` projections are old goldens and are not an adequate oracle for new template-based cleanup unless the user explicitly asks to refresh them.

## Design

Introduce a common visual watermark oracle:

- Render six-side F3D projections in both color and edge/wireframe modes for original, cleaned, and removed-geometry models.
- Detect only known watermark content: `LCEDA`, `EasyEDA`, and EasyEDA logo templates.
- Support both black/dark and white/light watermark marks, plus partial/cut-off template matches.
- Produce `WatermarkVisualRoi` records containing:
  - model side/view,
  - template id,
  - 2D projection rectangle/mask,
  - projected 3D box,
  - host face/plane,
  - top/bottom relief interval,
  - polarity/color class,
  - confidence score.

Use those ROIs everywhere:

- Cleanup may only modify geometry whose projected footprint and 3D bounds are inside an accepted `WatermarkVisualRoi`.
- Bump-based and cut-based marks are handled by detecting the host plane plus top/bottom watermark faces inside the ROI.
- Removed-geometry export may only contain faces/entities assigned to one or more accepted ROIs.
- Verification scans the full cleaned model for known templates; it must not depend on the cleaner's own reported regions.

## Work Items

1. Add failing visual residual tests.
   - Add a focused test mode such as `--text-logo-visual-residuals`.
   - Fixtures must include:
     - `USB-B-TH_USB-B10-BRW.step`
     - `USB-A-TH_FUS264-FDSW3K.step`
     - `CONN-TH_MR30PW-M30-G-Y.step`
     - `CONN-SMD_DF56_40S_0.3V_51.step`
     - `CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51.step`
     - `SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30.step`
     - `CONN-TH_XT60PB-M.step`
   - Test flow:
     - run cleaner,
     - render all six clean projections in color and edge modes,
     - run known-template detection on the full projection image,
     - fail if any `LCEDA`, `EasyEDA`, or logo detection remains above threshold.
   - This test should fail on the current commit for the files listed by the user.

2. Add failing removed-geometry locality tests.
   - Add a focused test mode such as `--removed-geometry-roi-locality`.
   - Fixtures must include at least:
     - `USB-B-TH_USB-B10-BRW.step`
     - `CONN-TH_MR30PW-M30-G-Y.step`
   - Test flow:
     - run cleaner with removed-geometry export,
     - collect accepted original watermark ROIs from the independent visual oracle,
     - render/inspect removed geometry,
     - fail if any connected component, face, or entity projects outside all inflated watermark ROIs,
     - fail if removed geometry is empty while the original has known watermark ROIs.

3. Implement `StepWatermarkVisualOracle`.
   - Own six-side rendering and full-image known-template detection.
   - Use already marked data and template sources to build text/logo templates.
   - Detect dark-on-light and light-on-dark marks.
   - Return residual detections independently from cleaner reports.
   - Expose summary metrics for tests and post-clean verification.

4. Refactor template promotion into ROI-first cleanup.
   - Introduce `WatermarkVisualRoi` and make template promotion return ROI-bound cleanup candidates.
   - Reject candidates without a visual template match.
   - Reject candidates whose candidate faces/entities extend outside the ROI box.
   - Do not broaden cleanup by whole host face, whole color cluster, whole solid, or distant arc/loop topology.

5. Add host-plane and relief-depth limiting.
   - For each ROI, search the host plane and top/bottom watermark relief faces.
   - Handle both raised/bump and recessed/cut watermarks.
   - Build a cleanup volume from the ROI footprint and detected relief interval.
   - Flatten/remove only geometry inside that volume.

6. Make `RemovedGeometry` use the same accepted ROI assignments.
   - Export only faces/entities actually modified or removed inside accepted ROIs.
   - Record removed entity to ROI mapping in the cleanup report.
   - Add a guard: if an exported removed entity is outside all accepted ROIs, skip it and report verifier failure.

7. Strengthen existing checks.
   - `--text-logo-cleanup-promotion` must assert visual disappearance, not only counters.
   - `--text-logo-verifier` must scan all six clean projections whenever the original contains a known visual watermark.
   - `--removed-geometry` must include locality assertions.
   - Keep clean-vs-validated comparison separate and do not refresh validated images without request.

8. Verification commands.
   - Build `Test/StepCleaner/StepCleaner.Tests.csproj`.
   - Run:
     - `--text-logo-visual-residuals`
     - `--removed-geometry-roi-locality`
     - `--text-logo-cleanup-promotion`
     - `--text-logo-negative-classifier`
     - `--text-logo-verifier`
     - `--removed-geometry`
     - `--xt60-lceda`
     - `--clean-text`

## Subagent Split For Implementation

- Subagent A: visual oracle and failing residual tests.
- Subagent B: ROI-first cleanup candidate selection and host-plane relief limiting.
- Subagent C: removed-geometry ROI mapping and locality tests.
- Main agent: integration, focused verification, git status, and commit only tracked source/test/template changes.

## Acceptance Criteria

- Current problematic fixtures fail before the code fix and pass after the fix.
- A cleaned model cannot pass while known `LCEDA`, `EasyEDA`, or logo templates remain visible in six-side color or edge projections.
- `RemovedGeometry` cannot pass if it contains geometry outside accepted watermark ROIs.
- Generated `Clean` and `RemovedGeometry` outputs remain untracked.
- `Validated` remains unchanged unless explicitly requested.
