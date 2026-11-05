using BirkNext.DeliveryReporting;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>How an execution was tied to a canonical test: by the test's stable identity (the TRX import correlated it to the source test
/// definition), or only by a matching name — which is never enough to attribute a result.</summary>
public enum TestExecutionAttribution { Identity, NameOnly }

/// <summary>Execution state of one requirement-linked test in the selected environment.</summary>
public enum RequirementTestExecutionState
{
    /// <summary>No test results are imported in this workspace at all: nothing can be said about execution.</summary>
    Unavailable,
    /// <summary>Results are imported, but none of them is this test in this environment.</summary>
    NotExecuted,
    /// <summary>At least one run in this environment executed this test (identity match).</summary>
    Observed,
}

/// <summary>One run of one test: every result row of the run (a theory has several) folded into one outcome, with the run's provenance.</summary>
public sealed record TestRunResult(Guid? RunId, string RunLabel, string? ArtifactFile, DateTimeOffset? ExecutedAt, string Environment, bool EnvironmentRecorded,
    string Outcome, int Rows, string Currentness, string? CurrentnessReason, string? Build, string? SourceSnapshotId, string? Error, TestExecutionAttribution Attribution, string Basis);

public sealed record TestExecutionSummary(RequirementTestExecutionState State, TestRunResult? Current, IReadOnlyList<TestRunResult> Runs,
    IReadOnlyList<TestRunResult> OtherEnvironments, IReadOnlyList<TestRunResult> Unattributed, string Text, string SelectionRule)
{
    public string Label => State switch
    {
        RequirementTestExecutionState.Observed => Current?.Outcome ?? "Executed (history only)",
        RequirementTestExecutionState.NotExecuted => "Not executed",
        _ => "Execution evidence unavailable",
    };
}

/// <summary>
/// Requirement → VERIFIES test → TRX execution, over the test results imported into the workspace (the one SDD lifecycle, written by the TRX
/// import). Rules:
/// <list type="bullet">
/// <item>Attribution needs identity: an execution counts for the canonical test only when the import correlated it (Confirmed) to the same stable
/// test identity. A result whose name merely matches stays an unattributed candidate — shown with its evidence, never as this test's pass/fail.</item>
/// <item>Environment: a run answers for the selected environment only when it records that environment (its id or name). A run without an
/// environment is "environment not recorded" — never assigned to the selected one. "All environments" lists every run, each with its own.</item>
/// <item>Runs are never collapsed: every run is listed; the current result is the newest run in the environment whose result is still current
/// (a result whose test source changed later is kept as history).</item>
/// </list>
/// Discovery is never execution; a Markdown checkbox is never a result.
/// </summary>
public static class RequirementTestExecutions
{
    public const string SelectionRule = "Current result = the newest run in this environment (by execution time) whose result is still current; " +
        "results whose test source changed afterwards stay in the history. Every run is listed.";

    public static TestExecutionSummary For(ScopeTestLink link, SddLifecycleState? state, string? environmentId, string? environmentName, bool crossEnvironment)
    {
        var executions = state?.TestExecutions ?? [];
        if (executions.Count == 0)
            return new(RequirementTestExecutionState.Unavailable, null, [], [], [],
                "Execution evidence unavailable — no test results (TRX) are imported in this workspace. A discovered test is not an executed test.", SelectionRule);

        var runs = (state!.TestRuns ?? []).ToDictionary(r => r.RunId);
        var artifacts = (state.TestResultArtifacts ?? []).ToDictionary(a => a.ArtifactId);
        string? EnvironmentOf(SddTestExecutionEvidence e) =>
            e.EnvironmentReference is { Length: > 0 } own ? own : e.RunId is { } id && runs.TryGetValue(id, out var run) && run.EnvironmentReference is { Length: > 0 } r ? r : null;
        bool InEnvironment(SddTestExecutionEvidence e) => crossEnvironment || EnvironmentOf(e) is { } env
            && (string.Equals(env, environmentId, StringComparison.OrdinalIgnoreCase) || string.Equals(env, environmentName, StringComparison.OrdinalIgnoreCase));

        var identity = executions.Where(e => e.CorrelationState == "Confirmed" && string.Equals(e.TestId, link.TestIdentity, StringComparison.Ordinal)).ToList();
        var byName = executions.Where(e => e.CorrelationState != "Confirmed"
            && string.Equals(e.FullyQualifiedTestName ?? e.TestName, link.TestName, StringComparison.Ordinal)).ToList();

        IReadOnlyList<TestRunResult> Fold(IEnumerable<SddTestExecutionEvidence> rows, TestExecutionAttribution attribution) =>
            [.. rows.GroupBy(e => e.RunId).Select(g => Run(g.ToList(), attribution)).OrderByDescending(r => r.ExecutedAt ?? DateTimeOffset.MinValue).ThenBy(r => r.RunLabel, StringComparer.Ordinal)];

        TestRunResult Run(IReadOnlyList<SddTestExecutionEvidence> rows, TestExecutionAttribution attribution)
        {
            var first = rows[0];
            var run = first.RunId is { } id ? runs.GetValueOrDefault(id) : null;
            var artifact = first.ArtifactId is { } aid ? artifacts.GetValueOrDefault(aid) : null;
            var environment = EnvironmentOf(first);
            var label = run is { ProviderRunId.Length: > 0 } ? run.ProviderRunId : artifact?.FileName ?? first.ResultSource;
            var stale = rows.FirstOrDefault(r => r.Currentness != "Current");
            return new(first.RunId, label, artifact?.FileName ?? first.ResultSource, rows.Max(r => r.ExecutedAt), environment ?? "Environment not recorded", environment is not null,
                Outcome(rows), rows.Count, stale?.Currentness ?? "Current", stale?.CurrentnessReason, first.BuildReference ?? run?.BuildReference, first.SourceSnapshotId,
                rows.Select(r => r.ErrorMessage).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)), attribution,
                attribution == TestExecutionAttribution.Identity ? first.CorrelationBasis ?? "Same test identity" : $"Name only ({first.CorrelationState}): {first.CorrelationBasis ?? "the result was not correlated to a source test"}");
        }

        var inEnvironment = Fold(identity.Where(InEnvironment), TestExecutionAttribution.Identity);
        var elsewhere = crossEnvironment ? [] : Fold(identity.Where(e => !InEnvironment(e)), TestExecutionAttribution.Identity);
        var unattributed = Fold(byName, TestExecutionAttribution.NameOnly);
        var current = inEnvironment.FirstOrDefault(r => r.Currentness == "Current");
        var where = crossEnvironment ? "any environment" : environmentName ?? environmentId ?? "the selected environment";
        if (inEnvironment.Count > 0)
        {
            var text = current is null
                ? $"Executed in {where}, but every result is history (the test source changed afterwards): no current result."
                : $"{current.Outcome} in run {current.RunLabel}{(current.ExecutedAt is { } at ? $" ({at.ToLocalTime():yyyy-MM-dd HH:mm})" : "")}{(crossEnvironment ? $" · {current.Environment}" : "")}.";
            return new(RequirementTestExecutionState.Observed, current, inEnvironment, elsewhere, unattributed, text, SelectionRule);
        }
        var notExecuted = $"Not executed in {where}: none of the {executions.Count} imported test result(s) is this test there.";
        if (elsewhere.Count > 0) notExecuted += $" {elsewhere.Count} run(s) in other environments or without a recorded environment are listed apart — they do not count here.";
        if (unattributed.Count > 0) notExecuted += $" {unattributed.Count} run(s) report a test with the same name but were not correlated to this test: not attributed.";
        return new(RequirementTestExecutionState.NotExecuted, null, [], elsewhere, unattributed, notExecuted, SelectionRule);
    }

    /// <summary>One run's outcome over its rows: any failure fails it; all passed passes it; all skipped is Skipped; otherwise the execution state
    /// (timed out, aborted, execution failed) or Inconclusive. Never upgraded to Passed.</summary>
    public static string Outcome(IReadOnlyList<SddTestExecutionEvidence> rows)
    {
        if (rows.Any(r => r.Result == "Failed")) return "Failed";
        if (rows.All(r => r.Result == "Passed")) return "Passed";
        if (rows.All(r => r.ExecutionState == "NotExecuted")) return "Skipped";
        if (rows.Any(r => r.Result == "Inconclusive")) return "Inconclusive";
        return rows.Select(r => r.ExecutionState).FirstOrDefault(s => s is "TimedOut" or "Aborted" or "ExecutionFailed") switch
        {
            "TimedOut" => "Timed out (no verdict)",
            "Aborted" => "Aborted (no verdict)",
            "ExecutionFailed" => "Execution failed (no verdict)",
            _ => rows.Any(r => r.Result == "Passed") ? "Partly passed (other rows have no verdict)" : "Unknown",
        };
    }

    public static SemanticStatus Status(string outcome) => outcome switch
    {
        "Passed" => SemanticStatus.Verified,
        "Failed" => SemanticStatus.Failed,
        "Skipped" => SemanticStatus.NotRun,
        _ => SemanticStatus.Unknown,
    };
}
