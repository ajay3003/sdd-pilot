using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.Analysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.Analysis;

/// <summary>
/// Builder tests for Analysis page models.
/// Verifies each page returns proper readiness status based on workspace artifacts.
/// </summary>
public class AnalysisPageModelBuilderTests
{
    [Fact]
    public async Task SpecDrift_NoWorkspace_ReturnsBlocked()
    {
        await using var db = CreateInMemoryDb();
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new SpecDriftPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<SpecDriftPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(AnalysisStatus.Blocked, model.ReadinessStatus);
        Assert.False(model.Summary.CanRun);
        Assert.NotEmpty(model.MissingInputs);
    }

    [Fact]
    public async Task ImpactAnalysis_NoWorkspace_ReturnsBlocked()
    {
        await using var db = CreateInMemoryDb();
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new ImpactAnalysisPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<ImpactAnalysisPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(AnalysisStatus.Blocked, model.ReadinessStatus);
        Assert.False(model.Summary.CanRun);
    }

    [Fact]
    public async Task RequirementsTraceability_NoWorkspace_ReturnsBlocked()
    {
        await using var db = CreateInMemoryDb();
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new RequirementsTraceabilityPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<RequirementsTraceabilityPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(AnalysisStatus.Blocked, model.ReadinessStatus);
        Assert.False(model.Summary.CanRun);
    }

    [Fact]
    public async Task ImplementationReview_NoWorkspace_ReturnsBlocked()
    {
        await using var db = CreateInMemoryDb();
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new ImplementationReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<ImplementationReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(AnalysisStatus.Blocked, model.ReadinessStatus);
        Assert.False(model.Summary.CanRun);
        Assert.NotEmpty(model.MissingInputs);
    }

    [Fact]
    public async Task ImplementationTraceability_NoWorkspace_ReturnsBlocked()
    {
        await using var db = CreateInMemoryDb();
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new ImplementationTraceabilityPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<ImplementationTraceabilityPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(AnalysisStatus.Blocked, model.ReadinessStatus);
        Assert.False(model.Summary.CanRun);
    }

    [Fact]
    public async Task ImplementationReview_WithSpecification_ReturnsReady()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = await CreateWorkspaceAsync(db);
        await CreateArtifactAsync(db, workspaceId, ArtifactType.Specification);

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new ImplementationReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<ImplementationReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(AnalysisStatus.Ready, model.ReadinessStatus);
        Assert.True(model.Summary.CanRun);
        Assert.Empty(model.MissingInputs);
    }

    [Fact]
    public async Task ImplementationReview_WithSavedNonCurrentWorkspace_ReturnsBlocked()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = await CreateWorkspaceAsync(db, isCurrent: false);
        await CreateArtifactAsync(db, workspaceId, ArtifactType.Specification);

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new ImplementationReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<ImplementationReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(AnalysisStatus.Blocked, model.ReadinessStatus);
        Assert.Contains("active workspace", model.MissingInputs);
        Assert.False(model.Summary.CanRun);
    }

    [Fact]
    public async Task ImplementationReview_WithCurrentWorkspaceButNoArtifacts_ReturnsBlocked()
    {
        await using var db = CreateInMemoryDb();
        await CreateWorkspaceAsync(db);

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new ImplementationReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<ImplementationReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Equal(AnalysisStatus.Blocked, model.ReadinessStatus);
        Assert.Contains("specification for context", model.MissingInputs);
        Assert.False(model.Summary.CanRun);
    }

    [Fact]
    public async Task AllAnalysisPages_ReturnAnalysisPageModel()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = await CreateWorkspaceAsync(db);
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);

        var specDriftBuilder = new SpecDriftPageModelBuilder(
            db, artifactStatus, NullLogger<SpecDriftPageModelBuilder>.Instance);
        var impactBuilder = new ImpactAnalysisPageModelBuilder(
            db, artifactStatus, NullLogger<ImpactAnalysisPageModelBuilder>.Instance);
        var reqTraceBuilder = new RequirementsTraceabilityPageModelBuilder(
            db, artifactStatus, NullLogger<RequirementsTraceabilityPageModelBuilder>.Instance);
        var implReviewBuilder = new ImplementationReviewPageModelBuilder(
            db, artifactStatus, NullLogger<ImplementationReviewPageModelBuilder>.Instance);
        var implTraceBuilder = new ImplementationTraceabilityPageModelBuilder(
            db, artifactStatus, NullLogger<ImplementationTraceabilityPageModelBuilder>.Instance);

        var specDriftModel = await specDriftBuilder.BuildPageModelAsync();
        var impactModel = await impactBuilder.BuildPageModelAsync();
        var reqTraceModel = await reqTraceBuilder.BuildPageModelAsync();
        var implReviewModel = await implReviewBuilder.BuildPageModelAsync();
        var implTraceModel = await implTraceBuilder.BuildPageModelAsync();

        Assert.NotNull(specDriftModel);
        Assert.NotNull(impactModel);
        Assert.NotNull(reqTraceModel);
        Assert.NotNull(implReviewModel);
        Assert.NotNull(implTraceModel);

        Assert.IsType<AnalysisPageModel>(specDriftModel);
        Assert.IsType<AnalysisPageModel>(impactModel);
        Assert.IsType<AnalysisPageModel>(reqTraceModel);
        Assert.IsType<AnalysisPageModel>(implReviewModel);
        Assert.IsType<AnalysisPageModel>(implTraceModel);
    }

    [Fact]
    public async Task AnalysisPages_EmptyIsNotFail()
    {
        await using var db = CreateInMemoryDb();
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new ImplementationReviewPageModelBuilder(
            db, artifactStatus, NullLogger<ImplementationReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        // Blocked is not Fail - it's a normal expected state when prerequisites are missing
        Assert.NotEqual(AnalysisStatus.Fail, model.ReadinessStatus);
        Assert.True(
            model.ReadinessStatus == AnalysisStatus.Blocked ||
            model.ReadinessStatus == AnalysisStatus.Empty,
            "Missing inputs should result in Blocked or Empty, not Fail");
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
