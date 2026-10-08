using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Source Analysis after a Project Import: the snapshot the import created is already the current one (no second upload), it says which
/// import created it, and Import Project is the recommended action while a source-only upload stays available.
/// </summary>
public sealed class SourceAnalysisProjectImportTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _api = new();
    private List<IqrSourceSnapshot> _snapshots = [];

    public SourceAnalysisProjectImportTests()
    {
        _api.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _snapshots.ToList());
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static IqrSourceSnapshot Snapshot(ProjectImportProvenance? provenance) => new()
    {
        Id = Guid.NewGuid(), IntegrationId = "source-analysis", Archive = new SourceArchive("shop.zip", new string('a', 64), 42),
        AnalyzedAt = DateTimeOffset.Parse("2026-10-08T08:00:00Z"), Status = SourceAnalysisStatus.Ready, ProjectImport = provenance,
    };

    [Fact]
    public void SnapshotFromProjectImport_IsCurrentWithoutAnotherUpload_AndShowsItsImport()
    {
        var provenance = new ProjectImportProvenance { ImportId = "import-aaaaaaaaaaaaaaaa", ArchiveFileName = "shop.zip", ArchiveSha256 = new string('a', 64), ImportedAt = DateTimeOffset.Parse("2026-10-08T08:00:00Z") };
        _snapshots = [Snapshot(provenance)];

        var cut = Render<SourceAnalysis>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-current]"));
        cut.Find("[data-testid=sa-current-origin]").TextContent.Should().StartWith("Project Import");
        var link = cut.Find("[data-testid=sa-import-provenance]");
        link.GetAttribute("data-import").Should().Be(provenance.ImportId);
        link.QuerySelector("a")!.GetAttribute("href").Should().Be("project-import");
        _api.Verify(a => a.AnalyzeSourceSnapshotDetailedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void StandaloneUpload_SnapshotSaysSo()
    {
        _snapshots = [Snapshot(null)];

        var cut = Render<SourceAnalysis>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-current]"));
        cut.Find("[data-testid=sa-current-origin]").TextContent.Should().Be("Source-only upload");
        cut.FindAll("[data-testid=sa-import-provenance]").Should().BeEmpty();
    }

    [Fact]
    public void EmptyState_RecommendsImportProject_AndKeepsTheSourceOnlyUpload()
    {
        var cut = Render<SourceAnalysis>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-empty]"));
        cut.Find("[data-testid=sa-empty-import-project]").ClassList.Should().Contain("sa-file-primary");
        cut.Find("[data-testid=sa-empty-import-project]").GetAttribute("href").Should().Be("project-import");
        cut.Find("[data-testid=sa-empty-upload]").GetAttribute("aria-label").Should().Contain("Upload source only");
        cut.FindAll("[data-testid=sa-upload]").Should().BeEmpty("the source-only upload is offered once, in the empty state");
    }
}
