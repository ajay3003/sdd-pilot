using BirkNext.DeliveryReporting;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Requirement → VERIFIES test → TRX execution: attribution by test identity only (a matching name is an unattributed candidate), environment
/// isolation (a run without a recorded environment never answers for the selected one), every run listed (never collapsed), the current result
/// chosen by a stated rule, theory rows folded per run, and "unavailable" apart from "not executed".
/// </summary>
public sealed class RequirementTestExecutionsTests
{
    private const string Identity = "M2LB-language::M2LB.SkoleService.Tests::M2LB.SkoleService.Tests.RapportTests.Sends_report";
    private const string Name = "M2LB.SkoleService.Tests.RapportTests.Sends_report";
    private static readonly DateTimeOffset Mon = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static ScopeTestLink Link(ScopeTestLinkKind kind = ScopeTestLinkKind.Explicit) =>
        new("rk", "env-dev", kind, Identity, Name, null, "Explicit", "High", "verifies.test-name-id", "", "NotReviewed", [], "");

    private static (SddTestRunEvidence Run, SddTestResultArtifact Artifact) Run(SddLifecycleState state, string providerRunId, string? environment)
    {
        var artifact = new SddTestResultArtifact { FileName = $"{providerRunId}.trx", EnvironmentReference = environment };
        var run = new SddTestRunEvidence { ArtifactId = artifact.ArtifactId, ProviderRunId = providerRunId, EnvironmentReference = environment };
        state.TestResultArtifacts.Add(artifact);
        state.TestRuns.Add(run);
        return (run, artifact);
    }

    private static SddTestExecutionEvidence Exec((SddTestRunEvidence Run, SddTestResultArtifact Artifact) r, string result, DateTimeOffset at, string correlation = "Confirmed",
        string executionState = "Completed", string? testId = null, string currentness = "Current") => new()
    {
        TestId = testId ?? (correlation == "Confirmed" ? Identity : Name), TestName = "Sends_report", FullyQualifiedTestName = Name, Result = result, ExecutionState = executionState,
        ExecutedAt = at, RunId = r.Run.RunId, ArtifactId = r.Artifact.ArtifactId, EnvironmentReference = r.Run.EnvironmentReference, CorrelationState = correlation,
        CorrelationBasis = correlation == "Confirmed" ? "Same fully-qualified name in the bound snapshot" : "Several source tests share the name", Currentness = currentness,
    };

    [Fact]
    public void Without_imported_results_execution_is_unavailable_never_not_executed_or_passed()
    {
        var x = RequirementTestExecutions.For(Link(), new SddLifecycleState(), "env-dev", "Development", false);
        x.State.Should().Be(RequirementTestExecutionState.Unavailable);
        x.Label.Should().Be("Execution evidence unavailable");
        x.Text.Should().Contain("not an executed test");
        RequirementTestExecutions.For(Link(), null, "env-dev", "Development", false).State.Should().Be(RequirementTestExecutionState.Unavailable);
    }

    [Fact]
    public void Every_run_in_the_environment_is_listed_and_the_current_result_is_the_newest_current_run_traceable_to_its_run()
    {
        var state = new SddLifecycleState();
        var older = Run(state, "run-1", "Development");
        var newer = Run(state, "run-2", "env-dev");
        state.TestExecutions.AddRange([Exec(older, "Passed", Mon), Exec(newer, "Failed", Mon.AddDays(1))]);

        var x = RequirementTestExecutions.For(Link(), state, "env-dev", "Development", false);
        x.State.Should().Be(RequirementTestExecutionState.Observed);
        x.Runs.Select(r => (r.RunLabel, r.Outcome)).Should().Equal([("run-2", "Failed"), ("run-1", "Passed")], "multiple executions are never collapsed; newest first");
        x.Current!.Should().Match<TestRunResult>(r => r.RunLabel == "run-2" && r.Outcome == "Failed" && r.ArtifactFile == "run-2.trx" && r.RunId == newer.Run.RunId,
            "a failed result is traceable to its originating run");
        x.SelectionRule.Should().Contain("newest run in this environment");
        x.Label.Should().Be("Failed");
    }

    [Fact]
    public void A_result_whose_test_source_changed_later_stays_history_and_is_not_the_current_result()
    {
        var state = new SddLifecycleState();
        var stale = Run(state, "run-new", "env-dev");
        var current = Run(state, "run-old", "env-dev");
        state.TestExecutions.AddRange([Exec(stale, "Passed", Mon.AddDays(2), currentness: "PotentiallyStale"), Exec(current, "Failed", Mon)]);
        var x = RequirementTestExecutions.For(Link(), state, "env-dev", null, false);
        x.Runs.Should().HaveCount(2);
        x.Current!.RunLabel.Should().Be("run-old");
    }

    [Fact]
    public void Runs_of_other_environments_or_without_an_environment_never_answer_for_the_selected_one()
    {
        var state = new SddLifecycleState();
        state.TestExecutions.AddRange([Exec(Run(state, "qa-run", "env-qa"), "Passed", Mon), Exec(Run(state, "local-run", null), "Passed", Mon)]);

        var x = RequirementTestExecutions.For(Link(), state, "env-dev", "Development", false);
        x.State.Should().Be(RequirementTestExecutionState.NotExecuted, "QA's pass and an unrecorded environment are not Development evidence");
        x.Current.Should().BeNull();
        x.OtherEnvironments.Select(r => (r.RunLabel, r.Environment, r.EnvironmentRecorded)).Should().BeEquivalentTo(
            [("qa-run", "env-qa", true), ("local-run", "Environment not recorded", false)]);
        x.Text.Should().Contain("do not count here");

        var all = RequirementTestExecutions.For(Link(), state, null, null, true);
        all.State.Should().Be(RequirementTestExecutionState.Observed);
        all.Runs.Should().HaveCount(2, "All environments lists every run, each with its own environment");
        all.OtherEnvironments.Should().BeEmpty();
    }

    [Fact]
    public void A_result_matching_only_by_name_is_shown_as_an_unattributed_candidate_and_never_as_this_tests_outcome()
    {
        var state = new SddLifecycleState();
        state.TestExecutions.Add(Exec(Run(state, "run-1", "env-dev"), "Failed", Mon, correlation: "Ambiguous"));
        // A confirmed result of another test identity with the same display name is a different test.
        state.TestExecutions.Add(Exec(Run(state, "run-2", "env-dev"), "Passed", Mon, testId: "Other-repo::X::" + Name));

        var x = RequirementTestExecutions.For(Link(), state, "env-dev", null, false);
        x.State.Should().Be(RequirementTestExecutionState.NotExecuted);
        x.Current.Should().BeNull("no pass/fail is attributed without identity");
        x.Unattributed.Should().ContainSingle().Which.Should().Match<TestRunResult>(r => r.Attribution == TestExecutionAttribution.NameOnly && r.Basis.StartsWith("Name only (Ambiguous)"));
        x.Runs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(new[] { "Passed", "Passed" }, new[] { "Completed", "Completed" }, "Passed")]
    [InlineData(new[] { "Passed", "Failed" }, new[] { "Completed", "Completed" }, "Failed")]
    [InlineData(new[] { "Unknown", "Unknown" }, new[] { "NotExecuted", "NotExecuted" }, "Skipped")]
    [InlineData(new[] { "Unknown" }, new[] { "TimedOut" }, "Timed out (no verdict)")]
    [InlineData(new[] { "Passed", "Unknown" }, new[] { "Completed", "Aborted" }, "Aborted (no verdict)")]
    [InlineData(new[] { "Inconclusive" }, new[] { "Completed" }, "Inconclusive")]
    public void Theory_rows_fold_into_one_run_outcome_that_is_never_upgraded_to_passed(string[] results, string[] states, string outcome) =>
        RequirementTestExecutions.Outcome([.. results.Zip(states, (r, s) => new SddTestExecutionEvidence { Result = r, ExecutionState = s })]).Should().Be(outcome);

    [Fact]
    public void Skipped_and_failed_map_to_the_shared_status_vocabulary()
    {
        RequirementTestExecutions.Status("Passed").Should().Be(SemanticStatus.Verified);
        RequirementTestExecutions.Status("Failed").Should().Be(SemanticStatus.Failed);
        RequirementTestExecutions.Status("Skipped").Should().Be(SemanticStatus.NotRun);
        RequirementTestExecutions.Status("Timed out (no verdict)").Should().Be(SemanticStatus.Unknown);
    }
}
