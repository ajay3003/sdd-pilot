using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

public class WorkspaceSessionRestoreServiceTests
{
    private readonly Mock<IWorkspaceStateManager> _stateManager = new();
    private readonly Mock<IReviewContextProvider> _reviewContextProvider = new();
    private readonly SampleProjectsHttpHandler _httpHandler = new();
    private readonly WorkspaceArtifactRepository _workspace = new();
    private WorkspaceSessionRestoreService _service = null!;

    [Fact]
    public async Task RestoreLegacySampleProjectWorkspace_SkipsPersistedArtifactCopies()
    {
        // Arrange: Legacy SavedWorkspace containing Sample Project identity + old artifact copies
        ArrangeSampleProjectCatalog("autorisasjon", "Autorisasjon");

        var legacyWorkspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Autorisasjon",
            ProjectName = "autorisasjon",
            Artifacts = new()
            {
                new() { ArtifactType = "Constitution", Content = "old constitution", FileName = "constitution.md" },
                new() { ArtifactType = "Specification", Content = "old spec", FileName = "spec.md" },
                new() { ArtifactType = "Plan", Content = "old plan", FileName = "plan.md" },
                new() { ArtifactType = "Tasks", Content = "old tasks", FileName = "tasks.md" },
                new() { ArtifactType = "DataModel", Content = "old datamodel", FileName = "data-model.md" },
            }
        };

        // Act
        await _service.RestoreWorkspaceAsync(legacyWorkspace);

        // Assert: Identity restored, persisted artifacts NOT restored
        _workspace.CurrentProject.Should().Be("autorisasjon");
        _workspace.GetAllArtifacts().Should().BeEmpty("Sample Project artifacts should not be restored");
    }
    }

    [Fact]
    public async Task RestoreCatalogFailure_PreservesIdentity_WithholdsArtifacts()
    {
        // Arrange: Catalog fails but workspace has Sample Project identity + artifacts
        _httpHandler.FailGetProjects();

        var unknownWorkspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Unknown Type",
            ProjectName = "autorisasjon",
            Artifacts = new()
            {
                new() { ArtifactType = "Constitution", Content = "old constitution", FileName = "constitution.md" },
                new() { ArtifactType = "Specification", Content = "old spec", FileName = "spec.md" },
            }
        };

        // Act
        await _service.RestoreWorkspaceAsync(unknownWorkspace);

        // Assert: Identity preserved but artifacts withheld (safety on Unknown classification)
        _workspace.CurrentProject.Should().Be("autorisasjon");
        _workspace.GetAllArtifacts().Should().BeEmpty("Artifacts withheld when workspace type cannot be safely determined");
    }

    [Fact]
    public async Task RestoreGenericWorkspace_RestoresPersistedArtifactsCopiesNormally()
    {
        // Arrange: Generic workspace with manual artifacts (ProjectName does not match Sample Project)
        ArrangeSampleProjectCatalog("autorisasjon", "Autorisasjon");

        var genericWorkspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Manual Analysis",
            ProjectName = "my-custom-analysis",
            Artifacts = new()
            {
                new() { ArtifactType = "Constitution", Content = "my constitution", FileName = "constitution.md" },
                new() { ArtifactType = "Specification", Content = "my spec", FileName = "spec.md" },
            }
        };

        // Act
        await _service.RestoreWorkspaceAsync(genericWorkspace);

        // Assert: Both identity and artifacts restored for generic workspace
        _workspace.CurrentProject.Should().Be("my-custom-analysis");
        _workspace.GetAllArtifacts().Should().HaveCount(2, "Generic workspace artifacts should be restored normally");
        _workspace.Constitution?.Content.Should().Be("my constitution");
        _workspace.Specification?.Content.Should().Be("my spec");
    }

    [Fact]
    public async Task RestoreNullProjectIdentity_TreatsAsGenericWorkspace_RestoresArtifacts()
    {
        // Arrange: SavedWorkspace with no project identity (null) but has artifacts
        ArrangeSampleProjectCatalog("autorisasjon", "Autorisasjon");

        var genericWorkspace = new SavedWorkspaceDto
        {
            Id = Guid.NewGuid(),
            Name = "Manual Analysis",
            ProjectName = null,
            Artifacts = new()
            {
                new() { ArtifactType = "Plan", Content = "my plan", FileName = "plan.md" },
            }
        };

        // Act
        await _service.RestoreWorkspaceAsync(genericWorkspace);

        // Assert: Null identity preserved, artifact restored
        _workspace.CurrentProject.Should().BeNull();
        _workspace.GetAllArtifacts().Should().HaveCount(1);
        _workspace.Plan?.Content.Should().Be("my plan");
    }

    private void ArrangeSampleProjectCatalog(string slug, string name)
    {
        var project = new SampleProjectDto(
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

        _httpHandler.SetProjects(project);
        SetupService();
    }

    private void SetupService()
    {
        var client = new HttpClient(_httpHandler)
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var sampleProjects = new SampleProjectsApiService(client);

        _service = new WorkspaceSessionRestoreService(
            _workspace,
            _stateManager.Object,
            _reviewContextProvider.Object,
            sampleProjects,
            NullLogger<WorkspaceSessionRestoreService>.Instance);

        _reviewContextProvider.Setup(r => r.RebuildAsync()).Returns(Task.CompletedTask);
    }

    public sealed class SampleProjectsHttpHandler : HttpMessageHandler
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

        public void FailGetProjects()
        {
            _throwOnGetProjects = true;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');

            if (request.Method == HttpMethod.Get && path == "api/sample-projects")
            {
                if (_throwOnGetProjects)
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("Catalog unavailable"));
                return Json(_projects.Values.ToList());
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json<T>(T value)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(value, JsonOptions);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
