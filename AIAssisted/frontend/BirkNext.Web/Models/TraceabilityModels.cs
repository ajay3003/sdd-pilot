namespace BirkNext.Web.Models;

public enum TraceCoverageStatus
{
    Covered,        // requirement has ≥1 linked test
    MissingTests,   // requirement with 0 tests
    Orphaned,       // test or SC with no counterpart
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
    public bool IsEmpty => Requirements.Count == 0;

    public int TotalTests => Requirements.Sum(r => r.LinkedTests.Count) + OrphanedTests.Count;
    public int CoveredCount => Requirements.Count(r => r.Status == TraceCoverageStatus.Covered);
    public int CoveragePercent => Requirements.Count == 0 ? 0 : CoveredCount * 100 / Requirements.Count;
    public int GapCount => Requirements.Count(r => r.Status == TraceCoverageStatus.MissingTests) + OrphanedTests.Count;
}
