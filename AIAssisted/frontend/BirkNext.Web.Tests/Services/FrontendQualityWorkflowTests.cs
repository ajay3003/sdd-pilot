using BirkNext.RuntimeAuthentication;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Frontend Quality Review guided steps and the screenshot's "Auto resolves to None while the scope is Public + authenticated": the configured
/// mode that is not used says why, the saved scope is not changed silently, unavailable checks are never passed, and running is not reviewing.
/// </summary>
public sealed class FrontendQualityWorkflowTests
{
    private static FrontendQualityCapabilityRow Row(FrontendQualityEngineId id, string name, FrontendQualityEngineRequirement policy, FrontendQualityCapabilityState state) =>
        new(id, name, policy, state, null, null);

    private static readonly FrontendQualityCapabilityRow[] AllAvailable =
    [
        Row(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Enabled),
        Row(FrontendQualityEngineId.BrowserRuntime, "Browser Runtime", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Ready),
    ];

    private static RuntimeAuthenticationResolution Auth(RuntimeAuthenticationMode effective, RuntimeAuthenticationMode requested = RuntimeAuthenticationMode.Auto) =>
        new() { Requested = requested, Effective = effective, Reason = "The environment does not require sign-in." };

    private static FrontendQualityReviewReadiness Ready => new(FrontendQualityReviewReadinessLevel.Ready, "Ready to review", "Every required check can run.", []);

    private static IReadOnlyList<GuidedStep> Derive(FrontendReviewAccessScope scope, RuntimeAuthenticationResolution? auth, IReadOnlyList<FrontendQualityCapabilityRow>? rows = null,
        FrontendQualityReviewReadiness? readiness = null, string? runDisabled = null) =>
        FrontendQualityWorkflow.Derive("https://app.example.test/", scope, WcagProfiles.Norwegian, auth, rows ?? AllAvailable, readiness ?? Ready, true, runDisabled);

    private static GuidedStep Step(IReadOnlyList<GuidedStep> steps, string id) => steps.Single(s => s.Id == id);

    [Fact]
    public void Auto_resolving_to_None_under_Public_plus_authenticated_says_signed_in_pages_are_not_reviewed()
    {
        var steps = Derive(FrontendReviewAccessScope.PublicAndAuthenticated, Auth(RuntimeAuthenticationMode.None));
        var auth = Step(steps, "authentication");
        auth.State.Should().Be(GuidedStepState.Current);
        auth.StatusLabel.Should().Be("Signed-in pages not reviewed");
        auth.Explanation.Should().Contain("Auto chose None (The environment does not require sign-in)")
            .And.Contain("only public pages are reviewed").And.Contain("not assessed, never passed").And.Contain("Select Attended")
            .And.Contain("Authenticated API traffic captured by the proxy is not signed-in page content");
        Step(steps, "run").State.Should().Be(GuidedStepState.Waiting, "one current step; the run itself stays possible below");
    }

    [Fact]
    public void Public_only_or_Attended_need_no_explanation()
    {
        FrontendQualityWorkflow.ScopeAuthenticationMismatch(FrontendReviewAccessScope.PublicOnly, Auth(RuntimeAuthenticationMode.None)).Should().BeNull();
        FrontendQualityWorkflow.ScopeAuthenticationMismatch(FrontendReviewAccessScope.PublicAndAuthenticated, Auth(RuntimeAuthenticationMode.Attended)).Should().BeNull();
        var attended = Step(Derive(FrontendReviewAccessScope.PublicAndAuthenticated, Auth(RuntimeAuthenticationMode.Attended)), "authentication");
        attended.State.Should().Be(GuidedStepState.Done);
        attended.Explanation.Should().Contain("authenticated API traffic alone is not signed-in page content");
    }

    [Fact]
    public void Target_step_states_scope_and_the_48_criterion_profile()
    {
        var target = Step(Derive(FrontendReviewAccessScope.PublicOnly, null), "target");
        target.State.Should().Be(GuidedStepState.Done);
        target.Explanation.Should().Contain("Public only").And.Contain($"({WcagProfiles.Norwegian.CriterionIds.Count} criteria)").And.Contain("never widened");
        WcagProfiles.Norwegian.CriterionIds.Should().HaveCount(48);
    }

    [Fact]
    public void A_missing_required_engine_is_the_current_step_and_never_passed()
    {
        var rows = new[]
        {
            Row(FrontendQualityEngineId.StaticSecurity, "HTTP Security", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Enabled),
            Row(FrontendQualityEngineId.BrowserRuntime, "Browser Runtime", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Unavailable),
        };
        var checks = Step(Derive(FrontendReviewAccessScope.PublicOnly, null, rows), "checks");
        checks.State.Should().Be(GuidedStepState.Current);
        checks.StatusLabel.Should().Be("1 required check unavailable");
        checks.Explanation.Should().Contain("Browser Runtime").And.Contain("never shown as passed");
    }

    [Fact]
    public void A_missing_optional_engine_is_a_limitation_not_a_blocker()
    {
        var rows = AllAvailable.Append(Row(FrontendQualityEngineId.PassivePerformance, "Lighthouse", FrontendQualityEngineRequirement.Optional, FrontendQualityCapabilityState.Unavailable)).ToArray();
        var steps = Derive(FrontendReviewAccessScope.PublicOnly, null, rows, new(FrontendQualityReviewReadinessLevel.Limited, "Ready with limitations", "Optional checks unavailable.", []));
        Step(steps, "checks").State.Should().Be(GuidedStepState.Done);
        Step(steps, "checks").Explanation.Should().Contain("Lighthouse").And.Contain("never as passed");
        Step(steps, "readiness").StatusLabel.Should().Be("Ready with limitations");
        Step(steps, "run").State.Should().Be(GuidedStepState.Current);
    }

    [Fact]
    public void Blocked_readiness_and_a_run_blocker_keep_the_run_waiting_with_the_reason()
    {
        var steps = Derive(FrontendReviewAccessScope.PublicOnly, null, readiness: new(FrontendQualityReviewReadinessLevel.Blocked, "Cannot review", "No active engines.", []),
            runDisabled: "Enable at least one engine.");
        Step(steps, "readiness").State.Should().Be(GuidedStepState.Blocked);
        Step(steps, "run").DisabledReason.Should().Be("Enable at least one engine.");
        Step(steps, "findings").Explanation.Should().Contain("No findings is not a pass").And.Contain("do not establish WCAG conformance");
        Step(steps, "run").Explanation.Should().Contain("Execution completed is not review complete");
    }
}
