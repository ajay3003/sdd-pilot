using BirkNext.Applicability;

namespace BirkNext.Web.Services;

public interface IWorkflowReadinessService
{
    event Action? ReadinessChanged;

    Task<WorkflowReadiness> GetReadinessAsync();
}

/// <summary>
/// Step 1 (load project artifacts) — about artifact availability only, never a review decision. <see cref="Required"/>: no
/// document artifact is available (or no workspace), so no document review can run; <see cref="Partial"/>: artifacts are
/// available but some roles the review steps read are absent — those steps are not applicable, nothing is blocked;
/// <see cref="Done"/>: every role the review steps read is available. No role is required of every project.
/// </summary>
public enum ArtifactLoadState { Required, Partial, Done }

/// <summary>A review the project's inputs make applicable, in sidebar order, with the lane (sidebar section) it belongs to.</summary>
public sealed record WorkflowReviewOption(string ReviewId, string Label, string Route, string Lane, ApplicabilityStatus Status, string Reason);

/// <summary>Whether the viewed Artifact Pack is explicitly bound to source evidence (recorded CodeLinks in its scope). Never inferred from file names.</summary>
public enum SourceBindingState { NoSource, NotEstablished, Established }

/// <summary>Azure DevOps change provenance: optional, independent of Source Analysis.</summary>
public enum ChangeProvenanceState { Unknown, NotConfigured, Configured, DemoEvidence, Disabled }

/// <summary>
/// One evidence branch after the document and alignment reviews (source-backed implementation evidence, Azure DevOps change provenance,
/// runtime evidence). Branches are independent: none is a prerequisite of another, and none is a step of the core review path.
/// </summary>
public sealed record WorkflowEvidenceBranch(string Key, string Title, string Purpose, string State, string Detail, string Route, string ActionLabel, bool Ready);

/// <summary>
/// Workflow readiness for the current project inputs. Whether a workspace, project or artifact exists comes from
/// <see cref="CurrentWorkspaceSnapshot"/> (the same read model the Dashboard uses); source and target come from the shared
/// applicability state (active Target Environment and the workspace's latest Source Analysis snapshot). This record only adds the document
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

    /// <summary>Source snapshot analyzed ≠ Artifact Pack bound to source ≠ implementation evidence assessed.</summary>
    public SourceBindingState SourceBinding { get; init; } = SourceBindingState.NoSource;

    /// <summary>Azure DevOps change provenance (Change Traceability) — optional evidence, never a blocker for source-backed review.</summary>
    public ChangeProvenanceState ChangeProvenance { get; init; } = ChangeProvenanceState.Unknown;

    /// <summary>Evidence-based reviews after the core path: source evidence, change provenance, runtime evidence.</summary>
    public IReadOnlyList<WorkflowEvidenceBranch> EvidenceBranches { get; init; } = [];

    /// <summary>Required gates with a review decision recorded on their current revisions: reviewed, reviewed with gaps or approved.</summary>
    public int DecidedReviewCount => RequiredReviewGates.Count(step => ArtifactReviewPresentation.Of(step).IsDecided);

    /// <summary>Every required review decision is recorded (and at least one applies).</summary>
    public bool RequiredDecisionsComplete => RequiredReviewGates.Count > 0 && DecidedReviewCount == RequiredReviewGates.Count;

    /// <summary>Other useful starts shown beside the recommendation when no input is provided yet (add source, configure a target).</summary>
    public IReadOnlyList<WorkflowStepViewModel> AlternativeActions { get; init; } = [];

    /// <summary>Step 1: whether document artifacts are available. A workspace alone does not complete it.</summary>
    public ArtifactLoadState ArtifactLoad { get; init; } = ArtifactLoadState.Required;

    /// <summary>Roles the non-optional review steps read, from the backend step definitions. Each step requires its own roles; none is required of the project.</summary>
    public IReadOnlyList<WorkspaceArtifactType> RequiredRoles { get; init; } = [];

    /// <summary>Of those roles, the ones with no artifact in the current workspace (the steps that read them are not applicable).</summary>
    public IReadOnlyList<WorkspaceArtifactType> MissingRoles { get; init; } = [];

    /// <summary>Every manual review gate (document and analysis review steps), applicable or not, in workflow order.</summary>
    public IReadOnlyList<WorkflowStepViewModel> ReviewGates => Steps.Where(WorkflowReadinessService.IsReviewGate).ToList();

    /// <summary>Gates that apply and are required: the Manual Review and Release Readiness denominator.</summary>
    public IReadOnlyList<WorkflowStepViewModel> RequiredReviewGates =>
        ReviewGates.Where(step => !step.IsOptional && ArtifactReviewPresentation.Of(step).Applies).ToList();

    /// <summary>Gates that apply but are optional (a Data Model review): shown, never counted against release.</summary>
    public IReadOnlyList<WorkflowStepViewModel> OptionalReviewGates =>
        ReviewGates.Where(step => step.IsOptional && ArtifactReviewPresentation.Of(step).Applies).ToList();

    /// <summary>The document review gate (explorer step) that reads a role, or null when the workflow has none for it.</summary>
    public WorkflowStepViewModel? ReviewGateFor(WorkspaceArtifactType role) =>
        ReviewGates.FirstOrDefault(step => step.StepType == "Explorer" && step.ArtifactRoles.Contains(role.ToString(), StringComparer.OrdinalIgnoreCase));

    /// <summary>The review status of a role's artifact, from its document review gate — what the Dashboard and the workflow both show.</summary>
    public ArtifactReviewStatus? ReviewFor(WorkspaceArtifactType role) =>
        ReviewGateFor(role) is { } gate ? ArtifactReviewPresentation.Of(gate) : null;

    /// <summary>Document review gates that apply: the governance decisions recorded against project artifact revisions.</summary>
    public IReadOnlyList<WorkflowStepViewModel> DocumentReviewGates =>
        ReviewGates.Where(step => step.StepType == "Explorer" && ArtifactReviewPresentation.Of(step).Applies).ToList();

    /// <summary>True when the recommendation is one of the listed steps: the step card carries the action, not the summary card.</summary>
    public bool RecommendationIsListedStep => NextRecommendedAction is { } next && Steps.Contains(next);

    /// <summary>Nothing is provided yet: Recommended Workflow shows the three inputs as the way in.</summary>
    public bool IsOnboarding => !Inputs.AnyProvided && !WorkspaceLoaded && !WorkspaceError;

    /// <summary>
    /// Share of the required review steps with a recorded review decision, or null when nothing has been decided yet. Not assessed is
    /// never shown as 0%. Guidance only: it is not release readiness (no readiness policy is configured).
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

    public const string EstablishSourceBindingKey = "EstablishSourceBinding";
    public const string ImplementationEvidenceRoute = "implementation-review";
    public const string ChangeTraceabilityRoute = "implementation-traceability";
    public const string NoReleasePolicy = "Recommended Workflow guides reviews; it does not decide release readiness — no readiness policy is configured.";

    private readonly ICurrentWorkspaceProjection _workspace;
    private readonly ProjectApplicabilityState _applicability;
    private readonly IRecommendedWorkflowApiService _workflowApi;
    private readonly ILogger<WorkflowReadinessService> _logger;
    private readonly IWorkspaceSessionService? _session;
    private readonly ImplementationTraceabilityApiService? _changeTraceability;
    private readonly FeatureVisibilityService? _features;

    public event Action? ReadinessChanged;

    public WorkflowReadinessService(
        ICurrentWorkspaceProjection workspace,
        ProjectApplicabilityState applicability,
        IRecommendedWorkflowApiService workflowApi,
        ILogger<WorkflowReadinessService> logger,
        IWorkspaceSessionService? session = null,
        ImplementationTraceabilityApiService? changeTraceability = null,
        FeatureVisibilityService? features = null)
    {
        _features = features;
        _workspace = workspace;
        _applicability = applicability;
        _workflowApi = workflowApi;
        _logger = logger;
        _session = session;
        _changeTraceability = changeTraceability;
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
        var inputs = ProjectInputPresentation.Build(workspace, _applicability.Profile, _applicability.Coverage, environmentKnown && _applicability.LoadError is null);
        var binding = SourceBindingOf(inputs);
        var change = await ChangeProvenanceAsync();
        var reviews = ApplicableReviews(change);
        var branches = EvidenceBranches(inputs, binding, change);

        if (workspace.State == CurrentWorkspaceState.Error)
            return Empty(workspace, null, "The workspace could not be read, so release readiness cannot be evaluated.") with { Inputs = inputs };

        if (!workspace.WorkspaceLoaded)
        {
            // No documents and no workspace. With source or a target the project can still progress through their reviews;
            // with nothing at all, step 1 (load project artifacts) is the action, and source and target are alternatives.
            if (inputs.AnyProvided)
                return Empty(workspace, NextAction(workspace, inputs, [], reviews, ArtifactLoadState.Required, [], binding),
                    NoReleasePolicy) with
                {
                    Inputs = inputs, ApplicableReviews = reviews, SourceBinding = binding, ChangeProvenance = change, EvidenceBranches = branches,
                };
            var load = LoadStep(Synthetic(LoadWorkspaceKey, "", "", "", ""), ArtifactLoadState.Required, [], workspace);
            return Empty(workspace, load, NoReleasePolicy) with
            {
                Inputs = inputs,
                ApplicableReviews = reviews,
                SourceBinding = binding, ChangeProvenance = change, EvidenceBranches = branches,
                Steps = [load],
                AlternativeActions = [ImportDocuments([]), AddSource(inputs.Source), ConfigureTarget(inputs.Target)],
            };
        }

        // Each available role's selected revision goes with the request: decisions are current only for exactly these revisions.
        var steps = (await _workflowApi.BuildWorkflowStepsAsync(
            workspace.WorkspaceId ?? Guid.Empty,
            workspace.Has(WorkspaceArtifactType.Constitution),
            workspace.Has(WorkspaceArtifactType.Specification),
            workspace.Has(WorkspaceArtifactType.Plan),
            workspace.Has(WorkspaceArtifactType.Tasks),
            workspace.Has(WorkspaceArtifactType.DataModel),
            workspace.ArtifactRevisions) ?? [])
            .Where(IsVisibleWorkflowStep)
            .ToList();

        // Step 1 is complete when document artifacts are available — not because a workspace exists. A role a review step reads
        // but the workspace lacks makes that step not applicable; it does not hold the project back.
        var requiredRoles = RequiredRolesOf(steps);
        var missingRoles = requiredRoles.Where(role => !workspace.Has(role)).ToList();
        var artifactLoad = workspace.AvailableRoleCount == 0 ? ArtifactLoadState.Required
            : missingRoles.Count == 0 ? ArtifactLoadState.Done
            : ArtifactLoadState.Partial;
        steps = steps.Select(step => IsLoadStep(step) ? LoadStep(step, artifactLoad, missingRoles, workspace) : step).ToList();
        if (artifactLoad == ArtifactLoadState.Required)
        {
            // Exactly one current step: loading artifacts comes first.
            foreach (var step in steps.Where(step => !IsLoadStep(step))) step.IsCurrent = false;
        }
        foreach (var step in steps.Where(step => step.Status is WorkflowStepStatus.Locked or WorkflowStepStatus.NotApplicable))
            step.DisabledReason = LockReason(step, workspace);

        var specificationState = FindStep(steps, "Specification");
        var traceabilityState = FindStep(steps, "Traceability");
        var implementationState = FindStep(steps, "Implementation");
        var qualityGateState = FindStep(steps, "Quality");

        // The required gates that apply (not N/A, not optional). A gate is done when a review decision is recorded on the artifact revisions
        // it reads now — reviewed, reviewed with gaps or approved. Approval counts only where a step asks for it; viewing never counts.
        var required = steps.Where(step => IsReleaseReviewStep(step) && !step.IsOptional && step.Status != WorkflowStepStatus.NotApplicable).ToList();
        var approved = required.Count(step => step.Status == WorkflowStepStatus.Approved);
        var decided = required.Count(step => ArtifactReviewPresentation.Of(step).IsDecided);
        var assessed = required.Any(step => ArtifactReviewPresentation.Of(step) is { IsDecided: true } or { State: ArtifactReviewState.Stale });
        int? releasePercent = required.Count > 0 && assessed ? decided * 100 / required.Count : null;
        // No readiness policy is configured: the workflow never claims release readiness.
        const bool canRelease = false;

        return new WorkflowReadiness(
            Workspace: workspace,
            SpecificationExplorerState: specificationState,
            TraceabilityState: traceabilityState,
            ImplementationReviewState: implementationState,
            QualityGateState: qualityGateState,
            NextRecommendedAction: NextAction(workspace, inputs, steps, reviews, artifactLoad, missingRoles, binding),
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
            ReleaseReason: NoReleasePolicy)
        {
            ReleaseReadinessPercent = releasePercent,
            Inputs = inputs,
            ApplicableReviews = reviews,
            SourceBinding = binding,
            ChangeProvenance = change,
            EvidenceBranches = branches,
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

    /// <summary>Step 1 as the workflow shows it: an action while no document artifact is available, done once there are some.</summary>
    private static WorkflowStepViewModel LoadStep(WorkflowStepViewModel step, ArtifactLoadState state, IReadOnlyList<WorkspaceArtifactType> missing,
        CurrentWorkspaceSnapshot workspace)
    {
        var missingText = string.Join(", ", missing.Select(r => Explorers.ArtifactExplorerRoles.Label(r) == "Task" ? "Tasks" : Explorers.ArtifactExplorerRoles.Label(r)));
        var roles = workspace.AvailableRoleCount;
        step.Title = "Load project artifacts";
        step.Description = state switch
        {
            ArtifactLoadState.Required => "No project artifacts are available yet. Import the project ZIP (documents and source in one upload), choose a Sample Project, or import documents in the explorers.",
            _ => $"{roles} project artifact role{(roles == 1 ? " is" : "s are")} available."
                 + (missing.Count == 0 ? "" : $" Reviews that read {missingText} do not apply until {(missing.Count == 1 ? "it is" : "they are")} added."),
        };
        step.Route = ProjectInputPresentation.ProjectImportRoute;
        step.ActionLabel = LoadArtifactsLabel;
        step.CanOpen = state == ArtifactLoadState.Required;
        step.IsCurrent = state == ArtifactLoadState.Required;
        step.IsFuture = false;
        step.Status = state == ArtifactLoadState.Required ? WorkflowStepStatus.Available : WorkflowStepStatus.Approved;
        step.Prerequisites = PrerequisiteState.Available;
        step.DisabledReason = "";
        step.RequiresApproval = false;
        step.RequiresManualReview = false;
        return step;
    }

    /// <summary>
    /// Why a step is not applicable (the artifacts it requires are absent) or blocked (no artifact chosen, or an earlier approval
    /// missing — the backend names which).
    /// </summary>
    private static string LockReason(WorkflowStepViewModel step, CurrentWorkspaceSnapshot workspace)
    {
        if (step.Status == WorkflowStepStatus.Locked && !string.IsNullOrWhiteSpace(step.DisabledReason)
            && !step.DisabledReason.StartsWith("Requires", StringComparison.Ordinal) && !step.DisabledReason.StartsWith("Load required", StringComparison.Ordinal))
            return step.DisabledReason;
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
        ArtifactLoadState artifactLoad, IReadOnlyList<WorkspaceArtifactType> missingRoles, SourceBindingState binding)
    {
        if (workspace.WorkspaceLoaded && artifactLoad == ArtifactLoadState.Required)
            return steps.FirstOrDefault(IsLoadStep) ?? LoadStep(Synthetic(LoadWorkspaceKey, "", "", "", ""), artifactLoad, missingRoles, workspace);

        // A monorepo without a chosen module: reviews read one module's documents, so the user picks where to start. Navigation, not an
        // ambiguity — "needs selection" is kept for several candidates in the same pack.
        if (workspace.ModuleRequired && workspace.Roles.FirstOrDefault(r => r.ModuleRequired && r.DetectedCount > 0) is { } moduleRole)
        {
            return Synthetic(ChooseArtifactsKey, "Choose a module to review",
                $"The project has {workspace.ModuleCount} modules with their own documents. Choose the module to review; a module with one artifact per role uses it automatically.",
                ExplorerRoute(moduleRole.Role), $"Open {moduleRole.Label} Explorer");
        }

        if (workspace.PackRequired && workspace.Roles.FirstOrDefault(r => r.PackRequired) is { } packRole)
        {
            return Synthetic(ChooseArtifactsKey, "Select an artifact pack to explore",
                $"The {(workspace.ModuleLabel is { } pm ? $"module {pm}" : "project")} is organised in {workspace.PackCount} artifact packs. Reviews read one pack and its shared artifacts; a pack with one artifact per role uses it automatically.",
                ExplorerRoute(packRole.Role), $"Open {packRole.Label} Explorer");
        }

        if (workspace.Roles.FirstOrDefault(r => r.Selection == ArtifactRoleSelection.SelectionRequired) is { } unresolved)
        {
            var where = workspace.PackLabel is { } pack ? $"Pack {pack} has" : workspace.ModuleLabel is { } module ? $"Module {module} has" : "The workspace has";
            return Synthetic(ChooseArtifactsKey, $"Needs selection: choose the {unresolved.Label} to review",
                $"{where} {unresolved.ArtifactCount} {unresolved.Label} artifacts and none is selected. Reviews read the selected one.",
                ExplorerRoute(unresolved.Role), $"Open {unresolved.Label} Explorer");
        }

        if (inputs.Source.Status == ProjectInputStatus.NeedsAttention)
            return Synthetic(RefreshSourceKey, "Analyze the source again", inputs.Source.Detail, ProjectInputPresentation.SourceAnalysisRoute, "Open Source Analysis");

        // The first applicable review gate without a review decision on its current revisions (open, in review or stale). Reviewed, reviewed
        // with gaps and approved are decisions: the recommendation moves on. A low traceability coverage is never a blocker by itself.
        var documentStep = steps.FirstOrDefault(step => step.IsCurrent && IsReleaseReviewStep(step) && !ArtifactReviewPresentation.Of(step).IsDecided)
            ?? steps.FirstOrDefault(step => IsReleaseReviewStep(step) && IsOpenGate(step));
        if (documentStep is not null) return documentStep;

        // Source-backed evidence after the core path: analyzed source is not yet implementation evidence — the pack needs an explicit binding.
        if (workspace.WorkspaceLoaded && steps.Count > 0)
        {
            if (binding == SourceBindingState.NotEstablished)
                return Synthetic(EstablishSourceBindingKey, "Establish source evidence binding",
                    "Source snapshot is available, but this Artifact Pack is not yet mapped to source evidence. Bind explicit CodeLinks in Implementation Evidence Review; implementation stays Not assessed until then.",
                    ImplementationEvidenceRoute, "Open Implementation Evidence Review");
            if (binding == SourceBindingState.Established)
                return Synthetic(OpenReviewKeyPrefix + "implementation-evidence", "Open Implementation Evidence Review",
                    "This Artifact Pack is bound to source evidence: review its source-backed implementation and test evidence.",
                    ImplementationEvidenceRoute, "Open Implementation Evidence Review");
        }

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

    /// <summary>A gate still waiting for a review decision: ready, opened, or decided on an earlier revision only (stale).</summary>
    private static bool IsOpenGate(WorkflowStepViewModel step) =>
        ArtifactReviewPresentation.Of(step).State is ArtifactReviewState.ReadyToReview or ArtifactReviewState.InReview or ArtifactReviewState.Stale
        || step.RequiresApproval && step.Status == WorkflowStepStatus.Reviewed;

    /// <summary>
    /// Source snapshot → Artifact Pack binding. Bound only when explicit implementation evidence (CodeLinks) is recorded in the viewed pack's
    /// requirement scope — the same fact Implementation Evidence Review uses. An analyzed snapshot alone never binds a pack.
    /// </summary>
    private SourceBindingState SourceBindingOf(ProjectInputs inputs)
    {
        if (inputs.Source.Status != ProjectInputStatus.Ready) return SourceBindingState.NoSource;
        var lifecycle = _session?.SddLifecycle;
        if (lifecycle is null) return SourceBindingState.NotEstablished;
        return lifecycle.ImplementationEvidence.Any(e => RequirementScopes.Matches(e.RequirementScope, lifecycle.ActiveRequirementScope))
            ? SourceBindingState.Established : SourceBindingState.NotEstablished;
    }

    /// <summary>Azure DevOps change provenance from the backend status. Unknown when it cannot be read — never a blocker either way.</summary>
    private async Task<ChangeProvenanceState> ChangeProvenanceAsync()
    {
        // Hidden by Feature & Menu Visibility (the default): not offered, not queried, never a gap or a penalty.
        if (_features is { ImplementationTraceability: false }) return ChangeProvenanceState.Disabled;
        if (_changeTraceability is null) return ChangeProvenanceState.Unknown;
        try
        {
            var status = await _changeTraceability.GetStatusAsync();
            return status.Configured ? ChangeProvenanceState.Configured
                : status.UsingMock ? ChangeProvenanceState.DemoEvidence
                : ChangeProvenanceState.NotConfigured;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Change Traceability status could not be read");
            return ChangeProvenanceState.Unknown;
        }
    }

    /// <summary>The three evidence branches after the core review path. Independent of each other; each names its own prerequisite.</summary>
    private static IReadOnlyList<WorkflowEvidenceBranch> EvidenceBranches(ProjectInputs inputs, SourceBindingState binding, ChangeProvenanceState change) =>
        [.. AllEvidenceBranches(inputs, binding, change).Where(b => b.Key != "change" || change != ChangeProvenanceState.Disabled)];

    private static IReadOnlyList<WorkflowEvidenceBranch> AllEvidenceBranches(ProjectInputs inputs, SourceBindingState binding, ChangeProvenanceState change) =>
    [
        new("source", "Implementation Evidence Review",
            "Assess documented requirements against explicit source-backed implementation and test evidence.",
            binding switch
            {
                SourceBindingState.NoSource => "Source unavailable",
                SourceBindingState.NotEstablished => "Source analyzed · Artifact Pack source binding not established",
                _ => "Ready to review",
            },
            binding switch
            {
                SourceBindingState.NoSource => "Add a source snapshot (Import Project or Source Analysis) to review implementation evidence. Implementation is Not assessed.",
                SourceBindingState.NotEstablished => "Source snapshot is available, but this Artifact Pack is not yet mapped to source evidence. Implementation is Not assessed until explicit CodeLinks are bound.",
                _ => "Explicit source-backed evidence is recorded for this Artifact Pack.",
            },
            binding == SourceBindingState.NoSource ? inputs.Source.Route : ImplementationEvidenceRoute,
            binding == SourceBindingState.NoSource ? inputs.Source.ActionLabel : "Open Implementation Evidence Review",
            binding == SourceBindingState.Established),
        new("change", "Change Traceability",
            "Trace Azure DevOps work items through pull requests, commits and changed files. Optional; it does not prove a requirement is implemented.",
            change switch
            {
                ChangeProvenanceState.Configured => "Azure DevOps configured",
                ChangeProvenanceState.DemoEvidence => "Developer demo evidence only — not project evidence",
                ChangeProvenanceState.NotConfigured => "Not configured",
                _ => "Status unknown",
            },
            change == ChangeProvenanceState.Configured
                ? "Change provenance can be loaded for this project's work items."
                : "Azure DevOps is not configured for this project. This does not block source-backed implementation review.",
            ChangeTraceabilityRoute, "Open Change Traceability", change == ChangeProvenanceState.Configured),
        new("runtime", "Runtime and environment reviews",
            "Reviews that test the deployed system (frontend, API, integration, performance).",
            inputs.Target.Status == ProjectInputStatus.Ready ? "Configured" : inputs.Target.Status == ProjectInputStatus.Partial ? "Partially configured" : "Not configured",
            inputs.Target.Status == ProjectInputStatus.Ready
                ? "Runtime reviews can run against the configured Target Environment."
                : "Required for runtime reviews only — never for document, traceability or source reviews.",
            inputs.Target.Route, inputs.Target.ActionLabel, inputs.Target.Status == ProjectInputStatus.Ready),
    ];

    /// <summary>The review runs against a configured browser or API target (from its declared requirements, not its name).</summary>
    private static bool NeedsTarget(string reviewId) =>
        BirkNext.Technology.ReviewCatalog.Find(reviewId)?.Requires.Any(c => c is Capability.BrowserTarget or Capability.ApiTarget) == true;

    private static WorkflowStepViewModel AddSource(ProjectInput source) =>
        Synthetic("AddSource", "Add a source snapshot", source.Detail, source.Route, "Add Source Snapshot", isCurrent: false);

    private static WorkflowStepViewModel ConfigureTarget(ProjectInput target) =>
        Synthetic("ConfigureTarget", "Configure a Target Environment", target.Detail, target.Route, "Configure Target Environment", isCurrent: false);

    /// <summary>Applicable and partially applicable reviews, in sidebar order, each with its sidebar section as the lane.</summary>
    private IReadOnlyList<WorkflowReviewOption> ApplicableReviews(ChangeProvenanceState change)
    {
        var reviews = NavigationCatalog.Sections
            .SelectMany(section => section.Items.Where(item => item.ReviewId is not null).Select(item => (section, item)))
            .Select(x => (x.section, x.item, applicability: _applicability.For(x.item.ReviewId!)))
            .Where(x => x.applicability?.Status is ApplicabilityStatus.Applicable or ApplicabilityStatus.PartiallyApplicable)
            .Select(x => new WorkflowReviewOption(x.item.ReviewId!, x.item.Label, x.item.Route, x.section.Label, x.applicability!.Status, x.applicability.Reason))
            .ToList();
        // Target-project analysis of generated documentation: applicable only when the imported project carries generated docs. Absent = neutral.
        var lifecycle = _session?.SddLifecycle;
        if (lifecycle?.CurrentProjectImportId is { } importId
            && lifecycle.GeneratedDocumentationSets.FirstOrDefault(s => s.ProjectImportId == importId) is { State: "Present" })
            reviews.Add(new("generated-documentation-health", "Generated Documentation Health", NavigationCatalog.GeneratedDocumentationHealthRoute, LaneOf(NavigationCatalog.GeneratedDocumentationHealthRoute),
                ApplicabilityStatus.Applicable, "Generated documentation detected in the current target project."));
        // Azure DevOps change provenance: optional evidence, applicable only when Azure DevOps is configured.
        if (change == ChangeProvenanceState.Configured)
            reviews.Add(new("change-traceability", "Change Traceability", ChangeTraceabilityRoute, LaneOf(ChangeTraceabilityRoute),
                ApplicabilityStatus.Applicable, "Azure DevOps is configured for this project."));
        return reviews;
    }

    /// <summary>The lane of an optional review is its sidebar section (a navigation fact only, never a prerequisite).</summary>
    private static string LaneOf(string route) => NavigationCatalog.SectionFor(route)?.Label ?? "";

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

    private static bool IsQualityGatePassed(WorkflowStepViewModel? step) =>
        step is null
        || step.Status == WorkflowStepStatus.Approved
        || (!step.RequiresApproval && step.CanOpen && step.Status != WorkflowStepStatus.Locked);

    private static bool IsVisibleWorkflowStep(WorkflowStepViewModel step) =>
        !step.Key.Equals("ReviewContextValidation", StringComparison.OrdinalIgnoreCase);

    /// <summary>A manual review gate: a step that asks for a review or an approval (not step 1, the Dashboard or diagnostics).</summary>
    public static bool IsReviewGate(WorkflowStepViewModel step) => IsReleaseReviewStep(step);

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
