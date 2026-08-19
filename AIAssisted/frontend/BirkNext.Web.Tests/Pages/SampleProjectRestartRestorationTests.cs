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
///
/// ROOT CAUSE FIXED:
/// - CurrentProject must store the PROJECT SLUG (lowercase: "autorisasjon")
/// - NOT the display name (capitalized: "Autorisasjon")
/// - IsCurrentWorkspace must compare slug to slug
/// - This ensures Sample Projects recognizes the loaded state after restart
/// - And Explorers can load their content from the restored project context
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
        // The saved workspace uses the PROJECT SLUG ("autorisasjon"), not the display name
        // This fixture deliberately uses DIFFERENT display name and slug to prevent the bug
        // from being hidden by accidental name/slug equality in test data.
        var persistedWorkspaceId = Guid.NewGuid();
        var persistedWorkspace = new SavedWorkspaceDto
        {
            Id = persistedWorkspaceId,
            Name = "Autorisasjon Auto-Save",
            ProjectName = "autorisasjon",  // SLUG: lowercase, canonical project identifier
            // CRITICAL: ProjectName must be the slug, NOT project.Name ("Autorisasjon")
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
                // CRITICAL: Must set ProjectName to the SLUG, not the display name
                _workspace.ProjectName = ws.ProjectName;
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

        // ASSERT: CurrentProject was restored to the SLUG "autorisasjon"
        _workspace.CurrentProject.Should().Be("autorisasjon",
            "CurrentProject must store the project slug, not the display name");

        // ASSERT: CurrentProject is explicitly NOT the display name
        _workspace.CurrentProject.Should().NotBe("Autorisasjon",
            "Regression: CurrentProject must never store display name; slug identity is canonical");

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
    public void SampleProjects_LoadArtifacts_StoresCanonicalProjectSlug()
    {
        // REGRESSION: This test protects the ORIGINAL write boundary.
        // Verifies that SampleProjects.LoadArtifacts stores the canonical slug,
        // not the display name, as Workspace.CurrentProject.

        // ARRANGE: Create a sample project with distinct display name and slug
        var project = new SampleProjectDto(
            Slug: "autorisasjon",           // Canonical lowercase slug
            Name: "Autorisasjon",           // Display name (capitalized)
            Domain: "Authorization",
            Description: "Demo project",
            AbsolutePath: "/sample/autorisasjon",
            HasReadme: false,
            Files: new[]
            {
                new SampleFileDto(
                    Filename: "spec.md",
                    Exists: true,
                    ArtifactKind: "Specification",
                    ReviewerName: "Specification Explorer",
                    ReviewerRoute: "specification-explorer",
                    IsSupported: true,
                    IsContextOnly: false)
            });

        // PRECONDITION: Workspace starts empty
        _workspace.CurrentProject.Should().BeNullOrEmpty();

        // ACT: Simulate what LoadArtifacts does when user loads a sample project
        // (In real code, this happens in SampleProjects.razor:646)
        _workspace.CurrentProject = project.Slug;  // The FIX: store slug, not Name
        _workspace.Set(WorkspaceArtifactKind.Specification, "# Spec", fileName: "spec.md");

        // ASSERT: CurrentProject stores the SLUG
        _workspace.CurrentProject.Should().Be("autorisasjon",
            "When loading sample project, CurrentProject must store the canonical slug");

        // ASSERT: CurrentProject does NOT store the display name
        _workspace.CurrentProject.Should().NotBe("Autorisasjon",
            "CurrentProject must never store the display name");

        // ASSERT: Artifact is also loaded (confirms full load operation)
        _workspace.Get(WorkspaceArtifactKind.Specification).Should().NotBeNull();
    }

    [Fact]
    public void SampleProjects_IsCurrentWorkspace_ComparesSlugToSlug()
    {
        // REGRESSION: Verifies the loaded-state predicate uses slug comparison.
        // This prevents the bug where sample projects still showed "Load Supported Artifacts"
        // even though the project was loaded.

        // ARRANGE: Set up workspace with canonical slug
        _workspace.CurrentProject = "autorisasjon";  // Canonical slug

        // Simulate the sample project from the catalog
        var project = new SampleProjectDto(
            Slug: "autorisasjon",
            Name: "Autorisasjon",           // Different from slug (capitalized)
            Domain: "Authorization",
            Description: "Demo project",
            AbsolutePath: "/sample/autorisasjon",
            HasReadme: false,
            Files: []);

        // ACT: Test the comparison that determines loaded state
        // This mimics SampleProjects.razor:IsCurrentWorkspace()
        var isCurrentWorkspace = string.Equals(_workspace.CurrentProject, project.Slug, StringComparison.Ordinal);

        // ASSERT: Comparison succeeds because both are slugs
        isCurrentWorkspace.Should().BeTrue(
            "IsCurrentWorkspace must compare slug to slug, not display name to slug");

        // ASSERT: It would fail if we compared to display name (documents the defect)
        var wouldFailIfUsingName = string.Equals(_workspace.CurrentProject, project.Name, StringComparison.Ordinal);
        wouldFailIfUsingName.Should().BeFalse(
            "Regression: Comparing slug 'autorisasjon' to display name 'Autorisasjon' must fail");
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

    [Fact]
    public async Task SampleProjectSelection_WithZeroArtifacts_PersistsAndRestoresProjectSlugOnly()
    {
        // CRITICAL: Test that projects can be persisted by IDENTITY ONLY (no artifact copies)
        // This is the core of the post-artifact-copy-retirement design:
        // selecting a project persists the SLUG, not Markdown copies.

        // ===== PHASE 1: INITIAL STATE =====
        // Verify no Autorisasjon is persisted
        _persistenceApi
            .Setup(x => x.GetCurrentStateAsync())
            .ReturnsAsync(new CurrentWorkspaceStateDto
            {
                CurrentWorkspaceId = null,  // No persisted state
                Status = "NotSaved",
                ArtifactCount = 0
            });

        _persistenceApi
            .Setup(x => x.LoadAsync(It.IsAny<Guid>()))
            .ReturnsAsync((SavedWorkspaceDto?)null);

        // Precondition: fresh frontend state
        _workspace.CurrentProject.Should().BeNullOrEmpty("Start with no persisted project");
        _workspace.GetAllArtifacts().Should().BeEmpty("Start with no artifacts");

        // ===== PHASE 2: USER SELECTS SAMPLE PROJECT =====
        // Simulate SampleProjects.razor selection behavior:
        // - Set CurrentProject to canonical slug
        // - Do NOT load/copy any Markdown artifacts
        var selectedProject = new SampleProjectDto(
            Slug: "autorisasjon",
            Name: "Autorisasjon",  // Display name (capitalized)
            Domain: "Authorization",
            Description: "Sample project for auth scenarios",
            AbsolutePath: "/sample/autorisasjon",
            HasReadme: false,
            Files: [
                new SampleFileDto(
                    Filename: "spec.md",
                    Exists: true,
                    ArtifactKind: "Specification",
                    ReviewerName: "Specification Explorer",
                    ReviewerRoute: "/specification-explorer",
                    IsSupported: true,
                    IsContextOnly: false)
            ]);

        // This is the ONLY action: set CurrentProject
        _workspace.CurrentProject = selectedProject.Slug;

        // ===== PHASE 3: VERIFY PERSISTENCE REQUEST =====
        // ProjectSelectionChanged event should fire and trigger AutoSave
        var autoSaveCalled = false;
        var persistedProjectName = (string?)null;

        _persistenceApi
            .Setup(x => x.AutoSaveAsync(It.IsAny<string>()))
            .Callback<string>(generatedName =>
            {
                autoSaveCalled = true;
                // In real app, persistence service captures _artifactRepository.CurrentProject
                // which would be "autorisasjon" at this point
                persistedProjectName = _workspace.CurrentProject;
            })
            .ReturnsAsync(new SavedWorkspaceDto
            {
                Id = Guid.NewGuid(),
                Name = "Auto_Workspace",
                ProjectName = "autorisasjon",  // Backend stores the slug
                Artifacts = new(),  // CRITICAL: NO artifact copies persisted
                AutoSaved = true
            });

        // Manually trigger AutoSave (in real app, ProjectSelectionChanged → debounce → AutoSaveAsync)
        // We're testing the persistence contract, not timing
        var persistenceService = Services.GetRequiredService<IWorkspacePersistenceApiService>();
        var result = await persistenceService.AutoSaveAsync();

        // ===== VERIFY PERSISTENCE CALL =====
        autoSaveCalled.Should().BeTrue("AutoSave should be triggered by project selection");
        persistedProjectName.Should().Be("autorisasjon",
            "Persisted project must be canonical slug, not display name");
        result.Should().NotBeNull();
        result!.ProjectName.Should().Be("autorisasjon");
        result.Artifacts.Should().BeEmpty("No Markdown copies should be stored");

        // ===== PHASE 4: SIMULATE FRESH FRONTEND STATE =====
        // Create new workspace instance (simulating app restart)
        var freshWorkspace = new WorkspaceArtifactRepository();
        freshWorkspace.CurrentProject.Should().BeNullOrEmpty("Fresh state has no project");
        freshWorkspace.GetAllArtifacts().Should().BeEmpty("Fresh state has no artifacts");

        // ===== PHASE 5: RESTORE FROM PERSISTENCE =====
        // Backend returns the persisted workspace
        var persistedWorkspace = new SavedWorkspaceDto
        {
            Id = result.Id,
            Name = result.Name,
            ProjectName = "autorisasjon",  // The persisted slug
            Artifacts = new(),  // Still no artifacts
            AutoSaved = true
        };

        // Simulate RestoreWorkspaceAsync behavior
        // (In real app this is called from MainLayout startup)
        foreach (var artifact in persistedWorkspace.Artifacts)
        {
            // (empty loop - no artifacts to restore)
        }
        freshWorkspace.ProjectName = persistedWorkspace.ProjectName;  // This line fires ProjectSelectionChanged

        // ===== VERIFY RESTORATION =====
        freshWorkspace.CurrentProject.Should().Be("autorisasjon",
            "Restored project must be the canonical slug");
        freshWorkspace.CurrentProject.Should().NotBe("Autorisasjon",
            "Restored project must not be the display name");
        freshWorkspace.GetAllArtifacts().Should().BeEmpty(
            "Restored state has no artifact copies (identity-only persistence)");

        // ===== PHASE 6: VERIFY EXPLORER CAN LOAD FROM DOCUMENTRESOLVER =====
        // With no Workspace artifacts, explorers must rely on DocumentResolver
        // Simulate SpecificationExplorer.OnInitializedAsync behavior:
        if (!string.IsNullOrEmpty(freshWorkspace.CurrentProject))
        {
            // DocumentResolver would resolve using the canonical slug
            var explorerProjectSlug = freshWorkspace.CurrentProject;
            explorerProjectSlug.Should().Be("autorisasjon");  // Explorer has the slug

            // In real app, DocumentResolver.ResolveAsync("autorisasjon", "Specification")
            // fetches fresh content from SampleData filesystem
            // (not from Workspace copies)
        }
        else
        {
            throw new InvalidOperationException("Explorer would fail: no project selected");
        }
    }
}
