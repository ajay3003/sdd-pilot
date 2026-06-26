namespace BirkNext.Web.Models;

public enum FlowCoverageStatus { Covered, Partial, MissingTests, NoRequirements }

public enum StoryHealthStatus
{
    Covered,
    MissingTests,
    MissingSuccessCriteria,
    MissingUserStoryMapping,
    Partial,
    NoRequirements,
}

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
    public int Priority { get; init; } // 1=P1, 2=P2, 3=P3, 0=unspecified

    public int CoveredReqCount => Requirements.Count(r => r.HasTests);

    public FlowCoverageStatus CoverageStatus =>
        IsDecisionLane             ? FlowCoverageStatus.NoRequirements :
        Requirements.Count == 0    ? FlowCoverageStatus.NoRequirements :
        CoveredReqCount == Requirements.Count ? FlowCoverageStatus.Covered :
        CoveredReqCount == 0       ? FlowCoverageStatus.MissingTests :
                                     FlowCoverageStatus.Partial;

    public StoryHealthStatus HealthStatus
    {
        get
        {
            if (IsUnmapped)          return StoryHealthStatus.MissingUserStoryMapping;
            if (IsDecisionLane || Requirements.Count == 0) return StoryHealthStatus.NoRequirements;
            if (CoveredReqCount == 0) return StoryHealthStatus.MissingTests;
            if (CoveredReqCount < Requirements.Count) return StoryHealthStatus.Partial;
            if (SuccessCriteria.Count == 0) return StoryHealthStatus.MissingSuccessCriteria;
            return StoryHealthStatus.Covered;
        }
    }

    /// <summary>0–100. Based on test coverage (50%) + success criteria presence (50%).</summary>
    public int QaReadinessScore
    {
        get
        {
            if (IsDecisionLane || Requirements.Count == 0) return 0;
            var testScore = CoveredReqCount * 50 / Requirements.Count;
            var scScore   = SuccessCriteria.Count > 0 ? 50 : 0;
            return testScore + scScore;
        }
    }
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

    /// <summary>Functional requirements (FR-###) without test coverage.</summary>
    public int GapFunctionalRequirements =>
        Stories.Where(s => !s.IsDecisionLane)
               .SelectMany(s => s.Requirements)
               .Count(r => !r.HasTests && (r.FrId?.StartsWith("FR-", StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>User story items (US-###) without test coverage.</summary>
    public int GapUserStories =>
        Stories.Where(s => !s.IsDecisionLane)
               .SelectMany(s => s.Requirements)
               .Count(r => !r.HasTests && (r.FrId?.StartsWith("US-", StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>Total items in decision / clarification lanes (not counted toward gaps).</summary>
    public int DecisionItemCount =>
        Stories.Where(s => s.IsDecisionLane).Sum(s => s.Requirements.Count);

    public int StoriesMissingTests =>
        Stories.Count(s => !s.IsUnmapped && !s.IsDecisionLane && s.Requirements.Count > 0
                           && s.CoveredReqCount < s.Requirements.Count);

    public int StoriesMissingSuccessCriteria =>
        Stories.Count(s => !s.IsUnmapped && !s.IsDecisionLane && s.Requirements.Count > 0
                           && s.SuccessCriteria.Count == 0);
}
