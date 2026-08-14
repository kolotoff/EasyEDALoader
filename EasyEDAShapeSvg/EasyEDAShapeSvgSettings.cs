using EDP;
using System;

namespace EasyEDA_Loader.EasyEDAShapeSvg
{
    public sealed class EasyEDAShapeSvgSettings : IOutputSettings
    {
        private const string IncludePadsKey = "IncludePads";

        public bool IncludePads { get; set; } = true;

        public string ExportToParameters()
        {
            return IncludePadsKey + "=" + (IncludePads ? "True" : "False");
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
                if (string.Equals(key, IncludePadsKey, StringComparison.OrdinalIgnoreCase)
                    && bool.TryParse(value, out bool includePads))
                {
                    IncludePads = includePads;
                }
            }
        }

        public void ResetToDefault()
        {
            IncludePads = true;
        }
    }
}
