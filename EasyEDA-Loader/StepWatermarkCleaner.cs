using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyEDA_Loader
{
    public sealed class StepWatermarkCleanerOptions
    {
        public double WatermarkMinLuminance { get; set; } = 0.92;
        public double EmbeddedWatermarkMinLuminance { get; set; } = 0.62;
        public double BodyMaxLuminance { get; set; } = 0.46;
        public double NeutralBodyMaxLuminance { get; set; } = 0.78;
        public double NeutralMaxChannelSpread { get; set; } = 0.16;
        public double MaxFaceAreaRatio { get; set; } = 0.18;
        public double MaxFaceDiagonalRatio { get; set; } = 0.45;
        public double ThinSolidMaxThickness { get; set; } = 0.01;
        public double ThinSolidMaxSize { get; set; } = 4.0;
        public double EmbeddedReliefMaxDepth { get; set; } = 0.01;
        public double HostPlaneSearchDistance { get; set; } = 0.02;
        public double HostPlaneProjectionPadding { get; set; } = 0.05;
        public double HostLoopAdjacentMaxDepth { get; set; } = 0.08;
        public double PlaneTolerance { get; set; } = 0.0002;
        public bool RequireDarkOwner { get; set; } = true;
        public bool RemoveEmbeddedWatermarkTopology { get; set; } = true;
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
        public IReadOnlyList<string> Diagnostics { get; internal set; }
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

        public static StepWatermarkCleanerReport CleanWithReport(string stepText, StepWatermarkCleanerOptions options = null)
        {
            if (stepText == null)
                throw new ArgumentNullException(nameof(stepText));

            options = options ?? new StepWatermarkCleanerOptions();

            var data = StepData.Parse(stepText);
            data.BuildIndexes();

            var diagnostics = new List<string>();
            var solidIds = data.Entities.Values
                .Where(e => e.Type == "MANIFOLD_SOLID_BREP")
                .Select(e => e.Id)
                .ToList();

            var styledItems = BuildStyledItems(data);
            var styledByTarget = styledItems
                .GroupBy(s => s.TargetId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var faceOwners = BuildFaceOwnerMap(data, solidIds);
            var solidInfo = BuildSolidInfo(data, solidIds, faceOwners, styledByTarget, options);

            int styledFaceCount = styledItems.Count(s => data.GetTypeName(s.TargetId) == "ADVANCED_FACE");
            var edits = new Dictionary<int, string>();

            var removableSolids = FindRemovableWatermarkSolids(solidInfo, styledByTarget, options);
            RemoveSolidsFromShapeRepresentations(data, removableSolids, edits);

            var embeddedFaces = FindEmbeddedWatermarkFaces(data, styledItems, faceOwners, solidInfo, removableSolids, options);
            var flattenResult = FlattenEmbeddedWatermarkFaces(data, embeddedFaces, faceOwners, solidInfo, styledByTarget, options, edits);

            int removedEmbeddedFaces = 0;
            int removedHostLoops = 0;
            if (options.RemoveEmbeddedWatermarkTopology)
            {
                removedEmbeddedFaces = RemoveFacesFromClosedShells(data, flattenResult.FlattenedFaces, edits);
                removedHostLoops = RemoveFaceBounds(data, flattenResult.HostFaceBoundsToRemove, edits);
            }

            int recoloredCount = RecolorFlattenedFaces(data, flattenResult.FlattenedFaces, faceOwners, solidInfo, styledItems, edits);
            string cleaned = data.ApplyDefinitionEdits(edits);

            diagnostics.Add("Approach: remove thin neutral watermark solids, then flatten embedded neutral relief faces and merge their host-plane cut loops.");
            diagnostics.Add($"Removed thin watermark solids: {removableSolids.Count}");
            diagnostics.Add($"Embedded topology removal enabled: {options.RemoveEmbeddedWatermarkTopology}");
            diagnostics.Add($"Removed embedded watermark faces from shells: {removedEmbeddedFaces}");
            diagnostics.Add($"Removed host-face inner loops: {removedHostLoops}");
            if (removableSolids.Count > 0)
                diagnostics.Add("Removed solid ids: " + string.Join(", ", removableSolids.OrderBy(id => id).Select(id => "#" + id.ToString(CultureInfo.InvariantCulture))));

            foreach (var operation in flattenResult.Operations)
            {
                diagnostics.Add(
                    $"Flattened {operation.FaceCount} faces on solid #{operation.SolidId} along {AxisName(operation.Axis)} to {operation.TargetCoordinate.ToString("G17", CultureInfo.InvariantCulture)} using host face #{operation.HostFaceId?.ToString(CultureInfo.InvariantCulture) ?? "none"}.");
            }

            foreach (var info in solidInfo.Values.OrderBy(s => s.SolidId))
            {
                string body = info.ReplacementColor.HasValue
                    ? info.ReplacementColor.Value.ToString()
                    : "none";
                diagnostics.Add($"Solid #{info.SolidId}: faces={info.FaceIds.Count}, replacementStyle=#{info.ReplacementStyleId?.ToString(CultureInfo.InvariantCulture) ?? "none"}, replacementColor={body}");
            }

            return new StepWatermarkCleanerReport
            {
                CleanedStep = cleaned,
                SolidCount = solidIds.Count,
                StyledFaceCount = styledFaceCount,
                CandidateFaceCount = embeddedFaces.Count,
                RecoloredFaceCount = recoloredCount,
                RemovedSolidCount = removableSolids.Count,
                FlattenedFaceCount = flattenResult.FlattenedFaceCount,
                FlattenedPointCount = flattenResult.FlattenedPointCount,
                Diagnostics = diagnostics
            };
        }

        private static HashSet<int> FindRemovableWatermarkSolids(
            Dictionary<int, SolidInfo> solidInfo,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            var result = new HashSet<int>();

            foreach (var info in solidInfo.Values)
            {
                if (!info.Bounds.HasValue)
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
                        if (IsWatermarkColor(faceStyle.Color.Value, options))
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

        private static List<int> FindEmbeddedWatermarkFaces(
            StepData data,
            List<StyledItemInfo> styledItems,
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            HashSet<int> removableSolids,
            StepWatermarkCleanerOptions options)
        {
            var result = new List<int>();
            var seenFaces = new HashSet<int>();

            foreach (var styledItem in styledItems)
            {
                if (data.GetTypeName(styledItem.TargetId) != "ADVANCED_FACE")
                    continue;

                if (!styledItem.Color.HasValue || !IsEmbeddedWatermarkColor(styledItem.Color.Value, options))
                    continue;

                if (!faceOwners.TryGetValue(styledItem.TargetId, out int ownerSolidId))
                    continue;

                if (removableSolids.Contains(ownerSolidId))
                    continue;

                if (!solidInfo.TryGetValue(ownerSolidId, out var ownerInfo))
                    continue;

                if (!ownerInfo.ReplacementStyleId.HasValue || !ownerInfo.ReplacementColor.HasValue)
                    continue;

                if (options.RequireDarkOwner && ownerInfo.ReplacementColor.Value.Luminance > options.NeutralBodyMaxLuminance)
                    continue;

                var faceBounds = data.GetBounds(styledItem.TargetId);
                if (!faceBounds.HasValue || !ownerInfo.Bounds.HasValue)
                    continue;

                if (!LooksLikeSmallMark(faceBounds.Value, ownerInfo.Bounds.Value, options))
                    continue;

                if (seenFaces.Add(styledItem.TargetId))
                    result.Add(styledItem.TargetId);
            }

            return result;
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
                    var componentPointIds = new HashSet<int>();
                    foreach (int faceId in componentFaces)
                    {
                        foreach (int pointId in data.GetPointIds(faceId, includeSurface: false))
                            componentPointIds.Add(pointId);
                    }

                    var componentBounds = data.GetBoundsFromPointIds(componentPointIds);
                    if (!componentBounds.HasValue)
                        continue;

                    var host = ChooseHostPlane(data, ownerInfo, componentFaces, componentBounds.Value, styledByTarget, options);
                    if (host == null)
                        continue;

                    var hostBoundsToRemove = new HashSet<int>();
                    foreach (int boundId in data.GetMatchingInnerFaceBounds(host.HostFaceId, componentBounds.Value, host.Axis, options.HostPlaneProjectionPadding))
                        hostBoundsToRemove.Add(boundId);

                    if (hostBoundsToRemove.Count > 0)
                    {
                        foreach (int boundId in ExpandHostFaceBounds(data, host.HostFaceId, hostBoundsToRemove, options))
                            hostBoundsToRemove.Add(boundId);
                    }

                    var facesToRemove = new HashSet<int>(componentFaces);
                    if (hostBoundsToRemove.Count > 0)
                    {
                        foreach (int adjacentFaceId in FindHostLoopAdjacentFaces(data, ownerInfo, host, hostBoundsToRemove, options))
                            facesToRemove.Add(adjacentFaceId);
                    }

                    var editPointIds = new HashSet<int>();
                    foreach (int faceId in componentFaces)
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

                    if (changedPoints == 0)
                    {
                        if (hostBoundsToRemove.Count == 0)
                            continue;
                    }

                    int addedFaceCount = 0;
                    foreach (int faceId in facesToRemove)
                    {
                        if (result.FlattenedFaces.Add(faceId))
                            addedFaceCount++;
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
                        FaceCount = componentFaces.Count,
                        HostFaceId = host.HostFaceId
                    });
                }
            }

            return result;
        }

        private static HostPlaneMatch ChooseHostPlane(
            StepData data,
            SolidInfo ownerInfo,
            HashSet<int> componentFaces,
            Bounds componentBounds,
            Dictionary<int, List<StyledItemInfo>> styledByTarget,
            StepWatermarkCleanerOptions options)
        {
            HostPlaneMatch best = null;

            for (int axis = 0; axis < 3; axis++)
            {
                double reliefSize = componentBounds.Size.Get(axis);
                if (reliefSize > options.EmbeddedReliefMaxDepth)
                    continue;

                var host = FindHostPlaneForAxis(data, ownerInfo, componentFaces, componentBounds, axis, styledByTarget, options);
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
                        if (color.Value.Luminance > options.NeutralBodyMaxLuminance)
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

            var allInnerBounds = data.GetInnerFaceBounds(hostFaceId).ToList();
            if (allInnerBounds.Count == 0 || allInnerBounds.Count == seedBounds.Count)
                return result;

            var hostBounds = data.GetBounds(hostFaceId.Value);
            var innerBounds = UnionBounds(data, allInnerBounds);
            if (!hostBounds.HasValue || !innerBounds.HasValue)
                return result;

            if (!LooksLikeSmallMark(innerBounds.Value, hostBounds.Value, options))
                return result;

            foreach (int boundId in allInnerBounds)
                result.Add(boundId);

            return result;
        }

        private static HashSet<int> FindHostLoopAdjacentFaces(
            StepData data,
            SolidInfo ownerInfo,
            HostPlaneMatch host,
            HashSet<int> hostBoundIds,
            StepWatermarkCleanerOptions options)
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

                    if (!IsShallowFaceInHostLoopRegion(data, faceId, loopBounds.Value, host, options))
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

                        if (!IsShallowFaceInHostLoopRegion(data, neighborFaceId, loopBounds.Value, host, options))
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
            StepWatermarkCleanerOptions options)
        {
            var faceBounds = data.GetBounds(faceId);
            if (!faceBounds.HasValue)
                return false;

            if (!ProjectionIntersects(faceBounds.Value, loopBounds, host.Axis, options.HostPlaneProjectionPadding))
                return false;

            double minDistance = Math.Abs(faceBounds.Value.Min.Get(host.Axis) - host.TargetCoordinate);
            double maxDistance = Math.Abs(faceBounds.Value.Max.Get(host.Axis) - host.TargetCoordinate);
            return Math.Max(minDistance, maxDistance) <= options.HostLoopAdjacentMaxDepth;
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
            Dictionary<int, int> faceOwners,
            Dictionary<int, SolidInfo> solidInfo,
            List<StyledItemInfo> styledItems,
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

                if (!ownerInfo.ReplacementStyleId.HasValue)
                    continue;

                string newDefinition = ReplaceFirstReference(styledItem.Entity.Definition, styledItem.StyleId, ownerInfo.ReplacementStyleId.Value);
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

        private static bool IsEmbeddedWatermarkColor(StepColor color, StepWatermarkCleanerOptions options)
        {
            return color.Luminance >= options.EmbeddedWatermarkMinLuminance &&
                color.ChannelSpread <= options.NeutralMaxChannelSpread;
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

                foreach (int faceId in faceIds)
                {
                    if (!styledByTarget.TryGetValue(faceId, out var faceStyles))
                        continue;

                    foreach (var faceStyle in faceStyles)
                    {
                        if (!faceStyle.Color.HasValue)
                            continue;

                        if (faceStyle.Color.Value.Luminance > options.NeutralBodyMaxLuminance)
                            continue;

                        if (IsWatermarkColor(faceStyle.Color.Value, options))
                            continue;

                        if (!styleCounts.TryGetValue(faceStyle.StyleId, out var use))
                        {
                            use = new StyleUse
                            {
                                StyleId = faceStyle.StyleId,
                                Color = faceStyle.Color.Value
                            };
                            styleCounts.Add(faceStyle.StyleId, use);
                        }

                        use.Count++;
                    }
                }

                int? replacementStyleId = null;
                StepColor? replacementColor = null;

                var dominant = styleCounts.Values
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
                        s.Color.Value.Luminance <= options.NeutralBodyMaxLuminance &&
                        !IsWatermarkColor(s.Color.Value, options));
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
                else if (type == "MANIFOLD_SOLID_BREP")
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
