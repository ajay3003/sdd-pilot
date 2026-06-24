using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IConstitutionComplianceService
{
    // Any artifact may be null — partial analysis is performed gracefully.
    ConstitutionComplianceReport Analyze(
        ConstitutionDocument? constitution,
        SpecTree?             spec,
        PlanDocument?         plan,
        TaskTree?             tasks);

    // Search and filter helpers for the UI
    IEnumerable<ComplianceResult>        SearchResults(IEnumerable<ComplianceResult> results, string query);
    IEnumerable<ComplianceResult>        FilterResultsByStatus(IEnumerable<ComplianceResult> results, ComplianceStatus? status);
    IEnumerable<ComplianceResult>        FilterResultsByRuleType(IEnumerable<ComplianceResult> results, ConstitutionRuleType? type);

    IEnumerable<ComplianceViolation>     SearchViolations(IEnumerable<ComplianceViolation> violations, string query);
    IEnumerable<ComplianceViolation>     FilterViolationsBySeverity(IEnumerable<ComplianceViolation> violations, ViolationSeverity? severity);
    IEnumerable<ComplianceViolation>     FilterViolationsByArtifact(IEnumerable<ComplianceViolation> violations, ArtifactType? artifact);

    IEnumerable<ComplianceGap>           SearchGaps(IEnumerable<ComplianceGap> gaps, string query);
    IEnumerable<ComplianceGap>           FilterGapsBySeverity(IEnumerable<ComplianceGap> gaps, ViolationSeverity? severity);

    IEnumerable<ComplianceRecommendation> SearchRecommendations(IEnumerable<ComplianceRecommendation> recs, string query);
    IEnumerable<ComplianceRecommendation> FilterRecommendationsByArtifact(IEnumerable<ComplianceRecommendation> recs, ArtifactType? artifact);
}
