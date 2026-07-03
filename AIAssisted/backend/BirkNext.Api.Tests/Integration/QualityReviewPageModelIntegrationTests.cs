using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.QualityReview;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Integration;

/// <summary>
/// End-to-end integration tests for Quality Review page model workflow.
/// Verifies the complete artifact → model → API response pipeline.
/// </summary>
public class QualityReviewPageModelIntegrationTests
{
    private AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task ArtifactUpload_Updates_PageModel_Correctly()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = Guid.NewGuid();

        // Setup workspace
        var workspace = new SavedWorkspace
        {
            Id = workspaceId,
            UserId = "test-user",
            Name = "Test",
            ProjectName = "Test"
        };
        db.SavedWorkspaces.Add(workspace);
        await db.SaveChangesAsync();

        // No artifacts initially
        var artifactStatus1 = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var status1 = await artifactStatus1.GetStatusAsync(workspaceId);
        Assert.False(status1.HasSpecification);

        // Add specification artifact
        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = ArtifactType.Specification,
            Content = "# Specification",
            ContentHash = "hash",
            LastModified = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        // Artifact status should update
        var artifactStatus2 = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var status2 = await artifactStatus2.GetStatusAsync(workspaceId);
        Assert.True(status2.HasSpecification);
    }

    [Fact]
    public async Task PageModel_Reflects_Available_Packs_Based_On_Artifacts()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = Guid.NewGuid();

        // Setup workspace with no artifacts
        var workspace = new SavedWorkspace
        {
            Id = workspaceId,
            UserId = "test-user",
            Name = "Test",
            ProjectName = "Test",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaces.Add(workspace);
        await db.SaveChangesAsync();

        // Build model with no artifacts
        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QualityReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QualityReviewPageModelBuilder>.Instance);

        var modelBefore = await builder.BuildPageModelAsync();

        // All packs should be blocked
        Assert.False(modelBefore.Summary.CanRun);
        Assert.All(modelBefore.ReviewPacks, pack =>
            Assert.Equal(QualityReviewStatus.Blocked, pack.Status));

        // Now add specification
        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = ArtifactType.Specification,
            Content = "# Spec",
            ContentHash = "hash",
            LastModified = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        // Rebuild model
        var modelAfter = await builder.BuildPageModelAsync();

        // Now some packs should be available
        Assert.True(modelAfter.Summary.CanRun);
        Assert.Contains(modelAfter.ReviewPacks, pack => pack.Status == QualityReviewStatus.Available);
    }

    [Fact]
    public async Task Page_Model_Lists_Exact_Missing_Prerequisites()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = Guid.NewGuid();

        var workspace = new SavedWorkspace
        {
            Id = workspaceId,
            UserId = "test-user",
            Name = "Test",
            ProjectName = "Test"
        };
        db.SavedWorkspaces.Add(workspace);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QualityReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QualityReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        // Find QA Auditor pack
        var qaAuditor = model.ReviewPacks.FirstOrDefault(p => p.Name == "QA Auditor");
        Assert.NotNull(qaAuditor);

        // Should list all missing prerequisites
        Assert.NotEmpty(qaAuditor.MissingInputs);
        Assert.Contains("specification.md", qaAuditor.MissingInputs);
        Assert.Contains("plan.md", qaAuditor.MissingInputs);
        Assert.Contains("tasks", qaAuditor.MissingInputs);
    }

    [Fact]
    public async Task Run_Button_Enabled_Only_When_Packs_Available()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = Guid.NewGuid();

        var workspace = new SavedWorkspace
        {
            Id = workspaceId,
            UserId = "test-user",
            Name = "Test",
            ProjectName = "Test"
        };
        db.SavedWorkspaces.Add(workspace);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QualityReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QualityReviewPageModelBuilder>.Instance);

        // No artifacts = cannot run
        var modelBefore = await builder.BuildPageModelAsync();
        Assert.False(modelBefore.Summary.CanRun);

        // Add specification
        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = ArtifactType.Specification,
            Content = "# Spec",
            ContentHash = "hash",
            LastModified = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        // Can run now
        var modelAfter = await builder.BuildPageModelAsync();
        Assert.True(modelAfter.Summary.CanRun);
    }

    [Fact]
    public async Task Removing_Artifact_Blocks_Dependent_Packs()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = Guid.NewGuid();

        var workspace = new SavedWorkspace
        {
            Id = workspaceId,
            UserId = "test-user",
            Name = "Test",
            ProjectName = "Test"
        };
        db.SavedWorkspaces.Add(workspace);

        // Add specification
        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = ArtifactType.Specification,
            Content = "# Spec",
            ContentHash = "hash",
            LastModified = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QualityReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QualityReviewPageModelBuilder>.Instance);

        // Can run with specification
        var modelWith = await builder.BuildPageModelAsync();
        Assert.True(modelWith.Summary.CanRun);

        // Remove specification
        db.SavedWorkspaceArtifacts.Remove(artifact);
        await db.SaveChangesAsync();

        // Cannot run anymore
        var modelWithout = await builder.BuildPageModelAsync();
        Assert.False(modelWithout.Summary.CanRun);
    }

    [Fact]
    public async Task Page_Model_Includes_All_Pack_Categories()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = Guid.NewGuid();

        var workspace = new SavedWorkspace
        {
            Id = workspaceId,
            UserId = "test-user",
            Name = "Test",
            ProjectName = "Test"
        };
        db.SavedWorkspaces.Add(workspace);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QualityReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QualityReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        // Should have multiple categories of packs
        var categories = model.ReviewPacks.Select(p => p.Category).Distinct();
        Assert.Contains("QA", categories);
        Assert.Contains("Data", categories);
        Assert.Contains("Compliance", categories);
        Assert.Contains("Accessibility", categories);
        Assert.Contains("Security", categories);
    }

    [Fact]
    public async Task Readiness_Message_Explains_Missing_Requirements()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = Guid.NewGuid();

        var workspace = new SavedWorkspace
        {
            Id = workspaceId,
            UserId = "test-user",
            Name = "Test",
            ProjectName = "Test"
        };
        db.SavedWorkspaces.Add(workspace);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var builder = new QualityReviewPageModelBuilder(
            db,
            artifactStatus,
            NullLogger<QualityReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        // When blocked, should have helpful message
        Assert.False(model.Summary.CanRun);
        Assert.NotEmpty(model.Summary.ReadinessMessage);
        Assert.Contains("artifacts", model.Summary.ReadinessMessage.ToLower());
    }
}
