using System;
using System.Collections.Generic;
using System.Globalization;

namespace EasyEDA_Loader
{
    public static class SymbolImportRules
    {
        public const double MilPerMm = 1000.0 / 25.4;
        public const double GostGridMil = 2.5 * MilPerMm;
        public const double GostPinLengthMil = 5.0 * MilPerMm;
        public const double GostPinPitchMil = 5.0 * MilPerMm;

        public static string FormatPinName(string designator, string name, bool isConnector)
        {
            string cleanedName = Clean(name);
            string cleanedDesignator = Clean(designator);

            if (isConnector)
            {
                if (IsPositiveInteger(cleanedName))
                    return "Pin " + cleanedName;
                if (string.IsNullOrWhiteSpace(cleanedName) && IsPositiveInteger(cleanedDesignator))
                    return "Pin " + cleanedDesignator;
            }

            return cleanedName;
        }

        public static string SelectDesignator(string sourceDesignator, string partName, string description, string package)
        {
            sourceDesignator = Clean(sourceDesignator);
            string text = $"{partName} {description} {package}".ToLowerInvariant();

            if (ContainsAny(text, "op amp", "opamp", "operational amplifier", "rf amplifier", "low noise amplifier"))
                return "DA?";
            if (IsUsbConnector(text) || ContainsAny(text, "receptacle", "socket", "jack", "female"))
                return "XS?";
            if (ContainsAny(text, "header", "plug", "pin header", "male"))
                return "XP?";
            if (ContainsAny(text, "connector", "conn", "terminal", "ffc", "fpc"))
                return "X?";

            if (string.IsNullOrWhiteSpace(sourceDesignator))
                return "DD?";
            if (string.Equals(sourceDesignator, "U?", StringComparison.OrdinalIgnoreCase))
                return "DD?";
            if (string.Equals(sourceDesignator, "J?", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceDesignator, "P?", StringComparison.OrdinalIgnoreCase))
                return "X?";

            return sourceDesignator;
        }

        public static string SelectLibraryComment(string designItemId)
        {
            return Clean(designItemId);
        }

        public static string SelectVisibleDesignator(string designator)
        {
            string cleaned = Clean(designator);
            return string.IsNullOrWhiteSpace(cleaned) ? "DD?" : cleaned;
        }

        public static string SelectSymbolDescription(
            string productDescription,
            string componentDescription,
            string packageTitle,
            string packageName,
            string partNumber,
            string mounting,
            IReadOnlyDictionary<string, string> parameters,
            FootprintDescriptionGeometry geometry)
        {
            var identifiers = BuildIdentifiers(partNumber, packageName);
            foreach (string candidate in new[] { productDescription, componentDescription, packageTitle })
            {
                string cleaned = Clean(candidate);
                if (IsUsefulDescription(cleaned, identifiers))
                    return cleaned;
            }

            string synthesized = SynthesizeSymbolDescription(packageName, mounting, parameters, geometry);
            if (!string.IsNullOrWhiteSpace(synthesized))
                return synthesized;

            string packagePrefix = GetPackagePrefix(packageName);
            if (!string.IsNullOrWhiteSpace(packagePrefix) && !string.IsNullOrWhiteSpace(Clean(mounting)))
                return packagePrefix + " package, " + Clean(mounting);
            if (!string.IsNullOrWhiteSpace(packagePrefix))
                return packagePrefix + " package";

            return "Component";
        }

        public static string SelectValueType(string designator, string partName, string description, string package)
        {
            string text = $"{partName} {description} {package}".ToLowerInvariant();

            if (IsUsbConnector(text) || (designator != null && designator.StartsWith("XS", StringComparison.OrdinalIgnoreCase) && text.Contains("usb")))
                return "Разъём USB";

            if ((designator != null && designator.StartsWith("X", StringComparison.OrdinalIgnoreCase))
                || ContainsAny(text, "connector", "conn", "terminal", "header", "plug", "receptacle", "socket", "jack", "female", "ffc", "fpc", "jst"))
                return "Разъём";

            return "Микросхема";
        }

        public static bool IsCustomParameter(string name)
        {
            return !IsModelOrBuiltInParameter(name);
        }

        public static string SelectDesignItemId(
            string manufacturerPart,
            string symbolName,
            string componentTitle,
            string searchResultName,
            string searchPart,
            string lcscNumber,
            string szlcscNumber)
        {
            string manufacturerStyle = FirstNonCatalog(
                manufacturerPart,
                symbolName,
                searchPart,
                searchResultName,
                componentTitle);
            if (!string.IsNullOrWhiteSpace(manufacturerStyle))
                return manufacturerStyle;

            return FirstNonEmpty(manufacturerPart, symbolName, searchPart, searchResultName, componentTitle, lcscNumber, szlcscNumber);
        }

        public static bool IsModelOrBuiltInParameter(string name)
        {
            return string.Equals(name, "Comment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Description", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Designator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Footprint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "footrpint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "FootprintLibrary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Package", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Mounting", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsConnectorDesignator(string designator)
        {
            return !string.IsNullOrWhiteSpace(designator)
                && designator.StartsWith("X", StringComparison.OrdinalIgnoreCase);
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string cleaned = value.Trim();
            if (cleaned == "-" || cleaned == "*")
                return "";

            return cleaned;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                string cleaned = Clean(value);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    return cleaned;
            }

            return "";
        }

        private static string FirstNonCatalog(params string[] values)
        {
            foreach (string value in values)
            {
                string cleaned = Clean(value);
                if (!string.IsNullOrWhiteSpace(cleaned) && !IsCatalogIdentifier(cleaned))
                    return cleaned;
            }

            return "";
        }

        private static bool IsCatalogIdentifier(string value)
        {
            string cleaned = Clean(value);
            if (cleaned.Length < 2)
                return false;
            if (cleaned[0] != 'C' && cleaned[0] != 'c')
                return false;

            for (int i = 1; i < cleaned.Length; i++)
            {
                if (!char.IsDigit(cleaned[i]))
                    return false;
            }

            return true;
        }

        private static HashSet<string> BuildIdentifiers(string partNumber, string packageName)
        {
            var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddIdentifier(identifiers, partNumber);
            AddIdentifier(identifiers, packageName);
            AddIdentifier(identifiers, GetPackageSuffix(packageName));
            return identifiers;
        }

        private static void AddIdentifier(HashSet<string> identifiers, string value)
        {
            string cleaned = Clean(value);
            if (!string.IsNullOrWhiteSpace(cleaned))
                identifiers.Add(cleaned);
        }

        private static bool IsUsefulDescription(string value, HashSet<string> identifiers)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            if (identifiers.Contains(value))
                return false;

            string lower = value.ToLowerInvariant();
            return !lower.Contains("generated from")
                && !lower.Contains("copied from")
                && !lower.Contains("copy from")
                && !lower.Contains("reference symbol")
                && !lower.Contains("reference footprint");
        }

        private static string SynthesizeSymbolDescription(
            string packageName,
            string mounting,
            IReadOnlyDictionary<string, string> parameters,
            FootprintDescriptionGeometry geometry)
        {
            string cleanedMounting = Clean(mounting);
            if (geometry == null || geometry.PositionCount <= 0 || string.IsNullOrWhiteSpace(cleanedMounting))
                return "";

            string lcscPartName = GetParameter(parameters, "LCSC Part Name");
            var clauses = new List<string>
            {
                geometry.PositionCount.ToString(CultureInfo.InvariantCulture) + "-position " +
                    SelectOrientation(lcscPartName) + " " +
                    cleanedMounting + " " +
                    SelectGender(lcscPartName) + " " +
                    SelectConnectorRole(lcscPartName)
            };

            if (geometry.PitchMm > 0)
                clauses.Add(FormatMm(geometry.PitchMm) + " mm pitch");

            return string.Join(", ", clauses);
        }

        private static string SelectOrientation(string lcscPartName)
        {
            if (ContainsAny(lcscPartName, "vertical", "立式"))
                return "vertical";
            if (ContainsAny(lcscPartName, "right angle", "horizontal", "卧式"))
                return "right-angle";

            return "vertical";
        }

        private static string SelectGender(string lcscPartName)
        {
            if (ContainsAny(lcscPartName, "female", "socket", "母头"))
                return "female";
            if (ContainsAny(lcscPartName, "male", "plug", "公头", "插头"))
                return "male";

            return "connector";
        }

        private static string SelectConnectorRole(string lcscPartName)
        {
            var words = new List<string>();
            if (ContainsAny(lcscPartName, "pcb", "PCB"))
                words.Add("PCB");
            if (ContainsAny(lcscPartName, "power", "动力", "电源"))
                words.Add("power");
            if (ContainsAny(lcscPartName, "plug", "插头"))
                words.Add("plug");
            words.Add("connector");

            return string.Join(" ", words);
        }

        private static string GetParameter(IReadOnlyDictionary<string, string> parameters, string name)
        {
            if (parameters == null)
                return "";

            foreach (var parameter in parameters)
            {
                if (string.Equals(parameter.Key, name, StringComparison.OrdinalIgnoreCase))
                    return Clean(parameter.Value);
            }

            return "";
        }

        private static string FormatMm(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string GetPackagePrefix(string packageName)
        {
            string cleaned = Clean(packageName);
            if (string.IsNullOrWhiteSpace(cleaned))
                return "";

            int separator = cleaned.IndexOf('_');
            if (separator > 0)
                return cleaned.Substring(0, separator);

            return cleaned;
        }

        private static string GetPackageSuffix(string packageName)
        {
            string cleaned = Clean(packageName);
            if (string.IsNullOrWhiteSpace(cleaned))
                return "";

            int separator = cleaned.IndexOf('_');
            if (separator >= 0 && separator + 1 < cleaned.Length)
                return cleaned.Substring(separator + 1);

            return "";
        }

        private static bool IsPositiveInteger(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (char ch in value)
            {
                if (!char.IsDigit(ch))
                    return false;
            }

            return true;
        }

        private static bool IsUsbConnector(string text)
        {
            return text.Contains("usb")
                && ContainsAny(text, "type-c", "type c", "receptacle", "socket", "jack", "connector", "conn", "plug");
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (string needle in needles)
            {
                if (text.Contains(needle))
                    return true;
            }

            return false;
        }
    }
}
