using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IArtifactTraceabilityService
{
    // Any document may be null — partial analysis is performed gracefully.
    // reviewContext: required aggregated semantic models; provides cross-artifact links
    ArtifactTraceabilityReport Analyze(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks,
        ReviewContext reviewContext);

    // Search and filter helpers for the UI
    IEnumerable<TraceabilityMatrixRow> SearchMatrix(IEnumerable<TraceabilityMatrixRow> rows, string query);
    IEnumerable<TraceabilityMatrixRow> FilterMatrixByStatus(IEnumerable<TraceabilityMatrixRow> rows, TraceabilityStatus? status);

    IEnumerable<TraceabilityGap> SearchGaps(IEnumerable<TraceabilityGap> gaps, string query);
    IEnumerable<TraceabilityGap> FilterGapsByArtifact(IEnumerable<TraceabilityGap> gaps, ArtifactType? type);
    IEnumerable<TraceabilityGap> FilterGapsBySeverity(IEnumerable<TraceabilityGap> gaps, GapSeverity? severity);

    IEnumerable<ChainCoverage> SearchChain(IEnumerable<ChainCoverage> chain, string query);
    IEnumerable<ChainCoverage> FilterChainByStatus(IEnumerable<ChainCoverage> chain, TraceabilityStatus? status);
}
