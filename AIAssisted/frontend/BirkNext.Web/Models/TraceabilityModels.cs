namespace BirkNext.Web.Models;

public enum TraceArtifactType
{
    Requirement,
    AcceptanceTest,
    Clarification,
    Decision,
    Assumption,
    ArchitectureNote,
    Metadata,
}

public enum TraceCoverageStatus
{
    Covered,
    MissingTests,
    Orphaned,
    NotEligible,
}

public sealed class TracedRequirement
{
    public required Guid CandidateId { get; init; }
    public required string Title { get; init; }
    public string? FullContent { get; init; }
    public string? FrId { get; init; }
    public string? UserStoryId { get; init; }
    public string? UserStorySource { get; init; }
    public List<ExtractionCandidate> LinkedTests { get; init; } = [];
    public List<string> LinkedScIds { get; init; } = [];
    public string? SuccessCriteriaSource { get; init; }
    public string? CoverageReason { get; init; }
    public TraceCoverageStatus Status { get; init; }
    public TraceArtifactType ArtifactType { get; init; } = TraceArtifactType.Requirement;
    public bool NeedsReviewWarning { get; init; }

    public bool IsEligible => ArtifactType == TraceArtifactType.Requirement;
}

public sealed class TracedSc
{
    public required string SpecItemId { get; init; }
    public required string Title { get; init; }
    public string Excerpt { get; init; } = string.Empty;
    public List<string> LinkedFrIds { get; init; } = [];
    public int LinkedTestCount { get; init; }
    public TraceCoverageStatus Status { get; init; }
}

public sealed class TraceabilityModel
{
    public List<TracedRequirement> Requirements { get; init; } = [];
    public List<TracedSc> SuccessCriteria { get; init; } = [];
    public List<ExtractionCandidate> OrphanedTests { get; init; } = [];

    public required int TotalTests { get; init; }
    public int TotalCandidates { get; init; }
    public int RequirementCandidateCount { get; init; }
    public int DerivedRequirementCount { get; init; }

    public bool IsEmpty => Requirements.Count == 0 && SuccessCriteria.Count == 0;

    public int EligibleCount      => Requirements.Count(r => r.IsEligible);
    public int CoveredCount       => Requirements.Count(r => r.IsEligible && r.Status == TraceCoverageStatus.Covered);
    public int MissingTestsCount  => Requirements.Count(r => r.IsEligible && r.Status == TraceCoverageStatus.MissingTests);
    public int MissingUserStoryCount => Requirements.Count(r => r.IsEligible && string.IsNullOrWhiteSpace(r.UserStoryId));
    public int MissingSuccessCriteriaCount => Requirements.Count(r => r.IsEligible && r.LinkedScIds.Count == 0);
    public int OrphanTestCount    => OrphanedTests.Count;
    public int CoveragePercent    => EligibleCount == 0 ? 0 : CoveredCount * 100 / EligibleCount;
    public int GapCount           => MissingTestsCount + MissingUserStoryCount + MissingSuccessCriteriaCount + OrphanTestCount;

    public int RequirementCount    => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Requirement);
    public int ClarificationCount  => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Clarification);
    public int DecisionCount       => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Decision);
    public int AssumptionCount     => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Assumption);
    public int ArchitectureNoteCount => Requirements.Count(r => r.ArtifactType == TraceArtifactType.ArchitectureNote);
    public int MetadataCount       => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Metadata);

    public bool HasNonEligibleArtifacts =>
        ClarificationCount + DecisionCount + AssumptionCount + ArchitectureNoteCount + MetadataCount > 0;
}
