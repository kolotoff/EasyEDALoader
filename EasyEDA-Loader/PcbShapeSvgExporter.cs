using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace EasyEDA_Loader
{
    internal enum ShapeExportScope
    {
        AllComponents,
        SelectedComponent
    }

    internal sealed class ShapeExportResult
    {
        public int ComponentCount { get; set; }
        public int FileCount { get; set; }
        public int PrimitiveCount { get; set; }
        public string DiagnosticsPath { get; set; }
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<string> MissingMechanical2Footprints { get; } = new List<string>();
        public List<string> MissingFootprintNames { get; } = new List<string>();
        public ShapeCaptureStats CaptureStats { get; } = new ShapeCaptureStats();
    }

    internal sealed class ShapeCaptureStats
    {
        public int TotalPrimitives { get; set; }
        public int PlaceholderTextPrimitives { get; set; }
        public int TextPrimitives { get; set; }
        public int NotMechanical2Primitives { get; set; }
        public int Mechanical2Primitives { get; set; }
        public int CreatedPrimitives { get; set; }
        public int FailedConversions { get; set; }
        public Dictionary<string, int> LayerCounts { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, int> ObjectCounts { get; } = new Dictionary<int, int>();

        public void Add(ShapeCaptureStats other)
        {
            if (other == null)
                return;

            TotalPrimitives += other.TotalPrimitives;
            PlaceholderTextPrimitives += other.PlaceholderTextPrimitives;
            TextPrimitives += other.TextPrimitives;
            NotMechanical2Primitives += other.NotMechanical2Primitives;
            Mechanical2Primitives += other.Mechanical2Primitives;
            CreatedPrimitives += other.CreatedPrimitives;
            FailedConversions += other.FailedConversions;
            foreach (KeyValuePair<string, int> entry in other.LayerCounts)
                AddCount(LayerCounts, entry.Key, entry.Value);
            foreach (KeyValuePair<int, int> entry in other.ObjectCounts)
                AddCount(ObjectCounts, entry.Key, entry.Value);
        }

        private static void AddCount<TKey>(Dictionary<TKey, int> counts, TKey key, int value)
        {
            if (counts.TryGetValue(key, out int count))
                counts[key] = count + value;
            else
                counts[key] = value;
        }
    }

    internal sealed class ShapeExportProgress
    {
        public string Message { get; set; }
        public string Detail { get; set; }
        public double? Percent { get; set; }
    }

    internal static class PcbShapeSvgExporter
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private const double EmptyViewBoxSizeMm = 1.0;
        private const int MaxBoardIteratorObjects = 250000;
        private const int MaxGroupIteratorObjects = 50000;
        private const int MaxLibraryIteratorObjects = 50000;
        private const int MaxPrimitiveAtObjectsPerType = 10000;
        private const int MaxPrimitiveAtEmptyRun = 100;
        private const int MaxPolygonSegments = 10000;
        private const int MaxContourPoints = 10000;

        private sealed class ExportComponentItem
        {
            public ExportComponentItem(object component, string exportName)
            {
                Component = component;
                ExportName = exportName ?? "";
            }

            public object Component { get; }
            public string ExportName { get; }
        }

        private static readonly int[] ShapeObjectIds =
        {
            (int)TObjectId.eTrackObject,
            (int)TObjectId.eArcObject,
            (int)TObjectId.eFillObject,
            (int)TObjectId.eRegionObject,
            (int)TObjectId.ePolyObject,
            (int)TObjectId.eTextObject
        };

        public static ShapeExportResult Export(
            ShapeExportScope scope,
            string folder,
            IServerDocumentView commandView = null,
            Action<ShapeExportProgress> progress = null,
            bool diagnosticsEnabled = false,
            Func<bool> isCancellationRequested = null)
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("Export folder is required.", nameof(folder));

            Directory.CreateDirectory(folder);
            ThrowIfCancellationRequested(isCancellationRequested);

            IPCB_Library pcbLib = AltiumApi.GlobalVars.PCBServer.GetCurrentPCBLibrary();
            if (pcbLib != null)
                return ExportPcbLibrary(pcbLib, scope, folder, progress, diagnosticsEnabled, isCancellationRequested, null);

            IPCB_Board board = EEPCB.GetCurrentPcbBoard(commandView);
            if (board != null)
                return ExportPcbBoard(board, scope, folder, progress, diagnosticsEnabled, isCancellationRequested);

            throw new InvalidOperationException("Open a PCB document or PCB footprint library before exporting shapes.");
        }

        public static ShapeExportResult ExportLibrary(
            IPCB_Library pcbLib,
            string folder,
            Action<ShapeExportProgress> progress = null,
            bool diagnosticsEnabled = false,
            Func<bool> isCancellationRequested = null,
            HashSet<string> usedNames = null)
        {
            if (pcbLib == null)
                throw new ArgumentNullException(nameof(pcbLib));
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("Export folder is required.", nameof(folder));

            Directory.CreateDirectory(folder);
            ThrowIfCancellationRequested(isCancellationRequested);
            return ExportPcbLibrary(
                pcbLib,
                ShapeExportScope.AllComponents,
                folder,
                progress,
                diagnosticsEnabled,
                isCancellationRequested,
                usedNames);
        }

        private static ShapeExportResult ExportPcbLibrary(
            IPCB_Library pcbLib,
            ShapeExportScope scope,
            string folder,
            Action<ShapeExportProgress> progress,
            bool diagnosticsEnabled,
            Func<bool> isCancellationRequested,
            HashSet<string> usedNames)
        {
            IEnumerable<object> components = scope == ShapeExportScope.SelectedComponent
                ? new[] { GetCurrentPcbLibExportComponent(pcbLib) }.Where(component => component != null)
                : EnumeratePcbLibExportComponents(pcbLib);

            return ExportComponents(components, folder, "Footprint", preferFootprintName: true, progress, diagnosticsEnabled, isCancellationRequested, usedNames);
        }

        private static ShapeExportResult ExportPcbBoard(IPCB_Board board, ShapeExportScope scope, string folder, Action<ShapeExportProgress> progress, bool diagnosticsEnabled, Func<bool> isCancellationRequested)
        {
            ThrowIfCancellationRequested(isCancellationRequested);
            progress?.Invoke(new ShapeExportProgress
            {
                Message = scope == ShapeExportScope.AllComponents ? "Collecting unique PCB footprints..." : "Collecting selected PCB component...",
                Detail = "PCB editor"
            });

            IEnumerable<object> components = scope == ShapeExportScope.SelectedComponent
                ? GetSelectedBoardComponents(board)
                : EnumerateBoardComponents(board);

            if (scope == ShapeExportScope.AllComponents)
                components = UniqueBoardComponentsByFootprint(components);

            return ExportComponents(components, folder, "Component", preferFootprintName: true, progress, diagnosticsEnabled, isCancellationRequested);
        }

        private static ShapeExportResult ExportComponents(
            IEnumerable<object> components,
            string folder,
            string fallbackPrefix,
            bool preferFootprintName,
            Action<ShapeExportProgress> progress,
            bool diagnosticsEnabled,
            Func<bool> isCancellationRequested,
            HashSet<string> usedNames = null)
        {
            var result = new ShapeExportResult();
            HashSet<string> outputNames = usedNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var diagnostics = diagnosticsEnabled ? ShapeExportDiagnostics.Create(folder, fallbackPrefix, preferFootprintName) : null;
            result.DiagnosticsPath = diagnostics?.FilePath;
            progress?.Invoke(new ShapeExportProgress
            {
                Message = "Collecting components...",
                Detail = fallbackPrefix
            });
            var exportComponents = new List<object>();
            foreach (object component in components)
            {
                ThrowIfCancellationRequested(isCancellationRequested);
                if (component != null)
                    exportComponents.Add(component);
            }
            progress?.Invoke(new ShapeExportProgress
            {
                Message = "Exporting shapes...",
                Detail = exportComponents.Count.ToString(CultureInfo.InvariantCulture) + " component(s)",
                Percent = exportComponents.Count == 0 ? 100.0 : 0.0
            });

            try
            {
                for (int index = 0; index < exportComponents.Count; index++)
                {
                    ThrowIfCancellationRequested(isCancellationRequested);
                    object exportComponent = exportComponents[index];
                    object component = UnwrapExportComponent(exportComponent);
                    result.ComponentCount++;
                    string componentName = ReadComponentExportName(exportComponent, preferFootprintName, fallbackPrefix + result.ComponentCount.ToString(CultureInfo.InvariantCulture));
                    try
                    {
                        if (preferFootprintName && string.Equals(fallbackPrefix, "Footprint", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ReadFootprintName(exportComponent)))
                        {
                            string identity = ComponentDebugIdentity(exportComponent);
                            result.MissingFootprintNames.Add(identity);
                            result.Warnings.Add(identity + ": no readable PCB library footprint name found.");
                            continue;
                        }

                        progress?.Invoke(new ShapeExportProgress
                        {
                            Message = "Exporting " + componentName,
                            Detail = (index + 1).ToString(CultureInfo.InvariantCulture) + " of " + exportComponents.Count.ToString(CultureInfo.InvariantCulture),
                            Percent = exportComponents.Count == 0 ? 100.0 : index * 100.0 / exportComponents.Count
                        });

                        List<SvgPrimitive> primitives = CaptureMechanicalShapePrimitives(component, diagnostics, componentName, out ShapeCaptureStats captureStats, isCancellationRequested);
                        result.CaptureStats.Add(captureStats);
                        if (primitives.Count == 0)
                        {
                            string footprintName = FirstNonEmpty(ReadFootprintName(exportComponent), componentName);
                            result.MissingMechanical2Footprints.Add(footprintName);
                            result.Warnings.Add(componentName + ": no Mechanical 2 shape primitives found.");
                            progress?.Invoke(new ShapeExportProgress
                            {
                                Message = "Skipped " + componentName,
                                Detail = "No Mechanical 2 shape primitives found",
                                Percent = (index + 1) * 100.0 / exportComponents.Count
                            });
                            continue;
                        }

                        string fileName = UniqueFileName(SanitizeFileName(componentName), outputNames) + ".svg";
                        string filePath = Path.Combine(folder, fileName);
                        File.WriteAllText(filePath, BuildSvg(primitives), Utf8NoBom);
                        result.FileCount++;
                        result.PrimitiveCount += primitives.Count;
                        progress?.Invoke(new ShapeExportProgress
                        {
                            Message = "Exported " + componentName,
                            Detail = (index + 1).ToString(CultureInfo.InvariantCulture) + " of " + exportComponents.Count.ToString(CultureInfo.InvariantCulture),
                            Percent = (index + 1) * 100.0 / exportComponents.Count
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add(componentName + ": " + ex.Message);
                        EasyEDALoaderModule.Trace("Shape export failed for footprint " + componentName + ": " + ex);
                        progress?.Invoke(new ShapeExportProgress
                        {
                            Message = "Failed " + componentName,
                            Detail = ex.Message,
                            Percent = (index + 1) * 100.0 / exportComponents.Count
                        });
                    }
                }
            }
            finally
            {
                diagnostics?.Finish(result);
            }

            if (result.ComponentCount == 0)
                throw new InvalidOperationException("No components with a readable footprint pattern were found to export.");
            if (result.FileCount == 0 && result.MissingFootprintNames.Count > 0)
                throw new InvalidOperationException(BuildNoFootprintNamesMessage(result));
            if (result.FileCount == 0 && result.Errors.Count > 0)
                throw new InvalidOperationException(BuildExportErrorsMessage(result.Errors));
            if (result.FileCount == 0)
                throw new InvalidOperationException(BuildNoMechanical2Message(result));

            return result;
        }

        private static string BuildExportErrorsMessage(IEnumerable<string> errors)
        {
            List<string> messages = errors
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .Take(20)
                .ToList();
            var builder = new StringBuilder();
            builder.AppendLine("No SVG files were created because footprint export errors occurred.");
            builder.AppendLine();
            foreach (string message in messages)
                builder.AppendLine(message);
            return builder.ToString().TrimEnd();
        }

        private static string BuildNoMechanical2Message(ShapeExportResult result)
        {
            var names = result.MissingMechanical2Footprints
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count == 0)
                return "No Mechanical 2 shape primitives were found on the requested component(s).";

            const int maxNames = 25;
            var builder = new StringBuilder();
            builder.AppendLine("No Mechanical 2 shape primitives were found on the requested component(s).");
            AppendCaptureSummary(builder, result.CaptureStats);
            builder.AppendLine();
            builder.AppendLine("Footprint(s):");
            foreach (string name in names.Take(maxNames))
                builder.AppendLine(name);
            if (names.Count > maxNames)
                builder.AppendLine("... and " + (names.Count - maxNames).ToString(CultureInfo.InvariantCulture) + " more.");

            return builder.ToString().TrimEnd();
        }

        private static string BuildNoFootprintNamesMessage(ShapeExportResult result)
        {
            var names = result.MissingFootprintNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();

            var builder = new StringBuilder();
            builder.AppendLine("No readable PCB library footprint names were returned by Altium.");
            builder.AppendLine();
            builder.AppendLine("Export stopped to avoid creating Footprint*.svg files.");
            if (names.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Unnamed object(s):");
                foreach (string name in names)
                    builder.AppendLine(name);
                if (result.MissingFootprintNames.Count > names.Count)
                    builder.AppendLine("... and " + (result.MissingFootprintNames.Count - names.Count).ToString(CultureInfo.InvariantCulture) + " more.");
            }

            return builder.ToString().TrimEnd();
        }

        private static void AppendCaptureSummary(StringBuilder builder, ShapeCaptureStats stats)
        {
            if (stats == null)
                return;

            builder.AppendLine();
            builder.AppendLine("Capture summary:");
            builder.AppendLine("Total primitives: " + stats.TotalPrimitives.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Mechanical 2: " + stats.Mechanical2Primitives.ToString(CultureInfo.InvariantCulture)
                + ", created: " + stats.CreatedPrimitives.ToString(CultureInfo.InvariantCulture)
                + ", failed conversion: " + stats.FailedConversions.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Skipped text: " + stats.TextPrimitives.ToString(CultureInfo.InvariantCulture)
                + ", placeholders: " + stats.PlaceholderTextPrimitives.ToString(CultureInfo.InvariantCulture)
                + ", other layer: " + stats.NotMechanical2Primitives.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("Objects: " + FormatTopCounts(stats.ObjectCounts, 8));
            builder.AppendLine("Layers: " + FormatTopCounts(stats.LayerCounts, 8));
        }

        private static string FormatTopCounts<TKey>(Dictionary<TKey, int> counts, int limit)
        {
            if (counts == null || counts.Count == 0)
                return "<none>";

            var parts = counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => Convert.ToString(pair.Key, CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .Take(limit)
                .Select(pair => Convert.ToString(pair.Key, CultureInfo.InvariantCulture) + "=" + pair.Value.ToString(CultureInfo.InvariantCulture))
                .ToList();

            if (counts.Count > limit)
                parts.Add("...");

            return string.Join(", ", parts);
        }

        private static IEnumerable<object> UniqueBoardComponentsByFootprint(IEnumerable<object> components)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object component in components.Where(component => component != null))
            {
                string pattern = ReadFootprintName(component);
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                string key = "footprint:" + pattern.Trim();
                if (seen.Add(key))
                    yield return component;
            }
        }

        private static IEnumerable<object> EnumerateBoardComponents(IPCB_Board board)
        {
            return EnumerateBoardFullComponentFootprints(board);
        }

        private static List<object> EnumerateBoardFullComponentFootprints(IPCB_Board board)
        {
            var result = new List<object>();
            if (!(board is IPCB_BoardEx boardEx))
                return result;

            object fullComponents = SafeObjectCall(() => boardEx.Internal_GetState_FullComponents());
            object currentList = Invoke(fullComponents, "GetComponentsForCurrentVariant")
                ?? Invoke(fullComponents, "Internal_GetComponentsForCurrentVariant");
            AddFullComponentFootprints(result, currentList);

            if (result.Count == 0)
            {
                object allList = Invoke(fullComponents, "GetComponentsForAllVariants")
                    ?? Invoke(fullComponents, "Internal_GetComponentsForAllVariants");
                AddFullComponentFootprints(result, allList);
            }

            return result;
        }

        private static void AddFullComponentFootprints(List<object> result, object list)
        {
            if (list == null)
                return;

            int count = GetInt(list, "GetCount");
            if (count <= 0)
                return;

            AddFullComponentFootprints(result, list, count, startIndex: 0);
            if (result.Count == 0)
                AddFullComponentFootprints(result, list, count, startIndex: 1);
        }

        private static void AddFullComponentFootprints(List<object> result, object list, int count, int startIndex)
        {
            for (int i = 0; i < count; i++)
            {
                object fullComponent = Invoke(list, "GetItem", i + startIndex)
                    ?? Invoke(list, "Internal_GetItem", i + startIndex);
                object footprint = Invoke(fullComponent, "GetFootprint")
                    ?? Invoke(fullComponent, "Internal_GetFootprint");
                if (IsComponentObject(footprint))
                    result.Add(footprint);
            }
        }

        private static List<SvgPrimitive> CaptureMechanicalShapePrimitives(object component, ShapeExportDiagnostics diagnostics, string exportName, out ShapeCaptureStats stats, Func<bool> isCancellationRequested)
        {
            ThrowIfCancellationRequested(isCancellationRequested);
            stats = new ShapeCaptureStats();
            int originX = GetInt(component, "GetState_XLocation");
            int originY = GetInt(component, "GetState_YLocation");
            double rotation = IsBoardComponent(component) ? GetDouble(component, "GetState_Rotation") : 0.0;
            bool mirrored = IsBoardComponent(component) && GetBool(component, "GetState_FlippedOnLayer");
            string componentName = FirstNonEmpty(ReadFootprintName(component), ReadDesignator(component), ObjectIdentity(component));
            HashSet<string> excludedTextValues = BuildComponentTextExclusionSet(component);
            List<object> shapeObjects = EnumerateComponentShapeObjects(component).ToList();
            HashSet<string> repeatedBoardTextValues = BuildRepeatedBoardTextExclusionSet(shapeObjects);
            diagnostics?.BeginComponent(exportName, component, originX, originY, rotation, mirrored);

            var result = new List<SvgPrimitive>();
            var objectCounts = new Dictionary<int, int>();
            var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            int arcCandidates = 0;
            int mechanicalArcCandidates = 0;
            int createdArcs = 0;
            int failedArcs = 0;
            foreach (object primitive in shapeObjects)
            {
                ThrowIfCancellationRequested(isCancellationRequested);
                total++;
                int objectId = GetObjectId(primitive);
                string layerDescription = DescribePrimitiveLayer(primitive);
                bool isArcCandidate = HasArcFields(primitive);
                if (isArcCandidate)
                    arcCandidates++;
                IncrementCount(objectCounts, objectId);
                IncrementCount(layerCounts, layerDescription);
                stats.TotalPrimitives++;
                IncrementCount(stats.ObjectCounts, objectId);
                IncrementCount(stats.LayerCounts, layerDescription);
                bool placeholderText = IsExcludedTextPrimitive(primitive, objectId, excludedTextValues, repeatedBoardTextValues);
                bool mechanical2 = !placeholderText && IsMechanical2Primitive(primitive, objectId);
                string textValue = mechanical2 ? ReadExportTextString(primitive, objectId, excludedTextValues) : "";
                bool textPrimitive = !string.IsNullOrWhiteSpace(textValue);
                SvgPrimitive svgPrimitive = null;
                string conversion;
                if (placeholderText)
                {
                    stats.PlaceholderTextPrimitives++;
                    conversion = "skipped-placeholder-text";
                }
                else if (mechanical2)
                {
                    stats.Mechanical2Primitives++;
                    if (isArcCandidate)
                        mechanicalArcCandidates++;
                    svgPrimitive = textPrimitive
                        ? TryCreateTextPrimitive(primitive, textValue, originX, originY, rotation, mirrored)
                        : TryCreateSvgPrimitive(primitive, objectId, originX, originY, rotation, mirrored);
                    conversion = svgPrimitive == null ? "failed" : svgPrimitive.GetType().Name;
                    if (svgPrimitive == null)
                        stats.FailedConversions++;
                }
                else
                {
                    stats.NotMechanical2Primitives++;
                    conversion = "skipped-not-mechanical2";
                }

                if (mechanical2 && isArcCandidate && svgPrimitive == null)
                    failedArcs++;
                if (svgPrimitive != null)
                {
                    if (isArcCandidate)
                        createdArcs++;
                    stats.CreatedPrimitives++;
                    result.Add(svgPrimitive);
                }

                diagnostics?.RecordPrimitive(total, primitive, objectId, layerDescription, placeholderText, mechanical2, isArcCandidate, conversion, originX, originY, rotation, mirrored);
            }

            diagnostics?.EndComponent(total, objectCounts, layerCounts, result.Count, arcCandidates, mechanicalArcCandidates, createdArcs, failedArcs);
            if (diagnostics != null && (result.Count == 0 || arcCandidates > 0))
                TracePrimitiveCapture(componentName, total, objectCounts, layerCounts, result.Count, arcCandidates, mechanicalArcCandidates, createdArcs, failedArcs);

            return result;
        }

        private static IEnumerable<object> EnumerateComponentShapeObjects(object component)
        {
            var seen = new List<object>();
            foreach (object primitive in EnumerateGroupObjectsByObjectId(component))
            {
                if (AddSeenObject(seen, primitive))
                    yield return primitive;
            }

            foreach (object primitive in EnumerateGroupObjectsByIteratorObjectId(component))
            {
                if (AddSeenObject(seen, primitive))
                    yield return primitive;
            }

            foreach (object primitive in EnumerateGroupObjects(component))
            {
                if (AddSeenObject(seen, primitive))
                    yield return primitive;
            }
        }

        private static IEnumerable<object> EnumerateGroupObjectsByObjectId(object group)
        {
            if (group == null)
                yield break;

            foreach (int objectId in ShapeObjectIds)
            {
                int emptyRun = 0;
                for (int index = 0; index < MaxPrimitiveAtObjectsPerType && emptyRun < MaxPrimitiveAtEmptyRun; index++)
                {
                    object primitive = Invoke(group, "Internal_GetPrimitiveAt", index, objectId);
                    if (primitive == null && index == 0)
                        primitive = Invoke(group, "Internal_GetPrimitiveAt", 1, objectId);

                    if (primitive == null)
                    {
                        emptyRun++;
                        continue;
                    }

                    emptyRun = 0;
                    yield return primitive;
                }
            }
        }

        private static IEnumerable<object> EnumerateGroupObjectsByIteratorObjectId(object group)
        {
            foreach (int objectId in ShapeObjectIds)
            {
                foreach (object primitive in EnumerateGroupObjects(group, objectId))
                    yield return primitive;
            }
        }

        private static bool AddSeenObject(List<object> seen, object value)
        {
            if (value == null)
                return false;

            if (seen.Any(existing => ReferenceEquals(existing, value)))
                return false;

            seen.Add(value);
            return true;
        }

        private static SvgPrimitive TryCreateSvgPrimitive(object primitive, int objectId, int originX, int originY, double rotation, bool mirrored)
        {
            if (HasArcFields(primitive))
                return TryCreateArcPrimitive(primitive, originX, originY, rotation, mirrored);
            if (HasTrackFields(primitive))
                return TryCreateTrackPrimitive(primitive, originX, originY, rotation, mirrored);
            if (primitive is IPCB_Region)
                return TryCreateRegionPrimitive(primitive, originX, originY, rotation, mirrored);
            if (primitive is IPCB_Fill)
                return TryCreateBoundsPrimitive(primitive, originX, originY, rotation, mirrored);
            if (primitive is IPCB_Polygon)
                return TryCreatePolygonPrimitive(primitive, originX, originY, rotation, mirrored);

            if (objectId == (int)TObjectId.eArcObject)
                return TryCreateArcPrimitive(primitive, originX, originY, rotation, mirrored);
            if (objectId == (int)TObjectId.eTrackObject)
                return TryCreateTrackPrimitive(primitive, originX, originY, rotation, mirrored);
            if (objectId == (int)TObjectId.eRegionObject)
                return TryCreateRegionPrimitive(primitive, originX, originY, rotation, mirrored);
            if (objectId == (int)TObjectId.eFillObject)
                return TryCreateBoundsPrimitive(primitive, originX, originY, rotation, mirrored);
            if (objectId == (int)TObjectId.ePolyObject)
                return TryCreatePolygonPrimitive(primitive, originX, originY, rotation, mirrored);

            return null;
        }

        private static bool HasArcFields(object primitive)
        {
            return TryGetInt(primitive, "GetState_Radius", out int radius)
                && radius > 0
                && TryGetInt(primitive, "GetState_CenterX", out _)
                && TryGetInt(primitive, "GetState_CenterY", out _);
        }

        private static bool HasTrackFields(object primitive)
        {
            return TryGetInt(primitive, "GetState_X1", out _)
                && TryGetInt(primitive, "GetState_Y1", out _)
                && TryGetInt(primitive, "GetState_X2", out _)
                && TryGetInt(primitive, "GetState_Y2", out _);
        }

        private static SvgPrimitive TryCreateTrackPrimitive(object primitive, int originX, int originY, double rotation, bool mirrored)
        {
            if (!TryGetInt(primitive, "GetState_X1", out int rawX1)
                || !TryGetInt(primitive, "GetState_Y1", out int rawY1)
                || !TryGetInt(primitive, "GetState_X2", out int rawX2)
                || !TryGetInt(primitive, "GetState_Y2", out int rawY2))
                return null;

            if (rawX1 == rawX2 && rawY1 == rawY2)
                return null;

            TransformPoint(rawX1, rawY1, originX, originY, rotation, mirrored, out double x1, out double y1);
            TransformPoint(rawX2, rawY2, originX, originY, rotation, mirrored, out double x2, out double y2);
            double width = Math.Max(0.001, AltiumApi.CoordToMm(GetInt(primitive, "GetState_Width")));

            var line = new SvgPathPrimitive
            {
                StrokeWidth = width,
                Data = "M " + Format(x1) + " " + Format(y1) + " L " + Format(x2) + " " + Format(y2)
            };
            line.AddBounds(x1, y1);
            line.AddBounds(x2, y2);
            return line;
        }

        private static SvgPrimitive TryCreateArcPrimitive(object primitive, int originX, int originY, double rotation, bool mirrored)
        {
            if (!TryGetInt(primitive, "GetState_Radius", out int radiusCoord)
                || !TryGetInt(primitive, "GetState_CenterX", out int rawCenterX)
                || !TryGetInt(primitive, "GetState_CenterY", out int rawCenterY))
                return null;

            if (radiusCoord <= 0)
                return null;

            TransformPoint(rawCenterX, rawCenterY, originX, originY, rotation, mirrored, out double centerX, out double centerY);
            double radius = AltiumApi.CoordToMm(radiusCoord);
            double startAngle = NormalizeAngle(GetDouble(primitive, "GetState_StartAngle") - rotation);
            double endAngle = NormalizeAngle(GetDouble(primitive, "GetState_EndAngle") - rotation);
            if (mirrored)
            {
                startAngle = NormalizeAngle(180.0 - startAngle);
                endAngle = NormalizeAngle(180.0 - endAngle);
            }

            double sweep = NormalizeSweep(endAngle - startAngle);
            double strokeWidth = Math.Max(0.001, AltiumApi.CoordToMm(GetInt(primitive, "GetState_LineWidth")));
            if (Math.Abs(sweep) < 0.001 || Math.Abs(sweep - 360.0) < 0.001)
                return CreateCirclePrimitive(centerX, centerY, radius, strokeWidth);

            if (!TryGetArcEndpoint(primitive, "GetState_StartX", "GetState_StartY", originX, originY, rotation, mirrored, out double startX, out double startY))
                ArcPoint(centerX, centerY, radius, startAngle, out startX, out startY);
            if (!TryGetArcEndpoint(primitive, "GetState_EndX", "GetState_EndY", originX, originY, rotation, mirrored, out double endX, out double endY))
                ArcPoint(centerX, centerY, radius, endAngle, out endX, out endY);
            int largeArc = sweep > 180.0 ? 1 : 0;
            int sweepFlag = mirrored ? 0 : 1;

            var path = new SvgPathPrimitive
            {
                StrokeWidth = strokeWidth,
                Data = "M " + Format(startX) + " " + Format(startY) +
                    " A " + Format(radius) + " " + Format(radius) + " 0 " + largeArc.ToString(CultureInfo.InvariantCulture) + " " + sweepFlag.ToString(CultureInfo.InvariantCulture) + " " +
                    Format(endX) + " " + Format(endY)
            };
            AddArcBounds(path, centerX, centerY, radius, startAngle, endAngle);
            return path;
        }

        private static bool TryGetArcEndpoint(
            object primitive,
            string xMethod,
            string yMethod,
            int originX,
            int originY,
            double rotation,
            bool mirrored,
            out double x,
            out double y)
        {
            x = 0;
            y = 0;
            if (!TryGetInt(primitive, xMethod, out int rawX) || !TryGetInt(primitive, yMethod, out int rawY))
                return false;

            TransformPoint(rawX, rawY, originX, originY, rotation, mirrored, out x, out y);
            return true;
        }

        private static SvgPrimitive TryCreatePolygonPrimitive(object primitive, int originX, int originY, double rotation, bool mirrored)
        {
            List<object> segments = GetPolygonSegments(primitive);
            if (segments.Count == 0)
                return TryCreateBoundsPrimitive(primitive, originX, originY, rotation, mirrored);

            bool hasArc = segments.Any(IsArcPolySegment);
            return hasArc
                ? TryCreateSegmentedPolygonPrimitive(segments, originX, originY, rotation, mirrored)
                : TryCreateLinePolygonPrimitive(segments, originX, originY, rotation, mirrored);
        }

        private static SvgPrimitive TryCreateLinePolygonPrimitive(IReadOnlyList<object> segments, int originX, int originY, double rotation, bool mirrored)
        {
            var points = new List<SvgPoint>();
            foreach (object segment in segments)
            {
                if (!TryGetInt(segment, "GetVx", out int x) || !TryGetInt(segment, "GetVy", out int y))
                    continue;

                TransformPoint(x, y, originX, originY, rotation, mirrored, out double localX, out double localY);
                points.Add(new SvgPoint(localX, localY));
            }

            if (points.Count < 2)
                return null;

            var path = new SvgPathPrimitive
            {
                Fill = "#111111",
                StrokeWidth = 0,
                Data = BuildPolygonPath(new[] { points })
            };
            foreach (SvgPoint point in points)
                path.AddBounds(point.X, point.Y);
            return path;
        }

        private static SvgPrimitive TryCreateSegmentedPolygonPrimitive(IReadOnlyList<object> segments, int originX, int originY, double rotation, bool mirrored)
        {
            var path = new SvgPathPrimitive
            {
                Fill = "#111111",
                StrokeWidth = 0
            };
            var data = new StringBuilder();
            bool started = false;
            double currentX = 0;
            double currentY = 0;

            foreach (object segment in segments)
            {
                if (IsArcPolySegment(segment))
                {
                    if (!TryAppendPolyArc(data, path, segment, originX, originY, rotation, mirrored, ref started, ref currentX, ref currentY))
                        continue;
                }
                else if (TryGetInt(segment, "GetVx", out int x) && TryGetInt(segment, "GetVy", out int y))
                {
                    TransformPoint(x, y, originX, originY, rotation, mirrored, out double localX, out double localY);
                    if (!started)
                    {
                        data.Append("M ");
                        data.Append(Format(localX));
                        data.Append(' ');
                        data.Append(Format(localY));
                        started = true;
                    }
                    else
                    {
                        data.Append(" L ");
                        data.Append(Format(localX));
                        data.Append(' ');
                        data.Append(Format(localY));
                    }

                    currentX = localX;
                    currentY = localY;
                    path.AddBounds(localX, localY);
                }
            }

            if (!started)
                return null;

            data.Append(" Z");
            path.Data = data.ToString();
            return path;
        }

        private static bool TryAppendPolyArc(
            StringBuilder data,
            SvgPathPrimitive path,
            object segment,
            int originX,
            int originY,
            double rotation,
            bool mirrored,
            ref bool started,
            ref double currentX,
            ref double currentY)
        {
            if (!TryGetInt(segment, "GetCx", out int rawCenterX)
                || !TryGetInt(segment, "GetCy", out int rawCenterY)
                || !TryGetInt(segment, "GetRadius", out int rawRadius))
                return false;

            double radius = AltiumApi.CoordToMm(rawRadius);
            if (radius <= 0)
                return false;

            TransformPoint(rawCenterX, rawCenterY, originX, originY, rotation, mirrored, out double centerX, out double centerY);
            double startAngle = NormalizeAngle(GetDouble(segment, "GetAngle1") - rotation);
            double endAngle = NormalizeAngle(GetDouble(segment, "GetAngle2") - rotation);
            if (mirrored)
            {
                startAngle = NormalizeAngle(180.0 - startAngle);
                endAngle = NormalizeAngle(180.0 - endAngle);
            }

            ArcPoint(centerX, centerY, radius, startAngle, out double startX, out double startY);
            ArcPoint(centerX, centerY, radius, endAngle, out double endX, out double endY);
            if (!started)
            {
                data.Append("M ");
                data.Append(Format(startX));
                data.Append(' ');
                data.Append(Format(startY));
                started = true;
            }
            else if (Distance(currentX, currentY, startX, startY) > 0.001)
            {
                data.Append(" L ");
                data.Append(Format(startX));
                data.Append(' ');
                data.Append(Format(startY));
            }

            double sweep = NormalizeSweep(endAngle - startAngle);
            int largeArc = sweep > 180.0 ? 1 : 0;
            int sweepFlag = mirrored ? 0 : 1;
            data.Append(" A ");
            data.Append(Format(radius));
            data.Append(' ');
            data.Append(Format(radius));
            data.Append(" 0 ");
            data.Append(largeArc.ToString(CultureInfo.InvariantCulture));
            data.Append(' ');
            data.Append(sweepFlag.ToString(CultureInfo.InvariantCulture));
            data.Append(' ');
            data.Append(Format(endX));
            data.Append(' ');
            data.Append(Format(endY));

            AddArcBounds(path, centerX, centerY, radius, startAngle, endAngle);
            currentX = endX;
            currentY = endY;
            return true;
        }

        private static SvgPrimitive CreateCirclePrimitive(double centerX, double centerY, double radius, double strokeWidth)
        {
            var circle = new SvgPathPrimitive
            {
                StrokeWidth = strokeWidth,
                Data = "M " + Format(centerX + radius) + " " + Format(centerY) +
                    " A " + Format(radius) + " " + Format(radius) + " 0 1 1 " + Format(centerX - radius) + " " + Format(centerY) +
                    " A " + Format(radius) + " " + Format(radius) + " 0 1 1 " + Format(centerX + radius) + " " + Format(centerY)
            };
            circle.Left = centerX - radius;
            circle.Right = centerX + radius;
            circle.Bottom = centerY - radius;
            circle.Top = centerY + radius;
            return circle;
        }

        private static SvgPrimitive TryCreateRegionPrimitive(object primitive, int originX, int originY, double rotation, bool mirrored)
        {
            var contours = new List<List<SvgPoint>>();
            object polygon = Invoke(primitive, "Internal_GetGeometricPolygon");
            if (polygon != null)
            {
                int contourCount = GetInt(polygon, "GetState_Count");
                for (int contourIndex = 0; contourIndex < contourCount; contourIndex++)
                    AddContour(contours, Invoke(polygon, "Internal_GetState_Contour", contourIndex), originX, originY, rotation, mirrored);
                if (contours.Count == 0 && contourCount > 0)
                {
                    for (int contourIndex = 1; contourIndex <= contourCount; contourIndex++)
                        AddContour(contours, Invoke(polygon, "Internal_GetState_Contour", contourIndex), originX, originY, rotation, mirrored);
                }
            }

            if (contours.Count == 0)
                AddContour(contours, Invoke(primitive, "Internal_GetMainContour"), originX, originY, rotation, mirrored);
            if (contours.Count == 0)
                return TryCreateBoundsPrimitive(primitive, originX, originY, rotation, mirrored);

            var path = new SvgPathPrimitive
            {
                Fill = "#111111",
                StrokeWidth = 0,
                Data = BuildPolygonPath(contours)
            };
            foreach (SvgPoint point in contours.SelectMany(contour => contour))
                path.AddBounds(point.X, point.Y);

            return path;
        }

        private static void AddContour(List<List<SvgPoint>> contours, object contour, int originX, int originY, double rotation, bool mirrored)
        {
            if (contour == null)
                return;

            int pointCount = ClampApiCount(GetInt(contour, "GetState_Count"), MaxContourPoints, "region contour point");
            var points = new List<SvgPoint>();
            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
                AddContourPoint(points, contour, pointIndex, originX, originY, rotation, mirrored);
            if (points.Count == 0 && pointCount > 0)
            {
                for (int pointIndex = 1; pointIndex <= pointCount; pointIndex++)
                    AddContourPoint(points, contour, pointIndex, originX, originY, rotation, mirrored);
            }

            if (points.Count >= 2)
                contours.Add(points);
        }

        private static void AddContourPoint(List<SvgPoint> points, object contour, int pointIndex, int originX, int originY, double rotation, bool mirrored)
        {
            object rawX = Invoke(contour, "GetState_PointX", pointIndex);
            object rawY = Invoke(contour, "GetState_PointY", pointIndex);
            if (!TryConvertToInt(rawX, out int x) || !TryConvertToInt(rawY, out int y))
                return;

            TransformPoint(x, y, originX, originY, rotation, mirrored, out double localX, out double localY);
            points.Add(new SvgPoint(localX, localY));
        }

        private static SvgPrimitive TryCreateBoundsPrimitive(object primitive, int originX, int originY, double rotation, bool mirrored)
        {
            if (!TryGetBoundsCoord(primitive, out int left, out int bottom, out int right, out int top))
                return null;

            var points = new List<SvgPoint>();
            TransformPoint(left, bottom, originX, originY, rotation, mirrored, out double x1, out double y1);
            TransformPoint(right, bottom, originX, originY, rotation, mirrored, out double x2, out double y2);
            TransformPoint(right, top, originX, originY, rotation, mirrored, out double x3, out double y3);
            TransformPoint(left, top, originX, originY, rotation, mirrored, out double x4, out double y4);
            points.Add(new SvgPoint(x1, y1));
            points.Add(new SvgPoint(x2, y2));
            points.Add(new SvgPoint(x3, y3));
            points.Add(new SvgPoint(x4, y4));

            var path = new SvgPathPrimitive
            {
                Fill = "#111111",
                StrokeWidth = 0,
                Data = BuildPolygonPath(new[] { points })
            };
            foreach (SvgPoint point in points)
                path.AddBounds(point.X, point.Y);
            return path;
        }

        private static void AddTransformedBounds(SvgPrimitive primitive, int left, int bottom, int right, int top, int originX, int originY, double rotation, bool mirrored)
        {
            GetTransformedBounds(
                left,
                bottom,
                right,
                top,
                originX,
                originY,
                rotation,
                mirrored,
                out double transformedLeft,
                out double transformedBottom,
                out double transformedRight,
                out double transformedTop);
            primitive.Left = transformedLeft;
            primitive.Bottom = transformedBottom;
            primitive.Right = transformedRight;
            primitive.Top = transformedTop;
        }

        private static void GetTransformedBounds(
            int left,
            int bottom,
            int right,
            int top,
            int originX,
            int originY,
            double rotation,
            bool mirrored,
            out double transformedLeft,
            out double transformedBottom,
            out double transformedRight,
            out double transformedTop)
        {
            TransformPoint(left, bottom, originX, originY, rotation, mirrored, out double x1, out double y1);
            TransformPoint(right, bottom, originX, originY, rotation, mirrored, out double x2, out double y2);
            TransformPoint(right, top, originX, originY, rotation, mirrored, out double x3, out double y3);
            TransformPoint(left, top, originX, originY, rotation, mirrored, out double x4, out double y4);
            transformedLeft = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
            transformedRight = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
            transformedBottom = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
            transformedTop = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
        }

        private static SvgPrimitive TryCreateTextPrimitive(object primitive, string text, int originX, int originY, double rotation, bool mirrored)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            int rawX = 0;
            int rawY = 0;
            bool hasLocation = TryGetInt(primitive, "GetState_XLocation", out rawX)
                && TryGetInt(primitive, "GetState_YLocation", out rawY);

            int left = 0;
            int bottom = 0;
            int right = 0;
            int top = 0;
            bool hasBounds = TryGetBoundsCoord(primitive, out left, out bottom, out right, out top);
            if (!hasLocation)
            {
                if (!hasBounds)
                    return null;

                rawX = left;
                rawY = bottom;
            }

            TransformPoint(rawX, rawY, originX, originY, rotation, mirrored, out double x, out double y);
            double fontSize = AltiumApi.CoordToMm(GetInt(primitive, "GetState_Size"));
            if (fontSize <= 0 && hasBounds)
                fontSize = Math.Max(0.1, AltiumApi.CoordToMm(Math.Abs(top - bottom)));
            if (fontSize <= 0)
                fontSize = 1.0;

            double rawTextRotation = GetDouble(primitive, "GetState_Rotation") - rotation;
            if (mirrored)
                rawTextRotation = -rawTextRotation;

            double width = Math.Max(fontSize, text.Trim().Length * fontSize * 0.65);
            double height = fontSize;
            double boundsLeft = x;
            double boundsBottom = y - height * 0.25;
            double boundsRight = x + width;
            double boundsTop = y + height;
            if (hasBounds)
            {
                GetTransformedBounds(
                    left,
                    bottom,
                    right,
                    top,
                    originX,
                    originY,
                    rotation,
                    mirrored,
                    out boundsLeft,
                    out boundsBottom,
                    out boundsRight,
                    out boundsTop);
            }

            bool centerTextInBounds;
            double textRotation = ResolveTextRotation(
                rawTextRotation,
                hasBounds,
                boundsLeft,
                boundsBottom,
                boundsRight,
                boundsTop,
                y,
                width,
                height,
                text.Trim().Length,
                GetBool(primitive, "GetState_Multiline"),
                out centerTextInBounds);

            var textPrimitive = new SvgTextPrimitive
            {
                Text = text.Trim(),
                X = x,
                Y = y,
                FontSize = fontSize,
                Rotation = textRotation,
                CenterAnchored = centerTextInBounds,
                TextWidth = width
            };

            if (hasBounds)
            {
                textPrimitive.Left = boundsLeft;
                textPrimitive.Bottom = boundsBottom;
                textPrimitive.Right = boundsRight;
                textPrimitive.Top = boundsTop;
                if (centerTextInBounds)
                {
                    textPrimitive.X = (textPrimitive.Left + textPrimitive.Right) / 2.0;
                    textPrimitive.Y = (textPrimitive.Bottom + textPrimitive.Top) / 2.0;
                }
            }
            else
            {
                textPrimitive.Left = x;
                textPrimitive.Right = x + width;
                textPrimitive.Bottom = y - height * 0.25;
                textPrimitive.Top = y + height;
            }

            return textPrimitive;
        }

        private static string BuildSvg(IReadOnlyList<SvgPrimitive> primitives)
        {
            double left = primitives.Min(primitive => primitive.Left);
            double right = primitives.Max(primitive => primitive.Right);
            double bottom = primitives.Min(primitive => primitive.Bottom);
            double top = primitives.Max(primitive => primitive.Top);
            if (right <= left)
                right = left + EmptyViewBoxSizeMm;
            if (top <= bottom)
                top = bottom + EmptyViewBoxSizeMm;

            double width = right - left;
            double height = top - bottom;
            double pad = Math.Max(0.05, primitives.Max(primitive => primitive.StrokeWidth) / 2.0);
            left -= pad;
            right += pad;
            bottom -= pad;
            top += pad;
            width = right - left;
            height = top - bottom;

            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Encoding = Utf8NoBom,
                OmitXmlDeclaration = false,
                Indent = true,
                NewLineChars = "\n"
            };
            using (var stringWriter = new Utf8StringWriter(sb))
            using (var writer = XmlWriter.Create(stringWriter, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("svg", "http://www.w3.org/2000/svg");
                writer.WriteAttributeString("version", "1.2");
                writer.WriteAttributeString("baseProfile", "tiny");
                writer.WriteAttributeString("width", Format(width) + "mm");
                writer.WriteAttributeString("height", Format(height) + "mm");
                writer.WriteAttributeString("viewBox", Format(left) + " " + Format(-top) + " " + Format(width) + " " + Format(height));
                writer.WriteStartElement("g");
                writer.WriteAttributeString("id", "Shape");
                writer.WriteAttributeString("transform", "scale(1,-1)");
                foreach (SvgPrimitive primitive in primitives)
                    primitive.Write(writer);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }

            return sb.ToString();
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public Utf8StringWriter(StringBuilder builder)
                : base(builder, CultureInfo.InvariantCulture)
            {
            }

            public override Encoding Encoding
            {
                get { return Utf8NoBom; }
            }
        }

        private static string BuildPolygonPath(IEnumerable<List<SvgPoint>> contours)
        {
            var sb = new StringBuilder();
            foreach (List<SvgPoint> contour in contours)
            {
                if (contour.Count == 0)
                    continue;

                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append("M ");
                sb.Append(Format(contour[0].X));
                sb.Append(' ');
                sb.Append(Format(contour[0].Y));
                for (int i = 1; i < contour.Count; i++)
                {
                    sb.Append(" L ");
                    sb.Append(Format(contour[i].X));
                    sb.Append(' ');
                    sb.Append(Format(contour[i].Y));
                }
                sb.Append(" Z");
            }

            return sb.ToString();
        }

        private static void AddArcBounds(SvgPathPrimitive path, double centerX, double centerY, double radius, double startAngle, double endAngle)
        {
            AddArcBoundsPoint(path, centerX, centerY, radius, startAngle);
            AddArcBoundsPoint(path, centerX, centerY, radius, endAngle);
            for (int angle = 0; angle < 360; angle += 90)
            {
                if (AngleInSweep(angle, startAngle, endAngle))
                    AddArcBoundsPoint(path, centerX, centerY, radius, angle);
            }
        }

        private static void AddArcBoundsPoint(SvgPathPrimitive path, double centerX, double centerY, double radius, double angle)
        {
            ArcPoint(centerX, centerY, radius, angle, out double x, out double y);
            path.AddBounds(x, y);
        }

        private static bool AngleInSweep(double angle, double startAngle, double endAngle)
        {
            double sweep = NormalizeSweep(endAngle - startAngle);
            double relative = NormalizeSweep(angle - startAngle);
            return relative >= 0 && relative <= sweep;
        }

        private static void ArcPoint(double centerX, double centerY, double radius, double angleDeg, out double x, out double y)
        {
            double radians = angleDeg * Math.PI / 180.0;
            x = centerX + Math.Cos(radians) * radius;
            y = centerY + Math.Sin(radians) * radius;
        }

        private static void TransformPoint(int x, int y, int originX, int originY, double rotationDeg, bool mirrored, out double localX, out double localY)
        {
            double dx = AltiumApi.CoordToMm(x - originX);
            double dy = AltiumApi.CoordToMm(y - originY);
            double radians = -rotationDeg * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            double rotatedX = (dx * cos) - (dy * sin);
            double rotatedY = (dx * sin) + (dy * cos);
            localX = mirrored ? -rotatedX : rotatedX;
            localY = rotatedY;
        }

        private static IEnumerable<object> EnumeratePcbLibExportComponents(IPCB_Library pcbLib)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IPCB_LibraryIterator iterator = IPCB_LibraryHelper.LibraryIterator_Create(pcbLib);
            if (iterator == null)
                yield break;

            try
            {
                iterator.SetState_FilterAll();
                object rawFootprint = iterator.Internal_FirstPCBObject();
                int count = 0;
                while (rawFootprint != null)
                {
                    GuardIteratorObject(ref count, MaxLibraryIteratorObjects, "PCB library component");
                    IPCB_LibComponent footprint = rawFootprint as IPCB_LibComponent;
                    if (footprint != null)
                    {
                        string footprintName = SafeCall(() => footprint.GetState_Pattern());
                        if (!string.IsNullOrWhiteSpace(footprintName) && seen.Add(footprintName))
                            yield return new ExportComponentItem(footprint, footprintName);
                    }

                    rawFootprint = iterator.Internal_NextPCBObject();
                }
            }
            finally
            {
                IPCB_LibraryHelper.LibraryIterator_Destroy(pcbLib, ref iterator);
            }
        }

        private static ExportComponentItem GetCurrentPcbLibExportComponent(IPCB_Library pcbLib)
        {
            IPCB_LibComponent footprint = IPCB_LibraryHelper.GetState_CurrentComponent(pcbLib);
            if (footprint == null)
                return null;

            string footprintName = SafeCall(() => footprint.GetState_Pattern());
            return string.IsNullOrWhiteSpace(footprintName)
                ? null
                : new ExportComponentItem(footprint, footprintName);
        }

        private static IEnumerable<object> EnumerateBoardObjects(IPCB_Board board, params int[] objectIds)
        {
            object iterator = Invoke(board, "BoardIterator_Create") ?? Invoke(board, "Internal_BoardIterator_Create");
            if (iterator == null)
                yield break;

            try
            {
                if (objectIds != null && objectIds.Length > 0)
                    Invoke(iterator, "AddFilter_ObjectSet", CreateObjectSet(objectIds));
                else
                    Invoke(iterator, "SetState_FilterAll");

                object primitive = Invoke(iterator, "FirstPCBObject") ?? Invoke(iterator, "Internal_FirstPCBObject");
                int count = 0;
                while (primitive != null)
                {
                    GuardIteratorObject(ref count, MaxBoardIteratorObjects, "PCB board");
                    yield return primitive;
                    primitive = Invoke(iterator, "NextPCBObject") ?? Invoke(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                Invoke(board, "BoardIterator_Destroy", iterator);
            }
        }

        private static IEnumerable<object> EnumerateGroupObjects(object group)
        {
            return EnumerateGroupObjects(group, null);
        }

        private static IEnumerable<object> EnumerateGroupObjects(object group, int? objectId)
        {
            object iterator = Invoke(group, "GroupIterator_Create") ?? Invoke(group, "Internal_GroupIterator_Create");
            if (iterator == null)
                yield break;

            try
            {
                if (objectId.HasValue)
                    Invoke(iterator, "AddFilter_ObjectSet", CreateObjectSet(objectId.Value));
                else
                    Invoke(iterator, "SetState_FilterAll");

                object primitive = Invoke(iterator, "FirstPCBObject") ?? Invoke(iterator, "Internal_FirstPCBObject");
                int count = 0;
                while (primitive != null)
                {
                    GuardIteratorObject(ref count, MaxGroupIteratorObjects, "component primitive");
                    yield return primitive;
                    primitive = Invoke(iterator, "NextPCBObject") ?? Invoke(iterator, "Internal_NextPCBObject");
                }
            }
            finally
            {
                Invoke(group, "GroupIterator_Destroy", iterator);
            }
        }

        private static IEnumerable<object> GetSelectedBoardComponents(IPCB_Board board)
        {
            var result = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object selected in GetSelectedObjects(board))
            {
                if (!IsComponentObject(selected))
                    continue;

                string identity = ObjectIdentity(selected);
                if (seen.Add(identity))
                    result.Add(selected);
            }

            if (result.Count == 0)
                throw new InvalidOperationException("Select a PCB component before exporting the selected shape.");

            return result;
        }

        private static List<object> GetSelectedObjects(IPCB_Board board)
        {
            var result = new List<object>();
            int count = GetInt(board, "GetState_SelectecObjectCount");
            if (count <= 0)
                count = GetInt(board, "SelectedObjectsCount");

            for (int i = 0; i < count; i++)
                AddDistinctObject(result, Invoke(board, "Internal_GetState_SelectecObject", i));
            for (int i = 1; i <= count; i++)
                AddDistinctObject(result, Invoke(board, "Internal_GetState_SelectecObject", i));

            if (result.Count > 0)
                return result;

            return result;
        }

        private static void AddDistinctObject(List<object> result, object value)
        {
            if (value == null)
                return;

            if (!result.Any(existing => ReferenceEquals(existing, value)))
                result.Add(value);
        }

        private static bool IsMechanical2Primitive(object primitive, int objectId)
        {
            if (primitive == null)
                return false;

            if (!IsSupportedShapePrimitive(primitive, objectId))
                return false;

            if (!TryGetPrimitiveLayerNumber(primitive, out int layerNumber))
                return false;

            try
            {
                if (layerNumber == new V7_Layer(TLayerConstant.eMechanical2).Number())
                    return true;
            }
            catch
            {
            }

            return layerNumber == (int)TLayerConstant.eMechanical2
                || layerNumber == 2;
        }

        private static bool IsSupportedShapePrimitive(object primitive, int objectId)
        {
            return HasArcFields(primitive)
                || HasTrackFields(primitive)
                || primitive is IPCB_Track
                || primitive is IPCB_Arc
                || primitive is IPCB_Fill
                || primitive is IPCB_Region
                || primitive is IPCB_Polygon
                || objectId == (int)TObjectId.eTrackObject
                || objectId == (int)TObjectId.eArcObject
                || objectId == (int)TObjectId.eFillObject
                || objectId == (int)TObjectId.eRegionObject
                || objectId == (int)TObjectId.ePolyObject;
        }

        private static bool IsExcludedTextPrimitive(
            object primitive,
            int objectId,
            HashSet<string> excludedTextValues,
            HashSet<string> repeatedBoardTextValues)
        {
            if (primitive == null)
                return false;

            if (IsBooleanTrue(primitive, "GetState_IsDesignator")
                || IsBooleanTrue(primitive, "GetState_IsComment")
                || IsBooleanTrue(primitive, "IsDesignator")
                || IsBooleanTrue(primitive, "IsComment"))
                return true;

            foreach (string value in ReadTextCandidates(primitive, objectId))
            {
                if (IsExcludedTextValue(value, excludedTextValues))
                    return true;

                string normalized = NormalizeTextForCompare(value);
                if (!string.IsNullOrWhiteSpace(normalized)
                    && repeatedBoardTextValues != null
                    && repeatedBoardTextValues.Contains(normalized))
                    return true;
            }

            return false;
        }

        private static bool IsAssemblyPlaceholderString(string value)
        {
            return string.Equals(value, ".Designator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, ".Comment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Designator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Comment", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExcludedTextValue(string value, HashSet<string> excludedTextValues)
        {
            string normalized = NormalizeTextForCompare(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return IsAssemblyPlaceholderString(normalized)
                || (excludedTextValues != null && excludedTextValues.Contains(normalized));
        }

        private static HashSet<string> BuildComponentTextExclusionSet(object component)
        {
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTextExclusion(values, ".Designator");
            AddTextExclusion(values, ".Comment");
            AddTextExclusion(values, "Designator");
            AddTextExclusion(values, "Comment");
            AddTextExclusion(values, ReadDesignator(component));
            AddTextExclusion(values, ReadComponentComment(component));
            AddTextObjectExclusions(values, Invoke(component, "Internal_GetState_Name") ?? Invoke(component, "GetState_Name"));
            AddTextObjectExclusions(values, Invoke(component, "Internal_GetState_Comment") ?? Invoke(component, "GetState_Comment"));
            AddTextValueExclusions(values, Invoke(component, "GetState_SourceDesignator"));
            AddTextValueExclusions(values, Invoke(component, "GetState_Designator"));
            AddTextValueExclusions(values, Invoke(component, "GetState_SourceComment"));
            AddTextValueExclusions(values, Invoke(component, "GetState_Comment"));
            AddTextValueExclusions(values, Invoke(component, "GetState_CommentString"));
            AddTextExclusion(values, Convert.ToString(Invoke(component, "GetState_SourceLibReference")));
            AddTextExclusion(values, Convert.ToString(Invoke(component, "GetState_SourceCompDesignItemID")));
            if (component is IPCB_Component pcbComponent)
            {
                AddTextObjectExclusions(values, SafeObjectCall(() => pcbComponent.Internal_GetState_Name()));
                AddTextObjectExclusions(values, SafeObjectCall(() => pcbComponent.Internal_GetState_Comment()));
            }
            return values;
        }

        private static HashSet<string> BuildRepeatedBoardTextExclusionSet(IEnumerable<object> primitives)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (object primitive in primitives ?? Enumerable.Empty<object>())
            {
                int objectId = GetObjectId(primitive);
                if (!IsMechanical2Primitive(primitive, objectId))
                    continue;

                string value = "";
                foreach (string candidate in ReadTextCandidates(primitive, objectId))
                {
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        value = NormalizeTextForCompare(candidate);
                        break;
                    }
                }

                if (!LooksLikeBoardResolvedLabel(value))
                    continue;

                if (counts.TryGetValue(value, out int count))
                    counts[value] = count + 1;
                else
                    counts[value] = 1;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> entry in counts)
            {
                if (entry.Value > 1)
                    result.Add(entry.Key);
            }

            return result;
        }

        private static bool LooksLikeBoardResolvedLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string text = value.Trim();
            if (IsAssemblyPlaceholderString(text))
                return true;

            return text.Length >= 4 && text.Any(ch => ch == '_' || ch == '-' || char.IsDigit(ch));
        }

        private static void AddTextExclusion(HashSet<string> values, string value)
        {
            string normalized = NormalizeTextForCompare(value);
            if (!string.IsNullOrWhiteSpace(normalized))
                values.Add(normalized);
        }

        private static void AddTextValueExclusions(HashSet<string> values, object value)
        {
            if (value == null)
                return;

            if (value is string text)
            {
                AddTextExclusion(values, text);
                return;
            }

            AddTextObjectExclusions(values, value);
            AddTextExclusion(values, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void AddTextObjectExclusions(HashSet<string> values, object textObject)
        {
            if (textObject == null)
                return;

            foreach (string candidate in ReadTextCandidates(textObject, (int)TObjectId.eTextObject))
                AddTextExclusion(values, candidate);
        }

        private static string NormalizeTextForCompare(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static bool IsTextLikePrimitive(object primitive, int objectId)
        {
            if (primitive == null)
                return false;

            return objectId == (int)TObjectId.eTextObject;
        }

        private static bool IsComponentObject(object primitive)
        {
            return primitive is IPCB_Component || GetObjectId(primitive) == (int)TObjectId.eComponentObject;
        }

        private static bool IsBoardComponent(object primitive)
        {
            return primitive is IPCB_Component || GetObjectId(primitive) == (int)TObjectId.eComponentObject;
        }

        private static string ReadDesignator(object component)
        {
            if (component is IPCB_Component pcbComponent)
            {
                string sourceDesignator = SafeCall(() => pcbComponent.GetState_SourceDesignator());
                if (!string.IsNullOrWhiteSpace(sourceDesignator))
                    return sourceDesignator;

                return ReadTextObject(SafeObjectCall(() => pcbComponent.Internal_GetState_Name()));
            }

            object name = Invoke(component, "Internal_GetState_Name") ?? Invoke(component, "GetState_Name");
            string text = ReadTextObject(name);
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            return FirstNonEmpty(
                Convert.ToString(Invoke(component, "GetState_SourceDesignator")),
                Convert.ToString(Invoke(component, "GetState_Designator")));
        }

        private static string ReadComponentComment(object component)
        {
            if (component is IPCB_Component pcbComponent)
            {
                string commentText = ReadTextObject(SafeObjectCall(() => pcbComponent.Internal_GetState_Comment()));
                if (!string.IsNullOrWhiteSpace(commentText))
                    return commentText;
            }

            return FirstNonEmpty(
                ReadTextObject(Invoke(component, "Internal_GetState_Comment") ?? Invoke(component, "GetState_Comment")),
                Convert.ToString(Invoke(component, "GetState_SourceComment")),
                Convert.ToString(Invoke(component, "GetState_CommentString")),
                Convert.ToString(Invoke(component, "GetState_SourceLibReference")),
                Convert.ToString(Invoke(component, "GetState_SourceCompDesignItemID")));
        }

        private static string ReadComponentExportName(object component, bool preferFootprintName, string fallback)
        {
            object rawComponent = UnwrapExportComponent(component);

            if (preferFootprintName)
            {
                string footprint = ReadFootprintName(component);
                if (!string.IsNullOrWhiteSpace(footprint))
                    return footprint;

                string designator = ReadDesignator(rawComponent);
                return string.IsNullOrWhiteSpace(designator) ? fallback : designator;
            }

            string preferredDesignator = ReadDesignator(rawComponent);
            if (!string.IsNullOrWhiteSpace(preferredDesignator))
                return preferredDesignator;

            string fallbackFootprint = ReadFootprintName(component);
            return string.IsNullOrWhiteSpace(fallbackFootprint) ? fallback : fallbackFootprint;
        }

        private static object UnwrapExportComponent(object component)
        {
            return component is ExportComponentItem item ? item.Component : component;
        }

        private static string ReadTextObject(object textObject)
        {
            if (textObject is IPCB_Text pcbText)
            {
                return FirstNonEmpty(
                    SafeCall(() => pcbText.GetState_Text()),
                    SafeCall(() => pcbText.GetState_UnderlyingString()),
                    SafeCall(() => pcbText.GetState_ConvertedString()));
            }

            return ReadPrimitiveText(textObject);
        }

        private static string ReadPrimitiveText(object primitive)
        {
            return FirstNonEmpty(
                Convert.ToString(Invoke(primitive, "GetState_Text")),
                Convert.ToString(Invoke(primitive, "GetState_UnderlyingString")),
                Convert.ToString(Invoke(primitive, "GetState_ConvertedString")),
                Convert.ToString(Invoke(primitive, "GetState_Name")),
                Convert.ToString(Invoke(primitive, "GetState_OriginalString")),
                Convert.ToString(Invoke(primitive, "GetState_String")));
        }

        private static string ReadTextPrimitiveString(object primitive)
        {
            return FirstNonEmpty(
                Convert.ToString(Invoke(primitive, "GetState_Text")),
                Convert.ToString(Invoke(primitive, "GetState_UnderlyingString")),
                Convert.ToString(Invoke(primitive, "GetState_ConvertedString")),
                Convert.ToString(Invoke(primitive, "GetState_OriginalString")),
                Convert.ToString(Invoke(primitive, "GetState_String")));
        }

        private static string ReadExportTextString(object primitive, int objectId, HashSet<string> excludedTextValues)
        {
            string value = "";
            foreach (string candidate in ReadTextCandidates(primitive, objectId))
            {
                if (IsExcludedTextValue(candidate, excludedTextValues))
                    return "";
                if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(candidate))
                    value = candidate;
            }

            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static IEnumerable<string> ReadTextCandidates(object primitive, int objectId)
        {
            yield return Convert.ToString(Invoke(primitive, "GetState_Text"));
            yield return Convert.ToString(Invoke(primitive, "GetState_UnderlyingString"));
            yield return Convert.ToString(Invoke(primitive, "GetState_ConvertedString"));
            yield return Convert.ToString(Invoke(primitive, "GetState_OriginalString"));
            yield return Convert.ToString(Invoke(primitive, "GetState_String"));
            if (objectId == (int)TObjectId.eTextObject)
                yield return Convert.ToString(Invoke(primitive, "GetState_Name"));
        }

        private static bool HasTextPrimitiveFields(object primitive)
        {
            return HasCallable(primitive, "GetState_Text")
                || HasCallable(primitive, "GetState_UnderlyingString")
                || HasCallable(primitive, "GetState_ConvertedString")
                || HasCallable(primitive, "GetState_OriginalString")
                || HasCallable(primitive, "GetState_String");
        }

        private static string ReadFootprintName(object component)
        {
            if (component is ExportComponentItem item)
                return FirstNonEmpty(item.ExportName, ReadFootprintName(item.Component));

            if (component is IPCB_Component pcbComponent)
            {
                return FirstNonEmpty(
                    SafeCall(() => pcbComponent.GetState_Pattern()),
                    SafeCall(() => pcbComponent.GetState_FootprintConfiguratorName()));
            }

            if (component is IPCB_LibComponent libComponent)
                return SafeCall(() => libComponent.GetState_Pattern());

            return FirstNonEmpty(
                Convert.ToString(Invoke(component, "GetState_Pattern")),
                Convert.ToString(Invoke(component, "GetState_FootprintConfiguratorName")),
                Convert.ToString(Invoke(component, "GetState_LibReference")),
                Convert.ToString(Invoke(component, "GetState_Name")),
                Convert.ToString(Invoke(component, "Internal_GetState_Name")),
                ReadTextObject(Invoke(component, "GetState_Name")),
                ReadTextObject(Invoke(component, "Internal_GetState_Name")));
        }

        private static string SafeCall(Func<string> getter)
        {
            try
            {
                return getter?.Invoke() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static object SafeObjectCall(Func<object> getter)
        {
            try
            {
                return getter?.Invoke();
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetPrimitiveLayerNumber(object primitive, out int layer)
        {
            layer = 0;

            object v7Layer = Invoke(primitive, "Internal_GetState_V7Layer") ?? Invoke(primitive, "GetState_V7Layer");
            if (v7Layer is IV7_Layer layerObject)
            {
                try
                {
                    layer = new V7_Layer(layerObject).Number();
                    return true;
                }
                catch
                {
                }
            }

            object raw = Invoke(primitive, "GetState_Layer") ?? Invoke(primitive, "Internal_GetState_Layer");
            if (TryConvertToInt(raw, out layer))
                return true;

            try
            {
                if (v7Layer != null)
                {
                    layer = new V7_Layer((IV7_Layer)v7Layer).Number();
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                if (v7Layer != null)
                {
                    layer = (int)new V7_Layer((IV7_Layer)v7Layer).SafeV6Layer();
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static string DescribePrimitiveLayer(object primitive)
        {
            if (TryGetPrimitiveLayerNumber(primitive, out int layer))
                return layer.ToString(CultureInfo.InvariantCulture);

            object raw = Invoke(primitive, "Internal_GetState_Layer") ?? Invoke(primitive, "GetState_Layer");
            if (raw != null)
                return Convert.ToString(raw, CultureInfo.InvariantCulture);

            object v7Layer = Invoke(primitive, "Internal_GetState_V7Layer") ?? Invoke(primitive, "GetState_V7Layer");
            if (v7Layer != null)
            {
                try
                {
                    var layerObject = new V7_Layer((IV7_Layer)v7Layer);
                    return "v7:n=" + layerObject.GetN().ToString(CultureInfo.InvariantCulture)
                        + ",id=" + layerObject.GetID().ToString(CultureInfo.InvariantCulture)
                        + ",ord=" + layerObject.GetOrd().ToString(CultureInfo.InvariantCulture);
                }
                catch
                {
                    return v7Layer.GetType().Name;
                }
            }

            return "unknown";
        }

        private sealed class ShapeExportDiagnostics
        {
            private const int MaxDetailedPrimitives = 1200;
            private const int MaxPolygonSegmentsToWrite = 48;

            private readonly StringBuilder _text = new StringBuilder();
            private int _detailedPrimitiveCount;
            private bool _truncated;

            private ShapeExportDiagnostics(string filePath, string fallbackPrefix, bool preferFootprintName)
            {
                FilePath = filePath;
                _text.AppendLine("EasyEDA Loader shape export diagnostics");
                _text.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                _text.AppendLine("FallbackPrefix: " + fallbackPrefix);
                _text.AppendLine("PreferFootprintName: " + preferFootprintName.ToString(CultureInfo.InvariantCulture));
                _text.AppendLine("Units: raw Altium coord plus mm where useful");
                _text.AppendLine();
            }

            public string FilePath { get; }

            public static ShapeExportDiagnostics Create(string folder, string fallbackPrefix, bool preferFootprintName)
            {
                string fileName = "EasyEDA-ShapeExport-Diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt";
                return new ShapeExportDiagnostics(Path.Combine(folder, fileName), fallbackPrefix, preferFootprintName);
            }

            public void BeginComponent(string exportName, object component, int originX, int originY, double rotation, bool mirrored)
            {
                _text.AppendLine("Component: " + exportName);
                _text.AppendLine("  runtimeType: " + TypeName(component));
                _text.AppendLine("  identity: " + ObjectIdentity(component));
                _text.AppendLine("  designator: " + ReadDesignator(component));
                _text.AppendLine("  footprint: " + ReadFootprintName(component));
                _text.AppendLine("  originRaw: x=" + originX.ToString(CultureInfo.InvariantCulture) + ", y=" + originY.ToString(CultureInfo.InvariantCulture));
                _text.AppendLine("  originMm: x=" + Format(AltiumApi.CoordToMm(originX)) + ", y=" + Format(AltiumApi.CoordToMm(originY)));
                _text.AppendLine("  rotation: " + Format(rotation));
                _text.AppendLine("  mirrored: " + mirrored.ToString(CultureInfo.InvariantCulture));
            }

            public void RecordPrimitive(
                int index,
                object primitive,
                int objectId,
                string layerDescription,
                bool placeholderText,
                bool mechanical2,
                bool arcCandidate,
                string conversion,
                int originX,
                int originY,
                double rotation,
                bool mirrored)
            {
                bool supported = IsSupportedShapePrimitive(primitive, objectId);
                bool shouldDetail = mechanical2 || arcCandidate || index <= 32;
                if (!shouldDetail)
                    return;

                if (_detailedPrimitiveCount >= MaxDetailedPrimitives)
                {
                    if (!_truncated)
                    {
                        _text.AppendLine("  primitive details truncated after " + MaxDetailedPrimitives.ToString(CultureInfo.InvariantCulture) + " entries.");
                        _truncated = true;
                    }
                    return;
                }

                _detailedPrimitiveCount++;
                _text.AppendLine("  Primitive #" + index.ToString(CultureInfo.InvariantCulture)
                    + ": type=" + TypeName(primitive)
                    + ", objectId=" + objectId.ToString(CultureInfo.InvariantCulture)
                    + ", layer=" + layerDescription
                    + ", supported=" + supported.ToString(CultureInfo.InvariantCulture)
                    + ", mechanical2=" + mechanical2.ToString(CultureInfo.InvariantCulture)
                    + ", arcCandidate=" + arcCandidate.ToString(CultureInfo.InvariantCulture)
                    + ", placeholderText=" + placeholderText.ToString(CultureInfo.InvariantCulture)
                    + ", conversion=" + conversion
                    + ", identity=" + ObjectIdentity(primitive));
                AppendPrimitiveCommon(primitive);
                if (primitive is IPCB_Track || objectId == (int)TObjectId.eTrackObject)
                    AppendTrack(primitive, originX, originY, rotation, mirrored);
                if (primitive is IPCB_Arc || objectId == (int)TObjectId.eArcObject)
                    AppendArc(primitive, originX, originY, rotation, mirrored);
                if (primitive is IPCB_Polygon || objectId == (int)TObjectId.ePolyObject)
                    AppendPolygon(primitive);
                if (primitive is IPCB_Region || objectId == (int)TObjectId.eRegionObject)
                    AppendRegion(primitive);
            }

            public void EndComponent(
                int total,
                Dictionary<int, int> objectCounts,
                Dictionary<string, int> layerCounts,
                int mechanicalCount,
                int arcCandidates,
                int mechanicalArcCandidates,
                int createdArcs,
                int failedArcs)
            {
                _text.AppendLine("  Summary: total=" + total.ToString(CultureInfo.InvariantCulture)
                    + ", mechanical2=" + mechanicalCount.ToString(CultureInfo.InvariantCulture)
                    + ", arcs=" + arcCandidates.ToString(CultureInfo.InvariantCulture)
                    + ", mechanical2Arcs=" + mechanicalArcCandidates.ToString(CultureInfo.InvariantCulture)
                    + ", createdArcs=" + createdArcs.ToString(CultureInfo.InvariantCulture)
                    + ", failedArcs=" + failedArcs.ToString(CultureInfo.InvariantCulture));
                _text.AppendLine("  ObjectCounts: " + FormatCounts(objectCounts));
                _text.AppendLine("  LayerCounts: " + FormatCounts(layerCounts));
                _text.AppendLine();
            }

            public void Finish(ShapeExportResult result)
            {
                _text.AppendLine("Final result: components=" + result.ComponentCount.ToString(CultureInfo.InvariantCulture)
                    + ", files=" + result.FileCount.ToString(CultureInfo.InvariantCulture)
                    + ", primitives=" + result.PrimitiveCount.ToString(CultureInfo.InvariantCulture));
                if (result.Warnings.Count > 0)
                    _text.AppendLine("Warnings: " + string.Join(" | ", result.Warnings));
                if (result.Errors.Count > 0)
                    _text.AppendLine("Errors: " + string.Join(" | ", result.Errors));
                try
                {
                    File.WriteAllText(FilePath, _text.ToString(), Utf8NoBom);
                    EasyEDALoaderModule.Trace("ExportShape diagnostics written: " + FilePath);
                }
                catch (Exception ex)
                {
                    EasyEDALoaderModule.Trace("ExportShape diagnostics write failed: " + ex.Message);
                }
            }

            private void AppendPrimitiveCommon(object primitive)
            {
                if (TryGetBoundsCoord(primitive, out int left, out int bottom, out int right, out int top))
                {
                    _text.AppendLine("    boundsRaw: left=" + left.ToString(CultureInfo.InvariantCulture)
                        + ", bottom=" + bottom.ToString(CultureInfo.InvariantCulture)
                        + ", right=" + right.ToString(CultureInfo.InvariantCulture)
                        + ", top=" + top.ToString(CultureInfo.InvariantCulture));
                    _text.AppendLine("    boundsMm: left=" + Format(AltiumApi.CoordToMm(left))
                        + ", bottom=" + Format(AltiumApi.CoordToMm(bottom))
                        + ", right=" + Format(AltiumApi.CoordToMm(right))
                        + ", top=" + Format(AltiumApi.CoordToMm(top)));
                }
                AppendMethodValues(primitive, "common", new[]
                {
                    "Internal_GetState_ObjectID",
                    "GetState_ObjectID",
                    "GetState_ObjectId",
                    "GetState_Layer",
                    "Internal_GetState_Layer",
                    "GetState_Selected",
                    "GetState_Text",
                    "GetState_UnderlyingString",
                    "GetState_ConvertedString",
                    "GetState_Name",
                    "GetState_OriginalString",
                    "GetState_String"
                });
                if (HasTextPrimitiveFields(primitive))
                {
                    AppendMethodValues(primitive, "textGeometry", new[]
                    {
                        "GetState_XLocation",
                        "GetState_YLocation",
                        "GetState_Rotation",
                        "GetState_Size",
                        "GetState_Width",
                        "GetState_Mirror",
                        "GetState_Multiline",
                        "GetState_UseTTFonts",
                        "GetState_TTFTextHeight",
                        "GetState_TTFTextWidth"
                    });
                }
            }

            private void AppendTrack(object primitive, int originX, int originY, double rotation, bool mirrored)
            {
                AppendMethodValues(primitive, "track", new[]
                {
                    "GetState_X1",
                    "GetState_Y1",
                    "GetState_X2",
                    "GetState_Y2",
                    "GetState_Width"
                });
                if (TryGetInt(primitive, "GetState_X1", out int x1)
                    && TryGetInt(primitive, "GetState_Y1", out int y1)
                    && TryGetInt(primitive, "GetState_X2", out int x2)
                    && TryGetInt(primitive, "GetState_Y2", out int y2))
                {
                    TransformPoint(x1, y1, originX, originY, rotation, mirrored, out double lx1, out double ly1);
                    TransformPoint(x2, y2, originX, originY, rotation, mirrored, out double lx2, out double ly2);
                    _text.AppendLine("    trackLocalMm: x1=" + Format(lx1) + ", y1=" + Format(ly1)
                        + ", x2=" + Format(lx2) + ", y2=" + Format(ly2)
                        + ", width=" + Format(AltiumApi.CoordToMm(GetInt(primitive, "GetState_Width"))));
                }
            }

            private void AppendArc(object primitive, int originX, int originY, double rotation, bool mirrored)
            {
                AppendMethodValues(primitive, "arc", new[]
                {
                    "GetState_CenterX",
                    "GetState_CenterY",
                    "GetState_XCenter",
                    "GetState_YCenter",
                    "GetState_Radius",
                    "GetState_StartAngle",
                    "GetState_EndAngle",
                    "GetState_LineWidth",
                    "GetState_StartX",
                    "GetState_StartY",
                    "GetState_EndX",
                    "GetState_EndY"
                });
                if (TryGetInt(primitive, "GetState_CenterX", out int centerRawX)
                    && TryGetInt(primitive, "GetState_CenterY", out int centerRawY)
                    && TryGetInt(primitive, "GetState_Radius", out int radiusRaw))
                {
                    TransformPoint(centerRawX, centerRawY, originX, originY, rotation, mirrored, out double centerX, out double centerY);
                    double startAngle = NormalizeAngle(GetDouble(primitive, "GetState_StartAngle") - rotation);
                    double endAngle = NormalizeAngle(GetDouble(primitive, "GetState_EndAngle") - rotation);
                    if (mirrored)
                    {
                        startAngle = NormalizeAngle(180.0 - startAngle);
                        endAngle = NormalizeAngle(180.0 - endAngle);
                    }
                    _text.AppendLine("    arcLocalMm: centerX=" + Format(centerX)
                        + ", centerY=" + Format(centerY)
                        + ", radius=" + Format(AltiumApi.CoordToMm(radiusRaw))
                        + ", startAngle=" + Format(startAngle)
                        + ", endAngle=" + Format(endAngle)
                        + ", sweep=" + Format(NormalizeSweep(endAngle - startAngle)));
                }
            }

            private void AppendPolygon(object primitive)
            {
                List<object> segments = GetPolygonSegments(primitive);
                _text.AppendLine("    polygon: pointCount=" + ValueText(Invoke(primitive, "GetState_PointCount"))
                    + ", segmentCount=" + segments.Count.ToString(CultureInfo.InvariantCulture));
                int limit = Math.Min(MaxPolygonSegmentsToWrite, segments.Count);
                for (int index = 0; index < limit; index++)
                {
                    object segment = segments[index];
                    _text.AppendLine("      segment[" + index.ToString(CultureInfo.InvariantCulture) + "]: type=" + TypeName(segment)
                        + ", kind=" + ValueText(Invoke(segment, "GetKind"))
                        + ", vx=" + CoordValueText(Invoke(segment, "GetVx"))
                        + ", vy=" + CoordValueText(Invoke(segment, "GetVy"))
                        + ", cx=" + CoordValueText(Invoke(segment, "GetCx"))
                        + ", cy=" + CoordValueText(Invoke(segment, "GetCy"))
                        + ", radius=" + CoordValueText(Invoke(segment, "GetRadius"))
                        + ", angle1=" + ValueText(Invoke(segment, "GetAngle1"))
                        + ", angle2=" + ValueText(Invoke(segment, "GetAngle2")));
                }
                if (segments.Count > limit)
                    _text.AppendLine("      segment details truncated at " + limit.ToString(CultureInfo.InvariantCulture) + " entries.");
            }

            private void AppendRegion(object primitive)
            {
                object polygon = Invoke(primitive, "Internal_GetGeometricPolygon");
                object mainContour = Invoke(primitive, "Internal_GetMainContour");
                _text.AppendLine("    region: geometricPolygon=" + TypeName(polygon)
                    + ", polygonContourCount=" + ValueText(Invoke(polygon, "GetState_Count"))
                    + ", mainContour=" + TypeName(mainContour)
                    + ", mainContourPointCount=" + ValueText(Invoke(mainContour, "GetState_Count")));
            }

            private void AppendMethodValues(object target, string label, IReadOnlyList<string> methodNames)
            {
                var parts = new List<string>();
                foreach (string methodName in methodNames)
                    parts.Add(methodName + "=" + ValueText(Invoke(target, methodName)));
                _text.AppendLine("    " + label + ": " + string.Join(", ", parts));
            }

            private static string FormatCounts<TKey>(Dictionary<TKey, int> counts)
            {
                if (counts == null || counts.Count == 0)
                    return "";
                return string.Join(", ", counts.OrderBy(pair => Convert.ToString(pair.Key, CultureInfo.InvariantCulture), StringComparer.Ordinal)
                    .Select(pair => Convert.ToString(pair.Key, CultureInfo.InvariantCulture) + "=" + pair.Value.ToString(CultureInfo.InvariantCulture)));
            }

            private static string CoordValueText(object value)
            {
                if (!TryConvertToInt(value, out int coord))
                    return ValueText(value);
                return coord.ToString(CultureInfo.InvariantCulture) + " (" + Format(AltiumApi.CoordToMm(coord)) + "mm)";
            }

            private static string ValueText(object value)
            {
                if (value == null)
                    return "<null>";
                if (ReferenceEquals(value, Missing.Value))
                    return "<missing>";
                try
                {
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null-string>";
                }
                catch
                {
                    return TypeName(value);
                }
            }

            private static string TypeName(object value)
            {
                return value == null ? "<null>" : value.GetType().FullName;
            }
        }

        private static void TracePrimitiveCapture(
            string componentName,
            int total,
            Dictionary<int, int> objectCounts,
            Dictionary<string, int> layerCounts,
            int mechanicalCount,
            int arcCandidates,
            int mechanicalArcCandidates,
            int createdArcs,
            int failedArcs)
        {
            try
            {
                string objects = string.Join(", ", objectCounts.OrderBy(pair => pair.Key).Select(pair => pair.Key.ToString(CultureInfo.InvariantCulture) + "=" + pair.Value.ToString(CultureInfo.InvariantCulture)));
                string layers = string.Join(", ", layerCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture)));
                EasyEDALoaderModule.Trace("ExportShape capture " + componentName
                    + ": total=" + total.ToString(CultureInfo.InvariantCulture)
                    + ", mechanical2=" + mechanicalCount.ToString(CultureInfo.InvariantCulture)
                    + ", arcs=" + arcCandidates.ToString(CultureInfo.InvariantCulture)
                    + ", mechanical2Arcs=" + mechanicalArcCandidates.ToString(CultureInfo.InvariantCulture)
                    + ", createdArcs=" + createdArcs.ToString(CultureInfo.InvariantCulture)
                    + ", failedArcs=" + failedArcs.ToString(CultureInfo.InvariantCulture)
                    + ", objects=[" + objects + "]"
                    + ", layers=[" + layers + "]");
            }
            catch
            {
            }
        }

        private static void IncrementCount<TKey>(Dictionary<TKey, int> counts, TKey key)
        {
            if (counts.TryGetValue(key, out int count))
                counts[key] = count + 1;
            else
                counts[key] = 1;
        }

        private static bool TryGetExpectedMechanical2LayerNumber(out int layer)
        {
            layer = 0;
            try
            {
                layer = new V7_Layer(TLayerConstant.eMechanical2).Number();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int GetObjectId(object primitive)
        {
            object value = Invoke(primitive, "Internal_GetState_ObjectID")
                ?? Invoke(primitive, "GetState_ObjectID")
                ?? Invoke(primitive, "GetState_ObjectId")
                ?? Invoke(primitive, "ObjectId");
            return TryConvertToInt(value, out int id) ? id : 0;
        }

        private static bool TryGetBoundsCoord(object primitive, out int left, out int bottom, out int right, out int top)
        {
            left = 0;
            bottom = 0;
            right = 0;
            top = 0;
            object rect = Invoke(primitive, "Internal_BoundingRectangle") ?? Invoke(primitive, "Internal_xBoundingRectangle");
            return rect != null
                && TryConvertToInt(Invoke(rect, "GetLeft"), out left)
                && TryConvertToInt(Invoke(rect, "GetBottom"), out bottom)
                && TryConvertToInt(Invoke(rect, "GetRight"), out right)
                && TryConvertToInt(Invoke(rect, "GetTop"), out top)
                && right > left
                && top > bottom;
        }

        private static object Invoke(object target, string methodName, params object[] args)
        {
            if (target == null)
                return null;

            object typed = TryInvokeTyped(target, methodName, args);
            if (typed != Missing.Value)
                return typed;

            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != methodName || method.GetParameters().Length != args.Length)
                    continue;

                try
                {
                    return method.Invoke(target, args);
                }
                catch
                {
                }
            }

            object comValue = InvokeByComDispatch(target, methodName, args);
            if (comValue != Missing.Value)
                return comValue;

            return null;
        }

        private static bool HasCallable(object target, string methodName)
        {
            if (target == null)
                return false;

            if (target.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Any(method => method.Name == methodName && method.GetParameters().Length == 0))
                return true;

            return InvokeByComDispatch(target, methodName, Array.Empty<object>()) != Missing.Value;
        }

        private static object InvokeByComDispatch(object target, string methodName, object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
                return Missing.Value;

            Type type = target.GetType();
            if (!type.IsCOMObject)
                return Missing.Value;

            object value = InvokeByComTypeMember(target, methodName, args, BindingFlags.InvokeMethod);
            if (value != Missing.Value)
                return value;

            if (args == null || args.Length == 0)
            {
                value = InvokeByComTypeMember(target, methodName, null, BindingFlags.GetProperty);
                if (value != Missing.Value)
                    return value;
            }

            value = InvokeByIDispatch(target, methodName, args, DispatchMethod);
            if (value != Missing.Value)
                return value;

            if (args == null || args.Length == 0)
            {
                value = InvokeByIDispatch(target, methodName, null, DispatchPropertyGet);
                if (value != Missing.Value)
                    return value;
            }

            return Missing.Value;
        }

        private static object InvokeByComTypeMember(object target, string methodName, object[] args, BindingFlags flags)
        {
            try
            {
                return target.GetType().InvokeMember(
                    methodName,
                    flags | BindingFlags.Instance | BindingFlags.Public,
                    null,
                    target,
                    args,
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                return Missing.Value;
            }
        }

        private const ushort DispatchMethod = 1;
        private const ushort DispatchPropertyGet = 2;
        private const int LocaleSystemDefault = 0x0800;
        private static readonly Guid IidNull = Guid.Empty;

        [ComImport]
        [Guid("00020400-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDispatch
        {
            [PreserveSig]
            int GetTypeInfoCount(out int pctinfo);

            [PreserveSig]
            int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo);

            [PreserveSig]
            int GetIDsOfNames(
                ref Guid riid,
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgszNames,
                int cNames,
                int lcid,
                [MarshalAs(UnmanagedType.LPArray)] int[] rgDispId);

            [PreserveSig]
            int Invoke(
                int dispIdMember,
                ref Guid riid,
                int lcid,
                ushort wFlags,
                ref DispatchParameters pDispParams,
                out object pVarResult,
                ref DispatchExceptionInfo pExcepInfo,
                out uint puArgErr);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatchParameters
        {
            public IntPtr rgvarg;
            public IntPtr rgdispidNamedArgs;
            public int cArgs;
            public int cNamedArgs;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatchExceptionInfo
        {
            public ushort wCode;
            public ushort wReserved;
            public IntPtr bstrSource;
            public IntPtr bstrDescription;
            public IntPtr bstrHelpFile;
            public uint dwHelpContext;
            public IntPtr pvReserved;
            public IntPtr pfnDeferredFillIn;
            public int scode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeVariant
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr data1;
            public IntPtr data2;
        }

        [DllImport("oleaut32.dll")]
        private static extern int VariantClear(IntPtr pvarg);

        private static object InvokeByIDispatch(object target, string methodName, object[] args, ushort dispatchFlags)
        {
            IntPtr dispatchPointer = IntPtr.Zero;
            IntPtr variantArgs = IntPtr.Zero;

            try
            {
                dispatchPointer = Marshal.GetIDispatchForObject(target);
                var dispatch = (IDispatch)Marshal.GetObjectForIUnknown(dispatchPointer);
                string[] names = { methodName };
                int[] dispIds = { 0 };
                Guid iidNull = IidNull;
                if (dispatch.GetIDsOfNames(ref iidNull, names, names.Length, LocaleSystemDefault, dispIds) != 0)
                    return Missing.Value;

                object[] callArgs = args ?? Array.Empty<object>();
                int variantSize = Marshal.SizeOf<NativeVariant>();
                if (callArgs.Length > 0)
                {
                    variantArgs = Marshal.AllocCoTaskMem(variantSize * callArgs.Length);
                    for (int i = 0; i < callArgs.Length; i++)
                    {
                        IntPtr argAddress = IntPtr.Add(variantArgs, i * variantSize);
                        Marshal.GetNativeVariantForObject(callArgs[callArgs.Length - 1 - i], argAddress);
                    }
                }

                var parameters = new DispatchParameters
                {
                    rgvarg = variantArgs,
                    rgdispidNamedArgs = IntPtr.Zero,
                    cArgs = callArgs.Length,
                    cNamedArgs = 0
                };
                var exceptionInfo = new DispatchExceptionInfo();
                uint argError;
                iidNull = IidNull;
                int hr = dispatch.Invoke(dispIds[0], ref iidNull, LocaleSystemDefault, dispatchFlags, ref parameters, out object result, ref exceptionInfo, out argError);
                return hr == 0 ? result : Missing.Value;
            }
            catch
            {
                return Missing.Value;
            }
            finally
            {
                if (variantArgs != IntPtr.Zero)
                {
                    object[] callArgs = args ?? Array.Empty<object>();
                    int variantSize = Marshal.SizeOf<NativeVariant>();
                    for (int i = 0; i < callArgs.Length; i++)
                        VariantClear(IntPtr.Add(variantArgs, i * variantSize));
                    Marshal.FreeCoTaskMem(variantArgs);
                }

                if (dispatchPointer != IntPtr.Zero)
                    Marshal.Release(dispatchPointer);
            }
        }

        private static object TryInvokeTyped(object target, string methodName, object[] args)
        {
            try
            {
                if (target is IClient client)
                {
                    switch (methodName)
                    {
                        case "GetDocumentKindFromDocumentPath" when args.Length == 1:
                            return client.GetDocumentKindFromDocumentPath(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                        case "OpenDocumentShowOrHide" when args.Length == 3:
                            return client.Internal_OpenDocumentShowOrHide(
                                Convert.ToString(args[0], CultureInfo.InvariantCulture),
                                Convert.ToString(args[1], CultureInfo.InvariantCulture),
                                Convert.ToBoolean(args[2], CultureInfo.InvariantCulture));
                        case "OpenDocument" when args.Length == 2:
                            return client.Internal_OpenDocument(
                                Convert.ToString(args[0], CultureInfo.InvariantCulture),
                                Convert.ToString(args[1], CultureInfo.InvariantCulture));
                        case "ShowDocument" when args.Length == 1:
                            client.ShowDocument(args[0]);
                            return null;
                        case "CloseDocument" when args.Length == 1:
                            client.CloseDocument(args[0]);
                            return null;
                    }
                }

                if (target is IPCB_Board board)
                {
                    switch (methodName)
                    {
                        case "Internal_GetState_FullComponents" when args.Length == 0:
                        case "GetState_FullComponents" when args.Length == 0:
                            if (target is IPCB_BoardEx boardEx)
                                return boardEx.Internal_GetState_FullComponents();
                            break;
                        case "Internal_BoardIterator_Create" when args.Length == 0:
                        case "BoardIterator_Create" when args.Length == 0:
                            return board.Internal_BoardIterator_Create();
                        case "BoardIterator_Destroy" when args.Length == 1:
                            object iterator = args[0];
                            board.BoardIterator_Destroy(ref iterator);
                            return null;
                        case "Internal_GetState_SelectecObject" when args.Length == 1:
                            return board.Internal_GetState_SelectecObject(Convert.ToInt32(args[0], CultureInfo.InvariantCulture));
                        case "GetState_SelectecObjectCount" when args.Length == 0:
                            return board.GetState_SelectecObjectCount();
                        case "SelectedObjectsCount" when args.Length == 0:
                            return board.SelectedObjectsCount();
                    }
                }

                if (target is IPCB_FullComponents fullComponents)
                {
                    switch (methodName)
                    {
                        case "Internal_GetComponentsForCurrentVariant" when args.Length == 0:
                        case "GetComponentsForCurrentVariant" when args.Length == 0:
                            return fullComponents.Internal_GetComponentsForCurrentVariant();
                        case "Internal_GetComponentsForAllVariants" when args.Length == 0:
                        case "GetComponentsForAllVariants" when args.Length == 0:
                            return fullComponents.Internal_GetComponentsForAllVariants();
                    }
                }

                if (target is IPCB_FullComponentList fullComponentList)
                {
                    switch (methodName)
                    {
                        case "GetCount" when args.Length == 0:
                            return fullComponentList.GetCount();
                        case "Internal_GetItem" when args.Length == 1:
                        case "GetItem" when args.Length == 1:
                            return fullComponentList.Internal_GetItem(Convert.ToInt32(args[0], CultureInfo.InvariantCulture));
                    }
                }

                if (target is IPCB_FullComponent fullComponent)
                {
                    switch (methodName)
                    {
                        case "Internal_GetFootprint" when args.Length == 0:
                        case "GetFootprint" when args.Length == 0:
                            return fullComponent.Internal_GetFootprint();
                    }
                }

                if (target is IPCB_Library pcbLib)
                {
                    switch (methodName)
                    {
                        case "Internal_LibraryIterator_Create" when args.Length == 0:
                        case "LibraryIterator_Create" when args.Length == 0:
                            return pcbLib.Internal_LibraryIterator_Create();
                        case "LibraryIterator_Destroy" when args.Length == 1:
                            object iterator = args[0];
                            pcbLib.LibraryIterator_Destroy(ref iterator);
                            return null;
                        case "GetComponentByName" when args.Length == 1:
                            return IPCB_LibraryHelper.GetComponentByName(pcbLib, Convert.ToString(args[0], CultureInfo.InvariantCulture));
                        case "Internal_GetComponentByName" when args.Length == 1:
                            return pcbLib.Internal_GetComponentByName(Convert.ToString(args[0], CultureInfo.InvariantCulture));
                        case "ComponentCount" when args.Length == 0:
                            return pcbLib.ComponentCount();
                        case "GetComponent" when args.Length == 1:
                            return IPCB_LibraryHelper.GetComponent(pcbLib, Convert.ToInt32(args[0], CultureInfo.InvariantCulture));
                        case "Internal_GetComponent" when args.Length == 1:
                            return pcbLib.Internal_GetComponent(Convert.ToInt32(args[0], CultureInfo.InvariantCulture));
                    }
                }

                if (target is IPCB_Group group)
                {
                    switch (methodName)
                    {
                        case "Internal_GroupIterator_Create" when args.Length == 0:
                        case "GroupIterator_Create" when args.Length == 0:
                            return group.Internal_GroupIterator_Create();
                        case "Internal_GetPrimitiveAt" when args.Length == 2:
                            return group.Internal_GetPrimitiveAt(
                                Convert.ToInt32(args[0], CultureInfo.InvariantCulture),
                                Convert.ToInt32(args[1], CultureInfo.InvariantCulture));
                        case "GroupIterator_Destroy" when args.Length == 1:
                            object iterator = args[0];
                            group.GroupIterator_Destroy(ref iterator);
                            return null;
                        case "GetState_XLocation" when args.Length == 0:
                            return group.GetState_XLocation();
                        case "GetState_YLocation" when args.Length == 0:
                            return group.GetState_YLocation();
                    }
                }

                if (target is IPCB_Component component)
                {
                    switch (methodName)
                    {
                        case "GetState_Pattern" when args.Length == 0:
                            return component.GetState_Pattern();
                        case "GetState_FootprintConfiguratorName" when args.Length == 0:
                            return component.GetState_FootprintConfiguratorName();
                        case "GetState_Rotation" when args.Length == 0:
                            return component.GetState_Rotation();
                        case "GetState_FlippedOnLayer" when args.Length == 0:
                            return component.GetState_FlippedOnLayer();
                        case "Internal_GetState_Name" when args.Length == 0:
                            return component.Internal_GetState_Name();
                        case "GetState_SourceDesignator" when args.Length == 0:
                            return component.GetState_SourceDesignator();
                    }
                }

                if (target is IPCB_LibComponent libComponent)
                {
                    switch (methodName)
                    {
                        case "GetState_Pattern" when args.Length == 0:
                            return libComponent.GetState_Pattern();
                    }
                }

                if (target is IPCB_RectangularPrimitive rectangularPrimitive)
                {
                    switch (methodName)
                    {
                        case "GetState_XLocation" when args.Length == 0:
                            return rectangularPrimitive.GetState_XLocation();
                        case "GetState_YLocation" when args.Length == 0:
                            return rectangularPrimitive.GetState_YLocation();
                        case "GetState_Rotation" when args.Length == 0:
                            return rectangularPrimitive.GetState_Rotation();
                    }
                }

                if (target is IPCB_Primitive primitive)
                {
                    switch (methodName)
                    {
                        case "Internal_BoundingRectangle" when args.Length == 0:
                            return primitive.Internal_BoundingRectangle();
                        case "Internal_GetState_Component" when args.Length == 0:
                            return primitive.Internal_GetState_Component();
                        case "Internal_GetState_Layer" when args.Length == 0:
                            return primitive.Internal_GetState_Layer();
                        case "Internal_GetState_ObjectID" when args.Length == 0:
                            return primitive.Internal_GetState_ObjectID();
                        case "Internal_GetState_V7Layer" when args.Length == 0:
                            return primitive.Internal_GetState_V7Layer();
                        case "GetState_Selected" when args.Length == 0:
                            return primitive.GetState_Selected();
                    }
                }

                if (target is IPCB_Track track)
                {
                    switch (methodName)
                    {
                        case "GetState_X1" when args.Length == 0:
                            return track.GetState_X1();
                        case "GetState_Y1" when args.Length == 0:
                            return track.GetState_Y1();
                        case "GetState_X2" when args.Length == 0:
                            return track.GetState_X2();
                        case "GetState_Y2" when args.Length == 0:
                            return track.GetState_Y2();
                        case "GetState_Width" when args.Length == 0:
                            return track.GetState_Width();
                    }
                }

                if (target is IPCB_Arc arc)
                {
                    switch (methodName)
                    {
                        case "GetState_CenterX" when args.Length == 0:
                            return arc.GetState_CenterX();
                        case "GetState_CenterY" when args.Length == 0:
                            return arc.GetState_CenterY();
                        case "GetState_Radius" when args.Length == 0:
                            return arc.GetState_Radius();
                        case "GetState_StartAngle" when args.Length == 0:
                            return arc.GetState_StartAngle();
                        case "GetState_EndAngle" when args.Length == 0:
                            return arc.GetState_EndAngle();
                        case "GetState_LineWidth" when args.Length == 0:
                            return arc.GetState_LineWidth();
                        case "GetState_StartX" when args.Length == 0:
                            return arc.GetState_StartX();
                        case "GetState_StartY" when args.Length == 0:
                            return arc.GetState_StartY();
                        case "GetState_EndX" when args.Length == 0:
                            return arc.GetState_EndX();
                        case "GetState_EndY" when args.Length == 0:
                            return arc.GetState_EndY();
                    }
                }

                if (target is IPCB_Fill fill)
                {
                    switch (methodName)
                    {
                        case "GetState_LocationX" when args.Length == 0:
                            return fill.GetState_LocationX();
                        case "GetState_LocationY" when args.Length == 0:
                            return fill.GetState_LocationY();
                        case "GetState_Width" when args.Length == 0:
                            return fill.GetState_Width();
                        case "GetState_Length" when args.Length == 0:
                            return fill.GetState_Length();
                    }
                }

                if (target is IPCB_Region region)
                {
                    switch (methodName)
                    {
                        case "Internal_GetGeometricPolygon" when args.Length == 0:
                            return region.Internal_GetGeometricPolygon();
                        case "Internal_GetMainContour" when args.Length == 0:
                            return region.Internal_GetMainContour();
                    }
                }

                if (target is IPCB_Polygon polygon)
                {
                    switch (methodName)
                    {
                        case "GetState_PointCount" when args.Length == 0:
                            return polygon.GetState_PointCount();
                        case "Internal_GetState_Segments" when args.Length == 1 && TryConvertToInt(args[0], out int segmentIndex):
                            return polygon.Internal_GetState_Segments(segmentIndex);
                        case "Internal_xBoundingRectangle" when args.Length == 0:
                            return polygon.Internal_xBoundingRectangle();
                    }
                }

                if (target is IPCB_GeometricPolygon geometricPolygon)
                {
                    switch (methodName)
                    {
                        case "GetState_Count" when args.Length == 0:
                            return geometricPolygon.GetState_Count();
                        case "Internal_GetState_Contour" when args.Length == 1 && TryConvertToInt(args[0], out int contourIndex):
                            return geometricPolygon.Internal_GetState_Contour(contourIndex);
                    }
                }

                if (target is IPCB_Contour contour)
                {
                    switch (methodName)
                    {
                        case "GetState_Count" when args.Length == 0:
                            return contour.GetState_Count();
                        case "GetState_PointX" when args.Length == 1 && TryConvertToInt(args[0], out int pointXIndex):
                            return contour.GetState_PointX(pointXIndex);
                        case "GetState_PointY" when args.Length == 1 && TryConvertToInt(args[0], out int pointYIndex):
                            return contour.GetState_PointY(pointYIndex);
                    }
                }

                if (target is IPolySegment segment)
                {
                    switch (methodName)
                    {
                        case "GetKind" when args.Length == 0:
                            return segment.GetKind();
                        case "GetVx" when args.Length == 0:
                            return segment.GetVx();
                        case "GetVy" when args.Length == 0:
                            return segment.GetVy();
                        case "GetCx" when args.Length == 0:
                            return segment.GetCx();
                        case "GetCy" when args.Length == 0:
                            return segment.GetCy();
                        case "GetRadius" when args.Length == 0:
                            return segment.GetRadius();
                        case "GetAngle1" when args.Length == 0:
                            return segment.GetAngle1();
                        case "GetAngle2" when args.Length == 0:
                            return segment.GetAngle2();
                    }
                }

                if (target is IPCB_Text text)
                {
                    switch (methodName)
                    {
                        case "GetState_Size" when args.Length == 0:
                            return text.GetState_Size();
                        case "GetState_Width" when args.Length == 0:
                            return text.GetState_Width();
                        case "GetState_Mirror" when args.Length == 0:
                            return text.GetState_Mirror();
                        case "GetState_Multiline" when args.Length == 0:
                            return text.GetState_Multiline();
                        case "GetState_UseTTFonts" when args.Length == 0:
                            return text.GetState_UseTTFonts();
                        case "GetState_TTFTextHeight" when args.Length == 0:
                            return text.GetState_TTFTextHeight();
                        case "GetState_TTFTextWidth" when args.Length == 0:
                            return text.GetState_TTFTextWidth();
                        case "GetState_Text" when args.Length == 0:
                            return text.GetState_Text();
                        case "GetState_UnderlyingString" when args.Length == 0:
                            return text.GetState_UnderlyingString();
                        case "GetState_ConvertedString" when args.Length == 0:
                            return text.GetState_ConvertedString();
                    }
                }

                if (target is ICoordRect rect)
                {
                    switch (methodName)
                    {
                        case "GetLeft" when args.Length == 0:
                            return rect.GetLeft();
                        case "GetRight" when args.Length == 0:
                            return rect.GetRight();
                        case "GetBottom" when args.Length == 0:
                            return rect.GetBottom();
                        case "GetTop" when args.Length == 0:
                            return rect.GetTop();
                    }
                }

                if (target is IPCB_AbstractIterator abstractIterator)
                {
                    switch (methodName)
                    {
                        case "SetState_FilterAll" when args.Length == 0:
                            abstractIterator.SetState_FilterAll();
                            return null;
                        case "AddFilter_ObjectSet" when args.Length == 1 && args[0] is DXP.ITransportSet objectSet:
                            abstractIterator.AddFilter_ObjectSet(objectSet);
                            return null;
                        case "Internal_FirstPCBObject" when args.Length == 0:
                        case "FirstPCBObject" when args.Length == 0:
                            return abstractIterator.Internal_FirstPCBObject();
                        case "Internal_NextPCBObject" when args.Length == 0:
                        case "NextPCBObject" when args.Length == 0:
                            return abstractIterator.Internal_NextPCBObject();
                    }
                }
            }
            catch
            {
                return null;
            }

            object reflected = InvokeByReflection(target, methodName, args);
            if (!ReferenceEquals(reflected, Missing.Value))
                return reflected;

            return Missing.Value;
        }

        private static object InvokeByReflection(object target, string methodName, object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
                return Missing.Value;

            try
            {
                return target.GetType().InvokeMember(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod,
                    null,
                    target,
                    args ?? Array.Empty<object>(),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
            }

            if (args != null && args.Length != 0)
                return Missing.Value;

            try
            {
                return target.GetType().InvokeMember(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty,
                    null,
                    target,
                    Array.Empty<object>(),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                return Missing.Value;
            }
        }

        private static DXP.ITransportSet CreateObjectSet(params int[] objectIds)
        {
            var set = new DXP.GenericSet();
            int[] mask = set.Mask;
            foreach (int objectId in objectIds ?? Array.Empty<int>())
            {
                if (objectId < 0)
                    continue;

                int index = objectId / 32;
                if (index >= mask.Length)
                    continue;

                mask[index] |= unchecked((int)(1u << (objectId % 32)));
            }
            return new DXP.TransportSet(set);
        }

        private static string ObjectIdentity(object value)
        {
            return FirstNonEmpty(
                Convert.ToString(Invoke(value, "GetState_Handle")),
                Convert.ToString(Invoke(value, "GetState_UniqueId")),
                RuntimeHelpersId(value));
        }

        private static string ComponentDebugIdentity(object value)
        {
            object component = UnwrapExportComponent(value);
            return FirstNonEmpty(
                ReadFootprintName(value),
                ReadDesignator(component),
                TypeName(component) + " #" + ObjectIdentity(component));
        }

        private static string TypeName(object value)
        {
            return value == null ? "<null>" : value.GetType().FullName;
        }

        private static void ThrowIfCancellationRequested(Func<bool> isCancellationRequested)
        {
            if (isCancellationRequested != null && isCancellationRequested())
                throw new OperationCanceledException();
        }

        private static string RuntimeHelpersId(object value)
        {
            return value == null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture);
        }

        private static void GuardIteratorObject(ref int count, int maxCount, string source)
        {
            count++;
            if (count > maxCount)
                throw new InvalidOperationException(source + " iterator exceeded " + maxCount.ToString(CultureInfo.InvariantCulture) + " objects. Export stopped to avoid locking Altium.");
        }

        private static int ClampApiCount(int count, int maxCount, string itemName)
        {
            if (count <= 0)
                return 0;
            if (count > maxCount)
                throw new InvalidOperationException("Altium returned " + count.ToString(CultureInfo.InvariantCulture) + " " + itemName + " entries. Export stopped to avoid locking Altium.");

            return count;
        }

        private static string UniqueFileName(string baseName, HashSet<string> usedNames)
        {
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "shape";

            string candidate = baseName;
            int suffix = 2;
            while (!usedNames.Add(candidate))
            {
                candidate = baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            return candidate;
        }

        private static string SanitizeFileName(string value)
        {
            string cleaned = string.IsNullOrWhiteSpace(value) ? "shape" : value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                cleaned = cleaned.Replace(invalid, '_');
            return cleaned.Length == 0 ? "shape" : cleaned;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        private static int GetInt(object target, string methodName)
        {
            object value = Invoke(target, methodName);
            return TryConvertToInt(value, out int result) ? result : 0;
        }

        private static bool TryGetInt(object target, string methodName, out int result)
        {
            object value = Invoke(target, methodName);
            return TryConvertToInt(value, out result);
        }

        private static double GetDouble(object target, string methodName)
        {
            object value = Invoke(target, methodName);
            if (value == null)
                return 0;

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static bool GetBool(object target, string methodName)
        {
            object value = Invoke(target, methodName);
            if (value == null)
                return false;

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBooleanTrue(object target, string methodName)
        {
            object value = Invoke(target, methodName);
            if (value == null)
                return false;

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertToInt(object raw, out int value)
        {
            value = 0;
            if (raw == null)
                return false;

            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double NormalizeAngle(double angle)
        {
            angle %= 360.0;
            if (angle < 0)
                angle += 360.0;
            return angle;
        }

        private static double NormalizeSweep(double sweep)
        {
            sweep %= 360.0;
            if (sweep < 0)
                sweep += 360.0;
            return sweep;
        }

        private static double ResolveTextRotation(
            double angle,
            bool hasBounds,
            double left,
            double bottom,
            double right,
            double top,
            double anchorY,
            double estimatedWidth,
            double estimatedHeight,
            int characterCount,
            bool multiline,
            out bool centerTextInBounds)
        {
            centerTextInBounds = false;
            double normalized = NormalizeSignedAngle(angle);
            if (!hasBounds || multiline || characterCount < 2)
                return normalized;

            double boundsWidth = Math.Abs(right - left);
            double boundsHeight = Math.Abs(top - bottom);
            bool textExpectedHorizontal = estimatedWidth > estimatedHeight * 1.05;
            bool boundsAreVertical = boundsHeight > boundsWidth * 1.15;
            bool reportedRotationIsVertical = Math.Abs(Math.Abs(normalized) - 90.0) < 1.0;
            if (!textExpectedHorizontal || !boundsAreVertical || reportedRotationIsVertical)
                return normalized;

            centerTextInBounds = true;
            double distanceToTop = Math.Abs(anchorY - top);
            double distanceToBottom = Math.Abs(anchorY - bottom);
            double ambiguousThreshold = Math.Max(0.01, estimatedHeight * 0.2);
            if (Math.Abs(distanceToTop - distanceToBottom) <= ambiguousThreshold)
                return -90.0;

            return distanceToTop < distanceToBottom ? 90.0 : -90.0;
        }

        private static double NormalizeSignedAngle(double angle)
        {
            angle %= 360.0;
            if (angle > 180.0)
                angle -= 360.0;
            if (angle <= -180.0)
                angle += 360.0;
            return angle;
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static List<object> GetPolygonSegments(object polygon)
        {
            var result = new List<object>();
            int pointCount = ClampApiCount(GetInt(polygon, "GetState_PointCount"), MaxPolygonSegments, "polygon segment");
            for (int index = 0; index < pointCount; index++)
            {
                object segment = Invoke(polygon, "Internal_GetState_Segments", index);
                if (segment != null)
                    result.Add(segment);
            }

            if (result.Count == 0 && pointCount > 0)
            {
                for (int index = 1; index <= pointCount; index++)
                {
                    object segment = Invoke(polygon, "Internal_GetState_Segments", index);
                    if (segment != null)
                        result.Add(segment);
                }
            }

            return result;
        }

        private static bool IsArcPolySegment(object segment)
        {
            object kind = Invoke(segment, "GetKind");
            return TryConvertToInt(kind, out int value) && value == (int)TPolySegmentType.ePolySegmentArc;
        }

        private static string Format(double value)
        {
            if (Math.Abs(value) < 0.0000005)
                value = 0;
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private abstract class SvgPrimitive
        {
            public double Left { get; set; } = double.PositiveInfinity;
            public double Bottom { get; set; } = double.PositiveInfinity;
            public double Right { get; set; } = double.NegativeInfinity;
            public double Top { get; set; } = double.NegativeInfinity;
            public double StrokeWidth { get; set; }

            public abstract void Write(XmlWriter writer);
        }

        private sealed class SvgPathPrimitive : SvgPrimitive
        {
            public string Data { get; set; }
            public string Fill { get; set; } = "none";

            public void AddBounds(double x, double y)
            {
                Left = Math.Min(Left, x);
                Right = Math.Max(Right, x);
                Bottom = Math.Min(Bottom, y);
                Top = Math.Max(Top, y);
            }

            public override void Write(XmlWriter writer)
            {
                writer.WriteStartElement("path");
                writer.WriteAttributeString("d", Data);
                writer.WriteAttributeString("fill", Fill);
                writer.WriteAttributeString("stroke", Fill == "none" ? "#111111" : "none");
                if (Fill == "none")
                {
                    writer.WriteAttributeString("stroke-width", Format(StrokeWidth));
                    writer.WriteAttributeString("stroke-linecap", "round");
                    writer.WriteAttributeString("stroke-linejoin", "round");
                }
                writer.WriteEndElement();
            }
        }

        private sealed class SvgTextPrimitive : SvgPrimitive
        {
            public string Text { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double FontSize { get; set; }
            public double TextWidth { get; set; }
            public double Rotation { get; set; }
            public bool CenterAnchored { get; set; }

            public override void Write(XmlWriter writer)
            {
                writer.WriteStartElement("text");
                if (CenterAnchored)
                {
                    writer.WriteAttributeString("x", Format(-TextWidth / 2.0));
                    writer.WriteAttributeString("y", Format(FontSize * 0.35));
                }
                else
                {
                    writer.WriteAttributeString("x", "0");
                    writer.WriteAttributeString("y", "0");
                }
                writer.WriteAttributeString("fill", "#111111");
                writer.WriteAttributeString("font-family", "Arial");
                writer.WriteAttributeString("font-size", Format(FontSize));
                writer.WriteAttributeString("transform",
                    "translate(" + Format(X) + " " + Format(Y) + ") rotate(" + Format(Rotation) + ") scale(1 -1)");
                writer.WriteString(Text ?? string.Empty);
                writer.WriteEndElement();
            }
        }

        private readonly struct SvgPoint
        {
            public SvgPoint(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }
    }
}
