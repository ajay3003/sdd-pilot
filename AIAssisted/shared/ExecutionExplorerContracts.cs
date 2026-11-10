using System.Text.Json.Serialization;

namespace BirkNext.ExecutionExplorer;

// Test Execution & Evidence: a READ-ONLY projection over the existing evidence owners (Execution Evidence runs and calls, Integration Suite run links,
// Integration Journey runs, Critical E2E history, Performance Test Review runs, Parquet Quality Review evidence, Security Classification reviews).
// Nothing here is a second result store: every row points back to its owner by (Source, SourceRunId), and every outcome keeps the owner's own
// status next to the normalised one. Static assessments are never counted as executions.

/// <summary>Which owner recorded the result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExplorerSource { ContractTests, ContractChain, IntegrationSuite, CompositeE2E, IntegrationJourney, BrowserE2E, Performance, ParquetValidation, SecurityReview }

/// <summary>What kind of evidence it is. StaticAssessment = evidence was assessed, nothing was executed against a target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExplorerCategory { FunctionalTest, IntegrationTest, BrowserE2E, Performance, DataValidation, SecurityRuntime, StaticAssessment }

/// <summary>The normalised run outcome. The owner's own status is kept beside it (<see cref="ExplorerRun.SourceStatus"/>); a value is never upgraded.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExplorerOutcome { Passed, Failed, Partial, Blocked, NotAssessed, Cancelled, TimedOut, UnknownOutcome, Running, Interrupted, NotSupported }

/// <summary>How much of the evidence behind the outcome is stored: Complete = every call/step has its evidence; Partial = some; SummaryOnly = only the
/// owner's summary (counts, verdict); Missing = the owner says evidence is absent.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceCompleteness { Complete, Partial, SummaryOnly, Missing }

/// <summary>Drill-down an owner supports for its runs (the UI shows only these).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExplorerCapability { Calls, Attempts, Assertions, RequestResponse, Stages, Correlation, Metrics, Rules, Timeline, Compare, Protect, Delete, Export }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderState { Available, Unavailable, Empty }

/// <summary>One run (or one stored assessment) as the explorer lists it. Light: no request/response bodies.</summary>
public sealed record ExplorerRun
{
    public ExplorerSource Source { get; init; }
    public ExplorerCategory Category { get; init; }
    /// <summary>The owner's identifier (an execution run id, a journey run id, a Critical E2E run id…). With <see cref="Source"/> the exact reference.</summary>
    public string SourceRunId { get; init; } = "";
    /// <summary>Short display id (first 8 characters of the owner id).</summary>
    public string DisplayId { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? DurationMs { get; init; }
    public string EnvironmentId { get; init; } = "";
    /// <summary>The project (Source Analysis repository identity) when the owner recorded one or it follows from a recorded reference; null = not recorded.</summary>
    public string? ProjectKey { get; init; }
    public ExplorerOutcome Outcome { get; init; }
    /// <summary>The owner's own status text, unchanged (e.g. "Passed", "FULL_E2E_VERIFIED", "Completed · Fail").</summary>
    public string SourceStatus { get; init; } = "";
    /// <summary>One sentence: why the outcome is what it is (never a payload).</summary>
    public string? OutcomeReason { get; init; }
    /// <summary>Tests/steps/stages/rules — what the owner counts; <see cref="CountLabel"/> names the unit.</summary>
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int NotAssessed { get; init; }
    public string CountLabel { get; init; } = "tests";
    public EvidenceCompleteness Evidence { get; init; }
    public bool Protected { get; init; }
    /// <summary>Suite, chain, flow, definition or file the run belongs to (exact reference with revision where the owner keeps one).</summary>
    public string? SuiteRef { get; init; }
    public List<string> ContractRefs { get; init; } = [];
    public List<ExplorerCapability> Capabilities { get; init; } = [];
    /// <summary>Relative link to the owning module where the result is managed.</summary>
    public string OwnerHref { get; init; } = "";
    public string OwnerLabel { get; init; } = "";
}

public sealed record ExplorerQuery
{
    public string? EnvironmentId { get; init; }
    public string? ProjectKey { get; init; }
    /// <summary>With a project filter: also list runs whose project was not recorded (shown as such, never attributed).</summary>
    public bool IncludeUnattributed { get; init; }
    public List<ExplorerSource> Sources { get; init; } = [];
    public List<ExplorerCategory> Categories { get; init; } = [];
    public List<ExplorerOutcome> Outcomes { get; init; } = [];
    public string? Search { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public bool? Protected { get; init; }
    public EvidenceCompleteness? Evidence { get; init; }
    /// <summary>Static assessments are listed only when asked for (they are not executions).</summary>
    public bool IncludeStatic { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 25;
}

public sealed record ExplorerProviderStatus(ExplorerSource Source, ProviderState State, int Scanned, string? Message);

public sealed record ExplorerPage(List<ExplorerRun> Runs, int Total, int Skip, int Take, List<ExplorerProviderStatus> Providers, List<string> Limitations)
{
    /// <summary>True when an owner had more rows than the bounded scan window: older rows are reachable only through narrower filters.</summary>
    public bool Truncated { get; init; }
}

public sealed record ExplorerMetric(string Key, string Label, string Value, string? Detail = null);

/// <summary>Executed vs verified per category, with the denominator spelled out. Never one score across unrelated categories.</summary>
public sealed record ExplorerCoverageRow(ExplorerCategory Category, int Runs, int Passed, int Failed, int Partial, int NotAssessed, int Blocked, string Denominator, string? Missing);

public sealed record ExplorerOverview
{
    public int TotalRuns { get; init; }
    public int PassedRuns { get; init; }
    public int FailedRuns { get; init; }
    public int PartialRuns { get; init; }
    public int NotAssessedRuns { get; init; }
    public int BlockedRuns { get; init; }
    public int RunningRuns { get; init; }
    public int TestsExecuted { get; init; }
    public ExplorerRun? Latest { get; init; }
    public int StaticAssessments { get; init; }
    public List<ExplorerCoverageRow> Coverage { get; init; } = [];
    /// <summary>Failed, blocked, unknown-outcome or interrupted runs, newest first (bounded).</summary>
    public List<ExplorerRun> Attention { get; init; } = [];
    public List<ExplorerProviderStatus> Providers { get; init; } = [];
    /// <summary>Projects seen in the scanned window (recorded or derived), for the project selector.</summary>
    public List<string> Projects { get; init; } = [];
    public List<string> Environments { get; init; } = [];
    public bool Truncated { get; init; }
    public List<string> Limitations { get; init; } = [];
}

/// <summary>One recorded event on the run's timeline. Only recorded events: an unobserved stage is listed as NotObserved, never filled in.</summary>
public sealed record ExplorerTimelineEvent(DateTimeOffset? At, string Stage, string Action, string Outcome, string? EvidenceRef, string Correlation);

/// <summary>A test, step, stage or rule result of a run.</summary>
public sealed record ExplorerItem(string Id, string Name, string Kind, string Outcome, string? Detail, string? Ref, double? DurationMs = null);

/// <summary>A confirmed relationship of the run (contract version, test revision, Test Data version, source snapshot, requirement, suite, stage).</summary>
public sealed record ExplorerTrace(string Kind, string Reference, string Relationship, string? Href);

public sealed record ExplorerRunDetail
{
    public ExplorerRun Run { get; init; } = new();
    public List<ExplorerItem> Items { get; init; } = [];
    public string ItemsLabel { get; init; } = "Tests";
    public List<ExplorerTimelineEvent> Timeline { get; init; } = [];
    public List<ExplorerTrace> Traceability { get; init; } = [];
    public List<ExplorerMetric> Metrics { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    /// <summary>Execution Evidence runs: the run id whose calls/attempts/request/response the existing call explorer shows.</summary>
    public Guid? ExecutionRunId { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComparisonChange { NewFailure, ResolvedFailure, Regressed, StillFailing, Unchanged, ChangedStatus, OnlyInBaseline, OnlyInCurrent, LatencyChanged }

public sealed record ExplorerComparisonRow(string Key, string Name, string? Baseline, string? Current, ComparisonChange Change, string? Detail);

/// <summary>Two runs compared only when they are comparable (same owner, same definition/chain/suite, same environment); otherwise the reasons.</summary>
public sealed record ExplorerComparison(ExplorerRun? Baseline, ExplorerRun? Current, bool Comparable, List<string> Reasons, List<ExplorerComparisonRow> Rows, List<string> Limitations);

/// <summary>A sanitized, tester-oriented report of one run, generated on demand from stored evidence (never stored, never re-executed).</summary>
public sealed record ExplorerReport(string FileName, string Markdown, int Bytes);
