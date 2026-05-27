using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Models;

public enum SpecDeltaStatus
{
    Added,
    Modified,
    Removed,
    Unchanged,
}

public sealed record SpecDeltaItem(
    SpecDeltaStatus Status,
    ScenarioKind Classification,
    ExtractionCandidate? OldCandidate,
    ExtractionCandidate? NewCandidate,
    string MatchKey,
    IReadOnlyList<string> ImpactHints);

public sealed record SpecComparisonSummary(
    int AddedRequirements,
    int ModifiedRequirements,
    int RemovedRequirements,
    int UnchangedRequirements,
    int AddedTests,
    int RemovedTests,
    int PotentiallyImpactedTests,
    int AddedClarifications,
    int RemovedClarifications,
    int StillUnresolvedClarifications,
    int UncoveredRequirements,
    int NewClarificationRisks);

public sealed record SpecComparisonResult(
    IReadOnlyList<SpecDeltaItem> RequirementDeltas,
    IReadOnlyList<SpecDeltaItem> TestDeltas,
    IReadOnlyList<SpecDeltaItem> ClarificationDeltas,
    SpecComparisonSummary Summary);
