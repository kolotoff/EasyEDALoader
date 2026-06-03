using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace StepCleaner.Tests
{
    internal static class OcctHiddenLineProjectionReport
    {
        private const int ImageSizePixels = 1600;
        private const int PaddingPixels = 90;
        private const int LabelHeightPixels = 60;
        private const int GapPixels = 24;

        public static int Run(string[] args)
        {
            if (args.Length > 3)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-hlr-report [validated-dir] [report-dir]");
                return 2;
            }

            string validatedDirectory = args.Length >= 2
                ? Path.GetFullPath(args[1])
                : Path.GetFullPath(Path.Combine("Test", "StepCleaner", "Data", "Validated"));
            string reportDirectory = args.Length >= 3
                ? Path.GetFullPath(args[2])
                : Path.GetFullPath(Path.Combine("Test", "StepCleaner", "Data", "SilhouetteReport"));

            if (!Directory.Exists(validatedDirectory))
            {
                Console.Error.WriteLine("Validated STEP directory does not exist: " + validatedDirectory);
                return 2;
            }

            string oldDirectory = Path.Combine(reportDirectory, "old");
            string newDirectory = Path.Combine(reportDirectory, "new");
            string combinedDirectory = Path.Combine(reportDirectory, "old-new");
            Directory.CreateDirectory(newDirectory);
            Directory.CreateDirectory(combinedDirectory);

            var failures = new List<string>();
            var entries = new List<ReportEntry>();
            foreach (string stepPath in Directory.GetFiles(validatedDirectory, "*.step").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string baseName = Path.GetFileNameWithoutExtension(stepPath);
                string oldImagePath = Path.Combine(oldDirectory, baseName + ".png");
                string newImagePath = Path.Combine(newDirectory, baseName + ".png");
                string combinedImagePath = Path.Combine(combinedDirectory, baseName + ".png");

                if (!File.Exists(oldImagePath))
                {
                    failures.Add("Missing saved old silhouette image: " + oldImagePath);
                    continue;
                }

                IReadOnlyList<StepSilhouettePrimitive> primitives;
                try
                {
                    primitives = StepSilhouetteProjection.Generate(
                        File.ReadAllBytes(stepPath),
                        CreateDefaultPlacement());
                    StepSilhouetteImageRenderer.SavePng(
                        primitives,
                        newImagePath,
                        new StepSilhouetteImageRenderOptions
                        {
                            ImageSizePixels = ImageSizePixels,
                            PaddingPixels = PaddingPixels,
                            DrawGrid = false,
                            DrawAxes = false,
                            Title = baseName
                        });
                    CreateSideBySideImage(oldImagePath, newImagePath, combinedImagePath);
                }
                catch (Exception ex)
                {
                    failures.Add("Failed to render " + Path.GetFileName(stepPath) + ": " + ex.Message);
                    continue;
                }

                int lineCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Line);
                int arcCount = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Arc);
                entries.Add(new ReportEntry
                {
                    ComponentName = Path.GetFileName(stepPath),
                    OldImagePath = oldImagePath,
                    NewImagePath = newImagePath,
                    CombinedImagePath = combinedImagePath,
                    LineCount = lineCount,
                    ArcCount = arcCount
                });
                Console.WriteLine(
                    Path.GetFileName(stepPath) +
                    ": " +
                    lineCount.ToString(CultureInfo.InvariantCulture) +
                    " line(s), " +
                    arcCount.ToString(CultureInfo.InvariantCulture) +
                    " arc(s).");
            }

            string reportPath = Path.Combine(reportDirectory, "old-new-report.md");
            WriteMarkdownReport(reportPath, validatedDirectory, entries, failures);
            Console.WriteLine("OCCT HLR old/new report written: " + reportPath);
            Console.WriteLine("Side-by-side image count: " + entries.Count.ToString(CultureInfo.InvariantCulture));

            if (failures.Count > 0)
            {
                Console.Error.WriteLine("OCCT HLR report completed with failure(s).");
                foreach (string failure in failures)
                    Console.Error.WriteLine("  " + failure);
                return 1;
            }

            return 0;
        }

        private static StepSilhouettePlacement CreateDefaultPlacement()
        {
            return new StepSilhouettePlacement
            {
                TargetBounds = new StepSilhouetteBounds
                {
                    Left = -0.5,
                    Bottom = -0.5,
                    Right = 0.5,
                    Top = 0.5
                },
                RotX = 0.0,
                RotY = 0.0,
                RotZ = 0.0,
                Rotation2D = 0.0
            };
        }

        private static void CreateSideBySideImage(string oldImagePath, string newImagePath, string outputPath)
        {
            using (SKBitmap oldImage = SKBitmap.Decode(oldImagePath))
            using (SKBitmap newImage = SKBitmap.Decode(newImagePath))
            {
                if (oldImage == null)
                    throw new InvalidDataException("Could not decode old image: " + oldImagePath);
                if (newImage == null)
                    throw new InvalidDataException("Could not decode new image: " + newImagePath);

                int width = oldImage.Width + GapPixels + newImage.Width;
                int height = LabelHeightPixels + Math.Max(oldImage.Height, newImage.Height);
                using (var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
                using (var canvas = new SKCanvas(bitmap))
                using (var labelPaint = new SKPaint())
                using (var dividerPaint = new SKPaint())
                using (var font = new SKFont())
                {
                    canvas.Clear(SKColors.White);

                    font.Size = 30.0f;
                    labelPaint.Color = SKColors.Black;
                    labelPaint.IsAntialias = true;
                    dividerPaint.Color = new SKColor(200, 200, 200);
                    dividerPaint.StrokeWidth = 2.0f;

                    canvas.DrawText("OLD", 18.0f, 40.0f, SKTextAlign.Left, font, labelPaint);
                    canvas.DrawText("NEW OCCT HLR", oldImage.Width + GapPixels + 18.0f, 40.0f, SKTextAlign.Left, font, labelPaint);
                    canvas.DrawLine(oldImage.Width + GapPixels / 2.0f, 0.0f, oldImage.Width + GapPixels / 2.0f, height, dividerPaint);
                    canvas.DrawBitmap(oldImage, 0.0f, LabelHeightPixels);
                    canvas.DrawBitmap(newImage, oldImage.Width + GapPixels, LabelHeightPixels);

                    string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    using (SKImage image = SKImage.FromBitmap(bitmap))
                    using (SKData data = image.Encode(SKEncodedImageFormat.Png, 95))
                        File.WriteAllBytes(outputPath, data.ToArray());
                }
            }
        }

        private static void WriteMarkdownReport(
            string reportPath,
            string validatedDirectory,
            IReadOnlyList<ReportEntry> entries,
            IReadOnlyList<string> failures)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# OCCT HLR Silhouette Report");
            builder.AppendLine();
            builder.AppendLine("Validated STEP directory: `" + validatedDirectory + "`");
            builder.AppendLine();
            builder.AppendLine("| Component | New primitives | Old | New | Old / New |");
            builder.AppendLine("| --- | ---: | --- | --- | --- |");
            foreach (ReportEntry entry in entries)
            {
                builder.Append("| ");
                builder.Append(EscapeMarkdown(entry.ComponentName));
                builder.Append(" | ");
                builder.Append(entry.LineCount.ToString(CultureInfo.InvariantCulture));
                builder.Append(" lines, ");
                builder.Append(entry.ArcCount.ToString(CultureInfo.InvariantCulture));
                builder.Append(" arcs | ");
                builder.Append(Link("old", reportPath, entry.OldImagePath));
                builder.Append(" | ");
                builder.Append(Link("new", reportPath, entry.NewImagePath));
                builder.Append(" | ");
                builder.Append(Link("side-by-side", reportPath, entry.CombinedImagePath));
                builder.AppendLine(" |");
            }

            if (failures.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## Failures");
                foreach (string failure in failures)
                    builder.AppendLine("- " + failure);
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(reportPath, builder.ToString(), Encoding.UTF8);
        }

        private static string Link(string text, string reportPath, string targetPath)
        {
            string relativePath = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(reportPath)), targetPath);
            return "[" + text + "](" + relativePath.Replace('\\', '/') + ")";
        }

        private static string EscapeMarkdown(string text)
        {
            return text.Replace("|", "\\|");
        }

        private sealed class ReportEntry
        {
            public string ComponentName { get; set; }
            public string OldImagePath { get; set; }
            public string NewImagePath { get; set; }
            public string CombinedImagePath { get; set; }
            public int LineCount { get; set; }
            public int ArcCount { get; set; }
        }
    }
}
