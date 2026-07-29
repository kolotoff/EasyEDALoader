using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    public static class TronstolE1PartNumber
    {
        private static readonly Regex MultipleSpaces = new Regex(" {2,}", RegexOptions.CultureInvariant);

        public static string FromParameters(IEnumerable<KeyValuePair<string, string>> parameters)
        {
            if (parameters == null)
                return string.Empty;

            foreach (KeyValuePair<string, string> parameter in parameters)
            {
                if (string.Equals(parameter.Key, "PartNumber", StringComparison.OrdinalIgnoreCase))
                    return parameter.Value ?? string.Empty;
            }

            return string.Empty;
        }

        public static string Normalize(
            string partNumber,
            string footprint,
            bool removeTrailingFootprint,
            bool collapseSpaces)
        {
            string value = partNumber ?? string.Empty;
            string footprintValue = footprint ?? string.Empty;

            if (removeTrailingFootprint
                && !string.IsNullOrWhiteSpace(footprintValue)
                && !string.Equals(value.Trim(), footprintValue.Trim(), StringComparison.OrdinalIgnoreCase)
                && value.EndsWith(footprintValue, StringComparison.OrdinalIgnoreCase))
            {
                int suffixStart = value.Length - footprintValue.Length;
                if (suffixStart > 0 && char.IsWhiteSpace(value[suffixStart - 1]))
                    value = value.Substring(0, suffixStart).TrimEnd();
            }

            if (collapseSpaces)
                value = MultipleSpaces.Replace(value, " ");

            return value;
        }
    }
}
