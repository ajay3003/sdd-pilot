using System.Text;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// What makes two source observations the SAME problem.
///
/// The page is the thing that must not be part of the identity. Page-specific engines emit one finding per route with
/// the route appended to the title ("Contrast (Minimum) — /admin/user-access") and repeated on the evidence line
/// ("Page: /admin/user-access"). Treating those as distinct problems turned one contrast defect into five issues, five
/// recommendations and five things to triage. Here the route is lifted off the observation and collected as a PLACE the
/// one issue occurs in.
///
/// What stays in the identity is the rule that fired and what it fired ABOUT. The subject matters because one rule can
/// legitimately report different problems: "Slow GraphQL operation: HentNodtilganger" and "Slow GraphQL operation:
/// HentBrukere" share a rule id and are two operations to look at, so they stay apart while each of them merges across
/// every page it was seen on.
/// </summary>
public static class FrontendQualityIssueIdentity
{
    /// <summary>How a page-qualified title separates the subject from the route it was observed on.</summary>
    private const string PageSeparator = " — ";

    /// <summary>The evidence line page-specific engines record the route on.</summary>
    private const string PageEvidencePrefix = "Page: ";

    /// <summary>
    /// The route an observation was made on, or null when it is not page-specific. The evidence line is authoritative
    /// because it is the value the engine recorded; the title suffix is a rendering of it and is only a fallback.
    /// </summary>
    public static string? Page(FrontendQualityFinding finding)
    {
        var recorded = finding.Evidence
            .FirstOrDefault(e => e.StartsWith(PageEvidencePrefix, StringComparison.Ordinal))?[PageEvidencePrefix.Length..]
            .Trim();
        if (!string.IsNullOrWhiteSpace(recorded)) return recorded;

        // Only a suffix that actually looks like a route or an origin: a title may contain an em dash of its own.
        var index = finding.Title.LastIndexOf(PageSeparator, StringComparison.Ordinal);
        if (index < 0) return null;
        var candidate = finding.Title[(index + PageSeparator.Length)..].Trim();
        return LooksLikeLocation(candidate) ? candidate : null;
    }

    /// <summary>What the observation is about, with any page qualifier removed. Never empty.</summary>
    public static string Subject(FrontendQualityFinding finding)
    {
        var page = Page(finding);
        var title = finding.Title;
        if (page is not null)
        {
            var suffix = PageSeparator + page;
            if (title.EndsWith(suffix, StringComparison.Ordinal))
                title = title[..^suffix.Length];
        }
        return title.Trim() is { Length: > 0 } trimmed ? trimmed : finding.Title;
    }

    /// <summary>
    /// The grouping key for an observation the registry does not name. Built from the rule that fired and the subject
    /// it fired about, so the same rule on twenty routes is one key and two different operations stay two.
    ///
    /// The engine is part of the key: two engines reporting the same rule id mean nothing to each other, and a genuine
    /// cross-engine equivalence is a product decision that belongs in the registry, not in a string match.
    /// </summary>
    public static string DerivedKey(FrontendQualityFindingInstance instance)
    {
        var subject = Normalize(instance.Subject ?? instance.Title);
        return string.IsNullOrWhiteSpace(instance.SourceRuleId)
            ? $"subject:{instance.EngineId}:{subject}"
            : $"rule:{instance.EngineId}:{instance.SourceRuleId}:{subject}";
    }

    /// <summary>
    /// Case, spacing, punctuation and digits removed, so wording that differs only in presentation does not split a
    /// group. Digits go because they are almost always the measurement ("3 localhost URLs", "4 repeated calls") rather
    /// than the problem; a subject that is ONLY digits keeps them, so it cannot collapse to nothing.
    /// </summary>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetter(ch)) { builder.Append(char.ToLowerInvariant(ch)); lastWasSpace = false; }
            else if (!lastWasSpace) { builder.Append(' '); lastWasSpace = true; }
        }
        var normalized = builder.ToString().Trim();
        return normalized.Length > 0 ? normalized : value.Trim().ToLowerInvariant();
    }

    /// <summary>A route, an origin or an absolute URL — the shapes an engine records a location as.</summary>
    private static bool LooksLikeLocation(string candidate) =>
        candidate.Length > 0 &&
        (candidate.StartsWith('/') ||
         candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
