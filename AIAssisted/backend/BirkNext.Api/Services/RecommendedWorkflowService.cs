using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>
/// Service for managing Recommended Workflow state and approvals.
/// Determines step status based on artifacts, review state, and approvals.
/// Persists only human decisions via WorkspaceReviewProgress.
/// Computes Available/Locked status at runtime from WorkflowDefinitions and artifact availability.
/// </summary>
public interface IRecommendedWorkflowService
{
    /// <summary>
    /// Build workflow steps for a workspace with current state.
    /// Returns steps with status reflecting artifacts, prerequisites, and approval state.
    /// </summary>
    Task<List<WorkflowStepViewModel>> BuildWorkflowStepsAsync(
        Guid workspaceId,
        bool hasConstitution,
        bool hasSpecification,
        bool hasPlan,
        bool hasTasks,
        bool hasDataModel);

    /// <summary>
    /// Mark a step as in-progress (user opened the page).
    /// </summary>
    Task MarkStepInProgressAsync(Guid workspaceId, string stepKey, string? userId = null);

    /// <summary>
    /// Mark a step as reviewed.
    /// </summary>
    Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, string? comment = null, string? userId = null);

    /// <summary>
    /// Approve a step.
    /// </summary>
    Task ApproveStepAsync(
        Guid workspaceId,
        string stepKey,
        string? artifactSetHash = null,
        string? comment = null,
        string? userId = null);

    /// <summary>
    /// Reject a step or mark as needs changes.
    /// </summary>
    Task RejectStepAsync(
        Guid workspaceId,
        string stepKey,
        string? comment = null,
        string? userId = null);

    /// <summary>
    /// Invalidate approvals for steps that depend on changed artifacts.
    /// </summary>
    Task InvalidateArtifactDependentApprovalsAsync(
        Guid workspaceId,
        List<string> changedArtifactTypes,
        string currentArtifactSetHash);

    /// <summary>
    /// Get review/approval progress for a specific step.
    /// </summary>
    Task<WorkspaceReviewProgress?> GetReviewProgressAsync(Guid workspaceId, string stepKey);

    /// <summary>
    /// Get all review progress for a workspace.
    /// </summary>
    Task<List<WorkspaceReviewProgress>> GetWorkspaceReviewProgressAsync(Guid workspaceId);

    /// <summary>
    /// Determine which step should be current (next recommended action).
    /// </summary>
    WorkflowStepViewModel? GetCurrentRecommendedStep(List<WorkflowStepViewModel> steps);

    /// <summary>
    /// Calculate overall workflow readiness (0-100%).
    /// Weights: 30% artifacts, 30% reviews, 40% approvals.
    /// </summary>
    int CalculateWorkflowReadiness(List<WorkflowStepViewModel> steps);

    /// <summary>
    /// Get a detailed readiness breakdown for dashboard display.
    /// </summary>
    WorkflowReadinessBreakdown GetReadinessBreakdown(List<WorkflowStepViewModel> steps);
}

public class RecommendedWorkflowService : IRecommendedWorkflowService
{
    private readonly AppDbContext _db;
    private readonly ILogger<RecommendedWorkflowService> _logger;

    // Artifact dependencies per step (from WorkflowDefinitions)
    private static readonly Dictionary<string, string[]> StepDependencies = new()
    {
        { "LoadSampleProject", Array.Empty<string>() },
        { "ConstitutionExplorer", new[] { "Constitution" } },
        { "SpecificationExplorer", new[] { "Specification" } },
        { "PlanExplorer", new[] { "Plan" } },
        { "TaskExplorer", new[] { "Tasks" } },
        { "DataModelExplorer", new[] { "DataModel" } },
        { "SpecificationReview", new[] { "Specification" } },
        { "ArtifactTraceability", new[] { "Constitution", "Specification", "Plan", "Tasks" } },
        { "ImplementationReview", new[] { "Specification", "Tasks" } },
        { "ReviewContextValidation", Array.Empty<string>() },
        { "Dashboard", Array.Empty<string>() }
    };

    // Approval dependencies per step (steps that must be approved first)
    private static readonly Dictionary<string, List<string>> ApprovalDependencies = new()
    {
        { "ArtifactTraceability", new[] { "SpecificationReview" }.ToList() },
        { "ImplementationReview", new[] { "ArtifactTraceability" }.ToList() }
    };

    public RecommendedWorkflowService(AppDbContext db, ILogger<RecommendedWorkflowService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<WorkflowStepViewModel>> BuildWorkflowStepsAsync(
        Guid workspaceId,
        bool hasConstitution,
        bool hasSpecification,
        bool hasPlan,
        bool hasTasks,
        bool hasDataModel)
    {
        // Load persisted progress for this workspace
        var progressRecords = await _db.WorkspaceReviewProgress
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync();

        var progressLookup = progressRecords.ToDictionary(p => p.StepKey);

        // Map artifact availability
        var loadedArtifacts = new Dictionary<string, bool>
        {
            { "Constitution", hasConstitution },
            { "Specification", hasSpecification },
            { "Plan", hasPlan },
            { "Tasks", hasTasks },
            { "DataModel", hasDataModel }
        };

        var viewModels = new List<WorkflowStepViewModel>();
        var currentStepAssigned = false;

        // Build view model for reviewer workflow definitions only.
        var visibleDefinitions = WorkflowDefinitions.AllSteps
            .Where(definition => ShouldIncludeInReviewerWorkflow(definition, hasDataModel))
            .ToList();

        for (int index = 0; index < visibleDefinitions.Count; index++)
        {
            var definition = visibleDefinitions[index];
            var visibleNumber = index + 1;  // Renumber based on visible steps to avoid gaps

            var progress = progressLookup.TryGetValue(definition.StepKey, out var p) ? p : null;

            // Check if required artifacts are available
            // Optional artifacts are not blocking
            var requiredArtifactsLoaded = definition.RequiredArtifacts.Count == 0 ||
                definition.RequiredArtifacts.All(art =>
                    loadedArtifacts.TryGetValue(art, out var loaded) && loaded);

            // Check if approval dependencies are met
            var approvalDepsRequired = ApprovalDependencies.TryGetValue(definition.StepKey, out var deps) ? deps : new List<string>();
            var approvalDepsMet = approvalDepsRequired.All(depKey =>
                progressLookup.TryGetValue(depKey, out var depProgress) &&
                depProgress.ApprovalState == ApprovalState.Approved);

            // Compute workflow status at runtime
            var status = ComputeStepStatus(
                definition,
                requiredArtifactsLoaded,
                approvalDepsMet,
                progress);

            var isAvailable = status switch
            {
                WorkflowStepStatus.Available or
                WorkflowStepStatus.InProgress or
                WorkflowStepStatus.Reviewed or
                WorkflowStepStatus.Approved or
                WorkflowStepStatus.NeedsAttention => true,
                _ => false
            };

            // Only mark as current if step requires approval/review OR it's the first available
            var stepNeedsApproval = definition.RequiresApproval || definition.RequiresManualReview;
            var isCurrent = !currentStepAssigned &&
                isAvailable &&
                status != WorkflowStepStatus.Approved &&
                stepNeedsApproval;

            if (isCurrent)
                currentStepAssigned = true;

            var vm = new WorkflowStepViewModel
            {
                Number = visibleNumber,
                Key = definition.StepKey,
                Title = definition.Title,
                Description = definition.Description,
                Route = definition.Route,
                ActionLabel = definition.ActionLabel,
                Color = definition.Color,
                Status = status,
                Prerequisites = requiredArtifactsLoaded ? PrerequisiteState.Available : PrerequisiteState.Missing,
                ReviewState = progress?.ReviewState ?? ReviewState.NotStarted,
                ApprovalState = progress?.ApprovalState ?? ApprovalState.Pending,
                CanOpen = isAvailable,
                DisabledReason = !requiredArtifactsLoaded && definition.RequiredArtifacts.Count > 0
                    ? "Load required artifacts first"
                    : !approvalDepsMet
                    ? "Complete prerequisite approvals first"
                    : "",
                IsCurrent = isCurrent,
                IsFuture = !isAvailable,
                IsOptional = definition.IsOptional,
                RequiresApproval = definition.RequiresApproval,
                RequiresManualReview = definition.RequiresManualReview
            };

            viewModels.Add(vm);
        }

        return viewModels;
    }

    public async Task MarkStepInProgressAsync(Guid workspaceId, string stepKey, string? userId = null)
    {
        var progress = await GetOrCreateProgressAsync(workspaceId, stepKey);
        if (progress.ReviewState < ReviewState.InProgress)
        {
            progress.ReviewState = ReviewState.InProgress;
            progress.LastOpenedAt = DateTimeOffset.UtcNow;
            progress.UpdatedAt = DateTimeOffset.UtcNow;
            _db.WorkspaceReviewProgress.Update(progress);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Marked step {StepKey} as in progress for workspace {WorkspaceId}", stepKey, workspaceId);
        }
    }

    public async Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, string? comment = null, string? userId = null)
    {
        if (workspaceId == Guid.Empty)
            throw new InvalidOperationException("Workflow operations require a saved workspace. Save the workspace first before marking steps as reviewed.");

        var progress = await GetOrCreateProgressAsync(workspaceId, stepKey);
        progress.ReviewState = ReviewState.Reviewed;
        progress.ReviewedAt = DateTimeOffset.UtcNow;
        progress.ReviewedBy = userId ?? "Local Developer";
        if (!string.IsNullOrWhiteSpace(comment))
            progress.Comment = comment;
        progress.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewProgress.Update(progress);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Marked step {StepKey} as reviewed for workspace {WorkspaceId}", stepKey, workspaceId);
    }

    public async Task ApproveStepAsync(
        Guid workspaceId,
        string stepKey,
        string? artifactSetHash = null,
        string? comment = null,
        string? userId = null)
    {
        if (workspaceId == Guid.Empty)
            throw new InvalidOperationException("Workflow operations require a saved workspace. Save the workspace first before approving steps.");

        var progress = await GetOrCreateProgressAsync(workspaceId, stepKey);
        progress.ReviewState = ReviewState.Reviewed;
        progress.ApprovalState = ApprovalState.Approved;
        progress.ApprovedAt = DateTimeOffset.UtcNow;
        progress.ApprovedBy = userId ?? "Local Developer";
        progress.ArtifactSetHashAtApproval = artifactSetHash;
        if (!string.IsNullOrWhiteSpace(comment))
            progress.Comment = comment;
        progress.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewProgress.Update(progress);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Approved step {StepKey} for workspace {WorkspaceId}", stepKey, workspaceId);
    }

    public async Task RejectStepAsync(
        Guid workspaceId,
        string stepKey,
        string? comment = null,
        string? userId = null)
    {
        if (workspaceId == Guid.Empty)
            throw new InvalidOperationException("Workflow operations require a saved workspace. Save the workspace first before rejecting steps.");

        var progress = await GetOrCreateProgressAsync(workspaceId, stepKey);
        progress.ApprovalState = ApprovalState.NeedsChanges;
        progress.RejectedAt = DateTimeOffset.UtcNow;
        progress.RejectedBy = userId ?? "Local Developer";
        if (!string.IsNullOrWhiteSpace(comment))
            progress.Comment = comment;
        progress.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewProgress.Update(progress);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Rejected step {StepKey} for workspace {WorkspaceId}", stepKey, workspaceId);
    }

    public async Task InvalidateArtifactDependentApprovalsAsync(
        Guid workspaceId,
        List<string> changedArtifactTypes,
        string currentArtifactSetHash)
    {
        var approvedSteps = await _db.WorkspaceReviewProgress
            .Where(p => p.WorkspaceId == workspaceId && p.ApprovalState == ApprovalState.Approved)
            .ToListAsync();

        var invalidated = new List<string>();

        foreach (var step in approvedSteps)
        {
            var shouldInvalidate = ShouldInvalidateStep(step.StepKey, changedArtifactTypes);
            if (shouldInvalidate && step.ArtifactSetHashAtApproval != currentArtifactSetHash)
            {
                step.ApprovalState = ApprovalState.InvalidatedByArtifactChange;
                step.UpdatedAt = DateTimeOffset.UtcNow;
                _db.WorkspaceReviewProgress.Update(step);
                invalidated.Add(step.StepKey);
            }
        }

        if (invalidated.Any())
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Invalidated approvals for steps {Steps} due to artifact changes in workspace {WorkspaceId}",
                string.Join(", ", invalidated), workspaceId);
        }
    }

    public async Task<WorkspaceReviewProgress?> GetReviewProgressAsync(Guid workspaceId, string stepKey)
    {
        return await _db.WorkspaceReviewProgress
            .FirstOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.StepKey == stepKey);
    }

    public async Task<List<WorkspaceReviewProgress>> GetWorkspaceReviewProgressAsync(Guid workspaceId)
    {
        return await _db.WorkspaceReviewProgress
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync();
    }

    public WorkflowStepViewModel? GetCurrentRecommendedStep(List<WorkflowStepViewModel> steps)
    {
        return steps.FirstOrDefault(s => s.IsCurrent && IsReleaseReviewStep(s));
    }

    public int CalculateWorkflowReadiness(List<WorkflowStepViewModel> steps)
    {
        var breakdown = GetReadinessBreakdown(steps);
        return breakdown.OverallReadiness;
    }

    public WorkflowReadinessBreakdown GetReadinessBreakdown(List<WorkflowStepViewModel> steps)
    {
        // Filter to reviewer/release steps only. Developer diagnostics and informational pages
        // must not affect approval counts or release readiness.
        var releaseSteps = steps.Where(IsReleaseReviewStep).ToList();
        var requiredSteps = releaseSteps.Where(s => !s.IsOptional).ToList();

        // Count artifact readiness
        var artifactsLoaded = releaseSteps.Count(s => s.Prerequisites == PrerequisiteState.Available);
        var artifactTotal = releaseSteps.Count;

        // Calculate actual artifact availability (distinct artifacts from required steps)
        var requiredArtifacts = GetRequiredArtifactsForSteps(requiredSteps);
        var loadedArtifactCount = requiredArtifacts.Count(art => steps.Any(s =>
            s.Prerequisites == PrerequisiteState.Available && s.Key.Contains(art)));

        // Count review completion
        var stepsReviewed = requiredSteps.Count(s =>
            s.ReviewState == ReviewState.Reviewed ||
            s.ApprovalState == ApprovalState.Approved);
        var stepsRequiringReview = requiredSteps.Count(s => s.RequiresManualReview);

        // Count approval completion
        var stepsApproved = requiredSteps.Count(s => s.ApprovalState == ApprovalState.Approved);
        var stepsRequiringApproval = requiredSteps.Count(s => s.RequiresApproval);

        // Count blocking issues
        var blockingIssues = requiredSteps.Count(s =>
            s.Status == WorkflowStepStatus.NeedsAttention &&
            !s.IsOptional);

        // Calculate percentages (avoid division by zero)
        var artifactScore = artifactTotal > 0
            ? (int)((artifactsLoaded / (double)artifactTotal) * 100)
            : 100;

        var reviewScore = stepsRequiringReview > 0
            ? (int)((stepsReviewed / (double)stepsRequiringReview) * 100)
            : 100;

        var approvalScore = stepsRequiringApproval > 0
            ? (int)((stepsApproved / (double)stepsRequiringApproval) * 100)
            : 100;

        // Overall readiness: 30% artifacts, 30% reviews, 40% approvals
        var overallReadiness = (int)(
            (artifactScore * 0.30) +
            (reviewScore * 0.30) +
            (approvalScore * 0.40));

        // Ready for release: all required steps approved, no blocking issues
        var readyForRelease =
            artifactScore == 100 &&
            approvalScore == 100 &&
            blockingIssues == 0;

        return new WorkflowReadinessBreakdown
        {
            OverallReadiness = overallReadiness,
            ArtifactReadiness = artifactScore,
            ReviewReadiness = reviewScore,
            ApprovalReadiness = approvalScore,
            ReadyForRelease = readyForRelease,
            ArtifactsLoaded = artifactsLoaded,
            ArtifactTotal = artifactTotal,
            StepsReviewed = stepsReviewed,
            StepsRequiringReview = stepsRequiringReview,
            StepsApproved = stepsApproved,
            StepsRequiringApproval = stepsRequiringApproval,
            BlockingIssues = blockingIssues
        };
    }

    // Helper methods

    private static bool ShouldIncludeInReviewerWorkflow(WorkflowStepDefinition definition, bool hasDataModel)
    {
        if (definition.IsDeveloperOnly)
            return false;

        if (definition.StepKey == "DataModelExplorer" && !hasDataModel)
            return false;

        return true;
    }

    private static bool IsReleaseReviewStep(WorkflowStepViewModel step)
    {
        if (step.Key is "LoadSampleProject" or "Dashboard" or "ReviewContextValidation")
            return false;

        return step.RequiresManualReview || step.RequiresApproval;
    }

    private WorkflowStepStatus ComputeStepStatus(
        WorkflowStepDefinition definition,
        bool requiredArtifactsLoaded,
        bool approvalDepsMet,
        WorkspaceReviewProgress? progress)
    {
        // If required artifacts or approval dependencies not met, step is locked
        if (!requiredArtifactsLoaded || !approvalDepsMet)
            return WorkflowStepStatus.Locked;

        // If no progress record yet, step is available
        if (progress == null)
            return WorkflowStepStatus.Available;

        // Compute status from persisted approval/review decisions
        return progress.ApprovalState switch
        {
            ApprovalState.Approved => WorkflowStepStatus.Approved,
            ApprovalState.NeedsChanges => WorkflowStepStatus.NeedsAttention,
            ApprovalState.InvalidatedByArtifactChange => WorkflowStepStatus.NeedsAttention,
            ApprovalState.Pending => progress.ReviewState switch
            {
                ReviewState.NotStarted => WorkflowStepStatus.Available,
                ReviewState.InProgress => WorkflowStepStatus.InProgress,
                ReviewState.Reviewed => WorkflowStepStatus.Reviewed,
                _ => WorkflowStepStatus.Available
            },
            _ => WorkflowStepStatus.Available
        };
    }

    private bool ShouldInvalidateStep(string stepKey, List<string> changedArtifactTypes)
    {
        if (!StepDependencies.TryGetValue(stepKey, out var dependencies))
            return false;

        return dependencies.Any(dep => changedArtifactTypes.Contains(dep));
    }

    private async Task<WorkspaceReviewProgress> GetOrCreateProgressAsync(Guid workspaceId, string stepKey)
    {
        var existing = await _db.WorkspaceReviewProgress
            .FirstOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.StepKey == stepKey);

        if (existing != null)
            return existing;

        var newProgress = new WorkspaceReviewProgress
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            StepKey = stepKey,
            ReviewState = ReviewState.NotStarted,
            ApprovalState = ApprovalState.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.WorkspaceReviewProgress.Add(newProgress);
        await _db.SaveChangesAsync();
        return newProgress;
    }

    private List<string> GetRequiredArtifactsForSteps(List<WorkflowStepViewModel> steps)
    {
        // Map step keys to their associated artifact types
        var stepToArtifact = new Dictionary<string, string>
        {
            { "ConstitutionExplorer", "Constitution" },
            { "SpecificationExplorer", "Specification" },
            { "PlanExplorer", "Plan" },
            { "TaskExplorer", "Tasks" },
            { "DataModelExplorer", "DataModel" },
            { "SpecificationReview", "Specification" },
        };

        return steps
            .Where(s => stepToArtifact.ContainsKey(s.Key))
            .Select(s => stepToArtifact[s.Key])
            .Distinct()
            .ToList();
    }
}
