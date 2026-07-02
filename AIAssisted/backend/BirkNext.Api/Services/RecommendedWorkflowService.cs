using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>
/// Service for managing Recommended Workflow state and approvals.
/// Determines step status based on artifacts, review state, and approvals.
/// Persists approval state per workspace.
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
    /// Get review/approval state for a specific step.
    /// </summary>
    Task<WorkspaceReviewStep?> GetReviewStepAsync(Guid workspaceId, string stepKey);

    /// <summary>
    /// Get all review steps for a workspace.
    /// </summary>
    Task<List<WorkspaceReviewStep>> GetWorkspaceReviewStepsAsync(Guid workspaceId);

    /// <summary>
    /// Determine which step should be current (next recommended action).
    /// </summary>
    WorkflowStepViewModel? GetCurrentRecommendedStep(List<WorkflowStepViewModel> steps);
}

public class RecommendedWorkflowService : IRecommendedWorkflowService
{
    private readonly AppDbContext _db;
    private readonly ILogger<RecommendedWorkflowService> _logger;

    private static readonly Dictionary<string, string[]> StepDependencies = new()
    {
        // LoadSampleProject has no dependencies
        { "LoadSampleProject", Array.Empty<string>() },

        // Explorers depend on their respective artifacts
        { "ConstitutionExplorer", new[] { "Constitution" } },
        { "PlanExplorer", new[] { "Plan" } },
        { "TaskExplorer", new[] { "Tasks" } },
        { "DataModelExplorer", new[] { "DataModel" } },

        // Specification Review depends on spec
        { "SpecificationReview", new[] { "Specification" } },

        // Artifact Traceability depends on multiple artifacts
        { "ArtifactTraceability", new[] { "Constitution", "Specification", "Plan", "Tasks" } },

        // Implementation Review depends on spec and tasks
        { "ImplementationReview", new[] { "Specification", "Tasks" } },

        // These are optional/future
        { "ReviewContextValidation", Array.Empty<string>() },
        { "DashboardReview", Array.Empty<string>() }
    };

    private static readonly Dictionary<string, List<string>> ApprovalDependencies = new()
    {
        // Artifact Traceability needs prior exploration approvals
        { "ArtifactTraceability", new[] { "SpecificationReview" }.ToList() },

        // Implementation Review needs Artifact Traceability approved
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
        // Get or create review steps for this workspace
        var existingSteps = await _db.WorkspaceReviewSteps
            .Where(r => r.WorkspaceId == workspaceId)
            .ToListAsync();

        var existingLookup = existingSteps.ToDictionary(s => s.StepKey);

        // Map artifact types for dependency checking
        var loadedArtifacts = new[]
        {
            ("Constitution", hasConstitution),
            ("Specification", hasSpecification),
            ("Plan", hasPlan),
            ("Tasks", hasTasks),
            ("DataModel", hasDataModel)
        };

        var viewModels = new List<WorkflowStepViewModel>();
        int stepNumber = 1;
        WorkflowStepViewModel? previousApprovedStep = null;
        var currentStepAssigned = false;

        // Define all possible steps
        var stepDefinitions = new[]
        {
            new { Key = "LoadSampleProject", Title = "Load Sample Project", Description = "Load a sample project or import artifacts to get started", Route = "sample-projects", ActionLabel = "Open Sample Projects", Color = "#15803d" },
            new { Key = "ConstitutionExplorer", Title = "Constitution Explorer", Description = "Review governance rules and quality standards", Route = "constitution-explorer", ActionLabel = "Open Constitution Explorer", Color = "#1e40af" },
            new { Key = "PlanExplorer", Title = "Plan Explorer", Description = "Inspect implementation plan and architecture decisions", Route = "plan-explorer", ActionLabel = "Open Plan Explorer", Color = "#6d28d9" },
            new { Key = "TaskExplorer", Title = "Task Explorer", Description = "Review task coverage and delivery risk", Route = "task-explorer", ActionLabel = "Open Task Explorer", Color = "#b45309" },
            new { Key = "DataModelExplorer", Title = "Data Model Explorer", Description = "Review entities, relationships, and constraints", Route = "data-model-explorer", ActionLabel = "Open Data Model Explorer", Color = "#065f46" },
            new { Key = "SpecificationReview", Title = "Specification Review", Description = "Run analysis to extract requirements and tests", Route = "extract", ActionLabel = "Run Specification Review", Color = "#0f766e" },
            new { Key = "ArtifactTraceability", Title = "Artifact Traceability", Description = "Analyze end-to-end coverage across artifacts", Route = "artifact-traceability", ActionLabel = "Run Artifact Traceability", Color = "#2563eb" },
            new { Key = "ImplementationReview", Title = "Implementation Review", Description = "Validate tasks against spec for alignment gaps", Route = "task-alignment", ActionLabel = "Run Implementation Review", Color = "#c2410c" }
        };

        foreach (var definition in stepDefinitions)
        {
            var reviewStep = existingLookup.TryGetValue(definition.Key, out var existing) ? existing : null;

            // Determine prerequisites
            var requiredArtifacts = StepDependencies.TryGetValue(definition.Key, out var deps) ? deps : Array.Empty<string>();
            var prerequisitesMet = requiredArtifacts.Length == 0 ||
                requiredArtifacts.All(art => loadedArtifacts.Any(la => la.Item1 == art && la.Item2));

            var prerequisiteState = prerequisitesMet ? PrerequisiteState.Available : PrerequisiteState.Missing;

            // Get approval dependencies
            var approvalDeps = ApprovalDependencies.TryGetValue(definition.Key, out var aDeps) ? aDeps : new List<string>();
            var approvalDepsMet = approvalDeps.All(depKey =>
                existingLookup.TryGetValue(depKey, out var depStep) &&
                depStep.ApprovalState == ApprovalState.Approved);

            // Determine workflow status
            var status = DetermineStepStatus(
                prerequisiteMet: prerequisitesMet,
                approvalDepMet: approvalDepsMet,
                reviewStep: reviewStep);

            var isAvailable = status == WorkflowStepStatus.Available || status == WorkflowStepStatus.InProgress ||
                            status == WorkflowStepStatus.Reviewed || status == WorkflowStepStatus.Approved ||
                            status == WorkflowStepStatus.NeedsAttention;

            var isCurrent = !currentStepAssigned && isAvailable && status != WorkflowStepStatus.Approved;
            if (isCurrent)
                currentStepAssigned = true;

            var vm = new WorkflowStepViewModel
            {
                Number = stepNumber++,
                Key = definition.Key,
                Title = definition.Title,
                Description = definition.Description,
                Route = definition.Route,
                ActionLabel = definition.ActionLabel,
                Color = definition.Color,
                Status = status,
                Prerequisites = prerequisiteState,
                ReviewState = reviewStep?.ReviewState ?? ReviewState.NotStarted,
                ApprovalState = reviewStep?.ApprovalState ?? ApprovalState.Pending,
                CanOpen = isAvailable,
                DisabledReason = prerequisiteState == PrerequisiteState.Missing ? "Load required artifacts first" :
                                !approvalDepsMet ? "Complete prerequisite steps first" : "",
                IsCurrent = isCurrent,
                IsFuture = !isAvailable
            };

            viewModels.Add(vm);
        }

        return viewModels;
    }

    public async Task MarkStepInProgressAsync(Guid workspaceId, string stepKey, string? userId = null)
    {
        var step = await GetOrCreateReviewStepAsync(workspaceId, stepKey);
        if (step.ReviewState < ReviewState.InProgress)
        {
            step.ReviewState = ReviewState.InProgress;
            step.LastOpenedAt = DateTimeOffset.UtcNow;
            step.UpdatedAt = DateTimeOffset.UtcNow;
            _db.WorkspaceReviewSteps.Update(step);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Marked step {StepKey} as in progress for workspace {WorkspaceId}", stepKey, workspaceId);
        }
    }

    public async Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, string? comment = null, string? userId = null)
    {
        var step = await GetOrCreateReviewStepAsync(workspaceId, stepKey);
        step.ReviewState = ReviewState.Reviewed;
        step.ReviewedAt = DateTimeOffset.UtcNow;
        step.ReviewedBy = userId ?? "Local Developer";
        if (!string.IsNullOrWhiteSpace(comment))
            step.Comment = comment;
        step.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewSteps.Update(step);
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
        var step = await GetOrCreateReviewStepAsync(workspaceId, stepKey);
        step.ReviewState = ReviewState.Reviewed;
        step.ApprovalState = ApprovalState.Approved;
        step.ApprovedAt = DateTimeOffset.UtcNow;
        step.ApprovedBy = userId ?? "Local Developer";
        step.ArtifactSetHashAtApproval = artifactSetHash;
        if (!string.IsNullOrWhiteSpace(comment))
            step.Comment = comment;
        step.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewSteps.Update(step);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Approved step {StepKey} for workspace {WorkspaceId}", stepKey, workspaceId);
    }

    public async Task RejectStepAsync(
        Guid workspaceId,
        string stepKey,
        string? comment = null,
        string? userId = null)
    {
        var step = await GetOrCreateReviewStepAsync(workspaceId, stepKey);
        step.ApprovalState = ApprovalState.NeedsChanges;
        step.RejectedAt = DateTimeOffset.UtcNow;
        step.RejectedBy = userId ?? "Local Developer";
        if (!string.IsNullOrWhiteSpace(comment))
            step.Comment = comment;
        step.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewSteps.Update(step);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Rejected step {StepKey} for workspace {WorkspaceId}", stepKey, workspaceId);
    }

    public async Task InvalidateArtifactDependentApprovalsAsync(
        Guid workspaceId,
        List<string> changedArtifactTypes,
        string currentArtifactSetHash)
    {
        var steps = await _db.WorkspaceReviewSteps
            .Where(r => r.WorkspaceId == workspaceId && r.ApprovalState == ApprovalState.Approved)
            .ToListAsync();

        var invalidated = new List<string>();

        foreach (var step in steps)
        {
            var shouldInvalidate = ShouldInvalidateStep(step.StepKey, changedArtifactTypes);
            if (shouldInvalidate && step.ArtifactSetHashAtApproval != currentArtifactSetHash)
            {
                step.ApprovalState = ApprovalState.InvalidatedByArtifactChange;
                step.UpdatedAt = DateTimeOffset.UtcNow;
                _db.WorkspaceReviewSteps.Update(step);
                invalidated.Add(step.StepKey);
            }
        }

        if (invalidated.Any())
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation("Invalidated approvals for steps {Steps} due to artifact changes in workspace {WorkspaceId}",
                string.Join(", ", invalidated), workspaceId);
        }
    }

    public async Task<WorkspaceReviewStep?> GetReviewStepAsync(Guid workspaceId, string stepKey)
    {
        return await _db.WorkspaceReviewSteps
            .FirstOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.StepKey == stepKey);
    }

    public async Task<List<WorkspaceReviewStep>> GetWorkspaceReviewStepsAsync(Guid workspaceId)
    {
        return await _db.WorkspaceReviewSteps
            .Where(r => r.WorkspaceId == workspaceId)
            .ToListAsync();
    }

    public WorkflowStepViewModel? GetCurrentRecommendedStep(List<WorkflowStepViewModel> steps)
    {
        return steps.FirstOrDefault(s => s.IsCurrent);
    }

    // Helper methods

    private WorkflowStepStatus DetermineStepStatus(
        bool prerequisiteMet,
        bool approvalDepMet,
        WorkspaceReviewStep? reviewStep)
    {
        if (!prerequisiteMet || !approvalDepMet)
            return WorkflowStepStatus.Locked;

        if (reviewStep == null)
            return WorkflowStepStatus.Available;

        return reviewStep.ApprovalState switch
        {
            ApprovalState.Approved => WorkflowStepStatus.Approved,
            ApprovalState.NeedsChanges => WorkflowStepStatus.NeedsAttention,
            ApprovalState.InvalidatedByArtifactChange => WorkflowStepStatus.NeedsAttention,
            ApprovalState.Pending => reviewStep.ReviewState switch
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

    private async Task<WorkspaceReviewStep> GetOrCreateReviewStepAsync(Guid workspaceId, string stepKey)
    {
        var existing = await _db.WorkspaceReviewSteps
            .FirstOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.StepKey == stepKey);

        if (existing != null)
            return existing;

        var newStep = new WorkspaceReviewStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            StepKey = stepKey,
            StepTitle = GetStepTitle(stepKey),
            PrerequisiteState = PrerequisiteState.Missing,
            ReviewState = ReviewState.NotStarted,
            ApprovalState = ApprovalState.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.WorkspaceReviewSteps.Add(newStep);
        await _db.SaveChangesAsync();
        return newStep;
    }

    private string GetStepTitle(string stepKey)
    {
        return stepKey switch
        {
            "LoadSampleProject" => "Load Sample Project",
            "ConstitutionExplorer" => "Constitution Explorer",
            "PlanExplorer" => "Plan Explorer",
            "TaskExplorer" => "Task Explorer",
            "DataModelExplorer" => "Data Model Explorer",
            "SpecificationReview" => "Specification Review",
            "ArtifactTraceability" => "Artifact Traceability",
            "ImplementationReview" => "Implementation Review",
            "ReviewContextValidation" => "ReviewContext Validation",
            "DashboardReview" => "Dashboard Review",
            _ => stepKey
        };
    }
}
