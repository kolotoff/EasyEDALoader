using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EasyEDA_Loader
{
    public static class StepVectorWatermarkRegionCombiner
    {
        private const int MaximumReturnedRegions = 1;

        public static IReadOnlyList<StepVectorWatermarkDetectionRegion> Combine(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> logoDetections,
            IReadOnlyList<StepVectorWatermarkDetectionRegion> textDetections,
            StepVectorWatermarkDetectionInput input,
            StepTextLogoDetectionOptions options)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            var parts = new List<StepVectorWatermarkDetectionRegion>();
            parts.AddRange((logoDetections ?? Array.Empty<StepVectorWatermarkDetectionRegion>())
                .Where(IsValidPart));
            parts.AddRange((textDetections ?? Array.Empty<StepVectorWatermarkDetectionRegion>())
                .Where(IsValidPart));
            if (parts.Count == 0)
                return Array.Empty<StepVectorWatermarkDetectionRegion>();

            List<List<StepVectorWatermarkDetectionRegion>> clusters = BuildClusters(parts, input);
            List<StepVectorWatermarkDetectionRegion> regions = clusters
                .Select(cluster => CreateRegion(cluster, input))
                .Select(region => ExpandTallLogoOnlyRegion(region, input))
                .Where(region => region != null && region.Width > 0 && region.Height > 0)
                .Where(region => IsAcceptedOutput(region, options))
                .OrderByDescending(OutputPriority)
                .ThenBy(region => region.Width * region.Height)
                .ToList();

            return SuppressOverlaps(regions)
                .Take(MaximumReturnedRegions)
                .ToList();
        }

        private static List<List<StepVectorWatermarkDetectionRegion>> BuildClusters(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> parts,
            StepVectorWatermarkDetectionInput input)
        {
            var clusters = new List<List<StepVectorWatermarkDetectionRegion>>();
            foreach (StepVectorWatermarkDetectionRegion part in parts.OrderByDescending(region => region.Score))
            {
                List<StepVectorWatermarkDetectionRegion> cluster = clusters.FirstOrDefault(existing =>
                    existing.Any(member => ShouldMerge(member, part, input)));
                if (cluster == null)
                {
                    cluster = new List<StepVectorWatermarkDetectionRegion>();
                    clusters.Add(cluster);
                }

                cluster.Add(part);
            }

            return clusters;
        }

        private static bool ShouldMerge(
            StepVectorWatermarkDetectionRegion left,
            StepVectorWatermarkDetectionRegion right,
            StepVectorWatermarkDetectionInput input)
        {
            if (left == null || right == null)
                return false;
            if (IntersectionOverUnion(left, right) > 0.02)
                return true;

            int leftRight = left.X + left.Width;
            int rightRight = right.X + right.Width;
            int leftBottom = left.Y + left.Height;
            int rightBottom = right.Y + right.Height;
            int gapX = Math.Max(0, Math.Max(left.X, right.X) - Math.Min(leftRight, rightRight));
            int gapY = Math.Max(0, Math.Max(left.Y, right.Y) - Math.Min(leftBottom, rightBottom));
            int unionWidth = Math.Max(leftRight, rightRight) - Math.Min(left.X, right.X);
            int unionHeight = Math.Max(leftBottom, rightBottom) - Math.Min(left.Y, right.Y);
            int maxWidth = Math.Max(left.Width, right.Width);
            int maxHeight = Math.Max(left.Height, right.Height);

            int imageWidth = Math.Max(1, input.ImageWidth);
            int imageHeight = Math.Max(1, input.ImageHeight);
            if (unionWidth > imageWidth * 0.48 || unionHeight > imageHeight * 0.34)
                return false;

            double verticalOverlap = OverlapLength(left.Y, leftBottom, right.Y, rightBottom) /
                (double)Math.Max(1, Math.Min(left.Height, right.Height));
            double horizontalOverlap = OverlapLength(left.X, leftRight, right.X, rightRight) /
                (double)Math.Max(1, Math.Min(left.Width, right.Width));
            if (verticalOverlap < 0.10 && horizontalOverlap < 0.10 && (gapX > 54 || gapY > 42))
                return false;

            bool stackedText = gapY <= Math.Max(28, maxHeight) && gapX <= Math.Max(96, maxWidth * 2);
            bool adjacentLogoText = gapX <= Math.Max(54, maxWidth) && gapY <= Math.Max(54, maxHeight);
            return stackedText || adjacentLogoText;
        }

        private static StepVectorWatermarkDetectionRegion CreateRegion(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> cluster,
            StepVectorWatermarkDetectionInput input)
        {
            if (cluster == null || cluster.Count == 0)
                return null;
            if (cluster.Count == 1)
                return Clone(cluster[0], input);

            bool hasLogo = cluster.Any(IsLogo);
            bool hasText = cluster.Any(IsText);
            IEnumerable<StepVectorWatermarkDetectionRegion> boundsSource = hasText
                ? cluster.Where(IsText)
                : cluster;
            int left = boundsSource.Min(region => region.X);
            int top = boundsSource.Min(region => region.Y);
            int right = boundsSource.Max(region => region.X + region.Width);
            int bottom = boundsSource.Max(region => region.Y + region.Height);
            left = Math.Max(0, left);
            top = Math.Max(0, top);
            if (input.ImageWidth > 0)
                right = Math.Min(input.ImageWidth, right);
            if (input.ImageHeight > 0)
                bottom = Math.Min(input.ImageHeight, bottom);

            StepVectorWatermarkDetectionRegion best = cluster.OrderByDescending(region => region.Score).First();
            StepVectorWatermarkDetectionRegion bestLogo = cluster.Where(IsLogo)
                .OrderByDescending(region => region.Score)
                .FirstOrDefault();
            StepVectorWatermarkDetectionRegion bestText = cluster.Where(IsText)
                .OrderByDescending(region => region.Score)
                .FirstOrDefault();
            string[] templateNames = cluster
                .Select(region => region.TemplateName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] texts = cluster
                .Select(region => region.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new StepVectorWatermarkDetectionRegion
            {
                TemplateName = templateNames.Length == 0
                    ? best.TemplateName
                    : string.Join("+", templateNames),
                Kind = hasLogo && hasText ? "watermark-combined" : hasText ? "text" : best.Kind,
                Text = texts.Length == 0 ? best.Text : string.Join(" ", texts),
                X = left,
                Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                OrientationDegrees = best.OrientationDegrees,
                LogoOrientationDegrees = bestLogo == null ? 0 : bestLogo.LogoOrientationDegrees,
                TextOrientationDegrees = bestText == null ? 0 : bestText.TextOrientationDegrees,
                Score = Math.Round(Math.Min(100.0, cluster.Max(region => region.Score) + (cluster.Count - 1) * 6.0), 3),
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

        private static StepVectorWatermarkDetectionRegion Clone(
            StepVectorWatermarkDetectionRegion region,
            StepVectorWatermarkDetectionInput input)
        {
            int left = Math.Max(0, region.X);
            int top = Math.Max(0, region.Y);
            int right = region.X + region.Width;
            int bottom = region.Y + region.Height;
            if (input.ImageWidth > 0)
                right = Math.Min(input.ImageWidth, right);
            if (input.ImageHeight > 0)
                bottom = Math.Min(input.ImageHeight, bottom);

            return new StepVectorWatermarkDetectionRegion
            {
                TemplateName = region.TemplateName,
                Kind = region.Kind,
                Text = region.Text,
                X = left,
                Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                OrientationDegrees = region.OrientationDegrees,
                LogoOrientationDegrees = region.LogoOrientationDegrees,
                TextOrientationDegrees = region.TextOrientationDegrees,
                Score = region.Score,
                ChamferDistance = region.ChamferDistance,
                PrimitiveCount = region.PrimitiveCount,
                PrimitiveSourceIndices = region.PrimitiveSourceIndices ?? Array.Empty<int>()
            };
        }

        private static StepVectorWatermarkDetectionRegion ExpandTallLogoOnlyRegion(
            StepVectorWatermarkDetectionRegion region,
            StepVectorWatermarkDetectionInput input)
        {
            if (region == null || input == null || input.Primitives == null || input.Primitives.Count == 0)
                return region;
            if (!IsLogo(region) || region.Height < 90 || region.Width < 60)
                return region;

            int imageWidth = Math.Max(1, input.ImageWidth);
            int imageHeight = Math.Max(1, input.ImageHeight);
            double maxWidth = Math.Min(imageWidth * 0.50, Math.Max(region.Width + 220.0, region.Width * 2.45));
            double maxHeight = Math.Min(imageHeight * 0.35, Math.Max(region.Height + 280.0, region.Height * 2.85));
            RectD expanded = RectD.FromRegion(region);
            var primitiveIndices = new HashSet<int>(region.PrimitiveSourceIndices ?? Array.Empty<int>());
            bool changed;
            do
            {
                changed = false;
                for (int primitiveIndex = 0; primitiveIndex < input.Primitives.Count; primitiveIndex++)
                {
                    StepVectorWatermarkPrimitive primitive = input.Primitives[primitiveIndex];
                    if (!IsPlausibleWatermarkSupportPrimitive(primitive, input))
                        continue;

                    RectD primitiveRect = RectD.FromBounds(primitive.ImageBounds);
                    if (!IsNear(expanded, primitiveRect, 190.0, 260.0))
                        continue;

                    RectD union = RectD.Union(expanded, primitiveRect);
                    if (union.Width > maxWidth || union.Height > maxHeight || expanded.Contains(primitiveRect))
                        continue;

                    expanded = union;
                    primitiveIndices.Add(primitiveIndex);
                    changed = true;
                }
            }
            while (changed);

            int left = Math.Max(0, (int)Math.Floor(expanded.Left));
            int top = Math.Max(0, (int)Math.Floor(expanded.Top));
            int right = Math.Min(imageWidth, (int)Math.Ceiling(expanded.Right));
            int bottom = Math.Min(imageHeight, (int)Math.Ceiling(expanded.Bottom));
            if (right <= left || bottom <= top)
                return region;

            return new StepVectorWatermarkDetectionRegion
            {
                TemplateName = region.TemplateName,
                Kind = "watermark-combined",
                Text = region.Text,
                X = left,
                Y = top,
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                OrientationDegrees = region.OrientationDegrees,
                LogoOrientationDegrees = region.LogoOrientationDegrees,
                TextOrientationDegrees = region.TextOrientationDegrees,
                Score = region.Score,
                ChamferDistance = region.ChamferDistance,
                PrimitiveCount = region.PrimitiveCount,
                PrimitiveSourceIndices = primitiveIndices
                    .OrderBy(index => index)
                    .ToList()
            };
        }

        private static bool IsPlausibleWatermarkSupportPrimitive(
            StepVectorWatermarkPrimitive primitive,
            StepVectorWatermarkDetectionInput input)
        {
            if (primitive == null || primitive.ImageBounds == null)
                return false;
            if (!string.IsNullOrWhiteSpace(primitive.Visibility) &&
                primitive.Visibility.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            double width = primitive.ImageBounds.Width;
            double height = primitive.ImageBounds.Height;
            if (width <= 0.0 && height <= 0.0)
                return false;

            double imageLong = Math.Max(1.0, Math.Max(input.ImageWidth, input.ImageHeight));
            double longDimension = Math.Max(width, height);
            double shortDimension = Math.Min(width, height);
            if (longDimension > Math.Max(260.0, imageLong * 0.24))
                return false;
            if (longDimension > imageLong * 0.16 && shortDimension <= 3.0)
                return false;

            return true;
        }

        private static bool IsNear(RectD left, RectD right, double marginX, double marginY)
        {
            double gapX = Math.Max(0.0, Math.Max(left.Left, right.Left) - Math.Min(left.Right, right.Right));
            double gapY = Math.Max(0.0, Math.Max(left.Top, right.Top) - Math.Min(left.Bottom, right.Bottom));
            return gapX <= marginX && gapY <= marginY;
        }

        private static bool IsAcceptedOutput(StepVectorWatermarkDetectionRegion region, StepTextLogoDetectionOptions options)
        {
            if (region == null || region.Width <= 0 || region.Height <= 0)
                return false;
            if (IsLogo(region))
                return region.Score >= 35.0;
            if (IsText(region))
                return region.Score >= 56.0;
            if (IsCombined(region))
                return region.Score >= 45.0;

            return false;
        }

        private static double OutputPriority(StepVectorWatermarkDetectionRegion region)
        {
            double kindBoost = IsCombined(region) ? 400.0 : IsText(region) ? 200.0 : 120.0;
            double score = Math.Max(0.0, region.Score);
            double sizePenalty = Math.Log(Math.Max(1.0, region.Width * region.Height), Math.E) * 0.02;
            return kindBoost + score - sizePenalty;
        }

        private static List<StepVectorWatermarkDetectionRegion> SuppressOverlaps(
            IReadOnlyList<StepVectorWatermarkDetectionRegion> regions)
        {
            var accepted = new List<StepVectorWatermarkDetectionRegion>();
            foreach (StepVectorWatermarkDetectionRegion region in regions)
            {
                if (accepted.Any(existing => IntersectionOverUnion(existing, region) > 0.58))
                    continue;

                accepted.Add(region);
            }

            return accepted;
        }

        private static bool IsValidPart(StepVectorWatermarkDetectionRegion region)
        {
            return region != null &&
                region.Width > 0 &&
                region.Height > 0 &&
                (IsLogo(region) || IsText(region));
        }

        private static bool IsLogo(StepVectorWatermarkDetectionRegion region)
        {
            return region != null &&
                string.Equals(region.Kind, "logo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsText(StepVectorWatermarkDetectionRegion region)
        {
            return region != null &&
                string.Equals(region.Kind, "text", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCombined(StepVectorWatermarkDetectionRegion region)
        {
            return region != null &&
                string.Equals(region.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase);
        }

        private static int OverlapLength(int leftStart, int leftEnd, int rightStart, int rightEnd)
        {
            return Math.Max(0, Math.Min(leftEnd, rightEnd) - Math.Max(leftStart, rightStart));
        }

        private static double OverlapLength(double leftStart, double leftEnd, double rightStart, double rightEnd)
        {
            return Math.Max(0.0, Math.Min(leftEnd, rightEnd) - Math.Max(leftStart, rightStart));
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

        private struct RectD
        {
            public double Left { get; private set; }
            public double Top { get; private set; }
            public double Right { get; private set; }
            public double Bottom { get; private set; }
            public double Width => Math.Max(0.0, Right - Left);
            public double Height => Math.Max(0.0, Bottom - Top);

            public static RectD FromRegion(StepVectorWatermarkDetectionRegion region)
            {
                return new RectD
                {
                    Left = region.X,
                    Top = region.Y,
                    Right = region.X + region.Width,
                    Bottom = region.Y + region.Height
                };
            }

            public static RectD FromBounds(StepVectorWatermarkBounds bounds)
            {
                return new RectD
                {
                    Left = Math.Min(bounds.Left, bounds.Right),
                    Top = Math.Min(bounds.Bottom, bounds.Top),
                    Right = Math.Max(bounds.Left, bounds.Right),
                    Bottom = Math.Max(bounds.Bottom, bounds.Top)
                };
            }

            public static RectD Union(RectD left, RectD right)
            {
                return new RectD
                {
                    Left = Math.Min(left.Left, right.Left),
                    Top = Math.Min(left.Top, right.Top),
                    Right = Math.Max(left.Right, right.Right),
                    Bottom = Math.Max(left.Bottom, right.Bottom)
                };
            }

            public bool Contains(RectD other)
            {
                return other.Left >= Left &&
                    other.Top >= Top &&
                    other.Right <= Right &&
                    other.Bottom <= Bottom;
            }
        }
    }
}
