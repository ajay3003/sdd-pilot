namespace BirkNext.Api.Data.DeliveryReporting;

// Entities of the delivery_reporting schema. Nothing here references a BirkNext operational table: a reporting project is its own identity,
// and every snapshot holds its own parsed content. Enum-valued columns are stored as text.

public sealed class DeliveryProjectRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Optional provenance reference to something outside Delivery Reporting. Never read to build snapshots or reports.</summary>
    public string? ExternalReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One imported archive: metadata and fingerprint only. The archive bytes are not retained (see DeliveryReportingImportService).</summary>
public sealed class DeliveryReportSourceRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string SourceType { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; }
    public string Fingerprint { get; set; } = "";
    public string? GitCommit { get; set; }
    public string? GitBranch { get; set; }
    public long ArchiveBytes { get; set; }
    public long EntryCount { get; set; }
    public string MetadataJson { get; set; } = "{}";
}

public sealed class DeliverySnapshotRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ReportSourceId { get; set; }
    public string Label { get; set; } = "";
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public string Role { get; set; } = "";
    public string CoverageStatus { get; set; } = "";
    public string ParseStatus { get; set; } = "";
    public int WarningsCount { get; set; }
    public int FeatureCount { get; set; }
    public int TaskCount { get; set; }
    public int CompletedTaskCount { get; set; }
    public string ModulesJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
}

public sealed class DeliveryFeatureSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public string FeatureKey { get; set; } = "";
    public string Module { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Title { get; set; } = "";
    public int Position { get; set; }
    public string? SpecPath { get; set; }
    public string? TasksPath { get; set; }
    public string? PlanPath { get; set; }
    public string? SpecFingerprint { get; set; }
    public string? AcceptanceFingerprint { get; set; }
    public string AcceptanceCriteriaJson { get; set; } = "[]";
    public int RequirementCount { get; set; }
    public int UserStoryCount { get; set; }
    public int OpenClarifications { get; set; }
    public int TaskTotal { get; set; }
    public int TaskCompleted { get; set; }
    public int UntrackedTaskCount { get; set; }
    public string Status { get; set; } = "";
    public string StatusReason { get; set; } = "";
}

public sealed class DeliveryTaskSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid FeatureSnapshotId { get; set; }
    public string TaskKey { get; set; } = "";
    public string? TaskId { get; set; }
    public string Text { get; set; } = "";
    public bool Completed { get; set; }
    public string? Phase { get; set; }
    public string? UserStory { get; set; }
    public int Position { get; set; }
    public string SourcePath { get; set; } = "";
}

/// <summary>One file of a snapshot: the Markdown documents and readable source files, by path and content fingerprint (never content).</summary>
public sealed class DeliveryArtifactSnapshotRecord
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public string Path { get; set; } = "";
    public string Module { get; set; } = "";
    public string? FeatureKey { get; set; }
    public string Kind { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

/// <summary>Explicit evidence behind a feature's status: a status line or an open clarification marker, with its location.</summary>
public sealed class DeliveryEvidenceReferenceRecord
{
    public Guid Id { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid FeatureSnapshotId { get; set; }
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>A baseline/current comparison, owned by the report that produced it. Snapshot ids become null if a snapshot is deleted later; the
/// report keeps its frozen content.</summary>
public sealed class DeliverySnapshotComparisonRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? BaselineSnapshotId { get; set; }
    public Guid? CurrentSnapshotId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int FeaturesAdded { get; set; }
    public int FeaturesRemoved { get; set; }
    public int FeaturesChanged { get; set; }
}

public sealed class DeliveryFeatureDeltaRecord
{
    public Guid Id { get; set; }
    public Guid ComparisonId { get; set; }
    public string FeatureKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Module { get; set; } = "";
    public string? BaselineStatus { get; set; }
    public string? CurrentStatus { get; set; }
    public int BaselineTaskCompleted { get; set; }
    public int BaselineTaskTotal { get; set; }
    public int CurrentTaskCompleted { get; set; }
    public int CurrentTaskTotal { get; set; }
    public int TaskDelta { get; set; }
    public bool SpecificationChanged { get; set; }
    public bool AcceptanceCriteriaChanged { get; set; }
    public bool NewFeature { get; set; }
    public bool RemovedFeature { get; set; }
    public string DeliveryMovement { get; set; } = "";
    public string ReasonsJson { get; set; } = "[]";
}

/// <summary>A discrete change found by a comparison (task checked/reopened, status change, feature added/removed).</summary>
public sealed class DeliveryStatusEventRecord
{
    public Guid Id { get; set; }
    public Guid ComparisonId { get; set; }
    public string FeatureKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? FromValue { get; set; }
    public string? ToValue { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class DeliveryWeeklyReportRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ComparisonId { get; set; }
    public Guid? BaselineSnapshotId { get; set; }
    public Guid? CurrentSnapshotId { get; set; }
    public string Title { get; set; } = "";
    public string WeekLabel { get; set; } = "";
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public string CoverageStatus { get; set; } = "";
    public int ContentVersion { get; set; }
    /// <summary>The frozen <c>WeeklyDeliveryReport</c> document. Views and exports render only this.</summary>
    public string ContentJson { get; set; } = "{}";
}

public sealed class DeliveryWeeklyReportItemRecord
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public string Section { get; set; } = "";
    public int Position { get; set; }
    public string? FeatureKey { get; set; }
    public string Title { get; set; } = "";
    public string? Movement { get; set; }
    public string? Kind { get; set; }
    public string Text { get; set; } = "";
}
