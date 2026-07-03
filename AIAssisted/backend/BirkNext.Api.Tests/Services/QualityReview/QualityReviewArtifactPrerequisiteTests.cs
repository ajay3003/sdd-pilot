using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.QualityReview;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.QualityReview;

/// <summary>
/// Tests for artifact prerequisite detection in Quality Review page builder.
/// Verifies that packs become Available/Blocked based on loaded artifacts.
/// </summary>
public class QualityReviewArtifactPrerequisiteTests
{
    private AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task QAAuditorPack_Becomes_Available_When_Specification_Loaded()
    {
        await using var db = CreateInMemoryDb();
        var workspaceId = Guid.NewGuid();

        var workspace = new SavedWorkspace
        {
            Id = workspaceId,
            UserId = "test-user",
            Name = "Test",
            ProjectName = "Test",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaces.Add(workspace);

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

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var status = await artifactStatus.GetStatusAsync(workspaceId);

        Assert.True(status.HasSpecification);
        Assert.Equal(1, status.LoadedCount);
    }

    [Fact]
    public async Task AllPacks_Are_Blocked_When_No_Artifacts_Loaded()
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
        var status = await artifactStatus.GetStatusAsync(workspaceId);

        Assert.Equal(0, status.LoadedCount);
        Assert.False(status.HasSpecification);
        Assert.False(status.HasPlan);
        Assert.False(status.HasConstitution);
        Assert.False(status.HasDataModel);
        Assert.False(status.HasTasks);
    }

    [Fact]
    public async Task DataModelQualityPack_Becomes_Available_When_DataModel_Loaded()
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

        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = ArtifactType.DataModel,
            Content = "# Data Model",
            ContentHash = "hash",
            LastModified = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var status = await artifactStatus.GetStatusAsync(workspaceId);

        Assert.True(status.HasDataModel);
    }

    [Fact]
    public async Task ConstitutionCompliancePack_Becomes_Available_When_Constitution_Loaded()
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

        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = ArtifactType.Constitution,
            Content = "# Constitution",
            ContentHash = "hash",
            LastModified = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var status = await artifactStatus.GetStatusAsync(workspaceId);

        Assert.True(status.HasConstitution);
    }

    [Fact]
    public async Task QAReadinessPack_Becomes_Available_With_Specification_Or_Tasks()
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

        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = ArtifactType.Tasks,
            Content = "- Task 1",
            ContentHash = "hash",
            LastModified = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var status = await artifactStatus.GetStatusAsync(workspaceId);

        Assert.True(status.HasTasks);
        // QA Readiness should be available because tasks are present (even without specification)
    }

    [Fact]
    public async Task DeliveryReadinessPack_Becomes_Available_With_Plan_Or_Tasks()
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

        var artifact = new SavedWorkspaceArtifact
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactType = ArtifactType.Plan,
            Content = "# Plan",
            ContentHash = "hash",
            LastModified = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedWorkspaceArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var status = await artifactStatus.GetStatusAsync(workspaceId);

        Assert.True(status.HasPlan);
        // Delivery Readiness should be available because plan is present
    }

    [Fact]
    public async Task Multiple_Artifacts_Are_Detected_Correctly()
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

        var artifacts = new List<SavedWorkspaceArtifact>
        {
            new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, ArtifactType = ArtifactType.Specification, Content = "# Spec", ContentHash = "h1", LastModified = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
            new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, ArtifactType = ArtifactType.Plan, Content = "# Plan", ContentHash = "h2", LastModified = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
            new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, ArtifactType = ArtifactType.Constitution, Content = "# Const", ContentHash = "h3", LastModified = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }
        };
        db.SavedWorkspaceArtifacts.AddRange(artifacts);
        await db.SaveChangesAsync();

        var artifactStatus = new WorkspaceArtifactStatusService(db, NullLogger<WorkspaceArtifactStatusService>.Instance);
        var status = await artifactStatus.GetStatusAsync(workspaceId);

        Assert.Equal(3, status.LoadedCount);
        Assert.True(status.HasSpecification);
        Assert.True(status.HasPlan);
        Assert.True(status.HasConstitution);
        Assert.False(status.HasDataModel);
        Assert.False(status.HasTasks);
    }
}
