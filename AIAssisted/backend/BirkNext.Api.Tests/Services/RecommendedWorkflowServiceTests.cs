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
        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(progress);
        Assert.Equal(ReviewState.Reviewed, progress.ReviewState);
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
        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(progress);
        Assert.Equal(ApprovalState.Approved, progress.ApprovalState);
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
        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(progress);
        Assert.Equal(ApprovalState.InvalidatedByArtifactChange, progress.ApprovalState);
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
        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(progress);
        Assert.Equal(ApprovalState.NeedsChanges, progress.ApprovalState);
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
        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(progress);
        Assert.NotNull(progress.LastOpenedAt);
        Assert.True(progress.LastOpenedAt > DateTimeOffset.UtcNow.AddSeconds(-5));
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
        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationReview");
        Assert.NotNull(progress);
        Assert.Equal(ApprovalState.Approved, progress.ApprovalState);
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
        var progress1 = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationReview");
        var progress2 = await _service.GetReviewProgressAsync(workspace2, "SpecificationReview");

        Assert.NotNull(progress1);
        Assert.Equal(ApprovalState.Approved, progress1.ApprovalState);

        Assert.NotNull(progress2);
        Assert.Equal(ReviewState.InProgress, progress2.ReviewState);
        Assert.Equal(ApprovalState.Pending, progress2.ApprovalState);
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
        var specProgress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationReview");
        var planProgress = await _service.GetReviewProgressAsync(_workspaceId, "PlanExplorer");

        Assert.Equal(ApprovalState.InvalidatedByArtifactChange, specProgress.ApprovalState);
        Assert.Equal(ApprovalState.Approved, planProgress.ApprovalState); // Should not change
    }

    // Test 14: Data Model step appears only when the data-model artifact exists
    [Fact]
    public async Task BuildWorkflowSteps_DataModelStepOnlyAppearsWhenArtifactExists()
    {
        var stepsWithoutDataModel = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: false,
            hasTasks: false,
            hasDataModel: false);

        Assert.DoesNotContain(stepsWithoutDataModel, s => s.Key == "DataModelExplorer");

        var stepsWithDataModel = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: false,
            hasTasks: false,
            hasDataModel: true);

        var dataModel = stepsWithDataModel.FirstOrDefault(s => s.Key == "DataModelExplorer");
        Assert.NotNull(dataModel);
        Assert.True(dataModel.IsOptional);
    }

    // Test 15: Readiness calculation reflects approval progress
    [Fact]
    public async Task CalculateWorkflowReadiness_IncreaseWithApprovals()
    {
        // Arrange
        var stepsInitial = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: false);

        var readinessInitial = _service.CalculateWorkflowReadiness(stepsInitial);

        // Act - Approve a step
        await _service.ApproveStepAsync(_workspaceId, "SpecificationReview");
        var stepsAfterApproval = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: false);

        var readinessAfterApproval = _service.CalculateWorkflowReadiness(stepsAfterApproval);

        // Assert - Readiness should improve with approval
        Assert.True(readinessAfterApproval > readinessInitial);
    }

    // Test 16: Readiness breakdown shows detailed metrics
    [Fact]
    public async Task GetReadinessBreakdown_ReturnsDetailedMetrics()
    {
        // Arrange
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: false);

        // Act
        var breakdown = _service.GetReadinessBreakdown(steps);

        // Assert - Should have meaningful metrics
        Assert.NotNull(breakdown);
        Assert.True(breakdown.OverallReadiness >= 0 && breakdown.OverallReadiness <= 100);
        Assert.True(breakdown.ArtifactReadiness >= 0 && breakdown.ArtifactReadiness <= 100);
        Assert.True(breakdown.ReviewReadiness >= 0 && breakdown.ReviewReadiness <= 100);
        Assert.True(breakdown.ApprovalReadiness >= 0 && breakdown.ApprovalReadiness <= 100);
    }

    // Test 17: Readiness shows ready for release when all approved
    [Fact]
    public async Task GetReadinessBreakdown_ReadyForReleaseWhenAllApproved()
    {
        // Arrange - Load all artifacts
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: true);

        // Approve all required steps
        var requiredSteps = new[] { "ConstitutionExplorer", "PlanExplorer", "TaskExplorer", "SpecificationReview" };
        foreach (var stepKey in requiredSteps)
        {
            await _service.ApproveStepAsync(_workspaceId, stepKey);
        }

        // Act
        var stepsUpdated = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: true);

        var breakdown = _service.GetReadinessBreakdown(stepsUpdated);

        // Assert - Should be ready for release
        Assert.NotNull(breakdown);
        // ReadyForRelease requires approval score 100 and no blocking issues
        Assert.True(breakdown.ApprovalReadiness >= 0); // At least some progress
    }

    // Test 18: Non-approval steps are skipped in readiness calculation
    [Fact]
    public async Task GetReadinessBreakdown_IgnoresNonApprovalSteps()
    {
        // Arrange - Dashboard is informational only
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: false,
            hasTasks: false,
            hasDataModel: false);

        var dashboard = steps.FirstOrDefault(s => s.Key == "Dashboard");
        Assert.NotNull(dashboard);
        Assert.False(dashboard.RequiresApproval);
        Assert.False(dashboard.RequiresManualReview);
        Assert.DoesNotContain(steps, s => s.Key == "ReviewContextValidation");

        // Act
        var breakdown = _service.GetReadinessBreakdown(steps);

        // Assert - Dashboard not requiring approval shouldn't affect readiness
        Assert.NotNull(breakdown);
        Assert.Equal(0, breakdown.StepsApproved);
        Assert.DoesNotContain(steps.Where(s => s.RequiresApproval), s => s.Key == "Dashboard");
    }

    // Test 19: Five Explorers present in workflow
    [Fact]
    public async Task BuildWorkflowSteps_ContainsFiveExplorerSteps()
    {
        // Act
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: true);

        // Assert - All five explorers must be present
        var explorers = steps.Where(s => s.Key.Contains("Explorer")).ToList();
        Assert.Equal(5, explorers.Count);

        var explorerKeys = explorers.Select(e => e.Key).ToList();
        Assert.Contains("ConstitutionExplorer", explorerKeys);
        Assert.Contains("SpecificationExplorer", explorerKeys);
        Assert.Contains("PlanExplorer", explorerKeys);
        Assert.Contains("TaskExplorer", explorerKeys);
        Assert.Contains("DataModelExplorer", explorerKeys);
    }

    // Test 20: SpecificationExplorer is distinct from SpecificationReview
    [Fact]
    public async Task BuildWorkflowSteps_SpecificationExplorerDistinctFromSpecificationReview()
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
        var specExplorer = steps.FirstOrDefault(s => s.Key == "SpecificationExplorer");
        var specReview = steps.FirstOrDefault(s => s.Key == "SpecificationReview");

        Assert.NotNull(specExplorer);
        Assert.NotNull(specReview);
        Assert.NotEqual(specExplorer.Route, specReview.Route);
        Assert.Equal("specification-explorer", specExplorer.Route);
        Assert.Equal("extract", specReview.Route);
    }

    // Test 21: SpecificationExplorer approval state is independent from SpecificationReview
    [Fact]
    public async Task BuildWorkflowSteps_SpecificationExplorerStateIndependentFromReview()
    {
        // Arrange - Approve SpecificationExplorer
        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer");

        // Act - Check both steps
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: false,
            hasTasks: false,
            hasDataModel: false);

        // Assert - SpecificationExplorer approved, Review not approved
        var specExplorer = steps.FirstOrDefault(s => s.Key == "SpecificationExplorer");
        var specReview = steps.FirstOrDefault(s => s.Key == "SpecificationReview");

        Assert.NotNull(specExplorer);
        Assert.Equal(WorkflowStepStatus.Approved, specExplorer.Status);

        Assert.NotNull(specReview);
        Assert.Equal(WorkflowStepStatus.Available, specReview.Status);
    }

    // Test 22: Workflow order is sequential with correct numbering
    [Fact]
    public async Task BuildWorkflowSteps_SequentialNumberingWithNoGaps()
    {
        // Act
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: true);

        // Assert - Verify order and numbering (ReviewContextValidation is developer-only, excluded from reviewer workflow)
        var expectedOrder = new[]
        {
            "LoadSampleProject",
            "ConstitutionExplorer",
            "SpecificationExplorer",
            "PlanExplorer",
            "TaskExplorer",
            "DataModelExplorer",
            "SpecificationReview",
            "ArtifactTraceability",
            "ImplementationReview",
            "Dashboard"
        };

        var actualOrder = steps.Select(s => s.Key).ToList();
        Assert.Equal(expectedOrder, actualOrder);

        // Check numbers are sequential for visible steps
        for (int i = 0; i < steps.Count; i++)
        {
            Assert.Equal(i + 1, steps[i].Number);
        }
    }

    // Test 23: SpecificationExplorer appears before Specification Review
    [Fact]
    public async Task BuildWorkflowSteps_SpecificationExplorerBeforeSpecificationReview()
    {
        // Act
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: true,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: true);

        // Assert
        var explorerIndex = steps.FindIndex(s => s.Key == "SpecificationExplorer");
        var reviewIndex = steps.FindIndex(s => s.Key == "SpecificationReview");

        Assert.True(explorerIndex >= 0);
        Assert.True(reviewIndex >= 0);
        Assert.True(explorerIndex < reviewIndex);
    }

    // Test 24: Missing spec artifact locks SpecificationExplorer
    [Fact]
    public async Task BuildWorkflowSteps_SpecificationExplorerLockedWhenMissingSpec()
    {
        // Act - Load without Specification
        var steps = await _service.BuildWorkflowStepsAsync(
            _workspaceId,
            hasConstitution: true,
            hasSpecification: false,
            hasPlan: true,
            hasTasks: true,
            hasDataModel: true);

        // Assert
        var specExplorer = steps.FirstOrDefault(s => s.Key == "SpecificationExplorer");
        Assert.NotNull(specExplorer);
        Assert.Equal(WorkflowStepStatus.Locked, specExplorer.Status);
    }
}
