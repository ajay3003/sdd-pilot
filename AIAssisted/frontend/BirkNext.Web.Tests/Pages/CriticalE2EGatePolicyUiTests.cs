using BirkNext.CriticalE2E;
using BirkNext.Scopes;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The release gate shows, per flow, whether it counts toward Business E2E and why, and the gate policy version that produced the verdict.
/// Everything shown comes from the backend's evaluation; rendering never starts a run.
/// </summary>
public sealed class CriticalE2EGatePolicyUiTests : BunitContext
{
    private sealed class StubApi(CriticalE2EOverview overview) : ICriticalE2EApiService
    {
        public int Runs { get; private set; }
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) => Task.FromResult(overview);
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new List<CriticalE2EFlowDefinition>());
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => Task.FromResult(flow);
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default) { Runs++; return Task.FromResult(new CriticalE2ERunBatchResult { Overview = overview }); }
        public Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CriticalE2EElementPickResult { Status = CriticalE2EStatus.Blocked, Message = "Not used in these tests." });
    }

    private sealed class StubContextFactory : IFrontendAnalysisContextFactory
    {
        public Task<FrontendAnalysisContext> GetActiveContextAsync() => Task.FromResult(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB QA", EnvironmentType = FrontendEnvironmentType.QA },
        });
    }

    private static CriticalE2EFlowSummary Flow(string id, CriticalE2ETestType type, CriticalE2EGateEligibilityKind eligibility, string reason,
        string policy = CriticalE2EGatePolicy.Current) => new()
    {
        FlowId = id, Name = id, Module = "M01", Mode = CriticalE2EExecutionMode.CompanionBrowser, Kind = CriticalE2EFlowKind.Critical, TestType = type,
        Enabled = true, RequiredForRelease = true, Configured = true, LastStatus = CriticalE2EStatus.Passed, StepCount = 3,
        LastRunAt = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero),
        Gate = new CriticalE2EGateEligibility
        {
            PolicyVersion = policy, Eligibility = eligibility, Reason = reason,
            CountsTowardBusinessE2E = eligibility is CriticalE2EGateEligibilityKind.Counts or CriticalE2EGateEligibilityKind.NotExecuted,
        },
    };

    private static CriticalE2EOverview Overview(string policy, CriticalE2EReleaseDisposition disposition, string summary, params CriticalE2EFlowSummary[] flows) => new()
    {
        EnvironmentId = "dev", EnvironmentName = "M2LB QA",
        Release = new CriticalE2EReleaseStatus
        {
            BuildId = "RC60", Disposition = disposition, PolicyVersion = policy, Summary = summary, ModulesTotal = 1,
            RequiredFlowsTotal = flows.Length, RequiredFlowsLegacyReview = flows.Count(f => f.Gate.Eligibility == CriticalE2EGateEligibilityKind.LegacyReview),
        },
        Flows = [.. flows],
        Attended = new CriticalE2EAttendedReadiness
        {
            Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = "Ready." },
            CompanionConnected = true, OpenApprovedPages = 1, CurrentOrigin = "https://example.test", CurrentRoute = "/", ElementPickSupported = true,
        },
        BrowserEngine = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready },
        IntegrationEngine = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready },
        ElementPick = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready },
    };

    private static CriticalE2EOverview Mixed() => Overview(CriticalE2EGatePolicy.Current, CriticalE2EReleaseDisposition.ReviewRequired,
        "1 required flow(s) need review: legacy classification requires review before they can count toward Business E2E.",
        Flow("smoke", CriticalE2ETestType.UiSmoke, CriticalE2EGateEligibilityKind.Never, "UI smoke — never counts toward Business E2E"),
        Flow("functional", CriticalE2ETestType.UiFunctional, CriticalE2EGateEligibilityKind.FunctionalOnly, "UI functional — counts as functional coverage only, not Business E2E"),
        Flow("api", CriticalE2ETestType.IntegrationApi, CriticalE2EGateEligibilityKind.IntegrationOnly, "Integration/API — counts toward integration coverage only, not Business E2E"),
        Flow("business", CriticalE2ETestType.BusinessE2E, CriticalE2EGateEligibilityKind.Counts, "Counts — Business E2E, critical, release-ready and passed on this build"),
        Flow("business-pending", CriticalE2ETestType.BusinessE2E, CriticalE2EGateEligibilityKind.NotExecuted, "Business E2E — no passed run on this build, no credit"),
        Flow("legacy", CriticalE2ETestType.Unknown, CriticalE2EGateEligibilityKind.LegacyReview, CriticalE2EGatePolicy.LegacyReviewReason));

    private static readonly Guid Scope = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000b");

    private static ScopeSelection Selection(IEnumerable<string> ids) => new()
    {
        ProjectKey = "import:m2lb", Scopes = [new(Scope, "Report release", 1)],
        Members = [.. ids.Select(id => new ScopeSelectionMember(new ScopeMember { Kind = ScopeMemberKind.CriticalE2EFlow, TargetId = id, Label = id }, [new ScopeContribution(Scope, "Report release", 1)]))],
    };

    private IRenderedComponent<CriticalE2ERegression> Render(CriticalE2EOverview overview, out StubApi api)
    {
        api = new StubApi(overview);
        Services.AddSingleton<ICriticalE2EApiService>(api);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new StubContextFactory());
        var scopes = new Mock<IScopeRegistryApiService>();
        scopes.Setup(x => x.ListAsync("import:m2lb", It.IsAny<CancellationToken>())).ReturnsAsync(([
            new ScopeSummary { Id = Scope, Name = "Report release", State = ScopeState.Approved, ApprovedRevision = 1, CurrentRevision = 1 }], (string?)null));
        scopes.Setup(x => x.SelectionAsync("import:m2lb", It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Selection(overview.Flows.Select(f => f.FlowId)), (string?)null));
        Services.AddSingleton(scopes.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import:m2lb", "M2LB")).Object);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"critical-e2e-regression?scope={Scope}");
        return Render<CriticalE2ERegression>();
    }

    [Fact]
    public void EachFlowShowsWhetherItCountsTowardBusinessE2EAndWhy()
    {
        var page = Render(Mixed(), out var api);
        page.WaitForAssertion(() => page.FindAll("[data-testid=e2e-gate-eligibility]").Should().HaveCount(6));

        AngleSharp.Dom.IElement Eligibility(string flowId) => page.Find($"[data-testid=e2e-flow-{flowId}] [data-testid=e2e-gate-eligibility]");
        (Eligibility("smoke").GetAttribute("data-eligibility"), Eligibility("smoke").TextContent.Trim()).Should().Be(("never", "UI smoke — never counts toward Business E2E"));
        Eligibility("functional").GetAttribute("data-eligibility").Should().Be("functional-only");
        Eligibility("functional").TextContent.Should().Contain("functional coverage only");
        Eligibility("api").GetAttribute("data-eligibility").Should().Be("integration-only");
        Eligibility("business").GetAttribute("data-eligibility").Should().Be("counts");
        Eligibility("business").ClassList.Should().Contain("e2e-tone-good");
        Eligibility("business-pending").GetAttribute("data-eligibility").Should().Be("not-executed");
        Eligibility("legacy").GetAttribute("data-eligibility").Should().Be("legacy-review");
        Eligibility("legacy").TextContent.Trim().Should().StartWith("Legacy classification requires review");
        Eligibility("legacy").ClassList.Should().Contain("e2e-tone-warning");
        Eligibility("legacy").GetAttribute("data-policy").Should().Be(CriticalE2EGatePolicy.Current);
        api.Runs.Should().Be(0, "showing the gate never starts a run");
    }

    [Fact]
    public void TheVerdictNamesThePolicyVersion_AndALegacyReviewIsNeitherPassNorFailure()
    {
        var page = Render(Mixed(), out _);
        page.WaitForAssertion(() => page.FindAll("[data-testid=e2e-gate-policy]").Should().ContainSingle());

        var policy = page.Find("[data-testid=e2e-gate-policy]");
        policy.GetAttribute("data-policy").Should().Be("critical-e2e-gate/2");
        policy.TextContent.Trim().Should().Be("Gate policy critical-e2e-gate/2 · test type aware");
        page.Find("[data-testid=e2e-verdict-label]").TextContent.Trim().Should().Be("Release regression needs review");
        page.Find("[data-testid=e2e-verdict]").ClassList.Should().Contain("e2e-tone-warning");
    }

    [Fact]
    public void ABuildEvaluatedByTheLegacyPolicySaysSo()
    {
        var overview = Overview(CriticalE2EGatePolicy.Legacy, CriticalE2EReleaseDisposition.Ready, "All 1 required flow(s) passed against this build.",
            Flow("old", CriticalE2ETestType.Unknown, CriticalE2EGateEligibilityKind.Counts,
                "Counts — critical, release-ready and passed on this build (legacy policy: evidence recorded before test types)", CriticalE2EGatePolicy.Legacy));
        var page = Render(overview, out _);
        page.WaitForAssertion(() => page.FindAll("[data-testid=e2e-gate-policy]").Should().ContainSingle());

        page.Find("[data-testid=e2e-gate-policy]").TextContent.Trim()
            .Should().Be("Gate policy critical-e2e-gate/1 · legacy (criticality only, evidence recorded before test types)");
        page.Find("[data-testid=e2e-verdict-label]").TextContent.Trim().Should().Be("Release regression ready");
        var eligibility = page.Find("[data-testid=e2e-flow-old] [data-testid=e2e-gate-eligibility]");
        (eligibility.GetAttribute("data-eligibility"), eligibility.GetAttribute("data-policy")).Should().Be(("counts", "critical-e2e-gate/1"));
    }

    [Fact]
    public void ThePresentationMapsEligibilityToTokenLabelAndTone()
    {
        var view = CriticalE2EPresentation.GateView(new CriticalE2EGateEligibility { Eligibility = CriticalE2EGateEligibilityKind.LegacyReview, Reason = "" });
        (view.Token, view.Label, view.Reason, view.Tone).Should().Be(("legacy-review", "Legacy review", "Legacy review", CriticalE2ETone.Warning));
        CriticalE2EPresentation.GateView(new CriticalE2EGateEligibility { Eligibility = CriticalE2EGateEligibilityKind.Never, Reason = "x" }).Tone.Should().Be(CriticalE2ETone.Neutral);
        CriticalE2EPresentation.Rows(Mixed()).Single(r => r.FlowId == "smoke").Gate!.Token.Should().Be("never");
        CriticalE2EPresentation.GatePolicyLabel(new CriticalE2EReleaseStatus { PolicyVersion = CriticalE2EGatePolicy.Current })
            .Should().Be("Gate policy critical-e2e-gate/2 · test type aware");
    }
}
