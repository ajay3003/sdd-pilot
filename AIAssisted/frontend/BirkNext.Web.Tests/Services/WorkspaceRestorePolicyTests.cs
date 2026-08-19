using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Tests for WorkspaceSessionRestoreService classification and artifact restoration policies.
/// </summary>
public class WorkspaceRestorePolicyTests
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly Mock<IWorkspaceStateManager> _stateManager = new();
    private readonly Mock<IReviewContextProvider> _reviewContextProvider = new();
    private WorkspaceSessionRestoreService _service = null!;

    [Fact]
    public async Task RestoreLegacySampleProjectWorkspace_SkipsPersistedArtifactCopies()
    {
        // Arrange: Legacy SavedWorkspace with Sample Project identity + five old artifact copies
        var handler = new SampleProjectHttpHandler();
        handler.SetProjects(CreateSampleProject("autorisasjon", "Autorisasjon"));
        InitializeService(handler);

        var legacyWorkspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Autorisasjon",
            ProjectName = "autorisasjon",
            Artifacts = new List<SavedWorkspaceArtifactDto>
            {
                new() { ArtifactType = "Constitution", Content = "old constitution text", FileName = "constitution.md" },
                new() { ArtifactType = "Specification", Content = "old spec text", FileName = "spec.md" },
                new() { ArtifactType = "Plan", Content = "old plan text", FileName = "plan.md" },
                new() { ArtifactType = "Tasks", Content = "old tasks text", FileName = "tasks.md" },
                new() { ArtifactType = "DataModel", Content = "old datamodel text", FileName = "data-model.md" },
            }
        };

        // Act
        await _service.RestoreWorkspaceAsync(legacyWorkspace);

        // Assert: Identity restored, persisted artifact copies NOT restored
        _workspace.CurrentProject.Should().Be("autorisasjon");
        _workspace.GetAllArtifacts().Should().BeEmpty("Sample Project legacy artifact copies should not be restored");
    }

    [Fact]
    public async Task RestoreCatalogFailure_PreservesIdentity_WithholdsArtifacts()
    {
        // Arrange: Catalog fails + persisted Sample Project identity + legacy artifacts
        var handler = new SampleProjectHttpHandler();
        handler.FailGetProjects();  // Simulate catalog failure

        InitializeService(handler);

        var unknownWorkspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Unknown Type",
            ProjectName = "autorisasjon",
            Artifacts = new List<SavedWorkspaceArtifactDto>
            {
                new() { ArtifactType = "Constitution", Content = "artifact", FileName = "constitution.md" },
                new() { ArtifactType = "Specification", Content = "artifact", FileName = "spec.md" },
            }
        };

        // Act
        await _service.RestoreWorkspaceAsync(unknownWorkspace);

        // Assert: Identity preserved but artifacts withheld (Unknown classification → safety)
        _workspace.CurrentProject.Should().Be("autorisasjon");
        _workspace.GetAllArtifacts().Should().BeEmpty("Artifacts withheld when workspace type cannot be determined safely");
    }

    [Fact]
    public async Task RestoreGenericWorkspace_RestoresPersistedArtifactsNormally()
    {
        // Arrange: Generic workspace with manual artifacts + catalog returns no matching slug
        var handler = new SampleProjectHttpHandler();
        handler.SetProjects(CreateSampleProject("autorisasjon", "Autorisasjon")); // Different slug than ProjectName

        InitializeService(handler);

        var genericWorkspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Manual Analysis",
            ProjectName = "my-custom-analysis",  // Does not match any Sample Project
            Artifacts = new List<SavedWorkspaceArtifactDto>
            {
                new() { ArtifactType = "Constitution", Content = "my constitution", FileName = "constitution.md" },
                new() { ArtifactType = "Specification", Content = "my spec", FileName = "spec.md" },
            }
        };

        // Act
        await _service.RestoreWorkspaceAsync(genericWorkspace);

        // Assert: Both identity and artifacts restored for generic workspace
        _workspace.ProjectName.Should().Be("my-custom-analysis");
        _workspace.CurrentProject.Should().Be("my-custom-analysis");
        _workspace.GetAllArtifacts().Should().HaveCount(2, "Generic workspace artifacts should be restored normally");
        _workspace.Constitution?.Text.Should().Be("my constitution");
        _workspace.Specification?.Text.Should().Be("my spec");
    }

    [Fact]
    public async Task RestoreNullProjectIdentity_TreatsAsGenericWorkspace_RestoresArtifacts()
    {
        // Arrange: SavedWorkspace with null project identity + legitimate manual artifact
        var handler = new SampleProjectHttpHandler();
        handler.SetProjects(CreateSampleProject("autorisasjon", "Autorisasjon"));

        InitializeService(handler);

        var genericWorkspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Manual Analysis",
            ProjectName = null,  // Null identity
            Artifacts = new List<SavedWorkspaceArtifactDto>
            {
                new() { ArtifactType = "Plan", Content = "my plan", FileName = "plan.md" },
            }
        };

        // Act
        await _service.RestoreWorkspaceAsync(genericWorkspace);

        // Assert: Null identity preserved, legitimate artifact restored
        _workspace.CurrentProject.Should().BeNull();
        _workspace.ProjectName.Should().BeNull();
        _workspace.GetAllArtifacts().Should().HaveCount(1);
        _workspace.Plan?.Text.Should().Be("my plan");
    }

    private void InitializeService(SampleProjectHttpHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var sampleProjects = new SampleProjectsApiService(client);

        _service = new WorkspaceSessionRestoreService(
            _workspace,
            _stateManager.Object,
            _reviewContextProvider.Object,
            sampleProjects,
            NullLogger<WorkspaceSessionRestoreService>.Instance);

        _reviewContextProvider.Setup(r => r.RebuildAsync()).Returns(Task.CompletedTask);
    }

    private static SampleProjectDto CreateSampleProject(string slug, string name)
    {
        return new SampleProjectDto(
            slug,
            name,
            slug.ToUpper(),
            $"{name} description",
            $"C:\\SampleData\\{slug}",
            true,
            new[]
            {
                new SampleFileDto("constitution.md", true, "Constitution", "", "", true, false),
                new SampleFileDto("spec.md", true, "Specification", "", "", true, false),
                new SampleFileDto("plan.md", true, "Plan", "", "", true, false),
                new SampleFileDto("tasks.md", true, "Tasks", "", "", true, false),
                new SampleFileDto("data-model.md", true, "DataModel", "", "", true, false),
            });
    }

    /// <summary>
    /// Stub HTTP handler for SampleProjectsApiService.
    /// </summary>
    private sealed class SampleProjectHttpHandler : HttpMessageHandler
    {
        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
            new(System.Text.Json.JsonSerializerDefaults.Web);
        private readonly Dictionary<string, SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);
        private bool _throwOnGetProjects = false;

        public void SetProjects(params SampleProjectDto[] projects)
        {
            foreach (var project in projects)
                _projects[project.Slug] = project;
        }

        public void FailGetProjects() => _throwOnGetProjects = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath.Trim('/');

            if (request.Method == HttpMethod.Get && path == "api/sample-projects")
            {
                if (_throwOnGetProjects)
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("Catalog unavailable"));

                var json = System.Text.Json.JsonSerializer.Serialize(_projects.Values.ToList(), JsonOptions);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
