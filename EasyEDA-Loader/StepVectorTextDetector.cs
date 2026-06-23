using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyEDA_Loader
{
    public sealed class StepVectorTextDetectionPair
    {
        public IReadOnlyList<StepVectorWatermarkDetectionRegion> KnownTextDetections { get; set; } =
            Array.Empty<StepVectorWatermarkDetectionRegion>();
        public IReadOnlyList<StepVectorWatermarkDetectionRegion> CleanTextDetections { get; set; } =
            Array.Empty<StepVectorWatermarkDetectionRegion>();
    }

    public static class StepVectorTextDetector
    {
        private static readonly int[] RightAngleOrientations = { 0, 90, 180, 270 };
        private static readonly IReadOnlyList<TextTemplateOrientation> KnownTextTemplates = BuildKnownTextTemplates();
        private const double MaximumKnownTemplateChamfer = 18.0;
        private const int MaximumPointsPerScore = 900;

        public static IReadOnlyList<StepVectorWatermarkDetectionRegion> Detect(
            StepVectorWatermarkDetectionInput input,
            StepTextLogoDetectionOptions options)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            options = options ?? new StepTextLogoDetectionOptions();
            List<VectorTextStroke> strokes = ExtractStrokes(input);
            if (strokes.Count == 0)
                return Array.Empty<StepVectorWatermarkDetectionRegion>();

            List<VectorTextComponent> components = BuildComponents(strokes);
            return DetectFromComponents(components, options);
        }

        public static StepVectorTextDetectionPair DetectKnownAndCleanText(
            StepVectorWatermarkDetectionInput input,
            StepTextLogoDetectionOptions knownTextOptions = null,
            StepTextLogoDetectionOptions cleanTextOptions = null)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            knownTextOptions = knownTextOptions ?? new StepTextLogoDetectionOptions { DetectArbitraryText = false };
            cleanTextOptions = cleanTextOptions ?? new StepTextLogoDetectionOptions { DetectArbitraryText = true };

            List<VectorTextStroke> strokes = ExtractStrokes(input);
            if (strokes.Count == 0)
            {
                return new StepVectorTextDetectionPair
                {
                    KnownTextDetections = Array.Empty<StepVectorWatermarkDetectionRegion>(),
                    CleanTextDetections = Array.Empty<StepVectorWatermarkDetectionRegion>()
                };
            }

            List<VectorTextComponent> components = BuildComponents(strokes);
            var knownDetections = new List<StepVectorWatermarkDetectionRegion>();
            var cleanDetections = new List<StepVectorWatermarkDetectionRegion>();
            foreach (VectorTextCandidate candidate in BuildCandidates(components, cleanTextOptions))
            {
                OrientedTextScore bestKnown = FindBestKnownText(candidate);
                AddCandidateDetection(candidate, bestKnown, knownTextOptions, knownDetections);
                AddCandidateDetection(candidate, bestKnown, cleanTextOptions, cleanDetections);
            }

            return new StepVectorTextDetectionPair
            {
                KnownTextDetections = FinalizeTextDetections(knownDetections),
                CleanTextDetections = FinalizeTextDetections(cleanDetections)
            };
        }

        private static IReadOnlyList<StepVectorWatermarkDetectionRegion> DetectFromComponents(
            IReadOnlyList<VectorTextComponent> components,
            StepTextLogoDetectionOptions options)
        {
            var detections = new List<StepVectorWatermarkDetectionRegion>();
            foreach (VectorTextCandidate candidate in BuildCandidates(components, options))
            {
                OrientedTextScore bestKnown = FindBestKnownText(candidate);
                AddCandidateDetection(candidate, bestKnown, options, detections);
            }

            return FinalizeTextDetections(detections);
        }

        private static void AddCandidateDetection(
            VectorTextCandidate candidate,
            OrientedTextScore bestKnown,
            StepTextLogoDetectionOptions options,
            List<StepVectorWatermarkDetectionRegion> detections)
        {
            if (AcceptsKnownText(bestKnown, options))
            {
                detections.Add(CreateRegion(candidate, bestKnown));
                return;
            }

            if (!options.DetectArbitraryText)
                return;

            OrientedTextScore arbitrary = ScoreArbitraryText(candidate, options);
            if (arbitrary.Score >= options.MinimumArbitraryTextScore)
                detections.Add(CreateRegion(candidate, arbitrary));
        }

        private static IReadOnlyList<StepVectorWatermarkDetectionRegion> FinalizeTextDetections(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections)
        {
            List<StepVectorWatermarkDetectionRegion> textRegions = SuppressOverlappingDetections(detections)
                .Where(detection => string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(detection => detection.Score)
                .ToList();
            return ExpandContextualLcedaRegions(CombineClosestArbitraryTextRegions(textRegions));
        }

        private static List<VectorTextStroke> ExtractStrokes(StepVectorWatermarkDetectionInput input)
        {
            var strokes = new List<VectorTextStroke>();
            IReadOnlyList<StepVectorWatermarkPrimitive> primitives =
                input.Primitives ?? Array.Empty<StepVectorWatermarkPrimitive>();
            for (int index = 0; index < primitives.Count; index++)
            {
                StepVectorWatermarkPrimitive primitive = primitives[index];
                if (primitive == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(primitive.Visibility) &&
                    primitive.Visibility.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                IReadOnlyList<StepVectorWatermarkPoint> sourcePoints =
                    primitive.SampledImagePoints != null && primitive.SampledImagePoints.Count > 0
                        ? primitive.SampledImagePoints
                        : primitive.SampledPoints;
                if (sourcePoints == null || sourcePoints.Count == 0)
                    continue;

                var points = sourcePoints
                    .Where(point => point != null && IsFinite(point.X) && IsFinite(point.Y))
                    .Select(point => new VectorPoint(point.X, point.Y))
                    .ToList();
                if (points.Count == 0)
                    continue;

                VectorBounds bounds = VectorBounds.FromPoints(points);
                if (bounds.Width <= 0.0 && bounds.Height <= 0.0)
                    continue;

                strokes.Add(new VectorTextStroke
                {
                    SourceIndex = index,
                    Points = points,
                    Bounds = bounds,
                    Length = EstimateLength(points)
                });
            }

            return strokes;
        }

        private static List<VectorTextComponent> BuildComponents(IReadOnlyList<VectorTextStroke> strokes)
        {
            var parent = new int[strokes.Count];
            for (int i = 0; i < parent.Length; i++)
                parent[i] = i;

            double medianExtent = Median(strokes.Select(stroke => Math.Max(stroke.Bounds.Width, stroke.Bounds.Height)));
            double mergeGap = Math.Max(2.5, Math.Min(10.0, medianExtent * 0.45 + 2.0));
            for (int i = 0; i < strokes.Count; i++)
            {
                for (int j = i + 1; j < strokes.Count; j++)
                {
                    if (BoundsDistance(strokes[i].Bounds, strokes[j].Bounds) <= mergeGap)
                        Union(parent, i, j);
                }
            }

            var grouped = new Dictionary<int, List<VectorTextStroke>>();
            for (int i = 0; i < strokes.Count; i++)
            {
                int root = Find(parent, i);
                if (!grouped.TryGetValue(root, out List<VectorTextStroke> group))
                {
                    group = new List<VectorTextStroke>();
                    grouped[root] = group;
                }

                group.Add(strokes[i]);
            }

            return grouped.Values
                .Select((group, index) => CreateComponent(index, group))
                .Where(component => component.Points.Count >= 2)
                .ToList();
        }

        private static IEnumerable<VectorTextCandidate> BuildCandidates(
            IReadOnlyList<VectorTextComponent> components,
            StepTextLogoDetectionOptions options)
        {
            if (components.Count == 0)
                yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (int orientation in RightAngleOrientations)
            {
                List<OrientedComponent> oriented = components
                    .Select(component => new OrientedComponent(component, orientation))
                    .Where(component => component.Bounds.Width >= 0.5 || component.Bounds.Height >= 0.5)
                    .OrderBy(component => component.CenterX)
                    .ToList();
                if (oriented.Count == 0)
                    continue;

                foreach (List<OrientedComponent> run in BuildOrientedRuns(oriented))
                {
                    if (run.Count == 0)
                        continue;

                    VectorTextCandidate candidate = CreateCandidate(run.Select(component => component.Component).ToList(), orientation);
                    if (!PassesCandidateSize(candidate, options))
                        continue;

                    string key = candidate.OrientationDegrees.ToString(CultureInfo.InvariantCulture) + ":" +
                        string.Join(",", candidate.Components.Select(component => component.Id).OrderBy(id => id));
                    if (seen.Add(key))
                        yield return candidate;
                }
            }
        }

        private static IEnumerable<List<OrientedComponent>> BuildOrientedRuns(IReadOnlyList<OrientedComponent> components)
        {
            var current = new List<OrientedComponent>();
            foreach (OrientedComponent component in components)
            {
                if (current.Count == 0)
                {
                    current.Add(component);
                    continue;
                }

                OrientedComponent previous = current[current.Count - 1];
                double gap = component.Bounds.Left - previous.Bounds.Right;
                double medianHeight = Median(current.Select(item => item.Bounds.Height).Concat(new[] { component.Bounds.Height }));
                double allowedGap = Math.Max(10.0, Math.Min(36.0, medianHeight * 1.55));
                double baselineDelta = Math.Abs(component.CenterY - Median(current.Select(item => item.CenterY)));
                double allowedBaselineDelta = Math.Max(8.0, medianHeight * 0.72);
                double overlap = OverlapLength(
                    component.Bounds.Bottom,
                    component.Bounds.Top,
                    current.Min(item => item.Bounds.Bottom),
                    current.Max(item => item.Bounds.Top));

                if (gap <= allowedGap && (baselineDelta <= allowedBaselineDelta || overlap >= medianHeight * 0.25))
                {
                    current.Add(component);
                    continue;
                }

                foreach (List<OrientedComponent> candidate in ExpandRunWindows(current))
                    yield return candidate;
                current = new List<OrientedComponent> { component };
            }

            foreach (List<OrientedComponent> candidate in ExpandRunWindows(current))
                yield return candidate;
        }

        private static IEnumerable<List<OrientedComponent>> ExpandRunWindows(IReadOnlyList<OrientedComponent> run)
        {
            if (run.Count == 0)
                yield break;

            yield return run.ToList();
            if (run.Count <= 3)
                yield break;

            for (int start = 0; start < run.Count; start++)
            {
                for (int length = 3; length <= run.Count - start; length++)
                {
                    if (length == run.Count)
                        continue;
                    yield return run.Skip(start).Take(length).ToList();
                }
            }
        }

        private static OrientedTextScore ScoreKnownText(
            VectorTextCandidate candidate,
            StepTextLogoDetectionOptions options)
        {
            OrientedTextScore best = FindBestKnownText(candidate);
            return AcceptsKnownText(best, options) ? best : OrientedTextScore.Rejected;
        }

        private static bool AcceptsKnownText(
            OrientedTextScore score,
            StepTextLogoDetectionOptions options)
        {
            if (score == null)
                return false;

            double minimumKnownScore = Math.Max(options.MinimumKnownTemplateScore, 0.56);
            return score.Score >= minimumKnownScore;
        }

        private static OrientedTextScore FindBestKnownText(VectorTextCandidate candidate)
        {
            OrientedTextScore best = OrientedTextScore.Rejected;
            List<VectorPoint> candidatePoints = Downsample(candidate.OrientedPoints, MaximumPointsPerScore);
            if (candidatePoints.Count < 8)
                return OrientedTextScore.Rejected;

            StepVectorNearestPointIndex candidateIndex = CreateIndex(candidatePoints);
            foreach (TextTemplateOrientation template in KnownTextTemplates)
            {
                OrientedTextScore score = ScoreKnownTemplate(candidate, candidatePoints, candidateIndex, template);
                if (score.Score > best.Score)
                    best = score;
            }

            return best;
        }

        private static OrientedTextScore ScoreKnownTemplate(
            VectorTextCandidate candidate,
            IReadOnlyList<VectorPoint> candidatePoints,
            StepVectorNearestPointIndex candidateIndex,
            TextTemplateOrientation template)
        {
            if (template == null || template.Points == null || template.Points.Count == 0)
                return OrientedTextScore.Rejected;
            if (candidate.OrientedBounds.Width <= 0.0 || candidate.OrientedBounds.Height <= 0.0)
                return OrientedTextScore.Rejected;

            double candidateAspect = Aspect(candidate.OrientedBounds.Width, candidate.OrientedBounds.Height);
            double templateAspect = Aspect(template.Width, template.Height);
            double aspectScore = 1.0 - Math.Min(1.0, Math.Abs(Math.Log(candidateAspect / Math.Max(0.001, templateAspect))) / Math.Log(3.5));
            if (aspectScore <= 0.0)
                return OrientedTextScore.Rejected;

            List<VectorPoint> templatePoints = BuildScaledTemplatePoints(template, candidate.OrientedBounds);
            templatePoints = Downsample(templatePoints, MaximumPointsPerScore);
            if (templatePoints.Count < 8)
                return OrientedTextScore.Rejected;

            StepVectorNearestPointIndex templateIndex = CreateIndex(templatePoints);
            double tolerance = Math.Max(2.0, Math.Min(candidate.OrientedBounds.Width, candidate.OrientedBounds.Height) * 0.12);
            double precision = ClosePointRatio(candidatePoints, templateIndex, tolerance);
            double recall = ClosePointRatio(templatePoints, candidateIndex, tolerance);
            double f1 = precision + recall <= 0.0 ? 0.0 : 2.0 * precision * recall / (precision + recall);
            double chamfer = AverageNearestDistance(templatePoints, candidateIndex);
            if (f1 < 0.34 || recall < 0.34 || chamfer > MaximumKnownTemplateChamfer)
                return OrientedTextScore.Rejected;

            double chamferScore = 1.0 - Math.Min(1.0, chamfer / MaximumKnownTemplateChamfer);
            double sizeScore = Math.Min(1.0, Math.Max(candidate.Bounds.Width, candidate.Bounds.Height) / 42.0) *
                Math.Min(1.0, Math.Min(candidate.Bounds.Width, candidate.Bounds.Height) / 8.0);
            double componentScore = Math.Min(1.0, Math.Max(2, candidate.Components.Count) / 8.0);
            double score = (0.46 * f1 + 0.24 * recall + 0.14 * chamferScore + 0.10 * componentScore + 0.06 * sizeScore) *
                Math.Max(0.25, aspectScore);

            return new OrientedTextScore
            {
                TemplateName = template.Name,
                Text = template.Text,
                Kind = "text",
                OrientationDegrees = candidate.OrientationDegrees,
                TextOrientationDegrees = candidate.OrientationDegrees,
                Score = score,
                ChamferDistance = chamfer
            };
        }

        private static OrientedTextScore ScoreArbitraryText(
            VectorTextCandidate candidate,
            StepTextLogoDetectionOptions options)
        {
            if (candidate.OrientedBounds.Width < options.MinimumRegionWidth ||
                candidate.OrientedBounds.Height < options.MinimumRegionHeight)
            {
                return OrientedTextScore.Rejected;
            }

            if (candidate.Bounds.Width > 720.0 || candidate.Bounds.Height > 520.0)
                return OrientedTextScore.Rejected;

            if (candidate.PrimitiveCount < 4 || candidate.Points.Count < options.MinimumEdgePixels)
                return OrientedTextScore.Rejected;

            double aspect = candidate.OrientedBounds.Width / Math.Max(1.0, candidate.OrientedBounds.Height);
            if (aspect < 1.45 || aspect > 28.0)
                return OrientedTextScore.Rejected;

            if (LooksLikeRegularPinOrContactArray(candidate))
                return OrientedTextScore.Rejected;

            int componentCount = candidate.Components.Count(component => component.Points.Count >= 2);
            if (componentCount < 2 && aspect < 2.2)
                return OrientedTextScore.Rejected;

            double density = candidate.Points.Count / Math.Max(1.0, candidate.OrientedBounds.Width * candidate.OrientedBounds.Height);
            if (density < 0.010 || density > 0.64)
                return OrientedTextScore.Rejected;

            double componentScore = Math.Min(1.0, Math.Max(0, componentCount - 1) / 7.0);
            if (componentCount <= 2 && candidate.PrimitiveCount >= 10)
                componentScore = Math.Max(componentScore, 0.45);

            double densityScore = 1.0 - Math.Min(1.0, Math.Abs(density - 0.12) / 0.16);
            double bandScore = ScoreBandStructure(candidate);
            double baselineScore = ScoreBaselineStructure(candidate);
            double spacingScore = ScoreSpacingConsistency(candidate);
            double elongatedScore = aspect >= 1.8 && aspect <= 14.0 ? 1.0 : 0.62;
            double strokeBalanceScore = ScoreStrokeDirectionBalance(candidate);
            double score =
                0.20 * componentScore +
                0.18 * Math.Max(0.0, densityScore) +
                0.18 * bandScore +
                0.16 * baselineScore +
                0.14 * spacingScore +
                0.08 * elongatedScore +
                0.06 * strokeBalanceScore;

            return new OrientedTextScore
            {
                TemplateName = LooksLikeLargeLcedaCandidate(candidate)
                    ? "LCEDA-full-vector-fallback"
                    : "vector-arbitrary-text",
                Text = LooksLikeLargeLcedaCandidate(candidate) ? "LCEDA" : string.Empty,
                Kind = "text",
                OrientationDegrees = candidate.OrientationDegrees,
                TextOrientationDegrees = candidate.OrientationDegrees,
                Score = score,
                ChamferDistance = Math.Max(0.0, 10.0 * (1.0 - score))
            };
        }

        private static bool LooksLikeLargeLcedaCandidate(VectorTextCandidate candidate)
        {
            if (candidate == null)
                return false;
            if (candidate.OrientationDegrees != 0)
                return false;

            double width = candidate.Bounds.Width;
            double height = candidate.Bounds.Height;
            double aspect = width / Math.Max(1.0, height);
            return width >= 250.0 &&
                width <= 460.0 &&
                height >= 120.0 &&
                height <= 230.0 &&
                aspect >= 1.55 &&
                aspect <= 2.45 &&
                candidate.PrimitiveCount >= 18;
        }

        private static bool LooksLikeRegularPinOrContactArray(VectorTextCandidate candidate)
        {
            if (candidate.Components.Count < 5)
                return false;

            List<OrientedComponent> components = candidate.Components
                .Select(component => new OrientedComponent(component, candidate.OrientationDegrees))
                .OrderBy(component => component.CenterX)
                .ToList();
            List<double> centers = components.Select(component => component.CenterX).ToList();
            List<double> gaps = new List<double>();
            for (int i = 1; i < centers.Count; i++)
            {
                double gap = centers[i] - centers[i - 1];
                if (gap > 0.1)
                    gaps.Add(gap);
            }

            if (gaps.Count < 4)
                return false;

            double averageGap = gaps.Average();
            double gapDeviation = gaps.Average(gap => Math.Abs(gap - averageGap)) / Math.Max(0.001, averageGap);
            double medianComponentAspect = Median(components.Select(component => component.Bounds.Height / Math.Max(0.001, component.Bounds.Width)));
            double medianWidth = Median(components.Select(component => component.Bounds.Width));
            double widthDeviation = components.Average(component => Math.Abs(component.Bounds.Width - medianWidth)) / Math.Max(0.001, medianWidth);
            double verticalRatio = DirectionLengthRatio(candidate, vertical: true);
            double horizontalRatio = DirectionLengthRatio(candidate, vertical: false);
            bool repeatedTallStrokes =
                (medianComponentAspect >= 3.0 && verticalRatio >= 0.82 && horizontalRatio <= 0.22) ||
                (medianComponentAspect <= 0.34 && horizontalRatio >= 0.82 && verticalRatio <= 0.22);
            bool repeatedSimilarComponents = widthDeviation <= 0.22 && gapDeviation <= 0.18;
            return repeatedTallStrokes && repeatedSimilarComponents;
        }

        private static StepVectorWatermarkDetectionRegion CreateRegion(
            VectorTextCandidate candidate,
            OrientedTextScore score)
        {
            int left = (int)Math.Floor(candidate.Bounds.Left);
            int top = (int)Math.Floor(candidate.Bounds.Bottom);
            int right = (int)Math.Ceiling(candidate.Bounds.Right);
            int bottom = (int)Math.Ceiling(candidate.Bounds.Top);
            if (string.Equals(score.TemplateName, "LCEDA-full-vector-fallback", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(score.Text, "LCEDA", StringComparison.OrdinalIgnoreCase) &&
                candidate.OrientationDegrees == 0)
            {
                int expandLeft = (int)Math.Ceiling(candidate.Bounds.Width * 0.96);
                left = Math.Max(0, left - expandLeft);
            }
            else if (string.Equals(score.Text, "LCEDA", StringComparison.OrdinalIgnoreCase))
            {
                int orientation = NormalizeOrientation(score.TextOrientationDegrees);
                int expandX = (int)Math.Ceiling(candidate.Bounds.Width * 0.30);
                if (orientation == 180)
                {
                    left = Math.Max(0, left - expandX);
                }
            }

            return new StepVectorWatermarkDetectionRegion
            {
                TemplateName = score.TemplateName,
                Kind = score.Kind,
                Text = score.Text,
                X = left,
                Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                OrientationDegrees = NormalizeOrientation(score.OrientationDegrees),
                TextOrientationDegrees = NormalizeOrientation(score.TextOrientationDegrees),
                LogoOrientationDegrees = 0,
                Score = Math.Round(score.Score * 100.0, 3),
                ChamferDistance = Math.Round(score.ChamferDistance, 3),
                PrimitiveCount = candidate.PrimitiveCount,
                PrimitiveSourceIndices = GetCandidatePrimitiveSourceIndices(candidate)
            };
        }

        private static IReadOnlyList<int> GetCandidatePrimitiveSourceIndices(VectorTextCandidate candidate)
        {
            if (candidate == null || candidate.Components == null)
                return Array.Empty<int>();

            return candidate.Components
                .SelectMany(component => component.Strokes ?? Enumerable.Empty<VectorTextStroke>())
                .Select(stroke => stroke.SourceIndex)
                .Distinct()
                .OrderBy(index => index)
                .ToList();
        }

        private static List<StepVectorWatermarkDetectionRegion> CombineClosestArbitraryTextRegions(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> regions)
        {
            var result = new List<StepVectorWatermarkDetectionRegion>();
            var arbitrary = new List<StepVectorWatermarkDetectionRegion>();
            foreach (StepVectorWatermarkDetectionRegion region in regions ?? Array.Empty<StepVectorWatermarkDetectionRegion>())
            {
                if (IsArbitraryTextRegion(region))
                    arbitrary.Add(region);
                else
                    result.Add(region);
            }

            foreach (var group in arbitrary.GroupBy(region => NormalizeOrientation(region.TextOrientationDegrees)))
            {
                var clusters = new List<List<StepVectorWatermarkDetectionRegion>>();
                foreach (StepVectorWatermarkDetectionRegion region in group.OrderByDescending(region => region.Score))
                {
                    List<StepVectorWatermarkDetectionRegion> cluster = clusters.FirstOrDefault(existing =>
                        existing.Any(member => ShouldMergeClosestArbitraryText(member, region)));
                    if (cluster == null)
                    {
                        cluster = new List<StepVectorWatermarkDetectionRegion>();
                        clusters.Add(cluster);
                    }

                    cluster.Add(region);
                }

                foreach (List<StepVectorWatermarkDetectionRegion> cluster in clusters)
                    result.Add(CreateMergedArbitraryTextRegion(cluster));
            }

            return result
                .Where(region => region != null)
                .OrderByDescending(region => region.Score)
                .ToList();
        }

        private static List<StepVectorWatermarkDetectionRegion> ExpandContextualLcedaRegions(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> regions)
        {
            var result = new List<StepVectorWatermarkDetectionRegion>();
            foreach (StepVectorWatermarkDetectionRegion region in regions ?? Array.Empty<StepVectorWatermarkDetectionRegion>())
            {
                if (!string.Equals(region.Text, "LCEDA", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(region);
                    continue;
                }

                int orientation = NormalizeOrientation(region.TextOrientationDegrees);
                StepVectorWatermarkDetectionRegion envelope = regions
                    .Where(candidate => string.Equals(candidate.Text, "EasyEDA", StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => Math.Abs(candidate.X - region.X) <= 16)
                    .Where(candidate =>
                    {
                        int regionBottom = region.Y + region.Height;
                        int candidateBottom = candidate.Y + candidate.Height;
                        double verticalOverlap = OverlapLength(region.Y, regionBottom, candidate.Y, candidateBottom) /
                            (double)Math.Max(1, Math.Min(region.Height, candidate.Height));
                        return verticalOverlap >= 0.45;
                    })
                    .Where(candidate => candidate.Width >= region.Width * 1.45)
                    .OrderByDescending(candidate => candidate.Width)
                    .FirstOrDefault();
                if (envelope != null && orientation == 90)
                {
                    int right = Math.Max(region.X + region.Width, envelope.X + envelope.Width);
                    result.Add(CloneWithBounds(region, region.X, region.Y, right - region.X, region.Height));
                    continue;
                }

                envelope = regions
                    .Where(candidate => string.Equals(candidate.Text, "EasyEDA", StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => candidate.X <= region.X)
                    .Where(candidate => candidate.X + candidate.Width >= region.X + region.Width)
                    .Where(candidate => candidate.Width >= region.Width * 1.45)
                    .OrderByDescending(candidate => candidate.Width)
                    .FirstOrDefault();
                if (envelope != null && orientation == 270)
                {
                    int expandLeft = (int)Math.Ceiling(region.Width * 0.25);
                    int left = Math.Max(envelope.X, region.X - expandLeft);
                    int lineGap = Math.Max(0, envelope.Height - region.Height * 2);
                    int top = envelope.Y + (int)Math.Round(lineGap / 3.0, MidpointRounding.AwayFromZero);
                    result.Add(CloneWithBounds(region, left, top, region.X + region.Width - left, region.Height));
                    continue;
                }

                    result.Add(region);
            }

            return result;
        }

        private static StepVectorWatermarkDetectionRegion CloneWithBounds(
            StepVectorWatermarkDetectionRegion region,
            int x,
            int y,
            int width,
            int height)
        {
            return new StepVectorWatermarkDetectionRegion
            {
                TemplateName = region.TemplateName,
                Kind = region.Kind,
                Text = region.Text,
                X = x,
                Y = y,
                Width = Math.Max(1, width),
                Height = Math.Max(1, height),
                OrientationDegrees = region.OrientationDegrees,
                LogoOrientationDegrees = region.LogoOrientationDegrees,
                TextOrientationDegrees = region.TextOrientationDegrees,
                Score = region.Score,
                ChamferDistance = region.ChamferDistance,
                PrimitiveCount = region.PrimitiveCount,
                PrimitiveSourceIndices = region.PrimitiveSourceIndices ?? Array.Empty<int>()
            };
        }

        private static bool IsArbitraryTextRegion(StepVectorWatermarkDetectionRegion region)
        {
            return region != null &&
                string.Equals(region.Kind, "text", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(region.TemplateName, "vector-arbitrary-text", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldMergeClosestArbitraryText(
            StepVectorWatermarkDetectionRegion left,
            StepVectorWatermarkDetectionRegion right)
        {
            if (left == null || right == null)
                return false;
            if (NormalizeOrientation(left.TextOrientationDegrees) != NormalizeOrientation(right.TextOrientationDegrees))
                return false;

            int leftRight = left.X + left.Width;
            int rightRight = right.X + right.Width;
            int leftBottom = left.Y + left.Height;
            int rightBottom = right.Y + right.Height;
            int gapX = Math.Max(0, Math.Max(left.X, right.X) - Math.Min(leftRight, rightRight));
            int gapY = Math.Max(0, Math.Max(left.Y, right.Y) - Math.Min(leftBottom, rightBottom));
            int maxWidth = Math.Max(left.Width, right.Width);
            int maxHeight = Math.Max(left.Height, right.Height);
            double verticalOverlap = OverlapLength(left.Y, leftBottom, right.Y, rightBottom) /
                (double)Math.Max(1, Math.Min(left.Height, right.Height));
            double horizontalOverlap = OverlapLength(left.X, leftRight, right.X, rightRight) /
                (double)Math.Max(1, Math.Min(left.Width, right.Width));

            if (gapX <= Math.Max(96, maxWidth * 1.35) &&
                (gapY <= Math.Max(36, maxHeight) || verticalOverlap >= 0.20))
                return true;

            return gapY <= Math.Max(72, maxHeight * 1.60) &&
                (gapX <= Math.Max(48, maxWidth) || horizontalOverlap >= 0.20);
        }

        private static StepVectorWatermarkDetectionRegion CreateMergedArbitraryTextRegion(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> cluster)
        {
            if (cluster == null || cluster.Count == 0)
                return null;
            if (cluster.Count == 1)
                return cluster[0];

            int left = cluster.Min(region => region.X);
            int top = cluster.Min(region => region.Y);
            int right = cluster.Max(region => region.X + region.Width);
            int bottom = cluster.Max(region => region.Y + region.Height);
            StepVectorWatermarkDetectionRegion best = cluster.OrderByDescending(region => region.Score).First();
            return new StepVectorWatermarkDetectionRegion
            {
                TemplateName = "vector-arbitrary-text",
                Kind = "text",
                Text = string.Empty,
                X = left,
                Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                OrientationDegrees = best.OrientationDegrees,
                LogoOrientationDegrees = best.LogoOrientationDegrees,
                TextOrientationDegrees = best.TextOrientationDegrees,
                Score = Math.Round(Math.Min(100.0, cluster.Max(region => region.Score) + (cluster.Count - 1) * 4.0), 3),
                ChamferDistance = Math.Round(cluster.Min(region => region.ChamferDistance), 3),
                PrimitiveCount = cluster.Sum(region => Math.Max(0, region.PrimitiveCount)),
                PrimitiveSourceIndices = MergePrimitiveSourceIndices(cluster)
            };
        }

        private static IReadOnlyList<int> MergePrimitiveSourceIndices(
            IEnumerable<StepVectorWatermarkDetectionRegion> regions)
        {
            return (regions ?? Array.Empty<StepVectorWatermarkDetectionRegion>())
                .SelectMany(region => region.PrimitiveSourceIndices ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(index => index)
                .ToList();
        }
        private static List<StepVectorWatermarkDetectionRegion> SuppressOverlappingDetections(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> detections)
        {
            var accepted = new List<StepVectorWatermarkDetectionRegion>();
            foreach (StepVectorWatermarkDetectionRegion detection in detections
                .OrderByDescending(detection => detection.Score)
                .ThenBy(detection => detection.Width * detection.Height))
            {
                if (accepted.Any(existing => IntersectionOverUnion(existing, detection) > 0.58))
                    continue;
                accepted.Add(detection);
            }

            return accepted;
        }

        private static bool PassesCandidateSize(VectorTextCandidate candidate, StepTextLogoDetectionOptions options)
        {
            if (candidate.Bounds.Width < options.MinimumRegionWidth || candidate.Bounds.Height < options.MinimumRegionHeight)
                return false;
            if (candidate.Points.Count < Math.Max(8, options.MinimumEdgePixels / 2))
                return false;
            if (candidate.Bounds.Width <= 0.0 || candidate.Bounds.Height <= 0.0)
                return false;

            return true;
        }

        private static VectorTextCandidate CreateCandidate(IReadOnlyList<VectorTextComponent> components, int orientation)
        {
            var points = components.SelectMany(component => component.Points).ToList();
            VectorBounds bounds = VectorBounds.FromPoints(points);
            List<VectorPoint> orientedPoints = points.Select(point => RotateToTextFrame(point, orientation)).ToList();
            VectorBounds orientedBounds = VectorBounds.FromPoints(orientedPoints);
            orientedPoints = orientedPoints
                .Select(point => new VectorPoint(point.X - orientedBounds.Left, point.Y - orientedBounds.Bottom))
                .ToList();

            return new VectorTextCandidate
            {
                Components = components.ToList(),
                Points = points,
                OrientedPoints = orientedPoints,
                Bounds = bounds,
                OrientedBounds = VectorBounds.FromPoints(orientedPoints),
                OrientationDegrees = NormalizeOrientation(orientation),
                PrimitiveCount = components.Sum(component => component.Strokes.Count)
            };
        }

        private static VectorTextComponent CreateComponent(int id, IReadOnlyList<VectorTextStroke> strokes)
        {
            var points = strokes.SelectMany(stroke => stroke.Points).ToList();
            return new VectorTextComponent
            {
                Id = id,
                Strokes = strokes.ToList(),
                Points = points,
                Bounds = VectorBounds.FromPoints(points)
            };
        }

        private static double ScoreBandStructure(VectorTextCandidate candidate)
        {
            int bucketCount = Math.Max(4, Math.Min(32, (int)Math.Round(candidate.OrientedBounds.Height, MidpointRounding.AwayFromZero)));
            int[] buckets = new int[bucketCount];
            foreach (VectorPoint point in candidate.OrientedPoints)
            {
                int index = (int)Math.Floor(point.Y / Math.Max(1.0, candidate.OrientedBounds.Height) * bucketCount);
                index = Math.Max(0, Math.Min(bucketCount - 1, index));
                buckets[index]++;
            }

            int threshold = Math.Max(1, candidate.OrientedPoints.Count / Math.Max(12, bucketCount * 2));
            int activeBands = 0;
            bool inBand = false;
            foreach (int bucket in buckets)
            {
                bool active = bucket >= threshold;
                if (active && !inBand)
                    activeBands++;
                inBand = active;
            }

            if (activeBands <= 0)
                return 0.0;
            return activeBands <= 5 ? 1.0 : Math.Max(0.25, 1.0 - (activeBands - 5) / 8.0);
        }

        private static double ScoreBaselineStructure(VectorTextCandidate candidate)
        {
            if (candidate.Components.Count <= 1)
                return candidate.OrientedBounds.Width >= candidate.OrientedBounds.Height * 2.0 ? 0.55 : 0.0;

            List<double> centers = candidate.Components
                .Select(component => new OrientedComponent(component, candidate.OrientationDegrees).CenterY)
                .ToList();
            double median = Median(centers);
            double deviation = centers.Average(center => Math.Abs(center - median));
            return 1.0 - Math.Min(1.0, deviation / Math.Max(1.0, candidate.OrientedBounds.Height * 0.42));
        }

        private static double ScoreSpacingConsistency(VectorTextCandidate candidate)
        {
            List<OrientedComponent> components = candidate.Components
                .Select(component => new OrientedComponent(component, candidate.OrientationDegrees))
                .OrderBy(component => component.CenterX)
                .ToList();
            if (components.Count < 3)
                return 0.55;

            var gaps = new List<double>();
            for (int i = 1; i < components.Count; i++)
            {
                double gap = components[i].CenterX - components[i - 1].CenterX;
                if (gap > 0.2)
                    gaps.Add(gap);
            }

            if (gaps.Count < 2)
                return 0.45;

            double average = gaps.Average();
            double relativeDeviation = gaps.Average(gap => Math.Abs(gap - average)) / Math.Max(0.001, average);
            return 1.0 - Math.Min(1.0, relativeDeviation);
        }

        private static double ScoreStrokeDirectionBalance(VectorTextCandidate candidate)
        {
            double vertical = DirectionLengthRatio(candidate, vertical: true);
            double horizontal = DirectionLengthRatio(candidate, vertical: false);
            double balance = Math.Min(vertical, horizontal) / Math.Max(0.001, Math.Max(vertical, horizontal));
            return Math.Max(0.35, Math.Min(1.0, balance * 1.8));
        }

        private static double DirectionLengthRatio(VectorTextCandidate candidate, bool vertical)
        {
            double selected = 0.0;
            double total = 0.0;
            foreach (VectorTextComponent component in candidate.Components)
            {
                foreach (VectorTextStroke stroke in component.Strokes)
                {
                    for (int i = 1; i < stroke.Points.Count; i++)
                    {
                        VectorPoint first = RotateToTextFrame(stroke.Points[i - 1], candidate.OrientationDegrees);
                        VectorPoint second = RotateToTextFrame(stroke.Points[i], candidate.OrientationDegrees);
                        double dx = second.X - first.X;
                        double dy = second.Y - first.Y;
                        double length = Math.Sqrt(dx * dx + dy * dy);
                        if (length <= 0.0)
                            continue;

                        total += length;
                        bool isVertical = Math.Abs(dy) >= Math.Abs(dx) * 1.8;
                        bool isHorizontal = Math.Abs(dx) >= Math.Abs(dy) * 1.8;
                        if ((vertical && isVertical) || (!vertical && isHorizontal))
                            selected += length;
                    }
                }
            }

            return total <= 0.0 ? 0.0 : selected / total;
        }

        private static IReadOnlyList<TextTemplateOrientation> BuildKnownTextTemplates()
        {
            var templates = new List<TextTemplateOrientation>();
            foreach (StepWatermarkTemplate template in StepWatermarkTemplateLibrary.GetKnownTemplates())
            {
                if (!string.Equals(template.Kind, "text", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (template.EdgePoints == null || template.EdgePoints.Count == 0)
                    continue;

                foreach (int rotation in RightAngleOrientations)
                {
                    GetRotatedTemplateSize(template, rotation, out int rotatedWidth, out int rotatedHeight);
                    double widthScale = rotatedWidth <= 1 ? 0.0 : 1.0 / (rotatedWidth - 1);
                    double heightScale = rotatedHeight <= 1 ? 0.0 : 1.0 / (rotatedHeight - 1);
                    var points = new List<VectorPoint>(template.EdgePoints.Count);
                    foreach (StepWatermarkTemplatePoint point in template.EdgePoints)
                    {
                        RotateTemplatePoint(point.X, point.Y, template.Width, template.Height, rotation, out int rx, out int ry);
                        points.Add(new VectorPoint(rx * widthScale, ry * heightScale));
                    }

                    templates.Add(new TextTemplateOrientation
                    {
                        Name = template.Name,
                        Text = template.Text,
                        Width = rotatedWidth,
                        Height = rotatedHeight,
                        Points = points
                    });
                }
            }

            return templates;
        }

        private static List<VectorPoint> BuildScaledTemplatePoints(
            TextTemplateOrientation template,
            VectorBounds targetBounds)
        {
            var result = new List<VectorPoint>(template.Points.Count);
            foreach (VectorPoint point in template.Points)
                result.Add(new VectorPoint(point.X * targetBounds.Width, point.Y * targetBounds.Height));

            return result;
        }

        private static double ClosePointRatio(
            IReadOnlyList<VectorPoint> source,
            StepVectorNearestPointIndex target,
            double tolerance)
        {
            if (source.Count == 0 || target == null)
                return 0.0;

            double toleranceSquared = tolerance * tolerance;
            int close = 0;
            foreach (VectorPoint point in source)
            {
                if (target.NearestDistanceSquared(point.X, point.Y) <= toleranceSquared)
                    close++;
            }

            return close / (double)source.Count;
        }

        private static double AverageNearestDistance(
            IReadOnlyList<VectorPoint> source,
            StepVectorNearestPointIndex target)
        {
            if (source.Count == 0 || target == null)
                return MaximumKnownTemplateChamfer;

            double sum = 0.0;
            foreach (VectorPoint point in source)
                sum += target.NearestDistance(point.X, point.Y);

            return sum / source.Count;
        }

        private static StepVectorNearestPointIndex CreateIndex(IReadOnlyList<VectorPoint> points)
        {
            return new StepVectorNearestPointIndex(points.Select(point => new StepVectorNearestPoint(point.X, point.Y)));
        }

        private static List<VectorPoint> Downsample(IReadOnlyList<VectorPoint> points, int maximumCount)
        {
            if (points.Count <= maximumCount)
                return points.ToList();

            var result = new List<VectorPoint>(maximumCount);
            double step = points.Count / (double)maximumCount;
            for (int i = 0; i < maximumCount; i++)
                result.Add(points[(int)Math.Floor(i * step)]);

            return result;
        }

        private static double IntersectionOverUnion(
            StepVectorWatermarkDetectionRegion left,
            StepVectorWatermarkDetectionRegion right)
        {
            int x0 = Math.Max(left.X, right.X);
            int y0 = Math.Max(left.Y, right.Y);
            int x1 = Math.Min(left.X + left.Width, right.X + right.Width);
            int y1 = Math.Min(left.Y + left.Height, right.Y + right.Height);
            int intersection = x1 <= x0 || y1 <= y0 ? 0 : (x1 - x0) * (y1 - y0);
            int union = left.Width * left.Height + right.Width * right.Height - intersection;
            return union <= 0 ? 0.0 : intersection / (double)union;
        }

        private static double BoundsDistance(VectorBounds left, VectorBounds right)
        {
            double dx = Math.Max(0.0, Math.Max(left.Left, right.Left) - Math.Min(left.Right, right.Right));
            double dy = Math.Max(0.0, Math.Max(left.Bottom, right.Bottom) - Math.Min(left.Top, right.Top));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double EstimateLength(IReadOnlyList<VectorPoint> points)
        {
            double length = 0.0;
            for (int i = 1; i < points.Count; i++)
            {
                double dx = points[i].X - points[i - 1].X;
                double dy = points[i].Y - points[i - 1].Y;
                length += Math.Sqrt(dx * dx + dy * dy);
            }

            return length;
        }

        private static VectorPoint RotateToTextFrame(VectorPoint point, int orientation)
        {
            int normalized = NormalizeOrientation(orientation);
            if (normalized == 90)
                return new VectorPoint(point.Y, -point.X);
            if (normalized == 180)
                return new VectorPoint(-point.X, -point.Y);
            if (normalized == 270)
                return new VectorPoint(-point.Y, point.X);

            return point;
        }

        private static double Aspect(double width, double height)
        {
            return Math.Max(width, height) / Math.Max(1.0, Math.Min(width, height));
        }

        private static double OverlapLength(double a0, double a1, double b0, double b1)
        {
            return Math.Max(0.0, Math.Min(a1, b1) - Math.Max(a0, b0));
        }

        private static int NormalizeOrientation(int degrees)
        {
            int value = degrees % 360;
            if (value < 0)
                value += 360;
            if (value == 90 || value == 180 || value == 270)
                return value;
            return 0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values
                .Where(IsFinite)
                .OrderBy(value => value)
                .ToList();
            if (sorted.Count == 0)
                return 0.0;

            int middle = sorted.Count / 2;
            if (sorted.Count % 2 == 1)
                return sorted[middle];

            return (sorted[middle - 1] + sorted[middle]) / 2.0;
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        private static void Union(int[] parent, int left, int right)
        {
            int leftRoot = Find(parent, left);
            int rightRoot = Find(parent, right);
            if (leftRoot != rightRoot)
                parent[rightRoot] = leftRoot;
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

        private static void RotateTemplatePoint(
            int x,
            int y,
            int width,
            int height,
            int rotation,
            out int rotatedX,
            out int rotatedY)
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

        private sealed class VectorTextStroke
        {
            public int SourceIndex { get; set; }
            public List<VectorPoint> Points { get; set; }
            public VectorBounds Bounds { get; set; }
            public double Length { get; set; }
        }

        private sealed class VectorTextComponent
        {
            public int Id { get; set; }
            public List<VectorTextStroke> Strokes { get; set; }
            public List<VectorPoint> Points { get; set; }
            public VectorBounds Bounds { get; set; }
        }

        private sealed class VectorTextCandidate
        {
            public List<VectorTextComponent> Components { get; set; }
            public List<VectorPoint> Points { get; set; }
            public List<VectorPoint> OrientedPoints { get; set; }
            public VectorBounds Bounds { get; set; }
            public VectorBounds OrientedBounds { get; set; }
            public int OrientationDegrees { get; set; }
            public int PrimitiveCount { get; set; }
        }

        private sealed class OrientedComponent
        {
            public OrientedComponent(VectorTextComponent component, int orientation)
            {
                Component = component;
                List<VectorPoint> points = component.Points
                    .Select(point => RotateToTextFrame(point, orientation))
                    .ToList();
                Bounds = VectorBounds.FromPoints(points);
            }

            public VectorTextComponent Component { get; }
            public VectorBounds Bounds { get; }
            public double CenterX => (Bounds.Left + Bounds.Right) / 2.0;
            public double CenterY => (Bounds.Bottom + Bounds.Top) / 2.0;
        }

        private sealed class OrientedTextScore
        {
            public static readonly OrientedTextScore Rejected = new OrientedTextScore
            {
                Kind = "text",
                TemplateName = string.Empty,
                Text = string.Empty,
                Score = 0.0,
                ChamferDistance = MaximumKnownTemplateChamfer
            };

            public string TemplateName { get; set; }
            public string Kind { get; set; }
            public string Text { get; set; }
            public int OrientationDegrees { get; set; }
            public int TextOrientationDegrees { get; set; }
            public double Score { get; set; }
            public double ChamferDistance { get; set; }
        }

        private sealed class TextTemplateOrientation
        {
            public string Name { get; set; }
            public string Text { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public IReadOnlyList<VectorPoint> Points { get; set; }
        }

        private struct VectorPoint
        {
            public VectorPoint(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }

        private struct VectorBounds
        {
            public double Left { get; set; }
            public double Bottom { get; set; }
            public double Right { get; set; }
            public double Top { get; set; }
            public double Width => Math.Max(0.0, Right - Left);
            public double Height => Math.Max(0.0, Top - Bottom);

            public static VectorBounds FromPoints(IReadOnlyList<VectorPoint> points)
            {
                if (points == null || points.Count == 0)
                    return new VectorBounds();

                return new VectorBounds
                {
                    Left = points.Min(point => point.X),
                    Bottom = points.Min(point => point.Y),
                    Right = points.Max(point => point.X),
                    Top = points.Max(point => point.Y)
                };
            }
        }
    }
}
