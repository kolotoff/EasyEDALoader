using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
        private const byte EdgeThreshold = 80;
        private const int ChamferOrthogonalCost = 3;
        private const int ChamferDiagonalCost = 4;
        private const int MaxChamferCost = 255 * ChamferOrthogonalCost;
        private const int CandidateMergePaddingPixels = 42;
        private const int CandidateSearchPaddingPixels = 32;
        private const int MinimumTemplatePointCount = 20;
        private const double MinimumAcceptedScore = 7.5;
        private const double MaximumAcceptedChamferDistance = 12.0;

        public static IReadOnlyList<StepTextLogoDetectionRegion> Detect(
            StepProjectionImage colorImage,
            StepProjectionImage edgeImage)
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

            bool[] edgeMap = BuildEdgeMap(edgeImage);
            int[] distance = BuildChamferDistance(edgeMap, edgeImage.Width, edgeImage.Height);
            List<CandidateBox> candidates = BuildCandidateBoxes(colorImage, edgeMap);
            if (candidates.Count == 0)
                return Array.Empty<StepTextLogoDetectionRegion>();

            var results = new List<StepTextLogoDetectionRegion>();
            foreach (StepWatermarkTemplate template in StepWatermarkTemplateLibrary.GetKnownTemplates())
            {
                TemplateMatch best = FindBestTemplateMatch(template, candidates, distance, edgeMap, colorImage.Width, colorImage.Height);
                if (best == null)
                    continue;
                if (best.Score < MinimumAcceptedScore || best.ChamferDistance > MaximumAcceptedChamferDistance)
                    continue;

                results.Add(new StepTextLogoDetectionRegion
                {
                    TemplateName = template.Name,
                    Kind = template.Kind,
                    Text = template.Text,
                    X = best.X,
                    Y = best.Y,
                    Width = best.Width,
                    Height = best.Height,
                    Score = best.Score,
                    ChamferDistance = best.ChamferDistance,
                    EdgePixelCount = CountEdgePixels(edgeMap, colorImage.Width, colorImage.Height, best.X, best.Y, best.Width, best.Height)
                });
            }

            return results
                .OrderByDescending(result => result.Score)
                .ToList();
        }

        private static TemplateMatch FindBestTemplateMatch(
            StepWatermarkTemplate template,
            List<CandidateBox> candidates,
            int[] distance,
            bool[] edgeMap,
            int imageWidth,
            int imageHeight)
        {
            if (template.EdgePoints == null || template.EdgePoints.Count < MinimumTemplatePointCount)
                return null;

            TemplateMatch best = null;
            foreach (CandidateBox candidate in candidates)
            {
                if (candidate.Width <= 8 || candidate.Height <= 8 || LooksLikeProtectedMetalField(candidate))
                    continue;

                foreach (TemplateVariant variant in BuildTemplateVariants(template, candidate))
                {
                    if (variant.Points.Count < MinimumTemplatePointCount)
                        continue;
                    if (variant.Width <= 8 || variant.Height <= 8)
                        continue;
                    if (variant.Width > candidate.Width + CandidateSearchPaddingPixels * 2 ||
                        variant.Height > candidate.Height + CandidateSearchPaddingPixels * 2)
                    {
                        continue;
                    }

                    int minX = Math.Max(0, candidate.X - CandidateSearchPaddingPixels);
                    int minY = Math.Max(0, candidate.Y - CandidateSearchPaddingPixels);
                    int maxX = Math.Min(imageWidth - variant.Width, candidate.Right + CandidateSearchPaddingPixels - variant.Width);
                    int maxY = Math.Min(imageHeight - variant.Height, candidate.Bottom + CandidateSearchPaddingPixels - variant.Height);
                    if (maxX < minX || maxY < minY)
                        continue;

                    foreach (SearchPosition position in BuildSearchPositions(candidate, variant, minX, minY, maxX, maxY))
                    {
                        TemplateMatch match = ScoreTemplateAt(
                            template,
                            variant,
                            candidate,
                            distance,
                            edgeMap,
                            imageWidth,
                            imageHeight,
                            position.X,
                            position.Y);
                        if (match == null)
                            continue;
                        if (best == null || match.Score > best.Score)
                            best = match;
                    }
                }
            }

            return best;
        }

        private static IEnumerable<SearchPosition> BuildSearchPositions(
            CandidateBox candidate,
            TemplateVariant variant,
            int minX,
            int minY,
            int maxX,
            int maxY)
        {
            var positions = new List<SearchPosition>();
            int[] xs =
            {
                candidate.X,
                candidate.X + (candidate.Width - variant.Width) / 2,
                candidate.Right - variant.Width + 1,
                candidate.X - Math.Max(4, variant.Width / 12),
                candidate.X + Math.Max(4, variant.Width / 12)
            };
            int[] ys =
            {
                candidate.Y,
                candidate.Y + (candidate.Height - variant.Height) / 2,
                candidate.Bottom - variant.Height + 1,
                candidate.Y - Math.Max(4, variant.Height / 12),
                candidate.Y + Math.Max(4, variant.Height / 12)
            };

            foreach (int x in xs)
            {
                foreach (int y in ys)
                {
                    positions.Add(new SearchPosition(
                        Math.Max(minX, Math.Min(maxX, x)),
                        Math.Max(minY, Math.Min(maxY, y))));
                }
            }

            return positions.Distinct(new SearchPositionComparer());
        }

        private static TemplateMatch ScoreTemplateAt(
            StepWatermarkTemplate template,
            TemplateVariant variant,
            CandidateBox candidate,
            int[] distance,
            bool[] edgeMap,
            int imageWidth,
            int imageHeight,
            int x,
            int y)
        {
            int hitCount = 0;
            double distanceSum = 0.0;
            foreach (StepWatermarkTemplatePoint point in variant.Points)
            {
                int px = x + point.X;
                int py = y + point.Y;
                if (px < 0 || py < 0 || px >= imageWidth || py >= imageHeight)
                    return null;

                double pixelDistance = distance[py * imageWidth + px] / (double)ChamferOrthogonalCost;
                distanceSum += Math.Min(pixelDistance, 30.0);
                if (pixelDistance <= 4.0)
                    hitCount++;
            }

            double chamferDistance = distanceSum / variant.Points.Count;
            double hitRatio = (double)hitCount / variant.Points.Count;
            if (hitRatio < 0.18)
                return null;

            int edgePixelCount = CountEdgePixels(edgeMap, imageWidth, imageHeight, x, y, variant.Width, variant.Height);
            double edgeDensity = edgePixelCount / (double)Math.Max(1, variant.Width * variant.Height);
            if (edgePixelCount < Math.Min(variant.Points.Count / 4, 32))
                return null;

            double candidateOverlap = IntersectionArea(
                    x,
                    y,
                    variant.Width,
                    variant.Height,
                    candidate.X,
                    candidate.Y,
                    candidate.Width,
                    candidate.Height) /
                (double)Math.Max(1, Math.Min(variant.Width * variant.Height, candidate.Width * candidate.Height));
            if (candidateOverlap < 0.08)
                return null;

            double prior = candidate.ColorPrior;
            double score =
                100.0 *
                hitRatio *
                Math.Max(0.12, candidateOverlap) *
                Math.Min(1.5, Math.Max(0.35, edgeDensity * 28.0)) *
                prior /
                (1.0 + chamferDistance / 3.5);

            if (LooksLikeNegativeConnectorGeometry(edgeMap, imageWidth, imageHeight, x, y, variant.Width, variant.Height, edgePixelCount))
                return null;

            return new TemplateMatch
            {
                TemplateName = template.Name,
                X = x,
                Y = y,
                Width = variant.Width,
                Height = variant.Height,
                Score = score,
                ChamferDistance = chamferDistance
            };
        }

        private static List<TemplateVariant> BuildTemplateVariants(StepWatermarkTemplate template, CandidateBox candidate)
        {
            var variants = new List<TemplateVariant>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int rotation = 0; rotation < 360; rotation += 90)
            {
                GetRotatedTemplateSize(template, rotation, out int rotatedWidth, out int rotatedHeight);
                foreach (double scale in BuildScaleCandidates(candidate, rotatedWidth, rotatedHeight))
                {
                    TemplateVariant variant = BuildVariant(template, rotation, scale);
                    string key =
                        rotation.ToString(CultureInfo.InvariantCulture) +
                        "|" +
                        variant.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" +
                        variant.Height.ToString(CultureInfo.InvariantCulture);
                    if (keys.Add(key))
                        variants.Add(variant);
                }
            }

            return variants;
        }

        private static IEnumerable<double> BuildScaleCandidates(CandidateBox candidate, int templateWidth, int templateHeight)
        {
            var scales = new List<double>
            {
                1.0,
                1.25,
                1.5,
                1.75,
                2.0,
                2.5,
                3.0,
                3.5,
                4.0,
                4.5,
                5.0,
                5.5
            };

            if (templateWidth > 0 && templateHeight > 0)
            {
                double fitScale = Math.Min(
                    candidate.Width / (double)templateWidth,
                    candidate.Height / (double)templateHeight);
                double widthScale = candidate.Width / (double)templateWidth;
                double heightScale = candidate.Height / (double)templateHeight;
                foreach (double scale in new[] { fitScale, widthScale, heightScale })
                {
                    if (scale <= 0.0 || double.IsNaN(scale) || double.IsInfinity(scale))
                        continue;
                    scales.Add(scale * 0.55);
                    scales.Add(scale * 0.75);
                    scales.Add(scale);
                    scales.Add(scale * 1.25);
                }
            }

            return scales
                .Where(scale => scale >= 0.45 && scale <= 7.0)
                .Distinct(new RoundedDoubleComparer())
                .OrderBy(scale => scale);
        }

        private static TemplateVariant BuildVariant(StepWatermarkTemplate template, int rotation, double scale)
        {
            GetRotatedTemplateSize(template, rotation, out int rotatedWidth, out int rotatedHeight);
            int width = Math.Max(1, (int)Math.Round(rotatedWidth * scale, MidpointRounding.AwayFromZero));
            int height = Math.Max(1, (int)Math.Round(rotatedHeight * scale, MidpointRounding.AwayFromZero));
            var pointKeys = new HashSet<int>();
            var points = new List<StepWatermarkTemplatePoint>();

            foreach (StepWatermarkTemplatePoint sourcePoint in template.EdgePoints)
            {
                RotatePoint(sourcePoint.X, sourcePoint.Y, template.Width, template.Height, rotation, out int rotatedX, out int rotatedY);
                int x = rotatedWidth <= 1
                    ? 0
                    : (int)Math.Round(rotatedX * (width - 1) / (double)(rotatedWidth - 1), MidpointRounding.AwayFromZero);
                int y = rotatedHeight <= 1
                    ? 0
                    : (int)Math.Round(rotatedY * (height - 1) / (double)(rotatedHeight - 1), MidpointRounding.AwayFromZero);
                x = Math.Max(0, Math.Min(width - 1, x));
                y = Math.Max(0, Math.Min(height - 1, y));
                int key = y * width + x;
                if (pointKeys.Add(key))
                    points.Add(new StepWatermarkTemplatePoint(x, y));
            }

            return new TemplateVariant
            {
                Width = width,
                Height = height,
                Points = points
            };
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

        private static void RotatePoint(
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

        private static bool[] BuildEdgeMap(StepProjectionImage image)
        {
            var edgeMap = new bool[image.Width * image.Height];
            byte[] rgba = image.RgbaBytes;
            for (int pixel = 0, offset = 0; pixel < edgeMap.Length; pixel++, offset += 4)
            {
                edgeMap[pixel] =
                    rgba[offset + 3] > 0 &&
                    rgba[offset] <= EdgeThreshold &&
                    rgba[offset + 1] <= EdgeThreshold &&
                    rgba[offset + 2] <= EdgeThreshold;
            }

            return edgeMap;
        }

        private static int[] BuildChamferDistance(bool[] edgeMap, int width, int height)
        {
            var distance = new int[edgeMap.Length];
            for (int i = 0; i < distance.Length; i++)
                distance[i] = edgeMap[i] ? 0 : MaxChamferCost;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x;
                    int value = distance[index];
                    if (x > 0)
                        value = Math.Min(value, distance[index - 1] + ChamferOrthogonalCost);
                    if (y > 0)
                    {
                        value = Math.Min(value, distance[index - width] + ChamferOrthogonalCost);
                        if (x > 0)
                            value = Math.Min(value, distance[index - width - 1] + ChamferDiagonalCost);
                        if (x + 1 < width)
                            value = Math.Min(value, distance[index - width + 1] + ChamferDiagonalCost);
                    }

                    distance[index] = value;
                }
            }

            for (int y = height - 1; y >= 0; y--)
            {
                int row = y * width;
                for (int x = width - 1; x >= 0; x--)
                {
                    int index = row + x;
                    int value = distance[index];
                    if (x + 1 < width)
                        value = Math.Min(value, distance[index + 1] + ChamferOrthogonalCost);
                    if (y + 1 < height)
                    {
                        value = Math.Min(value, distance[index + width] + ChamferOrthogonalCost);
                        if (x + 1 < width)
                            value = Math.Min(value, distance[index + width + 1] + ChamferDiagonalCost);
                        if (x > 0)
                            value = Math.Min(value, distance[index + width - 1] + ChamferDiagonalCost);
                    }

                    distance[index] = value;
                }
            }

            return distance;
        }

        private static List<CandidateBox> BuildCandidateBoxes(StepProjectionImage colorImage, bool[] edgeMap)
        {
            int width = colorImage.Width;
            int height = colorImage.Height;
            byte[] rgba = colorImage.RgbaBytes;
            var mask = new bool[width * height];
            for (int pixel = 0, offset = 0; pixel < mask.Length; pixel++, offset += 4)
            {
                mask[pixel] = LooksLikeNeutralWatermarkPixel(
                    rgba[offset],
                    rgba[offset + 1],
                    rgba[offset + 2],
                    rgba[offset + 3]);
            }

            List<CandidateBox> components = ExtractComponents(mask, edgeMap, colorImage);
            List<CandidateBox> merged = MergeCandidateBoxes(components, width, height);
            return merged
                .Where(candidate => candidate.Width > 8 && candidate.Height > 8)
                .OrderByDescending(candidate => candidate.EdgePixels)
                .Take(64)
                .ToList();
        }

        private static bool LooksLikeNeutralWatermarkPixel(byte r, byte g, byte b, byte a)
        {
            if (a == 0)
                return false;

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
            if (luminance < 72.0 || luminance > 188.0)
                return false;
            if (max - min > 54)
                return false;
            if (r > 135 && g > 115 && b < 105 && r > b + 35)
                return false;

            return true;
        }

        private static List<CandidateBox> ExtractComponents(
            bool[] mask,
            bool[] edgeMap,
            StepProjectionImage colorImage)
        {
            int width = colorImage.Width;
            int height = colorImage.Height;
            byte[] rgba = colorImage.RgbaBytes;
            var visited = new bool[mask.Length];
            var queue = new int[mask.Length];
            var result = new List<CandidateBox>();

            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || visited[start])
                    continue;

                int head = 0;
                int tail = 0;
                queue[tail++] = start;
                visited[start] = true;
                int minX = start % width;
                int maxX = minX;
                int minY = start / width;
                int maxY = minY;
                int area = 0;
                int edgePixels = 0;
                long rSum = 0;
                long gSum = 0;
                long bSum = 0;

                while (head < tail)
                {
                    int index = queue[head++];
                    int x = index % width;
                    int y = index / width;
                    area++;
                    if (edgeMap[index])
                        edgePixels++;
                    int offset = index * 4;
                    rSum += rgba[offset];
                    gSum += rgba[offset + 1];
                    bSum += rgba[offset + 2];
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);

                    Enqueue(mask, visited, queue, ref tail, width, height, x - 1, y);
                    Enqueue(mask, visited, queue, ref tail, width, height, x + 1, y);
                    Enqueue(mask, visited, queue, ref tail, width, height, x, y - 1);
                    Enqueue(mask, visited, queue, ref tail, width, height, x, y + 1);
                    Enqueue(mask, visited, queue, ref tail, width, height, x - 1, y - 1);
                    Enqueue(mask, visited, queue, ref tail, width, height, x + 1, y - 1);
                    Enqueue(mask, visited, queue, ref tail, width, height, x - 1, y + 1);
                    Enqueue(mask, visited, queue, ref tail, width, height, x + 1, y + 1);
                }

                int boxWidth = maxX - minX + 1;
                int boxHeight = maxY - minY + 1;
                if (area < 12 || boxWidth < 2 || boxHeight < 2)
                    continue;
                if (boxWidth > width * 0.70 || boxHeight > height * 0.70)
                    continue;

                result.Add(new CandidateBox
                {
                    X = minX,
                    Y = minY,
                    Width = boxWidth,
                    Height = boxHeight,
                    Area = area,
                    EdgePixels = edgePixels,
                    AverageRed = rSum / (double)area,
                    AverageGreen = gSum / (double)area,
                    AverageBlue = bSum / (double)area,
                    ColorPrior = 1.0
                });
            }

            return result;
        }

        private static void Enqueue(
            bool[] mask,
            bool[] visited,
            int[] queue,
            ref int tail,
            int width,
            int height,
            int x,
            int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;

            int index = y * width + x;
            if (!mask[index] || visited[index])
                return;

            visited[index] = true;
            queue[tail++] = index;
        }

        private static List<CandidateBox> MergeCandidateBoxes(List<CandidateBox> components, int imageWidth, int imageHeight)
        {
            var boxes = components
                .Where(component => !LooksLikeProtectedMetalField(component))
                .ToList();
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < boxes.Count && !changed; i++)
                {
                    for (int j = i + 1; j < boxes.Count; j++)
                    {
                        if (!ShouldMerge(boxes[i], boxes[j], imageWidth, imageHeight))
                            continue;

                        boxes[i] = Merge(boxes[i], boxes[j]);
                        boxes.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            } while (changed);

            boxes.AddRange(components);
            return boxes
                .Distinct(new CandidateBoxComparer())
                .ToList();
        }

        private static bool ShouldMerge(CandidateBox a, CandidateBox b, int imageWidth, int imageHeight)
        {
            int left = Math.Max(a.X - CandidateMergePaddingPixels, b.X - CandidateMergePaddingPixels);
            int top = Math.Max(a.Y - CandidateMergePaddingPixels, b.Y - CandidateMergePaddingPixels);
            int right = Math.Min(a.Right + CandidateMergePaddingPixels, b.Right + CandidateMergePaddingPixels);
            int bottom = Math.Min(a.Bottom + CandidateMergePaddingPixels, b.Bottom + CandidateMergePaddingPixels);
            if (right < left || bottom < top)
                return false;

            int mergedWidth = Math.Max(a.Right, b.Right) - Math.Min(a.X, b.X) + 1;
            int mergedHeight = Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Y, b.Y) + 1;
            if (mergedWidth > imageWidth * 0.55 || mergedHeight > imageHeight * 0.55)
                return false;

            double aspect = Math.Max(mergedWidth / (double)Math.Max(1, mergedHeight), mergedHeight / (double)Math.Max(1, mergedWidth));
            return aspect <= 9.0;
        }

        private static CandidateBox Merge(CandidateBox a, CandidateBox b)
        {
            int area = a.Area + b.Area;
            return new CandidateBox
            {
                X = Math.Min(a.X, b.X),
                Y = Math.Min(a.Y, b.Y),
                Width = Math.Max(a.Right, b.Right) - Math.Min(a.X, b.X) + 1,
                Height = Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Y, b.Y) + 1,
                Area = area,
                EdgePixels = a.EdgePixels + b.EdgePixels,
                AverageRed = WeightedAverage(a.AverageRed, a.Area, b.AverageRed, b.Area),
                AverageGreen = WeightedAverage(a.AverageGreen, a.Area, b.AverageGreen, b.Area),
                AverageBlue = WeightedAverage(a.AverageBlue, a.Area, b.AverageBlue, b.Area),
                ColorPrior = Math.Min(a.ColorPrior, b.ColorPrior)
            };
        }

        private static double WeightedAverage(double a, int aWeight, double b, int bWeight)
        {
            int total = Math.Max(1, aWeight + bWeight);
            return (a * aWeight + b * bWeight) / total;
        }

        private static bool LooksLikeProtectedMetalField(CandidateBox candidate)
        {
            double max = Math.Max(candidate.AverageRed, Math.Max(candidate.AverageGreen, candidate.AverageBlue));
            double min = Math.Min(candidate.AverageRed, Math.Min(candidate.AverageGreen, candidate.AverageBlue));
            double luminance = 0.299 * candidate.AverageRed + 0.587 * candidate.AverageGreen + 0.114 * candidate.AverageBlue;
            bool goldLike =
                candidate.AverageRed > 120.0 &&
                candidate.AverageGreen > 95.0 &&
                candidate.AverageBlue < 95.0 &&
                candidate.AverageRed > candidate.AverageBlue + 30.0;
            bool silverFieldLike =
                luminance > 145.0 &&
                max - min < 28.0 &&
                candidate.Area > 2500 &&
                candidate.Area / (double)Math.Max(1, candidate.Width * candidate.Height) > 0.35;
            bool broadFilledField =
                candidate.Area > 12000 &&
                candidate.Area / (double)Math.Max(1, candidate.Width * candidate.Height) > 0.45;

            return goldLike || silverFieldLike || broadFilledField;
        }

        private static bool LooksLikeNegativeConnectorGeometry(
            bool[] edgeMap,
            int imageWidth,
            int imageHeight,
            int x,
            int y,
            int width,
            int height,
            int edgePixelCount)
        {
            if (width <= 0 || height <= 0 || edgePixelCount <= 0)
                return false;

            if (LooksLikeLongStraightBodySeam(edgeMap, imageWidth, imageHeight, x, y, width, height, edgePixelCount))
                return true;

            List<EdgeComponent> components = ExtractLocalEdgeComponents(edgeMap, imageWidth, imageHeight, x, y, width, height);
            return LooksLikeSingleConnectorContact(components, width, height, edgePixelCount) ||
                LooksLikeRegularPinArray(components, width, height);
        }

        private static bool LooksLikeLongStraightBodySeam(
            bool[] edgeMap,
            int imageWidth,
            int imageHeight,
            int x,
            int y,
            int width,
            int height,
            int edgePixelCount)
        {
            int longDimension = Math.Max(width, height);
            int shortDimension = Math.Min(width, height);
            if (longDimension < 64 || shortDimension > longDimension / 4)
                return false;

            var rowCounts = new int[height];
            var columnCounts = new int[width];
            for (int py = 0; py < height; py++)
            {
                int imageY = y + py;
                if (imageY < 0 || imageY >= imageHeight)
                    continue;

                int row = imageY * imageWidth;
                for (int px = 0; px < width; px++)
                {
                    int imageX = x + px;
                    if (imageX < 0 || imageX >= imageWidth)
                        continue;

                    if (!edgeMap[row + imageX])
                        continue;

                    rowCounts[py]++;
                    columnCounts[px]++;
                }
            }

            int longRows = rowCounts.Count(count => count >= width * 0.58);
            int longColumns = columnCounts.Count(count => count >= height * 0.58);
            int maxRow = rowCounts.Length == 0 ? 0 : rowCounts.Max();
            int maxColumn = columnCounts.Length == 0 ? 0 : columnCounts.Max();
            bool horizontalSeam = width >= 64 && maxRow >= width * 0.68 && longRows <= 5;
            bool verticalSeam = height >= 64 && maxColumn >= height * 0.68 && longColumns <= 5;
            if (!horizontalSeam && !verticalSeam)
                return false;

            double edgeDensity = edgePixelCount / (double)Math.Max(1, width * height);
            return edgeDensity <= 0.28;
        }

        private static List<EdgeComponent> ExtractLocalEdgeComponents(
            bool[] edgeMap,
            int imageWidth,
            int imageHeight,
            int x,
            int y,
            int width,
            int height)
        {
            var visited = new bool[width * height];
            var queue = new int[visited.Length];
            var result = new List<EdgeComponent>();

            for (int localIndex = 0; localIndex < visited.Length; localIndex++)
            {
                if (visited[localIndex])
                    continue;

                int startX = localIndex % width;
                int startY = localIndex / width;
                if (!LocalEdgeAt(edgeMap, imageWidth, imageHeight, x, y, width, height, startX, startY))
                    continue;

                int head = 0;
                int tail = 0;
                queue[tail++] = localIndex;
                visited[localIndex] = true;
                int minX = startX;
                int maxX = startX;
                int minY = startY;
                int maxY = startY;
                int pixels = 0;

                while (head < tail)
                {
                    int index = queue[head++];
                    int localX = index % width;
                    int localY = index / width;
                    pixels++;
                    minX = Math.Min(minX, localX);
                    maxX = Math.Max(maxX, localX);
                    minY = Math.Min(minY, localY);
                    maxY = Math.Max(maxY, localY);

                    EnqueueLocalEdge(edgeMap, visited, queue, ref tail, imageWidth, imageHeight, x, y, width, height, localX - 1, localY);
                    EnqueueLocalEdge(edgeMap, visited, queue, ref tail, imageWidth, imageHeight, x, y, width, height, localX + 1, localY);
                    EnqueueLocalEdge(edgeMap, visited, queue, ref tail, imageWidth, imageHeight, x, y, width, height, localX, localY - 1);
                    EnqueueLocalEdge(edgeMap, visited, queue, ref tail, imageWidth, imageHeight, x, y, width, height, localX, localY + 1);
                    EnqueueLocalEdge(edgeMap, visited, queue, ref tail, imageWidth, imageHeight, x, y, width, height, localX - 1, localY - 1);
                    EnqueueLocalEdge(edgeMap, visited, queue, ref tail, imageWidth, imageHeight, x, y, width, height, localX + 1, localY - 1);
                    EnqueueLocalEdge(edgeMap, visited, queue, ref tail, imageWidth, imageHeight, x, y, width, height, localX - 1, localY + 1);
                    EnqueueLocalEdge(edgeMap, visited, queue, ref tail, imageWidth, imageHeight, x, y, width, height, localX + 1, localY + 1);
                }

                result.Add(new EdgeComponent
                {
                    X = minX,
                    Y = minY,
                    Width = maxX - minX + 1,
                    Height = maxY - minY + 1,
                    Pixels = pixels
                });
            }

            return result;
        }

        private static void EnqueueLocalEdge(
            bool[] edgeMap,
            bool[] visited,
            int[] queue,
            ref int tail,
            int imageWidth,
            int imageHeight,
            int originX,
            int originY,
            int width,
            int height,
            int x,
            int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;

            int index = y * width + x;
            if (visited[index])
                return;

            if (!LocalEdgeAt(edgeMap, imageWidth, imageHeight, originX, originY, width, height, x, y))
                return;

            visited[index] = true;
            queue[tail++] = index;
        }

        private static bool LocalEdgeAt(
            bool[] edgeMap,
            int imageWidth,
            int imageHeight,
            int originX,
            int originY,
            int width,
            int height,
            int x,
            int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return false;

            int imageX = originX + x;
            int imageY = originY + y;
            if (imageX < 0 || imageY < 0 || imageX >= imageWidth || imageY >= imageHeight)
                return false;

            return edgeMap[imageY * imageWidth + imageX];
        }

        private static bool LooksLikeRegularPinArray(List<EdgeComponent> components, int width, int height)
        {
            var significant = components
                .Where(component => component.Pixels >= 8 && component.Width >= 3 && component.Height >= 3)
                .Where(component => component.Width <= width * 0.35 && component.Height <= height * 0.70)
                .ToList();
            if (significant.Count < 6)
                return false;

            double medianWidth = Median(significant.Select(component => component.Width));
            double medianHeight = Median(significant.Select(component => component.Height));
            if (medianWidth <= 0.0 || medianHeight <= 0.0)
                return false;

            var similar = significant
                .Where(component =>
                    Math.Abs(component.Width - medianWidth) <= Math.Max(4.0, medianWidth * 0.45) &&
                    Math.Abs(component.Height - medianHeight) <= Math.Max(4.0, medianHeight * 0.45))
                .ToList();
            if (similar.Count < 6 || similar.Count < significant.Count * 0.62)
                return false;

            double rowTolerance = Math.Max(5.0, medianHeight * 0.65);
            double columnTolerance = Math.Max(5.0, medianWidth * 0.65);
            int largestRow = LargestAlignedGroup(similar.Select(component => component.CenterY), rowTolerance);
            int largestColumn = LargestAlignedGroup(similar.Select(component => component.CenterX), columnTolerance);
            if (Math.Max(largestRow, largestColumn) < 6)
                return false;

            double spanX = similar.Max(component => component.CenterX) - similar.Min(component => component.CenterX);
            double spanY = similar.Max(component => component.CenterY) - similar.Min(component => component.CenterY);
            return spanX >= medianWidth * 4.0 || spanY >= medianHeight * 4.0;
        }

        private static bool LooksLikeSingleConnectorContact(
            List<EdgeComponent> components,
            int width,
            int height,
            int edgePixelCount)
        {
            int longDimension = Math.Max(width, height);
            int shortDimension = Math.Min(width, height);
            if (longDimension < 36 || shortDimension <= 0 || longDimension / (double)shortDimension < 2.6)
                return false;

            var significant = components
                .Where(component => component.Pixels >= 10)
                .OrderByDescending(component => component.Pixels)
                .ToList();
            if (significant.Count == 0 || significant.Count > 3)
                return false;

            EdgeComponent largest = significant[0];
            if (largest.Pixels < edgePixelCount * 0.62)
                return false;

            bool fillsVerticalContact =
                height >= width &&
                largest.Height >= height * 0.72 &&
                largest.Width >= width * 0.45;
            bool fillsHorizontalContact =
                width > height &&
                largest.Width >= width * 0.72 &&
                largest.Height >= height * 0.45;
            if (!fillsVerticalContact && !fillsHorizontalContact)
                return false;

            double edgeDensity = edgePixelCount / (double)Math.Max(1, width * height);
            return edgeDensity >= 0.08 && edgeDensity <= 0.55;
        }

        private static int LargestAlignedGroup(IEnumerable<double> coordinates, double tolerance)
        {
            var sorted = coordinates.OrderBy(value => value).ToList();
            int best = 0;
            for (int start = 0; start < sorted.Count; start++)
            {
                int count = 0;
                for (int i = start; i < sorted.Count; i++)
                {
                    if (sorted[i] - sorted[start] > tolerance)
                        break;

                    count++;
                }

                best = Math.Max(best, count);
            }

            return best;
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

        private static int CountEdgePixels(
            bool[] edgeMap,
            int imageWidth,
            int imageHeight,
            int x,
            int y,
            int width,
            int height)
        {
            int left = Math.Max(0, x);
            int top = Math.Max(0, y);
            int right = Math.Min(imageWidth - 1, x + width - 1);
            int bottom = Math.Min(imageHeight - 1, y + height - 1);
            int count = 0;
            for (int py = top; py <= bottom; py++)
            {
                int row = py * imageWidth;
                for (int px = left; px <= right; px++)
                {
                    if (edgeMap[row + px])
                        count++;
                }
            }

            return count;
        }

        private static int IntersectionArea(
            int ax,
            int ay,
            int aw,
            int ah,
            int bx,
            int by,
            int bw,
            int bh)
        {
            int left = Math.Max(ax, bx);
            int top = Math.Max(ay, by);
            int right = Math.Min(ax + aw - 1, bx + bw - 1);
            int bottom = Math.Min(ay + ah - 1, by + bh - 1);
            if (right < left || bottom < top)
                return 0;

            return (right - left + 1) * (bottom - top + 1);
        }

        private sealed class TemplateVariant
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public List<StepWatermarkTemplatePoint> Points { get; set; }
        }

        private sealed class TemplateMatch
        {
            public string TemplateName { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public double Score { get; set; }
            public double ChamferDistance { get; set; }
        }

        private struct SearchPosition
        {
            public SearchPosition(int x, int y)
            {
                X = x;
                Y = y;
            }

            public int X { get; }
            public int Y { get; }
        }

        private sealed class CandidateBox
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Right => X + Width - 1;
            public int Bottom => Y + Height - 1;
            public int Area { get; set; }
            public int EdgePixels { get; set; }
            public double AverageRed { get; set; }
            public double AverageGreen { get; set; }
            public double AverageBlue { get; set; }
            public double ColorPrior { get; set; }
        }

        private sealed class EdgeComponent
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Pixels { get; set; }
            public double CenterX => X + (Width - 1) / 2.0;
            public double CenterY => Y + (Height - 1) / 2.0;
        }

        private sealed class RoundedDoubleComparer : IEqualityComparer<double>
        {
            public bool Equals(double x, double y)
            {
                return Math.Abs(x - y) < 0.04;
            }

            public int GetHashCode(double obj)
            {
                return ((int)Math.Round(obj / 0.04, MidpointRounding.AwayFromZero)).GetHashCode();
            }
        }

        private sealed class CandidateBoxComparer : IEqualityComparer<CandidateBox>
        {
            public bool Equals(CandidateBox x, CandidateBox y)
            {
                if (ReferenceEquals(x, y))
                    return true;
                if (x == null || y == null)
                    return false;

                return x.X == y.X && x.Y == y.Y && x.Width == y.Width && x.Height == y.Height;
            }

            public int GetHashCode(CandidateBox obj)
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

        private sealed class SearchPositionComparer : IEqualityComparer<SearchPosition>
        {
            public bool Equals(SearchPosition x, SearchPosition y)
            {
                return x.X == y.X && x.Y == y.Y;
            }

            public int GetHashCode(SearchPosition obj)
            {
                unchecked
                {
                    return (obj.X * 397) ^ obj.Y;
                }
            }
        }
    }
}
