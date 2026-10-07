namespace BirkNext.Web.Services;

public interface IWorkflowReadinessService
{
    event Action? ReadinessChanged;

    Task<WorkflowReadiness> GetReadinessAsync();
}

/// <summary>
/// Workflow readiness for the current workspace. Whether a workspace, project or artifact exists comes from
/// <see cref="CurrentWorkspaceSnapshot"/> (the same read model the Dashboard uses); this record only adds the review steps,
/// the next recommended action and release readiness.
/// </summary>
public sealed record WorkflowReadiness(
    CurrentWorkspaceSnapshot Workspace,
    WorkflowStepViewModel? SpecificationExplorerState,
    WorkflowStepViewModel? TraceabilityState,
    WorkflowStepViewModel? ImplementationReviewState,
    WorkflowStepViewModel? QualityGateState,
    WorkflowStepViewModel? NextRecommendedAction,
    WorkflowReadinessBreakdown OverallReadiness,
    int RequiredReviewCount,
    int ApprovedReviewCount,
    IReadOnlyList<WorkflowStepViewModel> Steps,
    bool CanRelease,
    string ReleaseReason)
{
    public bool WorkspaceLoaded => Workspace.WorkspaceLoaded;
    public bool WorkspaceError => Workspace.State == CurrentWorkspaceState.Error;
    public string WorkspaceName => Workspace.WorkspaceName;
    public string ProjectName => Workspace.ProjectDisplay;
    public Guid? WorkspaceId => Workspace.WorkspaceId;

    /// <summary>
    /// Approved share of the required review steps, or null when nothing has been assessed yet (no required step, or no step
    /// reviewed, approved or rejected). Not assessed is never shown as 0%.
    /// </summary>
    public int? ReleaseReadinessPercent { get; init; }

    public string LastSavedText => Workspace.LastSavedAt is { } at ? FormatElapsed(at) : "-";

    private static string FormatElapsed(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.UtcNow - at;
        return elapsed.TotalSeconds < 60 ? "just now"
            : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
            : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
            : $"{(int)elapsed.TotalDays}d ago";
    }
}

public sealed class WorkflowReadinessService : IWorkflowReadinessService, IDisposable
{
    public const string LoadWorkspaceKey = "LoadWorkspace";
    public const string AddArtifactsKey = "AddArtifacts";
    public const string ChooseArtifactsKey = "ChooseArtifacts";

    private readonly ICurrentWorkspaceProjection _workspace;
    private readonly IRecommendedWorkflowApiService _workflowApi;
    private readonly ILogger<WorkflowReadinessService> _logger;

    public event Action? ReadinessChanged;

    public WorkflowReadinessService(
        ICurrentWorkspaceProjection workspace,
        IRecommendedWorkflowApiService workflowApi,
        ILogger<WorkflowReadinessService> logger)
    {
        _workspace = workspace;
        _workflowApi = workflowApi;
        _logger = logger;
        _workspace.Changed += OnWorkspaceChanged;
    }

    public async Task<WorkflowReadiness> GetReadinessAsync()
    {
        // Recommendations are computed only from a finished workspace snapshot, never from a partially initialized one.
        var workspace = await _workspace.GetAsync();

        if (workspace.State == CurrentWorkspaceState.Error)
            return Empty(workspace, null, "The workspace could not be read, so release readiness cannot be evaluated.");

        if (!workspace.WorkspaceLoaded)
        {
            var load = Synthetic(LoadWorkspaceKey, "Load project artifacts",
                "Select a Sample Project, import documents in an explorer, or resume a saved workspace to begin the review workflow.",
                "sample-projects", "Open Sample Projects");
            return Empty(workspace, load, "Load a workspace before release readiness can be evaluated.") with { Steps = [load] };
        }

        var steps = (await _workflowApi.BuildWorkflowStepsAsync(
            workspace.WorkspaceId ?? Guid.Empty,
            workspace.Has(WorkspaceArtifactType.Constitution),
            workspace.Has(WorkspaceArtifactType.Specification),
            workspace.Has(WorkspaceArtifactType.Plan),
            workspace.Has(WorkspaceArtifactType.Tasks),
            workspace.Has(WorkspaceArtifactType.DataModel)) ?? [])
            .Where(IsVisibleWorkflowStep)
            .ToList();

        var specificationState = FindStep(steps, "Specification");
        var traceabilityState = FindStep(steps, "Traceability");
        var implementationState = FindStep(steps, "Implementation");
        var qualityGateState = FindStep(steps, "Quality");

        // Required review steps lock themselves when their artifacts are missing, so an approved, unlocked step implies its artifacts.
        var canRelease = IsApproved(specificationState)
            && IsApproved(traceabilityState)
            && IsApproved(implementationState)
            && IsQualityGatePassed(qualityGateState);

        var required = steps.Where(step => IsReleaseReviewStep(step) && !step.IsOptional).ToList();
        var approved = required.Count(step => step.ApprovalState == ApprovalState.Approved);
        var assessed = required.Any(step => step.ApprovalState != ApprovalState.Pending || step.ReviewState != ReviewState.NotStarted);
        int? releasePercent = required.Count > 0 && assessed ? approved * 100 / required.Count : null;

        return new WorkflowReadiness(
            Workspace: workspace,
            SpecificationExplorerState: specificationState,
            TraceabilityState: traceabilityState,
            ImplementationReviewState: implementationState,
            QualityGateState: qualityGateState,
            NextRecommendedAction: NextAction(workspace, steps),
            OverallReadiness: new WorkflowReadinessBreakdown
            {
                ArtifactReadiness = workspace.AvailableRoleCount * 100 / CurrentWorkspaceSnapshot.WorkflowRoles.Count,
                ReviewReadiness = releasePercent ?? 0,
                ApprovalReadiness = releasePercent ?? 0,
                OverallReadiness = releasePercent ?? 0
            },
            RequiredReviewCount: required.Count,
            ApprovedReviewCount: approved,
            Steps: steps,
            CanRelease: canRelease,
            ReleaseReason: canRelease
                ? "Specification reviewed. Traceability approved. Implementation approved. Quality gates passed."
                : "Release is available only after all required review steps are approved.")
        {
            ReleaseReadinessPercent = releasePercent
        };
    }

    /// <summary>
    /// The next action from the actual workspace state: no artifacts → add them; a role with several artifacts and no choice →
    /// choose one; otherwise the first open review step. Never "Load project artifacts" while the workspace has artifacts.
    /// </summary>
    private static WorkflowStepViewModel? NextAction(CurrentWorkspaceSnapshot workspace, IReadOnlyList<WorkflowStepViewModel> steps)
    {
        if (workspace.AvailableRoleCount == 0 && workspace.Roles.All(r => r.Availability == ArtifactRoleAvailability.Missing))
        {
            return Synthetic(AddArtifactsKey, "Add project artifacts",
                $"Workspace {workspace.WorkspaceName} has no Constitution, Specification, Plan, Tasks or Data Model artifact yet. Import one in an explorer to start the review workflow.",
                "specification-explorer", "Open Specification Explorer");
        }

        if (workspace.Roles.FirstOrDefault(r => r.Selection == ArtifactRoleSelection.SelectionRequired) is { } unresolved)
        {
            return Synthetic(ChooseArtifactsKey, $"Choose the {unresolved.Label} to review",
                $"The workspace has {unresolved.ArtifactCount} {unresolved.Label} artifacts and none is selected. Reviews read the selected one.",
                ExplorerRoute(unresolved.Role), $"Open {unresolved.Label} Explorer");
        }

        return steps.FirstOrDefault(step => step.IsCurrent && IsReleaseReviewStep(step))
            ?? steps.FirstOrDefault(step => IsReleaseReviewStep(step) && step.Status != WorkflowStepStatus.Approved);
    }

    private static string ExplorerRoute(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "constitution-explorer",
        WorkspaceArtifactType.Plan => "plan-explorer",
        WorkspaceArtifactType.Tasks => "task-explorer",
        WorkspaceArtifactType.DataModel => "data-model-explorer",
        _ => "specification-explorer",
    };

    private static WorkflowReadiness Empty(CurrentWorkspaceSnapshot workspace, WorkflowStepViewModel? next, string reason) =>
        new(workspace, null, null, null, null, next, new WorkflowReadinessBreakdown(), 0, 0, [], false, reason);

    private static WorkflowStepViewModel Synthetic(string key, string title, string description, string route, string actionLabel) => new()
    {
        Number = 1,
        Key = key,
        Title = title,
        Description = description,
        Route = route,
        ActionLabel = actionLabel,
        Color = "#0284c7",
        CanOpen = true,
        IsCurrent = true,
        Status = WorkflowStepStatus.Available,
        Prerequisites = PrerequisiteState.Available,
        ReviewState = ReviewState.NotStarted,
        ApprovalState = ApprovalState.Pending,
        RequiresApproval = false,
        RequiresManualReview = false
    };

    private static WorkflowStepViewModel? FindStep(IReadOnlyList<WorkflowStepViewModel> steps, string token) =>
        steps.FirstOrDefault(step =>
            step.Title.Contains(token, StringComparison.OrdinalIgnoreCase)
            || step.Key.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool IsApproved(WorkflowStepViewModel? step) =>
        step?.ApprovalState == ApprovalState.Approved && step.Status != WorkflowStepStatus.Locked;

    private static bool IsQualityGatePassed(WorkflowStepViewModel? step) =>
        step is null
        || step.Status == WorkflowStepStatus.Approved
        || (!step.RequiresApproval && step.CanOpen && step.Status != WorkflowStepStatus.Locked);

    private static bool IsVisibleWorkflowStep(WorkflowStepViewModel step) =>
        !step.Key.Equals("ReviewContextValidation", StringComparison.OrdinalIgnoreCase);

    private static bool IsReleaseReviewStep(WorkflowStepViewModel step) =>
        !step.Key.Equals("LoadSampleProject", StringComparison.OrdinalIgnoreCase)
        && !step.Key.Equals(LoadWorkspaceKey, StringComparison.OrdinalIgnoreCase)
        && !step.Key.Equals("Dashboard", StringComparison.OrdinalIgnoreCase)
        && !step.Key.Equals("ReviewContextValidation", StringComparison.OrdinalIgnoreCase)
        && (step.RequiresManualReview || step.RequiresApproval);

    private void OnWorkspaceChanged() => ReadinessChanged?.Invoke();

    public void Dispose() => _workspace.Changed -= OnWorkspaceChanged;
}
