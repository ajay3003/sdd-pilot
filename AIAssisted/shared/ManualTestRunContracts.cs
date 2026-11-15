using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Manual test execution (Manual Testing → Execute & document) ──────────────────────────────────────────────────
// A manual run executes ONE saved test case (plan id + case id) as it stood when the run started: the steps, expected results and the case's
// content fingerprint are pinned in the run, so a later edit of the case never rewrites a past result. Outcomes are recorded by a person only
// (never generated). Evidence is a REFERENCE (a link or a note naming where the evidence is kept): BirkNext stores no uploaded file here.
// A defect is a local observation, optionally referencing an existing external work item; nothing is written to Azure DevOps.

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
}

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
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualDefectStatus { Open, Closed }

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
}

/// <summary>A run in a history list.</summary>
public sealed record ManualRunSummary(Guid Id, Guid DraftId, string CaseId, string CaseKey, string Title, int PlanRevision, string CaseFingerprint, ManualRunState State,
    ManualRunOutcome Outcome, string Tester, string? Environment, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int Steps, int Passed, int Failed, int Blocked,
    int NotRun, int Undecided, int Defects, int OpenDefects, int Evidence);

public sealed record StartManualRunRequest(string ProjectKey, Guid DraftId, string CaseId, string? Environment);

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
public sealed record AbandonManualRunRequest(int BaseRevision, string Reason);

/// <summary>The rules every consumer (runner, history, Test Coverage, Requirements Traceability) shares.</summary>
public static class ManualRunRules
{
    public static readonly IReadOnlyList<string> Severities = ["Critical", "High", "Medium", "Low"];

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
        r.Defects.Count, r.Defects.Count(d => d.Status == ManualDefectStatus.Open), r.Evidence.Count);
}
