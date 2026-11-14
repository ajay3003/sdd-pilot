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
/// The shared scope filter on Critical E2E Regression: a selected scope narrows the flow list to the flows it names by flow id, counts what
/// the overview already knows per flow (prepared, executable, executed, passed) separately for critical and smoke/diagnostic flows, and never
/// starts, enables or authorizes a run.
/// </summary>
public sealed class CriticalE2EScopeFilterTests : BunitContext
{
    private sealed class StubApi(CriticalE2EOverview overview) : ICriticalE2EApiService
    {
        public int Runs { get; private set; }
        public int Overviews { get; private set; }
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) { Overviews++; return Task.FromResult(overview); }
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

    private static CriticalE2EFlowSummary Flow(string id, CriticalE2EStatus status, CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical, bool enabled = true, bool configured = true) => new()
    {
        FlowId = id, Name = id, Module = "M01", Mode = CriticalE2EExecutionMode.CompanionBrowser, Kind = kind, Enabled = enabled, RequiredForRelease = kind == CriticalE2EFlowKind.Critical,
        Configured = configured, ConfigurationProblem = configured ? null : "The flow has no final business assertion.", LastStatus = status, StepCount = 3,
        LastRunAt = status == CriticalE2EStatus.NotRun ? null : new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero),
    };

    private static CriticalE2EOverview Overview(params CriticalE2EFlowSummary[] flows) => new()
    {
        EnvironmentId = "dev", EnvironmentName = "M2LB QA",
        Release = new CriticalE2EReleaseStatus { Disposition = CriticalE2EReleaseDisposition.NotEvaluated, ModulesCovered = 0, ModulesTotal = 1, Summary = "" },
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

    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ScopeB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private static ScopeSelectionMember Member(ScopeMemberKind kind, string id, params Guid[] scopes) =>
        new(new ScopeMember { Kind = kind, TargetId = id, Label = id }, [.. scopes.Select(s => new ScopeContribution(s, s == ScopeA ? "Report release" : "Smoke checks", 1))]);

    private IRenderedComponent<CriticalE2ERegression> Render(CriticalE2EOverview overview, ScopeSelection selection, out StubApi api)
    {
        api = new StubApi(overview);
        Services.AddSingleton<ICriticalE2EApiService>(api);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new StubContextFactory());
        var scopes = new Mock<IScopeRegistryApiService>();
        scopes.Setup(x => x.ListAsync("import:m2lb", It.IsAny<CancellationToken>())).ReturnsAsync(([
            new ScopeSummary { Id = ScopeA, Name = "Report release", State = ScopeState.Approved, ApprovedRevision = 1, CurrentRevision = 1 },
            new ScopeSummary { Id = ScopeB, Name = "Smoke checks", State = ScopeState.Approved, ApprovedRevision = 1, CurrentRevision = 1 }], (string?)null));
        scopes.Setup(x => x.SelectionAsync("import:m2lb", It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((selection, (string?)null));
        Services.AddSingleton(scopes.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import:m2lb", "M2LB")).Object);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"critical-e2e-regression?scope={ScopeA}&scope={ScopeB}");
        return Render<CriticalE2ERegression>();
    }

    private static ScopeSelection Selection() => new()
    {
        ProjectKey = "import:m2lb", Scopes = [new(ScopeA, "Report release", 1), new(ScopeB, "Smoke checks", 1)],
        Members =
        [
            Member(ScopeMemberKind.CriticalE2EFlow, "report-passed", ScopeA, ScopeB),
            Member(ScopeMemberKind.CriticalE2EFlow, "report-not-run", ScopeA),
            Member(ScopeMemberKind.CriticalE2EFlow, "report-unprepared", ScopeA),
            Member(ScopeMemberKind.CriticalE2EFlow, "smoke-page-opens", ScopeB),
            Member(ScopeMemberKind.CriticalE2EFlow, "flow-removed", ScopeB),
            Member(ScopeMemberKind.TestDataAsset, "tda_report", ScopeA),
        ],
    };

    private static CriticalE2EOverview FourFlowsAndOneOutside() => Overview(
        Flow("report-passed", CriticalE2EStatus.Passed),
        Flow("report-not-run", CriticalE2EStatus.NotRun),
        Flow("report-unprepared", CriticalE2EStatus.NotRun, configured: false),
        Flow("smoke-page-opens", CriticalE2EStatus.Passed, CriticalE2EFlowKind.Diagnostic),
        Flow("outside-scope", CriticalE2EStatus.Failed));

    private static string Text(IRenderedComponent<CriticalE2ERegression> page, string testId) => page.Find($"[data-testid={testId}]").TextContent.Trim();

    [Fact]
    public void A_selected_scope_shows_only_the_flows_it_names_and_counts_them_once()
    {
        var page = Render(FourFlowsAndOneOutside(), Selection(), out _);

        page.WaitForAssertion(() => page.FindAll("[data-testid=e2e-scope-summary]").Should().ContainSingle());
        page.FindAll("[data-testid^=e2e-flow-]").Where(e => e.TagName == "TR").Select(e => e.GetAttribute("data-testid"))
            .Should().BeEquivalentTo(new[] { "e2e-flow-report-passed", "e2e-flow-report-not-run", "e2e-flow-report-unprepared", "e2e-flow-smoke-page-opens" },
                "a named smoke flow is shown (marked Smoke); a flow the scope does not name is not");
        page.Find("[data-testid=e2e-kind-smoke-page-opens]").TextContent.Should().Be("Smoke");
        Text(page, "e2e-scope-named").Should().Contain("5 Critical E2E flows named by 2 selected scopes").And.Contain("1 named by more than one scope, counted once")
            .And.Contain("Whole environment: 5 flows");
        Text(page, "e2e-scope-missing").Should().Contain("1 named flow is not in this environment");
        Text(page, "e2e-scope-from-report-passed").Should().Be("from 2 scopes");
        page.FindAll("[data-testid=e2e-show-diagnostic]").Should().BeEmpty("the scope decides which smoke flows are shown");
    }

    [Fact]
    public void Counts_use_the_overviews_own_flow_data_and_keep_smoke_flows_out_of_business_e2e()
    {
        var page = Render(FourFlowsAndOneOutside(), Selection(), out _);
        page.WaitForAssertion(() => page.FindAll("[data-testid=e2e-scope-counts]").Should().ContainSingle());

        Text(page, "e2e-scope-critical-named").Should().Be("3");
        Text(page, "e2e-scope-critical-prepared").Should().Be("2", "a flow with a configuration problem is not prepared");
        Text(page, "e2e-scope-critical-executable").Should().Be("2");
        Text(page, "e2e-scope-critical-executed").Should().Be("1");
        Text(page, "e2e-scope-critical-passed").Should().Be("1", "the passed smoke flow is never counted as a critical / Business E2E pass");
        Text(page, "e2e-scope-diagnostic-named").Should().Be("1");
        Text(page, "e2e-scope-diagnostic-passed").Should().Be("1");
        Text(page, "e2e-scope-smoke-note").Should().Contain("never count toward Business E2E");
    }

    [Fact]
    public void Selecting_a_scope_never_runs_enables_or_authorizes_anything()
    {
        var page = Render(FourFlowsAndOneOutside(), Selection(), out var api);
        page.WaitForAssertion(() => page.FindAll("[data-testid=e2e-scope-summary]").Should().ContainSingle());

        api.Runs.Should().Be(0, "a scope is a filter, never a run trigger");
        var overviews = api.Overviews;
        page.Find("[data-testid=e2e-run-flow-report-unprepared]").HasAttribute("disabled").Should().BeTrue("being named by a scope does not make a flow runnable");
        Text(page, "e2e-scope-filter-note").Should().Contain("never starts, enables or authorizes a run");

        page.Find("[data-testid=scf-clear]").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid=e2e-scope-summary]").Should().BeEmpty());
        page.FindAll("[data-testid=e2e-flow-outside-scope]").Should().ContainSingle("without a scope the page is unchanged");
        page.FindAll("[data-testid=e2e-flow-smoke-page-opens]").Should().BeEmpty("smoke flows are hidden by default again");
        api.Runs.Should().Be(0);
        api.Overviews.Should().Be(overviews, "changing the scope selection does not reload or re-evaluate the environment");
    }

    [Fact]
    public void Presentation_counts_a_flow_named_by_two_scopes_once_and_reports_missing_ids()
    {
        var counts = CriticalE2EScopePresentation.Counts(FourFlowsAndOneOutside(), Selection());
        counts.Named.Should().Be(5);
        counts.SharedByScopes.Should().Be(1);
        counts.MissingIds.Should().Equal("flow-removed");
        (counts.Critical.Named + counts.Diagnostic.Named).Should().Be(4);
        CriticalE2EScopePresentation.FlowIds(null).Should().BeNull("no selection means no filter");
    }
}
