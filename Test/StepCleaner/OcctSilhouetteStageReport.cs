using EasyEDA_Loader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace StepCleaner.Tests
{
    internal static class OcctSilhouetteStageReport
    {
        public static int Run(string[] args)
        {
            if (args.Length > 2)
            {
                Console.Error.WriteLine("Usage: StepCleaner.Tests --occt-stage-report [output-dir]");
                return 2;
            }

            string outputDirectory = args.Length >= 2
                ? Path.GetFullPath(args[1])
                : Path.GetFullPath(Path.Combine("Test", "StepCleaner", "Data", "SilhouetteReport", "stages"));
            string validatedDirectory = Path.GetFullPath(Path.Combine("Test", "StepCleaner", "Data", "Validated"));
            Directory.CreateDirectory(outputDirectory);

            string[] fileNames =
            {
                "LQFP-100_L14.0-W14.0-H1.4-LS16.0-P0.50.step",
                "CONN-SMD_DF56_40S_0.3V_51.step"
            };
            CleanPreviousStageImages(outputDirectory, fileNames);

            foreach (string fileName in fileNames)
            {
                string stepPath = Path.Combine(validatedDirectory, fileName);
                if (!File.Exists(stepPath))
                {
                    Console.Error.WriteLine("Missing STEP file: " + stepPath);
                    return 2;
                }

                RenderStages(stepPath, outputDirectory);
            }

            Console.WriteLine("OCCT silhouette stage images written: " + outputDirectory);
            return 0;
        }

        private static void CleanPreviousStageImages(string outputDirectory, IEnumerable<string> fileNames)
        {
            foreach (string fileName in fileNames)
            {
                string baseName = Path.GetFileNameWithoutExtension(fileName);
                foreach (string outputPath in Directory.EnumerateFiles(outputDirectory, baseName + "__*.png"))
                    File.Delete(outputPath);
            }
        }

        private static void RenderStages(string stepPath, string outputDirectory)
        {
            StepSilhouettePlacement placement = CreateDefaultPlacement();
            List<StepSilhouettePrimitive> source = ReadSourcePrimitives(stepPath, placement);
            StepSilhouetteBounds sourceBounds = BoundsForPrimitives(source);
            string baseName = Path.GetFileNameWithoutExtension(stepPath);

            List<StepSilhouettePrimitive> rawPlaced = PlacePrimitives(source, placement.TargetBounds, sourceBounds, 0.0);
            List<StepSilhouettePrimitive> merged = MergeLines(rawPlaced);
            List<StepSilhouettePrimitive> overlapRemoved = RemoveOverlap(merged);
            List<StepSilhouettePrimitive> cutoffRemoved = RemoveSmall(overlapRemoved);

            SaveStage(rawPlaced, outputDirectory, baseName, "01_without_optimization", "without optimization");
            SaveStage(merged, outputDirectory, baseName, "02_with_line_merge", "with 0.001mm centerline + interval line merge");
            SaveStage(overlapRemoved, outputDirectory, baseName, "03_with_overlap_removal", "with line merge + 90% overlap removal");
            SaveStage(cutoffRemoved, outputDirectory, baseName, "04_with_cutoff", "with line merge + overlap removal + 0.03mm cutoff");
        }

        private static void SaveStage(
            IReadOnlyList<StepSilhouettePrimitive> primitives,
            string outputDirectory,
            string baseName,
            string suffix,
            string title)
        {
            string outputPath = Path.Combine(outputDirectory, baseName + "__" + suffix + ".png");
            StepSilhouetteImageRenderer.SavePng(
                primitives,
                outputPath,
                new StepSilhouetteImageRenderOptions
                {
                    ImageSizePixels = 1800,
                    PaddingPixels = 80,
                    DrawGrid = false,
                    DrawAxes = false,
                    Title = baseName + " - " + title
                });

            int lines = primitives.Count(primitive => primitive.Kind == StepSilhouettePrimitiveKind.Line);
            int arcs = primitives.Count - lines;
            Console.WriteLine(Path.GetFileName(outputPath) + ": " + lines.ToString(CultureInfo.InvariantCulture) + " line(s), " + arcs.ToString(CultureInfo.InvariantCulture) + " arc(s).");
        }

        private static List<StepSilhouettePrimitive> ReadSourcePrimitives(string stepPath, StepSilhouettePlacement placement)
        {
            string helperPath = InvokePrivate<string>("FindOcctHlrExecutable");
            if (string.IsNullOrWhiteSpace(helperPath))
                throw new InvalidOperationException("StepOcctHlr helper was not found.");

            string tempJson = Path.Combine(Path.GetTempPath(), "EasyEDALoaderStage_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add(stepPath);
                startInfo.ArgumentList.Add(tempJson);
                startInfo.ArgumentList.Add("--rot-x");
                startInfo.ArgumentList.Add(placement.RotX.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--rot-y");
                startInfo.ArgumentList.Add(placement.RotY.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--rot-z");
                startInfo.ArgumentList.Add(placement.RotZ.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--rotation2d");
                startInfo.ArgumentList.Add(placement.Rotation2D.ToString(CultureInfo.InvariantCulture));

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                        throw new InvalidOperationException("Could not start StepOcctHlr helper.");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); }
                        catch { }
                        throw new TimeoutException("StepOcctHlr helper timed out.");
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException("StepOcctHlr helper exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");
                }

                return ParseSourcePrimitives(tempJson);
            }
            finally
            {
                try { File.Delete(tempJson); }
                catch { }
            }
        }

        private static List<StepSilhouettePrimitive> ParseSourcePrimitives(string jsonPath)
        {
            string json = File.ReadAllText(jsonPath);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("Success", out JsonElement successElement) || !successElement.GetBoolean())
                    throw new InvalidDataException("OCCT helper did not report success.");

                var primitives = new List<StepSilhouettePrimitive>();
                foreach (JsonElement primitiveElement in root.GetProperty("Primitives").EnumerateArray())
                {
                    string kind = primitiveElement.GetProperty("Kind").GetString();
                    if (kind == "Line")
                    {
                        primitives.Add(StepSilhouettePrimitive.Line(
                            primitiveElement.GetProperty("X1").GetDouble(),
                            primitiveElement.GetProperty("Y1").GetDouble(),
                            primitiveElement.GetProperty("X2").GetDouble(),
                            primitiveElement.GetProperty("Y2").GetDouble()));
                    }
                    else if (kind == "Arc")
                    {
                        primitives.Add(StepSilhouettePrimitive.Arc(
                            primitiveElement.GetProperty("CenterX").GetDouble(),
                            primitiveElement.GetProperty("CenterY").GetDouble(),
                            primitiveElement.GetProperty("Radius").GetDouble(),
                            primitiveElement.GetProperty("StartAngle").GetDouble(),
                            primitiveElement.GetProperty("EndAngle").GetDouble()));
                    }
                }

                return primitives;
            }
        }

        private static StepSilhouetteBounds BoundsForPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            return InvokePrivate<StepSilhouetteBounds>("BoundsForPrimitives", primitives);
        }

        private static List<StepSilhouettePrimitive> PlacePrimitives(
            List<StepSilhouettePrimitive> primitives,
            StepSilhouetteBounds targetBounds,
            StepSilhouetteBounds sourceBounds,
            double minimumLengthMm)
        {
            return InvokePrivate<List<StepSilhouettePrimitive>>(
                "PlacePrimitivesWithoutRescale",
                primitives,
                targetBounds,
                sourceBounds,
                minimumLengthMm);
        }

        private static List<StepSilhouettePrimitive> RemoveOverlap(List<StepSilhouettePrimitive> primitives)
        {
            return InvokePrivate<List<StepSilhouettePrimitive>>("RemoveFullyOverlappedOcctPrimitives", primitives);
        }

        private static List<StepSilhouettePrimitive> MergeLines(List<StepSilhouettePrimitive> primitives)
        {
            return InvokePrivate<List<StepSilhouettePrimitive>>("MergeTouchingOcctLinePrimitives", primitives);
        }

        private static List<StepSilhouettePrimitive> RemoveSmall(List<StepSilhouettePrimitive> primitives)
        {
            return InvokePrivate<List<StepSilhouettePrimitive>>("RemoveSmallOcctPrimitives", primitives);
        }

        private static T InvokePrivate<T>(string methodName, params object[] args)
        {
            Type[] argumentTypes = args.Select(arg => arg.GetType()).ToArray();
            MethodInfo method = typeof(StepSilhouetteProjection).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                argumentTypes,
                null);
            if (method == null)
                method = typeof(StepSilhouetteProjection).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new MissingMethodException(typeof(StepSilhouetteProjection).FullName, methodName);
            return (T)method.Invoke(null, args);
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
    }
}
