using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyEDA_Loader
{
    public static class StepVectorLogoDetector
    {
        private static readonly int[] LogoOrientations = { 0, 90, 180, 270 };
        private const int MaximumReturnedDetections = 1;
        private const double MinimumLogoScore = 0.58;
        private const double MaximumLogoChamferPixels = 18.0;

        public static IReadOnlyList<StepVectorWatermarkDetectionRegion> Detect(
            StepVectorWatermarkDetectionInput input,
            StepTextLogoDetectionOptions options)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            StepWatermarkTemplate logoTemplate = StepWatermarkTemplateLibrary.GetKnownTemplates()
                .FirstOrDefault(template =>
                    string.Equals(template.Name, "easyeda-logo", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(template.Kind, "logo", StringComparison.OrdinalIgnoreCase));
            if (logoTemplate == null ||
                logoTemplate.EdgePoints == null ||
                logoTemplate.EdgePoints.Count == 0 ||
                input.Primitives == null ||
                input.Primitives.Count == 0)
            {
                return Array.Empty<StepVectorWatermarkDetectionRegion>();
            }

            double minimumScore = Math.Max(
                MinimumLogoScore,
                options == null ? 0.0 : options.MinimumKnownTemplateScore);

            List<VectorPrimitiveStroke> strokes = input.Primitives
                .Where(IsUsablePrimitive)
                .Select(VectorPrimitiveStroke.FromPrimitive)
                .Where(stroke => stroke != null && !LooksLikeObviousMechanicalPrimitive(stroke, input))
                .ToList();
            if (strokes.Count == 0)
                return Array.Empty<StepVectorWatermarkDetectionRegion>();

            List<VectorStrokeCluster> clusters = BuildCompactClusters(strokes, input);
            var detections = new List<StepVectorWatermarkDetectionRegion>();
            foreach (VectorStrokeCluster cluster in clusters)
            {
                if (cluster.Points.Count < Math.Max(24, options == null ? 24 : options.MinimumEdgePixels))
                    continue;
                if (LooksLikeWholeBodyOrMechanicalCluster(cluster, input))
                    continue;

                LogoClusterScore best = ScoreCluster(cluster, logoTemplate);
                if (best.Score < minimumScore)
                    continue;
                if (best.ChamferDistance > MaximumLogoChamferPixels)
                    continue;

                detections.Add(ToRegion(cluster, logoTemplate, best, input));
            }

            return SuppressOverlappingDetections(detections)
                .Take(MaximumReturnedDetections)
                .ToList();
        }

        private static bool IsUsablePrimitive(StepVectorWatermarkPrimitive primitive)
        {
            if (primitive == null ||
                primitive.SampledImagePoints == null ||
                primitive.SampledImagePoints.Count == 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(primitive.Visibility) &&
                primitive.Visibility.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            StepVectorWatermarkBounds bounds = primitive.ImageBounds ?? BoundsForPoints(primitive.SampledImagePoints);
            return bounds.Width > 0.0 || bounds.Height > 0.0;
        }

        private static List<VectorStrokeCluster> BuildCompactClusters(
            IReadOnlyList<VectorPrimitiveStroke> strokes,
            StepVectorWatermarkDetectionInput input)
        {
            var clusters = new List<VectorStrokeCluster>();
            var visited = new bool[strokes.Count];
            for (int start = 0; start < strokes.Count; start++)
            {
                if (visited[start])
                    continue;

                visited[start] = true;
                var queue = new Queue<int>();
                queue.Enqueue(start);
                var clusterStrokes = new List<VectorPrimitiveStroke>();
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    VectorPrimitiveStroke currentStroke = strokes[current];
                    clusterStrokes.Add(currentStroke);

                    for (int candidate = 0; candidate < strokes.Count; candidate++)
                    {
                        if (visited[candidate])
                            continue;
                        if (!ShouldJoinStrokes(currentStroke, strokes[candidate], input))
                            continue;

                        visited[candidate] = true;
                        queue.Enqueue(candidate);
                    }
                }

                VectorStrokeCluster cluster = VectorStrokeCluster.FromStrokes(clusterStrokes);
                if (cluster != null)
                    clusters.Add(cluster);
            }

            return clusters;
        }

        private static bool ShouldJoinStrokes(
            VectorPrimitiveStroke left,
            VectorPrimitiveStroke right,
            StepVectorWatermarkDetectionInput input)
        {
            double imageLong = Math.Max(1.0, Math.Max(input.ImageWidth, input.ImageHeight));
            double maxGap = Math.Min(
                26.0,
                Math.Max(8.0, imageLong * 0.018));
            double localGap = Math.Max(
                maxGap,
                Math.Min(Math.Max(left.Bounds.Width, left.Bounds.Height), Math.Max(right.Bounds.Width, right.Bounds.Height)) * 0.85);
            return BoundsGap(left.Bounds, right.Bounds) <= localGap;
        }

        private static LogoClusterScore ScoreCluster(VectorStrokeCluster cluster, StepWatermarkTemplate logoTemplate)
        {
            LogoClusterScore best = LogoClusterScore.Rejected;
            foreach (int orientation in LogoOrientations)
            {
                RotatedTemplate rotated = RotatedTemplate.Create(logoTemplate, orientation);
                if (rotated.Points.Count == 0)
                    continue;

                double candidateAspect = cluster.Bounds.Width / Math.Max(1.0, cluster.Bounds.Height);
                double templateAspect = rotated.Width / Math.Max(1.0, rotated.Height);
                double aspectScore = 1.0 - Math.Min(
                    1.0,
                    Math.Abs(Math.Log(candidateAspect / Math.Max(0.001, templateAspect))) / Math.Log(2.8));
                if (aspectScore < 0.22)
                    continue;

                List<UnitPoint> candidatePoints = NormalizePoints(cluster.Points, cluster.Bounds, 700);
                List<UnitPoint> templatePoints = NormalizeTemplatePoints(rotated, 700);
                double forward = AverageNearestDistance(candidatePoints, templatePoints);
                double reverse = AverageNearestDistance(templatePoints, candidatePoints);
                double distance = (forward + reverse) * 0.5;
                double chamferPixels = distance * Math.Max(cluster.Bounds.Width, cluster.Bounds.Height);
                double chamferScore = 1.0 - Math.Min(1.0, distance / 0.135);

                double candidateSupport = SupportRatio(candidatePoints, templatePoints, 0.042);
                double templateSupport = SupportRatio(templatePoints, candidatePoints, 0.042);
                double overlapScore = Math.Sqrt(Math.Max(0.0, candidateSupport * templateSupport));
                double longDimension = Math.Max(cluster.Bounds.Width, cluster.Bounds.Height);
                double shortDimension = Math.Min(cluster.Bounds.Width, cluster.Bounds.Height);
                double sizeScore = Math.Min(1.0, longDimension / 46.0) * Math.Min(1.0, shortDimension / 15.0);
                double compactnessScore = CompactnessScore(cluster);
                double score =
                    0.44 * Math.Max(0.0, chamferScore) +
                    0.24 * Math.Max(0.0, overlapScore) +
                    0.18 * Math.Max(0.0, aspectScore) +
                    0.08 * Math.Max(0.0, sizeScore) +
                    0.06 * Math.Max(0.0, compactnessScore);

                if (score > best.Score)
                {
                    best = new LogoClusterScore
                    {
                        Score = score,
                        ChamferDistance = chamferPixels,
                        OrientationDegrees = orientation
                    };
                }
            }

            return best;
        }

        private static StepVectorWatermarkDetectionRegion ToRegion(
            VectorStrokeCluster cluster,
            StepWatermarkTemplate logoTemplate,
            LogoClusterScore score,
            StepVectorWatermarkDetectionInput input)
        {
            int left = Math.Max(0, (int)Math.Floor(cluster.Bounds.Left));
            int top = Math.Max(0, (int)Math.Floor(cluster.Bounds.Bottom));
            int right = input.ImageWidth > 0
                ? Math.Min(input.ImageWidth, (int)Math.Ceiling(cluster.Bounds.Right))
                : (int)Math.Ceiling(cluster.Bounds.Right);
            int bottom = input.ImageHeight > 0
                ? Math.Min(input.ImageHeight, (int)Math.Ceiling(cluster.Bounds.Top))
                : (int)Math.Ceiling(cluster.Bounds.Top);

            return new StepVectorWatermarkDetectionRegion
            {
                TemplateName = logoTemplate.Name,
                Kind = "logo",
                Text = string.Empty,
                X = left,
                Y = top,
                Width = Math.Max(1, right - left + 1),
                Height = Math.Max(1, bottom - top + 1),
                OrientationDegrees = score.OrientationDegrees,
                LogoOrientationDegrees = score.OrientationDegrees,
                TextOrientationDegrees = 0,
                Score = Math.Round(score.Score * 100.0, 3),
                ChamferDistance = Math.Round(score.ChamferDistance, 3),
                PrimitiveCount = cluster.Strokes.Count
            };
        }

        private static List<StepVectorWatermarkDetectionRegion> SuppressOverlappingDetections(
            IEnumerable<StepVectorWatermarkDetectionRegion> detections)
        {
            var accepted = new List<StepVectorWatermarkDetectionRegion>();
            foreach (StepVectorWatermarkDetectionRegion detection in detections.OrderByDescending(region => region.Score))
            {
                if (accepted.Any(existing => IntersectionOverUnion(existing, detection) > 0.52))
                    continue;
                accepted.Add(detection);
            }

            return accepted;
        }

        private static bool LooksLikeObviousMechanicalPrimitive(
            VectorPrimitiveStroke stroke,
            StepVectorWatermarkDetectionInput input)
        {
            double width = stroke.Bounds.Width;
            double height = stroke.Bounds.Height;
            double longDimension = Math.Max(width, height);
            double shortDimension = Math.Min(width, height);
            double imageLong = Math.Max(1.0, Math.Max(input.ImageWidth, input.ImageHeight));

            if (longDimension > imageLong * 0.42 && shortDimension <= 5.0)
                return true;
            if (longDimension > imageLong * 0.62)
                return true;

            return false;
        }

        private static bool LooksLikeWholeBodyOrMechanicalCluster(
            VectorStrokeCluster cluster,
            StepVectorWatermarkDetectionInput input)
        {
            double imageWidth = Math.Max(1.0, input.ImageWidth);
            double imageHeight = Math.Max(1.0, input.ImageHeight);
            double imageArea = imageWidth * imageHeight;
            double area = cluster.Bounds.Width * cluster.Bounds.Height;
            if (area > imageArea * 0.10)
                return true;
            if (cluster.Bounds.Width > imageWidth * 0.54 || cluster.Bounds.Height > imageHeight * 0.54)
                return true;

            double longDimension = Math.Max(cluster.Bounds.Width, cluster.Bounds.Height);
            double shortDimension = Math.Min(cluster.Bounds.Width, cluster.Bounds.Height);
            if (longDimension > 90.0 &&
                shortDimension <= 5.0 &&
                cluster.Strokes.Count <= 3)
            {
                return true;
            }

            if (LooksLikeRegularConnectorArray(cluster))
                return true;

            return false;
        }

        private static bool LooksLikeRegularConnectorArray(VectorStrokeCluster cluster)
        {
            if (cluster.Strokes.Count < 8)
                return false;

            double spanX = cluster.Bounds.Width;
            double spanY = cluster.Bounds.Height;
            double ratio = Math.Max(spanX, spanY) / Math.Max(1.0, Math.Min(spanX, spanY));
            if (ratio < 5.0)
                return false;

            bool horizontal = spanX >= spanY;
            List<double> centers = cluster.Strokes
                .Select(stroke => horizontal ? stroke.CenterX : stroke.CenterY)
                .OrderBy(value => value)
                .ToList();
            var gaps = new List<double>();
            for (int index = 1; index < centers.Count; index++)
            {
                double gap = centers[index] - centers[index - 1];
                if (gap > 1.0)
                    gaps.Add(gap);
            }

            if (gaps.Count < 5)
                return false;

            double average = gaps.Average();
            if (average <= 1.0)
                return false;

            double variance = gaps.Sum(gap => Math.Pow(gap - average, 2.0)) / gaps.Count;
            double regularity = Math.Sqrt(variance) / average;
            double maxPrimitiveLong = cluster.Strokes.Max(stroke => Math.Max(stroke.Bounds.Width, stroke.Bounds.Height));
            return regularity < 0.18 && maxPrimitiveLong < Math.Max(12.0, average * 1.6);
        }

        private static double CompactnessScore(VectorStrokeCluster cluster)
        {
            double area = Math.Max(1.0, cluster.Bounds.Width * cluster.Bounds.Height);
            double uniqueCells = cluster.Points
                .Select(point => ((int)Math.Round((point.X - cluster.Bounds.Left) / 3.0)).ToString(CultureInfo.InvariantCulture) +
                    ":" +
                    ((int)Math.Round((point.Y - cluster.Bounds.Top) / 3.0)).ToString(CultureInfo.InvariantCulture))
                .Distinct(StringComparer.Ordinal)
                .Count();
            double density = uniqueCells / area;
            if (density < 0.002)
                return 0.0;
            if (density > 0.035)
                return 1.0;
            return density / 0.035;
        }

        private static List<UnitPoint> NormalizePoints(
            IReadOnlyList<StepVectorWatermarkPoint> points,
            StepVectorWatermarkBounds bounds,
            int maximumPoints)
        {
            double width = Math.Max(1.0, bounds.Width);
            double height = Math.Max(1.0, bounds.Height);
            IEnumerable<StepVectorWatermarkPoint> selected = Downsample(points, maximumPoints);
            return selected
                .Select(point => new UnitPoint(
                    (point.X - bounds.Left) / width,
                    (point.Y - bounds.Bottom) / height))
                .ToList();
        }

        private static List<UnitPoint> NormalizeTemplatePoints(RotatedTemplate template, int maximumPoints)
        {
            double width = Math.Max(1.0, template.Width - 1.0);
            double height = Math.Max(1.0, template.Height - 1.0);
            return Downsample(template.Points, maximumPoints)
                .Select(point => new UnitPoint(point.X / width, point.Y / height))
                .ToList();
        }

        private static IEnumerable<T> Downsample<T>(IReadOnlyList<T> points, int maximumPoints)
        {
            if (points.Count <= maximumPoints)
                return points;

            int step = Math.Max(1, (int)Math.Ceiling(points.Count / (double)maximumPoints));
            return points.Where((_, index) => index % step == 0).Take(maximumPoints);
        }

        private static double AverageNearestDistance(IReadOnlyList<UnitPoint> source, IReadOnlyList<UnitPoint> target)
        {
            if (source.Count == 0 || target.Count == 0)
                return 1.0;

            double sum = 0.0;
            foreach (UnitPoint point in source)
                sum += NearestDistance(point, target);
            return sum / source.Count;
        }

        private static double SupportRatio(IReadOnlyList<UnitPoint> source, IReadOnlyList<UnitPoint> target, double maximumDistance)
        {
            if (source.Count == 0 || target.Count == 0)
                return 0.0;

            int supported = 0;
            foreach (UnitPoint point in source)
            {
                if (NearestDistance(point, target) <= maximumDistance)
                    supported++;
            }

            return supported / (double)source.Count;
        }

        private static double NearestDistance(UnitPoint point, IReadOnlyList<UnitPoint> target)
        {
            double best = double.MaxValue;
            foreach (UnitPoint candidate in target)
            {
                double dx = point.X - candidate.X;
                double dy = point.Y - candidate.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < best)
                    best = distance;
            }

            return best;
        }

        private static double BoundsGap(StepVectorWatermarkBounds left, StepVectorWatermarkBounds right)
        {
            double gapX = Math.Max(0.0, Math.Max(left.Left, right.Left) - Math.Min(left.Right, right.Right));
            double gapY = Math.Max(0.0, Math.Max(left.Bottom, right.Bottom) - Math.Min(left.Top, right.Top));
            return Math.Sqrt(gapX * gapX + gapY * gapY);
        }

        private static StepVectorWatermarkBounds BoundsForPoints(IReadOnlyList<StepVectorWatermarkPoint> points)
        {
            return new StepVectorWatermarkBounds
            {
                Left = points.Min(point => point.X),
                Bottom = points.Min(point => point.Y),
                Right = points.Max(point => point.X),
                Top = points.Max(point => point.Y)
            };
        }

        private static double IntersectionOverUnion(
            StepVectorWatermarkDetectionRegion left,
            StepVectorWatermarkDetectionRegion right)
        {
            int x0 = Math.Max(left.X, right.X);
            int y0 = Math.Max(left.Y, right.Y);
            int x1 = Math.Min(left.X + left.Width, right.X + right.Width);
            int y1 = Math.Min(left.Y + left.Height, right.Y + right.Height);
            int intersection = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
            int leftArea = Math.Max(0, left.Width) * Math.Max(0, left.Height);
            int rightArea = Math.Max(0, right.Width) * Math.Max(0, right.Height);
            int union = leftArea + rightArea - intersection;
            return union <= 0 ? 0.0 : intersection / (double)union;
        }

        private sealed class VectorPrimitiveStroke
        {
            public StepVectorWatermarkBounds Bounds { get; private set; }
            public IReadOnlyList<StepVectorWatermarkPoint> Points { get; private set; }
            public double CenterX => (Bounds.Left + Bounds.Right) * 0.5;
            public double CenterY => (Bounds.Top + Bounds.Bottom) * 0.5;

            public static VectorPrimitiveStroke FromPrimitive(StepVectorWatermarkPrimitive primitive)
            {
                IReadOnlyList<StepVectorWatermarkPoint> points = primitive.SampledImagePoints;
                if (points == null || points.Count == 0)
                    return null;

                return new VectorPrimitiveStroke
                {
                    Bounds = primitive.ImageBounds ?? BoundsForPoints(points),
                    Points = points
                };
            }
        }

        private sealed class VectorStrokeCluster
        {
            public IReadOnlyList<VectorPrimitiveStroke> Strokes { get; private set; }
            public IReadOnlyList<StepVectorWatermarkPoint> Points { get; private set; }
            public StepVectorWatermarkBounds Bounds { get; private set; }

            public static VectorStrokeCluster FromStrokes(IReadOnlyList<VectorPrimitiveStroke> strokes)
            {
                if (strokes == null || strokes.Count == 0)
                    return null;

                List<StepVectorWatermarkPoint> points = strokes
                    .SelectMany(stroke => stroke.Points)
                    .ToList();
                if (points.Count == 0)
                    return null;

                return new VectorStrokeCluster
                {
                    Strokes = strokes.ToList(),
                    Points = points,
                    Bounds = BoundsForPoints(points)
                };
            }
        }

        private sealed class RotatedTemplate
        {
            public int Width { get; private set; }
            public int Height { get; private set; }
            public IReadOnlyList<UnitPoint> Points { get; private set; }

            public static RotatedTemplate Create(StepWatermarkTemplate template, int orientation)
            {
                var points = new List<UnitPoint>();
                foreach (StepWatermarkTemplatePoint point in template.EdgePoints)
                {
                    RotateTemplatePoint(
                        point.X,
                        point.Y,
                        template.Width,
                        template.Height,
                        orientation,
                        out int x,
                        out int y);
                    points.Add(new UnitPoint(x, y));
                }

                return new RotatedTemplate
                {
                    Width = orientation == 90 || orientation == 270 ? template.Height : template.Width,
                    Height = orientation == 90 || orientation == 270 ? template.Width : template.Height,
                    Points = points
                };
            }

            private static void RotateTemplatePoint(
                int x,
                int y,
                int width,
                int height,
                int orientation,
                out int rotatedX,
                out int rotatedY)
            {
                switch (orientation)
                {
                    case 90:
                        rotatedX = height - 1 - y;
                        rotatedY = x;
                        return;
                    case 180:
                        rotatedX = width - 1 - x;
                        rotatedY = height - 1 - y;
                        return;
                    case 270:
                        rotatedX = y;
                        rotatedY = width - 1 - x;
                        return;
                    default:
                        rotatedX = x;
                        rotatedY = y;
                        return;
                }
            }
        }

        private sealed class LogoClusterScore
        {
            public static readonly LogoClusterScore Rejected = new LogoClusterScore
            {
                Score = 0.0,
                ChamferDistance = double.MaxValue,
                OrientationDegrees = 0
            };

            public double Score { get; set; }
            public double ChamferDistance { get; set; }
            public int OrientationDegrees { get; set; }
        }

        private struct UnitPoint
        {
            public UnitPoint(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }
    }
}
