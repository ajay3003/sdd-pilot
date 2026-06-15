namespace BirkNext.Web.Models;

public enum TraceArtifactType
{
    Requirement,      // Testable functional requirement — coverage-eligible
    AcceptanceTest,   // Test scenario (not put in Requirements list)
    Clarification,    // Q/A or NeedsClarification item — not coverage-eligible
    Decision,         // Architecture/API decision or Q/A session — not coverage-eligible
    ArchitectureNote, // Architecture description/component note — not coverage-eligible
    Metadata,         // Config, settings, or metadata section — not coverage-eligible
}

public enum TraceCoverageStatus
{
    Covered,     // requirement has ≥1 linked test
    MissingTests, // requirement with 0 tests
    Orphaned,    // test or SC with no counterpart
    NotEligible, // non-testable artifact (clarification, decision, architecture note, metadata)
}

public sealed class TracedRequirement
{
    public required Guid CandidateId { get; init; }
    public required string Title { get; init; }
    public string? FrId { get; init; }
    public string? UserStoryId { get; init; }
    public List<ExtractionCandidate> LinkedTests { get; init; } = [];
    public List<string> LinkedScIds { get; init; } = [];
    public TraceCoverageStatus Status { get; init; }
    public TraceArtifactType ArtifactType { get; init; } = TraceArtifactType.Requirement;

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

    /// <summary>Distinct test count — avoids double-counting when tests are linked to multiple requirements by proximity.</summary>
    public required int TotalTests { get; init; }

    public bool IsEmpty => Requirements.Count == 0 && SuccessCriteria.Count == 0;

    // Coverage — eligible artifacts only
    public int EligibleCount      => Requirements.Count(r => r.IsEligible);
    public int CoveredCount       => Requirements.Count(r => r.IsEligible && r.Status == TraceCoverageStatus.Covered);
    public int CoveragePercent    => EligibleCount == 0 ? 0 : CoveredCount * 100 / EligibleCount;
    public int GapCount           => Requirements.Count(r => r.IsEligible && r.Status == TraceCoverageStatus.MissingTests)
                                   + OrphanedTests.Count;

    // Artifact breakdown
    public int RequirementCount    => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Requirement);
    public int ClarificationCount  => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Clarification);
    public int DecisionCount       => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Decision);
    public int ArchitectureNoteCount => Requirements.Count(r => r.ArtifactType == TraceArtifactType.ArchitectureNote);
    public int MetadataCount       => Requirements.Count(r => r.ArtifactType == TraceArtifactType.Metadata);

    public bool HasNonEligibleArtifacts =>
        ClarificationCount + DecisionCount + ArchitectureNoteCount + MetadataCount > 0;
}
