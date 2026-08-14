using System;
using System.Collections.Generic;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    public static class TronstolE1Description
    {
        public static string Resolve(
            string partNumber,
            string comment,
            string componentDescription)
        {
            partNumber = TronstolE1Text.Normalize(partNumber);
            comment = TronstolE1Text.Normalize(comment);
            componentDescription = TronstolE1Text.Normalize(componentDescription);

            if (string.IsNullOrEmpty(comment)
                || ContainsText(partNumber, comment)
                || ContainsMostCommentParts(componentDescription, comment))
            {
                return componentDescription;
            }

            if (string.IsNullOrEmpty(componentDescription))
                return comment;

            return comment + "; " + componentDescription;
        }

        private static bool ContainsText(string text, string value)
        {
            return !string.IsNullOrEmpty(text)
                && !string.IsNullOrEmpty(value)
                && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsMostCommentParts(string description, string comment)
        {
            if (string.IsNullOrEmpty(description) || string.IsNullOrEmpty(comment))
                return false;

            if (ContainsText(description, comment))
                return true;

            string[] commentParts = comment.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (commentParts.Length == 0)
                return false;

            HashSet<string> descriptionParts = new HashSet<string>(
                description.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            int matchedParts = 0;
            for (int index = 0; index < commentParts.Length; index++)
            {
                if (descriptionParts.Contains(commentParts[index]))
                    matchedParts++;
            }

            return matchedParts * 2 > commentParts.Length;
        }
    }
}
