using BirkNext.Api.Data;
using BirkNext.Api.Services;
using BirkNext.Api.Services.Library;
using Microsoft.EntityFrameworkCore;
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
    public async Task CreateTestScenario_NoWorkspace_ReturnsBlockedNotFail()
    {
        await using var db = CreateInMemoryDb();
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new CreateTestScenarioPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<CreateTestScenarioPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(LibraryStatus.Blocked, model.ReadinessStatus);
        Assert.NotEmpty(model.MissingInputs);
        Assert.False(model.Summary.HasAvailableActions);
    }

    [Fact]
    public async Task SampleProjects_NoWorkspace_ReturnsReadySamples()
    {
        await using var db = CreateInMemoryDb();
        var builder = new SampleProjectsPageModelBuilder(
            db,
            NullLogger<SampleProjectsPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(LibraryStatus.Ready, model.ReadinessStatus);
        Assert.Equal(3, model.Items.Count);
        Assert.True(model.Summary.HasAvailableActions);
    }

    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }
}
