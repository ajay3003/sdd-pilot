using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>
/// Service for managing Recommended Workflow state and approvals.
/// Determines step status based on artifacts, review state, and approvals.
/// Persists only human decisions via WorkspaceReviewProgress, each bound to the exact artifact revisions the step reads
/// (<see cref="WorkflowArtifactBinding"/>): a decision is current only while those revisions are unchanged.
/// Computes Available/Locked/NotApplicable/Stale status at runtime from WorkflowDefinitions, artifact availability and the
/// revisions the client reads now.
/// </summary>
public interface IRecommendedWorkflowService
{
    /// <summary>
    /// Build workflow steps for a workspace with current state. <paramref name="artifacts"/> names the selected artifact
    /// revision of each available role; a decision recorded on other revisions is reported as stale, never as current.
    /// </summary>
    Task<List<WorkflowStepViewModel>> BuildWorkflowStepsAsync(
        Guid workspaceId,
        bool hasConstitution,
        bool hasSpecification,
        bool hasPlan,
        bool hasTasks,
        bool hasDataModel,
        IReadOnlyCollection<ArtifactRevisionRef>? artifacts = null);

    /// <summary>
    /// Mark a step as in-progress (user opened the page). Not a decision: it never makes a step reviewed.
    /// </summary>
    Task MarkStepInProgressAsync(Guid workspaceId, string stepKey, string? userId = null);

    /// <summary>
    /// Record that the reviewer inspected the step's exact artifact revisions. No approval is implied.
    /// </summary>
    Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, IReadOnlyCollection<ArtifactRevisionRef>? artifacts, string? comment = null, string? userId = null);

    /// <summary>
    /// Approve the step's exact artifact revisions.
    /// </summary>
    Task ApproveStepAsync(Guid workspaceId, string stepKey, IReadOnlyCollection<ArtifactRevisionRef>? artifacts, string? comment = null, string? userId = null);

    /// <summary>
    /// Return the step's exact artifact revisions for changes.
    /// </summary>
    Task RejectStepAsync(Guid workspaceId, string stepKey, IReadOnlyCollection<ArtifactRevisionRef>? artifacts, string? comment = null, string? userId = null);

    /// <summary>
    /// Invalidate approvals for steps that depend on changed artifacts.
    /// </summary>
    Task InvalidateArtifactDependentApprovalsAsync(
        Guid workspaceId,
        List<string> changedArtifactTypes,
        string currentArtifactSetHash);

    /// <summary>
    /// The latest review/approval progress for a step, on any revision.
    /// </summary>
    Task<WorkspaceReviewProgress?> GetReviewProgressAsync(Guid workspaceId, string stepKey);

    /// <summary>
    /// Get all review progress for a workspace, including decisions on earlier revisions.
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
        { "ArtifactTraceability", new[] { "Constitution", "Specification", "Plan", "Tasks" } },
        { "ImplementationReview", new[] { "Specification", "Tasks" } },
        { "ReviewContextValidation", Array.Empty<string>() },
        { "Dashboard", Array.Empty<string>() }
    };

    // Approval dependencies per step (steps that must be approved first)
    private static readonly Dictionary<string, List<string>> ApprovalDependencies = new()
    {
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
        bool hasDataModel,
        IReadOnlyCollection<ArtifactRevisionRef>? artifacts = null)
    {
        artifacts ??= Array.Empty<ArtifactRevisionRef>();

        // Every decision of this workspace, on any revision: the current one is the row bound to today's revisions.
        var progressRecords = await _db.WorkspaceReviewProgress
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync();
        var progressByStep = progressRecords.GroupBy(p => p.StepKey).ToDictionary(g => g.Key, g => g.ToList());

        // Map artifact availability
        var loadedArtifacts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
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

        bool Applies(WorkflowStepDefinition definition) =>
            definition.RequiredArtifacts.All(art => loadedArtifacts.TryGetValue(art, out var loaded) && loaded);

        var bindings = visibleDefinitions.ToDictionary(d => d.StepKey, d => WorkflowArtifactBinding.For(d, loadedArtifacts, artifacts));

        WorkspaceReviewProgress? CurrentDecision(string stepKey) =>
            bindings.TryGetValue(stepKey, out var binding) && binding is not null && progressByStep.TryGetValue(stepKey, out var rows)
                ? rows.FirstOrDefault(r => r.ArtifactSetHash == binding.Hash)
                : null;

        for (int index = 0; index < visibleDefinitions.Count; index++)
        {
            var definition = visibleDefinitions[index];
            var visibleNumber = index + 1;  // Renumber based on visible steps to avoid gaps

            // Required artifacts decide applicability; optional artifacts never block.
            var applies = Applies(definition);
            var binding = bindings[definition.StepKey];
            var reviewStep = definition.RequiresApproval || definition.RequiresManualReview;

            // An approval dependency blocks only while the step it names applies and is not approved on its current revisions.
            var approvalDepsRequired = ApprovalDependencies.TryGetValue(definition.StepKey, out var deps) ? deps : new List<string>();
            var unmetDependency = approvalDepsRequired
                .Select(WorkflowDefinitions.GetDefinition)
                .FirstOrDefault(dep => dep is not null && visibleDefinitions.Contains(dep) && Applies(dep)
                                       && CurrentDecision(dep.StepKey)?.ApprovalState != ApprovalState.Approved);

            // Stale: nothing decided on the current revisions, but a decision exists on an earlier revision of the same artifacts
            // (or a legacy decision that named no revision). Decisions on other artifacts of the role are not carried over.
            var current = CurrentDecision(definition.StepKey);
            var previous = current is null && binding is not null && progressByStep.TryGetValue(definition.StepKey, out var history)
                ? history.Where(r => IsDecision(r) && (r.ArtifactIdentityHash is null || r.ArtifactIdentityHash == binding.IdentityHash))
                    .OrderByDescending(r => r.UpdatedAt).FirstOrDefault()
                : null;

            var status = !applies ? WorkflowStepStatus.NotApplicable
                : reviewStep && binding is null ? WorkflowStepStatus.Locked
                : unmetDependency is not null ? WorkflowStepStatus.Locked
                : current is not null ? ComputeStepStatus(current)
                : previous is not null ? WorkflowStepStatus.Stale
                : WorkflowStepStatus.Available;

            var isAvailable = status switch
            {
                WorkflowStepStatus.Available or
                WorkflowStepStatus.InProgress or
                WorkflowStepStatus.Reviewed or
                WorkflowStepStatus.Approved or
                WorkflowStepStatus.NeedsAttention or
                WorkflowStepStatus.Stale => true,
                _ => false
            };

            // The current step is the first applicable review step that is open, still unapproved, needs changes or is stale.
            var isCurrent = !currentStepAssigned &&
                isAvailable &&
                status != WorkflowStepStatus.Approved &&
                reviewStep;

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
                Prerequisites = applies ? PrerequisiteState.Available : PrerequisiteState.Missing,
                ReviewState = current?.ReviewState ?? ReviewState.NotStarted,
                ApprovalState = current?.ApprovalState ?? ApprovalState.Pending,
                CanOpen = isAvailable,
                DisabledReason = !applies
                    ? $"Requires {string.Join(", ", definition.RequiredArtifacts.Where(a => !loadedArtifacts.GetValueOrDefault(a)))}"
                    : reviewStep && binding is null
                    ? $"Choose the {UnidentifiedRole(definition, loadedArtifacts, artifacts)} artifact to review first"
                    : unmetDependency is not null
                    ? $"Approve {unmetDependency.Title} first"
                    : "",
                IsCurrent = isCurrent,
                IsFuture = !isAvailable,
                IsOptional = definition.IsOptional,
                RequiresApproval = definition.RequiresApproval,
                RequiresManualReview = definition.RequiresManualReview,
                RequiredArtifacts = definition.RequiredArtifacts.ToList(),
                ArtifactRoles = WorkflowArtifactBinding.RolesOf(definition).ToList(),
                StepType = definition.StepType.ToString(),
                ArtifactSetHash = applies && binding is not null && binding != WorkflowArtifactBinding.NoArtifacts ? binding.Hash : null,
                ArtifactReferences = applies ? binding?.References : null,
                DecidedAt = current is null ? null : DecidedAt(current),
                PreviousDecision = previous is null ? null : DecisionLabel(previous),
                PreviousDecisionAt = previous is null ? null : DecidedAt(previous),
                PreviousArtifactReferences = previous?.ArtifactReferences,
            };

            viewModels.Add(vm);
        }

        return viewModels;
    }

    public async Task MarkStepInProgressAsync(Guid workspaceId, string stepKey, string? userId = null)
    {
        var progress = await GetOrCreateProgressAsync(workspaceId, stepKey, null);
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

    public async Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, IReadOnlyCollection<ArtifactRevisionRef>? artifacts, string? comment = null, string? userId = null)
    {
        if (workspaceId == Guid.Empty)
            throw new InvalidOperationException("Workflow operations require a saved workspace. Save the workspace first before marking steps as reviewed.");

        var binding = DecisionBinding(stepKey, artifacts);
        var progress = await GetOrCreateProgressAsync(workspaceId, stepKey, binding);
        progress.ReviewState = ReviewState.Reviewed;
        progress.ReviewedAt = DateTimeOffset.UtcNow;
        progress.ReviewedBy = userId ?? "Local Developer";
        progress.ArtifactSetHashAtReview = binding.Hash;
        if (!string.IsNullOrWhiteSpace(comment))
            progress.Comment = comment;
        progress.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewProgress.Update(progress);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Marked step {StepKey} as reviewed for workspace {WorkspaceId} ({Artifacts})", stepKey, workspaceId, binding.References);
    }

    public async Task ApproveStepAsync(Guid workspaceId, string stepKey, IReadOnlyCollection<ArtifactRevisionRef>? artifacts, string? comment = null, string? userId = null)
    {
        if (workspaceId == Guid.Empty)
            throw new InvalidOperationException("Workflow operations require a saved workspace. Save the workspace first before approving steps.");

        var binding = DecisionBinding(stepKey, artifacts);
        var progress = await GetOrCreateProgressAsync(workspaceId, stepKey, binding);
        progress.ReviewState = ReviewState.Reviewed;
        progress.ApprovalState = ApprovalState.Approved;
        progress.ApprovedAt = DateTimeOffset.UtcNow;
        progress.ApprovedBy = userId ?? "Local Developer";
        progress.ArtifactSetHashAtApproval = binding.Hash;
        if (!string.IsNullOrWhiteSpace(comment))
            progress.Comment = comment;
        progress.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewProgress.Update(progress);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Approved step {StepKey} for workspace {WorkspaceId} ({Artifacts})", stepKey, workspaceId, binding.References);
    }

    public async Task RejectStepAsync(Guid workspaceId, string stepKey, IReadOnlyCollection<ArtifactRevisionRef>? artifacts, string? comment = null, string? userId = null)
    {
        if (workspaceId == Guid.Empty)
            throw new InvalidOperationException("Workflow operations require a saved workspace. Save the workspace first before rejecting steps.");

        var binding = DecisionBinding(stepKey, artifacts);
        var progress = await GetOrCreateProgressAsync(workspaceId, stepKey, binding);
        progress.ReviewState = ReviewState.Reviewed;
        progress.ApprovalState = ApprovalState.NeedsChanges;
        progress.RejectedAt = DateTimeOffset.UtcNow;
        progress.RejectedBy = userId ?? "Local Developer";
        if (!string.IsNullOrWhiteSpace(comment))
            progress.Comment = comment;
        progress.UpdatedAt = DateTimeOffset.UtcNow;

        _db.WorkspaceReviewProgress.Update(progress);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Rejected step {StepKey} for workspace {WorkspaceId} ({Artifacts})", stepKey, workspaceId, binding.References);
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
            .Where(p => p.WorkspaceId == workspaceId && p.StepKey == stepKey)
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefaultAsync();
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
        // Filter to reviewer/release steps that apply. Developer diagnostics, informational pages and steps whose required
        // artifacts are absent must not affect approval counts or release readiness.
        var releaseSteps = steps.Where(s => IsReleaseReviewStep(s) && s.Status != WorkflowStepStatus.NotApplicable).ToList();
        var requiredSteps = releaseSteps.Where(s => !s.IsOptional).ToList();

        // Count artifact readiness
        var artifactsLoaded = releaseSteps.Count(s => s.Prerequisites == PrerequisiteState.Available);
        var artifactTotal = releaseSteps.Count;

        // Count review completion
        var stepsReviewed = requiredSteps.Count(s =>
            s.ReviewState == ReviewState.Reviewed ||
            s.ApprovalState == ApprovalState.Approved);
        var stepsRequiringReview = requiredSteps.Count(s => s.RequiresManualReview);

        // Count approval completion
        var stepsApproved = requiredSteps.Count(s => s.ApprovalState == ApprovalState.Approved);
        var stepsRequiringApproval = requiredSteps.Count(s => s.RequiresApproval);

        // Count blocking issues: needs changes or a stale decision on a required step
        var blockingIssues = requiredSteps.Count(s =>
            s.Status is WorkflowStepStatus.NeedsAttention or WorkflowStepStatus.Stale);

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

    /// <summary>
    /// The binding a decision is recorded against. The decision must name an identified revision of every role the step
    /// requires; otherwise there is nothing exact to decide on and the request is refused.
    /// </summary>
    private static WorkflowArtifactBinding DecisionBinding(string stepKey, IReadOnlyCollection<ArtifactRevisionRef>? artifacts)
    {
        var definition = WorkflowDefinitions.GetDefinition(stepKey)
            ?? throw new InvalidOperationException($"Unknown workflow step '{stepKey}'.");
        if (!definition.RequiresApproval && !definition.RequiresManualReview)
            throw new InvalidOperationException($"{definition.Title} is not a review step.");

        artifacts ??= Array.Empty<ArtifactRevisionRef>();
        var named = artifacts.Where(a => !string.IsNullOrWhiteSpace(a.ArtifactId) && !string.IsNullOrWhiteSpace(a.Fingerprint))
            .Select(a => a.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unnamed = definition.RequiredArtifacts.Where(role => !named.Contains(role)).ToList();
        if (unnamed.Count > 0)
            throw new InvalidOperationException(
                $"A decision on {definition.Title} must name the exact revision of its {string.Join(", ", unnamed)} artifact.");

        var available = WorkflowArtifactBinding.RolesOf(definition)
            .ToDictionary(role => role, role => named.Contains(role), StringComparer.OrdinalIgnoreCase);
        return WorkflowArtifactBinding.For(definition, available, artifacts)
            ?? throw new InvalidOperationException($"A decision on {definition.Title} must name the exact artifact revision it is about.");
    }

    private static string UnidentifiedRole(WorkflowStepDefinition definition, IReadOnlyDictionary<string, bool> available,
        IReadOnlyCollection<ArtifactRevisionRef> artifacts) =>
        WorkflowArtifactBinding.RolesOf(definition)
            .FirstOrDefault(role => available.GetValueOrDefault(role)
                                    && !artifacts.Any(a => string.Equals(a.Role, role, StringComparison.OrdinalIgnoreCase)))
        ?? definition.RequiredArtifacts.FirstOrDefault() ?? "artifact";

    /// <summary>A human decision (reviewed, approved, needs changes). Opening a step is not one.</summary>
    private static bool IsDecision(WorkspaceReviewProgress progress) =>
        progress.ApprovalState != ApprovalState.Pending || progress.ReviewState == ReviewState.Reviewed;

    private static string DecisionLabel(WorkspaceReviewProgress progress) => progress.ApprovalState switch
    {
        ApprovalState.Approved or ApprovalState.InvalidatedByArtifactChange => "Approved",
        ApprovalState.NeedsChanges => "Needs changes",
        _ => "Reviewed",
    };

    private static DateTimeOffset? DecidedAt(WorkspaceReviewProgress progress) => progress.ApprovalState switch
    {
        ApprovalState.Approved or ApprovalState.InvalidatedByArtifactChange => progress.ApprovedAt ?? progress.UpdatedAt,
        ApprovalState.NeedsChanges => progress.RejectedAt ?? progress.UpdatedAt,
        _ => progress.ReviewState == ReviewState.Reviewed ? progress.ReviewedAt ?? progress.UpdatedAt : null,
    };

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

    /// <summary>Status from the decision recorded on the step's current artifact revisions.</summary>
    private static WorkflowStepStatus ComputeStepStatus(WorkspaceReviewProgress progress) => progress.ApprovalState switch
    {
        ApprovalState.Approved => WorkflowStepStatus.Approved,
        ApprovalState.NeedsChanges => WorkflowStepStatus.NeedsAttention,
        ApprovalState.InvalidatedByArtifactChange => WorkflowStepStatus.Stale,
        ApprovalState.Pending => progress.ReviewState switch
        {
            ReviewState.NotStarted => WorkflowStepStatus.Available,
            ReviewState.InProgress => WorkflowStepStatus.InProgress,
            ReviewState.Reviewed => WorkflowStepStatus.Reviewed,
            _ => WorkflowStepStatus.Available
        },
        _ => WorkflowStepStatus.Available
    };

    private bool ShouldInvalidateStep(string stepKey, List<string> changedArtifactTypes)
    {
        if (!StepDependencies.TryGetValue(stepKey, out var dependencies))
            return false;

        return dependencies.Any(dep => changedArtifactTypes.Contains(dep));
    }

    private async Task<WorkspaceReviewProgress> GetOrCreateProgressAsync(Guid workspaceId, string stepKey, WorkflowArtifactBinding? binding)
    {
        var hash = binding?.Hash;
        var existing = await _db.WorkspaceReviewProgress
            .FirstOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.StepKey == stepKey && p.ArtifactSetHash == hash);

        if (existing != null)
            return existing;

        var newProgress = new WorkspaceReviewProgress
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            StepKey = stepKey,
            ArtifactSetHash = hash,
            ArtifactIdentityHash = binding?.IdentityHash,
            ArtifactReferences = binding?.References,
            ReviewState = ReviewState.NotStarted,
            ApprovalState = ApprovalState.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.WorkspaceReviewProgress.Add(newProgress);
        await _db.SaveChangesAsync();
        return newProgress;
    }
}
