using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProjectImportPage = BirkNext.Web.Pages.ProjectImport;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// "Import another project" after a successful import: it is itself a file chooser, so choosing the next ZIP starts the next preview at once
/// (it used to only reset the page and leave the upload control at the end of a long page). Only the transient import session is reset; the
/// first project stays current until the second import succeeds, its record and snapshot stay, nothing is committed before Import, duplicates
/// are still detected, and no IQR binding is touched. Generic fixtures.
/// </summary>
public sealed class ProjectImportAnotherTests : BunitContext
{
    private readonly Mock<IProjectImportApiService> _api = new();
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new(MockBehavior.Strict);
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly ProjectImportActivation _activation;
    private readonly Queue<(ProjectImportPreview? Preview, SourceUploadFailure? Failure)> _previews = new();
    private readonly Dictionary<Guid, ProjectImportCommitResult> _commits = [];

    private const string ShaA = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string ShaB = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private static readonly Guid SnapshotA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SnapshotB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public ProjectImportAnotherTests()
    {
        _activation = new ProjectImportActivation(_repository);
        _api.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _previews.Dequeue());
        _api.Setup(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid staging, IProgress<ProjectImportCommitEvent>? _, CancellationToken _) => (_commits[staging], (SourceUploadFailure?)null));
        // Source setup is read-only; no binding endpoint is ever called (strict mock).
        _journeys.Setup(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<SourceSetupView>(new SourceSetupView(), null));
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_journeys.Object);
        Services.AddSingleton(_repository);
        Services.AddSingleton(_activation);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private ProjectImportPreview Queue(string archive, string sha, Guid snapshot)
    {
        var preview = ProjectImportActivationTests.Preview(sha, source: true, ("app/specs/001/spec.md", ProjectImportActivationTests.Spec)) with
        {
            Archive = new ProjectImportArchive(archive, sha, 4096, 12), ProjectName = Path.GetFileNameWithoutExtension(archive),
        };
        _previews.Enqueue((preview, null));
        _commits[preview.StagingId] = ProjectImportActivationTests.Commit(preview, ProjectImportSourceState.Created, snapshot);
        return preview;
    }

    private static void Choose(IRenderedComponent<ProjectImportPage> cut, string inputTestId, string file)
    {
        cut.FindComponents<InputFile>().Single(i => i.Instance.AdditionalAttributes?["data-testid"]?.ToString() == inputTestId)
            .UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], file));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pi-preview], [data-testid=pi-failure]").Should().NotBeEmpty());
    }

    private IRenderedComponent<ProjectImportPage> ImportFirst()
    {
        Queue("orders-a.zip", ShaA, SnapshotA);
        var cut = Render<ProjectImportPage>();
        Choose(cut, "pi-choose-file", "orders-a.zip");
        cut.Find("[data-testid=pi-import]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        return cut;
    }

    [Fact]
    public void AfterASuccessfulImport_ImportAnotherIsItselfAKeyboardReachableFileChooser()
    {
        var cut = ImportFirst();
        var label = cut.Find("[data-testid=pi-import-another]");
        label.TagName.Should().Be("LABEL");
        label.TextContent.Trim().Should().Be("Import another project");
        var input = cut.Find("[data-testid=pi-import-another-file]");
        input.GetAttribute("type").Should().Be("file", "clicking or pressing Enter/Space opens the chooser at once");
        input.GetAttribute("accept").Should().Be(".zip");
        input.GetAttribute("aria-label").Should().Contain("Import another project");
    }

    [Fact]
    public void ChoosingTheNextZip_StartsTheNextPreview_AndImportingItKeepsTheFirstAsHistory()
    {
        var cut = ImportFirst();
        _activation.Current!.SourceSnapshotId.Should().Be(SnapshotA);
        Queue("billing-b.zip", ShaB, SnapshotB);

        Choose(cut, "pi-import-another-file", "billing-b.zip");
        cut.Find("[data-testid=pi-preview]").TextContent.Should().Contain("billing-b.zip");
        cut.FindAll("[data-testid=pi-result]").Should().BeEmpty("the previous result is cleared");
        _activation.Current!.ArchiveFileName.Should().Be("orders-a.zip", "choosing a file activates nothing; A stays current until B succeeds");
        _api.Verify(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()), Times.Once,
            "choosing a file commits nothing — only Import does");

        cut.Find("[data-testid=pi-import]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        _activation.Current!.ArchiveFileName.Should().Be("billing-b.zip");
        var a = _activation.Imports.Single(i => i.ArchiveSha256 == ShaA);
        a.SourceSnapshotId.Should().Be(SnapshotA, "the first import and its snapshot are preserved");
        _repository.SddLifecycle.Revisions.Should().Contain(r => r.ProjectImportId == a.ImportId, "A's documents stay in the repository");
        _journeys.Verify(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _journeys.VerifyNoOtherCalls();
    }

    [Fact]
    public void TheCurrentProjectCard_OffersImportAnother_WithoutLeavingTheProject()
    {
        var cut = ImportFirst();
        Queue("billing-b.zip", ShaB, SnapshotB);
        cut.Find("[data-testid=pi-result]");
        cut.Dispose();
        var page = Render<ProjectImportPage>();
        page.Find("[data-testid=pi-current] [data-testid=pi-current-import-another]").TextContent.Trim().Should().Be("Import another project");
        Choose(page, "pi-current-import-another-file", "billing-b.zip");
        page.Find("[data-testid=pi-preview]").TextContent.Should().Contain("billing-b.zip");
    }

    [Fact]
    public void ChoosingTheSameZipAgain_StillShowsTheDuplicateChoice()
    {
        var cut = ImportFirst();
        Queue("orders-a-copy.zip", ShaA, SnapshotA);
        Choose(cut, "pi-import-another-file", "orders-a-copy.zip");
        cut.Find("[data-testid=pi-duplicate]").TextContent.Should().Contain("Matching imported source already exists");
        cut.Find("[data-testid=pi-import]").HasAttribute("disabled").Should().BeTrue("a duplicate is never re-imported silently");
    }

    [Fact]
    public void AFailedSecondImport_LeavesTheFirstProjectIntact_AndAnotherFileCanBeChosen()
    {
        var cut = ImportFirst();
        _previews.Enqueue((null, new SourceUploadFailure("ARCHIVE_INVALID", "validation", "The file is not a ZIP archive.")));
        Choose(cut, "pi-import-another-file", "not-a-zip.zip");

        cut.Find("[data-testid=pi-failure]").TextContent.Should().Contain("not a ZIP");
        _activation.Current!.ArchiveFileName.Should().Be("orders-a.zip");
        _activation.Current!.SourceSnapshotId.Should().Be(SnapshotA);
        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("orders-a.zip");
        cut.FindAll("[data-testid=pi-choose-file]").Should().ContainSingle("the upload control is available again");
    }

    [Fact]
    public void BackFromThePreview_ReturnsToTheCurrentProject_WithoutLosingIt()
    {
        var cut = ImportFirst();
        Queue("billing-b.zip", ShaB, SnapshotB);
        Choose(cut, "pi-import-another-file", "billing-b.zip");
        cut.Find("[data-testid=pi-cancel]").Click();
        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("orders-a.zip");
        _activation.Imports.Should().ContainSingle();
    }
}
