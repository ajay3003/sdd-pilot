using BirkNext.CriticalE2E;
using BirkNext.IntegrationTopology;
using BirkNext.TestData;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Critical E2E authoring and the flow overview as release evidence: modules come from registration, a flow states a concrete outcome,
/// steps carry page context, the boundary says what a pass covers, and Test Data is referenced by alias.
/// </summary>
public sealed class CriticalE2EReleaseEvidenceUiTests : BunitContext
{
    private sealed class Api(CriticalE2EOverview? overview = null, Func<CriticalE2EElementPickRequest, CriticalE2EElementPickResult>? pick = null) : ICriticalE2EApiService
    {
        public List<CriticalE2EModuleRequest> Registered { get; } = [];
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) => Task.FromResult(overview ?? new CriticalE2EOverview());
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new List<CriticalE2EFlowDefinition>());
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => Task.FromResult(flow);
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default) => Task.FromResult(new CriticalE2ERunBatchResult { Overview = overview ?? new() });
        public Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken ct = default) =>
            Task.FromResult(pick?.Invoke(request) ?? new CriticalE2EElementPickResult());
        public Task<CriticalE2EOverview> RegisterModuleAsync(CriticalE2EModuleRequest request, CancellationToken ct = default)
        {
            Registered.Add(request);
            return Task.FromResult(overview ?? new CriticalE2EOverview());
        }
    }

    private sealed class Context : IFrontendAnalysisContextFactory
    {
        public Task<FrontendAnalysisContext> GetActiveContextAsync() => Task.FromResult(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development },
        });
    }

    private static readonly CriticalE2EEngineStatus Ready = new() { State = CriticalE2EEngineState.Ready, Message = "Select an element in the paired browser." };

    private static CriticalE2EElementPickResult PickedLink() => new()
    {
        Status = CriticalE2EStatus.Passed, Outcome = CriticalE2EPickOutcome.Picked,
        Element = new CompanionElementDescriptor
        {
            PageOrigin = "https://m2lbdev.example.invalid", PageRoute = "/admin/emergency-access", TagName = "a", Role = "link", AccessibleName = "Generelle roller",
            Href = "https://m2lbdev.example.invalid/admin/general-roles", Visible = true, Enabled = true,
            Recommended = new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "link", Name = "Generelle roller" },
            Candidates = [new() { Selector = new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "link", Name = "Generelle roller" }, MatchCount = 1, Unique = true }],
        },
    };

    /// <summary>The flow created before this remediation: free-text module, a final-marked click, policy text.</summary>
    private static CriticalE2EFlowDefinition Legacy() => new()
    {
        Id = "flow-legacy", Module = "M01", Name = "Test", Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev", EnvironmentId = "dev",
        RequiredForRelease = true, AutomationBoundary = "Browser only", TestDataPolicy = "Synthetic data only, prefixed M2LB-E2E-",
        Steps = [new CriticalE2EStepDefinition { StepId = "step-1", Description = "Step 1 - Click", BrowserAction = CompanionActionKind.Click,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "link", Name = "Generelle roller" }, IsFinalAssertion = true }],
    };

    private static CriticalE2EFlowDefinition Blank() => new()
    {
        Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev", EnvironmentId = "dev", Name = "Open general roles",
        Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.Click, Selector = new CompanionSelector() }],
    };

    private static TestDataAssetSummary Asset(string alias, TestDataApproval approval = TestDataApproval.Approved, TestDataKind kind = TestDataKind.Scalar) =>
        new() { Id = alias, Alias = alias, Kind = kind, Approval = approval, Source = "Configured" };

    private static readonly IntegrationTopologyView Topology = new()
    {
        EnvironmentId = "dev",
        Components =
        [
            new TopologyComponent { Id = "web", Name = "M2LB web", Kind = TopologyComponentKind.Application },
            new TopologyComponent { Id = "sb", Name = "Service Bus", Kind = TopologyComponentKind.Resource },
            new TopologyComponent { Id = "bufdata", Name = "Bufdata", Kind = TopologyComponentKind.External },
        ],
    };

    private IRenderedComponent<CriticalE2EFlowEditor> Editor(CriticalE2EFlowDefinition flow, Api? api = null, IReadOnlyList<string>? modules = null,
        IReadOnlyList<TestDataAssetSummary>? assets = null, List<CriticalE2EFlowDefinition>? saved = null)
    {
        Services.AddSingleton<ICriticalE2EApiService>(api ?? new Api(pick: _ => PickedLink()));
        return Render<CriticalE2EFlowEditor>(p => p
            .Add(e => e.Flow, flow).Add(e => e.ProfileId, "dev").Add(e => e.EnvironmentId, "dev").Add(e => e.EnvironmentType, "Development")
            .Add(e => e.EnvironmentName, "M2LB DEV").Add(e => e.ElementPick, Ready).Add(e => e.RegisteredModules, modules ?? ["M01", "M02"])
            .Add(e => e.TestDataAssets, assets ?? []).Add(e => e.Topology, Topology)
            .Add(e => e.OnSave, (CriticalE2EFlowDefinition f) => saved?.Add(f)));
    }

    // ── Modules ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModulesAreChosenFromRegisteredModules_AndATypedLabelIsMarkedManual()
    {
        var cut = Editor(Blank());
        var options = cut.FindAll("[data-testid=e2e-editor-module] option").Select(o => o.TextContent.Trim()).ToList();
        options.Should().Equal("Unassigned / diagnostic", "M01", "M02", "Manual label…");
        cut.Find("[data-testid=e2e-editor-module-help]").TextContent.Should().Be("Unassigned · never counts toward release coverage");

        cut.Find("[data-testid=e2e-editor-module]").Change("M02");
        cut.Find("[data-testid=e2e-editor-module-help]").TextContent.Should().Be("Registered delivery module");

        cut.Find("[data-testid=e2e-editor-module]").Change("__manual");
        cut.Find("[data-testid=e2e-editor-module-manual]").Change("M99");
        cut.Find("[data-testid=e2e-editor-module-help]").TextContent.Should().Be("Unresolved module · Manual label · does not count toward release coverage");
    }

    // ── Legacy migration ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLegacyFlowLoadsWithEveryFieldPreserved_AndIsMarkedForReview()
    {
        var cut = Editor(Legacy(), modules: []);

        cut.Find("[data-testid=e2e-editor-module] option:checked").TextContent.Trim().Should().Be("M01 · unresolved module");
        cut.Find("[data-testid=e2e-editor-module-help]").TextContent.Should().StartWith("Unresolved module");
        cut.Find("[data-testid=e2e-step-legacy-final-0]").TextContent.Should().StartWith("Legacy assertion marker · concrete outcome required");
        cut.Find("[data-testid=e2e-editor-outcome-legacy]").TextContent.Should().Be("Legacy assertion marker · concrete outcome required");
        cut.Find("[data-testid=e2e-editor-testdata-legacy]").TextContent.Should().Be("Legacy note: Synthetic data only, prefixed M2LB-E2E-");
        cut.Find("[data-testid=e2e-editor-boundary-legacy]").TextContent.Should().Be("Legacy note: Browser only");
        cut.Find("[data-testid=e2e-editor-readiness-summary]").TextContent.Should().Be("Release requirement configured · not release-ready");
        cut.FindAll("[data-testid=e2e-editor-readiness-blockers] li").Select(li => li.GetAttribute("data-key")).Should().Contain(["module", "outcome", "boundary"]);
        cut.FindAll("[data-testid=e2e-step-final-0]").Should().BeEmpty("a click is not an assertion, so it is not offered as the final outcome");
    }

    // ── Authoring from a pick ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APickProposesActionNameAndPageContext_ButNeverAppliesTheDestinationSilently()
    {
        var saved = new List<CriticalE2EFlowDefinition>();
        var cut = Editor(Blank() with { Module = "M01", RequiredForRelease = true }, saved: saved);

        cut.Find("[data-testid=e2e-step-pick-0]").Click();

        cut.Find("[data-testid=e2e-step-name-0]").GetAttribute("value").Should().Be("Open Generelle roller");
        cut.Find("[data-testid=e2e-step-captured-on-0]").TextContent.Should().Be("Captured on /admin/emergency-access");
        cut.Find("[data-testid=e2e-step-execution-route-0]").GetAttribute("value").Should().Be("/admin/emergency-access");
        cut.Find("[data-testid=e2e-step-destination-0]").GetAttribute("value").Should().BeEmpty("the captured page never becomes the destination");
        cut.Find("[data-testid=e2e-step-destination-suggestion-0]").TextContent.Should().Contain("/admin/general-roles");
        cut.Find("[data-testid=e2e-step-stability-0]").TextContent.Should().Be("Selector stability: Preferred");

        cut.Find("[data-testid=e2e-step-destination-use-0]").Click();
        cut.Find("[data-testid=e2e-step-destination-0]").GetAttribute("value").Should().Be("/admin/general-roles");
        cut.Find("[data-testid=e2e-editor-outcome-suggestion]").TextContent.Should().Contain("Page /admin/general-roles is loaded");
        cut.Find("[data-testid=e2e-editor-outcome-use]").Click();
        cut.Find("[data-testid=e2e-editor-outcome-text]").TextContent.Should().Be("Page /admin/general-roles is loaded");
        cut.Find("[data-testid=e2e-editor-boundary-suggest]").Click();
        cut.Find("[data-testid=e2e-editor-readiness-summary]").TextContent.Should().Be("Required for release · definition release-ready");

        cut.Find("[data-testid=e2e-editor-save]").Click();
        var flow = saved.Single();
        var step = flow.Steps.Single();
        (step.CapturedOnRoute, step.ExecutionRoute, step.ExpectedDestination).Should().Be(("/admin/emergency-access", "/admin/emergency-access", "/admin/general-roles"));
        flow.FinalOutcome.Should().Be(new CriticalE2EFinalOutcome { Kind = CriticalE2EOutcomeKind.UrlEquals, Expected = "/admin/general-roles" });
        flow.Boundary!.Source.Should().Be("Integration Topology");
    }

    [Fact]
    public void AnElementOutcomeNeedsItsElementAndExpectedValue()
    {
        var cut = Editor(Blank() with { Module = "M01", Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.Click,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "save" } }] });

        cut.Find("[data-testid=e2e-editor-outcome-kind]").Change(nameof(CriticalE2EOutcomeKind.TextContains));
        cut.Find("[data-testid=e2e-editor-problem]").TextContent.Should().Be("The final outcome has no element to check.");
        cut.Find("[data-testid=e2e-editor-outcome-role]").Change("status");
        cut.Find("[data-testid=e2e-editor-problem]").TextContent.Should().Be("The final outcome has no expected value.");
        cut.Find("[data-testid=e2e-editor-outcome-expected]").Change("Tilgang lagret");
        cut.FindAll("[data-testid=e2e-editor-problem]").Should().BeEmpty();
        cut.Find("[data-testid=e2e-editor-outcome-text]").TextContent.Should().Be("role=status contains \"Tilgang lagret\"");
    }

    [Fact]
    public void AFragileSelectorAsksForReviewBeforeReleaseUse()
    {
        var cut = Editor(Blank() with { Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.Click,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.Css, Value = "main > div:nth-of-type(3) a" } }] });
        cut.Find("[data-testid=e2e-step-stability-0]").TextContent.Should().Be("Selector stability: Fragile");
        cut.Find("[data-testid=e2e-step-reviewed-0]").HasAttribute("checked").Should().BeFalse();
    }

    // ── Boundary ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBoundaryIsProposedFromTopology_AndAHandEditKeepsItsProvenance()
    {
        var cut = Editor(Blank());
        cut.Find("[data-testid=e2e-editor-boundary-suggest]").Click();

        cut.Find("[data-testid=e2e-editor-boundary-start]").GetAttribute("value").Should().Be("Approved application page (Browser Companion)");
        cut.Find("[data-testid=e2e-editor-boundary-end]").GetAttribute("value").Should().Be("Browser-visible outcome");
        cut.Find("[data-testid=e2e-editor-boundary-downstream]").GetAttribute("value").Should().Be("Bufdata, Service Bus — verified separately (Integration Quality Review)");
        cut.Find("[data-testid=e2e-editor-boundary-source]").TextContent.Should().Be("Source: Integration Topology");

        cut.Find("[data-testid=e2e-editor-boundary-end]").Change("Report accepted page");
        cut.Find("[data-testid=e2e-editor-boundary-source]").TextContent.Should().Be("Source: Manual override (from Integration Topology)");
    }

    // ── Central Test Data ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StepsReferenceCentralTestDataByAlias_AndOnlyApprovedAssetsAreOfferedByDefault()
    {
        var saved = new List<CriticalE2EFlowDefinition>();
        var assets = new[] { Asset("synthetic-child"), Asset("draft-child", TestDataApproval.Unapproved), Asset("create-person-valid", kind: TestDataKind.Structured) };
        var cut = Editor(Blank() with { Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.Fill,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.Label, Value = "Child identifier" }, Value = "${data:childId}" },
            new CriticalE2EStepDefinition { StepId = "s2", BrowserAction = CompanionActionKind.AssertVisible, Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "ok" }, IsFinalAssertion = true }] },
            assets: assets, saved: saved);

        cut.FindAll("[data-testid=e2e-editor-testdata-assets] input").Select(i => i.GetAttribute("data-testid")).Should()
            .Equal("e2e-editor-asset-create-person-valid", "e2e-editor-asset-synthetic-child");
        cut.Find("[data-testid=e2e-editor-testdata-status]").TextContent.Should().Be("Missing");
        cut.FindAll("[data-testid=e2e-editor-mapping-asset-childId] option").Select(o => o.GetAttribute("value")).Should().Equal("", "synthetic-child");

        cut.Find("[data-testid=e2e-editor-mapping-asset-childId]").Change("synthetic-child");
        cut.Find("[data-testid=e2e-editor-asset-create-person-valid]").Change(true);
        cut.Find("[data-testid=e2e-editor-testdata-status]").TextContent.Should().Be("Ready");
        cut.Find("[data-testid=e2e-editor-testdata-all]").Change(true);
        cut.FindAll("[data-testid=e2e-editor-asset-draft-child]").Should().ContainSingle("unapproved assets appear only when asked for");

        cut.Find("[data-testid=e2e-editor-save]").Click();
        var flow = saved.Single();
        flow.DataMappings.Should().ContainSingle().Which.Should().Be(new CriticalE2EDataMapping("childId", ["synthetic-child"], TestDataDataType.SimpleValues, "step value") with { AssetAliases = flow.DataMappings![0].AssetAliases });
        flow.DataMappings![0].AssetAliases.Should().Equal("synthetic-child");
        flow.TestDataAliases.Should().Equal("create-person-valid");
        flow.TestDataPolicy.Should().BeEmpty("a new flow carries references, not a policy text");
    }

    // ── Page ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static CriticalE2EFlowSummary Summary(bool ready, string module = "M01") => new()
    {
        FlowId = "roles", Name = "Open general roles", Module = module, Mode = CriticalE2EExecutionMode.CompanionBrowser, Enabled = true, RequiredForRelease = true,
        Configured = true, StepCount = 2, ActionSteps = 1, Assertions = 2, Outcome = "Page /admin/general-roles is loaded", BoundaryStatus = ready ? "Explicit" : "Not explicit",
        BoundaryText = ready ? "Approved application page → Browser-visible outcome · downstream: Service Bus verified separately" : null,
        Readiness = ready ? new CriticalE2EFlowReadiness { ModuleResolved = true } : new CriticalE2EFlowReadiness
        {
            ModuleResolved = false, Blockers = [new("module", "Unresolved module \"M01\" · manual label · does not count toward release coverage until it is a registered delivery module.")],
        },
    };

    private static CriticalE2EOverview PageOverview(bool ready, CriticalE2EAttendedReadiness? attended = null, List<CriticalE2EFlowSummary>? flows = null) => new()
    {
        EnvironmentId = "dev", EnvironmentName = "M2LB DEV",
        Release = new CriticalE2EReleaseStatus { Disposition = CriticalE2EReleaseDisposition.NotEvaluated, ModulesTotal = ready ? 1 : 0, ModulesCovered = ready ? 1 : 0, RequiredFlowsTotal = 1 },
        Flows = flows ?? [Summary(ready)],
        Modules = ready ? [new CriticalE2EModuleCoverage { Module = "M01", Flows = [Summary(true)], Status = "M01 · required flow ready · no build selected" }] : [],
        RegisteredModules = ready ? [new CriticalE2ERegisteredModule { Name = "M01" }] : [],
        UnresolvedModules = ready ? [] : ["M01"],
        Attended = attended ?? new CriticalE2EAttendedReadiness { Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready }, CompanionConnected = true, OpenApprovedPages = 1 },
        ElementPick = Ready,
    };

    private IRenderedComponent<CriticalE2ERegression> Page(CriticalE2EOverview overview, out Api api)
    {
        api = new Api(overview);
        Services.AddSingleton<ICriticalE2EApiService>(api);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new Context());
        return Render<CriticalE2ERegression>();
    }

    [Fact]
    public void TheFlowTableShowsReleaseReadinessWithoutOpeningEdit()
    {
        var page = Page(PageOverview(ready: true), out _);
        page.Find("[data-testid=e2e-steps-roles]").TextContent.Should().Be("1 action · 2 assertions");
        page.Find("[data-testid=e2e-outcome-roles]").TextContent.Should().Be("Page /admin/general-roles is loaded");
        page.Find("[data-testid=e2e-testdata-roles]").TextContent.Should().Be("Not required");
        page.Find("[data-testid=e2e-boundary-roles]").TextContent.Should().StartWith("Explicit");
        page.Find("[data-testid=e2e-release-ready-roles]").TextContent.Should().Be("Release-ready");
        page.Find("[data-testid=e2e-coverage]").TextContent.Should().Be("1 / 1 modules covered");
        page.Find("[data-testid=e2e-build-source]").TextContent.Should().Be("No build selected");
    }

    [Fact]
    public void AnUnresolvedModuleIsShownAsAManualLabel_AndRegisteringItIsExplicit()
    {
        var page = Page(PageOverview(ready: false), out var api);
        page.Find("[data-testid=e2e-module-unresolved-roles]").TextContent.Should().Be("Unresolved module · manual label");
        page.Find("[data-testid=e2e-release-ready-roles]").TextContent.Should().StartWith("Not release-ready · Unresolved module");
        page.Find("[data-testid=e2e-coverage]").TextContent.Should().Be("No registered delivery modules");
        page.Find("[data-testid=e2e-coverage-unresolved]").TextContent.Should().Be("1 unresolved module label · does not count");
        api.Registered.Should().BeEmpty("viewing never registers a module");

        page.Find("[data-testid=e2e-modules-toggle]").Click();
        page.Find("[data-testid=e2e-module-register-M01]").Click();
        api.Registered.Should().ContainSingle().Which.Should().Match<CriticalE2EModuleRequest>(r => r.Name == "M01" && r.Source == "Module administration");
    }

    [Fact]
    public void WithoutAReadyBrowserTheEmptyStateLeadsWithPreparingIt()
    {
        var notReady = new CriticalE2EAttendedReadiness { Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession, Message = "The Browser Companion is not connected." } };
        var page = Page(PageOverview(ready: true, notReady, flows: []), out _);
        page.Find("[data-testid=e2e-prepare-browser]").TextContent.Should().Be("Prepare attended browser");
        page.Find("[data-testid=e2e-no-flows] [data-testid=e2e-add-flow]").ClassList.Should().Contain("btn-secondary");
        page.Find("[data-testid=e2e-empty-modules]").TextContent.Should().Be("Registered delivery modules: M01");
    }

    [Fact]
    public void ABrowserPassIsReportedSeparatelyFromDownstream()
    {
        var run = new CriticalE2ERunResult
        {
            RunId = "r1", FlowId = "roles", FlowName = "Open general roles", Status = CriticalE2EStatus.Passed, FlowVersion = 1, ReleaseEvidence = false,
            ReleaseEvidenceNote = "Diagnostic result · does not count as release evidence (no build identity)", DownstreamOutcome = "Not verified by this flow · Service Bus verified separately",
            OutcomeText = "Page /admin/general-roles is loaded", StepResults = [new CriticalE2EStepResult { StepId = "final-outcome", Status = CriticalE2EStatus.Passed, Kind = "Outcome", Description = "Final outcome" }],
        };
        var overview = PageOverview(ready: true);
        Services.AddSingleton<ICriticalE2EApiService>(new RunApi(overview, run));
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new Context());
        var page = Render<CriticalE2ERegression>();

        page.Find("[data-testid=e2e-run-flow-roles]").Click();

        page.Find("[data-testid=e2e-last-run-status]").TextContent.Should().Be("Diagnostic pass · browser journey only");
        page.Find("[data-testid=e2e-last-run-browser]").TextContent.Should().Be("Browser journey: Passed");
        page.Find("[data-testid=e2e-last-run-downstream]").TextContent.Should().Be("Downstream integration: Not verified by this flow · Service Bus verified separately");
        page.Find("[data-testid=e2e-last-run-evidence]").TextContent.Should().StartWith("Diagnostic result · does not count as release evidence");
        page.Find("[data-testid=e2e-last-run-kind-final-outcome]").TextContent.Should().Be("Outcome");
        page.Markup.Should().NotContain("E2E Passed");
    }

    private sealed class RunApi(CriticalE2EOverview overview, CriticalE2ERunResult run) : ICriticalE2EApiService
    {
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) => Task.FromResult(overview);
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new List<CriticalE2EFlowDefinition>());
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => Task.FromResult(flow);
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CriticalE2ERunBatchResult { Overview = overview, Runs = [run] });
        public Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken ct = default) => Task.FromResult(new CriticalE2EElementPickResult());
    }
}
