namespace BirkNext.Api.Services.IntegrationTopology;

/// <summary>
/// The names a source's component name states for itself — the one identity rule Integration Topology and Integration Quality Review share.
/// A name is itself; "Full name (ABBR)" also states ABBR and the full name, when ABBR is an abbreviation of the full name: letters and digits
/// only, 2–10 characters, its letters upper case, starting with the full name's first letter ("Order Service (OS)" states OS). A
/// bracketed qualifier that is not such an abbreviation ("Portal (QA2)", "API (Development)") states nothing more. Nothing is fuzzy: names are
/// equal after normalization (case and punctuation ignored) or they are different.
/// </summary>
public static class ComponentNames
{
    /// <summary>The name, then (for "Full name (ABBR)") the abbreviation and the full name.</summary>
    public static IReadOnlyList<string> Stated(string name)
    {
        var trimmed = name.Trim();
        var open = trimmed.LastIndexOf('(');
        if (open <= 0 || !trimmed.EndsWith(')')) return [trimmed];
        var abbreviation = trimmed[(open + 1)..^1].Trim();
        var full = trimmed[..open].Trim();
        return IsAbbreviationOf(abbreviation, full) ? [trimmed, abbreviation, full] : [trimmed];
    }

    /// <summary>Two names of the same component: one of the names one states equals one of the names the other states (normalized).</summary>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && Stated(a).Select(TopologyBuilder.Normalize).Where(x => x.Length > 0).Intersect(Stated(b).Select(TopologyBuilder.Normalize)).Any();

    private static bool IsAbbreviationOf(string abbreviation, string full) =>
        abbreviation.Length is >= 2 and <= 10 && full.Length > abbreviation.Length
        && abbreviation.All(char.IsLetterOrDigit) && abbreviation.Count(char.IsLetter) >= 2 && abbreviation.Where(char.IsLetter).All(char.IsUpper)
        && char.ToUpperInvariant(abbreviation[0]) == char.ToUpperInvariant(full[0]);
}
