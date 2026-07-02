using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BirkNext.Api.Tests.Services;

public class RecommendedWorkflowServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly IRecommendedWorkflowService _service;
    private readonly ILogger<RecommendedWorkflowService> _logger;
    private readonly Guid _workspaceId = Guid.NewGuid();

    public RecommendedWorkflowServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(options);
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<RecommendedWorkflowService>();
        _service = new RecommendedWorkflowService(_db, _logger);

        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db?.Dispose();
    }

    // Test 1: Loaded artifact creates Available step, not Approved
    [Fact]
    public async Task BuildWorkflowSteps_WithLoadedArtifacts_CreatesAvailableNotApproved()
    {
        // Act
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: false,
            hasTasks: false,
            hasDataModel: false);

        // Assert
        var specReview = steps.FirstOrDefault(s => s.Key == "SpecificationReview");
        Assert.NotNull(specReview);
        Assert.Equal(WorkflowStepStatus.Available, specReview.Status);
        Assert.NotEqual(WorkflowStepStatus.Approved, specReview.Status);
    }

    // Test 2: Step becomes Reviewed only after Mark Reviewed
    [Fact]
    public async Task MarkStepReviewed_ChangesReviewState()
    {
        // Arrange
        await _service.MarkStepInProgressAsync(_workspaceId, "SpecificationReview");

        // Act
        await _service.MarkStepReviewedAsync(_workspaceId, "SpecificationReview");

        // Assert
        var step = await _service.GetReviewStepAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(step);
        Assert.Equal(ReviewState.Reviewed, step.ReviewState);
    }

    // Test 3: Step becomes Approved only after Approve
    [Fact]
    public async Task ApproveStep_SetsApprovedState()
    {
        // Arrange
        await _service.MarkStepReviewedAsync(_workspaceId, "SpecificationReview");

        // Act
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview");

        // Assert
        var step = await _service.GetReviewStepAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(step);
        Assert.Equal(ApprovalState.Approved, step.ApprovalState);
    }

    // Test 4: Approved step persists after workspace reload
    [Fact]
    public async Task ApprovedStep_PersistedInDatabase()
    {
        // Arrange
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview", comment: "Test approval");

        // Act - Rebuild steps with same workspace ID
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: false,
            hasTasks: false,
            hasDataModel: false);

        // Assert
        var specReview = steps.FirstOrDefault(s => s.Key == "SpecificationReview");
        Assert.NotNull(specReview);
        Assert.Equal(WorkflowStepStatus.Approved, specReview.Status);
    }

    // Test 5: Artifact content change invalidates dependent approval
    [Fact]
    public async Task InvalidateApprovalsAsync_InvalidatesDependentSteps()
    {
        // Arrange - Approve SpecificationReview
        var hash1 = "hash_abc123";
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview", artifactSetHash: hash1);

        // Act - Invalidate because spec changed
        var hash2 = "hash_xyz789";
        await _service.InvalidateArtifactDependentApprovalsAsync(
            _workspaceId,
            new List<string> { "Specification" },
            hash2);

        // Assert
        var step = await _service.GetReviewStepAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(step);
        Assert.Equal(ApprovalState.InvalidatedByArtifactChange, step.ApprovalState);
    }

    // Test 6: Artifact Traceability locked until prerequisites approved
    [Fact]
    public async Task BuildWorkflowSteps_LocksTraceabilityUntilPrerequisitesApproved()
    {
        // Act - Without SpecificationReview approved
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: false);

        // Assert - ArtifactTraceability should be Locked
        var traceability = steps.FirstOrDefault(s => s.Key == "ArtifactTraceability");
        Assert.NotNull(traceability);
        Assert.Equal(WorkflowStepStatus.Locked, traceability.Status);

        // Now approve SpecificationReview
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview");

        // Act - Rebuild steps
        steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: false);

        // Assert - ArtifactTraceability should now be Available
        traceability = steps.FirstOrDefault(s => s.Key == "ArtifactTraceability");
        Assert.NotNull(traceability);
        Assert.Equal(WorkflowStepStatus.Available, traceability.Status);
    }

    // Test 7: Implementation Review locked until Artifact Traceability approved
    [Fact]
    public async Task BuildWorkflowSteps_LocksImplementationReviewUntilTraceabilityApproved()
    {
        // Arrange - Approve SpecificationReview
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview");

        // Act - Without ArtifactTraceability approved
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: false);

        // Assert - ImplementationReview should be Locked
        var implReview = steps.FirstOrDefault(s => s.Key == "ImplementationReview");
        Assert.NotNull(implReview);
        Assert.Equal(WorkflowStepStatus.Locked, implReview.Status);

        // Now approve ArtifactTraceability
        await _service.ApproveStepAsync(_workspaceId, "ArtifactTraceability");

        // Act - Rebuild steps
        steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: false);

        // Assert - ImplementationReview should now be Available
        implReview = steps.FirstOrDefault(s => s.Key == "ImplementationReview");
        Assert.NotNull(implReview);
        Assert.Equal(WorkflowStepStatus.Available, implReview.Status);
    }

    // Test 8: Reject marks step as NeedsChanges
    [Fact]
    public async Task RejectStep_SetsNeedsChangesState()
    {
        // Act
        await _service.RejectStepAsync(_workspaceId, "SpecificationReview", comment: "Needs revision");

        // Assert
        var step = await _service.GetReviewStepAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(step);
        Assert.Equal(ApprovalState.NeedsChanges, step.ApprovalState);
    }

    // Test 9: GetCurrentRecommendedStep returns first available
    [Fact]
    public async Task GetCurrentRecommendedStep_ReturnsFirstAvailableApprovedStep()
    {
        // Arrange
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: false,
            hasTasks: false,
            hasDataModel: false);

        // Act
        var current = _service.GetCurrentRecommendedStep(steps);

        // Assert
        Assert.NotNull(current);
        Assert.True(current.IsCurrent);
    }

    // Test 10: Mark InProgress updates LastOpenedAt
    [Fact]
    public async Task MarkStepInProgress_UpdatesLastOpenedAt()
    {
        // Act
        await _service.MarkStepInProgressAsync(_workspaceId, "SpecificationReview");

        // Assert
        var step = await _service.GetReviewStepAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(step);
        Assert.NotNull(step.LastOpenedAt);
        Assert.True(step.LastOpenedAt > DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    // Test 11: Approvals not invalidated if hash matches
    [Fact]
    public async Task InvalidateApprovalsAsync_DoesNotInvalidateIfHashMatches()
    {
        // Arrange
        var hash = "hash_abc123";
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview", artifactSetHash: hash);

        // Act - Invalidate with same hash
        await _service.InvalidateArtifactDependentApprovalsAsync(
            _workspaceId,
            new List<string> { "Specification" },
            hash);

        // Assert - Should still be Approved
        var step = await _service.GetReviewStepAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(step);
        Assert.Equal(ApprovalState.Approved, step.ApprovalState);
    }

    // Test 12: Multiple workspaces have independent state
    [Fact]
    public async Task MultipleWorkspaces_HaveIndependentState()
    {
        // Arrange
        var workspace2 = Guid.NewGuid();

        // Act
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview");
        // Mark step in progress in workspace2 to create it
        await _service.MarkStepInProgressAsync(workspace2, "SpecificationReview");

        // Assert
        var step1 = await _service.GetReviewStepAsync(_workspaceId, "SpecificationReview");
        var step2 = await _service.GetReviewStepAsync(workspace2, "SpecificationReview");

        Assert.NotNull(step1);
        Assert.Equal(ApprovalState.Approved, step1.ApprovalState);

        Assert.NotNull(step2);
        Assert.Equal(ReviewState.InProgress, step2.ReviewState);
        Assert.Equal(ApprovalState.Pending, step2.ApprovalState);
    }

    // Test 13: Loaded artifact changes don't affect other artifacts' approvals
    [Fact]
    public async Task InvalidateApprovalsAsync_OnlyInvalidatesDependentSteps()
    {
        // Arrange - Approve multiple steps
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview", artifactSetHash: "spec_hash");
        await _service.ApproveStepAsync(_workspaceId, "PlanExplorer", artifactSetHash: "plan_hash");

        // Act - Invalidate only spec-dependent steps
        await _service.InvalidateArtifactDependentApprovalsAsync(
            _workspaceId,
            new List<string> { "Specification" },
            "new_hash");

        // Assert
        var specStep = await _service.GetReviewStepAsync(_workspaceId, "SpecificationReview");
        var planStep = await _service.GetReviewStepAsync(_workspaceId, "PlanExplorer");

        Assert.Equal(ApprovalState.InvalidatedByArtifactChange, specStep.ApprovalState);
        Assert.Equal(ApprovalState.Approved, planStep.ApprovalState); // Should not change
    }
}
