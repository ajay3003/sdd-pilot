using System.Text.RegularExpressions;

namespace BirkNext.Web.Services;

/// <summary>Manual notes are plain text. Reject likely credentials and markup before persistence.</summary>
public static partial class WcagReviewText
{
    public static string Validate(string? value, int limit)
    {
        var text = (value ?? "").Trim();
        if (text.Length > limit || Unsafe().IsMatch(text))
            throw new ArgumentException("Use a short plain-text review without credentials, personal data, URLs or markup.");
        return text;
    }

    [GeneratedRegex(@"(?i)bearer|authorization|cookie|password|access[_-]?token|refresh[_-]?token|id[_-]?token|eyJ[A-Za-z0-9_-]+\.|[A-Za-z0-9_-]{48,}|[<>]|https?://|[\w.+-]+@[\w.-]+\.[a-z]{2,}|\b\d{6,}\b")]
    private static partial Regex Unsafe();
}
