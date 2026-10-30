using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Operations;
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
/// Regression for "the import stops when I leave Import Project": the import is owned by ProjectImportSession / the operation manager, so
/// disposing the page (navigating to Dashboard or Delivery Overview), switching Workspace View or selecting another project never stops it,
/// returning restores its stage, it keeps its own project context, completes only after finalization, can be cancelled explicitly, fails
/// and retries safely, and a repeated click starts nothing twice.
/// </summary>
public sealed class ProjectImportBackgroundTests : BunitContext
{
    private readonly Mock<IProjectImportApiService> _api = new();
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();
    private readonly ProjectImportPreview _preview;
    private TaskCompletionSource<(ProjectImportCommitResult?, SourceUploadFailure?)> _commit = new();
    private TaskCompletionSource<(ProjectImportPreview?, SourceUploadFailure?)>? _previewGate;
    private IProgress<ProjectImportCommitEvent>? _progress;
    private CancellationToken _commitToken;
    private int _commits;

    private static readonly (string, string)[] Docs =
    [
        ("m2lb/.specify/memory/constitution.md", ProjectImportActivationTests.Constitution),
        ("m2lb/specs/001-cart/spec.md", ProjectImportActivationTests.Spec),
        ("m2lb/specs/001-cart/tasks.md", ProjectImportActivationTests.Tasks),
    ];

    public ProjectImportBackgroundTests()
    {
        _preview = ProjectImportActivationTests.Preview(new string('b', 64), source: true, Docs);
        _api.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(() => _previewGate?.Task ?? Task.FromResult<(ProjectImportPreview?, SourceUploadFailure?)>((_preview, null)));
        _api.Setup(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, IProgress<ProjectImportCommitEvent>?, CancellationToken>((_, progress, ct) =>
            {
                _commits++;
                _progress = progress;
                _commitToken = ct;
                ct.Register(() => _commit.TrySetCanceled(ct));
                return _commit.Task;
            });
        _autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_repository);
        Services.AddSingleton(new ProjectImportActivation(_repository));
        Services.AddSingleton(context.Object);
        Services.AddSingleton(_autoSave.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped<BackgroundOperationService>(_ => new BackgroundOperationService());
        Services.AddScoped<ProjectImportSession>();
        Services.AddScoped<NavigationSectionState>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private BackgroundOperationService Operations => Services.GetRequiredService<BackgroundOperationService>();
    private ProjectImportSession Session => Services.GetRequiredService<ProjectImportSession>();

    private IRenderedComponent<ProjectImportPage> StartImport()
    {
        var page = Render<ProjectImportPage>();
        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "m2lb.zip"));
        page.WaitForAssertion(() => page.Find("[data-testid=pi-preview]"));
        page.Find("[data-testid=pi-import]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=pi-progress]"));
        return page;
    }

    /// <summary>Navigating away: the router disposes the page component (all rendered components here).</summary>
    private void Leave() => DisposeComponentsAsync().GetAwaiter().GetResult();

    private OperationSnapshot ImportOperation => Operations.Operations.Single(o => o.Kind == OperationKind.ProjectImport);

    private void Report(string stage, int n, int count) =>
        _progress!.Report(new ProjectImportCommitEvent { Kind = ProjectImportCommitEventKind.Progress, Stage = stage, StageNumber = n, StageCount = count, Detail = "working" });

    [Fact]
    public void LeavingThePage_SwitchingViewAndProject_DoesNotStopTheImport_ItKeepsItsContext_AndReturningRestoresIt()
    {
        var page = StartImport();
        ImportOperation.Status.Should().Be(OperationStatus.Running);
        ImportOperation.Context.ProjectKey.Should().Be(_preview.ImportId);

        // Navigate to Dashboard / Delivery Overview: the page is disposed.
        Leave();
        Report("Analyzing source architecture", 3, 17);
        ImportOperation.Status.Should().Be(OperationStatus.Running, "leaving the page only ends the subscription");
        _commitToken.IsCancellationRequested.Should().BeFalse("the backend request is not aborted by navigation");

        // Switch Workspace View and select another project while it runs.
        Services.GetRequiredService<NavigationSectionState>().SetView(WorkspaceView.ScrumMaster);
        _repository.ProjectName = "Other sample project";
        ImportOperation.Status.Should().Be(OperationStatus.Running);
        ImportOperation.Context.ProjectLabel.Should().Be(_preview.ProjectName, "the context captured at start never follows the selection");

        // Return to Import Project: the running stage is shown, no new upload is offered.
        var back = Render<ProjectImportPage>();
        back.Find("[data-testid=pi-progress]").Should().NotBeNull();
        back.Find("[data-testid=pi-status]").TextContent.Should().Contain("Analyzing source architecture");
        back.Find("[data-testid=pi-stage-count]").TextContent.Should().Contain("stage 3 of 17");
        back.FindAll("[data-testid=pi-choose]").Should().BeEmpty();

        // Completes only after activation and save; it is recorded under its own project, the user's selection is kept.
        _commit.SetResult((ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, Guid.NewGuid()), null));
        back.WaitForAssertion(() => back.Find("[data-testid=pi-result]"));
        SpinWait.SpinUntil(() => ImportOperation.Status == OperationStatus.Completed, TimeSpan.FromSeconds(10)).Should().BeTrue();
        _autoSave.Verify(a => a.SaveNowAsync(), Times.Once);
        _repository.SddLifecycle.ProjectImports.Should().ContainSingle(i => i.ImportId == _preview.ImportId);
        _repository.ProjectName.Should().Be("Other sample project", "a late import never replaces the project selected meanwhile");
        _repository.SddLifecycle.CurrentProjectImportId.Should().BeNull();
        back.Find("[data-testid=pi-kept-selection]").TextContent.Should().Contain("selection was kept");
        Services.GetRequiredService<NavigationSectionState>().View.Should().Be(WorkspaceView.ScrumMaster, "the import never changes the Workspace View");
        _commits.Should().Be(1);
    }

    [Fact]
    public void WithoutAProjectSwitch_TheImportBecomesCurrent_AfterFinalization_EvenWhenThePageWasLeft()
    {
        var page = StartImport();
        Leave();
        _repository.SddLifecycle.ProjectImports.Should().BeEmpty("nothing is activated before the backend result");
        _commit.SetResult((ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, Guid.NewGuid()), null));

        SpinWait.SpinUntil(() => ImportOperation.Status == OperationStatus.Completed, TimeSpan.FromSeconds(5)).Should().BeTrue("the import completes without the page");
        _repository.SddLifecycle.CurrentProjectImportId.Should().Be(_preview.ImportId);
        (Session.Phase, Session.ResultSeen).Should().Be((ProjectImportPhase.Result, false));
        var back = Render<ProjectImportPage>();
        back.WaitForAssertion(() => back.FindAll("[data-testid=pi-result]").Should().NotBeEmpty("a result that finished while away is shown once on return; " + back.Markup[..Math.Min(400, back.Markup.Length)]));
        Leave();
        Render<ProjectImportPage>().FindAll("[data-testid=pi-result]").Should().BeEmpty("once seen, Import Project opens on the upload view again");
    }

    [Fact]
    public void RepeatedClicks_AndRemounts_StartNoSecondImport()
    {
        var page = StartImport();
        Session.StartImportAsync();
        Session.StartImportAsync();
        Leave();
        Render<ProjectImportPage>();
        _commits.Should().Be(1);
        Operations.Operations.Count(o => o.Kind == OperationKind.ProjectImport).Should().Be(1);
    }

    [Fact]
    public void ExplicitCancel_StopsOnlyThisImport_PublishesNothing_AndTheStagedArchiveCanBeImportedAgain()
    {
        var other = Operations.Start(new(OperationKind.DeliveryReportGeneration, "Report", new("Other project", "other"), "delivery-reporting"),
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return OperationOutcome.Success(); });
        var page = StartImport();

        page.Find("[data-testid=pi-cancel-running]").Click();

        SpinWait.SpinUntil(() => ImportOperation.Status == OperationStatus.Cancelled, TimeSpan.FromSeconds(10)).Should().BeTrue();
        _commitToken.IsCancellationRequested.Should().BeTrue("explicit cancellation aborts the backend request");
        _repository.SddLifecycle.ProjectImports.Should().BeEmpty("a cancelled import activates nothing");
        Operations.Find(other.Operation.Id)!.IsActive.Should().BeTrue("other operations are not affected");
        page.WaitForAssertion(() => page.Find("[data-testid=pi-preview]"));
        page.Find("[data-testid=pi-commit-failure]").TextContent.Should().Contain("The import was cancelled").And.Contain("Nothing was activated");
        Operations.Cancel(other.Operation.Id);
    }

    [Fact]
    public void Failure_IsNotComplete_PreservesTheDiagnosis_AndRetryLinksToIt()
    {
        var page = StartImport();
        _commit.SetResult((null, new SourceUploadFailure("PROJECT_IMPORT_FAILED", "commit", "The import could not be completed.", CorrelationId: "trace-1")));
        SpinWait.SpinUntil(() => ImportOperation.Status == OperationStatus.Failed, TimeSpan.FromSeconds(10)).Should().BeTrue();
        _repository.SddLifecycle.ProjectImports.Should().BeEmpty("a failed import is never presented as complete");
        var failed = ImportOperation;

        _commit = new();
        page.Find("[data-testid=pi-import]").Click();
        _commit.SetResult((ProjectImportActivationTests.Commit(_preview, ProjectImportSourceState.Created, Guid.NewGuid()), null));

        page.WaitForAssertion(() => page.Find("[data-testid=pi-result]"));
        SpinWait.SpinUntil(() => Operations.Operations.Any(o => o.Kind == OperationKind.ProjectImport && o.Status == OperationStatus.Completed), TimeSpan.FromSeconds(10)).Should().BeTrue();
        var retried = Operations.Operations.Single(o => o.Kind == OperationKind.ProjectImport && o.Status == OperationStatus.Completed);
        retried.RetryOf.Should().Be(failed.Id);
        Operations.Find(failed.Id)!.Error.Should().Be("The import could not be completed.");
        _repository.SddLifecycle.ProjectImports.Should().ContainSingle("the retry reuses the staged archive: no duplicate import record");
    }

    [Fact]
    public void Upload_ContinuesWhenThePageIsLeft_AndThePreviewIsThereOnReturn()
    {
        _previewGate = new();
        var page = Render<ProjectImportPage>();
        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3, 4], "m2lb.zip"));
        page.WaitForAssertion(() => Session.Phase.Should().Be(ProjectImportPhase.Validating));
        var upload = Operations.Operations.Single(o => o.Kind == OperationKind.ProjectImportUpload);
        upload.Status.Should().Be(OperationStatus.Running);

        Leave();
        _previewGate.SetResult((_preview, null));

        SpinWait.SpinUntil(() => Operations.Find(upload.Id)!.Status == OperationStatus.Completed, TimeSpan.FromSeconds(5)).Should().BeTrue("the upload completes without the page");
        Session.Phase.Should().Be(ProjectImportPhase.Preview);
        var back = Render<ProjectImportPage>();
        back.Find("[data-testid=pi-preview]").TextContent.Should().Contain(_preview.Archive.FileName);
        _repository.SddLifecycle.ProjectImports.Should().BeEmpty("a preview imports nothing until the user confirms");
    }
}
