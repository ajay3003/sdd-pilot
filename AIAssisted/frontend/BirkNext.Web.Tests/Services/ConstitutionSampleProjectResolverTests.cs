using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

public sealed class ConstitutionSampleProjectResolverTests
{
    [Fact]
    public async Task ResolveAsync_Constitution_ReturnsSelectedProjectFile()
    {
        var resolver = CreateResolver(
            [
                Project("autorisasjon", "Autorisasjon", "constitution.md", "spec.md")
            ],
            new Dictionary<(string, string), string>
            {
                [("autorisasjon", "constitution.md")] = "# Constitution\nPP-01: Test",
            });

        var result = await resolver.ResolveAsync("autorisasjon", ExplorerDocumentType.Constitution);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("# Constitution");
        result.ProjectSlug.Should().Be("autorisasjon");
        result.Filename.Should().Be("constitution.md");
    }

    [Fact]
    public async Task ResolveAsync_MissingDocument_ReturnsNotFoundNotFallback()
    {
        var resolver = CreateResolver(
            [
                Project("autorisasjon", "Autorisasjon", "spec.md")
            ],
            new Dictionary<(string, string), string>());

        var result = await resolver.ResolveAsync("autorisasjon", ExplorerDocumentType.Constitution);

        result.IsSuccess.Should().BeFalse();
        result.IsMissing.Should().BeTrue();
        result.Content.Should().BeNull();
        result.ErrorMessage.Should().Contain("not available");
    }

    [Fact]
    public async Task ResolveAsync_InvalidProject_ReturnsError()
    {
        var resolver = CreateResolver([], new Dictionary<(string, string), string>());

        var result = await resolver.ResolveAsync("nonexistent", ExplorerDocumentType.Constitution);

        result.IsSuccess.Should().BeFalse();
        result.ProjectSlug.Should().BeNull();
    }

    [Fact]
    public void SetSelectedProject_UpdatesContext()
    {
        var api = new SampleProjectsApiService(new HttpClient(new SampleProjectsHandler([], new Dictionary<(string, string), string>()))
        {
            BaseAddress = new Uri("http://localhost/")
        });
        var mockWorkspace = new Mock<IWorkspaceSessionService>();
        var resolver = new SampleProjectDocumentResolver(api, mockWorkspace.Object);

        resolver.SetSelectedProject("autorisasjon");

        mockWorkspace.VerifySet(w => w.CurrentProject = "autorisasjon", Times.Once);
    }

    [Fact]
    public async Task GetAvailableProjectsAsync_ReturnsOnlySampleDataProjects()
    {
        var projects = new List<SampleProjectDto>
        {
            Project("autorisasjon", "Autorisasjon"),
            Project("person-module", "Person Module"),
        };
        var resolver = CreateResolver(projects, new Dictionary<(string, string), string>());

        var available = await resolver.GetAvailableProjectsAsync();

        available.Should().HaveCount(2);
        available.Should().ContainSingle(p => p.Slug == "autorisasjon");
        available.Should().ContainSingle(p => p.Slug == "person-module");
    }

    [Fact]
    public async Task ResolveAsync_SwitchProjects_ClearsOldContent()
    {
        var resolver = CreateResolver(
            [
                Project("projectA", "Project A", "constitution.md"),
                Project("projectB", "Project B", "constitution.md"),
            ],
            new Dictionary<(string, string), string>
            {
                [("projectA", "constitution.md")] = "# A Constitution",
                [("projectB", "constitution.md")] = "# B Constitution",
            });

        var resultA = await resolver.ResolveAsync("projectA", ExplorerDocumentType.Constitution);
        resultA.Content.Should().Contain("A Constitution");

        var resultB = await resolver.ResolveAsync("projectB", ExplorerDocumentType.Constitution);
        resultB.Content.Should().Contain("B Constitution");
        resultB.Content.Should().NotContain("A Constitution");
    }

    [Fact]
    public async Task WorkspaceCache_DoesNotOverrideSelectedProject_PolicyEnforcement()
    {
        var mockWorkspace = new Mock<IWorkspaceSessionService>();
        mockWorkspace.Setup(w => w.CurrentProject).Returns("projectB");

        var resolver = CreateResolver(
            [
                Project("projectB", "Project B", "constitution.md")
            ],
            new Dictionary<(string, string), string>
            {
                [("projectB", "constitution.md")] = "# B Constitution",
            },
            mockWorkspace.Object);

        resolver.GetSelectedProject().Should().Be("projectB");

        var result = await resolver.ResolveAsync("projectB", ExplorerDocumentType.Constitution);
        result.Content.Should().Contain("B Constitution");
    }

    private static SampleProjectDocumentResolver CreateResolver(
        IReadOnlyList<SampleProjectDto> projects,
        IReadOnlyDictionary<(string ProjectSlug, string Filename), string> files,
        IWorkspaceSessionService? workspace = null)
    {
        var client = new HttpClient(new SampleProjectsHandler(projects, files))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        return new SampleProjectDocumentResolver(
            new SampleProjectsApiService(client),
            workspace ?? Mock.Of<IWorkspaceSessionService>());
    }

    private static SampleProjectDto Project(string slug, string name, params string[] filenames) =>
        new(
            slug,
            name,
            "test",
            $"Test project {name}",
            $"/SampleData/{slug}",
            false,
            filenames.Select(filename => new SampleFileDto(filename, true, null, null, null, true, false)).ToList());

    private sealed class SampleProjectsHandler(
        IReadOnlyList<SampleProjectDto> projects,
        IReadOnlyDictionary<(string ProjectSlug, string Filename), string> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Equals("/api/sample-projects", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(projects));
            }

            if (path.Contains("/api/sample-projects/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/file", StringComparison.OrdinalIgnoreCase))
            {
                var slug = Uri.UnescapeDataString(path.Split('/')[3]);
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri?.Query ?? string.Empty);
                var filename = query["filename"] ?? string.Empty;

                if (files.TryGetValue((slug, filename), out var content))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(content, Encoding.UTF8, "text/plain")
                    });

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse<T>(T value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
