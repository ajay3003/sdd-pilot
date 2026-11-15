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

    private static (TechnicalTestingWorkspace Loader, Mock<ITechnologyCoverageApiService> Coverage) Create(bool project, Guid? snapshot, int? contracts = null)
    {
        var services = new ServiceCollection();
        if (contracts is { } n)
        {
            var api = new Mock<IContractTestingApiService>();
            api.Setup(c => c.ProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<BirkNext.ContractTesting.ContractProject>?)
                [new("repo:roomly", "Roomly", ["repo:roomly"], 1, n, snapshot, DateTimeOffset.UnixEpoch), new("repo:other", "Other", ["repo:other"], 1, 9, Guid.NewGuid(), DateTimeOffset.UnixEpoch)], (string?)null));
            services.AddSingleton(api.Object);
        }
        services.AddSingleton(WorkspaceSnapshots.Projection(project ? WorkspaceSnapshots.Loaded("Imported", "roomly", "Roomly") : CurrentWorkspaceSnapshot.None()).Object);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", TargetUrl = "https://dev.example" } });
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
}