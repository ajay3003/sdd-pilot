using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
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
/// Sequential multi-import: many imports are kept, exactly one is current. Every "Import source" action opens import mode (the upload form is
/// not rendered while a project is current otherwise — the old #pi-choose-heading fragment resolved against the app base and left the page).
/// Cancel leaves import mode without touching the current project or history; Set as current / Clear current never upload, re-import or
/// create snapshots. Generic fixtures.
/// </summary>
public sealed class ProjectImportMultiImportTests : BunitContext
{
    private readonly Mock<IProjectImportApiService> _api = new();
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new();
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly ProjectImportActivation _activation;
    private readonly Queue<(ProjectImportPreview? Preview, SourceUploadFailure? Failure)> _previews = new();
    private readonly Dictionary<Guid, (ProjectImportCommitResult? Result, SourceUploadFailure? Failure)> _commits = [];

    public ProjectImportMultiImportTests()
    {
        _activation = new ProjectImportActivation(_repository);
        _api.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _previews.Dequeue());
        _api.Setup(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid staging, IProgress<ProjectImportCommitEvent>? _, CancellationToken _) => _commits[staging]);
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

    private static string Sha(char c) => new(c, 64);

    private ProjectImportPreview Queue(string archive, string sha, Guid snapshot, SourceUploadFailure? commitFailure = null)
    {
        var preview = ProjectImportActivationTests.Preview(sha, source: true, ("app/specs/001/spec.md", ProjectImportActivationTests.Spec)) with
        {
            Archive = new ProjectImportArchive(archive, sha, 4096, 12), ProjectName = Path.GetFileNameWithoutExtension(archive),
        };
        _previews.Enqueue((preview, null));
        _commits[preview.StagingId] = commitFailure is null ? (ProjectImportActivationTests.Commit(preview, ProjectImportSourceState.Created, snapshot), null) : (null, commitFailure);
        return preview;
    }

    private static void Choose(IRenderedComponent<ProjectImportPage> cut, string inputTestId, string file)
    {
        cut.FindComponents<InputFile>().Single(i => i.Instance.AdditionalAttributes?["data-testid"]?.ToString() == inputTestId)
            .UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], file));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pi-preview], [data-testid=pi-failure]").Should().NotBeEmpty());
    }

    private static void Import(IRenderedComponent<ProjectImportPage> cut)
    {
        cut.Find("[data-testid=pi-import]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pi-result], [data-testid=pi-commit-failure]").Should().NotBeEmpty());
    }

    /// <summary>Imports archive after archive the way a tester does: first from the open form, then via the current card's Import another project.</summary>
    private IRenderedComponent<ProjectImportPage> ImportSequence(params (string Archive, char Sha, Guid Snapshot)[] archives)
    {
        var cut = Render<ProjectImportPage>();
        foreach (var (archive, sha, snapshot) in archives)
        {
            Queue(archive, Sha(sha), snapshot);
            if (cut.FindAll("[data-testid=pi-result]").Count > 0) cut.Find("[data-testid=pi-back]").Click();
            if (cut.FindAll("[data-testid=pi-current-import-another]").Count > 0) cut.Find("[data-testid=pi-current-import-another]").Click();
            Choose(cut, "pi-choose-file", archive);
            cut.FindAll("[data-testid=pi-duplicate]").Should().BeEmpty($"{archive} is a different archive, not a duplicate of an earlier import");
            Import(cut);
            cut.Find("[data-testid=pi-result]");
        }
        cut.Find("[data-testid=pi-back]").Click();
        return cut;
    }

    private static readonly Guid SnapA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SnapB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid SnapC = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    [Fact]
    public void Two_imports_both_persist_the_newest_is_current_and_the_first_stays_in_history_with_its_snapshot()
    {
        var cut = ImportSequence(("orders-a.zip", 'a', SnapA), ("orders-b.zip", 'b', SnapB));

        _activation.Imports.Select(i => (i.ArchiveFileName, i.SourceSnapshotId)).Should().BeEquivalentTo([("orders-a.zip", (Guid?)SnapA), ("orders-b.zip", (Guid?)SnapB)]);
        _activation.Current!.ArchiveFileName.Should().Be("orders-b.zip", "a successful import becomes the current project");
        cut.Find("[data-testid=pi-current-name]").TextContent.Should().Be("orders-b");
        cut.FindAll("[data-testid=pi-previous-row]").Select(r => r.GetAttribute("data-snapshot")).Should().Equal(SnapA.ToString());
    }

    [Fact]
    public void Three_imports_stay_available_and_switching_current_never_uploads_reimports_or_changes_snapshots()
    {
        var cut = ImportSequence(("orders-a.zip", 'a', SnapA), ("orders-b.zip", 'b', SnapB), ("orders-c.zip", 'c', SnapC));
        var imports = JsonSerializer.Serialize(_activation.Imports);

        foreach (var (archive, name) in new[] { ("orders-a.zip", "orders-a"), ("orders-c.zip", "orders-c"), ("orders-b.zip", "orders-b") })
        {
            var id = _activation.Imports.Single(i => i.ArchiveFileName == archive).ImportId;
            cut.Find($"[data-testid=pi-previous-row][data-import='{id}'] [data-testid=pi-previous-use]").Click();
            cut.Find("[data-testid=pi-current-name]").TextContent.Should().Be(name);
            cut.FindAll("[data-testid=pi-previous-row]").Should().HaveCount(2, "the other two stay in history");
        }

        JsonSerializer.Serialize(_activation.Imports).Should().Be(imports, "no record or snapshot changed");
        _api.Verify(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        _api.Verify(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        _api.Verify(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Each_import_gets_a_fresh_file_input_and_choosing_the_same_archive_again_runs_the_duplicate_flow()
    {
        var cut = ImportSequence(("orders-a.zip", 'a', SnapA));
        cut.Find("[data-testid=pi-current-import-another]").Click();
        var generation = cut.Find("[data-testid=pi-choose-file]").GetAttribute("data-generation");

        Queue("renamed-a.zip", Sha('a'), SnapA);
        Choose(cut, "pi-choose-file", "renamed-a.zip");
        cut.Find("[data-testid=pi-duplicate]").TextContent.Should().Contain("Matching imported source already exists", "same content, other file name → existing fingerprint policy");
        cut.Find("[data-testid=pi-import]").HasAttribute("disabled").Should().BeTrue("a duplicate needs an explicit choice");

        cut.Find("[data-testid=pi-cancel]").Click();
        cut.Find("[data-testid=pi-choose-file]").GetAttribute("data-generation").Should().NotBe(generation, "the previous selection never sticks in the input");
        _activation.Imports.Should().ContainSingle();
    }

    [Fact]
    public void A_failed_second_import_keeps_the_first_current_and_history_intact_and_can_be_retried()
    {
        var cut = ImportSequence(("orders-a.zip", 'a', SnapA));
        var before = JsonSerializer.Serialize(_repository.SddLifecycle);
        var failed = Queue("orders-b.zip", Sha('b'), SnapB, new SourceUploadFailure("IMPORT_FAILED", "commit", "The import stopped before a result was returned."));

        cut.Find("[data-testid=pi-current-import-another]").Click();
        Choose(cut, "pi-choose-file", "orders-b.zip");
        Import(cut);

        cut.Find("[data-testid=pi-commit-failure]");
        JsonSerializer.Serialize(_repository.SddLifecycle).Should().Be(before, "nothing of the failed import is activated or recorded");
        _activation.Current!.ArchiveFileName.Should().Be("orders-a.zip");
        cut.Find("[data-testid=pi-import]").TextContent.Should().Be("Retry import");

        _commits[failed.StagingId] = (ProjectImportActivationTests.Commit(failed, ProjectImportSourceState.Created, SnapB), null);
        Import(cut);
        _activation.Imports.Select(i => i.ArchiveFileName).Should().BeEquivalentTo(["orders-a.zip", "orders-b.zip"]);
    }

    [Fact]
    public void Import_source_opens_import_mode_with_a_project_current_and_cancel_changes_nothing()
    {
        var cut = ImportSequence(("orders-a.zip", 'a', SnapA));
        var before = JsonSerializer.Serialize(_repository.SddLifecycle);
        cut.FindAll("[data-testid=pi-choose]").Should().BeEmpty();

        Services.GetRequiredService<NavigationManager>().NavigateTo(ProjectImportPresentation.ImportSourceHref);
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-choose]"));
        cut.Find("#pi-choose-heading").TextContent.Should().Be("Import another project");
        cut.Find("[data-testid=pi-choose-keeps]").TextContent.Should().Contain("orders-a stays the current project until the new import succeeds").And.Contain("Imported projects remain available in history.");
        JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("pi-choose-file");

        cut.Find("[data-testid=pi-choose-cancel]").Click();
        cut.FindAll("[data-testid=pi-choose]").Should().BeEmpty();
        cut.Find("[data-testid=pi-current-import-another]").GetAttribute("aria-expanded").Should().Be("false");
        JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("pi-current-import-another");
        cut.Find("#pi-current-import-another").GetAttribute("data-testid").Should().Be("pi-current-import-another", "the focus helper finds elements by id");
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/project-import", "a reload does not reopen import mode");
        JsonSerializer.Serialize(_repository.SddLifecycle).Should().Be(before, "Cancel is not Clear current project");
    }

    [Fact]
    public void Opening_the_page_with_the_import_query_shows_the_form_and_cancel_is_only_offered_while_a_project_is_current()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(ProjectImportPresentation.ImportSourceHref);
        var empty = Render<ProjectImportPage>();
        empty.Find("[data-testid=pi-choose]");
        empty.FindAll("[data-testid=pi-choose-cancel]").Should().BeEmpty("without a current project the form is the page");
    }

    [Fact]
    public void Clearing_the_current_project_keeps_every_import_and_snapshot_and_a_new_import_still_works()
    {
        var cut = ImportSequence(("orders-a.zip", 'a', SnapA), ("orders-b.zip", 'b', SnapB));
        cut.Find("[data-testid=pi-close]").Click();

        _activation.Current.Should().BeNull();
        _activation.Imports.Select(i => i.SourceSnapshotId).Should().BeEquivalentTo([(Guid?)SnapA, SnapB]);
        cut.FindAll("[data-testid=pi-previous-row]").Should().HaveCount(2);

        Queue("orders-c.zip", Sha('c'), SnapC);
        Choose(cut, "pi-choose-file", "orders-c.zip");
        Import(cut);
        _activation.Imports.Should().HaveCount(3);
        _activation.Current!.ArchiveFileName.Should().Be("orders-c.zip");
    }

    [Fact]
    public void Every_import_source_action_targets_import_mode_not_a_fragment()
    {
        ProjectImportPresentation.ImportSourceHref.Should().Be("project-import?import=source").And.NotContain("#");
        var missing = new TestPackSourceStatus(new TestPackSourceRole("app", "M2LB application", "") { Requirement = TestPackSourceRequirement.Required }, TestPackSourceState.Missing);
        ImportProjectOverview.Line(missing).Href.Should().Be(ProjectImportPresentation.ImportSourceHref);
    }
}
