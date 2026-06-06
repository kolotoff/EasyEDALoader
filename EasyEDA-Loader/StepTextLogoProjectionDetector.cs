using OpenCvSharp;
using OpenCvSharp.Features2D;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace EasyEDA_Loader
{
    public sealed class StepTextLogoDetectionRegion
    {
        public string TemplateName { get; internal set; }
        public string Kind { get; internal set; }
        public string Text { get; internal set; }
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public double Score { get; internal set; }
        public double ChamferDistance { get; internal set; }
        public int EdgePixelCount { get; internal set; }
    }

    public static class StepTextLogoProjectionDetector
    {
        private const byte EdgeThreshold = 96;
        private const int MaxCandidateCount = 350;
        private const int TemplateSearchPeaksPerScale = 3;
        private const int MinimumMechanicalLineLengthPixels = 150;
        private const int MinimumLogoWidthPixels = 48;
        private const int MinimumLogoHeightPixels = 28;
        private const double MinimumLogoInkMaskTemplateScore = 0.62;

        public static IReadOnlyList<StepTextLogoDetectionRegion> Detect(
            StepProjectionImage colorImage,
            StepProjectionImage edgeImage)
        {
            return Detect(colorImage, edgeImage, new StepTextLogoDetectionOptions());
        }

        public static IReadOnlyList<StepTextLogoDetectionRegion> Detect(
            StepProjectionImage colorImage,
            StepProjectionImage edgeImage,
            StepTextLogoDetectionOptions options)
        {
            return Detect(colorImage, edgeImage, logoEdgeImage: null, options);
        }

        public static IReadOnlyList<StepTextLogoDetectionRegion> Detect(
            StepProjectionImage colorImage,
            StepProjectionImage edgeImage,
            StepProjectionImage logoEdgeImage,
            StepTextLogoDetectionOptions options)
        {
            if (colorImage == null)
                throw new ArgumentNullException(nameof(colorImage));
            if (edgeImage == null)
                throw new ArgumentNullException(nameof(edgeImage));
            if (colorImage.Width != edgeImage.Width || colorImage.Height != edgeImage.Height)
                throw new ArgumentException("Color and edge projections must have identical dimensions.");
            if (edgeImage.RgbaBytes == null || edgeImage.RgbaBytes.Length != edgeImage.Width * edgeImage.Height * 4)
                throw new ArgumentException("Edge projection image data is invalid.", nameof(edgeImage));
            if (colorImage.RgbaBytes == null || colorImage.RgbaBytes.Length != colorImage.Width * colorImage.Height * 4)
                throw new ArgumentException("Color projection image data is invalid.", nameof(colorImage));
            if (logoEdgeImage != null)
            {
                if (logoEdgeImage.Width != colorImage.Width || logoEdgeImage.Height != colorImage.Height)
                    throw new ArgumentException("Logo edge projection image dimensions must match color projection dimensions.", nameof(logoEdgeImage));
                if (logoEdgeImage.RgbaBytes == null || logoEdgeImage.RgbaBytes.Length != logoEdgeImage.Width * logoEdgeImage.Height * 4)
                    throw new ArgumentException("Logo edge projection image data is invalid.", nameof(logoEdgeImage));
            }

            options = options ?? new StepTextLogoDetectionOptions();
            using (Mat edgeMask = BuildEdgeForegroundMask(edgeImage))
            using (Mat logoEdgeMask = logoEdgeImage == null ? edgeMask.Clone() : BuildEdgeForegroundMask(logoEdgeImage))
            using (Mat silhouetteMask = BuildSilhouetteFeatureMask(edgeMask))
            {
                var detections = FindLogoShapeMatches(colorImage, options);
                detections.AddRange(FindLogoEdgeProjectionMatches(logoEdgeMask, options));
                if (options.UseGrayscaleLogoMatching)
                {
                    using (Mat colorGray = BuildLogoGrayscaleTarget(colorImage))
                    {
                        detections.AddRange(FindLogoGrayscaleTemplateMatches(colorGray, options));
                        detections.AddRange(FindLogoFeatureMatches(colorGray, options, binaryReference: false, sourceName: "grayscale"));
                    }
                }
                if (options.UseSiftLogoMatching)
                {
                    using (Mat colorGray = BuildLogoGrayscaleTarget(colorImage))
                        detections.AddRange(FindLogoSiftFeatureMatches(colorGray, options, sourceName: "grayscale"));
                }

                if (options.UseColorProjectionCandidates)
                {
                    using (Mat colorMask = BuildColorForegroundMask(colorImage))
                    {
                        detections.AddRange(FindLogoFeatureMatches(colorMask, options, binaryReference: true, sourceName: "color-mask"));
                        detections.AddRange(FindKnownColorObjects(colorMask, edgeMask, options));
                        if (options.DetectArbitraryText)
                            detections.AddRange(FindColorTextRegions(colorMask, edgeMask, detections, options));
                    }
                }

                detections.AddRange(FindKnownSilhouetteObjects(silhouetteMask, options));
                if (options.DetectArbitraryText)
                    detections.AddRange(FindSilhouetteOcrTextRegions(silhouetteMask, detections, options));

                BoostClusteredWatermarkScores(detections);
                if (options.IncludeCombinedWatermarkRegion)
                {
                    List<StepTextLogoDetectionRegion> splitDetections = SuppressSplitDetections(detections);
                    splitDetections.AddRange(BuildCombinedWatermarkRegions(splitDetections, silhouetteMask.Width, silhouetteMask.Height));
                    return splitDetections
                        .OrderByDescending(detection => detection.Score)
                        .ToList();
                }

                detections = SuppressSplitDetections(detections);
                return detections
                    .OrderByDescending(detection => detection.Score)
                    .ToList();
            }
        }

        private static List<StepTextLogoDetectionRegion> FindKnownColorObjects(
            Mat colorMask,
            Mat edgeMask,
            StepTextLogoDetectionOptions options)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            foreach (VisualRoi roi in ExtractVisualRois(colorMask, edgeMask, options))
            {
                if (LooksTooLargeForColorProjectionCandidate(roi.Bounds, colorMask.Width, colorMask.Height))
                    continue;

                using (Mat roiMask = new Mat(colorMask, roi.Bounds))
                {
                    List<ComponentBox> components = ExtractComponents(roiMask);
                    if (LooksLikeRegularPinArray(components, roi.Bounds) ||
                        LooksLikeLongStraightSeam(roiMask, roi.Bounds) ||
                        LooksLikeSingleConnectorContact(components, roi.Bounds))
                    {
                        continue;
                    }

                    TemplateScore best = FindBestKnownTemplate(roiMask, includeLogoTemplates: false);
                    if (best.Score < options.MinimumKnownTemplateScore)
                    {
                        TextShapeScore textShape = ScoreArbitraryTextRoi(roiMask, components);
                        best = FindBestGeometryTemplate(roiMask, textShape);
                        if (best.Score < options.MinimumKnownTemplateScore)
                            continue;
                    }

                    detections.Add(new StepTextLogoDetectionRegion
                    {
                        TemplateName = best.Template.Name,
                        Kind = best.Template.Kind,
                        Text = best.Template.Text,
                        X = roi.Bounds.X,
                        Y = roi.Bounds.Y,
                        Width = roi.Bounds.Width,
                        Height = roi.Bounds.Height,
                        Score = Math.Round(best.Score * 100.0 + 5.0, 3),
                        ChamferDistance = best.ChamferDistance,
                        EdgePixelCount = roi.ForegroundPixels
                    });
                }
            }

            return detections;
        }

        private static List<StepTextLogoDetectionRegion> FindColorTextRegions(
            Mat colorMask,
            Mat edgeMask,
            IReadOnlyList<StepTextLogoDetectionRegion> knownDetections,
            StepTextLogoDetectionOptions options)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            foreach (VisualRoi roi in ExtractVisualRois(colorMask, edgeMask, options))
            {
                if (knownDetections.Any(known => IntersectionOverUnion(known, roi.Bounds) > 0.20))
                    continue;
                if (LooksTooLargeForColorProjectionCandidate(roi.Bounds, colorMask.Width, colorMask.Height))
                    continue;

                using (Mat roiMask = new Mat(colorMask, roi.Bounds))
                {
                    List<ComponentBox> components = ExtractComponents(roiMask);
                    if (LooksLikeRegularPinArray(components, roi.Bounds) ||
                        LooksLikeLongStraightSeam(roiMask, roi.Bounds) ||
                        LooksLikeSingleConnectorContact(components, roi.Bounds))
                    {
                        continue;
                    }

                    TextShapeScore score = ScoreArbitraryTextRoi(roiMask, components);
                    if (score.Score < options.MinimumArbitraryTextScore)
                        continue;

                    detections.Add(new StepTextLogoDetectionRegion
                    {
                        TemplateName = "color-text",
                        Kind = "text",
                        Text = "",
                        X = roi.Bounds.X,
                        Y = roi.Bounds.Y,
                        Width = roi.Bounds.Width,
                        Height = roi.Bounds.Height,
                        Score = Math.Round(score.Score * 100.0 + 4.0, 3),
                        ChamferDistance = score.ChamferDistance,
                        EdgePixelCount = roi.ForegroundPixels
                    });
                }
            }

            return detections;
        }

        private static bool LooksTooLargeForColorProjectionCandidate(Rect bounds, int imageWidth, int imageHeight)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return true;

            double widthRatio = bounds.Width / (double)Math.Max(1, imageWidth);
            double heightRatio = bounds.Height / (double)Math.Max(1, imageHeight);
            double areaRatio = (bounds.Width * bounds.Height) / (double)Math.Max(1, imageWidth * imageHeight);
            return widthRatio > 0.36 || heightRatio > 0.30 || areaRatio > 0.055;
        }

        private static List<StepTextLogoDetectionRegion> FindLogoShapeMatches(
            StepProjectionImage colorImage,
            StepTextLogoDetectionOptions options)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            using (Mat reference = LoadLogoReferenceMask(options, binaryReference: true))
            using (Mat targetMask = BuildLogoShapeTargetMask(colorImage))
            using (Mat targetInkMask = BuildLogoInkMask(colorImage))
            {
                if (reference == null || reference.Empty() || targetMask.Empty())
                    return detections;

                Rect referenceBounds = TightForegroundBounds(reference, new Rect(0, 0, reference.Width, reference.Height));
                if (referenceBounds.Width <= 0 || referenceBounds.Height <= 0)
                    return detections;

                using (Mat referenceTight = new Mat(reference, referenceBounds).Clone())
                using (Mat searchInkMask = ResizeUnitBinaryMask(
                    targetInkMask,
                    Math.Max(1, targetInkMask.Width / 2),
                    Math.Max(1, targetInkMask.Height / 2)))
                using (Mat searchInkReference = ResizeUnitBinaryMask(
                    referenceTight,
                    Math.Max(1, referenceTight.Width / 2),
                    Math.Max(1, referenceTight.Height / 2)))
                using (Mat searchTarget = ResizeBinaryMask(
                    targetMask,
                    Math.Max(1, targetMask.Width / 2),
                    Math.Max(1, targetMask.Height / 2)))
                using (Mat searchReference = ResizeBinaryMask(
                    referenceTight,
                    Math.Max(1, referenceTight.Width / 2),
                    Math.Max(1, referenceTight.Height / 2)))
                {
                    AddLogoInkMaskTemplateCandidates(searchInkMask, targetInkMask, targetMask, searchInkReference, detections);
                    AddLogoContourShapeCandidates(targetMask, referenceTight, detections);
                    if (options.UseGeneralizedHoughLogoMatching)
                        AddLogoHoughShapeCandidates(searchTarget, targetMask, searchReference, detections);

                }
            }

            return SuppressOverlappingDetections(detections, maxCount: 8);
        }

        private static IEnumerable<Mat> BuildLogoReferenceOrientations(Mat reference)
        {
            foreach (Mat basis in BuildLogoReferenceBases(reference))
            {
                using (basis)
                {
                    yield return basis.Clone();

                    var rotate90 = new Mat();
                    Cv2.Rotate(basis, rotate90, RotateFlags.Rotate90Clockwise);
                    yield return rotate90;

                    var rotate180 = new Mat();
                    Cv2.Rotate(basis, rotate180, RotateFlags.Rotate180);
                    yield return rotate180;

                    var rotate270 = new Mat();
                    Cv2.Rotate(basis, rotate270, RotateFlags.Rotate90Counterclockwise);
                    yield return rotate270;
                }
            }
        }

        private static IEnumerable<Mat> BuildLogoReferenceBases(Mat reference)
        {
            yield return reference.Clone();

            var flipHorizontal = new Mat();
            Cv2.Flip(reference, flipHorizontal, FlipMode.Y);
            yield return flipHorizontal;
        }

        private static IEnumerable<double> BuildLogoShapeScales()
        {
            for (double scale = 0.55; scale <= 1.90; scale *= 1.11)
                yield return scale;
        }

        private static IEnumerable<double> BuildLogoInkMaskTemplateScales()
        {
            for (double scale = 0.42; scale <= 1.70; scale *= 1.08)
                yield return scale;
        }

        private static void AddLogoInkMaskTemplateCandidates(
            Mat searchInkMask,
            Mat fullInkMask,
            Mat fullTargetMask,
            Mat referenceMask,
            List<StepTextLogoDetectionRegion> detections)
        {
            if (searchInkMask == null || searchInkMask.Empty() ||
                fullInkMask == null || fullInkMask.Empty() ||
                fullTargetMask == null || fullTargetMask.Empty() ||
                referenceMask == null || referenceMask.Empty())
            {
                return;
            }

            using (var targetIntegral = new Mat())
            {
                Cv2.Integral(searchInkMask, targetIntegral, MatType.CV_32S);
                foreach (Mat orientedReference in BuildLogoReferenceOrientations(referenceMask))
                {
                    using (orientedReference)
                    {
                        foreach (double scale in BuildLogoInkMaskTemplateScales())
                        {
                            int width = (int)Math.Round(orientedReference.Width * scale, MidpointRounding.AwayFromZero);
                            int height = (int)Math.Round(orientedReference.Height * scale, MidpointRounding.AwayFromZero);
                            if (width < MinimumLogoWidthPixels / 2 ||
                                height < MinimumLogoHeightPixels / 2 ||
                                width >= searchInkMask.Width ||
                                height >= searchInkMask.Height)
                            {
                                continue;
                            }

                            using (Mat template = ResizeUnitBinaryMask(orientedReference, width, height))
                            using (var overlap = new Mat())
                            {
                                int templatePixels = Cv2.CountNonZero(template);
                                if (templatePixels < 60)
                                    continue;

                                Cv2.MatchTemplate(searchInkMask, template, overlap, TemplateMatchModes.CCorr);
                                AddLogoInkMaskTemplatePeaks(
                                    searchInkMask,
                                    fullInkMask,
                                    fullTargetMask,
                                    targetIntegral,
                                    template,
                                    overlap,
                                    templatePixels,
                                    detections);
                            }
                        }
                    }
                }
            }
        }

        private static void AddLogoInkMaskTemplatePeaks(
            Mat searchInkMask,
            Mat fullInkMask,
            Mat fullTargetMask,
            Mat targetIntegral,
            Mat template,
            Mat overlap,
            int templatePixels,
            List<StepTextLogoDetectionRegion> detections)
        {
            using (Mat mutableScore = new Mat(overlap.Size(), MatType.CV_32FC1, Scalar.All(0)))
            {
                int rows = overlap.Rows;
                int cols = overlap.Cols;
                for (int y = 0; y < rows; y += 2)
                {
                    for (int x = 0; x < cols; x += 2)
                    {
                        int targetPixels = SumIntegral(targetIntegral, x, y, template.Width, template.Height);
                        if (targetPixels < 18)
                            continue;

                        double hits = overlap.At<float>(y, x);
                        double score = 2.0 * hits / Math.Max(1.0, templatePixels + targetPixels);
                        if (score >= MinimumLogoInkMaskTemplateScore)
                            mutableScore.Set(y, x, (float)score);
                    }
                }

                for (int peak = 0; peak < 4; peak++)
                {
                    Cv2.MinMaxLoc(mutableScore, out _, out double score, out _, out Point location);
                    if (score < MinimumLogoInkMaskTemplateScore)
                        break;

                    Rect bounds = ClipRect(new Rect(location.X, location.Y, template.Width, template.Height), searchInkMask.Width, searchInkMask.Height);
                    Rect fullBounds = ScaleRect(bounds, 2.0, fullTargetMask.Width, fullTargetMask.Height);
                    if (LooksLikePlausibleLogoBounds(fullBounds, fullTargetMask.Width, fullTargetMask.Height))
                    {
                        Rect expanded = ExpandRect(
                            fullBounds,
                            fullTargetMask.Width,
                            fullTargetMask.Height,
                            Math.Max(10, Math.Min(28, Math.Max(fullBounds.Width, fullBounds.Height) / 6)));
                        if (!detections.Any(existing => IntersectionOverUnion(existing, expanded) > 0.45))
                        {
                            detections.Add(new StepTextLogoDetectionRegion
                            {
                                TemplateName = "easyeda-logo-ink-mask",
                                Kind = "logo",
                                Text = "",
                                X = expanded.X,
                                Y = expanded.Y,
                                Width = expanded.Width,
                                Height = expanded.Height,
                                Score = Math.Round(score * 100.0, 3),
                                ChamferDistance = Math.Max(0.0, 12.0 - score * 12.0),
                                EdgePixelCount = CountNonZero(fullTargetMask, expanded)
                            });
                        }
                    }

                    SuppressTemplateResultPeak(mutableScore, location, template.Width, template.Height);
                }
            }
        }

        private static bool LooksLikeLogoAtFullResolution(
            Mat fullInkMask,
            Mat searchTemplate,
            Rect fullBounds,
            double templateScore)
        {
            if (fullInkMask == null || fullInkMask.Empty() ||
                searchTemplate == null || searchTemplate.Empty() ||
                fullBounds.Width <= 0 || fullBounds.Height <= 0)
            {
                return false;
            }

            Rect clipped = ClipRect(fullBounds, fullInkMask.Width, fullInkMask.Height);
            if (clipped.Width <= 0 || clipped.Height <= 0)
                return false;

            using (Mat roi = new Mat(fullInkMask, clipped))
            using (Mat fullTemplate = ResizeUnitBinaryMask(searchTemplate, clipped.Width, clipped.Height))
            {
                LogoShapeScore verification = ScoreLogoShapeCandidate(roi, fullTemplate, templateScore);
                return verification.Score >= 0.48 &&
                    verification.ChamferDistance <= 16.0;
            }
        }

        private static void AddLogoContourShapeCandidates(
            Mat targetMask,
            Mat reference,
            List<StepTextLogoDetectionRegion> detections)
        {
            Point[] referenceContour = FindLargestContour(reference);
            if (referenceContour == null || referenceContour.Length < 16)
                return;

            foreach (Rect candidate in ExtractLogoContourCandidateRects(targetMask))
            {
                using (Mat candidateMask = new Mat(targetMask, candidate))
                {
                    Point[] candidateContour = FindLargestContour(candidateMask);
                    if (candidateContour == null || candidateContour.Length < 12)
                        continue;

                    double shapeDistance = Cv2.MatchShapes(
                        referenceContour,
                        candidateContour,
                        ShapeMatchModes.I1,
                        0.0);
                    if (double.IsNaN(shapeDistance) || double.IsInfinity(shapeDistance))
                        continue;

                    double contourScore = 1.0 / (1.0 + shapeDistance * 6.0);
                    if (contourScore < 0.34)
                        continue;

                    LogoShapeScore best = LogoShapeScore.Rejected;
                    foreach (Mat orientedReference in BuildLogoReferenceOrientations(reference))
                    {
                        using (orientedReference)
                        using (Mat template = ResizeBinaryMask(orientedReference, candidate.Width, candidate.Height))
                        {
                            LogoShapeScore maskScore = ScoreLogoShapeCandidate(candidateMask, template, correlation: contourScore);
                            double densityScore = LogoCandidateDensityScore(candidateMask);
                            double score = 0.42 * contourScore + 0.43 * maskScore.Score + 0.15 * densityScore;
                            if (score > best.Score)
                            {
                                best = new LogoShapeScore
                                {
                                    Score = score,
                                    ChamferDistance = maskScore.ChamferDistance
                                };
                            }
                        }
                    }

                    if (best.Score < 0.48)
                        continue;

                    Rect tight = TightForegroundBounds(targetMask, candidate);
                    if (tight.Width < MinimumLogoWidthPixels || tight.Height < MinimumLogoHeightPixels)
                        tight = candidate;
                    tight = ExpandRect(tight, targetMask.Width, targetMask.Height, Math.Max(3, Math.Min(10, Math.Max(tight.Width, tight.Height) / 34)));

                    detections.Add(new StepTextLogoDetectionRegion
                    {
                        TemplateName = "easyeda-logo-contour",
                        Kind = "logo",
                        Text = "",
                        X = tight.X,
                        Y = tight.Y,
                        Width = tight.Width,
                        Height = tight.Height,
                        Score = Math.Round(best.Score * 100.0, 3),
                        ChamferDistance = best.ChamferDistance,
                        EdgePixelCount = CountNonZero(targetMask, tight)
                    });
                }
            }
        }

        private static IEnumerable<Rect> ExtractLogoContourCandidateRects(Mat targetMask)
        {
            var candidates = new List<Rect>();
            foreach (Size kernel in BuildLogoContourGroupingKernels())
            {
                using (var grouped = new Mat())
                {
                    Cv2.MorphologyEx(
                        targetMask,
                        grouped,
                        MorphTypes.Close,
                        Cv2.GetStructuringElement(MorphShapes.Rect, kernel));
                    Cv2.MorphologyEx(
                        grouped,
                        grouped,
                        MorphTypes.Open,
                        Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2)));

                    Cv2.FindContours(
                        grouped,
                        out Point[][] contours,
                        out _,
                        RetrievalModes.External,
                        ContourApproximationModes.ApproxSimple);
                    foreach (Point[] contour in contours)
                    {
                        if (contour == null || contour.Length < 4)
                            continue;

                        Rect bounds = Cv2.BoundingRect(contour);
                        if (!LooksLikePlausibleLogoBounds(bounds, targetMask.Width, targetMask.Height))
                            continue;

                        Rect tight = TightForegroundBounds(targetMask, bounds);
                        if (!LooksLikePlausibleLogoBounds(tight, targetMask.Width, targetMask.Height))
                            continue;

                        candidates.Add(ExpandRect(tight, targetMask.Width, targetMask.Height, Math.Max(3, Math.Min(12, Math.Max(tight.Width, tight.Height) / 30))));
                    }
                }
            }

            return candidates
                .Distinct(new RectComparer())
                .OrderBy(rect => rect.Y)
                .ThenBy(rect => rect.X)
                .Take(120)
                .ToList();
        }

        private static IEnumerable<Size> BuildLogoContourGroupingKernels()
        {
            yield return new Size(5, 5);
            yield return new Size(9, 7);
            yield return new Size(15, 9);
            yield return new Size(23, 13);
            yield return new Size(31, 17);
        }

        private static bool LooksLikePlausibleLogoBounds(Rect bounds, int imageWidth, int imageHeight)
        {
            if (bounds.Width < MinimumLogoWidthPixels || bounds.Height < MinimumLogoHeightPixels)
                return false;
            if (bounds.Width > imageWidth * 0.34 || bounds.Height > imageHeight * 0.28)
                return false;

            double aspect = bounds.Width / (double)Math.Max(1, bounds.Height);
            return aspect >= 0.55 && aspect <= 3.80;
        }

        private static Point[] FindLargestContour(Mat binaryMask)
        {
            Cv2.FindContours(
                binaryMask,
                out Point[][] contours,
                out _,
                RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);

            return contours
                .Where(contour => contour != null && contour.Length > 0)
                .OrderByDescending(contour => Math.Abs(Cv2.ContourArea(contour)))
                .FirstOrDefault();
        }

        private static double LogoCandidateDensityScore(Mat candidateMask)
        {
            double density = Cv2.CountNonZero(candidateMask) / (double)Math.Max(1, candidateMask.Width * candidateMask.Height);
            if (density < 0.030 || density > 0.58)
                return 0.25;
            return 1.0 - Math.Min(1.0, Math.Abs(density - 0.22) / 0.35);
        }

        private static Mat ResizeBinaryMask(Mat source, int width, int height)
        {
            var resized = new Mat();
            Cv2.Resize(source, resized, new Size(width, height), 0, 0, InterpolationFlags.Nearest);
            Cv2.Threshold(resized, resized, 0, 255, ThresholdTypes.Binary);
            return resized;
        }

        private static Mat ResizeUnitBinaryMask(Mat source, int width, int height)
        {
            var resized = new Mat();
            if (source == null || source.Empty())
                return resized;

            Cv2.Resize(source, resized, new Size(width, height), 0, 0, InterpolationFlags.Nearest);
            Cv2.Threshold(resized, resized, 0, 1, ThresholdTypes.Binary);
            return resized;
        }

        private static Mat ResizeGrayImage(Mat source, int width, int height)
        {
            var resized = new Mat();
            if (source == null || source.Empty())
                return resized;

            Cv2.Resize(source, resized, new Size(width, height), 0, 0, InterpolationFlags.Area);
            return resized;
        }

        private static int SumIntegral(Mat integral, int x, int y, int width, int height)
        {
            int x2 = Math.Min(integral.Width - 1, x + width);
            int y2 = Math.Min(integral.Height - 1, y + height);
            if (x < 0 || y < 0 || x2 <= x || y2 <= y)
                return 0;

            return integral.At<int>(y2, x2) -
                integral.At<int>(y, x2) -
                integral.At<int>(y2, x) +
                integral.At<int>(y, x);
        }

        private static void AddLogoHoughShapeCandidates(
            Mat searchMask,
            Mat fullTargetMask,
            Mat reference,
            List<StepTextLogoDetectionRegion> detections)
        {
            try
            {
                using (Mat referenceEdges = BuildHoughEdgeMask(reference))
                using (Mat targetEdges = BuildHoughEdgeMask(searchMask))
                using (var hough = GeneralizedHoughGuil.Create())
                using (var positions = new Mat())
                using (var votes = new Mat())
                {
                    hough.MinDist = Math.Max(24.0, Math.Min(reference.Width, reference.Height) * 0.45);
                    hough.Dp = 2.0;
                    hough.CannyLowThresh = 20;
                    hough.CannyHighThresh = 80;
                    hough.MinAngle = 0.0;
                    hough.MaxAngle = 360.0;
                    hough.AngleStep = 90.0;
                    hough.MinScale = 0.55;
                    hough.MaxScale = 1.90;
                    hough.ScaleStep = 0.12;
                    hough.Xi = 90.0;
                    hough.Levels = 360;
                    hough.AngleThresh = 20;
                    hough.ScaleThresh = 16;
                    hough.PosThresh = 18;

                    hough.SetTemplate(referenceEdges, new Point(referenceEdges.Width / 2, referenceEdges.Height / 2));
                    hough.Detect(targetEdges, positions, votes);

                    foreach (LogoHoughCandidate candidate in ReadLogoHoughCandidates(positions).Take(24))
                    {
                        TryAddVerifiedHoughLogoCandidate(
                            searchMask,
                            fullTargetMask,
                            reference,
                            candidate,
                            detections);
                    }
                }
            }
            catch (OpenCVException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        private static Mat BuildHoughEdgeMask(Mat binaryMask)
        {
            var edges = new Mat();
            Cv2.Canny(binaryMask, edges, 30, 90);
            if (Cv2.CountNonZero(edges) == 0)
                return binaryMask.Clone();
            return edges;
        }

        private static IEnumerable<LogoHoughCandidate> ReadLogoHoughCandidates(Mat positions)
        {
            if (positions == null || positions.Empty())
                yield break;

            for (int y = 0; y < positions.Rows; y++)
            {
                for (int x = 0; x < positions.Cols; x++)
                {
                    LogoHoughCandidate candidate;
                    if (positions.Type() == MatType.CV_32FC4)
                    {
                        Vec4f value = positions.At<Vec4f>(y, x);
                        candidate = new LogoHoughCandidate
                        {
                            CenterX = value.Item0,
                            CenterY = value.Item1,
                            Scale = value.Item2 <= 0 ? 1.0 : value.Item2,
                            Angle = value.Item3
                        };
                    }
                    else if (positions.Type() == MatType.CV_32FC3)
                    {
                        Vec3f value = positions.At<Vec3f>(y, x);
                        candidate = new LogoHoughCandidate
                        {
                            CenterX = value.Item0,
                            CenterY = value.Item1,
                            Scale = value.Item2 <= 0 ? 1.0 : value.Item2,
                            Angle = 0.0
                        };
                    }
                    else if (positions.Type() == MatType.CV_32FC2)
                    {
                        Vec2f value = positions.At<Vec2f>(y, x);
                        candidate = new LogoHoughCandidate
                        {
                            CenterX = value.Item0,
                            CenterY = value.Item1,
                            Scale = 1.0,
                            Angle = 0.0
                        };
                    }
                    else
                    {
                        continue;
                    }

                    if (candidate.CenterX >= 0 && candidate.CenterY >= 0)
                        yield return candidate;
                }
            }
        }

        private static void TryAddVerifiedHoughLogoCandidate(
            Mat searchMask,
            Mat fullTargetMask,
            Mat reference,
            LogoHoughCandidate candidate,
            List<StepTextLogoDetectionRegion> detections)
        {
            using (Mat oriented = BuildRightAngleLogoReference(reference, candidate.Angle))
            {
                int width = (int)Math.Round(oriented.Width * candidate.Scale, MidpointRounding.AwayFromZero);
                int height = (int)Math.Round(oriented.Height * candidate.Scale, MidpointRounding.AwayFromZero);
                if (width < MinimumLogoWidthPixels / 2 || height < MinimumLogoHeightPixels / 2)
                    return;

                Rect bounds = ClipRect(
                    new Rect(
                        (int)Math.Round(candidate.CenterX - width / 2.0, MidpointRounding.AwayFromZero),
                        (int)Math.Round(candidate.CenterY - height / 2.0, MidpointRounding.AwayFromZero),
                        width,
                        height),
                    searchMask.Width,
                    searchMask.Height);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return;

                using (Mat template = ResizeBinaryMask(oriented, bounds.Width, bounds.Height))
                using (Mat roi = new Mat(searchMask, bounds))
                {
                    LogoShapeScore score = ScoreLogoShapeCandidate(roi, template, correlation: 0.42);
                    if (score.Score < 0.34)
                        return;

                    Rect fullBounds = ScaleRect(bounds, 2.0, fullTargetMask.Width, fullTargetMask.Height);
                    Rect tight = TightForegroundBounds(fullTargetMask, fullBounds);
                    if (tight.Width < MinimumLogoWidthPixels || tight.Height < MinimumLogoHeightPixels)
                        tight = fullBounds;
                    tight = ExpandRect(tight, fullTargetMask.Width, fullTargetMask.Height, Math.Max(3, Math.Min(10, Math.Max(tight.Width, tight.Height) / 32)));

                    detections.Add(new StepTextLogoDetectionRegion
                    {
                        TemplateName = "easyeda-logo-hough",
                        Kind = "logo",
                        Text = "",
                        X = tight.X,
                        Y = tight.Y,
                        Width = tight.Width,
                        Height = tight.Height,
                        Score = Math.Round(Math.Min(100.0, score.Score * 100.0 + 7.5), 3),
                        ChamferDistance = score.ChamferDistance,
                        EdgePixelCount = CountNonZero(fullTargetMask, tight)
                    });
                }
            }
        }

        private static Mat BuildRightAngleLogoReference(Mat reference, double angle)
        {
            double normalized = ((angle % 360.0) + 360.0) % 360.0;
            int quadrant = (int)Math.Round(normalized / 90.0, MidpointRounding.AwayFromZero) % 4;
            var result = new Mat();
            if (quadrant == 1)
                Cv2.Rotate(reference, result, RotateFlags.Rotate90Clockwise);
            else if (quadrant == 2)
                Cv2.Rotate(reference, result, RotateFlags.Rotate180);
            else if (quadrant == 3)
                Cv2.Rotate(reference, result, RotateFlags.Rotate90Counterclockwise);
            else
                result = reference.Clone();

            return result;
        }

        private static void AddLogoShapeMatchPeaks(
            Mat searchMask,
            Mat fullTargetMask,
            Mat template,
            Mat result,
            List<StepTextLogoDetectionRegion> detections)
        {
            using (Mat mutableResult = result.Clone())
            {
                for (int peak = 0; peak < 4; peak++)
                {
                    Cv2.MinMaxLoc(mutableResult, out _, out double correlation, out _, out Point location);
                    if (correlation < 0.30)
                        break;

                    Rect bounds = ClipRect(new Rect(location.X, location.Y, template.Width, template.Height), searchMask.Width, searchMask.Height);
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                        break;

                    using (Mat roi = new Mat(searchMask, bounds))
                    {
                        LogoShapeScore score = ScoreLogoShapeCandidate(roi, template, correlation);
                        if (score.Score >= 0.38)
                        {
                            Rect fullBounds = ScaleRect(bounds, 2.0, fullTargetMask.Width, fullTargetMask.Height);
                            Rect tight = TightForegroundBounds(fullTargetMask, fullBounds);
                            if (tight.Width < MinimumLogoWidthPixels || tight.Height < MinimumLogoHeightPixels)
                                tight = fullBounds;
                            tight = ExpandRect(tight, fullTargetMask.Width, fullTargetMask.Height, Math.Max(3, Math.Min(10, Math.Max(tight.Width, tight.Height) / 32)));

                            detections.Add(new StepTextLogoDetectionRegion
                            {
                                TemplateName = "easyeda-logo-shape",
                                Kind = "logo",
                                Text = "",
                                X = tight.X,
                                Y = tight.Y,
                                Width = tight.Width,
                                Height = tight.Height,
                                Score = Math.Round(score.Score * 100.0, 3),
                                ChamferDistance = score.ChamferDistance,
                                EdgePixelCount = CountNonZero(fullTargetMask, tight)
                            });
                        }
                    }

                    SuppressTemplateResultPeak(mutableResult, location, template.Width, template.Height);
                }
            }
        }

        private static Rect ScaleRect(Rect rect, double scale, int imageWidth, int imageHeight)
        {
            int left = (int)Math.Floor(rect.X * scale);
            int top = (int)Math.Floor(rect.Y * scale);
            int right = (int)Math.Ceiling((rect.X + rect.Width) * scale);
            int bottom = (int)Math.Ceiling((rect.Y + rect.Height) * scale);
            return ClipRect(new Rect(left, top, right - left, bottom - top), imageWidth, imageHeight);
        }

        private static LogoShapeScore ScoreLogoShapeCandidate(Mat roiMask, Mat templateMask, double correlation)
        {
            using (Mat overlapMask = new Mat())
            {
                Cv2.BitwiseAnd(roiMask, templateMask, overlapMask);
                int overlap = Cv2.CountNonZero(overlapMask);
                int targetPixels = Math.Max(1, Cv2.CountNonZero(roiMask));
                int templatePixels = Math.Max(1, Cv2.CountNonZero(templateMask));
                double precision = overlap / (double)targetPixels;
                double recall = overlap / (double)templatePixels;
                double f1 = precision + recall <= 0.0 ? 0.0 : 2.0 * precision * recall / (precision + recall);
                double chamfer = EstimateChamferDistance(roiMask, templateMask);
                double reverseChamfer = EstimateChamferDistance(templateMask, roiMask);
                double chamferScore = 1.0 - Math.Min(1.0, Math.Min(chamfer, reverseChamfer) / 18.0);
                double xProjection = ProjectionSimilarity(roiMask, templateMask, byColumn: true);
                double yProjection = ProjectionSimilarity(roiMask, templateMask, byColumn: false);
                double score =
                    0.28 * Math.Max(0.0, correlation) +
                    0.34 * f1 +
                    0.18 * recall +
                    0.12 * chamferScore +
                    0.08 * ((xProjection + yProjection) / 2.0);

                if (precision < 0.10 || recall < 0.24)
                    score *= 0.55;

                return new LogoShapeScore
                {
                    Score = score,
                    ChamferDistance = Math.Min(chamfer, reverseChamfer)
                };
            }
        }

        private static Mat BuildLogoShapeTargetMask(StepProjectionImage colorImage)
        {
            using (Mat bgra = StepProjectionImageOpenCv.ToBgraMat(colorImage))
            using (var gray = new Mat())
            using (var nonBackground = new Mat())
            using (var blurred = new Mat())
            using (var darkContrast = new Mat())
            using (var darkAbsolute = new Mat())
            {
                Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
                Cv2.Threshold(gray, nonBackground, 244, 255, ThresholdTypes.BinaryInv);
                Cv2.GaussianBlur(gray, blurred, new Size(0, 0), 13.0);
                Cv2.Subtract(blurred, gray, darkContrast);
                Cv2.Threshold(darkContrast, darkContrast, 8, 255, ThresholdTypes.Binary);
                Cv2.Threshold(gray, darkAbsolute, 138, 255, ThresholdTypes.BinaryInv);
                Cv2.BitwiseOr(darkContrast, darkAbsolute, darkContrast);
                Cv2.BitwiseAnd(darkContrast, nonBackground, darkContrast);
                Cv2.MorphologyEx(
                    darkContrast,
                    darkContrast,
                    MorphTypes.Close,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));
                Cv2.MorphologyEx(
                    darkContrast,
                    darkContrast,
                    MorphTypes.Open,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2)));
                return darkContrast.Clone();
            }
        }

        private static Mat BuildLogoInkMask(StepProjectionImage colorImage)
        {
            using (Mat bgra = StepProjectionImageOpenCv.ToBgraMat(colorImage))
            using (var gray = new Mat())
            using (var closed = new Mat())
            using (var blackHat = new Mat())
            using (var darkAbsolute = new Mat())
            {
                Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
                Cv2.MorphologyEx(
                    gray,
                    closed,
                    MorphTypes.Close,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(45, 45)));
                Cv2.Subtract(closed, gray, blackHat);
                Cv2.Threshold(blackHat, blackHat, 16, 1, ThresholdTypes.Binary);
                Cv2.Threshold(gray, darkAbsolute, 110, 1, ThresholdTypes.BinaryInv);
                Cv2.BitwiseOr(blackHat, darkAbsolute, blackHat);
                Cv2.MorphologyEx(
                    blackHat,
                    blackHat,
                    MorphTypes.Open,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2)));
                return blackHat.Clone();
            }
        }

        private static List<StepTextLogoDetectionRegion> FindLogoFeatureMatches(
            Mat targetImage,
            StepTextLogoDetectionOptions options,
            bool binaryReference,
            string sourceName)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            using (Mat reference = LoadLogoReferenceMask(options, binaryReference))
            {
                if (reference == null || reference.Empty())
                    return detections;

                using (var orb = ORB.Create(750))
                using (var referenceDescriptors = new Mat())
                using (var targetDescriptors = new Mat())
                {
                    orb.DetectAndCompute(reference, null, out KeyPoint[] referenceKeypoints, referenceDescriptors);
                    orb.DetectAndCompute(targetImage, null, out KeyPoint[] targetKeypoints, targetDescriptors);
                    if (referenceKeypoints == null || targetKeypoints == null ||
                        referenceKeypoints.Length < 8 || targetKeypoints.Length < 8 ||
                        referenceDescriptors.Empty() || targetDescriptors.Empty())
                    {
                        return detections;
                    }

                    using (var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false))
                    {
                        DMatch[][] knn = matcher.KnnMatch(referenceDescriptors, targetDescriptors, 2);
                        var good = new List<DMatch>();
                        foreach (DMatch[] pair in knn)
                        {
                            if (pair == null || pair.Length < 2)
                                continue;
                            if (pair[0].Distance < 0.78 * pair[1].Distance)
                                good.Add(pair[0]);
                        }

                        if (good.Count < 8)
                            return detections;

                        Point2f[] referencePoints = good.Select(match => referenceKeypoints[match.QueryIdx].Pt).ToArray();
                        Point2f[] targetPoints = good.Select(match => targetKeypoints[match.TrainIdx].Pt).ToArray();
                        using (Mat inlierMask = new Mat())
                        using (Mat homography = Cv2.FindHomography(
                            InputArray.Create(referencePoints),
                            InputArray.Create(targetPoints),
                            HomographyMethods.Ransac,
                            4.0,
                            inlierMask))
                        {
                            if (homography == null || homography.Empty())
                                return detections;

                            int inliers = Cv2.CountNonZero(inlierMask);
                            if (inliers < 7)
                                return detections;

                            Point2f[] corners =
                            {
                                new Point2f(0, 0),
                                new Point2f(reference.Width - 1, 0),
                                new Point2f(reference.Width - 1, reference.Height - 1),
                                new Point2f(0, reference.Height - 1)
                            };
                            Point2f[] projected = Cv2.PerspectiveTransform(corners, homography);
                            Rect bounds = BoundsForPoints(projected, targetImage.Width, targetImage.Height);
                            if (bounds.Width < options.MinimumRegionWidth || bounds.Height < options.MinimumRegionHeight)
                                return detections;
                            if (bounds.Width > targetImage.Width * 0.55 || bounds.Height > targetImage.Height * 0.40)
                                return detections;

                            detections.Add(new StepTextLogoDetectionRegion
                            {
                                TemplateName = "easyeda-logo-orb-" + sourceName,
                                Kind = "logo",
                                Text = "",
                                X = bounds.X,
                                Y = bounds.Y,
                                Width = bounds.Width,
                                Height = bounds.Height,
                                Score = Math.Round(Math.Min(100.0, 45.0 + inliers * 3.0), 3),
                                ChamferDistance = Math.Max(0.0, 8.0 - inliers * 0.4),
                                EdgePixelCount = CountNonZero(targetImage, bounds)
                            });
                        }
                    }
                }
            }

            return detections;
        }

        private static List<StepTextLogoDetectionRegion> FindLogoGrayscaleTemplateMatches(
            Mat targetGray,
            StepTextLogoDetectionOptions options)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            using (Mat reference = LoadLogoReferenceGray(options))
            {
                if (targetGray == null || targetGray.Empty() || reference == null || reference.Empty())
                    return detections;

                Cv2.EqualizeHist(reference, reference);
                foreach (Mat orientedReference in BuildLogoReferenceOrientations(reference))
                {
                    using (orientedReference)
                    {
                        foreach (double scale in BuildLogoInkMaskTemplateScales())
                        {
                            int width = (int)Math.Round(orientedReference.Width * scale, MidpointRounding.AwayFromZero);
                            int height = (int)Math.Round(orientedReference.Height * scale, MidpointRounding.AwayFromZero);
                            if (width < MinimumLogoWidthPixels / 2 ||
                                height < MinimumLogoHeightPixels / 2 ||
                                width >= targetGray.Width ||
                                height >= targetGray.Height)
                            {
                                continue;
                            }

                            using (var template = new Mat())
                            using (var result = new Mat())
                            {
                                Cv2.Resize(orientedReference, template, new Size(width, height), 0, 0, InterpolationFlags.Area);
                                Cv2.MatchTemplate(targetGray, template, result, TemplateMatchModes.CCoeffNormed);
                                AddLogoGrayscaleTemplatePeaks(targetGray, result, template.Width, template.Height, detections);
                            }
                        }
                    }
                }
            }

            return SuppressOverlappingDetections(detections, maxCount: 6);
        }

        private static List<StepTextLogoDetectionRegion> FindLogoEdgeProjectionMatches(
            Mat edgeMask,
            StepTextLogoDetectionOptions options)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            if (edgeMask == null || edgeMask.Empty())
                return detections;

            using (Mat referenceMask = LoadLogoReferenceMask(options, binaryReference: true))
            using (Mat referenceEdge = BuildLogoReferenceEdgeMask(referenceMask))
            using (Mat searchEdge = ResizeUnitBinaryMask(edgeMask, Math.Max(1, edgeMask.Width / 2), Math.Max(1, edgeMask.Height / 2)))
            using (var targetIntegral = new Mat())
            {
                if (referenceEdge.Empty() || searchEdge.Empty())
                    return detections;

                Cv2.Integral(searchEdge, targetIntegral, MatType.CV_32S);
                foreach (Mat orientedReference in BuildLogoReferenceOrientations(referenceEdge))
                {
                    using (orientedReference)
                    {
                        foreach (double scale in BuildLogoInkMaskTemplateScales())
                        {
                            int width = (int)Math.Round(orientedReference.Width * scale / 2.0, MidpointRounding.AwayFromZero);
                            int height = (int)Math.Round(orientedReference.Height * scale / 2.0, MidpointRounding.AwayFromZero);
                            if (width < MinimumLogoWidthPixels / 4 ||
                                height < MinimumLogoHeightPixels / 4 ||
                                width >= searchEdge.Width ||
                                height >= searchEdge.Height)
                            {
                                continue;
                            }

                            using (Mat template = ResizeUnitBinaryMask(orientedReference, width, height))
                            using (var overlap = new Mat())
                            {
                                int templatePixels = Cv2.CountNonZero(template);
                                if (templatePixels < 24)
                                    continue;

                                Cv2.MatchTemplate(searchEdge, template, overlap, TemplateMatchModes.CCorr);
                                AddLogoEdgeProjectionPeaks(
                                    searchEdge,
                                    edgeMask,
                                    targetIntegral,
                                    template,
                                    overlap,
                                    templatePixels,
                                    detections);
                            }
                        }
                    }
                }
            }

            return SuppressOverlappingDetections(detections, maxCount: 6);
        }

        private static Mat BuildLogoReferenceEdgeMask(Mat referenceMask)
        {
            if (referenceMask == null || referenceMask.Empty())
                return new Mat();

            Rect tight = TightForegroundBounds(referenceMask, new Rect(0, 0, referenceMask.Width, referenceMask.Height));
            if (tight.Width <= 0 || tight.Height <= 0)
                return new Mat();

            using (Mat cropped = new Mat(referenceMask, tight))
            {
                var edge = new Mat();
                Cv2.MorphologyEx(
                    cropped,
                    edge,
                    MorphTypes.Gradient,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));
                Cv2.Threshold(edge, edge, 0, 1, ThresholdTypes.Binary);
                return edge;
            }
        }

        private static void AddLogoEdgeProjectionPeaks(
            Mat searchEdge,
            Mat fullEdgeMask,
            Mat targetIntegral,
            Mat template,
            Mat overlap,
            int templatePixels,
            List<StepTextLogoDetectionRegion> detections)
        {
            using (Mat mutableScore = new Mat(overlap.Size(), MatType.CV_32FC1, Scalar.All(0)))
            {
                int rows = overlap.Rows;
                int cols = overlap.Cols;
                for (int y = 0; y < rows; y += 2)
                {
                    for (int x = 0; x < cols; x += 2)
                    {
                        int targetPixels = SumIntegral(targetIntegral, x, y, template.Width, template.Height);
                        if (targetPixels < 16)
                            continue;

                        double density = targetPixels / (double)Math.Max(1, template.Width * template.Height);
                        if (density > 0.28)
                            continue;

                        double hits = overlap.At<float>(y, x);
                        double score = 2.0 * hits / Math.Max(1.0, templatePixels + targetPixels);
                        if (score >= 0.34)
                            mutableScore.Set(y, x, (float)score);
                    }
                }

                for (int peak = 0; peak < 4; peak++)
                {
                    Cv2.MinMaxLoc(mutableScore, out _, out double score, out _, out Point location);
                    if (score < 0.34)
                        break;

                    Rect searchBounds = ClipRect(new Rect(location.X, location.Y, template.Width, template.Height), searchEdge.Width, searchEdge.Height);
                    Rect fullBounds = ScaleRect(searchBounds, 2.0, fullEdgeMask.Width, fullEdgeMask.Height);
                    if (LooksLikePlausibleLogoBounds(fullBounds, fullEdgeMask.Width, fullEdgeMask.Height) &&
                        !detections.Any(existing => IntersectionOverUnion(existing, fullBounds) > 0.45))
                    {
                        detections.Add(new StepTextLogoDetectionRegion
                        {
                            TemplateName = "easyeda-logo-edge-projection",
                            Kind = "logo",
                            Text = "",
                            X = fullBounds.X,
                            Y = fullBounds.Y,
                            Width = fullBounds.Width,
                            Height = fullBounds.Height,
                            Score = Math.Round(score * 100.0, 3),
                            ChamferDistance = Math.Max(0.0, 10.0 - score * 10.0),
                            EdgePixelCount = CountNonZero(fullEdgeMask, fullBounds)
                        });
                    }

                    SuppressTemplateResultPeak(mutableScore, location, template.Width, template.Height);
                }
            }
        }

        private static void AddLogoGrayscaleTemplatePeaks(
            Mat targetGray,
            Mat result,
            int templateWidth,
            int templateHeight,
            List<StepTextLogoDetectionRegion> detections)
        {
            using (Mat mutableResult = result.Clone())
            {
                for (int peak = 0; peak < 4; peak++)
                {
                    Cv2.MinMaxLoc(mutableResult, out _, out double score, out _, out Point location);
                    if (score < 0.48)
                        break;

                    Rect bounds = ClipRect(
                        new Rect(location.X, location.Y, templateWidth, templateHeight),
                        targetGray.Width,
                        targetGray.Height);
                    if (LooksLikePlausibleLogoBounds(bounds, targetGray.Width, targetGray.Height) &&
                        LooksLikeFlatLogoSurface(targetGray, bounds) &&
                        !detections.Any(existing => IntersectionOverUnion(existing, bounds) > 0.45))
                    {
                        detections.Add(new StepTextLogoDetectionRegion
                        {
                            TemplateName = "easyeda-logo-grayscale-template",
                            Kind = "logo",
                            Text = "",
                            X = bounds.X,
                            Y = bounds.Y,
                            Width = bounds.Width,
                            Height = bounds.Height,
                            Score = Math.Round(score * 100.0, 3),
                            ChamferDistance = Math.Max(0.0, 10.0 - score * 10.0),
                            EdgePixelCount = CountNonZero(targetGray, bounds)
                        });
                    }

                    SuppressTemplateResultPeak(mutableResult, location, templateWidth, templateHeight);
                }
            }
        }

        private static bool LooksLikeFlatLogoSurface(Mat targetGray, Rect bounds)
        {
            Rect clipped = ClipRect(bounds, targetGray.Width, targetGray.Height);
            if (clipped.Width <= 0 || clipped.Height <= 0)
                return false;

            using (Mat roi = new Mat(targetGray, clipped))
            using (var dark = new Mat())
            using (var light = new Mat())
            {
                Cv2.MeanStdDev(roi, out _, out Scalar stddev);
                if (stddev.Val0 > 58.0)
                    return false;

                Cv2.Threshold(roi, dark, 70, 255, ThresholdTypes.BinaryInv);
                Cv2.Threshold(roi, light, 185, 255, ThresholdTypes.Binary);
                double area = Math.Max(1.0, clipped.Width * clipped.Height);
                double darkRatio = Cv2.CountNonZero(dark) / area;
                double lightRatio = Cv2.CountNonZero(light) / area;
                return !(darkRatio > 0.25 && lightRatio > 0.25);
            }
        }

        private static List<StepTextLogoDetectionRegion> FindLogoSiftFeatureMatches(
            Mat targetImage,
            StepTextLogoDetectionOptions options,
            string sourceName)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            using (Mat reference = LoadLogoReferenceMask(options, binaryReference: false))
            {
                if (reference == null || reference.Empty())
                    return detections;

                using (var sift = SIFT.Create(1200))
                using (var targetDescriptors = new Mat())
                {
                    sift.DetectAndCompute(targetImage, null, out KeyPoint[] targetKeypoints, targetDescriptors);
                    if (targetKeypoints == null ||
                        targetKeypoints.Length < 8 ||
                        targetDescriptors.Empty())
                    {
                        return detections;
                    }

                    foreach (Mat orientedReference in BuildLogoReferenceOrientations(reference))
                    {
                        using (orientedReference)
                        using (var referenceDescriptors = new Mat())
                        {
                            sift.DetectAndCompute(orientedReference, null, out KeyPoint[] referenceKeypoints, referenceDescriptors);
                            if (referenceKeypoints == null ||
                                referenceKeypoints.Length < 6 ||
                                referenceDescriptors.Empty())
                            {
                                continue;
                            }

                            using (var matcher = new BFMatcher(NormTypes.L2, crossCheck: false))
                            {
                                DMatch[][] knn = matcher.KnnMatch(referenceDescriptors, targetDescriptors, 2);
                                var good = new List<DMatch>();
                                foreach (DMatch[] pair in knn)
                                {
                                    if (pair == null || pair.Length < 2)
                                        continue;
                                    if (pair[0].Distance < 0.72 * pair[1].Distance)
                                        good.Add(pair[0]);
                                }

                                if (good.Count < 6)
                                    continue;

                                StepTextLogoDetectionRegion detection = TryCreateFeatureLogoDetection(
                                    orientedReference,
                                    targetImage,
                                    referenceKeypoints,
                                    targetKeypoints,
                                    good,
                                    minimumInliers: 5,
                                    templateName: "easyeda-logo-sift-" + sourceName,
                                    scoreBase: 48.0,
                                    scorePerInlier: 4.0);
                                if (detection != null)
                                    detections.Add(detection);
                            }
                        }
                    }
                }
            }

            return SuppressOverlappingDetections(detections, maxCount: 4);
        }

        private static StepTextLogoDetectionRegion TryCreateFeatureLogoDetection(
            Mat reference,
            Mat targetImage,
            KeyPoint[] referenceKeypoints,
            KeyPoint[] targetKeypoints,
            IReadOnlyList<DMatch> matches,
            int minimumInliers,
            string templateName,
            double scoreBase,
            double scorePerInlier)
        {
            if (matches == null || matches.Count < Math.Max(4, minimumInliers))
                return null;

            Point2f[] referencePoints = matches.Select(match => referenceKeypoints[match.QueryIdx].Pt).ToArray();
            Point2f[] targetPoints = matches.Select(match => targetKeypoints[match.TrainIdx].Pt).ToArray();
            using (Mat inlierMask = new Mat())
            using (Mat homography = Cv2.FindHomography(
                InputArray.Create(referencePoints),
                InputArray.Create(targetPoints),
                HomographyMethods.Ransac,
                4.0,
                inlierMask))
            {
                if (homography == null || homography.Empty())
                    return null;

                int inliers = Cv2.CountNonZero(inlierMask);
                if (inliers < minimumInliers)
                    return null;

                Point2f[] corners =
                {
                    new Point2f(0, 0),
                    new Point2f(reference.Width - 1, 0),
                    new Point2f(reference.Width - 1, reference.Height - 1),
                    new Point2f(0, reference.Height - 1)
                };
                Point2f[] projected = Cv2.PerspectiveTransform(corners, homography);
                Rect bounds = BoundsForPoints(projected, targetImage.Width, targetImage.Height);
                if (bounds.Width < 24 || bounds.Height < 18)
                    return null;
                if (!LooksLikePlausibleLogoBounds(bounds, targetImage.Width, targetImage.Height))
                    return null;

                return new StepTextLogoDetectionRegion
                {
                    TemplateName = templateName,
                    Kind = "logo",
                    Text = "",
                    X = bounds.X,
                    Y = bounds.Y,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Score = Math.Round(Math.Min(100.0, scoreBase + inliers * scorePerInlier), 3),
                    ChamferDistance = Math.Max(0.0, 8.0 - inliers * 0.4),
                    EdgePixelCount = CountNonZero(targetImage, bounds)
                };
            }
        }

        private static Mat LoadLogoReferenceMask(StepTextLogoDetectionOptions options, bool binaryReference)
        {
            if (options != null &&
                !string.IsNullOrWhiteSpace(options.LogoReferenceImagePath) &&
                System.IO.File.Exists(options.LogoReferenceImagePath))
            {
                Mat reference = Cv2.ImRead(options.LogoReferenceImagePath, ImreadModes.Grayscale);
                if (reference.Empty())
                    return reference;

                if (binaryReference)
                    Cv2.Threshold(reference, reference, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
                else
                    Cv2.EqualizeHist(reference, reference);
                return reference;
            }

            StepWatermarkTemplate logo = StepWatermarkTemplateLibrary.GetKnownTemplates()
                .FirstOrDefault(template => string.Equals(template.Kind, "logo", StringComparison.OrdinalIgnoreCase));
            if (logo == null)
                return new Mat();

            return BuildTemplateMask(logo, 96, 96, 0);
        }

        private static Mat LoadLogoReferenceGray(StepTextLogoDetectionOptions options)
        {
            if (options != null &&
                !string.IsNullOrWhiteSpace(options.LogoReferenceImagePath) &&
                System.IO.File.Exists(options.LogoReferenceImagePath))
            {
                return Cv2.ImRead(options.LogoReferenceImagePath, ImreadModes.Grayscale);
            }

            StepWatermarkTemplate logo = StepWatermarkTemplateLibrary.GetKnownTemplates()
                .FirstOrDefault(template => string.Equals(template.Kind, "logo", StringComparison.OrdinalIgnoreCase));
            if (logo == null)
                return new Mat();

            return BuildTemplateMask(logo, 96, 96, 0);
        }

        private static Mat BuildLogoRawGrayTarget(StepProjectionImage colorImage)
        {
            return StepProjectionImageOpenCv.ToGrayMat(colorImage);
        }

        private static Mat BuildLogoGrayscaleTarget(StepProjectionImage colorImage)
        {
            Mat gray = StepProjectionImageOpenCv.ToGrayMat(colorImage);
            Cv2.EqualizeHist(gray, gray);
            return gray;
        }

        private static Rect BoundsForPoints(Point2f[] points, int imageWidth, int imageHeight)
        {
            if (points == null || points.Length == 0)
                return new Rect();

            int left = (int)Math.Floor(points.Min(point => point.X));
            int top = (int)Math.Floor(points.Min(point => point.Y));
            int right = (int)Math.Ceiling(points.Max(point => point.X));
            int bottom = (int)Math.Ceiling(points.Max(point => point.Y));
            return ExpandRect(
                ClipRect(new Rect(left, top, Math.Max(1, right - left + 1), Math.Max(1, bottom - top + 1)), imageWidth, imageHeight),
                imageWidth,
                imageHeight,
                4);
        }

        private static Mat BuildSilhouetteFeatureMask(Mat edgeMask)
        {
            var cleaned = new Mat();
            using (var horizontal = new Mat())
            using (var vertical = new Mat())
            using (var horizontalMechanicalLines = new Mat())
            using (var verticalMechanicalLines = new Mat())
            using (var longLines = new Mat())
            using (var inverseLongLines = new Mat())
            {
                Cv2.MorphologyEx(
                    edgeMask,
                    horizontal,
                    MorphTypes.Open,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(55, 1)));
                Cv2.MorphologyEx(
                    edgeMask,
                    vertical,
                    MorphTypes.Open,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, 55)));
                KeepMechanicalLineContours(horizontal, horizontalMechanicalLines, horizontalLines: true);
                KeepMechanicalLineContours(vertical, verticalMechanicalLines, horizontalLines: false);
                Cv2.BitwiseOr(horizontalMechanicalLines, verticalMechanicalLines, longLines);
                Cv2.Dilate(longLines, longLines, Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));
                Cv2.BitwiseNot(longLines, inverseLongLines);
                Cv2.BitwiseAnd(edgeMask, inverseLongLines, cleaned);
                Cv2.MorphologyEx(
                    cleaned,
                    cleaned,
                    MorphTypes.Close,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2)));
            }

            return cleaned;
        }

        private static void KeepMechanicalLineContours(Mat source, Mat destination, bool horizontalLines)
        {
            destination.Create(source.Size(), MatType.CV_8UC1);
            destination.SetTo(Scalar.Black);
            Cv2.FindContours(
                source,
                out Point[][] contours,
                out _,
                RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);
            foreach (Point[] contour in contours)
            {
                if (contour == null || contour.Length == 0)
                    continue;

                Rect bounds = Cv2.BoundingRect(contour);
                int longDimension = horizontalLines ? bounds.Width : bounds.Height;
                int shortDimension = horizontalLines ? bounds.Height : bounds.Width;
                if (longDimension < MinimumMechanicalLineLengthPixels)
                    continue;
                if (shortDimension > 12)
                    continue;

                Cv2.DrawContours(destination, new[] { contour }, -1, Scalar.White, -1);
            }
        }

        private static List<StepTextLogoDetectionRegion> FindKnownSilhouetteObjects(
            Mat silhouetteMask,
            StepTextLogoDetectionOptions options)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            foreach (VisualRoi roi in ExtractSilhouetteTextRois(silhouetteMask, options))
            {
                using (Mat roiMask = new Mat(silhouetteMask, roi.Bounds))
                {
                    List<ComponentBox> components = ExtractComponents(roiMask);
                    if (LooksLikeRegularPinArray(components, roi.Bounds) ||
                        LooksLikeLongStraightSeam(roiMask, roi.Bounds) ||
                        LooksLikeSingleConnectorContact(components, roi.Bounds))
                    {
                        continue;
                    }

                    TemplateScore best = FindBestKnownTemplate(roiMask, includeLogoTemplates: false);
                    if (best.Score < options.MinimumKnownTemplateScore)
                    {
                        TextShapeScore textShape = ScoreArbitraryTextRoi(roiMask, components);
                        best = FindBestGeometryTemplate(roiMask, textShape);
                        if (best.Score < options.MinimumKnownTemplateScore)
                            continue;
                    }

                    detections.Add(new StepTextLogoDetectionRegion
                    {
                        TemplateName = best.Template.Name,
                        Kind = best.Template.Kind,
                        Text = best.Template.Text,
                        X = roi.Bounds.X,
                        Y = roi.Bounds.Y,
                        Width = roi.Bounds.Width,
                        Height = roi.Bounds.Height,
                        Score = Math.Round(best.Score * 100.0, 3),
                        ChamferDistance = best.ChamferDistance,
                        EdgePixelCount = roi.EdgePixels
                    });
                }
            }

            if (detections.Count == 0)
                AddSlidingTemplateFallbacks(silhouetteMask, options, detections);
            return detections;
        }

        private static void AddSlidingTemplateFallbacks(
            Mat silhouetteMask,
            StepTextLogoDetectionOptions options,
            List<StepTextLogoDetectionRegion> detections)
        {
            foreach (StepWatermarkTemplate template in StepWatermarkTemplateLibrary.GetKnownTemplates())
            {
                if (string.Equals(template.Kind, "logo", StringComparison.OrdinalIgnoreCase))
                    continue;

                for (int rotation = 0; rotation < 360; rotation += 90)
                {
                    GetRotatedTemplateSize(template, rotation, out int sourceWidth, out int sourceHeight);
                    foreach (double scale in BuildTemplateScales())
                    {
                        int width = (int)Math.Round(sourceWidth * scale, MidpointRounding.AwayFromZero);
                        int height = (int)Math.Round(sourceHeight * scale, MidpointRounding.AwayFromZero);
                        if (width < options.MinimumRegionWidth || height < options.MinimumRegionHeight)
                            continue;
                        if (width >= silhouetteMask.Width || height >= silhouetteMask.Height)
                            continue;

                        using (Mat templateMask = BuildTemplateMask(template, width, height, rotation))
                        using (Mat templateSearchMask = new Mat())
                        using (Mat result = new Mat())
                        {
                            Cv2.Dilate(
                                templateMask,
                                templateSearchMask,
                                Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));
                            Cv2.MatchTemplate(silhouetteMask, templateSearchMask, result, TemplateMatchModes.CCorrNormed);
                            AddTemplateMatchPeaks(
                                silhouetteMask,
                                result,
                                template,
                                rotation,
                                width,
                                height,
                                options,
                                detections);
                        }
                    }
                }
            }
        }

        private static IEnumerable<double> BuildTemplateScales()
        {
            for (double scale = 0.35; scale <= 2.20; scale *= 1.08)
                yield return scale;
        }

        private static void AddTemplateMatchPeaks(
            Mat silhouetteMask,
            Mat result,
            StepWatermarkTemplate template,
            int rotation,
            int width,
            int height,
            StepTextLogoDetectionOptions options,
            List<StepTextLogoDetectionRegion> detections)
        {
            using (Mat mutableResult = result.Clone())
            {
                for (int peak = 0; peak < TemplateSearchPeaksPerScale; peak++)
                {
                    Cv2.MinMaxLoc(mutableResult, out _, out double templateMatchScore, out _, out Point location);
                    if (templateMatchScore < 0.18)
                        break;

                    Rect bounds = ClipRect(new Rect(location.X, location.Y, width, height), silhouetteMask.Width, silhouetteMask.Height);
                    if (bounds.Width <= 0 || bounds.Height <= 0)
                        break;

                    using (Mat roiMask = new Mat(silhouetteMask, bounds))
                    {
                        int foregroundPixels = Cv2.CountNonZero(roiMask);
                        if (foregroundPixels < options.MinimumEdgePixels)
                        {
                            SuppressTemplateResultPeak(mutableResult, location, width, height);
                            continue;
                        }

                        TemplateScore exact = ScoreKnownTemplate(roiMask, template, rotation);
                        double score = 0.62 * exact.Score + 0.38 * templateMatchScore;
                        score *= EdgeSupportFactor(template, foregroundPixels);
                        if (score >= options.MinimumKnownTemplateScore)
                        {
                            detections.Add(new StepTextLogoDetectionRegion
                            {
                                TemplateName = template.Name,
                                Kind = template.Kind,
                                Text = template.Text,
                                X = bounds.X,
                                Y = bounds.Y,
                                Width = bounds.Width,
                                Height = bounds.Height,
                                Score = Math.Round(score * 100.0, 3),
                                ChamferDistance = exact.ChamferDistance,
                                EdgePixelCount = foregroundPixels
                            });
                        }
                    }

                    SuppressTemplateResultPeak(mutableResult, location, width, height);
                }
            }
        }

        private static double EdgeSupportFactor(StepWatermarkTemplate template, int foregroundPixels)
        {
            int expectedMinimum;
            if (string.Equals(template.Name, "EasyEDA", StringComparison.OrdinalIgnoreCase))
                expectedMinimum = 110;
            else if (string.Equals(template.Name, "LCEDA", StringComparison.OrdinalIgnoreCase))
                expectedMinimum = 70;
            else
                expectedMinimum = 95;

            return Math.Min(1.0, Math.Max(0.45, foregroundPixels / (double)expectedMinimum));
        }

        private static void BoostClusteredWatermarkScores(List<StepTextLogoDetectionRegion> detections)
        {
            foreach (StepTextLogoDetectionRegion detection in detections)
            {
                int neighborKinds = detections
                    .Where(candidate => !ReferenceEquals(candidate, detection))
                    .Where(candidate => !string.Equals(candidate.TemplateName, detection.TemplateName, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => CentersAreNear(detection, candidate))
                    .Select(candidate => candidate.TemplateName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (neighborKinds == 0)
                    detection.Score *= 0.72;
                else
                    detection.Score += Math.Min(30.0, neighborKinds * 16.0);
            }
        }

        private static bool CentersAreNear(StepTextLogoDetectionRegion left, StepTextLogoDetectionRegion right)
        {
            double leftX = left.X + left.Width / 2.0;
            double leftY = left.Y + left.Height / 2.0;
            double rightX = right.X + right.Width / 2.0;
            double rightY = right.Y + right.Height / 2.0;
            double dx = Math.Abs(leftX - rightX);
            double dy = Math.Abs(leftY - rightY);
            double span = Math.Max(Math.Max(left.Width, left.Height), Math.Max(right.Width, right.Height));
            return dx <= Math.Max(80.0, span * 4.0) && dy <= Math.Max(60.0, span * 3.0);
        }

        private static List<StepTextLogoDetectionRegion> MergeClusteredWatermarkDetections(
            List<StepTextLogoDetectionRegion> detections,
            int imageWidth,
            int imageHeight)
        {
            if (detections == null || detections.Count <= 1)
                return detections ?? new List<StepTextLogoDetectionRegion>();

            var visited = new bool[detections.Count];
            var merged = new List<StepTextLogoDetectionRegion>();
            for (int index = 0; index < detections.Count; index++)
            {
                if (visited[index])
                    continue;

                var clusterIndexes = new List<int>();
                var pending = new Queue<int>();
                pending.Enqueue(index);
                visited[index] = true;
                while (pending.Count > 0)
                {
                    int current = pending.Dequeue();
                    clusterIndexes.Add(current);
                    for (int candidate = 0; candidate < detections.Count; candidate++)
                    {
                        if (visited[candidate])
                            continue;
                        if (!ShouldMergeWatermarkDetections(detections[current], detections[candidate], imageWidth, imageHeight))
                            continue;
                        visited[candidate] = true;
                        pending.Enqueue(candidate);
                    }
                }

                merged.Add(MergeWatermarkCluster(
                    clusterIndexes.Select(clusterIndex => detections[clusterIndex]).ToList(),
                    imageWidth,
                    imageHeight));
            }

            return merged;
        }

        private static bool ShouldMergeWatermarkDetections(
            StepTextLogoDetectionRegion left,
            StepTextLogoDetectionRegion right,
            int imageWidth,
            int imageHeight)
        {
            if (left == null || right == null)
                return false;
            if (IntersectionOverUnion(left, right) > 0.02)
                return true;
            if (!CentersAreNear(left, right))
                return false;

            int gapX = Math.Max(0, Math.Max(left.X, right.X) - Math.Min(left.X + left.Width, right.X + right.Width));
            int gapY = Math.Max(0, Math.Max(left.Y, right.Y) - Math.Min(left.Y + left.Height, right.Y + right.Height));
            int maxWidth = Math.Max(left.Width, right.Width);
            int maxHeight = Math.Max(left.Height, right.Height);
            int unionWidth = Math.Max(left.X + left.Width, right.X + right.Width) - Math.Min(left.X, right.X);
            int unionHeight = Math.Max(left.Y + left.Height, right.Y + right.Height) - Math.Min(left.Y, right.Y);
            if (unionWidth > imageWidth * 0.48 || unionHeight > imageHeight * 0.34)
                return false;

            bool stackedText = gapY <= Math.Max(24, maxHeight) && gapX <= Math.Max(80, maxWidth * 2);
            bool adjacentLogoText = gapX <= Math.Max(48, maxWidth) && gapY <= Math.Max(48, maxHeight);
            return stackedText || adjacentLogoText;
        }

        private static StepTextLogoDetectionRegion MergeWatermarkCluster(
            IReadOnlyList<StepTextLogoDetectionRegion> cluster,
            int imageWidth,
            int imageHeight)
        {
            if (cluster == null || cluster.Count == 0)
                return null;
            if (cluster.Count == 1)
                return cluster[0];

            int left = cluster.Min(detection => detection.X);
            int top = cluster.Min(detection => detection.Y);
            int right = cluster.Max(detection => detection.X + detection.Width);
            int bottom = cluster.Max(detection => detection.Y + detection.Height);
            int padding = Math.Max(2, Math.Min(10, Math.Max(right - left, bottom - top) / 28));
            Rect bounds = ClipRect(
                new Rect(left - padding, top - padding, right - left + padding * 2, bottom - top + padding * 2),
                imageWidth,
                imageHeight);

            StepTextLogoDetectionRegion best = cluster.OrderByDescending(detection => detection.Score).First();
            string[] names = cluster
                .Select(detection => detection.TemplateName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] texts = cluster
                .Select(detection => detection.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(text => text, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new StepTextLogoDetectionRegion
            {
                TemplateName = names.Length == 0 ? best.TemplateName : string.Join("+", names),
                Kind = cluster.Count > 1 ? "watermark" : best.Kind,
                Text = texts.Length == 0 ? best.Text : string.Join(" ", texts),
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                Score = Math.Round(Math.Min(100.0, cluster.Max(detection => detection.Score) + (cluster.Count - 1) * 8.0), 3),
                ChamferDistance = cluster.Min(detection => detection.ChamferDistance),
                EdgePixelCount = cluster.Sum(detection => detection.EdgePixelCount)
            };
        }

        private static void SuppressTemplateResultPeak(Mat result, Point location, int width, int height)
        {
            int suppressWidth = Math.Max(8, width / 2);
            int suppressHeight = Math.Max(8, height / 2);
            Rect suppress = ClipRect(
                new Rect(location.X - suppressWidth / 2, location.Y - suppressHeight / 2, suppressWidth, suppressHeight),
                result.Width,
                result.Height);
            if (suppress.Width > 0 && suppress.Height > 0)
                Cv2.Rectangle(result, suppress, Scalar.Black, -1);
        }

        private static List<StepTextLogoDetectionRegion> FindSilhouetteOcrTextRegions(
            Mat silhouetteMask,
            IReadOnlyList<StepTextLogoDetectionRegion> knownDetections,
            StepTextLogoDetectionOptions options)
        {
            var detections = new List<StepTextLogoDetectionRegion>();
            foreach (VisualRoi roi in ExtractSilhouetteTextRois(silhouetteMask, options))
            {
                if (knownDetections.Any(known => IntersectionOverUnion(known, roi.Bounds) > 0.20))
                    continue;
                using (Mat roiMask = new Mat(silhouetteMask, roi.Bounds))
                {
                    List<ComponentBox> components = ExtractComponents(roiMask);
                    if (LooksLikeRegularPinArray(components, roi.Bounds) ||
                        LooksLikeLongStraightSeam(roiMask, roi.Bounds) ||
                        LooksLikeSingleConnectorContact(components, roi.Bounds))
                    {
                        continue;
                    }

                    TextShapeScore score = ScoreArbitraryTextRoi(roiMask, components);
                    if (score.Score < options.MinimumArbitraryTextScore)
                        continue;

                    detections.Add(new StepTextLogoDetectionRegion
                    {
                        TemplateName = "ocr-text",
                        Kind = "text",
                        Text = "",
                        X = roi.Bounds.X,
                        Y = roi.Bounds.Y,
                        Width = roi.Bounds.Width,
                        Height = roi.Bounds.Height,
                        Score = Math.Round(score.Score * 100.0, 3),
                        ChamferDistance = score.ChamferDistance,
                        EdgePixelCount = roi.EdgePixels
                    });
                }
            }

            return detections;
        }

        private static List<VisualRoi> ExtractSilhouetteTextRois(Mat silhouetteMask, StepTextLogoDetectionOptions options)
        {
            var candidates = new List<Rect>();
            foreach (Size kernelSize in BuildSilhouetteGroupingKernels())
            {
                using (var grouped = new Mat())
                {
                    Cv2.MorphologyEx(
                        silhouetteMask,
                        grouped,
                        MorphTypes.Close,
                        Cv2.GetStructuringElement(MorphShapes.Rect, kernelSize));
                    AddContourCandidates(grouped, candidates, silhouetteMask.Width, silhouetteMask.Height);
                }
            }

            return candidates
                .Select(rect => TightForegroundBounds(silhouetteMask, rect))
                .Where(rect => rect.Width >= options.MinimumRegionWidth && rect.Height >= options.MinimumRegionHeight)
                .Select(rect => ExpandRect(rect, silhouetteMask.Width, silhouetteMask.Height, GetCandidatePadding(rect)))
                .Distinct(new RectComparer())
                .Select(rect => new VisualRoi
                {
                    Bounds = rect,
                    EdgePixels = CountNonZero(silhouetteMask, rect),
                    ColorPixels = 0
                })
                .Where(roi => roi.EdgePixels >= options.MinimumEdgePixels)
                .Where(roi => !LooksLikeWholePartBody(roi, silhouetteMask.Width, silhouetteMask.Height))
                .OrderByDescending(roi => roi.EdgePixels)
                .Take(MaxCandidateCount)
                .ToList();
        }

        private static IEnumerable<Size> BuildSilhouetteGroupingKernels()
        {
            yield return new Size(5, 3);
            yield return new Size(11, 3);
            yield return new Size(3, 11);
            yield return new Size(23, 5);
            yield return new Size(5, 23);
            yield return new Size(45, 7);
            yield return new Size(7, 45);
        }

        private static Mat BuildColorForegroundMask(StepProjectionImage colorImage)
        {
            using (Mat bgra = StepProjectionImageOpenCv.ToBgraMat(colorImage))
            using (var gray = new Mat())
            using (var nonBackground = new Mat())
            using (var localContrast = new Mat())
            using (var blurred = new Mat())
            {
                Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
                Cv2.Threshold(gray, nonBackground, 244, 255, ThresholdTypes.BinaryInv);
                Cv2.GaussianBlur(gray, blurred, new Size(0, 0), 9.0);
                Cv2.Absdiff(gray, blurred, localContrast);
                Cv2.Threshold(localContrast, localContrast, 6, 255, ThresholdTypes.Binary);

                Cv2.BitwiseAnd(localContrast, nonBackground, localContrast);
                Cv2.MorphologyEx(
                    localContrast,
                    localContrast,
                    MorphTypes.Close,
                    Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2)));
                return localContrast.Clone();
            }
        }

        private static Mat BuildNeutralForegroundMask(StepProjectionImage colorImage)
        {
            var maskBytes = new byte[colorImage.Width * colorImage.Height];
            byte[] rgba = colorImage.RgbaBytes;
            for (int pixel = 0, offset = 0; pixel < maskBytes.Length; pixel++, offset += 4)
            {
                byte r = rgba[offset];
                byte g = rgba[offset + 1];
                byte b = rgba[offset + 2];
                if (r >= 245 && g >= 245 && b >= 245)
                    continue;

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int luminance = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b, MidpointRounding.AwayFromZero);
                bool neutral = max - min <= 48;
                if (neutral && (luminance <= 125 || luminance >= 178))
                    maskBytes[pixel] = 255;
            }

            var mask = new Mat(colorImage.Height, colorImage.Width, MatType.CV_8UC1);
            Marshal.Copy(maskBytes, 0, mask.Data, maskBytes.Length);
            return mask;
        }

        private static Mat BuildEdgeForegroundMask(StepProjectionImage edgeImage)
        {
            using (Mat gray = StepProjectionImageOpenCv.ToGrayMat(edgeImage))
            {
                var edgeMask = new Mat();
                Cv2.Threshold(gray, edgeMask, EdgeThreshold, 255, ThresholdTypes.BinaryInv);
                return edgeMask;
            }
        }

        private static List<VisualRoi> ExtractVisualRois(Mat colorMask, Mat edgeMask, StepTextLogoDetectionOptions options)
        {
            var candidates = new List<Rect>();
            foreach (Size kernelSize in BuildColorGroupingKernels())
            {
                using (var grouped = new Mat())
                {
                    Cv2.MorphologyEx(
                        colorMask,
                        grouped,
                        MorphTypes.Close,
                        Cv2.GetStructuringElement(MorphShapes.Rect, kernelSize));
                    AddContourCandidates(grouped, candidates, colorMask.Width, colorMask.Height);
                }
            }

            return candidates
                .Select(rect => TightForegroundBoundsUnion(edgeMask, colorMask, rect))
                .Where(rect => rect.Width >= options.MinimumRegionWidth && rect.Height >= options.MinimumRegionHeight)
                .Select(rect => ExpandRect(rect, edgeMask.Width, edgeMask.Height, GetCandidatePadding(rect)))
                .Distinct(new RectComparer())
                .Select(rect => new VisualRoi
                {
                    Bounds = rect,
                    EdgePixels = CountNonZero(edgeMask, rect),
                    ColorPixels = CountNonZero(colorMask, rect)
                })
                .Where(roi => roi.ForegroundPixels >= options.MinimumEdgePixels)
                .Where(roi => !LooksLikeWholePartBody(roi, edgeMask.Width, edgeMask.Height))
                .OrderByDescending(roi => roi.ForegroundPixels)
                .Take(MaxCandidateCount)
                .ToList();
        }

        private static IEnumerable<Size> BuildColorGroupingKernels()
        {
            yield return new Size(3, 3);
            yield return new Size(7, 3);
            yield return new Size(3, 7);
            yield return new Size(15, 5);
            yield return new Size(5, 15);
        }

        private static void AddContourCandidates(Mat mask, List<Rect> candidates, int imageWidth, int imageHeight)
        {
            Cv2.FindContours(
                mask,
                out Point[][] contours,
                out _,
                RetrievalModes.List,
                ContourApproximationModes.ApproxSimple);
            foreach (Point[] contour in contours)
            {
                if (contour == null || contour.Length == 0)
                    continue;
                Rect rect = Cv2.BoundingRect(contour);
                if (rect.Width <= 2 || rect.Height <= 2)
                    continue;
                if (rect.Width > imageWidth * 0.65 || rect.Height > imageHeight * 0.65)
                    continue;
                candidates.Add(rect);
            }
        }

        private static TemplateScore FindBestKnownTemplate(Mat roiEdgeMask, bool includeLogoTemplates)
        {
            TemplateScore best = TemplateScore.Rejected;
            foreach (StepWatermarkTemplate template in StepWatermarkTemplateLibrary.GetKnownTemplates())
            {
                if (!includeLogoTemplates && string.Equals(template.Kind, "logo", StringComparison.OrdinalIgnoreCase))
                    continue;

                for (int rotation = 0; rotation < 360; rotation += 90)
                {
                    TemplateScore score = ScoreKnownTemplate(roiEdgeMask, template, rotation);
                    if (score.Score > best.Score)
                        best = score;
                }
            }

            return best;
        }

        private static TemplateScore FindBestGeometryTemplate(Mat roiEdgeMask, TextShapeScore textShape)
        {
            if (textShape.Score < 0.34)
                return TemplateScore.Rejected;

            int longDimension = Math.Max(roiEdgeMask.Width, roiEdgeMask.Height);
            int shortDimension = Math.Min(roiEdgeMask.Width, roiEdgeMask.Height);
            if (longDimension < 20 || shortDimension < 6)
                return TemplateScore.Rejected;

            double aspect = longDimension / (double)Math.Max(1, shortDimension);
            if (aspect < 1.8 || aspect > 8.0)
                return TemplateScore.Rejected;

            string preferredName = aspect >= 3.65 ? "LCEDA" : "EasyEDA";
            StepWatermarkTemplate template = StepWatermarkTemplateLibrary.GetKnownTemplates()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            if (template == null)
                return TemplateScore.Rejected;

            double targetAspect = string.Equals(preferredName, "LCEDA", StringComparison.OrdinalIgnoreCase) ? 4.5 : 3.0;
            double aspectScore = 1.0 - Math.Min(1.0, Math.Abs(Math.Log(aspect / targetAspect)) / Math.Log(2.5));
            double density = Cv2.CountNonZero(roiEdgeMask) / (double)Math.Max(1, roiEdgeMask.Width * roiEdgeMask.Height);
            double densityScore = density >= 0.035 && density <= 0.42 ? 1.0 : 0.45;
            double sizeScore = Math.Min(1.0, longDimension / 52.0) * Math.Min(1.0, shortDimension / 11.0);
            double score =
                0.38 * textShape.Score +
                0.34 * Math.Max(0.0, aspectScore) +
                0.18 * densityScore +
                0.10 * sizeScore;

            return new TemplateScore
            {
                Template = template,
                Score = score,
                ChamferDistance = Math.Max(0.0, 6.0 * (1.0 - score))
            };
        }

        private static TemplateScore ScoreKnownTemplate(Mat roiEdgeMask, StepWatermarkTemplate template, int rotation)
        {
            if (template == null || template.EdgePoints == null || template.EdgePoints.Count == 0)
                return TemplateScore.Rejected;
            if (roiEdgeMask.Width <= 0 || roiEdgeMask.Height <= 0)
                return TemplateScore.Rejected;
            int roiLong = Math.Max(roiEdgeMask.Width, roiEdgeMask.Height);
            int roiShort = Math.Min(roiEdgeMask.Width, roiEdgeMask.Height);
            if (roiLong < 20 || roiShort < 6)
                return TemplateScore.Rejected;

            using (Mat templateMask = BuildTemplateMask(template, roiEdgeMask.Width, roiEdgeMask.Height, rotation))
            using (Mat templateDilated = new Mat())
            using (Mat edgeDilated = new Mat())
            using (Mat edgeOnTemplate = new Mat())
            using (Mat templateOnEdge = new Mat())
            {
                Cv2.Dilate(templateMask, templateDilated, Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));
                Cv2.Dilate(roiEdgeMask, edgeDilated, Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));
                Cv2.BitwiseAnd(roiEdgeMask, templateDilated, edgeOnTemplate);
                Cv2.BitwiseAnd(templateMask, edgeDilated, templateOnEdge);

                int templatePixels = Math.Max(1, Cv2.CountNonZero(templateMask));
                int edgePixels = Math.Max(1, Cv2.CountNonZero(roiEdgeMask));
                int edgeOverlap = Cv2.CountNonZero(edgeOnTemplate);
                int templateOverlap = Cv2.CountNonZero(templateOnEdge);
                double precision = edgeOverlap / (double)edgePixels;
                double recall = templateOverlap / (double)templatePixels;
                double f1 = precision + recall <= 0.0 ? 0.0 : 2.0 * precision * recall / (precision + recall);
                double xProjection = ProjectionSimilarity(roiEdgeMask, templateMask, byColumn: true);
                double yProjection = ProjectionSimilarity(roiEdgeMask, templateMask, byColumn: false);
                GetRotatedTemplateSize(template, rotation, out int sourceWidth, out int sourceHeight);
                double roiAspect = Math.Max(
                    roiEdgeMask.Width / (double)Math.Max(1, roiEdgeMask.Height),
                    roiEdgeMask.Height / (double)Math.Max(1, roiEdgeMask.Width));
                double templateAspect = Math.Max(
                    sourceWidth / (double)Math.Max(1, sourceHeight),
                    sourceHeight / (double)Math.Max(1, sourceWidth));
                double aspectScore = 1.0 - Math.Min(1.0, Math.Abs(Math.Log(roiAspect / Math.Max(0.001, templateAspect))) / Math.Log(4.0));
                double sizeScore = Math.Min(1.0, roiLong / 36.0) * Math.Min(1.0, roiShort / 10.0);
                double score = (0.55 * f1 + 0.25 * recall + 0.12 * xProjection + 0.08 * yProjection) *
                    Math.Max(0.0, aspectScore) *
                    Math.Max(0.35, sizeScore);
                double chamfer = EstimateChamferDistance(roiEdgeMask, templateMask);

                return new TemplateScore
                {
                    Template = template,
                    Score = score,
                    ChamferDistance = chamfer
                };
            }
        }

        private static Mat BuildTemplateMask(StepWatermarkTemplate template, int width, int height, int rotation)
        {
            var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
            GetRotatedTemplateSize(template, rotation, out int sourceWidth, out int sourceHeight);
            foreach (StepWatermarkTemplatePoint point in template.EdgePoints)
            {
                RotateTemplatePoint(point.X, point.Y, template.Width, template.Height, rotation, out int rx, out int ry);
                int x = sourceWidth <= 1
                    ? 0
                    : (int)Math.Round(rx * (width - 1) / (double)(sourceWidth - 1), MidpointRounding.AwayFromZero);
                int y = sourceHeight <= 1
                    ? 0
                    : (int)Math.Round(ry * (height - 1) / (double)(sourceHeight - 1), MidpointRounding.AwayFromZero);
                if (x >= 0 && y >= 0 && x < width && y < height)
                    mask.Set(y, x, (byte)255);
            }

            return mask;
        }

        private static TextShapeScore ScoreArbitraryTextRoi(Mat roiEdgeMask, IReadOnlyList<ComponentBox> components)
        {
            int edgePixels = Cv2.CountNonZero(roiEdgeMask);
            int area = Math.Max(1, roiEdgeMask.Width * roiEdgeMask.Height);
            double density = edgePixels / (double)area;
            if (Math.Max(roiEdgeMask.Width, roiEdgeMask.Height) < 20 ||
                Math.Min(roiEdgeMask.Width, roiEdgeMask.Height) < 6)
            {
                return TextShapeScore.Rejected;
            }

            if (density < 0.018 || density > 0.55)
                return TextShapeScore.Rejected;

            double aspect = Math.Max(
                roiEdgeMask.Width / (double)Math.Max(1, roiEdgeMask.Height),
                roiEdgeMask.Height / (double)Math.Max(1, roiEdgeMask.Width));
            if (aspect > 32.0)
                return TextShapeScore.Rejected;

            int significantComponents = components.Count(component =>
                component.Pixels >= 3 &&
                component.Width <= roiEdgeMask.Width * 0.85 &&
                component.Height <= roiEdgeMask.Height * 0.85);
            double componentScore = significantComponents >= 2
                ? Math.Min(1.0, significantComponents / 8.0)
                : (aspect >= 1.8 && edgePixels >= 18 ? 0.55 : 0.0);
            if (componentScore <= 0.0)
                return TextShapeScore.Rejected;

            double densityScore = 1.0 - Math.Min(1.0, Math.Abs(density - 0.12) / 0.18);
            double aspectScore = aspect >= 1.25 && aspect <= 18.0 ? 1.0 : 0.58;
            double bandScore = Math.Max(
                ProjectionBandScore(roiEdgeMask, byColumn: true),
                ProjectionBandScore(roiEdgeMask, byColumn: false));
            double spacingScore = ComponentSpacingScore(components, roiEdgeMask.Width, roiEdgeMask.Height);
            double score =
                0.26 * componentScore +
                0.24 * Math.Max(0.0, densityScore) +
                0.20 * aspectScore +
                0.18 * bandScore +
                0.12 * spacingScore;

            return new TextShapeScore
            {
                Score = score,
                ChamferDistance = Math.Max(0.0, 10.0 * (1.0 - score))
            };
        }

        private static List<ComponentBox> ExtractComponents(Mat binaryMask)
        {
            Cv2.FindContours(
                binaryMask,
                out Point[][] contours,
                out _,
                RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);
            var result = new List<ComponentBox>();
            foreach (Point[] contour in contours)
            {
                Rect rect = Cv2.BoundingRect(contour);
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;
                using (var componentMask = new Mat(binaryMask, rect))
                {
                    result.Add(new ComponentBox
                    {
                        X = rect.X,
                        Y = rect.Y,
                        Width = rect.Width,
                        Height = rect.Height,
                        Pixels = Cv2.CountNonZero(componentMask)
                    });
                }
            }

            return result;
        }

        private static bool LooksLikeProtectedMetal(Mat bgra, Rect roi)
        {
            int protectedPixels = 0;
            int foregroundPixels = 0;
            Rect clipped = ClipRect(roi, bgra.Width, bgra.Height);
            if (clipped.Width <= 0 || clipped.Height <= 0)
                return false;

            for (int y = clipped.Y; y < clipped.Bottom; y++)
            {
                for (int x = clipped.X; x < clipped.Right; x++)
                {
                    Vec4b pixel = bgra.At<Vec4b>(y, x);
                    byte b = pixel.Item0;
                    byte g = pixel.Item1;
                    byte r = pixel.Item2;
                    if (r >= 245 && g >= 245 && b >= 245)
                        continue;
                    foregroundPixels++;

                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    bool goldOrCopper = r > 120 && g > 85 && b < 125 && r > b + 25;
                    bool silver = r > 190 && g > 190 && b > 190 && max - min < 22;
                    if (goldOrCopper || silver)
                        protectedPixels++;
                }
            }

            return foregroundPixels > 0 && protectedPixels >= foregroundPixels * 0.45;
        }

        private static bool LooksLikeRegularPinArray(IReadOnlyList<ComponentBox> components, Rect roi)
        {
            var significant = components
                .Where(component => component.Pixels >= 4 && component.Width >= 2 && component.Height >= 2)
                .Where(component => component.Width <= roi.Width * 0.45 && component.Height <= roi.Height * 0.75)
                .ToList();
            if (significant.Count < 5)
                return false;

            double medianWidth = Median(significant.Select(component => component.Width));
            double medianHeight = Median(significant.Select(component => component.Height));
            var similar = significant
                .Where(component =>
                    Math.Abs(component.Width - medianWidth) <= Math.Max(3.0, medianWidth * 0.45) &&
                    Math.Abs(component.Height - medianHeight) <= Math.Max(3.0, medianHeight * 0.45))
                .ToList();
            if (similar.Count < 5 || similar.Count < significant.Count * 0.65)
                return false;

            double spacing = ComponentRegularity(similar);
            return spacing >= 0.82;
        }

        private static bool LooksLikeLongStraightSeam(Mat edgeMask, Rect roi)
        {
            int longDimension = Math.Max(roi.Width, roi.Height);
            int shortDimension = Math.Min(roi.Width, roi.Height);
            if (longDimension < 48 || shortDimension <= 0 || longDimension / (double)shortDimension < 6.0)
                return false;

            int edgePixels = Cv2.CountNonZero(edgeMask);
            double density = edgePixels / (double)Math.Max(1, roi.Width * roi.Height);
            return density < 0.10;
        }

        private static bool LooksLikeSingleConnectorContact(IReadOnlyList<ComponentBox> components, Rect roi)
        {
            int longDimension = Math.Max(roi.Width, roi.Height);
            int shortDimension = Math.Min(roi.Width, roi.Height);
            if (longDimension < 36 || shortDimension <= 0 || longDimension / (double)shortDimension < 2.6)
                return false;

            var significant = components.Where(component => component.Pixels >= 10).OrderByDescending(component => component.Pixels).ToList();
            if (significant.Count == 0 || significant.Count > 3)
                return false;

            ComponentBox largest = significant[0];
            return largest.Width >= roi.Width * 0.70 || largest.Height >= roi.Height * 0.70;
        }

        private static List<StepTextLogoDetectionRegion> SuppressSplitDetections(List<StepTextLogoDetectionRegion> detections)
        {
            var selected = new List<StepTextLogoDetectionRegion>();
            List<StepTextLogoDetectionRegion> selectedText = SuppressOverlappingDetections(
                detections.Where(detection => IsTextDetection(detection)).ToList(),
                maxCount: 2);
            List<StepTextLogoDetectionRegion> selectedLogo = SelectLogoDetections(
                detections.Where(detection => IsLogoDetection(detection)).ToList());

            selected.AddRange(selectedLogo);
            selected.AddRange(selectedText);

            if (selected.Count == 0)
                selected.AddRange(SuppressOverlappingDetections(detections, maxCount: 2));

            return selected
                .OrderByDescending(detection => detection.Score)
                .ToList();
        }

        private static List<StepTextLogoDetectionRegion> SelectLogoDetections(
            List<StepTextLogoDetectionRegion> logoDetections)
        {
            List<StepTextLogoDetectionRegion> candidates = SuppressOverlappingDetections(
                logoDetections,
                maxCount: 8);
            if (candidates.Count == 0)
                return candidates;

            return candidates
                .Where(IsSelectableLogoCandidate)
                .OrderByDescending(LogoSelectionScore)
                .Take(1)
                .ToList();
        }

        private static bool IsSelectableLogoCandidate(StepTextLogoDetectionRegion logo)
        {
            if (logo == null)
                return false;

            if (string.Equals(logo.TemplateName, "easyeda-logo-grayscale-template", StringComparison.OrdinalIgnoreCase))
                return logo.Score >= 48.0;

            if (string.Equals(logo.TemplateName, "easyeda-logo-edge-projection", StringComparison.OrdinalIgnoreCase))
                return logo.Score >= 34.0;

            return logo.Score >= MinimumLogoInkMaskTemplateScore * 100.0;
        }

        private static double LogoSelectionScore(StepTextLogoDetectionRegion logo)
        {
            if (logo == null)
                return double.NegativeInfinity;

            double score = logo.Score;
            if (string.Equals(logo.TemplateName, "easyeda-logo-grayscale-template", StringComparison.OrdinalIgnoreCase))
                score += 12.0;
            else if (string.Equals(logo.TemplateName, "easyeda-logo-edge-projection", StringComparison.OrdinalIgnoreCase))
                score += 20.0;
            else if (!string.IsNullOrEmpty(logo.TemplateName) &&
                logo.TemplateName.IndexOf("sift", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 4.0;

            return score;
        }

        private static List<StepTextLogoDetectionRegion> MergeNearbyLogoCandidates(
            IReadOnlyList<StepTextLogoDetectionRegion> logoDetections)
        {
            if (logoDetections == null || logoDetections.Count <= 1)
                return logoDetections?.ToList() ?? new List<StepTextLogoDetectionRegion>();

            var visited = new bool[logoDetections.Count];
            var merged = new List<StepTextLogoDetectionRegion>();
            for (int index = 0; index < logoDetections.Count; index++)
            {
                if (visited[index])
                    continue;

                var cluster = new List<StepTextLogoDetectionRegion>();
                var pending = new Queue<int>();
                pending.Enqueue(index);
                visited[index] = true;
                while (pending.Count > 0)
                {
                    int current = pending.Dequeue();
                    StepTextLogoDetectionRegion currentLogo = logoDetections[current];
                    cluster.Add(currentLogo);
                    for (int candidate = 0; candidate < logoDetections.Count; candidate++)
                    {
                        if (visited[candidate])
                            continue;
                        if (!ShouldMergeLogoCandidates(currentLogo, logoDetections[candidate]))
                            continue;

                        visited[candidate] = true;
                        pending.Enqueue(candidate);
                    }
                }

                merged.Add(MergeLogoCandidateCluster(cluster));
            }

            return merged;
        }

        private static bool ShouldMergeLogoCandidates(
            StepTextLogoDetectionRegion left,
            StepTextLogoDetectionRegion right)
        {
            if (left == null || right == null)
                return false;
            if (IntersectionOverUnion(left, right) > 0.04)
                return true;

            int leftCenterX = left.X + left.Width / 2;
            int leftCenterY = left.Y + left.Height / 2;
            int rightCenterX = right.X + right.Width / 2;
            int rightCenterY = right.Y + right.Height / 2;
            int maxWidth = Math.Max(left.Width, right.Width);
            int maxHeight = Math.Max(left.Height, right.Height);
            return Math.Abs(leftCenterX - rightCenterX) <= Math.Max(28, maxWidth) &&
                Math.Abs(leftCenterY - rightCenterY) <= Math.Max(24, maxHeight);
        }

        private static StepTextLogoDetectionRegion MergeLogoCandidateCluster(
            IReadOnlyList<StepTextLogoDetectionRegion> cluster)
        {
            if (cluster == null || cluster.Count == 0)
                return null;
            if (cluster.Count == 1)
                return cluster[0];

            int left = cluster.Min(detection => detection.X);
            int top = cluster.Min(detection => detection.Y);
            int right = cluster.Max(detection => detection.X + detection.Width);
            int bottom = cluster.Max(detection => detection.Y + detection.Height);
            StepTextLogoDetectionRegion best = cluster.OrderByDescending(detection => detection.Score).First();
            return new StepTextLogoDetectionRegion
            {
                TemplateName = "easyeda-logo-merged",
                Kind = "logo",
                Text = "",
                X = left,
                Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                Score = Math.Round(Math.Min(100.0, best.Score + Math.Min(10.0, (cluster.Count - 1) * 2.5)), 3),
                ChamferDistance = cluster.Min(detection => detection.ChamferDistance),
                EdgePixelCount = cluster.Sum(detection => detection.EdgePixelCount)
            };
        }

        private static double TextLogoAnchorScore(
            StepTextLogoDetectionRegion logo,
            StepTextLogoDetectionRegion text)
        {
            if (logo == null || text == null)
                return 0.0;

            int logoRight = logo.X + logo.Width;
            int logoBottom = logo.Y + logo.Height;
            int textRight = text.X + text.Width;
            int textBottom = text.Y + text.Height;
            int gapX = Math.Max(0, Math.Max(logo.X, text.X) - Math.Min(logoRight, textRight));
            int gapY = Math.Max(0, Math.Max(logo.Y, text.Y) - Math.Min(logoBottom, textBottom));
            int unionWidth = Math.Max(logoRight, textRight) - Math.Min(logo.X, text.X);
            int unionHeight = Math.Max(logoBottom, textBottom) - Math.Min(logo.Y, text.Y);

            int maxAllowedGapX = Math.Max(96, (int)Math.Round(Math.Max(logo.Width, text.Width) * 1.15, MidpointRounding.AwayFromZero));
            int maxAllowedGapY = Math.Max(48, (int)Math.Round(Math.Min(Math.Max(logo.Height, text.Height), logo.Height * 2.0) * 0.70, MidpointRounding.AwayFromZero));
            if (gapX > maxAllowedGapX || gapY > maxAllowedGapY)
            {
                return 0.0;
            }

            double verticalOverlap = OverlapLength(logo.Y, logoBottom, text.Y, textBottom) /
                (double)Math.Max(1, Math.Min(logo.Height, text.Height));
            double horizontalOverlap = OverlapLength(logo.X, logoRight, text.X, textRight) /
                (double)Math.Max(1, Math.Min(logo.Width, text.Width));
            if (verticalOverlap < 0.12 && horizontalOverlap < 0.12 && (gapX > 36 || gapY > 28))
                return 0.0;

            double gapScore = 1.0 / (1.0 + Math.Sqrt(gapX * gapX + gapY * gapY) / 90.0);
            double unionPenalty = unionWidth > 720 || unionHeight > 520 ? 0.55 : 1.0;
            double alignment = Math.Max(verticalOverlap, horizontalOverlap);
            return unionPenalty * (0.48 * alignment + 0.40 * gapScore + 0.12 * Math.Min(1.0, logo.Score / 100.0));
        }

        private static int OverlapLength(int leftStart, int leftEnd, int rightStart, int rightEnd)
        {
            return Math.Max(0, Math.Min(leftEnd, rightEnd) - Math.Max(leftStart, rightStart));
        }

        private static List<StepTextLogoDetectionRegion> BuildCombinedWatermarkRegions(
            IReadOnlyList<StepTextLogoDetectionRegion> detections,
            int imageWidth,
            int imageHeight)
        {
            var parts = detections
                .Where(detection => IsLogoDetection(detection) || IsTextDetection(detection))
                .ToList();
            if (parts.Count == 0)
                return new List<StepTextLogoDetectionRegion>();

            var clusters = new List<List<StepTextLogoDetectionRegion>>();
            foreach (StepTextLogoDetectionRegion part in parts.OrderByDescending(detection => detection.Score))
            {
                List<StepTextLogoDetectionRegion> cluster = clusters.FirstOrDefault(existing =>
                    existing.Any(member => ShouldMergeWatermarkDetections(member, part, imageWidth, imageHeight)));
                if (cluster == null)
                {
                    cluster = new List<StepTextLogoDetectionRegion>();
                    clusters.Add(cluster);
                }

                cluster.Add(part);
            }

            var combined = new List<StepTextLogoDetectionRegion>();
            foreach (List<StepTextLogoDetectionRegion> cluster in clusters)
            {
                if (!cluster.Any(IsLogoDetection) || !cluster.Any(IsTextDetection))
                    continue;

                StepTextLogoDetectionRegion merged = MergeWatermarkCluster(cluster, imageWidth, imageHeight);
                if (merged == null)
                    continue;

                combined.Add(new StepTextLogoDetectionRegion
                {
                    TemplateName = "easyeda-watermark-combined",
                    Kind = "watermark-combined",
                    Text = merged.Text,
                    X = merged.X,
                    Y = merged.Y,
                    Width = merged.Width,
                    Height = merged.Height,
                    Score = merged.Score,
                    ChamferDistance = merged.ChamferDistance,
                    EdgePixelCount = merged.EdgePixelCount
                });
            }

            return SuppressOverlappingDetections(combined, maxCount: 1);
        }

        private static bool IsLogoDetection(StepTextLogoDetectionRegion detection)
        {
            return detection != null &&
                string.Equals(detection.Kind, "logo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTextDetection(StepTextLogoDetectionRegion detection)
        {
            return detection != null &&
                string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase);
        }

        private static List<StepTextLogoDetectionRegion> SuppressOverlappingDetections(
            List<StepTextLogoDetectionRegion> detections,
            int maxCount)
        {
            var ordered = detections.OrderByDescending(detection => detection.Score).ToList();
            var accepted = new List<StepTextLogoDetectionRegion>();
            foreach (StepTextLogoDetectionRegion detection in ordered)
            {
                if (accepted.Any(existing => IntersectionOverUnion(existing, detection) > 0.62))
                    continue;
                accepted.Add(detection);
            }

            return accepted.Take(Math.Max(0, maxCount)).ToList();
        }

        private static Rect TightForegroundBounds(Mat mask, Rect searchRect)
        {
            Rect clipped = ClipRect(searchRect, mask.Width, mask.Height);
            if (clipped.Width <= 0 || clipped.Height <= 0)
                return new Rect();

            int left = clipped.Right;
            int top = clipped.Bottom;
            int right = clipped.X - 1;
            int bottom = clipped.Y - 1;
            for (int y = clipped.Y; y < clipped.Bottom; y++)
            {
                for (int x = clipped.X; x < clipped.Right; x++)
                {
                    if (mask.At<byte>(y, x) == 0)
                        continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            if (right < left || bottom < top)
                return new Rect();
            return new Rect(left, top, right - left + 1, bottom - top + 1);
        }

        private static Rect TightForegroundBoundsUnion(Mat firstMask, Mat secondMask, Rect searchRect)
        {
            Rect first = TightForegroundBounds(firstMask, searchRect);
            Rect second = TightForegroundBounds(secondMask, searchRect);
            if (first.Width <= 0 || first.Height <= 0)
                return second;
            if (second.Width <= 0 || second.Height <= 0)
                return first;

            int left = Math.Min(first.X, second.X);
            int top = Math.Min(first.Y, second.Y);
            int right = Math.Max(first.X + first.Width, second.X + second.Width);
            int bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);
            return new Rect(left, top, right - left, bottom - top);
        }

        private static int CountNonZero(Mat mask, Rect rect)
        {
            Rect clipped = ClipRect(rect, mask.Width, mask.Height);
            if (clipped.Width <= 0 || clipped.Height <= 0)
                return 0;
            using (var roi = new Mat(mask, clipped))
                return Cv2.CountNonZero(roi);
        }

        private static Rect ExpandRect(Rect rect, int imageWidth, int imageHeight, int padding)
        {
            return ClipRect(
                new Rect(rect.X - padding, rect.Y - padding, rect.Width + padding * 2, rect.Height + padding * 2),
                imageWidth,
                imageHeight);
        }

        private static Rect ClipRect(Rect rect, int imageWidth, int imageHeight)
        {
            int left = Math.Max(0, rect.X);
            int top = Math.Max(0, rect.Y);
            int right = Math.Min(imageWidth, rect.X + rect.Width);
            int bottom = Math.Min(imageHeight, rect.Y + rect.Height);
            if (right <= left || bottom <= top)
                return new Rect();
            return new Rect(left, top, right - left, bottom - top);
        }

        private static int GetCandidatePadding(Rect rect)
        {
            return Math.Max(1, Math.Min(5, Math.Max(rect.Width, rect.Height) / 20));
        }

        private static bool LooksLikeWholePartBody(VisualRoi roi, int imageWidth, int imageHeight)
        {
            if (roi.Bounds.Width > imageWidth * 0.45 || roi.Bounds.Height > imageHeight * 0.45)
                return true;
            double fill = roi.ColorPixels / (double)Math.Max(1, roi.Bounds.Width * roi.Bounds.Height);
            return fill > 0.70 && roi.Bounds.Width > 80 && roi.Bounds.Height > 80;
        }

        private static double ProjectionSimilarity(Mat left, Mat right, bool byColumn)
        {
            int count = byColumn ? left.Width : left.Height;
            if (count <= 0)
                return 0.0;
            double intersection = 0.0;
            double union = 0.0;
            for (int i = 0; i < count; i++)
            {
                int leftValue = CountProjection(left, i, byColumn);
                int rightValue = CountProjection(right, i, byColumn);
                intersection += Math.Min(leftValue, rightValue);
                union += Math.Max(leftValue, rightValue);
            }

            return union <= 0.0 ? 0.0 : intersection / union;
        }

        private static int CountProjection(Mat mask, int index, bool byColumn)
        {
            int count = 0;
            if (byColumn)
            {
                for (int y = 0; y < mask.Height; y++)
                {
                    if (mask.At<byte>(y, index) != 0)
                        count++;
                }
            }
            else
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.At<byte>(index, x) != 0)
                        count++;
                }
            }

            return count;
        }

        private static double ProjectionBandScore(Mat mask, bool byColumn)
        {
            int count = byColumn ? mask.Width : mask.Height;
            int activeBands = 0;
            bool inBand = false;
            int threshold = Math.Max(1, (byColumn ? mask.Height : mask.Width) / 20);
            for (int i = 0; i < count; i++)
            {
                bool active = CountProjection(mask, i, byColumn) >= threshold;
                if (active && !inBand)
                    activeBands++;
                inBand = active;
            }

            return Math.Min(1.0, activeBands / 8.0);
        }

        private static double EstimateChamferDistance(Mat edgeMask, Mat templateMask)
        {
            using (Mat inverted = new Mat())
            using (Mat distance = new Mat())
            {
                Cv2.BitwiseNot(edgeMask, inverted);
                Cv2.DistanceTransform(inverted, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                double sum = 0.0;
                int count = 0;
                for (int y = 0; y < templateMask.Height; y++)
                {
                    for (int x = 0; x < templateMask.Width; x++)
                    {
                        if (templateMask.At<byte>(y, x) == 0)
                            continue;
                        sum += Math.Min(30.0, distance.At<float>(y, x));
                        count++;
                    }
                }

                return count == 0 ? 30.0 : sum / count;
            }
        }

        private static double ComponentSpacingScore(IReadOnlyList<ComponentBox> components, int width, int height)
        {
            var significant = components.Where(component => component.Pixels >= 4).ToList();
            if (significant.Count < 2)
                return 0.45;

            double regularity = ComponentRegularity(significant);
            if (regularity >= 0.92 && significant.Count >= 5)
                return 0.05;

            double spanX = significant.Max(component => component.CenterX) - significant.Min(component => component.CenterX);
            double spanY = significant.Max(component => component.CenterY) - significant.Min(component => component.CenterY);
            double span = Math.Max(spanX / Math.Max(1, width), spanY / Math.Max(1, height));
            return Math.Max(0.15, Math.Min(1.0, span + 0.25));
        }

        private static double ComponentRegularity(IReadOnlyList<ComponentBox> components)
        {
            List<double> centers = components
                .OrderBy(component => component.CenterX)
                .Select(component => component.CenterX)
                .ToList();
            if (centers.Count < 3)
                return 0.0;
            var gaps = new List<double>();
            for (int i = 1; i < centers.Count; i++)
            {
                double gap = centers[i] - centers[i - 1];
                if (gap > 0.5)
                    gaps.Add(gap);
            }

            if (gaps.Count < 2)
                return 0.0;

            double average = gaps.Average();
            if (average <= 0.0)
                return 0.0;
            double meanDeviation = gaps.Average(gap => Math.Abs(gap - average));
            return Math.Max(0.0, 1.0 - meanDeviation / average);
        }

        private static double Median(IEnumerable<int> values)
        {
            var sorted = values.OrderBy(value => value).ToList();
            if (sorted.Count == 0)
                return 0.0;
            int middle = sorted.Count / 2;
            if (sorted.Count % 2 == 1)
                return sorted[middle];
            return (sorted[middle - 1] + sorted[middle]) / 2.0;
        }

        private static double IntersectionOverUnion(StepTextLogoDetectionRegion left, StepTextLogoDetectionRegion right)
        {
            int intersection = IntersectionArea(left.X, left.Y, left.Width, left.Height, right.X, right.Y, right.Width, right.Height);
            int union = left.Width * left.Height + right.Width * right.Height - intersection;
            return union <= 0 ? 0.0 : intersection / (double)union;
        }

        private static double IntersectionOverUnion(StepTextLogoDetectionRegion left, Rect right)
        {
            int intersection = IntersectionArea(left.X, left.Y, left.Width, left.Height, right.X, right.Y, right.Width, right.Height);
            int union = left.Width * left.Height + right.Width * right.Height - intersection;
            return union <= 0 ? 0.0 : intersection / (double)union;
        }

        private static int IntersectionArea(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh)
        {
            int left = Math.Max(ax, bx);
            int top = Math.Max(ay, by);
            int right = Math.Min(ax + aw, bx + bw);
            int bottom = Math.Min(ay + ah, by + bh);
            if (right <= left || bottom <= top)
                return 0;
            return (right - left) * (bottom - top);
        }

        private static void GetRotatedTemplateSize(StepWatermarkTemplate template, int rotation, out int width, out int height)
        {
            if (rotation == 90 || rotation == 270)
            {
                width = template.Height;
                height = template.Width;
                return;
            }

            width = template.Width;
            height = template.Height;
        }

        private static void RotateTemplatePoint(int x, int y, int width, int height, int rotation, out int rotatedX, out int rotatedY)
        {
            if (rotation == 90)
            {
                rotatedX = height - 1 - y;
                rotatedY = x;
                return;
            }
            if (rotation == 180)
            {
                rotatedX = width - 1 - x;
                rotatedY = height - 1 - y;
                return;
            }
            if (rotation == 270)
            {
                rotatedX = y;
                rotatedY = width - 1 - x;
                return;
            }

            rotatedX = x;
            rotatedY = y;
        }

        private sealed class VisualRoi
        {
            public Rect Bounds { get; set; }
            public int EdgePixels { get; set; }
            public int ColorPixels { get; set; }
            public int ForegroundPixels => Math.Max(EdgePixels, ColorPixels);
        }

        private sealed class ComponentBox
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Pixels { get; set; }
            public double CenterX => X + (Width - 1) / 2.0;
            public double CenterY => Y + (Height - 1) / 2.0;
        }

        private sealed class TemplateScore
        {
            public static readonly TemplateScore Rejected = new TemplateScore { Score = 0.0, ChamferDistance = 30.0 };
            public StepWatermarkTemplate Template { get; set; }
            public double Score { get; set; }
            public double ChamferDistance { get; set; }
        }

        private sealed class TextShapeScore
        {
            public static readonly TextShapeScore Rejected = new TextShapeScore { Score = 0.0, ChamferDistance = 30.0 };
            public double Score { get; set; }
            public double ChamferDistance { get; set; }
        }

        private sealed class LogoShapeScore
        {
            public static readonly LogoShapeScore Rejected = new LogoShapeScore { Score = 0.0, ChamferDistance = 30.0 };
            public double Score { get; set; }
            public double ChamferDistance { get; set; }
        }

        private sealed class LogoHoughCandidate
        {
            public double CenterX { get; set; }
            public double CenterY { get; set; }
            public double Scale { get; set; }
            public double Angle { get; set; }
        }

        private sealed class RectComparer : IEqualityComparer<Rect>
        {
            public bool Equals(Rect x, Rect y)
            {
                return x.X == y.X && x.Y == y.Y && x.Width == y.Width && x.Height == y.Height;
            }

            public int GetHashCode(Rect obj)
            {
                unchecked
                {
                    int hash = obj.X;
                    hash = (hash * 397) ^ obj.Y;
                    hash = (hash * 397) ^ obj.Width;
                    hash = (hash * 397) ^ obj.Height;
                    return hash;
                }
            }
        }
    }
}
