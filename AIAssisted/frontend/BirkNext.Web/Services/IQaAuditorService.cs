using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IQaAuditorService
{
    /// <summary>
    /// Execute all deterministic audit rules against the four artifact documents.
    /// Any argument may be null; the service degrades gracefully per missing artifact.
    /// </summary>
    QaAuditReport Audit(
        ConstitutionDocument? constitution,
        SpecTree?             spec,
        PlanDocument?         plan,
        TaskTree?             tasks,
        ReviewContext?        context = null);

    IEnumerable<QaFinding>        SearchFindings(IEnumerable<QaFinding> findings, string query);
    IEnumerable<QaFinding>        FilterFindingsBySeverity(IEnumerable<QaFinding> findings, QaSeverity? severity);
    IEnumerable<QaFinding>        FilterFindingsByCategory(IEnumerable<QaFinding> findings, QaCategory? category);
    IEnumerable<QaGap>            SearchGaps(IEnumerable<QaGap> gaps, string query);
    IEnumerable<QaRecommendation> SearchRecommendations(IEnumerable<QaRecommendation> recs, string query);
    IEnumerable<QaRecommendation> FilterRecommendationsByCategory(IEnumerable<QaRecommendation> recs, QaCategory? category);
}
