using BirkNext.CriticalE2E;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The Critical E2E surface. What is on screen without interacting: the release verdict, the coverage sentence, the two
/// execution modes with their counts and engine state, and the flow rows. Everything diagnostic is one disclosure away.
///
/// These tests assert WORDING and STATE rather than layout, because the information architecture is the contract: a
/// page that says "Ready" when a required flow has never run is wrong no matter how it is styled.
/// </summary>
public sealed class CriticalE2ERegressionTests : BunitContext
{
    private sealed class StubApi(CriticalE2EOverview overview) : ICriticalE2EApiService
    {
        public CriticalE2ERunFlowRequest? LastRun { get; private set; }
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) => Task.FromResult(overview);
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new List<CriticalE2EFlowDefinition>());
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => Task.FromResult(flow);
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default)
        {
            LastRun = request;
            return Task.FromResult(new CriticalE2ERunBatchResult { Overview = overview });
        }
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

    private static CriticalE2EFlowSummary Flow(string id, string module, CriticalE2EExecutionMode mode, CriticalE2EStatus status,
        bool matchesRelease = true, bool required = true, bool configured = true, string? problem = null) => new()
        {
            FlowId = id, Name = id, Module = module, Mode = mode, Enabled = true, RequiredForRelease = required,
            Configured = configured, ConfigurationProblem = problem, LastStatus = status,
            LastResultMatchesRelease = matchesRelease, LastBuildId = matchesRelease ? "12345" : "12344",
        };

    private static CriticalE2EOverview Overview(
        IEnumerable<CriticalE2EFlowSummary>? flows = null,
        CriticalE2EReleaseDisposition disposition = CriticalE2EReleaseDisposition.Ready,
        CriticalE2EEngineStatus? browser = null, CriticalE2EEngineStatus? integration = null,
        string summary = "All 2 required flow(s) passed against this build.")
    {
        var list = (flows ?? [Flow("tjeneste", "Tjeneste", CriticalE2EExecutionMode.CompanionBrowser, CriticalE2EStatus.Passed)]).ToList();
        return new CriticalE2EOverview
        {
            EnvironmentId = "dev", EnvironmentName = "M2LB QA",
            Release = new CriticalE2EReleaseStatus
            {
                Disposition = disposition, ModulesCovered = 5, ModulesTotal = 6, Summary = summary,
                RequiredFlowsPassed = list.Count(f => f.LastStatus == CriticalE2EStatus.Passed), RequiredFlowsTotal = list.Count,
            },
            Modules = [new CriticalE2EModuleCoverage { Module = "Tjeneste", Flows = list }],
            Flows = list,
            BrowserEngine = browser ?? new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = "Ready on https://m2lbqa.bufetat.no." },
            IntegrationEngine = integration ?? new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = "Authenticated API access is available." },
        };
    }

    private IRenderedComponent<CriticalE2ERegression> Render(CriticalE2EOverview overview, out StubApi api)
    {
        var stub = new StubApi(overview);
        api = stub;
        Services.AddSingleton<ICriticalE2EApiService>(stub);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new StubContextFactory());
        return base.Render<CriticalE2ERegression>();
    }

    [Fact]
    public void TheVerdictCoverageAndBothModesAreVisibleWithoutInteracting()
    {
        var page = Render(Overview(), out _);
        page.Find("[data-testid=e2e-verdict]").TextContent.Should().Contain("Ready");
        // Coverage is a claim about test design, stated separately from whether those tests pass.
        page.Find("[data-testid=e2e-coverage]").TextContent.Should().Contain("5 / 6 modules");
        page.Find("[data-testid=e2e-mode-browser]").Should().NotBeNull();
        page.Find("[data-testid=e2e-mode-integration]").Should().NotBeNull();
        page.Find("[data-testid=e2e-environment]").TextContent.Should().Contain("M2LB QA");
    }

    [Fact]
    public void ARequiredFlowThatHasNotRunReadsAsIncompleteNotFailed()
    {
        var page = Render(Overview(
            [Flow("tjeneste", "Tjeneste", CriticalE2EExecutionMode.CompanionBrowser, CriticalE2EStatus.NotRun)],
            CriticalE2EReleaseDisposition.Incomplete, summary: "1 required flow(s) have not run against this build yet."), out _);

        var verdict = page.Find("[data-testid=e2e-verdict]").TextContent;
        verdict.Should().Contain("Incomplete");
        verdict.Should().NotContain("Failed", "nothing is known to be wrong; nobody has looked yet");
    }

    [Fact]
    public void APassFromAnotherBuildReadsAsPendingAndSaysWhichBuildItCameFrom()
    {
        var page = Render(Overview(
            [Flow("tjeneste", "Tjeneste", CriticalE2EExecutionMode.CompanionBrowser, CriticalE2EStatus.Passed, matchesRelease: false)],
            CriticalE2EReleaseDisposition.Incomplete), out _);

        var cell = page.Find("[data-testid=e2e-status-tjeneste]").TextContent;
        cell.Should().Contain("Pending");
        cell.Should().Contain("12344", "a green run from another build must say which build it was");
    }

    [Fact]
    public void AFlowThatCannotRunAsConfiguredSaysSoRatherThanLookingLikeAFailingTest()
    {
        var page = Render(Overview(
            [Flow("tjeneste", "Tjeneste", CriticalE2EExecutionMode.CompanionBrowser, CriticalE2EStatus.NotRun,
                configured: false, problem: "The flow has no final business assertion.")]), out _);

        var cell = page.Find("[data-testid=e2e-status-tjeneste]").TextContent;
        cell.Should().Contain("Not runnable").And.Contain("final business assertion");
        cell.Should().NotContain("Failed");
    }

    [Fact]
    public void WhenTheCompanionIsNotConnectedTheBrowserRunIsDisabledAndSaysWhatToDo()
    {
        var page = Render(Overview(browser: new CriticalE2EEngineStatus
        {
            State = CriticalE2EEngineState.RequiresBrowserSession,
            Message = "The Browser Companion is not connected.",
            Action = "Open the application in your normal browser, sign in, and pair the Browser Companion.",
        }), out _);

        page.Find("[data-testid=e2e-run-browser]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=e2e-engine-browser]").TextContent.Should().Contain("not connected");
        // A greyed-out button with no explanation makes the user guess.
        page.Find("[data-testid=e2e-action-browser]").TextContent.Should().Contain("sign in");
    }

    [Fact]
    public void ProductionIsReportedAsOutOfScopeRatherThanAsSomethingToFix()
    {
        var page = Render(Overview(browser: new CriticalE2EEngineStatus
        {
            State = CriticalE2EEngineState.Unavailable,
            Message = "Automation is not permitted against a Production environment.",
        }), out _);

        page.Find("[data-testid=e2e-run-browser]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=e2e-engine-browser]").TextContent.Should().Contain("not permitted");
        page.FindAll("[data-testid=e2e-action-browser]").Should().BeEmpty("there is nothing for the user to do about it");
    }

    [Fact]
    public void RunningBrowserRegressionAsksForThatModeOnly()
    {
        var page = Render(Overview(), out var api);
        page.Find("[data-testid=e2e-run-browser]").Click();
        api.LastRun!.Mode.Should().Be(CriticalE2EExecutionMode.CompanionBrowser);
        api.LastRun.FlowId.Should().BeNull();
    }

    [Fact]
    public void RunningOneFlowAsksForThatFlowOnly()
    {
        var page = Render(Overview(), out var api);
        page.Find("[data-testid=e2e-run-flow-tjeneste]").Click();
        api.LastRun!.FlowId.Should().Be("tjeneste");
    }

    [Fact]
    public void TheDetailIsCollapsedByDefaultSoTheDefaultViewIsNotADiagnosticsDump()
    {
        var page = Render(Overview(), out _);
        // ReviewDisclosure keeps its body in the DOM and hides it, so "collapsed" is an attribute, not an absence.
        page.FindAll("[data-testid=e2e-modules] [hidden]").Should().NotBeEmpty();
    }

    [Fact]
    public void ACompanionBrowserFlowIsShownAsPendingAttendedValidationForAPipeline()
    {
        var page = Render(Overview(), out _);
        var note = page.Find("[data-testid=e2e-pipeline-note]").TextContent;
        note.Should().Contain("pending attended browser validation");
        // A pipeline that cannot sign in is reporting its own limitation, not a defect in M2LB.
        note.Should().NotContain("failed");
    }

    [Fact]
    public void AnUnreachableBackendIsStatedPlainlyRatherThanRenderingAnEmptyGate()
    {
        Services.AddSingleton<ICriticalE2EApiService>(new ThrowingApi());
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new StubContextFactory());
        var page = base.Render<CriticalE2ERegression>();
        page.Find("[data-testid=e2e-error]").TextContent.Should().Contain("not reachable");
    }

    private sealed class ThrowingApi : ICriticalE2EApiService
    {
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) => throw new HttpRequestException("down");
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => throw new HttpRequestException("down");
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => throw new HttpRequestException("down");
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => throw new HttpRequestException("down");
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default) => throw new HttpRequestException("down");
        public Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken ct = default) => throw new HttpRequestException("down");
    }
}
