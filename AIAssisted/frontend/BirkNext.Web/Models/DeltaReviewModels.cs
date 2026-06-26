namespace BirkNext.Web.Models;

public record DeltaItemDto(
    string Status,
    string Classification,
    string MatchKey,
    string? OldTitle,
    string? NewTitle,
    string? ContextHeading,
    IReadOnlyList<string> ImpactHints);

public record DeltaSummaryDto(
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
