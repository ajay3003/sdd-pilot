using BirkNext.Applicability;

namespace BirkNext.Web.Services;

public interface IWorkflowReadinessService
{
    event Action? ReadinessChanged;

    Task<WorkflowReadiness> GetReadinessAsync();
}

/// <summary>
/// Step 1 (load project artifacts) — about artifact availability only, never a review decision. <see cref="Required"/>: no required
/// role is available (or no workspace); <see cref="Partial"/>: some required roles are missing; <see cref="Done"/>: every role the
/// required review steps need is available.
/// </summary>
public enum ArtifactLoadState { Required, Partial, Done }

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

    /// <summary>Step 1: whether the roles the required review steps need are available. A workspace alone does not complete it.</summary>
    public ArtifactLoadState ArtifactLoad { get; init; } = ArtifactLoadState.Required;

    /// <summary>Roles the required (non-optional) review steps need, from the backend step definitions.</summary>
    public IReadOnlyList<WorkspaceArtifactType> RequiredRoles { get; init; } = [];

    /// <summary>Required roles with no artifact in the current workspace.</summary>
    public IReadOnlyList<WorkspaceArtifactType> MissingRoles { get; init; } = [];

    /// <summary>True when the recommendation is one of the listed steps: the step card carries the action, not the summary card.</summary>
    public bool RecommendationIsListedStep => NextRecommendedAction is { } next && Steps.Contains(next);

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
    /// <summary>The backend's step 1 (WorkflowDefinitions). The frontend decides its state from artifact availability.</summary>
    public const string LoadSampleProjectKey = "LoadSampleProject";
    public const string LoadArtifactsLabel = "Load artifacts";
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
            // No documents and no workspace. With source or a target the project can still progress through their reviews;
            // with nothing at all, step 1 (load project artifacts) is the action, and source and target are alternatives.
            if (inputs.AnyProvided)
                return Empty(workspace, NextAction(workspace, inputs, [], reviews, ArtifactLoadState.Required, []),
                    "Release readiness is based on document review approvals; no project documents are loaded.") with
                {
                    Inputs = inputs, ApplicableReviews = reviews,
                };
            var load = LoadStep(Synthetic(LoadWorkspaceKey, "", "", "", ""), ArtifactLoadState.Required, [], workspace);
            return Empty(workspace, load, "Add project documents, source or a target before release readiness can be evaluated.") with
            {
                Inputs = inputs,
                ApplicableReviews = reviews,
                Steps = [load],
                AlternativeActions = [ImportDocuments([]), AddSource(inputs.Source), ConfigureTarget(inputs.Target)],
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

        // Step 1 is complete only when the roles the required review steps need are available — not because a workspace exists.
        var requiredRoles = RequiredRolesOf(steps);
        var missingRoles = requiredRoles.Where(role => !workspace.Has(role)).ToList();
        var artifactLoad = requiredRoles.Count == 0
            ? (workspace.AvailableRoleCount > 0 ? ArtifactLoadState.Done : ArtifactLoadState.Required)
            : missingRoles.Count == 0 ? ArtifactLoadState.Done
            : missingRoles.Count == requiredRoles.Count ? ArtifactLoadState.Required
            : ArtifactLoadState.Partial;
        steps = steps.Select(step => IsLoadStep(step) ? LoadStep(step, artifactLoad, missingRoles, workspace) : step).ToList();
        if (artifactLoad != ArtifactLoadState.Done)
        {
            // Exactly one current step: loading artifacts comes first.
            foreach (var step in steps.Where(step => !IsLoadStep(step))) step.IsCurrent = false;
        }
        foreach (var step in steps.Where(step => step.Status == WorkflowStepStatus.Locked))
            step.DisabledReason = LockReason(step, workspace);

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
            NextRecommendedAction: NextAction(workspace, inputs, steps, reviews, artifactLoad, missingRoles),
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
            ArtifactLoad = artifactLoad,
            RequiredRoles = requiredRoles,
            MissingRoles = missingRoles,
            AlternativeActions = artifactLoad == ArtifactLoadState.Done ? [] : [ImportDocuments(missingRoles)],
        };
    }

    /// <summary>Union of the roles the non-optional review steps require (backend WorkflowDefinitions), in workflow role order.</summary>
    private static IReadOnlyList<WorkspaceArtifactType> RequiredRolesOf(IEnumerable<WorkflowStepViewModel> steps)
    {
        var names = steps.Where(step => IsReleaseReviewStep(step) && !step.IsOptional)
            .SelectMany(step => step.RequiredArtifacts ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CurrentWorkspaceSnapshot.WorkflowRoles.Where(role => names.Contains(role.ToString())).ToList();
    }

    private static bool IsLoadStep(WorkflowStepViewModel step) =>
        step.Key.Equals(LoadSampleProjectKey, StringComparison.OrdinalIgnoreCase) || step.Key.Equals(LoadWorkspaceKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Step 1 as the workflow shows it: an action while required roles are missing, done when they are available.</summary>
    private static WorkflowStepViewModel LoadStep(WorkflowStepViewModel step, ArtifactLoadState state, IReadOnlyList<WorkspaceArtifactType> missing,
        CurrentWorkspaceSnapshot workspace)
    {
        var missingText = string.Join(", ", missing.Select(r => Explorers.ArtifactExplorerRoles.Label(r) == "Task" ? "Tasks" : Explorers.ArtifactExplorerRoles.Label(r)));
        step.Title = state == ArtifactLoadState.Partial ? "Add missing artifacts" : "Load project artifacts";
        step.Description = state switch
        {
            ArtifactLoadState.Done => $"Required project artifacts are available ({workspace.RoleSummary}).",
            ArtifactLoadState.Partial => $"Required project artifacts are missing: {missingText}. Choose a Sample Project that has them, or import them in the explorers.",
            _ => "Required project artifacts are missing. Choose a Sample Project, or import your own documents in the explorers.",
        };
        step.Route = ProjectInputPresentation.SampleProjectsRoute;
        step.ActionLabel = LoadArtifactsLabel;
        step.CanOpen = state != ArtifactLoadState.Done;
        step.IsCurrent = state != ArtifactLoadState.Done;
        step.IsFuture = false;
        step.Status = state == ArtifactLoadState.Done ? WorkflowStepStatus.Approved : WorkflowStepStatus.Available;
        step.Prerequisites = PrerequisiteState.Available;
        step.DisabledReason = "";
        step.RequiresApproval = false;
        step.RequiresManualReview = false;
        return step;
    }

    /// <summary>Why a step is locked, from what it actually lacks: its artifacts, or an earlier approval.</summary>
    private static string LockReason(WorkflowStepViewModel step, CurrentWorkspaceSnapshot workspace)
    {
        var missing = (step.RequiredArtifacts ?? [])
            .Select(name => Enum.TryParse<WorkspaceArtifactType>(name, true, out var role) ? role : (WorkspaceArtifactType?)null)
            .Where(role => role is not null && !workspace.Has(role.Value))
            .Select(role => role!.Value == WorkspaceArtifactType.Tasks ? "Tasks" : Explorers.ArtifactExplorerRoles.Label(role.Value))
            .ToList();
        return missing.Count switch
        {
            1 => $"Requires {missing[0]} artifact",
            > 1 => $"Requires {string.Join(", ", missing.Take(missing.Count - 1))} and {missing[^1]} artifacts",
            _ => "Complete the previous step first",
        };
    }

    /// <summary>The other way to provide documents: import them in the explorer of the first missing role.</summary>
    private static WorkflowStepViewModel ImportDocuments(IReadOnlyList<WorkspaceArtifactType> missing) =>
        Synthetic("ImportDocuments", "Import documents", "Import your own documents in an explorer.",
            ExplorerRoute(missing.Count > 0 ? missing[0] : WorkspaceArtifactType.Specification), "Import documents instead", isCurrent: false);

    /// <summary>
    /// The next action from the actual input and review state, in this order: step 1 while a document workspace lacks required
    /// roles; an ambiguous document role → choose one; outdated source → analyze again; an open document review step; the first
    /// applicable review in sidebar order; an incomplete target. Never a review step while its artifacts are missing, and
    /// never a source or target prerequisite for work that does not need it.
    /// </summary>
    private static WorkflowStepViewModel? NextAction(CurrentWorkspaceSnapshot workspace, ProjectInputs inputs,
        IReadOnlyList<WorkflowStepViewModel> steps, IReadOnlyList<WorkflowReviewOption> reviews,
        ArtifactLoadState artifactLoad, IReadOnlyList<WorkspaceArtifactType> missingRoles)
    {
        if (workspace.WorkspaceLoaded && artifactLoad != ArtifactLoadState.Done)
            return steps.FirstOrDefault(IsLoadStep) ?? LoadStep(Synthetic(LoadWorkspaceKey, "", "", "", ""), artifactLoad, missingRoles, workspace);

        if (workspace.Roles.FirstOrDefault(r => r.Selection == ArtifactRoleSelection.SelectionRequired) is { } unresolved)
        {
            return Synthetic(ChooseArtifactsKey, $"Choose the {unresolved.Label} to review",
                $"The workspace has {unresolved.ArtifactCount} {unresolved.Label} artifacts and none is selected. Reviews read the selected one.",
                ExplorerRoute(unresolved.Role), $"Open {unresolved.Label} Explorer");
        }

        if (inputs.Source.Status == ProjectInputStatus.NeedsAttention)
            return Synthetic(RefreshSourceKey, "Analyze the source again", inputs.Source.Detail, ProjectInputPresentation.SourceAnalysisRoute, "Open Source Analysis");

        var documentStep = steps.FirstOrDefault(step => step.IsCurrent && IsReleaseReviewStep(step))
            ?? steps.FirstOrDefault(step => IsReleaseReviewStep(step) && step.Status is not (WorkflowStepStatus.Approved or WorkflowStepStatus.Locked));
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
        !IsLoadStep(step)
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
