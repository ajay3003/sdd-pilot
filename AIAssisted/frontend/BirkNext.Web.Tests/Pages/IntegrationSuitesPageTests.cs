using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Generate Integration Test Suite page: a suite is generated only after a snapshot is chosen explicitly; the result keeps the three outcomes apart
/// (can run now / waiting for a prerequisite / needs a domain decision); Kode 6/7 scenarios are grouped by that triage with their prerequisites and
/// open questions; only ready tests are selectable; a run needs a Target Environment and explicit authorisation. Synthetic fixtures; the page shows
/// what the backend stored and never computes a pass.
/// </summary>
public sealed class IntegrationSuitesPageTests : BunitContext
{
    private static readonly Guid Snap = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000001");
    private static readonly Guid SuiteId = Guid.Parse("bbbbbbbb-2222-0000-0000-000000000002");
    private static readonly Guid Ready = Guid.Parse("cccccccc-2222-0000-0000-000000000003");
    private static readonly Guid Waiting = Guid.Parse("dddddddd-2222-0000-0000-000000000004");
    private readonly Mock<IIntegrationSuiteApiService> _api = new();
    private IntegrationSuiteGenerateRequest? _sent;

    public IntegrationSuitesPageTests()
    {
        _api.Setup(a => a.SnapshotsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([new IqrSourceSnapshotSummary
        {
            Id = Snap, Archive = new("M2LB-synthetic.zip", new string('a', 64), 1234), AnalyzedAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), Status = SourceAnalysisStatus.Ready,
        }], null));
        _api.Setup(a => a.ScopesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([
            new IntegrationSuiteScope { ScopeId = "skolenaervaer", DisplayName = "Skolenærvær (M2LB)", Description = "Altinn → MU → MM → Skole → Person/Tjeneste → period lock → Bufdata" },
            new IntegrationSuiteScope { ScopeId = "api", DisplayName = "API modules", Description = "Chosen modules' contracts" }], null));
        _api.Setup(a => a.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([], null));
        _api.Setup(a => a.GenerateAsync(It.IsAny<IntegrationSuiteGenerateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((IntegrationSuiteGenerateRequest r, CancellationToken _) => _sent = r)
            .ReturnsAsync((new IntegrationSuiteGenerateResult(Suite(), false, null), null));
        _api.Setup(a => a.GetAsync(SuiteId, It.IsAny<CancellationToken>())).ReturnsAsync((Suite(), null));
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(new Mock<IFrontendAnalysisContextFactory>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static IntegrationSuite Suite() => new()
    {
        Id = SuiteId, SuiteKey = "skolenaervaer:m2lb:abc", Revision = 2, Status = SuiteStatus.PartiallyExecutable, StatusReason = "Downstream stages are not observable per message.",
        Outcome = SuiteExecutionOutcome.NotExecuted,
        Snapshot = new() { SnapshotId = Snap, ArchiveFileName = "M2LB-synthetic.zip", ArchiveSha256 = new string('a', 64), MissingSourceAreas = ["Tjeneste/ is not in this archive"] },
        Scope = new() { ScopeId = "skolenaervaer", DisplayName = "Skolenærvær (M2LB)" },
        Tests =
        [
            new() { TestId = Ready, Operation = "GET /health", Kind = "Positive", ContractRef = "ward-api@v1", Readiness = "Ready", Triage = SuiteTriage.CanRunNow },
            new() { TestId = Waiting, Operation = "GET /children", Kind = "Positive", ContractRef = "ward-api@v1", Readiness = "Blocked", ReadinessGaps = ["Test data: no binding"], Triage = SuiteTriage.WaitingForPrerequisite },
        ],
        Stages = [new() { Order = 1, StageId = "bufdata", Label = "Bufdata", Component = "Bufdata", Coverage = SuiteStageCoverage.Blocked, Readiness = SuiteItemReadiness.Blocked, Triage = SuiteTriage.WaitingForPrerequisite, Blockers = ["No per-message correlated observer"] }],
        Security =
        [
            new() { ScenarioId = "K67-01", Title = "Kode 7 child stored and withheld at lock", Triage = SuiteTriage.WaitingForPrerequisite, Readiness = SuiteItemReadiness.Blocked,
                Prerequisites = ["An approved synthetic Kode 7 child (Test Data capability classification-level-2)"] },
            new() { ScenarioId = "K67-05", Title = "Kode 6/7 in aggregate Bufdata figures", Triage = SuiteTriage.NeedsDomainDecision, Readiness = SuiteItemReadiness.NeedsReview,
                OpenQuestion = "Do Kode 6/7 children count in aggregate Bufdata figures?" },
            new() { ScenarioId = "M01:protected-downstream-handling", Title = "Existing M01 requirement", Reuses = "M01 Security Classification review", Triage = SuiteTriage.CanRunNow, Readiness = SuiteItemReadiness.Ready },
        ],
        Readiness = [new("Correlation", SuiteItemReadiness.Blocked, "Downstream stages need a per-message correlated observer.")],
        Review = [new() { Key = "topology:mu->mm", Area = "Topology", Question = "Is MU → MM confirmed?", Why = "Candidate mapping" }],
        Drift = ["ward-api: contract v1 → v2. Existing tests stay on v1."],
        Contracts = [new() { SourceContractId = "openapi:Ward/openapi.yaml", SourceFile = "Ward/openapi.yaml", Name = "Ward API", Type = "OpenApi", ContractKey = "ward-api", ContractVersion = 2 }],
    };

    private IRenderedComponent<IntegrationSuitesPage> RenderGenerateArea()
    {
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("integration-suites?area=generate");
        return Render<IntegrationSuitesPage>();
    }

    private static void Pick(IRenderedComponent<IntegrationSuitesPage> cut) => cut.Find("[data-testid=its-snapshot-option]").Change(Snap.ToString("D"));

    private IRenderedComponent<IntegrationSuitesPage> Generated()
    {
        var cut = RenderGenerateArea();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-snapshot-option]"));
        Pick(cut);
        cut.Find("[data-testid=its-generate]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-suite]"));
        return cut;
    }

    [Fact]
    public void Environment_blockers_hand_off_to_bulk_configuration_with_the_suites_contracts_preselected()
    {
        var cut = Generated();
        cut.Find("[data-testid=its-resolve-environment]").GetAttribute("href").Should().Be("api-quality-review/contracts?mode=configure&keys=ward-api");
    }

    [Fact]
    public void A_hand_off_from_Contract_Testing_preselects_snapshot_scope_and_module_but_generates_only_on_click()
    {
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"integration-suites?snapshot={Snap:D}&scope=api&modules=Autorisasjon");
        var cut = Render<IntegrationSuitesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-modules]").GetAttribute("value").Should().Be("Autorisasjon"));
        cut.Find("[data-testid=its-snapshot-facts]").TextContent.Should().Contain("M2LB-synthetic.zip");
        _sent.Should().BeNull("a hand-off never generates by itself");
        cut.Find("[data-testid=its-generate]").Click();
        cut.WaitForAssertion(() => _sent.Should().NotBeNull());
        _sent!.Should().Match<IntegrationSuiteGenerateRequest>(r => r.SnapshotId == Snap && r.ScopeId == "api" && r.Modules.SequenceEqual(new[] { "Autorisasjon" }));
    }

    [Fact]
    public void Generate_is_disabled_until_a_snapshot_is_chosen_and_sends_the_chosen_scope_and_snapshot()
    {
        var cut = RenderGenerateArea();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-generate]").HasAttribute("disabled").Should().BeTrue());
        Pick(cut);
        cut.Find("[data-testid=its-snapshot-facts]").TextContent.Should().Contain("M2LB-synthetic.zip").And.Contain("aaaaaaaaaaaaaaaa");
        cut.Find("[data-testid=its-generate]").Click();
        cut.WaitForAssertion(() => _sent.Should().NotBeNull());
        _sent!.SnapshotId.Should().Be(Snap);
        _sent.ScopeId.Should().Be("skolenaervaer");
        _sent.IncludeSecurity.Should().BeTrue();
        cut.Find("[data-testid=its-message]").TextContent.Should().Contain("Nothing was sent");
    }

    [Fact]
    public void The_result_keeps_can_run_waiting_and_needs_decision_apart_and_shows_drift()
    {
        var cut = Generated();
        cut.Find("[data-testid=its-status]").TextContent.Should().Be("Partially executable");
        cut.Find("[data-testid=its-outcome]").TextContent.Should().Be("Not executed");
        cut.Find("[data-testid=its-can-run]").TextContent.Should().Be("2"); // one test + one reused M01 requirement
        cut.Find("[data-testid=its-waiting]").TextContent.Should().Be("3"); // one test + one Kode 7 scenario + one stage
        cut.Find("[data-testid=its-decision]").TextContent.Should().Be("1");
        cut.Find("[data-testid=its-drift]").TextContent.Should().Contain("Existing tests stay on v1");
        cut.Find("[data-testid=its-dimension]").GetAttribute("data-state").Should().Be("Blocked");
        cut.Find("[data-testid=its-missing-areas]").TextContent.Should().Contain("Tjeneste/");
    }

    [Fact]
    public void Kode67_scenarios_are_grouped_by_triage_with_prerequisites_and_open_questions()
    {
        var cut = Generated();
        cut.Find("[data-testid=its-tab-kode-6-7]").Click();
        cut.Find("[data-testid=its-sec-group-WaitingForPrerequisite]").TextContent.Should().Contain("Waiting for prerequisite (1)");
        cut.Find("[data-testid=its-sec-group-NeedsDomainDecision]").TextContent.Should().Contain("Needs domain decision (1)");
        cut.Find("[data-id=K67-01]").TextContent.Should().Contain("classification-level-2");
        cut.Find("[data-testid=its-sec-question]").TextContent.Should().Contain("aggregate Bufdata figures");
        cut.Find("[data-id='M01:protected-downstream-handling']").TextContent.Should().Contain("Reuses M01 Security Classification review");
    }

    [Fact]
    public void Only_ready_tests_are_selectable_and_a_run_needs_a_target_environment()
    {
        var cut = Generated();
        cut.Find("[data-testid=its-tab-tests]").Click();
        var boxes = cut.FindAll("[data-testid=its-select]");
        boxes[0].HasAttribute("disabled").Should().BeFalse();
        boxes[1].HasAttribute("disabled").Should().BeTrue("a test waiting for a prerequisite cannot be selected");
        cut.Find("[data-testid=its-tab-execution]").Click();
        cut.Find("[data-testid=its-no-environment]").TextContent.Should().Contain("Target Environment");
        cut.FindAll("[data-testid=its-run]").Should().BeEmpty();
        _api.Verify(a => a.ExecuteAsync(It.IsAny<Guid>(), It.IsAny<IntegrationSuiteExecuteRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void A_backend_refusal_is_shown_and_no_suite_appears()
    {
        _api.Setup(a => a.GenerateAsync(It.IsAny<IntegrationSuiteGenerateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new IntegrationSuiteGenerateResult(null, false, "The source snapshot failed analysis; no suite is generated from it."), null));
        var cut = RenderGenerateArea();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-snapshot-option]"));
        Pick(cut);
        cut.Find("[data-testid=its-generate]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-error]").TextContent.Should().Contain("failed analysis"));
        cut.FindAll("[data-testid=its-suite]").Should().BeEmpty();
    }

    [Fact]
    public void A_journey_being_prepared_is_sent_with_the_generated_suite_and_stays_in_the_address_across_areas()
    {
        var journey = new DeclaredIntegrationJourney { ProjectKey = "library", JourneyId = "loan-flow", DisplayName = "Loan flow", Components = ["Web", "Api"], Revision = 3 };
        var workspace = new Mock<ICurrentWorkspaceProjection>();
        workspace.Setup(w => w.GetAsync()).ReturnsAsync(BirkNext.Web.Tests.Services.WorkspaceSnapshots.AllRoles(project: "library"));
        var journeys = new Mock<IDeclaredIntegrationJourneyApiService>();
        journeys.Setup(j => j.ListAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync(([journey], (string?)null));
        Services.AddSingleton(workspace.Object);
        Services.AddSingleton(journeys.Object);
        _api.Setup(a => a.GenerateAsync(It.IsAny<IntegrationSuiteGenerateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((IntegrationSuiteGenerateRequest r, CancellationToken _) => _sent = r)
            .ReturnsAsync((new IntegrationSuiteGenerateResult(Suite() with { PreparedForJourneys = [new("project.library:loan-flow", 3, "Loan flow")] }, false, null), null));
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"integration-suites?area=generate&journey={Uri.EscapeDataString(journey.Id)}");
        var cut = Render<IntegrationSuitesPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=its-journey-context]").TextContent.Should().Contain("Loan flow"));
        Pick(cut);
        cut.Find("[data-testid=its-generate]").Click();
        cut.WaitForAssertion(() => _sent.Should().NotBeNull());
        _sent!.PreparedForJourneyId.Should().Be("project.library:loan-flow");
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-prepared-for-journey]").TextContent.Should().Be("Loan flow (revision 3)"));

        cut.FindAll("a[data-testid^=its-area-]").Should().OnlyContain(a => a.GetAttribute("href")!.Contains("journey=project.library%3Aloan-flow"),
            "switching area keeps the journey, so a reload or Back does not silently drop it");
    }

    [Fact]
    public void The_generated_suite_can_be_downloaded_as_one_result_report()
    {
        var cut = Generated();
        cut.Find("[data-testid=its-download-report]").Click();
        var call = JSInterop.Invocations.Single(i => i.Identifier == "downloadTextFile");
        ((string)call.Arguments[0]!).Should().StartWith("integration-suite-").And.Contain("-r2-");
        ((string)call.Arguments[1]!).Should().Contain("Integration test result").And.Contain("M2LB-synthetic.zip").And.Contain("Bufdata");
    }

    [Fact]
    public void The_projects_own_snapshot_is_listed_first_marked_and_chosen_while_other_projects_snapshots_stay_available()
    {
        var other = new IqrSourceSnapshotSummary
        {
            Id = Guid.Parse("aaaaaaaa-2222-0000-0000-0000000000ff"), Archive = new("someone-else.zip", new string('b', 64), 99),
            AnalyzedAt = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero), Status = SourceAnalysisStatus.Ready,
        };
        _api.Setup(a => a.SnapshotsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([other, new IqrSourceSnapshotSummary
        {
            Id = Snap, Archive = new("M2LB-synthetic.zip", new string('a', 64), 1234), AnalyzedAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), Status = SourceAnalysisStatus.Ready,
        }], null));
        var package = new BirkNext.Web.Services.ProjectEvidence.ProjectEvidencePackage("import-m", "M2LB", "M2LB-synthetic.zip", "sha", DateTimeOffset.UnixEpoch, Snap, [],
            new Dictionary<BirkNext.Web.Services.WorkspaceArtifactType, int>(), 0, [], [], [], null);
        var evidence = new Mock<BirkNext.Web.Services.ProjectEvidence.IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(new BirkNext.Web.Services.ProjectEvidence.ProjectEvidenceView("dev", "DEV", null, package, [], [], [], []));
        Services.AddSingleton(evidence.Object);
        var workspace = new Mock<ICurrentWorkspaceProjection>();
        workspace.Setup(w => w.GetAsync()).ReturnsAsync(BirkNext.Web.Tests.Services.WorkspaceSnapshots.AllRoles(project: "m2lb"));
        Services.AddSingleton(workspace.Object);

        var cut = RenderGenerateArea();

        cut.WaitForAssertion(() => cut.Find("[data-testid=its-page]").GetAttribute("aria-busy").Should().Be("false"));
        var options = cut.FindAll("[data-testid=its-snapshot-option]");
        options.Select(o => o.GetAttribute("value")).Should().Equal(Snap.ToString("D"), other.Id.ToString("D"));
        options[0].HasAttribute("checked").Should().BeTrue("the project's own snapshot is chosen by default, never the newest of another project");
        options[1].HasAttribute("checked").Should().BeFalse();
        cut.Find("[data-testid=its-snapshot-own]").TextContent.Should().Contain("this project");
        cut.Find("[data-testid=its-generate]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Without_a_selected_project_generating_says_to_choose_the_project_first_instead_of_failing_after_the_click()
    {
        // Skolenærvær browser run (2026-11-19): on a fresh installation Generate answered 400 "Select a project first" only after the click.
        var cut = RenderGenerateArea();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-page]").GetAttribute("aria-busy").Should().Be("false"));
        Pick(cut);
        cut.Find("[data-testid=its-generate]").HasAttribute("disabled").Should().BeTrue();
        _api.Verify(a => a.GenerateAsync(It.IsAny<IntegrationSuiteGenerateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
