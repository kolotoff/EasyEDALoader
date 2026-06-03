using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace EasyEDA_Loader
{
    internal enum StepSilhouettePrimitiveKind
    {
        Line,
        Arc
    }

    internal sealed class StepSilhouettePrimitive
    {
        public StepSilhouettePrimitiveKind Kind { get; private set; }
        public double X1 { get; private set; }
        public double Y1 { get; private set; }
        public double X2 { get; private set; }
        public double Y2 { get; private set; }
        public double CenterX { get; private set; }
        public double CenterY { get; private set; }
        public double Radius { get; private set; }
        public double StartAngle { get; private set; }
        public double EndAngle { get; private set; }

        public static StepSilhouettePrimitive Line(double x1, double y1, double x2, double y2)
        {
            return new StepSilhouettePrimitive
            {
                Kind = StepSilhouettePrimitiveKind.Line,
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2
            };
        }

        public static StepSilhouettePrimitive Arc(double centerX, double centerY, double radius, double startAngle, double endAngle)
        {
            return new StepSilhouettePrimitive
            {
                Kind = StepSilhouettePrimitiveKind.Arc,
                CenterX = centerX,
                CenterY = centerY,
                Radius = radius,
                StartAngle = startAngle,
                EndAngle = endAngle
            };
        }
    }

    internal sealed class StepSilhouetteBounds
    {
        public double Left { get; set; }
        public double Bottom { get; set; }
        public double Right { get; set; }
        public double Top { get; set; }

        public double CenterX => (Left + Right) / 2.0;
        public double CenterY => (Bottom + Top) / 2.0;
        public double Width => Right - Left;
        public double Height => Top - Bottom;
    }

    internal sealed class StepSilhouettePlacement
    {
        public StepSilhouetteBounds TargetBounds { get; set; }
        public double RotX { get; set; }
        public double RotY { get; set; }
        public double RotZ { get; set; }
        public double Rotation2D { get; set; }
    }

    internal static class StepSilhouetteProjection
    {
        private const double PointEpsilonMm = 1e-5;
        private const int OutputCoordDecimals = 3;
        private const double OutputMinLineLengthMm = 0.03;
        private const double OcctOutputMinLineLengthMm = 0.03;
        private const double OcctOverlapCoverageThreshold = 0.90;
        private const double OcctLineCenterlineBucketToleranceMm = 0.01;
        private const double OcctLineCenterlineMergeToleranceMm = 0.001;
        private const double OcctLineProjectedIntervalTouchToleranceMm = 0.001;
        private const double OcctLineParallelCrossTolerance = 0.0001;
        private const double OcctContactArcCenterToleranceMm = 0.001;
        private const double OcctContactArcRadiusBucketMm = 0.02;
        private const double OcctContactArcMaxRadiusMm = 0.6;
        private const double OcctContactChordRadiusToleranceMm = 0.03;
        private const double OcctContactChordMinRadiusMarginMm = 0.12;
        private const double OcctContactChordMaxRadiusFactor = 2.2;
        private const double OcctContactChordMinSweepDeg = 20.0;
        private const double OcctContactChordMaxSweepDeg = 120.0;
        private const double OcctContactChordMinLineLengthMm = 0.25;
        private const double ProjectionLineWidthMm = 0.1;
        private const double OptimizePointGridMm = 0.001;
        private const double StrokeCoverageSampleStepFactor = 0.33;

        public static IReadOnlyList<StepSilhouettePrimitive> Generate(
            byte[] stepData,
            StepSilhouettePlacement placement)
        {
            if (stepData == null || stepData.Length == 0)
                return Array.Empty<StepSilhouettePrimitive>();
            if (placement == null || placement.TargetBounds == null)
                return Array.Empty<StepSilhouettePrimitive>();

            return GenerateWithOcctHelper(stepData, placement);
        }

        private static IReadOnlyList<StepSilhouettePrimitive> GenerateWithOcctHelper(
            byte[] stepData,
            StepSilhouettePlacement placement)
        {
            string helperPath = FindOcctHlrExecutable();
            if (string.IsNullOrWhiteSpace(helperPath))
                throw new InvalidOperationException(
                    "OCCT HLR helper was not found. Reinstall EasyEDA-Loader with the StepOcctHlr folder or set EASYEDA_LOADER_OCCT_HLR to StepOcctHlr.exe.");

            string tempStep = null;
            string tempJson = null;
            try
            {
                tempStep = Path.Combine(Path.GetTempPath(), "EasyEDALoaderHlr_" + Guid.NewGuid().ToString("N") + ".step");
                tempJson = Path.Combine(Path.GetTempPath(), "EasyEDALoaderHlr_" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllBytes(tempStep, stepData);

                var startInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    WorkingDirectory = Path.GetDirectoryName(helperPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add(tempStep);
                startInfo.ArgumentList.Add(tempJson);
                startInfo.ArgumentList.Add("--rot-x");
                startInfo.ArgumentList.Add(placement.RotX.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--rot-y");
                startInfo.ArgumentList.Add(placement.RotY.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--rot-z");
                startInfo.ArgumentList.Add(placement.RotZ.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--rotation2d");
                startInfo.ArgumentList.Add(placement.Rotation2D.ToString(CultureInfo.InvariantCulture));

                var standardOutput = new StringBuilder();
                var standardError = new StringBuilder();
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                        throw new InvalidOperationException("OCCT HLR helper process did not start: " + helperPath);
                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                            standardOutput.AppendLine(args.Data);
                    };
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                            standardError.AppendLine(args.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); }
                        catch { }
                        throw new TimeoutException("OCCT HLR helper timed out after 30 seconds: " + helperPath);
                    }
                    process.WaitForExit();

                    if (process.ExitCode != 0 || !File.Exists(tempJson))
                    {
                        string detail = FirstNonEmpty(
                            standardError.ToString().Trim(),
                            standardOutput.ToString().Trim(),
                            File.Exists(tempJson) ? ReadOcctError(tempJson) : null,
                            "No error output.");
                        throw new InvalidOperationException(
                            "OCCT HLR helper failed with exit code " +
                            process.ExitCode.ToString(CultureInfo.InvariantCulture) +
                            ": " +
                            detail);
                    }
                }

                return ReadOcctProjectionJson(tempJson, placement.TargetBounds);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OCCT HLR projection failed: " + ex.Message);
                throw;
            }
            finally
            {
                TryDeleteFile(tempStep);
                TryDeleteFile(tempJson);
            }
        }

        private static string FindOcctHlrExecutable()
        {
            string configuredPath = Environment.GetEnvironmentVariable("EASYEDA_LOADER_OCCT_HLR");
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;

            foreach (string baseDirectory in GetOcctHlrBaseDirectories())
            {
                string local = Path.Combine(baseDirectory, "StepOcctHlr.exe");
                if (File.Exists(local))
                    return local;

                string sibling = Path.Combine(baseDirectory, "StepOcctHlr", "StepOcctHlr.exe");
                if (File.Exists(sibling))
                    return sibling;

                string solutionDebug = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "StepOcctHlr", "bin", "Debug", "net8.0-windows7.0", "win-x64", "StepOcctHlr.exe"));
                if (File.Exists(solutionDebug))
                    return solutionDebug;

                string solutionRelease = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "StepOcctHlr", "bin", "Release", "net8.0-windows7.0", "win-x64", "StepOcctHlr.exe"));
                if (File.Exists(solutionRelease))
                    return solutionRelease;

                string testHarnessDebug = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "StepOcctHlr", "bin", "Debug", "net8.0-windows7.0", "win-x64", "StepOcctHlr.exe"));
                if (File.Exists(testHarnessDebug))
                    return testHarnessDebug;

                string testHarnessRelease = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "StepOcctHlr", "bin", "Release", "net8.0-windows7.0", "win-x64", "StepOcctHlr.exe"));
                if (File.Exists(testHarnessRelease))
                    return testHarnessRelease;
            }

            return null;
        }

        private static IEnumerable<string> GetOcctHlrBaseDirectories()
        {
            var directories = new List<string>();
            AddDirectory(directories, AppContext.BaseDirectory);

            string assemblyLocation = typeof(StepSilhouetteProjection).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(assemblyLocation))
                AddDirectory(directories, Path.GetDirectoryName(assemblyLocation));

            return directories;
        }

        private static void AddDirectory(List<string> directories, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directory);
            }
            catch
            {
                return;
            }

            if (!directories.Any(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
                directories.Add(fullPath);
        }

        private static IReadOnlyList<StepSilhouettePrimitive> ReadOcctProjectionJson(
            string jsonPath,
            StepSilhouetteBounds targetBounds)
        {
            string json = File.ReadAllText(jsonPath);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("Success", out JsonElement successElement) || !successElement.GetBoolean())
                    throw new InvalidOperationException("OCCT HLR helper failed: " + ReadOcctError(root));
                if (!root.TryGetProperty("Primitives", out JsonElement primitivesElement) || primitivesElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("OCCT HLR helper result does not contain a primitives array.");

                var sourcePrimitives = new List<StepSilhouettePrimitive>();
                foreach (JsonElement primitiveElement in primitivesElement.EnumerateArray())
                {
                    string kind = primitiveElement.GetProperty("Kind").GetString();
                    if (kind == "Line")
                    {
                        sourcePrimitives.Add(StepSilhouettePrimitive.Line(
                            primitiveElement.GetProperty("X1").GetDouble(),
                            primitiveElement.GetProperty("Y1").GetDouble(),
                            primitiveElement.GetProperty("X2").GetDouble(),
                            primitiveElement.GetProperty("Y2").GetDouble()));
                    }
                    else if (kind == "Arc")
                    {
                        sourcePrimitives.Add(StepSilhouettePrimitive.Arc(
                            primitiveElement.GetProperty("CenterX").GetDouble(),
                            primitiveElement.GetProperty("CenterY").GetDouble(),
                            primitiveElement.GetProperty("Radius").GetDouble(),
                            primitiveElement.GetProperty("StartAngle").GetDouble(),
                            primitiveElement.GetProperty("EndAngle").GetDouble()));
                    }
                }

                if (sourcePrimitives.Count == 0)
                    return Array.Empty<StepSilhouettePrimitive>();

                StepSilhouetteBounds sourceBounds = BoundsForPrimitives(sourcePrimitives);
                if (sourceBounds == null)
                    return Array.Empty<StepSilhouettePrimitive>();

                List<StepSilhouettePrimitive> placed = PlacePrimitivesWithoutRescale(
                    sourcePrimitives,
                    targetBounds,
                    sourceBounds,
                    0.0);
                return OptimizeOcctPrimitives(placed);
            }
        }

        private static string ReadOcctError(string jsonPath)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath)))
                {
                    return ReadOcctError(document.RootElement);
                }
            }
            catch (Exception ex)
            {
                return "Could not read helper error JSON: " + ex.Message;
            }
        }

        private static string ReadOcctError(JsonElement root)
        {
            if (root.TryGetProperty("Error", out JsonElement errorElement) &&
                errorElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(errorElement.GetString()))
                return errorElement.GetString();

            return "Unknown helper error.";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private static List<StepSilhouettePrimitive> PlacePrimitivesWithoutRescale(
            List<StepSilhouettePrimitive> primitives,
            StepSilhouetteBounds targetBounds,
            StepSilhouetteBounds sourceBounds,
            double minimumLengthMm = OutputMinLineLengthMm)
        {
            double sourceCenterX = sourceBounds.CenterX;
            double sourceCenterY = sourceBounds.CenterY;
            double targetCenterX = targetBounds.CenterX;
            double targetCenterY = targetBounds.CenterY;
            var placed = new List<StepSilhouettePrimitive>(primitives.Count);

            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    Point2d a = MapPlacedPoint(primitive.X1, primitive.Y1, sourceCenterX, sourceCenterY, targetCenterX, targetCenterY);
                    Point2d b = MapPlacedPoint(primitive.X2, primitive.Y2, sourceCenterX, sourceCenterY, targetCenterX, targetCenterY);
                    Point2d roundedA = new Point2d(RoundCoord(a.X), RoundCoord(a.Y));
                    Point2d roundedB = new Point2d(RoundCoord(b.X), RoundCoord(b.Y));
                    if (Distance(roundedA, roundedB) >= minimumLengthMm)
                        placed.Add(StepSilhouettePrimitive.Line(roundedA.X, roundedA.Y, roundedB.X, roundedB.Y));
                }
                else
                {
                    Point2d center = MapPlacedPoint(primitive.CenterX, primitive.CenterY, sourceCenterX, sourceCenterY, targetCenterX, targetCenterY);
                    if (primitive.Radius >= minimumLengthMm)
                    {
                        placed.Add(StepSilhouettePrimitive.Arc(
                            RoundCoord(center.X),
                            RoundCoord(center.Y),
                            RoundCoord(primitive.Radius),
                            RoundCoord(primitive.StartAngle),
                            RoundCoord(primitive.EndAngle)));
                    }
                }
            }

            return placed;
        }

        private static Point2d MapPlacedPoint(
            double x,
            double y,
            double sourceCenterX,
            double sourceCenterY,
            double targetCenterX,
            double targetCenterY)
        {
            return new Point2d(targetCenterX + x - sourceCenterX, targetCenterY + y - sourceCenterY);
        }

        private static StepSilhouetteBounds BoundsForPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            var bounds = new List<StepSilhouetteBounds>();
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    bounds.Add(new StepSilhouetteBounds
                    {
                        Left = Math.Min(primitive.X1, primitive.X2),
                        Bottom = Math.Min(primitive.Y1, primitive.Y2),
                        Right = Math.Max(primitive.X1, primitive.X2),
                        Top = Math.Max(primitive.Y1, primitive.Y2)
                    });
                }
                else
                {
                    bounds.Add(BoundsForArc(primitive.CenterX, primitive.CenterY, primitive.Radius, primitive.StartAngle, primitive.EndAngle));
                }
            }

            if (bounds.Count == 0)
                return null;

            return new StepSilhouetteBounds
            {
                Left = bounds.Min(bound => bound.Left),
                Bottom = bounds.Min(bound => bound.Bottom),
                Right = bounds.Max(bound => bound.Right),
                Top = bounds.Max(bound => bound.Top)
            };
        }

        private static List<StepSilhouettePrimitive> OptimizeOcctPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            primitives = MergeTouchingOcctLinePrimitives(primitives ?? new List<StepSilhouettePrimitive>());
            primitives = RemoveFullyOverlappedOcctPrimitives(primitives);
            primitives = ReplaceCircularContactChordLinesWithArcs(primitives);
            return RemoveSmallOcctPrimitives(primitives);
        }

        private static List<StepSilhouettePrimitive> RemoveFullyOverlappedOcctPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            if (primitives == null || primitives.Count < 2)
                return primitives ?? new List<StepSilhouettePrimitive>();

            double coverageTolerance = ProjectionLineWidthMm;
            double sampleStep = Math.Max(ProjectionLineWidthMm * StrokeCoverageSampleStepFactor, OptimizePointGridMm);
            double[] lengths = primitives.Select(PrimitiveLength).ToArray();
            StepSilhouetteBounds[] bounds = primitives
                .Select(primitive => ExpandedPrimitiveBounds(primitive, coverageTolerance))
                .ToArray();
            int[] order = Enumerable.Range(0, primitives.Count)
                .OrderByDescending(index => lengths[index])
                .ThenBy(index => index)
                .ToArray();
            var remove = new bool[primitives.Count];
            foreach (int candidateIndex in order)
            {
                if (remove[candidateIndex])
                    continue;
                if (lengths[candidateIndex] <= PointEpsilonMm)
                    continue;

                double coveredRatio = OcctStrokeAreaCoverageRatio(
                    primitives,
                    bounds,
                    remove,
                    candidateIndex,
                    coverageTolerance,
                    sampleStep);
                if (coveredRatio >= OcctOverlapCoverageThreshold - PointEpsilonMm)
                    remove[candidateIndex] = true;
            }

            var result = new List<StepSilhouettePrimitive>(primitives.Count);
            for (int index = 0; index < primitives.Count; index++)
            {
                if (!remove[index])
                    result.Add(primitives[index]);
            }

            return result;
        }

        private static List<StepSilhouettePrimitive> ReplaceCircularContactChordLinesWithArcs(List<StepSilhouettePrimitive> primitives)
        {
            if (primitives == null || primitives.Count < 2)
                return primitives ?? new List<StepSilhouettePrimitive>();

            List<CircularContactCluster> clusters = BuildCircularContactClusters(primitives);
            if (clusters.Count == 0)
                return primitives;

            var result = new List<StepSilhouettePrimitive>(primitives.Count + clusters.Count * 3);
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line &&
                    TryCreateCircularContactChordArc(primitive, clusters, out StepSilhouettePrimitive arc))
                {
                    result.Add(arc);
                    continue;
                }

                result.Add(primitive);
            }

            return result;
        }

        private static bool TryCreateCircularContactChordArc(
            StepSilhouettePrimitive primitive,
            List<CircularContactCluster> clusters,
            out StepSilhouettePrimitive arc)
        {
            arc = null;

            Point2d start = new Point2d(primitive.X1, primitive.Y1);
            Point2d end = new Point2d(primitive.X2, primitive.Y2);
            if (Distance(start, end) < OcctContactChordMinLineLengthMm)
                return false;

            foreach (CircularContactCluster cluster in clusters)
            {
                Point2d center = new Point2d(cluster.CenterX, cluster.CenterY);
                double startRadius = Distance(start, center);
                double endRadius = Distance(end, center);
                double radius = (startRadius + endRadius) / 2.0;
                if (Math.Abs(startRadius - endRadius) > OcctContactChordRadiusToleranceMm)
                    continue;
                if (radius < cluster.MaxRadius + OcctContactChordMinRadiusMarginMm)
                    continue;
                if (radius > cluster.MaxRadius * OcctContactChordMaxRadiusFactor)
                    continue;

                double startAngle = AngleForPoint(center, start);
                double endAngle = AngleForPoint(center, end);
                double sweep = PositiveModulo(endAngle - startAngle, 360.0);
                if (sweep > 180.0)
                {
                    double temp = startAngle;
                    startAngle = endAngle;
                    endAngle = temp;
                    sweep = 360.0 - sweep;
                }

                if (sweep < OcctContactChordMinSweepDeg || sweep > OcctContactChordMaxSweepDeg)
                    continue;

                arc = StepSilhouettePrimitive.Arc(
                    RoundCoord(cluster.CenterX),
                    RoundCoord(cluster.CenterY),
                    RoundCoord(radius),
                    RoundCoord(startAngle),
                    RoundCoord(startAngle + sweep));
                return true;
            }

            return false;
        }

        private static List<CircularContactCluster> BuildCircularContactClusters(List<StepSilhouettePrimitive> primitives)
        {
            var builders = new Dictionary<string, CircularContactClusterBuilder>();
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind != StepSilhouettePrimitiveKind.Arc ||
                    primitive.Radius <= PointEpsilonMm ||
                    primitive.Radius > OcctContactArcMaxRadiusMm)
                {
                    continue;
                }

                string key =
                    ((int)Math.Round(primitive.CenterX / OcctContactArcCenterToleranceMm)).ToString(CultureInfo.InvariantCulture) +
                    "|" +
                    ((int)Math.Round(primitive.CenterY / OcctContactArcCenterToleranceMm)).ToString(CultureInfo.InvariantCulture);
                if (!builders.TryGetValue(key, out CircularContactClusterBuilder builder))
                {
                    builder = new CircularContactClusterBuilder();
                    builders[key] = builder;
                }

                builder.AddArc(primitive.CenterX, primitive.CenterY, primitive.Radius);
            }

            var clusters = new List<CircularContactCluster>();
            foreach (CircularContactClusterBuilder builder in builders.Values)
            {
                if (builder.ArcCount >= 3 && builder.DistinctRadiusCount >= 3)
                    clusters.Add(new CircularContactCluster(builder.CenterX, builder.CenterY, builder.MaxRadius));
            }

            return clusters;
        }

        private static List<StepSilhouettePrimitive> RemoveSmallOcctPrimitives(List<StepSilhouettePrimitive> primitives)
        {
            if (primitives == null || primitives.Count == 0)
                return new List<StepSilhouettePrimitive>();

            var result = new List<StepSilhouettePrimitive>(primitives.Count);
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                {
                    var start = new Point2d(primitive.X1, primitive.Y1);
                    var end = new Point2d(primitive.X2, primitive.Y2);
                    if (Distance(start, end) >= OcctOutputMinLineLengthMm)
                        result.Add(primitive);
                }
                else if (primitive.Radius >= OcctOutputMinLineLengthMm)
                {
                    result.Add(primitive);
                }
            }

            return result;
        }

        private static List<StepSilhouettePrimitive> MergeTouchingOcctLinePrimitives(List<StepSilhouettePrimitive> primitives)
        {
            var groups = new Dictionary<string, List<LineInterval>>();
            var passthrough = new List<StepSilhouettePrimitive>();
            foreach (StepSilhouettePrimitive primitive in primitives)
            {
                if (primitive.Kind != StepSilhouettePrimitiveKind.Line)
                {
                    passthrough.Add(primitive);
                    continue;
                }

                double dx = primitive.X2 - primitive.X1;
                double dy = primitive.Y2 - primitive.Y1;
                double length = Hypot(dx, dy);
                if (length <= PointEpsilonMm)
                    continue;

                double ux = dx / length;
                double uy = dy / length;
                if (ux < -1e-12 || (Math.Abs(ux) <= 1e-12 && uy < 0.0))
                {
                    ux = -ux;
                    uy = -uy;
                }

                double normalX = -uy;
                double normalY = ux;
                double offset = normalX * primitive.X1 + normalY * primitive.Y1;
                double angle = Math.Atan2(uy, ux);
                string key =
                    ((int)Math.Round(angle / 0.001)).ToString(CultureInfo.InvariantCulture) +
                    "|" +
                    ((int)Math.Round(offset / OcctLineCenterlineBucketToleranceMm)).ToString(CultureInfo.InvariantCulture);
                double t1 = ux * primitive.X1 + uy * primitive.Y1;
                double t2 = ux * primitive.X2 + uy * primitive.Y2;
                if (!groups.TryGetValue(key, out List<LineInterval> intervals))
                {
                    intervals = new List<LineInterval>();
                    groups[key] = intervals;
                }

                intervals.Add(new LineInterval(Math.Min(t1, t2), Math.Max(t1, t2), ux, uy, normalX, normalY, offset));
            }

            var merged = new List<StepSilhouettePrimitive>();
            foreach (List<LineInterval> intervals in groups.Values)
            {
                intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
                LineInterval current = intervals[0];
                for (int index = 1; index < intervals.Count; index++)
                {
                    LineInterval next = intervals[index];
                    if (CanMergeOcctLineIntervals(current, next))
                    {
                        current.End = Math.Max(current.End, next.End);
                        continue;
                    }

                    AddMergedOcctLine(merged, current);
                    current = next;
                }

                AddMergedOcctLine(merged, current);
            }

            passthrough.AddRange(merged);
            return passthrough;
        }

        private static bool CanMergeOcctLineIntervals(LineInterval current, LineInterval next)
        {
            if (next.Start > current.End + OcctLineProjectedIntervalTouchToleranceMm)
                return false;

            double cross = Math.Abs(current.Ux * next.Uy - current.Uy * next.Ux);
            if (cross > OcctLineParallelCrossTolerance)
                return false;

            double startDistance = DistanceFromLineIntervalCenterline(current, next, next.Start);
            double endDistance = DistanceFromLineIntervalCenterline(current, next, next.End);
            return startDistance <= OcctLineCenterlineMergeToleranceMm &&
                endDistance <= OcctLineCenterlineMergeToleranceMm;
        }

        private static double DistanceFromLineIntervalCenterline(LineInterval centerline, LineInterval interval, double position)
        {
            double x = interval.Ux * position + interval.NormalX * interval.Offset;
            double y = interval.Uy * position + interval.NormalY * interval.Offset;
            return Math.Abs(centerline.NormalX * x + centerline.NormalY * y - centerline.Offset);
        }

        private static void AddMergedOcctLine(List<StepSilhouettePrimitive> merged, LineInterval interval)
        {
            var start = new Point2d(
                interval.Ux * interval.Start + interval.NormalX * interval.Offset,
                interval.Uy * interval.Start + interval.NormalY * interval.Offset);
            var end = new Point2d(
                interval.Ux * interval.End + interval.NormalX * interval.Offset,
                interval.Uy * interval.End + interval.NormalY * interval.Offset);
            if (Distance(start, end) <= PointEpsilonMm)
                return;

            merged.Add(StepSilhouettePrimitive.Line(
                RoundCoord(start.X),
                RoundCoord(start.Y),
                RoundCoord(end.X),
                RoundCoord(end.Y)));
        }

        private static double OcctStrokeAreaCoverageRatio(
            List<StepSilhouettePrimitive> primitives,
            StepSilhouetteBounds[] bounds,
            bool[] remove,
            int candidateIndex,
            double toleranceMm,
            double sampleStepMm)
        {
            StepSilhouettePrimitive candidate = primitives[candidateIndex];
            double length = PrimitiveLength(candidate);
            int segmentCount = Math.Max(1, Math.Min(512, (int)Math.Ceiling(length / sampleStepMm)));
            double coveredAreaRatioSum = 0.0;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                Point2d sample = PrimitivePointAtFraction(candidate, (segment + 0.5) / segmentCount);
                coveredAreaRatioSum += OcctStrokeAreaCoverageAtPoint(
                    sample,
                    primitives,
                    bounds,
                    remove,
                    candidateIndex,
                    toleranceMm);
            }

            return coveredAreaRatioSum / segmentCount;
        }

        private static double OcctStrokeAreaCoverageAtPoint(
            Point2d point,
            List<StepSilhouettePrimitive> primitives,
            StepSilhouetteBounds[] bounds,
            bool[] remove,
            int candidateIndex,
            double toleranceMm)
        {
            double coverage = 0.0;
            for (int index = 0; index < primitives.Count; index++)
            {
                if (index == candidateIndex || remove[index])
                    continue;
                if (!PointInBounds(point, bounds[index]))
                    continue;
                double distance = DistancePointToPrimitive(point, primitives[index]);
                if (distance > toleranceMm + PointEpsilonMm)
                    continue;

                coverage = Math.Max(coverage, Math.Max(0.0, 1.0 - distance / ProjectionLineWidthMm));
                if (coverage >= 1.0 - PointEpsilonMm)
                    return 1.0;
            }

            return coverage;
        }

        private static Point2d PrimitivePointAtFraction(StepSilhouettePrimitive primitive, double fraction)
        {
            fraction = Math.Max(0.0, Math.Min(1.0, fraction));
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
            {
                return new Point2d(
                    primitive.X1 + (primitive.X2 - primitive.X1) * fraction,
                    primitive.Y1 + (primitive.Y2 - primitive.Y1) * fraction);
            }

            double sweep = PrimitiveArcSweepDegrees(primitive);
            return ArcEndpoint(primitive, primitive.StartAngle + sweep * fraction);
        }

        private static StepSilhouetteBounds ExpandedPrimitiveBounds(StepSilhouettePrimitive primitive, double paddingMm)
        {
            StepSilhouetteBounds bounds = primitive.Kind == StepSilhouettePrimitiveKind.Line
                ? new StepSilhouetteBounds
                {
                    Left = Math.Min(primitive.X1, primitive.X2),
                    Bottom = Math.Min(primitive.Y1, primitive.Y2),
                    Right = Math.Max(primitive.X1, primitive.X2),
                    Top = Math.Max(primitive.Y1, primitive.Y2)
                }
                : BoundsForArc(primitive.CenterX, primitive.CenterY, primitive.Radius, primitive.StartAngle, primitive.EndAngle);

            return new StepSilhouetteBounds
            {
                Left = bounds.Left - paddingMm,
                Bottom = bounds.Bottom - paddingMm,
                Right = bounds.Right + paddingMm,
                Top = bounds.Top + paddingMm
            };
        }

        private static bool PointInBounds(Point2d point, StepSilhouetteBounds bounds)
        {
            return point.X >= bounds.Left - PointEpsilonMm &&
                point.X <= bounds.Right + PointEpsilonMm &&
                point.Y >= bounds.Bottom - PointEpsilonMm &&
                point.Y <= bounds.Top + PointEpsilonMm;
        }

        private static StepSilhouetteBounds BoundsForArc(double centerX, double centerY, double radius, double startAngle, double endAngle)
        {
            var bounds = new StepSilhouetteBounds
            {
                Left = Math.Min(centerX - radius, centerX + radius),
                Bottom = Math.Min(centerY - radius, centerY + radius),
                Right = Math.Max(centerX - radius, centerX + radius),
                Top = Math.Max(centerY - radius, centerY + radius)
            };

            double[] cardinalAngles = { 0.0, 90.0, 180.0, 270.0 };
            foreach (double angle in cardinalAngles)
            {
                if (AngleInArcSweep(angle, startAngle, endAngle))
                {
                    double x = centerX + radius * Math.Cos(DegreesToRadians(angle));
                    double y = centerY + radius * Math.Sin(DegreesToRadians(angle));
                    bounds.Left = Math.Min(bounds.Left, x);
                    bounds.Right = Math.Max(bounds.Right, x);
                    bounds.Bottom = Math.Min(bounds.Bottom, y);
                    bounds.Top = Math.Max(bounds.Top, y);
                }
            }

            return bounds;
        }

        private static bool AngleInArcSweep(double testAngle, double startAngle, double endAngle)
        {
            while (testAngle < startAngle)
                testAngle += 360.0;
            return testAngle >= startAngle && testAngle <= endAngle;
        }

        private static double PrimitiveArcSweepDegrees(StepSilhouettePrimitive primitive)
        {
            double end = primitive.EndAngle;
            while (end < primitive.StartAngle)
                end += 360.0;
            return end - primitive.StartAngle;
        }

        private static double PrimitiveLength(StepSilhouettePrimitive primitive)
        {
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                return Hypot(primitive.X2 - primitive.X1, primitive.Y2 - primitive.Y1);
            return primitive.Radius * DegreesToRadians(PrimitiveArcSweepDegrees(primitive));
        }

        private static Point2d ArcEndpoint(StepSilhouettePrimitive primitive, double angleDegrees)
        {
            double angle = DegreesToRadians(angleDegrees);
            return new Point2d(
                primitive.CenterX + primitive.Radius * Math.Cos(angle),
                primitive.CenterY + primitive.Radius * Math.Sin(angle));
        }

        private static double DistancePointToPrimitive(Point2d point, StepSilhouettePrimitive primitive)
        {
            if (primitive.Kind == StepSilhouettePrimitiveKind.Line)
                return DistancePointToSegment(point, new Point2d(primitive.X1, primitive.Y1), new Point2d(primitive.X2, primitive.Y2));

            Point2d center = new Point2d(primitive.CenterX, primitive.CenterY);
            double pointAngle = RadiansToDegrees(Math.Atan2(point.Y - center.Y, point.X - center.X));
            while (pointAngle < 0)
                pointAngle += 360.0;

            if (AngleInArcSweep(pointAngle, primitive.StartAngle, primitive.EndAngle))
                return Math.Abs(Distance(point, center) - primitive.Radius);

            Point2d start = ArcEndpoint(primitive, primitive.StartAngle);
            Point2d end = ArcEndpoint(primitive, primitive.EndAngle);
            return Math.Min(Distance(point, start), Distance(point, end));
        }

        private static double DistancePointToSegment(Point2d point, Point2d start, Point2d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSq = dx * dx + dy * dy;
            if (lengthSq <= 1e-12)
                return Distance(point, start);

            double projection = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSq;
            projection = Math.Max(0.0, Math.Min(1.0, projection));
            return Distance(point, new Point2d(start.X + projection * dx, start.Y + projection * dy));
        }

        private static double AngleForPoint(Point2d center, Point2d point)
        {
            return PositiveModulo(RadiansToDegrees(Math.Atan2(point.Y - center.Y, point.X - center.X)), 360.0);
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static double Hypot(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }

        private static double Distance(Point2d a, Point2d b)
        {
            return Hypot(a.X - b.X, a.Y - b.Y);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static double RadiansToDegrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        private static double PositiveModulo(double value, double modulus)
        {
            double result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static double RoundCoord(double value)
        {
            return Math.Round(value, OutputCoordDecimals, MidpointRounding.AwayFromZero);
        }

        private sealed class LineInterval
        {
            public LineInterval(double start, double end, double ux, double uy, double normalX, double normalY, double offset)
            {
                Start = start;
                End = end;
                Ux = ux;
                Uy = uy;
                NormalX = normalX;
                NormalY = normalY;
                Offset = offset;
            }

            public double Start { get; set; }
            public double End { get; set; }
            public double Ux { get; }
            public double Uy { get; }
            public double NormalX { get; }
            public double NormalY { get; }
            public double Offset { get; }
        }

        private sealed class CircularContactClusterBuilder
        {
            private readonly HashSet<int> radiusBuckets = new HashSet<int>();
            private double centerXSum;
            private double centerYSum;

            public double CenterX => centerXSum / ArcCount;
            public double CenterY => centerYSum / ArcCount;
            public double MaxRadius { get; private set; }
            public int ArcCount { get; private set; }
            public int DistinctRadiusCount => radiusBuckets.Count;

            public void AddArc(double centerX, double centerY, double radius)
            {
                ArcCount++;
                centerXSum += centerX;
                centerYSum += centerY;

                MaxRadius = Math.Max(MaxRadius, radius);
                int bucket = (int)Math.Round(radius / OcctContactArcRadiusBucketMm);
                radiusBuckets.Add(bucket);
            }
        }

        private sealed class CircularContactCluster
        {
            public CircularContactCluster(double centerX, double centerY, double maxRadius)
            {
                CenterX = centerX;
                CenterY = centerY;
                MaxRadius = maxRadius;
            }

            public double CenterX { get; }
            public double CenterY { get; }
            public double MaxRadius { get; }
        }

        private struct Point2d
        {
            public Point2d(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }
    }
}
