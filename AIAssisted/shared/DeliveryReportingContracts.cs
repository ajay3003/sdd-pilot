using System.Text.Json.Serialization;

namespace BirkNext.DeliveryReporting;

// ── Delivery Reporting: weekly story/feature movement between two owned snapshots ─────────────────────────────────────────
// A standalone reporting area for Scrum Masters and Project Leaders. It owns its projects, its imported snapshots and its generated
// reports (PostgreSQL schema delivery_reporting) and never reads current BirkNext state (Project bindings, Target Environment, Test Data,
// Connected Systems, Source Analysis snapshots, review results). Snapshots are parsed once at import and never mutate; a generated report
// is frozen as one document, and every export renders that frozen document.
//   Task progress ≠ delivery status · all tasks checked ≠ Done · source changed ≠ progress · effective date ≠ import date.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliverySourceType { ZipUpload, GitArchive, OtherLocalArchive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliverySnapshotRole { Baseline, Current, AdHoc }

/// <summary>How completely a snapshot (or a report) could be read. Complete = every feature folder read; Partial = read with gaps (skipped
/// documents, unreadable specifications); Insufficient = nothing usable to report on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryCoverage { Complete, Partial, Insufficient }

/// <summary>
/// Management status of one feature in one snapshot. Task-derived values (NotStarted, InProgress, TasksComplete) come from tasks.md
/// checkboxes only. ReadyForTest, Testing, Blocked and Done need an explicit status line in the feature's own documents; NeedsClarification
/// needs open [NEEDS CLARIFICATION] markers in spec.md. TasksComplete is deliberately not Done.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryStatus { NotAssessed, NotStarted, InProgress, TasksComplete, ReadyForTest, Testing, Blocked, NeedsClarification, Done }

/// <summary>How a feature moved between baseline and current. Never derived from timestamps.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryMovement { NoSignificantChange, Progressed, Completed, NewThisPeriod, Removed, Regressed, NeedsReview }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryAttentionKind
{
    Blocked, NeedsClarification, SpecificationWithoutTasks, TasksDisappeared, TaskCompletionWentBackwards, AcceptanceCriteriaChanged,
    FeatureRemoved, AmbiguousFeatureIdentity, UnmappedArtifact,
}

/// <summary>Blocker = explicit blocked evidence only. Review = worth a look; never a blocker.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryAttentionSeverity { Blocker, Review }

public static class DeliveryReportingText
{
    public const string Title = "Delivery Reporting";

    public static string Status(DeliveryStatus status) => status switch
    {
        DeliveryStatus.NotAssessed => "Not assessed",
        DeliveryStatus.NotStarted => "Not started",
        DeliveryStatus.InProgress => "In progress",
        DeliveryStatus.TasksComplete => "Tasks complete",
        DeliveryStatus.ReadyForTest => "Ready for test",
        DeliveryStatus.Testing => "Testing",
        DeliveryStatus.Blocked => "Blocked",
        DeliveryStatus.NeedsClarification => "Needs clarification",
        DeliveryStatus.Done => "Done",
        _ => status.ToString(),
    };

    public static string Movement(DeliveryMovement movement) => movement switch
    {
        DeliveryMovement.NoSignificantChange => "No significant change",
        DeliveryMovement.Progressed => "Progressed",
        DeliveryMovement.Completed => "Completed",
        DeliveryMovement.NewThisPeriod => "New this period",
        DeliveryMovement.Removed => "Removed – needs review",
        DeliveryMovement.Regressed => "Regressed / blocked",
        DeliveryMovement.NeedsReview => "Needs review",
        _ => movement.ToString(),
    };

    public static string Role(DeliverySnapshotRole role) => role switch
    {
        DeliverySnapshotRole.AdHoc => "Ad hoc",
        _ => role.ToString(),
    };

    public static string Source(DeliverySourceType type) => type switch
    {
        DeliverySourceType.GitArchive => "Git archive",
        DeliverySourceType.ZipUpload => "ZIP import",
        _ => "Local archive",
    };

    public static string Attention(DeliveryAttentionKind kind) => kind switch
    {
        DeliveryAttentionKind.Blocked => "Blocked",
        DeliveryAttentionKind.NeedsClarification => "Needs clarification",
        DeliveryAttentionKind.SpecificationWithoutTasks => "Specification has no tasks",
        DeliveryAttentionKind.TasksDisappeared => "Tasks disappeared",
        DeliveryAttentionKind.TaskCompletionWentBackwards => "Task completion went backwards",
        DeliveryAttentionKind.AcceptanceCriteriaChanged => "Acceptance criteria changed",
        DeliveryAttentionKind.FeatureRemoved => "Feature removed",
        DeliveryAttentionKind.AmbiguousFeatureIdentity => "Ambiguous feature identity",
        DeliveryAttentionKind.UnmappedArtifact => "Unmapped task file",
        _ => kind.ToString(),
    };

    /// <summary>Statuses that only explicit evidence can produce (never task checkboxes alone).</summary>
    public static bool IsExplicitOnly(DeliveryStatus status) =>
        status is DeliveryStatus.ReadyForTest or DeliveryStatus.Testing or DeliveryStatus.Blocked or DeliveryStatus.Done;
}

// ── Projects ──────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record DeliveryProject(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int SnapshotCount, int ReportCount);

public sealed record CreateDeliveryProjectRequest(string Name, string? Description);

// ── Import ────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record DeliveryImportWarning(string Code, string Message, string? Path = null);

public sealed record DeliveryPreviewFeature(string Key, string Title, string Module, bool HasSpecification, bool HasTasks, int TaskTotal,
    int TaskCompleted, string Status);

/// <summary>What an uploaded archive holds before anything is stored. Nothing is persisted until the import is confirmed.</summary>
public sealed record DeliveryImportPreview
{
    public Guid StagingId { get; init; }
    public string FileName { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public DeliverySourceType SourceType { get; init; }
    public string? GitCommit { get; init; }
    /// <summary>A date the archive itself suggests (uniform entry times of a Git export). A suggestion only: the user sets the effective date.</summary>
    public DateTimeOffset? SuggestedEffectiveAt { get; init; }
    public string? SuggestedEffectiveReason { get; init; }
    public List<string> Modules { get; init; } = [];
    public List<DeliveryPreviewFeature> Features { get; init; } = [];
    public int TaskTotal { get; init; }
    public int TaskCompleted { get; init; }
    public int TaskOpen => TaskTotal - TaskCompleted;
    public DeliveryCoverage Coverage { get; init; }
    public List<DeliveryImportWarning> Warnings { get; init; } = [];
    /// <summary>Set when the selected project already holds this exact archive or Git commit. The import then needs explicit confirmation.</summary>
    public string? DuplicateWarning { get; init; }
}

public sealed record DeliveryImportCommitRequest
{
    public Guid ProjectId { get; init; }
    public DateTimeOffset EffectiveAt { get; init; }
    public DeliverySnapshotRole Role { get; init; } = DeliverySnapshotRole.AdHoc;
    public string? Label { get; init; }
    /// <summary>Required to import an archive or commit that the project already holds.</summary>
    public bool ConfirmDuplicate { get; init; }
}

public sealed record DeliveryImportCommitResult(DeliverySnapshotSummary? Snapshot, string? DuplicateWarning, string? Error);

// ── Snapshots ─────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record DeliverySnapshotSummary
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public string Label { get; init; } = "";
    public DateTimeOffset EffectiveAt { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public DeliverySnapshotRole Role { get; init; }
    public DeliverySourceType SourceType { get; init; }
    public string SourceFileName { get; init; } = "";
    public string? GitCommit { get; init; }
    public string Fingerprint { get; init; } = "";
    public DeliveryCoverage Coverage { get; init; }
    public int FeatureCount { get; init; }
    public int TaskCount { get; init; }
    public int CompletedTaskCount { get; init; }
    public int WarningsCount { get; init; }
    public int ReportCount { get; init; }
}

public sealed record DeliverySnapshotFeature(string Key, string Title, string Module, string Status, string StatusReason, int TaskTotal,
    int TaskCompleted, string? SpecPath, string? TasksPath);

public sealed record DeliverySnapshotDetail(DeliverySnapshotSummary Summary, List<string> Modules, List<DeliverySnapshotFeature> Features,
    List<DeliveryImportWarning> Warnings);

public sealed record UpdateDeliverySnapshotRequest(string? Label, DeliverySnapshotRole? Role);

/// <summary>Nearest earlier snapshot of the same project. <see cref="SnapshotId"/> is null when nothing fits or the choice is ambiguous.</summary>
public sealed record DeliveryBaselineSuggestion(Guid? SnapshotId, string Reason, bool Ambiguous, List<Guid> Candidates);

// ── Weekly report (frozen document) ───────────────────────────────────────────────────────────────────────────────────

public sealed record GenerateWeeklyReportRequest(Guid ProjectId, Guid BaselineSnapshotId, Guid CurrentSnapshotId);

public sealed record DeliveryReportSummaryRow(Guid Id, string Title, string WeekLabel, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    DateTimeOffset GeneratedAt, DeliveryCoverage Coverage, string BaselineLabel, string CurrentLabel, int StoriesChanged, int AttentionCount);

public sealed record DeliveryReportSnapshotRef
{
    public Guid SnapshotId { get; init; }
    public string Label { get; init; } = "";
    public DateTimeOffset EffectiveAt { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public DeliverySourceType SourceType { get; init; }
    public string SourceFileName { get; init; } = "";
    public string? GitCommit { get; init; }
    public string Fingerprint { get; init; } = "";
    public int FeatureCount { get; init; }
    public int TaskCount { get; init; }
    public int CompletedTaskCount { get; init; }
}

public sealed record DeliveryReportMetric(string Key, string Label, int Value, string? Hint = null);

/// <summary>Technical provenance of one story row. Shown under "Show evidence" and only in an export's evidence appendix.</summary>
public sealed record DeliveryStoryEvidence
{
    public string? SpecPath { get; init; }
    public string? TasksPath { get; init; }
    public string? PlanPath { get; init; }
    public string? BaselineStatusReason { get; init; }
    public string? CurrentStatusReason { get; init; }
    public List<string> AcceptanceAdded { get; init; } = [];
    public List<string> AcceptanceRemoved { get; init; } = [];
    public List<string> TasksCompleted { get; init; } = [];
    public List<string> TasksReopened { get; init; } = [];
    public List<string> TasksAdded { get; init; } = [];
    public List<string> TasksRemoved { get; init; } = [];
    /// <summary>Supporting only: source files of the feature's module that changed between the snapshots. Never a status.</summary>
    public string? SourceSummary { get; init; }
}

public sealed record DeliveryStoryRow
{
    public string FeatureKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Module { get; init; } = "";
    public DeliveryStatus? BaselineStatus { get; init; }
    public DeliveryStatus? CurrentStatus { get; init; }
    public DeliveryMovement Movement { get; init; }
    public int BaselineTaskCompleted { get; init; }
    public int BaselineTaskTotal { get; init; }
    public int CurrentTaskCompleted { get; init; }
    public int CurrentTaskTotal { get; init; }
    /// <summary>Change in completed-task count (current − baseline).</summary>
    public int TaskDelta { get; init; }
    /// <summary>Tasks open (or absent) in the baseline and checked in the current snapshot.</summary>
    public int TasksCompletedThisPeriod { get; init; }
    public bool SpecificationChanged { get; init; }
    public bool AcceptanceCriteriaChanged { get; init; }
    public bool NewFeature { get; init; }
    public bool RemovedFeature { get; init; }
    public bool HasActiveAttention { get; init; }
    /// <summary>Plain-language reasons for the movement ("task completion changed from 3/8 to 7/8").</summary>
    public List<string> Reasons { get; init; } = [];
    public List<string> Attention { get; init; } = [];
    public DeliveryStoryEvidence Evidence { get; init; } = new();

    [JsonIgnore] public bool Changed => Movement != DeliveryMovement.NoSignificantChange;
}

public sealed record DeliveryAttentionItem(DeliveryAttentionKind Kind, DeliveryAttentionSeverity Severity, string Title, string Detail, string? FeatureKey = null);

public sealed record DeliveryFocusItem(string Text, List<string> Features);

/// <summary>
/// The frozen weekly report. Generated once from two Delivery Reporting snapshots and stored as-is; viewing and exporting always render this
/// document, so a historical report never changes when snapshots are imported, relabelled or deleted later.
/// </summary>
public sealed record WeeklyDeliveryReport
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public Guid ReportId { get; init; }
    public Guid ProjectId { get; init; }
    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>ISO week of the current snapshot's effective date, e.g. 2026-W44.</summary>
    public string WeekLabel { get; init; } = "";
    public DateTimeOffset WeekStart { get; init; }
    public DateTimeOffset PeriodStart { get; init; }
    public DateTimeOffset PeriodEnd { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    /// <summary>Time zone the report's dates are shown in (frozen with the report, so exports never shift).</summary>
    public string TimeZone { get; init; } = "UTC";
    public DeliveryReportSnapshotRef Baseline { get; init; } = new();
    public DeliveryReportSnapshotRef Current { get; init; } = new();
    public DeliveryCoverage Coverage { get; init; }
    public List<string> CoverageNotes { get; init; } = [];
    /// <summary>True when at least one feature carries explicit status evidence, so Ready for test / Testing / Blocked counts are meaningful.</summary>
    public bool HasExplicitStatusEvidence { get; init; }
    public List<DeliveryReportMetric> Metrics { get; init; } = [];
    public List<string> ExecutiveSummary { get; init; } = [];
    public List<DeliveryStoryRow> Stories { get; init; } = [];
    public List<DeliveryAttentionItem> Attention { get; init; } = [];
    public List<DeliveryFocusItem> NextWeekFocus { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryExportFormat { Markdown, Html }

public sealed record DeliveryReportExport(string FileName, string ContentType, string Content);

/// <summary>Story selection shared by the page and the exports: by default only stories that changed or carry active attention.</summary>
public static class DeliveryReportStories
{
    public static bool IsDefaultVisible(DeliveryStoryRow story) => story.Changed || story.HasActiveAttention;

    public static IReadOnlyList<DeliveryStoryRow> Select(WeeklyDeliveryReport report, bool includeUnchanged) =>
        includeUnchanged ? report.Stories : report.Stories.Where(IsDefaultVisible).ToList();

    public static string Progress(int completed, int total) => total == 0 ? "–" : $"{completed}/{total}";

    public static string ProgressChange(DeliveryStoryRow story) =>
        story.NewFeature ? $"new · {Progress(story.CurrentTaskCompleted, story.CurrentTaskTotal)}"
        : story.RemovedFeature ? $"{Progress(story.BaselineTaskCompleted, story.BaselineTaskTotal)} → removed"
        : $"{Progress(story.BaselineTaskCompleted, story.BaselineTaskTotal)} → {Progress(story.CurrentTaskCompleted, story.CurrentTaskTotal)}";

    public static string StatusOrDash(DeliveryStatus? status) => status is { } s ? DeliveryReportingText.Status(s) : "–";
}
