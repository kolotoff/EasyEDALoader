using System;
using System.Collections.Generic;
using System.Globalization;

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
                lower.Contains("reference footprint"))
                return false;

            return true;
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
            string manufacturer = SelectManufacturer(parameters);
            string manufacturerPart = FirstNonEmpty(
                GetParameter(parameters, "Manufacturer Part"),
                partNumber,
                GetPackageSuffix(packageName));
            string family = SelectFamily(manufacturerPart);
            string lcscPartName = GetParameter(parameters, "LCSC Part Name");
            string cleanedMounting = Clean(mounting);

            if (string.IsNullOrWhiteSpace(manufacturer) ||
                string.IsNullOrWhiteSpace(family) ||
                string.IsNullOrWhiteSpace(cleanedMounting) ||
                geometry == null ||
                geometry.PositionCount <= 0)
                return "";

            var clauses = new List<string>
            {
                manufacturer + " " + family,
                geometry.PositionCount.ToString(CultureInfo.InvariantCulture) + "-position " +
                    SelectOrientation(lcscPartName) + " " +
                    cleanedMounting + " " +
                    SelectGender(lcscPartName) + " " +
                    SelectConnectorRole(lcscPartName)
            };

            if (geometry.PitchMm > 0)
                clauses.Add(FormatMm(geometry.PitchMm) + " mm pitch");

            if (geometry.BodyWidthMm > 0 && geometry.BodyHeightMm > 0)
                clauses.Add(FormatMm(geometry.BodyWidthMm) + " x " + FormatMm(geometry.BodyHeightMm) + " mm body");

            return string.Join(", ", clauses);
        }

        private static string SelectManufacturer(IReadOnlyDictionary<string, string> parameters)
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

            return Clean(manufacturer);
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

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "-")
                return "";

            return value.Trim();
        }
    }
}
