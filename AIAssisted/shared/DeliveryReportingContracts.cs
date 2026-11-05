using System.Text.Json.Serialization;

namespace BirkNext.DeliveryReporting;

// ── Delivery Reporting: weekly story/feature movement between two owned snapshots ─────────────────────────────────────────
// A standalone reporting area for Scrum Masters and Project Leaders. It owns its projects, its imported snapshots and its generated
// reports (PostgreSQL schema delivery_reporting) and never reads current BirkNext state (Project bindings, Target Environment, Test Data,
// Connected Systems, Source Analysis snapshots, review results). Snapshots are parsed once at import and never mutate; a generated report
// is frozen as one document, and every export renders that frozen document.
//   Task progress ≠ delivery status · all tasks checked ≠ Done · source changed ≠ progress · effective date ≠ import date.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliverySourceType { ZipUpload, GitArchive, OtherLocalArchive, AzureDevOps }

/// <summary>
/// Safe provenance of a snapshot imported from Azure DevOps: where the repository state came from and which commit was resolved. Never a credential.
/// <see cref="RequestedAt"/> is the state point the user asked for (it drives the reporting period); <see cref="CommitAt"/> is when the resolved commit
/// was made — the latest commit on the branch at or before the requested time, so that tree IS the repository state at the requested time.
/// </summary>
public sealed record DeliveryAzureDevOpsSource(string Organization, string Project, string Repository, string Branch, string CommitId, DateTimeOffset CommitAt)
{
    public DateTimeOffset? RequestedAt { get; init; }
    public string? CommitAuthor { get; init; }
    public string? CommitMessage { get; init; }
    public string ShortCommit => CommitId.Length > 7 ? CommitId[..7] : CommitId;
}

// ── Azure DevOps (read-only source for Delivery Reporting snapshots) ──

/// <summary>A configured Azure DevOps connection. The PAT is stored encrypted on the backend and never returned: only whether one is configured.</summary>
public sealed record AzureDevOpsConnectionView(Guid Id, string DisplayName, string Organization, bool CredentialConfigured, string? LastValidationStatus, DateTimeOffset? LastValidatedAt);

/// <summary>Create/update a connection. <see cref="Pat"/> null/empty on update keeps the stored credential.</summary>
public sealed record AzureDevOpsConnectionRequest(string DisplayName, string Organization, string? Pat);

public sealed record AzureDevOpsValidationResult(bool Success, string Status, string Message);

public sealed record AzureDevOpsProjectItem(string Id, string Name);

public sealed record AzureDevOpsRepositoryItem(string Id, string Name, string? DefaultBranch);

/// <summary>A branch by its friendly name ("main", "release/1.2"); the full ref only as advanced detail.</summary>
public sealed record AzureDevOpsBranchItem(string Name, string Ref);

public sealed record AzureDevOpsList<T>(List<T> Items, string? Error);

/// <summary>Resolve (and preview) the repository state at <see cref="RequestedAt"/> on a branch. Null RequestedAt = the branch head now ("Current/latest").</summary>
public sealed record AzureDevOpsResolveRequest(Guid ConnectionId, string Project, string Repository, string Branch, DateTimeOffset? RequestedAt)
{
    public Guid? ProjectId { get; init; }
}

public sealed record AzureDevOpsResolveResult(DeliveryAzureDevOpsSource? Commit, string? Note, string? Error);

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
    FeatureRemoved, AmbiguousFeatureIdentity, UnmappedArtifact, BrokenTraceability,
    /// <summary>Tasks reference a story the specification marks removed.</summary>
    RemovedStoryReference,
    /// <summary>A source document already contains encoding damage (shown as written, never repaired).</summary>
    SourceEncoding,
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
        DeliverySourceType.AzureDevOps => "Azure DevOps",
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
        DeliveryAttentionKind.BrokenTraceability => "Unresolved story reference",
        DeliveryAttentionKind.RemovedStoryReference => "References removed story",
        DeliveryAttentionKind.SourceEncoding => "Source text encoding",
        _ => kind.ToString(),
    };

    public static string Change(DeliveryItemChange change) => change switch
    {
        DeliveryItemChange.Added => "Added",
        DeliveryItemChange.Removed => "Removed",
        DeliveryItemChange.Changed => "Changed",
        _ => "Unchanged",
    };

    public static string TaskChange(DeliveryTaskChange change) => change switch
    {
        DeliveryTaskChange.CompletedThisPeriod => "Completed this period",
        DeliveryTaskChange.New => "New",
        DeliveryTaskChange.Removed => "Removed",
        DeliveryTaskChange.TextChanged => "Changed",
        DeliveryTaskChange.Reopened => "Reopened",
        _ => "Unchanged",
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
    int TaskCompleted, string Status)
{
    public int PhaseCount { get; init; }
    public int StoryCount { get; init; }
    public int CriteriaCount { get; init; }
}

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
    /// <summary>Explicit hierarchy detected (phases from "Phase N" task groups, user story sections, acceptance criteria). A feature without
    /// user stories is valid; warnings say so without blocking the import.</summary>
    public int PhaseCount { get; init; }
    public int StoryCount { get; init; }
    public int CriteriaCount { get; init; }
    public DeliveryCoverage Coverage { get; init; }
    public List<DeliveryImportWarning> Warnings { get; init; } = [];
    /// <summary>Set when the selected project already holds this exact archive or Git commit. The import then needs explicit confirmation.</summary>
    public string? DuplicateWarning { get; init; }
    /// <summary>Azure DevOps provenance when the archive came from a resolved commit (null for an uploaded ZIP).</summary>
    public DeliveryAzureDevOpsSource? AzureDevOps { get; init; }
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
    public DeliveryAzureDevOpsSource? AzureDevOps { get; init; }
    /// <summary>Where the effective date comes from: "Entered at import", "Archive timestamps (Git export)", "Azure DevOps commit", "Corrected".</summary>
    public string EffectiveAtProvenance { get; init; } = "Entered at import";
    public List<DeliveryEffectiveDateCorrection> EffectiveAtCorrections { get; init; } = [];
}

public sealed record DeliverySnapshotFeature(string Key, string Title, string Module, string Status, string StatusReason, int TaskTotal,
    int TaskCompleted, string? SpecPath, string? TasksPath);

public sealed record DeliverySnapshotDetail(DeliverySnapshotSummary Summary, List<string> Modules, List<DeliverySnapshotFeature> Features,
    List<DeliveryImportWarning> Warnings);

/// <summary>Label/role edits, and an explicit effective-date correction (with a reason): the previous value is kept in the snapshot's audit trail.</summary>
public sealed record UpdateDeliverySnapshotRequest(string? Label, DeliverySnapshotRole? Role, DateTimeOffset? EffectiveAt = null, string? EffectiveAtReason = null);

/// <summary>One correction of a snapshot's effective date: never silent, never lost.</summary>
public sealed record DeliveryEffectiveDateCorrection(DateTimeOffset Previous, DateTimeOffset Corrected, DateTimeOffset CorrectedAt, string? Reason);

/// <summary>Nearest earlier snapshot of the same project. <see cref="SnapshotId"/> is null when nothing fits or the choice is ambiguous.</summary>
public sealed record DeliveryBaselineSuggestion(Guid? SnapshotId, string Reason, bool Ambiguous, List<Guid> Candidates);

// ── Weekly report (frozen document) ───────────────────────────────────────────────────────────────────────────────────

public sealed record GenerateWeeklyReportRequest(Guid ProjectId, Guid BaselineSnapshotId, Guid CurrentSnapshotId)
{
    /// <summary>Explicit confirmation that the direction is right when the comparison preview asks for it (e.g. the current snapshot holds fewer
    /// features and tasks). Recorded in the frozen report; never inferred.</summary>
    public bool ConfirmDirection { get; init; }
    /// <summary>Optional Source (stable source id) and Functional scope (stable scope id) the report covers; both null = the whole comparison.</summary>
    public string? SourceId { get; init; }
    public string? FunctionalScopeId { get; init; }
    /// <summary>Optional work type / quality category the report covers (shared feature classification).</summary>
    public DeliveryWorkType? WorkType { get; init; }
    public QualityCategory? Quality { get; init; }
}

/// <summary>Full-period: the baseline covers the reporting week. Partial-period: part of it. Same-day: both snapshots on one day — a change report,
/// never a week of delivery. Unknown: dates cannot support a coverage claim.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryPeriodCoverage { FullPeriod, PartialPeriod, SameDay, Unknown }

/// <summary>Chronological = baseline earlier, inventories plausible. SameEffectiveDate = both on one day (order unknowable from dates).
/// InventoryReduced = current holds fewer features/tasks than baseline: scope reduction, rollback or reversed snapshots — needs review, never
/// auto-reversed. BaselineLater = dates are inverted (swap).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryDirectionState { Chronological, SameEffectiveDate, InventoryReduced, BaselineLater }

public sealed record DeliveryInventory(int Features, int Tasks, int CheckedTasks);

/// <summary>Shown before a report is generated: Baseline → Current inventories, deltas, direction and coverage. Nothing is stored.</summary>
public sealed record DeliveryComparisonPreview
{
    public DeliveryReportSnapshotRef Baseline { get; init; } = new();
    public DeliveryReportSnapshotRef Current { get; init; } = new();
    public DeliveryInventory BaselineInventory { get; init; } = new(0, 0, 0);
    public DeliveryInventory CurrentInventory { get; init; } = new(0, 0, 0);
    public int FeatureDelta => CurrentInventory.Features - BaselineInventory.Features;
    public int TaskDelta => CurrentInventory.Tasks - BaselineInventory.Tasks;
    public int CheckedDelta => CurrentInventory.CheckedTasks - BaselineInventory.CheckedTasks;
    public List<string> MissingFeatures { get; init; } = [];
    public List<string> NewFeatures { get; init; } = [];
    public DeliveryDirectionState Direction { get; init; }
    public List<string> Warnings { get; init; } = [];
    /// <summary>Generation needs <see cref="GenerateWeeklyReportRequest.ConfirmDirection"/>.</summary>
    public bool RequiresConfirmation { get; init; }
    /// <summary>False only for an impossible pair (same snapshot, inverted dates: swap them instead).</summary>
    public bool CanGenerate { get; init; }
    public DeliveryPeriodCoverage PeriodCoverage { get; init; }
    public string CoverageText { get; init; } = "";
    public DeliveryReportKind Kind { get; init; }
}

/// <summary>The direction decision frozen with a report: what was checked, what was found, and whether it was explicitly confirmed.</summary>
public sealed record DeliveryDirectionReview
{
    public DeliveryDirectionState State { get; init; }
    public bool Confirmed { get; init; }
    public DeliveryInventory BaselineInventory { get; init; } = new(0, 0, 0);
    public DeliveryInventory CurrentInventory { get; init; } = new(0, 0, 0);
    public List<string> Warnings { get; init; } = [];
}

public sealed record DeliveryReportSummaryRow(Guid Id, string Title, string WeekLabel, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    DateTimeOffset GeneratedAt, DeliveryCoverage Coverage, string BaselineLabel, string CurrentLabel, int StoriesChanged, int AttentionCount)
{
    /// <summary>The frozen report's own display title (coverage-aware from v3).</summary>
    public string DisplayTitle { get; init; } = "";
    public string CoverageText { get; init; } = "";
    /// <summary>Exact source identities of the compared pair: archive file name and content fingerprint (short).</summary>
    public string BaselineSource { get; init; } = "";
    public string CurrentSource { get; init; } = "";
    public DateTimeOffset? BaselineEffectiveAt { get; init; }
    public DateTimeOffset? CurrentEffectiveAt { get; init; }
    /// <summary>Baseline/current snapshot ids: two reports of one week with different pairs are different comparisons, not versions of one.</summary>
    public string PairKey { get; init; } = "";
    public DeliveryDirectionState? Direction { get; init; }
    public bool DirectionConfirmed { get; init; }
    public int CalculationVersion { get; init; }
    /// <summary>Source / Functional scope the report was generated for ("M2LB-language · ADR-029: Translate English"); empty = whole comparison.</summary>
    public string ScopeLabel { get; init; } = "";
}

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DeliveryAzureDevOpsSource? AzureDevOps { get; init; }
}

/// <summary>A report figure in one of three groups that are never mixed: "period" (changes between the snapshots), "inventory" (the current
/// snapshot's totals) and "quality" (review and traceability signals).</summary>
public sealed record DeliveryReportMetric(string Key, string Label, int Value, string? Hint = null)
{
    public string Group { get; init; } = DeliveryMetricGroups.Period;
}

public static class DeliveryMetricGroups
{
    public const string Period = "period", Inventory = "inventory", Quality = "quality";

    public static string Label(string group) => group switch
    {
        Inventory => "Current inventory",
        Quality => "Quality and review",
        _ => "Period changes",
    };
}

/// <summary>Three different kinds of signal, never one "needs attention" number: a scope change to confirm is not a defect, a traceability gap is
/// a data-quality issue, and blocked/clarification/reopened work is a delivery signal.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryAttentionCategory { ScopeChange, DataQuality, DeliverySignal }

public static class DeliveryAttentionCategories
{
    public static DeliveryAttentionCategory Of(DeliveryAttentionKind kind) => kind switch
    {
        DeliveryAttentionKind.TasksDisappeared or DeliveryAttentionKind.AcceptanceCriteriaChanged or DeliveryAttentionKind.FeatureRemoved => DeliveryAttentionCategory.ScopeChange,
        DeliveryAttentionKind.SpecificationWithoutTasks or DeliveryAttentionKind.AmbiguousFeatureIdentity or DeliveryAttentionKind.UnmappedArtifact
            or DeliveryAttentionKind.BrokenTraceability or DeliveryAttentionKind.RemovedStoryReference or DeliveryAttentionKind.SourceEncoding => DeliveryAttentionCategory.DataQuality,
        _ => DeliveryAttentionCategory.DeliverySignal,
    };

    public static string Label(DeliveryAttentionCategory category) => category switch
    {
        DeliveryAttentionCategory.ScopeChange => "Scope and change review",
        DeliveryAttentionCategory.DataQuality => "Data quality and traceability",
        _ => "Delivery status signals",
    };
}

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
    /// <summary>Absent from a current snapshot that was not read completely: missing, not a confirmed removal (review required).</summary>
    public bool RemovalUnconfirmed { get; init; }
    public bool HasActiveAttention { get; init; }
    /// <summary>Plain-language reasons for the movement ("task completion changed from 3/8 to 7/8").</summary>
    public List<string> Reasons { get; init; } = [];
    public List<string> Attention { get; init; } = [];
    public DeliveryStoryEvidence Evidence { get; init; } = new();
    /// <summary>Phases, user stories, acceptance criteria and traceable tasks with their change this period (report v2). Null in v1 reports.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DeliveryFeatureHierarchy? Hierarchy { get; init; }

    [JsonIgnore] public bool Changed => Movement != DeliveryMovement.NoSignificantChange;
}

/// <summary>How an item changed between baseline and current.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryItemChange { Unchanged, Added, Removed, Changed }

/// <summary>A task's change: only a matched baseline-open → current-checked task is CompletedThisPeriod; a new checked task is New.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryTaskChange { Unchanged, CompletedThisPeriod, New, Removed, TextChanged, Reopened }

public sealed record DeliveryCriterionRow(string Key, string Text, DeliveryItemChange Change)
{
    /// <summary>The baseline text when the criterion changed (shown only then).</summary>
    public string? BaselineText { get; init; }
    public string? StoryKey { get; init; }
}

/// <summary>
/// How a task relates to the requirement model of its feature. Only <see cref="ExplicitStory"/> is a formal User Story link; the others say
/// exactly what the source supports. Shared &amp; foundational work and functional/scenario groups are normal structures, not traceability defects;
/// only <see cref="RemovedStoryReference"/> and <see cref="UnresolvedReference"/> need review.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryTaskRelationship
{
    /// <summary>[USn] names a User Story defined in spec.md.</summary>
    ExplicitStory,
    /// <summary>No tag, but the task sits in a tasks.md phase titled for a story ("Phase 3: User Story 1 …").</summary>
    StoryPhase,
    /// <summary>[USn] groups tasks, but the feature has no spec.md user stories (or no spec.md): a functional group derived from tasks.md.</summary>
    FunctionalGroup,
    /// <summary>[USn] groups tasks in a feature whose spec.md is written as scenarios, not user stories.</summary>
    ScenarioGroup,
    /// <summary>No story tag outside story phases (setup, foundational, polish, cross-cutting): not owned by one story.</summary>
    SharedFoundational,
    /// <summary>[USn] names a story the specification explicitly marks removed.</summary>
    RemovedStoryReference,
    /// <summary>[USn] names no story of the specification, or is not a single story reference.</summary>
    UnresolvedReference,
}

/// <summary>How firmly a task is linked to its group: an explicit tag, a phase title naming it, group-level only, or not at all.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryMappingConfidence { Explicit, PhaseTitle, GroupLevel, None }

/// <summary>
/// One requirement-model group of a feature with its tasks: a User Story, a functional group, a scenario group, shared &amp; foundational work, removed
/// story references or unresolved references. Explanation and provenance say why BirkNext created it.
/// </summary>
public sealed record DeliveryWorkGroupRow(string Key, DeliveryTaskRelationship Kind, string Title)
{
    public string Explanation { get; init; } = "";
    /// <summary>Where the grouping comes from ("tasks.md [US1] tags", "spec.md User Story 2").</summary>
    public string Provenance { get; init; } = "";
    public DeliveryMappingConfidence Confidence { get; init; }
    /// <summary>The story or scenario this group is linked to, when the source supports it.</summary>
    public string? LinkedKey { get; init; }
    public int TaskTotal { get; init; }
    public int TaskCompleted { get; init; }
    /// <summary>Removed or unresolved references: a person should look at them.</summary>
    public bool NeedsReview { get; init; }
}

public sealed record DeliveryTaskRow(string Key, string? TaskId, string Text, bool Completed, DeliveryTaskChange Change)
{
    /// <summary>The task's relationship to the requirement model (report v4; null in reports frozen before it).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DeliveryTaskRelationship? Relationship { get; init; }
    /// <summary>The group the task belongs to (US1, S4, shared); null for shared work.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GroupKey { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DeliveryMappingConfidence? MappingConfidence { get; init; }
    /// <summary>Why the task has this relationship, in plain language.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RelationshipNote { get; init; }
    /// <summary>The shared interpretation rule that produced the relationship (WI-EXPLICIT, WI-SHARED, …); null in reports frozen before it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RelationshipRule { get; init; }
    public string? PhaseKey { get; init; }
    /// <summary>Only an explicit [USn] reference to an existing story; null = feature-level.</summary>
    public string? UserStoryKey { get; init; }
    public string? SourcePath { get; init; }
    /// <summary>The raw [USn] reference when it names no story in the specification ("Parent story unresolved"); never repaired by guessing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? UnresolvedStoryReference { get; init; }
    /// <summary>The source marks the task [P] (parallelisable).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Parallel { get; init; }
    /// <summary>Completion state in the baseline (null when the task is new).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BaselineCompleted { get; init; }
}

/// <summary>A feature-level functional requirement (FR-001 …). Kept separate from acceptance criteria; linked to a story only when the source does.</summary>
public sealed record DeliveryRequirementRow(string Key, string Text, DeliveryItemChange Change);

public sealed record DeliveryUserStoryRow(string Key, string Title, DeliveryItemChange Change)
{
    /// <summary>"UserStory" or "Scenario" — a scenario-based specification is never presented as User Stories.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Kind { get; init; }
    /// <summary>The specification marks this story removed (struck-through heading or an explicit "Removed" marker).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Removed { get; init; }
    public string? Description { get; init; }
    /// <summary>Only when the heading declares it ("Priority: P1"); never invented.</summary>
    public string? Priority { get; init; }
    public List<string> PhaseKeys { get; init; } = [];
    public List<DeliveryCriterionRow> Criteria { get; init; } = [];
    public int TaskTotal { get; init; }
    public int TaskCompleted { get; init; }
    public bool TextChanged { get; init; }
    public bool CriteriaChanged { get; init; }
    public int SourceLine { get; init; }
}

/// <summary>An explicit implementation phase. Not a status: delivery state is derived separately.</summary>
public sealed record DeliveryPhaseRow(string Key, string Title, DeliveryItemChange Change)
{
    public int Position { get; init; }
    public int TaskTotal { get; init; }
    public int TaskCompleted { get; init; }
    public List<string> StoryKeys { get; init; } = [];
    /// <summary>Task changes inside the phase this period (new, removed, completed, reopened, text changed).</summary>
    public int Changes { get; init; }
}

/// <summary>
/// The feature's hierarchy for the report: Feature → optional Phase → User Story → Acceptance Criteria, with traceable tasks. For a removed feature
/// it is the baseline state, for a new feature the current state. <see cref="Captured"/> false = a snapshot imported before hierarchy support.
/// </summary>
public sealed record DeliveryFeatureHierarchy
{
    public bool Captured { get; init; }
    public List<DeliveryPhaseRow> Phases { get; init; } = [];
    public List<DeliveryUserStoryRow> Stories { get; init; } = [];
    /// <summary>Criteria no story owns (kept feature-level, never duplicated under stories).</summary>
    public List<DeliveryCriterionRow> FeatureCriteria { get; init; } = [];
    public List<DeliveryTaskRow> Tasks { get; init; } = [];
    /// <summary>Functional requirements (feature-level). Empty in reports frozen before v3.</summary>
    public List<DeliveryRequirementRow> FunctionalRequirements { get; init; } = [];
    /// <summary>The feature's requirement-model groups with their tasks (report v4). Empty in older reports.</summary>
    public List<DeliveryWorkGroupRow> Groups { get; init; } = [];
    /// <summary>"User stories", "Scenarios", "Functional groups", "No specification" or "No task list" (report v4).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RequirementModel { get; init; }
    /// <summary>False when the feature has no tasks.md: progress cannot be derived from tasks (never "0/0").</summary>
    public bool HasTaskList { get; init; } = true;
    public int CriteriaBaseline { get; init; }
    public int CriteriaCurrent { get; init; }
    public int CriteriaAdded { get; init; }
    public int CriteriaChanged { get; init; }
    public int CriteriaRemoved { get; init; }
    public int StoriesAdded { get; init; }
    public int StoriesRemoved { get; init; }
    public int StoriesChanged { get; init; }
    public int TasksAdded { get; init; }
    public int TasksRemoved { get; init; }
    public int TasksReopened { get; init; }
    public int TasksTextChanged { get; init; }
}

/// <summary>A feature added to or removed from scope between baseline and current (scope change — not a quality problem).</summary>
public sealed record DeliveryScopeChange(string Kind, string FeatureKey, string Title, string Module, string Detail);

public sealed record DeliveryAttentionItem(DeliveryAttentionKind Kind, DeliveryAttentionSeverity Severity, string Title, string Detail, string? FeatureKey = null);

public sealed record DeliveryFocusItem(string Text, List<string> Features);

/// <summary>
/// The frozen weekly report. Generated once from two Delivery Reporting snapshots and stored as-is; viewing and exporting always render this
/// document, so a historical report never changes when snapshots are imported, relabelled or deleted later.
/// </summary>
public sealed record WeeklyDeliveryReport
{
    /// <summary>2: feature hierarchy (phases, user stories, acceptance criteria, task changes), scope changes, inventory metrics. v1 reports keep
    /// rendering as they were frozen.</summary>
    /// <summary>4: task relationships (explicit story / story phase / functional group / scenario group / shared &amp; foundational / removed /
    /// unresolved) and requirement-model groups.</summary>
    public const int CurrentVersion = 4;
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
    /// <summary>Shown as "Potential focus": observations only, no invented priority.</summary>
    public List<DeliveryFocusItem> NextWeekFocus { get; init; } = [];
    /// <summary>Features added to / removed from scope (v2). Separate from Needs attention.</summary>
    public List<DeliveryScopeChange> ScopeChanges { get; init; } = [];
    /// <summary>Weekly only when the baseline covers the reporting week; any other interval (same day, a few days, several weeks) is a
    /// Delivery Change Report. Reports frozen before this existed read as Weekly.</summary>
    public DeliveryReportKind Kind { get; init; }
    /// <summary>Comparison-period problems found at generation (same day, identical content, unknown revisions …). Frozen with the report.</summary>
    public List<string> ComparisonWarnings { get; init; } = [];
    /// <summary>Version of the delivery calculation rules the report was generated with (two reports of one period can differ by it).</summary>
    public int CalculationVersion { get; init; } = 1;
    /// <summary>v3: how much of the reporting period the evidence covers, in words ("Coverage: 30 Oct only").</summary>
    public DeliveryPeriodCoverage PeriodCoverage { get; init; } = DeliveryPeriodCoverage.Unknown;
    public string CoverageText { get; init; } = "";
    /// <summary>v3: the direction check and its explicit confirmation, frozen with the report.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DeliveryDirectionReview? Direction { get; init; }
    /// <summary>Version of the source parser that produced the snapshots' hierarchy (reproducibility).</summary>
    public int ParserVersion { get; init; }
    /// <summary>The work-item interpretation rule set the report was classified with (BirkNext.Interpretation.WorkItemInterpreter.RuleSetVersion);
    /// null in reports frozen before the shared engine. A frozen report is never reclassified with a newer rule set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? InterpretationRuleSet { get; init; }
    /// <summary>The quality-classification rule set (FunctionalQualityClassifier.RuleSetVersion) in force when the report was frozen; null in reports
    /// frozen before it was recorded, which are viewed with the legacy rules (no Integration category) — never silently reclassified.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? QualityRuleSet { get; init; }
    /// <summary>Set when the report covers one Source and/or Functional scope only (exact ids, frozen); null = the whole comparison.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DeliveryReportScope? Scope { get; init; }
}

public static class DeliveryReportTitles
{
    /// <summary>
    /// The title as shown and exported. v3 never implies more temporal coverage than the evidence supports: "Weekly Delivery Status — W44"
    /// only for full-period coverage; "Partial weekly report — W44"; same-day comparisons are change reports for that day. Older reports keep
    /// their frozen "Title — WeekLabel".
    /// </summary>
    public static string Display(WeeklyDeliveryReport r)
    {
        if (r.Version < 3) return $"{r.Title} — {r.WeekLabel}";
        var week = r.WeekLabel.Contains("-W", StringComparison.Ordinal) ? r.WeekLabel[(r.WeekLabel.IndexOf("-W", StringComparison.Ordinal) + 1)..] : r.WeekLabel;
        return r.PeriodCoverage switch
        {
            DeliveryPeriodCoverage.FullPeriod when r.Kind == DeliveryReportKind.Weekly => $"{r.ProjectName} Weekly Delivery Status — {week}",
            DeliveryPeriodCoverage.SameDay => $"{r.ProjectName} Delivery Change Report — same-day comparison ({week})",
            DeliveryPeriodCoverage.PartialPeriod or DeliveryPeriodCoverage.FullPeriod => $"{r.ProjectName} Partial weekly report — {week}",
            _ => $"{r.ProjectName} Delivery Change Report — {week}",
        };
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryReportKind { Weekly, Change }

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

    /// <summary>"3 stories · 1 story added · 1 acceptance criterion changed" — the feature's hierarchy changes this period (empty when not captured).</summary>
    public static string HierarchySummary(DeliveryStoryRow row)
    {
        if (row.Hierarchy is not { Captured: true } h) return "";
        var parts = new List<string> { h.Stories.Count == 0 ? "no explicit user stories" : $"{h.Stories.Count} {(h.Stories.Count == 1 ? "story" : "stories")}" };
        if (h.Phases.Count > 0) parts.Add($"{h.Phases.Count} phase{(h.Phases.Count == 1 ? "" : "s")}");
        if (row.NewFeature || row.RemovedFeature) return string.Join(" · ", parts);
        void Add(int n, string what) { if (n > 0) parts.Add($"{n} {what}"); }
        Add(h.StoriesAdded, h.StoriesAdded == 1 ? "story added" : "stories added");
        Add(h.StoriesRemoved, h.StoriesRemoved == 1 ? "story removed" : "stories removed");
        Add(h.StoriesChanged, h.StoriesChanged == 1 ? "story changed" : "stories changed");
        Add(h.CriteriaAdded, h.CriteriaAdded == 1 ? "acceptance criterion added" : "acceptance criteria added");
        Add(h.CriteriaChanged, h.CriteriaChanged == 1 ? "acceptance criterion changed" : "acceptance criteria changed");
        Add(h.CriteriaRemoved, h.CriteriaRemoved == 1 ? "acceptance criterion removed" : "acceptance criteria removed");
        Add(h.TasksAdded, h.TasksAdded == 1 ? "task added" : "tasks added");
        Add(h.TasksRemoved, h.TasksRemoved == 1 ? "task removed" : "tasks removed");
        return string.Join(" · ", parts);
    }

    /// <summary>"Delivery status signals" (v3), "Potential focus" (v2) or the frozen v1 heading.</summary>
    public static string FocusHeading(WeeklyDeliveryReport report) => report.Version >= 3 ? "Delivery status signals" : report.Version >= 2 ? "Potential focus" : "Next-week focus";

    public const string FocusNote = "Based on task evidence only — not project priority. A high share of checked tasks does not prove testing, production readiness or business priority.";

    /// <summary>Feature progress in words: "No task list found — progress cannot be derived from tasks" instead of a measured-looking 0/0.</summary>
    public static string TaskProgress(int completed, int total, bool hasTaskList) =>
        !hasTaskList ? "No task list found — progress cannot be derived from tasks" : total == 0 ? "Task list has no checkbox tasks" : $"{completed}/{total} tasks checked";
}
