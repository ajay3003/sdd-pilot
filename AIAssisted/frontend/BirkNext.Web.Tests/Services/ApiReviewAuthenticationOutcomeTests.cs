using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeAuthentication;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The screenshot's "Automated review: Failed" with 0 operations executed: the engine blocked the only (protected) target before sending
/// anything because Auto resolved to None. Blocked is not failed; None never reaches a protected endpoint; a verified attended session is used
/// only when Attended is explicitly selected; zero execution is never success.
/// </summary>
public sealed class ApiReviewAuthenticationOutcomeTests
{
    private static FrontendAnalysisContext Context()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://app.example.test/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        return new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = "https://app.example.test/" };
    }

    private static ApiReviewTarget Target(string id, bool auth = true, string? contract = null) => new()
    {
        TargetId = id, ApiType = ApiReviewTargetType.Rest, Host = "api.example.test", BasePath = "/api/" + id, ServiceName = id, AuthRequired = auth, Selected = true, ContractSource = contract,
    };

    private static AuthenticatedReviewCapabilities Session(bool available) =>
        new() { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = available ? AuthenticatedApiContextStatus.Available : AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, PublicApi = true, AuthenticatedApi = available, AuthenticatedRest = available };

    private static RuntimeAuthenticationResolution Auth(RuntimeAuthenticationMode effective, RuntimeAuthenticationMode requested = RuntimeAuthenticationMode.Auto, string reason = "The environment does not require sign-in.") =>
        new() { Requested = requested, Effective = effective, Reason = reason };

    private static ApiReviewTargetResult Result(ApiReviewTarget target, ApiReviewTargetStatus status, ApiReviewAccessMode access) =>
        new() { Target = target, Status = status, AccessMode = access, Operations = [new ApiReviewOperationResult { Display = "GET /api", Executed = false, Result = ApiReviewCheckResult.Blocked }] };

    private static ApiReviewReport Report(params ApiReviewTargetResult[] targets) => new()
    {
        GeneratedAt = DateTimeOffset.UtcNow, Targets = [.. targets], Findings = [], ManualReviewItems = ["Write operations", "Access control", "Business correctness"], Limitations = [],
        Coverage = new ApiReviewCoverage { RestOperationsTotal = 2 },
    };

    [Fact]
    public void A_run_blocked_before_execution_is_blocked_not_failed_and_assesses_nothing()
    {
        var outcome = ApiReviewPostRunPresentation.Outcome(Report(Result(Target("svc"), ApiReviewTargetStatus.Blocked, ApiReviewAccessMode.Unavailable)));
        outcome.Execution.Should().Be("Blocked before execution");
        outcome.Assessment.Should().Be("Not assessed", "zero execution is never success and never 'no issues'");
        outcome.ServicesReviewed.Should().Be(0);
        outcome.RequestsExecuted.Should().Be(0);
        ApiReviewResultStates.Label(ApiReviewResultState.BlockedBeforeExecution).Should().Be("Blocked before execution");
        ApiReviewResultStates.IsCompleted(ApiReviewResultState.BlockedBeforeExecution).Should().BeFalse();
    }

    [Fact]
    public void A_blocked_run_headline_never_reads_completed_or_no_issues()
    {
        var report = Report(Result(Target("svc"), ApiReviewTargetStatus.Blocked, ApiReviewAccessMode.Unavailable));
        var outcome = ApiReviewPostRunPresentation.Outcome(report);
        var headline = ApiReviewPostRunPresentation.Headline(outcome, ApiReviewPostRunPresentation.Limitations(report));
        headline.Should().Be("Automated review was blocked before execution: no request was executed, so nothing was assessed.");
        headline.Should().NotContain("completed").And.NotContain("No automated issues");
    }

    [Fact]
    public void A_run_that_tried_and_executed_nothing_stays_failed()
    {
        var outcome = ApiReviewPostRunPresentation.Outcome(Report(Result(Target("svc"), ApiReviewTargetStatus.NotTested, ApiReviewAccessMode.AuthenticatedHttp)));
        outcome.Execution.Should().Be("Failed");
        outcome.Assessment.Should().Be("Incomplete");
    }

    [Fact]
    public void Mixed_blocked_and_failed_targets_are_a_failure_not_a_clean_block()
    {
        var result = ApiReviewPresentation.Result(Report(Result(Target("a"), ApiReviewTargetStatus.Blocked, ApiReviewAccessMode.Unavailable),
            Result(Target("b"), ApiReviewTargetStatus.NotTested, ApiReviewAccessMode.AuthenticatedHttp)));
        result.State.Should().Be(ApiReviewResultState.FailedToRun);
    }

    [Fact]
    public void Authentication_None_never_makes_a_protected_target_runnable_even_with_a_verified_session()
    {
        var eligibility = ApiReviewRunEligibility.Evaluate(Context(), [Target("svc")], ["svc"], Session(true), RuntimeAuthenticationMode.None);
        eligibility.Enabled.Should().BeFalse("nothing would execute");
        eligibility.Reason.Should().Contain("Authentication None is selected").And.Contain("Select Attended to use the verified session").And.Contain("nothing is transferred automatically");
        ApiReviewRunEligibility.Evaluate(Context(), [Target("svc")], ["svc"], Session(true), RuntimeAuthenticationMode.Attended).Enabled.Should().BeTrue();
        ApiReviewRunEligibility.Evaluate(Context(), [Target("svc")], ["svc"], Session(true)).Enabled.Should().BeTrue("unresolved mode keeps the earlier behaviour");
    }

    [Fact]
    public void Authentication_None_with_a_public_target_runs_and_states_that_protected_ones_are_blocked()
    {
        var eligibility = ApiReviewRunEligibility.Evaluate(Context(), [Target("public", auth: false), Target("svc")], ["public", "svc"], Session(true), RuntimeAuthenticationMode.None);
        eligibility.Enabled.Should().BeTrue();
        eligibility.Reason.Should().Contain("1 of 2 targets runnable").And.Contain("authentication None selected");
    }

    [Fact]
    public void A_protected_target_with_a_contract_still_gets_the_static_review_under_None()
    {
        var eligibility = ApiReviewRunEligibility.Evaluate(Context(), [Target("svc", contract: "https://api.example.test/openapi.json")], ["svc"], Session(true), RuntimeAuthenticationMode.None);
        eligibility.Enabled.Should().BeTrue("a limited static review is supported");
        eligibility.Reason.Should().Contain("authenticated checks will be skipped (authentication None selected)");
    }

    [Fact]
    public void Auto_resolving_to_None_explains_why_and_names_the_explicit_choice()
    {
        var note = ApiReviewPresentation.AuthenticationModeNote(Auth(RuntimeAuthenticationMode.None), [Target("svc")], Session(true));
        note.Should().Contain("Auto chose None (The environment does not require sign-in)").And.Contain("the API is a separate question")
            .And.Contain("blocked before execution — reported as blocked, never passed").And.Contain("select Attended to use it").And.Contain("Nothing is transferred automatically");
        ApiReviewPresentation.AuthenticationModeNote(Auth(RuntimeAuthenticationMode.None), [Target("svc")], Session(false))!.Should().Contain("sign in through Target Environment → Authentication");
        ApiReviewPresentation.AuthenticationModeNote(Auth(RuntimeAuthenticationMode.Attended), [Target("svc")], Session(true)).Should().BeNull();
        ApiReviewPresentation.AuthenticationModeNote(Auth(RuntimeAuthenticationMode.None), [Target("public", auth: false)], Session(true)).Should().BeNull("nothing protected is selected");
    }

    [Fact]
    public void Guided_steps_put_authentication_first_when_None_blocks_every_protected_target()
    {
        var targets = new[] { Target("svc") };
        var eligibility = ApiReviewRunEligibility.Evaluate(Context(), targets, ["svc"], Session(true), RuntimeAuthenticationMode.None);
        var steps = ApiReviewWorkflow.Derive(targets, ["svc"], Session(true), Auth(RuntimeAuthenticationMode.None), eligibility, "dev");
        steps.Select(s => s.Id).Should().Equal("targets", "authentication", "contracts", "readiness", "run", "findings");
        var auth = steps.Single(s => s.Id == "authentication");
        auth.State.Should().Be(GuidedStepState.Current);
        auth.Explanation.Should().Contain("blocked before execution, never passed").And.Contain("select Attended");
        steps.Single(s => s.Id == "readiness").State.Should().Be(GuidedStepState.Blocked);
        steps.Single(s => s.Id == "contracts").StatusLabel.Should().Be("0 of 1 with a contract");
        steps.Single(s => s.Id == "contracts").Explanation.Should().Contain("a limitation, not a blocker");
        steps.Single(s => s.Id == "run").State.Should().Be(GuidedStepState.Waiting);
        steps.Single(s => s.Id == "findings").Explanation.Should().Contain("No findings is not a passed review");
    }

    [Fact]
    public void With_Attended_and_a_session_the_run_is_the_next_step()
    {
        var targets = new[] { Target("svc", contract: "c") };
        var eligibility = ApiReviewRunEligibility.Evaluate(Context(), targets, ["svc"], Session(true), RuntimeAuthenticationMode.Attended);
        var steps = ApiReviewWorkflow.Derive(targets, ["svc"], Session(true), Auth(RuntimeAuthenticationMode.Attended, RuntimeAuthenticationMode.Attended), eligibility, "dev");
        steps.Single(s => s.Id == "authentication").State.Should().Be(GuidedStepState.Done);
        steps.Single(s => s.Id == "authentication").Explanation.Should().Contain("never shown or copied");
        steps.Single(s => s.Id == "run").State.Should().Be(GuidedStepState.Current);
    }

    [Fact]
    public void Each_manual_obligation_says_its_purpose_and_the_evidence_required()
    {
        ApiReviewPresentation.ManualReviewObligations.Should().HaveCount(3);
        ApiReviewPresentation.ManualReviewObligations.SelectMany(o => o.Details).Should().HaveCount(5);
        foreach (var o in ApiReviewPresentation.ManualReviewObligations)
        {
            var (purpose, evidence) = ApiReviewPresentation.ManualObligationGuide(o.Area);
            purpose.Should().NotBe("Review this area manually.", $"{o.Area} has its own guidance");
            evidence.Should().NotBeNullOrWhiteSpace();
        }
        ApiReviewPresentation.ManualObligationGuide("Authorization between roles and tenants").Purpose.Should().Contain("one rejected anonymous request proves nothing");
    }
}
