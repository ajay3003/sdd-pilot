using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The current workspace is decided once, from the workspace repository and the selected project's documents. Save state,
/// ReviewContext, explorer selection and authority add facts; none of them decides whether a workspace or artifact exists.
/// </summary>
public sealed class CurrentWorkspaceProjectionTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly Mock<IWorkspaceSessionRestoreService> _restore = new();
    private readonly Mock<IWorkspacePersistenceApiService> _persistence = new();
    private readonly ArtifactExplorerContext _explorers;
    private readonly CurrentWorkspaceProjection _projection;

    public CurrentWorkspaceProjectionTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
        _projection = new CurrentWorkspaceProjection(_explorers, _restore.Object, _persistence.Object, NullLogger<CurrentWorkspaceProjection>.Instance);
    }

    private void PersonModule()
    {
        _samples.SetProjectConstitution("person-module", "# Constitution");
        _samples.SetProjectSpecification("person-module", "# Person spec");
        _samples.SetProjectPlan("person-module", "# Plan");
        _samples.SetProjectTasks("person-module", "# Tasks");
        _samples.SetProjectDataModel("person-module", "# Data model");
    }

    [Fact]
    public async Task NothingSelectedOrImported_IsNoWorkspace()
    {
        var snapshot = await _projection.GetAsync();

        snapshot.State.Should().Be(CurrentWorkspaceState.NoWorkspace);
        snapshot.WorkspaceLoaded.Should().BeFalse();
        snapshot.ProjectLoaded.Should().BeFalse();
        snapshot.AvailableRoleCount.Should().Be(0);
    }

    [Fact]
    public async Task SelectedSampleProject_WithoutSessionCopies_IsLoadedWithAllRoles()
    {
        PersonModule();
        _repository.CurrentProject = "person-module";

        var snapshot = await _projection.GetAsync();

        _repository.Has(WorkspaceArtifactType.Specification).Should().BeFalse("Sample Project documents are never copied into the session");
        snapshot.WorkspaceLoaded.Should().BeTrue();
        snapshot.ProjectLoaded.Should().BeTrue();
        snapshot.ProjectSlug.Should().Be("person-module");
        snapshot.ProjectName.Should().Be("person module");
        snapshot.WorkspaceName.Should().Be("person module", "an unsaved or auto-saved workspace is named after its project");
        snapshot.AvailableRoleCount.Should().Be(5);
        snapshot.RoleSummary.Should().Be("5 artifact roles available");
        snapshot.SaveStatus.Should().Be("NotSaved", "unsaved is still loaded");
    }

    [Fact]
    public async Task PartialProject_CountsOnlyItsRoles_AndIsStillLoaded()
    {
        _samples.SetProjectSpecification("docs", "# Spec");
        _samples.SetProjectTasks("docs", "# Tasks");
        _repository.CurrentProject = "docs";

        var snapshot = await _projection.GetAsync();

        snapshot.WorkspaceLoaded.Should().BeTrue();
        snapshot.AvailableRoles.Select(r => r.Role).Should().BeEquivalentTo([WorkspaceArtifactType.Specification, WorkspaceArtifactType.Tasks]);
        snapshot.Role(WorkspaceArtifactType.Plan).Availability.Should().Be(ArtifactRoleAvailability.Missing);
    }

    [Fact]
    public async Task ProjectWithoutDocuments_IsALoadedWorkspaceWithZeroRoles()
    {
        _samples.RegisterProject("source-only");
        _repository.CurrentProject = "source-only";

        var snapshot = await _projection.GetAsync();

        snapshot.State.Should().Be(CurrentWorkspaceState.Loaded);
        snapshot.AvailableRoleCount.Should().Be(0);
        snapshot.ProjectInCatalog.Should().BeTrue();
    }

    [Fact]
    public async Task ManualImportWithoutSampleProject_IsLoaded_WithNoProjectAssigned()
    {
        _explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# Requirements", "requirements.md", "File"));
        _explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Plan, "# Roadmap", "roadmap.md", "Paste"));

        var snapshot = await _projection.GetAsync();

        snapshot.WorkspaceLoaded.Should().BeTrue();
        snapshot.ProjectLoaded.Should().BeFalse();
        snapshot.ProjectDisplay.Should().Be("Not assigned");
        snapshot.WorkspaceName.Should().Be("Unsaved workspace");
        snapshot.AvailableRoleCount.Should().Be(2);
        snapshot.Role(WorkspaceArtifactType.Specification).SelectedArtifact!.FileName.Should().Be("requirements.md");
    }

    [Fact]
    public async Task SessionCopies_FromALegacyManualWorkspace_CountAsArtifacts()
    {
        _repository.Set(WorkspaceArtifactType.Specification, "# Spec", "spec.md");

        var snapshot = await _projection.GetAsync();

        snapshot.WorkspaceLoaded.Should().BeTrue();
        snapshot.Has(WorkspaceArtifactType.Specification).Should().BeTrue();
    }

    [Fact]
    public async Task SeveralArtifactsOfOneRole_AreOneRole_AndRequireASelection()
    {
        _samples.AddDocument("multi", WorkspaceArtifactType.Specification, "specs/001/spec.md", "# One");
        _samples.AddDocument("multi", WorkspaceArtifactType.Specification, "specs/002/spec.md", "# Two");
        _samples.AddDocument("multi", WorkspaceArtifactType.Specification, "specs/003/spec.md", "# Three");
        _samples.SetProjectPlan("multi", "# Plan");
        _repository.CurrentProject = "multi";

        var snapshot = await _projection.GetAsync();

        snapshot.AvailableRoleCount.Should().Be(2);
        snapshot.ArtifactCount.Should().Be(4);
        snapshot.RoleSummary.Should().Be("2 artifact roles available · 4 artifacts");
        snapshot.Role(WorkspaceArtifactType.Specification).Selection.Should().Be(ArtifactRoleSelection.SelectionRequired);
    }

    [Fact]
    public async Task SelectionAndAuthorityChanges_NeverChangeRoleAvailability()
    {
        var first = _explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# A", "a.md", "File"));
        _explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# B", "b.md", "File"));
        var before = await _projection.GetAsync();

        _explorers.Select(WorkspaceArtifactType.Specification, first.ArtifactId!);
        var revision = _repository.SddLifecycle.Revisions.First(r => r.FileName == "a.md");
        _repository.SetArtifactAuthority(revision.RevisionId, "Baseline");
        var after = await _projection.GetAsync();

        after.AvailableRoleCount.Should().Be(before.AvailableRoleCount).And.Be(1);
        after.ArtifactCount.Should().Be(before.ArtifactCount).And.Be(2);
        after.Role(WorkspaceArtifactType.Specification).SelectedArtifact!.FileName.Should().Be("a.md");
        after.Role(WorkspaceArtifactType.Specification).Authority.Should().Be("Baseline");
    }

    [Fact]
    public async Task ProjectSwitch_RaisesChanged_AndNeverShowsThePreviousProject()
    {
        PersonModule();
        _samples.SetProjectSpecification("skole", "# Skole spec");
        _repository.CurrentProject = "person-module";
        (await _projection.GetAsync()).AvailableRoleCount.Should().Be(5);
        var changed = 0;
        _projection.Changed += () => changed++;

        _repository.CurrentProject = "skole";
        var snapshot = await _projection.GetAsync();

        changed.Should().BeGreaterThan(0);
        snapshot.ProjectSlug.Should().Be("skole");
        snapshot.AvailableRoleCount.Should().Be(1);
    }

    [Fact]
    public async Task Reset_ClearsTheWorkspace_AndRaisesChanged()
    {
        PersonModule();
        _repository.CurrentProject = "person-module";
        (await _projection.GetAsync()).WorkspaceLoaded.Should().BeTrue();
        var changed = 0;
        _projection.Changed += () => changed++;

        _repository.ClearAll();
        var snapshot = await _projection.GetAsync();

        changed.Should().BeGreaterThan(0);
        snapshot.State.Should().Be(CurrentWorkspaceState.NoWorkspace);
        snapshot.ProjectSlug.Should().BeNull();
    }

    [Fact]
    public async Task ResumedEmptyWorkspace_IsLoaded_NotNoWorkspace()
    {
        _restore.Setup(r => r.GetCurrentWorkspaceMetadataAsync()).ReturnsAsync(new CurrentWorkspaceMetadata
        {
            WorkspaceId = Guid.NewGuid(), WorkspaceName = "Empty review", ProjectName = "", AutoSaved = false
        });

        var snapshot = await _projection.GetAsync();

        snapshot.State.Should().Be(CurrentWorkspaceState.Loaded);
        snapshot.WorkspaceName.Should().Be("Empty review");
        snapshot.AvailableRoleCount.Should().Be(0);
    }

    [Fact]
    public async Task ResumedWorkspaceOfAnotherProject_DoesNotNameTheCurrentOne()
    {
        PersonModule();
        _restore.Setup(r => r.GetCurrentWorkspaceMetadataAsync()).ReturnsAsync(new CurrentWorkspaceMetadata
        {
            WorkspaceId = Guid.NewGuid(), WorkspaceName = "Skole review", ProjectName = "skole", AutoSaved = false
        });
        _repository.CurrentProject = "person-module";

        var snapshot = await _projection.GetAsync();

        snapshot.WorkspaceName.Should().Be("person module");
        snapshot.WorkspaceId.Should().BeNull();
    }

    [Fact]
    public async Task SavedWorkspace_ReportsSaveStateSeparatelyFromExistence()
    {
        PersonModule();
        var id = Guid.NewGuid();
        _persistence.Setup(p => p.GetCurrentStateAsync()).ReturnsAsync(new CurrentWorkspaceStateDto
        {
            CurrentWorkspaceId = id, WorkspaceName = "Person review", ProjectName = "person-module", Status = "Saved", LastSavedAt = DateTimeOffset.UtcNow
        });
        _repository.CurrentProject = "person-module";

        var snapshot = await _projection.GetAsync();

        snapshot.WorkspaceLoaded.Should().BeTrue();
        snapshot.WorkspaceId.Should().Be(id);
        snapshot.WorkspaceName.Should().Be("Person review");
        snapshot.SaveStatusLabel.Should().Be("Saved");
    }

    [Fact]
    public async Task AutoSavedWorkspace_IsNamedAfterItsProject_NotItsGeneratedName()
    {
        PersonModule();
        _persistence.Setup(p => p.GetCurrentStateAsync()).ReturnsAsync(new CurrentWorkspaceStateDto
        {
            CurrentWorkspaceId = Guid.NewGuid(), WorkspaceName = "Auto_20261007_101010", ProjectName = "person-module", Status = "AutoSaved"
        });
        _repository.CurrentProject = "person-module";

        var snapshot = await _projection.GetAsync();

        snapshot.WorkspaceName.Should().Be("person module");
        snapshot.SaveStatusLabel.Should().Be("Auto-saved");
    }

    [Fact]
    public async Task UnreadableProjectDocuments_AreAnError_NotNoWorkspace()
    {
        PersonModule();
        _samples.DiscoveryFailure = new HttpRequestException("backend offline");
        _repository.CurrentProject = "person-module";

        var snapshot = await _projection.GetAsync();

        snapshot.State.Should().Be(CurrentWorkspaceState.Error);
        snapshot.WorkspaceLoaded.Should().BeFalse();
        snapshot.ProjectSlug.Should().Be("person-module");
        snapshot.Error.Should().Contain("backend offline");
        snapshot.Roles.Should().OnlyContain(r => r.Availability == ArtifactRoleAvailability.Unknown);
    }

    [Fact]
    public async Task ProjectMissingFromCatalog_IsLoaded_WithUnknownAvailability()
    {
        _repository.CurrentProject = "removed-project";

        var snapshot = await _projection.GetAsync();

        snapshot.WorkspaceLoaded.Should().BeTrue();
        snapshot.ProjectInCatalog.Should().BeFalse();
        snapshot.Roles.Should().OnlyContain(r => r.Availability == ArtifactRoleAvailability.Unknown);
    }

    [Fact]
    public async Task ConcurrentReaders_ShareOneSnapshot_AndAChangeInvalidatesIt()
    {
        PersonModule();
        _repository.CurrentProject = "person-module";

        var a = _projection.GetAsync();
        var b = _projection.GetAsync();
        (await a).Should().BeSameAs(await b);
        _projection.Current.Should().BeSameAs(await a);

        _repository.NotifyArtifactsChanged();
        var c = await _projection.GetAsync();

        c.Should().NotBeSameAs(await a);
        c.Version.Should().BeGreaterThan((await a).Version);
    }
}
