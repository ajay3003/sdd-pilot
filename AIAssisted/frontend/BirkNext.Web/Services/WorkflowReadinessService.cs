namespace BirkNext.Web.Services;

public interface IWorkflowReadinessService
{
    event Action? ReadinessChanged;

    Task<WorkflowReadiness> GetReadinessAsync();
}

public sealed record WorkflowReadiness(
    WorkflowWorkspace? CurrentWorkspace,
    bool WorkspaceLoaded,
    string WorkspaceName,
    string ProjectName,
    string WorkspaceStatus,
    string WorkspaceStatusClass,
    DateTimeOffset? LastSavedAt,
    string LastSavedText,
    WorkspaceArtifactStatus ArtifactStatus,
    IReadOnlyList<WorkflowArtifactReadiness> Artifacts,
    WorkflowStepViewModel? SpecificationReviewState,
    WorkflowStepViewModel? TraceabilityState,
    WorkflowStepViewModel? ImplementationReviewState,
    WorkflowStepViewModel? QualityGateState,
    WorkflowStepViewModel? NextRecommendedAction,
    WorkflowReadinessBreakdown OverallReadiness,
    IReadOnlyList<WorkflowStepViewModel> Steps,
    bool CanRelease,
    string ReleaseReason,
    IReadOnlyList<string> Warnings);

public sealed record WorkflowWorkspace(
    Guid? WorkspaceId,
    string WorkspaceName,
    string? ProjectName,
    int ArtifactCount,
    DateTimeOffset? LoadedAt,
    string? ArtifactSetHash,
    bool AutoSaved);

public sealed record WorkflowArtifactReadiness(string Name, bool IsLoaded);

public sealed class WorkflowReadinessService : IWorkflowReadinessService, IDisposable
{
    private readonly IWorkspaceArtifactStatusService _artifactStatus;
    private readonly IWorkspaceSessionRestoreService _workspaceRestore;
    private readonly IWorkspaceSessionService _workspaceSession;
    private readonly IWorkspaceUpdateCoordinator _updates;
    private readonly IWorkspacePersistenceApiService _workspacePersistence;
    private readonly IRecommendedWorkflowApiService _workflowApi;
    private readonly ILogger<WorkflowReadinessService> _logger;

    public event Action? ReadinessChanged;

    public WorkflowReadinessService(
        IWorkspaceArtifactStatusService artifactStatus,
        IWorkspaceSessionRestoreService workspaceRestore,
        IWorkspaceSessionService workspaceSession,
        IWorkspaceUpdateCoordinator updates,
        IWorkspacePersistenceApiService workspacePersistence,
        IRecommendedWorkflowApiService workflowApi,
        ILogger<WorkflowReadinessService> logger)
    {
        _artifactStatus = artifactStatus;
        _workspaceRestore = workspaceRestore;
        _workspaceSession = workspaceSession;
        _updates = updates;
        _workspacePersistence = workspacePersistence;
        _workflowApi = workflowApi;
        _logger = logger;

        _artifactStatus.StatusChanged += OnReadinessChanged;
        _workspaceRestore.ReviewContextRebuildNeeded += OnReviewContextRebuildNeeded;
        _workspaceSession.ReviewContextRebuildNeeded += OnReviewContextRebuildNeeded;
        _updates.ArtifactsChanged += OnArtifactsChanged;
    }

    public async Task<WorkflowReadiness> GetReadinessAsync()
    {
        var artifactStatus = _artifactStatus.GetStatus();
        var metadata = await _workspaceRestore.GetCurrentWorkspaceMetadataAsync();
        var persistedState = await _workspacePersistence.GetCurrentStateAsync();
        var workspaceLoaded = metadata is not null || artifactStatus.ArtifactCount > 0;
        var workspace = BuildWorkspace(metadata, persistedState, artifactStatus, workspaceLoaded);

        if (!workspaceLoaded)
        {
            return CreateEmptyReadiness(artifactStatus);
        }

        var workspaceId = workspace?.WorkspaceId ?? Guid.Empty;
        var steps = await _workflowApi.BuildWorkflowStepsAsync(
            workspaceId,
            artifactStatus.HasConstitution,
            artifactStatus.HasSpecification,
            artifactStatus.HasPlan,
            artifactStatus.HasTasks,
            artifactStatus.HasDataModel) ?? [];
        steps = steps.Where(IsVisibleWorkflowStep).ToList();

        var specificationState = FindStep(steps, "Specification");
        var traceabilityState = FindStep(steps, "Traceability");
        var implementationState = FindStep(steps, "Implementation");
        var qualityGateState = FindStep(steps, "Quality");
        var nextAction = steps.FirstOrDefault(step => step.IsCurrent && IsReleaseReviewStep(step))
            ?? steps.FirstOrDefault(step => IsReleaseReviewStep(step) && step.Status != WorkflowStepStatus.Approved);
        var canRelease = artifactStatus.IsFullyLoaded
            && IsApproved(specificationState)
            && IsApproved(traceabilityState)
            && IsApproved(implementationState)
            && IsQualityGatePassed(qualityGateState);

        var warnings = new List<string>();
        if (metadata is null && artifactStatus.ArtifactCount > 0)
        {
            warnings.Add("Artifacts are loaded in the current session, but the workspace has not been saved yet.");
        }

        var workspaceStatus = persistedState?.Status ?? (metadata?.AutoSaved == true ? "AutoSaved" : "Not Saved");
        var lastSavedAt = persistedState?.LastSavedAt ?? metadata?.LoadedAt;

        return new WorkflowReadiness(
            CurrentWorkspace: workspace,
            WorkspaceLoaded: true,
            WorkspaceName: workspace?.WorkspaceName ?? "Unsaved workspace",
            ProjectName: ResolveProjectName(artifactStatus, metadata, persistedState),
            WorkspaceStatus: workspaceStatus,
            WorkspaceStatusClass: GetStatusClass(workspaceStatus),
            LastSavedAt: lastSavedAt,
            LastSavedText: GetLastSavedText(lastSavedAt),
            ArtifactStatus: artifactStatus,
            Artifacts: BuildArtifactReadiness(artifactStatus),
            SpecificationReviewState: specificationState,
            TraceabilityState: traceabilityState,
            ImplementationReviewState: implementationState,
            QualityGateState: qualityGateState,
            NextRecommendedAction: nextAction,
            OverallReadiness: BuildOverallReadiness(artifactStatus, steps, canRelease),
            Steps: steps,
            CanRelease: canRelease,
            ReleaseReason: canRelease
                ? "Specification reviewed. Traceability approved. Implementation approved. Quality gates passed."
                : "Release is available only after artifacts are loaded and all required review steps are approved.",
            Warnings: warnings);
    }

    private static WorkflowWorkspace? BuildWorkspace(
        CurrentWorkspaceMetadata? metadata,
        CurrentWorkspaceStateDto? persistedState,
        WorkspaceArtifactStatus artifactStatus,
        bool workspaceLoaded)
    {
        if (!workspaceLoaded)
        {
            return null;
        }

        return new WorkflowWorkspace(
            WorkspaceId: metadata?.WorkspaceId ?? persistedState?.CurrentWorkspaceId,
            WorkspaceName: metadata?.WorkspaceName
                ?? persistedState?.WorkspaceName
                ?? ResolveProjectName(artifactStatus, metadata, persistedState),
            ProjectName: metadata?.ProjectName ?? persistedState?.ProjectName ?? artifactStatus.ActiveProjectName,
            ArtifactCount: artifactStatus.ArtifactCount,
            LoadedAt: metadata?.LoadedAt ?? persistedState?.LastSavedAt,
            ArtifactSetHash: metadata?.ArtifactSetHash,
            AutoSaved: metadata?.AutoSaved ?? false);
    }

    private static WorkflowReadiness CreateEmptyReadiness(WorkspaceArtifactStatus artifactStatus)
    {
        var emptyAction = new WorkflowStepViewModel
        {
            Number = 1,
            Key = "LoadWorkspace",
            Title = "Load Sample Project",
            Description = "Load a sample project or resume a saved workspace to begin the review workflow.",
            Route = "sample-projects",
            ActionLabel = "Load Sample Project",
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

        return new WorkflowReadiness(
            CurrentWorkspace: null,
            WorkspaceLoaded: false,
            WorkspaceName: "No workspace loaded",
            ProjectName: "No project loaded",
            WorkspaceStatus: "Not Saved",
            WorkspaceStatusClass: GetStatusClass("Not Saved"),
            LastSavedAt: null,
            LastSavedText: "-",
            ArtifactStatus: artifactStatus,
            Artifacts: BuildArtifactReadiness(artifactStatus),
            SpecificationReviewState: null,
            TraceabilityState: null,
            ImplementationReviewState: null,
            QualityGateState: null,
            NextRecommendedAction: emptyAction,
            OverallReadiness: new WorkflowReadinessBreakdown(),
            Steps: [emptyAction],
            CanRelease: false,
            ReleaseReason: "Load a workspace before release readiness can be evaluated.",
            Warnings: []);
    }

    private static IReadOnlyList<WorkflowArtifactReadiness> BuildArtifactReadiness(WorkspaceArtifactStatus status) =>
    [
        new("Constitution", status.HasConstitution),
        new("Specification", status.HasSpecification),
        new("Plan", status.HasPlan),
        new("Tasks", status.HasTasks),
        new("Data Model", status.HasDataModel)
    ];

    private static WorkflowStepViewModel? FindStep(IReadOnlyList<WorkflowStepViewModel> steps, string token) =>
        steps.FirstOrDefault(step =>
            step.Title.Contains(token, StringComparison.OrdinalIgnoreCase)
            || step.Key.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool IsApproved(WorkflowStepViewModel? step) =>
        step?.ApprovalState == ApprovalState.Approved;

    private static bool IsQualityGatePassed(WorkflowStepViewModel? step) =>
        step is null
        || step.Status == WorkflowStepStatus.Approved
        || (!step.RequiresApproval && step.CanOpen && step.Status != WorkflowStepStatus.Locked);

    private static bool IsVisibleWorkflowStep(WorkflowStepViewModel step) =>
        !step.Key.Equals("ReviewContextValidation", StringComparison.OrdinalIgnoreCase);

    private static bool IsReleaseReviewStep(WorkflowStepViewModel step) =>
        !step.Key.Equals("LoadSampleProject", StringComparison.OrdinalIgnoreCase)
        && !step.Key.Equals("LoadWorkspace", StringComparison.OrdinalIgnoreCase)
        && !step.Key.Equals("Dashboard", StringComparison.OrdinalIgnoreCase)
        && !step.Key.Equals("ReviewContextValidation", StringComparison.OrdinalIgnoreCase)
        && (step.RequiresManualReview || step.RequiresApproval);

    private static WorkflowReadinessBreakdown BuildOverallReadiness(
        WorkspaceArtifactStatus artifactStatus,
        IReadOnlyList<WorkflowStepViewModel> steps,
        bool canRelease)
    {
        var requiredSteps = steps.Where(step => IsReleaseReviewStep(step) && !step.IsOptional).ToList();
        var approvedSteps = requiredSteps.Count(step => step.ApprovalState == ApprovalState.Approved);
        var approvalReadiness = requiredSteps.Count == 0 ? 0 : approvedSteps * 100 / requiredSteps.Count;

        return new WorkflowReadinessBreakdown
        {
            ArtifactReadiness = artifactStatus.ArtifactCount * 20,
            ReviewReadiness = approvalReadiness,
            ApprovalReadiness = approvalReadiness,
            OverallReadiness = approvalReadiness
        };
    }

    private static string ResolveProjectName(
        WorkspaceArtifactStatus artifactStatus,
        CurrentWorkspaceMetadata? metadata,
        CurrentWorkspaceStateDto? persistedState) =>
        FirstNonBlank(metadata?.ProjectName, persistedState?.ProjectName, artifactStatus.ActiveProjectName, "No project loaded");

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string GetStatusClass(string? status) => status switch
    {
        "Saved" => "status-saved",
        "AutoSaved" => "status-auto-saved",
        "UnsavedChanges" => "status-unsaved",
        _ => "status-not-saved"
    };

    private static string GetLastSavedText(DateTimeOffset? lastSavedAt)
    {
        if (lastSavedAt is null) return "-";

        var elapsed = DateTimeOffset.UtcNow - lastSavedAt.Value;
        return elapsed.TotalSeconds < 60
            ? "just now"
            : elapsed.TotalMinutes < 60
                ? $"{(int)elapsed.TotalMinutes}m ago"
                : elapsed.TotalHours < 24
                    ? $"{(int)elapsed.TotalHours}h ago"
                    : $"{(int)elapsed.TotalDays}d ago";
    }

    private void OnReadinessChanged() => ReadinessChanged?.Invoke();

    private void OnReviewContextRebuildNeeded(object? sender, EventArgs e) => OnReadinessChanged();

    private void OnArtifactsChanged(object? sender, EventArgs e) => OnReadinessChanged();

    public void Dispose()
    {
        _artifactStatus.StatusChanged -= OnReadinessChanged;
        _workspaceRestore.ReviewContextRebuildNeeded -= OnReviewContextRebuildNeeded;
        _workspaceSession.ReviewContextRebuildNeeded -= OnReviewContextRebuildNeeded;
        _updates.ArtifactsChanged -= OnArtifactsChanged;
    }
}
