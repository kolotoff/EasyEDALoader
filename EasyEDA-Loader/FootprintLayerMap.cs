using System;
using System.Collections.Generic;

namespace EasyEDA_Loader
{
    public static class FootprintLayerMap
    {
        private static readonly Dictionary<string, string> OptionalLcscMechanicalLayerAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ComponentShapeLayer", "TopAssembly" },
            { "ComponentMarkingLayer", "TopAssembly" },
            { "ComponentPolarityLayer", "TopAssembly" },
            { "LeadShapeLayer", "Mechanical" },
            { "Document", "Mechanical" }
        };

        public static string NormalizeLayerName(string layer)
        {
            return NormalizeLayerName(layer, true);
        }

        public static string NormalizeLayerName(string layer, bool importLcscMechanicalLayers, bool importAdditionalLayers = false)
        {
            if (layer == null)
                return null;

            if (string.Equals(layer, "BoardOutLine", StringComparison.Ordinal))
                return importAdditionalLayers ? "BoardOutline" : null;
            if (string.Equals(layer, "BottomAssembly", StringComparison.OrdinalIgnoreCase))
                return importAdditionalLayers ? "TopAssembly" : null;

            if (!OptionalLcscMechanicalLayerAliases.TryGetValue(layer, out string normalizedLayer))
                return layer;

            return importLcscMechanicalLayers
                ? normalizedLayer
                : null;
        }
    }
}
