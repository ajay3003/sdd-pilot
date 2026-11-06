using BirkNext.Integrations;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// M01 Kode 6/7: static review independent of runtime verification, one per-family matrix over the existing checks. A guard found in source is
/// a mechanism, a CDC mapping is not propagation, no finding is never Passed, blocked runtime never blocks the static review, and a completed
/// static review never unblocks runtime. Synthetic fixtures only — no identifier, token or endpoint value appears.
/// </summary>
public sealed class SecurityControlMatrixTests
{
    private static ClassificationCheck Static(string id, ClassificationArea area, ClassificationState state) =>
        new() { CheckId = id, Area = area, State = state, Title = id, Provenance = IntegrationEvidenceSource.SourceCode };

    private static ClassificationCheck Live(string id, ClassificationArea area, ClassificationState state) =>
        new() { CheckId = id, Area = area, State = state, Title = id, Provenance = IntegrationEvidenceSource.NetworkProbe };

    private static ClassificationReviewResult Result(params ClassificationCheck[] checks) => new() { RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.UnixEpoch, Checks = [.. checks] };

    private static SecurityControlMatrix.Row Row(ClassificationReviewResult r, string id) => SecurityControlMatrix.Build(r).Single(x => x.Family.Id == id);

    [Fact]
    public void Seven_families_each_traceable_to_existing_checks()
    {
        SecurityControlMatrix.Families.Select(f => f.Id).Should().Equal("SEC-01", "SEC-02", "SEC-03", "SEC-04", "SEC-05", "SEC-06", "SEC-07");
        var r = Result(Static("model-graded-levels", ClassificationArea.Model, ClassificationState.SourceVerified),
            Static("access-authz-fail-closed", ClassificationArea.ChildAccess, ClassificationState.SourceVerified));
        Row(r, "SEC-01").CheckIds.Should().Equal("model-graded-levels");
        Row(r, "SEC-07").CheckIds.Should().Contain("access-authz-fail-closed", "a check can support more than one family");
        Row(r, "SEC-03").CheckIds.Should().Contain("access-authz-fail-closed");
    }

    [Fact]
    public void A_guard_found_in_source_without_runtime_is_evidence_of_a_mechanism_never_passed()
    {
        var row = Row(Result(Static("guard-logic", ClassificationArea.Guard, ClassificationState.SourceVerified), Static("cdc-router", ClassificationArea.Pipeline, ClassificationState.SourceVerified)), "SEC-02");
        row.Static.Should().Be("Evidence found");
        row.Runtime.Should().Be("Not assessed");
        row.Result.Should().Be("Not assessed", "a CDC mapping in source is not verified propagation");
        row.ResidualRisk.Should().Contain("Mechanism found in source").And.Contain("a mapping in source is not propagation");
        row.NextAction.Should().StartWith("Runtime verification needed");
    }

    [Fact]
    public void No_static_finding_is_not_passed_and_missing_evidence_is_not_a_defect()
    {
        var r = Result(Static("model-reference", ClassificationArea.Model, ClassificationState.SourceVerified));
        var audit = Row(r, "SEC-06");
        audit.Static.Should().Be("No supporting evidence found");
        audit.Result.Should().Be("Not assessed");
        audit.ResidualRisk.Should().StartWith("Missing source evidence").And.NotContain("defect");
        audit.NextAction.Should().Contain("Bind source evidence");
        Row(Result(), "SEC-01").Static.Should().Be("Not assessed", "no source was reviewed at all");
    }

    [Fact]
    public void A_potential_static_issue_is_not_a_confirmed_defect()
    {
        var row = Row(Result(Static("graphql-endpoint-auth", ClassificationArea.GraphQL, ClassificationState.IssueDetected)), "SEC-03");
        row.Static.Should().Be("Potential issue");
        row.Result.Should().Be("Not assessed");
        row.ResidualRisk.Should().StartWith("Potential static concern (not a confirmed defect)");
    }

    [Fact]
    public void Passed_needs_runtime_checks_that_all_passed_and_a_mismatch_is_failed()
    {
        var passed = Row(Result(Static("access-child-specific", ClassificationArea.ChildAccess, ClassificationState.SourceVerified),
            Live("live-direct-unauthorized-1", ClassificationArea.DirectAccess, ClassificationState.Pass), Live("live-direct-authorized-1", ClassificationArea.DirectAccess, ClassificationState.Pass)), "SEC-03");
        passed.Result.Should().Be("Passed");
        passed.ResidualRisk.Should().Be("Verified for the tested identities and surfaces only.");
        var failed = Row(Result(Live("live-direct-unauthorized-1", ClassificationArea.DirectAccess, ClassificationState.Fail)), "SEC-03");
        failed.Runtime.Should().Be("Failed");
        failed.Result.Should().Be("Failed");
        var blocked = Row(Result(Live("live-search-1", ClassificationArea.Search, ClassificationState.NotTested)), "SEC-03");
        blocked.Result.Should().Be("Partial", "not executed is neither passed nor failed");
    }

    // ── Independent readiness and steps ───────────────────────────────────────────────────────────────────────────────

    private static ClassificationPresentation.MissingView Needed(params (ClassificationMissingGroup Group, ClassificationReadiness Status, string Title)[] rows) =>
        new([.. rows.Select((r, i) => new ClassificationReadinessRow { Id = $"r{i}", Group = r.Group, Status = r.Status, Title = r.Title })], false, [], []);

    [Fact]
    public void Static_review_is_ready_despite_missing_runtime_identities_and_runtime_stays_blocked()
    {
        var needed = Needed((ClassificationMissingGroup.SourceEvidence, ClassificationReadiness.Ready, "Source"),
            (ClassificationMissingGroup.RequiredForLiveChecks, ClassificationReadiness.Missing, "Unauthorized test identity"),
            (ClassificationMissingGroup.RequiredForLiveChecks, ClassificationReadiness.Missing, "Approved test context"));
        var steps = SecurityReviewWorkflow.Derive(true, true, needed, false, "No approved DEV/QA test context is configured.", null);
        var stat = steps.Single(s => s.Id == "static");
        stat.State.Should().Be(GuidedStepState.Current);
        stat.StatusLabel.Should().Be("Ready for static review");
        stat.Explanation.Should().Contain("mechanism exists, not that Kode 6/7 access control is correct");
        steps.Single(s => s.Id == "runtime-context").State.Should().Be(GuidedStepState.Blocked);
        steps.Single(s => s.Id == "runtime-context").Explanation.Should().Contain("2 runtime item(s) missing").And.Contain("never blocks the static review");
        steps.Single(s => s.Id == "runtime").StatusLabel.Should().Be("RUNTIME UNVERIFIED");
    }

    [Fact]
    public void A_completed_static_review_never_unblocks_runtime()
    {
        var needed = Needed((ClassificationMissingGroup.RequiredForLiveChecks, ClassificationReadiness.Missing, "Approved test context"));
        var done = Result(Static("model-reference", ClassificationArea.Model, ClassificationState.SourceVerified));
        var steps = SecurityReviewWorkflow.Derive(true, true, needed, false, "No approved DEV/QA test context is configured.", done);
        steps.Single(s => s.Id == "runtime").State.Should().Be(GuidedStepState.Blocked);
        steps.Single(s => s.Id == "matrix").State.Should().Be(GuidedStepState.Done);
        steps.Single(s => s.Id == "matrix").StatusLabel.Should().Contain("0 passed");
    }

    [Fact]
    public void An_approved_context_without_a_runtime_run_is_waiting_not_done_and_never_runs_by_itself()
    {
        var steps = SecurityReviewWorkflow.Derive(true, true, Needed((ClassificationMissingGroup.SourceEvidence, ClassificationReadiness.Ready, "Source")), true, "", null);
        var runtime = steps.Single(s => s.Id == "runtime");
        runtime.State.Should().Be(GuidedStepState.Waiting, "configured is not executed");
        runtime.StatusLabel.Should().Be("Not run — start it explicitly");
    }

    [Fact]
    public void Static_review_with_partial_source_has_limitations_and_without_source_is_blocked()
    {
        var partial = Needed((ClassificationMissingGroup.SourceEvidence, ClassificationReadiness.Missing, "CDC pipeline source"));
        SecurityReviewWorkflow.StaticReadiness(true, partial).Label.Should().Be("Ready with evidence limitations");
        SecurityReviewWorkflow.StaticReadiness(true, partial).Detail.Should().Contain("CDC pipeline source");
        SecurityReviewWorkflow.StaticReadiness(false, partial).Label.Should().Be("Blocked — insufficient applicable source evidence");
    }
}
