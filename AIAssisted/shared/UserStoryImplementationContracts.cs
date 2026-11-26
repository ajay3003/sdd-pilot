using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── User story implementation status (2026-11-26) ───────────────────────────────────────────────────────────────────────────────────────────
// One result contract for every consumer (Finn og forstå's story cards, Testoversikt, later reports). Computed on the server from the task lines
// the import already extracted (Implementation Gaps' persisted inputs) and the stored specification evidence; never in a frontend component.
// Four dimensions stay separate: task progress (tasks.md checkboxes), evidence quality, implementation confirmation (a person's decision), and
// — elsewhere — test preparation. A fully checked task list is «US-oppgaver fullført», never «Implementert».

/// <summary>The task-derived status of a user story.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserStoryTaskStatus
{
    /// <summary>No reliable task set (tasks.md missing, no task linked to the story, or the source data is missing).</summary>
    Unknown,
    /// <summary>A reliable direct task set, none complete.</summary>
    NotStarted,
    /// <summary>A reliable direct task set, some complete, some not.</summary>
    InProgress,
    /// <summary>Every eligible direct task is checked and nothing invalidates that.</summary>
    TasksCompleted,
    /// <summary>The story's tasks name a blocking dependency (explicit wording) that is still open.</summary>
    DependencyPending,
    /// <summary>The evidence is contradictory or contains a marker whose meaning the source does not state.</summary>
    NeedsClarification,
}

/// <summary>How complete the source evidence is — not a probability of implementation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserStoryEvidenceQuality { Complete, Partial, Ambiguous, Missing, Stale }

/// <summary>A person's confirmation that the story is implemented, for the task evidence it was given on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserStoryConfirmation { NotConfirmed, ConfirmedImplemented, ReconfirmationRequired }

/// <summary>One task's normalized state; <see cref="UserStoryTaskRef.RawMarker"/> keeps the marker as written.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserStoryTaskState { Complete, Open, Partial, CancelledOrDeferred, Unknown }

/// <summary>Why the task belongs to the story.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserStoryTaskLink
{
    /// <summary>The task line carries the story's own tag ("[US2]").</summary>
    ExplicitTag,
    /// <summary>No tag; the task sits in a phase titled for the story ("Phase 4: User Story 2").</summary>
    StoryPhase,
    /// <summary>Tagged for this story but placed in another story's phase: counted for its tag (the Spec Kit convention), reported as a conflict.</summary>
    ConflictingPhase,
}

/// <summary>How a dependency is documented.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserStoryDependencyKind
{
    /// <summary>Explicit dependency wording on the task line ("depends on T004", "requires T017", "blocks T022"): confirmed.</summary>
    Blocking,
    /// <summary>Another task is only mentioned ("see T030", "(T038 keeps …)"): related work, not a confirmed dependency.</summary>
    Related,
    /// <summary>The named task does not exist in tasks.md.</summary>
    Unresolved,
}

public sealed record UserStoryTaskRef(
    string TaskId, string Text, UserStoryTaskState State, string RawMarker, UserStoryTaskLink Link, string? Phase, string Path, int Line,
    string? StateNote = null, string? LinkNote = null);

public sealed record UserStoryDependency(string FromTaskId, string ToTaskId, UserStoryDependencyKind Kind, bool TargetOpen, string Evidence, string? TargetStory = null);

/// <summary>The status of one user story, with the evidence it was derived from.</summary>
public sealed record UserStoryImplementationStatus
{
    /// <summary>The canonical story key (specification path + story id, the key Finn og forstå's story cards use).</summary>
    public string StoryKey { get; init; } = "";
    public string StoryId { get; init; } = "";
    public string Title { get; init; } = "";
    public string SpecificationPath { get; init; } = "";
    public string Module { get; init; } = "";
    /// <summary>The tasks.md the tasks come from; null when the specification has none.</summary>
    public string? TasksPath { get; init; }
    public UserStoryTaskStatus Status { get; init; }
    public UserStoryEvidenceQuality Evidence { get; init; }
    /// <summary>The evidence quality in plain Norwegian («Oppgaver funnet og koblet til US.», «tasks.md mangler.»).</summary>
    public string EvidenceText { get; init; } = "";
    public UserStoryConfirmation Confirmation { get; init; }
    public string? ConfirmedBy { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public int Completed { get; init; }
    public int Open { get; init; }
    public int Partial { get; init; }
    public int UnknownState { get; init; }
    /// <summary>Superseded/cancelled tasks (by the file's own convention): listed, never counted.</summary>
    public int Cancelled { get; init; }
    /// <summary>The denominator: direct tasks that are not cancelled.</summary>
    public int Eligible { get; init; }
    /// <summary>Completed / eligible × 100 — null when it cannot be computed reliably (no tasks, or a marker of unknown meaning).</summary>
    public int? ProgressPercent { get; init; }
    public List<UserStoryTaskRef> Tasks { get; init; } = [];
    /// <summary>Open shared/foundational tasks of the same specification: listed as context («Fellesoppgaver»), never in this story's count.</summary>
    public List<UserStoryTaskRef> SharedOpen { get; init; } = [];
    public List<UserStoryDependency> Dependencies { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    /// <summary>What the status means, in plain Norwegian.</summary>
    public string Explanation { get; init; } = "";
    /// <summary>Fingerprint of the task evidence (the direct tasks' ids, markers and text): a confirmation applies to this fingerprint only.</summary>
    public string EvidenceFingerprint { get; init; } = "";
}

/// <summary>Every story of a project (or one specification) with the source it was computed from.</summary>
public sealed record UserStoryStatusView
{
    public string ProjectKey { get; init; } = "";
    public Guid? SnapshotId { get; init; }
    /// <summary>When the task lines were read (the import's evaluation time).</summary>
    public DateTimeOffset? SourceReadAt { get; init; }
    public DateTimeOffset CalculatedAt { get; init; }
    public string RuleVersion { get; init; } = "";
    /// <summary>False when the project's import has no task extraction at all (then every story is Unknown).</summary>
    public bool TasksAvailable { get; init; }
    /// <summary>The specification catalog and the task lines come from different imports (their snapshots differ).</summary>
    public bool SourcesDiffer { get; init; }
    public List<UserStoryImplementationStatus> Stories { get; init; } = [];
}

/// <summary>A person's confirmation that a story is implemented (or the withdrawal of one).</summary>
public sealed record ConfirmUserStoryRequest(string ProjectKey, string StoryKey, bool Confirmed, string? Note = null);

/// <summary>Labels, the same words in every consumer.</summary>
public static class UserStoryStatusText
{
    public static string Label(UserStoryTaskStatus s) => s switch
    {
        UserStoryTaskStatus.NotStarted => "Ikke startet iht. plan",
        UserStoryTaskStatus.InProgress => "Under utvikling",
        UserStoryTaskStatus.TasksCompleted => "US-oppgaver fullført",
        UserStoryTaskStatus.DependencyPending => "Avhengighet gjenstår",
        UserStoryTaskStatus.NeedsClarification => "Må avklares",
        _ => "Ukjent",
    };

    public static string Tone(UserStoryTaskStatus s) => s switch
    {
        UserStoryTaskStatus.TasksCompleted => "ok",
        UserStoryTaskStatus.InProgress => "progress",
        UserStoryTaskStatus.DependencyPending or UserStoryTaskStatus.NeedsClarification => "warn",
        _ => "neutral",
    };

    public static string Label(UserStoryConfirmation c) => c switch
    {
        UserStoryConfirmation.ConfirmedImplemented => "Bekreftet implementert",
        UserStoryConfirmation.ReconfirmationRequired => "Må bekreftes på nytt",
        _ => "Ikke bekreftet",
    };

    public static string Label(UserStoryEvidenceQuality q) => q switch
    {
        UserStoryEvidenceQuality.Complete => "Fullstendig grunnlag",
        UserStoryEvidenceQuality.Partial => "Delvis grunnlag",
        UserStoryEvidenceQuality.Ambiguous => "Uklart grunnlag",
        UserStoryEvidenceQuality.Stale => "Utdatert grunnlag",
        _ => "Mangler grunnlag",
    };

    public static string Label(UserStoryTaskState s) => s switch
    {
        UserStoryTaskState.Complete => "Fullført",
        UserStoryTaskState.Partial => "Delvis",
        UserStoryTaskState.CancelledOrDeferred => "Utgått",
        UserStoryTaskState.Unknown => "Ukjent markering",
        _ => "Ikke fullført",
    };

    public static string Label(UserStoryDependencyKind k) => k switch
    {
        UserStoryDependencyKind.Blocking => "Bekreftet avhengighet",
        UserStoryDependencyKind.Related => "Relatert oppgave",
        _ => "Uløst henvisning",
    };

    /// <summary>The filter values of Finn og forstå's implementation-status filter, in display order.</summary>
    public static readonly UserStoryTaskStatus[] FilterOrder =
        [UserStoryTaskStatus.NotStarted, UserStoryTaskStatus.InProgress, UserStoryTaskStatus.TasksCompleted, UserStoryTaskStatus.DependencyPending, UserStoryTaskStatus.NeedsClarification, UserStoryTaskStatus.Unknown];
}
