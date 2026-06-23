using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    public sealed class StepWatermarkCleanerOptions
    {
        public double WatermarkMinLuminance { get; set; } = 0.92;
        public double EmbeddedWatermarkMinLuminance { get; set; } = 0.62;
        public double DarkWatermarkMaxLuminance { get; set; } = 0.08;
        public double BodyMaxLuminance { get; set; } = 0.46;
        public double NeutralBodyMaxLuminance { get; set; } = 0.78;
        public double NeutralMaxChannelSpread { get; set; } = 0.16;
        public double MaxFaceAreaRatio { get; set; } = 0.18;
        public double MaxFaceDiagonalRatio { get; set; } = 0.45;
        public double ThinSolidMaxThickness { get; set; } = 0.01;
        public double ThinSolidMaxSize { get; set; } = 4.0;
        public double EmbeddedReliefMaxDepth { get; set; } = 0.08;
        public double HostPlaneSearchDistance { get; set; } = 0.08;
        public double HostPlaneProjectionPadding { get; set; } = 0.05;
        public double HostLoopAdjacentMaxDepth { get; set; } = 0.08;
        public double PlaneTolerance { get; set; } = 0.0002;
        public bool RequireDarkOwner { get; set; } = true;
        public bool RemoveEmbeddedWatermarkTopology { get; set; } = true;
        public bool CleanText { get; set; }
        public int CleanTextMinCandidateFaceCount { get; set; } = 80;
        public bool UseMarkedRegionsOnly { get; set; }
        public double MarkedRegionPaddingPixels { get; set; } = 18.0;
        public double MarkedCandidateMinOverlap { get; set; } = 0.05;
        public double MarkedLoopMinOverlap { get; set; } = 0.25;
        public double AutomaticClusterGapRatio { get; set; } = 0.06;
        public int AutomaticClusterMinFaceCount { get; set; } = 3;
        public int AutomaticClusterMinPointCount { get; set; } = 24;
        public bool RequireKnownWatermarkPattern { get; set; } = true;
        public bool BuildRemovedGeometryStep { get; set; } = true;
        public List<StepWatermarkMarkedRegion> MarkedRegions { get; } = new List<StepWatermarkMarkedRegion>();
    }

    public sealed class StepWatermarkMarkedRegion
    {
        public string ViewName { get; set; }
        public string SourceMarkerPath { get; set; }
        public string SourceProjectionPath { get; set; }
        public int UAxis { get; set; }
        public int USign { get; set; }
        public int VAxis { get; set; }
        public int VSign { get; set; }
        public int DepthAxis { get; set; }
        public int DepthSign { get; set; }
        public double ModelUMin { get; set; }
        public double ModelUMax { get; set; }
        public double ModelVMin { get; set; }
        public double ModelVMax { get; set; }
        public double ScalePixelsPerModelUnit { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public int RectangleX { get; set; }
        public int RectangleY { get; set; }
        public int RectangleWidth { get; set; }
        public int RectangleHeight { get; set; }
        public string TemplateName { get; set; }
        public string Kind { get; set; }
        public double Score { get; set; }
        public double ChamferDistance { get; set; }
        public int EdgePixelCount { get; set; }
    }

    public sealed class StepWatermarkCleanerReport
    {
        public string CleanedStep { get; internal set; }
        public string RemovedGeometryStep { get; internal set; }
        public int SolidCount { get; internal set; }
        public int StyledFaceCount { get; internal set; }
        public int CandidateFaceCount { get; internal set; }
        public int RecoloredFaceCount { get; internal set; }
        public int RemovedSolidCount { get; internal set; }
        public int FlattenedFaceCount { get; internal set; }
        public int FlattenedPointCount { get; internal set; }
        public StepWatermarkDetectionReport DetectionReport { get; internal set; }
        public IReadOnlyList<string> Diagnostics { get; internal set; }
        public IReadOnlyList<StepWatermarkCleanerTiming> Timings { get; internal set; }
    }

    public sealed class StepWatermarkCleanerTiming
    {
        public string Name { get; internal set; }
        public long ElapsedMilliseconds { get; internal set; }
    }

    public sealed class StepWatermarkDetectionReport
    {
        public int SolidCount { get; internal set; }
        public int StyledFaceCount { get; internal set; }
        public int RemovableSolidCount { get; internal set; }
        public int EmbeddedFaceCount { get; internal set; }
        public int CoplanarFaceCount { get; internal set; }
        public int HostLoopCandidateCount { get; internal set; }
        public int HostLoopCount { get; internal set; }
        public IReadOnlyList<int> RemovableSolidIds { get; internal set; }
        public IReadOnlyList<int> EmbeddedFaceIds { get; internal set; }
        public IReadOnlyList<int> CoplanarFaceIds { get; internal set; }
        public IReadOnlyList<StepWatermarkHostLoopDetection> HostLoops { get; internal set; }
        public IReadOnlyList<StepWatermarkRegionDetection> Regions { get; internal set; }
        public IReadOnlyList<string> Diagnostics { get; internal set; }
    }

    public sealed class StepWatermarkRegionDetection
    {
        public int EntityId { get; internal set; }
        public string Kind { get; internal set; }
        public string ViewName { get; internal set; }
        public string TemplateName { get; internal set; }
        public string Text { get; internal set; }
        public double Score { get; internal set; }
        public double ChamferDistance { get; internal set; }
        public int EdgePixelCount { get; internal set; }
        public int? RectangleX { get; internal set; }
        public int? RectangleY { get; internal set; }
        public int? RectangleWidth { get; internal set; }
        public int? RectangleHeight { get; internal set; }
        public int? ImageWidth { get; internal set; }
        public int? ImageHeight { get; internal set; }
    }

    public sealed class StepWatermarkHostLoopDetection
    {
        public int HostFaceId { get; internal set; }
        public int BoundId { get; internal set; }
        public string ProjectionAxis { get; internal set; }
    }

    public sealed class StepWatermarkResidualTopologyReport
    {
        public IReadOnlyList<string> Failures { get; internal set; }
    }

    public static class StepWatermarkCleaner
    {
        private static readonly Regex ReferenceRegex = new Regex(@"#(\d+)", RegexOptions.Compiled);
        private static readonly Regex EntityTypeRegex = new Regex(@"^\s*([A-Z0-9_]+)\s*\(", RegexOptions.Compiled);
        private static readonly Regex ColourRegex = new Regex(
            @"COLOUR_RGB\s*\(\s*'[^']*'\s*,\s*([-+0-9.Ee]+)\s*,\s*([-+0-9.Ee]+)\s*,\s*([-+0-9.Ee]+)\s*\)",
            RegexOptions.Compiled);
        private static readonly Regex CartesianPointRegex = new Regex(
            @"CARTESIAN_POINT\s*\(\s*(?:'[^']*'|\$)\s*,\s*\(([^)]*)\)",
            RegexOptions.Compiled);
        private static readonly TextProjectionViewSpec[] TextProjectionViews =
        {
            new TextProjectionViewSpec("x_plus", 0, 1, 1, 1, 2, 1),
            new TextProjectionViewSpec("x_minus", 0, -1, 1, -1, 2, 1),
            new TextProjectionViewSpec("y_plus", 1, 1, 0, -1, 2, 1),
            new TextProjectionViewSpec("y_minus", 1, -1, 0, 1, 2, 1),
            new TextProjectionViewSpec("z_plus", 2, 1, 0, 1, 1, 1),
            new TextProjectionViewSpec("z_minus", 2, -1, 0, -1, 1, 1)
        };

        public static byte[] Clean(byte[] stepData, StepWatermarkCleanerOptions options = null)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            var text = Encoding.Latin1.GetString(stepData);
            var report = CleanWithReport(text, CopyOptions(options, buildRemovedGeometryStep: false));
            return Encoding.Latin1.GetBytes(report.CleanedStep);
        }

        public static string Clean(string stepText, StepWatermarkCleanerOptions options = null)
        {
            return CleanWithReport(stepText, CopyOptions(options, buildRemovedGeometryStep: false)).CleanedStep;
        }

        private static StepWatermarkCleanerOptions CopyOptions(
            StepWatermarkCleanerOptions source,
            bool? buildRemovedGeometryStep = null)
        {
            source = source ?? new StepWatermarkCleanerOptions();
            var result = new StepWatermarkCleanerOptions
            {
                WatermarkMinLuminance = source.WatermarkMinLuminance,
                EmbeddedWatermarkMinLuminance = source.EmbeddedWatermarkMinLuminance,
                DarkWatermarkMaxLuminance = source.DarkWatermarkMaxLuminance,
                BodyMaxLuminance = source.BodyMaxLuminance,
                NeutralBodyMaxLuminance = source.NeutralBodyMaxLuminance,
                NeutralMaxChannelSpread = source.NeutralMaxChannelSpread,
                MaxFaceAreaRatio = source.MaxFaceAreaRatio,
                MaxFaceDiagonalRatio = source.MaxFaceDiagonalRatio,
                ThinSolidMaxThickness = source.ThinSolidMaxThickness,
                ThinSolidMaxSize = source.ThinSolidMaxSize,
                EmbeddedReliefMaxDepth = source.EmbeddedReliefMaxDepth,
                HostPlaneSearchDistance = source.HostPlaneSearchDistance,
                HostPlaneProjectionPadding = source.HostPlaneProjectionPadding,
                HostLoopAdjacentMaxDepth = source.HostLoopAdjacentMaxDepth,
                PlaneTolerance = source.PlaneTolerance,
                RequireDarkOwner = source.RequireDarkOwner,
                RemoveEmbeddedWatermarkTopology = source.RemoveEmbeddedWatermarkTopology,
                CleanText = source.CleanText,
                CleanTextMinCandidateFaceCount = source.CleanTextMinCandidateFaceCount,
                UseMarkedRegionsOnly = source.UseMarkedRegionsOnly,
                MarkedRegionPaddingPixels = source.MarkedRegionPaddingPixels,
                MarkedCandidateMinOverlap = source.MarkedCandidateMinOverlap,
                MarkedLoopMinOverlap = source.MarkedLoopMinOverlap,
                AutomaticClusterGapRatio = source.AutomaticClusterGapRatio,
                AutomaticClusterMinFaceCount = source.AutomaticClusterMinFaceCount,
                AutomaticClusterMinPointCount = source.AutomaticClusterMinPointCount,
                RequireKnownWatermarkPattern = source.RequireKnownWatermarkPattern,
                BuildRemovedGeometryStep = buildRemovedGeometryStep ?? source.BuildRemovedGeometryStep
            };

            result.MarkedRegions.AddRange(source.MarkedRegions);
            return result;
        }

        public static StepWatermarkDetectionReport Detect(byte[] stepData, StepWatermarkCleanerOptions options = null)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            return Detect(Encoding.Latin1.GetString(stepData), options);
        }

        public static StepWatermarkDetectionReport Detect(string stepText, StepWatermarkCleanerOptions options = null)
        {
            if (stepText == null)
                throw new ArgumentNullException(nameof(stepText));

            options = options ?? new StepWatermarkCleanerOptions();
            var context = BuildCleanupContext(stepText, options, null);
            var detection = DetectAutomaticWatermarks(context);
            return BuildPublicDetectionReport(context, detection);
        }

        public static StepWatermarkDetectionReport CreateVerifiedCleanupDetectionReport(StepWatermarkDetectionReport detectionReport)
        {
            if (detectionReport == null)
                throw new ArgumentNullException(nameof(detectionReport));

            IReadOnlyList<StepWatermarkRegionDetection> sourceRegions =
                detectionReport.Regions ?? Array.Empty<StepWatermarkRegionDetection>();
            List<StepWatermarkRegionDetection> verifiedRegions = sourceRegions
                .Where(region => IsVerifiedCleanupRegionKind(region.Kind))
                .ToList();

            return new StepWatermarkDetectionReport
            {
                SolidCount = detectionReport.SolidCount,
                StyledFaceCount = detectionReport.StyledFaceCount,
                RemovableSolidCount = detectionReport.RemovableSolidCount,
                EmbeddedFaceCount = detectionReport.EmbeddedFaceCount,
                CoplanarFaceCount = detectionReport.CoplanarFaceCount,
                HostLoopCandidateCount = detectionReport.HostLoopCandidateCount,
                HostLoopCount = detectionReport.HostLoopCount,
                RemovableSolidIds = detectionReport.RemovableSolidIds ?? Array.Empty<int>(),
                EmbeddedFaceIds = detectionReport.EmbeddedFaceIds ?? Array.Empty<int>(),
                CoplanarFaceIds = detectionReport.CoplanarFaceIds ?? Array.Empty<int>(),
                HostLoops = detectionReport.HostLoops ?? Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = verifiedRegions,
                Diagnostics = detectionReport.Diagnostics ?? Array.Empty<string>()
            };
        }

        private static bool IsVerifiedCleanupRegionKind(string kind)
        {
            return !string.Equals(kind, "solid-candidate", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDirectDetectionRegion(StepWatermarkRegionDetection region)
        {
            return region != null &&
                region.RectangleX.HasValue &&
                region.RectangleY.HasValue &&
                region.RectangleWidth.HasValue &&
                region.RectangleHeight.HasValue &&
                region.RectangleWidth.Value > 0 &&
                region.RectangleHeight.Value > 0;
        }

        public static StepWatermarkResidualTopologyReport FindResidualCleanupTopology(
            string originalStepText,
            string cleanStepText,
            StepWatermarkDetectionReport detectionReport,
            StepWatermarkCleanerOptions options = null)
        {
            if (originalStepText == null)
                throw new ArgumentNullException(nameof(originalStepText));
            if (cleanStepText == null)
                throw new ArgumentNullException(nameof(cleanStepText));

            options = options ?? new StepWatermarkCleanerOptions();
            var failures = new List<string>();
            if (detectionReport == null)
            {
                failures.Add("Detection report is missing; residual cleanup topology cannot be verified.");
                return new StepWatermarkResidualTopologyReport
                {
                    Failures = failures
                };
            }

            if (detectionReport.HostLoopCount > 0 &&
                (detectionReport.HostLoops == null || detectionReport.HostLoops.Count == 0))
            {
                failures.Add("Detection report is missing host-loop details; residual cleanup topology cannot be verified.");
            }

            if (detectionReport.HostLoops == null || detectionReport.HostLoops.Count == 0)
            {
                return new StepWatermarkResidualTopologyReport
                {
                    Failures = failures
                };
            }

            var originalData = StepData.Parse(originalStepText);
            originalData.BuildIndexes();
            var cleanData = StepData.Parse(cleanStepText);
            cleanData.BuildIndexes();

            foreach (var hostLoop in detectionReport.HostLoops)
            {
                var originalLoopBounds = originalData.GetBounds(hostLoop.BoundId);
                var originalHostBounds = originalData.GetBounds(hostLoop.HostFaceId);
                if (!originalLoopBounds.HasValue || !originalHostBounds.HasValue)
                {
                    failures.Add(
                        "Detected host loop #" +
                        hostLoop.BoundId.ToString(CultureInfo.InvariantCulture) +
                        " on host face #" +
                        hostLoop.HostFaceId.ToString(CultureInfo.InvariantCulture) +
                        " is missing original topology bounds; residual cleanup topology cannot be verified.");
                    continue;
                }

                if (!cleanData.Entities.TryGetValue(hostLoop.HostFaceId, out var cleanHostFace) ||
                    cleanHostFace.Type != "ADVANCED_FACE")
                {
                    continue;
                }

                if (cleanData.GetAdvancedFaceBounds(hostLoop.HostFaceId).Contains(hostLoop.BoundId))
                {
                    failures.Add(
                        "Detected host loop #" +
                        hostLoop.BoundId.ToString(CultureInfo.InvariantCulture) +
                        " remains on host face #" +
                        hostLoop.HostFaceId.ToString(CultureInfo.InvariantCulture) +
                        ".");
                }

                int axis = ProjectionAxisIndex(hostLoop.ProjectionAxis);
                if (axis < 0)
                    axis = FindPlanarAxis(originalHostBounds.Value, options);
                if (axis < 0)
                    axis = GetSmallestAxis(originalHostBounds.Value);

                double hostCoordinate = (originalHostBounds.Value.Min.Get(axis) + originalHostBounds.Value.Max.Get(axis)) / 2.0;
                double maxResidualDepth = Math.Max(
                    Math.Max(options.HostLoopAdjacentMaxDepth, options.HostPlaneSearchDistance),
                    options.EmbeddedReliefMaxDepth) * 2.0;

                foreach (var entity in cleanData.Entities.Values)
                {
                    if (entity.Type != "ADVANCED_FACE" || entity.Id == hostLoop.HostFaceId)
                        continue;

                    var faceBounds = cleanData.GetBounds(entity.Id);
                    if (!faceBounds.HasValue)
                        continue;

                    if (!ProjectedBoundsInside(faceBounds.Value, originalLoopBounds.Value, axis, options.HostPlaneProjectionPadding))
                        continue;

                    double minDistance = Math.Abs(faceBounds.Value.Min.Get(axis) - hostCoordinate);
                    double maxDistance = Math.Abs(faceBounds.Value.Max.Get(axis) - hostCoordinate);
                    if (Math.Max(minDistance, maxDistance) <= options.PlaneTolerance)
                        continue;

                    if (Math.Max(minDistance, maxDistance) > maxResidualDepth)
                        continue;

                    failures.Add(
                        "Residual cleanup face #" +
                        entity.Id.ToString(CultureInfo.InvariantCulture) +
                        " remains inside detected host loop #" +
                        hostLoop.BoundId.ToString(CultureInfo.InvariantCulture) +
                        " on host face #" +
                        hostLoop.HostFaceId.ToString(CultureInfo.InvariantCulture) +
                        ".");
                    break;
                }
            }

            return new StepWatermarkResidualTopologyReport
            {
                Failures = failures
            };
        }

        public static IReadOnlyList<StepWatermarkMarkedRegion> LoadMarkedRegionsForStepFile(
            string stepFilePath,
            string projectionDirectory,
            string markedDirectory)
        {
            if (string.IsNullOrEmpty(stepFilePath))
                throw new ArgumentException("STEP file path is required.", nameof(stepFilePath));

            var result = new List<StepWatermarkMarkedRegion>();
            if (string.IsNullOrEmpty(markedDirectory) || !Directory.Exists(markedDirectory))
                return result;

            if (string.IsNullOrEmpty(projectionDirectory) || !Directory.Exists(projectionDirectory))
                return result;

            string modelName = Path.GetFileNameWithoutExtension(stepFilePath);
            foreach (string markerPath in Directory.GetFiles(markedDirectory, modelName + "__*.json"))
            {
                string projectionPath = Path.Combine(projectionDirectory, Path.GetFileName(markerPath));
                if (!File.Exists(projectionPath))
                    continue;

                AppendMarkedRegions(result, markerPath, projectionPath);
            }

            return result;
        }

        public static StepWatermarkCleanerReport CleanWithReport(string stepText, StepWatermarkCleanerOptions options = null)
        {
            if (stepText == null)
                throw new ArgumentNullException(nameof(stepText));

            options = options ?? new StepWatermarkCleanerOptions();
            var timings = new List<StepWatermarkCleanerTiming>();
            var totalStopwatch = Stopwatch.StartNew();
            var context = BuildCleanupContext(stepText, options, timings);

            var detection = DetectAutomaticWatermarks(context, timings);
            StepWatermarkCleanerReport report = CleanWithAutomaticDetection(stepText, context, detection, timings);
            totalStopwatch.Stop();
            timings.Insert(
                0,
                new StepWatermarkCleanerTiming
                {
                    Name = "cleaner_total",
                    ElapsedMilliseconds = totalStopwatch.ElapsedMilliseconds
                });
            report.Timings = timings.ToList();
            return report;
        }

        private static StepWatermarkCleanerReport CleanWithAutomaticDetection(
            string stepText,
            CleanupContext context,
            AutomaticWatermarkDetection detection,
            List<StepWatermarkCleanerTiming> timings)
        {
            var edits = new Dictionary<int, string>();
            var data = context.Data;
            var options = context.Options;
            var inactiveDefinitionRoots = new HashSet<int>(detection.RemovableSolidIds);

            MeasureCleanerTiming(
                timings,
                "edit_remove_solids_from_shape_representations",
                () => RemoveSolidsFromShapeRepresentations(data, detection.RemovableSolidIds, edits));

            var flattenResult = MeasureCleanerTiming(
                timings,
                "edit_flatten_embedded_faces",
                () => FlattenEmbeddedWatermarkFaces(
                    data,
                    detection.EmbeddedFaceIds,
                    context.FaceOwners,
                    context.SolidInfo,
                    context.StyledByTarget,
                    options,
                    edits));

            MeasureCleanerTiming(
                timings,
                "edit_add_embedded_host_loops",
                () => AddEmbeddedFaceHostLoopsToFlattenResult(
                    data,
                    detection.EmbeddedFaceIds,
                    context.FaceOwners,
                    context.SolidInfo,
                    context.StyledByTarget,
                    flattenResult,
                    options,
                    edits));

            MeasureCleanerTiming(
                timings,
                "edit_merge_host_face_bounds",
                () => MergeHostFaceBounds(flattenResult, detection.HostFaceBoundsToRemove));
            var flattenRegions = MeasureCleanerTiming(
                timings,
                "edit_build_automatic_flatten_regions",
                () => BuildAutomaticFlattenRegions(
                    data,
                    detection,
                    context.FaceOwners,
                    context.SolidInfo,
                    context.StyledByTarget,
                    options));
            var cleanupVolumes = MeasureCleanerTiming(
                timings,
                "edit_build_automatic_cleanup_volumes",
                () => BuildAutomaticCleanupVolumes(data, context.SolidInfo, flattenRegions, options));
            MeasureCleanerTiming(
                timings,
                "edit_flatten_automatic_regions",
                () => FlattenAllGeometryInsideAutomaticRegions(data, context.SolidInfo, context.StyledByTarget, flattenResult, cleanupVolumes, options, edits));

            MeasureCleanerTiming(
                timings,
                "edit_flatten_coplanar_faces",
                () => FlattenCoplanarWatermarkFaces(
                    data,
                    detection.CoplanarFaceIds,
                    context.FaceOwners,
                    context.SolidInfo,
                    context.StyledByTarget,
                    flattenResult,
                    options,
                    edits));

            int removedEmbeddedFaces = 0;
            int removedHostLoops = 0;
            int removedInactiveDefinitions = 0;
            int recoloredCount = 0;
            if (options.RemoveEmbeddedWatermarkTopology)
            {
                MeasureCleanerTiming(
                    timings,
                    "edit_add_host_loop_residual_interior_faces",
                    () => AddHostLoopResidualInteriorFacesToFlattenResult(
                        data,
                        detection.HostFaceBoundsToRemove,
                        context.FaceOwners,
                        context.SolidInfo,
                        context.StyledByTarget,
                        flattenResult,
                        options));

                foreach (int faceId in flattenResult.FlattenedFaces)
                    inactiveDefinitionRoots.Add(faceId);
                foreach (int boundId in flattenResult.HostFaceBoundsToRemove.Values.SelectMany(boundIds => boundIds))
                    inactiveDefinitionRoots.Add(boundId);

                removedEmbeddedFaces = MeasureCleanerTiming(
                    timings,
                    "edit_remove_faces_from_closed_shells",
                    () => RemoveFacesFromClosedShells(data, flattenResult.FlattenedFaces, edits));
                removedHostLoops = MeasureCleanerTiming(
                    timings,
                    "edit_remove_face_bounds",
                    () => RemoveFaceBounds(data, flattenResult.HostFaceBoundsToRemove, edits));
                recoloredCount = MeasureCleanerTiming(
                    timings,
                    "edit_recolor_flattened_faces",
                    () => RecolorFlattenedFaces(
                        data,
                        flattenResult.FlattenedFaces,
                        flattenResult.ReplacementFaceByRemovedFace,
                        context.FaceOwners,
                        context.SolidInfo,
                        context.StyledItems,
                        context.StyledByTarget,
                        edits));
                var removedFaceStyleTargets = new HashSet<int>(flattenResult.FlattenedFaces);
                foreach (int solidId in detection.RemovableSolidIds)
                    removedFaceStyleTargets.Add(solidId);
                foreach (int faceId in GetSolidFaceIds(context.SolidInfo, detection.RemovableSolidIds))
                    removedFaceStyleTargets.Add(faceId);
                foreach (int styledItemId in MeasureCleanerTiming(
                    timings,
                    "edit_remove_styled_items",
                    () => RemoveStyledItemsForRemovedFaces(data, context.StyledItems, removedFaceStyleTargets, edits)))
                    inactiveDefinitionRoots.Add(styledItemId);
            }

            string cleaned = MeasureCleanerTiming(
                timings,
                "edit_apply_definition_edits",
                () => ApplyCleanupDefinitionEdits(data, edits, inactiveDefinitionRoots, out removedInactiveDefinitions));
            var residualVectorBoundRewrite = new ResidualVectorBoundRewriteResult();
            for (int residualPass = 0; residualPass < 6; residualPass++)
            {
                ResidualVectorBoundRewriteResult residualPassRewrite = MeasureCleanerTiming(
                    timings,
                    "edit_residual_vector_bound_rewrite_pass_" + (residualPass + 1).ToString(CultureInfo.InvariantCulture),
                    () => FindResidualVectorBoundsToRemove(
                        cleaned,
                        options,
                        detection.TemplateTextLogoMarkedRegions,
                        allowBroadSourceRegionSweep: detection.TemplateTextLogoAcceptedRegionCount == 0));
                residualVectorBoundRewrite.DetectionCount += residualPassRewrite.DetectionCount;
                residualVectorBoundRewrite.RemovedFaceCount += residualPassRewrite.RemovedFaceCount;
                residualVectorBoundRewrite.RemovedBoundCount += residualPassRewrite.RemovedBoundCount;
                residualVectorBoundRewrite.BlockedSourceCount += residualPassRewrite.BlockedSourceCount;
                residualVectorBoundRewrite.UnknownPrimitiveCount += residualPassRewrite.UnknownPrimitiveCount;
                foreach (string detail in residualPassRewrite.Diagnostics)
                    residualVectorBoundRewrite.Diagnostics.Add(
                        "pass " +
                        (residualPass + 1).ToString(CultureInfo.InvariantCulture) +
                        ": " +
                        detail);

                if (residualPassRewrite.RemovedFaceCount <= 0 &&
                    residualPassRewrite.RemovedBoundCount <= 0)
                {
                    break;
                }

                foreach (int faceId in residualPassRewrite.FaceIdsToRemove)
                {
                    inactiveDefinitionRoots.Add(faceId);
                    if (flattenResult.FlattenedFaces.Add(faceId))
                        flattenResult.FlattenedFaceCount++;
                    residualVectorBoundRewrite.FaceIdsToRemove.Add(faceId);
                }

                MergeHostFaceBounds(flattenResult.HostFaceBoundsToRemove, residualPassRewrite.FaceBoundsToRemove);
                MergeHostFaceBounds(residualVectorBoundRewrite.FaceBoundsToRemove, residualPassRewrite.FaceBoundsToRemove);
                foreach (int boundId in residualPassRewrite.FaceBoundsToRemove.Values.SelectMany(boundIds => boundIds))
                    inactiveDefinitionRoots.Add(boundId);
                var residualFacesToRemove = new HashSet<int>(residualPassRewrite.FaceIdsToRemove);
                if (residualPassRewrite.FaceBoundsToRemove.Count > 0)
                {
                    var flattenedFacesBeforeResidualLoops = new HashSet<int>(flattenResult.FlattenedFaces);
                    AddHostLoopResidualInteriorFacesToFlattenResult(
                        data,
                        residualPassRewrite.FaceBoundsToRemove,
                        context.FaceOwners,
                        context.SolidInfo,
                        context.StyledByTarget,
                        flattenResult,
                        options);
                    foreach (int faceId in flattenResult.FlattenedFaces)
                    {
                        if (flattenedFacesBeforeResidualLoops.Contains(faceId))
                            continue;

                        residualFacesToRemove.Add(faceId);
                    }
                }

                foreach (int faceId in residualFacesToRemove)
                {
                    inactiveDefinitionRoots.Add(faceId);
                    residualVectorBoundRewrite.FaceIdsToRemove.Add(faceId);
                }

                removedEmbeddedFaces += MeasureCleanerTiming(
                    timings,
                    "edit_remove_residual_vector_faces_from_closed_shells_pass_" + (residualPass + 1).ToString(CultureInfo.InvariantCulture),
                    () => RemoveFacesFromClosedShells(data, residualFacesToRemove, edits));
                removedHostLoops += MeasureCleanerTiming(
                    timings,
                    "edit_remove_residual_vector_face_bounds_pass_" + (residualPass + 1).ToString(CultureInfo.InvariantCulture),
                    () => RemoveFaceBounds(data, residualPassRewrite.FaceBoundsToRemove, edits));
                foreach (int styledItemId in MeasureCleanerTiming(
                    timings,
                    "edit_remove_residual_vector_styled_items_pass_" + (residualPass + 1).ToString(CultureInfo.InvariantCulture),
                    () => RemoveStyledItemsForRemovedFaces(data, context.StyledItems, residualFacesToRemove, edits)))
                    inactiveDefinitionRoots.Add(styledItemId);
                cleaned = MeasureCleanerTiming(
                    timings,
                    "edit_reapply_definition_edits_after_residual_vector_bound_rewrite_pass_" + (residualPass + 1).ToString(CultureInfo.InvariantCulture),
                    () => ApplyCleanupDefinitionEdits(data, edits, inactiveDefinitionRoots, out removedInactiveDefinitions));
            }

            string removedGeometry = options.BuildRemovedGeometryStep
                ? MeasureCleanerTiming(
                    timings,
                    "report_build_removed_geometry_step",
                    () => BuildRemovedGeometryStep(data, context, detection, flattenResult))
                : string.Empty;
            var diagnostics = new List<string>();

            diagnostics.Add("Approach: remove thin neutral watermark solids, then flatten embedded neutral relief faces and merge their host-plane cut loops.");
            diagnostics.Add("Stage 1 detection: pattern-gated automatic detection; marked rectangles are not used.");
            diagnostics.Add($"Clean text enabled: {options.CleanText}");
            diagnostics.Add($"Detected thin watermark solids: {detection.RemovableSolidIds.Count}");
            diagnostics.Add($"Detected embedded watermark faces: {detection.EmbeddedFaceIds.Count}");
            diagnostics.Add($"Detected coplanar watermark faces: {detection.CoplanarFaceIds.Count}");
            diagnostics.Add($"Template text detections: {detection.TemplateTextDetectionCount}");
            diagnostics.Add($"Template text cleanup candidates: {detection.TemplateTextCandidateCount}");
            diagnostics.Add($"Template text faces: {detection.TemplateTextFaceCount}");
            diagnostics.Add($"Template text host rejects: {detection.TemplateTextHostRejectCount}");
            diagnostics.Add($"Template text boundary rejects: {detection.TemplateTextBoundaryRejectCount}");
            diagnostics.Add($"Template text/logo detections: {detection.TemplateTextLogoDetectionCount}");
            diagnostics.Add($"Template text/logo cleanup candidates: {detection.TemplateTextLogoCandidateCount}");
            diagnostics.Add($"Template text/logo cleanup regions: {detection.TemplateTextLogoAcceptedRegionCount}");
            diagnostics.Add($"Template text/logo faces: {detection.TemplateTextLogoFaceCount}");
            diagnostics.Add($"Template text/logo host rejects: {detection.TemplateTextLogoHostRejectCount}");
            diagnostics.Add($"Template text/logo protected rejects: {detection.TemplateTextLogoProtectedRejectCount}");
            foreach (string detail in detection.TemplateTextLogoDiagnostics)
                diagnostics.Add(detail);
            if (detection.TemplateTextLogoFaceCount > 0 || detection.CoplanarFaceIds.Count > 0)
                diagnostics.Add("Coplanar/text-logo face ids: " + string.Join(", ", detection.CoplanarFaceIds.OrderBy(id => id).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))));
            if (detection.HostFaceBoundsToRemove.Count > 0)
                diagnostics.Add("Host bounds to remove: " + string.Join("; ", detection.HostFaceBoundsToRemove.OrderBy(kvp => kvp.Key).Select(kvp =>
                    "#" + kvp.Key.ToString(CultureInfo.InvariantCulture) + " -> " +
                    string.Join(",", kvp.Value.OrderBy(id => id).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))))));
            diagnostics.Add($"Text cleanup candidates: {detection.TextCandidateCount}");
            diagnostics.Add($"Text cleanup clusters: {detection.TextClusterCount}");
            diagnostics.Add($"Detected text faces: {detection.TextFaceIds.Count}");
            diagnostics.Add($"Embedded topology removal enabled: {options.RemoveEmbeddedWatermarkTopology}");
            diagnostics.Add($"Removed embedded watermark faces from shells: {removedEmbeddedFaces}");
            diagnostics.Add($"Removed host-face inner loops: {removedHostLoops}");
            diagnostics.Add($"Residual vector detections checked: {residualVectorBoundRewrite.DetectionCount}");
            diagnostics.Add($"Residual vector contained faces removed: {residualVectorBoundRewrite.RemovedFaceCount}");
            diagnostics.Add($"Residual vector retained bounds removed: {residualVectorBoundRewrite.RemovedBoundCount}");
            if (residualVectorBoundRewrite.BlockedSourceCount > 0)
                diagnostics.Add($"Residual vector blocked topology sources: {residualVectorBoundRewrite.BlockedSourceCount}");
            if (residualVectorBoundRewrite.UnknownPrimitiveCount > 0)
                diagnostics.Add($"Residual vector unknown primitive sources: {residualVectorBoundRewrite.UnknownPrimitiveCount}");
            foreach (string detail in residualVectorBoundRewrite.Diagnostics)
                diagnostics.Add(detail);
            diagnostics.Add($"Removed inactive cleanup definitions: {removedInactiveDefinitions}");
            diagnostics.Add($"Automatic removable-solid host-face loops: {detection.RemovableSolidHostLoopCount}");
            diagnostics.Add($"Automatic removable-solid host-face candidates: {detection.RemovableSolidHostLoopCandidateCount}");
            diagnostics.Add($"Automatic embedded host-face loops: {detection.EmbeddedHostLoopCount}");
            diagnostics.Add($"Automatic host-face loop candidates: {detection.HostLoopCandidateCount}");
            diagnostics.Add($"Automatic host-face watermark loops: {detection.HostLoopCount}");
            diagnostics.Add($"Automatic cleanup volumes: {cleanupVolumes.Count}");
            if (cleanupVolumes.Count > 0)
            {
                diagnostics.Add("Automatic cleanup volume detail: " + string.Join("; ", cleanupVolumes.Select(volume =>
                    "owner=#" + volume.OwnerId.ToString(CultureInfo.InvariantCulture) +
                    " host=#" + volume.HostFaceId.ToString(CultureInfo.InvariantCulture) +
                    " axis=" + volume.Axis.ToString(CultureInfo.InvariantCulture) +
                    " coord=" + volume.HostCoordinate.ToString("G6", CultureInfo.InvariantCulture) +
                    " min=" + volume.MinCoordinate.ToString("G6", CultureInfo.InvariantCulture) +
                    " max=" + volume.MaxCoordinate.ToString("G6", CultureInfo.InvariantCulture) +
                    " bounds=[" + volume.Bounds.Min.X.ToString("G6", CultureInfo.InvariantCulture) + "," + volume.Bounds.Min.Y.ToString("G6", CultureInfo.InvariantCulture) + "," + volume.Bounds.Min.Z.ToString("G6", CultureInfo.InvariantCulture) +
                    " -> " + volume.Bounds.Max.X.ToString("G6", CultureInfo.InvariantCulture) + "," + volume.Bounds.Max.Y.ToString("G6", CultureInfo.InvariantCulture) + "," + volume.Bounds.Max.Z.ToString("G6", CultureInfo.InvariantCulture) + "]")));
            }
            diagnostics.Add($"Edited geometry outside cleanup volumes: {flattenResult.EditedOutsideCleanupVolumeCount}");
            diagnostics.Add($"Recolored removed watermark face styles: {recoloredCount}");
            if (detection.RemovableSolidIds.Count > 0)
                diagnostics.Add("Removed solid ids: " + string.Join(", ", detection.RemovableSolidIds.OrderBy(id => id).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))));

            foreach (var operation in flattenResult.Operations)
            {
                diagnostics.Add(
                    $"Flattened {operation.FaceCount} faces on solid #{operation.SolidId} along {AxisName(operation.Axis)} to {operation.TargetCoordinate.ToString("G17", CultureInfo.InvariantCulture)} using host face #{operation.HostFaceId?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
            }

            foreach (var info in context.SolidInfo.Values.OrderBy(s => s.SolidId))
            {
                string body = info.ReplacementColor.HasValue
                    ? info.ReplacementColor.Value.ToString()
                    : "none";
                diagnostics.Add($"Solid #{info.SolidId}: faces={info.FaceIds.Count}, replacementStyle=#{info.ReplacementStyleId?.ToString(CultureInfo.InvariantCulture) ?? "none"}, replacementColor={body}");
            }

            return new StepWatermarkCleanerReport
            {
                CleanedStep = cleaned,
                RemovedGeometryStep = removedGeometry,
                SolidCount = context.SolidIds.Count,
                StyledFaceCount = context.StyledFaceCount,
                CandidateFaceCount = detection.EmbeddedFaceIds.Count + detection.CoplanarFaceIds.Count + detection.HostLoopCount,
                RecoloredFaceCount = recoloredCount,
                RemovedSolidCount = detection.RemovableSolidIds.Count,
                FlattenedFaceCount = flattenResult.FlattenedFaceCount,
                FlattenedPointCount = flattenResult.FlattenedPointCount,
                DetectionReport = MeasureCleanerTiming(
                    timings,
                    "report_build_public_detection_report",
                    () => BuildPublicDetectionReport(context, detection)),
                Diagnostics = diagnostics,
                Timings = timings.ToList()
            };
        }

        private static CleanupContext BuildCleanupContext(
            string stepText,
            StepWatermarkCleanerOptions options,
            List<StepWatermarkCleanerTiming> timings)
        {
            var data = MeasureCleanerTiming(timings, "context_parse_step_entities", () => StepData.Parse(stepText));
            MeasureCleanerTiming(timings, "context_build_indexes", () => data.BuildIndexes());

            var solidIds = MeasureCleanerTiming(
                timings,
                "context_collect_cleanup_solids",
                () => data.Entities.Values
                    .Where(e => IsCleanupOwnerRootType(e.Type))
                    .Select(e => e.Id)
                    .ToList());

            var styledItems = MeasureCleanerTiming(timings, "context_build_styled_items", () => BuildStyledItems(data));
            var styledByTarget = MeasureCleanerTiming(
                timings,
                "context_group_styles_by_target",
                () => styledItems
                    .GroupBy(s => s.TargetId)
                    .ToDictionary(g => g.Key, g => g.ToList()));
            var faceOwners = MeasureCleanerTiming(timings, "context_build_face_owner_map", () => BuildFaceOwnerMap(data, solidIds));
            var solidInfo = MeasureCleanerTiming(timings, "context_build_solid_info", () => BuildSolidInfo(data, solidIds, faceOwners, styledByTarget, options));
            int styledFaceCount = MeasureCleanerTiming(timings, "context_count_styled_faces", () => styledItems.Count(s => data.GetTypeName(s.TargetId) == "ADVANCED_FACE"));

            return new CleanupContext
            {
                Data = data,
                Options = options,
                SolidIds = solidIds,
                StyledItems = styledItems,
                StyledByTarget = styledByTarget,
                FaceOwners = faceOwners,
                SolidInfo = solidInfo,
                StyledFaceCount = styledFaceCount
            };
        }

        private static AutomaticWatermarkDetection DetectAutomaticWatermarks(
            CleanupContext context,
            List<StepWatermarkCleanerTiming> timings = null)
        {
            var detection = new AutomaticWatermarkDetection();
            var data = context.Data;
            var options = context.Options;

            Bounds? modelBounds = MeasureCleanerTiming(timings, "detect_get_model_bounds", () => GetModelBounds(context));
            List<StepWatermarkMarkedRegion> vectorTextLogoRegions = modelBounds.HasValue
                ? MeasureCleanerTiming(
                    timings,
                    "detect_vector_text_logo_regions",
                    () => DetectTemplateTextLogoRegions(
                        context.Data.Text,
                        modelBounds.Value,
                        textOnly: false,
                        requireHighConfidence: true))
                : new List<StepWatermarkMarkedRegion>();
            bool hasVectorTextLogoRegions = vectorTextLogoRegions.Any(HasMarkedRegionArea);

            var removableSolidCandidateIds = hasVectorTextLogoRegions
                ? new HashSet<int>()
                : MeasureCleanerTiming(
                    timings,
                    "detect_removable_watermark_solids",
                    () => FindRemovableWatermarkSolids(
                        data,
                        context.SolidInfo,
                        context.StyledByTarget,
                        modelBounds,
                        options));
            detection.SolidRegionCandidateIds = removableSolidCandidateIds;

            var removableSolidHostLoops = hasVectorTextLogoRegions
                ? new AutomaticWatermarkLoopResult()
                : MeasureCleanerTiming(
                    timings,
                    "detect_removable_solid_host_loops",
                    () => FindRemovableSolidHostLoops(
                        data,
                        removableSolidCandidateIds,
                        context.SolidInfo,
                        context.StyledByTarget,
                        modelBounds,
                        options));
            detection.AutomaticRegions.AddRange(removableSolidHostLoops.Regions);
            MergeHostFaceBounds(detection.HostFaceBoundsToRemove, removableSolidHostLoops.HostFaceBoundsToRemove);
            detection.RemovableSolidHostLoopCount = CountHostLoopBounds(removableSolidHostLoops.HostFaceBoundsToRemove);
            detection.RemovableSolidHostLoopCandidateCount = removableSolidHostLoops.CandidateCount;

            detection.EmbeddedFaceIds = hasVectorTextLogoRegions
                ? new List<int>()
                : MeasureCleanerTiming(
                    timings,
                    "detect_embedded_watermark_faces",
                    () => FindEmbeddedWatermarkFaces(
                        data,
                        context.StyledItems,
                        context.FaceOwners,
                        context.SolidInfo,
                        detection.RemovableSolidIds,
                        context.StyledByTarget,
                        options));

            var embeddedHostLoops = hasVectorTextLogoRegions
                ? new AutomaticWatermarkLoopResult()
                : MeasureCleanerTiming(
                    timings,
                    "detect_embedded_host_loops",
                    () => FindAutomaticEmbeddedHostLoops(
                        data,
                        detection.EmbeddedFaceIds,
                        context.FaceOwners,
                        context.SolidInfo,
                        context.StyledByTarget,
                        options));
            detection.EmbeddedHostLoopCount = CountHostLoopBounds(embeddedHostLoops.HostFaceBoundsToRemove);
            detection.AutomaticRegions.AddRange(embeddedHostLoops.Regions);
            MergeHostFaceBounds(detection.HostFaceBoundsToRemove, embeddedHostLoops.HostFaceBoundsToRemove);

            var automaticHostLoops = hasVectorTextLogoRegions
                ? new AutomaticWatermarkLoopResult()
                : MeasureCleanerTiming(
                    timings,
                    "detect_automatic_watermark_host_loops",
                    () => FindAutomaticWatermarkHostLoops(
                        data,
                        context.SolidInfo,
                        context.StyledByTarget,
                        options));
            detection.HostLoopCandidateCount = automaticHostLoops.CandidateCount;
            detection.AutomaticRegions.AddRange(automaticHostLoops.Regions);
            MergeHostFaceBounds(detection.HostFaceBoundsToRemove, automaticHostLoops.HostFaceBoundsToRemove);

            detection.CoplanarFaceIds = hasVectorTextLogoRegions
                ? new List<int>()
                : MeasureCleanerTiming(
                    timings,
                    "detect_automatic_region_watermark_faces",
                    () => FindAutomaticRegionWatermarkFaces(
                        data,
                        context.StyledItems,
                        context.FaceOwners,
                        automaticHostLoops.Regions,
                        options))
                    .ToList();
            var neutralCoplanarFaceIds = hasVectorTextLogoRegions
                ? new List<int>()
                : MeasureCleanerTiming(
                    timings,
                    "detect_neutral_coplanar_watermark_faces",
                    () => FindNeutralCoplanarWatermarkFaces(
                        data,
                        context.StyledItems,
                        context.FaceOwners,
                        context.SolidInfo,
                        context.StyledByTarget,
                        options));
            detection.CoplanarFaceIds = detection.CoplanarFaceIds
                .Concat(neutralCoplanarFaceIds)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (modelBounds.HasValue)
            {
                var projectionPromotion = MeasureCleanerTiming(
                    timings,
                    "detect_projection_text_logo_cleanup_regions",
                    () => PromoteTemplateTextLogoCleanupRegions(
                        context,
                        modelBounds.Value,
                        detection.AutomaticRegions,
                        detection.HostFaceBoundsToRemove,
                        detection.EmbeddedFaceIds.Concat(detection.CoplanarFaceIds).ToList(),
                        vectorTextLogoRegions,
                        options));
                detection.TemplateTextLogoDetectionCount = projectionPromotion.DetectionCount;
                detection.TemplateTextLogoCandidateCount = projectionPromotion.CandidateCount;
                detection.TemplateTextLogoAcceptedRegionCount = projectionPromotion.Regions.Count;
                detection.TemplateTextLogoFaceCount = projectionPromotion.FaceIds.Count;
                detection.TemplateTextLogoHostRejectCount = projectionPromotion.HostRejectCount;
                detection.TemplateTextLogoProtectedRejectCount = projectionPromotion.ProtectedRejectCount;
                detection.TemplateTextLogoMarkedRegions = projectionPromotion.MarkedRegions;
                detection.TemplateTextLogoDiagnostics = projectionPromotion.Diagnostics;
                PruneGenericCleanupToTextLogoVisualRegions(
                    context,
                    detection,
                    projectionPromotion.MarkedRegions,
                    options);
                detection.AutomaticRegions.AddRange(projectionPromotion.Regions);
                MergeHostFaceBounds(detection.HostFaceBoundsToRemove, projectionPromotion.HostFaceBoundsToRemove);
                detection.CoplanarFaceIds = detection.CoplanarFaceIds
                    .Concat(projectionPromotion.FaceIds)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();
            }
            if (options.CleanText)
            {
                var templateTextDetection = MeasureCleanerTiming(
                    timings,
                    "detect_template_text_faces",
                    () => FindTemplateTextFaces(
                        context,
                        detection.RemovableSolidIds,
                        detection.EmbeddedFaceIds,
                        detection.CoplanarFaceIds,
                        options));
                detection.TemplateTextDetectionCount = templateTextDetection.DetectionCount;
                detection.TemplateTextCandidateCount = templateTextDetection.CandidateCount;
                detection.TemplateTextFaceCount = templateTextDetection.FaceIds.Count;
                detection.TemplateTextHostRejectCount = templateTextDetection.HostRejectCount;
                detection.TemplateTextBoundaryRejectCount = templateTextDetection.BoundaryRejectCount;
                detection.TextFaceIds = templateTextDetection.FaceIds;
                detection.CoplanarFaceIds = detection.CoplanarFaceIds
                    .Concat(detection.TextFaceIds)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();

                var textDetection = MeasureCleanerTiming(
                    timings,
                    "detect_text_string_faces",
                    () => FindAutomaticTextStringFaces(
                        data,
                        context.StyledItems,
                        context.FaceOwners,
                        context.SolidInfo,
                        context.StyledByTarget,
                        detection.RemovableSolidIds,
                        detection.EmbeddedFaceIds,
                        detection.CoplanarFaceIds,
                        options));
                detection.TextFaceIds = detection.TextFaceIds
                    .Concat(textDetection.FaceIds)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();
                detection.TextCandidateCount = textDetection.CandidateCount;
                detection.TextClusterCount = textDetection.ClusterCount;
                detection.CoplanarFaceIds = detection.CoplanarFaceIds
                    .Concat(detection.TextFaceIds)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();
            }

            MeasureCleanerTiming(
                timings,
                "detect_companion_host_bounds",
                () => AddAutomaticRegionCompanionHostBounds(
                    data,
                    context.SolidInfo,
                    detection,
                    options));

            RemoveProtectedCylindricalHostLoopBounds(
                data,
                context.SolidInfo,
                context.StyledByTarget,
                detection.HostFaceBoundsToRemove,
                options);
            detection.HostLoopCount = CountHostLoopBounds(detection.HostFaceBoundsToRemove);
            detection.RemovableSolidIds = new HashSet<int>();

            return detection;
        }

        private static T MeasureCleanerTiming<T>(
            List<StepWatermarkCleanerTiming> timings,
            string name,
            Func<T> action)
        {
            if (timings == null)
                return action();

            var stopwatch = Stopwatch.StartNew();
            T result = action();
            stopwatch.Stop();
            timings.Add(new StepWatermarkCleanerTiming
            {
                Name = name,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            });
            return result;
        }

        private static void MeasureCleanerTiming(
            List<StepWatermarkCleanerTiming> timings,
            string name,
            Action action)
        {
            if (timings == null)
            {
                action();
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            timings.Add(new StepWatermarkCleanerTiming
            {
                Name = name,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            });
        }

        private static StepWatermarkDetectionReport BuildPublicDetectionReport(
            CleanupContext context,
            AutomaticWatermarkDetection detection)
        {
            var hostLoops = new List<StepWatermarkHostLoopDetection>();
            var regions = new List<StepWatermarkRegionDetection>();
            var seenRegions = new HashSet<string>(StringComparer.Ordinal);
            Bounds? modelBounds = GetModelBounds(context);

            foreach (int solidId in detection.RemovableSolidIds.OrderBy(id => id))
                AddSolidDetectionRegion(context, regions, seenRegions, solidId, "solid", modelBounds);

            foreach (int solidId in detection.SolidRegionCandidateIds
                .Where(id => !detection.RemovableSolidIds.Contains(id))
                .OrderBy(id => id))
                AddSolidDetectionRegion(context, regions, seenRegions, solidId, "solid-candidate", modelBounds);

            foreach (int faceId in detection.EmbeddedFaceIds.OrderBy(id => id))
                AddFaceDetectionRegion(context, regions, seenRegions, faceId, "face", modelBounds);

            foreach (int faceId in detection.CoplanarFaceIds.OrderBy(id => id))
                AddFaceDetectionRegion(context, regions, seenRegions, faceId, "face", modelBounds);

            foreach (var kvp in detection.HostFaceBoundsToRemove.OrderBy(kvp => kvp.Key))
            {
                var hostBounds = context.Data.GetBounds(kvp.Key);
                int axis = hostBounds.HasValue
                    ? FindPlanarAxis(hostBounds.Value, context.Options)
                    : -1;
                string axisName = axis >= 0 ? AxisName(axis) : "?";
                foreach (int boundId in kvp.Value.OrderBy(id => id))
                {
                    hostLoops.Add(new StepWatermarkHostLoopDetection
                    {
                        HostFaceId = kvp.Key,
                        BoundId = boundId,
                        ProjectionAxis = axisName
                    });

                    AddHostLoopDetectionRegion(context, regions, seenRegions, kvp.Key, boundId, axis, modelBounds);
                }
            }

            foreach (StepWatermarkMarkedRegion region in detection.TemplateTextLogoMarkedRegions)
                AddMarkedDetectionRegion(regions, seenRegions, region, "visual");

            var diagnostics = new List<string>
            {
                "Stage 1 detection only: pattern-gated automatic detection; marked rectangles are ignored.",
                "Detected thin watermark solids: " + detection.RemovableSolidIds.Count.ToString(CultureInfo.InvariantCulture),
                "Detected embedded watermark faces: " + detection.EmbeddedFaceIds.Count.ToString(CultureInfo.InvariantCulture),
                "Detected coplanar watermark faces: " + detection.CoplanarFaceIds.Count.ToString(CultureInfo.InvariantCulture),
                "Detected host-face watermark loops: " + detection.HostLoopCount.ToString(CultureInfo.InvariantCulture),
                "Template text/logo detections: " + detection.TemplateTextLogoDetectionCount.ToString(CultureInfo.InvariantCulture),
                "Template text/logo cleanup candidates: " + detection.TemplateTextLogoCandidateCount.ToString(CultureInfo.InvariantCulture),
                "Template text/logo cleanup regions: " + detection.TemplateTextLogoAcceptedRegionCount.ToString(CultureInfo.InvariantCulture)
            };

            return new StepWatermarkDetectionReport
            {
                SolidCount = context.SolidIds.Count,
                StyledFaceCount = context.StyledFaceCount,
                RemovableSolidCount = detection.RemovableSolidIds.Count,
                EmbeddedFaceCount = detection.EmbeddedFaceIds.Count,
                CoplanarFaceCount = detection.CoplanarFaceIds.Count,
                HostLoopCandidateCount = detection.HostLoopCandidateCount,
                HostLoopCount = detection.HostLoopCount,
                RemovableSolidIds = detection.RemovableSolidIds.OrderBy(id => id).ToList(),
                EmbeddedFaceIds = detection.EmbeddedFaceIds.OrderBy(id => id).ToList(),
                CoplanarFaceIds = detection.CoplanarFaceIds.OrderBy(id => id).ToList(),
                HostLoops = hostLoops,
                Regions = regions,
                Diagnostics = diagnostics
            };
        }

        private static void AddSolidDetectionRegion(
            CleanupContext context,
            List<StepWatermarkRegionDetection> regions,
            HashSet<string> seenRegions,
            int solidId,
            string kind,
            Bounds? modelBounds)
        {
            var bounds = context.Data.GetBounds(solidId);
            if (!bounds.HasValue || !modelBounds.HasValue)
                return;

            AddDetectionRegion(
                regions,
                seenRegions,
                solidId,
                kind,
                GetBoundsPrimaryViewName(bounds.Value, modelBounds.Value));
        }

        private static void AddFaceDetectionRegion(
            CleanupContext context,
            List<StepWatermarkRegionDetection> regions,
            HashSet<string> seenRegions,
            int faceId,
            string kind,
            Bounds? modelBounds)
        {
            if (!context.FaceOwners.TryGetValue(faceId, out int ownerId))
                return;

            if (!context.SolidInfo.TryGetValue(ownerId, out var ownerInfo))
                return;

            var faceBounds = context.Data.GetBounds(faceId);
            if (!faceBounds.HasValue)
                return;

            var singleFace = new HashSet<int> { faceId };
            bool allowLightHost = ComponentHasDarkWatermarkFace(singleFace, context.StyledByTarget, context.Options);
            var host = ChooseHostPlane(
                context.Data,
                ownerInfo,
                singleFace,
                faceBounds.Value,
                context.StyledByTarget,
                allowLightHost,
                context.Options);

            string viewName;
            int planarAxis = kind == "coplanar"
                ? FindPlanarAxis(faceBounds.Value, context.Options)
                : -1;
            if (planarAxis >= 0)
            {
                Bounds referenceBounds = ownerInfo.Bounds ?? modelBounds ?? faceBounds.Value;
                double coordinate = (faceBounds.Value.Min.Get(planarAxis) + faceBounds.Value.Max.Get(planarAxis)) / 2.0;
                viewName = GetAxisCoordinateViewName(planarAxis, coordinate, referenceBounds);
            }
            else if (host != null)
            {
                Bounds referenceBounds = ownerInfo.Bounds ?? modelBounds ?? faceBounds.Value;
                viewName = GetAxisCoordinateViewName(host.Axis, host.TargetCoordinate, referenceBounds);
            }
            else if (modelBounds.HasValue)
            {
                viewName = GetBoundsPrimaryViewName(faceBounds.Value, modelBounds.Value);
            }
            else
            {
                viewName = "z_plus";
            }

            AddDetectionRegion(regions, seenRegions, faceId, kind, viewName);
        }

        private static void AddHostLoopDetectionRegion(
            CleanupContext context,
            List<StepWatermarkRegionDetection> regions,
            HashSet<string> seenRegions,
            int hostFaceId,
            int boundId,
            int axis,
            Bounds? modelBounds)
        {
            var hostBounds = context.Data.GetBounds(hostFaceId);
            var boundBounds = context.Data.GetBounds(boundId);
            if (!boundBounds.HasValue)
                return;

            if (axis < 0)
                axis = FindPlanarAxis(boundBounds.Value, context.Options);

            string viewName;
            if (axis >= 0 && hostBounds.HasValue)
            {
                double coordinate = (hostBounds.Value.Min.Get(axis) + hostBounds.Value.Max.Get(axis)) / 2.0;
                viewName = GetAxisCoordinateViewName(axis, coordinate, modelBounds ?? hostBounds.Value);
            }
            else if (modelBounds.HasValue)
            {
                viewName = GetBoundsPrimaryViewName(boundBounds.Value, modelBounds.Value);
            }
            else
            {
                viewName = "z_plus";
            }

            AddDetectionRegion(regions, seenRegions, boundId, "loop", viewName);
        }

        private static void AddDetectionRegion(
            List<StepWatermarkRegionDetection> regions,
            HashSet<string> seenRegions,
            int entityId,
            string kind,
            string viewName)
        {
            string key = kind + "|" +
                entityId.ToString(CultureInfo.InvariantCulture) + "|" +
                (viewName ?? string.Empty);
            if (!seenRegions.Add(key))
                return;

            regions.Add(new StepWatermarkRegionDetection
            {
                EntityId = entityId,
                Kind = kind,
                ViewName = viewName
            });
        }

        private static void AddMarkedDetectionRegion(
            List<StepWatermarkRegionDetection> regions,
            HashSet<string> seenRegions,
            StepWatermarkMarkedRegion region,
            string kind)
        {
            if (region == null || !HasMarkedRegionArea(region))
                return;

            string viewName = region.ViewName ?? string.Empty;
            string key = kind + "|" +
                viewName + "|" +
                region.RectangleX.ToString(CultureInfo.InvariantCulture) + "|" +
                region.RectangleY.ToString(CultureInfo.InvariantCulture) + "|" +
                region.RectangleWidth.ToString(CultureInfo.InvariantCulture) + "|" +
                region.RectangleHeight.ToString(CultureInfo.InvariantCulture);
            if (!seenRegions.Add(key))
                return;

            regions.Add(new StepWatermarkRegionDetection
            {
                EntityId = 0,
                Kind = kind,
                ViewName = region.ViewName,
                RectangleX = region.RectangleX,
                RectangleY = region.RectangleY,
                RectangleWidth = region.RectangleWidth,
                RectangleHeight = region.RectangleHeight,
                ImageWidth = region.ImageWidth,
                ImageHeight = region.ImageHeight,
                TemplateName = region.TemplateName,
                Text = region.TemplateName,
                Score = region.Score,
                ChamferDistance = region.ChamferDistance,
                EdgePixelCount = region.EdgePixelCount
            });
        }

        private static Bounds? GetModelBounds(CleanupContext context)
        {
            Bounds bounds = new Bounds();
            bool hasBounds = false;
            foreach (var info in context.SolidInfo.Values)
            {
                if (!info.Bounds.HasValue)
                    continue;

                bounds.Include(info.Bounds.Value);
                hasBounds = true;
            }

            return hasBounds ? bounds : (Bounds?)null;
        }

        private static string GetBoundsPrimaryViewName(Bounds bounds, Bounds modelBounds)
        {
            Vec3d size = bounds.Size;
            int axis = 0;
            double best = Math.Abs(size.X);
            if (Math.Abs(size.Y) < best)
            {
                axis = 1;
                best = Math.Abs(size.Y);
            }

            if (Math.Abs(size.Z) < best)
                axis = 2;

            double coordinate = (bounds.Min.Get(axis) + bounds.Max.Get(axis)) / 2.0;
            return GetAxisCoordinateViewName(axis, coordinate, modelBounds);
        }

        private static string GetAxisCoordinateViewName(int axis, double coordinate, Bounds referenceBounds)
        {
            double center = (referenceBounds.Min.Get(axis) + referenceBounds.Max.Get(axis)) / 2.0;
            bool positive = coordinate >= center;
            switch (axis)
            {
                case 0: return positive ? "x_plus" : "x_minus";
                case 1: return positive ? "y_plus" : "y_minus";
                case 2: return positive ? "z_plus" : "z_minus";
                default: return "z_plus";
            }
        }

        private static int MergeHostFaceBounds(FlattenResult result, Dictionary<int, HashSet<int>> hostFaceBoundsToAdd)
        {
            int addedCount = 0;
            foreach (var kvp in hostFaceBoundsToAdd)
            {
                if (!result.HostFaceBoundsToRemove.TryGetValue(kvp.Key, out var boundIds))
                {
                    boundIds = new HashSet<int>();
                    result.HostFaceBoundsToRemove.Add(kvp.Key, boundIds);
                }

                foreach (int boundId in kvp.Value)
                {
                    if (boundIds.Add(boundId))
                        addedCount++;
                }
            }

            return addedCount;
        }

        private static int MergeHostFaceBounds(
            Dictionary<int, HashSet<int>> target,
            Dictionary<int, HashSet<int>> source)
        {
            int addedCount = 0;
            foreach (var kvp in source)
            {
                if (!target.TryGetValue(kvp.Key, out var boundIds))
                {
                    boundIds = new HashSet<int>();
                    target.Add(kvp.Key, boundIds);
                }

                foreach (int boundId in kvp.Value)
                {
                    if (boundIds.Add(boundId))
                        addedCount++;
                }
            }

            return addedCount;
        }

        private static int CountHostLoopBounds(Dictionary<int, HashSet<int>> hostFaceBounds)
        {
            return hostFaceBounds.Sum(kvp => kvp.Value.Count);
        }

        private static StepWatermarkCleanerReport CleanWithMarkedRegions(
            string stepText,
            StepData data,
            StepWatermarkCleanerOptions options,
            List<int> solidIds,
            List<StyledItemInfo> styledItems,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            int styledFaceCount)
        {
            var markedRegions = options.MarkedRegions
                .Where(HasMarkedRegionArea)
                .ToList();

            var diagnostics = new List<string>();
            var edits = new Dictionary<int, string>();

            diagnostics.Add("Approach: projection-guided cleanup; only geometry inside marked rectangle sidecars may be selected.");
            diagnostics.Add("Marked regions: " + markedRegions.Count.ToString(CultureInfo.InvariantCulture));

            if (markedRegions.Count == 0)
            {
                diagnostics.Add("No marked rectangles were provided, so no geometry was changed.");
                return new StepWatermarkCleanerReport
                {
                    CleanedStep = stepText,
                    SolidCount = solidIds.Count,
                    StyledFaceCount = styledFaceCount,
                    CandidateFaceCount = 0,
                    RecoloredFaceCount = 0,
                    RemovedSolidCount = 0,
                    FlattenedFaceCount = 0,
                    FlattenedPointCount = 0,
                    Diagnostics = diagnostics
                };
            }

            var removableSolids = FindMarkedRemovableWatermarkSolids(data, solidInfo, styledByTarget, markedRegions, options);
            var inactiveDefinitionRoots = new HashSet<int>(removableSolids);
            RemoveSolidsFromShapeRepresentations(data, removableSolids, edits);

            var embeddedFaces = FindMarkedEmbeddedWatermarkFaces(
                data,
                styledItems,
                faceOwners,
                solidInfo,
                removableSolids,
                markedRegions,
                options);

            var flattenResult = FlattenEmbeddedWatermarkFaces(
                data,
                embeddedFaces,
                faceOwners,
                solidInfo,
                styledByTarget,
                options,
                edits);

            int markedHostLoopCount = AddMarkedHostLoopCleanup(
                data,
                markedRegions,
                faceOwners,
                solidInfo,
                flattenResult,
                options);

            int removedEmbeddedFaces = 0;
            int removedHostLoops = 0;
            int removedInactiveDefinitions = 0;
            if (options.RemoveEmbeddedWatermarkTopology)
            {
                foreach (int faceId in flattenResult.FlattenedFaces)
                    inactiveDefinitionRoots.Add(faceId);
                foreach (int boundId in flattenResult.HostFaceBoundsToRemove.Values.SelectMany(boundIds => boundIds))
                    inactiveDefinitionRoots.Add(boundId);

                removedEmbeddedFaces = RemoveFacesFromClosedShells(data, flattenResult.FlattenedFaces, edits);
                removedHostLoops = RemoveFaceBounds(data, flattenResult.HostFaceBoundsToRemove, edits);
                var removedFaceStyleTargets = new HashSet<int>(flattenResult.FlattenedFaces);
                foreach (int solidId in removableSolids)
                    removedFaceStyleTargets.Add(solidId);
                foreach (int faceId in GetSolidFaceIds(solidInfo, removableSolids))
                    removedFaceStyleTargets.Add(faceId);
                foreach (int styledItemId in RemoveStyledItemsForRemovedFaces(data, styledItems, removedFaceStyleTargets, edits))
                    inactiveDefinitionRoots.Add(styledItemId);
            }

            int recoloredCount = 0;

            string cleaned = ApplyCleanupDefinitionEdits(data, edits, inactiveDefinitionRoots, out removedInactiveDefinitions);

            diagnostics.Add("Removed marked thin watermark solids: " + removableSolids.Count.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Marked styled watermark faces: " + embeddedFaces.Count.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Marked host loops selected: " + markedHostLoopCount.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Removed embedded watermark faces from shells: " + removedEmbeddedFaces.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Removed host-face inner loops: " + removedHostLoops.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Removed inactive cleanup definitions: " + removedInactiveDefinitions.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Preserved original face colors: no marked-mode STYLED_ITEM recolor edits were applied.");
            if (removableSolids.Count > 0)
                diagnostics.Add("Removed solid ids: " + string.Join(", ", removableSolids.OrderBy(id => id).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))));

            foreach (var operation in flattenResult.Operations)
            {
                diagnostics.Add(
                    $"Flattened {operation.FaceCount} faces on solid #{operation.SolidId} along {AxisName(operation.Axis)} to {operation.TargetCoordinate.ToString("G17", CultureInfo.InvariantCulture)} using host face #{operation.HostFaceId?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
            }

            return new StepWatermarkCleanerReport
            {
                CleanedStep = cleaned,
                SolidCount = solidIds.Count,
                StyledFaceCount = styledFaceCount,
                CandidateFaceCount = embeddedFaces.Count + markedHostLoopCount,
                RecoloredFaceCount = recoloredCount,
                RemovedSolidCount = removableSolids.Count,
                FlattenedFaceCount = flattenResult.FlattenedFaceCount,
                FlattenedPointCount = flattenResult.FlattenedPointCount,
                Diagnostics = diagnostics
            };
        }

        private static HashSet<int> FindMarkedRemovableWatermarkSolids(
            StepData data,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            List<StepWatermarkMarkedRegion> markedRegions,
            StepWatermarkCleanerOptions options)
        {
            var result = new HashSet<int>();

            foreach (var info in solidInfo.Values)
            {
                if (!info.Bounds.HasValue)
                    continue;

                if (!BoundsOverlapsMarkedRegions(info.Bounds.Value, markedRegions, options.MarkedCandidateMinOverlap, options))
                    continue;

                var size = info.Bounds.Value.Size;
                double minSize = Math.Min(size.X, Math.Min(size.Y, size.Z));
                double maxSize = Math.Max(size.X, Math.Max(size.Y, size.Z));
                if (minSize > options.ThinSolidMaxThickness || maxSize > options.ThinSolidMaxSize)
                    continue;

                int styledFaceCount = 0;
                int watermarkFaceCount = 0;

                foreach (int faceId in info.FaceIds)
                {
                    if (!styledByTarget.TryGetValue(faceId, out var faceStyles))
                        continue;

                    foreach (var faceStyle in faceStyles)
                    {
                        if (!faceStyle.Color.HasValue)
                            continue;

                        styledFaceCount++;
                        if (IsStandaloneWatermarkColor(faceStyle.Color.Value, options))
                            watermarkFaceCount++;
                    }
                }

                if (styledFaceCount == 0)
                    continue;

                if (watermarkFaceCount >= Math.Max(3, styledFaceCount * 3 / 4))
                    result.Add(info.SolidId);
            }

            return result;
        }

        private static List<int> FindMarkedEmbeddedWatermarkFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            HashSet<int> removableSolids,
            List<StepWatermarkMarkedRegion> markedRegions,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<int>();
            var seenFaces = new HashSet<int>();

            foreach (var styledItem in styledItems)
            {
                if (data.GetTypeName(styledItem.TargetId) != "ADVANCED_FACE")
                    continue;

                if (!styledItem.Color.HasValue || !IsAutomaticEmbeddedWatermarkColor(styledItem.Color.Value, options))
                    continue;

                if (!faceOwners.TryGetValue(styledItem.TargetId, out int ownerSolidId))
                    continue;

                if (removableSolids.Contains(ownerSolidId))
                    continue;

                if (!solidInfo.TryGetValue(ownerSolidId, out var ownerInfo))
                    continue;

                bool allowLightHost = IsDarkWatermarkColor(styledItem.Color.Value, options);
                if (!allowLightHost &&
                    (!ownerInfo.ReplacementStyleId.HasValue || !ownerInfo.ReplacementColor.HasValue))
                    continue;

                if (!allowLightHost &&
                    options.RequireDarkOwner &&
                    ownerInfo.ReplacementColor.Value.Luminance > options.NeutralBodyMaxLuminance)
                    continue;

                var faceBounds = data.GetBounds(styledItem.TargetId);
                if (!faceBounds.HasValue || !ownerInfo.Bounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                if (!BoundsOverlapsMarkedRegions(faceBounds.Value, markedRegions, options.MarkedCandidateMinOverlap, options))
                    continue;

                if (seenFaces.Add(styledItem.TargetId))
                    result.Add(styledItem.TargetId);
            }

            return result;
        }

        private static int AddMarkedHostLoopCleanup(
            StepData data,
            List<StepWatermarkMarkedRegion> markedRegions,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            FlattenResult result,
            StepWatermarkCleanerOptions options)
        {
            int selectedLoopCount = 0;

            foreach (var ownerInfo in solidInfo.Values)
            {
                if (!ownerInfo.Bounds.HasValue)
                    continue;

                foreach (int faceId in ownerInfo.FaceIds)
                {
                    var faceBounds = data.GetBounds(faceId);
                    if (!faceBounds.HasValue)
                        continue;

                    var markedBoundIds = new HashSet<int>();
                    StepWatermarkMarkedRegion bestRegion = null;
                    foreach (int boundId in data.GetInnerFaceBounds(faceId))
                    {
                        var boundBounds = data.GetBounds(boundId);
                        if (!boundBounds.HasValue)
                            continue;

                        if (!LooksLikeSmallMark(boundBounds.Value, faceBounds.Value, options))
                            continue;

                        if (!TryFindMarkedRegion(boundBounds.Value, markedRegions, options.MarkedLoopMinOverlap, options, out StepWatermarkMarkedRegion matchingRegion))
                            continue;

                        bestRegion = matchingRegion;
                        markedBoundIds.Add(boundId);
                    }

                    if (markedBoundIds.Count == 0)
                        continue;

                    int hostAxis = FindPlanarAxis(faceBounds.Value, options);
                    if (hostAxis < 0 && bestRegion != null)
                        hostAxis = bestRegion.DepthAxis;

                    if (hostAxis < 0)
                        continue;

                    double targetCoordinate = (faceBounds.Value.Min.Get(hostAxis) + faceBounds.Value.Max.Get(hostAxis)) / 2.0;
                    var host = new HostPlaneMatch
                    {
                        Axis = hostAxis,
                        TargetCoordinate = targetCoordinate,
                        HostFaceId = faceId,
                        Score = 1.0
                    };

                    foreach (int boundId in markedBoundIds)
                    {
                        if (!result.HostFaceBoundsToRemove.TryGetValue(faceId, out var boundIds))
                        {
                            boundIds = new HashSet<int>();
                            result.HostFaceBoundsToRemove.Add(faceId, boundIds);
                        }

                        if (boundIds.Add(boundId))
                            selectedLoopCount++;
                    }

                    foreach (int adjacentFaceId in FindHostLoopAdjacentFaces(data, ownerInfo, host, markedBoundIds, options))
                    {
                        if (adjacentFaceId == faceId)
                            continue;

                        if (result.FlattenedFaces.Add(adjacentFaceId))
                            result.FlattenedFaceCount++;

                        result.ReplacementFaceByRemovedFace[adjacentFaceId] = faceId;
                    }
                }
            }

            return selectedLoopCount;
        }

        private static void AppendMarkedRegions(List<StepWatermarkMarkedRegion> result, string markerPath, string projectionPath)
        {
            using (JsonDocument markerDocument = JsonDocument.Parse(File.ReadAllText(markerPath)))
            using (JsonDocument projectionDocument = JsonDocument.Parse(File.ReadAllText(projectionPath)))
            {
                JsonElement markerRoot = markerDocument.RootElement;
                JsonElement projectionRoot = projectionDocument.RootElement;
                if (!markerRoot.TryGetProperty("Rectangles", out JsonElement rectangles) ||
                    rectangles.ValueKind != JsonValueKind.Array)
                    return;

                JsonElement image = RequireObject(projectionRoot, "image", projectionPath);
                JsonElement axes = RequireObject(projectionRoot, "model_axes", projectionPath);
                JsonElement mapping = RequireObject(projectionRoot, "mapping", projectionPath);

                int imageWidth = RequireInt(image, "width", projectionPath);
                int imageHeight = RequireInt(image, "height", projectionPath);
                int padding = RequireInt(image, "padding", projectionPath);
                int uAxis = AxisIndex(RequireString(axes, "u_axis", projectionPath));
                int uSign = RequireInt(axes, "u_sign", projectionPath);
                int vAxis = AxisIndex(RequireString(axes, "v_axis", projectionPath));
                int vSign = RequireInt(axes, "v_sign", projectionPath);
                int depthAxis = AxisIndex(RequireString(axes, "depth_axis", projectionPath));
                int depthSign = RequireInt(axes, "depth_sign", projectionPath);
                string viewName = RequireString(projectionRoot, "view", projectionPath);
                double scale = RequireDouble(mapping, "scale_pixels_per_model_unit", projectionPath);
                double mappingUMin = RequireDouble(mapping, "u_min", projectionPath);
                double mappingVMin = RequireDouble(mapping, "v_min", projectionPath);

                if (scale <= 0.0)
                    return;

                foreach (JsonElement rectangle in rectangles.EnumerateArray())
                {
                    int x = RequireInt(rectangle, "X", markerPath);
                    int y = RequireInt(rectangle, "Y", markerPath);
                    int width = RequireInt(rectangle, "Width", markerPath);
                    int height = RequireInt(rectangle, "Height", markerPath);
                    if (width <= 0 || height <= 0)
                        continue;

                    double u0 = mappingUMin + (x - padding) / scale;
                    double u1 = mappingUMin + (x + width - padding) / scale;
                    double v0 = mappingVMin + (imageHeight - padding - y) / scale;
                    double v1 = mappingVMin + (imageHeight - padding - (y + height)) / scale;

                    result.Add(new StepWatermarkMarkedRegion
                    {
                        ViewName = viewName,
                        SourceMarkerPath = markerPath,
                        SourceProjectionPath = projectionPath,
                        UAxis = uAxis,
                        USign = NormalizeSign(uSign),
                        VAxis = vAxis,
                        VSign = NormalizeSign(vSign),
                        DepthAxis = depthAxis,
                        DepthSign = NormalizeSign(depthSign),
                        ModelUMin = Math.Min(u0, u1),
                        ModelUMax = Math.Max(u0, u1),
                        ModelVMin = Math.Min(v0, v1),
                        ModelVMax = Math.Max(v0, v1),
                        ScalePixelsPerModelUnit = scale,
                        ImageWidth = imageWidth,
                        ImageHeight = imageHeight,
                        RectangleX = x,
                        RectangleY = y,
                        RectangleWidth = width,
                        RectangleHeight = height
                    });
                }
            }
        }

        private static bool HasMarkedRegionArea(StepWatermarkMarkedRegion region)
        {
            return region != null &&
                region.ModelUMax > region.ModelUMin &&
                region.ModelVMax > region.ModelVMin &&
                region.ScalePixelsPerModelUnit > 0.0;
        }

        private static bool BoundsOverlapsMarkedRegions(
            Bounds bounds,
            List<StepWatermarkMarkedRegion> markedRegions,
            double minOverlapRatio,
            StepWatermarkCleanerOptions options)
        {
            return TryFindMarkedRegion(bounds, markedRegions, minOverlapRatio, options, out _);
        }

        private static bool TryFindMarkedRegion(
            Bounds bounds,
            List<StepWatermarkMarkedRegion> markedRegions,
            double minOverlapRatio,
            StepWatermarkCleanerOptions options,
            out StepWatermarkMarkedRegion bestRegion)
        {
            bestRegion = null;
            double bestRatio = 0.0;
            foreach (var region in markedRegions)
            {
                double ratio = MarkedOverlapRatio(bounds, region, options);
                if (ratio <= bestRatio)
                    continue;

                bestRatio = ratio;
                bestRegion = region;
            }

            return bestRatio >= minOverlapRatio;
        }

        private static bool BoundsInsideMarkedRegions(
            Bounds bounds,
            List<StepWatermarkMarkedRegion> markedRegions,
            StepWatermarkCleanerOptions options)
        {
            return TryFindContainingMarkedRegion(bounds, markedRegions, options, out _);
        }

        private static bool TryFindContainingMarkedRegion(
            Bounds bounds,
            List<StepWatermarkMarkedRegion> markedRegions,
            StepWatermarkCleanerOptions options,
            out StepWatermarkMarkedRegion matchingRegion)
        {
            matchingRegion = null;
            foreach (var region in markedRegions)
            {
                if (!BoundsInsideMarkedRegion(bounds, region, options))
                    continue;

                matchingRegion = region;
                return true;
            }

            return false;
        }

        private static bool BoundsInsideMarkedRegion(
            Bounds bounds,
            StepWatermarkMarkedRegion region,
            StepWatermarkCleanerOptions options)
        {
            if (!HasMarkedRegionArea(region))
                return false;

            double padding = region.ScalePixelsPerModelUnit > 0.0
                ? options.MarkedRegionPaddingPixels / region.ScalePixelsPerModelUnit
                : 0.0;

            double u0 = bounds.Min.Get(region.UAxis) * region.USign;
            double u1 = bounds.Max.Get(region.UAxis) * region.USign;
            double v0 = bounds.Min.Get(region.VAxis) * region.VSign;
            double v1 = bounds.Max.Get(region.VAxis) * region.VSign;
            double uMin = Math.Min(u0, u1);
            double uMax = Math.Max(u0, u1);
            double vMin = Math.Min(v0, v1);
            double vMax = Math.Max(v0, v1);

            return uMin >= region.ModelUMin - padding &&
                uMax <= region.ModelUMax + padding &&
                vMin >= region.ModelVMin - padding &&
                vMax <= region.ModelVMax + padding;
        }

        private static double MarkedOverlapRatio(
            Bounds bounds,
            StepWatermarkMarkedRegion region,
            StepWatermarkCleanerOptions options)
        {
            double padding = region.ScalePixelsPerModelUnit > 0.0
                ? options.MarkedRegionPaddingPixels / region.ScalePixelsPerModelUnit
                : 0.0;

            double u0 = bounds.Min.Get(region.UAxis) * region.USign;
            double u1 = bounds.Max.Get(region.UAxis) * region.USign;
            double v0 = bounds.Min.Get(region.VAxis) * region.VSign;
            double v1 = bounds.Max.Get(region.VAxis) * region.VSign;
            double uMin = Math.Min(u0, u1);
            double uMax = Math.Max(u0, u1);
            double vMin = Math.Min(v0, v1);
            double vMax = Math.Max(v0, v1);

            double candidateWidth = Math.Max(uMax - uMin, 0.0);
            double candidateHeight = Math.Max(vMax - vMin, 0.0);
            double centerU = (uMin + uMax) / 2.0;
            double centerV = (vMin + vMax) / 2.0;

            if (candidateWidth <= 0.0000001 || candidateHeight <= 0.0000001)
            {
                return centerU >= region.ModelUMin - padding &&
                    centerU <= region.ModelUMax + padding &&
                    centerV >= region.ModelVMin - padding &&
                    centerV <= region.ModelVMax + padding
                    ? 1.0
                    : 0.0;
            }

            double intersectionUMin = Math.Max(uMin, region.ModelUMin - padding);
            double intersectionUMax = Math.Min(uMax, region.ModelUMax + padding);
            double intersectionVMin = Math.Max(vMin, region.ModelVMin - padding);
            double intersectionVMax = Math.Min(vMax, region.ModelVMax + padding);
            double intersectionWidth = Math.Max(0.0, intersectionUMax - intersectionUMin);
            double intersectionHeight = Math.Max(0.0, intersectionVMax - intersectionVMin);
            double candidateArea = candidateWidth * candidateHeight;
            if (candidateArea <= 0.0000000001)
                return 0.0;

            double overlap = intersectionWidth * intersectionHeight / candidateArea;
            if (overlap > 0.0 &&
                centerU >= region.ModelUMin - padding &&
                centerU <= region.ModelUMax + padding &&
                centerV >= region.ModelVMin - padding &&
                centerV <= region.ModelVMax + padding)
                overlap = Math.Max(overlap, 0.5);

            return overlap;
        }

        private static int FindPlanarAxis(Bounds bounds, StepWatermarkCleanerOptions options)
        {
            int axis = GetSmallestAxis(bounds);
            double best = Math.Abs(bounds.Size.Get(axis));
            return best <= Math.Max(options.PlaneTolerance, 0.000001) ? axis : -1;
        }

        private static int GetSmallestAxis(Bounds bounds)
        {
            Vec3d size = bounds.Size;
            int axis = 0;
            double best = Math.Abs(size.X);
            if (Math.Abs(size.Y) < best)
            {
                axis = 1;
                best = Math.Abs(size.Y);
            }

            if (Math.Abs(size.Z) < best)
            {
                axis = 2;
            }

            return axis;
        }

        private static JsonElement RequireObject(JsonElement element, string propertyName, string path)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Projection metadata is missing object '" + propertyName + "': " + path);

            return value;
        }

        private static string RequireString(JsonElement element, string propertyName, string path)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("JSON is missing string '" + propertyName + "': " + path);

            return value.GetString();
        }

        private static int RequireInt(JsonElement element, string propertyName, string path)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || !value.TryGetInt32(out int result))
                throw new InvalidDataException("JSON is missing integer '" + propertyName + "': " + path);

            return result;
        }

        private static double RequireDouble(JsonElement element, string propertyName, string path)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value) || !value.TryGetDouble(out double result))
                throw new InvalidDataException("JSON is missing number '" + propertyName + "': " + path);

            return result;
        }

        private static int NormalizeSign(int sign)
        {
            return sign < 0 ? -1 : 1;
        }

        private static int AxisIndex(string axisName)
        {
            switch (axisName?.Trim().ToUpperInvariant())
            {
                case "X": return 0;
                case "Y": return 1;
                case "Z": return 2;
                default: throw new InvalidDataException("Unsupported projection axis name: " + axisName);
            }
        }

        private static HashSet<int> FindRemovableWatermarkSolids(
            StepData data,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            Bounds? modelBounds,
            StepWatermarkCleanerOptions options)
        {
            var candidates = new List<WatermarkSolidCandidate>();
            Bounds referenceBounds = modelBounds ?? new Bounds();
            bool hasReferenceBounds = modelBounds.HasValue;

            foreach (var info in solidInfo.Values)
            {
                if (!info.Bounds.HasValue)
                    continue;

                var size = info.Bounds.Value.Size;
                double minSize = Math.Min(size.X, Math.Min(size.Y, size.Z));
                double maxSize = Math.Max(size.X, Math.Max(size.Y, size.Z));
                if (minSize > options.ThinSolidMaxThickness || maxSize > options.ThinSolidMaxSize)
                    continue;

                int axis = GetSmallestAxis(info.Bounds.Value);

                int styledFaceCount = 0;
                int watermarkFaceCount = 0;

                foreach (int faceId in info.FaceIds)
                {
                    if (!styledByTarget.TryGetValue(faceId, out var faceStyles))
                        continue;

                    foreach (var faceStyle in faceStyles)
                    {
                        if (!faceStyle.Color.HasValue)
                            continue;

                        styledFaceCount++;
                        if (IsStandaloneWatermarkColor(faceStyle.Color.Value, options))
                            watermarkFaceCount++;
                    }
                }

                if (styledFaceCount == 0)
                    continue;

                if (watermarkFaceCount < Math.Max(3, styledFaceCount * 3 / 4))
                    continue;

                if (!hasReferenceBounds)
                {
                    referenceBounds.Include(info.Bounds.Value);
                    hasReferenceBounds = true;
                }

                candidates.Add(new WatermarkSolidCandidate
                {
                    SolidId = info.SolidId,
                    Bounds = info.Bounds.Value,
                    PointCount = Math.Max(
                        data.GetPointIds(info.SolidId, includeSurface: true).Count,
                        info.FaceIds.Count * 4),
                    Axis = axis,
                    Coordinate = (info.Bounds.Value.Min.Get(axis) + info.Bounds.Value.Max.Get(axis)) / 2.0
                });
            }

            if (candidates.Count == 0)
                return new HashSet<int>();

            if (!modelBounds.HasValue)
            {
                referenceBounds = new Bounds();
                foreach (var candidate in candidates)
                    referenceBounds.Include(candidate.Bounds);
            }

            if (!options.RequireKnownWatermarkPattern)
                return new HashSet<int>(candidates.Select(candidate => candidate.SolidId));

            return FilterRemovableWatermarkSolidClusters(candidates, referenceBounds, options);
        }

        private static HashSet<int> FilterRemovableWatermarkSolidClusters(
            List<WatermarkSolidCandidate> candidates,
            Bounds referenceBounds,
            StepWatermarkCleanerOptions options)
        {
            var result = new HashSet<int>();
            double planeTolerance = Math.Max(options.ThinSolidMaxThickness * 4.0, options.PlaneTolerance);
            var groups = candidates.GroupBy(candidate => new
            {
                candidate.Axis,
                Coordinate = Math.Round(candidate.Coordinate / planeTolerance)
            });

            foreach (var group in groups)
            {
                var groupCandidates = group.ToList();
                double gap = GetAutomaticClusterGap(referenceBounds, group.Key.Axis, options) * 1.8;
                bool acceptedCluster = false;
                foreach (var cluster in BuildProjectedSolidClusters(groupCandidates, group.Key.Axis, gap))
                {
                    if (!LooksLikeAutomaticWatermarkSolidCluster(cluster, referenceBounds, group.Key.Axis, options))
                        continue;

                    foreach (var candidate in cluster)
                        result.Add(candidate.SolidId);
                    acceptedCluster = true;
                }

                if (!acceptedCluster &&
                    LooksLikeAutomaticWatermarkSolidCluster(groupCandidates, referenceBounds, group.Key.Axis, options))
                {
                    foreach (var candidate in groupCandidates)
                        result.Add(candidate.SolidId);
                }
            }

            return result;
        }

        private static List<List<WatermarkSolidCandidate>> BuildProjectedSolidClusters(
            List<WatermarkSolidCandidate> candidates,
            int axis,
            double gap)
        {
            var result = new List<List<WatermarkSolidCandidate>>();
            var visited = new bool[candidates.Count];

            for (int i = 0; i < candidates.Count; i++)
            {
                if (visited[i])
                    continue;

                var cluster = new List<WatermarkSolidCandidate>();
                var queue = new Queue<int>();
                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    var current = candidates[index];
                    cluster.Add(current);

                    for (int j = 0; j < candidates.Count; j++)
                    {
                        if (visited[j])
                            continue;

                        if (!ProjectedBoundsOverlap(current.Bounds, candidates[j].Bounds, axis, gap))
                            continue;

                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }

                result.Add(cluster);
            }

            return result;
        }

        private static AutomaticWatermarkLoopResult FindRemovableSolidHostLoops(
            StepData data,
            HashSet<int> removableSolidIds,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            Bounds? modelBounds,
            StepWatermarkCleanerOptions options)
        {
            var result = new AutomaticWatermarkLoopResult();
            if (removableSolidIds.Count == 0)
                return result;

            var candidates = new List<WatermarkSolidCandidate>();
            foreach (int solidId in removableSolidIds)
            {
                if (!solidInfo.TryGetValue(solidId, out var info) || !info.Bounds.HasValue)
                    continue;

                for (int axis = 0; axis < 3; axis++)
                {
                    if (info.Bounds.Value.Size.Get(axis) > options.HostPlaneSearchDistance)
                        continue;

                    candidates.Add(new WatermarkSolidCandidate
                    {
                        SolidId = solidId,
                        Bounds = info.Bounds.Value,
                        PointCount = Math.Max(
                            data.GetPointIds(info.SolidId, includeSurface: true).Count,
                            info.FaceIds.Count * 4),
                        Axis = axis,
                        Coordinate = (info.Bounds.Value.Min.Get(axis) + info.Bounds.Value.Max.Get(axis)) / 2.0
                    });
                }
            }

            if (candidates.Count == 0)
                return result;

            Bounds referenceBounds = modelBounds ?? new Bounds();
            if (!modelBounds.HasValue)
            {
                foreach (var candidate in candidates)
                    referenceBounds.Include(candidate.Bounds);
            }

            double planeTolerance = Math.Max(options.ThinSolidMaxThickness * 4.0, options.PlaneTolerance);
            foreach (var group in candidates.GroupBy(candidate => new
            {
                candidate.Axis,
                Coordinate = Math.Round(candidate.Coordinate / planeTolerance)
            }))
            {
                var groupCandidates = group.ToList();
                double gap = GetAutomaticClusterGap(referenceBounds, group.Key.Axis, options) * 1.8;
                bool addedCluster = false;
                foreach (var cluster in BuildProjectedSolidClusters(groupCandidates, group.Key.Axis, gap))
                {
                    int previousCount = result.CandidateCount;
                    AddRemovableSolidHostLoopCluster(
                        data,
                        result,
                        removableSolidIds,
                        solidInfo,
                        styledByTarget,
                        cluster,
                        group.Key.Axis,
                        referenceBounds,
                        options);
                    if (result.CandidateCount > previousCount)
                        addedCluster = true;
                }

                if (!addedCluster)
                {
                    AddRemovableSolidHostLoopCluster(
                        data,
                        result,
                        removableSolidIds,
                        solidInfo,
                        styledByTarget,
                        groupCandidates,
                        group.Key.Axis,
                        referenceBounds,
                        options);
                }
            }

            return result;
        }

        private static void AddRemovableSolidHostLoopCluster(
            StepData data,
            AutomaticWatermarkLoopResult result,
            HashSet<int> removableSolidIds,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            List<WatermarkSolidCandidate> cluster,
            int axis,
            Bounds referenceBounds,
            StepWatermarkCleanerOptions options)
        {
            if (cluster.Count == 0)
                return;

            if (!LooksLikeRemovableSolidHostLoopCluster(cluster, referenceBounds, options))
                return;

            Bounds clusterBounds = new Bounds();
            foreach (var candidate in cluster)
                clusterBounds.Include(candidate.Bounds);

            double clusterCoordinate = (clusterBounds.Min.Get(axis) + clusterBounds.Max.Get(axis)) / 2.0;
            double padding = Math.Max(
                options.HostPlaneProjectionPadding,
                GetAutomaticClusterGap(referenceBounds, axis, options) * 0.75);

            foreach (var ownerInfo in solidInfo.Values)
            {
                if (removableSolidIds.Contains(ownerInfo.SolidId))
                    continue;

                foreach (int hostFaceId in ownerInfo.FaceIds)
                {
                    var hostBounds = data.GetBounds(hostFaceId);
                    if (!hostBounds.HasValue)
                        continue;

                    int hostPlanarAxis = FindPlanarAxis(hostBounds.Value, options);
                    if (hostPlanarAxis >= 0 && hostPlanarAxis != axis)
                        continue;

                    if (hostPlanarAxis < 0 && hostBounds.Value.Size.Get(axis) > options.HostPlaneSearchDistance)
                        continue;

                    if (!LooksLikeRemovableSolidHostFace(hostFaceId, styledByTarget, options))
                        continue;

                    double hostCoordinate = (hostBounds.Value.Min.Get(axis) + hostBounds.Value.Max.Get(axis)) / 2.0;
                    if (Math.Abs(hostCoordinate - clusterCoordinate) > options.HostPlaneSearchDistance)
                        continue;

                    if (!ProjectionIntersects(hostBounds.Value, clusterBounds, axis, padding))
                        continue;

                    var matchedBounds = new HashSet<int>(
                        data.GetMatchingInnerFaceBounds(hostFaceId, clusterBounds, axis, padding));
                    if (matchedBounds.Count == 0)
                        continue;

                    foreach (int expandedBoundId in ExpandHostFaceBounds(data, hostFaceId, matchedBounds, options))
                        matchedBounds.Add(expandedBoundId);

                    if (!result.HostFaceBoundsToRemove.TryGetValue(hostFaceId, out var boundIds))
                    {
                        boundIds = new HashSet<int>();
                        result.HostFaceBoundsToRemove.Add(hostFaceId, boundIds);
                    }

                    bool addedAnyBound = false;
                    foreach (int boundId in matchedBounds)
                    {
                        if (boundIds.Add(boundId))
                        {
                            result.CandidateCount++;
                            addedAnyBound = true;
                        }
                    }

                    if (!addedAnyBound)
                        continue;

                    result.Regions.Add(new AutomaticWatermarkRegion
                    {
                        OwnerId = ownerInfo.SolidId,
                        HostFaceId = hostFaceId,
                        Axis = axis,
                        HostCoordinate = hostCoordinate,
                        Bounds = clusterBounds,
                        HostBounds = hostBounds.Value
                    });
                }
            }
        }

        private static bool LooksLikeAutomaticWatermarkSolidCluster(
            List<WatermarkSolidCandidate> cluster,
            Bounds referenceBounds,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            if (cluster.Count == 0)
                return false;

            Bounds clusterBounds = new Bounds();
            foreach (var candidate in cluster)
                clusterBounds.Include(candidate.Bounds);

            if (!LooksLikeSmallMark(clusterBounds, referenceBounds, options))
                return false;

            if (TouchesProjectedBoundary(clusterBounds, referenceBounds, axis, GetAutomaticEdgeMargin(referenceBounds, axis)))
                return false;

            int pointCount = cluster.Sum(candidate => candidate.PointCount);
            return LooksLikeKnownWatermarkPattern(
                cluster.Select(candidate => candidate.Bounds),
                clusterBounds,
                referenceBounds,
                axis,
                cluster.Count,
                pointCount,
                hasColorCue: true,
                allowStandaloneColorPattern: true);
        }

        private static bool LooksLikeRemovableSolidHostLoopCluster(
            List<WatermarkSolidCandidate> cluster,
            Bounds referenceBounds,
            StepWatermarkCleanerOptions options)
        {
            if (cluster.Count == 0)
                return false;

            Bounds clusterBounds = new Bounds();
            foreach (var candidate in cluster)
                clusterBounds.Include(candidate.Bounds);

            if (!LooksLikeSmallMark(clusterBounds, referenceBounds, options))
                return false;

            int pointCount = cluster.Sum(candidate => candidate.PointCount);
            return cluster.Count >= options.AutomaticClusterMinFaceCount ||
                pointCount >= options.AutomaticClusterMinPointCount;
        }

        private static List<int> FindEmbeddedWatermarkFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            HashSet<int> removableSolids,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            var candidates = new List<WatermarkFaceCandidate>();
            var seenFaces = new HashSet<int>();

            foreach (var styledItem in styledItems)
            {
                if (data.GetTypeName(styledItem.TargetId) != "ADVANCED_FACE")
                    continue;

                if (!styledItem.Color.HasValue)
                    continue;

                bool hasEmbeddedColorCue = IsEmbeddedWatermarkColor(styledItem.Color.Value, options);
                if (!hasEmbeddedColorCue)
                    continue;

                if (!faceOwners.TryGetValue(styledItem.TargetId, out int ownerSolidId))
                    continue;

                if (removableSolids.Contains(ownerSolidId))
                    continue;

                if (!solidInfo.TryGetValue(ownerSolidId, out var ownerInfo))
                    continue;

                bool allowLightHost = IsDarkWatermarkColor(styledItem.Color.Value, options);
                if (!allowLightHost &&
                    (!ownerInfo.ReplacementStyleId.HasValue || !ownerInfo.ReplacementColor.HasValue))
                    continue;

                if (!allowLightHost &&
                    options.RequireDarkOwner &&
                    ownerInfo.ReplacementColor.Value.Luminance > options.NeutralBodyMaxLuminance)
                    continue;

                var faceBounds = data.GetBounds(styledItem.TargetId);
                if (!faceBounds.HasValue || !ownerInfo.Bounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                if (!seenFaces.Add(styledItem.TargetId))
                    continue;

                var singleFace = new HashSet<int> { styledItem.TargetId };
                var host = ChooseHostPlane(data, ownerInfo, singleFace, faceBounds.Value, styledByTarget, allowLightHost, options);
                if (host == null || !host.HostFaceId.HasValue)
                    continue;

                var hostBounds = data.GetBounds(host.HostFaceId.Value);
                if (!hostBounds.HasValue)
                    continue;

                if (!LooksLikeKnownWatermarkPatternComponent(faceBounds.Value, hostBounds.Value, host.Axis))
                    continue;

                candidates.Add(new WatermarkFaceCandidate
                {
                    FaceId = styledItem.TargetId,
                    OwnerId = ownerSolidId,
                    Bounds = faceBounds.Value,
                    PointCount = data.GetPointIds(styledItem.TargetId, includeSurface: false).Count,
                    Host = host,
                    HostBounds = hostBounds.Value,
                    HasColorCue = true,
                    ColorClass = IsDarkWatermarkColor(styledItem.Color.Value, options) ? -1 : 1
                });
            }

            AddGeometricWatermarkCandidates(data, faceOwners, solidInfo, removableSolids, styledByTarget, candidates, seenFaces, options);
            return FilterAutomaticWatermarkClusters(data, candidates, options);
        }

        private static void AddGeometricWatermarkCandidates(
            StepData data,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            HashSet<int> removableSolids,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            List<WatermarkFaceCandidate> candidates,
            HashSet<int> seenFaces,
            StepWatermarkCleanerOptions options)
        {
            foreach (var kvp in faceOwners)
            {
                int faceId = kvp.Key;
                int ownerSolidId = kvp.Value;
                if (seenFaces.Contains(faceId) || removableSolids.Contains(ownerSolidId))
                    continue;

                if (HasProtectedNonWatermarkColor(faceId, styledByTarget, options))
                    continue;

                if (!solidInfo.TryGetValue(ownerSolidId, out var ownerInfo))
                    continue;

                var faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue || !ownerInfo.Bounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                var singleFace = new HashSet<int> { faceId };
                var host = ChooseHostPlane(data, ownerInfo, singleFace, faceBounds.Value, styledByTarget, allowLightHost: true, options);
                if (host == null || !host.HostFaceId.HasValue)
                    continue;

                var hostBounds = data.GetBounds(host.HostFaceId.Value);
                if (!hostBounds.HasValue)
                    continue;

                if (!LooksLikeKnownWatermarkPatternComponent(faceBounds.Value, hostBounds.Value, host.Axis))
                    continue;

                if (TouchesProjectedBoundary(faceBounds.Value, hostBounds.Value, host.Axis, GetAutomaticEdgeMargin(hostBounds.Value, host.Axis) * 2.0))
                    continue;

                seenFaces.Add(faceId);
                candidates.Add(new WatermarkFaceCandidate
                {
                    FaceId = faceId,
                    OwnerId = ownerSolidId,
                    Bounds = faceBounds.Value,
                    PointCount = data.GetPointIds(faceId, includeSurface: false).Count,
                    Host = host,
                    HostBounds = hostBounds.Value,
                    HasColorCue = false,
                    ColorClass = 0
                });
            }
        }

        private static bool LooksLikeNeutralCoplanarWatermarkColor(
            StepColor? color,
            StepWatermarkCleanerOptions options)
        {
            return color.HasValue &&
                !IsEmbeddedWatermarkColor(color.Value, options) &&
                color.Value.Luminance <= options.NeutralBodyMaxLuminance &&
                color.Value.ChannelSpread <= options.NeutralMaxChannelSpread;
        }

        private static bool IsNeutralContrastWatermarkColor(
            StepColor color,
            StepWatermarkCleanerOptions options)
        {
            return color.ChannelSpread <= options.NeutralMaxChannelSpread &&
                color.Luminance > options.DarkWatermarkMaxLuminance &&
                color.Luminance < options.EmbeddedWatermarkMinLuminance;
        }

        private static List<int> FilterAutomaticWatermarkClusters(
            StepData data,
            List<WatermarkFaceCandidate> candidates,
            StepWatermarkCleanerOptions options)
        {
            var result = new HashSet<int>();
            if (candidates.Count == 0)
                return new List<int>();

            double planeTolerance = Math.Max(options.PlaneTolerance, 0.000001);
            var groups = candidates.GroupBy(candidate => new
            {
                candidate.OwnerId,
                HostFaceId = candidate.Host.HostFaceId.Value,
                candidate.Host.Axis,
                candidate.ColorClass,
                Coordinate = Math.Round(candidate.Host.TargetCoordinate / planeTolerance)
            });

            foreach (var group in groups)
            {
                var groupCandidates = group.ToList();
                double gap = GetAutomaticClusterGap(groupCandidates[0].HostBounds, group.Key.Axis, options);
                foreach (var cluster in BuildProjectedCandidateClusters(groupCandidates, group.Key.Axis, gap))
                {
                    if (!LooksLikeAutomaticWatermarkCluster(cluster, group.Key.Axis, options))
                        continue;

                    foreach (var candidate in SelectWatermarkClusterMembers(cluster, group.Key.Axis))
                        result.Add(candidate.FaceId);
                }
            }

            return result.OrderBy(id => id).ToList();
        }

        private static IEnumerable<WatermarkFaceCandidate> SelectWatermarkClusterMembers(
            List<WatermarkFaceCandidate> cluster,
            int axis)
        {
            if (cluster.Count < 8)
                return cluster;

            Bounds clusterBounds = new Bounds();
            foreach (var candidate in cluster)
                clusterBounds.Include(candidate.Bounds);

            int uAxis;
            int vAxis;
            GetProjectedAxes(axis, out uAxis, out vAxis);

            double width = Math.Abs(clusterBounds.Size.Get(uAxis));
            double height = Math.Abs(clusterBounds.Size.Get(vAxis));
            if (width <= 0.000001 || height <= 0.000001 || height <= width)
                return cluster;

            var componentBounds = cluster.Select(candidate => candidate.Bounds).ToList();
            int columnCount = CountProjectedBands(
                componentBounds,
                uAxis,
                Math.Max(width * 0.035, 0.000001));
            int rowCount = CountProjectedBands(
                componentBounds,
                vAxis,
                Math.Max(height * 0.08, 0.000001));
            if (columnCount < 2 || rowCount < 5)
                return cluster;

            double bandGap = Math.Max(width * 0.035, 0.000001);
            var bands = new List<ProjectedClusterBand>();
            foreach (var candidate in cluster.OrderBy(candidate => candidate.Bounds.Min.Get(uAxis)))
            {
                double min = candidate.Bounds.Min.Get(uAxis);
                double max = candidate.Bounds.Max.Get(uAxis);
                ProjectedClusterBand band = bands.FirstOrDefault(existing => min <= existing.Max + bandGap && max >= existing.Min - bandGap);
                if (band == null)
                {
                    band = new ProjectedClusterBand
                    {
                        Min = min,
                        Max = max,
                        VMin = candidate.Bounds.Min.Get(vAxis),
                        VMax = candidate.Bounds.Max.Get(vAxis)
                    };
                    bands.Add(band);
                }

                band.Min = Math.Min(band.Min, min);
                band.Max = Math.Max(band.Max, max);
                band.VMin = Math.Min(band.VMin, candidate.Bounds.Min.Get(vAxis));
                band.VMax = Math.Max(band.VMax, candidate.Bounds.Max.Get(vAxis));
                band.Candidates.Add(candidate);
                band.Score += Math.Max(candidate.PointCount, 1);
            }

            if (bands.Count < 2)
                return cluster;

            foreach (var band in bands)
            {
                double bandHeight = Math.Max(band.VMax - band.VMin, 0.000001);
                band.RowCount = CountProjectedBands(
                    band.Candidates.Select(candidate => candidate.Bounds).ToList(),
                    vAxis,
                    Math.Max(bandHeight * 0.08, 0.000001));
            }

            var rankedBands = bands.Any(band => band.RowCount >= 4)
                ? bands.Where(band => band.RowCount >= 4)
                : bands;
            var dominantBand = rankedBands
                .OrderByDescending(band => band.RowCount)
                .ThenByDescending(band => band.Candidates.Count)
                .ThenByDescending(band => band.Score)
                .ThenByDescending(band => band.VMax - band.VMin)
                .First();
            double maxDistance = Math.Max(width * 0.08, 0.000001);
            var selected = dominantBand.Candidates
                .Where(candidate => candidate.Bounds.Min.Get(uAxis) >= dominantBand.Min - maxDistance &&
                    candidate.Bounds.Max.Get(uAxis) <= dominantBand.Max + maxDistance)
                .ToList();

            return selected.Count >= 5 ? selected : cluster;
        }

        private static void AddAutomaticRegionAdjacentFacesToFlattenResult(
            StepData data,
            Dictionary<int, SolidInfo> solidInfo,
            FlattenResult flattenResult,
            List<AutomaticWatermarkRegion> regions,
            StepWatermarkCleanerOptions options)
        {
            foreach (var region in regions)
            {
                int hostFaceId = region.HostFaceId;
                if (TouchesProjectedBoundary(
                    region.Bounds,
                    region.HostBounds,
                    region.Axis,
                    GetAutomaticEdgeMargin(region.HostBounds, region.Axis)))
                    continue;

                var seedBounds = new HashSet<int>(
                    data.GetMatchingInnerFaceBounds(
                        hostFaceId,
                        region.Bounds,
                        region.Axis,
                        options.HostPlaneProjectionPadding)
                        .Where(boundId => EntityInsideDetectedRegion(data, boundId, region.Bounds, region.Axis, options.HostPlaneProjectionPadding)));
                if (seedBounds.Count == 0)
                    continue;

                SolidInfo ownerInfo = solidInfo.Values.FirstOrDefault(info => info.FaceIds.Contains(hostFaceId));
                if (ownerInfo == null)
                    continue;

                var host = new HostPlaneMatch
                {
                    Axis = region.Axis,
                    TargetCoordinate = region.HostCoordinate,
                    HostFaceId = hostFaceId
                };

                foreach (int adjacentFaceId in FindHostLoopAdjacentFaces(data, ownerInfo, host, seedBounds, options, region.Bounds))
                {
                    if (adjacentFaceId == hostFaceId)
                        continue;

                    if (flattenResult.FlattenedFaces.Add(adjacentFaceId))
                        flattenResult.FlattenedFaceCount++;

                    flattenResult.ReplacementFaceByRemovedFace[adjacentFaceId] = hostFaceId;
                }
            }
        }

        private static List<AutomaticWatermarkRegion> BuildAutomaticFlattenRegions(
            StepData data,
            AutomaticWatermarkDetection detection,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            var seeds = new List<AutomaticWatermarkRegion>();
            seeds.AddRange(detection.AutomaticRegions);

            foreach (int faceId in detection.EmbeddedFaceIds.Concat(detection.CoplanarFaceIds).Distinct())
            {
                if (!faceOwners.TryGetValue(faceId, out int ownerId))
                    continue;

                if (!solidInfo.TryGetValue(ownerId, out var ownerInfo))
                    continue;

                var faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                var singleFace = new HashSet<int> { faceId };
                bool allowLightHost = ComponentHasDarkWatermarkFace(singleFace, styledByTarget, options);
                var host = ChooseHostPlane(data, ownerInfo, singleFace, faceBounds.Value, styledByTarget, allowLightHost, options);
                if (host == null || !host.HostFaceId.HasValue)
                    continue;

                var hostBounds = data.GetBounds(host.HostFaceId.Value);
                if (!hostBounds.HasValue)
                    continue;

                seeds.Add(new AutomaticWatermarkRegion
                {
                    OwnerId = ownerId,
                    HostFaceId = host.HostFaceId.Value,
                    Axis = host.Axis,
                    HostCoordinate = host.TargetCoordinate,
                    Bounds = faceBounds.Value,
                    HostBounds = hostBounds.Value
                });
            }

            foreach (var kvp in detection.HostFaceBoundsToRemove)
            {
                int hostFaceId = kvp.Key;
                var ownerInfo = solidInfo.Values.FirstOrDefault(info => info.FaceIds.Contains(hostFaceId));
                if (ownerInfo == null)
                    continue;

                var hostBounds = data.GetBounds(hostFaceId);
                if (!hostBounds.HasValue)
                    continue;

                int axis = FindPlanarAxis(hostBounds.Value, options);
                if (axis < 0)
                    axis = GetSmallestAxis(hostBounds.Value);

                double hostCoordinate = (hostBounds.Value.Min.Get(axis) + hostBounds.Value.Max.Get(axis)) / 2.0;
                foreach (int boundId in kvp.Value)
                {
                    var boundBounds = data.GetBounds(boundId);
                    if (!boundBounds.HasValue)
                        continue;

                    seeds.Add(new AutomaticWatermarkRegion
                    {
                        OwnerId = ownerInfo.SolidId,
                        HostFaceId = hostFaceId,
                        Axis = axis,
                        HostCoordinate = hostCoordinate,
                        Bounds = boundBounds.Value,
                        HostBounds = hostBounds.Value
                    });
                }
            }

            return MergeAutomaticFlattenRegionSeeds(seeds, options);
        }

        private static List<AutomaticWatermarkRegion> MergeAutomaticFlattenRegionSeeds(
            List<AutomaticWatermarkRegion> seeds,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<AutomaticWatermarkRegion>();
            if (seeds.Count == 0)
                return result;

            double planeTolerance = Math.Max(options.PlaneTolerance, 0.000001);
            var groups = seeds.GroupBy(seed => new
            {
                seed.OwnerId,
                seed.HostFaceId,
                seed.Axis,
                Coordinate = Math.Round(seed.HostCoordinate / planeTolerance)
            });

            foreach (var group in groups)
            {
                var groupSeeds = group.ToList();
                double gap = GetAutomaticClusterGap(groupSeeds[0].HostBounds, group.Key.Axis, options);
                foreach (var cluster in BuildProjectedRegionClusters(groupSeeds, group.Key.Axis, gap))
                {
                    Bounds bounds = new Bounds();
                    foreach (var seed in cluster)
                        bounds.Include(seed.Bounds);

                    result.Add(new AutomaticWatermarkRegion
                    {
                        OwnerId = group.Key.OwnerId,
                        HostFaceId = group.Key.HostFaceId,
                        Axis = group.Key.Axis,
                        HostCoordinate = cluster[0].HostCoordinate,
                        Bounds = bounds,
                        HostBounds = cluster[0].HostBounds,
                        IsTemplatePromotion = cluster.Any(seed => seed.IsTemplatePromotion)
                    });
                }
            }

            return result;
        }

        private static List<List<AutomaticWatermarkRegion>> BuildProjectedRegionClusters(
            List<AutomaticWatermarkRegion> seeds,
            int axis,
            double gap)
        {
            var result = new List<List<AutomaticWatermarkRegion>>();
            var visited = new bool[seeds.Count];

            for (int i = 0; i < seeds.Count; i++)
            {
                if (visited[i])
                    continue;

                var cluster = new List<AutomaticWatermarkRegion>();
                var queue = new Queue<int>();
                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    var current = seeds[index];
                    cluster.Add(current);

                    for (int j = 0; j < seeds.Count; j++)
                    {
                        if (visited[j])
                            continue;

                        if (!ProjectedBoundsOverlap(current.Bounds, seeds[j].Bounds, axis, gap))
                            continue;

                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }

                result.Add(cluster);
            }

            return result;
        }

        private static List<AutomaticCleanupVolume> BuildAutomaticCleanupVolumes(
            StepData data,
            Dictionary<int, SolidInfo> solidInfo,
            List<AutomaticWatermarkRegion> regions,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<AutomaticCleanupVolume>();
            if (regions.Count == 0)
                return result;

            double planeTolerance = Math.Max(options.PlaneTolerance, 0.000001);
            var groups = regions.GroupBy(region => new
            {
                region.OwnerId,
                region.HostFaceId,
                region.Axis,
                Coordinate = Math.Round(region.HostCoordinate / planeTolerance)
            });

            foreach (var group in groups)
            {
                var groupRegions = group.ToList();
                double gap = Math.Max(
                    GetAutomaticClusterGap(groupRegions[0].HostBounds, group.Key.Axis, options) * 4.0,
                    options.HostPlaneProjectionPadding * 10.0);

                foreach (var cluster in BuildProjectedRegionClusters(groupRegions, group.Key.Axis, gap))
                {
                    Bounds projectedBounds = new Bounds();
                    foreach (var region in cluster)
                        projectedBounds.Include(region.Bounds);

                    double hostCoordinate = cluster[0].HostCoordinate;
                    double? oppositeCoordinate = null;
                    double bestOppositeDistance = double.PositiveInfinity;
                    if (solidInfo.TryGetValue(group.Key.OwnerId, out var ownerInfo))
                    {
                        double maxAllowedDepth = Math.Max(
                            Math.Max(options.HostPlaneSearchDistance, options.HostLoopAdjacentMaxDepth),
                            options.EmbeddedReliefMaxDepth) * 2.0;
                        foreach (int faceId in ownerInfo.FaceIds)
                        {
                            if (faceId == group.Key.HostFaceId)
                                continue;

                            var faceBounds = data.GetBounds(faceId);
                            if (!faceBounds.HasValue)
                                continue;

                            if (!ProjectedBoundsInside(faceBounds.Value, projectedBounds, group.Key.Axis, options.HostPlaneProjectionPadding))
                                continue;

                            if (!LooksLikeSmallMark(faceBounds.Value, cluster[0].HostBounds, options))
                                continue;

                            double minFaceCoordinate = faceBounds.Value.Min.Get(group.Key.Axis);
                            double maxFaceCoordinate = faceBounds.Value.Max.Get(group.Key.Axis);
                            double minDistance = Math.Abs(minFaceCoordinate - hostCoordinate);
                            double maxDistance = Math.Abs(maxFaceCoordinate - hostCoordinate);
                            double candidateCoordinate = minDistance > maxDistance
                                ? minFaceCoordinate
                                : maxFaceCoordinate;
                            double candidateDistance = Math.Abs(candidateCoordinate - hostCoordinate);
                            if (candidateDistance <= options.PlaneTolerance || candidateDistance > maxAllowedDepth)
                                continue;

                            if (candidateDistance < bestOppositeDistance)
                            {
                                bestOppositeDistance = candidateDistance;
                                oppositeCoordinate = candidateCoordinate;
                            }
                        }
                    }

                    double minCoordinate;
                    double maxCoordinate;
                    if (oppositeCoordinate.HasValue)
                    {
                        minCoordinate = Math.Min(hostCoordinate, oppositeCoordinate.Value);
                        maxCoordinate = Math.Max(hostCoordinate, oppositeCoordinate.Value);
                    }
                    else
                    {
                        double fallbackDepth = Math.Max(
                            Math.Max(options.HostPlaneSearchDistance, options.HostLoopAdjacentMaxDepth),
                            options.EmbeddedReliefMaxDepth);
                        minCoordinate = hostCoordinate - fallbackDepth;
                        maxCoordinate = hostCoordinate + fallbackDepth;
                    }

                    foreach (var region in cluster.Where(region => region.IsTemplatePromotion))
                    {
                        minCoordinate = Math.Min(minCoordinate, region.Bounds.Min.Get(group.Key.Axis));
                        maxCoordinate = Math.Max(maxCoordinate, region.Bounds.Max.Get(group.Key.Axis));
                    }

                    result.Add(new AutomaticCleanupVolume
                    {
                        OwnerId = group.Key.OwnerId,
                        HostFaceId = group.Key.HostFaceId,
                        Axis = group.Key.Axis,
                        HostCoordinate = hostCoordinate,
                        MinCoordinate = minCoordinate,
                        MaxCoordinate = maxCoordinate,
                        Bounds = projectedBounds,
                        HostBounds = cluster[0].HostBounds,
                        IsTemplatePromotion = cluster.Any(region => region.IsTemplatePromotion)
                    });
                }
            }

            return result;
        }

        private static void FlattenAllGeometryInsideAutomaticRegions(
            StepData data,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            FlattenResult flattenResult,
            List<AutomaticCleanupVolume> volumes,
            StepWatermarkCleanerOptions options,
            Dictionary<int, string> edits)
        {
            var editedPoints = new HashSet<int>();
            foreach (var volume in volumes)
            {
                if (!solidInfo.TryGetValue(volume.OwnerId, out var ownerInfo))
                    continue;

                if (!ownerInfo.FaceIds.Contains(volume.HostFaceId))
                    continue;

                if (!flattenResult.HostFaceBoundsToRemove.TryGetValue(volume.HostFaceId, out var boundIds))
                {
                    boundIds = new HashSet<int>();
                    flattenResult.HostFaceBoundsToRemove.Add(volume.HostFaceId, boundIds);
                }

                foreach (int boundId in data.GetMatchingInnerFaceBounds(
                    volume.HostFaceId,
                    volume.Bounds,
                    volume.Axis,
                    options.HostPlaneProjectionPadding)
                    .Where(boundId => EntityInsideDetectedRegion(data, boundId, volume.Bounds, volume.Axis, options.HostPlaneProjectionPadding))
                    .Where(boundId => !HostLoopContainsProtectedCylindricalFace(data, ownerInfo, styledByTarget, boundId, volume.Axis, options)))
                    boundIds.Add(boundId);

                foreach (int faceId in ownerInfo.FaceIds)
                {
                    if (faceId == volume.HostFaceId)
                        continue;

                    var faceBounds = data.GetBounds(faceId);
                    if (!faceBounds.HasValue)
                        continue;

                    bool insideCleanupVolume = BoundsInsideCleanupVolume(faceBounds.Value, volume, options);
                    if (!insideCleanupVolume)
                    {
                        if (!volume.IsTemplatePromotion ||
                            !ProjectionIntersects(faceBounds.Value, volume.Bounds, volume.Axis, options.HostPlaneProjectionPadding) ||
                            !BoundsIntersectsCleanupDepth(faceBounds.Value, volume, options) ||
                            ProjectedOverlapRatio(faceBounds.Value, volume.Bounds, volume.Axis) < 0.55)
                        {
                            continue;
                        }
                    }

                    bool protectedColor = HasProtectedNonWatermarkColor(faceId, styledByTarget, options);
                    if (protectedColor &&
                        (!volume.IsTemplatePromotion ||
                            !insideCleanupVolume))
                    {
                        continue;
                    }

                    if (IsCylindricalFace(data, faceId))
                        continue;

                    if (volume.IsTemplatePromotion)
                    {
                        if (flattenResult.FlattenedFaces.Add(faceId))
                            flattenResult.FlattenedFaceCount++;

                        flattenResult.ReplacementFaceByRemovedFace[faceId] = volume.HostFaceId;
                        continue;
                    }

                    bool coplanarResidue = IsCoplanarWithHostPlane(faceBounds.Value, volume.Axis, volume.HostCoordinate, options);
                    if (coplanarResidue)
                    {
                        if (flattenResult.FlattenedFaces.Add(faceId))
                            flattenResult.FlattenedFaceCount++;

                        flattenResult.ReplacementFaceByRemovedFace[faceId] = volume.HostFaceId;
                        continue;
                    }

                    int changedPoints = 0;
                    bool hasOffPlanePoint = false;
                    foreach (int pointId in data.GetPointIds(faceId, includeSurface: true))
                    {
                        if (!data.TryGetPoint(pointId, out var point))
                            continue;

                        if (!PointProjectionInsideCleanupVolume(point, volume, options.HostPlaneProjectionPadding))
                            continue;

                        if (!PointDepthInsideCleanupVolume(point, volume, options))
                            continue;

                        if (Math.Abs(point.Get(volume.Axis) - volume.HostCoordinate) <= options.PlaneTolerance)
                            continue;

                        hasOffPlanePoint = true;
                        if (!editedPoints.Add(pointId))
                            continue;

                        edits[pointId] = data.ReplacePointCoordinate(pointId, volume.Axis, volume.HostCoordinate);
                        changedPoints++;
                    }

                    if (!hasOffPlanePoint)
                        continue;

                    if (flattenResult.FlattenedFaces.Add(faceId))
                        flattenResult.FlattenedFaceCount++;

                    flattenResult.FlattenedPointCount += changedPoints;
                    flattenResult.ReplacementFaceByRemovedFace[faceId] = volume.HostFaceId;
                }
            }
        }

        private static bool BoundsIntersectsCleanupDepth(Bounds bounds, AutomaticCleanupVolume volume, StepWatermarkCleanerOptions options)
        {
            double min = Math.Min(volume.MinCoordinate, volume.MaxCoordinate) - options.PlaneTolerance;
            double max = Math.Max(volume.MinCoordinate, volume.MaxCoordinate) + options.PlaneTolerance;
            return bounds.Max.Get(volume.Axis) >= min && bounds.Min.Get(volume.Axis) <= max;
        }

        private static bool BoundsInsideCleanupDepth(Bounds bounds, AutomaticCleanupVolume volume, StepWatermarkCleanerOptions options)
        {
            double min = Math.Min(volume.MinCoordinate, volume.MaxCoordinate) - options.PlaneTolerance;
            double max = Math.Max(volume.MinCoordinate, volume.MaxCoordinate) + options.PlaneTolerance;
            return bounds.Min.Get(volume.Axis) >= min && bounds.Max.Get(volume.Axis) <= max;
        }

        private static bool BoundsInsideCleanupVolume(Bounds bounds, AutomaticCleanupVolume volume, StepWatermarkCleanerOptions options)
        {
            return ProjectedBoundsInside(bounds, volume.Bounds, volume.Axis, options.HostPlaneProjectionPadding) &&
                BoundsInsideCleanupDepth(bounds, volume, options);
        }

        private static void RemoveProtectedCylindricalHostLoopBounds(
            StepData data,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            Dictionary<int, HashSet<int>> hostFaceBoundsToRemove,
            StepWatermarkCleanerOptions options)
        {
            if (hostFaceBoundsToRemove.Count == 0)
                return;

            var hostFaces = hostFaceBoundsToRemove.Keys.ToList();
            foreach (int hostFaceId in hostFaces)
            {
                var hostBounds = data.GetBounds(hostFaceId);
                if (!hostBounds.HasValue)
                    continue;

                int axis = FindPlanarAxis(hostBounds.Value, options);
                if (axis < 0)
                    continue;

                SolidInfo ownerInfo = null;
                foreach (var info in solidInfo.Values)
                {
                    if (info.FaceIds.Contains(hostFaceId))
                    {
                        ownerInfo = info;
                        break;
                    }
                }

                if (ownerInfo == null)
                    continue;

                var boundIds = hostFaceBoundsToRemove[hostFaceId];
                foreach (int boundId in boundIds.ToList())
                {
                    if (HostLoopContainsProtectedCylindricalFace(data, ownerInfo, styledByTarget, boundId, axis, options))
                        boundIds.Remove(boundId);
                }

                if (boundIds.Count == 0)
                    hostFaceBoundsToRemove.Remove(hostFaceId);
            }
        }

        private static bool HostLoopContainsProtectedCylindricalFace(
            StepData data,
            SolidInfo ownerInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            int boundId,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            var boundBounds = data.GetBounds(boundId);
            if (!boundBounds.HasValue)
                return false;

            foreach (int faceId in ownerInfo.FaceIds)
            {
                if (!HasProtectedNonWatermarkColor(faceId, styledByTarget, options))
                    continue;

                if (!IsCylindricalFace(data, faceId))
                    continue;

                var faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (ProjectedBoundsInside(faceBounds.Value, boundBounds.Value, axis, options.HostPlaneProjectionPadding))
                    return true;
            }

            return false;
        }

        private static bool ProjectionRegionContainsProtectedContactFace(
            StepData data,
            SolidInfo ownerInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            Bounds regionBounds,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            foreach (int faceId in ownerInfo.FaceIds)
            {
                if (ownerInfo.FaceIds.Count > 80)
                    continue;

                bool protectedColor = HasProtectedNonWatermarkColor(faceId, styledByTarget, options);
                bool cylindrical = IsCylindricalFace(data, faceId);
                if (!protectedColor && !cylindrical)
                    continue;

                Bounds? faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (ProjectedBoundsInside(faceBounds.Value, regionBounds, axis, options.HostPlaneProjectionPadding))
                    return true;

                if (!ProjectionIntersects(faceBounds.Value, regionBounds, axis, options.HostPlaneProjectionPadding))
                    continue;

                double overlapRatio = ProjectedOverlapRatio(faceBounds.Value, regionBounds, axis);
                if (overlapRatio >= 0.35)
                    return true;
            }

            return false;
        }

        private static bool OwnerLooksLikeDiscreteConnectorPinOrPad(SolidInfo ownerInfo, Bounds modelBounds)
        {
            if (!ownerInfo.Bounds.HasValue || ownerInfo.FaceIds.Count > 80)
                return false;

            Vec3d ownerSize = ownerInfo.Bounds.Value.Size;
            Vec3d modelSize = modelBounds.Size;
            double modelArea = MaxProjectedArea(modelSize);
            if (modelArea <= 0.0)
                return false;

            double ownerAreaRatio = MaxProjectedArea(ownerSize) / modelArea;
            if (ownerAreaRatio > 0.08)
                return false;

            double ownerLongest = Math.Max(ownerSize.X, Math.Max(ownerSize.Y, ownerSize.Z));
            double modelLongest = Math.Max(modelSize.X, Math.Max(modelSize.Y, modelSize.Z));
            if (modelLongest <= 0.0 || ownerLongest > modelLongest * 0.45)
                return false;

            double ownerSmallest = Math.Min(ownerSize.X, Math.Min(ownerSize.Y, ownerSize.Z));
            return ownerSmallest <= Math.Max(ownerLongest * 0.18, 0.35);
        }

        private static bool IsCylindricalFace(StepData data, int faceId)
        {
            if (!data.Entities.TryGetValue(faceId, out StepEntity faceEntity) ||
                faceEntity.Type != "ADVANCED_FACE" ||
                faceEntity.References.Count == 0)
            {
                return false;
            }

            int surfaceId = faceEntity.References[faceEntity.References.Count - 1];
            return string.Equals(data.GetTypeName(surfaceId), "CYLINDRICAL_SURFACE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool PointDepthInsideCleanupVolume(Vec3d point, AutomaticCleanupVolume volume, StepWatermarkCleanerOptions options)
        {
            double coordinate = point.Get(volume.Axis);
            double min = Math.Min(volume.MinCoordinate, volume.MaxCoordinate) - options.PlaneTolerance;
            double max = Math.Max(volume.MinCoordinate, volume.MaxCoordinate) + options.PlaneTolerance;
            return coordinate >= min && coordinate <= max;
        }

        private static bool PointProjectionInsideCleanupVolume(Vec3d point, AutomaticCleanupVolume volume, double padding)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == volume.Axis)
                    continue;

                double coordinate = point.Get(axis);
                if (coordinate < volume.Bounds.Min.Get(axis) - padding)
                    return false;
                if (coordinate > volume.Bounds.Max.Get(axis) + padding)
                    return false;
            }

            return true;
        }

        private static void AddEmbeddedFaceHostLoopsToFlattenResult(
            StepData data,
            List<int> embeddedFaces,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            FlattenResult flattenResult,
            StepWatermarkCleanerOptions options,
            Dictionary<int, string> edits)
        {
            var editedPoints = new HashSet<int>();
            foreach (int faceId in embeddedFaces)
            {
                if (!faceOwners.TryGetValue(faceId, out int ownerId))
                    continue;

                if (!solidInfo.TryGetValue(ownerId, out var ownerInfo))
                    continue;

                var hostBounds = data.GetBounds(faceId);
                if (!hostBounds.HasValue)
                    continue;

                int axis = FindPlanarAxis(hostBounds.Value, options);
                if (axis < 0)
                    axis = GetSmallestAxis(hostBounds.Value);

                var matchedBounds = new HashSet<int>();
                Bounds? outerBounds = null;
                foreach (int boundId in data.GetInnerFaceBounds(faceId))
                {
                    var boundBounds = data.GetBounds(boundId);
                    if (!boundBounds.HasValue)
                        continue;

                    matchedBounds.Add(boundId);
                }

                if (matchedBounds.Count == 0)
                    continue;

                foreach (int boundId in data.GetAdvancedFaceBounds(faceId))
                {
                    if (data.GetTypeName(boundId) != "FACE_OUTER_BOUND")
                        continue;

                    outerBounds = data.GetBounds(boundId);
                    if (outerBounds.HasValue)
                        break;
                }

                foreach (int expandedBoundId in ExpandHostFaceBounds(data, faceId, matchedBounds, options))
                    matchedBounds.Add(expandedBoundId);

                if (!flattenResult.HostFaceBoundsToRemove.TryGetValue(faceId, out var boundIds))
                {
                    boundIds = new HashSet<int>();
                    flattenResult.HostFaceBoundsToRemove.Add(faceId, boundIds);
                }

                foreach (int boundId in matchedBounds)
                    boundIds.Add(boundId);

                if (outerBounds.HasValue)
                {
                    double targetCoordinate = (outerBounds.Value.Min.Get(axis) + outerBounds.Value.Max.Get(axis)) / 2.0;
                    foreach (int boundId in matchedBounds)
                    {
                        foreach (int pointId in data.GetPointIds(boundId, includeSurface: true))
                        {
                            if (edits.ContainsKey(pointId))
                                continue;

                            if (!data.TryGetPoint(pointId, out var point))
                                continue;

                            if (Math.Abs(point.Get(axis) - targetCoordinate) <= options.PlaneTolerance)
                                continue;

                            if (!editedPoints.Add(pointId))
                                continue;

                            edits[pointId] = data.ReplacePointCoordinate(pointId, axis, targetCoordinate);
                            flattenResult.FlattenedPointCount++;
                        }
                    }
                }

                var host = new HostPlaneMatch
                {
                    Axis = axis,
                    TargetCoordinate = (hostBounds.Value.Min.Get(axis) + hostBounds.Value.Max.Get(axis)) / 2.0,
                    HostFaceId = faceId
                };

                foreach (int adjacentFaceId in FindHostLoopAdjacentFaces(data, ownerInfo, host, matchedBounds, options, hostBounds.Value))
                {
                    if (adjacentFaceId == faceId)
                        continue;

                    if (HasProtectedNonWatermarkColor(adjacentFaceId, styledByTarget, options))
                        continue;

                    if (flattenResult.FlattenedFaces.Add(adjacentFaceId))
                        flattenResult.FlattenedFaceCount++;

                    flattenResult.ReplacementFaceByRemovedFace[adjacentFaceId] = faceId;
                }
            }
        }

        private static AutomaticWatermarkLoopResult FindAutomaticWatermarkHostLoops(
            StepData data,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            var result = new AutomaticWatermarkLoopResult();

            foreach (var ownerInfo in solidInfo.Values)
            {
                foreach (int faceId in ownerInfo.FaceIds)
                {
                    var hostBounds = data.GetBounds(faceId);
                    if (!hostBounds.HasValue)
                        continue;

                    bool allowProtectedHostLoopDetection = HasProtectedNonWatermarkColor(faceId, styledByTarget, options);
                    if (!LooksLikePotentialAutomaticHostFace(faceId, styledByTarget, options) &&
                        !allowProtectedHostLoopDetection)
                        continue;

                    var candidates = new List<WatermarkLoopCandidate>();
                    int planarAxis = FindPlanarAxis(hostBounds.Value, options);
                    foreach (int boundId in data.GetInnerFaceBounds(faceId))
                    {
                        var boundBounds = data.GetBounds(boundId);
                        if (!boundBounds.HasValue)
                            continue;

                        if (!LooksLikeSmallMark(boundBounds.Value, hostBounds.Value, options))
                            continue;

                        candidates.Add(new WatermarkLoopCandidate
                        {
                            BoundId = boundId,
                            HostFaceId = faceId,
                            Bounds = boundBounds.Value,
                            PointCount = data.GetPointIds(boundId, includeSurface: true).Count,
                            HostBounds = hostBounds.Value,
                            AllowStandaloneLoop = IsLightNeutralHostFace(faceId, styledByTarget, options),
                            RequireCompactEngravedCluster = allowProtectedHostLoopDetection
                        });
                    }

                    if (candidates.Count == 0)
                        continue;

                    result.CandidateCount += candidates.Count;
                    int[] axes = planarAxis >= 0
                        ? new[] { planarAxis }
                        : new[] { 0, 1, 2 };

                    foreach (int axis in axes)
                    {
                        double gap = GetAutomaticClusterGap(hostBounds.Value, axis, options) * 1.25;
                        bool acceptedCluster = false;
                        foreach (var cluster in BuildProjectedLoopClusters(candidates, axis, gap))
                        {
                            if (!LooksLikeAutomaticWatermarkLoopCluster(cluster, axis, options))
                                continue;

                            AddAutomaticLoopCluster(result, data, ownerInfo, faceId, hostBounds.Value, axis, cluster, options);
                            acceptedCluster = true;
                        }

                        if (!acceptedCluster && LooksLikeAutomaticWatermarkLoopCluster(candidates, axis, options))
                            AddAutomaticLoopCluster(result, data, ownerInfo, faceId, hostBounds.Value, axis, candidates, options);
                    }
                }
            }

            return result;
        }

        private static void AddAutomaticLoopCluster(
            AutomaticWatermarkLoopResult result,
            StepData data,
            SolidInfo ownerInfo,
            int faceId,
            Bounds hostBounds,
            int axis,
            List<WatermarkLoopCandidate> cluster,
            StepWatermarkCleanerOptions options)
        {
            if (!result.HostFaceBoundsToRemove.TryGetValue(faceId, out var boundIds))
            {
                boundIds = new HashSet<int>();
                result.HostFaceBoundsToRemove.Add(faceId, boundIds);
            }

            Bounds clusterBounds = new Bounds();
            var clusterBoundIds = new HashSet<int>();
            foreach (var candidate in cluster)
            {
                clusterBoundIds.Add(candidate.BoundId);
                clusterBounds.Include(candidate.Bounds);
            }

            foreach (int expandedBoundId in ExpandHostFaceBounds(data, faceId, clusterBoundIds, options))
                boundIds.Add(expandedBoundId);

            result.Regions.Add(new AutomaticWatermarkRegion
            {
                OwnerId = ownerInfo.SolidId,
                HostFaceId = faceId,
                Axis = axis,
                HostCoordinate = (hostBounds.Min.Get(axis) + hostBounds.Max.Get(axis)) / 2.0,
                Bounds = clusterBounds,
                HostBounds = hostBounds
            });
        }

        private static List<int> FindAutomaticRegionWatermarkFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            Dictionary<int, int> faceOwners,
            List<AutomaticWatermarkRegion> regions,
            StepWatermarkCleanerOptions options)
        {
            var result = new HashSet<int>();
            if (regions.Count == 0)
                return new List<int>();

            foreach (var styledItem in styledItems)
            {
                if (data.GetTypeName(styledItem.TargetId) != "ADVANCED_FACE")
                    continue;

                if (!LooksLikeAutomaticRegionWatermarkFaceColor(styledItem.Color, options))
                    continue;

                if (!faceOwners.TryGetValue(styledItem.TargetId, out int ownerId))
                    continue;

                var faceBounds = data.GetBounds(styledItem.TargetId);
                if (!faceBounds.HasValue)
                    continue;

                foreach (var region in regions)
                {
                    if (region.OwnerId != ownerId || region.HostFaceId == styledItem.TargetId)
                        continue;

                    if (!ProjectedBoundsInside(faceBounds.Value, region.Bounds, region.Axis, options.HostPlaneProjectionPadding))
                        continue;

                    double minDistance = Math.Abs(faceBounds.Value.Min.Get(region.Axis) - region.HostCoordinate);
                    double maxDistance = Math.Abs(faceBounds.Value.Max.Get(region.Axis) - region.HostCoordinate);
                    if (Math.Max(minDistance, maxDistance) > options.HostPlaneSearchDistance)
                        continue;

                    if (!LooksLikeSmallMark(faceBounds.Value, region.HostBounds, options))
                        continue;

                    result.Add(styledItem.TargetId);
                    break;
                }
            }

            return result.OrderBy(id => id).ToList();
        }

        private static List<int> FindNeutralCoplanarWatermarkFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            var candidates = new List<WatermarkFaceCandidate>();
            double planeTolerance = Math.Max(options.PlaneTolerance, 0.000001);

            foreach (var styledItem in styledItems)
            {
                if (data.GetTypeName(styledItem.TargetId) != "ADVANCED_FACE")
                    continue;

                if (!faceOwners.TryGetValue(styledItem.TargetId, out int ownerId))
                    continue;

                if (!solidInfo.TryGetValue(ownerId, out var ownerInfo) || !ownerInfo.Bounds.HasValue)
                    continue;

                var faceBounds = data.GetBounds(styledItem.TargetId);
                if (!faceBounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                int planarAxis = FindPlanarAxis(faceBounds.Value, options);
                if (planarAxis < 0)
                    continue;

                bool hasNeutralCoplanarColor = LooksLikeNeutralCoplanarWatermarkColor(styledItem.Color, options);
                bool hasProtectedCoplanarHost = HasProtectedCoplanarHostFace(
                    data,
                    styledItem.TargetId,
                    faceBounds.Value,
                    planarAxis,
                    ownerInfo,
                    styledByTarget,
                    options);
                bool hasDarkCoplanarColorOnColoredHost =
                    styledItem.Color.HasValue &&
                    IsDarkWatermarkColor(styledItem.Color.Value, options) &&
                    hasProtectedCoplanarHost;
                if (!hasNeutralCoplanarColor && !hasDarkCoplanarColorOnColoredHost)
                    continue;

                candidates.Add(new WatermarkFaceCandidate
                {
                    FaceId = styledItem.TargetId,
                    OwnerId = ownerId,
                    Bounds = faceBounds.Value,
                    PointCount = data.GetPointIds(styledItem.TargetId, includeSurface: false).Count,
                    HostBounds = ownerInfo.Bounds.Value,
                    HasColorCue = true,
                    AllowStandaloneColorPattern = hasProtectedCoplanarHost,
                    RestrictStandaloneColorPattern = hasProtectedCoplanarHost,
                    ColorClass = styledItem.Color.HasValue && IsDarkWatermarkColor(styledItem.Color.Value, options)
                        ? -1
                        : styledItem.Color.HasValue && IsEmbeddedWatermarkColor(styledItem.Color.Value, options) ? 1 : 0,
                    Host = new HostPlaneMatch
                    {
                        Axis = planarAxis,
                        TargetCoordinate = (faceBounds.Value.Min.Get(planarAxis) + faceBounds.Value.Max.Get(planarAxis)) / 2.0,
                        HostFaceId = styledItem.TargetId
                    }
                });
            }

            var result = new HashSet<int>();
            foreach (var group in candidates.GroupBy(candidate => new
            {
                candidate.OwnerId,
                candidate.Host.Axis,
                candidate.ColorClass,
                Coordinate = Math.Round(candidate.Host.TargetCoordinate / planeTolerance)
            }))
            {
                var groupCandidates = group.ToList();
                double gap = GetAutomaticClusterGap(groupCandidates[0].HostBounds, group.Key.Axis, options);
                foreach (var cluster in BuildProjectedCandidateClusters(groupCandidates, group.Key.Axis, gap))
                {
                    if (!LooksLikeAutomaticWatermarkCluster(cluster, group.Key.Axis, options))
                        continue;

                    var selectedCluster = SelectWatermarkClusterMembers(cluster, group.Key.Axis).ToList();
                    foreach (var candidate in selectedCluster)
                        result.Add(candidate.FaceId);

                    AddCoplanarCompanionFaces(
                        data,
                        styledItems,
                        faceOwners,
                        solidInfo,
                        result,
                        selectedCluster,
                        group.Key.Axis,
                        gap,
                        options);
                }
            }

            return result.OrderBy(id => id).ToList();
        }

        private static TextTemplateDetectionResult FindTemplateTextFaces(
            CleanupContext context,
            HashSet<int> removableSolids,
            List<int> embeddedFaces,
            List<int> coplanarFaces,
            StepWatermarkCleanerOptions options)
        {
            var result = new TextTemplateDetectionResult();
            Bounds? modelBounds = GetModelBounds(context);
            if (!modelBounds.HasValue)
                return result;

            List<StepWatermarkMarkedRegion> textRegions = DetectTemplateTextRegions(context.Data.Text, modelBounds.Value);
            result.DetectionCount = textRegions.Count;
            if (textRegions.Count == 0)
                return result;

            var excludedFaces = new HashSet<int>(embeddedFaces);
            foreach (int faceId in coplanarFaces)
                excludedFaces.Add(faceId);

            foreach (var faceOwner in context.FaceOwners.OrderBy(kvp => kvp.Key))
            {
                int faceId = faceOwner.Key;
                int ownerId = faceOwner.Value;
                if (excludedFaces.Contains(faceId))
                    continue;

                if (removableSolids.Contains(ownerId))
                    continue;

                if (context.Data.GetTypeName(faceId) != "ADVANCED_FACE")
                    continue;

                if (!context.SolidInfo.TryGetValue(ownerId, out SolidInfo ownerInfo) || !ownerInfo.Bounds.HasValue)
                    continue;

                if (HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, options))
                    continue;

                if (!LooksLikeTemplateTextFaceColor(faceId, context.StyledByTarget, options))
                    continue;

                Bounds? faceBounds = context.Data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                if (!TryFindMarkedRegion(faceBounds.Value, textRegions, options.MarkedCandidateMinOverlap, options, out StepWatermarkMarkedRegion textRegion))
                    continue;

                result.CandidateCount++;

                var singleFace = new HashSet<int> { faceId };
                HostPlaneMatch host = ChooseHostPlane(
                    context.Data,
                    ownerInfo,
                    singleFace,
                    faceBounds.Value,
                    context.StyledByTarget,
                    allowLightHost: true,
                    options);
                if (host == null || !host.HostFaceId.HasValue)
                {
                    result.HostRejectCount++;
                    continue;
                }

                Bounds? hostBounds = context.Data.GetBounds(host.HostFaceId.Value);
                if (!hostBounds.HasValue)
                {
                    result.HostRejectCount++;
                    continue;
                }

                if (TouchesProjectedBoundary(
                    faceBounds.Value,
                    hostBounds.Value,
                    host.Axis,
                    GetAutomaticEdgeMargin(hostBounds.Value, host.Axis) * 2.0) &&
                    !CanRelaxTemplateTextBoundary(ownerInfo, host, textRegion))
                {
                    result.BoundaryRejectCount++;
                    continue;
                }

                result.FaceIds.Add(faceId);
            }

            result.FaceIds.Sort();
            return result;
        }

        private static ProjectionPromotionResult PromoteTemplateTextLogoCleanupRegions(
            CleanupContext context,
            Bounds modelBounds,
            List<AutomaticWatermarkRegion> existingRegions,
            Dictionary<int, HashSet<int>> existingHostFaceBounds,
            List<int> existingFaceIds,
            List<StepWatermarkMarkedRegion> detectedRegions,
            StepWatermarkCleanerOptions options)
        {
            var result = new ProjectionPromotionResult();
            bool vectorPrismOnly = detectedRegions != null;
            List<StepWatermarkMarkedRegion> projectedRegions = detectedRegions ?? DetectTemplateTextLogoRegions(
                    context.Data.Text,
                    modelBounds,
                    textOnly: false,
                    requireHighConfidence: true);
            projectedRegions = PreferCombinedTemplateRegionsByView(projectedRegions);
            int detectedProjectionRegionCount = projectedRegions.Count;
            result.MarkedRegions = projectedRegions
                .Where(HasMarkedRegionArea)
                .Select(region => IsRuntimeTemplateRegion(region) && (region.USign < 0 || region.VSign < 0)
                    ? CreateSignedRuntimeTemplateRegion(region)
                    : region)
                .ToList();
            result.DetectionCount = detectedProjectionRegionCount;
            if (projectedRegions.Count == 0)
                return result;

            if (!vectorPrismOnly)
            {
                foreach (int faceId in FindFacesProjectingIntoTextLogoVisualRegions(context, result.MarkedRegions, modelBounds, options))
                    result.FaceIds.Add(faceId);
            }

            foreach (StepWatermarkMarkedRegion region in projectedRegions.Where(HasMarkedRegionArea))
            {
                bool acceptedRegion = false;
                if (vectorPrismOnly)
                {
                    var candidateRegions = new List<StepWatermarkMarkedRegion>();
                    if (IsRuntimeTemplateRegion(region) && (region.USign < 0 || region.VSign < 0))
                    {
                        candidateRegions.Add(CreateSignedRuntimeTemplateRegion(region));
                    }
                    else
                    {
                        candidateRegions.Add(region);
                        if (region.USign < 0 || region.VSign < 0)
                            candidateRegions.Add(CreateSignedRuntimeTemplateRegion(region));
                    }

                    foreach (StepWatermarkMarkedRegion candidateRegion in candidateRegions)
                    {
                        bool candidateAccepted = TryPromoteVectorPrismRegion(
                            context,
                            modelBounds,
                            candidateRegion,
                            existingRegions,
                            existingHostFaceBounds,
                            existingFaceIds,
                            result,
                            options);
                        if (candidateAccepted)
                            acceptedRegion = true;
                    }

                    if (!acceptedRegion)
                        result.HostRejectCount++;
                    continue;
                }

                foreach (SolidInfo ownerInfo in context.SolidInfo.Values)
                {
                    if (!ownerInfo.Bounds.HasValue)
                        continue;

                    foreach (int hostFaceId in ownerInfo.FaceIds)
                    {
                        Bounds? hostBounds = context.Data.GetBounds(hostFaceId);
                        if (!hostBounds.HasValue)
                            continue;

                        int hostAxis = FindPlanarAxis(hostBounds.Value, options);
                        if (hostAxis != region.DepthAxis)
                            continue;

                        if (HasProtectedNonWatermarkColor(hostFaceId, context.StyledByTarget, options))
                            continue;

                        Bounds regionBounds = CreateProjectionRegionBounds(region, hostBounds.Value, options);
                        if (!ProjectionIntersects(hostBounds.Value, regionBounds, hostAxis, options.HostPlaneProjectionPadding))
                            continue;

                        if (ProjectionRegionContainsProtectedContactFace(
                            context.Data,
                            ownerInfo,
                            context.StyledByTarget,
                            regionBounds,
                            hostAxis,
                            options))
                        {
                            result.ProtectedRejectCount++;
                            continue;
                        }

                        double hostCoordinate = (hostBounds.Value.Min.Get(hostAxis) + hostBounds.Value.Max.Get(hostAxis)) / 2.0;
                        var acceptedBounds = new Bounds();
                        var selectedBoundIds = new HashSet<int>();
                        bool rejectedProtectedLoop = false;

                        foreach (int boundId in context.Data.GetMatchingInnerFaceBounds(
                            hostFaceId,
                            regionBounds,
                            hostAxis,
                            options.HostPlaneProjectionPadding)
                            .Where(boundId => EntityInsideDetectedRegion(context.Data, boundId, regionBounds, hostAxis, options.HostPlaneProjectionPadding)))
                        {
                            Bounds? boundBounds = context.Data.GetBounds(boundId);
                            if (!boundBounds.HasValue)
                                continue;

                            if (!LooksLikeSmallMark(boundBounds.Value, hostBounds.Value, options))
                                continue;

                            if (!TryFindMarkedRegion(boundBounds.Value, projectedRegions, options.MarkedLoopMinOverlap, options, out StepWatermarkMarkedRegion matchedRegion) ||
                                !ReferenceEquals(matchedRegion, region))
                                continue;

                            result.CandidateCount++;
                            if (HostLoopContainsProtectedCylindricalFace(context.Data, ownerInfo, context.StyledByTarget, boundId, hostAxis, options))
                            {
                                rejectedProtectedLoop = true;
                                continue;
                            }

                            selectedBoundIds.Add(boundId);
                            acceptedBounds.Include(boundBounds.Value);
                        }

                        var selectedFaceIds = FindProjectionRegionShallowFaces(
                            context,
                            ownerInfo,
                            hostFaceId,
                            hostAxis,
                            hostCoordinate,
                            region,
                            regionBounds,
                            hostBounds.Value,
                            selectedBoundIds.Count > 0,
                            requireTemplateOrSmallMarkFace: false,
                            options);
                        foreach (int faceId in FindProjectionRegionContainedTemplateFaces(
                            context,
                            ownerInfo,
                            hostFaceId,
                            hostAxis,
                            regionBounds,
                            options))
                        {
                            if (!selectedFaceIds.Contains(faceId))
                                selectedFaceIds.Add(faceId);
                        }
                        foreach (int faceId in selectedFaceIds)
                        {
                            Bounds? faceBounds = context.Data.GetBounds(faceId);
                            if (faceBounds.HasValue)
                                acceptedBounds.Include(faceBounds.Value);
                        }

                        if (selectedFaceIds.Count > 0)
                            result.CandidateCount += selectedFaceIds.Count;

                        if (selectedBoundIds.Count == 0 && selectedFaceIds.Count == 0)
                        {
                            if (rejectedProtectedLoop)
                                result.ProtectedRejectCount++;
                            else
                                result.HostRejectCount++;
                            continue;
                        }

                        if (ExistingCleanupAlreadyCoversProjectionPromotion(
                            existingRegions,
                            existingHostFaceBounds,
                            existingFaceIds,
                            hostFaceId,
                            hostAxis,
                            selectedBoundIds,
                            selectedFaceIds,
                            acceptedBounds,
                            options))
                        {
                            acceptedRegion = true;
                            continue;
                        }

                        if (selectedBoundIds.Count > 0)
                        {
                            foreach (int expandedBoundId in ExpandHostFaceBounds(context.Data, hostFaceId, selectedBoundIds, options))
                            {
                                Bounds? expandedBounds = context.Data.GetBounds(expandedBoundId);
                                if (!expandedBounds.HasValue)
                                    continue;

                                if (HostLoopContainsProtectedCylindricalFace(context.Data, ownerInfo, context.StyledByTarget, expandedBoundId, hostAxis, options))
                                {
                                    result.ProtectedRejectCount++;
                                    continue;
                                }

                                selectedBoundIds.Add(expandedBoundId);
                                acceptedBounds.Include(expandedBounds.Value);
                            }

                            if (!result.HostFaceBoundsToRemove.TryGetValue(hostFaceId, out HashSet<int> boundIds))
                            {
                                boundIds = new HashSet<int>();
                                result.HostFaceBoundsToRemove.Add(hostFaceId, boundIds);
                            }

                            foreach (int boundId in selectedBoundIds)
                                boundIds.Add(boundId);
                        }

                        foreach (int faceId in selectedFaceIds)
                            result.FaceIds.Add(faceId);

                        result.Regions.Add(new AutomaticWatermarkRegion
                        {
                            OwnerId = ownerInfo.SolidId,
                            HostFaceId = hostFaceId,
                            Axis = hostAxis,
                            HostCoordinate = hostCoordinate,
                            Bounds = acceptedBounds,
                            HostBounds = hostBounds.Value,
                            IsTemplatePromotion = true
                        });
                        acceptedRegion = true;
                    }
                }

                if (!acceptedRegion)
                    acceptedRegion = TryPromoteProjectionRegionFaceCluster(
                        context,
                        modelBounds,
                        region,
                        existingRegions,
                        existingHostFaceBounds,
                        existingFaceIds,
                        result,
                        options);

                if (!acceptedRegion)
                    acceptedRegion = TryPromoteProjectionRegionHostFace(
                        context,
                        modelBounds,
                        region,
                        result,
                        options);

                if (!acceptedRegion)
                    result.HostRejectCount++;
            }

            result.FaceIds = result.FaceIds
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            return result;
        }

        private static bool TryPromoteVectorPrismRegion(
            CleanupContext context,
            Bounds modelBounds,
            StepWatermarkMarkedRegion region,
            List<AutomaticWatermarkRegion> existingRegions,
            Dictionary<int, HashSet<int>> existingHostFaceBounds,
            List<int> existingFaceIds,
            ProjectionPromotionResult result,
            StepWatermarkCleanerOptions options)
        {
            var candidatesByOwner = new Dictionary<int, VectorPrismHostCandidate>();
            bool addedRegion = false;
            int ownerCount = 0;
            int skippedPinOwnerCount = 0;
            int hostCandidateCount = 0;
            int projectionIntersectCount = 0;
            int ownerIntersectCount = 0;
            int coveragePassCount = 0;
            foreach (SolidInfo ownerInfo in context.SolidInfo.Values)
            {
                ownerCount++;
                if (!ownerInfo.Bounds.HasValue)
                    continue;

                if (OwnerLooksLikeDiscreteConnectorPinOrPad(ownerInfo, modelBounds))
                {
                    skippedPinOwnerCount++;
                    result.ProtectedRejectCount++;
                    continue;
                }

                List<PlanarHostCandidate> hostCandidates = ownerInfo.PlanarHostCandidatesByAxis != null &&
                    region.DepthAxis >= 0 &&
                    region.DepthAxis < ownerInfo.PlanarHostCandidatesByAxis.Length
                        ? ownerInfo.PlanarHostCandidatesByAxis[region.DepthAxis]
                        : null;
                if (hostCandidates == null)
                    hostCandidates = BuildPlanarHostCandidatesForAxis(context.Data, ownerInfo.FaceIds, region.DepthAxis, options);

                foreach (PlanarHostCandidate candidate in hostCandidates)
                {
                    hostCandidateCount++;
                    bool protectedHostFace = HasProtectedNonWatermarkColor(candidate.FaceId, context.StyledByTarget, options);
                    if (protectedHostFace)
                        result.ProtectedRejectCount++;

                    Bounds regionBounds = CreateProjectionRegionBounds(region, candidate.Bounds, options);
                    if (!ProjectionIntersects(candidate.Bounds, regionBounds, region.DepthAxis, options.HostPlaneProjectionPadding))
                        continue;
                    projectionIntersectCount++;

                    if (!ProjectionIntersects(ownerInfo.Bounds.Value, regionBounds, region.DepthAxis, options.HostPlaneProjectionPadding))
                        continue;
                    ownerIntersectCount++;

                    bool containsProtectedContact = ProjectionRegionContainsProtectedContactFace(
                        context.Data,
                        ownerInfo,
                        context.StyledByTarget,
                        regionBounds,
                        region.DepthAxis,
                        options);
                    if (containsProtectedContact)
                        result.ProtectedRejectCount++;

                    double coverage = ProjectedOverlapRatio(regionBounds, candidate.Bounds, region.DepthAxis);
                    coveragePassCount++;

                    double sideCoordinate = region.DepthSign >= 0
                        ? ownerInfo.Bounds.Value.Max.Get(region.DepthAxis)
                        : ownerInfo.Bounds.Value.Min.Get(region.DepthAxis);
                    double sideDistance = Math.Abs(candidate.Coordinate - sideCoordinate);
                    double score = coverage * 10000.0 - sideDistance * 100000.0 + candidate.ProjectedArea * 0.01;
                    if (!candidatesByOwner.TryGetValue(ownerInfo.SolidId, out VectorPrismHostCandidate ownerBest) ||
                        score > ownerBest.Score)
                    {
                        candidatesByOwner[ownerInfo.SolidId] = new VectorPrismHostCandidate
                        {
                            OwnerInfo = ownerInfo,
                            HostFaceId = candidate.FaceId,
                            Axis = region.DepthAxis,
                            HostCoordinate = candidate.Coordinate,
                            RegionBounds = regionBounds,
                            HostBounds = candidate.Bounds,
                            ProtectedHostFace = protectedHostFace,
                            ContainsProtectedContact = containsProtectedContact,
                            Score = score
                        };
                    }
                }
            }
            if (candidatesByOwner.Count == 0)
            {
                result.Diagnostics.Add(
                    "Vector prism candidate search: view=" + (region.ViewName ?? string.Empty) +
                    " template=" + (region.TemplateName ?? string.Empty) +
                    " owners=" + ownerCount.ToString(CultureInfo.InvariantCulture) +
                    " skippedPinOwners=" + skippedPinOwnerCount.ToString(CultureInfo.InvariantCulture) +
                    " hostCandidates=" + hostCandidateCount.ToString(CultureInfo.InvariantCulture) +
                    " projectedIntersections=" + projectionIntersectCount.ToString(CultureInfo.InvariantCulture) +
                    " ownerIntersections=" + ownerIntersectCount.ToString(CultureInfo.InvariantCulture) +
                    " coveragePass=" + coveragePassCount.ToString(CultureInfo.InvariantCulture));
            }

            int crossOwnerInnerBoundCount = region.DepthAxis == 2 && region.DepthSign > 0
                ? AddProjectionRegionInnerFaceBoundsForAllOwners(
                    context,
                    modelBounds,
                    region,
                    result.HostFaceBoundsToRemove,
                    options)
                : 0;
            if (crossOwnerInnerBoundCount > 0)
            {
                result.CandidateCount += crossOwnerInnerBoundCount;
                result.Diagnostics.Add(
                    "Vector prism inner face bounds: view=" + (region.ViewName ?? string.Empty) +
                    " template=" + (region.TemplateName ?? string.Empty) +
                    " acceptedBounds=" + crossOwnerInnerBoundCount.ToString(CultureInfo.InvariantCulture) +
                    " cleanupVolume=host-bounds-only");
                addedRegion = true;
            }

            if (candidatesByOwner.Count == 0)
                return addedRegion;

            foreach (VectorPrismHostCandidate candidate in candidatesByOwner.Values.OrderByDescending(candidate => candidate.Score))
            {
                List<int> selectedFaceIds = FindProjectionRegionShallowFaces(
                    context,
                    candidate.OwnerInfo,
                    candidate.HostFaceId,
                    candidate.Axis,
                    candidate.HostCoordinate,
                    region,
                    candidate.RegionBounds,
                    candidate.HostBounds,
                    hasHostLoopTopology: true,
                    requireTemplateOrSmallMarkFace: true,
                    options);
                if (selectedFaceIds.Count > 80)
                    selectedFaceIds.Clear();

                foreach (int faceId in FindProjectionRegionContainedTemplateFaces(
                        context,
                        candidate.OwnerInfo,
                        candidate.HostFaceId,
                        candidate.Axis,
                        candidate.RegionBounds,
                        options))
                {
                    if (!selectedFaceIds.Contains(faceId))
                        selectedFaceIds.Add(faceId);
                }
                if (selectedFaceIds.Count > 80)
                    selectedFaceIds.Clear();

                result.CandidateCount += Math.Max(selectedFaceIds.Count, 1);

                int selectedFaceBoundCount = candidate.Axis == 2 && region.DepthSign > 0
                    ? AddProjectionRegionInnerFaceBoundsForSelectedFaces(
                        context,
                        candidate.OwnerInfo,
                        candidate.Axis,
                        candidate.HostCoordinate,
                        candidate.RegionBounds,
                        result.HostFaceBoundsToRemove,
                        options)
                    : 0;

                var emptyBounds = new HashSet<int>();
                if (ExistingCleanupAlreadyCoversProjectionPromotion(
                    existingRegions,
                    existingHostFaceBounds,
                    existingFaceIds,
                    candidate.HostFaceId,
                    candidate.Axis,
                    emptyBounds,
                    selectedFaceIds,
                    candidate.RegionBounds,
                    options))
                {
                    continue;
                }

                Bounds automaticRegionBounds = candidate.RegionBounds;
                Bounds? selectedFaceBounds = UnionBounds(context.Data, selectedFaceIds);
                if (selectedFaceBounds.HasValue)
                    automaticRegionBounds = ExpandRegionDepth(candidate.RegionBounds, selectedFaceBounds.Value, candidate.Axis);

                if (!result.HostFaceBoundsToRemove.TryGetValue(candidate.HostFaceId, out HashSet<int> vectorBoundIds))
                {
                    vectorBoundIds = new HashSet<int>();
                    result.HostFaceBoundsToRemove.Add(candidate.HostFaceId, vectorBoundIds);
                }

                List<int> matchingBoundIds = context.Data.GetMatchingInnerFaceBounds(
                    candidate.HostFaceId,
                    candidate.RegionBounds,
                    candidate.Axis,
                    options.HostPlaneProjectionPadding)
                    .Where(boundId => EntityInsideDetectedRegion(context.Data, boundId, candidate.RegionBounds, candidate.Axis, options.HostPlaneProjectionPadding))
                    .ToList();
                HashSet<int> expandedMatchingBoundIds = ShouldExpandTemplateHostFaceBounds(
                    region,
                    selectedFaceIds,
                    matchingBoundIds)
                    ? ExpandTemplateHostFaceBounds(
                        context.Data,
                        candidate.HostFaceId,
                        new HashSet<int>(matchingBoundIds),
                        candidate.Axis,
                        options)
                    : new HashSet<int>(matchingBoundIds);
                int protectedBoundRejectCount = 0;
                foreach (int boundId in expandedMatchingBoundIds)
                {
                    if (HostLoopContainsProtectedCylindricalFace(context.Data, candidate.OwnerInfo, context.StyledByTarget, boundId, candidate.Axis, options))
                    {
                        protectedBoundRejectCount++;
                        continue;
                    }

                    vectorBoundIds.Add(boundId);
                }

                if (selectedFaceIds.Count == 0 &&
                    vectorBoundIds.Count == 0 &&
                    selectedFaceBoundCount == 0)
                {
                    result.Diagnostics.Add(
                        "Vector prism candidate skipped: view=" + (region.ViewName ?? string.Empty) +
                        " template=" + (region.TemplateName ?? string.Empty) +
                        " owner=#" + candidate.OwnerInfo.SolidId.ToString(CultureInfo.InvariantCulture) +
                        " host=#" + candidate.HostFaceId.ToString(CultureInfo.InvariantCulture) +
                        " axis=" + candidate.Axis.ToString(CultureInfo.InvariantCulture) +
                        " selectedFaces=" + selectedFaceIds.Count.ToString(CultureInfo.InvariantCulture) +
                        " matchingBounds=" + matchingBoundIds.Count.ToString(CultureInfo.InvariantCulture) +
                        " acceptedBounds=" + vectorBoundIds.Count.ToString(CultureInfo.InvariantCulture) +
                        " selectedFaceInnerBounds=" + selectedFaceBoundCount.ToString(CultureInfo.InvariantCulture) +
                        " protectedBoundRejects=" + protectedBoundRejectCount.ToString(CultureInfo.InvariantCulture) +
                        " protectedHost=" + candidate.ProtectedHostFace.ToString(CultureInfo.InvariantCulture).ToLowerInvariant() +
                        " protectedContact=" + candidate.ContainsProtectedContact.ToString(CultureInfo.InvariantCulture).ToLowerInvariant() +
                        " region=[" + candidate.RegionBounds.Min.X.ToString("G6", CultureInfo.InvariantCulture) + "," +
                        candidate.RegionBounds.Min.Y.ToString("G6", CultureInfo.InvariantCulture) + "," +
                        candidate.RegionBounds.Min.Z.ToString("G6", CultureInfo.InvariantCulture) + " -> " +
                        candidate.RegionBounds.Max.X.ToString("G6", CultureInfo.InvariantCulture) + "," +
                        candidate.RegionBounds.Max.Y.ToString("G6", CultureInfo.InvariantCulture) + "," +
                        candidate.RegionBounds.Max.Z.ToString("G6", CultureInfo.InvariantCulture) + "]");
                    continue;
                }

                result.Diagnostics.Add(
                    "Vector prism candidate: view=" + (region.ViewName ?? string.Empty) +
                    " template=" + (region.TemplateName ?? string.Empty) +
                    " owner=#" + candidate.OwnerInfo.SolidId.ToString(CultureInfo.InvariantCulture) +
                    " host=#" + candidate.HostFaceId.ToString(CultureInfo.InvariantCulture) +
                    " axis=" + candidate.Axis.ToString(CultureInfo.InvariantCulture) +
                    " selectedFaces=" + selectedFaceIds.Count.ToString(CultureInfo.InvariantCulture) +
                    " matchingBounds=" + matchingBoundIds.Count.ToString(CultureInfo.InvariantCulture) +
                    " acceptedBounds=" + vectorBoundIds.Count.ToString(CultureInfo.InvariantCulture) +
                    " selectedFaceInnerBounds=" + selectedFaceBoundCount.ToString(CultureInfo.InvariantCulture) +
                    " protectedBoundRejects=" + protectedBoundRejectCount.ToString(CultureInfo.InvariantCulture) +
                    " protectedHost=" + candidate.ProtectedHostFace.ToString(CultureInfo.InvariantCulture).ToLowerInvariant() +
                    " protectedContact=" + candidate.ContainsProtectedContact.ToString(CultureInfo.InvariantCulture).ToLowerInvariant() +
                    " region=[" + candidate.RegionBounds.Min.X.ToString("G6", CultureInfo.InvariantCulture) + "," +
                    candidate.RegionBounds.Min.Y.ToString("G6", CultureInfo.InvariantCulture) + "," +
                    candidate.RegionBounds.Min.Z.ToString("G6", CultureInfo.InvariantCulture) + " -> " +
                    candidate.RegionBounds.Max.X.ToString("G6", CultureInfo.InvariantCulture) + "," +
                    candidate.RegionBounds.Max.Y.ToString("G6", CultureInfo.InvariantCulture) + "," +
                    candidate.RegionBounds.Max.Z.ToString("G6", CultureInfo.InvariantCulture) + "]");

                result.Regions.Add(new AutomaticWatermarkRegion
                {
                    OwnerId = candidate.OwnerInfo.SolidId,
                    HostFaceId = candidate.HostFaceId,
                    Axis = candidate.Axis,
                    HostCoordinate = candidate.HostCoordinate,
                    Bounds = automaticRegionBounds,
                    HostBounds = candidate.HostBounds,
                    IsTemplatePromotion = true
                });
                addedRegion = true;
            }

            return addedRegion;
        }

        private static bool TryPromoteContainedVectorPrismOwner(
            CleanupContext context,
            StepWatermarkMarkedRegion region,
            SolidInfo ownerInfo,
            ProjectionPromotionResult result,
            StepWatermarkCleanerOptions options)
        {
            if (!ownerInfo.Bounds.HasValue)
                return false;

            Bounds regionBounds = CreateProjectionRegionBounds(region, ownerInfo.Bounds.Value, options);
            if (!ProjectionIntersects(ownerInfo.Bounds.Value, regionBounds, region.DepthAxis, options.HostPlaneProjectionPadding))
                return false;

            List<int> selectedFaceIds = FindProjectionRegionContainedTemplateFaces(
                context,
                ownerInfo,
                hostFaceId: 0,
                hostAxis: region.DepthAxis,
                regionBounds,
                options);
            if (selectedFaceIds.Count == 0)
                return false;

            foreach (int faceId in selectedFaceIds)
                result.FaceIds.Add(faceId);

            double hostCoordinate = region.DepthSign >= 0
                ? ownerInfo.Bounds.Value.Max.Get(region.DepthAxis)
                : ownerInfo.Bounds.Value.Min.Get(region.DepthAxis);
            result.CandidateCount += selectedFaceIds.Count;
            result.Regions.Add(new AutomaticWatermarkRegion
            {
                OwnerId = ownerInfo.SolidId,
                HostFaceId = 0,
                Axis = region.DepthAxis,
                HostCoordinate = hostCoordinate,
                Bounds = regionBounds,
                HostBounds = ownerInfo.Bounds.Value,
                IsTemplatePromotion = true
            });
            result.Diagnostics.Add(
                "Vector prism contained owner: view=" + (region.ViewName ?? string.Empty) +
                " template=" + (region.TemplateName ?? string.Empty) +
                " owner=#" + ownerInfo.SolidId.ToString(CultureInfo.InvariantCulture) +
                " selectedFaces=" + selectedFaceIds.Count.ToString(CultureInfo.InvariantCulture) +
                " region=[" + regionBounds.Min.X.ToString("G6", CultureInfo.InvariantCulture) + "," +
                regionBounds.Min.Y.ToString("G6", CultureInfo.InvariantCulture) + "," +
                regionBounds.Min.Z.ToString("G6", CultureInfo.InvariantCulture) + " -> " +
                regionBounds.Max.X.ToString("G6", CultureInfo.InvariantCulture) + "," +
                regionBounds.Max.Y.ToString("G6", CultureInfo.InvariantCulture) + "," +
                regionBounds.Max.Z.ToString("G6", CultureInfo.InvariantCulture) + "]");
            return true;
        }

        private static Bounds ExpandRegionDepth(Bounds regionBounds, Bounds depthBounds, int axis)
        {
            double[] min = { regionBounds.Min.X, regionBounds.Min.Y, regionBounds.Min.Z };
            double[] max = { regionBounds.Max.X, regionBounds.Max.Y, regionBounds.Max.Z };
            min[axis] = Math.Min(min[axis], depthBounds.Min.Get(axis));
            max[axis] = Math.Max(max[axis], depthBounds.Max.Get(axis));

            var result = new Bounds();
            result.Include(new Vec3d(min[0], min[1], min[2]));
            result.Include(new Vec3d(max[0], max[1], max[2]));
            return result;
        }

        private static int AddProjectionRegionInnerFaceBoundsForAllOwners(
            CleanupContext context,
            Bounds modelBounds,
            StepWatermarkMarkedRegion region,
            Dictionary<int, HashSet<int>> hostFaceBoundsToRemove,
            StepWatermarkCleanerOptions options)
        {
            int addedCount = 0;
            foreach (SolidInfo ownerInfo in context.SolidInfo.Values)
            {
                if (!ownerInfo.Bounds.HasValue)
                    continue;

                Bounds regionBounds = CreateProjectionRegionBounds(region, ownerInfo.Bounds.Value, options);
                if (!ProjectionIntersects(ownerInfo.Bounds.Value, regionBounds, region.DepthAxis, options.HostPlaneProjectionPadding))
                    continue;

                if (ProjectionRegionContainsProtectedContactFace(
                    context.Data,
                    ownerInfo,
                    context.StyledByTarget,
                    regionBounds,
                    region.DepthAxis,
                    options))
                {
                    continue;
                }

                double hostCoordinate = region.DepthSign >= 0
                    ? ownerInfo.Bounds.Value.Max.Get(region.DepthAxis)
                    : ownerInfo.Bounds.Value.Min.Get(region.DepthAxis);
                addedCount += AddProjectionRegionInnerFaceBoundsForSelectedFaces(
                    context,
                    ownerInfo,
                    region.DepthAxis,
                    hostCoordinate,
                    regionBounds,
                    hostFaceBoundsToRemove,
                    options);
            }

            return addedCount;
        }

        private static int AddProjectionRegionInnerFaceBoundsForSelectedFaces(
            CleanupContext context,
            SolidInfo ownerInfo,
            int axis,
            double hostCoordinate,
            Bounds regionBounds,
            Dictionary<int, HashSet<int>> hostFaceBoundsToRemove,
            StepWatermarkCleanerOptions options)
        {
            int addedCount = 0;
            double maxDepth = Math.Max(
                Math.Max(options.HostPlaneSearchDistance, options.HostLoopAdjacentMaxDepth),
                options.EmbeddedReliefMaxDepth) * 4.0;

            foreach (int faceId in ownerInfo.FaceIds)
            {
                Bounds? faceBounds = context.Data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (!ProjectedBoundsInside(faceBounds.Value, regionBounds, axis, options.HostPlaneProjectionPadding))
                    continue;

                double minDistance = Math.Abs(faceBounds.Value.Min.Get(axis) - hostCoordinate);
                double maxDistance = Math.Abs(faceBounds.Value.Max.Get(axis) - hostCoordinate);
                if (Math.Max(minDistance, maxDistance) > maxDepth)
                    continue;

                if (HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, options) ||
                    IsCylindricalFace(context.Data, faceId))
                {
                    continue;
                }

                foreach (int boundId in context.Data.GetMatchingInnerFaceBounds(
                    faceId,
                    regionBounds,
                    axis,
                    options.HostPlaneProjectionPadding))
                {
                    Bounds? boundBounds = context.Data.GetBounds(boundId);
                    if (!boundBounds.HasValue ||
                        !BoundsInsideDetectionVolume(boundBounds.Value, regionBounds, 0.006))
                        continue;

                    if (HostLoopContainsProtectedCylindricalFace(context.Data, ownerInfo, context.StyledByTarget, boundId, axis, options))
                        continue;

                    if (!hostFaceBoundsToRemove.TryGetValue(faceId, out HashSet<int> boundIds))
                    {
                        boundIds = new HashSet<int>();
                        hostFaceBoundsToRemove.Add(faceId, boundIds);
                    }

                    if (boundIds.Add(boundId))
                        addedCount++;
                }
            }

            return addedCount;
        }

        private static List<int> FindFacesProjectingIntoTextLogoVisualRegions(
            CleanupContext context,
            List<StepWatermarkMarkedRegion> visualRegions,
            Bounds modelBounds,
            StepWatermarkCleanerOptions options)
        {
            var result = new HashSet<int>();
            if (context == null || visualRegions == null || visualRegions.Count == 0)
                return result.ToList();

            var candidateFaceIds = context.FaceOwners.Keys
                .Where(faceId => context.Data.GetTypeName(faceId) == "ADVANCED_FACE")
                .Where(faceId => !HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, options))
                .Where(faceId => !IsCylindricalFace(context.Data, faceId))
                .OrderBy(faceId => faceId)
                .ToList();
            if (candidateFaceIds.Count == 0)
                return result.ToList();

            var boundedVisualRegions = visualRegions
                .Where(HasMarkedRegionArea)
                .ToList();
            foreach (int faceId in candidateFaceIds)
            {
                if (!context.FaceOwners.TryGetValue(faceId, out int ownerId) ||
                    !context.SolidInfo.TryGetValue(ownerId, out SolidInfo ownerInfo) ||
                    !ownerInfo.Bounds.HasValue)
                {
                    continue;
                }

                if (ownerInfo.FaceIds.Count > 500)
                    continue;

                Bounds? faceBounds = context.Data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                if (!TryFindContainingMarkedRegion(
                    faceBounds.Value,
                    boundedVisualRegions,
                    options,
                    out StepWatermarkMarkedRegion matchedVisualRegion))
                {
                    continue;
                }

                result.Add(faceId);
            }

            var faceReport = new StepWatermarkDetectionReport
            {
                RemovableSolidIds = Array.Empty<int>(),
                EmbeddedFaceIds = candidateFaceIds,
                CoplanarFaceIds = Array.Empty<int>(),
                HostLoops = Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = candidateFaceIds
                    .SelectMany(faceId => StepProjectionRenderer.ViewNames.Select(viewName => new StepWatermarkRegionDetection
                    {
                        EntityId = faceId,
                        Kind = "face",
                        ViewName = viewName
                    }))
                    .ToList(),
                Diagnostics = Array.Empty<string>()
            };

            IReadOnlyList<StepProjectionDetectionRegion> faceRegions;
            try
            {
                faceRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    Encoding.Latin1.GetBytes(context.Data.Text),
                    "text-logo-face-roi",
                    faceReport,
                    StepWatermarkVisualOracle.CreateProjectionOptions(StepProjectionRenderMode.Color));
            }
            catch
            {
                return result.ToList();
            }

            foreach (StepProjectionDetectionRegion faceRegion in faceRegions)
            {
                foreach (StepWatermarkMarkedRegion visualRegion in visualRegions)
                {
                    if (!string.Equals(faceRegion.ViewName, visualRegion.ViewName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!SmallProjectionInsideVisualRegion(faceRegion, visualRegion))
                        continue;

                    if (!context.FaceOwners.TryGetValue(faceRegion.EntityId, out int ownerId) ||
                        !context.SolidInfo.TryGetValue(ownerId, out SolidInfo ownerInfo) ||
                        !ownerInfo.Bounds.HasValue)
                    {
                        continue;
                    }

                    if (ownerInfo.FaceIds.Count > 500)
                        continue;

                    Bounds protectedCheckBounds = CreateProjectionRegionBounds(
                        visualRegion,
                        ownerInfo.Bounds.Value,
                        options);
                    if (ProjectionRegionContainsProtectedContactFace(
                        context.Data,
                        ownerInfo,
                        context.StyledByTarget,
                        protectedCheckBounds,
                        visualRegion.DepthAxis,
                        options))
                    {
                        continue;
                    }

                    result.Add(faceRegion.EntityId);
                    break;
                }
            }

            return result
                .OrderBy(id => id)
                .ToList();
        }

        private static bool SmallProjectionInsideVisualRegion(
            StepProjectionDetectionRegion faceRegion,
            StepWatermarkMarkedRegion visualRegion)
        {
            int padding = 10;
            int faceLeft = faceRegion.RectangleX;
            int faceTop = faceRegion.RectangleY;
            int faceRight = faceRegion.RectangleX + faceRegion.RectangleWidth;
            int faceBottom = faceRegion.RectangleY + faceRegion.RectangleHeight;
            int visualLeft = visualRegion.RectangleX - padding;
            int visualTop = visualRegion.RectangleY - padding;
            int visualRight = visualRegion.RectangleX + visualRegion.RectangleWidth + padding;
            int visualBottom = visualRegion.RectangleY + visualRegion.RectangleHeight + padding;
            if (faceLeft < visualLeft ||
                faceTop < visualTop ||
                faceRight > visualRight ||
                faceBottom > visualBottom)
                return false;

            int visualArea = Math.Max(1, visualRegion.RectangleWidth * visualRegion.RectangleHeight);
            int faceArea = Math.Max(1, faceRegion.RectangleWidth * faceRegion.RectangleHeight);
            if (faceArea > visualArea * 2)
                return false;

            return true;
        }

        private static bool FaceNearMarkedRegionDepthSide(
            Bounds faceBounds,
            StepWatermarkMarkedRegion region,
            Bounds modelBounds,
            StepWatermarkCleanerOptions options)
        {
            double modelSide = region.DepthSign >= 0
                ? modelBounds.Max.Get(region.DepthAxis)
                : modelBounds.Min.Get(region.DepthAxis);
            double faceSide = region.DepthSign >= 0
                ? faceBounds.Max.Get(region.DepthAxis)
                : faceBounds.Min.Get(region.DepthAxis);
            double tolerance = Math.Max(
                Math.Max(options.HostPlaneSearchDistance, options.HostLoopAdjacentMaxDepth),
                options.EmbeddedReliefMaxDepth) * 2.0;
            return Math.Abs(faceSide - modelSide) <= tolerance;
        }

        private static void PruneGenericCleanupToTextLogoVisualRegions(
            CleanupContext context,
            AutomaticWatermarkDetection detection,
            List<StepWatermarkMarkedRegion> visualRegions,
            StepWatermarkCleanerOptions options)
        {
            if (context == null || detection == null || visualRegions == null || visualRegions.Count == 0)
                return;

            var boundedVisualRegions = visualRegions
                .Where(HasMarkedRegionArea)
                .ToList();
            if (boundedVisualRegions.Count == 0)
                return;

            detection.RemovableSolidIds = new HashSet<int>(detection.RemovableSolidIds.Where(solidId =>
            {
                if (!context.SolidInfo.TryGetValue(solidId, out SolidInfo info) || !info.Bounds.HasValue)
                    return false;

                return BoundsOverlapsMarkedRegions(
                    info.Bounds.Value,
                    boundedVisualRegions,
                    minOverlapRatio: 0.01,
                    options);
            }));

            detection.EmbeddedFaceIds = detection.EmbeddedFaceIds
                .Where(faceId => EntityBoundsOverlapMarkedRegions(context.Data, faceId, boundedVisualRegions, options))
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            detection.CoplanarFaceIds = detection.CoplanarFaceIds
                .Where(faceId => EntityBoundsOverlapMarkedRegions(context.Data, faceId, boundedVisualRegions, options))
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            foreach (int hostFaceId in detection.HostFaceBoundsToRemove.Keys.ToList())
            {
                HashSet<int> boundIds = detection.HostFaceBoundsToRemove[hostFaceId];
                foreach (int boundId in boundIds.ToList())
                {
                    if (!EntityBoundsOverlapMarkedRegions(context.Data, boundId, boundedVisualRegions, options))
                        boundIds.Remove(boundId);
                }

                if (boundIds.Count == 0)
                    detection.HostFaceBoundsToRemove.Remove(hostFaceId);
            }

            detection.AutomaticRegions.RemoveAll(region =>
                region == null ||
                !BoundsOverlapsMarkedRegions(
                    region.Bounds,
                    boundedVisualRegions,
                    minOverlapRatio: 0.01,
                    options));
        }

        private static bool EntityBoundsOverlapMarkedRegions(
            StepData data,
            int entityId,
            List<StepWatermarkMarkedRegion> visualRegions,
            StepWatermarkCleanerOptions options)
        {
            Bounds? bounds = data.GetBounds(entityId);
            return bounds.HasValue &&
                BoundsOverlapsMarkedRegions(
                    bounds.Value,
                    visualRegions,
                    minOverlapRatio: 0.01,
                    options);
        }

        private static bool EntityBoundsInsideMarkedRegions(
            StepData data,
            int entityId,
            List<StepWatermarkMarkedRegion> visualRegions,
            StepWatermarkCleanerOptions options)
        {
            Bounds? bounds = data.GetBounds(entityId);
            return bounds.HasValue &&
                BoundsInsideMarkedRegions(bounds.Value, visualRegions, options);
        }

        private static bool TryPromoteProjectionRegionFaceCluster(
            CleanupContext context,
            Bounds modelBounds,
            StepWatermarkMarkedRegion region,
            List<AutomaticWatermarkRegion> existingRegions,
            Dictionary<int, HashSet<int>> existingHostFaceBounds,
            List<int> existingFaceIds,
            ProjectionPromotionResult result,
            StepWatermarkCleanerOptions options)
        {
            bool acceptedAny = false;
            var candidatesByHost = new Dictionary<string, ProjectionFaceCluster>(StringComparer.Ordinal);
            foreach (var faceOwner in context.FaceOwners.OrderBy(kvp => kvp.Key))
            {
                int faceId = faceOwner.Key;
                int ownerId = faceOwner.Value;
                if (!context.SolidInfo.TryGetValue(ownerId, out SolidInfo ownerInfo) || !ownerInfo.Bounds.HasValue)
                    continue;

                if (ownerInfo.FaceIds.Count > 500)
                    continue;

                if (context.Data.GetTypeName(faceId) != "ADVANCED_FACE")
                    continue;

                if (HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, options) ||
                    IsCylindricalFace(context.Data, faceId))
                    continue;

                if (!LooksLikeTemplatePromotionFaceColor(faceId, context.StyledByTarget, options))
                    continue;

                Bounds? faceBounds = context.Data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                if (!TryFindMarkedRegion(faceBounds.Value, new List<StepWatermarkMarkedRegion> { region }, options.MarkedCandidateMinOverlap, options, out _))
                    continue;

                var singleFace = new HashSet<int> { faceId };
                HostPlaneMatch host = ChooseHostPlane(
                    context.Data,
                    ownerInfo,
                    singleFace,
                    faceBounds.Value,
                    context.StyledByTarget,
                    allowLightHost: true,
                    options);
                if (host == null || !host.HostFaceId.HasValue || host.Axis != region.DepthAxis)
                    continue;

                Bounds? hostBounds = context.Data.GetBounds(host.HostFaceId.Value);
                if (!hostBounds.HasValue)
                    continue;

                Bounds regionBounds = CreateProjectionRegionBounds(region, hostBounds.Value, options);
                if (!ProjectedBoundsInside(faceBounds.Value, regionBounds, host.Axis, options.HostPlaneProjectionPadding))
                    continue;

                string key = ownerId.ToString(CultureInfo.InvariantCulture) + "|" +
                    host.HostFaceId.Value.ToString(CultureInfo.InvariantCulture) + "|" +
                    host.Axis.ToString(CultureInfo.InvariantCulture) + "|" +
                    Math.Round(host.TargetCoordinate / Math.Max(options.PlaneTolerance, 0.000001)).ToString(CultureInfo.InvariantCulture);
                if (!candidatesByHost.TryGetValue(key, out ProjectionFaceCluster cluster))
                {
                    cluster = new ProjectionFaceCluster
                    {
                        OwnerId = ownerId,
                        HostFaceId = host.HostFaceId.Value,
                        Axis = host.Axis,
                        HostCoordinate = host.TargetCoordinate,
                        HostBounds = hostBounds.Value
                    };
                    candidatesByHost.Add(key, cluster);
                }

                cluster.FaceIds.Add(faceId);
                cluster.Bounds.Include(faceBounds.Value);
            }

            foreach (ProjectionFaceCluster cluster in candidatesByHost.Values)
            {
                if (cluster.FaceIds.Count == 0)
                    continue;

                result.CandidateCount += cluster.FaceIds.Count;
                if (context.SolidInfo.TryGetValue(cluster.OwnerId, out SolidInfo ownerInfo) &&
                    ProjectionRegionContainsProtectedContactFace(
                        context.Data,
                        ownerInfo,
                        context.StyledByTarget,
                        cluster.Bounds,
                        cluster.Axis,
                        options))
                {
                    result.ProtectedRejectCount++;
                    continue;
                }

                var emptyBounds = new HashSet<int>();
                if (ExistingCleanupAlreadyCoversProjectionPromotion(
                    existingRegions,
                    existingHostFaceBounds,
                    existingFaceIds,
                    cluster.HostFaceId,
                    cluster.Axis,
                    emptyBounds,
                    cluster.FaceIds,
                    cluster.Bounds,
                    options))
                {
                    acceptedAny = true;
                    continue;
                }

                foreach (int faceId in cluster.FaceIds)
                    result.FaceIds.Add(faceId);

                result.Regions.Add(new AutomaticWatermarkRegion
                {
                    OwnerId = cluster.OwnerId,
                    HostFaceId = cluster.HostFaceId,
                    Axis = cluster.Axis,
                    HostCoordinate = cluster.HostCoordinate,
                    Bounds = cluster.Bounds,
                    HostBounds = cluster.HostBounds,
                    IsTemplatePromotion = true
                });
                acceptedAny = true;
            }

            return acceptedAny;
        }

        private static bool TryPromoteProjectionRegionHostFace(
            CleanupContext context,
            Bounds modelBounds,
            StepWatermarkMarkedRegion region,
            ProjectionPromotionResult result,
            StepWatermarkCleanerOptions options)
        {
            const int maxHostFallbackRegions = 12;
            bool acceptedAny = false;
            int acceptedCount = 0;

            foreach (SolidInfo ownerInfo in context.SolidInfo.Values)
            {
                if (!ownerInfo.Bounds.HasValue)
                    continue;

                if (OwnerLooksLikeDiscreteConnectorPinOrPad(ownerInfo, modelBounds))
                {
                    result.ProtectedRejectCount++;
                    continue;
                }

                foreach (int hostFaceId in ownerInfo.FaceIds)
                {
                    Bounds? hostBounds = context.Data.GetBounds(hostFaceId);
                    if (!hostBounds.HasValue)
                        continue;

                    int hostAxis = FindPlanarAxis(hostBounds.Value, options);
                    if (hostAxis < 0 &&
                        hostBounds.Value.Size.Get(region.DepthAxis) <= options.HostPlaneSearchDistance)
                    {
                        hostAxis = region.DepthAxis;
                    }

                    if (hostAxis != region.DepthAxis)
                        continue;

                    double hostCoordinate = (hostBounds.Value.Min.Get(hostAxis) + hostBounds.Value.Max.Get(hostAxis)) / 2.0;
                    if (HasProtectedNonWatermarkColor(hostFaceId, context.StyledByTarget, options))
                        continue;

                    Bounds regionBounds = CreateProjectionRegionBounds(region, hostBounds.Value, options);
                    bool hostIntersectsRegion = ProjectionIntersects(hostBounds.Value, regionBounds, hostAxis, options.HostPlaneProjectionPadding);
                    bool ownerIntersectsRegion = ownerInfo.Bounds.HasValue &&
                        ProjectionIntersects(ownerInfo.Bounds.Value, regionBounds, hostAxis, options.HostPlaneProjectionPadding);
                    if (!hostIntersectsRegion && !ownerIntersectsRegion)
                        continue;

                    if (ProjectionRegionContainsProtectedContactFace(
                        context.Data,
                        ownerInfo,
                        context.StyledByTarget,
                        regionBounds,
                        hostAxis,
                        options))
                    {
                        result.ProtectedRejectCount++;
                        continue;
                    }

                    result.CandidateCount++;
                    result.Regions.Add(new AutomaticWatermarkRegion
                    {
                        OwnerId = ownerInfo.SolidId,
                        HostFaceId = hostFaceId,
                        Axis = hostAxis,
                        HostCoordinate = hostCoordinate,
                        Bounds = regionBounds,
                        HostBounds = hostBounds.Value,
                        IsTemplatePromotion = true
                    });
                    acceptedAny = true;
                    acceptedCount++;
                    if (acceptedCount >= maxHostFallbackRegions)
                        return true;
                }

                List<PlanarHostCandidate> hostCandidates = ownerInfo.PlanarHostCandidatesByAxis != null &&
                    region.DepthAxis >= 0 &&
                    region.DepthAxis < ownerInfo.PlanarHostCandidatesByAxis.Length
                        ? ownerInfo.PlanarHostCandidatesByAxis[region.DepthAxis]
                        : null;
                if (hostCandidates == null)
                    hostCandidates = BuildPlanarHostCandidatesForAxis(context.Data, ownerInfo.FaceIds, region.DepthAxis, options);

                foreach (PlanarHostCandidate candidate in hostCandidates)
                {
                    if (HasProtectedNonWatermarkColor(candidate.FaceId, context.StyledByTarget, options))
                        continue;

                    Bounds regionBounds = CreateProjectionRegionBounds(region, ownerInfo.Bounds.Value, options);
                    bool hostIntersectsRegion = ProjectionIntersects(candidate.Bounds, regionBounds, region.DepthAxis, options.HostPlaneProjectionPadding);
                    bool ownerIntersectsRegion = ProjectionIntersects(ownerInfo.Bounds.Value, regionBounds, region.DepthAxis, options.HostPlaneProjectionPadding);
                    if (!hostIntersectsRegion && !ownerIntersectsRegion)
                        continue;

                    if (ProjectionRegionContainsProtectedContactFace(
                        context.Data,
                        ownerInfo,
                        context.StyledByTarget,
                        regionBounds,
                        region.DepthAxis,
                        options))
                    {
                        result.ProtectedRejectCount++;
                        continue;
                    }

                    result.CandidateCount++;
                    result.Regions.Add(new AutomaticWatermarkRegion
                    {
                        OwnerId = ownerInfo.SolidId,
                        HostFaceId = candidate.FaceId,
                        Axis = region.DepthAxis,
                        HostCoordinate = candidate.Coordinate,
                        Bounds = regionBounds,
                        HostBounds = candidate.Bounds,
                        IsTemplatePromotion = true
                    });
                    acceptedAny = true;
                    acceptedCount++;
                    if (acceptedCount >= maxHostFallbackRegions)
                        return true;
                }

                Bounds ownerRegionBounds = CreateProjectionRegionBounds(region, ownerInfo.Bounds.Value, options);
                if (!ProjectionIntersects(ownerInfo.Bounds.Value, ownerRegionBounds, region.DepthAxis, options.HostPlaneProjectionPadding))
                    continue;

                if (ProjectionRegionContainsProtectedContactFace(
                    context.Data,
                    ownerInfo,
                    context.StyledByTarget,
                    ownerRegionBounds,
                    region.DepthAxis,
                    options))
                {
                    result.ProtectedRejectCount++;
                    continue;
                }

                int fallbackHostFaceId = ownerInfo.FaceIds.FirstOrDefault(faceId =>
                    !HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, options));
                if (fallbackHostFaceId <= 0)
                    continue;

                double fallbackCoordinate = region.DepthSign >= 0
                    ? ownerInfo.Bounds.Value.Max.Get(region.DepthAxis)
                    : ownerInfo.Bounds.Value.Min.Get(region.DepthAxis);
                result.CandidateCount++;
                result.Regions.Add(new AutomaticWatermarkRegion
                {
                    OwnerId = ownerInfo.SolidId,
                    HostFaceId = fallbackHostFaceId,
                    Axis = region.DepthAxis,
                    HostCoordinate = fallbackCoordinate,
                    Bounds = ownerRegionBounds,
                    HostBounds = ownerInfo.Bounds.Value,
                    IsTemplatePromotion = true
                });
                acceptedAny = true;
                acceptedCount++;
                if (acceptedCount >= maxHostFallbackRegions)
                    return true;
            }

            if (acceptedAny)
                return true;

            foreach (SolidInfo ownerInfo in context.SolidInfo.Values)
            {
                if (!ownerInfo.Bounds.HasValue)
                    continue;

                if (OwnerLooksLikeDiscreteConnectorPinOrPad(ownerInfo, modelBounds))
                {
                    result.ProtectedRejectCount++;
                    continue;
                }

                int fallbackHostFaceId = ownerInfo.FaceIds.FirstOrDefault(faceId =>
                    !HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, options));
                if (fallbackHostFaceId <= 0)
                    continue;

                Bounds regionBounds = CreateProjectionRegionBounds(region, modelBounds, options);
                if (ProjectionRegionContainsProtectedContactFace(
                    context.Data,
                    ownerInfo,
                    context.StyledByTarget,
                    regionBounds,
                    region.DepthAxis,
                    options))
                {
                    result.ProtectedRejectCount++;
                    continue;
                }

                double fallbackCoordinate = region.DepthSign >= 0
                    ? modelBounds.Max.Get(region.DepthAxis)
                    : modelBounds.Min.Get(region.DepthAxis);
                result.CandidateCount++;
                result.Regions.Add(new AutomaticWatermarkRegion
                {
                    OwnerId = ownerInfo.SolidId,
                    HostFaceId = fallbackHostFaceId,
                    Axis = region.DepthAxis,
                    HostCoordinate = fallbackCoordinate,
                    Bounds = regionBounds,
                    HostBounds = modelBounds,
                    IsTemplatePromotion = true
                });
                acceptedAny = true;
                acceptedCount++;
                if (acceptedCount >= maxHostFallbackRegions)
                    return true;
            }

            return acceptedAny;
        }

        private static bool ExistingCleanupAlreadyCoversProjectionPromotion(
            List<AutomaticWatermarkRegion> existingRegions,
            Dictionary<int, HashSet<int>> existingHostFaceBounds,
            List<int> existingFaceIds,
            int hostFaceId,
            int axis,
            HashSet<int> selectedBoundIds,
            List<int> selectedFaceIds,
            Bounds acceptedBounds,
            StepWatermarkCleanerOptions options)
        {
            if (selectedBoundIds.Count == 0 && selectedFaceIds.Count == 0)
                return false;

            if (existingRegions.Any(region =>
                region.HostFaceId == hostFaceId &&
                region.Axis == axis &&
                ProjectionIntersects(region.Bounds, acceptedBounds, axis, options.HostPlaneProjectionPadding)))
            {
                return true;
            }

            bool hasNewBound = selectedBoundIds.Any(boundId =>
                !existingHostFaceBounds.TryGetValue(hostFaceId, out HashSet<int> existingBounds) ||
                !existingBounds.Contains(boundId));
            bool hasNewFace = selectedFaceIds.Any(faceId => !existingFaceIds.Contains(faceId));
            if (hasNewBound || hasNewFace)
                return false;

            return true;
        }

        private static List<int> FindProjectionRegionShallowFaces(
            CleanupContext context,
            SolidInfo ownerInfo,
            int hostFaceId,
            int hostAxis,
            double hostCoordinate,
            StepWatermarkMarkedRegion region,
            Bounds regionBounds,
            Bounds hostBounds,
            bool hasHostLoopTopology,
            bool requireTemplateOrSmallMarkFace,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<int>();
            double maxDepth = Math.Max(
                Math.Max(options.HostPlaneSearchDistance, options.HostLoopAdjacentMaxDepth),
                options.EmbeddedReliefMaxDepth) * 2.0;

            foreach (int faceId in ownerInfo.FaceIds)
            {
                if (faceId == hostFaceId)
                    continue;

                Bounds? faceBounds = context.Data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (!ProjectedBoundsInside(faceBounds.Value, regionBounds, hostAxis, options.HostPlaneProjectionPadding))
                    continue;

                double minDistance = Math.Abs(faceBounds.Value.Min.Get(hostAxis) - hostCoordinate);
                double maxDistance = Math.Abs(faceBounds.Value.Max.Get(hostAxis) - hostCoordinate);
                if (Math.Max(minDistance, maxDistance) > maxDepth)
                    continue;

                if (HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, options) ||
                    IsCylindricalFace(context.Data, faceId))
                    continue;

                if (requireTemplateOrSmallMarkFace &&
                    !LooksLikeTemplatePromotionFaceColor(faceId, context.StyledByTarget, options))
                {
                    continue;
                }

                result.Add(faceId);
            }

            return result;
        }

        private static List<int> FindProjectionRegionContainedTemplateFaces(
            CleanupContext context,
            SolidInfo ownerInfo,
            int hostFaceId,
            int hostAxis,
            Bounds regionBounds,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<int>();
            foreach (int faceId in ownerInfo.FaceIds)
            {
                if (faceId == hostFaceId)
                    continue;

                if (context.Data.GetTypeName(faceId) != "ADVANCED_FACE")
                    continue;

                Bounds? faceBounds = context.Data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (!ProjectedBoundsInside(faceBounds.Value, regionBounds, hostAxis, options.HostPlaneProjectionPadding) ||
                    !BoundsInsideDetectionVolume(faceBounds.Value, regionBounds, 0.006))
                {
                    continue;
                }

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                bool protectedColor = HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, options);
                bool templateColor = LooksLikeTemplatePromotionFaceColor(faceId, context.StyledByTarget, options);
                if (IsCylindricalFace(context.Data, faceId) ||
                    !templateColor ||
                    protectedColor)
                {
                    continue;
                }

                result.Add(faceId);
            }

            return result;
        }

        private static bool LooksLikeTemplatePromotionFaceColor(
            int faceId,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            if (!styledByTarget.TryGetValue(faceId, out List<StyledItemInfo> styles))
                return false;

            foreach (var style in styles)
            {
                if (!style.Color.HasValue)
                    continue;

                if (LooksLikeTextMarkColor(style.Color, options) ||
                    IsEmbeddedWatermarkColor(style.Color.Value, options) ||
                    IsStandaloneWatermarkColor(style.Color.Value, options))
                    return true;
            }

            return false;
        }

        private static Bounds CreateProjectionRegionBounds(
            StepWatermarkMarkedRegion region,
            Bounds hostBounds,
            StepWatermarkCleanerOptions options)
        {
            double[] min = { hostBounds.Min.X, hostBounds.Min.Y, hostBounds.Min.Z };
            double[] max = { hostBounds.Max.X, hostBounds.Max.Y, hostBounds.Max.Z };
            double padding = region.ScalePixelsPerModelUnit > 0.0
                ? options.MarkedRegionPaddingPixels / region.ScalePixelsPerModelUnit
                : 0.0;
            bool runtimeTemplateRegion =
                (region.SourceMarkerPath ?? string.Empty).StartsWith("runtime-template:", StringComparison.OrdinalIgnoreCase) ||
                (region.SourceProjectionPath ?? string.Empty).StartsWith("runtime-template:", StringComparison.OrdinalIgnoreCase);
            SetProjectionRegionAxisRange(
                min,
                max,
                region.UAxis,
                runtimeTemplateRegion ? region.ModelUMin - padding : (region.ModelUMin - padding) * region.USign,
                runtimeTemplateRegion ? region.ModelUMax + padding : (region.ModelUMax + padding) * region.USign);
            SetProjectionRegionAxisRange(
                min,
                max,
                region.VAxis,
                runtimeTemplateRegion ? region.ModelVMin - padding : (region.ModelVMin - padding) * region.VSign,
                runtimeTemplateRegion ? region.ModelVMax + padding : (region.ModelVMax + padding) * region.VSign);
            SetProjectionRegionAxisRange(min, max, region.DepthAxis, hostBounds.Min.Get(region.DepthAxis), hostBounds.Max.Get(region.DepthAxis));

            var bounds = new Bounds();
            bounds.Include(new Vec3d(min[0], min[1], min[2]));
            bounds.Include(new Vec3d(max[0], max[1], max[2]));
            return bounds;
        }

        private static bool IsRuntimeTemplateRegion(StepWatermarkMarkedRegion region)
        {
            if (region == null)
                return false;

            return (region.SourceMarkerPath ?? string.Empty).StartsWith("runtime-template:", StringComparison.OrdinalIgnoreCase) ||
                (region.SourceProjectionPath ?? string.Empty).StartsWith("runtime-template:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryCreateResidualProjectionRegionBounds(
            StepData data,
            StepWatermarkMarkedRegion region,
            Bounds modelBounds,
            StepWatermarkCleanerOptions options,
            out Bounds bounds)
        {
            if (TryFindResidualProjectionHostBounds(data, region, modelBounds, options, out Bounds hostBounds))
            {
                bounds = CreateProjectionRegionBounds(region, hostBounds, options);
                return true;
            }

            bounds = default;
            return false;
        }

        private static bool TryFindResidualProjectionHostBounds(
            StepData data,
            StepWatermarkMarkedRegion region,
            Bounds modelBounds,
            StepWatermarkCleanerOptions options,
            out Bounds hostBounds)
        {
            hostBounds = default;
            if (data == null || region == null || !HasMarkedRegionArea(region))
                return false;

            int axis = region.DepthAxis;
            double modelSideCoordinate = region.DepthSign >= 0
                ? modelBounds.Max.Get(axis)
                : modelBounds.Min.Get(axis);
            bool found = false;
            double bestScore = double.NegativeInfinity;

            foreach (int faceId in GetActiveAdvancedFaceIds(data))
            {
                if (!TryGetPlanarHostCandidateBounds(data, faceId, axis, options, out Bounds candidateBounds))
                    continue;

                Bounds candidateRegionBounds = CreateProjectionRegionBounds(region, candidateBounds, options);
                if (!ProjectionIntersects(candidateBounds, candidateRegionBounds, axis, options.HostPlaneProjectionPadding))
                    continue;

                double coverage = ProjectedOverlapRatio(candidateRegionBounds, candidateBounds, axis);
                if (coverage <= 0.0)
                    continue;

                double candidateCoordinate = (candidateBounds.Min.Get(axis) + candidateBounds.Max.Get(axis)) / 2.0;
                double sideDistance = Math.Abs(candidateCoordinate - modelSideCoordinate);
                double projectedArea = ProjectedArea(candidateBounds.Size, axis);
                double score =
                    coverage * 10000.0 -
                    sideDistance * 100000.0 +
                    Math.Min(projectedArea, 1000.0) * 0.01;

                if (!found || score > bestScore)
                {
                    found = true;
                    bestScore = score;
                    hostBounds = candidateBounds;
                }
            }

            return found;
        }

        private static StepWatermarkMarkedRegion CreateSignedRuntimeTemplateRegion(StepWatermarkMarkedRegion region)
        {
            return new StepWatermarkMarkedRegion
            {
                ViewName = region.ViewName,
                SourceMarkerPath = "runtime-signed-template:" + (region.TemplateName ?? string.Empty),
                SourceProjectionPath = "runtime-signed-template:" + (region.TemplateName ?? string.Empty),
                UAxis = region.UAxis,
                USign = region.USign,
                VAxis = region.VAxis,
                VSign = region.VSign,
                DepthAxis = region.DepthAxis,
                DepthSign = region.DepthSign,
                ModelUMin = region.ModelUMin,
                ModelUMax = region.ModelUMax,
                ModelVMin = region.ModelVMin,
                ModelVMax = region.ModelVMax,
                ScalePixelsPerModelUnit = region.ScalePixelsPerModelUnit,
                ImageWidth = region.ImageWidth,
                ImageHeight = region.ImageHeight,
                RectangleX = region.RectangleX,
                RectangleY = region.RectangleY,
                RectangleWidth = region.RectangleWidth,
                RectangleHeight = region.RectangleHeight,
                TemplateName = region.TemplateName,
                Kind = region.Kind,
                Score = region.Score,
                ChamferDistance = region.ChamferDistance,
                EdgePixelCount = region.EdgePixelCount
            };
        }

        private static void SetProjectionRegionAxisRange(double[] min, double[] max, int axis, double value0, double value1)
        {
            min[axis] = Math.Min(value0, value1);
            max[axis] = Math.Max(value0, value1);
        }

        private static bool LooksLikeTemplateTextFaceColor(
            int faceId,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            if (!styledByTarget.TryGetValue(faceId, out List<StyledItemInfo> styles))
                return true;

            bool sawColor = false;
            foreach (var style in styles)
            {
                if (!style.Color.HasValue)
                    continue;

                sawColor = true;
                if (LooksLikeTextMarkColor(style.Color, options))
                    return true;
            }

            return !sawColor;
        }

        private static bool CanRelaxTemplateTextBoundary(
            SolidInfo ownerInfo,
            HostPlaneMatch host,
            StepWatermarkMarkedRegion textRegion)
        {
            return ownerInfo.FaceIds.Count <= 250 &&
                host.Axis == textRegion.DepthAxis;
        }

        private static List<StepWatermarkMarkedRegion> DetectTemplateTextRegions(string stepText, Bounds modelBounds)
        {
            return DetectTemplateTextLogoRegions(
                stepText,
                modelBounds,
                textOnly: true,
                requireHighConfidence: true);
        }

        private static List<StepWatermarkMarkedRegion> DetectTemplateTextLogoRegions(
            string stepText,
            Bounds modelBounds,
            bool textOnly,
            bool requireHighConfidence)
        {
            byte[] stepData = Encoding.Latin1.GetBytes(stepText);
            var projectionOptions = CreateTemplateTextProjectionOptions(StepProjectionRenderMode.EdgeVisibleRaw);
            var views = TextProjectionViews.ToList();
            var results = new ConcurrentBag<TemplateTextProjectionDetection>();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = StepProjectionRenderer.GetVectorWatermarkProjectionParallelism(views.Count)
            };

            Parallel.ForEach(views.Select((view, index) => new { View = view, Index = index }), parallelOptions, item =>
            {
                TextProjectionViewSpec view = item.View;
                StepVectorWatermarkDetectionInput vectorInput =
                    StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                        stepData,
                        "clean-text",
                        view.Name,
                        projectionOptions);
                TextProjectionMapping mapping = vectorInput.ImageMapping != null
                    ? TextProjectionMapping.Create(view, vectorInput.ImageMapping)
                    : TextProjectionMapping.Create(
                        modelBounds,
                        view,
                        projectionOptions.ImageSizePixels,
                        projectionOptions.ImageSizePixels,
                        projectionOptions.PaddingPixels);

                foreach (StepVectorWatermarkDetectionRegion vectorDetection in StepVectorWatermarkProjectionDetector.Detect(
                    vectorInput,
                    new StepTextLogoDetectionOptions { DetectArbitraryText = textOnly }))
                {
                    if (textOnly &&
                        !string.Equals(vectorDetection.Kind, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (vectorDetection.Width <= 0 || vectorDetection.Height <= 0)
                        continue;

                    if (requireHighConfidence && !IsHighConfidenceVectorTemplateTextLogoDetection(vectorDetection))
                        continue;

                    results.Add(new TemplateTextProjectionDetection
                    {
                        ViewIndex = item.Index,
                        Region = mapping.ToMarkedRegion(vectorDetection)
                    });
                }
            });

            return results
                .OrderBy(item => item.ViewIndex)
                .ThenBy(item => item.Region.RectangleX)
                .ThenBy(item => item.Region.RectangleY)
                .Select(item => item.Region)
                .ToList();
        }

        private static List<StepWatermarkMarkedRegion> PreferCombinedTemplateRegionsByView(
            IReadOnlyList<StepWatermarkMarkedRegion> regions)
        {
            if (regions == null || regions.Count == 0)
                return new List<StepWatermarkMarkedRegion>();

            var combinedViews = new HashSet<string>(
                regions
                    .Where(region => string.Equals(region.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase))
                    .Select(region => region.ViewName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            if (combinedViews.Count == 0)
                return regions.ToList();

            return regions
                .Where(region =>
                    !combinedViews.Contains(region.ViewName ?? string.Empty) ||
                    string.Equals(region.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static bool IsHighConfidenceVectorTemplateTextLogoDetection(
            StepVectorWatermarkDetectionRegion detection)
        {
            if (detection == null)
                return false;

            if (string.Equals(detection.Kind, "watermark-combined", StringComparison.OrdinalIgnoreCase))
                return detection.Score >= 45.0 && detection.PrimitiveCount >= 2;

            if (string.Equals(detection.Kind, "text", StringComparison.OrdinalIgnoreCase))
                return detection.Score >= 56.0 && detection.PrimitiveCount >= 2;

            if (string.Equals(detection.Kind, "logo", StringComparison.OrdinalIgnoreCase))
                return detection.Score >= 35.0 && detection.PrimitiveCount >= 2;

            return false;
        }

        private static StepProjectionOptions CreateTemplateTextProjectionOptions(StepProjectionRenderMode renderMode)
        {
            var options = new StepProjectionOptions
            {
                ImageSizePixels = 1600,
                PaddingPixels = 80,
                WriteMetadata = false,
                RenderMode = renderMode
            };

            foreach (TextProjectionViewSpec view in TextProjectionViews)
                options.ViewNames.Add(view.Name);

            return options;
        }

        private static bool TryFindTextProjectionView(string viewName, out TextProjectionViewSpec view)
        {
            foreach (TextProjectionViewSpec candidate in TextProjectionViews)
            {
                if (string.Equals(candidate.Name, viewName, StringComparison.OrdinalIgnoreCase))
                {
                    view = candidate;
                    return true;
                }
            }

            view = default;
            return false;
        }

        private static TextStringDetectionResult FindAutomaticTextStringFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            HashSet<int> removableSolids,
            List<int> embeddedFaces,
            List<int> coplanarFaces,
            StepWatermarkCleanerOptions options)
        {
            var candidates = new List<WatermarkFaceCandidate>();
            var excludedFaces = new HashSet<int>(embeddedFaces);
            var seenCandidateFaces = new HashSet<int>();
            foreach (int faceId in coplanarFaces)
                excludedFaces.Add(faceId);

            foreach (var styledItem in styledItems)
            {
                seenCandidateFaces.Add(styledItem.TargetId);

                if (data.GetTypeName(styledItem.TargetId) != "ADVANCED_FACE")
                    continue;

                if (excludedFaces.Contains(styledItem.TargetId))
                    continue;

                if (!LooksLikeTextMarkColor(styledItem.Color, options))
                    continue;

                if (!faceOwners.TryGetValue(styledItem.TargetId, out int ownerId))
                    continue;

                if (removableSolids.Contains(ownerId))
                    continue;

                if (!solidInfo.TryGetValue(ownerId, out var ownerInfo) || !ownerInfo.Bounds.HasValue)
                    continue;

                if (HasProtectedNonWatermarkColor(styledItem.TargetId, styledByTarget, options))
                    continue;

                var faceBounds = data.GetBounds(styledItem.TargetId);
                if (!faceBounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                var singleFace = new HashSet<int> { styledItem.TargetId };
                var host = ChooseHostPlane(
                    data,
                    ownerInfo,
                    singleFace,
                    faceBounds.Value,
                    styledByTarget,
                    allowLightHost: true,
                    options);
                if (host == null)
                    continue;

                Bounds hostBounds = ownerInfo.Bounds.Value;
                if (host.HostFaceId.HasValue)
                {
                    var detectedHostBounds = data.GetBounds(host.HostFaceId.Value);
                    if (detectedHostBounds.HasValue)
                        hostBounds = detectedHostBounds.Value;
                }

                if (TouchesProjectedBoundary(
                    faceBounds.Value,
                    hostBounds,
                    host.Axis,
                    GetAutomaticEdgeMargin(hostBounds, host.Axis) * 2.0))
                    continue;

                candidates.Add(new WatermarkFaceCandidate
                {
                    FaceId = styledItem.TargetId,
                    OwnerId = ownerId,
                    Bounds = faceBounds.Value,
                    PointCount = data.GetPointIds(styledItem.TargetId, includeSurface: false).Count,
                    Host = host,
                    HostBounds = hostBounds,
                    HasColorCue = true,
                    ColorClass = 0
                });
            }

            foreach (var faceOwner in faceOwners)
            {
                int faceId = faceOwner.Key;
                int ownerId = faceOwner.Value;
                if (seenCandidateFaces.Contains(faceId) || excludedFaces.Contains(faceId))
                    continue;

                if (data.GetTypeName(faceId) != "ADVANCED_FACE")
                    continue;

                if (removableSolids.Contains(ownerId))
                    continue;

                if (!solidInfo.TryGetValue(ownerId, out var ownerInfo) || !ownerInfo.Bounds.HasValue)
                    continue;

                if (HasProtectedNonWatermarkColor(faceId, styledByTarget, options))
                    continue;

                var faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                var singleFace = new HashSet<int> { faceId };
                var host = ChooseHostPlane(
                    data,
                    ownerInfo,
                    singleFace,
                    faceBounds.Value,
                    styledByTarget,
                    allowLightHost: true,
                    options);
                if (host == null)
                    continue;

                Bounds hostBounds = ownerInfo.Bounds.Value;
                if (host.HostFaceId.HasValue)
                {
                    var detectedHostBounds = data.GetBounds(host.HostFaceId.Value);
                    if (detectedHostBounds.HasValue)
                        hostBounds = detectedHostBounds.Value;
                }

                if (TouchesProjectedBoundary(
                    faceBounds.Value,
                    hostBounds,
                    host.Axis,
                    GetAutomaticEdgeMargin(hostBounds, host.Axis) * 2.0))
                    continue;

                candidates.Add(new WatermarkFaceCandidate
                {
                    FaceId = faceId,
                    OwnerId = ownerId,
                    Bounds = faceBounds.Value,
                    PointCount = data.GetPointIds(faceId, includeSurface: false).Count,
                    Host = host,
                    HostBounds = hostBounds,
                    HasColorCue = false,
                    ColorClass = 0
                });
            }

            var detection = new TextStringDetectionResult
            {
                CandidateCount = candidates.Count
            };
            if (candidates.Count < options.CleanTextMinCandidateFaceCount)
                return detection;

            var textFaceIds = new HashSet<int>();
            foreach (var group in candidates.GroupBy(candidate => new
            {
                Axis = candidate.Host.Axis
            }))
            {
                var groupCandidates = group.ToList();
                double gap = GetAutomaticClusterGap(groupCandidates[0].HostBounds, group.Key.Axis, options);
                bool acceptedAnyCluster = false;

                foreach (var cluster in BuildProjectedCandidateClusters(groupCandidates, group.Key.Axis, gap))
                {
                    if (!LooksLikeTextStringCluster(cluster, group.Key.Axis, options))
                        continue;

                    acceptedAnyCluster = true;
                    detection.ClusterCount++;
                    foreach (var candidate in cluster)
                        textFaceIds.Add(candidate.FaceId);
                }

                if (!acceptedAnyCluster)
                {
                    foreach (var textBand in SelectTextStringBands(groupCandidates, group.Key.Axis, options))
                    {
                        detection.ClusterCount++;
                        foreach (var candidate in textBand)
                            textFaceIds.Add(candidate.FaceId);
                    }
                }
            }

            detection.FaceIds = textFaceIds
                .OrderBy(id => id)
                .ToList();
            return detection;
        }

        private static bool LooksLikeTextMarkColor(
            StepColor? color,
            StepWatermarkCleanerOptions options)
        {
            if (!color.HasValue)
                return true;

            return color.Value.ChannelSpread <= options.NeutralMaxChannelSpread &&
                color.Value.Luminance > options.DarkWatermarkMaxLuminance &&
                color.Value.Luminance <= options.NeutralBodyMaxLuminance;
        }

        private static bool LooksLikeTextStringCluster(
            List<WatermarkFaceCandidate> cluster,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            if (cluster.Count < 5)
                return false;

            int pointCount = cluster.Sum(candidate => candidate.PointCount);
            if (pointCount < 24)
                return false;

            Bounds clusterBounds = new Bounds();
            foreach (var candidate in cluster)
                clusterBounds.Include(candidate.Bounds);

            Bounds hostBounds = cluster[0].HostBounds;
            if (!LooksLikeSmallMark(clusterBounds, hostBounds, options))
                return false;

            if (TouchesProjectedBoundary(
                clusterBounds,
                hostBounds,
                axis,
                GetAutomaticEdgeMargin(hostBounds, axis) * 2.0))
                return false;

            int uAxis;
            int vAxis;
            GetProjectedAxes(axis, out uAxis, out vAxis);

            double width = Math.Abs(clusterBounds.Size.Get(uAxis));
            double height = Math.Abs(clusterBounds.Size.Get(vAxis));
            if (width <= 0.000001 || height <= 0.000001)
                return false;

            double hostWidth = Math.Max(Math.Abs(hostBounds.Size.Get(uAxis)), 0.000001);
            double hostHeight = Math.Max(Math.Abs(hostBounds.Size.Get(vAxis)), 0.000001);
            double widthRatio = width / hostWidth;
            double heightRatio = height / hostHeight;
            double areaRatio = (width * height) / Math.Max(hostWidth * hostHeight, 0.000001);
            if (widthRatio > 0.65 || heightRatio > 0.65 || areaRatio > 0.16)
                return false;

            double aspect = width >= height ? width / height : height / width;
            if (aspect < 1.35 || aspect > 12.0)
                return false;

            var componentBounds = cluster.Select(candidate => candidate.Bounds).ToList();
            int columnCount = CountProjectedBands(componentBounds, uAxis, Math.Max(width * 0.06, 0.000001));
            int rowCount = CountProjectedBands(componentBounds, vAxis, Math.Max(height * 0.10, 0.000001));

            bool horizontalString = columnCount >= 4 && rowCount <= 3;
            bool verticalString = rowCount >= 4 && columnCount <= 3;
            bool stackedString = columnCount >= 4 && rowCount >= 2 && rowCount <= 5 && aspect <= 4.0;

            return horizontalString || verticalString || stackedString;
        }

        private static List<List<WatermarkFaceCandidate>> SelectTextStringBands(
            List<WatermarkFaceCandidate> group,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<List<WatermarkFaceCandidate>>();
            if (group.Count < options.CleanTextMinCandidateFaceCount)
                return result;

            int pointCount = group.Sum(candidate => candidate.PointCount);
            if (pointCount < options.AutomaticClusterMinPointCount * 4)
                return result;

            int uAxis;
            int vAxis;
            GetProjectedAxes(axis, out uAxis, out vAxis);
            AddTextStringBandsForAxes(group, axis, uAxis, vAxis, options, result);
            AddTextStringBandsForAxes(group, axis, vAxis, uAxis, options, result);

            var seen = new HashSet<int>();
            var unique = new List<List<WatermarkFaceCandidate>>();
            foreach (var band in result
                .OrderByDescending(candidateBand => candidateBand.Count)
                .ThenByDescending(candidateBand => candidateBand.Sum(candidate => candidate.PointCount)))
            {
                var selected = band
                    .Where(candidate => seen.Add(candidate.FaceId))
                    .ToList();
                if (selected.Count >= 5)
                    unique.Add(selected);
            }

            return unique;
        }

        private static void AddTextStringBandsForAxes(
            List<WatermarkFaceCandidate> group,
            int hostAxis,
            int narrowAxis,
            int stringAxis,
            StepWatermarkCleanerOptions options,
            List<List<WatermarkFaceCandidate>> result)
        {
            Bounds groupBounds = new Bounds();
            foreach (var candidate in group)
                groupBounds.Include(candidate.Bounds);

            double narrowSpan = Math.Abs(groupBounds.Size.Get(narrowAxis));
            if (narrowSpan <= 0.000001)
                return;

            double bandGap = Math.Max(narrowSpan * 0.035, 0.000001);
            var bands = new List<ProjectedClusterBand>();
            foreach (var candidate in group.OrderBy(candidate => candidate.Bounds.Min.Get(narrowAxis)))
            {
                double min = candidate.Bounds.Min.Get(narrowAxis);
                double max = candidate.Bounds.Max.Get(narrowAxis);
                ProjectedClusterBand band = bands.FirstOrDefault(existing => min <= existing.Max + bandGap && max >= existing.Min - bandGap);
                if (band == null)
                {
                    band = new ProjectedClusterBand
                    {
                        Min = min,
                        Max = max,
                        VMin = candidate.Bounds.Min.Get(stringAxis),
                        VMax = candidate.Bounds.Max.Get(stringAxis)
                    };
                    bands.Add(band);
                }

                band.Min = Math.Min(band.Min, min);
                band.Max = Math.Max(band.Max, max);
                band.VMin = Math.Min(band.VMin, candidate.Bounds.Min.Get(stringAxis));
                band.VMax = Math.Max(band.VMax, candidate.Bounds.Max.Get(stringAxis));
                band.Candidates.Add(candidate);
                band.Score += Math.Max(candidate.PointCount, 1);
            }

            foreach (var band in bands)
            {
                if (band.Candidates.Count < 5 || band.Score < options.AutomaticClusterMinPointCount)
                    continue;

                double bandNarrowSpan = Math.Max(band.Max - band.Min, 0.000001);
                double bandStringSpan = Math.Max(band.VMax - band.VMin, 0.000001);
                double aspect = bandStringSpan / bandNarrowSpan;
                if (aspect < 1.35 || aspect > 18.0)
                    continue;

                var bandBounds = new Bounds();
                foreach (var candidate in band.Candidates)
                    bandBounds.Include(candidate.Bounds);

                Bounds hostBounds = band.Candidates[0].HostBounds;
                if (!LooksLikeSmallMark(bandBounds, hostBounds, options))
                    continue;

                if (TouchesProjectedBoundary(
                    bandBounds,
                    hostBounds,
                    hostAxis,
                    GetAutomaticEdgeMargin(hostBounds, hostAxis) * 2.0))
                    continue;

                var componentBounds = band.Candidates.Select(candidate => candidate.Bounds).ToList();
                int narrowBandCount = CountProjectedBands(componentBounds, narrowAxis, Math.Max(bandNarrowSpan * 0.10, 0.000001));
                int stringBandCount = CountProjectedBands(componentBounds, stringAxis, Math.Max(bandStringSpan * 0.045, 0.000001));
                bool denseElongatedString = band.Candidates.Count >= options.CleanTextMinCandidateFaceCount &&
                    narrowBandCount <= 2 &&
                    aspect >= 2.0;
                if (stringBandCount < 4 || narrowBandCount > 4)
                {
                    if (!denseElongatedString)
                        continue;
                }

                result.Add(band.Candidates.ToList());
            }
        }

        private static void AddCoplanarCompanionFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            HashSet<int> result,
            List<WatermarkFaceCandidate> cluster,
            int planarAxis,
            double gap,
            StepWatermarkCleanerOptions options)
        {
            if (cluster.Count == 0)
                return;

            int ownerId = cluster[0].OwnerId;
            double coordinate = cluster[0].Host.TargetCoordinate;
            Bounds clusterBounds = new Bounds();
            foreach (var candidate in cluster)
                clusterBounds.Include(candidate.Bounds);

            foreach (var styledItem in styledItems)
            {
                if (result.Contains(styledItem.TargetId))
                    continue;

                if (data.GetTypeName(styledItem.TargetId) != "ADVANCED_FACE")
                    continue;

                if (!faceOwners.TryGetValue(styledItem.TargetId, out int faceOwnerId) || faceOwnerId != ownerId)
                    continue;

                if (!solidInfo.TryGetValue(ownerId, out var ownerInfo) || !ownerInfo.Bounds.HasValue)
                    continue;

                var faceBounds = data.GetBounds(styledItem.TargetId);
                if (!faceBounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                if (FindPlanarAxis(faceBounds.Value, options) != planarAxis)
                    continue;

                double faceCoordinate = (faceBounds.Value.Min.Get(planarAxis) + faceBounds.Value.Max.Get(planarAxis)) / 2.0;
                if (Math.Abs(faceCoordinate - coordinate) > Math.Max(options.PlaneTolerance, 0.000001))
                    continue;

                if (!ProjectionIntersects(faceBounds.Value, clusterBounds, planarAxis, gap))
                    continue;

                if (!LooksLikeKnownWatermarkPatternComponent(faceBounds.Value, ownerInfo.Bounds.Value, planarAxis))
                    continue;

                result.Add(styledItem.TargetId);
            }
        }

        private static bool LooksLikeAutomaticRegionWatermarkFaceColor(
            StepColor? color,
            StepWatermarkCleanerOptions options)
        {
            if (!color.HasValue)
                return true;

            if (IsEmbeddedWatermarkColor(color.Value, options))
                return true;

            return color.Value.Luminance <= options.NeutralBodyMaxLuminance &&
                color.Value.ChannelSpread <= options.NeutralMaxChannelSpread;
        }

        private static bool HasProtectedNonWatermarkColor(
            int faceId,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            if (!styledByTarget.TryGetValue(faceId, out var styles))
                return false;

            foreach (var style in styles)
            {
                if (!style.Color.HasValue)
                    continue;

                StepColor color = style.Color.Value;
                if (IsStandaloneWatermarkColor(color, options) || IsEmbeddedWatermarkColor(color, options))
                    continue;

                if (color.ChannelSpread > options.NeutralMaxChannelSpread)
                    return true;
            }

            return false;
        }

        private static bool HasProtectedCoplanarHostFace(
            StepData data,
            int candidateFaceId,
            Bounds candidateBounds,
            int planarAxis,
            SolidInfo ownerInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            double tolerance = Math.Max(options.PlaneTolerance, 0.000001);
            double candidateCoordinate = (candidateBounds.Min.Get(planarAxis) + candidateBounds.Max.Get(planarAxis)) / 2.0;

            foreach (int faceId in ownerInfo.FaceIds)
            {
                if (faceId == candidateFaceId)
                    continue;

                if (!HasProtectedNonWatermarkColor(faceId, styledByTarget, options))
                    continue;

                var faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                int faceAxis = FindPlanarAxis(faceBounds.Value, options);
                if (faceAxis != planarAxis)
                    continue;

                double faceCoordinate = (faceBounds.Value.Min.Get(planarAxis) + faceBounds.Value.Max.Get(planarAxis)) / 2.0;
                if (Math.Abs(faceCoordinate - candidateCoordinate) > tolerance)
                    continue;

                if (!ProjectionIntersects(faceBounds.Value, candidateBounds, planarAxis, options.HostPlaneProjectionPadding))
                    continue;

                return true;
            }

            return false;
        }

        private static bool LooksLikeAutomaticHostFace(
            int faceId,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            if (!styledByTarget.TryGetValue(faceId, out var styles))
                return true;

            var color = styles.FirstOrDefault(style => style.Color.HasValue)?.Color;
            if (!color.HasValue)
                return true;

            return !IsWatermarkColor(color.Value, options);
        }

        private static bool LooksLikeRemovableSolidHostFace(
            int faceId,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            return LooksLikePotentialAutomaticHostFace(faceId, styledByTarget, options);
        }

        private static bool LooksLikePotentialAutomaticHostFace(
            int faceId,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            if (HasProtectedNonWatermarkColor(faceId, styledByTarget, options))
                return false;

            if (!styledByTarget.TryGetValue(faceId, out var styles))
                return true;

            foreach (var style in styles)
            {
                if (!style.Color.HasValue)
                    continue;

                if (style.Color.Value.ChannelSpread <= options.NeutralMaxChannelSpread)
                    return true;
            }

            return false;
        }

        private static bool IsLightNeutralHostFace(
            int faceId,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            if (!styledByTarget.TryGetValue(faceId, out var styles))
                return false;

            var color = styles.FirstOrDefault(style => style.Color.HasValue)?.Color;
            return color.HasValue &&
                !IsWatermarkColor(color.Value, options) &&
                color.Value.Luminance > options.BodyMaxLuminance &&
                color.Value.Luminance <= options.NeutralBodyMaxLuminance;
        }

        private static List<List<WatermarkLoopCandidate>> BuildProjectedLoopClusters(
            List<WatermarkLoopCandidate> candidates,
            int axis,
            double gap)
        {
            var result = new List<List<WatermarkLoopCandidate>>();
            var visited = new bool[candidates.Count];

            for (int i = 0; i < candidates.Count; i++)
            {
                if (visited[i])
                    continue;

                var cluster = new List<WatermarkLoopCandidate>();
                var queue = new Queue<int>();
                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    var current = candidates[index];
                    cluster.Add(current);

                    for (int j = 0; j < candidates.Count; j++)
                    {
                        if (visited[j])
                            continue;

                        if (!ProjectedBoundsOverlap(current.Bounds, candidates[j].Bounds, axis, gap))
                            continue;

                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }

                result.Add(cluster);
            }

            return result;
        }

        private static bool LooksLikeAutomaticWatermarkLoopCluster(
            List<WatermarkLoopCandidate> cluster,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            if (cluster.Count == 0)
                return false;

            int pointCount = cluster.Sum(candidate => candidate.PointCount);
            int minLoopCount = Math.Max(options.AutomaticClusterMinFaceCount, 5);

            Bounds clusterBounds = new Bounds();
            foreach (var candidate in cluster)
                clusterBounds.Include(candidate.Bounds);

            var hostBounds = cluster[0].HostBounds;
            if (!LooksLikeSmallMark(clusterBounds, hostBounds, options))
                return false;

            bool compactEngravedCandidate =
                cluster.Count >= 3 &&
                pointCount >= 30 &&
                LooksLikeCompactEngravedWordPattern(clusterBounds, hostBounds, axis);
            bool requiresCompactEngravedCluster = cluster.Any(candidate => candidate.RequireCompactEngravedCluster);
            var componentBounds = cluster.Select(candidate => candidate.Bounds).ToList();
            int uAxis;
            int vAxis;
            GetProjectedAxes(axis, out uAxis, out vAxis);
            int columnCount = CountProjectedBands(
                componentBounds,
                uAxis,
                Math.Max(Math.Abs(clusterBounds.Size.Get(uAxis)) * 0.08, 0.000001));
            int rowCount = CountProjectedBands(
                componentBounds,
                vAxis,
                Math.Max(Math.Abs(clusterBounds.Size.Get(vAxis)) * 0.08, 0.000001));
            if (!compactEngravedCandidate && columnCount <= 2 && rowCount <= 2)
                return false;

            bool denseStandaloneLoop =
                cluster.Count == 1 &&
                pointCount >= 30 &&
                cluster.All(candidate => candidate.AllowStandaloneLoop) &&
                GetProjectedAspect(clusterBounds, axis) >= 1.4;

            if (cluster.Count < minLoopCount && !compactEngravedCandidate && !denseStandaloneLoop)
                return false;

            if (TouchesProjectedBoundary(clusterBounds, hostBounds, axis, GetAutomaticEdgeMargin(hostBounds, axis)) &&
                cluster.Count < minLoopCount * 2 &&
                pointCount < options.AutomaticClusterMinPointCount * 2 &&
                !compactEngravedCandidate)
                return false;

            if (requiresCompactEngravedCluster)
                return compactEngravedCandidate;

            return !options.RequireKnownWatermarkPattern ||
                compactEngravedCandidate ||
                denseStandaloneLoop ||
                LooksLikeKnownWatermarkPattern(
                    cluster.Select(candidate => candidate.Bounds),
                    clusterBounds,
                    hostBounds,
                    axis,
                    cluster.Count,
                    pointCount,
                    hasColorCue: false);
        }

        private static List<List<WatermarkFaceCandidate>> BuildProjectedCandidateClusters(
            List<WatermarkFaceCandidate> candidates,
            int axis,
            double gap)
        {
            var result = new List<List<WatermarkFaceCandidate>>();
            var visited = new bool[candidates.Count];

            for (int i = 0; i < candidates.Count; i++)
            {
                if (visited[i])
                    continue;

                var cluster = new List<WatermarkFaceCandidate>();
                var queue = new Queue<int>();
                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    var current = candidates[index];
                    cluster.Add(current);

                    for (int j = 0; j < candidates.Count; j++)
                    {
                        if (visited[j])
                            continue;

                        if (!ProjectedBoundsOverlap(current.Bounds, candidates[j].Bounds, axis, gap))
                            continue;

                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }

                result.Add(cluster);
            }

            return result;
        }

        private static bool LooksLikeAutomaticWatermarkCluster(
            List<WatermarkFaceCandidate> cluster,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            if (cluster.Count == 0)
                return false;

            int pointCount = cluster.Sum(candidate => candidate.PointCount);
            bool hasColorCue = cluster.Any(candidate => candidate.HasColorCue);
            int minFaceCount = hasColorCue
                ? options.AutomaticClusterMinFaceCount
                : options.AutomaticClusterMinFaceCount * 3;
            int minPointCount = hasColorCue
                ? options.AutomaticClusterMinPointCount
                : options.AutomaticClusterMinPointCount * 3;

            Bounds clusterBounds = new Bounds();
            foreach (var candidate in cluster)
                clusterBounds.Include(candidate.Bounds);

            var hostBounds = cluster[0].HostBounds;
            if (!LooksLikeSmallMark(clusterBounds, hostBounds, options))
                return false;

            bool compactEngravedCandidate =
                !hasColorCue &&
                cluster.Count >= 3 &&
                pointCount >= 30 &&
                LooksLikeCompactEngravedWordPattern(clusterBounds, hostBounds, axis);

            var componentBounds = cluster.Select(candidate => candidate.Bounds).ToList();
            int uAxis;
            int vAxis;
            GetProjectedAxes(axis, out uAxis, out vAxis);
            int columnCount = CountProjectedBands(
                componentBounds,
                uAxis,
                Math.Max(Math.Abs(clusterBounds.Size.Get(uAxis)) * 0.08, 0.000001));
            int rowCount = CountProjectedBands(
                componentBounds,
                vAxis,
                Math.Max(Math.Abs(clusterBounds.Size.Get(vAxis)) * 0.08, 0.000001));
            if (!compactEngravedCandidate &&
                columnCount == 1 &&
                rowCount == 1 &&
                GetProjectedAspect(clusterBounds, axis) < 2.0)
                return false;

            if (!compactEngravedCandidate &&
                columnCount <= 2 &&
                rowCount <= 2 &&
                GetProjectedAspect(clusterBounds, axis) < 1.25)
                return false;

            if (!compactEngravedCandidate &&
                cluster.Count < minFaceCount &&
                pointCount < minPointCount)
                return false;

            if (TouchesProjectedBoundary(clusterBounds, hostBounds, axis, GetAutomaticEdgeMargin(hostBounds, axis)) &&
                (!hasColorCue || (cluster.Count < minFaceCount * 2 && pointCount < minPointCount * 2)))
                return false;

            return !options.RequireKnownWatermarkPattern ||
                LooksLikeKnownWatermarkPattern(
                    cluster.Select(candidate => candidate.Bounds),
                    clusterBounds,
                    hostBounds,
                    axis,
                    cluster.Count,
                    pointCount,
                    hasColorCue,
                    allowStandaloneColorPattern: cluster.Any(candidate => candidate.AllowStandaloneColorPattern),
                    restrictStandaloneColorPattern: cluster.Any(candidate => candidate.RestrictStandaloneColorPattern));
        }

        private static bool LooksLikeCompactEngravedWordPattern(
            Bounds clusterBounds,
            Bounds hostBounds,
            int excludedAxis)
        {
            int uAxis;
            int vAxis;
            GetProjectedAxes(excludedAxis, out uAxis, out vAxis);

            double width = Math.Abs(clusterBounds.Size.Get(uAxis));
            double height = Math.Abs(clusterBounds.Size.Get(vAxis));
            if (width <= 0.000001 || height <= 0.000001)
                return false;

            double hostWidth = Math.Max(Math.Abs(hostBounds.Size.Get(uAxis)), 0.000001);
            double hostHeight = Math.Max(Math.Abs(hostBounds.Size.Get(vAxis)), 0.000001);
            double widthRatio = width / hostWidth;
            double heightRatio = height / hostHeight;
            double areaRatio = (width * height) / Math.Max(hostWidth * hostHeight, 0.000001);
            double aspect = width >= height ? width / height : height / width;

            return aspect >= 2.0 &&
                aspect <= 5.0 &&
                widthRatio <= 0.45 &&
                heightRatio <= 0.45 &&
                areaRatio <= 0.08;
        }

        private static double GetProjectedAspect(Bounds bounds, int excludedAxis)
        {
            int uAxis;
            int vAxis;
            GetProjectedAxes(excludedAxis, out uAxis, out vAxis);

            double width = Math.Abs(bounds.Size.Get(uAxis));
            double height = Math.Abs(bounds.Size.Get(vAxis));
            if (width <= 0.000001 || height <= 0.000001)
                return 0.0;

            return width >= height ? width / height : height / width;
        }

        private static bool LooksLikeKnownWatermarkPattern(
            IEnumerable<Bounds> componentBounds,
            Bounds clusterBounds,
            Bounds hostBounds,
            int excludedAxis,
            int componentCount,
            int pointCount,
            bool hasColorCue,
            bool allowStandaloneColorPattern = false,
            bool restrictStandaloneColorPattern = false)
        {
            var components = componentBounds.ToList();
            if (components.Count == 0 || componentCount <= 0)
                return false;

            int uAxis;
            int vAxis;
            GetProjectedAxes(excludedAxis, out uAxis, out vAxis);

            double width = Math.Abs(clusterBounds.Size.Get(uAxis));
            double height = Math.Abs(clusterBounds.Size.Get(vAxis));
            if (width <= 0.000001 || height <= 0.000001)
                return false;

            double hostWidth = Math.Max(Math.Abs(hostBounds.Size.Get(uAxis)), 0.000001);
            double hostHeight = Math.Max(Math.Abs(hostBounds.Size.Get(vAxis)), 0.000001);
            double widthRatio = width / hostWidth;
            double heightRatio = height / hostHeight;
            double areaRatio = (width * height) / Math.Max(hostWidth * hostHeight, 0.000001);

            double maxWidthRatio = hasColorCue ? 0.85 : 0.55;
            double maxHeightRatio = hasColorCue ? 0.65 : 0.42;
            double maxAreaRatio = hasColorCue ? 0.32 : 0.16;
            if (widthRatio > maxWidthRatio || heightRatio > maxHeightRatio || areaRatio > maxAreaRatio)
                return false;

            double aspect = width >= height ? width / height : height / width;
            int rowCount = CountProjectedBands(components, vAxis, Math.Max(height * 0.16, 0.000001));
            int columnCount = CountProjectedBands(components, uAxis, Math.Max(width * 0.045, 0.000001));

            bool colorCuedKnownPattern =
                allowStandaloneColorPattern &&
                hasColorCue &&
                pointCount >= 20 &&
                aspect <= 18.0 &&
                rowCount <= 4 &&
                (!restrictStandaloneColorPattern ||
                    componentCount >= 5 ||
                    columnCount >= 4 ||
                    rowCount >= 2);

            if (colorCuedKnownPattern)
                return true;

            bool connectedColorCuedPattern =
                hasColorCue &&
                componentCount >= 8 &&
                pointCount >= 50 &&
                aspect >= 1.05 &&
                aspect <= 5.0 &&
                widthRatio <= 0.45 &&
                heightRatio <= 0.65 &&
                areaRatio <= 0.16;

            if (connectedColorCuedPattern)
                return true;

            bool textLike =
                componentCount >= 5 &&
                pointCount >= 18 &&
                aspect >= 1.05 &&
                aspect <= 9.0 &&
                columnCount >= 5 &&
                rowCount <= 3;

            if (textLike)
                return true;

            bool verticalTextLike =
                componentCount >= 5 &&
                pointCount >= 18 &&
                aspect >= 1.05 &&
                aspect <= 9.0 &&
                rowCount >= 5 &&
                columnCount <= 3;

            if (verticalTextLike)
                return true;

            bool engravedConnectedPattern =
                !hasColorCue &&
                componentCount >= 8 &&
                pointCount >= 100 &&
                aspect >= 1.05 &&
                aspect <= 3.6 &&
                rowCount <= 2 &&
                columnCount <= 3 &&
                widthRatio <= 0.35 &&
                heightRatio <= 0.35 &&
                areaRatio <= 0.10;

            if (engravedConnectedPattern)
                return true;

            bool engravedSingleWordPattern =
                !hasColorCue &&
                componentCount >= 5 &&
                pointCount >= 100 &&
                aspect >= 2.0 &&
                aspect <= 7.0 &&
                rowCount <= 2 &&
                columnCount >= 3 &&
                widthRatio <= 0.65 &&
                heightRatio <= 0.22 &&
                areaRatio <= 0.10;

            if (engravedSingleWordPattern)
                return true;

            bool compactEngravedWordPattern =
                !hasColorCue &&
                componentCount >= 3 &&
                pointCount >= 30 &&
                aspect >= 2.0 &&
                aspect <= 5.0 &&
                widthRatio <= 0.45 &&
                heightRatio <= 0.45 &&
                areaRatio <= 0.08;

            if (compactEngravedWordPattern)
                return true;

            bool stackedTextLike =
                componentCount >= 8 &&
                pointCount >= 28 &&
                aspect >= 0.85 &&
                aspect <= 3.2 &&
                columnCount >= 5 &&
                rowCount >= 2 &&
                rowCount <= 4;

            if (stackedTextLike)
                return true;

            bool logoLike =
                componentCount >= 3 &&
                pointCount >= (hasColorCue ? 20 : 32) &&
                aspect >= 1.25 &&
                aspect <= 2.8 &&
                columnCount >= 2 &&
                rowCount >= 2 &&
                widthRatio <= 0.35 &&
                heightRatio <= 0.35 &&
                areaRatio <= 0.08;

            return logoLike;
        }

        private static bool LooksLikeKnownWatermarkPatternComponent(
            Bounds componentBounds,
            Bounds hostBounds,
            int excludedAxis)
        {
            int uAxis;
            int vAxis;
            GetProjectedAxes(excludedAxis, out uAxis, out vAxis);

            double width = Math.Abs(componentBounds.Size.Get(uAxis));
            double height = Math.Abs(componentBounds.Size.Get(vAxis));
            if (width <= 0.000001 || height <= 0.000001)
                return false;

            double hostWidth = Math.Max(Math.Abs(hostBounds.Size.Get(uAxis)), 0.000001);
            double hostHeight = Math.Max(Math.Abs(hostBounds.Size.Get(vAxis)), 0.000001);
            double widthRatio = width / hostWidth;
            double heightRatio = height / hostHeight;
            double areaRatio = (width * height) / Math.Max(hostWidth * hostHeight, 0.000001);

            if (widthRatio > 0.38 || heightRatio > 0.38 || areaRatio > 0.06)
                return false;

            double aspect = width >= height ? width / height : height / width;
            return aspect <= 18.0;
        }

        private static void GetProjectedAxes(int excludedAxis, out int uAxis, out int vAxis)
        {
            uAxis = -1;
            vAxis = -1;
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == excludedAxis)
                    continue;

                if (uAxis < 0)
                    uAxis = axis;
                else
                    vAxis = axis;
            }

            if (vAxis < 0)
            {
                uAxis = 0;
                vAxis = 1;
            }
        }

        private static int CountProjectedBands(List<Bounds> components, int axis, double gap)
        {
            var intervals = new List<Tuple<double, double>>();
            foreach (Bounds bounds in components)
            {
                double min = bounds.Min.Get(axis);
                double max = bounds.Max.Get(axis);
                if (max < min)
                {
                    double temp = min;
                    min = max;
                    max = temp;
                }

                intervals.Add(Tuple.Create(min, max));
            }

            intervals.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            int count = 0;
            double currentMax = double.NegativeInfinity;
            foreach (var interval in intervals)
            {
                if (count == 0 || interval.Item1 > currentMax + gap)
                {
                    count++;
                    currentMax = interval.Item2;
                }
                else if (interval.Item2 > currentMax)
                {
                    currentMax = interval.Item2;
                }
            }

            return count;
        }

        private static double GetAutomaticClusterGap(Bounds hostBounds, int axis, StepWatermarkCleanerOptions options)
        {
            double minProjectedSize = double.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                if (i == axis)
                    continue;

                minProjectedSize = Math.Min(minProjectedSize, hostBounds.Size.Get(i));
            }

            if (double.IsInfinity(minProjectedSize) || minProjectedSize <= 0.0)
                minProjectedSize = 1.0;

            return Math.Max(options.HostPlaneProjectionPadding * 2.0, minProjectedSize * options.AutomaticClusterGapRatio);
        }

        private static double GetAutomaticEdgeMargin(Bounds hostBounds, int axis)
        {
            double minProjectedSize = double.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                if (i == axis)
                    continue;

                minProjectedSize = Math.Min(minProjectedSize, hostBounds.Size.Get(i));
            }

            if (double.IsInfinity(minProjectedSize) || minProjectedSize <= 0.0)
                return 0.0;

            return minProjectedSize * 0.01;
        }

        private static FlattenResult FlattenEmbeddedWatermarkFaces(
            StepData data,
            List<int> embeddedFaces,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options,
            Dictionary<int, string> edits)
        {
            var result = new FlattenResult();
            var editedPoints = new HashSet<int>();

            foreach (var ownerGroup in embeddedFaces.GroupBy(faceId => faceOwners[faceId]))
            {
                int solidId = ownerGroup.Key;
                if (!solidInfo.TryGetValue(solidId, out var ownerInfo))
                    continue;

                var components = BuildFaceComponents(data, ownerGroup.ToList());
                foreach (var componentFaces in components)
                {
                    var removableComponentFaces = new HashSet<int>(
                        componentFaces.Where(faceId => !HasProtectedNonWatermarkColor(faceId, styledByTarget, options)));
                    if (removableComponentFaces.Count == 0)
                        continue;

                    var componentPointIds = new HashSet<int>();
                    foreach (int faceId in removableComponentFaces)
                    {
                        foreach (int pointId in data.GetPointIds(faceId, includeSurface: false))
                            componentPointIds.Add(pointId);
                    }

                    var componentBounds = data.GetBoundsFromPointIds(componentPointIds);
                    if (!componentBounds.HasValue)
                        continue;

                    bool allowLightHost = ComponentHasDarkWatermarkFace(removableComponentFaces, styledByTarget, options);
                    var host = ChooseHostPlane(data, ownerInfo, removableComponentFaces, componentBounds.Value, styledByTarget, allowLightHost, options);
                    if (host == null)
                        continue;

                    if (host.HostFaceId.HasValue)
                    {
                        var hostBounds = data.GetBounds(host.HostFaceId.Value);
                        if (hostBounds.HasValue &&
                            TouchesProjectedBoundary(
                                componentBounds.Value,
                                hostBounds.Value,
                                host.Axis,
                                GetAutomaticEdgeMargin(hostBounds.Value, host.Axis)))
                            continue;
                    }

                    var hostBoundsToRemoveByFace = FindMatchingHostFaceBoundsInsideDetectedRegion(
                        data,
                        ownerInfo,
                        componentBounds.Value,
                        host.Axis,
                        options);

                    var facesToRemove = new HashSet<int>(removableComponentFaces);
                    foreach (var hostBoundGroup in hostBoundsToRemoveByFace)
                    {
                        int loopHostFaceId = hostBoundGroup.Key;
                        var loopHost = new HostPlaneMatch
                        {
                            Axis = host.Axis,
                            TargetCoordinate = host.TargetCoordinate,
                            HostFaceId = loopHostFaceId
                        };
                        var hostFaceBounds = data.GetBounds(loopHostFaceId);
                        double hostEdgeMargin = hostFaceBounds.HasValue
                            ? GetAutomaticEdgeMargin(hostFaceBounds.Value, host.Axis)
                            : 0.0;
                        foreach (int adjacentFaceId in FindHostLoopAdjacentFaces(data, ownerInfo, loopHost, hostBoundGroup.Value, options, componentBounds.Value))
                        {
                            if (HasProtectedNonWatermarkColor(adjacentFaceId, styledByTarget, options))
                                continue;

                            if (!EntityInsideDetectedRegion(data, adjacentFaceId, componentBounds.Value, host.Axis, options.HostPlaneProjectionPadding))
                                continue;

                            if (hostFaceBounds.HasValue)
                            {
                                var adjacentBounds = data.GetBounds(adjacentFaceId);
                                if (adjacentBounds.HasValue &&
                                    TouchesProjectedBoundary(adjacentBounds.Value, hostFaceBounds.Value, host.Axis, hostEdgeMargin))
                                    continue;
                            }

                            facesToRemove.Add(adjacentFaceId);
                        }
                    }

                    var editPointIds = new HashSet<int>();
                    foreach (int faceId in removableComponentFaces)
                    {
                        foreach (int pointId in data.GetPointIds(faceId, includeSurface: true))
                            editPointIds.Add(pointId);
                    }

                    int changedPoints = 0;
                    foreach (int pointId in editPointIds)
                    {
                        if (!data.TryGetPoint(pointId, out var point))
                            continue;

                        double coordinate = point.Get(host.Axis);
                        if (coordinate < componentBounds.Value.Min.Get(host.Axis) - options.HostPlaneSearchDistance ||
                            coordinate > componentBounds.Value.Max.Get(host.Axis) + options.HostPlaneSearchDistance)
                            continue;

                        if (Math.Abs(coordinate - host.TargetCoordinate) <= options.PlaneTolerance)
                            continue;

                        if (!editedPoints.Add(pointId))
                            continue;

                        edits[pointId] = data.ReplacePointCoordinate(pointId, host.Axis, host.TargetCoordinate);
                        changedPoints++;
                    }

                    bool coplanarOverlay = host.HostFaceId.HasValue &&
                        IsCoplanarWithHostPlane(componentBounds.Value, host.Axis, host.TargetCoordinate, options);

                    if (changedPoints == 0)
                    {
                        if (hostBoundsToRemoveByFace.Count == 0 && !coplanarOverlay)
                            continue;
                    }

                    int addedFaceCount = 0;
                    foreach (int faceId in facesToRemove)
                    {
                        if (result.FlattenedFaces.Add(faceId))
                            addedFaceCount++;

                        if (host.HostFaceId.HasValue && faceId != host.HostFaceId.Value)
                            result.ReplacementFaceByRemovedFace[faceId] = host.HostFaceId.Value;
                    }

                    foreach (var hostBoundGroup in hostBoundsToRemoveByFace)
                    {
                        if (!result.HostFaceBoundsToRemove.TryGetValue(hostBoundGroup.Key, out var boundIds))
                        {
                            boundIds = new HashSet<int>();
                            result.HostFaceBoundsToRemove.Add(hostBoundGroup.Key, boundIds);
                        }

                        foreach (int boundId in hostBoundGroup.Value)
                            boundIds.Add(boundId);
                    }

                    result.FlattenedFaceCount += addedFaceCount;
                    result.FlattenedPointCount += changedPoints;
                    result.Operations.Add(new FlattenOperation
                    {
                        SolidId = solidId,
                        Axis = host.Axis,
                        TargetCoordinate = host.TargetCoordinate,
                        FaceCount = removableComponentFaces.Count,
                        HostFaceId = host.HostFaceId
                    });
                }
            }

            return result;
        }

        private static void FlattenCoplanarWatermarkFaces(
            StepData data,
            List<int> coplanarFaces,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            FlattenResult result,
            StepWatermarkCleanerOptions options,
            Dictionary<int, string> edits)
        {
            var editedPoints = new HashSet<int>();
            foreach (int faceId in coplanarFaces)
            {
                if (!faceOwners.TryGetValue(faceId, out int solidId))
                    continue;

                if (!solidInfo.TryGetValue(solidId, out var ownerInfo))
                    continue;

                var faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                var singleFace = new HashSet<int> { faceId };
                bool allowLightHost = ComponentHasDarkWatermarkFace(singleFace, styledByTarget, options);
                var host = ChooseHostPlane(data, ownerInfo, singleFace, faceBounds.Value, styledByTarget, allowLightHost, options);
                if (host == null)
                {
                    if (result.FlattenedFaces.Add(faceId))
                        result.FlattenedFaceCount++;

                    continue;
                }

                int changedPoints = 0;
                foreach (int pointId in data.GetPointIds(faceId, includeSurface: true))
                {
                    if (!data.TryGetPoint(pointId, out var point))
                        continue;

                    double coordinate = point.Get(host.Axis);
                    if (coordinate < faceBounds.Value.Min.Get(host.Axis) - options.HostPlaneSearchDistance ||
                        coordinate > faceBounds.Value.Max.Get(host.Axis) + options.HostPlaneSearchDistance)
                        continue;

                    if (Math.Abs(coordinate - host.TargetCoordinate) <= options.PlaneTolerance)
                        continue;

                    if (!editedPoints.Add(pointId))
                        continue;

                    edits[pointId] = data.ReplacePointCoordinate(pointId, host.Axis, host.TargetCoordinate);
                    changedPoints++;
                }

                if (result.FlattenedFaces.Add(faceId))
                    result.FlattenedFaceCount++;

                if (host.HostFaceId.HasValue && faceId != host.HostFaceId.Value)
                {
                    result.ReplacementFaceByRemovedFace[faceId] = host.HostFaceId.Value;

                    var hostBoundsToRemove = new HashSet<int>(
                        data.GetMatchingInnerFaceBounds(
                            host.HostFaceId.Value,
                            faceBounds.Value,
                            host.Axis,
                            options.HostPlaneProjectionPadding)
                            .Where(boundId => EntityInsideDetectedRegion(data, boundId, faceBounds.Value, host.Axis, options.HostPlaneProjectionPadding)));
                    foreach (int expandedBoundId in ExpandHostFaceBounds(data, host.HostFaceId.Value, hostBoundsToRemove, options))
                        hostBoundsToRemove.Add(expandedBoundId);

                    if (hostBoundsToRemove.Count > 0)
                    {
                        if (!result.HostFaceBoundsToRemove.TryGetValue(host.HostFaceId.Value, out var boundIds))
                        {
                            boundIds = new HashSet<int>();
                            result.HostFaceBoundsToRemove.Add(host.HostFaceId.Value, boundIds);
                        }

                        foreach (int boundId in hostBoundsToRemove)
                            boundIds.Add(boundId);
                    }
                }

                result.FlattenedPointCount += changedPoints;
                result.Operations.Add(new FlattenOperation
                {
                    SolidId = solidId,
                    Axis = host.Axis,
                    TargetCoordinate = host.TargetCoordinate,
                    FaceCount = 1,
                    HostFaceId = host.HostFaceId
                });
            }
        }

        private static AutomaticWatermarkLoopResult FindAutomaticEmbeddedHostLoops(
            StepData data,
            List<int> embeddedFaces,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            var groups = new Dictionary<string, EmbeddedHostLoopGroup>(StringComparer.Ordinal);
            var result = new AutomaticWatermarkLoopResult();

            foreach (int faceId in embeddedFaces)
            {
                if (!faceOwners.TryGetValue(faceId, out int ownerId))
                    continue;

                if (!solidInfo.TryGetValue(ownerId, out var ownerInfo))
                    continue;

                var faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                var singleFace = new HashSet<int> { faceId };
                bool allowLightHost = ComponentHasDarkWatermarkFace(singleFace, styledByTarget, options);
                var host = ChooseHostPlane(data, ownerInfo, singleFace, faceBounds.Value, styledByTarget, allowLightHost, options);
                if (host == null || !host.HostFaceId.HasValue)
                    continue;

                var hostBounds = data.GetBounds(host.HostFaceId.Value);
                if (!hostBounds.HasValue)
                    continue;

                string key = ownerId.ToString(CultureInfo.InvariantCulture) + "|" +
                    host.HostFaceId.Value.ToString(CultureInfo.InvariantCulture) + "|" +
                    host.Axis.ToString(CultureInfo.InvariantCulture) + "|" +
                    Math.Round(host.TargetCoordinate / Math.Max(options.PlaneTolerance, 0.000001)).ToString(CultureInfo.InvariantCulture);

                if (!groups.TryGetValue(key, out var group))
                {
                    group = new EmbeddedHostLoopGroup
                    {
                        OwnerId = ownerId,
                        HostFaceId = host.HostFaceId.Value,
                        Axis = host.Axis,
                        TargetCoordinate = host.TargetCoordinate,
                        HostBounds = hostBounds.Value
                    };
                    groups.Add(key, group);
                }

                group.Bounds.Include(faceBounds.Value);
            }

            foreach (var group in groups.Values)
            {
                if (!LooksLikeSmallMark(group.Bounds, group.HostBounds, options))
                    continue;

                double padding = Math.Max(
                    options.HostPlaneProjectionPadding,
                    GetAutomaticClusterGap(group.HostBounds, group.Axis, options) * 2.0);

                if (!solidInfo.TryGetValue(group.OwnerId, out var ownerInfo))
                    continue;

                foreach (int hostFaceId in ownerInfo.FaceIds)
                {
                    var hostBounds = data.GetBounds(hostFaceId);
                    if (!hostBounds.HasValue)
                        continue;

                    if (FindPlanarAxis(hostBounds.Value, options) != group.Axis)
                        continue;

                    double hostCoordinate = (hostBounds.Value.Min.Get(group.Axis) + hostBounds.Value.Max.Get(group.Axis)) / 2.0;
                    if (Math.Abs(hostCoordinate - group.TargetCoordinate) > options.HostPlaneSearchDistance)
                        continue;

                    if (!ProjectionIntersects(hostBounds.Value, group.Bounds, group.Axis, padding))
                        continue;

                    bool addedAnyBound = false;
                    foreach (int boundId in data.GetMatchingInnerFaceBounds(hostFaceId, group.Bounds, group.Axis, padding)
                        .Where(boundId => EntityInsideDetectedRegion(data, boundId, group.Bounds, group.Axis, padding)))
                    {
                        if (!result.HostFaceBoundsToRemove.TryGetValue(hostFaceId, out var boundIds))
                        {
                            boundIds = new HashSet<int>();
                            result.HostFaceBoundsToRemove.Add(hostFaceId, boundIds);
                        }

                        if (boundIds.Add(boundId))
                        {
                            result.CandidateCount++;
                            addedAnyBound = true;
                        }
                    }

                    if (addedAnyBound)
                    {
                        result.Regions.Add(new AutomaticWatermarkRegion
                        {
                            OwnerId = group.OwnerId,
                            HostFaceId = hostFaceId,
                            Axis = group.Axis,
                            HostCoordinate = hostCoordinate,
                            Bounds = group.Bounds,
                            HostBounds = hostBounds.Value
                        });
                    }
                }
            }

            return result;
        }

        private static bool ComponentHasDarkWatermarkFace(
            HashSet<int> componentFaces,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            foreach (int faceId in componentFaces)
            {
                if (!styledByTarget.TryGetValue(faceId, out var styles))
                    continue;

                foreach (var style in styles)
                {
                    if (style.Color.HasValue && IsDarkWatermarkColor(style.Color.Value, options))
                        return true;
                }
            }

            return false;
        }

        private static HostPlaneMatch ChooseHostPlane(
            StepData data,
            SolidInfo ownerInfo,
            HashSet<int> componentFaces,
            Bounds componentBounds,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            bool allowLightHost,
            StepWatermarkCleanerOptions options)
        {
            HostPlaneMatch best = null;

            for (int axis = 0; axis < 3; axis++)
            {
                double reliefSize = componentBounds.Size.Get(axis);
                if (reliefSize > options.EmbeddedReliefMaxDepth)
                    continue;

                var host = FindHostPlaneForAxis(data, ownerInfo, componentFaces, componentBounds, axis, styledByTarget, allowLightHost, options);
                if (host == null)
                    continue;

                if (best == null || host.Score > best.Score)
                    best = host;
            }

            return best;
        }

        private static HostPlaneMatch FindHostPlaneForAxis(
            StepData data,
            SolidInfo ownerInfo,
            HashSet<int> componentFaces,
            Bounds componentBounds,
            int axis,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            bool allowLightHost,
            StepWatermarkCleanerOptions options)
        {
            HostPlaneMatch best = null;
            List<PlanarHostCandidate> hostCandidates = ownerInfo.PlanarHostCandidatesByAxis != null &&
                axis >= 0 &&
                axis < ownerInfo.PlanarHostCandidatesByAxis.Length
                    ? ownerInfo.PlanarHostCandidatesByAxis[axis]
                    : null;
            if (hostCandidates == null)
                hostCandidates = BuildPlanarHostCandidatesForAxis(data, ownerInfo.FaceIds, axis, options);

            foreach (PlanarHostCandidate candidate in hostCandidates)
            {
                int faceId = candidate.FaceId;
                if (componentFaces.Contains(faceId))
                    continue;

                Bounds faceBounds = candidate.Bounds;
                double coordinate = candidate.Coordinate;
                double distance = Math.Min(
                    Math.Abs(coordinate - componentBounds.Min.Get(axis)),
                    Math.Abs(coordinate - componentBounds.Max.Get(axis)));

                if (distance > options.HostPlaneSearchDistance)
                    continue;

                if (!ProjectionIntersects(faceBounds, componentBounds, axis, options.HostPlaneProjectionPadding))
                    continue;

                double colorWeight = 0.25;
                if (styledByTarget.TryGetValue(faceId, out var styles))
                {
                    var color = styles.FirstOrDefault(s => s.Color.HasValue)?.Color;
                    if (color.HasValue)
                    {
                        if (!allowLightHost && IsWatermarkColor(color.Value, options))
                            continue;

                        if (allowLightHost && IsDarkWatermarkColor(color.Value, options))
                            continue;

                        if (!allowLightHost && color.Value.Luminance > options.NeutralBodyMaxLuminance)
                            continue;

                        colorWeight = 1.0 + (options.NeutralBodyMaxLuminance - color.Value.Luminance);
                    }
                }

                double score = colorWeight * candidate.ProjectedArea / Math.Max(distance, 0.0001);
                if (best == null || score > best.Score)
                {
                    best = new HostPlaneMatch
                    {
                        Axis = axis,
                        TargetCoordinate = coordinate,
                        HostFaceId = faceId,
                        Score = score
                    };
                }
            }

            return best;
        }

        private static List<PlanarHostCandidate>[] BuildPlanarHostCandidatesByAxis(
            StepData data,
            List<int> faceIds,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<PlanarHostCandidate>[3];
            for (int axis = 0; axis < result.Length; axis++)
                result[axis] = BuildPlanarHostCandidatesForAxis(data, faceIds, axis, options);

            return result;
        }

        private static List<PlanarHostCandidate> BuildPlanarHostCandidatesForAxis(
            StepData data,
            List<int> faceIds,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<PlanarHostCandidate>();
            if (faceIds == null)
                return result;

            foreach (int faceId in faceIds)
            {
                if (!TryGetPlanarHostCandidateBounds(data, faceId, axis, options, out Bounds bounds))
                    continue;

                result.Add(new PlanarHostCandidate
                {
                    FaceId = faceId,
                    Bounds = bounds,
                    Coordinate = (bounds.Min.Get(axis) + bounds.Max.Get(axis)) / 2.0,
                    ProjectedArea = ProjectedArea(bounds.Size, axis)
                });
            }

            return result;
        }

        private static bool TryGetPlanarHostCandidateBounds(
            StepData data,
            int faceId,
            int axis,
            StepWatermarkCleanerOptions options,
            out Bounds bounds)
        {
            var faceBounds = data.GetBounds(faceId);
            if (faceBounds.HasValue && faceBounds.Value.Size.Get(axis) <= options.PlaneTolerance)
            {
                bounds = faceBounds.Value;
                return true;
            }

            Bounds bestOuterBounds = default;
            double bestOuterArea = -1.0;
            foreach (int boundId in data.GetAdvancedFaceBounds(faceId))
            {
                if (data.GetTypeName(boundId) != "FACE_OUTER_BOUND")
                    continue;

                var outerBounds = data.GetBounds(boundId);
                if (!outerBounds.HasValue || outerBounds.Value.Size.Get(axis) > options.PlaneTolerance)
                    continue;

                double area = ProjectedArea(outerBounds.Value.Size, axis);
                if (area <= bestOuterArea)
                    continue;

                bestOuterBounds = outerBounds.Value;
                bestOuterArea = area;
            }

            if (bestOuterArea <= 0.0)
            {
                bounds = default;
                return false;
            }

            bounds = bestOuterBounds;
            return true;
        }

        private static Dictionary<int, HashSet<int>> FindMatchingHostFaceBoundsInsideDetectedRegion(
            StepData data,
            SolidInfo ownerInfo,
            Bounds detectedRegion,
            int axis,
            StepWatermarkCleanerOptions options)
        {
            var result = new Dictionary<int, HashSet<int>>();
            foreach (int faceId in ownerInfo.FaceIds)
            {
                var matchedBounds = new HashSet<int>(
                    data.GetMatchingInnerFaceBounds(faceId, detectedRegion, axis, options.HostPlaneProjectionPadding)
                        .Where(boundId => EntityInsideDetectedRegion(data, boundId, detectedRegion, axis, options.HostPlaneProjectionPadding)));
                if (matchedBounds.Count == 0)
                    continue;

                foreach (int expandedBoundId in ExpandHostFaceBounds(data, faceId, matchedBounds, options))
                    matchedBounds.Add(expandedBoundId);

                if (TryGetPlanarHostCandidateBounds(data, faceId, axis, options, out Bounds hostBounds))
                {
                    double hostEdgeMargin = GetAutomaticEdgeMargin(hostBounds, axis);
                    matchedBounds.RemoveWhere(boundId =>
                    {
                        var boundBounds = data.GetBounds(boundId);
                        return boundBounds.HasValue &&
                            TouchesProjectedBoundary(boundBounds.Value, hostBounds, axis, hostEdgeMargin);
                    });
                }

                if (matchedBounds.Count > 0)
                    result[faceId] = matchedBounds;
            }

            return result;
        }

        private static void AddAutomaticRegionCompanionHostBounds(
            StepData data,
            Dictionary<int, SolidInfo> solidInfo,
            AutomaticWatermarkDetection detection,
            StepWatermarkCleanerOptions options)
        {
            if (detection.AutomaticRegions.Count == 0)
                return;

            double maxDepth = Math.Max(
                Math.Max(options.HostPlaneSearchDistance, options.HostLoopAdjacentMaxDepth),
                options.EmbeddedReliefMaxDepth) * 2.0;

            foreach (var region in detection.AutomaticRegions)
            {
                if (region.IsTemplatePromotion)
                    continue;

                if (!solidInfo.TryGetValue(region.OwnerId, out var ownerInfo))
                    continue;

                double companionGap = GetAutomaticClusterGap(region.HostBounds, region.Axis, options) * 2.0;
                foreach (int faceId in ownerInfo.FaceIds)
                {
                    if (faceId != region.HostFaceId)
                        continue;

                    var faceBounds = data.GetBounds(faceId);
                    if (!faceBounds.HasValue)
                        continue;

                    double minDistance = Math.Abs(faceBounds.Value.Min.Get(region.Axis) - region.HostCoordinate);
                    double maxDistance = Math.Abs(faceBounds.Value.Max.Get(region.Axis) - region.HostCoordinate);
                    if (Math.Max(minDistance, maxDistance) > maxDepth)
                        continue;

                    Bounds boundaryBounds = faceBounds.Value;
                    if (TryGetPlanarHostCandidateBounds(data, faceId, region.Axis, options, out Bounds hostCandidateBounds))
                        boundaryBounds = hostCandidateBounds;

                    double hostEdgeMargin = GetAutomaticEdgeMargin(boundaryBounds, region.Axis);
                    foreach (int boundId in data.GetInnerFaceBounds(faceId))
                    {
                        var boundBounds = data.GetBounds(boundId);
                        if (!boundBounds.HasValue)
                            continue;

                        if (!LooksLikeSmallMark(boundBounds.Value, region.HostBounds, options))
                            continue;

                        if (!ProjectionIntersects(boundBounds.Value, region.Bounds, region.Axis, companionGap))
                            continue;

                        if (TouchesProjectedBoundary(boundBounds.Value, boundaryBounds, region.Axis, hostEdgeMargin))
                            continue;

                        if (ownerInfo.Bounds.HasValue &&
                            TouchesProjectedBoundary(
                                boundBounds.Value,
                                ownerInfo.Bounds.Value,
                                region.Axis,
                                GetAutomaticEdgeMargin(ownerInfo.Bounds.Value, region.Axis) * 2.0))
                        {
                            continue;
                        }

                        if (!detection.HostFaceBoundsToRemove.TryGetValue(faceId, out var boundIds))
                        {
                            boundIds = new HashSet<int>();
                            detection.HostFaceBoundsToRemove.Add(faceId, boundIds);
                        }

                        boundIds.Add(boundId);
                    }
                }
            }
        }

        private static HashSet<int> ExpandHostFaceBounds(
            StepData data,
            int? hostFaceId,
            HashSet<int> seedBounds,
            StepWatermarkCleanerOptions options)
        {
            var result = new HashSet<int>(seedBounds);
            if (!hostFaceId.HasValue || seedBounds.Count == 0)
                return result;

            return result;
        }

        private static HashSet<int> ExpandTemplateHostFaceBounds(
            StepData data,
            int? hostFaceId,
            HashSet<int> seedBounds,
            int hostAxis,
            StepWatermarkCleanerOptions options)
        {
            var result = new HashSet<int>(seedBounds);
            if (!hostFaceId.HasValue || seedBounds.Count == 0)
                return result;

            Bounds? hostBounds = data.GetBounds(hostFaceId.Value);
            Bounds? clusterBounds = UnionBounds(data, result);
            if (!hostBounds.HasValue || !clusterBounds.HasValue)
                return result;

            double gap = GetAutomaticClusterGap(hostBounds.Value, hostAxis, options) * 1.25;
            const int maxExpandedBounds = 24;
            bool added;
            do
            {
                added = false;
                clusterBounds = UnionBounds(data, result);
                if (!clusterBounds.HasValue)
                    break;

                foreach (int boundId in data.GetInnerFaceBounds(hostFaceId.Value).OrderBy(id => id))
                {
                    if (result.Contains(boundId))
                        continue;

                    Bounds? boundBounds = data.GetBounds(boundId);
                    if (!boundBounds.HasValue)
                        continue;

                    if (!LooksLikeSmallMark(boundBounds.Value, hostBounds.Value, options))
                        continue;

                    if (!ProjectedBoundsNear(boundBounds.Value, clusterBounds.Value, hostAxis, gap))
                        continue;

                    result.Add(boundId);
                    added = true;
                    if (result.Count > maxExpandedBounds)
                        return seedBounds;
                }
            }
            while (added);

            return result;
        }

        private static bool ShouldExpandTemplateHostFaceBounds(
            StepWatermarkMarkedRegion region,
            IReadOnlyList<int> selectedFaceIds,
            IReadOnlyList<int> matchingBoundIds)
        {
            if (region == null ||
                selectedFaceIds == null ||
                matchingBoundIds == null ||
                selectedFaceIds.Count != 0 ||
                matchingBoundIds.Count == 0 ||
                matchingBoundIds.Count > 5)
            {
                return false;
            }

            return (region.TemplateName ?? string.Empty).IndexOf(
                "vector-arbitrary-text",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ProjectedBoundsNear(Bounds candidate, Bounds cluster, int excludedAxis, double gap)
        {
            int nearAxisCount = 0;
            int overlapAxisCount = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == excludedAxis)
                    continue;

                double candidateMin = candidate.Min.Get(axis);
                double candidateMax = candidate.Max.Get(axis);
                double clusterMin = cluster.Min.Get(axis);
                double clusterMax = cluster.Max.Get(axis);
                bool near = candidateMin <= clusterMax + gap && candidateMax >= clusterMin - gap;
                if (!near)
                    return false;

                nearAxisCount++;
                if (candidateMin <= clusterMax && candidateMax >= clusterMin)
                    overlapAxisCount++;
            }

            return nearAxisCount == 2 && overlapAxisCount >= 1;
        }

        private static HashSet<int> FindHostLoopAdjacentFaces(
            StepData data,
            SolidInfo ownerInfo,
            HostPlaneMatch host,
            HashSet<int> hostBoundIds,
            StepWatermarkCleanerOptions options,
            Bounds? detectedRegion = null)
        {
            var result = new HashSet<int>();
            if (!host.HostFaceId.HasValue || hostBoundIds.Count == 0)
                return result;

            var loopBounds = UnionBounds(data, hostBoundIds);
            if (!loopBounds.HasValue)
                return result;

            var seedEdgeIds = new HashSet<int>();
            foreach (int boundId in hostBoundIds)
            {
                foreach (int edgeId in data.GetReferencedIdsOfType(boundId, "EDGE_CURVE"))
                    seedEdgeIds.Add(edgeId);
            }

            if (seedEdgeIds.Count == 0)
                return result;

            var faceEdges = new Dictionary<int, HashSet<int>>();
            var edgeFaces = new Dictionary<int, List<int>>();
            foreach (int faceId in ownerInfo.FaceIds)
            {
                var edges = new HashSet<int>(data.GetReferencedIdsOfType(faceId, "EDGE_CURVE"));
                faceEdges.Add(faceId, edges);

                foreach (int edgeId in edges)
                {
                    if (!edgeFaces.TryGetValue(edgeId, out var faces))
                    {
                        faces = new List<int>();
                        edgeFaces.Add(edgeId, faces);
                    }

                    faces.Add(faceId);
                }
            }

            var queue = new Queue<int>();
            foreach (int edgeId in seedEdgeIds)
            {
                if (!edgeFaces.TryGetValue(edgeId, out var faces))
                    continue;

                foreach (int faceId in faces)
                {
                    if (faceId == host.HostFaceId.Value)
                        continue;

                    if (result.Contains(faceId))
                        continue;

                    if (!IsShallowFaceInHostLoopRegion(data, faceId, loopBounds.Value, host, options, detectedRegion))
                        continue;

                    result.Add(faceId);
                    queue.Enqueue(faceId);
                }
            }

            while (queue.Count > 0)
            {
                int faceId = queue.Dequeue();
                if (!faceEdges.TryGetValue(faceId, out var edges))
                    continue;

                foreach (int edgeId in edges)
                {
                    if (!edgeFaces.TryGetValue(edgeId, out var neighbors))
                        continue;

                    foreach (int neighborFaceId in neighbors)
                    {
                        if (neighborFaceId == host.HostFaceId.Value || result.Contains(neighborFaceId))
                            continue;

                        if (!IsShallowFaceInHostLoopRegion(data, neighborFaceId, loopBounds.Value, host, options, detectedRegion))
                            continue;

                        result.Add(neighborFaceId);
                        queue.Enqueue(neighborFaceId);
                    }
                }
            }

            return result;
        }

        private static void AddHostLoopResidualInteriorFacesToFlattenResult(
            StepData data,
            Dictionary<int, HashSet<int>> hostFaceBoundsToRemove,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            FlattenResult flattenResult,
            StepWatermarkCleanerOptions options)
        {
            if (hostFaceBoundsToRemove == null || hostFaceBoundsToRemove.Count == 0)
                return;

            double maxResidualDepth = Math.Max(
                Math.Max(options.HostLoopAdjacentMaxDepth, options.HostPlaneSearchDistance),
                options.EmbeddedReliefMaxDepth) * 2.0;
            foreach (var kvp in hostFaceBoundsToRemove)
            {
                int hostFaceId = kvp.Key;
                if (!faceOwners.TryGetValue(hostFaceId, out int ownerId) ||
                    !solidInfo.TryGetValue(ownerId, out SolidInfo ownerInfo))
                {
                    continue;
                }

                Bounds? hostBounds = data.GetBounds(hostFaceId);
                if (!hostBounds.HasValue)
                    continue;

                int axis = FindPlanarAxis(hostBounds.Value, options);
                if (axis < 0)
                    axis = GetSmallestAxis(hostBounds.Value);
                double hostCoordinate = (hostBounds.Value.Min.Get(axis) + hostBounds.Value.Max.Get(axis)) / 2.0;

                foreach (int boundId in kvp.Value)
                {
                    Bounds? loopBounds = data.GetBounds(boundId);
                    if (!loopBounds.HasValue)
                        continue;

                    foreach (int faceId in ownerInfo.FaceIds)
                    {
                        if (faceId == hostFaceId || flattenResult.FlattenedFaces.Contains(faceId))
                            continue;

                        if (IsCylindricalFace(data, faceId))
                            continue;

                        if (HasProtectedNonWatermarkColor(faceId, styledByTarget, options))
                            continue;

                        Bounds? faceBounds = data.GetBounds(faceId);
                        if (!faceBounds.HasValue)
                            continue;

                        if (!ProjectedBoundsInside(faceBounds.Value, loopBounds.Value, axis, options.HostPlaneProjectionPadding))
                            continue;

                        double minDistance = Math.Abs(faceBounds.Value.Min.Get(axis) - hostCoordinate);
                        double maxDistance = Math.Abs(faceBounds.Value.Max.Get(axis) - hostCoordinate);
                        if (Math.Max(minDistance, maxDistance) <= options.PlaneTolerance)
                            continue;

                        if (Math.Max(minDistance, maxDistance) > maxResidualDepth)
                            continue;

                        if (flattenResult.FlattenedFaces.Add(faceId))
                        {
                            flattenResult.FlattenedFaceCount++;
                            flattenResult.ReplacementFaceByRemovedFace[faceId] = hostFaceId;
                        }
                    }
                }
            }
        }

        private static bool IsShallowFaceInHostLoopRegion(
            StepData data,
            int faceId,
            Bounds loopBounds,
            HostPlaneMatch host,
            StepWatermarkCleanerOptions options,
            Bounds? detectedRegion)
        {
            var faceBounds = data.GetBounds(faceId);
            if (!faceBounds.HasValue)
                return false;

            if (detectedRegion.HasValue &&
                !ProjectedBoundsInside(faceBounds.Value, detectedRegion.Value, host.Axis, options.HostPlaneProjectionPadding))
                return false;

            if (!ProjectionIntersects(faceBounds.Value, loopBounds, host.Axis, options.HostPlaneProjectionPadding))
                return false;

            double minDistance = Math.Abs(faceBounds.Value.Min.Get(host.Axis) - host.TargetCoordinate);
            double maxDistance = Math.Abs(faceBounds.Value.Max.Get(host.Axis) - host.TargetCoordinate);
            return Math.Max(minDistance, maxDistance) <= options.HostLoopAdjacentMaxDepth;
        }

        private static bool VectorPrimitiveIntersectsDetection(
            StepVectorWatermarkPrimitive primitive,
            StepVectorWatermarkDetectionRegion detection)
        {
            double left = detection.X;
            double right = detection.X + detection.Width;
            double top = detection.Y;
            double bottom = detection.Y + detection.Height;
            return primitive.ImageBounds.Left <= right &&
                primitive.ImageBounds.Right >= left &&
                primitive.ImageBounds.Bottom >= top &&
                primitive.ImageBounds.Top <= bottom;
        }

        private static List<ProjectedStepTopologySource> BuildProjectedStepTopologySources(
            StepData data,
            TextProjectionViewSpec view)
        {
            var result = new List<ProjectedStepTopologySource>();
            foreach (int faceId in GetActiveAdvancedFaceIds(data))
            {
                foreach (int boundId in data.GetAdvancedFaceBounds(faceId))
                {
                    string boundType = data.GetTypeName(boundId);
                    if (boundType != "FACE_BOUND" && boundType != "FACE_OUTER_BOUND")
                        continue;

                    int edgeLoopId = data.Entities.TryGetValue(boundId, out StepEntity boundEntity)
                        ? boundEntity.References.FirstOrDefault(id => data.GetTypeName(id) == "EDGE_LOOP")
                        : 0;
                    if (edgeLoopId == 0 || !data.Entities.TryGetValue(edgeLoopId, out StepEntity edgeLoopEntity))
                        continue;

                    foreach (int orientedEdgeId in edgeLoopEntity.References)
                    {
                        if (!data.Entities.TryGetValue(orientedEdgeId, out StepEntity orientedEdgeEntity) ||
                            orientedEdgeEntity.Type != "ORIENTED_EDGE")
                        {
                            continue;
                        }

                        int edgeCurveId = orientedEdgeEntity.References.FirstOrDefault(id => data.GetTypeName(id) == "EDGE_CURVE");
                        if (edgeCurveId == 0)
                            continue;

                        List<ProjectedStepPoint> points = BuildProjectedEdgeCurvePoints(data, edgeCurveId, view);
                        if (points.Count < 2)
                            continue;

                        if (orientedEdgeEntity.Definition.TrimEnd().EndsWith(".F.)", StringComparison.OrdinalIgnoreCase))
                            points.Reverse();

                        result.Add(new ProjectedStepTopologySource
                        {
                            FaceId = faceId,
                            BoundId = boundId,
                            EdgeCurveId = edgeCurveId,
                            Points = points
                        });
                    }
                }
            }

            return result;
        }

        private static IEnumerable<int> GetActiveAdvancedFaceIds(StepData data)
        {
            var result = new SortedSet<int>();
            foreach (StepEntity entity in data.Entities.Values)
            {
                if (entity.Type != "CLOSED_SHELL")
                    continue;

                foreach (int referenceId in entity.References)
                {
                    if (data.GetTypeName(referenceId) == "ADVANCED_FACE")
                        result.Add(referenceId);
                }
            }

            return result;
        }

        private static List<ProjectedStepPoint> BuildProjectedEdgeCurvePoints(
            StepData data,
            int edgeCurveId,
            TextProjectionViewSpec view)
        {
            if (!data.Entities.TryGetValue(edgeCurveId, out StepEntity edgeCurveEntity))
                return new List<ProjectedStepPoint>();

            var result = new List<ProjectedStepPoint>();
            foreach (int referenceId in edgeCurveEntity.References)
            {
                string referenceType = data.GetTypeName(referenceId);
                if (referenceType == "VERTEX_POINT")
                {
                    if (TryGetProjectedVertexPoint(data, referenceId, view, out ProjectedStepPoint point))
                        AddProjectedPoint(result, point);
                    continue;
                }

                if (referenceType == "B_SPLINE_CURVE_WITH_KNOTS" ||
                    referenceType == "B_SPLINE_CURVE" ||
                    referenceType == "POLYLINE" ||
                    referenceType.IndexOf("B_SPLINE", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (int pointId in data.Entities[referenceId].References)
                    {
                        if (TryGetProjectedCartesianPoint(data, pointId, view, out ProjectedStepPoint point))
                            AddProjectedPoint(result, point);
                    }
                }
            }

            if (result.Count < 2)
            {
                foreach (int pointId in data.TraverseReferences(edgeCurveId)
                    .Where(id => data.GetTypeName(id) == "CARTESIAN_POINT")
                    .OrderBy(id => id))
                {
                    if (TryGetProjectedCartesianPoint(data, pointId, view, out ProjectedStepPoint point))
                        AddProjectedPoint(result, point);
                }
            }

            return result;
        }

        private static bool TryGetProjectedVertexPoint(
            StepData data,
            int vertexId,
            TextProjectionViewSpec view,
            out ProjectedStepPoint point)
        {
            point = default;
            if (!data.Entities.TryGetValue(vertexId, out StepEntity vertexEntity))
                return false;

            int pointId = vertexEntity.References.FirstOrDefault(id => data.GetTypeName(id) == "CARTESIAN_POINT");
            return pointId != 0 && TryGetProjectedCartesianPoint(data, pointId, view, out point);
        }

        private static bool TryGetProjectedCartesianPoint(
            StepData data,
            int pointId,
            TextProjectionViewSpec view,
            out ProjectedStepPoint point)
        {
            point = default;
            if (!data.TryGetPoint(pointId, out Vec3d modelPoint))
                return false;

            point = new ProjectedStepPoint(
                modelPoint.Get(view.UAxis) * view.USign,
                modelPoint.Get(view.VAxis) * view.VSign);
            return true;
        }

        private static void AddProjectedPoint(List<ProjectedStepPoint> points, ProjectedStepPoint point)
        {
            if (points.Count > 0)
            {
                ProjectedStepPoint last = points[points.Count - 1];
                if (Math.Abs(last.U - point.U) <= 0.000001 &&
                    Math.Abs(last.V - point.V) <= 0.000001)
                {
                    return;
                }
            }

            points.Add(point);
        }

        private static bool ProjectedTopologySourceIntersectsRegion(
            ProjectedStepTopologySource source,
            StepWatermarkMarkedRegion region,
            double padding)
        {
            if (source.Points.Count == 0)
                return false;

            double minU = source.Points.Min(point => point.U);
            double maxU = source.Points.Max(point => point.U);
            double minV = source.Points.Min(point => point.V);
            double maxV = source.Points.Max(point => point.V);
            return minU <= region.ModelUMax + padding &&
                maxU >= region.ModelUMin - padding &&
                minV <= region.ModelVMax + padding &&
                maxV >= region.ModelVMin - padding;
        }

        private static ResidualPrimitiveSourceMatch MatchResidualPrimitiveSource(
            StepVectorWatermarkPrimitive primitive,
            IReadOnlyList<ProjectedStepTopologySource> topology)
        {
            if (primitive != null &&
                topology != null &&
                (primitive.FaceId.HasValue || primitive.BoundId.HasValue || primitive.EdgeCurveId.HasValue))
            {
                ProjectedStepTopologySource directSource = topology.FirstOrDefault(source =>
                    (!primitive.FaceId.HasValue || source.FaceId == primitive.FaceId.Value) &&
                    (!primitive.BoundId.HasValue || source.BoundId == primitive.BoundId.Value) &&
                    (!primitive.EdgeCurveId.HasValue || source.EdgeCurveId == primitive.EdgeCurveId.Value));
                if (directSource != null)
                {
                    return new ResidualPrimitiveSourceMatch
                    {
                        Source = directSource,
                        AverageDistance = 0.0
                    };
                }
            }

            if (primitive == null)
                return new ResidualPrimitiveSourceMatch();

            IReadOnlyList<StepVectorWatermarkPoint> samples = primitive.SampledPoints ?? Array.Empty<StepVectorWatermarkPoint>();
            if (samples.Count < 2 || topology.Count == 0)
                return new ResidualPrimitiveSourceMatch();

            const double endpointTolerance = 0.005;
            const double sampleTolerance = 0.005;
            ProjectedStepTopologySource bestSource = null;
            double bestScore = double.PositiveInfinity;
            foreach (ProjectedStepTopologySource source in topology)
            {
                double firstDistance = DistanceToPolyline(samples[0].X, samples[0].Y, source.Points);
                double lastDistance = DistanceToPolyline(samples[samples.Count - 1].X, samples[samples.Count - 1].Y, source.Points);
                if (firstDistance > endpointTolerance || lastDistance > endpointTolerance)
                    continue;

                int onPolylineCount = 0;
                double totalDistance = 0.0;
                foreach (StepVectorWatermarkPoint sample in samples)
                {
                    double distance = DistanceToPolyline(sample.X, sample.Y, source.Points);
                    totalDistance += distance;
                    if (distance <= sampleTolerance)
                        onPolylineCount++;
                }

                if (onPolylineCount < Math.Ceiling(samples.Count * 0.80))
                    continue;

                double score = totalDistance / Math.Max(samples.Count, 1);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestSource = source;
                }
            }

            return new ResidualPrimitiveSourceMatch
            {
                Source = bestSource,
                AverageDistance = bestScore
            };
        }

        private static double DistanceToPolyline(double x, double y, IReadOnlyList<ProjectedStepPoint> points)
        {
            if (points.Count == 0)
                return double.PositiveInfinity;

            double best = double.PositiveInfinity;
            for (int i = 1; i < points.Count; i++)
                best = Math.Min(best, DistanceToSegment(x, y, points[i - 1], points[i]));

            if (points.Count == 1)
                best = Math.Min(best, Distance(x, y, points[0].U, points[0].V));

            return best;
        }

        private static double DistanceToSegment(double x, double y, ProjectedStepPoint a, ProjectedStepPoint b)
        {
            double dx = b.U - a.U;
            double dy = b.V - a.V;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.000000000001)
                return Distance(x, y, a.U, a.V);

            double t = ((x - a.U) * dx + (y - a.V) * dy) / lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return Distance(x, y, a.U + dx * t, a.V + dy * t);
        }

        private static double Distance(double x0, double y0, double x1, double y1)
        {
            double dx = x0 - x1;
            double dy = y0 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool EntityInsideDetectedRegion(
            StepData data,
            int entityId,
            Bounds detectedRegion,
            int axis,
            double padding)
        {
            var bounds = data.GetBounds(entityId);
            return bounds.HasValue && ProjectedBoundsInside(bounds.Value, detectedRegion, axis, padding);
        }

        private static bool BoundsInsideDetectionVolume(Bounds inner, Bounds outer, double padding)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (inner.Min.Get(axis) < outer.Min.Get(axis) - padding)
                    return false;

                if (inner.Max.Get(axis) > outer.Max.Get(axis) + padding)
                    return false;
            }

            return true;
        }

        private static bool EntityIntersectsDetectedRegion(
            StepData data,
            int entityId,
            Bounds detectedRegion,
            int axis,
            double padding)
        {
            var bounds = data.GetBounds(entityId);
            return bounds.HasValue && ProjectionIntersects(bounds.Value, detectedRegion, axis, padding);
        }

        private static bool ProjectedBoundsInside(Bounds inner, Bounds outer, int excludedAxis, double padding)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == excludedAxis)
                    continue;

                if (inner.Min.Get(axis) < outer.Min.Get(axis) - padding)
                    return false;

                if (inner.Max.Get(axis) > outer.Max.Get(axis) + padding)
                    return false;
            }

            return true;
        }

        private static double ProjectedOverlapRatio(Bounds inner, Bounds outer, int excludedAxis)
        {
            double intersectionArea = 1.0;
            double innerArea = 1.0;
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == excludedAxis)
                    continue;

                double min = Math.Max(inner.Min.Get(axis), outer.Min.Get(axis));
                double max = Math.Min(inner.Max.Get(axis), outer.Max.Get(axis));
                if (max <= min)
                    return 0.0;

                intersectionArea *= max - min;
                innerArea *= Math.Max(inner.Max.Get(axis) - inner.Min.Get(axis), 0.0001);
            }

            return intersectionArea / Math.Max(innerArea, 0.0001);
        }

        private static Bounds? UnionBounds(StepData data, IEnumerable<int> entityIds)
        {
            Bounds result = new Bounds();
            bool hasBounds = false;

            foreach (int entityId in entityIds)
            {
                var bounds = data.GetBounds(entityId);
                if (!bounds.HasValue)
                    continue;

                result.Include(bounds.Value);
                hasBounds = true;
            }

            return hasBounds ? result : (Bounds?)null;
        }

        private static List<HashSet<int>> BuildFaceComponents(StepData data, List<int> faceIds)
        {
            var facesByPoint = new Dictionary<int, List<int>>();
            var facePointsByFace = new Dictionary<int, HashSet<int>>();
            foreach (int faceId in faceIds)
            {
                var facePoints = data.GetPointIds(faceId, includeSurface: false);
                facePointsByFace[faceId] = facePoints;
                foreach (int pointId in facePoints)
                {
                    if (!facesByPoint.TryGetValue(pointId, out var faces))
                    {
                        faces = new List<int>();
                        facesByPoint.Add(pointId, faces);
                    }

                    faces.Add(faceId);
                }
            }

            var result = new List<HashSet<int>>();
            var visited = new HashSet<int>();
            foreach (int faceId in faceIds)
            {
                if (!visited.Add(faceId))
                    continue;

                var component = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(faceId);

                while (stack.Count > 0)
                {
                    int currentFaceId = stack.Pop();
                    component.Add(currentFaceId);

                    if (!facePointsByFace.TryGetValue(currentFaceId, out var facePoints))
                        continue;

                    foreach (int pointId in facePoints)
                    {
                        if (!facesByPoint.TryGetValue(pointId, out var neighbors))
                            continue;

                        foreach (int neighborFaceId in neighbors)
                        {
                            if (visited.Add(neighborFaceId))
                                stack.Push(neighborFaceId);
                        }
                    }
                }

                result.Add(component);
            }

            return result;
        }

        private static int RecolorFlattenedFaces(
            StepData data,
            HashSet<int> embeddedFaces,
            Dictionary<int, int> replacementFaceByRemovedFace,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            List<StyledItemInfo> styledItems,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            Dictionary<int, string> edits)
        {
            int recoloredCount = 0;

            foreach (var styledItem in styledItems)
            {
                if (!embeddedFaces.Contains(styledItem.TargetId))
                    continue;

                if (!faceOwners.TryGetValue(styledItem.TargetId, out int ownerSolidId))
                    continue;

                if (!solidInfo.TryGetValue(ownerSolidId, out var ownerInfo))
                    continue;

                if (replacementFaceByRemovedFace.TryGetValue(styledItem.TargetId, out int replacementFaceId))
                    replacementFaceId = ResolveReplacementFace(replacementFaceByRemovedFace, embeddedFaces, ownerInfo, replacementFaceId);

                int? replacementStyleId = null;
                if (replacementFaceId != 0 &&
                    styledByTarget.TryGetValue(replacementFaceId, out var replacementStyles))
                {
                    replacementStyleId = replacementStyles.FirstOrDefault()?.StyleId;
                }

                if (!replacementStyleId.HasValue)
                    replacementStyleId = ownerInfo.ReplacementStyleId;

                if (!replacementStyleId.HasValue)
                    continue;

                string newDefinition = ReplaceFirstReference(styledItem.Entity.Definition, styledItem.StyleId, replacementStyleId.Value);

                if (newDefinition == styledItem.Entity.Definition)
                    continue;

                edits[styledItem.Entity.Id] = newDefinition;
                recoloredCount++;
            }

            return recoloredCount;
        }

        private static int RemoveFacesFromClosedShells(StepData data, HashSet<int> faceIds, Dictionary<int, string> edits)
        {
            if (faceIds.Count == 0)
                return 0;

            int removedCount = 0;
            foreach (var entity in data.Entities.Values)
            {
                if (entity.Type != "CLOSED_SHELL")
                    continue;

                string definition = edits.TryGetValue(entity.Id, out string pendingDefinition)
                    ? pendingDefinition
                    : entity.Definition;
                foreach (int faceId in faceIds)
                {
                    string updatedDefinition = RemoveReferenceFromCommaList(definition, faceId);
                    if (updatedDefinition != definition)
                        removedCount++;

                    definition = updatedDefinition;
                }

                if (definition != entity.Definition)
                    edits[entity.Id] = definition;
            }

            return removedCount;
        }

        private static int RemoveFaceBounds(StepData data, Dictionary<int, HashSet<int>> faceBoundsToRemove, Dictionary<int, string> edits)
        {
            int removedCount = 0;
            foreach (var kvp in faceBoundsToRemove)
            {
                if (!data.Entities.TryGetValue(kvp.Key, out var entity) || entity.Type != "ADVANCED_FACE")
                    continue;

                string definition = edits.TryGetValue(entity.Id, out string pendingDefinition)
                    ? pendingDefinition
                    : entity.Definition;

                foreach (int boundId in kvp.Value)
                {
                    string updatedDefinition = RemoveReferenceFromCommaList(definition, boundId);
                    if (updatedDefinition != definition)
                        removedCount++;

                    definition = updatedDefinition;
                }

                if (definition != entity.Definition)
                    edits[entity.Id] = definition;
            }

            return removedCount;
        }

        private static ResidualVectorBoundRewriteResult FindResidualVectorBoundsToRemove(
            string cleanedStep,
            StepWatermarkCleanerOptions options,
            IReadOnlyList<StepWatermarkMarkedRegion> sourceRegions,
            bool allowBroadSourceRegionSweep)
        {
            var result = new ResidualVectorBoundRewriteResult();
            if (string.IsNullOrWhiteSpace(cleanedStep))
                return result;

            StepData data = StepData.Parse(cleanedStep);
            data.BuildIndexes();
            Bounds? modelBounds = GetModelBounds(data);
            if (!modelBounds.HasValue)
                return result;

            byte[] stepBytes = Encoding.Latin1.GetBytes(cleanedStep);
            StepProjectionOptions projectionOptions = CreateTemplateTextProjectionOptions(StepProjectionRenderMode.EdgeVisibleRaw);
            var residualViewNames = new HashSet<string>(
                (sourceRegions ?? Array.Empty<StepWatermarkMarkedRegion>())
                    .Select(region => region.ViewName)
                    .Where(viewName => !string.IsNullOrWhiteSpace(viewName)),
                StringComparer.OrdinalIgnoreCase);
            IEnumerable<TextProjectionViewSpec> views = residualViewNames.Count == 0
                ? TextProjectionViews
                : TextProjectionViews.Where(view => residualViewNames.Contains(view.Name));
            foreach (TextProjectionViewSpec view in views)
            {
                List<StepWatermarkMarkedRegion> sourceRegionsForView = (sourceRegions ?? Array.Empty<StepWatermarkMarkedRegion>())
                    .Where(region => HasMarkedRegionArea(region))
                    .Where(region => string.Equals(region.ViewName, view.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                List<ProjectedStepTopologySource> topology = BuildProjectedStepTopologySources(data, view);
                var sourceRegionPrimitiveMatches = new List<ResidualPrimitiveSourceMatch>();

                foreach (StepVectorWatermarkDetectionInput vectorInput in ProjectResidualVectorRewriteInputs(stepBytes, view.Name, projectionOptions))
                {
                    TextProjectionMapping mapping = vectorInput.ImageMapping != null
                        ? TextProjectionMapping.Create(view, vectorInput.ImageMapping)
                        : TextProjectionMapping.Create(
                            modelBounds.Value,
                            view,
                            projectionOptions.ImageSizePixels,
                            projectionOptions.ImageSizePixels,
                            projectionOptions.PaddingPixels);
                    if (sourceRegionsForView.Count > 0)
                    {
                        foreach (StepWatermarkMarkedRegion sourceRegion in sourceRegionsForView)
                        {
                            sourceRegionPrimitiveMatches.AddRange(
                                SelectSourceRegionResidualPrimitives(vectorInput, sourceRegion, options)
                                    .Select(primitive => MatchResidualPrimitiveSource(primitive, topology))
                                    .Where(match => match.Source != null));
                        }
                    }

                    IReadOnlyList<StepVectorWatermarkDetectionRegion> detections = StepVectorWatermarkProjectionDetector
                        .Detect(vectorInput, new StepTextLogoDetectionOptions { DetectArbitraryText = true })
                        .Where(IsHighConfidenceVectorTemplateTextLogoDetection)
                        .ToList();
                    if (detections.Count == 0)
                        continue;

                    foreach (StepVectorWatermarkDetectionRegion detection in detections)
                    {
                        StepWatermarkMarkedRegion region = mapping.ToMarkedRegion(detection);
                        StepWatermarkMarkedRegion sourceRegion = null;
                        if (sourceRegionsForView.Count > 0 &&
                            !TryFindContainingSourceRegion(region, sourceRegionsForView, options, out sourceRegion))
                        {
                            continue;
                        }

                        result.DetectionCount++;
                        if (!TryCreateResidualProjectionRegionBounds(
                            data,
                            region,
                            modelBounds.Value,
                            options,
                            out Bounds detectionBounds))
                        {
                            result.Diagnostics.Add(
                                "Residual vector rewrite skipped: view=" + view.Name +
                                " template=" + (detection.TemplateName ?? string.Empty) +
                                " reason=no-host-depth-anchor");
                            continue;
                        }

                        Bounds sourceBounds = detectionBounds;
                        if (sourceRegion != null &&
                            !TryCreateResidualProjectionRegionBounds(data, sourceRegion, modelBounds.Value, options, out sourceBounds))
                        {
                            result.Diagnostics.Add(
                                "Residual vector rewrite skipped: view=" + view.Name +
                                " template=" + (detection.TemplateName ?? string.Empty) +
                                " reason=no-source-host-depth-anchor");
                            continue;
                        }
                        List<ProjectedStepTopologySource> detectionTopology = topology
                            .Where(source => ProjectedTopologySourceIntersectsRegion(source, region, 0.02))
                            .ToList();
                        List<ResidualPrimitiveSourceMatch> matches = SelectDetectionMemberPrimitives(vectorInput, detection)
                            .Select(primitive => MatchResidualPrimitiveSource(primitive, detectionTopology))
                            .ToList();
                        TryAddContainedResidualBounds(
                            data,
                            view,
                            region,
                            detection,
                            detectionBounds,
                            sourceBounds,
                            detectionTopology,
                            matches,
                            options,
                            result);
                    }
                }

                TryAddSourceRegionResidualSweep(
                    data,
                    view,
                    modelBounds.Value,
                    sourceRegionsForView,
                    sourceRegionPrimitiveMatches,
                    topology,
                    options,
                    result);
            }

            return result;
        }

        private static IEnumerable<StepVectorWatermarkPrimitive> SelectDetectionMemberPrimitives(
            StepVectorWatermarkDetectionInput vectorInput,
            StepVectorWatermarkDetectionRegion detection)
        {
            if (vectorInput == null ||
                vectorInput.Primitives == null ||
                vectorInput.Primitives.Count == 0 ||
                detection == null)
            {
                yield break;
            }

            IReadOnlyList<int> primitiveSourceIndices = detection.PrimitiveSourceIndices ?? Array.Empty<int>();
            if (primitiveSourceIndices.Count > 0)
            {
                var emitted = new HashSet<int>();
                foreach (int primitiveIndex in primitiveSourceIndices)
                {
                    if (primitiveIndex < 0 || primitiveIndex >= vectorInput.Primitives.Count)
                        continue;
                    if (!emitted.Add(primitiveIndex))
                        continue;

                    StepVectorWatermarkPrimitive primitive = vectorInput.Primitives[primitiveIndex];
                    if (primitive != null)
                        yield return primitive;
                }

                yield break;
            }

            foreach (StepVectorWatermarkPrimitive primitive in vectorInput.Primitives)
            {
                if (primitive != null &&
                    primitive.ImageBounds != null &&
                    VectorPrimitiveIntersectsDetection(primitive, detection))
                {
                    yield return primitive;
                }
            }
        }

        private static void TryAddSourceRegionResidualSweep(
            StepData data,
            TextProjectionViewSpec view,
            Bounds modelBounds,
            IReadOnlyList<StepWatermarkMarkedRegion> sourceRegions,
            IReadOnlyList<ResidualPrimitiveSourceMatch> sourceRegionPrimitiveMatches,
            IReadOnlyList<ProjectedStepTopologySource> topology,
            StepWatermarkCleanerOptions options,
            ResidualVectorBoundRewriteResult result)
        {
            if (sourceRegions == null || sourceRegions.Count == 0)
                return;

            foreach (StepWatermarkMarkedRegion sourceRegion in sourceRegions)
            {
                if (!TryCreateResidualProjectionRegionBounds(
                    data,
                    sourceRegion,
                    modelBounds,
                    options,
                    out Bounds sourceBounds))
                {
                    result.Diagnostics.Add(
                        "Residual source-region sweep skipped: view=" + view.Name +
                        " template=" + (sourceRegion.TemplateName ?? string.Empty) +
                        " reason=no-host-depth-anchor");
                    continue;
                }
                int addedFaceCount = 0;
                int addedBoundCount = 0;
                var sourceRegionFaces = new HashSet<int>();
                foreach (StepEntity entity in data.Entities.Values)
                {
                    if (entity == null || entity.Type != "ADVANCED_FACE")
                        continue;

                    Bounds? faceBounds = data.GetBounds(entity.Id);
                    if (!faceBounds.HasValue)
                        continue;

                    int axis = FindPlanarAxis(faceBounds.Value, options);
                    if (axis != view.DepthAxis)
                        continue;

                    foreach (int boundId in data.GetMatchingInnerFaceBounds(
                        entity.Id,
                        sourceBounds,
                        view.DepthAxis,
                        options.HostPlaneProjectionPadding))
                    {
                        Bounds? boundBounds = data.GetBounds(boundId);
                        if (!boundBounds.HasValue ||
                            !ResidualBoundsProjectInsideSourceRegion(
                                boundBounds.Value,
                                sourceBounds,
                                sourceRegion,
                                view,
                                options))
                        {
                            continue;
                        }

                        if (!result.FaceBoundsToRemove.TryGetValue(entity.Id, out HashSet<int> boundIds))
                        {
                            boundIds = new HashSet<int>();
                            result.FaceBoundsToRemove.Add(entity.Id, boundIds);
                        }

                        if (boundIds.Add(boundId))
                            addedBoundCount++;
                    }
                }

                foreach (int faceId in FindSourceRegionResidualOuterFaces(
                    data,
                    view,
                    sourceRegion,
                    sourceBounds,
                    sourceRegionPrimitiveMatches,
                    options))
                {
                    if (sourceRegionFaces.Add(faceId) && result.FaceIdsToRemove.Add(faceId))
                        addedFaceCount++;
                }

                if (addedBoundCount > 0)
                {
                    result.RemovedBoundCount += addedBoundCount;
                    result.Diagnostics.Add(
                        "Residual source-region sweep: view=" + view.Name +
                        " template=" + (sourceRegion.TemplateName ?? string.Empty) +
                        " retainedBounds=" + addedBoundCount.ToString(CultureInfo.InvariantCulture));
                }

                if (addedFaceCount > 0)
                {
                    result.RemovedFaceCount += addedFaceCount;
                    result.Diagnostics.Add(
                        "Residual source-region primitive closure: view=" + view.Name +
                        " template=" + (sourceRegion.TemplateName ?? string.Empty) +
                        " matchedFaces=" + addedFaceCount.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        private static IEnumerable<int> FindSourceRegionResidualOuterFaces(
            StepData data,
            TextProjectionViewSpec view,
            StepWatermarkMarkedRegion sourceRegion,
            Bounds sourceBounds,
            IReadOnlyList<ResidualPrimitiveSourceMatch> sourceRegionPrimitiveMatches,
            StepWatermarkCleanerOptions options)
        {
            if (sourceRegionPrimitiveMatches == null || sourceRegionPrimitiveMatches.Count == 0)
                yield break;

            var emittedFaces = new HashSet<int>();
            foreach (ProjectedStepTopologySource source in sourceRegionPrimitiveMatches
                .Where(match => match.Source != null)
                .Select(match => match.Source)
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.First()))
            {
                if (!string.Equals(data.GetTypeName(source.BoundId), "FACE_OUTER_BOUND", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!ProjectedTopologySourceInsideRegion(source, sourceRegion, options.HostPlaneProjectionPadding))
                    continue;

                if (!emittedFaces.Add(source.FaceId))
                    continue;

                if (IsCylindricalFace(data, source.FaceId))
                    continue;

                StepColor? faceColor = data.ResolveColor(source.FaceId);
                if (HasProtectedResolvedColor(faceColor, options))
                    continue;

                Bounds? faceBounds = data.GetBounds(source.FaceId);
                if (!faceBounds.HasValue)
                    continue;

                if (!ResidualWallFaceCanBeRemovedByProvenance(faceBounds.Value, sourceBounds, view.DepthAxis, options))
                    continue;

                yield return source.FaceId;
            }
        }

        private static IEnumerable<StepVectorWatermarkPrimitive> SelectSourceRegionResidualPrimitives(
            StepVectorWatermarkDetectionInput vectorInput,
            StepWatermarkMarkedRegion sourceRegion,
            StepWatermarkCleanerOptions options)
        {
            if (vectorInput == null || vectorInput.Primitives == null || sourceRegion == null)
                yield break;

            foreach (StepVectorWatermarkPrimitive primitive in vectorInput.Primitives)
            {
                if (primitive != null && PrimitiveInsideMarkedRegion(primitive, sourceRegion, options))
                    yield return primitive;
            }
        }

        private static bool PrimitiveInsideMarkedRegion(
            StepVectorWatermarkPrimitive primitive,
            StepWatermarkMarkedRegion sourceRegion,
            StepWatermarkCleanerOptions options)
        {
            IReadOnlyList<StepVectorWatermarkPoint> samples = primitive.SampledPoints ?? Array.Empty<StepVectorWatermarkPoint>();
            if (samples.Count < 2 || !HasMarkedRegionArea(sourceRegion))
                return false;

            double modelPadding = sourceRegion.ScalePixelsPerModelUnit > 0.0
                ? options.MarkedRegionPaddingPixels / sourceRegion.ScalePixelsPerModelUnit
                : options.HostPlaneProjectionPadding;
            modelPadding = Math.Max(modelPadding, options.HostPlaneProjectionPadding);
            int insideCount = 0;
            foreach (StepVectorWatermarkPoint sample in samples)
            {
                if (sample.X >= sourceRegion.ModelUMin - modelPadding &&
                    sample.X <= sourceRegion.ModelUMax + modelPadding &&
                    sample.Y >= sourceRegion.ModelVMin - modelPadding &&
                    sample.Y <= sourceRegion.ModelVMax + modelPadding)
                {
                    insideCount++;
                }
            }

            return insideCount >= Math.Ceiling(samples.Count * 0.80);
        }

        private static bool ResidualBoundsProjectInsideSourceRegion(
            Bounds candidateBounds,
            Bounds sourceBounds,
            StepWatermarkMarkedRegion sourceRegion,
            TextProjectionViewSpec view,
            StepWatermarkCleanerOptions options)
        {
            if (!ProjectedBoundsInside(candidateBounds, sourceBounds, view.DepthAxis, options.HostPlaneProjectionPadding))
                return false;

            double hostCoordinate = sourceRegion.DepthSign >= 0
                ? sourceBounds.Max.Get(view.DepthAxis)
                : sourceBounds.Min.Get(view.DepthAxis);
            double maxDepth = GetResidualSourceRegionDepthLimit(options);
            double minDistance = Math.Abs(candidateBounds.Min.Get(view.DepthAxis) - hostCoordinate);
            double maxDistance = Math.Abs(candidateBounds.Max.Get(view.DepthAxis) - hostCoordinate);
            return Math.Max(minDistance, maxDistance) <= maxDepth;
        }

        private static IEnumerable<int> FindProjectedShallowResidualFaces(
            StepData data,
            StepWatermarkMarkedRegion sourceRegion,
            Bounds sourceBounds,
            TextProjectionViewSpec view,
            double sourceProjectedArea,
            double sourceProjectedMaxSpan,
            StepWatermarkCleanerOptions options)
        {
            if (sourceProjectedArea <= 0.0 || sourceProjectedMaxSpan <= 0.0)
                yield break;

            double hostCoordinate = sourceRegion.DepthSign >= 0
                ? sourceBounds.Max.Get(view.DepthAxis)
                : sourceBounds.Min.Get(view.DepthAxis);
            double maxDepth = GetResidualSourceRegionDepthLimit(options);

            foreach (StepEntity entity in data.Entities.Values)
            {
                if (entity == null || entity.Type != "ADVANCED_FACE")
                    continue;

                Bounds? faceBounds = data.GetBounds(entity.Id);
                if (!faceBounds.HasValue)
                    continue;

                if (IsCylindricalFace(data, entity.Id))
                    continue;

                StepColor? faceColor = data.ResolveColor(entity.Id);
                if (HasProtectedResolvedColor(faceColor, options))
                    continue;

                double minDistance = Math.Abs(faceBounds.Value.Min.Get(view.DepthAxis) - hostCoordinate);
                double maxDistance = Math.Abs(faceBounds.Value.Max.Get(view.DepthAxis) - hostCoordinate);
                if (Math.Max(minDistance, maxDistance) > maxDepth)
                    continue;

                if (!ProjectedBoundsInside(faceBounds.Value, sourceBounds, view.DepthAxis, options.HostPlaneProjectionPadding))
                    continue;

                double faceProjectedArea = ProjectedArea(faceBounds.Value.Size, view.DepthAxis);
                double faceProjectedMaxSpan = Math.Max(
                    faceBounds.Value.Size.Get(view.UAxis),
                    faceBounds.Value.Size.Get(view.VAxis));
                if (faceProjectedArea > sourceProjectedArea * 0.90 ||
                    faceProjectedMaxSpan > sourceProjectedMaxSpan * 0.95)
                {
                    continue;
                }

                yield return entity.Id;
            }
        }

        private static double GetResidualSourceRegionDepthLimit(StepWatermarkCleanerOptions options)
        {
            return Math.Max(
                Math.Max(options.HostPlaneSearchDistance, options.HostLoopAdjacentMaxDepth),
                options.EmbeddedReliefMaxDepth) * 5.0;
        }

        private static bool HasProtectedResolvedColor(StepColor? color, StepWatermarkCleanerOptions options)
        {
            if (!color.HasValue)
                return false;

            if (IsStandaloneWatermarkColor(color.Value, options) ||
                IsEmbeddedWatermarkColor(color.Value, options))
            {
                return false;
            }

            return color.Value.ChannelSpread > options.NeutralMaxChannelSpread;
        }

        private static IEnumerable<StepVectorWatermarkDetectionInput> ProjectResidualVectorRewriteInputs(
            byte[] stepBytes,
            string viewName,
            StepProjectionOptions rawProjectionOptions)
        {
            StepVectorWatermarkDetectionInput rawInput = null;
            try
            {
                rawInput = StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    stepBytes,
                    "residual-vector-rewrite",
                    viewName,
                    rawProjectionOptions);
            }
            catch
            {
            }

            if (rawInput != null)
                yield return rawInput;

            StepVectorWatermarkDetectionInput defaultInput = null;
            try
            {
                defaultInput = StepProjectionRenderer.ProjectVectorWatermarkDetectionInput(
                    stepBytes,
                    "residual-vector-rewrite",
                    viewName);
            }
            catch
            {
            }

            if (defaultInput != null)
                yield return defaultInput;
        }

        private static bool TryFindContainingSourceRegion(
            StepWatermarkMarkedRegion residualRegion,
            List<StepWatermarkMarkedRegion> sourceRegions,
            StepWatermarkCleanerOptions options,
            out StepWatermarkMarkedRegion sourceRegion)
        {
            sourceRegion = null;
            if (!HasMarkedRegionArea(residualRegion) || sourceRegions == null || sourceRegions.Count == 0)
                return false;

            foreach (StepWatermarkMarkedRegion candidate in sourceRegions)
            {
                if (!MarkedRegionOverlapsMarkedRegion(candidate, residualRegion, options))
                    continue;

                sourceRegion = candidate;
                return true;
            }

            return false;
        }

        private static bool MarkedRegionOverlapsMarkedRegion(
            StepWatermarkMarkedRegion outer,
            StepWatermarkMarkedRegion inner,
            StepWatermarkCleanerOptions options)
        {
            if (!HasMarkedRegionArea(outer) || !HasMarkedRegionArea(inner))
                return false;
            if (!string.Equals(outer.ViewName, inner.ViewName, StringComparison.OrdinalIgnoreCase))
                return false;
            int x0 = Math.Max(
                inner.RectangleX,
                (int)Math.Floor(outer.RectangleX - options.MarkedRegionPaddingPixels));
            int y0 = Math.Max(
                inner.RectangleY,
                (int)Math.Floor(outer.RectangleY - options.MarkedRegionPaddingPixels));
            int x1 = Math.Min(
                inner.RectangleX + inner.RectangleWidth,
                (int)Math.Ceiling(outer.RectangleX + outer.RectangleWidth + options.MarkedRegionPaddingPixels));
            int y1 = Math.Min(
                inner.RectangleY + inner.RectangleHeight,
                (int)Math.Ceiling(outer.RectangleY + outer.RectangleHeight + options.MarkedRegionPaddingPixels));
            int intersection = x1 <= x0 || y1 <= y0 ? 0 : (x1 - x0) * (y1 - y0);
            int innerArea = Math.Max(1, inner.RectangleWidth * inner.RectangleHeight);
            if (intersection / (double)innerArea >= 0.25)
            {
                return true;
            }

            if (outer.UAxis != inner.UAxis || outer.USign != inner.USign ||
                outer.VAxis != inner.VAxis || outer.VSign != inner.VSign)
                return false;

            double padding = outer.ScalePixelsPerModelUnit > 0.0
                ? options.MarkedRegionPaddingPixels / outer.ScalePixelsPerModelUnit
                : 0.0;
            double u0 = Math.Max(inner.ModelUMin, outer.ModelUMin - padding);
            double u1 = Math.Min(inner.ModelUMax, outer.ModelUMax + padding);
            double v0 = Math.Max(inner.ModelVMin, outer.ModelVMin - padding);
            double v1 = Math.Min(inner.ModelVMax, outer.ModelVMax + padding);
            double modelIntersection = u1 <= u0 || v1 <= v0 ? 0.0 : (u1 - u0) * (v1 - v0);
            double innerModelArea = Math.Max(
                0.000001,
                (inner.ModelUMax - inner.ModelUMin) * (inner.ModelVMax - inner.ModelVMin));
            return modelIntersection / innerModelArea >= 0.25;
        }

        private static void TryAddContainedResidualBounds(
            StepData data,
            TextProjectionViewSpec view,
            StepWatermarkMarkedRegion region,
            StepVectorWatermarkDetectionRegion detection,
            Bounds detectionBounds,
            Bounds sourceBounds,
            IReadOnlyList<ProjectedStepTopologySource> detectionTopology,
            IReadOnlyList<ResidualPrimitiveSourceMatch> matches,
            StepWatermarkCleanerOptions options,
            ResidualVectorBoundRewriteResult result)
        {
            if (matches.Count == 0)
                return;

            int unknownCount = matches.Count(match => match.Source == null);
            result.UnknownPrimitiveCount += unknownCount;
            bool excessiveUnknownSources = unknownCount > Math.Max(1, matches.Count / 10);

            var candidateBounds = new Dictionary<int, HashSet<int>>();
            var candidateFaces = new HashSet<int>();
            int blockedCount = 0;
            var blockedDetails = new List<string>();
            if (unknownCount > 0)
            {
                List<int> containedResidualFaces = FindContainedResidualFaces(data, detectionBounds)
                    .Take(81)
                    .ToList();
                if (containedResidualFaces.Count <= 80)
                {
                    foreach (int faceId in containedResidualFaces)
                        candidateFaces.Add(faceId);
                }

                AddUnknownResidualInnerBounds(
                    data,
                    view,
                    detectionBounds,
                    options,
                    candidateBounds);
            }

            if (excessiveUnknownSources)
            {
                AddContainedResidualTopologyIslandSources(
                    data,
                    view,
                    region,
                    detectionBounds,
                    sourceBounds,
                    detectionTopology,
                    options,
                    candidateFaces,
                    candidateBounds);
            }

            foreach (ProjectedStepTopologySource source in matches
                .Where(match => match.Source != null)
                .Select(match => match.Source)
                .GroupBy(source => source.Key, StringComparer.Ordinal)
                .Select(group => group.First()))
            {
                string boundType = data.GetTypeName(source.BoundId);
                Bounds? faceBounds = data.GetBounds(source.FaceId);
                Bounds? boundBounds = data.GetBounds(source.BoundId);
                if (faceBounds.HasValue &&
                    ResidualFaceSourceCanBeRemovedByProvenance(faceBounds.Value, detectionBounds, view.DepthAxis, options))
                {
                    candidateFaces.Add(source.FaceId);
                    continue;
                }

                if (boundType == "FACE_OUTER_BOUND" &&
                    faceBounds.HasValue &&
                    ResidualFaceSourceCanBeRemovedByProvenance(faceBounds.Value, detectionBounds, view.DepthAxis, options))
                {
                    candidateFaces.Add(source.FaceId);
                    continue;
                }

                if (boundType == "FACE_OUTER_BOUND" &&
                    faceBounds.HasValue &&
                    ProjectedTopologySourceInsideRegion(source, region, options.HostPlaneProjectionPadding) &&
                    ResidualFaceSourceCanBeRemovedByProvenance(faceBounds.Value, sourceBounds, view.DepthAxis, options))
                {
                    candidateFaces.Add(source.FaceId);
                    continue;
                }

                if (boundType == "FACE_OUTER_BOUND" &&
                    faceBounds.HasValue &&
                    boundBounds.HasValue &&
                    ResidualWallFaceCanBeRemovedByProvenance(boundBounds.Value, sourceBounds, view.DepthAxis, options))
                {
                    candidateFaces.Add(source.FaceId);
                    continue;
                }

                if (boundType != "FACE_BOUND" ||
                    !boundBounds.HasValue ||
                    (!BoundsInsideDetectionVolume(boundBounds.Value, detectionBounds, 0.006) &&
                        !ResidualSourceBoundsProjectInsideDetection(boundBounds.Value, detectionBounds, view.DepthAxis, options) &&
                        !ResidualWallFaceCanBeRemovedByProvenance(boundBounds.Value, sourceBounds, view.DepthAxis, options)))
                {
                    blockedCount++;
                    if (blockedDetails.Count < 12)
                    {
                        blockedDetails.Add(
                            "face=#" + source.FaceId.ToString(CultureInfo.InvariantCulture) +
                            " bound=#" + source.BoundId.ToString(CultureInfo.InvariantCulture) +
                            " type=" + boundType +
                            " edge=#" + source.EdgeCurveId.ToString(CultureInfo.InvariantCulture) +
                            (boundBounds.HasValue
                                ? " bounds=[" +
                                    boundBounds.Value.Min.X.ToString("G6", CultureInfo.InvariantCulture) + "," +
                                    boundBounds.Value.Min.Y.ToString("G6", CultureInfo.InvariantCulture) + "," +
                                    boundBounds.Value.Min.Z.ToString("G6", CultureInfo.InvariantCulture) + " -> " +
                                    boundBounds.Value.Max.X.ToString("G6", CultureInfo.InvariantCulture) + "," +
                                    boundBounds.Value.Max.Y.ToString("G6", CultureInfo.InvariantCulture) + "," +
                                    boundBounds.Value.Max.Z.ToString("G6", CultureInfo.InvariantCulture) + "]"
                                : " bounds=none"));
                    }

                    continue;
                }

                if (!candidateBounds.TryGetValue(source.FaceId, out HashSet<int> boundIds))
                {
                    boundIds = new HashSet<int>();
                    candidateBounds.Add(source.FaceId, boundIds);
                }

                boundIds.Add(source.BoundId);
            }

            if (blockedCount > 0)
                result.BlockedSourceCount += blockedCount;

            int addedFaceCount = 0;
            foreach (int faceId in candidateFaces)
            {
                if (result.FaceIdsToRemove.Add(faceId))
                    addedFaceCount++;
            }

            int addedCount = 0;
            foreach (var kvp in candidateBounds)
            {
                if (!result.FaceBoundsToRemove.TryGetValue(kvp.Key, out HashSet<int> boundIds))
                {
                    boundIds = new HashSet<int>();
                    result.FaceBoundsToRemove.Add(kvp.Key, boundIds);
                }

                foreach (int boundId in kvp.Value)
                {
                    if (boundIds.Add(boundId))
                        addedCount++;
                }
            }

            if (addedFaceCount > 0 || addedCount > 0)
            {
                result.RemovedFaceCount += addedFaceCount;
                result.RemovedBoundCount += addedCount;
                result.Diagnostics.Add(
                    "Residual vector rewrite: view=" + view.Name +
                    " template=" + (detection.TemplateName ?? string.Empty) +
                    " containedFaces=" + addedFaceCount.ToString(CultureInfo.InvariantCulture) +
                    " retainedBounds=" + addedCount.ToString(CultureInfo.InvariantCulture) +
                    " hosts=" + string.Join(",", candidateBounds.Keys.Concat(candidateFaces).Distinct().OrderBy(id => id).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))) +
                    (blockedCount > 0
                        ? " blockedSources=" + blockedCount.ToString(CultureInfo.InvariantCulture)
                        : string.Empty) +
                    (excessiveUnknownSources
                        ? " unknownSources=" + unknownCount.ToString(CultureInfo.InvariantCulture) + "/" + matches.Count.ToString(CultureInfo.InvariantCulture)
                        : string.Empty));
                return;
            }

            if (excessiveUnknownSources)
            {
                result.Diagnostics.Add(
                    "Residual vector rewrite skipped: view=" + view.Name +
                    " template=" + (detection.TemplateName ?? string.Empty) +
                    " unknown=" + unknownCount.ToString(CultureInfo.InvariantCulture) +
                    "/" + matches.Count.ToString(CultureInfo.InvariantCulture));
            }
            else if (blockedCount > 0)
            {
                result.Diagnostics.Add(
                    "Residual vector rewrite skipped: view=" + view.Name +
                    " template=" + (detection.TemplateName ?? string.Empty) +
                    " blockedSources=" + blockedCount.ToString(CultureInfo.InvariantCulture) +
                    (blockedDetails.Count > 0
                        ? " details=" + string.Join(" | ", blockedDetails)
                        : string.Empty));
            }
        }

        private static bool ResidualWallFaceCanBeRemovedByProvenance(
            Bounds sourceBounds,
            Bounds detectionBounds,
            int depthAxis,
            StepWatermarkCleanerOptions options)
        {
            if (!ProjectionIntersects(sourceBounds, detectionBounds, depthAxis, options.HostPlaneProjectionPadding))
                return false;

            double maxDepth = GetResidualSourceRegionDepthLimit(options);
            if (sourceBounds.Size.Get(depthAxis) > maxDepth)
                return false;

            double sourceArea = ProjectedArea(sourceBounds.Size, depthAxis);
            double detectionArea = ProjectedArea(detectionBounds.Size, depthAxis);
            if (detectionArea <= 0.0)
                return false;

            return sourceArea <= detectionArea * 8.0;
        }

        private static bool ResidualFaceSourceCanBeRemovedByProvenance(
            Bounds sourceBounds,
            Bounds detectionBounds,
            int depthAxis,
            StepWatermarkCleanerOptions options)
        {
            if (!ResidualSourceBoundsProjectInsideDetection(sourceBounds, detectionBounds, depthAxis, options))
                return false;

            double sourceArea = ProjectedArea(sourceBounds.Size, depthAxis);
            double detectionArea = ProjectedArea(detectionBounds.Size, depthAxis);
            if (detectionArea <= 0.0)
                return false;

            double sourceMaxSpan = MaxProjectedSpan(sourceBounds.Size, depthAxis);
            double detectionMaxSpan = MaxProjectedSpan(detectionBounds.Size, depthAxis);
            if (detectionMaxSpan <= 0.0)
                return false;

            return sourceArea <= detectionArea * 0.60 &&
                sourceMaxSpan <= detectionMaxSpan * 0.85;
        }

        private static double MaxProjectedSpan(Vec3d size, int depthAxis)
        {
            double result = 0.0;
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == depthAxis)
                    continue;

                result = Math.Max(result, size.Get(axis));
            }

            return result;
        }

        private static void AddContainedResidualTopologyIslandSources(
            StepData data,
            TextProjectionViewSpec view,
            StepWatermarkMarkedRegion region,
            Bounds detectionBounds,
            Bounds sourceBounds,
            IReadOnlyList<ProjectedStepTopologySource> detectionTopology,
            StepWatermarkCleanerOptions options,
            HashSet<int> candidateFaces,
            Dictionary<int, HashSet<int>> candidateBounds)
        {
            if (region == null || detectionTopology == null || detectionTopology.Count == 0)
                return;

            var islandFaces = new HashSet<int>();
            var islandBounds = new Dictionary<int, HashSet<int>>();
            foreach (ProjectedStepTopologySource source in detectionTopology
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.First()))
            {
                if (!ProjectedTopologySourceInsideRegion(source, region, options.HostPlaneProjectionPadding))
                    continue;

                Bounds? faceBounds = data.GetBounds(source.FaceId);
                Bounds? boundBounds = data.GetBounds(source.BoundId);
                string boundType = data.GetTypeName(source.BoundId);
                if (faceBounds.HasValue &&
                    ResidualFaceSourceCanBeRemovedByProvenance(faceBounds.Value, detectionBounds, view.DepthAxis, options))
                {
                    islandFaces.Add(source.FaceId);
                    continue;
                }

                if (boundType != "FACE_BOUND" ||
                    !boundBounds.HasValue ||
                    !ResidualSourceBoundsProjectInsideDetection(boundBounds.Value, detectionBounds, view.DepthAxis, options))
                {
                    continue;
                }

                if (!islandBounds.TryGetValue(source.FaceId, out HashSet<int> boundIds))
                {
                    boundIds = new HashSet<int>();
                    islandBounds.Add(source.FaceId, boundIds);
                }

                boundIds.Add(source.BoundId);
            }

            int projectedIslandSize = islandFaces.Count + islandBounds.Values.Sum(boundIds => boundIds.Count);
            if (projectedIslandSize == 0)
                return;

            foreach (int faceId in islandFaces)
                candidateFaces.Add(faceId);
            foreach (var kvp in islandBounds)
            {
                if (!candidateBounds.TryGetValue(kvp.Key, out HashSet<int> boundIds))
                {
                    boundIds = new HashSet<int>();
                    candidateBounds.Add(kvp.Key, boundIds);
                }

                foreach (int boundId in kvp.Value)
                    boundIds.Add(boundId);
            }
        }

        private static bool ProjectedTopologySourceInsideRegion(
            ProjectedStepTopologySource source,
            StepWatermarkMarkedRegion region,
            double padding)
        {
            if (source == null || source.Points.Count == 0 || !HasMarkedRegionArea(region))
                return false;

            double minU = source.Points.Min(point => point.U);
            double maxU = source.Points.Max(point => point.U);
            double minV = source.Points.Min(point => point.V);
            double maxV = source.Points.Max(point => point.V);
            return minU >= region.ModelUMin - padding &&
                maxU <= region.ModelUMax + padding &&
                minV >= region.ModelVMin - padding &&
                maxV <= region.ModelVMax + padding;
        }

        private static void AddUnknownResidualInnerBounds(
            StepData data,
            TextProjectionViewSpec view,
            Bounds detectionBounds,
            StepWatermarkCleanerOptions options,
            Dictionary<int, HashSet<int>> candidateBounds)
        {
            foreach (StepEntity entity in data.Entities.Values)
            {
                if (entity == null || entity.Type != "ADVANCED_FACE")
                    continue;

                Bounds? faceBounds = data.GetBounds(entity.Id);
                if (!faceBounds.HasValue)
                    continue;

                int axis = FindPlanarAxis(faceBounds.Value, options);
                if (axis != view.DepthAxis)
                    continue;

                foreach (int boundId in data.GetMatchingInnerFaceBounds(
                    entity.Id,
                    detectionBounds,
                    view.DepthAxis,
                    options.HostPlaneProjectionPadding))
                {
                    Bounds? boundBounds = data.GetBounds(boundId);
                    if (!boundBounds.HasValue ||
                        !BoundsInsideDetectionVolume(boundBounds.Value, detectionBounds, 0.006))
                    {
                        continue;
                    }

                    if (!candidateBounds.TryGetValue(entity.Id, out HashSet<int> boundIds))
                    {
                        boundIds = new HashSet<int>();
                        candidateBounds.Add(entity.Id, boundIds);
                    }

                    boundIds.Add(boundId);
                }
            }
        }

        private static bool ResidualSourceBoundsProjectInsideDetection(
            Bounds sourceBounds,
            Bounds detectionBounds,
            int depthAxis,
            StepWatermarkCleanerOptions options)
        {
            if (!ProjectionIntersects(sourceBounds, detectionBounds, depthAxis, options.HostPlaneProjectionPadding))
                return false;

            double maxDepth = Math.Max(
                Math.Max(options.HostPlaneSearchDistance, options.HostLoopAdjacentMaxDepth),
                options.EmbeddedReliefMaxDepth) * 2.0;
            if (sourceBounds.Size.Get(depthAxis) > maxDepth)
                return false;

            double sourceArea = ProjectedArea(sourceBounds.Size, depthAxis);
            double detectionArea = ProjectedArea(detectionBounds.Size, depthAxis);
            if (detectionArea <= 0.0)
                return false;

            if (sourceArea > detectionArea * 4.0)
                return false;

            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == depthAxis)
                    continue;

                double center = (sourceBounds.Min.Get(axis) + sourceBounds.Max.Get(axis)) / 2.0;
                if (center < detectionBounds.Min.Get(axis) - options.HostPlaneProjectionPadding ||
                    center > detectionBounds.Max.Get(axis) + options.HostPlaneProjectionPadding)
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<int> FindContainedResidualFaces(StepData data, Bounds detectionBounds)
        {
            foreach (StepEntity entity in data.Entities.Values)
            {
                if (entity == null || entity.Type != "ADVANCED_FACE")
                    continue;

                Bounds? faceBounds = data.GetBounds(entity.Id);
                if (!faceBounds.HasValue)
                    continue;

                if (BoundsInsideDetectionVolume(faceBounds.Value, detectionBounds, 0.006))
                    yield return entity.Id;
            }
        }

        private static Bounds? GetModelBounds(StepData data)
        {
            Bounds result = default;
            bool hasBounds = false;
            foreach (StepEntity entity in data.Entities.Values)
            {
                if (!IsCleanupOwnerRootType(entity.Type))
                    continue;

                Bounds? bounds = data.GetBounds(entity.Id);
                if (!bounds.HasValue)
                    continue;

                result.Include(bounds.Value);
                hasBounds = true;
            }

            if (hasBounds)
                return result;

            foreach (int faceId in GetActiveAdvancedFaceIds(data))
            {
                Bounds? bounds = data.GetBounds(faceId);
                if (!bounds.HasValue)
                    continue;

                result.Include(bounds.Value);
                hasBounds = true;
            }

            return hasBounds ? result : (Bounds?)null;
        }

        private static void RemoveSolidsFromShapeRepresentations(StepData data, HashSet<int> solidIds, Dictionary<int, string> edits)
        {
            if (solidIds.Count == 0)
                return;

            foreach (var entity in data.Entities.Values)
            {
                if (entity.Type != "ADVANCED_BREP_SHAPE_REPRESENTATION")
                    continue;

                string definition = entity.Definition;
                foreach (int solidId in solidIds)
                    definition = RemoveReferenceFromCommaList(definition, solidId);

                if (definition != entity.Definition)
                    edits[entity.Id] = definition;
            }
        }

        private static string BuildRemovedGeometryStep(
            StepData data,
            CleanupContext context,
            AutomaticWatermarkDetection detection,
            FlattenResult flattenResult)
        {
            var removedSolidIds = new HashSet<int>(detection.RemovableSolidIds);
            var removedFaceIds = new HashSet<int>(flattenResult.FlattenedFaces);
            removedFaceIds.RemoveWhere(faceId =>
                HasProtectedNonWatermarkColor(faceId, context.StyledByTarget, context.Options) &&
                !IsProtectedFaceRemovedWatermarkTopology(data, flattenResult, faceId, context.Options));
            // Removed-geometry diagnostics should show real removed topology, but not broad
            // source faces pulled in by residual rewrites outside runtime text/logo regions.
            PruneRemovedFacesToRuntimeTextLogoRegions(
                data,
                detection.TemplateTextLogoMarkedRegions,
                flattenResult,
                removedFaceIds,
                context.Options);
            if (removedSolidIds.Count == 0 && removedFaceIds.Count == 0)
                return string.Empty;

            var keptSolidIds = new HashSet<int>(removedSolidIds);
            foreach (int faceId in removedFaceIds)
            {
                if (context.FaceOwners.TryGetValue(faceId, out int ownerId))
                    keptSolidIds.Add(ownerId);
            }

            var edits = new Dictionary<int, string>();
            foreach (StepEntity entity in data.Entities.Values)
            {
                if (entity.Type == "ADVANCED_BREP_SHAPE_REPRESENTATION")
                {
                    string representationDefinition = entity.Definition;
                    foreach (int solidId in context.SolidIds)
                    {
                        if (!keptSolidIds.Contains(solidId))
                            representationDefinition = RemoveReferenceFromCommaList(representationDefinition, solidId);
                    }

                    if (representationDefinition != entity.Definition)
                        edits[entity.Id] = representationDefinition;
                    continue;
                }

                if (entity.Type != "CLOSED_SHELL")
                    continue;

                var shellFaceIds = entity.References
                    .Where(id => data.GetTypeName(id) == "ADVANCED_FACE")
                    .ToList();
                if (shellFaceIds.Count == 0)
                    continue;

                bool belongsToRemovedSolid = shellFaceIds.Any(faceId =>
                    context.FaceOwners.TryGetValue(faceId, out int ownerId) &&
                    removedSolidIds.Contains(ownerId));
                if (belongsToRemovedSolid)
                    continue;

                var removableFaceIds = new HashSet<int>(shellFaceIds.Where(faceId =>
                    !removedFaceIds.Contains(faceId)));

                string definition = RemoveReferencesFromCommaList(entity.Definition, removableFaceIds);
                if (definition != entity.Definition)
                    edits[entity.Id] = definition;
            }

            var inactiveRoots = new HashSet<int>(context.SolidIds.Where(solidId => !keptSolidIds.Contains(solidId)));
            var inactiveFaceIds = new HashSet<int>();
            foreach (var kvp in context.FaceOwners)
            {
                if (!removedFaceIds.Contains(kvp.Key) &&
                    !removedSolidIds.Contains(kvp.Value))
                {
                    inactiveFaceIds.Add(kvp.Key);
                    inactiveRoots.Add(kvp.Key);
                }
            }

            foreach (int styledItemId in RemoveStyledItemsForRemovedFaces(data, context.StyledItems, inactiveFaceIds, edits))
                inactiveRoots.Add(styledItemId);

            foreach (var entity in data.Entities.Values)
            {
                if (IsDiagnosticPresentationRootType(entity.Type))
                {
                    inactiveRoots.Add(entity.Id);
                    continue;
                }
            }

            return ApplyCleanupDefinitionEdits(data, edits, inactiveRoots, out _);
        }

        private static bool IsProtectedFaceRemovedWatermarkTopology(
            StepData data,
            FlattenResult flattenResult,
            int faceId,
            StepWatermarkCleanerOptions options)
        {
            if (!flattenResult.ReplacementFaceByRemovedFace.TryGetValue(faceId, out int hostFaceId))
                return false;

            if (!flattenResult.HostFaceBoundsToRemove.TryGetValue(hostFaceId, out HashSet<int> hostBoundIds) ||
                hostBoundIds.Count == 0)
            {
                return false;
            }

            Bounds? faceBounds = data.GetBounds(faceId);
            if (!faceBounds.HasValue)
                return false;

            Bounds? hostBounds = data.GetBounds(hostFaceId);
            if (!hostBounds.HasValue)
                return false;

            int axis = GetDominantThinAxis(hostBounds.Value);
            foreach (int boundId in hostBoundIds)
            {
                Bounds? boundBounds = data.GetBounds(boundId);
                if (!boundBounds.HasValue)
                    continue;

                if (ProjectionIntersects(faceBounds.Value, boundBounds.Value, axis, options.HostPlaneProjectionPadding))
                    return true;
            }

            return false;
        }

        private static int GetDominantThinAxis(Bounds bounds)
        {
            Vec3d size = bounds.Size;
            if (size.X <= size.Y && size.X <= size.Z)
                return 0;
            if (size.Y <= size.X && size.Y <= size.Z)
                return 1;
            return 2;
        }

        private static void PruneRemovedFacesToMarkedWatermarkRegions(
            StepData data,
            IReadOnlyList<StepWatermarkMarkedRegion> markedRegions,
            HashSet<int> removedFaceIds)
        {
            if (data == null || removedFaceIds == null || removedFaceIds.Count == 0 ||
                markedRegions == null || markedRegions.Count == 0)
            {
                return;
            }

            var regions = markedRegions
                .Where(HasMarkedRegionArea)
                .ToList();
            if (regions.Count == 0)
                return;

            removedFaceIds.RemoveWhere(faceId =>
            {
                Bounds? faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    return true;

                return !regions.Any(region => RemovedFaceProjectsIntoMarkedRegion(faceBounds.Value, region));
            });
        }

        private static void PruneRemovedFacesToRuntimeTextLogoRegions(
            StepData data,
            IReadOnlyList<StepWatermarkMarkedRegion> runtimeRegions,
            FlattenResult flattenResult,
            HashSet<int> removedFaceIds,
            StepWatermarkCleanerOptions options)
        {
            if (data == null || removedFaceIds == null || removedFaceIds.Count == 0 ||
                runtimeRegions == null || runtimeRegions.Count == 0)
            {
                return;
            }

            var regions = runtimeRegions
                .Where(HasMarkedRegionArea)
                .ToList();
            if (regions.Count == 0)
                return;

            removedFaceIds.RemoveWhere(faceId =>
            {
                if (IsProtectedFaceRemovedWatermarkTopology(data, flattenResult, faceId, options))
                    return false;

                Bounds? faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    return true;

                return !regions.Any(region => RemovedFaceProjectsIntoMarkedRegion(faceBounds.Value, region));
            });
        }

        private static bool RemovedFaceProjectsIntoMarkedRegion(Bounds faceBounds, StepWatermarkMarkedRegion region)
        {
            double padding = region.ScalePixelsPerModelUnit > 0.0
                ? 12.0 / region.ScalePixelsPerModelUnit
                : 0.02;
            double u0 = faceBounds.Min.Get(region.UAxis) * region.USign;
            double u1 = faceBounds.Max.Get(region.UAxis) * region.USign;
            double v0 = faceBounds.Min.Get(region.VAxis) * region.VSign;
            double v1 = faceBounds.Max.Get(region.VAxis) * region.VSign;
            double uMin = Math.Min(u0, u1);
            double uMax = Math.Max(u0, u1);
            double vMin = Math.Min(v0, v1);
            double vMax = Math.Max(v0, v1);
            double uCenter = (uMin + uMax) / 2.0;
            double vCenter = (vMin + vMax) / 2.0;
            if (uCenter < region.ModelUMin - padding ||
                uCenter > region.ModelUMax + padding ||
                vCenter < region.ModelVMin - padding ||
                vCenter > region.ModelVMax + padding)
            {
                return false;
            }

            double faceArea = Math.Max((uMax - uMin) * (vMax - vMin), 0.000001);
            double regionArea = Math.Max((region.ModelUMax - region.ModelUMin) * (region.ModelVMax - region.ModelVMin), 0.000001);
            if (faceArea <= regionArea * 2.0)
                return true;

            double intersectionUMin = Math.Max(uMin, region.ModelUMin - padding);
            double intersectionUMax = Math.Min(uMax, region.ModelUMax + padding);
            double intersectionVMin = Math.Max(vMin, region.ModelVMin - padding);
            double intersectionVMax = Math.Min(vMax, region.ModelVMax + padding);
            if (intersectionUMax <= intersectionUMin || intersectionVMax <= intersectionVMin)
                return false;

            double overlapArea = (intersectionUMax - intersectionUMin) * (intersectionVMax - intersectionVMin);
            return overlapArea / faceArea >= 0.35;
        }

        private static void PruneRemovedFacesToVisualWatermarkRois(StepData data, HashSet<int> removedFaceIds)
        {
            if (data == null || removedFaceIds == null || removedFaceIds.Count == 0)
                return;

            byte[] stepBytes = Encoding.Latin1.GetBytes(data.Text);
            StepWatermarkVisualScanResult visualScan;
            try
            {
                visualScan = StepWatermarkVisualOracle.DetectKnownWatermarks(stepBytes, "removed-geometry-roi");
            }
            catch
            {
                removedFaceIds.Clear();
                return;
            }

            if (visualScan.Detections.Count == 0)
            {
                removedFaceIds.Clear();
                return;
            }

            var faceReport = new StepWatermarkDetectionReport
            {
                RemovableSolidIds = Array.Empty<int>(),
                EmbeddedFaceIds = removedFaceIds.ToList(),
                CoplanarFaceIds = Array.Empty<int>(),
                HostLoops = Array.Empty<StepWatermarkHostLoopDetection>(),
                Regions = removedFaceIds
                    .SelectMany(faceId => StepProjectionRenderer.ViewNames.Select(viewName => new StepWatermarkRegionDetection
                    {
                        EntityId = faceId,
                        Kind = "face",
                        ViewName = viewName
                    }))
                    .ToList(),
                Diagnostics = Array.Empty<string>()
            };
            IReadOnlyList<StepProjectionDetectionRegion> faceRegions;
            try
            {
                faceRegions = StepProjectionRenderer.ProjectDetectionRegions(
                    stepBytes,
                    "removed-geometry-roi",
                    faceReport,
                    StepWatermarkVisualOracle.CreateProjectionOptions(StepProjectionRenderMode.Color));
            }
            catch
            {
                removedFaceIds.Clear();
                return;
            }

            var keptFaceIds = new HashSet<int>();
            foreach (var faceGroup in faceRegions.GroupBy(region => region.EntityId))
            {
                bool insideVisualRoi = faceGroup.Any(faceRegion =>
                    visualScan.Detections.Any(detection =>
                    {
                        if (!string.Equals(detection.ViewName, faceRegion.ViewName, StringComparison.OrdinalIgnoreCase))
                            return false;

                        int detectionArea = Math.Max(1, detection.Width * detection.Height);
                        int faceArea = Math.Max(1, faceRegion.RectangleWidth * faceRegion.RectangleHeight);
                        bool contained = ProjectionRectangleInsideDetectedRegion(
                            faceRegion.RectangleX,
                            faceRegion.RectangleY,
                            faceRegion.RectangleWidth,
                            faceRegion.RectangleHeight,
                            detection.X,
                            detection.Y,
                            detection.Width,
                            detection.Height,
                            6);
                        bool localIntersection =
                            faceArea <= detectionArea * 2 &&
                            RectanglesIntersect(
                                faceRegion.RectangleX,
                                faceRegion.RectangleY,
                                faceRegion.RectangleWidth,
                                faceRegion.RectangleHeight,
                                detection.X,
                                detection.Y,
                                detection.Width,
                                detection.Height,
                                6);
                        return contained || localIntersection;
                    }));
                if (insideVisualRoi)
                    keptFaceIds.Add(faceGroup.Key);
            }

            removedFaceIds.RemoveWhere(faceId => !keptFaceIds.Contains(faceId));
        }

        private static bool ProjectionRectangleInsideDetectedRegion(
            int innerX,
            int innerY,
            int innerWidth,
            int innerHeight,
            int outerX,
            int outerY,
            int outerWidth,
            int outerHeight,
            int padding)
        {
            if (innerWidth <= 0 || innerHeight <= 0 || outerWidth <= 0 || outerHeight <= 0)
                return false;

            int innerRight = innerX + innerWidth - 1;
            int innerBottom = innerY + innerHeight - 1;
            int outerRight = outerX + outerWidth - 1;
            int outerBottom = outerY + outerHeight - 1;
            return innerX >= outerX - padding &&
                innerY >= outerY - padding &&
                innerRight <= outerRight + padding &&
                innerBottom <= outerBottom + padding;
        }

        private static bool RectanglesIntersect(
            int leftX,
            int leftY,
            int leftWidth,
            int leftHeight,
            int rightX,
            int rightY,
            int rightWidth,
            int rightHeight,
            int padding)
        {
            int leftRight = leftX + leftWidth - 1;
            int leftBottom = leftY + leftHeight - 1;
            int rightRight = rightX + rightWidth - 1;
            int rightBottom = rightY + rightHeight - 1;
            return leftX <= rightRight + padding &&
                leftRight + padding >= rightX &&
                leftY <= rightBottom + padding &&
                leftBottom + padding >= rightY;
        }

        private static bool IsDiagnosticPresentationRootType(string type)
        {
            return type == "STYLED_ITEM" ||
                type == "MECHANICAL_DESIGN_GEOMETRIC_PRESENTATION_REPRESENTATION" ||
                type == "PRESENTATION_LAYER_ASSIGNMENT";
        }

        private static HashSet<int> RemoveStyledItemsForRemovedFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            HashSet<int> removedFaceIds,
            Dictionary<int, string> edits)
        {
            var styledItemIds = new HashSet<int>();
            if (removedFaceIds.Count == 0)
                return styledItemIds;

            foreach (var styledItem in styledItems.Where(item => removedFaceIds.Contains(item.TargetId)))
                styledItemIds.Add(styledItem.Entity.Id);

            if (styledItemIds.Count == 0)
                return styledItemIds;

            foreach (var styledItem in styledItems)
            {
                if (!styledItemIds.Contains(styledItem.Entity.Id))
                    continue;

                edits[styledItem.Entity.Id] = ReplaceStyledItemTargetWithNull(styledItem.Entity.Definition, styledItem.TargetId);
            }

            foreach (var entity in data.Entities.Values)
            {
                if (entity.Type != "MECHANICAL_DESIGN_GEOMETRIC_PRESENTATION_REPRESENTATION" &&
                    entity.Type != "PRESENTATION_LAYER_ASSIGNMENT")
                    continue;

                string definition = edits.TryGetValue(entity.Id, out string pendingDefinition)
                    ? pendingDefinition
                    : entity.Definition;
                definition = RemoveReferencesFromCommaList(definition, styledItemIds);

                if (definition != entity.Definition)
                    edits[entity.Id] = definition;
            }

            return styledItemIds;
        }

        private static IEnumerable<int> GetSolidFaceIds(
            Dictionary<int, SolidInfo> solidInfo,
            IEnumerable<int> solidIds)
        {
            foreach (int solidId in solidIds)
            {
                if (!solidInfo.TryGetValue(solidId, out SolidInfo info))
                    continue;

                foreach (int faceId in info.FaceIds)
                    yield return faceId;
            }
        }

        private static string ApplyCleanupDefinitionEdits(
            StepData data,
            Dictionary<int, string> edits,
            HashSet<int> inactiveDefinitionRoots,
            out int removedDefinitionCount)
        {
            return ApplyCleanupDefinitionEdits(data, edits, inactiveDefinitionRoots, out removedDefinitionCount, null);
        }

        private static string ApplyCleanupDefinitionEdits(
            StepData data,
            Dictionary<int, string> edits,
            HashSet<int> inactiveDefinitionRoots,
            out int removedDefinitionCount,
            IEnumerable<string> appendedDefinitions)
        {
            string edited = data.ApplyDefinitionEdits(edits, null, appendedDefinitions);
            removedDefinitionCount = 0;

            if (inactiveDefinitionRoots == null || inactiveDefinitionRoots.Count == 0)
                return edited;

            StepData editedData = StepData.Parse(edited);
            editedData.BuildIndexes();

            var candidates = new HashSet<int>();
            foreach (int rootId in inactiveDefinitionRoots)
            {
                if (!editedData.Entities.ContainsKey(rootId))
                    continue;

                foreach (int id in editedData.TraverseReferences(rootId))
                    candidates.Add(id);
            }

            if (candidates.Count == 0)
                return edited;

            HashSet<int> removable = FindInactiveCleanupDefinitions(editedData, candidates);
            removedDefinitionCount = removable.Count;
            return removedDefinitionCount == 0
                ? edited
                : editedData.ApplyDefinitionEdits(null, removable);
        }

        private static HashSet<int> FindInactiveCleanupDefinitions(StepData data, HashSet<int> candidates)
        {
            var removable = new HashSet<int>(candidates.Where(id => data.Entities.ContainsKey(id)));
            if (removable.Count == 0)
                return removable;

            Dictionary<int, List<int>> referrersByTarget = BuildReferrersByTarget(data, removable);
            var preserveQueue = new Queue<int>();
            foreach (int candidateId in removable)
            {
                if (HasReferrerOutsideCandidateSet(referrersByTarget, candidateId, removable))
                    preserveQueue.Enqueue(candidateId);
            }

            while (preserveQueue.Count > 0)
            {
                int preservedId = preserveQueue.Dequeue();
                if (!removable.Remove(preservedId))
                    continue;

                if (!data.Entities.TryGetValue(preservedId, out StepEntity preservedEntity))
                    continue;

                foreach (int referencedId in preservedEntity.References)
                {
                    if (removable.Contains(referencedId))
                        preserveQueue.Enqueue(referencedId);
                }
            }

            return removable;
        }

        private static Dictionary<int, List<int>> BuildReferrersByTarget(StepData data, HashSet<int> candidates)
        {
            var result = new Dictionary<int, List<int>>();
            foreach (StepEntity entity in data.Entities.Values)
            {
                foreach (int referenceId in entity.References)
                {
                    if (!candidates.Contains(referenceId))
                        continue;

                    if (!result.TryGetValue(referenceId, out List<int> referrers))
                    {
                        referrers = new List<int>();
                        result.Add(referenceId, referrers);
                    }

                    referrers.Add(entity.Id);
                }
            }

            return result;
        }

        private static bool HasReferrerOutsideCandidateSet(
            Dictionary<int, List<int>> referrersByTarget,
            int candidateId,
            HashSet<int> candidates)
        {
            if (!referrersByTarget.TryGetValue(candidateId, out List<int> referrers))
                return false;

            foreach (int referrerId in referrers)
            {
                if (!candidates.Contains(referrerId))
                    return true;
            }

            return false;
        }

        private static string ReplaceStyledItemTargetWithNull(string definition, int targetId)
        {
            string id = targetId.ToString(CultureInfo.InvariantCulture);
            return Regex.Replace(definition, @",\s*#" + id + @"(?=\s*\))", ", $", RegexOptions.RightToLeft);
        }

        private static string RemoveReferenceFromCommaList(string definition, int referenceId)
        {
            string id = referenceId.ToString(CultureInfo.InvariantCulture);
            string result = Regex.Replace(definition, @"(?<=\()\s*#" + id + @"\s*,\s*", string.Empty);
            result = Regex.Replace(result, @"\s*,\s*#" + id + @"(?=\s*[\),])", string.Empty);
            result = Regex.Replace(result, @"(?<=\()\s*#" + id + @"\s*(?=\))", string.Empty);
            return NormalizeCommaListDefinition(result);
        }

        private static string AddReferencesToClosedShellDefinition(string definition, IEnumerable<int> referenceIds)
        {
            var ids = referenceIds == null
                ? new List<int>()
                : referenceIds.Distinct().OrderBy(id => id).ToList();
            if (ids.Count == 0 || string.IsNullOrEmpty(definition))
                return definition;

            int outerClose = definition.LastIndexOf(')');
            if (outerClose < 0)
                return definition;

            int innerClose = definition.LastIndexOf(')', Math.Max(outerClose - 1, 0));
            if (innerClose < 0)
                return definition;

            int innerOpen = definition.LastIndexOf('(', innerClose);
            if (innerOpen < 0)
                return definition;

            string existing = definition.Substring(innerOpen + 1, innerClose - innerOpen - 1).Trim();
            string addition = string.Join(", ", ids.Select(id => "#" + id.ToString(CultureInfo.InvariantCulture)));
            string separator = string.IsNullOrEmpty(existing) ? string.Empty : ", ";
            return definition.Substring(0, innerClose) +
                separator +
                addition +
                definition.Substring(innerClose);
        }

        private static string RemoveReferencesFromCommaList(string definition, HashSet<int> referenceIds)
        {
            if (string.IsNullOrEmpty(definition) || referenceIds == null || referenceIds.Count == 0)
                return definition;

            List<RangeToRemove> ranges = null;
            foreach (Match match in ReferenceRegex.Matches(definition))
            {
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ||
                    !referenceIds.Contains(id))
                {
                    continue;
                }

                int start = match.Index;
                while (start > 0 && char.IsWhiteSpace(definition[start - 1]))
                    start--;

                int end = match.Index + match.Length;
                while (end < definition.Length && char.IsWhiteSpace(definition[end]))
                    end++;

                int next = end;
                while (next < definition.Length && char.IsWhiteSpace(definition[next]))
                    next++;

                if (next < definition.Length && definition[next] == ',')
                {
                    end = next + 1;
                    while (end < definition.Length && char.IsWhiteSpace(definition[end]))
                        end++;
                }
                else
                {
                    int previous = start - 1;
                    while (previous >= 0 && char.IsWhiteSpace(definition[previous]))
                        previous--;

                    if (previous >= 0 && definition[previous] == ',')
                        start = previous;
                }

                if (ranges == null)
                    ranges = new List<RangeToRemove>();
                AddRemovalRange(ranges, start, end);
            }

            if (ranges == null || ranges.Count == 0)
                return definition;

            var builder = new StringBuilder(definition.Length);
            int copyFrom = 0;
            foreach (RangeToRemove range in ranges)
            {
                if (range.Start > copyFrom)
                    builder.Append(definition, copyFrom, range.Start - copyFrom);

                copyFrom = Math.Max(copyFrom, range.End);
            }

            if (copyFrom < definition.Length)
                builder.Append(definition, copyFrom, definition.Length - copyFrom);

            return NormalizeCommaListDefinition(builder.ToString());
        }

        private static string NormalizeCommaListDefinition(string definition)
        {
            if (string.IsNullOrEmpty(definition))
                return definition;

            return Regex.Replace(definition, @",\s*(?=\))", string.Empty);
        }

        private static void AddRemovalRange(List<RangeToRemove> ranges, int start, int end)
        {
            if (start >= end)
                return;

            if (ranges.Count == 0 || start > ranges[ranges.Count - 1].End)
            {
                ranges.Add(new RangeToRemove { Start = start, End = end });
                return;
            }

            RangeToRemove last = ranges[ranges.Count - 1];
            if (end > last.End)
            {
                last.End = end;
                ranges[ranges.Count - 1] = last;
            }
        }

        private static bool LooksLikeSmallMark(Bounds faceBounds, Bounds ownerBounds, StepWatermarkCleanerOptions options)
        {
            var ownerSize = ownerBounds.Size;
            var faceSize = faceBounds.Size;

            double ownerDiagonal = Length(ownerSize);
            if (ownerDiagonal <= 0)
                return false;

            double faceDiagonal = Length(faceSize);
            if (faceDiagonal / ownerDiagonal > options.MaxFaceDiagonalRatio)
                return false;

            double ownerArea = MaxProjectedArea(ownerSize);
            double faceArea = MaxProjectedArea(faceSize);
            if (ownerArea <= 0)
                return false;

            return faceArea / ownerArea <= options.MaxFaceAreaRatio;
        }

        private static bool IsWatermarkColor(StepColor color, StepWatermarkCleanerOptions options)
        {
            return color.Luminance >= options.WatermarkMinLuminance &&
                color.ChannelSpread <= options.NeutralMaxChannelSpread;
        }

        private static bool IsDarkWatermarkColor(StepColor color, StepWatermarkCleanerOptions options)
        {
            return color.Luminance <= options.DarkWatermarkMaxLuminance &&
                color.ChannelSpread <= options.NeutralMaxChannelSpread;
        }

        private static bool IsStandaloneWatermarkColor(StepColor color, StepWatermarkCleanerOptions options)
        {
            return IsWatermarkColor(color, options) ||
                IsDarkWatermarkColor(color, options);
        }

        private static bool IsEmbeddedWatermarkColor(StepColor color, StepWatermarkCleanerOptions options)
        {
            return (color.Luminance >= options.EmbeddedWatermarkMinLuminance ||
                    color.Luminance <= options.DarkWatermarkMaxLuminance) &&
                color.ChannelSpread <= options.NeutralMaxChannelSpread;
        }

        private static bool IsAutomaticEmbeddedWatermarkColor(StepColor color, StepWatermarkCleanerOptions options)
        {
            return IsStandaloneWatermarkColor(color, options);
        }

        private static bool IsReplacementBodyColor(StepColor color, StepWatermarkCleanerOptions options, bool allowDark)
        {
            if (color.Luminance > options.NeutralBodyMaxLuminance)
                return false;

            if (IsWatermarkColor(color, options))
                return false;

            return allowDark || !IsDarkWatermarkColor(color, options);
        }

        private static bool ProjectionIntersects(Bounds a, Bounds b, int excludedAxis, double padding)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == excludedAxis)
                    continue;

                if (a.Max.Get(axis) < b.Min.Get(axis) - padding)
                    return false;

                if (a.Min.Get(axis) > b.Max.Get(axis) + padding)
                    return false;
            }

            return true;
        }

        private static bool ProjectedBoundsOverlap(Bounds a, Bounds b, int excludedAxis, double padding)
        {
            return ProjectionIntersects(a, b, excludedAxis, padding);
        }

        private static bool TouchesProjectedBoundary(Bounds inner, Bounds outer, int excludedAxis, double margin)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == excludedAxis)
                    continue;

                if (inner.Min.Get(axis) <= outer.Min.Get(axis) + margin)
                    return true;

                if (inner.Max.Get(axis) >= outer.Max.Get(axis) - margin)
                    return true;
            }

            return false;
        }

        private static bool IsCoplanarWithHostPlane(
            Bounds bounds,
            int axis,
            double hostCoordinate,
            StepWatermarkCleanerOptions options)
        {
            double minDistance = Math.Abs(bounds.Min.Get(axis) - hostCoordinate);
            double maxDistance = Math.Abs(bounds.Max.Get(axis) - hostCoordinate);
            return Math.Max(minDistance, maxDistance) <= options.PlaneTolerance;
        }

        private static double ProjectedArea(Vec3d size, int excludedAxis)
        {
            double area = 1.0;
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis != excludedAxis)
                    area *= Math.Max(size.Get(axis), 0.0001);
            }

            return area;
        }

        private static double Length(Vec3d vector)
        {
            return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        }

        private static double MaxProjectedArea(Vec3d size)
        {
            double xy = Math.Abs(size.X * size.Y);
            double xz = Math.Abs(size.X * size.Z);
            double yz = Math.Abs(size.Y * size.Z);
            return Math.Max(xy, Math.Max(xz, yz));
        }

        private static string AxisName(int axis)
        {
            switch (axis)
            {
                case 0: return "X";
                case 1: return "Y";
                case 2: return "Z";
                default: return "?";
            }
        }

        private static int ProjectionAxisIndex(string axisName)
        {
            switch (axisName?.Trim().ToUpperInvariant())
            {
                case "X": return 0;
                case "Y": return 1;
                case "Z": return 2;
                default: return -1;
            }
        }

        private static List<StyledItemInfo> BuildStyledItems(StepData data)
        {
            var result = new List<StyledItemInfo>();

            foreach (var entity in data.Entities.Values)
            {
                if (entity.Type != "STYLED_ITEM")
                    continue;

                if (entity.References.Count < 2)
                    continue;

                int styleId = entity.References[0];
                int targetId = entity.References[entity.References.Count - 1];
                result.Add(new StyledItemInfo
                {
                    Entity = entity,
                    StyleId = styleId,
                    TargetId = targetId,
                    Color = data.ResolveColor(styleId)
                });
            }

            return result;
        }

        private static Dictionary<int, int> BuildFaceOwnerMap(StepData data, IEnumerable<int> solidIds)
        {
            var result = new Dictionary<int, int>();

            foreach (int solidId in solidIds)
            {
                foreach (int id in data.TraverseReferences(solidId))
                {
                    if (data.GetTypeName(id) == "ADVANCED_FACE" && !result.ContainsKey(id))
                        result.Add(id, solidId);
                }
            }

            return result;
        }

        private static bool IsCleanupOwnerRootType(string type)
        {
            return type == "MANIFOLD_SOLID_BREP" ||
                type == "BREP_WITH_VOIDS" ||
                type == "FACETED_BREP" ||
                type == "SHELL_BASED_SURFACE_MODEL";
        }

        private static Dictionary<int, SolidInfo> BuildSolidInfo(
            StepData data,
            IEnumerable<int> solidIds,
            Dictionary<int, int> faceOwners,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            var result = new Dictionary<int, SolidInfo>();

            foreach (int solidId in solidIds)
            {
                var faceIds = faceOwners
                    .Where(kvp => kvp.Value == solidId)
                    .Select(kvp => kvp.Key)
                    .ToList();

                var styleCounts = new Dictionary<int, StyleUse>();
                var nonDarkStyleCounts = new Dictionary<int, StyleUse>();

                foreach (int faceId in faceIds)
                {
                    if (!styledByTarget.TryGetValue(faceId, out var faceStyles))
                        continue;

                    foreach (var faceStyle in faceStyles)
                    {
                        if (!faceStyle.Color.HasValue)
                            continue;

                        if (!IsReplacementBodyColor(faceStyle.Color.Value, options, allowDark: true))
                            continue;

                        AddStyleUse(styleCounts, faceStyle.StyleId, faceStyle.Color.Value);

                        if (IsReplacementBodyColor(faceStyle.Color.Value, options, allowDark: false))
                            AddStyleUse(nonDarkStyleCounts, faceStyle.StyleId, faceStyle.Color.Value);
                    }
                }

                int? replacementStyleId = null;
                StepColor? replacementColor = null;

                var dominant = nonDarkStyleCounts.Values
                    .OrderByDescending(s => s.Count)
                    .ThenBy(s => s.Color.Luminance)
                    .FirstOrDefault()
                    ?? styleCounts.Values
                    .OrderByDescending(s => s.Count)
                    .ThenBy(s => s.Color.Luminance)
                    .FirstOrDefault();

                if (dominant != null)
                {
                    replacementStyleId = dominant.StyleId;
                    replacementColor = dominant.Color;
                }
                else if (styledByTarget.TryGetValue(solidId, out var solidStyles))
                {
                    var solidStyle = solidStyles.FirstOrDefault(s =>
                        s.Color.HasValue &&
                        IsReplacementBodyColor(s.Color.Value, options, allowDark: false))
                        ?? solidStyles.FirstOrDefault(s =>
                            s.Color.HasValue &&
                            IsReplacementBodyColor(s.Color.Value, options, allowDark: true));
                    if (solidStyle != null)
                    {
                        replacementStyleId = solidStyle.StyleId;
                        replacementColor = solidStyle.Color.Value;
                    }
                }

                result.Add(solidId, new SolidInfo
                {
                    SolidId = solidId,
                    FaceIds = faceIds,
                    Bounds = data.GetBounds(solidId),
                    PlanarHostCandidatesByAxis = BuildPlanarHostCandidatesByAxis(data, faceIds, options),
                    ReplacementStyleId = replacementStyleId,
                    ReplacementColor = replacementColor
                });
            }

            return result;
        }

        private static int ResolveReplacementFace(
            Dictionary<int, int> replacementFaceByRemovedFace,
            HashSet<int> removedFaces,
            SolidInfo ownerInfo,
            int faceId)
        {
            var seen = new HashSet<int>();
            int current = faceId;
            while (replacementFaceByRemovedFace.TryGetValue(current, out int replacementFaceId))
            {
                if (!seen.Add(current) || replacementFaceId == current)
                    break;

                current = replacementFaceId;
            }

            if (removedFaces.Contains(current))
            {
                foreach (int ownerFaceId in ownerInfo.FaceIds)
                {
                    if (!removedFaces.Contains(ownerFaceId))
                        return ownerFaceId;
                }
            }

            return current;
        }

        private static void AddStyleUse(Dictionary<int, StyleUse> styleCounts, int styleId, StepColor color)
        {
            if (!styleCounts.TryGetValue(styleId, out var use))
            {
                use = new StyleUse
                {
                    StyleId = styleId,
                    Color = color
                };
                styleCounts.Add(styleId, use);
            }

            use.Count++;
        }

        private static string ReplaceFirstReference(string definition, int oldReferenceId, int newReferenceId)
        {
            bool replaced = false;
            return ReferenceRegex.Replace(definition, match =>
            {
                if (replaced)
                    return match.Value;

                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    return match.Value;

                if (id != oldReferenceId)
                    return match.Value;

                replaced = true;
                return "#" + newReferenceId.ToString(CultureInfo.InvariantCulture);
            }, 1);
        }

        private static string ReplaceLastReference(string definition, int oldReferenceId, int newReferenceId)
        {
            Match replacementMatch = null;
            foreach (Match match in ReferenceRegex.Matches(definition))
            {
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    continue;

                if (id == oldReferenceId)
                    replacementMatch = match;
            }

            if (replacementMatch == null)
                return definition;

            return definition.Substring(0, replacementMatch.Index) +
                "#" + newReferenceId.ToString(CultureInfo.InvariantCulture) +
                definition.Substring(replacementMatch.Index + replacementMatch.Length);
        }

        private sealed class CleanupContext
        {
            public StepData Data { get; set; }
            public StepWatermarkCleanerOptions Options { get; set; }
            public List<int> SolidIds { get; set; }
            public List<StyledItemInfo> StyledItems { get; set; }
            public Dictionary<int, List<StyledItemInfo>> StyledByTarget { get; set; }
            public Dictionary<int, int> FaceOwners { get; set; }
            public Dictionary<int, SolidInfo> SolidInfo { get; set; }
            public int StyledFaceCount { get; set; }
        }

        private struct TextProjectionViewSpec
        {
            public readonly string Name;
            public readonly int DepthAxis;
            public readonly int DepthSign;
            public readonly int UAxis;
            public readonly int USign;
            public readonly int VAxis;
            public readonly int VSign;

            public TextProjectionViewSpec(string name, int depthAxis, int depthSign, int uAxis, int uSign, int vAxis, int vSign)
            {
                Name = name;
                DepthAxis = depthAxis;
                DepthSign = depthSign;
                UAxis = uAxis;
                USign = uSign;
                VAxis = vAxis;
                VSign = vSign;
            }
        }

        private struct TextProjectionMapping
        {
            private TextProjectionViewSpec View { get; set; }
            private int ImageWidth { get; set; }
            private int ImageHeight { get; set; }
            private int Padding { get; set; }
            private double Scale { get; set; }
            private double UMin { get; set; }
            private double VMin { get; set; }

            public static TextProjectionMapping Create(
                Bounds bounds,
                TextProjectionViewSpec view,
                int imageWidth,
                int imageHeight,
                int padding)
            {
                double u0 = bounds.Min.Get(view.UAxis) * view.USign;
                double u1 = bounds.Max.Get(view.UAxis) * view.USign;
                double v0 = bounds.Min.Get(view.VAxis) * view.VSign;
                double v1 = bounds.Max.Get(view.VAxis) * view.VSign;

                double uMin = Math.Min(u0, u1);
                double uMax = Math.Max(u0, u1);
                double vMin = Math.Min(v0, v1);
                double vMax = Math.Max(v0, v1);
                double usableWidth = imageWidth - padding * 2.0;
                double usableHeight = imageHeight - padding * 2.0;
                double uSize = Math.Max(0.000001, uMax - uMin);
                double vSize = Math.Max(0.000001, vMax - vMin);
                double scale = Math.Min(usableWidth / uSize, usableHeight / vSize);
                double uPad = (usableWidth / scale - uSize) / 2.0;
                double vPad = (usableHeight / scale - vSize) / 2.0;

                return new TextProjectionMapping
                {
                    View = view,
                    ImageWidth = imageWidth,
                    ImageHeight = imageHeight,
                    Padding = padding,
                    Scale = scale,
                    UMin = uMin - uPad,
                    VMin = vMin - vPad
                };
            }

            public static TextProjectionMapping Create(
                TextProjectionViewSpec view,
                StepVectorWatermarkImageMapping mapping)
            {
                if (mapping == null)
                    throw new ArgumentNullException(nameof(mapping));

                return new TextProjectionMapping
                {
                    View = view,
                    ImageWidth = mapping.ImageWidth,
                    ImageHeight = mapping.ImageHeight,
                    Padding = mapping.PaddingPixels,
                    Scale = mapping.Scale,
                    UMin = mapping.UMin,
                    VMin = mapping.VMin
                };
            }

            public StepWatermarkMarkedRegion ToMarkedRegion(StepVectorWatermarkDetectionRegion detection)
            {
                double u0 = UMin + (detection.X - Padding) / Scale;
                double u1 = UMin + (detection.X + detection.Width - Padding) / Scale;
                double v0 = VMin + (ImageHeight - Padding - detection.Y) / Scale;
                double v1 = VMin + (ImageHeight - Padding - (detection.Y + detection.Height)) / Scale;

                return new StepWatermarkMarkedRegion
                {
                    ViewName = View.Name,
                    SourceMarkerPath = "runtime-template:" + detection.TemplateName,
                    SourceProjectionPath = "runtime-template:" + detection.TemplateName,
                    UAxis = View.UAxis,
                    USign = NormalizeSign(View.USign),
                    VAxis = View.VAxis,
                    VSign = NormalizeSign(View.VSign),
                    DepthAxis = View.DepthAxis,
                    DepthSign = NormalizeSign(View.DepthSign),
                    ModelUMin = Math.Min(u0, u1),
                    ModelUMax = Math.Max(u0, u1),
                    ModelVMin = Math.Min(v0, v1),
                    ModelVMax = Math.Max(v0, v1),
                    ScalePixelsPerModelUnit = Scale,
                    ImageWidth = ImageWidth,
                    ImageHeight = ImageHeight,
                    RectangleX = detection.X,
                    RectangleY = detection.Y,
                    RectangleWidth = detection.Width,
                    RectangleHeight = detection.Height,
                    TemplateName = detection.TemplateName,
                    Kind = detection.Kind,
                    Score = detection.Score,
                    ChamferDistance = detection.ChamferDistance,
                    EdgePixelCount = detection.PrimitiveCount
                };
            }
        }

        private sealed class AutomaticWatermarkDetection
        {
            public HashSet<int> RemovableSolidIds { get; set; } = new HashSet<int>();
            public HashSet<int> SolidRegionCandidateIds { get; set; } = new HashSet<int>();
            public List<int> EmbeddedFaceIds { get; set; } = new List<int>();
            public List<int> CoplanarFaceIds { get; set; } = new List<int>();
            public List<int> TextFaceIds { get; set; } = new List<int>();
            public int TemplateTextDetectionCount { get; set; }
            public int TemplateTextCandidateCount { get; set; }
            public int TemplateTextFaceCount { get; set; }
            public int TemplateTextHostRejectCount { get; set; }
            public int TemplateTextBoundaryRejectCount { get; set; }
            public int TemplateTextLogoDetectionCount { get; set; }
            public int TemplateTextLogoCandidateCount { get; set; }
            public int TemplateTextLogoAcceptedRegionCount { get; set; }
            public int TemplateTextLogoFaceCount { get; set; }
            public int TemplateTextLogoHostRejectCount { get; set; }
            public int TemplateTextLogoProtectedRejectCount { get; set; }
            public List<StepWatermarkMarkedRegion> TemplateTextLogoMarkedRegions { get; set; } = new List<StepWatermarkMarkedRegion>();
            public List<string> TemplateTextLogoDiagnostics { get; set; } = new List<string>();
            public int TextCandidateCount { get; set; }
            public int TextClusterCount { get; set; }
            public Dictionary<int, HashSet<int>> HostFaceBoundsToRemove { get; } = new Dictionary<int, HashSet<int>>();
            public List<AutomaticWatermarkRegion> AutomaticRegions { get; } = new List<AutomaticWatermarkRegion>();
            public int RemovableSolidHostLoopCount { get; set; }
            public int RemovableSolidHostLoopCandidateCount { get; set; }
            public int EmbeddedHostLoopCount { get; set; }
            public int HostLoopCandidateCount { get; set; }
            public int HostLoopCount { get; set; }
        }

        private sealed class StyledItemInfo
        {
            public StepEntity Entity { get; set; }
            public int StyleId { get; set; }
            public int TargetId { get; set; }
            public StepColor? Color { get; set; }
        }

        private struct RangeToRemove
        {
            public int Start;
            public int End;
        }

        private sealed class TextStringDetectionResult
        {
            public List<int> FaceIds { get; set; } = new List<int>();
            public int CandidateCount { get; set; }
            public int ClusterCount { get; set; }
        }

        private sealed class TextTemplateDetectionResult
        {
            public List<int> FaceIds { get; set; } = new List<int>();
            public int DetectionCount { get; set; }
            public int CandidateCount { get; set; }
            public int HostRejectCount { get; set; }
            public int BoundaryRejectCount { get; set; }
        }

        private sealed class ProjectionPromotionResult
        {
            public List<int> FaceIds { get; set; } = new List<int>();
            public Dictionary<int, HashSet<int>> HostFaceBoundsToRemove { get; } = new Dictionary<int, HashSet<int>>();
            public List<AutomaticWatermarkRegion> Regions { get; } = new List<AutomaticWatermarkRegion>();
            public List<StepWatermarkMarkedRegion> MarkedRegions { get; set; } = new List<StepWatermarkMarkedRegion>();
            public int DetectionCount { get; set; }
            public int CandidateCount { get; set; }
            public int HostRejectCount { get; set; }
            public int ProtectedRejectCount { get; set; }
            public List<string> Diagnostics { get; } = new List<string>();
        }

        private sealed class ProjectionFaceCluster
        {
            public int OwnerId { get; set; }
            public int HostFaceId { get; set; }
            public int Axis { get; set; }
            public double HostCoordinate { get; set; }
            public Bounds Bounds;
            public Bounds HostBounds { get; set; }
            public List<int> FaceIds { get; } = new List<int>();
        }

        private sealed class VectorPrismHostCandidate
        {
            public SolidInfo OwnerInfo { get; set; }
            public int HostFaceId { get; set; }
            public int Axis { get; set; }
            public double HostCoordinate { get; set; }
            public Bounds RegionBounds { get; set; }
            public Bounds HostBounds { get; set; }
            public bool ProtectedHostFace { get; set; }
            public bool ContainsProtectedContact { get; set; }
            public double Score { get; set; }
        }

        private sealed class StyleUse
        {
            public int StyleId { get; set; }
            public StepColor Color { get; set; }
            public int Count { get; set; }
        }

        private sealed class SolidInfo
        {
            public int SolidId { get; set; }
            public List<int> FaceIds { get; set; }
            public Bounds? Bounds { get; set; }
            public List<PlanarHostCandidate>[] PlanarHostCandidatesByAxis { get; set; }
            public int? ReplacementStyleId { get; set; }
            public StepColor? ReplacementColor { get; set; }
        }

        private sealed class PlanarHostCandidate
        {
            public int FaceId { get; set; }
            public Bounds Bounds { get; set; }
            public double Coordinate { get; set; }
            public double ProjectedArea { get; set; }
        }

        private sealed class HostPlaneMatch
        {
            public int Axis { get; set; }
            public double TargetCoordinate { get; set; }
            public int? HostFaceId { get; set; }
            public double Score { get; set; }
        }

        private sealed class WatermarkFaceCandidate
        {
            public int FaceId { get; set; }
            public int OwnerId { get; set; }
            public Bounds Bounds { get; set; }
            public int PointCount { get; set; }
            public HostPlaneMatch Host { get; set; }
            public Bounds HostBounds { get; set; }
            public bool HasColorCue { get; set; }
            public bool AllowStandaloneColorPattern { get; set; }
            public bool RestrictStandaloneColorPattern { get; set; }
            public int ColorClass { get; set; }
        }

        private sealed class ProjectedClusterBand
        {
            public double Min { get; set; }
            public double Max { get; set; }
            public double VMin { get; set; }
            public double VMax { get; set; }
            public int RowCount { get; set; }
            public int Score { get; set; }
            public List<WatermarkFaceCandidate> Candidates { get; } = new List<WatermarkFaceCandidate>();
        }

        private sealed class WatermarkSolidCandidate
        {
            public int SolidId { get; set; }
            public Bounds Bounds { get; set; }
            public int PointCount { get; set; }
            public int Axis { get; set; }
            public double Coordinate { get; set; }
        }

        private sealed class WatermarkLoopCandidate
        {
            public int BoundId { get; set; }
            public int HostFaceId { get; set; }
            public Bounds Bounds { get; set; }
            public int PointCount { get; set; }
            public Bounds HostBounds { get; set; }
            public bool AllowStandaloneLoop { get; set; }
            public bool RequireCompactEngravedCluster { get; set; }
        }

        private sealed class AutomaticWatermarkLoopResult
        {
            public Dictionary<int, HashSet<int>> HostFaceBoundsToRemove { get; } = new Dictionary<int, HashSet<int>>();
            public List<AutomaticWatermarkRegion> Regions { get; } = new List<AutomaticWatermarkRegion>();
            public int CandidateCount { get; set; }
        }

        private sealed class AutomaticWatermarkRegion
        {
            public int OwnerId { get; set; }
            public int HostFaceId { get; set; }
            public int Axis { get; set; }
            public double HostCoordinate { get; set; }
            public Bounds Bounds { get; set; }
            public Bounds HostBounds { get; set; }
            public bool IsTemplatePromotion { get; set; }
        }

        private sealed class AutomaticCleanupVolume
        {
            public int OwnerId { get; set; }
            public int HostFaceId { get; set; }
            public int Axis { get; set; }
            public double HostCoordinate { get; set; }
            public double MinCoordinate { get; set; }
            public double MaxCoordinate { get; set; }
            public Bounds Bounds { get; set; }
            public Bounds HostBounds { get; set; }
            public bool IsTemplatePromotion { get; set; }
        }

        private sealed class EmbeddedHostLoopGroup
        {
            public int OwnerId { get; set; }
            public int HostFaceId { get; set; }
            public int Axis { get; set; }
            public double TargetCoordinate { get; set; }
            public Bounds Bounds;
            public Bounds HostBounds { get; set; }
        }

        private sealed class FlattenOperation
        {
            public int SolidId { get; set; }
            public int Axis { get; set; }
            public double TargetCoordinate { get; set; }
            public int FaceCount { get; set; }
            public int? HostFaceId { get; set; }
        }

        private sealed class FlattenResult
        {
            public int FlattenedFaceCount { get; set; }
            public int FlattenedPointCount { get; set; }
            public int EditedOutsideCleanupVolumeCount { get; set; }
            public HashSet<int> FlattenedFaces { get; } = new HashSet<int>();
            public Dictionary<int, int> ReplacementFaceByRemovedFace { get; } = new Dictionary<int, int>();
            public Dictionary<int, HashSet<int>> HostFaceBoundsToRemove { get; } = new Dictionary<int, HashSet<int>>();
            public List<FlattenOperation> Operations { get; } = new List<FlattenOperation>();
        }

        private sealed class ResidualVectorBoundRewriteResult
        {
            public HashSet<int> FaceIdsToRemove { get; } = new HashSet<int>();
            public Dictionary<int, HashSet<int>> FaceBoundsToRemove { get; } =
                new Dictionary<int, HashSet<int>>();
            public int DetectionCount { get; set; }
            public int RemovedFaceCount { get; set; }
            public int RemovedBoundCount { get; set; }
            public int UnknownPrimitiveCount { get; set; }
            public int BlockedSourceCount { get; set; }
            public List<string> Diagnostics { get; } = new List<string>();
        }

        private sealed class ResidualPrimitiveSourceMatch
        {
            public ProjectedStepTopologySource Source { get; set; }
            public double AverageDistance { get; set; }
        }

        private sealed class ProjectedStepTopologySource
        {
            public int FaceId { get; set; }
            public int BoundId { get; set; }
            public int EdgeCurveId { get; set; }
            public List<ProjectedStepPoint> Points { get; set; } = new List<ProjectedStepPoint>();

            public string Key =>
                FaceId.ToString(CultureInfo.InvariantCulture) + "|" +
                BoundId.ToString(CultureInfo.InvariantCulture) + "|" +
                EdgeCurveId.ToString(CultureInfo.InvariantCulture);
        }

        private readonly struct ProjectedStepPoint
        {
            public ProjectedStepPoint(double u, double v)
            {
                U = u;
                V = v;
            }

            public double U { get; }
            public double V { get; }
        }

        private sealed class StepData
        {
            private readonly Dictionary<int, Bounds?> _boundsCache = new Dictionary<int, Bounds?>();
            private readonly Dictionary<int, StepColor?> _colorCache = new Dictionary<int, StepColor?>();
            private readonly Dictionary<string, HashSet<int>> _pointIdsCache = new Dictionary<string, HashSet<int>>();

            public string Text { get; private set; }
            public Dictionary<int, StepEntity> Entities { get; private set; }

            private StepData(string text, Dictionary<int, StepEntity> entities)
            {
                Text = text;
                Entities = entities;
            }

            public static StepData Parse(string text)
            {
                var entities = new Dictionary<int, StepEntity>();
                int cursor = 0;

                while (cursor < text.Length)
                {
                    int hash = text.IndexOf('#', cursor);
                    if (hash < 0)
                        break;

                    int idStart = hash + 1;
                    int idEnd = idStart;
                    while (idEnd < text.Length && char.IsDigit(text[idEnd]))
                        idEnd++;

                    if (idEnd == idStart)
                    {
                        cursor = hash + 1;
                        continue;
                    }

                    int afterId = SkipWhiteSpace(text, idEnd);
                    if (afterId >= text.Length || text[afterId] != '=')
                    {
                        cursor = idEnd;
                        continue;
                    }

                    int definitionStart = afterId + 1;
                    int semicolon = FindEntityEnd(text, definitionStart);
                    if (semicolon < 0)
                        break;

                    int id = int.Parse(text.Substring(idStart, idEnd - idStart), CultureInfo.InvariantCulture);
                    string definition = text.Substring(definitionStart, semicolon - definitionStart).Trim();
                    entities[id] = new StepEntity
                    {
                        Id = id,
                        StartIndex = hash,
                        EndIndex = semicolon + 1,
                        Definition = definition,
                        Type = GetEntityType(definition)
                    };

                    cursor = semicolon + 1;
                }

                return new StepData(text, entities);
            }

            public void BuildIndexes()
            {
                foreach (var entity in Entities.Values)
                    entity.References = ParseReferences(entity.Definition);
            }

            public string GetTypeName(int id)
            {
                return Entities.TryGetValue(id, out var entity) ? entity.Type : string.Empty;
            }

            public IEnumerable<int> TraverseReferences(int rootId)
            {
                var visited = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(rootId);

                while (stack.Count > 0)
                {
                    int id = stack.Pop();
                    if (!visited.Add(id))
                        continue;

                    yield return id;

                    if (!Entities.TryGetValue(id, out var entity))
                        continue;

                    for (int i = 0; i < entity.References.Count; i++)
                    {
                        int childId = entity.References[i];
                        if (!visited.Contains(childId))
                            stack.Push(childId);
                    }
                }
            }

            public HashSet<int> GetPointIds(int rootId, bool includeSurface)
            {
                string key = rootId.ToString(CultureInfo.InvariantCulture) + "|" + includeSurface.ToString(CultureInfo.InvariantCulture);
                if (_pointIdsCache.TryGetValue(key, out var cached))
                    return new HashSet<int>(cached);

                var result = new HashSet<int>();
                var startIds = new List<int>();

                if (GetTypeName(rootId) == "ADVANCED_FACE" && !includeSurface && Entities[rootId].References.Count > 1)
                {
                    for (int i = 0; i < Entities[rootId].References.Count - 1; i++)
                        startIds.Add(Entities[rootId].References[i]);
                }
                else
                {
                    startIds.Add(rootId);
                }

                foreach (int startId in startIds)
                {
                    foreach (int id in TraverseReferences(startId))
                    {
                        if (GetTypeName(id) == "CARTESIAN_POINT")
                            result.Add(id);
                    }
                }

                _pointIdsCache[key] = new HashSet<int>(result);
                return result;
            }

            public List<int> GetAdvancedFaceBounds(int faceId)
            {
                if (!Entities.TryGetValue(faceId, out var entity) || entity.Type != "ADVANCED_FACE" || entity.References.Count < 2)
                    return new List<int>();

                return entity.References.Take(entity.References.Count - 1).ToList();
            }

            public IEnumerable<int> GetMatchingInnerFaceBounds(int? faceId, Bounds componentBounds, int hostAxis, double padding)
            {
                if (!faceId.HasValue)
                    yield break;

                var bounds = GetAdvancedFaceBounds(faceId.Value);
                for (int i = 0; i < bounds.Count; i++)
                {
                    int boundId = bounds[i];
                    string boundType = GetTypeName(boundId);
                    if (boundType == "FACE_OUTER_BOUND")
                        continue;

                    if (boundType != "FACE_BOUND")
                        continue;

                    var boundBounds = GetBounds(boundId);
                    if (!boundBounds.HasValue)
                        continue;

                    if (!ProjectionIntersects(boundBounds.Value, componentBounds, hostAxis, padding))
                        continue;

                    yield return boundId;
                }
            }

            public IEnumerable<int> GetInnerFaceBounds(int? faceId)
            {
                if (!faceId.HasValue)
                    yield break;

                var bounds = GetAdvancedFaceBounds(faceId.Value);
                foreach (int boundId in bounds)
                {
                    string boundType = GetTypeName(boundId);
                    if (boundType == "FACE_BOUND")
                        yield return boundId;
                }
            }

            public IEnumerable<int> GetReferencedIdsOfType(int rootId, string typeName)
            {
                foreach (int id in TraverseReferences(rootId))
                {
                    if (GetTypeName(id) == typeName)
                        yield return id;
                }
            }

            public Bounds? GetBounds(int rootId)
            {
                if (_boundsCache.TryGetValue(rootId, out var cached))
                    return cached;

                Bounds? result;
                string type = GetTypeName(rootId);
                if (type == "ADVANCED_FACE")
                {
                    result = GetBoundsFromPointIds(GetPointIds(rootId, includeSurface: false));
                }
                else if (IsCleanupOwnerRootType(type))
                {
                    Bounds bounds = new Bounds();
                    bool hasBounds = false;
                    foreach (int id in TraverseReferences(rootId))
                    {
                        if (GetTypeName(id) != "ADVANCED_FACE")
                            continue;

                        var faceBounds = GetBounds(id);
                        if (!faceBounds.HasValue)
                            continue;

                        bounds.Include(faceBounds.Value);
                        hasBounds = true;
                    }

                    result = hasBounds ? bounds : (Bounds?)null;
                }
                else
                {
                    result = GetBoundsFromPointIds(GetPointIds(rootId, includeSurface: true));
                }

                _boundsCache[rootId] = result;
                return result;
            }

            public Bounds? GetBoundsFromPointIds(IEnumerable<int> pointIds)
            {
                Bounds bounds = new Bounds();
                bool hasPoint = false;

                foreach (int pointId in pointIds)
                {
                    if (!TryGetPoint(pointId, out var point))
                        continue;

                    bounds.Include(point);
                    hasPoint = true;
                }

                return hasPoint ? bounds : (Bounds?)null;
            }

            public bool TryGetPoint(int pointId, out Vec3d point)
            {
                if (!Entities.TryGetValue(pointId, out var entity) || entity.Type != "CARTESIAN_POINT")
                {
                    point = default;
                    return false;
                }

                return TryParsePoint(entity.Definition, out point);
            }

            public string ReplacePointCoordinate(int pointId, int axis, double value)
            {
                if (!Entities.TryGetValue(pointId, out var entity) || entity.Type != "CARTESIAN_POINT")
                    throw new ArgumentException("Entity is not a CARTESIAN_POINT.", nameof(pointId));

                return ReplacePointCoordinate(entity.Definition, axis, value);
            }

            public StepColor? ResolveColor(int rootId)
            {
                if (_colorCache.TryGetValue(rootId, out var cached))
                    return cached;

                var visited = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(rootId);

                while (stack.Count > 0)
                {
                    int id = stack.Pop();
                    if (!visited.Add(id))
                        continue;

                    if (!Entities.TryGetValue(id, out var entity))
                        continue;

                    if (entity.Type == "COLOUR_RGB" && TryParseColour(entity.Definition, out var color))
                    {
                        _colorCache[rootId] = color;
                        return color;
                    }

                    for (int i = 0; i < entity.References.Count; i++)
                    {
                        int childId = entity.References[i];
                        if (!visited.Contains(childId))
                            stack.Push(childId);
                    }
                }

                _colorCache[rootId] = null;
                return null;
            }

            public string ApplyDefinitionEdits(Dictionary<int, string> edits)
            {
                return ApplyDefinitionEdits(edits, null);
            }

            public string ApplyDefinitionEdits(Dictionary<int, string> edits, HashSet<int> removedEntityIds)
            {
                return ApplyDefinitionEdits(edits, removedEntityIds, null);
            }

            public string ApplyDefinitionEdits(
                Dictionary<int, string> edits,
                HashSet<int> removedEntityIds,
                IEnumerable<string> appendedDefinitions)
            {
                bool hasEdits = edits != null && edits.Count > 0;
                bool hasRemovals = removedEntityIds != null && removedEntityIds.Count > 0;
                var appended = appendedDefinitions == null
                    ? new List<string>()
                    : appendedDefinitions
                        .Where(definition => !string.IsNullOrWhiteSpace(definition))
                        .Select(definition => definition.Trim().TrimEnd(';').TrimEnd())
                        .ToList();
                bool hasAppends = appended.Count > 0;
                if (!hasEdits && !hasRemovals && !hasAppends)
                    return Text;

                var builder = new StringBuilder(Text.Length);
                int cursor = 0;

                foreach (var entity in Entities.Values.OrderBy(e => e.StartIndex))
                {
                    if (hasRemovals && removedEntityIds.Contains(entity.Id))
                    {
                        builder.Append(Text, cursor, entity.StartIndex - cursor);
                        cursor = entity.EndIndex;
                        continue;
                    }

                    if (!hasEdits || !edits.TryGetValue(entity.Id, out string newDefinition))
                        continue;

                    builder.Append(Text, cursor, entity.StartIndex - cursor);
                    builder.Append('#');
                    builder.Append(entity.Id.ToString(CultureInfo.InvariantCulture));
                    builder.Append(" = ");
                    builder.Append(newDefinition);
                    builder.Append(" ;");
                    cursor = entity.EndIndex;
                }

                builder.Append(Text, cursor, Text.Length - cursor);
                string edited = builder.ToString();
                if (!hasAppends)
                    return edited;

                int insertIndex = edited.LastIndexOf("ENDSEC;", StringComparison.Ordinal);
                if (insertIndex < 0)
                    insertIndex = edited.Length;

                var appendBuilder = new StringBuilder(edited.Length + appended.Sum(definition => definition.Length + 32));
                appendBuilder.Append(edited, 0, insertIndex);
                if (appendBuilder.Length > 0 && appendBuilder[appendBuilder.Length - 1] != '\n')
                    appendBuilder.AppendLine();

                int nextId = Entities.Count == 0 ? 1 : Entities.Keys.Max() + 1;
                foreach (string definition in appended)
                {
                    appendBuilder.Append('#');
                    appendBuilder.Append(nextId.ToString(CultureInfo.InvariantCulture));
                    appendBuilder.Append(" = ");
                    appendBuilder.Append(definition);
                    appendBuilder.AppendLine(" ;");
                    nextId++;
                }

                appendBuilder.Append(edited, insertIndex, edited.Length - insertIndex);
                return appendBuilder.ToString();
            }

            private static int SkipWhiteSpace(string text, int index)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                    index++;

                return index;
            }

            private static int FindEntityEnd(string text, int start)
            {
                bool inString = false;

                for (int i = start; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '\'')
                    {
                        if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i++;
                            continue;
                        }

                        inString = !inString;
                        continue;
                    }

                    if (!inString && c == ';')
                        return i;
                }

                return -1;
            }

            private static string GetEntityType(string definition)
            {
                var match = EntityTypeRegex.Match(definition);
                return match.Success ? match.Groups[1].Value : string.Empty;
            }

            private static List<int> ParseReferences(string definition)
            {
                var result = new List<int>();
                foreach (Match match in ReferenceRegex.Matches(definition))
                {
                    if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                        result.Add(id);
                }

                return result;
            }

            private static bool TryParseColour(string definition, out StepColor color)
            {
                var match = ColourRegex.Match(definition);
                if (!match.Success)
                {
                    color = default;
                    return false;
                }

                color = new StepColor(
                    ParseDouble(match.Groups[1].Value),
                    ParseDouble(match.Groups[2].Value),
                    ParseDouble(match.Groups[3].Value));
                return true;
            }

            private static bool TryParsePoint(string definition, out Vec3d point)
            {
                var match = CartesianPointRegex.Match(definition);
                if (!match.Success)
                {
                    point = default;
                    return false;
                }

                var parts = match.Groups[1].Value.Split(',');
                if (parts.Length < 3)
                {
                    point = default;
                    return false;
                }

                point = new Vec3d(
                    ParseDouble(parts[0]),
                    ParseDouble(parts[1]),
                    ParseDouble(parts[2]));
                return true;
            }

            private static string ReplacePointCoordinate(string definition, int axis, double value)
            {
                var match = CartesianPointRegex.Match(definition);
                if (!match.Success)
                    return definition;

                var parts = match.Groups[1].Value.Split(',');
                if (parts.Length < 3)
                    return definition;

                parts[axis] = " " + value.ToString("G17", CultureInfo.InvariantCulture) + " ";
                string replacement = string.Join(",", parts);
                return definition.Substring(0, match.Groups[1].Index) +
                    replacement +
                    definition.Substring(match.Groups[1].Index + match.Groups[1].Length);
            }

            private static double ParseDouble(string text)
            {
                return double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        private sealed class StepEntity
        {
            public int Id { get; set; }
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public string Definition { get; set; }
            public string Type { get; set; }
            public List<int> References { get; set; } = new List<int>();
        }

        private sealed class TemplateTextProjectionDetection
        {
            public int ViewIndex { get; set; }
            public StepWatermarkMarkedRegion Region { get; set; }
        }

        private struct StepColor
        {
            public readonly double R;
            public readonly double G;
            public readonly double B;

            public StepColor(double r, double g, double b)
            {
                R = r;
                G = g;
                B = b;
            }

            public double Luminance => 0.2126 * R + 0.7152 * G + 0.0722 * B;
            public double ChannelSpread => Math.Max(R, Math.Max(G, B)) - Math.Min(R, Math.Min(G, B));

            public override string ToString()
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "rgb({0:0.###},{1:0.###},{2:0.###}), lum={3:0.###}",
                    R,
                    G,
                    B,
                    Luminance);
            }
        }

        private struct Vec3d
        {
            public readonly double X;
            public readonly double Y;
            public readonly double Z;

            public Vec3d(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public double Get(int axis)
            {
                switch (axis)
                {
                    case 0: return X;
                    case 1: return Y;
                    case 2: return Z;
                    default: throw new ArgumentOutOfRangeException(nameof(axis));
                }
            }
        }

        private struct Bounds
        {
            private bool _initialized;
            private Vec3d _min;
            private Vec3d _max;

            public Vec3d Min => _min;
            public Vec3d Max => _max;
            public Vec3d Size => new Vec3d(
                _max.X - _min.X,
                _max.Y - _min.Y,
                _max.Z - _min.Z);

            public void Include(Vec3d point)
            {
                if (!_initialized)
                {
                    _min = point;
                    _max = point;
                    _initialized = true;
                    return;
                }

                _min = new Vec3d(
                    Math.Min(_min.X, point.X),
                    Math.Min(_min.Y, point.Y),
                    Math.Min(_min.Z, point.Z));
                _max = new Vec3d(
                    Math.Max(_max.X, point.X),
                    Math.Max(_max.Y, point.Y),
                    Math.Max(_max.Z, point.Z));
            }

            public void Include(Bounds bounds)
            {
                Include(bounds.Min);
                Include(bounds.Max);
            }
        }
    }
}
