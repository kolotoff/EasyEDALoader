using System.Text.RegularExpressions;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    public static class TronstolE1Text
    {
        private static readonly Regex WhitespaceRuns =
            new Regex(@"[\t\r\n\f\v ]+", RegexOptions.CultureInvariant);

        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return WhitespaceRuns.Replace(value, " ").Trim();
        }
    }
}
