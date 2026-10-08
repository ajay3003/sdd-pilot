using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
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
/// Import Project page: one archive chosen once; preview of documents and source; the import result keeps the two domains separate; one side
/// empty is neutral, never a red failure; a rejected archive activates nothing. Generic fixtures only.
/// </summary>
public sealed class ProjectImportPageTests : BunitContext
{
    private readonly Mock<IProjectImportApiService> _api = new();
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();
    private ProjectImportPreview? _preview;
    private SourceUploadFailure? _previewFailure;
    private ProjectImportCommitResult? _commit;
    private int _previews;
    private FrontendAnalysisProfile? _profile = new() { Id = "dev", Name = "Dev" };

    public ProjectImportPageTests()
    {
        _api.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { _previews++; return (_preview, _previewFailure); });
        _api.Setup(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (_commit, (SourceUploadFailure?)null));
        _autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => _profile is null ? new FrontendAnalysisContext() : new FrontendAnalysisContext { ActiveProfile = _profile });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_repository);
        Services.AddSingleton(new ProjectImportActivation(_repository));
        Services.AddSingleton(context.Object);
        Services.AddSingleton(_autoSave.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static readonly (string, string)[] Docs =
    [
        ("shop/.specify/memory/constitution.md", ProjectImportActivationTests.Constitution),
        ("shop/specs/001-cart/spec.md", ProjectImportActivationTests.Spec),
        ("shop/specs/001-cart/plan.md", ProjectImportActivationTests.Plan),
        ("shop/specs/001-cart/tasks.md", ProjectImportActivationTests.Tasks),
    ];

    private static ProjectImportPreview WithSource(ProjectImportPreview preview, params ProjectImportTechnology[] technologies) => preview with
    {
        Source = new ProjectImportSourceDetection { Detected = true, SourceFiles = 12, Technologies = [.. technologies] },
    };

    private IRenderedComponent<ProjectImportPage> ChooseArchive()
    {
        var cut = Render<ProjectImportPage>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "shop.zip"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pi-preview], [data-testid=pi-failure]").Should().NotBeEmpty());
        return cut;
    }

    [Fact]
    public void InitialState_OffersOneArchiveForDocumentsSourceOrBoth()
    {
        var cut = Render<ProjectImportPage>();

        cut.Find("[data-testid=pi-choose]").TextContent.Should().Contain("Choose a project archive");
        cut.Find("[data-testid=pi-outcomes]").TextContent.Should().Contain("Project documents").And.Contain("Source").And.Contain("Both");
        cut.Find("[data-testid=pi-choose-file]").GetAttribute("accept").Should().Be(".zip");
        cut.Find("[data-testid=pi-choose-file]").GetAttribute("aria-label").Should().Contain("Choose project ZIP");
        cut.FindAll("[data-testid=pi-current]").Should().BeEmpty();
    }

    [Fact]
    public void MixedArchive_PreviewShowsDocumentsAndSource_BeforeAnythingIsImported()
    {
        _preview = WithSource(ProjectImportActivationTests.Preview(new string('a', 64), source: true, Docs),
            new ProjectImportTechnology("lang.csharp", "C#", "Language", true, 8), new ProjectImportTechnology("iac.terraform", "Terraform", "Cloud", true, 2));

        var cut = ChooseArchive();

        cut.Find("[data-testid=pi-project-name]").TextContent.Should().Contain("shop").And.Contain("root folder");
        cut.FindAll("[data-testid=pi-role][data-state=Detected]").Select(r => r.GetAttribute("data-role"))
            .Should().BeEquivalentTo(["Constitution", "Specification", "Plan", "Tasks"]);
        cut.Find("[data-testid=pi-role][data-role=DataModel]").TextContent.Should().Contain("Not found").And.Contain("Optional");
        cut.Find("[data-testid=pi-source-status]").TextContent.Should().Contain("Source detected");
        cut.FindAll("[data-testid=pi-technology]").Select(t => t.TextContent).Should().Contain(["C#", "Terraform"]);
        _repository.SddLifecycle.ProjectImports.Should().BeEmpty("the preview activates nothing");
        _api.Verify(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void MixedImport_ResultShowsBothDomains_WithOneProvenance_AndLinksToEach()
    {
        var snapshot = Guid.NewGuid();
        _preview = WithSource(ProjectImportActivationTests.Preview(new string('a', 64), source: true, Docs));
        _commit = ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, snapshot);
        var cut = ChooseArchive();

        cut.Find("[data-testid=pi-import]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        cut.Find("[data-testid=pi-result]").GetAttribute("data-outcome").Should().Be(nameof(ProjectImportOutcome.Imported));
        cut.Find("[data-testid=pi-artifacts]").GetAttribute("data-has-artifacts").Should().Be("true");
        cut.Find("[data-testid=pi-source]").GetAttribute("data-state").Should().Be("Created");
        cut.Find("[data-testid=pi-snapshot-id]").TextContent.Should().Be(snapshot.ToString()[..8]);
        cut.Find("[data-testid=pi-import-id]").TextContent.Should().Be(_preview.ImportId);
        cut.Find("[data-testid=pi-result]").TextContent.Should().Contain("Imported is not reviewed or approved");
        cut.Find("[data-testid=pi-open-source]").GetAttribute("href").Should().Be("source-analysis");
        cut.Find("[data-testid=pi-open-documents]").GetAttribute("href").Should().Be("constitution-explorer");
        cut.Find("[data-testid=pi-target]").TextContent.Should().Contain("Selected").And.Contain("does not own or change the source snapshot");
        _previews.Should().Be(1, "the archive is uploaded once");
        _repository.SddLifecycle.ProjectImports.Single().SourceSnapshotId.Should().Be(snapshot);
        _autoSave.Verify(a => a.SaveNowAsync(), Times.Once);
    }

    [Fact]
    public void DocumentsOnly_SourceIsNeutral_NotAFailure()
    {
        _preview = ProjectImportActivationTests.Preview(new string('b', 64), source: false, Docs);
        _commit = ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.NotDetected);
        var cut = ChooseArchive();
        cut.Find("[data-testid=pi-source-status]").TextContent.Should().Contain("No source detected");

        cut.Find("[data-testid=pi-import]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        cut.Find("[data-testid=pi-result]").GetAttribute("data-outcome").Should().Be(nameof(ProjectImportOutcome.Imported));
        cut.Find("[data-testid=pi-source] .pi-badge").ClassList.Should().Contain("pi-badge-neutral");
        cut.FindAll(".pi-tone-rejected, [role=alert]").Should().BeEmpty("one empty side is never a red failure");
    }

    [Fact]
    public void SourceOnly_ArtifactsAreNeutral_AndSourceAnalysisIsTheNextStep()
    {
        _preview = WithSource(ProjectImportActivationTests.Preview(new string('c', 64), source: true));
        _commit = ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, Guid.NewGuid());
        var cut = ChooseArchive();

        cut.Find("[data-testid=pi-import]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        cut.Find("[data-testid=pi-artifacts-none]").TextContent.Should().Contain("No supported project artifacts detected");
        cut.Find("[data-testid=pi-artifacts-none] .pi-badge").ClassList.Should().Contain("pi-badge-neutral");
        cut.Find("[data-testid=pi-open-source]").ClassList.Should().Contain("btn-primary");
        cut.FindAll(".pi-tone-rejected").Should().BeEmpty();
    }

    [Fact]
    public void AmbiguousRole_IsImportedWithNotes_AndTheSourceStillSucceeds()
    {
        _preview = WithSource(ProjectImportActivationTests.Preview(new string('d', 64), source: true,
            ("a/spec.md", ProjectImportActivationTests.Spec), ("b/spec.md", ProjectImportActivationTests.Spec.Replace("Cart", "Checkout"))));
        _commit = ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, Guid.NewGuid());
        var cut = ChooseArchive();
        cut.Find("[data-testid=pi-role][data-role=Specification]").GetAttribute("data-state").Should().Be("2 candidates");

        cut.Find("[data-testid=pi-import]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        cut.Find("[data-testid=pi-result]").GetAttribute("data-outcome").Should().Be(nameof(ProjectImportOutcome.ImportedWithNotes));
        cut.Find("[data-testid=pi-notes]").TextContent.Should().Contain("Specification").And.Contain("Choose one");
        cut.Find("[data-testid=pi-source]").GetAttribute("data-state").Should().Be("Created");
    }

    [Fact]
    public void NoTargetEnvironment_MixedImport_CreatesTheSourceSnapshot_AndTheTargetIsOnlyRuntimeContext()
    {
        _profile = null;
        _preview = WithSource(ProjectImportActivationTests.Preview(new string('e', 64), source: true, Docs));
        _commit = ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, Guid.NewGuid());
        var cut = ChooseArchive();
        cut.Find("[data-testid=pi-source-status]").TextContent.Should().Contain("Source detected").And.Contain("a new Source Analysis snapshot will be created").And.NotContain("Target");
        cut.Find("[data-testid=pi-target]").TextContent.Should().Contain("Not configured").And.Contain("Required only for runtime reviews");
        cut.FindAll("[data-testid=pi-select-target]").Should().BeEmpty("a Target Environment is not a prerequisite of the import");

        cut.Find("[data-testid=pi-import]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-source]").GetAttribute("data-state").Should().Be("Created"));
        cut.FindAll("[data-testid=pi-retry-source]").Should().BeEmpty("no retry is needed because a target is absent");
        cut.FindAll("[data-testid=pi-notes] li").Select(n => n.TextContent).Should().NotContain(n => n.Contains("Target Environment"));
        cut.Find("[data-testid=pi-target]").TextContent.Should().Contain("Not configured");
        _repository.SddLifecycle.ProjectImports.Single().SourceState.Should().Be("Created");
        _api.Verify(a => a.CommitAsync(_preview.StagingId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GenuineSourceFailure_OffersARetryFromTheStagedArchive_WithoutReupload()
    {
        _preview = WithSource(ProjectImportActivationTests.Preview(new string('9', 64), source: true, Docs));
        _commit = ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Failed) with
        {
            Source = new ProjectImportSourceResult { State = ProjectImportSourceState.Failed, Code = "SOURCE_SNAPSHOT_SAVE_FAILED", Message = "The source snapshot could not be saved.", CanRetry = true },
            StagedUntil = DateTimeOffset.UtcNow.AddMinutes(30),
        };
        var cut = ChooseArchive();
        cut.Find("[data-testid=pi-import]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-retry-source]"));
        cut.Find("[data-testid=pi-result]").GetAttribute("data-outcome").Should().Be(nameof(ProjectImportOutcome.ImportedWithNotes));
        _repository.SddLifecycle.ProjectImports.Single().SourceState.Should().Be("Failed");

        _commit = ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, Guid.NewGuid());
        cut.Find("[data-testid=pi-retry-source]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-source]").GetAttribute("data-state").Should().Be("Created"));
        _previews.Should().Be(1, "the retry uses the staged archive");
        _repository.SddLifecycle.ProjectImports.Single().SourceState.Should().Be("Created");
    }

    [Fact]
    public void RejectedArchive_ShowsTheStructuredReason_AndActivatesNothing()
    {
        _previewFailure = new SourceUploadFailure("ARCHIVE_PATH_TRAVERSAL", "validation", "Archive entry escapes the project root: ../x.cs.", "../x.cs");

        var cut = ChooseArchive();

        var failure = cut.Find("[data-testid=pi-failure]");
        failure.GetAttribute("data-code").Should().Be("ARCHIVE_PATH_TRAVERSAL");
        failure.GetAttribute("role").Should().Be("alert");
        failure.TextContent.Should().Contain("Archive rejected").And.Contain("Nothing was imported");
        _repository.SddLifecycle.ProjectImports.Should().BeEmpty();
        _api.Verify(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ArchiveWithNothingSupported_IsNeutral_AndCannotBeImported()
    {
        _preview = ProjectImportActivationTests.Preview(new string('f', 64), source: false, ("README.md", "# Readme\n\nNotes."));

        var cut = ChooseArchive();

        cut.Find("[data-testid=pi-nothing]").TextContent.Should().Contain("Nothing to import");
        cut.Find("[data-testid=pi-import]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void CurrentImport_IsShownWithProvenance_AndCanBeClosed()
    {
        _preview = WithSource(ProjectImportActivationTests.Preview(new string('9', 64), source: true, Docs));
        new ProjectImportActivation(_repository).Activate(_preview, ProjectImportArtifactDiscovery.From(_preview),
            ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, Guid.NewGuid()));

        var cut = Render<ProjectImportPage>();

        cut.Find("[data-testid=pi-current-name]").TextContent.Should().Be("shop");
        cut.Find("[data-testid=pi-current-fingerprint]").TextContent.Should().Be(new string('9', 12));
        cut.Find("[data-testid=pi-current-source]").TextContent.Should().StartWith("Snapshot ");
        cut.Find("[data-testid=pi-close]").Click();
        _repository.SddLifecycle.CurrentProjectImportId.Should().BeNull();
        cut.FindAll("[data-testid=pi-current]").Should().BeEmpty();
    }
}
