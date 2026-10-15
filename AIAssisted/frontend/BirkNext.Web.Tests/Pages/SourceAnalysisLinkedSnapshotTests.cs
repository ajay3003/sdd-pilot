using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProjectImportPage = BirkNext.Web.Pages.ProjectImport;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Source Analysis opens the exact snapshot an import created (by id, never "the newest"), and its empty state says which of the distinct
/// situations applies: no snapshot at all, an imported project without source, a source part that produced no snapshot, a missing linked snapshot.
/// </summary>
public sealed class SourceAnalysisLinkedSnapshotTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _catalog = new();
    private readonly Mock<IProjectImportApiService> _import = new();
    private readonly WorkspaceArtifactRepository _repository = new();
    private List<IqrSourceSnapshot> _snapshots = [];

    public SourceAnalysisLinkedSnapshotTests()
    {
        _catalog.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _snapshots.ToList());
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext());
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        Services.AddSingleton(_catalog.Object);
        Services.AddSingleton(_import.Object);
        Services.AddSingleton(_repository);
        Services.AddSingleton(new ProjectImportActivation(_repository));
        Services.AddSingleton(context.Object);
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped<SourceAnalysisSnapshotState>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static IqrSourceSnapshot Snapshot(string archive, char fingerprint, DateTimeOffset at, string? importId = null) => new()
    {
        Id = Guid.NewGuid(), IntegrationId = "source-analysis", Archive = new SourceArchive(archive, new string(fingerprint, 64), 10), AnalyzedAt = at,
        Status = SourceAnalysisStatus.Ready,
        ProjectImport = importId is null ? null : new ProjectImportProvenance { ImportId = importId, ArchiveFileName = archive, ArchiveSha256 = new string(fingerprint, 64), ImportedAt = at },
    };

    private void Activate(ProjectImportSourceState state, Guid? snapshot = null, bool source = true, string? message = null)
    {
        var preview = ProjectImportActivationTests.Preview(new string('e', 64), source) with { ProjectName = "skole" };
        var commit = ProjectImportActivationTests.Commit(preview, state, snapshot);
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), commit with { Source = commit.Source with { Message = message } });
    }

    private IRenderedComponent<SourceAnalysis> Open(string url = "source-analysis")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<SourceAnalysis>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-source-state]"));
        return cut;
    }

    private static string Selected(IRenderedComponent<SourceAnalysis> cut) => cut.Find("[data-testid=source-snapshot]").GetAttribute("value") ?? "";

    [Fact]
    public void ExactSnapshotLink_SelectsThatSnapshot_NotTheNewest()
    {
        var at = DateTimeOffset.Parse("2026-10-15T08:00:00Z");
        var newest = Snapshot("other.zip", 'n', at.AddHours(2));
        var linked = Snapshot("skole.zip", 's', at, "import-sssssssssssssss");
        var older = Snapshot("older.zip", 'o', at.AddHours(-2));
        _snapshots = [newest, linked, older];

        var cut = Open($"source-analysis?snapshot={linked.Id:D}");

        Selected(cut).Should().Be(linked.Id.ToString());
        cut.Find("[data-testid=sa-source-state]").TextContent.Should().Be("Current");
        cut.Find("[data-testid=sa-current-origin]").TextContent.Should().StartWith("Project Import");
    }

    [Fact]
    public void MissingLinkedSnapshot_IsExplicit_AndNothingElseIsSubstituted()
    {
        _snapshots = [Snapshot("other.zip", 'n', DateTimeOffset.UtcNow)];
        var missing = Guid.NewGuid();

        var cut = Open($"source-analysis?snapshot={missing:D}");

        cut.Find("[data-testid=sa-source-state]").TextContent.Should().Be("None");
        cut.Find("#sa-empty-heading").TextContent.Should().Be("Linked source snapshot not found");
        cut.Find("[data-testid=sa-empty-reason]").GetAttribute("data-reason").Should().Be("LinkedSnapshotMissing");
        cut.Find("[data-testid=sa-empty-reason]").TextContent.Should().Contain(missing.ToString()[..8]);
    }

    [Fact]
    public void CurrentImportsSnapshot_IsSelectedByDefault_AmongSeveral()
    {
        var at = DateTimeOffset.Parse("2026-10-15T08:00:00Z");
        var newest = Snapshot("other.zip", 'n', at.AddHours(2));
        var linked = Snapshot("skole.zip", 's', at, "import-eeeeeeeeeeeeeeee");
        _snapshots = [newest, linked];
        Activate(ProjectImportSourceState.Created, linked.Id);

        var cut = Open();

        Selected(cut).Should().Be(linked.Id.ToString(), "the imported project's own snapshot, not the newest arbitrary one");
    }

    [Fact]
    public void ImportedProjectWithoutSource_SaysSo()
    {
        Activate(ProjectImportSourceState.NotDetected, source: false);

        var cut = Open();

        cut.Find("#sa-empty-heading").TextContent.Should().Be("This project has no source");
        cut.Find("[data-testid=sa-empty-reason]").TextContent.Should().Contain("skole");
        cut.FindAll("[data-testid=sa-empty-reason-import]").Should().BeEmpty();
    }

    [Fact]
    public void ImportedSourceWithoutSnapshot_ExplainsWhy_AndPointsBackToImportProject_NotToReimport()
    {
        Activate(ProjectImportSourceState.Failed, message: "The source snapshot could not be saved.");

        var cut = Open();

        cut.Find("#sa-empty-heading").TextContent.Should().Be("Source detected, but no snapshot was created");
        cut.Find("[data-testid=sa-empty-reason]").TextContent.Should().Contain("could not be saved");
        cut.Find("[data-testid=sa-empty-reason-import]").GetAttribute("href").Should().Be("project-import");
    }

    [Fact]
    public void NoProjectAndNoSnapshot_KeepsTheGenericEmptyState()
    {
        var cut = Open();

        cut.Find("#sa-empty-heading").TextContent.Should().Be("No source snapshot selected");
        cut.FindAll("[data-testid=sa-empty-reason]").Should().BeEmpty();
        cut.Find("[data-testid=sa-empty-upload]").Should().NotBeNull("source-only upload stays available without any import");
    }

    /// <summary>
    /// The cross-page contract that was missing: preview says source detected → Import project → Open Source Analysis → Source Analysis has
    /// exactly the snapshot the import created selected, with the import's fingerprint and identity — even when newer snapshots exist.
    /// </summary>
    [Fact]
    public void ImportProject_ThenOpenSourceAnalysis_SelectsTheImportsExactSnapshot()
    {
        var preview = ProjectImportActivationTests.Preview(new string('2', 64), source: true,
            ("skole/specs/001-a/spec.md", ProjectImportActivationTests.Spec)) with { ProjectName = "skole" };
        var created = Snapshot("skole.zip", '2', DateTimeOffset.Parse("2026-10-15T08:00:00Z"), preview.ImportId);
        var newer = Snapshot("other.zip", '9', DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
        _import.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync((preview, (SourceUploadFailure?)null));
        _import.Setup(a => a.CommitWithProgressAsync(preview.StagingId, It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectImportActivationTests.Commit(preview, ProjectImportSourceState.Created, created.Id), (SourceUploadFailure?)null));

        var import = Render<ProjectImportPage>();
        import.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "skole.zip"));
        import.WaitForAssertion(() => import.Find("[data-testid=pi-source-status]").TextContent.Should().Contain("Source detected"));
        import.Find("[data-testid=pi-import]").Click();
        import.WaitForAssertion(() => import.Find("[data-testid=pi-result]"));
        var href = import.Find("[data-testid=pi-open-source]").GetAttribute("href")!;

        _snapshots = [newer, created];
        var analysis = Open(href);

        Selected(analysis).Should().Be(created.Id.ToString());
        var selected = _snapshots.Single(s => s.Id.ToString() == Selected(analysis));
        var record = _repository.SddLifecycle.ProjectImports.Single();
        selected.Archive.Sha256.Should().Be(record.ArchiveSha256, "the snapshot and the imported project share the archive fingerprint");
        selected.ProjectImport!.ImportId.Should().Be(record.ImportId, "and the import identity");
        record.SourceSnapshotId.Should().Be(created.Id);
        analysis.FindAll("[data-testid=sa-empty]").Should().BeEmpty("Source Analysis never asks to import the same project again");
    }
}
