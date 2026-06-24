using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IQAReadinessService
{
    /// <summary>
    /// Compute an end-to-end readiness report from the four artifact documents.
    /// Any argument may be null; the service degrades gracefully.
    /// </summary>
    QAReadinessReport Assess(
        ConstitutionDocument? constitution,
        SpecTree?             spec,
        PlanDocument?         plan,
        TaskTree?             tasks);

    IEnumerable<ReadinessGap>            FilterGapsBySeverity(IEnumerable<ReadinessGap> gaps, ViolationSeverity? severity);
    IEnumerable<ReadinessRecommendation> FilterRecommendationsByArtifact(IEnumerable<ReadinessRecommendation> recs, ArtifactType? artifact);
    IEnumerable<ReadinessRecommendation> FilterRecommendationsByPriority(IEnumerable<ReadinessRecommendation> recs, ViolationSeverity? priority);
}
