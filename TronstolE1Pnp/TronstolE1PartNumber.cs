using System;
using System.Collections.Generic;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    public static class TronstolE1PartNumber
    {
        public static string FromParameters(IEnumerable<KeyValuePair<string, string>> parameters)
        {
            if (parameters == null)
                return string.Empty;

            foreach (KeyValuePair<string, string> parameter in parameters)
            {
                if (string.Equals(parameter.Key, "PartNumber", StringComparison.OrdinalIgnoreCase))
                    return TronstolE1Text.Normalize(parameter.Value);
            }

            return string.Empty;
        }

        public static string Normalize(
            string partNumber,
            string footprint,
            bool removeTrailingFootprint,
            bool collapseSpaces)
        {
            string value = TronstolE1Text.Normalize(partNumber);
            string footprintValue = TronstolE1Text.Normalize(footprint);

            if (removeTrailingFootprint
                && !string.IsNullOrWhiteSpace(footprintValue)
                && !string.Equals(value.Trim(), footprintValue.Trim(), StringComparison.OrdinalIgnoreCase)
                && value.EndsWith(footprintValue, StringComparison.OrdinalIgnoreCase))
            {
                int suffixStart = value.Length - footprintValue.Length;
                if (suffixStart > 0 && char.IsWhiteSpace(value[suffixStart - 1]))
                    value = value.Substring(0, suffixStart).TrimEnd();
            }

            return value;
        }
    }
}
