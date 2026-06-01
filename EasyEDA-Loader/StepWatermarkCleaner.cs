using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        public bool UseMarkedRegionsOnly { get; set; }
        public double MarkedRegionPaddingPixels { get; set; } = 8.0;
        public double MarkedCandidateMinOverlap { get; set; } = 0.05;
        public double MarkedLoopMinOverlap { get; set; } = 0.25;
        public double AutomaticClusterGapRatio { get; set; } = 0.06;
        public int AutomaticClusterMinFaceCount { get; set; } = 3;
        public int AutomaticClusterMinPointCount { get; set; } = 24;
        public bool RequireKnownWatermarkPattern { get; set; } = true;
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
    }

    public sealed class StepWatermarkCleanerReport
    {
        public string CleanedStep { get; internal set; }
        public int SolidCount { get; internal set; }
        public int StyledFaceCount { get; internal set; }
        public int CandidateFaceCount { get; internal set; }
        public int RecoloredFaceCount { get; internal set; }
        public int RemovedSolidCount { get; internal set; }
        public int FlattenedFaceCount { get; internal set; }
        public int FlattenedPointCount { get; internal set; }
        public StepWatermarkDetectionReport DetectionReport { get; internal set; }
        public IReadOnlyList<string> Diagnostics { get; internal set; }
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
    }

    public sealed class StepWatermarkHostLoopDetection
    {
        public int HostFaceId { get; internal set; }
        public int BoundId { get; internal set; }
        public string ProjectionAxis { get; internal set; }
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

        public static byte[] Clean(byte[] stepData, StepWatermarkCleanerOptions options = null)
        {
            if (stepData == null)
                throw new ArgumentNullException(nameof(stepData));

            var text = Encoding.Latin1.GetString(stepData);
            var report = CleanWithReport(text, options);
            return Encoding.Latin1.GetBytes(report.CleanedStep);
        }

        public static string Clean(string stepText, StepWatermarkCleanerOptions options = null)
        {
            return CleanWithReport(stepText, options).CleanedStep;
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
            var context = BuildCleanupContext(stepText, options);
            var detection = DetectAutomaticWatermarks(context);
            return BuildPublicDetectionReport(context, detection);
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
            var context = BuildCleanupContext(stepText, options);

            var detection = DetectAutomaticWatermarks(context);
            return CleanWithAutomaticDetection(stepText, context, detection);
        }

        private static StepWatermarkCleanerReport CleanWithAutomaticDetection(
            string stepText,
            CleanupContext context,
            AutomaticWatermarkDetection detection)
        {
            var edits = new Dictionary<int, string>();
            var data = context.Data;
            var options = context.Options;

            RemoveSolidsFromShapeRepresentations(data, detection.RemovableSolidIds, edits);

            var flattenResult = FlattenEmbeddedWatermarkFaces(
                data,
                detection.EmbeddedFaceIds,
                context.FaceOwners,
                context.SolidInfo,
                context.StyledByTarget,
                options,
                edits);

            AddEmbeddedFaceHostLoopsToFlattenResult(
                data,
                detection.EmbeddedFaceIds,
                context.FaceOwners,
                context.SolidInfo,
                context.StyledByTarget,
                flattenResult,
                options,
                edits);

            MergeHostFaceBounds(flattenResult, detection.HostFaceBoundsToRemove);
            AddAutomaticRegionAdjacentFacesToFlattenResult(data, context.SolidInfo, flattenResult, detection.AutomaticRegions, options);

            FlattenCoplanarWatermarkFaces(
                data,
                detection.CoplanarFaceIds,
                context.FaceOwners,
                context.SolidInfo,
                context.StyledByTarget,
                flattenResult,
                options,
                edits);

            int removedEmbeddedFaces = 0;
            int removedHostLoops = 0;
            int recoloredCount = 0;
            if (options.RemoveEmbeddedWatermarkTopology)
            {
                removedEmbeddedFaces = RemoveFacesFromClosedShells(data, flattenResult.FlattenedFaces, edits);
                removedHostLoops = RemoveFaceBounds(data, flattenResult.HostFaceBoundsToRemove, edits);
                recoloredCount = RecolorFlattenedFaces(
                    data,
                    flattenResult.FlattenedFaces,
                    flattenResult.ReplacementFaceByRemovedFace,
                    context.FaceOwners,
                    context.SolidInfo,
                    context.StyledItems,
                    context.StyledByTarget,
                    edits);
                RemoveStyledItemsForRemovedFaces(data, context.StyledItems, flattenResult.FlattenedFaces, edits);
            }

            string cleaned = data.ApplyDefinitionEdits(edits);
            var diagnostics = new List<string>();

            diagnostics.Add("Approach: remove thin neutral watermark solids, then flatten embedded neutral relief faces and merge their host-plane cut loops.");
            diagnostics.Add("Stage 1 detection: pattern-gated automatic detection; marked rectangles are not used.");
            diagnostics.Add($"Detected thin watermark solids: {detection.RemovableSolidIds.Count}");
            diagnostics.Add($"Detected embedded watermark faces: {detection.EmbeddedFaceIds.Count}");
            diagnostics.Add($"Detected coplanar watermark faces: {detection.CoplanarFaceIds.Count}");
            diagnostics.Add($"Embedded topology removal enabled: {options.RemoveEmbeddedWatermarkTopology}");
            diagnostics.Add($"Removed embedded watermark faces from shells: {removedEmbeddedFaces}");
            diagnostics.Add($"Removed host-face inner loops: {removedHostLoops}");
            diagnostics.Add($"Automatic removable-solid host-face loops: {detection.RemovableSolidHostLoopCount}");
            diagnostics.Add($"Automatic removable-solid host-face candidates: {detection.RemovableSolidHostLoopCandidateCount}");
            diagnostics.Add($"Automatic embedded host-face loops: {detection.EmbeddedHostLoopCount}");
            diagnostics.Add($"Automatic host-face loop candidates: {detection.HostLoopCandidateCount}");
            diagnostics.Add($"Automatic host-face watermark loops: {detection.HostLoopCount}");
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
                SolidCount = context.SolidIds.Count,
                StyledFaceCount = context.StyledFaceCount,
                CandidateFaceCount = detection.EmbeddedFaceIds.Count + detection.CoplanarFaceIds.Count + detection.HostLoopCount,
                RecoloredFaceCount = recoloredCount,
                RemovedSolidCount = detection.RemovableSolidIds.Count,
                FlattenedFaceCount = flattenResult.FlattenedFaceCount,
                FlattenedPointCount = flattenResult.FlattenedPointCount,
                DetectionReport = BuildPublicDetectionReport(context, detection),
                Diagnostics = diagnostics
            };
        }

        private static CleanupContext BuildCleanupContext(string stepText, StepWatermarkCleanerOptions options)
        {
            var data = StepData.Parse(stepText);
            data.BuildIndexes();

            var solidIds = data.Entities.Values
                .Where(e => IsCleanupOwnerRootType(e.Type))
                .Select(e => e.Id)
                .ToList();

            var styledItems = BuildStyledItems(data);
            var styledByTarget = styledItems
                .GroupBy(s => s.TargetId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var faceOwners = BuildFaceOwnerMap(data, solidIds);
            var solidInfo = BuildSolidInfo(data, solidIds, faceOwners, styledByTarget, options);
            int styledFaceCount = styledItems.Count(s => data.GetTypeName(s.TargetId) == "ADVANCED_FACE");

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

        private static AutomaticWatermarkDetection DetectAutomaticWatermarks(CleanupContext context)
        {
            var detection = new AutomaticWatermarkDetection();
            var data = context.Data;
            var options = context.Options;

            detection.RemovableSolidIds = FindRemovableWatermarkSolids(
                data,
                context.SolidInfo,
                context.StyledByTarget,
                GetModelBounds(context),
                options);

            var removableSolidHostLoops = FindRemovableSolidHostLoops(
                data,
                detection.RemovableSolidIds,
                context.SolidInfo,
                context.StyledByTarget,
                GetModelBounds(context),
                options);
            detection.AutomaticRegions.AddRange(removableSolidHostLoops.Regions);
            MergeHostFaceBounds(detection.HostFaceBoundsToRemove, removableSolidHostLoops.HostFaceBoundsToRemove);
            detection.RemovableSolidHostLoopCount = CountHostLoopBounds(removableSolidHostLoops.HostFaceBoundsToRemove);
            detection.RemovableSolidHostLoopCandidateCount = removableSolidHostLoops.CandidateCount;

            detection.EmbeddedFaceIds = FindEmbeddedWatermarkFaces(
                data,
                context.StyledItems,
                context.FaceOwners,
                context.SolidInfo,
                detection.RemovableSolidIds,
                context.StyledByTarget,
                options);

            var embeddedHostLoops = FindAutomaticEmbeddedHostLoops(
                data,
                detection.EmbeddedFaceIds,
                context.FaceOwners,
                context.SolidInfo,
                context.StyledByTarget,
                options);
            detection.EmbeddedHostLoopCount = CountHostLoopBounds(embeddedHostLoops.HostFaceBoundsToRemove);
            detection.AutomaticRegions.AddRange(embeddedHostLoops.Regions);
            MergeHostFaceBounds(detection.HostFaceBoundsToRemove, embeddedHostLoops.HostFaceBoundsToRemove);

            var automaticHostLoops = FindAutomaticWatermarkHostLoops(
                data,
                context.SolidInfo,
                context.StyledByTarget,
                options);
            detection.HostLoopCandidateCount = automaticHostLoops.CandidateCount;
            detection.AutomaticRegions.AddRange(automaticHostLoops.Regions);
            MergeHostFaceBounds(detection.HostFaceBoundsToRemove, automaticHostLoops.HostFaceBoundsToRemove);

            detection.CoplanarFaceIds = FindAutomaticRegionWatermarkFaces(
                data,
                context.StyledItems,
                context.FaceOwners,
                automaticHostLoops.Regions,
                options);
            detection.CoplanarFaceIds = detection.CoplanarFaceIds
                .Concat(FindNeutralCoplanarWatermarkFaces(
                    data,
                    context.StyledItems,
                    context.FaceOwners,
                    context.SolidInfo,
                    context.StyledByTarget,
                    options))
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            detection.HostLoopCount = CountHostLoopBounds(detection.HostFaceBoundsToRemove);

            return detection;
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
                AddSolidDetectionRegion(context, regions, seenRegions, solidId, modelBounds);

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

            var diagnostics = new List<string>
            {
                "Stage 1 detection only: pattern-gated automatic detection; marked rectangles are ignored.",
                "Detected thin watermark solids: " + detection.RemovableSolidIds.Count.ToString(CultureInfo.InvariantCulture),
                "Detected embedded watermark faces: " + detection.EmbeddedFaceIds.Count.ToString(CultureInfo.InvariantCulture),
                "Detected coplanar watermark faces: " + detection.CoplanarFaceIds.Count.ToString(CultureInfo.InvariantCulture),
                "Detected host-face watermark loops: " + detection.HostLoopCount.ToString(CultureInfo.InvariantCulture)
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
            Bounds? modelBounds)
        {
            var bounds = context.Data.GetBounds(solidId);
            if (!bounds.HasValue || !modelBounds.HasValue)
                return;

            AddDetectionRegion(
                regions,
                seenRegions,
                solidId,
                "solid",
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
            string key = kind + "|" + entityId.ToString(CultureInfo.InvariantCulture);
            if (!seenRegions.Add(key))
                return;

            regions.Add(new StepWatermarkRegionDetection
            {
                EntityId = entityId,
                Kind = kind,
                ViewName = viewName
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
            if (options.RemoveEmbeddedWatermarkTopology)
            {
                removedEmbeddedFaces = RemoveFacesFromClosedShells(data, flattenResult.FlattenedFaces, edits);
                removedHostLoops = RemoveFaceBounds(data, flattenResult.HostFaceBoundsToRemove, edits);
                RemoveStyledItemsForRemovedFaces(data, styledItems, flattenResult.FlattenedFaces, edits);
            }

            int recoloredCount = 0;

            string cleaned = data.ApplyDefinitionEdits(edits);

            diagnostics.Add("Removed marked thin watermark solids: " + removableSolids.Count.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Marked styled watermark faces: " + embeddedFaces.Count.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Marked host loops selected: " + markedHostLoopCount.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Removed embedded watermark faces from shells: " + removedEmbeddedFaces.ToString(CultureInfo.InvariantCulture));
            diagnostics.Add("Removed host-face inner loops: " + removedHostLoops.ToString(CultureInfo.InvariantCulture));
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

                    if (!LooksLikePotentialAutomaticHostFace(faceId, styledByTarget, options))
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
                            AllowStandaloneLoop = IsLightNeutralHostFace(faceId, styledByTarget, options)
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

                    var hostBoundsToRemove = new HashSet<int>();
                    foreach (int boundId in data.GetMatchingInnerFaceBounds(host.HostFaceId, componentBounds.Value, host.Axis, options.HostPlaneProjectionPadding)
                        .Where(boundId => EntityInsideDetectedRegion(data, boundId, componentBounds.Value, host.Axis, options.HostPlaneProjectionPadding)))
                        hostBoundsToRemove.Add(boundId);

                    if (hostBoundsToRemove.Count > 0)
                    {
                        foreach (int boundId in ExpandHostFaceBounds(data, host.HostFaceId, hostBoundsToRemove, options))
                            hostBoundsToRemove.Add(boundId);
                    }

                    bool removedBoundaryHostBounds = false;
                    if (hostBoundsToRemove.Count > 0 && host.HostFaceId.HasValue)
                    {
                        var hostFaceBounds = data.GetBounds(host.HostFaceId.Value);
                        if (hostFaceBounds.HasValue)
                        {
                            double hostEdgeMargin = GetAutomaticEdgeMargin(hostFaceBounds.Value, host.Axis);
                            var filteredHostBounds = new HashSet<int>();
                            foreach (int boundId in hostBoundsToRemove)
                            {
                                var boundBounds = data.GetBounds(boundId);
                                if (boundBounds.HasValue &&
                                    TouchesProjectedBoundary(boundBounds.Value, hostFaceBounds.Value, host.Axis, hostEdgeMargin))
                                {
                                    removedBoundaryHostBounds = true;
                                    continue;
                                }

                                filteredHostBounds.Add(boundId);
                            }

                            hostBoundsToRemove = filteredHostBounds;
                        }
                    }

                    if (removedBoundaryHostBounds && hostBoundsToRemove.Count == 0)
                        continue;

                    var facesToRemove = new HashSet<int>(removableComponentFaces);
                    if (hostBoundsToRemove.Count > 0)
                    {
                        var hostFaceBounds = host.HostFaceId.HasValue
                            ? data.GetBounds(host.HostFaceId.Value)
                            : null;
                        double hostEdgeMargin = hostFaceBounds.HasValue
                            ? GetAutomaticEdgeMargin(hostFaceBounds.Value, host.Axis)
                            : 0.0;
                        foreach (int adjacentFaceId in FindHostLoopAdjacentFaces(data, ownerInfo, host, hostBoundsToRemove, options, componentBounds.Value))
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
                        if (hostBoundsToRemove.Count == 0 && !coplanarOverlay)
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

                    foreach (int boundId in hostBoundsToRemove)
                    {
                        if (!result.HostFaceBoundsToRemove.TryGetValue(host.HostFaceId.Value, out var boundIds))
                        {
                            boundIds = new HashSet<int>();
                            result.HostFaceBoundsToRemove.Add(host.HostFaceId.Value, boundIds);
                        }

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

            foreach (int faceId in ownerInfo.FaceIds)
            {
                if (componentFaces.Contains(faceId))
                    continue;

                var faceBounds = data.GetBounds(faceId);
                if (!faceBounds.HasValue)
                    continue;

                if (faceBounds.Value.Size.Get(axis) > options.PlaneTolerance)
                    continue;

                double coordinate = (faceBounds.Value.Min.Get(axis) + faceBounds.Value.Max.Get(axis)) / 2.0;
                double distance = Math.Min(
                    Math.Abs(coordinate - componentBounds.Min.Get(axis)),
                    Math.Abs(coordinate - componentBounds.Max.Get(axis)));

                if (distance > options.HostPlaneSearchDistance)
                    continue;

                if (!ProjectionIntersects(faceBounds.Value, componentBounds, axis, options.HostPlaneProjectionPadding))
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

                double area = ProjectedArea(faceBounds.Value.Size, axis);
                double score = colorWeight * area / Math.Max(distance, 0.0001);
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
            foreach (int faceId in faceIds)
            {
                foreach (int pointId in data.GetPointIds(faceId, includeSurface: false))
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

                    foreach (int pointId in data.GetPointIds(currentFaceId, includeSurface: false))
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

                string definition = entity.Definition;
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

        private static void RemoveStyledItemsForRemovedFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            HashSet<int> removedFaceIds,
            Dictionary<int, string> edits)
        {
            if (removedFaceIds.Count == 0)
                return;

            var styledItemIds = new HashSet<int>(
                styledItems
                    .Where(item => removedFaceIds.Contains(item.TargetId))
                    .Select(item => item.Entity.Id));
            if (styledItemIds.Count == 0)
                return;

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
                foreach (int styledItemId in styledItemIds)
                    definition = RemoveReferenceFromCommaList(definition, styledItemId);

                if (definition != entity.Definition)
                    edits[entity.Id] = definition;
            }
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
            return result;
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

        private sealed class AutomaticWatermarkDetection
        {
            public HashSet<int> RemovableSolidIds { get; set; } = new HashSet<int>();
            public List<int> EmbeddedFaceIds { get; set; } = new List<int>();
            public List<int> CoplanarFaceIds { get; set; } = new List<int>();
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
            public int? ReplacementStyleId { get; set; }
            public StepColor? ReplacementColor { get; set; }
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
            public HashSet<int> FlattenedFaces { get; } = new HashSet<int>();
            public Dictionary<int, int> ReplacementFaceByRemovedFace { get; } = new Dictionary<int, int>();
            public Dictionary<int, HashSet<int>> HostFaceBoundsToRemove { get; } = new Dictionary<int, HashSet<int>>();
            public List<FlattenOperation> Operations { get; } = new List<FlattenOperation>();
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
                if (edits == null || edits.Count == 0)
                    return Text;

                var builder = new StringBuilder(Text.Length);
                int cursor = 0;

                foreach (var entity in Entities.Values.OrderBy(e => e.StartIndex))
                {
                    if (!edits.TryGetValue(entity.Id, out string newDefinition))
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
                return builder.ToString();
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
