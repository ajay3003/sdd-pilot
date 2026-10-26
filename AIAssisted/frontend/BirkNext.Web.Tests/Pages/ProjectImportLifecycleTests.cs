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
/// The Import Project lifecycle: choosing another file replaces every archive-specific value, the same ZIP can be chosen again, and the
/// current imported project states exactly what happened to its source — with an exact snapshot link, or a retry while the archive is staged.
/// </summary>
public sealed class ProjectImportLifecycleTests : BunitContext
{
    private readonly Mock<IProjectImportApiService> _api = new();
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();
    private readonly Queue<(ProjectImportPreview?, SourceUploadFailure?)> _previews = new();
    private ProjectImportCommitResult? _commit;
    private int _previewCalls;

    private static readonly (string, string)[] DocsA =
    [
        ("shop/.specify/memory/constitution.md", ProjectImportActivationTests.Constitution),
        ("shop/specs/001-cart/spec.md", ProjectImportActivationTests.Spec),
    ];

    public ProjectImportLifecycleTests()
    {
        _api.Setup(a => a.PreviewAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { _previewCalls++; return _previews.Dequeue(); });
        _api.Setup(a => a.CommitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => (_commit, (SourceUploadFailure?)null));
        _api.Setup(a => a.CommitWithProgressAsync(It.IsAny<Guid>(), It.IsAny<IProgress<ProjectImportCommitEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (_commit, (SourceUploadFailure?)null));
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
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ProjectImportPreview Archive(char fingerprint, string name, bool source, params (string, string)[] docs) =>
        ProjectImportActivationTests.Preview(new string(fingerprint, 64), source, docs) with
        {
            ProjectName = name, Archive = new ProjectImportArchive(name + ".zip", new string(fingerprint, 64), 4096, 12),
            Source = new ProjectImportSourceDetection { Detected = source, SourceFiles = source ? 7 : 0 },
        };

    private static void Choose(IRenderedComponent<ProjectImportPage> cut, string file = "shop.zip")
    {
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], file));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pi-preview], [data-testid=pi-failure]").Should().NotBeEmpty());
    }

    private static string Generation(IRenderedComponent<ProjectImportPage> cut) => cut.Find("[data-testid=pi-choose-file]").GetAttribute("data-generation")!;

    [Fact]
    public void ChooseAnotherFile_ClearsEveryArchiveSpecificValue_AndRecreatesTheFileInput()
    {
        _previews.Enqueue((Archive('a', "shop", source: true, DocsA), null));
        var cut = Render<ProjectImportPage>();
        var before = Generation(cut);
        Choose(cut);
        cut.Find("[data-testid=pi-fingerprint]").TextContent.Should().Be(new string('a', 12));

        cut.Find("[data-testid=pi-cancel]").Click();

        cut.FindAll("[data-testid=pi-preview], [data-testid=pi-fingerprint], [data-testid=pi-project-name], [data-testid=pi-source], [data-testid=pi-role], [data-testid=pi-failure], [data-testid=pi-commit-failure]")
            .Should().BeEmpty("no value from the previous archive survives");
        cut.Find("[data-testid=pi-choose]").Should().NotBeNull();
        Generation(cut).Should().NotBe(before, "a new file input element is rendered, so the browser forgets the previous selection");
        _api.Verify(a => a.DiscardAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TheSameZip_CanBeChosenAgain_AndIsAnalysedFromScratch()
    {
        _previews.Enqueue((Archive('a', "shop", source: true, DocsA), null));
        _previews.Enqueue((Archive('a', "shop", source: true, DocsA), null));
        var cut = Render<ProjectImportPage>();
        Choose(cut, "shop.zip");
        cut.Find("[data-testid=pi-cancel]").Click();

        Choose(cut, "shop.zip");

        _previewCalls.Should().Be(2, "the same file is uploaded and previewed again");
        cut.Find("[data-testid=pi-fingerprint]").TextContent.Should().Be(new string('a', 12));
    }

    [Fact]
    public void ASecondArchive_ReplacesTheFirstCompletely()
    {
        _previews.Enqueue((Archive('a', "shop", source: true, DocsA), null));
        _previews.Enqueue((Archive('b', "billing", source: false, ("billing/specs/001-pay/plan.md", ProjectImportActivationTests.Plan)), null));
        var cut = Render<ProjectImportPage>();
        Choose(cut, "shop.zip");
        cut.Find("[data-testid=pi-cancel]").Click();

        Choose(cut, "billing.zip");

        cut.Find("[data-testid=pi-fingerprint]").TextContent.Should().Be(new string('b', 12));
        cut.Find("[data-testid=pi-project-name]").TextContent.Should().Contain("billing").And.NotContain("shop");
        cut.Find("[data-testid=pi-source-status]").TextContent.Should().Contain("No source detected").And.NotContain("Source detected 7");
        cut.FindAll("[data-testid=pi-role][data-state=Detected]").Select(r => r.GetAttribute("data-role")).Should().Equal("Plan");
        cut.Markup.Should().NotContain("shop/.specify").And.NotContain("constitution.md");
    }

    [Fact]
    public void AnInvalidArchive_ThenAValidOne_ClearsTheError()
    {
        _previews.Enqueue((null, new SourceUploadFailure("ARCHIVE_INVALID", "validation", "The ZIP could not be read.")));
        _previews.Enqueue((Archive('c', "shop", source: true, DocsA), null));
        var cut = Render<ProjectImportPage>();
        var before = Generation(cut);
        Choose(cut, "broken.zip");
        cut.Markup.Should().Contain("The ZIP could not be read.");
        Generation(cut).Should().NotBe(before, "after a failure the same file can be chosen again");

        Choose(cut, "shop.zip");

        cut.Markup.Should().NotContain("The ZIP could not be read.");
        cut.Find("[data-testid=pi-fingerprint]").TextContent.Should().Be(new string('c', 12));
    }

    [Fact]
    public void ChooseAnotherFile_ResetsEvenWhenReleasingTheStagedArchiveFails()
    {
        _api.Setup(a => a.DiscardAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException("timeout"));
        _previews.Enqueue((Archive('a', "shop", source: true, DocsA), null));
        var cut = Render<ProjectImportPage>();
        Choose(cut);

        cut.Find("[data-testid=pi-cancel]").Click();

        cut.FindAll("[data-testid=pi-preview]").Should().BeEmpty();
        cut.Find("[data-testid=pi-choose]").Should().NotBeNull();
    }

    [Fact]
    public void DocumentsOnly_SaysThereIsNoSource_InPlainWords()
    {
        _previews.Enqueue((Archive('d', "docs", source: false, DocsA), null));
        var cut = Render<ProjectImportPage>();
        Choose(cut);

        cut.Find("[data-testid=pi-source-status]").TextContent.Should()
            .Contain("This archive contains no source files that Source Analysis reads. Project documents can still be imported.");
    }

    // ── Current imported project: the source part is exact ─────────────────────────────────────────────────────────────

    private SddProjectImportRecord Activate(ProjectImportSourceState state, Guid? snapshot = null, bool source = true, string? message = null, bool canRetry = false,
        DateTimeOffset? stagedUntil = null)
    {
        var preview = Archive('e', "shop", source, DocsA);
        var commit = ProjectImportActivationTests.Commit(preview, state, snapshot) with { StagedUntil = stagedUntil };
        commit = commit with { Source = commit.Source with { Message = message, CanRetry = canRetry } };
        return new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), commit);
    }

    [Fact]
    public void CurrentImport_WithSnapshot_LinksTheExactSnapshot()
    {
        var snapshot = Guid.NewGuid();
        Activate(ProjectImportSourceState.Created, snapshot);

        var cut = Render<ProjectImportPage>();

        cut.Find("[data-testid=pi-current-source]").GetAttribute("data-has-snapshot").Should().Be("true");
        cut.Find("[data-testid=pi-current-open-source]").GetAttribute("href").Should().Be($"source-analysis?snapshot={snapshot:D}");
    }

    [Theory]
    [InlineData(ProjectImportSourceState.NotCreated, "Source detected — no snapshot", "Source Analysis is turned off")]
    [InlineData(ProjectImportSourceState.Failed, "Source detected — snapshot not created", "choose the same ZIP again")]
    public void CurrentImport_WithoutSnapshot_SaysWhy_AndNeverOffersAnEmptySourceAnalysis(ProjectImportSourceState state, string status, string detail)
    {
        Activate(state, message: state == ProjectImportSourceState.NotCreated ? "Source Analysis is turned off in Feature Visibility, so no source snapshot was created." : null);

        var cut = Render<ProjectImportPage>();

        cut.Find("[data-testid=pi-current-source]").TextContent.Should().Contain("Source code found · analysis not available", "the tester view says it plainly");
        cut.Find("[data-testid=pi-current-snapshot]").TextContent.Should().Contain(status, "the exact state stays in Technical details");
        cut.Find("[data-testid=pi-current-source-detail]").TextContent.Should().Contain(detail);
        cut.FindAll("[data-testid=pi-current-open-source]").Should().BeEmpty("there is no snapshot to open");
        cut.Markup.Should().NotContain("Source detected, no snapshot yet");
    }

    [Fact]
    public void CurrentImport_WithoutSource_DoesNotImplyASnapshot()
    {
        Activate(ProjectImportSourceState.NotDetected, source: false);

        var cut = Render<ProjectImportPage>();

        cut.Find("[data-testid=pi-current-source]").TextContent.Should().Contain("No source code in this project");
        cut.Find("[data-testid=pi-current-snapshot]").TextContent.Should().Contain("No source in this archive");
        cut.FindAll("[data-testid=pi-current-open-source], [data-testid=pi-current-retry-source]").Should().BeEmpty();
    }

    [Fact]
    public void CurrentImport_FailedWhileStaged_CanCreateItsSnapshot_WithoutChoosingTheZipAgain()
    {
        var record = Activate(ProjectImportSourceState.Failed, message: "The source snapshot could not be saved.", canRetry: true, stagedUntil: DateTimeOffset.UtcNow.AddHours(1));
        var stagingId = record.SourceStagingId ?? throw new InvalidOperationException("A retryable failed source keeps its staging id.");
        var snapshot = Guid.NewGuid();
        _commit = new ProjectImportCommitResult
        {
            StagingId = stagingId,
            Source = new ProjectImportSourceResult { State = ProjectImportSourceState.Created, SnapshotId = snapshot, SnapshotStatus = SourceAnalysisStatus.Ready },
        };
        var cut = Render<ProjectImportPage>();
        cut.Find("[data-testid=pi-current-source-detail]").TextContent.Should().Be("The source snapshot could not be saved.");
        cut.Find("[data-testid=pi-current-retry-source]").TextContent.Should().Be("Retry creating source analysis");

        cut.Find("[data-testid=pi-current-retry-source]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-current-open-source]").GetAttribute("href").Should().Be($"source-analysis?snapshot={snapshot:D}"));
        _repository.SddLifecycle.ProjectImports.Single().SourceSnapshotId.Should().Be(snapshot);
        _api.Verify(a => a.CommitAsync(stagingId, It.IsAny<CancellationToken>()), Times.Once);
        record.SourceStagingId.Should().BeNull("a settled source part keeps no staging reference");
        _previewCalls.Should().Be(0, "nothing is uploaded again");
    }

    [Fact]
    public void ImportWhoseSourcePartFails_IsNotReportedAsFullSuccess_AndOffersRetry()
    {
        _previews.Enqueue((Archive('f', "shop", source: true, DocsA), null));
        var preview = Archive('f', "shop", source: true, DocsA);
        _commit = new ProjectImportCommitResult
        {
            StagingId = preview.StagingId, StagedUntil = DateTimeOffset.UtcNow.AddHours(1),
            Source = new ProjectImportSourceResult { State = ProjectImportSourceState.Failed, Code = "SOURCE_SNAPSHOT_SAVE_FAILED", Message = "The source snapshot could not be saved.", CanRetry = true },
        };
        var cut = Render<ProjectImportPage>();
        Choose(cut);

        cut.Find("[data-testid=pi-import]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pi-result]"));
        cut.Find("[data-testid=pi-result]").TextContent.Should().Contain("Imported with notes");
        cut.Find("[data-testid=pi-retry-source]").Should().NotBeNull();
        cut.FindAll("[data-testid=pi-open-source]").Should().BeEmpty("no snapshot exists yet");
        _repository.SddLifecycle.ProjectImports.Single().SourceState.Should().Be(nameof(ProjectImportSourceState.Failed));
    }
}
