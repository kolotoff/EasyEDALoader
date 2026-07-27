using PCB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyEDA_Loader
{
    internal static class LayoutDuplicationCapture
    {
        public static bool TryCaptureLayoutDuplicationSession(out LayoutDuplicationSession session, out string error)
        {
            return TryCaptureLayoutDuplicationSession(null, out session, out error);
        }

        public static bool TryCaptureLayoutDuplicationSession(
            DXP.IServerDocumentView commandView,
            out LayoutDuplicationSession session,
            out string error)
        {
            session = null;
            error = null;

            EasyEDALoaderModule.Trace("Duplicate layout capture: resolving PCB board.");
            IPCB_Board board = LayoutDuplicationPcbAccess.GetCurrentBoard(commandView);
            if (board == null)
            {
                error = "Open a PCB document before running Duplicate layout.";
                return false;
            }

            EasyEDALoaderModule.Trace("Duplicate layout capture: reading selected components.");
            var selectedComponents = CaptureSelectedSourceComponents(board);
            EasyEDALoaderModule.Trace("Duplicate layout capture: selected components=" + selectedComponents.Count);
            if (selectedComponents.Count == 0)
            {
                error = "Select source PCB components before running Duplicate layout.";
                return false;
            }

            EasyEDALoaderModule.Trace("Duplicate layout capture: reading selected routing.");
            int selectedRoutingPrimitiveCount = CaptureSelectedRoutingPrimitives(board).Count;
            session = new LayoutDuplicationSession
            {
                Board = board,
                SelectedRoutingPrimitiveCount = selectedRoutingPrimitiveCount,
                LastUsedModel = OllamaLayoutMappingClient.LoadLastUsedModel()
            };

            session.SourceComponents.AddRange(selectedComponents);
            return true;
        }

        public static List<LayoutComponentSnapshot> CaptureSelectedSourceComponents(IPCB_Board board)
        {
            List<object> selectedObjects = LayoutDuplicationPcbAccess.GetSelectedObjects(board);
            var selectedComponents = new List<LayoutComponentSnapshot>();
            var selectedDesignators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object selectedObject in selectedObjects)
            {
                if (LayoutDuplicationPcbAccess.IsComponentObject(selectedObject))
                {
                    AddSelectedDesignator(selectedDesignators, selectedObject);
                    AddComponentSnapshotIfValid(selectedComponents, board, selectedObject);
                    continue;
                }

                if (selectedObject is IPCB_Primitive selectedPrimitive)
                {
                    object typedParent = selectedPrimitive.Internal_GetState_Component();
                    if (LayoutDuplicationPcbAccess.IsComponentObject(typedParent))
                    {
                        AddSelectedDesignator(selectedDesignators, typedParent);
                        AddComponentSnapshotIfValid(selectedComponents, board, typedParent);
                        continue;
                    }
                }

                if (LayoutDuplicationPcbAccess.IsTextObject(selectedObject))
                {
                    AddSelectedText(selectedTexts, Convert.ToString(LayoutDuplicationPcbAccess.Invoke(selectedObject, "GetState_Text")));
                    AddSelectedText(selectedTexts, Convert.ToString(LayoutDuplicationPcbAccess.Invoke(selectedObject, "GetState_UnderlyingString")));
                }

                object parentComponent = LayoutDuplicationPcbAccess.Invoke(selectedObject, "Internal_GetState_Component")
                    ?? LayoutDuplicationPcbAccess.Invoke(selectedObject, "GetState_Component");
                if (LayoutDuplicationPcbAccess.IsComponentObject(parentComponent))
                {
                    AddSelectedDesignator(selectedDesignators, parentComponent);
                    AddComponentSnapshotIfValid(selectedComponents, board, parentComponent);
                }
            }

            if (selectedComponents.Count > 0)
                return selectedComponents
                    .GroupBy(component => component.Designator, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(component => component.Designator, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            List<LayoutComponentSnapshot> boardComponents = CaptureBoardComponents(board);
            selectedComponents = boardComponents
               .Where(component =>
                   LayoutDuplicationPcbAccess.IsSelected(component.PcbObject)
                   || selectedDesignators.Contains(component.Designator)
                   || selectedTexts.Contains(component.Designator)
                   || selectedTexts.Contains(component.Comment)
                   || ComponentHasSelectedChild(component.PcbObject, selectedObjects))
               .OrderBy(component => component.Designator, StringComparer.OrdinalIgnoreCase)
               .ToList();

            if (selectedComponents.Count == 0)
                TraceSelectedComponentCapture(board, selectedObjects, selectedDesignators, selectedTexts, boardComponents);

            return selectedComponents;
        }

        private static void AddComponentSnapshotIfValid(List<LayoutComponentSnapshot> components, IPCB_Board board, object component)
        {
            LayoutComponentSnapshot snapshot = CaptureComponentSnapshot(board, component, includePads: false);
            if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Designator))
                components.Add(snapshot);
        }

        private static void AddSelectedDesignator(HashSet<string> designators, object component)
        {
            string designator = ReadDesignator(component);
            if (!string.IsNullOrWhiteSpace(designator))
                designators.Add(designator);
        }

        private static void AddSelectedText(HashSet<string> texts, string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add(text);
        }

        private static bool ComponentHasSelectedChild(object componentObject, IReadOnlyList<object> selectedObjects)
        {
            if (!LayoutDuplicationPcbAccess.IsComponentObject(componentObject))
                return false;

            var selectedIdentities = new HashSet<string>(
                selectedObjects
                    .Select(LayoutDuplicationPcbAccess.GetObjectIdentity)
                    .Where(identity => !string.IsNullOrWhiteSpace(identity)),
                StringComparer.OrdinalIgnoreCase);
            object iterator = LayoutDuplicationPcbAccess.Invoke(componentObject, "GroupIterator_Create")
                ?? LayoutDuplicationPcbAccess.Invoke(componentObject, "Internal_GroupIterator_Create");
            if (iterator == null)
                return false;

            try
            {
                LayoutDuplicationPcbAccess.Invoke(iterator, "SetState_FilterAll");
                object primitive = LayoutDuplicationPcbAccess.Invoke(iterator, "FirstPCBObject");
                while (primitive != null)
                {
                    string identity = LayoutDuplicationPcbAccess.GetObjectIdentity(primitive);
                    if (LayoutDuplicationPcbAccess.IsSelected(primitive)
                        || selectedObjects.Contains(primitive)
                        || (!string.IsNullOrWhiteSpace(identity) && selectedIdentities.Contains(identity)))
                        return true;

                    primitive = LayoutDuplicationPcbAccess.Invoke(iterator, "NextPCBObject");
                }
            }
            finally
            {
                LayoutDuplicationPcbAccess.Invoke(componentObject, "GroupIterator_Destroy", iterator);
            }

            return false;
        }

        private static void TraceSelectedComponentCapture(
            IPCB_Board board,
            IReadOnlyList<object> selectedObjects,
            IReadOnlyCollection<string> selectedDesignators,
            IReadOnlyCollection<string> selectedTexts,
            IReadOnlyList<LayoutComponentSnapshot> boardComponents)
        {
            string selectedSummary = string.Join(
                "; ",
                selectedObjects
                    .Take(12)
                    .Select(selectedObject =>
                        (selectedObject?.GetType().Name ?? "<null>")
                        + "|objectId="
                        + LayoutDuplicationPcbAccess.GetObjectId(selectedObject)
                        + "|id="
                        + (LayoutDuplicationPcbAccess.GetObjectIdentity(selectedObject) ?? "")
                        + "|selected="
                        + LayoutDuplicationPcbAccess.IsSelected(selectedObject)));
            string boardObjectSummary = string.Join(
                "; ",
                LayoutDuplicationPcbAccess.EnumerateBoardObjects(board)
                    .Take(20)
                    .Select(boardObject =>
                        (boardObject?.GetType().Name ?? "<null>")
                        + "|objectId="
                        + LayoutDuplicationPcbAccess.GetObjectId(boardObject)
                        + "|selected="
                        + LayoutDuplicationPcbAccess.IsSelected(boardObject)));

            EasyEDALoaderModule.Trace(
                "Duplicate layout selected component capture found none. "
                + "boardSelectedCount="
                + selectedObjects.Count
                + " boardComponentCount="
                + boardComponents.Count
                + " selectedDesignators="
                + string.Join(",", selectedDesignators)
                + " selectedTexts="
                + string.Join(",", selectedTexts)
                + " selectedObjects="
                + selectedSummary
                + " boardObjects="
                + boardObjectSummary);
        }

        public static List<object> CaptureSelectedRoutingPrimitives(IPCB_Board board)
        {
            var result = new List<object>();
            foreach (object primitive in LayoutDuplicationPcbAccess.GetSelectedObjects(board))
            {
                if (IsSupportedRoutingPrimitive(primitive))
                    result.Add(primitive);
            }

            return result;
        }

        public static List<LayoutComponentSnapshot> CaptureTargetAnchors(
            LayoutDuplicationSession session,
            LayoutComponentSnapshot sourceAnchor,
            LayoutSchematicMatchContext schematicContext = null)
        {
            EnsureEquivalentTargetComponentsFromSchematic(session, new[] { sourceAnchor }, schematicContext);
            EnsureDirectRefDesFamilyTargets(session, new[] { sourceAnchor }, schematicContext);
            var selectedSourceDesignators = new HashSet<string>(
                session.SourceComponents.Select(component => component.Designator),
                StringComparer.OrdinalIgnoreCase);

            // exclude all selected source components from target anchor candidates
            var candidates = session.BoardComponents
                .Where(component => !selectedSourceDesignators.Contains(component.Designator))
                .Where(component => IsEquivalentAnchor(sourceAnchor, component))
                .ToList();

            return OrderBySchematicScore(candidates, schematicContext, new[] { sourceAnchor }, Array.Empty<LayoutComponentSnapshot>());
        }

        public static List<LayoutComponentSnapshot> CaptureDestinationCandidates(
            LayoutDuplicationSession session,
            IReadOnlyList<LayoutComponentSnapshot> targetAnchors,
            LayoutSchematicMatchContext schematicContext = null)
        {
            EnsureEquivalentTargetComponentsFromSchematic(session, session.SourceComponents, schematicContext);
            EnsureDirectRefDesFamilyTargets(session, session.SourceComponents, schematicContext);
            var selectedSourceDesignators = new HashSet<string>(
                session.SourceComponents.Select(component => component.Designator),
                StringComparer.OrdinalIgnoreCase);
            var targetDesignators = new HashSet<string>(
                targetAnchors.Select(component => component.Designator),
                StringComparer.OrdinalIgnoreCase);

            var candidates = session.BoardComponents
                .Where(component => !selectedSourceDesignators.Contains(component.Designator))
                .Where(component => targetDesignators.Contains(component.Designator) || MatchesAnySourceComponent(session.SourceComponents, component))
                .ToList();

            return OrderBySchematicScore(candidates, schematicContext, session.SourceComponents, targetAnchors);
        }

        private static void EnsureEquivalentTargetComponentsFromSchematic(
            LayoutDuplicationSession session,
            IEnumerable<LayoutComponentSnapshot> sourceComponents,
            LayoutSchematicMatchContext schematicContext)
        {
            if (session == null || session.Board == null || schematicContext == null || !schematicContext.HasHints)
                return;

            var known = new HashSet<string>(
                session.BoardComponents.Select(component => component.Designator),
                StringComparer.OrdinalIgnoreCase);
            var selectedSources = new HashSet<string>(
                session.SourceComponents.Select(component => component.Designator),
                StringComparer.OrdinalIgnoreCase);
            var sourceList = (sourceComponents ?? Enumerable.Empty<LayoutComponentSnapshot>()).ToList();
            int considered = 0;
            int matched = 0;
            foreach (LayoutSchematicComponentHint hint in schematicContext.Hints)
            {
                considered++;
                if (string.IsNullOrWhiteSpace(hint.Designator)
                    || selectedSources.Contains(hint.Designator)
                    || known.Contains(hint.Designator)
                    || !sourceList.Any(source => IsEquivalentSchematicCandidate(source, hint, schematicContext)))
                    continue;

                object component = LayoutDuplicationPcbAccess.GetComponentByRefDes(session.Board, hint.Designator);
                LayoutComponentSnapshot snapshot = CaptureKnownComponentSnapshot(session.Board, component, hint);
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Designator))
                    continue;

                session.BoardComponents.Add(snapshot);
                known.Add(snapshot.Designator);
                matched++;
            }

            EasyEDALoaderModule.Trace(
                "Duplicate layout schematic target discovery: sources="
                + string.Join(",", sourceList.Select(source => source.Designator))
                + " hints="
                + schematicContext.Hints.Count
                + " considered="
                + considered
                + " matched="
                + matched
                + " boardCandidates="
                + session.BoardComponents.Count);
        }

        private static void EnsureDirectRefDesFamilyTargets(
            LayoutDuplicationSession session,
            IEnumerable<LayoutComponentSnapshot> sourceComponents,
            LayoutSchematicMatchContext schematicContext)
        {
            if (session == null || session.Board == null)
                return;

            var sourceList = (sourceComponents ?? Enumerable.Empty<LayoutComponentSnapshot>())
                .Where(source => source != null && !string.IsNullOrWhiteSpace(source.Designator))
                .ToList();
            if (sourceList.Count == 0)
                return;

            var known = new HashSet<string>(
                session.BoardComponents.Select(component => component.Designator),
                StringComparer.OrdinalIgnoreCase);
            var selectedSources = new HashSet<string>(
                session.SourceComponents.Select(component => component.Designator),
                StringComparer.OrdinalIgnoreCase);

            int probes = 0;
            int found = 0;
            int matched = 0;
            foreach (IGrouping<string, LayoutComponentSnapshot> sourceGroup in sourceList.GroupBy(source => DesignatorPrefix(source.Designator), StringComparer.OrdinalIgnoreCase))
            {
                string prefix = sourceGroup.Key;
                if (string.IsNullOrWhiteSpace(prefix))
                    continue;

                int maxProbe = prefix.Length >= 2 ? 3000 : 1500;
                for (int number = 1; number <= maxProbe; number++)
                {
                    string designator = prefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (selectedSources.Contains(designator) || known.Contains(designator))
                        continue;

                    probes++;
                    object component = LayoutDuplicationPcbAccess.GetComponentByRefDes(session.Board, designator);
                    if (component == null)
                        continue;

                    found++;
                    LayoutComponentSnapshot snapshot = CaptureComponentSnapshot(session.Board, component, includePads: false);
                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Designator))
                        continue;

                    ApplySchematicHint(snapshot, FindSchematicHint(schematicContext, snapshot.Designator));
                    if (selectedSources.Contains(snapshot.Designator) || known.Contains(snapshot.Designator))
                        continue;

                    if (!sourceGroup.Any(source => IsEquivalentAnchor(source, snapshot)))
                        continue;

                    session.BoardComponents.Add(snapshot);
                    known.Add(snapshot.Designator);
                    matched++;
                }
            }

            EasyEDALoaderModule.Trace(
                "Duplicate layout direct refdes target discovery: sources="
                + string.Join(",", sourceList.Select(source => source.Designator))
                + " probes="
                + probes
                + " found="
                + found
                + " matched="
                + matched
                + " boardCandidates="
                + session.BoardComponents.Count);
        }

        private static bool IsEquivalentSchematicCandidate(
            LayoutComponentSnapshot source,
            LayoutSchematicComponentHint target,
            LayoutSchematicMatchContext schematicContext)
        {
            if (source == null || target == null)
                return false;

            LayoutSchematicComponentHint sourceHint = schematicContext.Hints
                .FirstOrDefault(hint => string.Equals(hint.Designator, source.Designator, StringComparison.OrdinalIgnoreCase));
            string sourceFootprint = FirstNonEmpty(source.Footprint, sourceHint?.Footprint);
            string sourceComment = FirstNonEmpty(source.Comment, sourceHint?.Comment);
            string sourceDescription = FirstNonEmpty(source.Description, sourceHint?.Description);

            bool footprintMatches = LooseSame(sourceFootprint, target.Footprint);
            bool hasMissingFootprint = string.IsNullOrWhiteSpace(sourceFootprint) || string.IsNullOrWhiteSpace(target.Footprint);

            if (footprintMatches && LooseSame(sourceComment, target.Comment))
                return true;

            if (footprintMatches && LooseSame(sourceDescription, target.Description))
                return true;

            if (hasMissingFootprint && LooseSame(sourceComment, target.Comment))
                return true;

            if (hasMissingFootprint && LooseSame(sourceDescription, target.Description))
                return true;

            return footprintMatches
                && SameDesignatorFamily(source.Designator, target.Designator)
                && !string.IsNullOrWhiteSpace(sourceFootprint)
                && !string.IsNullOrWhiteSpace(target.Footprint);
        }

        private static bool LooseSame(string left, string right)
        {
            string leftNorm = NormalizeComparable(left);
            string rightNorm = NormalizeComparable(right);
            if (string.IsNullOrWhiteSpace(leftNorm) || string.IsNullOrWhiteSpace(rightNorm))
                return false;

            return string.Equals(leftNorm, rightNorm, StringComparison.OrdinalIgnoreCase)
                || (leftNorm.Length >= 8 && rightNorm.Contains(leftNorm, StringComparison.OrdinalIgnoreCase))
                || (rightNorm.Length >= 8 && leftNorm.Contains(rightNorm, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeComparable(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        }

        private static bool SameDesignatorFamily(string left, string right)
        {
            string leftPrefix = DesignatorPrefix(left);
            string rightPrefix = DesignatorPrefix(right);
            return !string.IsNullOrWhiteSpace(leftPrefix)
                && string.Equals(leftPrefix, rightPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string DesignatorPrefix(string designator)
        {
            if (string.IsNullOrWhiteSpace(designator))
                return "";

            return new string(designator.TakeWhile(char.IsLetter).ToArray());
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static LayoutSchematicComponentHint FindSchematicHint(LayoutSchematicMatchContext context, string designator)
        {
            if (context == null || !context.HasHints || string.IsNullOrWhiteSpace(designator))
                return null;

            return context.Hints.FirstOrDefault(hint => string.Equals(hint.Designator, designator, StringComparison.OrdinalIgnoreCase));
        }

        private static void ApplySchematicHint(LayoutComponentSnapshot snapshot, LayoutSchematicComponentHint hint)
        {
            if (snapshot == null || hint == null)
                return;

            snapshot.PartNumber = FirstNonEmpty(snapshot.PartNumber, hint.PartNumber);
            snapshot.Comment = FirstNonEmpty(snapshot.Comment, hint.Comment);
            snapshot.Description = FirstNonEmpty(snapshot.Description, hint.Description);
            snapshot.Footprint = FirstNonEmpty(snapshot.Footprint, hint.Footprint);
        }

        private static List<LayoutComponentSnapshot> OrderBySchematicScore(
            IReadOnlyList<LayoutComponentSnapshot> candidates,
            LayoutSchematicMatchContext schematicContext,
            IEnumerable<LayoutComponentSnapshot> sourceComponents,
            IEnumerable<LayoutComponentSnapshot> targetAnchors)
        {
            if (schematicContext == null || !schematicContext.HasHints)
                return candidates.OrderBy(component => component.Designator, StringComparer.OrdinalIgnoreCase).ToList();

            return candidates
                .OrderByDescending(component => LayoutDuplicationSchematicMatcher.ScoreCandidate(
                    schematicContext,
                    sourceComponents,
                    targetAnchors,
                    component))
                .ThenBy(component => component.Designator, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<LayoutComponentSnapshot> CaptureBoardComponents(IPCB_Board board)
        {
            var result = new List<LayoutComponentSnapshot>();
            foreach (object primitive in LayoutDuplicationPcbAccess.EnumerateBoardObjects(board, (int)TObjectId.eComponentObject))
            {
                if (!LayoutDuplicationPcbAccess.IsComponentObject(primitive))
                    continue;

                LayoutComponentSnapshot snapshot = CaptureComponentSnapshot(board, primitive, includePads: false);
                if (!string.IsNullOrWhiteSpace(snapshot.Designator))
                    result.Add(snapshot);
            }

            return result;
        }

        private static LayoutComponentSnapshot CaptureComponentSnapshot(IPCB_Board board, object component, bool includePads)
        {
            if (board == null || !LayoutDuplicationPcbAccess.IsComponentObject(component))
                return null;

            int originX = LayoutDuplicationPcbAccess.GetInt(board, "GetState_XOrigin");
            int originY = LayoutDuplicationPcbAccess.GetInt(board, "GetState_YOrigin");

            var snapshot = new LayoutComponentSnapshot
            {
                Designator = ReadDesignator(component),
                PartNumber = ReadPartNumber(component),
                Comment = ReadComment(component),
                Description = ReadDescription(component),
                Footprint = ReadFootprint(component),
                Layer = ReadComponentLayerName(component),
                XMm = AltiumApi.CoordToMm(LayoutDuplicationPcbAccess.GetInt(component, "GetState_XLocation") - originX),
                YMm = AltiumApi.CoordToMm(LayoutDuplicationPcbAccess.GetInt(component, "GetState_YLocation") - originY),
                Rotation = LayoutDuplicationPcbAccess.GetDouble(component, "GetState_Rotation"),
                PcbObject = component
            };
            if (includePads)
                snapshot.Pads.AddRange(CapturePads(component));
            return snapshot;
        }

        private static LayoutComponentSnapshot CaptureKnownComponentSnapshot(
            IPCB_Board board,
            object component,
            LayoutSchematicComponentHint hint)
        {
            if (board == null || component == null || hint == null)
                return null;

            int originX = LayoutDuplicationPcbAccess.GetInt(board, "GetState_XOrigin");
            int originY = LayoutDuplicationPcbAccess.GetInt(board, "GetState_YOrigin");

            return new LayoutComponentSnapshot
            {
                Designator = hint.Designator,
                PartNumber = hint.PartNumber,
                Comment = hint.Comment,
                Description = hint.Description,
                Footprint = hint.Footprint,
                Layer = ReadComponentLayerName(component),
                XMm = AltiumApi.CoordToMm(LayoutDuplicationPcbAccess.GetInt(component, "GetState_XLocation") - originX),
                YMm = AltiumApi.CoordToMm(LayoutDuplicationPcbAccess.GetInt(component, "GetState_YLocation") - originY),
                Rotation = LayoutDuplicationPcbAccess.GetDouble(component, "GetState_Rotation"),
                PcbObject = component
            };
        }

        public static void EnsurePadsCaptured(LayoutComponentSnapshot component)
        {
            if (component == null || component.Pads.Count > 0 || component.PcbObject == null)
                return;

            component.Pads.AddRange(CapturePads(component.PcbObject));
        }

        private static List<LayoutPadSnapshot> CapturePads(object component)
        {
            var result = new List<LayoutPadSnapshot>();
            int padObjectId = (int)TObjectId.ePadObject;
            int emptyRun = 0;

            for (int index = 0; index < 10000 && emptyRun < 100; index++)
            {
                object primitive = LayoutDuplicationPcbAccess.Invoke(component, "Internal_GetPrimitiveAt", index, padObjectId);
                if (primitive == null && index == 0)
                    primitive = LayoutDuplicationPcbAccess.Invoke(component, "Internal_GetPrimitiveAt", 1, padObjectId);

                if (primitive == null)
                {
                    emptyRun++;
                    continue;
                }

                emptyRun = 0;
                if (!LayoutDuplicationPcbAccess.IsPadObject(primitive))
                    continue;

                result.Add(new LayoutPadSnapshot
                {
                    Name = LayoutDuplicationPcbAccess.ReadPadName(primitive),
                    Net = LayoutDuplicationPcbAccess.ReadNetName(primitive)
                });
            }

            return result;
        }

        private static bool IsEquivalentAnchor(LayoutComponentSnapshot source, LayoutComponentSnapshot target)
        {
            if (source == null || target == null)
                return false;

            if (!LooseSame(source.Footprint, target.Footprint))
                return false;

            if (LooseSame(source.Comment, target.Comment))
                return true;

            return LooseSame(source.Description, target.Description);
        }

        private static bool MatchesAnySourceComponent(IEnumerable<LayoutComponentSnapshot> sources, LayoutComponentSnapshot target)
        {
            return sources.Any(source => IsEquivalentAnchor(source, target));
        }

        private static bool IsSupportedRoutingPrimitive(object primitive)
        {
            return LayoutDuplicationPcbAccess.IsRoutingObject(primitive);
        }

        private static string ReadDesignator(object component)
        {
            object name = LayoutDuplicationPcbAccess.Invoke(component, "GetState_Name");
            if (name == null)
                name = LayoutDuplicationPcbAccess.Invoke(component, "Internal_GetState_Name");
            string text = Convert.ToString(LayoutDuplicationPcbAccess.Invoke(name, "GetState_Text"));
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            return Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_SourceDesignator"))
                ?? Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_Designator"))
                ?? "";
        }

        private static string ReadPartNumber(object component)
        {
            return FirstNonEmpty(
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_SourcePartNumber")),
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_SourceCompDesignItemID")),
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_SourceLibReference")),
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_LibReference")));
        }

        private static string ReadComment(object component)
        {
            return FirstNonEmpty(
                ReadPcbText(LayoutDuplicationPcbAccess.Invoke(component, "Internal_GetState_Comment")),
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_SourceLibReference")),
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_SourceCompDesignItemID")));
        }

        private static string ReadDescription(object component)
        {
            return FirstNonEmpty(
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_SourceDescription")),
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_FootprintDescription")));
        }

        private static string ReadFootprint(object component)
        {
            return Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_Pattern"))
                ?? Convert.ToString(LayoutDuplicationPcbAccess.Invoke(component, "GetState_Footprint"))
                ?? "";
        }

        private static string ReadComponentLayerName(object component)
        {
            object flipped = LayoutDuplicationPcbAccess.Invoke(component, "GetState_FlippedOnLayer");
            return flipped is bool isBottom && isBottom ? "Bottom" : "Top";
        }

        private static string ReadPcbText(object textObject)
        {
            return FirstNonEmpty(
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(textObject, "GetState_Text")),
                Convert.ToString(LayoutDuplicationPcbAccess.Invoke(textObject, "GetState_UnderlyingString")),
                textObject as string);
        }
    }
}
