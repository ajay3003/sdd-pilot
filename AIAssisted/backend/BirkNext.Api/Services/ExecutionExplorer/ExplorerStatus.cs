using BirkNext.CriticalE2E;
using BirkNext.ExecutionEvidence;
using BirkNext.ExecutionExplorer;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.ExecutionExplorer;

/// <summary>
/// Status normalisation of the evidence owners into <see cref="ExplorerOutcome"/>. Each owner keeps its own vocabulary (shown beside the normalised
/// outcome); the mapping never upgrades a result: no assertions assessed is never Passed, a subflow is never a full end-to-end pass, a performance
/// threshold verdict is never a functional pass, a static review is never an execution.
/// </summary>
public static class ExplorerStatus
{
    /// <summary>Executions are runs that sent something to a target or drove a browser. Static assessments and data-file validations are not.</summary>
    public static bool IsExecution(ExplorerCategory c) => c is not (ExplorerCategory.StaticAssessment or ExplorerCategory.DataValidation);

    public static (ExplorerOutcome Outcome, string? Reason) Execution(ExecutionState state, ExecutionRunCounts counts) => state switch
    {
        ExecutionState.Planned or ExecutionState.Queued or ExecutionState.Running => (ExplorerOutcome.Running, null),
        // A run whose calls all passed transport but whose assertions were never assessed is not a pass.
        ExecutionState.Passed when counts.Passed == 0 => (ExplorerOutcome.NotAssessed, "No call had an assessed assertion."),
        ExecutionState.Passed => (ExplorerOutcome.Passed, null),
        ExecutionState.Failed => (ExplorerOutcome.Failed, $"{counts.Failed} call(s) failed."),
        ExecutionState.Error => (ExplorerOutcome.Failed, $"{counts.Errors} call(s) ended in a transport or execution error."),
        ExecutionState.Blocked => (ExplorerOutcome.Blocked, "A prerequisite (authentication, safety policy or dependency) blocked the calls."),
        ExecutionState.Cancelled => (ExplorerOutcome.Cancelled, null),
        ExecutionState.TimedOut => (ExplorerOutcome.TimedOut, null),
        ExecutionState.Interrupted => (ExplorerOutcome.Interrupted, "The backend stopped before the run finished; the outcome of unfinished calls is unknown."),
        ExecutionState.Skipped or ExecutionState.NotAssessed => (ExplorerOutcome.NotAssessed, null),
        _ => (ExplorerOutcome.NotAssessed, null),
    };

    public static (ExplorerOutcome Outcome, string? Reason) Composite(CompositeRunVerdict verdict, string reason) => verdict switch
    {
        CompositeRunVerdict.FullE2EVerified => (ExplorerOutcome.Passed, reason),
        // A verified subflow is a partial verification of the integration — never a full end-to-end pass.
        CompositeRunVerdict.SubflowVerified => (ExplorerOutcome.Partial, "Subflow verified; not a full end-to-end result. " + reason),
        CompositeRunVerdict.Incomplete => (ExplorerOutcome.Partial, "Not every stage was observed. " + reason),
        CompositeRunVerdict.WrongCorrelation => (ExplorerOutcome.Failed, "A downstream stage answered for another transaction. " + reason),
        CompositeRunVerdict.UnknownOutcome => (ExplorerOutcome.UnknownOutcome, reason),
        CompositeRunVerdict.Failed => (ExplorerOutcome.Failed, reason),
        CompositeRunVerdict.Running => (ExplorerOutcome.Running, null),
        _ => (ExplorerOutcome.NotAssessed, reason),
    };

    public static ExplorerOutcome Journey(JourneyRunState state, string reason) => state switch
    {
        JourneyRunState.Completed => ExplorerOutcome.Passed,
        JourneyRunState.Partial => ExplorerOutcome.Partial,
        JourneyRunState.NotVerified => ExplorerOutcome.NotAssessed,
        JourneyRunState.Failed => ExplorerOutcome.Failed,
        JourneyRunState.Blocked => ExplorerOutcome.Blocked,
        JourneyRunState.Cancelled => reason.StartsWith("Interrupted", StringComparison.Ordinal) ? ExplorerOutcome.Interrupted : ExplorerOutcome.Cancelled,
        JourneyRunState.Running => ExplorerOutcome.Running,
        _ => ExplorerOutcome.NotAssessed,
    };

    public static bool JourneyStepVerified(JourneyStepState s) => s is JourneyStepState.Verified;
    public static bool JourneyStepFailed(JourneyStepState s) => s is JourneyStepState.UnexpectedResult;

    public static ExplorerOutcome BrowserE2E(CriticalE2EStatus status) => status switch
    {
        CriticalE2EStatus.Passed => ExplorerOutcome.Passed,
        CriticalE2EStatus.Failed => ExplorerOutcome.Failed,
        CriticalE2EStatus.Blocked => ExplorerOutcome.Blocked,
        CriticalE2EStatus.Cancelled => ExplorerOutcome.Cancelled,
        CriticalE2EStatus.Running => ExplorerOutcome.Running,
        // Every action ran but nothing proved the business outcome — never Passed.
        CriticalE2EStatus.Inconclusive => ExplorerOutcome.NotAssessed,
        _ => ExplorerOutcome.NotAssessed,
    };

    /// <summary>A completed k6 run is not a pass: the threshold verdict decides, and it is a performance verdict, not a functional one.</summary>
    public static (ExplorerOutcome Outcome, string? Reason) Performance(PerformanceRunState state, PerformanceQualityVerdict verdict, string? stateReason) => state switch
    {
        PerformanceRunState.Completed => verdict switch
        {
            PerformanceQualityVerdict.Pass => (ExplorerOutcome.Passed, "Every threshold passed."),
            PerformanceQualityVerdict.Fail => (ExplorerOutcome.Failed, "A threshold failed."),
            PerformanceQualityVerdict.Warning => (ExplorerOutcome.Partial, "A threshold is in its warning band."),
            _ => (ExplorerOutcome.NotAssessed, "Completed, but no threshold was assessed."),
        },
        PerformanceRunState.ExecutionFailed => (ExplorerOutcome.Failed, stateReason ?? "The load test did not execute to completion."),
        PerformanceRunState.TimedOut => (ExplorerOutcome.TimedOut, stateReason),
        PerformanceRunState.Cancelled => (ExplorerOutcome.Cancelled, stateReason),
        PerformanceRunState.Blocked => (ExplorerOutcome.Blocked, stateReason),
        _ => (ExplorerOutcome.Running, null),
    };

    public static ExplorerOutcome Parquet(int pass, int fail, int inconclusive) =>
        fail > 0 ? ExplorerOutcome.Failed : pass > 0 && inconclusive == 0 ? ExplorerOutcome.Passed : pass > 0 ? ExplorerOutcome.Partial : ExplorerOutcome.NotAssessed;

    public static ExplorerOutcome Security(ClassificationOverall overall) => overall switch
    {
        ClassificationOverall.Verified => ExplorerOutcome.Passed,
        ClassificationOverall.Partial => ExplorerOutcome.Partial,
        ClassificationOverall.IssueDetected => ExplorerOutcome.Failed,
        _ => ExplorerOutcome.NotAssessed,
    };

    public static string Short(string id) => id.Replace("-", "", StringComparison.Ordinal) is { Length: > 8 } s ? s[..8] : id;

    /// <summary>A failed, blocked, interrupted, timed-out or unknown result needs a person's attention.</summary>
    public static bool NeedsAttention(ExplorerOutcome o) => o is ExplorerOutcome.Failed or ExplorerOutcome.Blocked or ExplorerOutcome.UnknownOutcome or ExplorerOutcome.Interrupted or ExplorerOutcome.TimedOut;
}
