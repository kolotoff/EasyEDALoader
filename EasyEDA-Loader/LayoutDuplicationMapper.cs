using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EasyEDA_Loader
{
    internal static class LayoutDuplicationMapper
    {
        public static string BuildMappingPrompt(LayoutMappingRequest request)
        {
            var sourceKeys = request.SourceComponents.Select(component => component.Designator).ToList();
            var prompt = new StringBuilder();
            prompt.AppendLine("Return only JSON.");
            prompt.AppendLine("The map object MUST have exactly these source designator keys:");
            prompt.AppendLine(string.Join(", ", sourceKeys) + ".");
            prompt.AppendLine("Each value MUST be one bare destination designator from the destination list.");
            prompt.AppendLine("Do not invent designators.");
            prompt.AppendLine("Do not return coordinates or edit commands.");
            prompt.AppendLine("If any required source cannot be mapped, put it in ambiguous and omit that key.");
            if (request.UseSchematicMatching)
            {
                prompt.AppendLine("Prefer candidates on matching schematic sheet/channel/net roles when available.");
                prompt.AppendLine("Schematic hints are advisory only; footprint and part compatibility are still mandatory.");
            }
            prompt.AppendLine();
            prompt.AppendLine("Source anchor:");
            AppendComponent(prompt, request.SourceAnchor);
            prompt.AppendLine("Source components:");
            foreach (LayoutComponentSnapshot component in request.SourceComponents)
                AppendComponent(prompt, component);
            prompt.AppendLine("Target anchors:");
            foreach (LayoutComponentSnapshot component in request.TargetAnchors)
                AppendComponent(prompt, component);
            prompt.AppendLine("Destination list:");
            foreach (LayoutComponentSnapshot component in request.DestinationCandidates)
                AppendComponent(prompt, component);
            if (request.UseSchematicMatching)
            {
                prompt.AppendLine("Schematic matching hints:");
                if (request.SchematicHints == null || request.SchematicHints.Count == 0)
                {
                    prompt.AppendLine("No schematic component hints were available; use PCB metadata only.");
                }
                else
                {
                    foreach (LayoutSchematicComponentHint hint in request.SchematicHints)
                        AppendSchematicHint(prompt, hint);
                }
            }
            return prompt.ToString();
        }

        public static LayoutMappingValidationResult ValidateMappingResponse(
            string json,
            LayoutMappingRequest request)
        {
            var result = new LayoutMappingValidationResult();
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Mapping JSON parse failed: " + ex.Message);
                return result;
            }

            if (!(root["groups"] is JArray groups))
            {
                result.Errors.Add("Mapping response does not contain groups.");
                return result;
            }

            var sourceByDesignator = request.SourceComponents.ToDictionary(component => component.Designator, StringComparer.OrdinalIgnoreCase);
            var destinationByDesignator = request.DestinationCandidates.ToDictionary(component => component.Designator, StringComparer.OrdinalIgnoreCase);
            var checkedTargets = new HashSet<string>(request.TargetAnchors.Select(component => component.Designator), StringComparer.OrdinalIgnoreCase);
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JToken groupToken in groups)
            {
                string targetAnchor = groupToken["target_anchor"]?.ToString() ?? groupToken["anchor"]?.ToString();
                if (string.IsNullOrWhiteSpace(targetAnchor) || !checkedTargets.Contains(targetAnchor))
                {
                    result.Errors.Add("Returned target anchor is not checked: " + (targetAnchor ?? ""));
                    continue;
                }

                if (!seenTargets.Add(targetAnchor))
                {
                    result.Errors.Add("Duplicate returned target anchor: " + targetAnchor);
                    continue;
                }

                if (groupToken["ambiguous"] is JArray ambiguous && ambiguous.Count > 0)
                {
                    result.Errors.Add("Target " + targetAnchor + " is ambiguous.");
                    continue;
                }

                if (!(groupToken["map"] is JObject map))
                {
                    result.Errors.Add("Target " + targetAnchor + " has no map.");
                    continue;
                }

                var destinationsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var validated = new LayoutValidatedGroup { TargetAnchorDesignator = targetAnchor };
                bool groupValid = true;
                foreach (LayoutComponentSnapshot source in request.SourceComponents)
                {
                    JToken valueToken = map[source.Designator];
                    string destinationDesignator = valueToken?.ToString();
                    if (string.IsNullOrWhiteSpace(destinationDesignator))
                    {
                        result.Errors.Add("Target " + targetAnchor + " is missing source key " + source.Designator + ".");
                        groupValid = false;
                        continue;
                    }

                    if (!destinationByDesignator.TryGetValue(destinationDesignator, out LayoutComponentSnapshot destination))
                    {
                        result.Errors.Add("Target " + targetAnchor + " invented destination " + destinationDesignator + ".");
                        groupValid = false;
                        continue;
                    }

                    if (!destinationsUsed.Add(destinationDesignator))
                    {
                        result.Errors.Add("Target " + targetAnchor + " maps duplicate destination " + destinationDesignator + ".");
                        groupValid = false;
                        continue;
                    }

                    if (!Compatible(source, destination))
                    {
                        result.Errors.Add("Target " + targetAnchor + " maps incompatible component " + source.Designator + " to " + destinationDesignator + ".");
                        groupValid = false;
                        continue;
                    }

                    validated.SourceToDestination[source.Designator] = destinationDesignator;
                }

                if (groupValid && validated.SourceToDestination.Count == sourceByDesignator.Count)
                {
                    if (!validated.SourceToDestination.TryGetValue(request.SourceAnchor.Designator, out string mappedAnchor) ||
                        !string.Equals(mappedAnchor, targetAnchor, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Errors.Add("Source anchor does not map to target anchor " + targetAnchor + ".");
                        continue;
                    }

                    result.ValidGroups.Add(validated);
                }
            }

            return result;
        }

        private static void AppendComponent(StringBuilder prompt, LayoutComponentSnapshot component)
        {
            if (component == null)
                return;

            prompt.Append(component.Designator)
                .Append(" | comment=")
                .Append(component.Comment)
                .Append(" | description=")
                .Append(component.Description)
                .Append(" | footprint=")
                .Append(component.Footprint)
                .Append(" | layer=")
                .Append(component.Layer)
                .Append(" | x=")
                .Append(component.XMm.ToString("0.###"))
                .Append(" | y=")
                .Append(component.YMm.ToString("0.###"))
                .AppendLine();
        }

        private static void AppendSchematicHint(StringBuilder prompt, LayoutSchematicComponentHint hint)
        {
            if (hint == null)
                return;

            prompt.Append(hint.Designator)
                .Append(" | sheet=")
                .Append(hint.SheetPath)
                .Append(" | comment=")
                .Append(hint.Comment)
                .Append(" | description=")
                .Append(hint.Description)
                .Append(" | footprint=")
                .Append(hint.Footprint)
                .Append(" | nets=")
                .Append(string.Join(",", hint.NetNames.Take(16)))
                .Append(" | pins=")
                .Append(string.Join(",", hint.PinNames.Take(16)))
                .AppendLine();
        }

        private static bool Compatible(LayoutComponentSnapshot source, LayoutComponentSnapshot destination)
        {
            if (!string.Equals(source.Footprint ?? "", destination.Footprint ?? "", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(source.Comment) || !string.IsNullOrWhiteSpace(destination.Comment))
                return string.Equals(source.Comment ?? "", destination.Comment ?? "", StringComparison.OrdinalIgnoreCase);

            return string.Equals(source.Description ?? "", destination.Description ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
