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
/// Test type is a classification separate from criticality. A row shows it as one short label; Business E2E coverage counts only Critical flows
/// classified Business E2E; UI functional counts only as functional coverage; UI smoke never counts; legacy flows are "not classified". The
/// editor lets a person change it as a definition edit, which never starts a run.
/// </summary>
public sealed class CriticalE2ETestTypeTests : BunitContext
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

    private static CriticalE2EFlowSummary Flow(string id, CriticalE2ETestType type, CriticalE2EStatus status = CriticalE2EStatus.Passed,
        CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical) => new()
    {
        FlowId = id, Name = id, Module = "M01", Mode = CriticalE2EExecutionMode.CompanionBrowser, Kind = kind, TestType = type, Enabled = true,
        RequiredForRelease = kind == CriticalE2EFlowKind.Critical, Configured = true, LastStatus = status, StepCount = 3,
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

    // A critical UI smoke flow that passed, a critical Business E2E flow not run yet, a passed UI functional flow (diagnostic, as approved
    // from a suggestion), a passed critical legacy flow and a critical Integration/API flow.
    private static CriticalE2EOverview Mixed() => Overview(
        Flow("smoke-critical", CriticalE2ETestType.UiSmoke),
        Flow("business", CriticalE2ETestType.BusinessE2E, CriticalE2EStatus.NotRun),
        Flow("functional", CriticalE2ETestType.UiFunctional, kind: CriticalE2EFlowKind.Diagnostic),
        Flow("legacy", CriticalE2ETestType.Unknown),
        Flow("integration", CriticalE2ETestType.IntegrationApi, CriticalE2EStatus.Failed));

    private static readonly Guid Scope = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");

    private static ScopeSelection Selection(params string[] ids) => new()
    {
        ProjectKey = "import:m2lb", Scopes = [new(Scope, "Report release", 1)],
        Members = [.. ids.Select(id => new ScopeSelectionMember(new ScopeMember { Kind = ScopeMemberKind.CriticalE2EFlow, TargetId = id, Label = id }, [new ScopeContribution(Scope, "Report release", 1)]))],
    };

    private static ScopeSelection AllMixed() => Selection("smoke-critical", "business", "functional", "legacy", "integration");

    private IRenderedComponent<CriticalE2ERegression> Render(CriticalE2EOverview overview, ScopeSelection selection, out StubApi api)
    {
        api = new StubApi(overview);
        Services.AddSingleton<ICriticalE2EApiService>(api);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new StubContextFactory());
        var scopes = new Mock<IScopeRegistryApiService>();
        scopes.Setup(x => x.ListAsync("import:m2lb", It.IsAny<CancellationToken>())).ReturnsAsync(([
            new ScopeSummary { Id = Scope, Name = "Report release", State = ScopeState.Approved, ApprovedRevision = 1, CurrentRevision = 1 }], (string?)null));
        scopes.Setup(x => x.SelectionAsync("import:m2lb", It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync((selection, (string?)null));
        Services.AddSingleton(scopes.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import:m2lb", "M2LB")).Object);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"critical-e2e-regression?scope={Scope}");
        return Render<CriticalE2ERegression>();
    }

    private static string Text(IRenderedComponent<CriticalE2ERegression> page, string testId) => page.Find($"[data-testid={testId}]").TextContent.Trim();

    [Theory]
    [InlineData(CriticalE2ETestType.UiSmoke, "UI smoke")]
    [InlineData(CriticalE2ETestType.UiFunctional, "UI functional")]
    [InlineData(CriticalE2ETestType.BusinessE2E, "Business E2E")]
    [InlineData(CriticalE2ETestType.IntegrationApi, "Integration/API")]
    [InlineData(CriticalE2ETestType.Other, "Other")]
    [InlineData(CriticalE2ETestType.Unknown, "Not classified")]
    public void EachTestTypeHasOneShortLabel(CriticalE2ETestType type, string label) =>
        CriticalE2EPresentation.TestTypeLabel(type).Should().Be(label);

    [Fact]
    public void RowsCarryTheTestTypeAndShowItAsOneLabel()
    {
        CriticalE2EPresentation.Rows(Mixed()).Single(r => r.FlowId == "legacy").TestType.Should().Be(CriticalE2ETestType.Unknown);

        var page = Render(Mixed(), AllMixed(), out _);
        page.WaitForAssertion(() => page.FindAll("[data-testid=e2e-flow-business]").Should().ContainSingle());
        page.Find("[data-testid=e2e-show-diagnostic]").Change(true); // non-critical flows are listed on request

        string Label(string flowId) => page.Find($"[data-testid=e2e-flow-{flowId}] [data-testid=e2e-test-type]").TextContent.Trim();
        Label("smoke-critical").Should().Be("UI smoke");
        Label("business").Should().Be("Business E2E");
        Label("functional").Should().Be("UI functional");
        Label("legacy").Should().Be("Not classified");
        Label("integration").Should().Be("Integration/API");
        page.Find("[data-testid=e2e-flow-legacy] [data-testid=e2e-test-type]").GetAttribute("data-test-type").Should().Be("Unknown");
    }

    // ── Editor ────────────────────────────────────────────────────────────────────────────────────────────────

    private static CriticalE2EFlowDefinition Definition(CriticalE2ETestType type = CriticalE2ETestType.Unknown) => new()
    {
        Id = "flow", Name = "Search", Module = "Barn", ProfileId = "dev", EnvironmentId = "dev", Kind = CriticalE2EFlowKind.Critical, TestType = type,
        Steps = [new() { StepId = "A", BrowserAction = CompanionActionKind.AssertVisible, Selector = new() { Value = "result" }, IsFinalAssertion = true }],
    };

    [Fact]
    public void TheEditorShowsALegacyFlowAsNotClassified_AndSavesTheChosenTestTypeWithoutRunning()
    {
        var api = new Mock<ICriticalE2EApiService>(MockBehavior.Strict);
        Services.AddSingleton(api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        CriticalE2EFlowDefinition? saved = null;
        var cut = Render<CriticalE2EFlowEditor>(p => p.Add(e => e.Flow, Definition()).Add(e => e.ProfileId, "dev")
            .Add(e => e.EnvironmentId, "dev").Add(e => e.OnSave, f => saved = f));

        var select = cut.Find("[data-testid=e2e-editor-test-type]");
        select.GetAttribute("value").Should().Be(nameof(CriticalE2ETestType.Unknown));
        select.QuerySelectorAll("option").Select(o => o.TextContent.Trim()).Should().Contain(new[] { "Not classified", "UI smoke", "UI functional", "Business E2E", "Integration/API" });
        cut.Find("[data-testid=e2e-editor-test-type-help]").TextContent.Should().Contain("not classified");

        select.Change(nameof(CriticalE2ETestType.BusinessE2E));
        cut.Find("[data-testid=e2e-editor-save]").Click();

        saved!.TestType.Should().Be(CriticalE2ETestType.BusinessE2E);
        saved.Kind.Should().Be(CriticalE2EFlowKind.Critical, "test type and criticality are independent");
        api.VerifyNoOtherCalls();
    }

    [Fact]
    public void TheDraftRoundTripsTheTestType()
    {
        var draft = CriticalE2EFlowDraft.From(Definition(CriticalE2ETestType.UiFunctional) with { Kind = CriticalE2EFlowKind.Diagnostic });
        draft.TestType.Should().Be(CriticalE2ETestType.UiFunctional);
        var definition = draft.ToDefinition("dev", "dev");
        definition.TestType.Should().Be(CriticalE2ETestType.UiFunctional);
        definition.Kind.Should().Be(CriticalE2EFlowKind.Diagnostic);
    }
}
