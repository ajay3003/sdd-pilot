namespace BirkNext.Api.Services.ImplementationTraceability;

public enum EvidenceConfidence
{
    Confirmed, // Work item relation to PR exists
    Likely,    // PR title / branch / commit message contains task ID
    Missing,   // No evidence found
}

public enum EvidenceLinkReason
{
    WorkItemRelation,
    PrTitle,
    BranchName,
    CommitMessage,
}

public enum FileCategory
{
    Source,
    Test,
    Configuration,
    Documentation,
    Migration,
    Unknown,
}

public sealed class ChangedFileEvidence
{
    public required string Path { get; init; }
    public required string ChangeType { get; init; }
    public string? ObjectId { get; init; }
    public string? CommitId { get; init; }
    public string? PullRequestId { get; init; }
    public FileCategory Category { get; init; }
    public string? RelatedTestFile { get; init; }
    public bool HasTestEvidence { get; init; }
}

public sealed class CommitEvidence
{
    public required string ExternalId { get; init; }
    public required string DisplayTitle { get; init; }
    public string Source { get; init; } = "AzureDevOps";
    public string? SourceUrl { get; init; }
    public string? Author { get; init; }
    public DateTime? Date { get; init; }
}

public sealed class PullRequestEvidence
{
    public required string ExternalId { get; init; }
    public required string DisplayTitle { get; init; }
    public string Source { get; init; } = "AzureDevOps";
    public string? SourceUrl { get; init; }
    public string Status { get; init; } = string.Empty;
    public string SourceBranch { get; init; } = string.Empty;
    public string TargetBranch { get; init; } = string.Empty;
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }
    public DateTime? ClosedDate { get; init; }
    public string? MergeCommitId { get; init; }
    public EvidenceLinkReason LinkReason { get; init; }
    public List<CommitEvidence> Commits { get; init; } = [];
    public List<ChangedFileEvidence> ChangedFiles { get; init; } = [];
}

public sealed class TaskImplementationEvidence
{
    public required string ExternalId { get; init; }
    public required string DisplayTitle { get; init; }
    public string Source { get; init; } = "AzureDevOps";
    public string? SourceUrl { get; init; }
    public string State { get; init; } = string.Empty;
    public string? AssignedTo { get; init; }
    public string WorkItemType { get; init; } = string.Empty;
    public EvidenceConfidence Confidence { get; init; }
    public List<PullRequestEvidence> PullRequests { get; init; } = [];
}

public sealed class TestEvidenceItem
{
    public required string SourceFile { get; init; }
    public string? ExpectedTestFile { get; init; }
    public bool HasTest { get; init; }
    public string? FoundTestFile { get; init; }
    public string? PullRequestId { get; init; }
}

public sealed class TraceabilityGapItem
{
    public required string Description { get; init; }
    public string? RelatedExternalId { get; init; }
    public string GapKind { get; init; } = string.Empty;
}

public sealed class ImplementationTraceabilityReport
{
    public List<TaskImplementationEvidence> Tasks { get; init; } = [];
    public List<ChangedFileEvidence> UnmappedChanges { get; init; } = [];
    public List<TestEvidenceItem> TestEvidence { get; init; } = [];
    public List<TraceabilityGapItem> Gaps { get; init; } = [];
    public string Source { get; init; } = "Mock";
    public string? StatusMessage { get; init; }
}

public sealed class FetchEvidenceRequest
{
    public List<int> WorkItemIds { get; init; } = [];
    public string? RepositoryId { get; init; }
    public string? Branch { get; init; }
}

public sealed class ProviderStatusResponse
{
    public bool Configured { get; init; }
    public bool UsingMock { get; init; }
    public required string Message { get; init; }
}
