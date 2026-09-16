using System.Text;
using System.Text.RegularExpressions;

namespace RimWorldCodeRag.Common;

public static partial class TextUtilities
{
    private static readonly Regex CamelCaseBoundary = CamelCaseBoundaryRegex();
    private static readonly Regex NonIdentifier = NonIdentifierRegex();

    public static IEnumerable<string> SplitIdentifiers(IEnumerable<string> identifiers)
    {
        foreach (var id in identifiers)
        {
            foreach (var token in SplitIdentifier(id))
            {
                if (token.Length > 0)
                {
                    yield return token.ToLowerInvariant();
                }
            }
        }
    }

    public static IEnumerable<string> SplitIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            yield break;
        }

        var sanitized = NonIdentifier.Replace(identifier, " ");
        foreach (var raw in sanitized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var lowered = raw.ToLowerInvariant();
            if (lowered.Length == 0)
            {
                continue;
            }

            yield return lowered;

            foreach (var sub in CamelCaseBoundary.Split(raw))
            {
                var subLower = sub.ToLowerInvariant();
                if (subLower.Length > 0 && subLower != lowered)
                {
                    yield return subLower;
                }
            }
        }
    }

    public static string BuildPreview(string text, int maxLength = 320)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return TruncateForDisplay(trimmed, maxLength, " …");
    }

    public static string TruncateForDisplay(string? text, int maxLength, string ellipsis = "...")
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (maxLength <= 0)
        {
            return ellipsis;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        var safeLength = FindSafeTruncationBoundary(text, maxLength);
        if (safeLength <= 0)
        {
            return ellipsis;
        }

        return text[..safeLength] + ellipsis;
    }

    private static int FindSafeTruncationBoundary(string text, int maxLength)
    {
        var length = Math.Min(maxLength, text.Length);
        if (length <= 0)
        {
            return 0;
        }

        if (length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
        {
            length--;
        }

        if (length > 0 && char.IsLowSurrogate(text[length - 1]))
        {
            length--;
        }

        return Math.Max(0, length);
    }

    [GeneratedRegex("(?<!^)(?=[A-Z][a-z])|(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundaryRegex();

    [GeneratedRegex("[^A-Za-z0-9_\\.]", RegexOptions.Compiled)]
    private static partial Regex NonIdentifierRegex();
}
