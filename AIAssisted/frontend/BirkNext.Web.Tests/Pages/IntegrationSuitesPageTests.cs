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
    };

    private IRenderedComponent<IntegrationSuitesPage> Generated()
    {
        var cut = Render<IntegrationSuitesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-snapshot]"));
        cut.Find("[data-testid=its-snapshot]").Change(Snap.ToString("D"));
        cut.Find("[data-testid=its-generate]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-suite]"));
        return cut;
    }

    [Fact]
    public void Generate_is_disabled_until_a_snapshot_is_chosen_and_sends_the_chosen_scope_and_snapshot()
    {
        var cut = Render<IntegrationSuitesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-generate]").HasAttribute("disabled").Should().BeTrue());
        cut.Find("[data-testid=its-snapshot]").Change(Snap.ToString("D"));
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
        var cut = Render<IntegrationSuitesPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-snapshot]"));
        cut.Find("[data-testid=its-snapshot]").Change(Snap.ToString("D"));
        cut.Find("[data-testid=its-generate]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-error]").TextContent.Should().Contain("failed analysis"));
        cut.FindAll("[data-testid=its-suite]").Should().BeEmpty();
    }
}
