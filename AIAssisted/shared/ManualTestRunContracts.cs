using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Manual test execution (Manual Testing → Execute & document) ──────────────────────────────────────────────────
// A manual run executes ONE saved test case (plan id + case id) as it stood when the run started: the steps, expected results and the case's
// content fingerprint are pinned in the run, so a later edit of the case never rewrites a past result. Outcomes are recorded by a person only
// (never generated). Evidence is a link, a note naming where it is kept, or a file attached to the run (size- and type-limited, stored with the run's
// project). A defect is a local observation, optionally referencing an existing external work item; nothing is written to Azure DevOps. After a
// run is completed its steps, outcome and original evidence are final; a defect's status can still be followed up (with an audit history) and
// further evidence can be attached, marked as added after completion.

/// <summary>Where a run stands in its own lifecycle (separate from the case's preparation state and from the run's outcome).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualRunState { InProgress, Completed, Abandoned }

/// <summary>A tester's decision for one step. A step without a decision has a null outcome ("not decided yet").</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualStepOutcome { Pass, Fail, Blocked, NotRun }

/// <summary>The run's overall outcome, rolled up from the step decisions when the run is completed (<see cref="ManualRunRules.Rollup"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualRunOutcome { NotDecided, Passed, Failed, Blocked, NotRun }

/// <summary>One step as pinned at the start of the run, with the tester's decision and observation.</summary>
public sealed record ManualRunStep
{
    public int Number { get; init; }
    public string Action { get; init; } = "";
    public string? Expected { get; init; }
    public ManualStepOutcome? Outcome { get; init; }
    public string? Actual { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }
}

/// <summary>How a piece of evidence is referenced. Never an uploaded file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualEvidenceKind
{
    /// <summary>An http(s) link to where the evidence is kept (a screenshot in a team share, a log in a tool).</summary>
    Link,
    /// <summary>A plain description of where the evidence is kept (file name, folder, ticket) — not a link, not an upload.</summary>
    Note,
    /// <summary>A file attached to the run (screenshot, PDF, log): <see cref="ManualRunEvidence.Reference"/> is the attachment id.</summary>
    Attachment,
}

/// <summary>An attached evidence file as stored (metadata only). <paramref name="ScanStatus"/> is "not-scanned" unless a malware scanner ran.</summary>
public sealed record ManualRunAttachmentInfo(Guid Id, string FileName, string OriginalFileName, string ContentType, long Size, string Sha256, string AddedBy, DateTimeOffset AddedAt,
    string ScanStatus, DateTimeOffset? WithdrawnAt, string? WithdrawnBy, string? WithdrawReason);

public sealed record WithdrawAttachmentRequest(string Reason);

public sealed record ManualRunEvidence
{
    /// <summary>Assigned by the server when the reference is saved; empty for a new one.</summary>
    public string Id { get; init; } = "";
    /// <summary>The step it belongs to; null = the whole run.</summary>
    public int? Step { get; init; }
    public ManualEvidenceKind Kind { get; init; }
    public string Label { get; init; } = "";
    public string Reference { get; init; } = "";
    public string AddedBy { get; init; } = "";
    public DateTimeOffset AddedAt { get; init; }
    /// <summary>For an attachment: the original file name, its type and size in bytes.</summary>
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public long? Size { get; init; }
    /// <summary>Attached after the run was completed (follow-up evidence); the run's original evidence is never replaced.</summary>
    public bool AddedAfterCompletion { get; init; }
}

/// <summary>Open → Ready for retest (fixed, waiting for a retest) → Closed; a closed or ready defect can be reopened. Changes after completion are
/// recorded in <see cref="ManualRunDefect.History"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualDefectStatus { Open, Closed, ReadyForRetest }

/// <summary>One change of a defect's status after it was recorded: who, when, from what to what, why, and (optionally) the retest run that
/// verified it.</summary>
public sealed record ManualDefectEvent(DateTimeOffset At, string By, ManualDefectStatus From, ManualDefectStatus To, string? Note, Guid? RetestRunId = null,
    string? ExternalReference = null);

/// <summary>A defect observed during the run, recorded in BirkNext (a local observation). <see cref="ExternalReference"/> optionally names an
/// existing work item (an Azure DevOps id or an https link) the tester registered elsewhere; BirkNext never creates or changes it.</summary>
public sealed record ManualRunDefect
{
    public string Id { get; init; } = "";
    public int? Step { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Reproduction { get; init; }
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    /// <summary>Free choice from <see cref="ManualRunRules.Severities"/>; null = not assessed.</summary>
    public string? Severity { get; init; }
    public ManualDefectStatus Status { get; init; }
    public string? ExternalReference { get; init; }
    public string RecordedBy { get; init; } = "";
    public DateTimeOffset RecordedAt { get; init; }
    /// <summary>Status changes after the defect was recorded (follow-up), oldest first; omitted when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ManualDefectEvent>? History { get; init; }
}

public sealed record ManualTestRun
{
    public Guid Id { get; init; }
    public string ProjectKey { get; init; } = "";
    public Guid DraftId { get; init; }
    public string CaseId { get; init; } = "";
    /// <summary>The saved case's identity: "{draft id:N}/{case id}" (the same key Test Coverage and Requirements Traceability use).</summary>
    public string CaseKey { get; init; } = "";
    public string PlanName { get; init; } = "";
    /// <summary>The plan revision and the case content fingerprint the run executed (pinned at start).</summary>
    public int PlanRevision { get; init; }
    public string CaseFingerprint { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Preconditions { get; init; }
    public string? ExpectedResult { get; init; }
    /// <summary>The requirements the case was linked to (automatic and manual) when the run started.</summary>
    public List<string> RequirementIds { get; init; } = [];
    /// <summary>Test Data the case named when the run started (alias and asset id; never values).</summary>
    public List<string> TestData { get; init; } = [];
    /// <summary>Where the test was run, as the tester names it (free text, e.g. "QA", "Test environment 2"); null = not stated.</summary>
    public string? Environment { get; init; }
    public string Tester { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ManualRunState State { get; init; }
    public ManualRunOutcome Outcome { get; init; }
    public string? AbandonReason { get; init; }
    public string? Notes { get; init; }
    public List<ManualRunStep> Steps { get; init; } = [];
    public List<ManualRunEvidence> Evidence { get; init; } = [];
    public List<ManualRunDefect> Defects { get; init; } = [];
    public int Revision { get; init; }
    public string UpdatedBy { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Where the executed case came from, pinned at start (specification → scenario → case → run). Null on runs started before it was
    /// recorded; those still carry <see cref="RequirementIds"/> and <see cref="CaseFingerprint"/>.</summary>
    public ManualRunOrigin? Origin { get; init; }
    /// <summary>The earlier failed or blocked completed run of the same case this run retests (set at start when there is one); null otherwise.</summary>
    public Guid? RetestOf { get; init; }
}

/// <summary>The origin of the executed case at run start: its source (discovered scenario or written by hand), the scenario's fingerprint, the
/// specification file and scenario it was prepared from, and where each requirement id came from.</summary>
public sealed record ManualRunOrigin
{
    public ScenarioSourceKind SourceKind { get; init; }
    /// <summary>The discovered scenario's content fingerprint when the case was prepared (null for a case written by hand).</summary>
    public string? ScenarioFingerprint { get; init; }
    /// <summary>The specification file the case was prepared from (from its own evidence), when known.</summary>
    public string? SpecificationFile { get; init; }
    /// <summary>The acceptance scenario of that specification, when the case was prepared from one.</summary>
    public string? SpecificationScenarioId { get; init; }
    public List<ManualRunRequirementSource> Requirements { get; init; } = [];
}

/// <summary>A requirement the run's case named, with its specification file and basis: "Specification evidence" (prepared from the file) or
/// "Manual link" (a tester's confirmed link).</summary>
public sealed record ManualRunRequirementSource(string RequirementId, string? SpecificationFile, string Basis, string? LinkedBy = null);

/// <summary>A run in a history list.</summary>
public sealed record ManualRunSummary(Guid Id, Guid DraftId, string CaseId, string CaseKey, string Title, int PlanRevision, string CaseFingerprint, ManualRunState State,
    ManualRunOutcome Outcome, string Tester, string? Environment, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int Steps, int Passed, int Failed, int Blocked,
    int NotRun, int Undecided, int Defects, int OpenDefects, int Evidence, Guid? RetestOf = null);

public sealed record StartManualRunRequest(string ProjectKey, Guid DraftId, string CaseId, string? Environment);

/// <summary>One page of a project's runs, newest first, with the exact number of matching runs (so a list never hides that it is partial).</summary>
public sealed record ManualRunPage(List<ManualRunSummary> Runs, int Total, int Skip, int Take);

/// <summary>Saves progress of an in-progress run: step decisions and observations, notes, evidence references and defects (full lists; an item
/// without an id is new). Refused for a completed or abandoned run, and from an older run revision (409).</summary>
public sealed record SaveManualRunRequest
{
    public int BaseRevision { get; init; }
    public List<ManualStepInput> Steps { get; init; } = [];
    public string? Notes { get; init; }
    public string? Environment { get; init; }
    public List<ManualRunEvidence> Evidence { get; init; } = [];
    public List<ManualRunDefect> Defects { get; init; } = [];
}

public sealed record ManualStepInput(int Number, ManualStepOutcome? Outcome, string? Actual);

public sealed record CompleteManualRunRequest(int BaseRevision);

/// <summary>Follows up one defect of a run — also after the run is completed: a new status with an optional note, an updated external work item
/// reference, and the retest run that verified a fix. Only the defect changes; the run's steps, outcome and evidence stay as recorded.</summary>
public sealed record UpdateManualDefectRequest(int BaseRevision, ManualDefectStatus Status, string? Note, string? ExternalReference, Guid? RetestRunId);

/// <summary>A defect with the run it was found in (the project's defect list across runs).</summary>
public sealed record ManualDefectListItem(Guid RunId, string RunTitle, string CaseKey, ManualRunState RunState, ManualRunOutcome RunOutcome, DateTimeOffset RunStartedAt,
    string Tester, ManualRunDefect Defect);
public sealed record AbandonManualRunRequest(int BaseRevision, string Reason);

/// <summary>The rules every consumer (runner, history, Test Coverage, Requirements Traceability) shares.</summary>
public static class ManualRunRules
{
    public static readonly IReadOnlyList<string> Severities = ["Critical", "High", "Medium", "Low"];

    private static readonly System.Text.Json.JsonSerializerOptions FingerprintJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The identity of a case's executable content — title, preconditions, steps and expected results as the tester sees them — pinned on a
    /// run at start. One definition for the backend (pinning) and the web app (is the last run of this case's current content?).</summary>
    public static string CaseFingerprint(TestCaseDraft c) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(new { c.EffectiveTitle, c.EffectivePreconditions, Steps = c.EffectiveSteps, Expected = c.EffectiveStepExpected, c.EffectiveExpected }, FingerprintJson)))).ToLowerInvariant();

    /// <summary>
    /// Overall outcome from the step decisions: any Fail → Failed; else any Blocked → Blocked; else any NotRun → NotRun (the case was not fully
    /// executed); else every step Pass → Passed. Any undecided step → NotDecided (a run cannot be completed then).
    /// </summary>
    public static ManualRunOutcome Rollup(IReadOnlyCollection<ManualRunStep> steps) =>
        steps.Count == 0 || steps.Any(s => s.Outcome is null) ? ManualRunOutcome.NotDecided
        : steps.Any(s => s.Outcome == ManualStepOutcome.Fail) ? ManualRunOutcome.Failed
        : steps.Any(s => s.Outcome == ManualStepOutcome.Blocked) ? ManualRunOutcome.Blocked
        : steps.Any(s => s.Outcome == ManualStepOutcome.NotRun) ? ManualRunOutcome.NotRun
        : ManualRunOutcome.Passed;

    /// <summary>A completed run that actually exercised the case: Passed or Failed. Blocked and Not run are not executions of the case.</summary>
    public static bool Executed(ManualRunState state, ManualRunOutcome outcome) => state == ManualRunState.Completed && outcome is ManualRunOutcome.Passed or ManualRunOutcome.Failed;

    public static string OutcomeLabel(ManualRunOutcome outcome) => outcome switch
    {
        ManualRunOutcome.Passed => "Passed", ManualRunOutcome.Failed => "Failed", ManualRunOutcome.Blocked => "Blocked", ManualRunOutcome.NotRun => "Not run",
        _ => "Not decided",
    };

    public static string StepLabel(ManualStepOutcome? outcome) => outcome switch
    {
        ManualStepOutcome.Pass => "Pass", ManualStepOutcome.Fail => "Fail", ManualStepOutcome.Blocked => "Blocked", ManualStepOutcome.NotRun => "Not run", _ => "Not decided",
    };

    public static ManualRunSummary Summary(ManualTestRun r) => new(r.Id, r.DraftId, r.CaseId, r.CaseKey, r.Title, r.PlanRevision, r.CaseFingerprint, r.State, r.Outcome, r.Tester,
        r.Environment, r.StartedAt, r.CompletedAt, r.Steps.Count, r.Steps.Count(s => s.Outcome == ManualStepOutcome.Pass), r.Steps.Count(s => s.Outcome == ManualStepOutcome.Fail),
        r.Steps.Count(s => s.Outcome == ManualStepOutcome.Blocked), r.Steps.Count(s => s.Outcome == ManualStepOutcome.NotRun), r.Steps.Count(s => s.Outcome is null),
        r.Defects.Count, r.Defects.Count(d => d.Status != ManualDefectStatus.Closed), r.Evidence.Count, r.RetestOf);
}
