using System.Text.Json;
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
/// "Back to Import Project" after a completed import: the pre-import and result views share one route (the result is component state), so
/// Back resets only that transient import session and shows the upload view again. It is navigation, not rollback: the import record, its
/// documents and selections, its snapshot, the staged archive, the current project and IQR bindings stay exactly as they were. It differs
/// from "Import another project", which opens the file chooser directly. Generic fixtures.
/// </summary>
public sealed class ProjectImportBackTests : BunitContext
{
    private readonly Mock<IProjectImportApiService> _api = new();
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new(MockBehavior.Strict);
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly ProjectImportActivation _activation;
    private readonly Queue<(ProjectImportPreview? Preview, SourceUploadFailure? Failure)> _previews = new();
    private readonly Dictionary<Guid, (ProjectImportCommitResult? Result, SourceUploadFailure? Failure)> _commits = [];

    private const string ShaA = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    private const string ShaB = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    private static readonly Guid SnapshotA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SnapshotB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public ProjectImportBackTests()
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

    private ProjectImportPreview Queue(string archive, string sha, Guid? snapshot, ProjectImportSourceState state = ProjectImportSourceState.Created, bool canRetry = false)
    {
        var preview = ProjectImportActivationTests.Preview(sha, source: true, ("app/specs/001/spec.md", ProjectImportActivationTests.Spec)) with
        {
            Archive = new ProjectImportArchive(archive, sha, 4096, 12), ProjectName = Path.GetFileNameWithoutExtension(archive),
        };
        _previews.Enqueue((preview, null));
        var commit = ProjectImportActivationTests.Commit(preview, state, snapshot);
        _commits[preview.StagingId] = (commit with { StagedUntil = canRetry ? DateTimeOffset.UtcNow.AddMinutes(30) : null, Source = commit.Source with
        {
            CanRetry = canRetry, Message = state == ProjectImportSourceState.Failed ? "The source snapshot could not be saved." : null,
        } }, null);
        return preview;
    }

    private static void Choose(IRenderedComponent<ProjectImportPage> cut, string inputTestId, string file)
    {
        cut.FindComponents<InputFile>().Single(i => i.Instance.AdditionalAttributes?["data-testid"]?.ToString() == inputTestId)
            .UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], file));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pi-preview], [data-testid=pi-failure]").Should().NotBeEmpty());
    }

    private IRenderedComponent<ProjectImportPage> ImportFirst(ProjectImportSourceState state = ProjectImportSourceState.Created, Guid? snapshot = null, bool canRetry = false)
    {
        Queue("orders-a.zip", ShaA, state == ProjectImportSourceState.Created ? snapshot ?? SnapshotA : snapshot, state, canRetry);
        var cut = Render<ProjectImportPage>();
        Choose(cut, "pi-choose-file", "orders-a.zip");
        cut.Find("[data-testid=pi-import]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        return cut;
    }

    /// <summary>Everything the workspace persists about imports, documents and selections, as one comparable value.</summary>
    private string Persisted() => JsonSerializer.Serialize(_repository.SddLifecycle);

    private static void AssertPreImportView(IRenderedComponent<ProjectImportPage> cut)
    {
        cut.Find("[data-testid=pi-choose]");
        cut.FindAll("[data-testid=pi-choose-file]").Should().ContainSingle();
        cut.FindAll("[data-testid=pi-result], [data-testid=pi-preview], [data-testid=pi-progress], [data-testid=pi-failure], [data-testid=pi-commit-failure]")
            .Should().BeEmpty("no completed result, preview, progress or error of the finished import session remains");
        cut.FindAll("[data-testid=pi-import-id], [data-testid=pi-fingerprint], [data-testid=pi-notes], [data-testid=pi-candidate-review], [data-testid=pi-save-warning]")
            .Should().BeEmpty();
        cut.FindAll("[data-testid=pi-back], [data-testid=pi-back-bottom]").Should().BeEmpty("Back belongs to the result view only");
    }

    [Fact]
    public void TheResult_OffersBackToImportProject_InTheHeaderAndBesideImportAnother_AsSecondaryActions()
    {
        var cut = ImportFirst();
        var header = cut.Find(".page-header-actions [data-testid=pi-back]");
        header.TagName.Should().Be("BUTTON");
        header.GetAttribute("type").Should().Be("button");
        header.TextContent.Trim().Should().Be("Back to Import Project");
        header.ClassList.Should().Contain("btn-secondary").And.NotContain("btn-primary");

        var importGroup = cut.Find("[data-testid=pi-import-actions]");
        importGroup.QuerySelector("[data-testid=pi-back-bottom]")!.TextContent.Trim().Should().Be("Back to Import Project");
        importGroup.QuerySelector("[data-testid=pi-import-another]").Should().NotBeNull();
        cut.Find("#pi-import-actions-heading").TextContent.Should().Be("Import");
        cut.Find("#pi-next-heading").TextContent.Should().Be("Next steps");
        var next = cut.Find("[data-testid=pi-next]");
        next.QuerySelector("[data-testid=pi-open-documents]")!.ClassList.Should().Contain("btn-primary", "the next step stays the primary action");
        next.QuerySelector("[data-testid=pi-back], [data-testid=pi-back-bottom], [data-testid=pi-import-another]").Should().BeNull();
    }

    [Theory]
    [InlineData("pi-back")]
    [InlineData("pi-back-bottom")]
    public void Back_ShowsTheUploadView_AndKeepsTheImportedProjectCurrentAndPersisted(string backTestId)
    {
        var cut = ImportFirst();
        var before = Persisted();
        var record = _activation.Current!;

        cut.Find($"[data-testid={backTestId}]").Click();

        AssertPreImportView(cut);
        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("orders-a.zip", "the imported project stays the current project");
        cut.Find("#pi-choose-heading").TextContent.Should().Be("Import another project");
        _activation.Current!.ImportId.Should().Be(record.ImportId, "Back is navigation, not a rollback to the previous project");
        _activation.Current!.SourceSnapshotId.Should().Be(SnapshotA, "the Source Analysis snapshot stays linked");
        _repository.SddLifecycle.Revisions.Should().Contain(r => r.ProjectImportId == record.ImportId, "the imported documents stay");
        Persisted().Should().Be(before, "Back changes no import record, document, selection, scope or current project");
        _api.Verify(a => a.DiscardAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never, "the staged archive stays for source retry");
        _api.Verify(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _journeys.Verify(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _journeys.VerifyNoOtherCalls();
    }

    [Fact]
    public void Back_MovesFocusToTheTopOfTheUploadView_NotToARemovedButton()
    {
        var cut = ImportFirst();
        cut.Find("[data-testid=pi-back]").Click();
        cut.WaitForAssertion(() => JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("pi-current-heading"));
        cut.Find("#pi-current-heading").GetAttribute("tabindex").Should().Be("-1");
    }

    [Fact]
    public void Back_RecreatesTheFileInput_SoTheSameZipCanBeChosenAgain()
    {
        var cut = ImportFirst();
        var generation = cut.Find("[data-testid=pi-import-another-file]").GetAttribute("data-generation");
        cut.Find("[data-testid=pi-back]").Click();
        cut.Find("[data-testid=pi-choose-file]").GetAttribute("data-generation").Should().NotBe(generation, "no stale file selection survives");
    }

    [Fact]
    public void BackAndImportAnother_AreDifferent_BackChoosesNothing_ImportAnotherIsAFileChooser()
    {
        var cut = ImportFirst();
        cut.Find("[data-testid=pi-import-another-file]").GetAttribute("type").Should().Be("file");
        cut.Find("[data-testid=pi-back]").GetAttribute("type").Should().Be("button");

        cut.Find("[data-testid=pi-back]").Click();
        _api.Verify(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once, "Back uploads nothing");
        _activation.Imports.Should().ContainSingle();
    }

    [Fact]
    public void AfterBack_ANewImportShowsNoMetadataOfThePreviousOne_AndThePreviousStaysPersisted()
    {
        var cut = ImportFirst();
        var a = _activation.Current!;
        cut.Find("[data-testid=pi-back]").Click();

        Queue("billing-b.zip", ShaB, SnapshotB);
        Choose(cut, "pi-choose-file", "billing-b.zip");
        var preview = cut.Find("[data-testid=pi-preview]").TextContent;
        preview.Should().Contain("billing-b.zip").And.NotContain("orders-a.zip").And.NotContain(ShaA[..12]);
        cut.FindAll("[data-testid=pi-duplicate]").Should().BeEmpty();
        cut.Find("[data-testid=pi-import]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        cut.Find("[data-testid=pi-import-id]").TextContent.Should().Be("import-" + ShaB[..16]);

        _activation.Current!.ArchiveFileName.Should().Be("billing-b.zip", "B becomes current only after its own successful import");
        _activation.Imports.Single(i => i.ImportId == a.ImportId).SourceSnapshotId.Should().Be(SnapshotA);
        _repository.SddLifecycle.Revisions.Should().Contain(r => r.ProjectImportId == a.ImportId);
    }

    [Fact]
    public void ImportAnother_WhileTheNextArchiveValidates_ShowsNoCountsOfThePreviousArchive()
    {
        var cut = ImportFirst();
        var pending = new TaskCompletionSource<(ProjectImportPreview?, SourceUploadFailure?)>();
        _api.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).Returns(pending.Task);

        // UploadFiles returns only when the change handler completes, so the validating view is captured from another thread before the
        // preview is released.
        string? validating = null;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 400 && validating is null; i++)
            {
                await Task.Delay(25);
                var markup = cut.Markup;
                if (markup.Contains("data-testid=\"pi-progress\"")) validating = markup;
            }
            pending.SetResult((null, new SourceUploadFailure("ARCHIVE_INVALID", "validation", "The file is not a ZIP archive.")));
        });
        cut.FindComponents<InputFile>().Single(i => i.Instance.AdditionalAttributes?["data-testid"]?.ToString() == "pi-import-another-file")
            .UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "billing-b.zip"));

        validating.Should().NotBeNull("the validating view is rendered while the preview runs");
        validating.Should().Contain("billing-b.zip");
        validating.Should().NotContain("data-testid=\"pi-progress-detail\"", "the previous archive's document and source counts must not appear in the next import");
        validating.Should().NotContain("data-testid=\"pi-back\"", "no reset action while an archive is being checked");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-failure]"));
        _activation.Current!.ArchiveFileName.Should().Be("orders-a.zip");
    }

    [Fact]
    public void Back_FromAnImportWithNotes_WorksTheSame_AndKeepsTheRecord()
    {
        var cut = ImportFirst(ProjectImportSourceState.Failed, canRetry: true);
        cut.Find("[data-testid=pi-result]").GetAttribute("data-outcome").Should().NotBe(nameof(ProjectImportOutcome.Imported));
        var before = Persisted();

        cut.Find("[data-testid=pi-back]").Click();

        AssertPreImportView(cut);
        Persisted().Should().Be(before);
        cut.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("orders-a.zip");
        cut.FindAll("[data-testid=pi-current-retry-source]").Should().ContainSingle("the staged archive was kept, so the current card can still retry the source");
    }

    [Fact]
    public void Back_WhileASourceRetryRuns_DoesNotReviveTheResult_AndTheRecordStillGetsTheSnapshot()
    {
        var cut = ImportFirst(ProjectImportSourceState.Failed, canRetry: true);
        var pending = new TaskCompletionSource<(ProjectImportCommitResult?, SourceUploadFailure?)>();
        _api.Setup(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        var preview = _commits.Single();

        cut.Find("[data-testid=pi-retry-source]").Click();
        cut.Find("[data-testid=pi-back]").Click();
        AssertPreImportView(cut);

        var retried = preview.Value.Result! with { Source = new ProjectImportSourceResult { State = ProjectImportSourceState.Created, SnapshotId = SnapshotA, SnapshotStatus = SourceAnalysisStatus.Ready } };
        cut.InvokeAsync(() => pending.SetResult((retried, null)));

        cut.WaitForAssertion(() => _activation.Current!.SourceSnapshotId.Should().Be(SnapshotA));
        AssertPreImportView(cut);
    }

    [Fact]
    public void Back_WhileASourceRetryFails_ThrowsNothing_AndStaysOnTheUploadView()
    {
        var cut = ImportFirst(ProjectImportSourceState.Failed, canRetry: true);
        var pending = new TaskCompletionSource<(ProjectImportCommitResult?, SourceUploadFailure?)>();
        _api.Setup(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(pending.Task);

        cut.Find("[data-testid=pi-retry-source]").Click();
        cut.Find("[data-testid=pi-back]").Click();
        cut.InvokeAsync(() => pending.SetException(new HttpRequestException("connection refused")));

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-choose]"));
        AssertPreImportView(cut);
        _activation.Current!.ArchiveFileName.Should().Be("orders-a.zip");
    }

    [Fact]
    public void AFailedCommit_ChooseAnotherFile_ReturnsToACleanUploadView_WithoutAPersistedProject()
    {
        var preview = Queue("orders-a.zip", ShaA, null);
        _commits[preview.StagingId] = (null, new SourceUploadFailure("IMPORT_FAILED", "commit", "The import stopped before a result was returned."));
        var cut = Render<ProjectImportPage>();
        Choose(cut, "pi-choose-file", "orders-a.zip");
        cut.Find("[data-testid=pi-import]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-commit-failure]"));
        cut.FindAll("[data-testid=pi-back]").Should().BeEmpty("there is no result to leave; Retry import and Choose another file are the actions");

        cut.Find("[data-testid=pi-cancel]").Click();

        AssertPreImportView(cut);
        _activation.Imports.Should().BeEmpty("a failed import is never a persisted project");
        _activation.Current.Should().BeNull();
        cut.FindAll("[data-testid=pi-current]").Should().BeEmpty();
    }

    [Fact]
    public void OpeningImportProjectAgainAfterAnImport_ShowsTheUploadView_NotTheOldResult()
    {
        var cut = ImportFirst();
        cut.Dispose();
        var page = Render<ProjectImportPage>();
        AssertPreImportView(page);
        page.Find("[data-testid=pi-current-archive]").TextContent.Should().Be("orders-a.zip");
    }
}
