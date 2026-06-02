using System;

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
            return FirstNonEmpty(manufacturerPart, symbolName, componentTitle, searchResultName, searchPart, lcscNumber, szlcscNumber);
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
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
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
