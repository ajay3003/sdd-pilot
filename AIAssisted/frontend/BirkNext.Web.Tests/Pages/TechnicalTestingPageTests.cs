using BirkNext.ApiReview;
using BirkNext.ContractTesting;
using BirkNext.PerformanceTests;
using BirkNext.Technology;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The Technical Testing home and stages over stored specialist results: the shared workspace header with three stages, one next step, the areas
/// that apply (and the ones that do not, with why), recorded runtime results with environment and identity, out-of-date results marked, static
/// evidence kept apart from runtime results, per-area filtering, and read failures shown instead of "none".
/// </summary>
public sealed class TechnicalTestingPageTests : BunitContext
{
    private static readonly Guid CurrentSnapshot = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Mock<IApiReviewHistoryService> _aqr = new();
    private readonly Mock<IPerformanceTestApiService> _perf = new();
    private readonly Mock<IContractTestingApiService> _contracts = new();

    private sealed class FakeCoverageApi(ProjectTechnologyCoverage? coverage) : ITechnologyCoverageApiService
    {
        public Task<ProjectTechnologyCoverage?> GetAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(coverage);
    }

    private void Setup(bool project = true, bool target = true, List<ApiReviewRunSummary>? aqr = null, List<PerformanceTestRun>? perf = null, bool contractsFail = false)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(project
            ? BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")
            : CurrentWorkspaceSnapshot.None()).Object);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(target
            ? new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "qa", Name = "QA", TargetUrl = "https://qa.example" } }
            : new FrontendAnalysisContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddSingleton<ITechnologyCoverageApiService>(new FakeCoverageApi(new ProjectTechnologyCoverage { EnvironmentId = "qa", SourceSnapshotId = CurrentSnapshot }));
        Services.AddScoped<ProjectApplicabilityState>();
        _aqr.Setup(a => a.For("qa")).Returns(new ApiReviewHistory { Runs = aqr ?? [] });
        Services.AddSingleton(_aqr.Object);
        _perf.Setup(p => p.RunsAsync("qa", null, It.IsAny<CancellationToken>())).ReturnsAsync(perf ?? []);
        Services.AddSingleton(_perf.Object);
        if (contractsFail) _contracts.Setup(c => c.ProjectsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        else _contracts.Setup(c => c.ProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<ContractProject>?)[], (string?)null));
        Services.AddSingleton(_contracts.Object);
    }

    private IRenderedComponent<TComponent> Open<TComponent>(string url) where TComponent : IComponent
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<TComponent>();
        cut.WaitForAssertion(() => cut.Find("[aria-busy=false]"));
        return cut;
    }

    private static ApiReviewRunSummary AqrRun(int high) => new(DateTimeOffset.Parse("2026-11-14T10:00:00Z"), "QA", 2, 2, 0, high, 0, 0, 0);

    [Fact]
    public void Home_shows_the_shared_header_with_three_stages_and_the_project_and_target()
    {
        Setup();
        var cut = Open<TechnicalTesting>("technical-testing");
        cut.FindAll("[data-testid=twh-stage]").Select(a => a.GetAttribute("href")).Should().Equal(
            "technical-testing", "technical-testing/assess", "technical-testing/configure", "technical-testing/verify");
        cut.Find("[data-testid=twh-title]").TextContent.Should().Be("Technical Testing");
        cut.Find("[data-testid=twh-project]").TextContent.Should().Contain("Library").And.Contain("Target: QA");
        cut.FindAll("[data-testid=tt-stage]").Should().HaveCount(3);
    }

    [Fact]
    public void Without_a_project_the_next_step_is_choosing_one_and_no_area_is_shown()
    {
        Setup(project: false);
        var cut = Open<TechnicalTesting>("technical-testing");
        cut.Find("[data-testid=tt-next-action]").GetAttribute("href").Should().Be("project-import");
        cut.FindAll("[data-testid=tt-area]").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_recorded_result_is_the_next_step_and_keeps_its_environment()
    {
        Setup(aqr: [AqrRun(high: 2)]);
        var cut = Open<TechnicalTesting>("technical-testing");
        cut.Find("[data-testid=tt-next-action]").GetAttribute("href").Should().Be("technical-testing/verify?area=api");
        var api = cut.Find("[data-testid=tt-area][data-area=api]");
        api.GetAttribute("data-runtime").Should().Be("Failed");
        api.QuerySelector("[data-testid=tt-runtime]")!.TextContent.Should().Be("2 high-severity findings");

    }

    [Fact]
    public void Verify_lists_runtime_results_with_identity_and_marks_runs_on_an_earlier_source_snapshot()
    {
        var run = new PerformanceTestRun { State = PerformanceRunState.Completed, Verdict = PerformanceQualityVerdict.Pass, VersionLabel = "1.4.2",
            SourceSnapshotId = Guid.NewGuid(), FinishedAt = DateTimeOffset.Parse("2026-11-13T09:00:00Z") };
        Setup(perf: [run]);
        Services.AddSingleton(Evidence(CurrentSnapshot));
        var cut = Open<TechnicalTestingStage>("technical-testing/verify?area=performance");
        cut.Find("[data-testid=tts]").GetAttribute("data-stage").Should().Be("verify");
        cut.FindAll("[data-testid=tt-area]").Select(a => a.GetAttribute("data-area")).Should().Equal("performance");
        var r = cut.Find("[data-testid=tt-run]");
        r.TextContent.Should().Contain("Targets met").And.Contain("QA").And.Contain("1.4.2").And.Contain("Performance Test Review");
        r.GetAttribute("data-stale").Should().Be("true");
        cut.Find("[data-testid=tt-run-stale]").TextContent.Should().Contain("earlier source snapshot");
        cut.Find("[data-testid=tt-runtime]").TextContent.Should().Contain("may be out of date");
        cut.Find("[data-testid=twh-stage][data-stage=verify]").GetAttribute("aria-current").Should().Be("page");
    }

    [Fact]
    public void An_area_without_runs_says_static_evidence_is_not_a_runtime_result()
    {
        Setup();
        var cut = Open<TechnicalTestingStage>("technical-testing/verify?area=api");
        cut.Find("[data-testid=tt-runs-none]").TextContent.Should().Contain("Static evidence for this area is not a runtime result");
        cut.Find("[data-testid=tt-area]").GetAttribute("data-runtime").Should().Be("NotRun");
        cut.FindAll("[data-testid=tt-run]").Should().BeEmpty();
    }

    [Fact]
    public void Assess_lists_every_area_with_its_applicability_and_the_area_filter()
    {
        Setup();
        var cut = Open<TechnicalTestingStage>("technical-testing/assess");
        cut.FindAll("[data-testid=tt-area]").Select(a => a.GetAttribute("data-area")).Should().Equal("frontend", "api", "integration", "performance", "e2e");
        cut.FindAll("[data-testid=tt-filter-area]").Select(a => a.GetAttribute("href")).Should().Contain("technical-testing/assess?area=integration");
        cut.Find("[data-testid=tt-filter-all]").GetAttribute("aria-current").Should().Be("page");
        cut.FindAll("[data-testid=tt-applicability]").Should().HaveCount(5);
    }

    [Fact]
    public void Configure_without_a_target_says_so_and_links_where_to_set_it()
    {
        Setup(target: false);
        var cut = Open<TechnicalTestingStage>("technical-testing/configure?area=api");
        cut.Find("[data-testid=twh-project]").TextContent.Should().Contain("No target application");
        cut.Find("[data-testid=tt-area]").TextContent.Should().Contain("Target: none active");
        cut.FindAll("[data-testid=tt-area-link]").Select(a => a.GetAttribute("href")).Should().Contain("api-quality-review");
    }

    [Fact]
    public void The_area_card_lists_what_configuration_is_missing()
    {
        var area = new AreaFacts(TechnicalArea.CriticalE2E, new BirkNext.Applicability.ReviewApplicability { Status = BirkNext.Applicability.ApplicabilityStatus.Applicable, Reason = "flows exist" },
            [], ["No critical flow is defined yet."], []);
        var cut = Render<BirkNext.Web.Components.TechnicalAreaCard>(p => p.Add(c => c.Area, area).Add(c => c.Stage, "configure").Add(c => c.TargetName, "QA").Add(c => c.TestDataAssets, 3));
        cut.Find("[data-testid=tt-area]").GetAttribute("data-configuration").Should().Be("NeedsConfiguration");
        cut.Find("[data-testid=tt-gap]").TextContent.Should().Be("No critical flow is defined yet.");
        cut.Find("[data-testid=tt-configuration]").TextContent.Should().Be("Configuration needed");
        cut.Find("[data-testid=tt-runtime]").TextContent.Should().Be("Not run");
        cut.Markup.Should().Contain("Target: QA · Test Data: 3 assets");
    }
    [Fact]
    public void A_failed_read_is_shown_on_its_area_and_the_page_still_renders()
    {
        Setup(contractsFail: true);
        Services.AddSingleton(Evidence(CurrentSnapshot));
        var cut = Open<TechnicalTestingStage>("technical-testing/assess?area=api");
        cut.Find("[data-testid=tt-area-error]").TextContent.Should().Be("Contracts could not be read.");
        cut.FindAll("[data-testid=tt-area]").Should().ContainSingle();
    }

    /// <summary>Project evidence whose main package is the given snapshot (Peek only; no backend).</summary>
    private static BirkNext.Web.Services.ProjectEvidence.IProjectEvidenceService Evidence(Guid snapshot)
    {
        var package = new BirkNext.Web.Services.ProjectEvidence.ProjectEvidencePackage("import-lib", "Library", "library.zip", "sha", DateTimeOffset.UnixEpoch, snapshot, [],
            new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null);
        var evidence = new Mock<BirkNext.Web.Services.ProjectEvidence.IProjectEvidenceService>();
        evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(new BirkNext.Web.Services.ProjectEvidence.ProjectEvidenceView("qa", "QA", null, package, [], [], [], []));
        return evidence.Object;
    }
}