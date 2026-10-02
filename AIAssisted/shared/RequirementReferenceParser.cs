using System.Text.RegularExpressions;

namespace BirkNext.Sdd;

/// <summary>
/// The one requirement/acceptance-criterion identifier grammar shared by Specification parsing and source-test reference extraction.
/// Keeps the built-in compact Spec-Kit forms (FR1, US2 → FR-001, US-002) and accepts project IDs such as JIRA-123, US-A1 or ABC-4 without a
/// global prefix list. A matched identifier is a reference, not a link: callers link it only to an identifier that exists in the current
/// specification.
/// </summary>
public static class RequirementReferenceParser
{
    /// <summary>Identifier anywhere in text (word-bounded).</summary>
    public const string ReferencePattern = @"\b(?<id>(?:(?:FR|NFR|SC|US|UC|AC|TS|REQ|TC)-?\d{1,4}|[A-Z][A-Z0-9]*-[A-Z0-9]*\d[A-Z0-9]*))\b";

    private static readonly Regex AnyCase = new(ReferencePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Upper = new(ReferencePattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Compact = new(@"^(FR|NFR|SC|US|UC|AC|TS|REQ|TC)-?(\d{1,4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Distinct normalized identifiers in <paramref name="text"/>, in order of first appearance. Specification text matches case-insensitively;
    /// source text (<paramref name="requireUpperCase"/>) only as written in upper case, so prose such as "ui-2" is not an identifier.</summary>
    public static IReadOnlyList<string> Extract(string? text, bool requireUpperCase = false)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match m in (requireUpperCase ? Upper : AnyCase).Matches(text))
        {
            var id = Normalize(m.Groups["id"].Value);
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>Upper-cases and zero-pads the built-in numeric prefixes (FR-1 → FR-001); every other identifier is preserved as written (upper-cased).</summary>
    public static string Normalize(string value)
    {
        var id = value.Trim().ToUpperInvariant();
        var compact = Compact.Match(id);
        return compact.Success ? $"{compact.Groups[1].Value}-{compact.Groups[2].Value.PadLeft(3, '0')}" : id;
    }

    /// <summary>True when <paramref name="text"/> starts with an identifier (optionally after a label such as "Requirement:"): the structured form
    /// "// FR-023: …" rather than an identifier mentioned mid-sentence.</summary>
    public static bool StartsWithReference(string text) =>
        Regex.IsMatch(text.TrimStart(), @"^(?:(?:requirements?|req|covers|verifies|satisfies|acceptance\s+criteri(?:on|a)|ac)\s*[:#-]?\s*)?" + ReferencePattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
