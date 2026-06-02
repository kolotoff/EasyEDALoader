using System.Collections.Generic;

namespace EasyEDA_Loader
{
    public static class CleanStepCacheKeys
    {
        public static string GetCleanModeKey(string modelKey, bool cleanText)
        {
            return modelKey + (cleanText ? "__watermark_text" : "__watermark");
        }

        public static IReadOnlyList<string> GetCleanModeKeys(string modelKey)
        {
            return new[]
            {
                GetCleanModeKey(modelKey, false),
                GetCleanModeKey(modelKey, true)
            };
        }
    }
}
