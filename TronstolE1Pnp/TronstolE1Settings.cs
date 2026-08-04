using EDP;
using System;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    public sealed class TronstolE1Settings : IOutputSettings
    {
        private const string RemoveBgaSuffixKey = "RemoveBgaSuffix";
        private const string RemoveSpaceBgaSuffixKey = "RemoveSpaceBgaSuffix";
        private const string SkipNfComponentsKey = "SkipNfComponents";
        private const string SkipDnpComponentsKey = "SkipDnpComponents";
        private const string SkipManualSolderingComponentsKey = "SkipManualSolderingComponents";
        private const string SkipWaveSolderingComponentsKey = "SkipWaveSolderingComponents";
        private const string ExportPanelFiducialsKey = "ExportPanelFiducials";
        private const string ExportBoardDimensionsKey = "ExportBoardDimensions";
        private const string ExportEdgeRailsSizeKey = "ExportEdgeRailsSize";
        private const string RemoveFootprintFromPartNumberKey = "RemoveFootprintFromPartNumber";

        public bool RemoveBgaSuffix { get; set; } = true;
        public bool RemoveSpaceBgaSuffix { get; set; } = true;
        public bool SkipNfComponents { get; set; } = true;
        public bool SkipDnpComponents { get; set; } = true;
        public bool SkipManualSolderingComponents { get; set; } = true;
        public bool SkipWaveSolderingComponents { get; set; } = true;
        public bool ExportPanelFiducials { get; set; } = true;
        public bool ExportBoardDimensions { get; set; } = true;
        public bool ExportEdgeRailsSize { get; set; } = true;
        public bool RemoveFootprintFromPartNumber { get; set; } = true;

        public string ExportToParameters()
        {
            return RemoveBgaSuffixKey + "=" + (RemoveBgaSuffix ? "True" : "False")
                + "|"
                + RemoveSpaceBgaSuffixKey + "=" + (RemoveSpaceBgaSuffix ? "True" : "False")
                + "|"
                + SkipNfComponentsKey + "=" + (SkipNfComponents ? "True" : "False")
                + "|"
                + SkipDnpComponentsKey + "=" + (SkipDnpComponents ? "True" : "False")
                + "|"
                + SkipManualSolderingComponentsKey + "=" + (SkipManualSolderingComponents ? "True" : "False")
                + "|"
                + SkipWaveSolderingComponentsKey + "=" + (SkipWaveSolderingComponents ? "True" : "False")
                + "|"
                + ExportPanelFiducialsKey + "=" + (ExportPanelFiducials ? "True" : "False")
                + "|"
                + ExportBoardDimensionsKey + "=" + (ExportBoardDimensions ? "True" : "False")
                + "|"
                + ExportEdgeRailsSizeKey + "=" + (ExportEdgeRailsSize ? "True" : "False")
                + "|"
                + RemoveFootprintFromPartNumberKey + "=" + (RemoveFootprintFromPartNumber ? "True" : "False");
        }

        public void ImportFromParameters(string parameters)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            string[] entries = parameters.Split(new[] { '|', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string entry in entries)
            {
                int separator = entry.IndexOf('=');
                if (separator < 0)
                    continue;

                string key = entry.Substring(0, separator).Trim();
                string value = entry.Substring(separator + 1).Trim();
                if (string.Equals(key, RemoveBgaSuffixKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool enabled))
                {
                    RemoveBgaSuffix = enabled;
                }
                else if (string.Equals(key, RemoveSpaceBgaSuffixKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool removeSpaceBgaSuffix))
                {
                    RemoveSpaceBgaSuffix = removeSpaceBgaSuffix;
                }
                else if (string.Equals(key, SkipNfComponentsKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool skipNfComponents))
                {
                    SkipNfComponents = skipNfComponents;
                }
                else if (string.Equals(key, SkipDnpComponentsKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool skipDnpComponents))
                {
                    SkipDnpComponents = skipDnpComponents;
                }
                else if (string.Equals(key, SkipManualSolderingComponentsKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool skipManualSolderingComponents))
                {
                    SkipManualSolderingComponents = skipManualSolderingComponents;
                }
                else if (string.Equals(key, SkipWaveSolderingComponentsKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool skipWaveSolderingComponents))
                {
                    SkipWaveSolderingComponents = skipWaveSolderingComponents;
                }
                else if (string.Equals(key, ExportPanelFiducialsKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool exportPanelFiducials))
                {
                    ExportPanelFiducials = exportPanelFiducials;
                }
                else if (string.Equals(key, ExportBoardDimensionsKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool exportBoardDimensions))
                {
                    ExportBoardDimensions = exportBoardDimensions;
                }
                else if (string.Equals(key, ExportEdgeRailsSizeKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool exportEdgeRailsSize))
                {
                    ExportEdgeRailsSize = exportEdgeRailsSize;
                }
                else if (string.Equals(key, RemoveFootprintFromPartNumberKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool removeFootprintFromPartNumber))
                {
                    RemoveFootprintFromPartNumber = removeFootprintFromPartNumber;
                }
            }
        }

        public void ResetToDefault()
        {
            RemoveBgaSuffix = true;
            RemoveSpaceBgaSuffix = true;
            SkipNfComponents = true;
            SkipDnpComponents = true;
            SkipManualSolderingComponents = true;
            SkipWaveSolderingComponents = true;
            ExportPanelFiducials = true;
            ExportBoardDimensions = true;
            ExportEdgeRailsSize = true;
            RemoveFootprintFromPartNumber = true;
        }

        public string FormatFootprintName(string footprint)
        {
            string value = TronstolE1Text.Normalize(footprint);
            const string suffix = "_BGA";
            if (RemoveBgaSuffix
                && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - suffix.Length);
            }

            const string spaceSuffix = " BGA";
            if (RemoveSpaceBgaSuffix
                && value.EndsWith(spaceSuffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - spaceSuffix.Length);
            }

            return value;
        }

        public bool ShouldSkipComment(string comment)
        {
            string value = TronstolE1Text.Normalize(comment);
            return (SkipNfComponents && string.Equals(value, "NF", StringComparison.OrdinalIgnoreCase))
                || (SkipDnpComponents && string.Equals(value, "DNP", StringComparison.OrdinalIgnoreCase));
        }

        public bool ShouldSkipSolderingType(string solderingType)
        {
            string value = TronstolE1Text.Normalize(solderingType);
            return (SkipManualSolderingComponents
                    && string.Equals(value, "Manual", StringComparison.OrdinalIgnoreCase))
                || (SkipWaveSolderingComponents
                    && string.Equals(value, "Wave", StringComparison.OrdinalIgnoreCase));
        }

        public string FormatPartNumber(string partNumber, string footprint)
        {
            string value = TronstolE1PartNumber.Normalize(
                partNumber,
                footprint,
                RemoveFootprintFromPartNumber,
                true);
            string formattedFootprint = FormatFootprintName(footprint);
            if (!string.Equals(formattedFootprint, footprint, StringComparison.OrdinalIgnoreCase))
            {
                value = TronstolE1PartNumber.Normalize(
                    value,
                    formattedFootprint,
                    RemoveFootprintFromPartNumber,
                    true);
            }

            return value;
        }
    }
}
