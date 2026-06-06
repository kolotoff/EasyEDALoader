# StepCleaner Render And Detection Research

Date: 2026-06-06

This file records the current StepCleaner logo/text detection research so the render and detector experiments are not lost in generated reports or temporary files.

## Current Objective

Detect EasyEDA watermark content from projections:

- Cloud logo detection must be independent from text detection.
- CleanText=true must also detect arbitrary manufacturer text.
- Marked rectangles are truth only for report/testing, not detector input.
- Each detected logo/text region should be inside the matched marked rectangle.
- MarkedVsDetected should show split logo, split text, and an additional combined region.

The current cloud-logo target set is 15 marked views. The corrected truth includes `CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51__z_plus` and excludes `BUZ-SMD_4P-L7.5-W7.5-H2.5__z_minus`.

## Models With Cloud Logo

Current corrected report truth has cloud logos in these 15 model/view projections:

| Model | View |
| --- | --- |
| `BUZ-SMD_4P-L7.5-W7.5-H2.5` | `x_plus` |
| `BUZ-TH_D9.0-H5.5-P4.0` | `z_plus` |
| `CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51` | `z_plus` |
| `CONN-SMD_DF56_40S_0.3V_51` | `x_plus` |
| `CONN-TH_MR30PB-M30.A.G.Y` | `y_plus` |
| `CONN-TH_MR30PW-M30-G-Y` | `z_plus` |
| `HDMI-SMD_HDMI-001S` | `y_plus` |
| `LED-SMD_XL-3838UV2SA06G3` | `y_minus` |
| `LQFP-100_L14.0-W14.0-H1.4-LS16.0-P0.50` | `z_plus` |
| `SOT-223-4P_L6.5-W3.5-H1.6-LS7.0-P2.30` | `z_plus` |
| `SOT-89-3_L4.3-W2.5-H1.6-LS4.1-P1.50` | `x_plus` |
| `TYPE-C-TH_TYPEC-215-ARP14` | `x_plus` |
| `USB-A-SMD_USB-212-BCW` | `y_plus` |
| `USB-A-TH_FUS264-FDSW3K` | `x_plus` |
| `USB-B-TH_USB-B10-BRW` | `x_plus` |

## Projection Render Types

### Color Projection

Path examples:

- `Test/StepCleaner/Data/Projection/<model>__<view>.png`

Purpose:

- Main visual image for report overlays.
- Useful for grayscale/template matching where the watermark contrast survives the F3D render.
- Preserves colored model appearance.

Findings:

- Good for many text/logo cases.
- Not enough by itself for all cloud logos because watermark contrast can be weak or blended into the model surface.
- Grayscale matching on the color projection recovered several logos that binary color masks missed.

### Optimized Silhouette Edge Projection

Path examples:

- `Test/StepCleaner/Data/Projection/<model>__<view>__edge.png`

Code path:

- `StepProjectionRenderMode.Edge`
- `StepProjectionRenderer.ProjectFileImages`
- Uses `StepSilhouetteProjection.GenerateViews`.

Purpose:

- Existing optimized edge/silhouette path used by detection.
- Good input for text and silhouette region extraction because it removes/optimizes mechanical clutter.

Findings:

- Good for text/silhouette extraction.
- Not sufficient for the missing cloud case because the optimized edge output can lose or simplify the visible logo strokes.
- Should remain the main edge input for text, OCR-like component extraction, and general silhouette ROIs.

### Raw Edge Projection With Hidden Geometry

Purpose:

- Diagnostic path for full edge linework including hidden/invisible geometry.

Findings:

- It exposes many strokes, including non-visible mechanical linework.
- The extra non-visible geometry creates false positives and makes the image unsuitable as the general detection edge source.
- It can show watermark strokes but is too noisy for direct logo selection without strong filtering.

### Visible Raw Edge Projection Without Hidden Edges

Path examples:

- Temporary generated projection files: `<model>__<view>__edge_visible_raw.png`

Code path:

- `StepProjectionRenderMode.EdgeVisibleRaw`
- `StepProjectionRenderer.RenderVisibleRawEdgeProjectionImage`
- Test harness command:
  `dotnet Test/StepCleaner/bin/Debug/net8.0/StepCleaner.Tests.dll --edge-preview <input.step> <view> <output.png> --visible-raw`

Generated diagnostic contact sheet:

- `.codex-temp/visible-raw-edge-six-side/six-side-visible-raw-edge-collate.png`

Findings:

- The six-side collate shows the cloud logo clearly enough to detect in the target views.
- This is the best edge source for cloud logo matching because it preserves visible watermark strokes without hidden geometry clutter.
- It is not a good replacement for the general text/silhouette edge input: using it globally moved old text-template detections and created unrelated logo false positives.

Current conclusion:

- Use two edge inputs:
  - optimized edge for text/silhouette detection;
  - visible-raw edge only for cloud-logo edge-template detection.

## Detection Methods Tried

### Best Researched Results By Method

These results mix two evidence levels:

- `Report` means measured by MarkedVsDetected over the marked truth set.
- `Probe` means a focused diagnostic crop/score measurement, not yet promoted to the full detector/report result.

| Method | Best observed result | Evidence | Status |
| --- | --- | --- | --- |
| Color foreground mask | Could find some high-contrast watermark/text areas, but was not stable for cloud logos. Earlier color-first detector produced zero useful logo positives with many false positives. | Report/debug iterations before silhouette switch | Abandoned as primary logo detector; keep only as a supporting text/color ROI source. |
| Color/ink shape mask | Found some cloud-like candidates, but high scores also appeared on mechanical geometry. Example false candidate for `CONN-SMD_30P...__z_plus`: score about `0.614`, rect `1144:668:98:78`, outside truth. | Diagnostic template runs | Not reliable alone; keep below grayscale/edge methods in selection. |
| Grayscale template matching | Best full-report configuration before visible-raw edge work: `logo_matched=14`, `logo_missed=1`, with fewer false positives at threshold `0.48` and flat stddev limit `58.0`. | MarkedVsDetected report runs | Best current report-level logo contributor; still misses `CONN-SMD_30P...__z_plus`. |
| SIFT feature matching | No measured improvement over grayscale baseline; did not recover the remaining `CONN-SMD_30P...__z_plus` miss. | Focused report/debug runs | Optional supporting method only; cloud logo is too small/low-detail for dependable homography. |
| Binary/color feature matching | Did not produce a stable homography signal on small cloud logos. | Focused debug runs | Supporting method only. |
| Generalized Hough | Added rotation/scale-aware shape search, but mechanical linework generated plausible votes. | Focused debug runs | Not primary; false-positive risk is too high. |
| Optimized silhouette edge matching | Useful for text/silhouette ROI extraction; did not expose enough logo detail for every cloud case. | Report/debug comparisons | Keep for text and silhouette detection, not as sole logo edge source. |
| Raw edge with hidden geometry | Shows watermark strokes, but also includes non-visible mechanical linework that creates many false positives. | Render inspection | Diagnostic only for logo research; not suitable as general detector input. |
| Visible raw edge without hidden geometry | Six-side collate shows cloud strokes clearly across the target components. Latest split-edge report stayed at `logo_matched=14`, `logo_missed=1`, but a focused probe on the remaining miss found a valid in-truth candidate. | Report plus crop probe | Best next path for cloud logo. Needs smaller edge-specific scale search. |
| Visible raw edge crop probe for `CONN-SMD_30P...__z_plus` | Candidate inside truth: approx `761:845:49:36`, F1-like score `0.499`, truth rect `750:835:135:58`. | Probe | Confirms the missing logo is detectable from visible-raw edges; current C# matcher scale range starts too large. |
| OCR-like arbitrary text from optimized silhouette | Works as a shape/ROI detector for arbitrary text candidates when CleanText=true, but visible-raw edges shift text ROIs too much. | Report/debug comparisons | Keep on optimized silhouette edge input. |

### Color Foreground Mask

Code areas:

- `BuildColorForegroundMask`
- `FindKnownColorObjects`
- `FindColorTextRegions`

Findings:

- Useful for colored watermark/text contrast where foreground separation is clean.
- Weak for cloud logo when logo color is close to host surface or rendered with low contrast.
- Not robust enough as the primary logo detector.

### Color/Ink Shape Mask

Code areas:

- `FindLogoShapeMatches`
- `BuildLogoShapeTargetMask`
- `BuildLogoInkMask`
- `AddLogoInkMaskTemplateCandidates`
- `AddLogoContourShapeCandidates`
- `AddLogoHoughShapeCandidates`

Findings:

- Works on some high-contrast logos.
- Produces false positives on mechanical geometry with similar compact outline structure.
- Needs conservative selection and should not be trusted alone.

### Grayscale Template Matching

Code areas:

- `FindLogoGrayscaleTemplateMatches`
- `AddLogoGrayscaleTemplatePeaks`
- `LooksLikeFlatLogoSurface`

Settings at last better baseline:

- Normalized grayscale template threshold: `0.48`.
- Flat-surface stddev filter: `58.0`.
- Selection threshold for `easyeda-logo-grayscale-template`: `48.0`.

Findings:

- Recovered several logos missed by color-mask detection.
- False mechanical peaks are possible; the flat-surface filter reduces them.
- Still misses `CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51__z_plus`.

### SIFT Feature Matching

Code areas:

- `FindLogoSiftFeatureMatches`
- `TryCreateFeatureLogoDetection`

Implementation:

- Uses OpenCV SIFT descriptors.
- Runs on grayscale color projection.
- Uses BFMatcher + Lowe ratio + RANSAC homography.
- Reference variants include normal/flipped and 0/90/180/270 degree rotations.

Findings:

- Added for robustness but did not solve the remaining cloud miss.
- The EasyEDA cloud is small and low-detail; feature count is marginal in several projections.
- SIFT should remain optional, not the primary cloud detector.

### Feature Matching On Binary/Color Masks

Code areas:

- `FindLogoFeatureMatches`

Findings:

- Similar limitation to SIFT: small logo and sparse details make homography unstable.
- Useful only as a supporting signal.

### Generalized Hough

Code areas:

- `AddLogoHoughShapeCandidates`

Findings:

- Tried as a shape-based method with rotation/scale support.
- Did not become the reliable primary method; noisy mechanical edges can vote as plausible logo shapes.

### Edge Projection Template Matching

Code areas:

- `FindLogoEdgeProjectionMatches`
- `BuildLogoReferenceEdgeMask`
- `AddLogoEdgeProjectionPeaks`

Current implementation:

- Builds an edge outline from the marked/reference logo mask by morphological gradient.
- Matches the edge reference against a binary edge projection using correlation/F1-style score.
- Uses right-angle rotations and horizontal flip variants.
- Currently consumes the optional `logoEdgeImage` if provided; otherwise it falls back to the normal edge image.

Findings:

- This is the strongest direction for cloud-logo detection.
- The visible-raw edge crop for `CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51__z_plus` clearly contains the cloud.
- A pure-Pillow diagnostic probe around the marked crop found a strong match:
  - approximate full-resolution candidate: `x=761, y=845, w=49, h=36`;
  - F1-like score: `0.499`;
  - target rect is inside the marked truth rectangle `750:835:135:58`.
- The current C# edge matcher missed this because its reused scale range starts too large for this small edge-outline instance.

Immediate recommendation:

- Add an edge-specific scale range for `FindLogoEdgeProjectionMatches`, starting around `0.28` to `0.30`, instead of reusing `BuildLogoInkMaskTemplateScales()` which starts at `0.42`.
- Keep the score threshold near `0.34` initially; the missed CONN z_plus crop can score around `0.499` when the correct scale is searched.
- Keep visible-raw edge input isolated to logo detection; do not use it as the general silhouette/text edge source.

### OCR / Arbitrary Text

Code areas:

- `FindSilhouetteOcrTextRegions`
- `FindColorTextRegions`
- `ScoreArbitraryTextRoi`

Findings:

- Current OCR-like path is shape/ROI based rather than a full external OCR engine.
- It should continue to use the optimized silhouette edge mask, not visible-raw edge, because visible raw linework shifts text ROI selection.
- CleanText=true report needs separate text rectangles and a combined watermark rectangle.

## Current Report State

Latest MarkedVsDetected run with temporary visible-raw logo-edge files beside projection fixtures:

- report: `Test/StepCleaner/Data/CleanRunReport/MarkedVsDetected/Report.md`
- csv: `Test/StepCleaner/Data/CleanRunReport/MarkedVsDetected/marked-vs-detected.csv`
- `logo_matched=14`
- `logo_missed=1`
- `logo_not_expected=2`
- `logo_unmarked_detection=8`

Remaining missed cloud:

- `CONN-SMD_30P-P0.60_DF56C-30S-0.3V-51__z_plus`
- marked truth rectangle: `750:835:135:58`
- visible-raw edge image clearly shows the cloud + LCEDA/EasyEDA text inside this marked region.

Known false/unmarked logo detections in the latest report are mostly z_minus or non-cloud views where mechanical geometry resembles the logo. This confirms that logo selection still needs a stronger candidate prior or stricter context filter after adding the smaller edge scale.

## Current Design Direction

1. Keep detection split by kind:
   - logo detector produces `kind=logo`;
   - text/OCR detector produces `kind=text`;
   - report-only combined rectangle uses `kind=watermark-combined`.

2. Feed two edge images into detection:
   - optimized edge image for text and silhouette;
   - visible-raw edge image for cloud-logo edge matching only.

3. Make cloud-logo edge matching the primary cloud detector:
   - search smaller scales;
   - keep rotation and horizontal flip variants;
   - score candidates with edge F1/correlation;
   - require plausible bounds and selection priority;
   - later add context checks to reject mechanical false positives.

4. Keep marked data out of detector code:
   - marked rectangles are only for report truth, reference generation in the report harness, and verification.

5. Do not commit generated projection data:
   - diagnostic projections/contact sheets belong in `.codex-temp/` or report output only.
