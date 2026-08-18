using BirkNext.Web.Layout;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Regression tests for Sample Project restart restoration.
/// Verifies that MainLayout startup restoration correctly restores a persisted workspace
/// without requiring manual "Load Supported Artifacts" action.
/// </summary>
public sealed class SampleProjectRestartRestorationTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly Mock<IWorkspacePersistenceApiService> _persistenceApi = new();
    private readonly Mock<IWorkspaceSessionRestoreService> _restoreService = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();

    public SampleProjectRestartRestorationTests()
    {
        _autoSave.Setup(x => x.StartMonitoringAsync()).Returns(Task.CompletedTask);
        _autoSave.Setup(x => x.StopMonitoringAsync()).Returns(Task.CompletedTask);

        Services.AddSingleton<IWorkspaceArtifactRepository>(_workspace);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IWorkspaceArtifactStatusService>(sp =>
            new WorkspaceArtifactStatusService(sp.GetRequiredService<IWorkspaceSessionService>()));
        Services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
        Services.AddSingleton(_autoSave.Object);
        Services.AddSingleton(_persistenceApi.Object);
        Services.AddSingleton(_restoreService.Object);
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(new FeatureVisibilityService());
        Services.AddSingleton(new AdminApiService(new HttpClient()));
        Services.AddSingleton(NullLogger<MainLayout>.Instance);
    }

    [Fact]
    public void MainLayout_WithPersistedAutorisasjonWorkspace_RestoresProjectAndArtifactsOnStartup()
    {
        // ARRANGE: Set up persisted workspace representing Autorisasjon with all 5 artifacts
        var persistedWorkspaceId = Guid.NewGuid();
        var persistedWorkspace = new SavedWorkspaceDto
        {
            Id = persistedWorkspaceId,
            Name = "Autorisasjon Auto-Save",
            ProjectName = "autorisasjon",
            Artifacts = new List<SavedWorkspaceArtifactDto>
            {
                new SavedWorkspaceArtifactDto
                {
                    ArtifactType = nameof(WorkspaceArtifactKind.Constitution),
                    FileName = "constitution.md",
                    Content = "# Autorisasjon Constitution\nCore governance document."
                },
                new SavedWorkspaceArtifactDto
                {
                    ArtifactType = nameof(WorkspaceArtifactKind.Specification),
                    FileName = "spec.md",
                    Content = "# Autorisasjon Specification\nSystem requirements and behavior."
                },
                new SavedWorkspaceArtifactDto
                {
                    ArtifactType = nameof(WorkspaceArtifactKind.Plan),
                    FileName = "plan.md",
                    Content = "# Autorisasjon Plan\nImplementation roadmap."
                },
                new SavedWorkspaceArtifactDto
                {
                    ArtifactType = nameof(WorkspaceArtifactKind.Tasks),
                    FileName = "tasks.md",
                    Content = "# Autorisasjon Tasks\nWork breakdown."
                },
                new SavedWorkspaceArtifactDto
                {
                    ArtifactType = nameof(WorkspaceArtifactKind.DataModel),
                    FileName = "data-model.md",
                    Content = "# Autorisasjon Data Model\nEntity definitions."
                }
            }
        };

        // Mock the persistence API to return the persisted current workspace state
        _persistenceApi
            .Setup(x => x.GetCurrentStateAsync())
            .ReturnsAsync(new CurrentWorkspaceStateDto
            {
                CurrentWorkspaceId = persistedWorkspaceId,
                WorkspaceName = persistedWorkspace.Name,
                ProjectName = persistedWorkspace.ProjectName,
                ArtifactCount = 5,
                Status = "AutoSaved"
            });

        _persistenceApi
            .Setup(x => x.LoadAsync(persistedWorkspaceId))
            .ReturnsAsync(persistedWorkspace);

        // Mock RestoreWorkspaceAsync to actually populate the workspace
        _restoreService
            .Setup(x => x.RestoreWorkspaceAsync(It.IsAny<SavedWorkspaceDto>()))
            .Callback<SavedWorkspaceDto>(ws =>
            {
                // Simulate the restoration by populating workspace artifacts
                foreach (var artifact in ws.Artifacts)
                {
                    var kind = artifact.ArtifactType switch
                    {
                        nameof(WorkspaceArtifactKind.Constitution) => WorkspaceArtifactKind.Constitution,
                        nameof(WorkspaceArtifactKind.Specification) => WorkspaceArtifactKind.Specification,
                        nameof(WorkspaceArtifactKind.Plan) => WorkspaceArtifactKind.Plan,
                        nameof(WorkspaceArtifactKind.Tasks) => WorkspaceArtifactKind.Tasks,
                        nameof(WorkspaceArtifactKind.DataModel) => WorkspaceArtifactKind.DataModel,
                        _ => throw new ArgumentException($"Unknown artifact type: {artifact.ArtifactType}")
                    };
                    _workspace.Set(kind, artifact.Content, artifact.FileName);
                }
                _workspace.CurrentProject = ws.ProjectName;
            })
            .Returns(Task.CompletedTask);

        // PRECONDITION: Verify workspace starts empty (simulating fresh app restart)
        _workspace.CurrentProject.Should().BeNullOrEmpty("Workspace should start empty");
        _workspace.GetAllArtifacts().Should().BeEmpty("No artifacts should be loaded initially");

        // ACT: Render MainLayout (which calls RestoreCurrentWorkspaceAsync on startup)
        var cut = Render<MainLayout>();

        // ASSERT: MainLayout initialization completes
        cut.Markup.Should().NotBeNullOrEmpty();

        // ASSERT: Workspace restoration was called exactly once
        _persistenceApi.Verify(x => x.GetCurrentStateAsync(), Times.Once);
        _persistenceApi.Verify(x => x.LoadAsync(persistedWorkspaceId), Times.Once);
        _restoreService.Verify(x => x.RestoreWorkspaceAsync(It.IsAny<SavedWorkspaceDto>()), Times.Once);

        // ASSERT: CurrentProject was restored to Autorisasjon
        _workspace.CurrentProject.Should().Be("autorisasjon");

        // ASSERT: All five artifacts are restored
        var artifacts = _workspace.GetAllArtifacts().ToList();
        artifacts.Should().HaveCount(5);

        _workspace.Get(WorkspaceArtifactKind.Constitution).Should().NotBeNull();
        _workspace.Get(WorkspaceArtifactKind.Constitution)!.Text.Should().Contain("Constitution");

        _workspace.Get(WorkspaceArtifactKind.Specification).Should().NotBeNull();
        _workspace.Get(WorkspaceArtifactKind.Specification)!.Text.Should().Contain("Autorisasjon Specification");

        _workspace.Get(WorkspaceArtifactKind.Plan).Should().NotBeNull();
        _workspace.Get(WorkspaceArtifactKind.Plan)!.Text.Should().Contain("Plan");

        _workspace.Get(WorkspaceArtifactKind.Tasks).Should().NotBeNull();
        _workspace.Get(WorkspaceArtifactKind.Tasks)!.Text.Should().Contain("Tasks");

        _workspace.Get(WorkspaceArtifactKind.DataModel).Should().NotBeNull();
        _workspace.Get(WorkspaceArtifactKind.DataModel)!.Text.Should().Contain("Data Model");

        // ASSERT: No AutoSave was triggered during restoration (no duplicate save)
        _autoSave.Verify(x => x.OnArtifactChanged(), Times.Never,
            "AutoSave should not be triggered during startup restoration");
    }

    [Fact]
    public void MainLayout_WithNoPersistedCurrentWorkspace_LeavesWorkspaceEmpty()
    {
        // ARRANGE: Mock persistence API to return no current workspace
        _persistenceApi
            .Setup(x => x.GetCurrentStateAsync())
            .ReturnsAsync(new CurrentWorkspaceStateDto
            {
                CurrentWorkspaceId = null,
                Status = "NotSaved",
                ArtifactCount = 0
            });

        // PRECONDITION: Verify workspace starts empty
        _workspace.CurrentProject.Should().BeNullOrEmpty();
        _workspace.GetAllArtifacts().Should().BeEmpty();

        // ACT: Render MainLayout
        var cut = Render<MainLayout>();

        // ASSERT: No restoration occurs when there's no current workspace
        _persistenceApi.Verify(x => x.GetCurrentStateAsync(), Times.Once);
        _persistenceApi.Verify(x => x.LoadAsync(It.IsAny<Guid>()), Times.Never);
        _restoreService.Verify(x => x.RestoreWorkspaceAsync(It.IsAny<SavedWorkspaceDto>()), Times.Never);

        // ASSERT: Workspace remains empty
        _workspace.CurrentProject.Should().BeNullOrEmpty();
        _workspace.GetAllArtifacts().Should().BeEmpty();
    }
}
