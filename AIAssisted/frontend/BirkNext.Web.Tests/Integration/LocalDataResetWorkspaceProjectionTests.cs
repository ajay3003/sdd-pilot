using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// Packaged-app defect (2026-11-19): after Local Data Reset the Dashboard still named the previous auto-saved workspace ("Auto_…",
/// "Auto-saved") and kept its readiness hero, while the backend already reported no current workspace. A reload fixed it.
/// Cause: the projection's last invalidation during the reset came before the restored workspace metadata was cleared, so a reader that
/// recomputed on that change (Dashboard, sidebar) cached a snapshot built from the old metadata. Nothing invalidated it afterwards.
/// These tests wire the real restore service, explorer context, projection and reset coordinator together.
/// </summary>
public sealed class LocalDataResetWorkspaceProjectionTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly WorkspaceStateManager _stateManager = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly WorkspaceSessionRestoreService _restore;
    private readonly CurrentWorkspaceProjection _projection;
    private readonly ApplicationRuntimeResetService _reset;

    public LocalDataResetWorkspaceProjectionTests()
    {
        _samples.Repository = _repository;
        var explorers = new ArtifactExplorerContext(_repository, _samples, _samples, stateManager: _stateManager);
        var reviewContext = new Mock<IReviewContextProvider>();
        reviewContext.Setup(r => r.RebuildAsync()).Returns(Task.CompletedTask);
        var sampleProjects = new SampleProjectsApiService(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        _restore = new WorkspaceSessionRestoreService(_repository, _stateManager, reviewContext.Object, sampleProjects,
            NullLogger<WorkspaceSessionRestoreService>.Instance);
        // The backend has already reset: it reports no current workspace.
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(p => p.GetCurrentStateAsync()).ReturnsAsync(new CurrentWorkspaceStateDto { Status = "NotSaved", ResetEpoch = 1 });
        _projection = new CurrentWorkspaceProjection(explorers, _restore, persistence.Object, NullLogger<CurrentWorkspaceProjection>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceSessionRestoreService>(_restore);
        services.AddSingleton<ICurrentWorkspaceProjection>(_projection);
        var provider = services.BuildServiceProvider();
        _reset = new ApplicationRuntimeResetService(_repository, _stateManager, new QualityReviewSessionService(), new DashboardSnapshotService(_stateManager),
            new RuntimeReviewSessionService(), Mock.Of<IExtractionSessionService>(s => s.ClearAsync() == Task.CompletedTask), services: provider);
    }

    /// <summary>The auto-saved workspace the app restores on start (a project import has no Sample Project name).</summary>
    private Task RestoreAutoSavedImportAsync() => _restore.RestoreWorkspaceAsync(new SavedWorkspaceDto
    {
        Id = Guid.NewGuid(),
        Name = "Auto_20261119_094015",
        ProjectName = "",
        AutoSaved = true,
        Artifacts = [new SavedWorkspaceArtifactDto { ArtifactType = nameof(WorkspaceArtifactType.Specification), Content = "# LendoPlus spec", FileName = "spec.md" }],
    });

    [Fact]
    public async Task AfterReset_AReaderThatRecomputesOnEveryChange_SeesNoWorkspace()
    {
        await RestoreAutoSavedImportAsync();
        (await _projection.GetAsync()).WorkspaceName.Should().NotBeNullOrEmpty();
        // The Dashboard and the sidebar re-read the projection whenever it changes, during the reset too.
        _projection.Changed += () => _ = _projection.GetAsync();

        await _reset.ClearFrontendRuntimeStateAsync();
        var snapshot = await _projection.GetAsync();

        (await _restore.GetCurrentWorkspaceMetadataAsync()).Should().BeNull();
        snapshot.State.Should().Be(CurrentWorkspaceState.NoWorkspace, "the backend and the restore service both hold no workspace any more");
        snapshot.WorkspaceName.Should().NotBe("Auto_20261119_094015");
        snapshot.SaveStatus.Should().NotBe("AutoSaved");
        _reset.LastWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterReset_ANewPageReadingTheProjection_SeesNoWorkspace()
    {
        await RestoreAutoSavedImportAsync();
        _projection.Changed += () => _ = _projection.GetAsync();
        await _reset.ClearFrontendRuntimeStateAsync();

        // The Dashboard opened by the reset navigation reads the cached snapshot.
        _projection.Current.Should().NotBeNull();
        _projection.Current!.State.Should().Be(CurrentWorkspaceState.NoWorkspace);
    }

    [Fact]
    public async Task ClearingTheRestoredWorkspace_InvalidatesTheProjection_AfterTheMetadataIsGone()
    {
        await RestoreAutoSavedImportAsync();
        // No workspace id is current any more (the reset cleared it, or the project was never saved), so "workspace changed (null)" is
        // not raised again by the clear: the restore service must still announce the change once its metadata is gone.
        _stateManager.NotifyWorkspaceChanged(null);
        CurrentWorkspaceMetadata? metadataAtLastChange = new();
        _projection.Changed += () => metadataAtLastChange = _restore.GetCurrentWorkspaceMetadataAsync().Result;

        await _restore.ClearWorkspaceAsync();

        metadataAtLastChange.Should().BeNull("the last change notification must come after the restored workspace is forgotten");
    }
}
