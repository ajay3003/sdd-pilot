using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.Library;

public class LibraryPageModelBuilderTests
{
    [Fact]
    public async Task QAArtifactLibrary_NoWorkspace_ReturnsEmptyNotFail()
    {
        await using var db = CreateInMemoryDb();
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QAArtifactLibraryPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QAArtifactLibraryPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(LibraryStatus.Empty, model.ReadinessStatus);
        Assert.Equal(0, model.Summary.TotalItems);
        Assert.DoesNotContain("No active workspace", model.Summary.StatusMessage);
    }

    [Fact]
    public async Task QAArtifactLibrary_WithWorkspaceButNoArtifacts_ReturnsEmpty()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = await CreateWorkspaceAsync(db);

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QAArtifactLibraryPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QAArtifactLibraryPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(LibraryStatus.Empty, model.ReadinessStatus);
        Assert.Equal(0, model.Summary.TotalItems);
        Assert.False(model.Summary.HasAvailableActions);
    }

    [Fact]
    public async Task QAArtifactLibrary_WithArtifacts_ReturnsReady()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = await CreateWorkspaceAsync(db);
        await CreateArtifactAsync(db, workspaceId);

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QAArtifactLibraryPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QAArtifactLibraryPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(LibraryStatus.Ready, model.ReadinessStatus);
        Assert.Equal(1, model.Summary.TotalItems);
        Assert.True(model.Summary.HasAvailableActions);
    }

    [Fact]
    public async Task QAArtifactLibrary_WithSavedNonCurrentWorkspace_ReturnsEmpty()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = await CreateWorkspaceAsync(db, isCurrent: false);
        await CreateArtifactAsync(db, workspaceId);

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QAArtifactLibraryPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QAArtifactLibraryPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(LibraryStatus.Empty, model.ReadinessStatus);
        Assert.Equal(0, model.Summary.TotalItems);
        Assert.False(model.Summary.HasAvailableActions);
    }

    [Fact]
    public async Task SampleProjects_NoSampleData_ReturnsEmpty()
    {
        // Create config that points to a non-existent directory
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("SampleProjects:BaseDirectory", "/nonexistent") })
            .Build();

        var catalog = new SampleProjectCatalogService(config);
        var builder = new SampleProjectsPageModelBuilder(
            catalog,
            NullLogger<SampleProjectsPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(LibraryStatus.Empty, model.ReadinessStatus);
        Assert.Equal(0, model.Items.Count);
        Assert.False(model.Summary.HasAvailableActions);
    }

    [Fact]
    public async Task SampleProjects_WithRealSampleData_DiscoversDynamically()
    {
        // Create config without explicit base directory to trigger auto-discovery
        var config = new ConfigurationBuilder().Build();

        var catalog = new SampleProjectCatalogService(config);
        var builder = new SampleProjectsPageModelBuilder(
            catalog,
            NullLogger<SampleProjectsPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        // Should discover actual projects from SampleData directory
        Assert.True(model.Items.Count > 0, $"Expected to discover projects, but found {model.Items.Count}");
        Assert.Equal(LibraryStatus.Ready, model.ReadinessStatus);
        Assert.True(model.Summary.HasAvailableActions);
    }

    [Fact]
    public async Task LibraryPageModel_EmptyIsNeverFail()
    {
        await using var db = CreateInMemoryDb();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QAArtifactLibraryPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QAArtifactLibraryPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        // Empty state is not an error
        Assert.NotEqual(LibraryStatus.Fail, model.ReadinessStatus);
        Assert.Equal(LibraryStatus.Empty, model.ReadinessStatus);
    }

    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static async Task<Guid> CreateWorkspaceAsync(AppDbContext db, bool isCurrent = true)
    {
        var workspace = new SavedWorkspace
        {
            Id = Guid.NewGuid(),
            UserId = "default-user",
            Name = "Test Workspace",
            ProjectName = "Test Project",
            IsCurrent = isCurrent,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.SavedWorkspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace.Id;
    }

    private static async Task CreateArtifactAsync(
        AppDbContext db,
        Guid workspaceId,
        ArtifactType type = ArtifactType.Specification)
    {
        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = type,
            Content = "# Test Artifact\nContent",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();
    }
}
