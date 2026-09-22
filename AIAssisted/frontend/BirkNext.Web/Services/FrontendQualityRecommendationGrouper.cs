using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Remediation themes: one card per thing to DO, not one per observation that asks for it.
///
/// Recommendations used to be generated one per source finding. Five contrast observations on five routes produced five
/// identical "fix the contrast" cards, and a review with fifty-seven observations produced a wall of cards nobody could
/// read — which is the same as producing none. A theme is the work item: what to change, how much of it there is, and
/// how urgent the worst thing behind it is.
///
/// Grouping is by the REMEDIATION, not by the issue: two different logical issues that are fixed by the same change
/// ("compress this", "compress that") are one job. Issues whose remediations genuinely differ stay separate, however
/// similar their titles are.
/// </summary>
public static class FrontendQualityRecommendationGrouper
{
    /// <summary>How many themes the result shows before asking; the rest stay behind "View all recommendations".</summary>
    public const int DefaultVisibleCount = 6;

    public static List<FrontendQualityRecommendationTheme> Build(IReadOnlyList<FrontendQualityLogicalIssue> issues)
    {
        var themes = issues
            // Informational observations ask for nothing, and a derived conclusion is fixed by fixing what it is drawn
            // from — a recommendation for either would be a task nobody can close.
            .Where(issue => issue.IsActionable && !string.IsNullOrWhiteSpace(issue.Recommendation))
            .GroupBy(issue => FrontendQualityIssueIdentity.Normalize(issue.Recommendation), StringComparer.Ordinal)
            .Select(Theme)
            .OrderBy(theme => theme.Priority)
            .ThenByDescending(theme => theme.SourceFindingCount)
            .ThenBy(theme => theme.Title, StringComparer.Ordinal)
            .ToList();

        return themes;
    }

    private static FrontendQualityRecommendationTheme Theme(IGrouping<string, FrontendQualityLogicalIssue> group)
    {
        var issues = group
            .OrderBy(issue => issue.PrimarySeverity)
            .ThenBy(issue => issue.CanonicalTitle, StringComparer.Ordinal)
            .ToList();
        var lead = issues[0];

        // Every place the work has to be done, counted once across the issues the theme covers.
        var pages = issues.SelectMany(issue => issue.AffectedPages).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        return new FrontendQualityRecommendationTheme(
            Key: group.Key,
            // The remediation names the theme. A title taken from one of the issues would name one occurrence of a job
            // that spans several.
            Title: lead.Recommendation,
            // Priority follows the worst thing the theme fixes — never the count, which says how much work it is, not
            // how urgent it is.
            Priority: lead.PrimarySeverity,
            PrimaryCategory: lead.Category,
            Issues: issues,
            AffectedPages: pages,
            SourceFindingCount: issues.Sum(issue => issue.SourceFindingCount));
    }
}
