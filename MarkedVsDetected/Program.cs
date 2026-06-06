using EasyEDA_Loader;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static readonly string[] ViewNames =
    {
        "x_minus", "x_plus", "y_minus", "y_plus", "z_minus", "z_plus"
    };

    private static int Main(string[] args)
    {
        string repoRoot = FindRepoRoot();
        string dataRoot = Path.Combine(repoRoot, "Test", "StepCleaner", "Data");
        string originalDirectory = Path.Combine(dataRoot, "Original");
        string projectionDirectory = Path.Combine(dataRoot, "Projection");
        string markedDirectory = Path.Combine(dataRoot, "Marked");
        string detectionDebugDirectory = Path.Combine(dataRoot, "Clean", "Detection");
        string outputDirectory = Path.Combine(dataRoot, "CleanRunReport", "MarkedVsDetected");
        Directory.CreateDirectory(outputDirectory);

        var options = new StepProjectionOptions
        {
            ImageSizePixels = 1600,
            PaddingPixels = 80
        };

        var detectedByKey = new Dictionary<string, List<RectI>>(StringComparer.OrdinalIgnoreCase);
        var detectedSummary = new List<DetectedModelSummary>();
        foreach (string stepFile in Directory.GetFiles(originalDirectory, "*.step").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var detectionReport = StepWatermarkCleaner.Detect(
                File.ReadAllBytes(stepFile),
                new StepWatermarkCleanerOptions
                {
                    CleanText = true
                });
            IReadOnlyList<StepProjectionDetectionRegion> regions =
                StepProjectionRenderer.ProjectDetectionRegions(stepFile, detectionReport, options);

            foreach (StepProjectionDetectionRegion region in regions)
            {
                string key = region.ModelName + "__" + region.ViewName;
                if (!detectedByKey.TryGetValue(key, out List<RectI> rectangles))
                {
                    rectangles = new List<RectI>();
                    detectedByKey[key] = rectangles;
                }

                rectangles.Add(new RectI(region.RectangleX, region.RectangleY, region.RectangleWidth, region.RectangleHeight));
            }

            detectedSummary.Add(new DetectedModelSummary
            {
                Model = Path.GetFileName(stepFile),
                DetectedRegionCount = regions.Count,
                DetectedViews = string.Join("; ", regions
                    .GroupBy(region => region.ViewName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Key + ":" + group.Count().ToString(CultureInfo.InvariantCulture)))
            });
        }

        var rows = new List<CompareRow>();
        var markedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string markerPath in Directory.GetFiles(markedDirectory, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseMarkedKey(markerPath, out string modelName, out string viewName))
                continue;

            var markedFile = JsonSerializer.Deserialize<MarkedFile>(File.ReadAllText(markerPath));
            var markedRects = (markedFile?.Rectangles ?? new List<MarkedRectangle>())
                .Select(rectangle => new RectI(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height))
                .Where(rectangle => rectangle.Width > 0 && rectangle.Height > 0)
                .ToList();
            string key = modelName + "__" + viewName;
            markedKeys.Add(key);
            detectedByKey.TryGetValue(key, out List<RectI> detectedRects);
            detectedRects = detectedRects ?? new List<RectI>();

            Metrics metrics = ComputeMetrics(markedRects, detectedRects, 1600, 1600);
            string overlayPath = Path.Combine(outputDirectory, key + "__marked_vs_detected.png");
            string projectionPath = Path.Combine(projectionDirectory, key + ".png");
            string detectionDebugPath = Path.Combine(detectionDebugDirectory, key + ".png");
            DrawOverlay(projectionPath, overlayPath, markedRects, detectedRects);

            rows.Add(new CompareRow
            {
                Model = modelName,
                View = viewName,
                MarkedRects = markedRects.Count,
                DetectedRects = detectedRects.Count,
                MarkedArea = metrics.MarkedArea,
                DetectedArea = metrics.DetectedArea,
                IntersectionArea = metrics.IntersectionArea,
                MarkCoverage = metrics.MarkCoverage,
                DetectionInsideMark = metrics.DetectionInsideMark,
                BestIoU = metrics.BestIoU,
                Status = GetStatus(detectedRects.Count, metrics),
                MarkedFile = Path.GetFullPath(markerPath),
                OverlayImage = Path.GetFullPath(overlayPath),
                ProjectionImage = Path.GetFullPath(projectionPath),
                DetectionDebugImage = File.Exists(detectionDebugPath) ? Path.GetFullPath(detectionDebugPath) : ""
            });
        }

        foreach (KeyValuePair<string, List<RectI>> pair in detectedByKey.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            string key = pair.Key;
            if (markedKeys.Contains(key))
                continue;

            if (!TryParseProjectionKey(key, out string modelName, out string viewName))
                continue;

            List<RectI> detectedRects = pair.Value ?? new List<RectI>();
            var markedRects = new List<RectI>();
            Metrics metrics = ComputeMetrics(markedRects, detectedRects, 1600, 1600);
            string overlayPath = Path.Combine(outputDirectory, key + "__marked_vs_detected.png");
            string projectionPath = Path.Combine(projectionDirectory, key + ".png");
            string detectionDebugPath = Path.Combine(detectionDebugDirectory, key + ".png");
            DrawOverlay(projectionPath, overlayPath, markedRects, detectedRects);

            rows.Add(new CompareRow
            {
                Model = modelName,
                View = viewName,
                MarkedRects = 0,
                DetectedRects = detectedRects.Count,
                MarkedArea = metrics.MarkedArea,
                DetectedArea = metrics.DetectedArea,
                IntersectionArea = metrics.IntersectionArea,
                MarkCoverage = metrics.MarkCoverage,
                DetectionInsideMark = metrics.DetectionInsideMark,
                BestIoU = metrics.BestIoU,
                Status = GetStatus(detectedRects.Count, metrics),
                MarkedFile = "",
                OverlayImage = Path.GetFullPath(overlayPath),
                ProjectionImage = Path.GetFullPath(projectionPath),
                DetectionDebugImage = File.Exists(detectionDebugPath) ? Path.GetFullPath(detectionDebugPath) : ""
            });
        }

        string csvPath = Path.Combine(outputDirectory, "marked-vs-detected.csv");
        string summaryCsvPath = Path.Combine(outputDirectory, "detected-regions-by-model.csv");
        string reportPath = Path.Combine(outputDirectory, "Report.md");
        WriteCsv(csvPath, rows);
        WriteDetectionSummaryCsv(summaryCsvPath, detectedSummary);
        WriteReport(reportPath, rows, detectedSummary);

        Console.WriteLine("Marked vs detected report: " + Path.GetFullPath(reportPath));
        Console.WriteLine("CSV: " + Path.GetFullPath(csvPath));
        foreach (var group in rows.GroupBy(row => row.Status).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine(group.Key + "=" + group.Count().ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 16; i++)
        {
            if (File.Exists(Path.Combine(current, "EasyEDA-Loader", "EasyEDA-Loader.csproj")) &&
                Directory.Exists(Path.Combine(current, "Test", "StepCleaner", "Data")))
                return current;

            string parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;

            current = parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static bool TryParseMarkedKey(string path, out string modelName, out string viewName)
    {
        return TryParseProjectionKey(Path.GetFileNameWithoutExtension(path), out modelName, out viewName);
    }

    private static bool TryParseProjectionKey(string key, out string modelName, out string viewName)
    {
        string baseName = key;
        foreach (string view in ViewNames)
        {
            string suffix = "__" + view;
            if (!baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            modelName = baseName.Substring(0, baseName.Length - suffix.Length);
            viewName = view;
            return true;
        }

        modelName = "";
        viewName = "";
        return false;
    }

    private static Metrics ComputeMetrics(IReadOnlyList<RectI> markedRects, IReadOnlyList<RectI> detectedRects, int width, int height)
    {
        var markedMask = new bool[width * height];
        var detectedMask = new bool[width * height];
        FillMask(markedMask, width, height, markedRects);
        FillMask(detectedMask, width, height, detectedRects);

        int markedArea = 0;
        int detectedArea = 0;
        int intersectionArea = 0;
        for (int i = 0; i < markedMask.Length; i++)
        {
            if (markedMask[i])
                markedArea++;
            if (detectedMask[i])
                detectedArea++;
            if (markedMask[i] && detectedMask[i])
                intersectionArea++;
        }

        double bestIoU = 0;
        foreach (RectI marked in markedRects)
        {
            foreach (RectI detected in detectedRects)
            {
                int intersection = IntersectArea(marked, detected);
                int union = marked.Area + detected.Area - intersection;
                if (union > 0)
                    bestIoU = Math.Max(bestIoU, (double)intersection / union);
            }
        }

        return new Metrics
        {
            MarkedArea = markedArea,
            DetectedArea = detectedArea,
            IntersectionArea = intersectionArea,
            MarkCoverage = markedArea > 0 ? (double)intersectionArea / markedArea : 0,
            DetectionInsideMark = detectedArea > 0 ? (double)intersectionArea / detectedArea : 0,
            BestIoU = bestIoU
        };
    }

    private static void FillMask(bool[] mask, int width, int height, IReadOnlyList<RectI> rectangles)
    {
        foreach (RectI rectangle in rectangles)
        {
            int left = Math.Max(0, rectangle.X);
            int top = Math.Max(0, rectangle.Y);
            int right = Math.Min(width, rectangle.X + rectangle.Width);
            int bottom = Math.Min(height, rectangle.Y + rectangle.Height);
            for (int y = top; y < bottom; y++)
            {
                int row = y * width;
                for (int x = left; x < right; x++)
                    mask[row + x] = true;
            }
        }
    }

    private static int IntersectArea(RectI a, RectI b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || bottom <= top)
            return 0;
        return (right - left) * (bottom - top);
    }

    private static string GetStatus(int detectedRectCount, Metrics metrics)
    {
        if (metrics.MarkedArea == 0 && detectedRectCount > 0)
            return "unmarked_detection";
        if (detectedRectCount == 0)
            return "missed";
        if (metrics.MarkCoverage >= 0.80 && metrics.DetectionInsideMark >= 0.50)
            return "matched";
        if (metrics.MarkCoverage >= 0.30)
            return "partial";
        return "mismatch";
    }

    private static void DrawOverlay(
        string projectionPath,
        string outputPath,
        IReadOnlyList<RectI> markedRects,
        IReadOnlyList<RectI> detectedRects)
    {
        using SKBitmap bitmap = File.Exists(projectionPath)
            ? SKBitmap.Decode(projectionPath)
            : new SKBitmap(1600, 1600);
        using SKCanvas canvas = new SKCanvas(bitmap);
        using var markedStroke = new SKPaint
        {
            Color = new SKColor(0, 96, 255, 230),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5,
            IsAntialias = true
        };
        using var markedFill = new SKPaint
        {
            Color = new SKColor(0, 96, 255, 35),
            Style = SKPaintStyle.Fill
        };
        using var detectedStroke = new SKPaint
        {
            Color = new SKColor(240, 32, 32, 230),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4,
            IsAntialias = true
        };
        using var detectedFill = new SKPaint
        {
            Color = new SKColor(240, 32, 32, 35),
            Style = SKPaintStyle.Fill
        };
        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 32,
            IsAntialias = true
        };

        foreach (RectI rectangle in markedRects)
        {
            var rect = SKRect.Create(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            canvas.DrawRect(rect, markedFill);
            canvas.DrawRect(rect, markedStroke);
        }

        foreach (RectI rectangle in detectedRects)
        {
            var rect = SKRect.Create(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            canvas.DrawRect(rect, detectedFill);
            canvas.DrawRect(rect, detectedStroke);
        }

        canvas.DrawText("blue=marked  red=detected", 24, 48, textPaint);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using Stream stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    private static void WriteCsv(string path, IReadOnlyList<CompareRow> rows)
    {
        var lines = new List<string>
        {
            "Model,View,Status,MarkedRects,DetectedRects,MarkedArea,DetectedArea,IntersectionArea,MarkCoverage,DetectionInsideMark,BestIoU,MarkedFile,OverlayImage,ProjectionImage,DetectionDebugImage"
        };
        foreach (CompareRow row in rows)
        {
            lines.Add(string.Join(",", new[]
            {
                Csv(row.Model),
                Csv(row.View),
                Csv(row.Status),
                row.MarkedRects.ToString(CultureInfo.InvariantCulture),
                row.DetectedRects.ToString(CultureInfo.InvariantCulture),
                row.MarkedArea.ToString(CultureInfo.InvariantCulture),
                row.DetectedArea.ToString(CultureInfo.InvariantCulture),
                row.IntersectionArea.ToString(CultureInfo.InvariantCulture),
                row.MarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture),
                row.DetectionInsideMark.ToString("0.0000", CultureInfo.InvariantCulture),
                row.BestIoU.ToString("0.0000", CultureInfo.InvariantCulture),
                Csv(row.MarkedFile),
                Csv(row.OverlayImage),
                Csv(row.ProjectionImage),
                Csv(row.DetectionDebugImage)
            }));
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteDetectionSummaryCsv(string path, IReadOnlyList<DetectedModelSummary> rows)
    {
        var lines = new List<string> { "Model,DetectedRegionCount,DetectedViews" };
        foreach (DetectedModelSummary row in rows)
            lines.Add(Csv(row.Model) + "," + row.DetectedRegionCount.ToString(CultureInfo.InvariantCulture) + "," + Csv(row.DetectedViews));
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static void WriteReport(
        string path,
        IReadOnlyList<CompareRow> rows,
        IReadOnlyList<DetectedModelSummary> detectedSummary)
    {
        var lines = new List<string>
        {
            "# Marked vs Detected Watermark Regions",
            "",
            "Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            "",
            "Blue rectangles are human marked regions. Red rectangles are automatic detector regions.",
            "",
            "## Summary",
            ""
        };

        foreach (var group in rows.GroupBy(row => row.Status).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add("- " + group.Key + ": " + group.Count().ToString(CultureInfo.InvariantCulture));

        lines.Add("- marked views: " + rows.Count(row => !string.IsNullOrEmpty(row.MarkedFile)).ToString(CultureInfo.InvariantCulture));
        lines.Add("- detected views without marked data: " + rows.Count(row => string.IsNullOrEmpty(row.MarkedFile) && row.DetectedRects > 0).ToString(CultureInfo.InvariantCulture));
        lines.Add("- models with any detected regions: " + detectedSummary.Count(row => row.DetectedRegionCount > 0).ToString(CultureInfo.InvariantCulture));
        lines.Add("");
        lines.Add("## Missed, Weak, Or Unmarked Detections");
        lines.Add("");
        string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        foreach (CompareRow row in rows
            .Where(row => !string.Equals(row.Status, "matched", StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Status, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.View, StringComparer.OrdinalIgnoreCase))
        {
            string overlayPath = ToMarkdownPath(reportDirectory, row.OverlayImage);
            lines.Add("### " + row.Model + " " + row.View);
            lines.Add("");
            lines.Add("- status: `" + row.Status + "`");
            lines.Add("- marked coverage: " + row.MarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- detection inside mark: " + row.DetectionInsideMark.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- best IoU: " + row.BestIoU.ToString("0.0000", CultureInfo.InvariantCulture));
            lines.Add("- marked file: `" + (string.IsNullOrEmpty(row.MarkedFile) ? "none" : row.MarkedFile) + "`");
            lines.Add("- overlay image: `" + overlayPath + "`");
            lines.Add("");
            lines.Add("![" + row.Model + " " + row.View + "](" + overlayPath + ")");
            lines.Add("");
        }

        lines.Add("## All Compared Views");
        lines.Add("");
        lines.Add("| Model | View | Status | Marked rects | Detected rects | Mark coverage | Detection inside mark | Best IoU | Overlay |");
        lines.Add("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (CompareRow row in rows.OrderBy(row => row.Model, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.View, StringComparer.OrdinalIgnoreCase))
        {
            string overlayPath = ToMarkdownPath(reportDirectory, row.OverlayImage);
            lines.Add("| " + EscapePipe(row.Model) +
                " | " + row.View +
                " | " + row.Status +
                " | " + row.MarkedRects.ToString(CultureInfo.InvariantCulture) +
                " | " + row.DetectedRects.ToString(CultureInfo.InvariantCulture) +
                " | " + row.MarkCoverage.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | " + row.DetectionInsideMark.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | " + row.BestIoU.ToString("0.0000", CultureInfo.InvariantCulture) +
                " | `" + overlayPath + "` |");
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static string Csv(string value)
    {
        value = value ?? "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string ToMarkdownPath(string reportDirectory, string value)
    {
        return Path.GetRelativePath(reportDirectory, Path.GetFullPath(value)).Replace('\\', '/');
    }

    private static string EscapePipe(string value)
    {
        return (value ?? "").Replace("|", "\\|");
    }

    private sealed class MarkedFile
    {
        public List<MarkedRectangle> Rectangles { get; set; }
    }

    private sealed class MarkedRectangle
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private readonly struct RectI
    {
        public RectI(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public int Area => Math.Max(0, Width) * Math.Max(0, Height);
    }

    private sealed class Metrics
    {
        public int MarkedArea { get; set; }
        public int DetectedArea { get; set; }
        public int IntersectionArea { get; set; }
        public double MarkCoverage { get; set; }
        public double DetectionInsideMark { get; set; }
        public double BestIoU { get; set; }
    }

    private sealed class CompareRow
    {
        public string Model { get; set; }
        public string View { get; set; }
        public int MarkedRects { get; set; }
        public int DetectedRects { get; set; }
        public int MarkedArea { get; set; }
        public int DetectedArea { get; set; }
        public int IntersectionArea { get; set; }
        public double MarkCoverage { get; set; }
        public double DetectionInsideMark { get; set; }
        public double BestIoU { get; set; }
        public string Status { get; set; }
        public string MarkedFile { get; set; }
        public string OverlayImage { get; set; }
        public string ProjectionImage { get; set; }
        public string DetectionDebugImage { get; set; }
    }

    private sealed class DetectedModelSummary
    {
        public string Model { get; set; }
        public int DetectedRegionCount { get; set; }
        public string DetectedViews { get; set; }
    }
}
