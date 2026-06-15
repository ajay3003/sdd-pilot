namespace BirkNext.Web.Models;

public enum FlowCoverageStatus { Covered, Partial, MissingTests, NoRequirements }

public sealed class FlowTest
{
    public required string Title { get; init; }
    public Guid? CandidateId { get; init; }
    public string? BddGiven { get; init; }
    public string? BddWhen { get; init; }
    public string? BddThen { get; init; }
    public string? LinkedFrId { get; init; }
    public bool HasBdd => BddGiven is not null || BddWhen is not null || BddThen is not null;
}

public sealed class FlowRequirement
{
    public required string Title { get; init; }
    public string? FrId { get; init; }
    public Guid? CandidateId { get; init; }
    public List<FlowTest> LinkedTests { get; init; } = [];
    public List<string> LinkedScIds { get; init; } = [];
    public bool HasTests => LinkedTests.Count > 0;
}

public sealed class FlowSc
{
    public required string SpecItemId { get; init; }
    public required string Title { get; init; }
    public string Excerpt { get; init; } = string.Empty;
    public List<string> LinkedFrIds { get; init; } = [];
}

public sealed class FlowStory
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string? StoryId { get; init; }
    public string? ContextDescription { get; init; }
    public List<FlowRequirement> Requirements { get; init; } = [];
    public List<FlowTest> AllTests { get; init; } = [];
    public List<FlowSc> SuccessCriteria { get; init; } = [];
    public bool IsUnmapped { get; init; }
    public bool IsDecisionLane { get; init; }

    public int CoveredReqCount => Requirements.Count(r => r.HasTests);

    public FlowCoverageStatus CoverageStatus =>
        IsDecisionLane             ? FlowCoverageStatus.NoRequirements :
        Requirements.Count == 0    ? FlowCoverageStatus.NoRequirements :
        CoveredReqCount == Requirements.Count ? FlowCoverageStatus.Covered :
        CoveredReqCount == 0       ? FlowCoverageStatus.MissingTests :
                                     FlowCoverageStatus.Partial;
}

public sealed class FlowModel
{
    public List<FlowStory> Stories { get; init; } = [];

    /// <summary>SC items from the spec that are not linked to any story's FRs.</summary>
    public List<FlowSc> UnlinkedSuccessCriteria { get; init; } = [];

    public bool IsEmpty => Stories.Count == 0;

    /// <summary>Total requirements, excluding decision lanes.</summary>
    public int TotalRequirements => Stories.Where(s => !s.IsDecisionLane).Sum(s => s.Requirements.Count);

    public int TotalTests => Stories.Sum(s => s.AllTests.Count);

    /// <summary>Total SC count across stories and unlinked.</summary>
    public int TotalSc => Stories.Sum(s => s.SuccessCriteria.Count) + UnlinkedSuccessCriteria.Count;

    /// <summary>Requirements without test coverage, excluding decision lanes.</summary>
    public int GapCount => Stories.Where(s => !s.IsDecisionLane).Sum(s => s.Requirements.Count(r => !r.HasTests));

    public int CoveragePercent
    {
        get
        {
            var totalReqs   = Stories.Where(s => !s.IsUnmapped && !s.IsDecisionLane).Sum(s => s.Requirements.Count);
            if (totalReqs == 0) return 0;
            var coveredReqs = Stories.Where(s => !s.IsUnmapped && !s.IsDecisionLane).Sum(s => s.CoveredReqCount);
            return coveredReqs * 100 / totalReqs;
        }
    }
}
