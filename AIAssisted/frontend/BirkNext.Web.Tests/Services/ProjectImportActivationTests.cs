using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Project Import in the Shared Artifact Repository: detected documents become revisions in the import's own scope with shared provenance,
/// the import becomes the current project, and documents of another import, of the manual workspace or of a Sample Project never mix in.
/// Generic fixtures only.
/// </summary>
public sealed class ProjectImportActivationTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;
    private readonly ProjectImportActivation _activation;

    public ProjectImportActivationTests()
    {
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
        _activation = new ProjectImportActivation(_repository);
    }

    internal const string Constitution = "# Shop Constitution\n\n## Core Principles\n\n### I. Test first\nEvery change MUST have tests.\n";
    internal const string Spec = "# Feature Specification: Cart\n\n## User Scenarios & Testing\n\n### User Story 1 - Add to cart (Priority: P1)\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: System MUST store carts.\n";
    internal const string Plan = "# Implementation Plan: Cart\n\n## Summary\n\n## Technical Context\n\n**Language/Version**: C# 12\n\n## Project Structure\n";
    internal const string Tasks = "# Tasks: Cart\n\n## Phase 1: Setup\n\n- [ ] T001 Create project\n- [ ] T002 [US1] Add cart entity\n";

    internal static ProjectImportPreview Preview(string sha, bool source, params (string Path, string Content)[] documents) => new()
    {
        StagingId = Guid.NewGuid(),
        ImportId = "import-" + sha[..16],
        Archive = new ProjectImportArchive("shop.zip", sha, 4096, 12),
        ProjectName = "shop",
        ProjectNameBasis = ProjectNameBasis.ArchiveRoot,
        Documents = documents.Select(d => new ProjectImportDocument(d.Path, d.Path.Split('/')[^1], d.Content.Length, d.Content)).ToList(),
        Source = new ProjectImportSourceDetection { Detected = source, SourceFiles = source ? 3 : 0 },
    };

    internal static ProjectImportCommitResult Commit(ProjectImportPreview preview, ProjectImportSourceState state, Guid? snapshot = null) => new()
    {
        StagingId = preview.StagingId,
        Provenance = new ProjectImportProvenance { ImportId = preview.ImportId, ArchiveFileName = preview.Archive.FileName, ArchiveSha256 = preview.Archive.Sha256, ImportedAt = DateTimeOffset.UtcNow },
        Source = new ProjectImportSourceResult { State = state, SnapshotId = snapshot, EnvironmentId = snapshot is null ? null : "dev", SnapshotStatus = snapshot is null ? null : SourceAnalysisStatus.Ready },
    };

    private SddProjectImportRecord Activate(ProjectImportPreview preview, ProjectImportSourceState state = ProjectImportSourceState.NotDetected, Guid? snapshot = null) =>
        _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, state, snapshot));

    private Task<ArtifactExplorerState> State(WorkspaceArtifactType role) => _explorers.GetStateAsync(role);

    [Fact]
    public async Task MixedImport_PopulatesTheRepository_InItsOwnScope_WithSharedProvenance()
    {
        var snapshot = Guid.NewGuid();
        var preview = Preview(new string('a', 64), source: true,
            ("shop/.specify/memory/constitution.md", Constitution), ("shop/specs/001-cart/spec.md", Spec),
            ("shop/specs/001-cart/plan.md", Plan), ("shop/specs/001-cart/tasks.md", Tasks));

        var record = Activate(preview, ProjectImportSourceState.Created, snapshot);

        _explorers.CurrentScope.Should().Be("import:" + preview.ImportId);
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        spec.ProjectName.Should().Be("shop");
        spec.Selected!.SourcePath.Should().Be("shop/specs/001-cart/spec.md");
        spec.Selected.Origin.Should().Be(ProjectImportActivation.Origin);
        spec.Selected.Authority.Should().Be("Unknown", "imported is not approved");
        (await State(WorkspaceArtifactType.Plan)).Status.Should().Be(ExplorerArtifactStatus.Loaded);
        (await State(WorkspaceArtifactType.DataModel)).Status.Should().Be(ExplorerArtifactStatus.Empty, "a role the archive lacks stays empty (neutral)");

        record.SourceSnapshotId.Should().Be(snapshot, "the artifacts and the snapshot reference the same import");
        record.ArchiveSha256.Should().Be(preview.Archive.Sha256);
        _repository.SddLifecycle.Revisions.Where(r => r.Origin == ProjectImportActivation.Origin)
            .Should().OnlyContain(r => r.ProjectImportId == preview.ImportId && r.WorkspaceScope == "import:" + preview.ImportId);
        _repository.Get(WorkspaceArtifactType.Specification)!.Text.Should().Contain("FR-001", "session readers see the import's selected documents");
    }

    [Fact]
    public async Task SourceOnlyImport_IsTheCurrentProject_WithNoArtifacts()
    {
        var record = Activate(Preview(new string('b', 64), source: true), ProjectImportSourceState.Created, Guid.NewGuid());

        record.ArtifactDocumentCount.Should().Be(0);
        record.SourceState.Should().Be("Created");
        var state = await State(WorkspaceArtifactType.Specification);
        state.Status.Should().Be(ExplorerArtifactStatus.Empty);
        state.HasProject.Should().BeTrue();
        ProjectImportScope.HasCurrentImport(JsonSerializer.Serialize(_repository.SddLifecycle)).Should().BeTrue("a source-only import is still saved and restored");
    }

    [Fact]
    public async Task TwoCandidatesForARole_AreBothImported_AndTheUserChooses()
    {
        var preview = Preview(new string('c', 64), source: false,
            ("specs/001-cart/spec.md", Spec), ("specs/002-checkout/spec.md", Spec.Replace("Cart", "Checkout")), ("specs/001-cart/plan.md", Plan));

        var record = Activate(preview);

        record.AmbiguousRoles.Should().Equal("Specification");
        var spec = await State(WorkspaceArtifactType.Specification);
        spec.Status.Should().Be(ExplorerArtifactStatus.SelectionRequired, "never the first or the latest");
        spec.Artifacts.Should().HaveCount(2);
        (await State(WorkspaceArtifactType.Plan)).Status.Should().Be(ExplorerArtifactStatus.Loaded, "an ambiguous role does not block the others");
        _repository.Get(WorkspaceArtifactType.Specification).Should().BeNull();
    }

    [Fact]
    public async Task SecondImport_ReplacesTheFirst_WithoutCarryingOverItsDocuments()
    {
        Activate(Preview(new string('1', 64), source: false, ("v1/spec.md", Spec), ("v1/plan.md", Plan)));
        var v2 = Preview(new string('2', 64), source: false, ("v2/spec.md", Spec.Replace("FR-001", "FR-002")));

        Activate(v2);

        (await State(WorkspaceArtifactType.Plan)).Status.Should().Be(ExplorerArtifactStatus.Empty, "v1's plan never mixes into v2");
        _repository.Get(WorkspaceArtifactType.Plan).Should().BeNull();
        (await State(WorkspaceArtifactType.Specification)).Content.Should().Contain("FR-002");
        _repository.SddLifecycle.ProjectImports.Should().HaveCount(2, "the earlier import stays as history");
        _repository.SddLifecycle.CurrentProjectImportId.Should().Be(v2.ImportId);
    }

    [Fact]
    public async Task ReimportingTheSameArchive_DoesNotDuplicateRevisions_AndKeepsTheUsersChoice()
    {
        var preview = Preview(new string('d', 64), source: false, ("a/spec.md", Spec), ("b/spec.md", Spec.Replace("Cart", "Wish list")));
        Activate(preview);
        var chosen = (await State(WorkspaceArtifactType.Specification)).Artifacts[1];
        _explorers.Select(WorkspaceArtifactType.Specification, chosen.Id);
        var revisions = _repository.SddLifecycle.Revisions.Count;

        Activate(preview with { StagingId = Guid.NewGuid() });

        _repository.SddLifecycle.Revisions.Should().HaveCount(revisions);
        (await State(WorkspaceArtifactType.Specification)).Selected!.Id.Should().Be(chosen.Id);
        _repository.SddLifecycle.ProjectImports.Should().ContainSingle();
    }

    [Fact]
    public async Task ManualDocuments_AreKept_AndReturnWhenTheImportIsClosed()
    {
        _explorers.Import(new ArtifactImportRequest(WorkspaceArtifactType.Plan, "# My manual plan", "manual-plan.md", "Paste"));
        Activate(Preview(new string('e', 64), source: false, ("spec.md", Spec)));

        (await State(WorkspaceArtifactType.Plan)).Status.Should().Be(ExplorerArtifactStatus.Empty, "the manual workspace is not part of the imported project");

        _activation.Close();

        _explorers.CurrentScope.Should().BeNull();
        (await State(WorkspaceArtifactType.Plan)).Content.Should().Contain("My manual plan", "closing never destroys manual artifacts");
        (await State(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.Empty);
        _repository.Get(WorkspaceArtifactType.Plan)!.Text.Should().Contain("My manual plan");
        _repository.Get(WorkspaceArtifactType.Specification).Should().BeNull();
    }

    [Fact]
    public async Task SampleProjectAndImport_AreMutuallyExclusive()
    {
        _samples.RegisterProject("autorisasjon");
        _samples.SetProjectSpecification("autorisasjon", "# Sample spec");
        _repository.ProjectName = "autorisasjon";
        var preview = Preview(new string('f', 64), source: false, ("spec.md", Spec));

        Activate(preview);

        _repository.ProjectName.Should().BeNull("the import replaces the selected Sample Project");
        (await State(WorkspaceArtifactType.Specification)).Content.Should().Contain("FR-001");

        _repository.ProjectName = "autorisasjon";

        _repository.SddLifecycle.CurrentProjectImportId.Should().BeNull("selecting a Sample Project leaves the import");
        _explorers.CurrentScope.Should().Be("autorisasjon");
        (await State(WorkspaceArtifactType.Specification)).Content.Should().Contain("Sample spec");
        _repository.Get(WorkspaceArtifactType.Specification).Should().BeNull("an import's documents never stay behind as session artifacts");
    }

    [Fact]
    public async Task PersistedLifecycle_RestoresTheImportedProject()
    {
        var preview = Preview(new string('9', 64), source: false, ("spec.md", Spec), ("plan.md", Plan));
        Activate(preview);
        var json = JsonSerializer.Serialize(_repository.SddLifecycle);
        var restored = new WorkspaceArtifactRepository();
        var resolver = new MockSampleProjectDocumentResolver { Repository = restored };
        // The restore service sets the persisted session artifacts first, then the lifecycle.
        restored.Set(WorkspaceArtifactType.Specification, _repository.Get(WorkspaceArtifactType.Specification)!.Text, "spec.md");

        restored.RestoreSddLifecycle(json);

        var explorers = new ArtifactExplorerContext(restored, resolver, resolver);
        explorers.CurrentScope.Should().Be("import:" + preview.ImportId);
        (await explorers.GetStateAsync(WorkspaceArtifactType.Plan)).Status.Should().Be(ExplorerArtifactStatus.Loaded);
        restored.SddLifecycle.Revisions.Where(r => r.Role == "Specification").Should().ContainSingle("restoring never duplicates the import's documents into another scope");
    }

    [Fact]
    public void RetriedSource_UpdatesOnlyTheSourceProvenance()
    {
        var preview = Preview(new string('7', 64), source: true, ("spec.md", Spec));
        var record = Activate(preview, ProjectImportSourceState.NotCreated);
        var snapshot = Guid.NewGuid();

        _activation.UpdateSource(preview.ImportId, new ProjectImportSourceResult { State = ProjectImportSourceState.Created, SnapshotId = snapshot, EnvironmentId = "dev" });

        record.SourceState.Should().Be("Created");
        record.SourceSnapshotId.Should().Be(snapshot);
        record.DetectedRoles.Should().Equal("Specification");
    }
}
