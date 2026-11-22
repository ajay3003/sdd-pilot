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
        Services.AddScoped<BirkNext.Web.Services.Operations.BackgroundOperationService>(_ => new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddScoped<ProjectImportSession>();
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
        ImportFormSteps.Upload(cut).UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], file));
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
    public void AfterASuccessfulImport_Import_ZIP_opens_the_form_at_the_content_question()
    {
        // 2026-11-22: it used to open the file chooser at once, skipping "What does this ZIP contain?".
        var cut = ImportFirst();
        var button = cut.Find("[data-testid=pi-import-another]");
        button.TagName.Should().Be("BUTTON");
        button.TextContent.Trim().Should().Be("Import ZIP");
        button.Click();
        cut.Find("[data-testid=pi-choose] [data-testid=pi-roles]");
        cut.FindAll("[data-testid=pi-choose-file]").Should().BeEmpty("no file is chosen before the content question is answered");
        cut.Find("[data-testid=pi-choose-locked]");
        cut.WaitForAssertion(() => JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("pi-roles"));
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
        // With a current project the upload block is collapsed; Import another project reveals it and moves focus to its file chooser.
        page.FindAll("[data-testid=pi-choose]").Should().BeEmpty();
        var another = page.Find("[data-testid=pi-current] [data-testid=pi-current-import-another]");
        another.TextContent.Trim().Should().Be("Import ZIP");
        another.GetAttribute("aria-expanded").Should().Be("false");
        another.Click();
        page.Find("[data-testid=pi-current-import-another]").GetAttribute("aria-expanded").Should().Be("true");
        page.Find("[data-testid=pi-choose]").TextContent.Should().Contain("ZIP · maximum 50 MB · documents up to 1 MB each").And.Contain("What does this ZIP contain?");
        page.WaitForAssertion(() => JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("pi-roles"));
        _activation.Current!.ArchiveFileName.Should().Be("orders-a.zip", "revealing the upload changes nothing");
        Choose(page, "pi-choose-file", "billing-b.zip");
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

    // ── Import ZIP is always the primary action at the top (2026-11-13) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Import_ZIP_is_the_header_action_before_the_first_import_and_opens_nothing_new()
    {
        var cut = Render<ProjectImportPage>();
        var header = cut.Find("[data-testid=pi-header-import]");
        header.TextContent.Trim().Should().Be("Import ZIP");
        header.ClassList.Should().Contain("btn-primary");
        header.GetAttribute("aria-controls").Should().Be("pi-choose");
        header.GetAttribute("aria-expanded").Should().Be("false", "like Connected Systems, the form opens on request");
        cut.Find("[data-testid=pi-start]").TextContent.Should().Contain("No project is imported yet");
        cut.FindAll("[data-testid=pi-choose]").Should().BeEmpty();
        header.Click();
        cut.FindAll("[data-testid=pi-intent]").Should().BeEmpty("no application exists yet: the first import needs no intent choice");
        cut.FindAll("[data-testid=pi-header-import]").Should().BeEmpty("one Import ZIP on screen: the header button is hidden while its form is open");
        cut.FindAll("[data-testid=pi-start]").Should().BeEmpty();
        cut.WaitForAssertion(() => JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("pi-roles"));
        _api.Verify(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never, "the header action never starts an import");
    }

    [Fact]
    public void After_an_import_Import_ZIP_stays_in_the_header_and_opens_the_upload_above_the_current_import()
    {
        var cut = ImportFirst();
        cut.Find("[data-testid=pi-header-import]").TextContent.Trim().Should().Be("Import ZIP", "also on the result of an import");
        cut.Dispose();
        var page = Render<ProjectImportPage>();
        page.FindAll("[data-testid=pi-choose]").Should().BeEmpty("with a current import the form is collapsed until asked for");
        var header = page.Find("[data-testid=pi-header-import]");
        header.GetAttribute("aria-expanded").Should().Be("false");
        header.Click();
        page.FindAll("[data-testid=pi-header-import]").Should().BeEmpty("one Import ZIP on screen: the header button is hidden while its form is open");
        var markup = page.Markup;
        markup.IndexOf("data-testid=\"pi-choose\"", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("data-testid=\"pi-current\"", StringComparison.Ordinal),
            "the opened form sits right under the header, not after the current-import details");
        page.WaitForAssertion(() => JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("pi-roles"));
        _activation.Current!.ArchiveFileName.Should().Be("orders-a.zip", "opening the form changes nothing");
    }

    [Fact]
    public void Import_ZIP_opens_the_role_checkboxes_before_and_after_the_first_import()
    {
        var first = Render<ProjectImportPage>();
        first.Find("[data-testid=pi-header-import]").Click();
        first.Find("[data-testid=pi-roles]").TextContent.Should().Contain("What does this ZIP contain?");
        first.FindAll("[data-testid=pi-zip-role]").Count.Should().Be(ImportRoleChoice.Choices.Count);
        first.Dispose();

        ImportFirst().Dispose();
        var page = Render<ProjectImportPage>();
        page.FindAll("[data-testid=pi-roles]").Should().BeEmpty("collapsed until Import ZIP");
        page.Find("[data-testid=pi-header-import]").Click();
        page.FindAll("[data-testid=pi-zip-role]").Select(r => r.GetAttribute("data-role")).Should().Equal(ImportRoleChoice.Choices.Select(c => c.ToString()));
        page.FindAll("[data-testid=pi-zip-role]").Should().OnlyContain(r => !r.HasAttribute("checked"), "nothing is ticked until the tester answers");
        page.Find("[data-testid=pi-zip-detect]").HasAttribute("checked").Should().BeFalse();
        page.Find("[data-testid=pi-choose-locked]");
    }

    [Fact]
    public void With_an_application_the_import_intent_is_explicit_and_cancel_returns_to_Import_ZIP()
    {
        ImportFirst().Dispose();
        _activation.CurrentApplication.Should().NotBeNull("the first import created the application");
        var page = Render<ProjectImportPage>();
        page.Find("[data-testid=pi-header-import]").Click();
        page.Find("[data-testid=pi-intent]").TextContent.Should().Contain("Belongs to project");
        var current = _activation.CurrentApplication!;
        page.Find("[data-testid=pi-target-project] option[selected]").GetAttribute("value").Should().Be(current.Id, "the current project is the safe default: nothing is replaced");
        page.Find("[data-testid=pi-target-project] option[selected]").TextContent.Should().Be(current.Name);
        page.Find("[data-testid=pi-choose-target]").GetAttribute("data-target").Should().Be("CurrentApplication");

        page.Find("[data-testid=pi-target-project]").Change("new");
        page.Find("[data-testid=pi-choose-target]").GetAttribute("data-target").Should().Be("NewApplication");
        page.Find("#pi-choose-heading").TextContent.Should().Be("Import a ZIP as a new project");

        page.Find("[data-testid=pi-choose-cancel]").Click();
        page.FindAll("[data-testid=pi-choose]").Should().BeEmpty();
        page.WaitForAssertion(() => JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("pi-header-import"));
        _activation.Current!.ArchiveFileName.Should().Be("orders-a.zip", "cancel changes no application, import or history");
        _activation.Imports.Should().ContainSingle();
    }

    // ── Project name (2026-11-22): typed in the preview of a new project; Rename on the project card ──

    [Fact]
    public void A_new_projects_name_is_suggested_from_the_ZIP_and_can_be_changed_before_import()
    {
        var preview = Queue("orders-a.zip", ShaA, SnapshotA);
        var cut = Render<ProjectImportPage>();
        Choose(cut, "pi-choose-file", "orders-a.zip");
        var name = cut.Find("[data-testid=pi-new-project-name]");
        name.GetAttribute("value").Should().Be("orders-a", "the suggestion is the name read from the ZIP");
        cut.Find("[data-testid=pi-project-name]").TextContent.Should().Contain("Suggested").And.Contain("orders-a");

        name.Input("   ");
        cut.Find("[data-testid=pi-new-project-name-problem]").TextContent.Should().Be("Enter a project name.");
        cut.Find("[data-testid=pi-import]").HasAttribute("disabled").Should().BeTrue("a project is never created without a name");

        cut.Find("[data-testid=pi-new-project-name]").Input("Order handling");
        cut.FindAll("[data-testid=pi-new-project-name-problem]").Should().BeEmpty();
        cut.Find("[data-testid=pi-import]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));

        var application = _activation.CurrentApplication!;
        application.Name.Should().Be("Order handling");
        application.NameBasis.Should().Be(ApplicationContexts.ManualBasis, "the tester's name is kept: later imports never rename it");
        _activation.Current!.ProjectName.Should().Be(preview.ProjectName, "the package keeps its own name (it identifies the package's versions)");
    }

    [Fact]
    public void Adding_a_ZIP_to_an_existing_project_shows_that_project_and_asks_for_no_name()
    {
        ImportFirst().Dispose();
        Queue("billing-b.zip", ShaB, SnapshotB);
        var page = Render<ProjectImportPage>();
        Choose(page, "pi-choose-file", "billing-b.zip");
        page.FindAll("[data-testid=pi-new-project-name]").Should().BeEmpty();
        page.Find("[data-testid=pi-project-name]").TextContent.Should().Contain("orders-a").And.Contain("package billing-b");
    }

    [Fact]
    public void A_project_can_be_renamed_on_its_card_and_a_name_in_use_is_refused()
    {
        ImportFirst().Dispose();
        var application = _activation.CurrentApplication!;
        _repository.SddLifecycle.Applications.Add(new SddApplicationContext { Id = "app-other", Name = "Billing" });
        _repository.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "other", ApplicationId = "app-other", ArchiveFileName = "billing.zip" });
        var page = Render<ProjectImportPage>();

        page.Find("[data-testid=pi-rename-open]").Click();
        page.Find("[data-testid=pi-rename-input]").GetAttribute("value").Should().Be(application.Name);
        page.Find("[data-testid=pi-rename-input]").Input("billing");
        page.Find("[data-testid=pi-rename]").Submit();
        page.Find("[data-testid=pi-rename-problem]").TextContent.Should().Contain("already exists");
        application.Name.Should().Be("orders-a", "a refused name changes nothing");

        page.Find("[data-testid=pi-rename-input]").Input("Order handling");
        page.Find("[data-testid=pi-rename]").Submit();
        page.WaitForAssertion(() => page.Find("[data-testid=pi-application-name]").TextContent.Should().Be("Order handling"));
        application.NameBasis.Should().Be(ApplicationContexts.ManualBasis);
        _activation.Current!.ProjectName.Should().Be("orders-a", "renaming the project renames no package");

        page.Find("[data-testid=pi-rename-open]").Click();
        page.Find("[data-testid=pi-rename-cancel]").Click();
        page.FindAll("[data-testid=pi-rename]").Should().BeEmpty();
    }

    [Fact]
    public void Project_name_rules()
    {
        var lifecycle = new SddLifecycleState();
        lifecycle.Applications.Add(new SddApplicationContext { Id = "a", Name = "M2LB" });
        lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "i", ApplicationId = "a" });
        ApplicationContexts.NameProblem(lifecycle, "").Should().Be("Enter a project name.");
        ApplicationContexts.NameProblem(lifecycle, new string('x', ApplicationContexts.MaxNameLength + 1)).Should().Contain("at most");
        ApplicationContexts.NameProblem(lifecycle, " m2lb ").Should().Contain("already exists");
        ApplicationContexts.NameProblem(lifecycle, "M2LB", exceptApplicationId: "a").Should().BeNull("a project keeps its own name");
        ApplicationContexts.SuggestedName("M2LB.Common").Should().Be("M2LB");
        ApplicationContexts.Rename(lifecycle, "a", "  M2LB Skole ").Should().BeNull();
        lifecycle.Applications[0].Name.Should().Be("M2LB Skole");
        ApplicationContexts.Rename(lifecycle, "missing", "x").Should().Be("The project no longer exists.");
    }
}
