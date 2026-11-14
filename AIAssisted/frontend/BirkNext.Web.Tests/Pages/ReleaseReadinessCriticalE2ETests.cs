using BirkNext.CriticalE2E;
using BirkNext.Governance;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Components;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Release Readiness' Critical E2E section: it shows the gate evaluation the backend recorded (state, policy version, build, environment, per-flow
/// eligibility, Business E2E contribution vs mandatory release blocker), sends the build being released, and drills down to Critical E2E Regression for
/// the same environment and build. A frozen report shows what it recorded.
/// </summary>
public sealed class ReleaseReadinessCriticalE2ETests : BunitContext
{
    static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    static CriticalE2EReadinessFlow Smoke(bool failed) => new()
    {
        FlowId = "smoke", Name = "Smoke", Module = "M02", TestType = CriticalE2ETestType.UiSmoke, Criticality = CriticalE2EFlowKind.Critical, RequiredForRelease = true,
        ReleaseReady = true, Readiness = "Release-ready", ExecutionResult = failed ? CriticalE2EStatus.Failed : CriticalE2EStatus.Passed, RunId = "run-smoke",
        Eligibility = CriticalE2EGateEligibilityKind.Never, EligibilityReason = "UI smoke — never counts toward Business E2E", BusinessE2EContribution = false,
        MandatoryReleaseBlocker = failed, BlockerReason = failed ? "Failed required flow on this build" : null,
    };

    static CriticalE2EReadinessFlow Business() => new()
    {
        FlowId = "biz", Name = "Send report", Module = "EN01", TestType = CriticalE2ETestType.BusinessE2E, RequiredForRelease = true, ReleaseReady = true,
        Readiness = "Release-ready", ExecutionResult = CriticalE2EStatus.Passed, RunId = "run-biz", Eligibility = CriticalE2EGateEligibilityKind.Counts,
        EligibilityReason = "Counts — Business E2E, critical, release-ready and passed on this build", BusinessE2EContribution = true,
    };

    static CriticalE2EReadinessEvaluation Evaluation(CriticalE2EReadinessState state, string? build = "RC60", string policy = CriticalE2EGatePolicy.Current,
        CriticalE2EReleaseDisposition disposition = CriticalE2EReleaseDisposition.Ready, params CriticalE2EReadinessFlow[] flows) => new()
    {
        State = state, PolicyVersion = policy, Disposition = disposition, EnvironmentId = "dev", BuildId = build, BuildSource = build is null ? null : "Manual",
        ReadinessEnvironmentId = "dev", ReadinessBuildId = build, EvaluatedAt = Now, GateSummary = "summary", RequiredFlows = flows.Length,
        BlockingFlows = flows.Count(f => f.MandatoryReleaseBlocker), Flows = [.. flows],
        StaleReason = state == CriticalE2EReadinessState.Stale ? "The Critical E2E evaluation is for build RC59, not this readiness's build RC60." : null,
    };

    static ReleaseReadinessAssessment Assessment(CriticalE2EReadinessEvaluation? e) => new(new("dev", "Whole project", [], Guid.NewGuid()), GateDecision.NotAssessed, "reason",
        [
            new(ReadinessDimensionKey.CriticalE2E, "Critical E2E", DimensionApplicability.Required, e?.State switch
                {
                    CriticalE2EReadinessState.Passed => GateDecision.Ready,
                    CriticalE2EReadinessState.Blocked => GateDecision.Blocked,
                    _ => GateDecision.NotAssessed,
                }, "Critical E2E summary", [], null,
                $"critical-e2e-regression?environment=dev{(e?.ReadinessBuildId is { } b ? $"&build={b}" : "")}") { CriticalE2E = e },
        ], [], [], "release/1", "governance/1", Now) { BuildId = e?.ReadinessBuildId };

    private async Task<Mock<ICanonicalInterpretationApiService>> Setup(Func<ReleaseEvaluationContext?, ReleaseReadinessAssessment> assess)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.ReleaseReadinessAsync("dev", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<ReleaseEvaluationContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<string>? _, string? _, ReleaseEvaluationContext? context, CancellationToken _) => (assess(context), (string?)null));
        api.Setup(a => a.ReleaseReportsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        Services.AddSingleton(api.Object);
        return api;
    }

    [Fact]
    public async Task Passed_shows_policy_build_and_flows_and_drills_down_with_the_same_environment_and_build()
    {
        await Setup(_ => Assessment(Evaluation(CriticalE2EReadinessState.Passed, flows: [Business()])));
        Services.GetRequiredService<NavigationManager>().NavigateTo("release-readiness?build=RC60");
        var cut = Render<ReleaseReadiness>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-e2e-dimension]").GetAttribute("data-state").Should().Be("passed"));
        cut.Find("[data-testid=rr-e2e-policy]").TextContent.Should().Be(CriticalE2EGatePolicy.Current);
        cut.Find("[data-testid=rr-e2e-build]").TextContent.Should().Contain("RC60");
        cut.Find("[data-testid=rr-e2e-environment]").TextContent.Should().Be("dev");
        var flow = cut.Find("[data-testid=rr-e2e-flow]");
        (flow.GetAttribute("data-flow-id"), flow.GetAttribute("data-eligibility"), flow.GetAttribute("data-blocker")).Should().Be(("biz", "counts", "false"));
        cut.Find("[data-testid=rr-e2e-flow-result]").TextContent.Should().Be("Passed · run run-biz");
        cut.Find("[data-testid=rr-e2e-open]").GetAttribute("href").Should().Be("critical-e2e-regression?environment=dev&build=RC60");
        cut.Find("[data-testid=rr-e2e-build-input]").GetAttribute("value").Should().Be("RC60", "the ?build= query parameter is the build being released");
    }

    [Fact]
    public async Task A_failed_required_UI_smoke_flow_is_a_blocker_without_Business_E2E_contribution()
    {
        await Setup(_ => Assessment(Evaluation(CriticalE2EReadinessState.Blocked, disposition: CriticalE2EReleaseDisposition.Blocked, flows: [Smoke(failed: true), Business()])));
        var cut = Render<ReleaseReadiness>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-e2e-dimension]").GetAttribute("data-state").Should().Be("blocked"));
        var smoke = cut.Find("[data-testid=rr-e2e-flow][data-flow-id=smoke]");
        (smoke.GetAttribute("data-eligibility"), smoke.GetAttribute("data-blocker")).Should().Be(("never", "true"));
        smoke.QuerySelector("[data-testid=rr-e2e-flow-business]")!.TextContent.Should().Be("Business E2E contribution: No");
        smoke.QuerySelector("[data-testid=rr-e2e-flow-blocker]")!.TextContent.Should().Be("Mandatory release blocker: Yes (failed required flow on this build)");
        cut.Find("[data-testid=rr-e2e-flow][data-flow-id=biz]").QuerySelector("[data-testid=rr-e2e-flow-blocker]")!.TextContent.Should().Be("Mandatory release blocker: No");
        cut.Find("[data-testid=rr-e2e-blocking]").TextContent.Should().Be("1");
    }

    [Theory]
    [InlineData(CriticalE2EReadinessState.Incomplete, CriticalE2EReleaseDisposition.Incomplete, "incomplete", "Evaluated · incomplete")]
    [InlineData(CriticalE2EReadinessState.Incomplete, CriticalE2EReleaseDisposition.ReviewRequired, "incomplete", "Evaluated · review required")]
    [InlineData(CriticalE2EReadinessState.Stale, CriticalE2EReleaseDisposition.Ready, "stale", "Stale · other build or environment")]
    [InlineData(CriticalE2EReadinessState.NotAssessed, CriticalE2EReleaseDisposition.NotEvaluated, "not-assessed", "Not assessed")]
    public async Task Each_state_is_named_and_marked(CriticalE2EReadinessState state, CriticalE2EReleaseDisposition disposition, string token, string label)
    {
        await Setup(_ => Assessment(Evaluation(state, disposition: disposition, flows: [Business()])));
        var cut = Render<ReleaseReadiness>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-e2e-dimension]").GetAttribute("data-state").Should().Be(token));
        cut.Find("[data-testid=rr-e2e-state]").TextContent.Should().Be(label);
        if (state == CriticalE2EReadinessState.Stale) cut.Find("[data-testid=rr-e2e-stale]").TextContent.Should().Contain("build RC59");
    }

    [Fact]
    public async Task Without_a_recorded_evaluation_it_is_not_assessed_and_still_links_to_the_page()
    {
        await Setup(_ => Assessment(null));
        var cut = Render<ReleaseReadiness>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-e2e-dimension]").GetAttribute("data-state").Should().Be("not-assessed"));
        cut.FindAll("[data-testid=rr-e2e-flow]").Should().BeEmpty();
        cut.Find("[data-testid=rr-e2e-open]").GetAttribute("href").Should().Be("critical-e2e-regression?environment=dev");
    }

    [Fact]
    public async Task Entering_a_build_re_assesses_for_that_build_and_updates_the_drill_down()
    {
        var api = await Setup(context => Assessment(context?.BuildId is { } build
            ? Evaluation(CriticalE2EReadinessState.Incomplete, build, disposition: CriticalE2EReleaseDisposition.Incomplete, flows: [Business()])
            : Evaluation(CriticalE2EReadinessState.NotAssessed, null, disposition: CriticalE2EReleaseDisposition.NotEvaluated, flows: [Business()])));
        var cut = Render<ReleaseReadiness>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-e2e-dimension]").GetAttribute("data-state").Should().Be("not-assessed"));

        cut.Find("[data-testid=rr-e2e-build-input]").Input("RC61");
        cut.Find("[data-testid=rr-e2e-build-apply]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-e2e-dimension]").GetAttribute("data-state").Should().Be("incomplete"));
        cut.Find("[data-testid=rr-e2e-open]").GetAttribute("href").Should().Be("critical-e2e-regression?environment=dev&build=RC61");
        api.Verify(a => a.ReleaseReadinessAsync("dev", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.Is<ReleaseEvaluationContext?>(c => c != null && c.BuildId == "RC61"),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_frozen_report_shows_its_recorded_policy_1_verdict_and_is_never_re_evaluated()
    {
        var api = await Setup(_ => Assessment(null));
        var id = Guid.NewGuid();
        var frozen = Assessment(Evaluation(CriticalE2EReadinessState.Passed, "RC50", CriticalE2EGatePolicy.Legacy, flows: [Business()]));
        api.Setup(a => a.ReleaseReportAsync("dev", id, It.IsAny<CancellationToken>())).ReturnsAsync((new ReleaseReadinessReport(id, "RC50", Now, frozen, []), (string?)null));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"release-readiness?report={id}");
        var cut = Render<ReleaseReadiness>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-e2e-dimension]").GetAttribute("data-frozen").Should().Be("true"));
        cut.Find("[data-testid=rr-e2e-dimension]").GetAttribute("data-state").Should().Be("passed");
        cut.Find("[data-testid=rr-e2e-policy]").TextContent.Should().Be(CriticalE2EGatePolicy.Legacy);
        cut.Find("[data-testid=rr-e2e-evaluated]").TextContent.Should().Contain("frozen, never re-evaluated");
        cut.FindAll("[data-testid=rr-e2e-build-input]").Should().BeEmpty("a frozen report's build is what it recorded");
        cut.Find("[data-testid=rr-e2e-open]").GetAttribute("href").Should().Be("critical-e2e-regression?environment=dev&build=RC50");
        api.Verify(a => a.ReleaseReadinessAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<ReleaseEvaluationContext?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void A_frozen_verdict_under_another_policy_or_build_is_not_comparable()
    {
        var current = Evaluation(CriticalE2EReadinessState.Passed);
        CriticalE2EReleaseReadinessView.Incompatibility(Evaluation(CriticalE2EReadinessState.Passed, policy: CriticalE2EGatePolicy.Legacy), current).Should().StartWith("Stale:").And.Contain(CriticalE2EGatePolicy.Legacy);
        CriticalE2EReleaseReadinessView.Incompatibility(Evaluation(CriticalE2EReadinessState.Passed, build: "RC59"), current).Should().Contain("build RC59");
        CriticalE2EReleaseReadinessView.Incompatibility(Evaluation(CriticalE2EReadinessState.Passed), current).Should().BeNull();
    }
}
