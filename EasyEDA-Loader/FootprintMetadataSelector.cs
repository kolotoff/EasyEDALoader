using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EasyEDA_Loader
{
    public class FootprintDescriptionGeometry
    {
        public int PositionCount { get; set; }
        public double PitchMm { get; set; }
        public double BodyWidthMm { get; set; }
        public double BodyHeightMm { get; set; }
    }

    public static class FootprintMetadataSelector
    {
        public static string SelectName(string packageName, string partNumber)
        {
            string cleanedPackageName = Clean(packageName);
            string cleanedPartNumber = Clean(partNumber);
            string inferredPartName = InferPartNameFromPackage(cleanedPackageName);

            if (IsPackageSpecificToPart(cleanedPackageName, cleanedPartNumber))
            {
                if (string.Equals(cleanedPartNumber, cleanedPackageName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(inferredPartName))
                    return inferredPartName;

                return cleanedPartNumber;
            }

            if (!string.IsNullOrWhiteSpace(inferredPartName) &&
                (string.IsNullOrWhiteSpace(cleanedPartNumber) || IsCatalogIdentifier(cleanedPartNumber)))
                return inferredPartName;

            if (!string.IsNullOrWhiteSpace(inferredPartName) &&
                !string.IsNullOrWhiteSpace(cleanedPartNumber) &&
                IsConnectorFootprint(cleanedPackageName, cleanedPartNumber, "") &&
                AreCompatibleConnectorPartNames(inferredPartName, cleanedPartNumber))
                return cleanedPartNumber;

            return FirstNonEmpty(cleanedPackageName, cleanedPartNumber);
        }

        public static string SelectDescription(
            string productDescription,
            string componentDescription,
            string packageTitle,
            string packageName,
            string partNumber,
            string mounting)
        {
            return SelectDescription(
                productDescription,
                componentDescription,
                packageTitle,
                packageName,
                partNumber,
                mounting,
                null,
                null);
        }

        public static string SelectDescription(
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

            string detailedDescription = SynthesizeDetailedDescription(packageName, partNumber, mounting, parameters, geometry);
            if (!string.IsNullOrWhiteSpace(detailedDescription))
                return detailedDescription;

            return SynthesizeDescription(packageName, mounting);
        }

        private static HashSet<string> BuildIdentifiers(string partNumber, string packageName)
        {
            var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddIdentifier(identifiers, partNumber);
            AddIdentifier(identifiers, packageName);

            string packageSuffix = GetPackageSuffix(packageName);
            AddIdentifier(identifiers, packageSuffix);
            AddIdentifier(identifiers, InferPartNameFromPackage(packageName));

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
            if (lower.Contains("generated from") ||
                lower.Contains("copied from") ||
                lower.Contains("copy from") ||
                lower.Contains("reference footprint") ||
                LooksLikeParameterBlob(lower))
                return false;

            return true;
        }

        private static bool LooksLikeParameterBlob(string lowerValue)
        {
            int labelCount = 0;
            foreach (string label in new[]
            {
                "number of pins:",
                "pitch:",
                "mounting type:",
                "number of rows:",
                "connection type:",
                "contact material:",
                "contact plating:"
            })
            {
                if (lowerValue.Contains(label))
                    labelCount++;
            }

            return labelCount >= 2;
        }

        private static string SynthesizeDescription(string packageName, string mounting)
        {
            string packagePrefix = GetPackagePrefix(packageName);
            string cleanedMounting = Clean(mounting);

            if (!string.IsNullOrWhiteSpace(packagePrefix) && !string.IsNullOrWhiteSpace(cleanedMounting))
                return packagePrefix + " package, " + cleanedMounting;

            if (!string.IsNullOrWhiteSpace(packagePrefix))
                return packagePrefix + " package";

            if (!string.IsNullOrWhiteSpace(cleanedMounting))
                return "Component footprint, " + cleanedMounting;

            return "Component footprint";
        }

        private static string SynthesizeDetailedDescription(
            string packageName,
            string partNumber,
            string mounting,
            IReadOnlyDictionary<string, string> parameters,
            FootprintDescriptionGeometry geometry)
        {
            string inferredPartName = InferPartNameFromPackage(packageName);
            string manufacturerPart = FirstNonEmpty(
                GetParameter(parameters, "Manufacturer Part"),
                SelectDescriptionPartNumber(packageName, partNumber),
                inferredPartName,
                GetPackageSuffix(packageName));
            string manufacturer = SelectManufacturer(parameters, manufacturerPart);
            string family = SelectFamily(manufacturerPart);
            string lcscPartName = GetParameter(parameters, "LCSC Part Name");
            string cleanedMounting = Clean(mounting);

            if (string.IsNullOrWhiteSpace(cleanedMounting) ||
                geometry == null ||
                geometry.PositionCount <= 0)
                return "";

            if (!IsConnectorFootprint(packageName, manufacturerPart, lcscPartName))
                return SynthesizeGenericFootprintDescription(packageName, cleanedMounting, geometry);

            if (string.IsNullOrWhiteSpace(family))
                return "";

            string subject = FirstNonEmpty(
                JoinWords(manufacturer, family),
                family);

            var clauses = new List<string>
            {
                subject,
                geometry.PositionCount.ToString(CultureInfo.InvariantCulture) + "-position " +
                    SelectOrientation(lcscPartName, manufacturerPart) + " " +
                    cleanedMounting + " " +
                    SelectGender(lcscPartName, manufacturerPart) + " " +
                    SelectConnectorRole(lcscPartName)
            };

            if (geometry.PitchMm > 0)
                clauses.Add(FormatMm(geometry.PitchMm) + " mm pitch");

            if (geometry.BodyWidthMm > 0 && geometry.BodyHeightMm > 0)
                clauses.Add(FormatMm(geometry.BodyWidthMm) + " x " + FormatMm(geometry.BodyHeightMm) + " mm body");

            return string.Join(", ", clauses);
        }

        private static string SelectDescriptionPartNumber(string packageName, string partNumber)
        {
            string cleanedPackageName = Clean(packageName);
            string cleanedPartNumber = Clean(partNumber);
            if (string.Equals(cleanedPackageName, cleanedPartNumber, StringComparison.OrdinalIgnoreCase) ||
                IsCatalogIdentifier(cleanedPartNumber))
                return "";

            return cleanedPartNumber;
        }

        private static string SynthesizeGenericFootprintDescription(
            string packageName,
            string mounting,
            FootprintDescriptionGeometry geometry)
        {
            string packagePrefix = GetPackagePrefix(packageName);
            if (string.IsNullOrWhiteSpace(packagePrefix))
                return "";

            var clauses = new List<string>
            {
                packagePrefix + " package",
                geometry.PositionCount.ToString(CultureInfo.InvariantCulture) + "-pad " +
                    mounting + " footprint"
            };

            if (geometry.PitchMm > 0)
                clauses.Add(FormatMm(geometry.PitchMm) + " mm pitch");

            if (geometry.BodyWidthMm > 0 && geometry.BodyHeightMm > 0)
                clauses.Add(FormatMm(geometry.BodyWidthMm) + " x " + FormatMm(geometry.BodyHeightMm) + " mm body");

            return string.Join(", ", clauses);
        }

        private static string SelectManufacturer(IReadOnlyDictionary<string, string> parameters, string manufacturerPart)
        {
            string manufacturer = FirstNonEmpty(
                GetParameter(parameters, "Manufacturer"),
                GetParameter(parameters, "MFR"),
                GetParameter(parameters, "Mfr."));

            if (manufacturer.IndexOf("amass", StringComparison.OrdinalIgnoreCase) >= 0)
                return "AMASS";

            int parenthesis = manufacturer.IndexOf('(');
            if (parenthesis > 0)
                manufacturer = manufacturer.Substring(0, parenthesis);

            manufacturer = Clean(manufacturer);
            if (!string.IsNullOrWhiteSpace(manufacturer))
                return manufacturer;

            return InferManufacturerFromPart(manufacturerPart);
        }

        private static string InferManufacturerFromPart(string manufacturerPart)
        {
            string family = SelectFamily(manufacturerPart);
            if (family.StartsWith("DF", StringComparison.OrdinalIgnoreCase))
                return "HRS";

            return "";
        }

        private static string SelectFamily(string manufacturerPart)
        {
            string cleaned = Clean(manufacturerPart);
            if (string.IsNullOrWhiteSpace(cleaned))
                return "";

            int dash = cleaned.IndexOf('-');
            if (dash > 0)
                return cleaned.Substring(0, dash);

            return cleaned;
        }

        private static string SelectOrientation(string lcscPartName, string manufacturerPart)
        {
            if (ContainsAny(lcscPartName, "vertical", "立式"))
                return "vertical";

            if (ContainsAny(lcscPartName, "right angle", "horizontal", "卧式"))
                return "right-angle";

            if (HasPartOrientationCode(manufacturerPart, 'H'))
                return "right-angle";

            if (HasPartOrientationCode(manufacturerPart, 'V'))
                return "vertical";

            return "vertical";
        }

        private static string SelectGender(string lcscPartName, string manufacturerPart)
        {
            if (ContainsAny(lcscPartName, "female", "socket", "母头"))
                return "female";

            if (ContainsAny(lcscPartName, "male", "plug", "公头", "插头"))
                return "male";

            if (HasConnectorContactCode(manufacturerPart, 'S'))
                return "female";

            if (HasConnectorContactCode(manufacturerPart, 'P') ||
                HasConnectorContactCode(manufacturerPart, 'M'))
                return "male";

            return "male";
        }

        private static string SelectConnectorRole(string lcscPartName)
        {
            bool hasPcb = ContainsAny(lcscPartName, "pcb", "PCB");
            bool hasPower = ContainsAny(lcscPartName, "power", "动力", "电源");
            bool hasPlug = ContainsAny(lcscPartName, "plug", "插头");

            var words = new List<string>();
            if (hasPcb)
                words.Add("PCB");
            if (hasPower)
                words.Add("power");
            if (hasPlug)
                words.Add("plug");
            words.Add("connector");

            return string.Join(" ", words);
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (string needle in needles)
            {
                if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool IsConnectorFootprint(string packageName, string manufacturerPart, string lcscPartName)
        {
            string packagePrefix = GetPackagePrefix(packageName);
            return ContainsAny(packagePrefix, "CONN") ||
                ContainsAny(lcscPartName, "connector", "socket", "plug", "连接器", "插头", "母头", "公头") ||
                HasConnectorContactCode(manufacturerPart, 'S') ||
                HasConnectorContactCode(manufacturerPart, 'P') ||
                HasConnectorContactCode(manufacturerPart, 'M');
        }

        private static bool HasConnectorContactCode(string value, char code)
        {
            string cleaned = Clean(value);
            for (int i = 1; i < cleaned.Length; i++)
            {
                if (char.ToUpperInvariant(cleaned[i]) != code ||
                    !char.IsDigit(cleaned[i - 1]))
                    continue;

                if (i + 1 >= cleaned.Length || IsPartSeparator(cleaned[i + 1]))
                    return true;
            }

            return false;
        }

        private static bool HasPartOrientationCode(string value, char code)
        {
            string cleaned = Clean(value);
            for (int i = 1; i < cleaned.Length; i++)
            {
                if (char.ToUpperInvariant(cleaned[i]) != code ||
                    !char.IsDigit(cleaned[i - 1]))
                    continue;

                if (i + 1 >= cleaned.Length || IsPartSeparator(cleaned[i + 1]) || char.IsDigit(cleaned[i + 1]))
                    return true;
            }

            return false;
        }

        private static bool IsPartSeparator(char c)
        {
            return c == '-' || c == '_' || c == '(' || c == ')' || c == '.' || char.IsWhiteSpace(c);
        }

        private static string JoinWords(params string[] words)
        {
            var cleanedWords = new List<string>();
            foreach (string word in words)
            {
                string cleaned = Clean(word);
                if (!string.IsNullOrWhiteSpace(cleaned))
                    cleanedWords.Add(cleaned);
            }

            return string.Join(" ", cleanedWords);
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

        private static bool IsPackageSpecificToPart(string packageName, string partNumber)
        {
            if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(partNumber))
                return false;

            if (string.Equals(packageName, partNumber, StringComparison.OrdinalIgnoreCase))
                return true;

            string packageSuffix = GetPackageSuffix(packageName);
            if (string.Equals(packageSuffix, partNumber, StringComparison.OrdinalIgnoreCase))
                return true;

            string comparablePackageName = NormalizePartIdentifier(packageName);
            string comparablePartNumber = NormalizePartIdentifier(partNumber);
            if (!string.IsNullOrWhiteSpace(comparablePackageName) &&
                !string.IsNullOrWhiteSpace(comparablePartNumber) &&
                comparablePackageName.IndexOf(comparablePartNumber, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return packageName.IndexOf(partNumber, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizePartIdentifier(string value)
        {
            string cleaned = Clean(value);
            if (string.IsNullOrWhiteSpace(cleaned))
                return "";

            var normalized = new StringBuilder(cleaned.Length);
            foreach (char c in cleaned)
            {
                if (char.IsLetterOrDigit(c))
                    normalized.Append(c);
            }

            return normalized.ToString();
        }

        private static bool AreCompatibleConnectorPartNames(string packagePartName, string manufacturerPart)
        {
            string normalizedPackagePartName = NormalizePartIdentifier(packagePartName);
            string normalizedManufacturerPart = NormalizePartIdentifier(manufacturerPart);
            if (string.Equals(normalizedPackagePartName, normalizedManufacturerPart, StringComparison.OrdinalIgnoreCase))
                return true;

            string comparablePackagePartName = NormalizeConnectorVariantIdentifier(packagePartName);
            string comparableManufacturerPart = NormalizeConnectorVariantIdentifier(manufacturerPart);
            return !string.IsNullOrWhiteSpace(comparablePackagePartName) &&
                string.Equals(comparablePackagePartName, comparableManufacturerPart, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeConnectorVariantIdentifier(string value)
        {
            string cleaned = Clean(value).ToUpperInvariant();
            if (cleaned.Length < 5 ||
                cleaned[0] != 'D' ||
                cleaned[1] != 'F')
                return NormalizePartIdentifier(cleaned).ToUpperInvariant();

            int familyEnd = 2;
            while (familyEnd < cleaned.Length && char.IsDigit(cleaned[familyEnd]))
                familyEnd++;

            if (familyEnd <= 2 ||
                familyEnd + 1 >= cleaned.Length ||
                !char.IsLetter(cleaned[familyEnd]) ||
                (!char.IsDigit(cleaned[familyEnd + 1]) && !IsPartSeparator(cleaned[familyEnd + 1])))
                return NormalizePartIdentifier(cleaned).ToUpperInvariant();

            return NormalizePartIdentifier(cleaned.Remove(familyEnd, 1)).ToUpperInvariant();
        }

        private static string InferPartNameFromPackage(string packageName)
        {
            string cleaned = Clean(packageName);
            if (string.IsNullOrWhiteSpace(cleaned))
                return "";

            string[] tokens = cleaned.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            int start = -1;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (LooksLikePartFamily(tokens[i]))
                {
                    start = i;
                    break;
                }
            }

            if (start < 0)
                return "";

            var parts = new List<string>();
            for (int i = start; i < tokens.Length; i++)
                parts.Add(tokens[i]);

            if (parts.Count == 0)
                return "";

            string candidate;
            string last = parts[parts.Count - 1];
            if (parts.Count > 1 && IsDigitsOnly(last))
            {
                parts.RemoveAt(parts.Count - 1);
                candidate = string.Join("-", parts) + "(" + last + ")";
            }
            else
            {
                candidate = string.Join("-", parts);
            }

            return FormatTrailingParenthesizedSuffix(candidate);
        }

        private static bool LooksLikePartFamily(string value)
        {
            string cleaned = Clean(value);
            if (cleaned.Length < 3 || !char.IsLetter(cleaned[0]) || !char.IsLetter(cleaned[1]))
                return false;

            for (int i = 2; i < cleaned.Length; i++)
            {
                if (char.IsDigit(cleaned[i]))
                    return true;

                if (!char.IsLetter(cleaned[i]))
                    return false;
            }

            return false;
        }

        private static string FormatTrailingParenthesizedSuffix(string value)
        {
            string cleaned = Clean(value);
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.IndexOf('(') >= 0)
                return cleaned;

            int suffixStart = cleaned.Length;
            while (suffixStart > 0 && char.IsDigit(cleaned[suffixStart - 1]))
                suffixStart--;

            if (suffixStart == cleaned.Length)
                return cleaned;

            string suffix = cleaned.Substring(suffixStart);
            string prefix = cleaned.Substring(0, suffixStart);
            if (prefix.EndsWith("-", StringComparison.Ordinal) && PrefixContainsVoltageMarker(prefix))
                return prefix.Substring(0, prefix.Length - 1) + "(" + suffix + ")";

            if (PrefixContainsVoltageMarker(prefix))
                return prefix + "(" + suffix + ")";

            return cleaned;
        }

        private static bool PrefixContainsVoltageMarker(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            int marker = value.LastIndexOf('V');
            if (marker < 0)
                marker = value.LastIndexOf('v');

            return marker > 0 && char.IsDigit(value[marker - 1]);
        }

        private static bool IsDigitsOnly(string value)
        {
            string cleaned = Clean(value);
            if (string.IsNullOrWhiteSpace(cleaned))
                return false;

            foreach (char c in cleaned)
            {
                if (!char.IsDigit(c))
                    return false;
            }

            return true;
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

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-")
                return "";

            return value.Trim();
        }
    }
}
