using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// A workspace restored from its bootstrap manifest knows which documents it holds before their bodies arrive: navigation (role presence)
/// is answered from the manifest without loading anything, and a reader of documents loads them first — or reports the failure, never "missing".
/// </summary>
public sealed class WorkspaceDocumentPresenceTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IWorkspacePersistenceApiService> _persistence = new() { CallBase = true };
    private readonly ArtifactExplorerContext _explorers;

    public WorkspaceDocumentPresenceTests()
    {
        _repository.RestoreSddLifecycle(System.Text.Json.JsonSerializer.Serialize(new SddLifecycleState
        {
            CurrentProjectImportId = "imp",
            ProjectImports = [new SddProjectImportRecord { ImportId = "imp", ProjectName = "Shop", ArchiveFileName = "shop.zip" }],
            Revisions =
            [
                new SddArtifactRevision { Role = nameof(WorkspaceArtifactType.Specification), FileName = "specs/001/spec.md", Fingerprint = "a", ContentLength = 40, WorkspaceScope = "import:imp", Origin = "ProjectImport", ProjectImportId = "imp" },
                new SddArtifactRevision { Role = nameof(WorkspaceArtifactType.Plan), FileName = "specs/001/plan.md", Fingerprint = "b", ContentLength = 30, WorkspaceScope = "import:imp", Origin = "ProjectImport", ProjectImportId = "imp" },
            ],
        }));
        _repository.MarkDocumentsDeferred(Workspace);
        var services = new ServiceCollection().AddSingleton(_persistence.Object).BuildServiceProvider();
        var hydration = new WorkspaceDocumentHydration(_repository, services, NullLogger<WorkspaceDocumentHydration>.Instance);
        var samples = new MockSampleProjectDocumentResolver { Repository = _repository };
        _explorers = new ArtifactExplorerContext(_repository, samples, samples, documents: hydration);
    }

    [Fact]
    public async Task Role_presence_comes_from_the_manifest_without_loading_any_body()
    {
        var present = await _explorers.GetRolePresenceAsync();

        present.Should().BeEquivalentTo([WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan]);
        _persistence.Verify(p => p.LoadDocumentsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_document_reader_loads_the_bodies_first()
    {
        var spec = _repository.SddLifecycle.Revisions[0].RevisionId;
        _persistence.Setup(p => p.LoadDocumentsAsync(Workspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkspaceDocumentsDto { Revisions = { [spec] = "# Shop specification\n\n### FR-001: Orders" } });

        var state = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        state.Content.Should().Contain("FR-001");
    }

    [Fact]
    public async Task A_failed_load_is_an_error_not_missing_documents()
    {
        _persistence.Setup(p => p.LoadDocumentsAsync(Workspace, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down", null, System.Net.HttpStatusCode.BadGateway));

        var state = await _explorers.GetStateAsync(WorkspaceArtifactType.Specification);

        state.Status.Should().Be(ExplorerArtifactStatus.Error);
        state.Error.Should().Contain("could not be loaded").And.Contain("still stored");
    }
}
