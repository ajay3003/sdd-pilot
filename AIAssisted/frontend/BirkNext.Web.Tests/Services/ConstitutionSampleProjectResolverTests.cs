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
        var mockApiService = new Mock<SampleProjectsApiService>(null!);
        var mockWorkspace = new Mock<IWorkspaceSessionService>();

        mockApiService.Setup(s => s.GetProjectsAsync())
            .ReturnsAsync(new List<SampleProjectDto>
            {
                new("autorisasjon", "Autorisasjon", "Authorization", "Auth module", "/path/a",
                    false, new[]
                    {
                        new SampleFileDto("constitution.md", true, null, null, null, false, false),
                        new SampleFileDto("spec.md", true, null, null, null, false, false),
                    }.ToList())
            });

        mockApiService.Setup(s => s.GetFileAsync("autorisasjon", "constitution.md"))
            .ReturnsAsync("# Constitution\nPP-01: Test");

        var resolver = new SampleProjectDocumentResolver(mockApiService.Object, mockWorkspace.Object);

        var result = await resolver.ResolveAsync("autorisasjon", ExplorerDocumentType.Constitution);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("# Constitution");
        result.ProjectSlug.Should().Be("autorisasjon");
        result.Filename.Should().Be("constitution.md");
    }

    [Fact]
    public async Task ResolveAsync_MissingDocument_ReturnsNotFoundNotFallback()
    {
        var mockApiService = new Mock<SampleProjectsApiService>(null!);
        var mockWorkspace = new Mock<IWorkspaceSessionService>();

        mockApiService.Setup(s => s.GetProjectsAsync())
            .ReturnsAsync(new List<SampleProjectDto>
            {
                new("autorisasjon", "Autorisasjon", "Authorization", "Auth module", "/path/a",
                    false, new[] { new SampleFileDto("spec.md", true, null, null, null, false, false) }.ToList())
            });

        var resolver = new SampleProjectDocumentResolver(mockApiService.Object, mockWorkspace.Object);

        var result = await resolver.ResolveAsync("autorisasjon", ExplorerDocumentType.Constitution);

        result.IsSuccess.Should().BeFalse();
        result.IsMissing.Should().BeTrue();
        result.Content.Should().BeNull();
        result.ErrorMessage.Should().Contain("not available");
    }

    [Fact]
    public async Task ResolveAsync_InvalidProject_ReturnsError()
    {
        var mockApiService = new Mock<SampleProjectsApiService>(null!);
        var mockWorkspace = new Mock<IWorkspaceSessionService>();

        mockApiService.Setup(s => s.GetProjectsAsync())
            .ReturnsAsync(new List<SampleProjectDto>());

        var resolver = new SampleProjectDocumentResolver(mockApiService.Object, mockWorkspace.Object);

        var result = await resolver.ResolveAsync("nonexistent", ExplorerDocumentType.Constitution);

        result.IsSuccess.Should().BeFalse();
        result.ProjectSlug.Should().BeNull();
    }

    [Fact]
    public void SetSelectedProject_UpdatesContext()
    {
        var mockApiService = new Mock<SampleProjectsApiService>(null!);
        var mockWorkspace = new Mock<IWorkspaceSessionService>();

        var resolver = new SampleProjectDocumentResolver(mockApiService.Object, mockWorkspace.Object);

        resolver.SetSelectedProject("autorisasjon");

        mockWorkspace.VerifySet(w => w.CurrentProject = "autorisasjon", Times.Once);
    }

    [Fact]
    public async Task GetAvailableProjectsAsync_ReturnsOnlySampleDataProjects()
    {
        var mockApiService = new Mock<SampleProjectsApiService>(null!);
        var mockWorkspace = new Mock<IWorkspaceSessionService>();

        var projects = new List<SampleProjectDto>
        {
            new("autorisasjon", "Autorisasjon", "Auth", "Auth module", "/path/a", false, []),
            new("person-module", "Person Module", "Data", "Person data", "/path/p", false, [])
        };

        mockApiService.Setup(s => s.GetProjectsAsync())
            .ReturnsAsync(projects);

        var resolver = new SampleProjectDocumentResolver(mockApiService.Object, mockWorkspace.Object);

        var available = await resolver.GetAvailableProjectsAsync();

        available.Should().HaveCount(2);
        available.Should().ContainSingle(p => p.Slug == "autorisasjon");
        available.Should().ContainSingle(p => p.Slug == "person-module");
    }

    [Fact]
    public async Task ResolveAsync_SwitchProjects_ClearsOldContent()
    {
        var mockApiService = new Mock<SampleProjectsApiService>(null!);
        var mockWorkspace = new Mock<IWorkspaceSessionService>();

        mockApiService.Setup(s => s.GetProjectsAsync())
            .ReturnsAsync(new List<SampleProjectDto>
            {
                new("projectA", "Project A", "A", "Project A", "/a", false,
                    new[] { new SampleFileDto("constitution.md", true, null, null, null, false, false) }.ToList()),
                new("projectB", "Project B", "B", "Project B", "/b", false,
                    new[] { new SampleFileDto("constitution.md", true, null, null, null, false, false) }.ToList())
            });

        mockApiService.Setup(s => s.GetFileAsync("projectA", "constitution.md"))
            .ReturnsAsync("# A Constitution");
        mockApiService.Setup(s => s.GetFileAsync("projectB", "constitution.md"))
            .ReturnsAsync("# B Constitution");

        var resolver = new SampleProjectDocumentResolver(mockApiService.Object, mockWorkspace.Object);

        var resultA = await resolver.ResolveAsync("projectA", ExplorerDocumentType.Constitution);
        resultA.Content.Should().Contain("A Constitution");

        var resultB = await resolver.ResolveAsync("projectB", ExplorerDocumentType.Constitution);
        resultB.Content.Should().Contain("B Constitution");
        resultB.Content.Should().NotContain("A Constitution");
    }

    /// <summary>
    /// REGRESSION TEST: Workspace cache must not override selected Sample Project
    /// Violation: If CurrentProject=B but Workspace contains cached content from A,
    /// the page would display A instead of B/constitution.md
    /// </summary>
    [Fact]
    public void WorkspaceCache_DoesNotOverrideSelectedProject_PolicyEnforcement()
    {
        var mockApiService = new Mock<SampleProjectsApiService>(null!);
        var mockWorkspace = new Mock<IWorkspaceSessionService>();

        // Setup: resolver will be called for ProjectB
        mockWorkspace.Setup(w => w.CurrentProject).Returns("projectB");

        mockApiService.Setup(s => s.GetProjectsAsync())
            .ReturnsAsync(new List<SampleProjectDto>
            {
                new("projectB", "Project B", "B", "Project B", "/b", false,
                    new[] { new SampleFileDto("constitution.md", true, null, null, null, false, false) }.ToList())
            });

        mockApiService.Setup(s => s.GetFileAsync("projectB", "constitution.md"))
            .ReturnsAsync("# B Constitution");

        var resolver = new SampleProjectDocumentResolver(mockApiService.Object, mockWorkspace.Object);

        // Policy requirement: resolver should be the source, not Workspace cache
        var selected = resolver.GetSelectedProject();
        selected.Should().Be("projectB");

        // Resolver should return B, regardless of what might be in Workspace cache
        var result = resolver.ResolveAsync("projectB", ExplorerDocumentType.Constitution).Result;
        result.Content.Should().Contain("B Constitution");
    }
}
