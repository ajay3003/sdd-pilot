using BirkNext.CriticalE2E;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Critical E2E Regression, flow-first: the page is about critical user journeys run as attended browser automation, not
/// about execution technology. Configuration (Enabled, Required), outcome (Result) and browser readiness stay separate.
/// </summary>
public sealed class CriticalE2ERegressionTests : BunitContext
{
    private sealed class StubApi(CriticalE2EOverview overview, CriticalE2ERunBatchResult? runResult = null) : ICriticalE2EApiService
    {
        public CriticalE2ERunFlowRequest? LastRun { get; private set; }
        public int Overviews { get; private set; }
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) { Overviews++; LastOverview = request; return Task.FromResult(overview); }
        public CriticalE2EOverviewRequest? LastOverview { get; private set; }
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new List<CriticalE2EFlowDefinition>());
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => Task.FromResult(flow);
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default)
        {
            LastRun = request;
            return Task.FromResult(runResult ?? new CriticalE2ERunBatchResult { Overview = overview });
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

    private static CriticalE2EFlowSummary Flow(string id, string module = "M02", CriticalE2EStatus status = CriticalE2EStatus.Passed,
        bool matchesRelease = true, bool required = true, bool enabled = true, bool configured = true, string? problem = null,
        CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical, CriticalE2EExecutionMode mode = CriticalE2EExecutionMode.CompanionBrowser) => new()
        {
            FlowId = id, Name = id, Module = module, Mode = mode, Kind = kind, Enabled = enabled, RequiredForRelease = required,
            Configured = configured, ConfigurationProblem = problem, LastStatus = status, StepCount = 5,
            LastRunAt = status == CriticalE2EStatus.NotRun ? null : new DateTimeOffset(2026, 9, 24, 8, 11, 0, TimeSpan.Zero),
            LastResultMatchesRelease = matchesRelease, LastBuildId = matchesRelease ? "12345" : "12344",
        };

    private static readonly CriticalE2EAttendedReadiness ReadyBrowser = new()
    {
        Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = "Ready for attended browser steps." },
        CompanionConnected = true, OpenApprovedPages = 1, CurrentOrigin = "https://m2lbdev.bufetat.no", CurrentRoute = "/admin/general-roles", ElementPickSupported = true,
    };

    private static CriticalE2EOverview Overview(
        IEnumerable<CriticalE2EFlowSummary>? flows = null,
        CriticalE2EReleaseDisposition disposition = CriticalE2EReleaseDisposition.Ready,
        CriticalE2EAttendedReadiness? attended = null, CriticalE2EEngineStatus? integration = null,
        string summary = "All 1 required flow(s) passed against this build.", int covered = 1, int total = 1,
        List<CriticalE2ERunResult>? history = null)
    {
        var list = (flows ?? [Flow("m02-plassering")]).ToList();
        return new CriticalE2EOverview
        {
            EnvironmentId = "dev", EnvironmentName = "M2LB QA",
            Release = new CriticalE2EReleaseStatus { Disposition = disposition, ModulesCovered = covered, ModulesTotal = total, Summary = summary },
            Modules = list.Where(f => f.Kind == CriticalE2EFlowKind.Critical).GroupBy(f => f.Module)
                .Select(g => new CriticalE2EModuleCoverage { Module = g.Key, Flows = g.ToList() }).ToList(),
            Flows = list,
            Attended = attended ?? ReadyBrowser,
            BrowserEngine = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready },
            IntegrationEngine = integration ?? new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready, Message = "Authenticated API access is available." },
            ElementPick = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready },
            History = history ?? [],
        };
    }

    private IRenderedComponent<CriticalE2ERegression> Render(CriticalE2EOverview overview, out StubApi api, CriticalE2ERunBatchResult? run = null)
    {
        var stub = new StubApi(overview, run);
        api = stub;
        Services.AddSingleton<ICriticalE2EApiService>(stub);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new StubContextFactory());
        return base.Render<CriticalE2ERegression>();
    }

    private static string Text(IRenderedComponent<CriticalE2ERegression> page, string testId) => page.Find($"[data-testid={testId}]").TextContent.Trim();

    // ── Structure ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePageIsFlowFirst_NoExecutionModeHeroCards()
    {
        var page = Render(Overview(), out _);

        page.FindAll("[data-testid^=e2e-mode-]").Should().BeEmpty("execution technology is per-flow metadata, not the page's first question");
        page.Find("[data-testid=e2e-flow-table]").Should().NotBeNull();
        page.Find("#e2e-flows-heading").TextContent.Should().Be("Critical flows");
        var add = page.Find("[data-testid=e2e-add-flow]");
        add.ClassList.Should().Contain("btn-primary");
        add.Closest(".e2e-section-head").Should().NotBeNull("the primary action sits with the table heading, not below it");
        page.FindAll("[data-testid=e2e-summary] .e2e-card").Select(c => c.QuerySelector(".e2e-card-label")!.TextContent)
            .Should().Equal("Target", "Build", "Required flow coverage", "Attended browser");
    }

    [Fact]
    public void TheTableColumnsAreBusinessFirst()
    {
        var page = Render(Overview(), out _);
        page.FindAll("[data-testid=e2e-flow-table] thead th").Select(th => th.TextContent.Trim())
            .Should().Equal("Module", "Critical flow", "Steps", "Execution", "Required", "Last run", "Result", "Actions");
        var row = page.Find("[data-testid=e2e-flow-m02-plassering]");
        row.QuerySelector("[data-label=Execution]")!.TextContent.Should().Be("Attended browser");
        row.QuerySelector("[data-label=Steps]")!.TextContent.Should().Be("5");
    }

    // ── Page states ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoFlowsAtAll()
    {
        var page = Render(Overview([], CriticalE2EReleaseDisposition.NotConfigured, total: 0, covered: 0), out _);
        Text(page, "e2e-verdict-label").Should().Be("No critical flows configured");
        page.Find("[data-testid=e2e-no-flows]").TextContent.Should().Contain("Create a critical user journey");
        page.FindAll("[data-testid=e2e-run-browser]").Should().BeEmpty("no disabled run button with nothing behind it");
        Text(page, "e2e-coverage").Should().Be("No delivery modules yet");
    }

    [Fact]
    public void OnlySmokeFlows_TheCriticalViewSaysSoAndOffersTheSmokeView()
    {
        var smoke = Enumerable.Range(1, 15).Select(i => Flow($"smoke-{i}", "SMOKE", required: false, enabled: false, kind: CriticalE2EFlowKind.Diagnostic)).ToList();
        var page = Render(Overview(smoke, CriticalE2EReleaseDisposition.NotConfigured, total: 0, covered: 0), out _);

        Text(page, "e2e-verdict-label").Should().Be("No release-critical flows configured");
        Text(page, "e2e-verdict-detail").Should().Be("15 smoke/diagnostic flows are available.");
        page.Find("[data-testid=e2e-filter-critical]").GetAttribute("aria-pressed").Should().Be("true", "the default view is release-oriented");
        page.Find("[data-testid=e2e-no-flows]").TextContent.Should().Contain("15 smoke/diagnostic flows are available");
        page.FindAll("[data-testid^=e2e-flow-smoke-]").Should().BeEmpty();

        page.Find("[data-testid=e2e-show-diagnostic]").Click();
        page.FindAll("[data-testid^=e2e-flow-smoke-]").Should().HaveCount(15);
        page.Find("[data-testid=e2e-filter-diagnostic]").GetAttribute("aria-pressed").Should().Be("true");
        page.Find("[data-testid=e2e-kind-smoke-1]").TextContent.Should().Be("Smoke / diagnostic");
    }

    [Fact]
    public void FlowsExistButNoneRequired()
    {
        var page = Render(Overview([Flow("a", required: false), Flow("b", required: false)], CriticalE2EReleaseDisposition.NotConfigured, covered: 0, total: 1), out _);
        Text(page, "e2e-verdict-label").Should().Be("Release coverage not configured");
        Text(page, "e2e-verdict-detail").Should().Be("2 critical flows exist, but none are marked as required for release.");
        Text(page, "e2e-coverage").Should().Be("0 / 1 modules covered");
    }

    [Fact]
    public void AllCriticalFlowsDisabled()
    {
        var page = Render(Overview([Flow("a", enabled: false)], CriticalE2EReleaseDisposition.NotConfigured), out _);
        Text(page, "e2e-verdict-label").Should().Be("No enabled critical flows");
        page.Find("[data-testid=e2e-verdict]").TextContent.Should().NotContain("No flows configured");
    }

    [Fact]
    public void ARequiredFlowThatHasNotRunReadsAsIncompleteNotFailed()
    {
        var page = Render(Overview([Flow("m02", status: CriticalE2EStatus.NotRun)], CriticalE2EReleaseDisposition.Incomplete,
            summary: "1 required flow(s) have not run against this build yet."), out _);
        Text(page, "e2e-verdict-label").Should().Be("Release regression incomplete");
        page.Find("[data-testid=e2e-verdict]").TextContent.Should().NotContain("Failed");
    }

    [Fact]
    public void AllFilterShowsBothKinds()
    {
        var page = Render(Overview([Flow("m02"), Flow("smoke", "SMOKE", kind: CriticalE2EFlowKind.Diagnostic)]), out _);
        page.FindAll("[data-testid^=e2e-flow-]").Where(e => e.TagName == "TR").Should().ContainSingle("Critical is the default");
        page.Find("[data-testid=e2e-filter-all]").Click();
        page.FindAll("tr[data-testid^=e2e-flow-]").Select(r => r.GetAttribute("data-kind")).Should().Equal("Critical", "Diagnostic");
    }

    // ── Enabled ≠ Required ≠ Result ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DisabledIsABadge_NeverTheLastResult()
    {
        var page = Render(Overview([Flow("off", enabled: false, status: CriticalE2EStatus.Passed), Flow("never", status: CriticalE2EStatus.NotRun, enabled: false)]), out _);

        Text(page, "e2e-status-off").Should().Be("Passed", "the last result of a disabled flow is still its last result");
        Text(page, "e2e-status-never").Should().Be("Not run");
        Text(page, "e2e-enabled-off").Should().Be("Disabled");
        page.FindAll("[data-testid^=e2e-status-]").Select(s => s.TextContent).Should().NotContain("Disabled");

        var run = page.Find("[data-testid=e2e-run-flow-off]");
        run.HasAttribute("disabled").Should().BeTrue();
        page.Find($"#{run.GetAttribute("aria-describedby")}").TextContent.Should().Contain("This flow is disabled.");
    }

    [Fact]
    public void RequiredIsItsOwnColumn()
    {
        var page = Render(Overview([Flow("req", required: true), Flow("opt", required: false)]), out _);
        Text(page, "e2e-required-req").Should().Be("Required");
        Text(page, "e2e-required-opt").Should().Be("Optional");
    }

    [Theory]
    [InlineData(CriticalE2EStatus.Passed, "Passed")]
    [InlineData(CriticalE2EStatus.Failed, "Failed")]
    [InlineData(CriticalE2EStatus.Blocked, "Blocked")]
    [InlineData(CriticalE2EStatus.NotRun, "Not run")]
    public void TheResultColumnShowsTheLifecycleState(CriticalE2EStatus status, string label)
    {
        var page = Render(Overview([Flow("f", status: status)]), out _);
        Text(page, "e2e-status-f").Should().Be(label);
    }

    [Fact]
    public void APassFromAnotherBuildReadsAsPendingAndSaysWhichBuildItCameFrom()
    {
        var page = Render(Overview([Flow("f", matchesRelease: false)], CriticalE2EReleaseDisposition.Incomplete), out _);
        page.Find("[data-testid=e2e-status-f]").Closest("td")!.TextContent.Should().Contain("Pending").And.Contain("12344");
    }

    [Fact]
    public void AFlowThatCannotRunAsConfiguredNeedsAttentionRatherThanLookingLikeAFailingTest()
    {
        var page = Render(Overview([Flow("f", status: CriticalE2EStatus.NotRun, configured: false, problem: "The flow has no final business assertion.")]), out _);
        Text(page, "e2e-problem-f").Should().Contain("final business assertion");
        Text(page, "e2e-status-f").Should().Be("Not run");
    }

    // ── Readiness and running ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AttendedReadinessReady()
    {
        var page = Render(Overview(), out _);
        Text(page, "e2e-readiness-state").Should().Be("Ready");
        Text(page, "e2e-attended-state").Should().Be("Ready");
        Text(page, "e2e-readiness-companion").Should().Be("Connected");
        Text(page, "e2e-readiness-page").Should().Be("m2lbdev.bufetat.no/admin/general-roles");
        Text(page, "e2e-readiness-picking").Should().Be("Available");
        page.Find("[data-testid=e2e-companion-setup]").GetAttribute("href").Should().Be("/admin/system-settings?section=target-environments&tab=browser&profile=dev");
    }

    [Fact]
    public void CompanionNotConnected_TheRunSaysWhy()
    {
        var page = Render(Overview(attended: new CriticalE2EAttendedReadiness
        {
            Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession, Message = "The Browser Companion is not connected." },
        }), out _);

        Text(page, "e2e-readiness-state").Should().Be("Not ready");
        Text(page, "e2e-readiness-reason").Should().Be("The Browser Companion is not connected.");
        var run = page.Find("[data-testid=e2e-run-browser]");
        run.HasAttribute("disabled").Should().BeTrue();
        Text(page, run.GetAttribute("aria-describedby")!).Should().Be("The Browser Companion is not connected.");
        page.Find("[data-testid=e2e-run-flow-m02-plassering]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void TwoApprovedPagesArePreciselyBlocked()
    {
        var page = Render(Overview(attended: new CriticalE2EAttendedReadiness
        {
            Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession,
                Message = "2 approved application pages are open. Keep exactly one approved target page open." },
            CompanionConnected = true, OpenApprovedPages = 2, ElementPickSupported = true,
        }), out _);

        Text(page, "e2e-readiness-state").Should().Be("Blocked");
        Text(page, "e2e-readiness-reason").Should().Be("2 approved application pages are open. Keep exactly one approved target page open.");
        Text(page, "e2e-readiness-page").Should().Be("2 approved pages open");
        page.Find("[data-testid=e2e-readiness]").TextContent.Should().NotContain("Browser unavailable");
    }

    [Fact]
    public void PickerUnsupportedIsShownInReadiness()
    {
        var page = Render(Overview(attended: ReadyBrowser with { ElementPickSupported = false }), out _);
        Text(page, "e2e-readiness-picking").Should().Be("Not supported by this extension build");
    }

    [Fact]
    public void ProductionIsOutOfScopeRatherThanSomethingToFix()
    {
        var page = Render(Overview(attended: new CriticalE2EAttendedReadiness
        {
            Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Unavailable, Message = "Automation is not permitted against a Production environment." },
        }), out _);
        Text(page, "e2e-readiness-state").Should().Be("Out of scope");
        page.Find("[data-testid=e2e-run-browser]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void RunAttendedRegressionAsksForBrowserCriticalFlowsOnly()
    {
        var page = Render(Overview([Flow("m02"), Flow("smoke", "SMOKE", kind: CriticalE2EFlowKind.Diagnostic)]), out var api);
        Text(page, "e2e-run-browser").Should().Be("Run attended regression (1)", "diagnostic flows are never part of the regression");
        page.FindAll("[data-testid=e2e-run-integration]").Should().BeEmpty("no automated API flows exist");
        page.Find("[data-testid=e2e-run-browser]").Click();
        api.LastRun!.Mode.Should().Be(CriticalE2EExecutionMode.CompanionBrowser);
        api.LastRun.FlowId.Should().BeNull();
    }

    [Fact]
    public void RunningOneFlowAsksForThatFlowAndShowsItsStepResults()
    {
        var run = new CriticalE2ERunResult
        {
            RunId = "r1", FlowId = "m02", FlowName = "M2LB DEV picker smoke flow", Module = "SMOKE", Status = CriticalE2EStatus.Blocked, DurationMs = 4200,
            StepResults =
            [
                new CriticalE2EStepResult { StepId = "s1", Description = "Navigate /admin/general-roles", Status = CriticalE2EStatus.Passed },
                new CriticalE2EStepResult { StepId = "s2", Description = "Wait for Search field", Status = CriticalE2EStatus.Passed },
                new CriticalE2EStepResult { StepId = "s3", Description = "Fill Search field", Status = CriticalE2EStatus.Blocked, SanitizedError = "The bound browser page is no longer available." },
                new CriticalE2EStepResult { StepId = "s4", Description = "Assert hidden Admin - Generell", Status = CriticalE2EStatus.NotRun },
            ],
        };
        var overview = Overview([Flow("m02")]);
        var page = Render(overview, out var api, new CriticalE2ERunBatchResult { Overview = overview, Runs = [run] });

        page.Find("[data-testid=e2e-run-flow-m02]").Click();

        api.LastRun!.FlowId.Should().Be("m02");
        Text(page, "e2e-last-run-status").Should().Be("Blocked");
        Text(page, "e2e-last-run-counts").Should().StartWith("4 steps · 2 passed · 0 failed · 1 blocked · 4.2 s");
        Text(page, "e2e-last-run-problem").Should().Be("Step 3 blocked: Fill Search field. The bound browser page is no longer available.");
        page.FindAll("[data-testid=e2e-last-run-steps] li").Select(li => li.QuerySelector(".e2e-step-glyph")!.TextContent).Should().Equal("✓", "✓", "!", "○");
    }

    // ── Secondary sections ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SecondarySectionsAreCollapsedAndSurviveARefresh()
    {
        var page = Render(Overview(history: [new CriticalE2ERunResult { RunId = "h1", FlowName = "f", Module = "M02", Status = CriticalE2EStatus.Passed }]), out var api);
        foreach (var id in new[] { "e2e-modules", "e2e-history", "e2e-build-details" })
            page.Find($"[data-testid={id}-toggle]").GetAttribute("aria-expanded").Should().Be("false", id);

        page.Find("[data-testid=e2e-history-toggle]").Click();
        page.Find("[data-testid=e2e-readiness-refresh]").Click();   // a readiness refresh re-renders the page

        api.Overviews.Should().BeGreaterThan(1);
        page.Find("[data-testid=e2e-history-toggle]").GetAttribute("aria-expanded").Should().Be("true", "a refresh does not close what the tester opened");
        page.Find("[data-testid=e2e-history-table]").TextContent.Should().Contain("M02").And.Contain("Passed");
    }

    [Fact]
    public void ModulesAreNotEchoedBackIntoTheStore()
    {
        var page = Render(Overview(), out var api);
        page.Find("[data-testid=e2e-readiness-refresh]").Click();
        api.LastOverview!.Modules.Should().BeEmpty("echoing derived modules made every label ever seen, a smoke module included, permanent");
    }

    [Fact]
    public void BuildNotSetDoesNotBlock_AndSaysWhatItMeans()
    {
        var page = Render(Overview(), out _);
        Text(page, "e2e-build").Should().Be("Not set");
        Text(page, "e2e-build-help").Should().Be("Results from any build count. Set the build to record release evidence for it.");
        page.Find("[data-testid=e2e-run-browser]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void AnAttendedRequiredFlowIsPendingAttendedValidationForAPipeline()
    {
        var page = Render(Overview(), out _);
        var note = page.Find("[data-testid=e2e-pipeline-note]").TextContent;
        note.Should().Contain("pending attended validation").And.NotContain("failed");
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
