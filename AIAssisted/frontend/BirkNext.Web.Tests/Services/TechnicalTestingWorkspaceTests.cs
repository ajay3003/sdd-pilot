using BirkNext.Technology;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The Technical Testing loader reads applicability for the selected project's own source snapshot — never the newest snapshot of another
/// project — and leaves the areas empty (not "none") when no project is selected.
/// </summary>
public sealed class TechnicalTestingWorkspaceTests
{
    private static readonly Guid ProjectSnapshot = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (TechnicalTestingWorkspace Loader, Mock<ITechnologyCoverageApiService> Coverage) Create(bool project, Guid? snapshot, int? contracts = null,
        string slug = "roomly", EnvironmentProject? binding = null)
    {
        var services = new ServiceCollection();
        var active = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", TargetUrl = "https://dev.example", Project = binding };
        var settings = new Mock<IFrontendAnalysisSettingsService>();
        settings.SetupGet(x => x.Settings).Returns(new FrontendAnalysisSettings { Profiles = [active, new FrontendAnalysisProfile { Id = "qa", Name = "Roomly QA", Project = new EnvironmentProject { ProjectName = "Roomly", ProjectImportId = "import-r" } }] });
        services.AddSingleton(settings.Object);
        if (contracts is { } n)
        {
            var api = new Mock<IContractTestingApiService>();
            api.Setup(c => c.ProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<BirkNext.ContractTesting.ContractProject>?)
                [new("repo:roomly", "Roomly", ["repo:roomly"], 1, n, snapshot, DateTimeOffset.UnixEpoch), new("repo:other", "Other", ["repo:other"], 1, 9, Guid.NewGuid(), DateTimeOffset.UnixEpoch)], (string?)null));
            services.AddSingleton(api.Object);
        }
        services.AddSingleton(WorkspaceSnapshots.Projection(project ? WorkspaceSnapshots.Loaded("Imported", slug, "Roomly") : CurrentWorkspaceSnapshot.None()).Object);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = active });
        services.AddSingleton(context.Object);
        var coverage = new Mock<ITechnologyCoverageApiService>();
        coverage.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ProjectTechnologyCoverage { SourceSnapshotId = snapshot });
        services.AddSingleton(coverage.Object);
        if (snapshot is { } s)
        {
            var package = new ProjectEvidencePackage("import-r", "Roomly", "roomly.zip", "sha", DateTimeOffset.UnixEpoch, s, [], new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null);
            var evidence = new Mock<IProjectEvidenceService>();
            evidence.Setup(e => e.Peek(It.IsAny<string?>())).Returns(new ProjectEvidenceView("dev", "DEV", null, package, [], [], [], []));
            services.AddSingleton(evidence.Object);
        }
        var provider = services.BuildServiceProvider();
        return (new TechnicalTestingWorkspace(provider), coverage);
    }

    [Fact]
    public async Task Without_a_project_snapshot_no_coverage_is_read_so_another_projects_source_cannot_leak_in()
    {
        var (loader, coverage) = Create(project: true, snapshot: null);
        var ws = await loader.LoadAsync();
        coverage.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never());
        coverage.Verify(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        ws.Facts.ProjectKey.Should().Be("roomly");
        ws.Facts.Areas.Should().HaveCount(5).And.OnlyContain(a => a.Applicability != null, "applicability is still evaluated, with no source evidence");
    }

    [Fact]
    public async Task Coverage_is_read_for_the_selected_projects_own_snapshot()
    {
        var (loader, coverage) = Create(project: true, snapshot: ProjectSnapshot);
        var ws = await loader.LoadAsync();
        ws.Facts.Error.Should().BeNull();
        ws.SnapshotId.Should().Be(ProjectSnapshot);
        ws.Facts.TargetName.Should().Be("DEV");
        coverage.Verify(c => c.GetAsync("dev", ProjectSnapshot, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Without_a_project_nothing_project_specific_is_read()
    {
        var (loader, coverage) = Create(project: false, snapshot: ProjectSnapshot);
        var ws = await loader.LoadAsync();
        ws.Facts.ProjectKey.Should().BeNull();
        ws.Facts.Areas.Should().OnlyContain(a => a.Applicability == null && a.Runs.Count == 0);
        coverage.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, true)]
    public async Task Contracts_are_static_evidence_only_when_the_projects_own_source_has_some(int contracts, bool shown)
    {
        var (loader, _) = Create(project: true, snapshot: ProjectSnapshot, contracts: contracts);
        var api = (await loader.LoadAsync()).Facts.For(TechnicalArea.Api);
        api.Static.Any(s => s.Text.Contains("contract")).Should().Be(shown, "another project's 9 contracts never count, and 0 contracts is not evidence");
        if (shown) api.Static.Should().Contain(s => s.Text.StartsWith("3 contracts") && s.Href == "api-quality-review/contracts?project=repo%3Aroomly");
    }

    [Fact]
    public async Task An_environment_bound_to_another_project_contributes_no_results_and_says_why()
    {
        var (loader, coverage) = Create(project: true, snapshot: ProjectSnapshot, slug: "import:import-r", binding: new EnvironmentProject { ProjectName = "Lendo", ProjectImportId = "import-l" });
        var ws = await loader.LoadAsync();

        ws.Facts.EnvironmentNotice.Should().Contain("bound to Lendo").And.Contain("Roomly QA", "the environments bound to this project are named");
        ws.Facts.Areas.Should().OnlyContain(a => a.Runs.Count == 0, "another project's runtime results are never shown as this project's");
        ws.Facts.For(TechnicalArea.Frontend).ConfigurationGaps.Should().Contain(g => g.Contains("belongs to another project"));
        coverage.Verify(c => c.GetAsync("dev", It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task An_environment_bound_to_this_project_is_used_without_a_notice()
    {
        var (loader, coverage) = Create(project: true, snapshot: ProjectSnapshot, slug: "import:import-r", binding: new EnvironmentProject { ProjectName = "Roomly", ProjectImportId = "import-r" });
        var ws = await loader.LoadAsync();

        ws.Facts.EnvironmentNotice.Should().BeNull();
        ws.Facts.TargetName.Should().Be("DEV");
        coverage.Verify(c => c.GetAsync("dev", ProjectSnapshot, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Stored_API_review_runs_are_read_from_the_server_and_each_opens_its_own_report()
    {
        var services = new ServiceCollection();
        services.AddSingleton(WorkspaceSnapshots.Projection(WorkspaceSnapshots.Loaded("Imported", "roomly", "Roomly")).Object);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", TargetUrl = "https://dev.example" } });
        services.AddSingleton(context.Object);
        var (newer, older) = (Guid.NewGuid(), Guid.NewGuid());
        var at = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var history = new Mock<ITechnicalHistoryApiService>();
        history.Setup(h => h.ApiReviewRunsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<BirkNext.TechnicalHistory.StoredApiReviewRun>?)
            [new(newer, "roomly", "dev", "DEV", at, at, 2, 2, 0, 1, 0, 0, 0, at), new(older, "roomly", "dev", "DEV", at.AddDays(-1), at.AddDays(-1), 2, 2, 0, 0, 0, 0, 0, at)], (string?)null));
        services.AddSingleton(history.Object);

        var api = (await new TechnicalTestingWorkspace(services.BuildServiceProvider()).LoadAsync()).Facts.For(TechnicalArea.Api);

        api.Runs.Select(r => r.Href).Should().Equal($"api-quality-review?run={newer:D}&tab=findings", $"api-quality-review?run={older:D}&tab=findings");
    }

    [Fact]
    public async Task Specialist_reviews_stored_on_the_server_are_shown_for_this_project_only_and_a_frontend_review_opens_its_own_report()
    {
        var services = new ServiceCollection();
        services.AddSingleton(WorkspaceSnapshots.Projection(WorkspaceSnapshots.Loaded("Imported", "roomly", "Roomly")).Object);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", TargetUrl = "https://dev.example" } });
        services.AddSingleton(context.Object);
        var at = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        BirkNext.TechnicalHistory.StoredTechnicalReviewRun Run(Guid id, string kind, string? project, string outcome, int problems) =>
            new(id, kind, project, "dev", "DEV", null, null, at, outcome, 10, problems, "summary", new string('a', 64), at);
        var (fqr, other, fuzz, authz) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var history = new Mock<ITechnicalHistoryApiService>();
        history.Setup(h => h.ReviewRunsAsync(BirkNext.TechnicalHistory.TechnicalReviewKinds.FrontendQuality, "dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<BirkNext.TechnicalHistory.StoredTechnicalReviewRun>?)
            [Run(fqr, BirkNext.TechnicalHistory.TechnicalReviewKinds.FrontendQuality, "roomly", "ReviewRequired", 2), Run(other, BirkNext.TechnicalHistory.TechnicalReviewKinds.FrontendQuality, "lendo", "Blocked", 5)], (string?)null));
        history.Setup(h => h.ReviewRunsAsync(BirkNext.TechnicalHistory.TechnicalReviewKinds.SafeFuzzing, "dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<BirkNext.TechnicalHistory.StoredTechnicalReviewRun>?)
            [Run(fuzz, BirkNext.TechnicalHistory.TechnicalReviewKinds.SafeFuzzing, "roomly", "Full", 0)], (string?)null));
        history.Setup(h => h.ReviewRunsAsync(BirkNext.TechnicalHistory.TechnicalReviewKinds.Authorization, "dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<BirkNext.TechnicalHistory.StoredTechnicalReviewRun>?)
            [Run(authz, BirkNext.TechnicalHistory.TechnicalReviewKinds.Authorization, null, "Unexpected access", 1)], (string?)null));
        history.Setup(h => h.ApiReviewRunsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<BirkNext.TechnicalHistory.StoredApiReviewRun>?)
            [new(Guid.NewGuid(), "lendo", "dev", "DEV", at, at, 1, 1, 0, 0, 0, 0, 0, at)], (string?)null));
        services.AddSingleton(history.Object);

        var facts = (await new TechnicalTestingWorkspace(services.BuildServiceProvider()).LoadAsync()).Facts;

        var frontend = facts.For(TechnicalArea.Frontend).Runs.Should().ContainSingle("another project's review is not shown").Subject;
        frontend.Href.Should().Be($"frontend-quality-review?storedRun={fqr:D}");
        frontend.State.Should().Be(AreaRuntime.ReviewRequired);
        var api = facts.For(TechnicalArea.Api).Runs;
        api.Select(r => r.Where).Should().BeEquivalentTo(["Safe fuzzing", "Authorization checks"], "another project's API review run is not shown; a run stored without a project is");
        api.Single(r => r.Where == "Safe fuzzing").State.Should().Be(AreaRuntime.Passed);
        api.Single(r => r.Where == "Authorization checks").State.Should().Be(AreaRuntime.Failed);
    }
}
