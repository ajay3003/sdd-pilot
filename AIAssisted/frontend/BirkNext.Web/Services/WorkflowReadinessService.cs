using BirkNext.Applicability;

namespace BirkNext.Web.Services;

public interface IWorkflowReadinessService
{
    event Action? ReadinessChanged;

    Task<WorkflowReadiness> GetReadinessAsync();
}

/// <summary>A review the project's inputs make applicable, in sidebar order, with the lane (sidebar section) it belongs to.</summary>
public sealed record WorkflowReviewOption(string ReviewId, string Label, string Route, string Lane, ApplicabilityStatus Status, string Reason);

/// <summary>
/// Workflow readiness for the current project inputs. Whether a workspace, project or artifact exists comes from
/// <see cref="CurrentWorkspaceSnapshot"/> (the same read model the Dashboard uses); source and target come from the shared
/// applicability state (active Target Environment and its latest Source Analysis snapshot). This record only adds the document
/// review steps, the applicable reviews, the next recommended action and release readiness.
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

    /// <summary>Documents, source and target. Evidence availability, not a score.</summary>
    public ProjectInputs Inputs { get; init; } = ProjectInputPresentation.Build(Workspace, null, null);

    /// <summary>Reviews that currently apply (Applicable or Partial), in sidebar order.</summary>
    public IReadOnlyList<WorkflowReviewOption> ApplicableReviews { get; init; } = [];

    /// <summary>Other useful starts shown beside the recommendation when no input is provided yet (add source, configure a target).</summary>
    public IReadOnlyList<WorkflowStepViewModel> AlternativeActions { get; init; } = [];

    /// <summary>Nothing is provided yet: Recommended Workflow shows the three inputs as the way in.</summary>
    public bool IsOnboarding => !Inputs.AnyProvided && !WorkspaceLoaded && !WorkspaceError;

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
    public const string RefreshSourceKey = "RefreshSource";
    public const string CompleteTargetKey = "CompleteTarget";
    public const string OpenReviewKeyPrefix = "OpenReview:";

    private readonly ICurrentWorkspaceProjection _workspace;
    private readonly ProjectApplicabilityState _applicability;
    private readonly IRecommendedWorkflowApiService _workflowApi;
    private readonly ILogger<WorkflowReadinessService> _logger;

    public event Action? ReadinessChanged;

    public WorkflowReadinessService(
        ICurrentWorkspaceProjection workspace,
        ProjectApplicabilityState applicability,
        IRecommendedWorkflowApiService workflowApi,
        ILogger<WorkflowReadinessService> logger)
    {
        _workspace = workspace;
        _applicability = applicability;
        _workflowApi = workflowApi;
        _logger = logger;
        _workspace.Changed += OnWorkspaceChanged;
        // A Target Environment saved, a source analyzed or an integration configured refreshes applicability (on navigation and
        // workspace changes); the workflow follows it without polling.
        _applicability.Changed += OnWorkspaceChanged;
    }

    public async Task<WorkflowReadiness> GetReadinessAsync()
    {
        // Recommendations are computed only from a finished workspace snapshot, never from a partially initialized one.
        var workspace = await _workspace.GetAsync();
        var environmentKnown = true;
        try { await _applicability.EnsureLoadedAsync(); }
        catch (Exception ex) { environmentKnown = false; _logger.LogWarning(ex, "Source and target state could not be read"); }
        var inputs = ProjectInputPresentation.Build(workspace, _applicability.Profile, _applicability.Coverage, environmentKnown);
        var reviews = ApplicableReviews();

        if (workspace.State == CurrentWorkspaceState.Error)
            return Empty(workspace, null, "The workspace could not be read, so release readiness cannot be evaluated.") with { Inputs = inputs };

        if (!workspace.WorkspaceLoaded)
        {
            // No documents: source and target can still make reviews applicable. Only with no input at all is the start offered.
            var next = inputs.AnyProvided ? NextAction(workspace, inputs, [], reviews) : Onboarding();
            return Empty(workspace, next, inputs.AnyProvided
                    ? "Release readiness is based on document review approvals; no project documents are loaded."
                    : "Add project documents, source or a target before release readiness can be evaluated.") with
            {
                Inputs = inputs,
                ApplicableReviews = reviews,
                AlternativeActions = inputs.AnyProvided ? [] : [AddSource(inputs.Source), ConfigureTarget(inputs.Target)],
            };
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
            NextRecommendedAction: NextAction(workspace, inputs, steps, reviews),
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
            ReleaseReadinessPercent = releasePercent,
            Inputs = inputs,
            ApplicableReviews = reviews,
        };
    }

    /// <summary>
    /// The next action from the actual input and review state, in this order: no documents and nothing else → add documents;
    /// an ambiguous document role → choose one; outdated source → analyze again; an open document review step; the first
    /// applicable review in sidebar order; an incomplete target. Never "Load project artifacts" while documents exist, and
    /// never a source or target prerequisite for work that does not need it.
    /// </summary>
    private static WorkflowStepViewModel? NextAction(CurrentWorkspaceSnapshot workspace, ProjectInputs inputs,
        IReadOnlyList<WorkflowStepViewModel> steps, IReadOnlyList<WorkflowReviewOption> reviews)
    {
        if (workspace.WorkspaceLoaded && !inputs.AnyProvided && workspace.Roles.All(r => r.Availability == ArtifactRoleAvailability.Missing))
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

        if (inputs.Source.Status == ProjectInputStatus.NeedsAttention)
            return Synthetic(RefreshSourceKey, "Analyze the source again", inputs.Source.Detail, ProjectInputPresentation.SourceAnalysisRoute, "Open Source Analysis");

        var documentStep = steps.FirstOrDefault(step => step.IsCurrent && IsReleaseReviewStep(step))
            ?? steps.FirstOrDefault(step => IsReleaseReviewStep(step) && step.Status != WorkflowStepStatus.Approved);
        if (documentStep is not null) return documentStep;

        if (reviews.FirstOrDefault() is { } review)
        {
            // A runtime review against an incomplete target (no URL, or the seed's example URL) would test nothing real.
            if (inputs.Target.Status == ProjectInputStatus.Partial && NeedsTarget(review.ReviewId))
                return Synthetic(CompleteTargetKey, "Finish the Target Environment", inputs.Target.Detail, inputs.Target.Route, "Configure Target");
            return Synthetic(OpenReviewKeyPrefix + review.ReviewId, $"Open {review.Label}",
                $"{review.Label} applies to this project{(review.Status == ApplicabilityStatus.PartiallyApplicable ? " in part" : "")} ({review.Lane}). {review.Reason}".Trim(),
                review.Route, $"Open {review.Label}");
        }

        if (inputs.Target.Status == ProjectInputStatus.Partial)
            return Synthetic(CompleteTargetKey, "Finish the Target Environment", inputs.Target.Detail, inputs.Target.Route, "Configure Target");

        return null;
    }

    /// <summary>The review runs against a configured browser or API target (from its declared requirements, not its name).</summary>
    private static bool NeedsTarget(string reviewId) =>
        BirkNext.Technology.ReviewCatalog.Find(reviewId)?.Requires.Any(c => c is Capability.BrowserTarget or Capability.ApiTarget) == true;

    /// <summary>First start: no documents, source or target yet. Sample Projects is the easiest start, never a requirement.</summary>
    private static WorkflowStepViewModel Onboarding() => Synthetic(LoadWorkspaceKey, "Give BirkNext project context",
        "Start with project documents, source, or both. A Sample Project is the quickest way to explore BirkNext. Add a source snapshot for source-based reviews, and configure a Target Environment when you want runtime tests. No input is required for every project.",
        ProjectInputPresentation.SampleProjectsRoute, "Choose Sample Project");

    private static WorkflowStepViewModel AddSource(ProjectInput source) =>
        Synthetic("AddSource", "Add a source snapshot", source.Detail, source.Route, "Add Source Snapshot", isCurrent: false);

    private static WorkflowStepViewModel ConfigureTarget(ProjectInput target) =>
        Synthetic("ConfigureTarget", "Configure a Target Environment", target.Detail, target.Route, "Configure Target Environment", isCurrent: false);

    /// <summary>Applicable and partially applicable reviews, in sidebar order, each with its sidebar section as the lane.</summary>
    private IReadOnlyList<WorkflowReviewOption> ApplicableReviews() =>
        NavigationCatalog.Sections
            .SelectMany(section => section.Items.Where(item => item.ReviewId is not null).Select(item => (section, item)))
            .Select(x => (x.section, x.item, applicability: _applicability.For(x.item.ReviewId!)))
            .Where(x => x.applicability?.Status is ApplicabilityStatus.Applicable or ApplicabilityStatus.PartiallyApplicable)
            .Select(x => new WorkflowReviewOption(x.item.ReviewId!, x.item.Label, x.item.Route, x.section.Label, x.applicability!.Status, x.applicability.Reason))
            .ToList();

    private static string ExplorerRoute(WorkspaceArtifactType role) => ProjectInputPresentation.ExplorerRoute(role);

    private static WorkflowReadiness Empty(CurrentWorkspaceSnapshot workspace, WorkflowStepViewModel? next, string reason) =>
        new(workspace, null, null, null, null, next, new WorkflowReadinessBreakdown(), 0, 0, [], false, reason);

    private static WorkflowStepViewModel Synthetic(string key, string title, string description, string route, string actionLabel, bool isCurrent = true) => new()
    {
        Number = 1,
        Key = key,
        Title = title,
        Description = description,
        Route = route,
        ActionLabel = actionLabel,
        Color = "#0284c7",
        CanOpen = true,
        IsCurrent = isCurrent,
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

    public void Dispose()
    {
        _workspace.Changed -= OnWorkspaceChanged;
        _applicability.Changed -= OnWorkspaceChanged;
    }
}
