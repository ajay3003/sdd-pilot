using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public enum EvidenceConfidence
{
    Confirmed,
    Likely,
    Missing,
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
    [JsonPropertyName("path")]           public string Path { get; init; } = string.Empty;
    [JsonPropertyName("changeType")]     public string ChangeType { get; init; } = string.Empty;
    [JsonPropertyName("objectId")]       public string? ObjectId { get; init; }
    [JsonPropertyName("commitId")]       public string? CommitId { get; init; }
    [JsonPropertyName("pullRequestId")]  public string? PullRequestId { get; init; }
    [JsonPropertyName("category")]       public FileCategory Category { get; init; }
    [JsonPropertyName("relatedTestFile")] public string? RelatedTestFile { get; init; }
    [JsonPropertyName("hasTestEvidence")] public bool HasTestEvidence { get; init; }
}

public sealed class CommitEvidence
{
    [JsonPropertyName("externalId")]   public string ExternalId { get; init; } = string.Empty;
    [JsonPropertyName("displayTitle")] public string DisplayTitle { get; init; } = string.Empty;
    [JsonPropertyName("source")]       public string Source { get; init; } = string.Empty;
    [JsonPropertyName("sourceUrl")]    public string? SourceUrl { get; init; }
    [JsonPropertyName("author")]       public string? Author { get; init; }
    [JsonPropertyName("date")]         public DateTime? Date { get; init; }
}

public sealed class PullRequestEvidence
{
    [JsonPropertyName("externalId")]    public string ExternalId { get; init; } = string.Empty;
    [JsonPropertyName("displayTitle")]  public string DisplayTitle { get; init; } = string.Empty;
    [JsonPropertyName("source")]        public string Source { get; init; } = string.Empty;
    [JsonPropertyName("sourceUrl")]     public string? SourceUrl { get; init; }
    [JsonPropertyName("status")]        public string Status { get; init; } = string.Empty;
    [JsonPropertyName("sourceBranch")]  public string SourceBranch { get; init; } = string.Empty;
    [JsonPropertyName("targetBranch")]  public string TargetBranch { get; init; } = string.Empty;
    [JsonPropertyName("createdBy")]     public string? CreatedBy { get; init; }
    [JsonPropertyName("createdDate")]   public DateTime? CreatedDate { get; init; }
    [JsonPropertyName("closedDate")]    public DateTime? ClosedDate { get; init; }
    [JsonPropertyName("mergeCommitId")] public string? MergeCommitId { get; init; }
    [JsonPropertyName("linkReason")]    public EvidenceLinkReason LinkReason { get; init; }
    [JsonPropertyName("commits")]       public List<CommitEvidence> Commits { get; init; } = [];
    [JsonPropertyName("changedFiles")]  public List<ChangedFileEvidence> ChangedFiles { get; init; } = [];
}

public sealed class TaskImplementationEvidence
{
    [JsonPropertyName("externalId")]    public string ExternalId { get; init; } = string.Empty;
    [JsonPropertyName("displayTitle")]  public string DisplayTitle { get; init; } = string.Empty;
    [JsonPropertyName("source")]        public string Source { get; init; } = string.Empty;
    [JsonPropertyName("sourceUrl")]     public string? SourceUrl { get; init; }
    [JsonPropertyName("state")]         public string State { get; init; } = string.Empty;
    [JsonPropertyName("assignedTo")]    public string? AssignedTo { get; init; }
    [JsonPropertyName("workItemType")]  public string WorkItemType { get; init; } = string.Empty;
    [JsonPropertyName("confidence")]    public EvidenceConfidence Confidence { get; init; }
    [JsonPropertyName("pullRequests")]  public List<PullRequestEvidence> PullRequests { get; init; } = [];
}

public sealed class TestEvidenceItem
{
    [JsonPropertyName("sourceFile")]       public string SourceFile { get; init; } = string.Empty;
    [JsonPropertyName("expectedTestFile")] public string? ExpectedTestFile { get; init; }
    [JsonPropertyName("hasTest")]          public bool HasTest { get; init; }
    [JsonPropertyName("foundTestFile")]    public string? FoundTestFile { get; init; }
    [JsonPropertyName("pullRequestId")]    public string? PullRequestId { get; init; }
}

public sealed class TraceabilityGapItem
{
    [JsonPropertyName("description")]        public string Description { get; init; } = string.Empty;
    [JsonPropertyName("relatedExternalId")]  public string? RelatedExternalId { get; init; }
    [JsonPropertyName("gapKind")]            public string GapKind { get; init; } = string.Empty;
}

public sealed class ImplementationTraceabilityReport
{
    [JsonPropertyName("tasks")]           public List<TaskImplementationEvidence> Tasks { get; init; } = [];
    [JsonPropertyName("unmappedChanges")] public List<ChangedFileEvidence> UnmappedChanges { get; init; } = [];
    [JsonPropertyName("testEvidence")]    public List<TestEvidenceItem> TestEvidence { get; init; } = [];
    [JsonPropertyName("gaps")]            public List<TraceabilityGapItem> Gaps { get; init; } = [];
    [JsonPropertyName("source")]          public string Source { get; init; } = string.Empty;
    [JsonPropertyName("statusMessage")]   public string? StatusMessage { get; init; }
}

public sealed class ProviderStatus
{
    [JsonPropertyName("configured")] public bool Configured { get; init; }
    [JsonPropertyName("usingMock")]  public bool UsingMock { get; init; }
    [JsonPropertyName("message")]    public string Message { get; init; } = string.Empty;
}
