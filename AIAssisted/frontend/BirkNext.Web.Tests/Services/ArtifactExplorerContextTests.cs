using System.Text.Json;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Explorers select artifacts by ROLE from the current workspace: the selected Sample Project's discovered documents plus
/// documents imported into that workspace. File names, Sample Project selection and filesystem order never decide.
/// </summary>
public sealed class ArtifactExplorerContextTests
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _context;

    public ArtifactExplorerContextTests()
    {
        _context = new ArtifactExplorerContext(_workspace, _samples, _samples);
    }

    private SddArtifactRevisionHandle Import(WorkspaceArtifactType role, string fileName, string content, string? scope = null, bool select = false) =>
        new(_workspace.AddArtifactRevision(role, content, fileName, null, scope, "File", select)!);

    private sealed record SddArtifactRevisionHandle(BirkNext.Web.Models.SddArtifactRevision Revision);

    [Fact]
    public async Task EmptyManualWorkspace_IsEmptyWithoutProject()
    {
        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.Empty);
        state.HasProject.Should().BeFalse();
    }

    [Theory]
    [InlineData(WorkspaceArtifactType.Specification, "my-weird-document-name.md")]
    [InlineData(WorkspaceArtifactType.Specification, "requirements.md")]
    [InlineData(WorkspaceArtifactType.Constitution, "governance-rules.md")]
    [InlineData(WorkspaceArtifactType.Plan, "implementation-roadmap.md")]
    [InlineData(WorkspaceArtifactType.Tasks, "delivery-backlog.md")]
    [InlineData(WorkspaceArtifactType.DataModel, "domain-schema.md")]
    public async Task NoSampleProject_ImportedCustomFileName_LoadsByRole(WorkspaceArtifactType role, string fileName)
    {
        var result = _context.Import(new ArtifactImportRequest(role, "# Custom document\n\nBody", fileName, "File"));

        var state = await _context.GetStateAsync(role);

        result.IsSuccess.Should().BeTrue();
        _samples.GetSelectedProject().Should().BeNull();
        state.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        state.Selected!.FileName.Should().Be(fileName);
        state.Selected.DisplayName.Should().Be("Custom document");
        state.Content.Should().Contain("Body");
    }

    [Fact]
    public async Task ImportedRole_IsNotVisibleToOtherRoles()
    {
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# Spec", "spec.md", "File"));

        (await _context.GetStateAsync(WorkspaceArtifactType.Plan)).Status.Should().Be(ExplorerArtifactStatus.Empty);
    }

    [Fact]
    public async Task MultipleArtifacts_WithoutSelectionOrAuthority_RequireChoice()
    {
        Import(WorkspaceArtifactType.Specification, "requirements-a.md", "# A");
        Import(WorkspaceArtifactType.Specification, "requirements-b.md", "# B");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);
        state.Artifacts.Select(a => a.FileName).Should().BeEquivalentTo("requirements-a.md", "requirements-b.md");
        state.Selected.Should().BeNull();
        state.Content.Should().BeNull("several artifacts are never merged or resolved by order");
    }

    [Fact]
    public async Task MultipleArtifacts_OneAuthoritative_OpensTheAuthoritativeOne()
    {
        Import(WorkspaceArtifactType.Specification, "requirements-a.md", "# A");
        var b = Import(WorkspaceArtifactType.Specification, "requirements-b.md", "# B");
        _workspace.SetArtifactAuthority(b.Revision.RevisionId, "Approved");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        state.Selected!.FileName.Should().Be("requirements-b.md");
        state.Reason.Should().Be(ExplorerSelectionReason.Authoritative);
        state.Selected.IsAuthoritative.Should().BeTrue();
    }

    [Fact]
    public async Task ExplicitSelection_DiffersFromAuthority_WithoutChangingAuthority()
    {
        var a = Import(WorkspaceArtifactType.Specification, "requirements-a.md", "# A");
        var b = Import(WorkspaceArtifactType.Specification, "requirements-b.md", "# B");
        _workspace.SetArtifactAuthority(b.Revision.RevisionId, "Approved");

        _context.Select(WorkspaceArtifactType.Specification, ArtifactExplorerContext.WorkspacePrefix + "requirements-a.md");
        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Selected!.FileName.Should().Be("requirements-a.md");
        state.Reason.Should().Be(ExplorerSelectionReason.Explicit);
        a.Revision.Authority.Should().Be("Unknown");
        b.Revision.Authority.Should().Be("Approved");
        _workspace.SddLifecycle.Baselines.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_DoesNotCreateAuthorityOrBaseline()
    {
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Constitution, "# Rules", "governance-rules.md", "Paste"));

        var revision = _workspace.SddLifecycle.Revisions.Single();
        revision.Authority.Should().Be("Unknown");
        revision.Origin.Should().Be("Paste");
        revision.WorkspaceScope.Should().BeNull();
        _workspace.SddLifecycle.Baselines.Should().BeEmpty();
        (await _context.GetStateAsync(WorkspaceArtifactType.Constitution)).Selected!.IsAuthoritative.Should().BeFalse();
    }

    [Fact]
    public async Task ManualImport_IsTheRepositoryArtifact_NotExplorerState()
    {
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Plan, "# Roadmap", "implementation-roadmap.md", "Drop"));

        _workspace.Get(WorkspaceArtifactType.Plan)!.Text.Should().Be("# Roadmap");
        _workspace.Get(WorkspaceArtifactType.Plan)!.FileName.Should().Be("implementation-roadmap.md");
        (await _context.GetStateAsync(WorkspaceArtifactType.Plan)).Selected!.Origin.Should().Be("Drop");
    }

    [Fact]
    public async Task SampleProject_LoadsRoleFromAnyPath()
    {
        _samples.AddDocument("skole", WorkspaceArtifactType.Specification, "Skole/specs/001-elevfravaer/requirements.md", "# Elevfravær\n");
        _samples.SetSelectedProject("skole");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        state.Selected!.Source.Should().Be(ExplorerArtifactSource.SampleProject);
        state.Selected.SourcePath.Should().Be("Skole/specs/001-elevfravaer/requirements.md");
        state.Selected.FileName.Should().Be("requirements.md");
        state.ProjectName.Should().Be("skole");
    }

    [Fact]
    public async Task SampleProject_RoleAbsent_IsEmptyForThatProject()
    {
        _samples.SetProjectSpecification("project-a", "# Spec");
        _samples.SetSelectedProject("project-a");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Constitution);

        state.Status.Should().Be(ExplorerArtifactStatus.Empty);
        state.HasProject.Should().BeTrue();
    }

    [Fact]
    public async Task SampleProject_SeveralDocuments_RequireChoice_ThenExplorerChoiceIsSharedWithDiscovery()
    {
        _samples.AddDocument("p", WorkspaceArtifactType.Specification, "specs/001/spec.md", "# One");
        _samples.AddDocument("p", WorkspaceArtifactType.Specification, "specs/002/spec.md", "# Two");
        _samples.SetSelectedProject("p");

        (await _context.GetStateAsync(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.SelectionRequired);

        _context.Select(WorkspaceArtifactType.Specification, ArtifactExplorerContext.SamplePrefix + "specs/002/spec.md");
        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Content.Should().Be("# Two");
        _samples.ChosenPath("p", WorkspaceArtifactType.Specification).Should().Be("specs/002/spec.md",
            "analysis pages that resolve Sample Project documents follow the explorer choice");
    }

    [Fact]
    public async Task SampleProjectsPageChoice_IsAnExplicitSelection()
    {
        _samples.AddDocument("p", WorkspaceArtifactType.Plan, "a/plan.md", "# A");
        _samples.AddDocument("p", WorkspaceArtifactType.Plan, "b/plan.md", "# B");
        _samples.SetSelectedProject("p");
        _samples.ChooseDocument("p", WorkspaceArtifactType.Plan, "b/plan.md");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Plan);

        state.Content.Should().Be("# B");
        state.Reason.Should().Be(ExplorerSelectionReason.Explicit);
    }

    [Fact]
    public async Task ProjectSwitch_ShowsOnlyTheCurrentProjectsArtifacts()
    {
        _samples.SetProjectSpecification("project-a", "# A spec");
        _samples.SetProjectSpecification("project-b", "# B spec");
        _samples.SetSelectedProject("project-a");
        (await _context.GetStateAsync(WorkspaceArtifactType.Specification)).Content.Should().Be("# A spec");

        _samples.SetSelectedProject("project-b");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);
        state.Content.Should().Be("# B spec");
        state.Artifacts.Should().ContainSingle();
    }

    [Fact]
    public async Task ImportsAreScopedToTheirProject_AndNeverLeakAcrossProjects()
    {
        _samples.RegisterProject("project-a");
        _samples.RegisterProject("project-b");
        _samples.SetSelectedProject("project-a");
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Constitution, "# A rules", "governance-rules.md", "File"));
        (await _context.GetStateAsync(WorkspaceArtifactType.Constitution)).Content.Should().Be("# A rules");

        _samples.SetSelectedProject("project-b");
        (await _context.GetStateAsync(WorkspaceArtifactType.Constitution)).Status.Should().Be(ExplorerArtifactStatus.Empty);

        _samples.SetSelectedProject(null);
        (await _context.GetStateAsync(WorkspaceArtifactType.Constitution)).Status.Should().Be(ExplorerArtifactStatus.Empty,
            "the manual workspace does not show a project's imports");

        _samples.SetSelectedProject("project-a");
        (await _context.GetStateAsync(WorkspaceArtifactType.Constitution)).Content.Should().Be("# A rules");
        _workspace.Get(WorkspaceArtifactType.Constitution).Should().BeNull("a project import is not the manual workspace's session artifact");
    }

    [Fact]
    public async Task LegacySessionArtifact_StaysInTheManualWorkspace()
    {
        _workspace.Set(WorkspaceArtifactType.Specification, "# Old manual spec");
        _samples.SetProjectSpecification("project-a", "# Project spec");
        _samples.SetSelectedProject("project-a");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Content.Should().Be("# Project spec");
        state.Artifacts.Should().ContainSingle();
    }

    [Fact]
    public async Task Reload_ReconstructsArtifactsAndSelectionFromPersistedLifecycle()
    {
        Import(WorkspaceArtifactType.Specification, "requirements-a.md", "# A");
        Import(WorkspaceArtifactType.Specification, "requirements-b.md", "# B");
        _context.Select(WorkspaceArtifactType.Specification, ArtifactExplorerContext.WorkspacePrefix + "requirements-b.md");
        var json = JsonSerializer.Serialize(_workspace.SddLifecycle);

        var restored = new WorkspaceArtifactRepository();
        restored.RestoreSddLifecycle(json);
        var context = new ArtifactExplorerContext(restored, _samples, _samples);
        var state = await context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        state.Selected!.FileName.Should().Be("requirements-b.md");
        state.Artifacts.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reset_ClearsArtifactsAndRaisesChanged()
    {
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# Spec", "requirements.md", "File"));
        var raised = 0;
        _context.Changed += (_, _) => raised++;

        _workspace.ClearAll();

        raised.Should().BeGreaterThan(0);
        (await _context.GetStateAsync(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.Empty);
    }

    [Fact]
    public async Task HistoricalOrSupersededArtifact_IsMarked()
    {
        var a = Import(WorkspaceArtifactType.Tasks, "delivery-backlog.md", "# Backlog");
        _workspace.SetArtifactAuthority(a.Revision.RevisionId, "Historical");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Tasks);

        state.Selected!.Currentness.Should().Be(ExplorerArtifactCurrentness.Historical);
    }

    [Fact]
    public async Task RevisionsOfTheSameDocument_AreOneArtifact_ShowingTheCurrentRevision()
    {
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# Spec v1", "requirements.md", "File"));
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# Spec v2", "requirements.md", "File"));

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Artifacts.Should().ContainSingle();
        state.Selected!.Revision.Should().Be(2);
        state.Selected.RevisionCount.Should().Be(2);
        state.Content.Should().Be("# Spec v2");
    }

    [Fact]
    public async Task DiscoveryFailure_IsAnError_NotEmpty()
    {
        _samples.RegisterProject("p");
        _samples.SetSelectedProject("p");
        _samples.DiscoveryFailure = new HttpRequestException("backend down");

        var state = await _context.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.Error);
        state.Error.Should().Contain("backend down");
    }

    [Fact]
    public async Task SelectedProjectMissingFromCatalog_IsUnavailable()
    {
        _samples.SetSelectedProject("gone");

        (await _context.GetStateAsync(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.ProjectUnavailable);
    }

    [Fact]
    public void Import_KeepsSafetyLimits()
    {
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, new string('a', ArtifactExplorerContext.MaxImportBytes + 1), "big.md", "Paste"))
            .Error.Should().Contain("1 MB");
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "abc\0def", "bin.md", "Paste"))
            .Error.Should().Contain("Binary");
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "   ", "empty.md", "Paste"))
            .Error.Should().Contain("empty");
        _workspace.SddLifecycle.Revisions.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_FileNameIsReducedToADisplayName()
    {
        _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, "# S", "..\\..\\secret/dir/requirements.md", "File"));

        (await _context.GetStateAsync(WorkspaceArtifactType.Specification)).Selected!.FileName.Should().Be("requirements.md");
    }

    [Fact]
    public void Import_WithStrongOtherRole_KeepsChosenRoleAndSuggests()
    {
        const string tasks = "# Tasks: Feature\n\n## Phase 1: Setup\n\n- [ ] T001 Create project structure\n- [ ] T002 [P] Configure linting\n- [ ] T003 Add CI\n";

        var result = _context.Import(new ArtifactImportRequest(WorkspaceArtifactType.Specification, tasks, "tasks.md", "File"));

        result.IsSuccess.Should().BeTrue();
        result.SuggestedRole.Should().Be(WorkspaceArtifactType.Tasks);
        _workspace.SddLifecycle.Revisions.Single().Role.Should().Be("Specification");
    }

    [Fact]
    public void DisplayName_UsesFirstHeadingAfterFrontMatter()
    {
        ArtifactExplorerContext.DisplayNameOf("---\ntitle: x\n---\n# School attendance\n", "a.md").Should().Be("School attendance");
        ArtifactExplorerContext.DisplayNameOf("no heading", "a.md").Should().Be("a.md");
        var syncReport = "<!--\nSYNC IMPACT REPORT\n" + string.Concat(Enumerable.Repeat("# not a title\n", 100)) + "-->\n# Skoletjenesten Constitution\n";
        ArtifactExplorerContext.DisplayNameOf(syncReport, "constitution.md").Should().Be("Skoletjenesten Constitution");
        ArtifactExplorerContext.DisplayNameOf("```\n# code\n```\n# Real\n", "a.md").Should().Be("Real");
    }
}
