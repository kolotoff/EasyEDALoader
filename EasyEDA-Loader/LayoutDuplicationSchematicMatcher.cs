using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EasyEDA_Loader
{
    internal static class LayoutDuplicationSchematicMatcher
    {
        public static bool TryBuildSchematicMatchContext(
            LayoutDuplicationSession session,
            out LayoutSchematicMatchContext context)
        {
            context = new LayoutSchematicMatchContext();
            if (session == null)
                return false;

            try
            {
                object schServer = AltiumApi.GlobalVars.SCHServer;
                CollectCurrentSchematic(context, schServer);
                CollectWorkspaceSchematics(context, schServer);
                AddPcbNetFallbackHints(context, session);
                DeduplicateHints(context);
                TraceContext("Schematic matching context", context);
                return context.HasHints;
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("Schematic matching context unavailable: " + ex.Message);
                AddPcbNetFallbackHints(context, session);
                DeduplicateHints(context);
                TraceContext("Schematic matching fallback context", context);
                return context.HasHints;
            }
        }

        public static int ScoreCandidate(
            LayoutSchematicMatchContext context,
            IEnumerable<LayoutComponentSnapshot> sourceComponents,
            IEnumerable<LayoutComponentSnapshot> targetAnchors,
            LayoutComponentSnapshot candidate)
        {
            if (context == null || candidate == null || !context.HasHints)
                return 0;

            LayoutSchematicComponentHint candidateHint = FindHint(context, candidate.Designator);
            if (candidateHint == null)
                return 0;

            int score = 0;
            var targetHints = (targetAnchors ?? Enumerable.Empty<LayoutComponentSnapshot>())
                .Select(component => FindHint(context, component.Designator))
                .Where(hint => hint != null)
                .ToList();
            var sourceHints = (sourceComponents ?? Enumerable.Empty<LayoutComponentSnapshot>())
                .Select(component => FindHint(context, component.Designator))
                .Where(hint => hint != null)
                .ToList();

            if (targetHints.Any(hint => SameText(hint.SheetPath, candidateHint.SheetPath)))
                score += 100;
            else if (targetHints.Any(hint => SameSheetFamily(hint.SheetPath, candidateHint.SheetPath)))
                score += 50;

            score += Math.Min(50, targetHints.Sum(hint => CountOverlap(hint.NetNames, candidateHint.NetNames)) * 10);

            if (sourceHints.Any(hint => SameText(hint.Footprint, candidateHint.Footprint)))
                score += 10;
            if (sourceHints.Any(hint => SameText(hint.Comment, candidateHint.Comment)))
                score += 10;
            if (sourceHints.Any(hint => SameText(hint.Description, candidateHint.Description)))
                score += 5;

            return score;
        }

        public static IReadOnlyList<LayoutSchematicComponentHint> GetHintsForComponents(
            LayoutSchematicMatchContext context,
            IEnumerable<LayoutComponentSnapshot> components)
        {
            if (context == null || components == null || !context.HasHints)
                return Array.Empty<LayoutSchematicComponentHint>();

            var result = new List<LayoutSchematicComponentHint>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LayoutComponentSnapshot component in components)
            {
                LayoutSchematicComponentHint hint = FindHint(context, component.Designator);
                if (hint != null && seen.Add(hint.Designator))
                    result.Add(hint);
            }

            return result;
        }

        private static void CollectCurrentSchematic(LayoutSchematicMatchContext context, object schServer)
        {
            object current = Invoke(schServer, "GetCurrentSchDocument")
                ?? Invoke(schServer, "GetCurrentSCHDocument");
            CollectSchematicDocument(context, current, ReadDocumentPath(current));
        }

        private static void CollectWorkspaceSchematics(LayoutSchematicMatchContext context, object schServer)
        {
            object workspace = AltiumApi.GlobalVars.Workspace;
            foreach (object project in EnumerateProjectCandidates(workspace))
            {
                foreach (object document in EnumerateDocumentCandidates(project))
                {
                    if (!LooksLikeSchematicDocument(document))
                        continue;

                    object schDocument = ResolveSchematicDocument(schServer, document);
                    CollectSchematicDocument(context, schDocument ?? document, ReadDocumentPath(document));
                }
            }
        }

        private static void CollectSchematicDocument(
            LayoutSchematicMatchContext context,
            object schDocument,
            string sheetPath)
        {
            if (schDocument == null)
                return;

            foreach (object component in EnumerateSchematicComponents(schDocument))
            {
                LayoutSchematicComponentHint hint = ReadComponentHint(component, sheetPath);
                if (!string.IsNullOrWhiteSpace(hint.Designator))
                    context.Hints.Add(hint);
            }
        }

        private static IEnumerable<object> EnumerateSchematicComponents(object schDocument)
        {
            object iterator = Invoke(schDocument, "SchIterator_Create")
                ?? Invoke(schDocument, "Iterator_Create");
            if (iterator == null)
                yield break;

            Invoke(iterator, "SetState_FilterAll");
            Invoke(iterator, "AddFilter_ObjectSet", TryCreateObjectSet());

            object current = Invoke(iterator, "FirstSchObject")
                ?? Invoke(iterator, "FirstObject");
            while (current != null)
            {
                if (LooksLikeSchematicComponent(current))
                    yield return current;

                current = Invoke(iterator, "NextSchObject")
                    ?? Invoke(iterator, "NextObject");
            }

            Invoke(schDocument, "SchIterator_Destroy", iterator);
            Invoke(schDocument, "Iterator_Destroy", iterator);
        }

        private static LayoutSchematicComponentHint ReadComponentHint(object component, string sheetPath)
        {
            var hint = new LayoutSchematicComponentHint
            {
                Designator = ReadText(component, "GetState_Designator", "GetState_SourceDesignator", "GetState_UniqueId"),
                SheetPath = sheetPath ?? "",
                PartNumber = ReadParameter(component, "PartNumber", "Manufacturer Part Number", "MPN"),
                Comment = ReadText(component, "GetState_Comment", "GetState_LibReference"),
                Description = ReadText(component, "GetState_ComponentDescription", "GetState_Description"),
                Footprint = ReadParameter(component, "Footprint", "Package", "Pattern"),
                ComponentKind = Convert.ToString(component.GetType().Name)
            };

            foreach (object child in EnumerateChildObjects(component))
            {
                string pinName = ReadText(child, "GetState_Name", "GetState_Designator", "GetState_PinName");
                if (!string.IsNullOrWhiteSpace(pinName))
                    AddDistinct(hint.PinNames, pinName);

                string netName = ReadText(child, "GetState_NetName", "GetState_ElectricalNet", "GetState_Net");
                if (!string.IsNullOrWhiteSpace(netName))
                    AddDistinct(hint.NetNames, netName);
            }

            return hint;
        }

        private static void AddPcbNetFallbackHints(LayoutSchematicMatchContext context, LayoutDuplicationSession session)
        {
            var known = new HashSet<string>(context.Hints.Select(hint => hint.Designator), StringComparer.OrdinalIgnoreCase);
            foreach (LayoutComponentSnapshot component in session.BoardComponents)
            {
                if (string.IsNullOrWhiteSpace(component.Designator) || known.Contains(component.Designator))
                    continue;

                var hint = new LayoutSchematicComponentHint
                {
                    Designator = component.Designator,
                    SheetPath = "",
                    PartNumber = component.PartNumber,
                    Comment = component.Comment,
                    Description = component.Description,
                    Footprint = component.Footprint,
                    ComponentKind = "PCB fallback"
                };
                foreach (LayoutPadSnapshot pad in component.Pads)
                {
                    AddDistinct(hint.PinNames, pad.Name);
                    AddDistinct(hint.NetNames, pad.Net);
                }

                context.Hints.Add(hint);
            }
        }

        private static void DeduplicateHints(LayoutSchematicMatchContext context)
        {
            var merged = new Dictionary<string, LayoutSchematicComponentHint>(StringComparer.OrdinalIgnoreCase);
            foreach (LayoutSchematicComponentHint hint in context.Hints)
            {
                if (string.IsNullOrWhiteSpace(hint.Designator))
                    continue;

                if (!merged.TryGetValue(hint.Designator, out LayoutSchematicComponentHint existing))
                {
                    merged[hint.Designator] = hint;
                    continue;
                }

                existing.SheetPath = FirstNonEmpty(existing.SheetPath, hint.SheetPath);
                existing.PartNumber = FirstNonEmpty(existing.PartNumber, hint.PartNumber);
                existing.Comment = FirstNonEmpty(existing.Comment, hint.Comment);
                existing.Description = FirstNonEmpty(existing.Description, hint.Description);
                existing.Footprint = FirstNonEmpty(existing.Footprint, hint.Footprint);
                foreach (string netName in hint.NetNames)
                    AddDistinct(existing.NetNames, netName);
                foreach (string pinName in hint.PinNames)
                    AddDistinct(existing.PinNames, pinName);
            }

            context.Hints.Clear();
            context.Hints.AddRange(merged.Values.OrderBy(hint => hint.Designator, StringComparer.OrdinalIgnoreCase));
        }

        private static void TraceContext(string label, LayoutSchematicMatchContext context)
        {
            if (context == null)
            {
                EasyEDALoaderModule.Trace(label + ": unavailable");
                return;
            }

            EasyEDALoaderModule.Trace(
                label
                + ": hints="
                + context.Hints.Count
                + " sample="
                + string.Join(",", context.Hints.Take(8).Select(hint => hint.Designator)));
        }

        private static LayoutSchematicComponentHint FindHint(LayoutSchematicMatchContext context, string designator)
        {
            if (string.IsNullOrWhiteSpace(designator))
                return null;

            return context.Hints.FirstOrDefault(hint => SameText(hint.Designator, designator));
        }

        private static IEnumerable<object> EnumerateProjectCandidates(object workspace)
        {
            foreach (object item in EnumerateIndexed(workspace, "DM_ProjectCount", "DM_Projects"))
                yield return item;
            foreach (object item in EnumerateIndexed(workspace, "GetState_ProjectCount", "GetState_Project"))
                yield return item;

            object focused = Invoke(workspace, "DM_FocusedProject")
                ?? Invoke(workspace, "GetState_FocusedProject");
            if (focused != null)
                yield return focused;
        }

        private static IEnumerable<object> EnumerateDocumentCandidates(object project)
        {
            foreach (object item in EnumerateIndexed(project, "DM_LogicalDocumentCount", "DM_LogicalDocuments"))
                yield return item;
            foreach (object item in EnumerateIndexed(project, "DM_DocumentCount", "DM_Documents"))
                yield return item;
            foreach (object item in EnumerateIndexed(project, "GetState_DocumentCount", "GetState_Document"))
                yield return item;
        }

        private static IEnumerable<object> EnumerateIndexed(object owner, string countMethod, string itemMethod)
        {
            int count = ToInt(Invoke(owner, countMethod));
            for (int index = 0; index < count; index++)
            {
                object item = Invoke(owner, itemMethod, index);
                if (item != null)
                    yield return item;
            }
        }

        private static IEnumerable<object> EnumerateChildObjects(object owner)
        {
            object iterator = Invoke(owner, "SchIterator_Create")
                ?? Invoke(owner, "Iterator_Create");
            if (iterator == null)
                yield break;

            Invoke(iterator, "SetState_FilterAll");
            object current = Invoke(iterator, "FirstSchObject") ?? Invoke(iterator, "FirstObject");
            while (current != null)
            {
                yield return current;
                current = Invoke(iterator, "NextSchObject") ?? Invoke(iterator, "NextObject");
            }

            Invoke(owner, "SchIterator_Destroy", iterator);
            Invoke(owner, "Iterator_Destroy", iterator);
        }

        private static object ResolveSchematicDocument(object schServer, object document)
        {
            return Invoke(document, "GetState_SchDocument")
                ?? Invoke(document, "GetState_Document")
                ?? Invoke(schServer, "GetSchDocumentByPath", ReadDocumentPath(document))
                ?? Invoke(schServer, "OpenSchDocument", ReadDocumentPath(document));
        }

        private static bool LooksLikeSchematicDocument(object document)
        {
            string path = ReadDocumentPath(document);
            string kind = ReadText(document, "DM_DocumentKind", "GetState_DocumentKind", "GetState_Kind");
            return path.EndsWith(".SchDoc", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".SchLib", StringComparison.OrdinalIgnoreCase)
                || kind.IndexOf("Sch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeSchematicComponent(object value)
        {
            if (value == null)
                return false;

            string typeName = value.GetType().Name;
            if (typeName.IndexOf("Component", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return !string.IsNullOrWhiteSpace(ReadText(value, "GetState_Designator", "GetState_SourceDesignator"))
                && (!string.IsNullOrWhiteSpace(ReadText(value, "GetState_LibReference", "GetState_ComponentDescription"))
                    || !string.IsNullOrWhiteSpace(ReadParameter(value, "PartNumber", "Footprint")));
        }

        private static string ReadDocumentPath(object document)
        {
            return ReadText(document, "DM_FullPath", "GetState_FullPath", "GetState_FileName", "GetFileName");
        }

        private static string ReadParameter(object owner, params string[] names)
        {
            foreach (string name in names)
            {
                object parameter = Invoke(owner, "GetState_ParameterByName", name)
                    ?? Invoke(owner, "GetParameterByName", name)
                    ?? Invoke(owner, "ParameterByName", name);
                string text = ReadText(parameter, "GetState_Text", "GetState_Value", "GetState_ParameterValue");
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            foreach (object child in EnumerateChildObjects(owner))
            {
                string parameterName = ReadText(child, "GetState_Name", "GetState_ParameterName");
                if (!names.Any(name => SameText(name, parameterName)))
                    continue;

                string text = ReadText(child, "GetState_Text", "GetState_Value", "GetState_ParameterValue");
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return "";
        }

        private static string ReadText(object owner, params string[] methodNames)
        {
            foreach (string methodName in methodNames)
            {
                object value = Invoke(owner, methodName);
                string text = ConvertToText(value);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return "";
        }

        private static string ConvertToText(object value)
        {
            if (value == null)
                return "";

            if (value is string text)
                return text;

            object nestedText = Invoke(value, "GetState_Text");
            if (nestedText != null && !ReferenceEquals(nestedText, value))
                return Convert.ToString(nestedText) ?? "";

            return Convert.ToString(value) ?? "";
        }

        private static object Invoke(object target, string methodName, params object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
                return null;

            MethodInfo method = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                    && candidate.GetParameters().Length == args.Length);
            if (method == null)
                return null;

            try
            {
                return method.Invoke(target, args);
            }
            catch
            {
                return null;
            }
        }

        private static object TryCreateObjectSet()
        {
            return null;
        }

        private static int ToInt(object value)
        {
            if (value == null)
                return 0;
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static int CountOverlap(IEnumerable<string> left, IEnumerable<string> right)
        {
            var rightSet = new HashSet<string>(right ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return (left ?? Enumerable.Empty<string>()).Count(item => !string.IsNullOrWhiteSpace(item) && rightSet.Contains(item));
        }

        private static bool SameText(string left, string right)
        {
            return string.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameSheetFamily(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            string leftStem = TrimTrailingDigits(System.IO.Path.GetFileNameWithoutExtension(left));
            string rightStem = TrimTrailingDigits(System.IO.Path.GetFileNameWithoutExtension(right));
            return string.Equals(leftStem, rightStem, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimTrailingDigits(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            int end = value.Length;
            while (end > 0 && char.IsDigit(value[end - 1]))
                end--;
            return value.Substring(0, end);
        }

        private static string FirstNonEmpty(string preferred, string fallback)
        {
            return string.IsNullOrWhiteSpace(preferred) ? fallback ?? "" : preferred;
        }

        private static void AddDistinct(List<string> values, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !values.Any(item => SameText(item, value)))
                values.Add(value);
        }
    }
}
