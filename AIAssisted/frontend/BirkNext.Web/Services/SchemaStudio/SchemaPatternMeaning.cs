using System.Text.RegularExpressions;

namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>
/// Plain-language meaning of an XSD pattern, only where it is safely recognizable. A pattern is named by behaviour, not by its text: it must
/// accept every canonical example of the format and reject every counter-example (XSD patterns are implicitly anchored). Anything else —
/// including a pattern that cannot be compiled here — returns null, so callers say "Pattern rule defined" and show the raw regex. Nothing
/// is guessed from field or type names.
/// </summary>
public static class SchemaPatternMeaning
{
    private sealed record Format(string Meaning, string[] Accepts, string[] Rejects);

    private static readonly Format[] Formats =
    [
        new("UUID format",
            ["123e4567-e89b-12d3-a456-426614174000", "00000000-0000-0000-0000-000000000000", "a1b2c3d4-e5f6-7890-abcd-ef0123456789"],
            ["123e4567e89b12d3a456426614174000", "123e4567-e89b-12d3-a456-42661417400", "not-a-uuid", "", "123e4567-e89b-12d3-a456-4266141740000"]),
        new("Version number format",
            ["1.0", "2.4.1", "10.20.30"],
            ["v", "a.b", "1..2", "", "1.0 beta", "*"]),
    ];

    /// <summary>"UUID format", "Version number format", or null when the pattern is not one of the recognized formats.</summary>
    public static string? Recognize(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 300) return null;
        Regex regex;
        try { regex = new Regex($"^(?:{pattern})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { return null; }
        try
        {
            foreach (var format in Formats)
                if (format.Accepts.All(regex.IsMatch) && !format.Rejects.Any(regex.IsMatch)) return format.Meaning;
        }
        catch (RegexMatchTimeoutException) { return null; }
        return null;
    }
}
