using BirkNext.Applicability;
using BirkNext.BrowserAuthentication;
using BirkNext.Technology;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

// ── Review paths: what the user can do next with the evidence and configuration available ─────────────────────────────────
// Recommended Workflow separates WHERE (Target Environment), WHAT (Target Application: the deployed URL and its detected
// characteristics), EVIDENCE (Project: the logical system and the packages bound to it), RUNTIME CAPABILITY (authentication,
// connected systems, test data) and WORK (reviews). A missing input blocks only the work that needs it:
//   missing documents → document reviews · missing environment → runtime reviews · missing unattended login → automated browser runs.
// Everything here is a pure function of existing state — no persistence, no AI — so the same state always gives the same paths.

/// <summary>What kind of evidence the workspace holds, from the workspace snapshot and the Source Analysis capability classifier.</summary>
public enum WorkflowEvidenceProfile { NoEvidence, DocumentsOnly, SourceOnly, InfrastructureOnly, SourceAndDocuments, RuntimeConfigurationOnly }

public enum WorkflowPathKind { Source, Documents, Runtime, ImportEvidence }

/// <summary>Ready = can start now; NeedsSetup = a concrete step first; Unavailable = the evidence it reads does not exist.</summary>
public enum WorkflowPathState { Ready, NeedsSetup, Unavailable }

/// <summary>Applicability of one setup item for the path the user is on. Optional is neutral, never a warning.</summary>
public enum SetupApplicability { RequiredNow, Recommended, Optional, NotApplicable, Unavailable }

public sealed record WorkflowLink(string Label, string Route);

/// <summary>One step of runtime setup in its fixed order: environment → Target Application → Project evidence → active → browser login.</summary>
public sealed record RuntimeSetupStep(string Key, string Label, bool Met, string Detail, string ActionLabel, string Route);

/// <summary>
/// One review path the user can take. It has exactly one primary action (the next step on that path); secondary links are plain
/// links to reviews that are already actionable on it.
/// </summary>
public sealed record WorkflowPath(
    WorkflowPathKind Kind, string Title, WorkflowPathState State, string StateLabel, string Summary, string Reason,
    string PrimaryLabel, string PrimaryRoute, IReadOnlyList<WorkflowLink> Links, IReadOnlyList<RuntimeSetupStep> Setup, string? Unlocks = null)
{
    public string Id => Kind.ToString().ToLowerInvariant();
}

public static class WorkflowPaths
{
    public const string ChoosePathKey = "ChooseReviewPath";
    public const string RuntimeSetupKeyPrefix = "RuntimeSetup:";

    public const string EnvironmentStep = "environment";
    public const string TargetApplicationStep = "target-application";
    public const string ProjectStep = "project";
    public const string ActiveStep = "active";
    public const string UnattendedLoginStep = "unattended-login";

    public const string AuthenticationRoute = "admin/system-settings?section=authentication";
    public const string RuntimeUnlocks = "Frontend runtime review, API review, integration review and performance execution. It does not affect document or source reviews.";

    /// <summary>Application or service code in the analyzed source (an infrastructure-only package has none of these).</summary>
    private static readonly HashSet<Capability> ApplicationCapabilities =
    [
        Capability.BackendApplication, Capability.FrontendApplication, Capability.RestApi, Capability.GraphQlApi, Capability.SoapApi, Capability.GrpcApi,
    ];

    public static WorkflowEvidenceProfile Classify(CurrentWorkspaceSnapshot workspace, ProjectInputs inputs, ProjectTechnologyCoverage? coverage)
    {
        var documents = workspace.AvailableRoleCount > 0;
        var source = inputs.Source.Status is ProjectInputStatus.Ready or ProjectInputStatus.NeedsAttention;
        if (documents && source) return WorkflowEvidenceProfile.SourceAndDocuments;
        if (documents) return WorkflowEvidenceProfile.DocumentsOnly;
        if (source)
        {
            // From the Source Analysis capability classifier, never from archive or file names.
            var capabilities = coverage?.Source?.Capabilities.Select(c => c.Capability).ToHashSet() ?? [];
            return capabilities.Overlaps([Capability.InfrastructureAsCode, Capability.Pipeline]) && !capabilities.Overlaps(ApplicationCapabilities)
                ? WorkflowEvidenceProfile.InfrastructureOnly : WorkflowEvidenceProfile.SourceOnly;
        }
        return inputs.Target.Status is ProjectInputStatus.Ready or ProjectInputStatus.Partial ? WorkflowEvidenceProfile.RuntimeConfigurationOnly : WorkflowEvidenceProfile.NoEvidence;
    }

    public static string Label(WorkflowEvidenceProfile profile) => profile switch
    {
        WorkflowEvidenceProfile.NoEvidence => "No evidence yet",
        WorkflowEvidenceProfile.DocumentsOnly => "Documents",
        WorkflowEvidenceProfile.SourceOnly => "Source",
        WorkflowEvidenceProfile.InfrastructureOnly => "Infrastructure / source",
        WorkflowEvidenceProfile.SourceAndDocuments => "Source and documents",
        WorkflowEvidenceProfile.RuntimeConfigurationOnly => "Runtime configuration only",
        _ => profile.ToString(),
    };

    public static string PathLabel(WorkflowPathKind? kind) => kind switch
    {
        WorkflowPathKind.Source => "Source review",
        WorkflowPathKind.Documents => "Document review",
        WorkflowPathKind.Runtime => "Runtime / integration review",
        _ => "None selected",
    };

    /// <summary>The review path named in the page address (?path=source|documents|runtime): navigation state only, never persisted.</summary>
    public static WorkflowPathKind? ParsePath(string? value) => value?.ToLowerInvariant() switch
    {
        "source" => WorkflowPathKind.Source,
        "documents" or "document" => WorkflowPathKind.Documents,
        "runtime" => WorkflowPathKind.Runtime,
        _ => null,
    };

    /// <summary>The three review paths (or, with no evidence and no environment, the two ways to start).</summary>
    public static IReadOnlyList<WorkflowPath> Build(WorkflowEvidenceProfile profile, CurrentWorkspaceSnapshot workspace, ProjectInputs inputs,
        IReadOnlyList<ReviewCapability> capabilities, IReadOnlyList<RuntimeSetupStep> runtimeSetup, WorkflowStepViewModel? openDocumentStep)
    {
        var runtime = Runtime(capabilities, runtimeSetup);
        // Nothing imported and nothing configured: the two ways to start. An imported package (even one without documents or analyzed source)
        // means the user is past the start, so the three review paths apply.
        if (profile == WorkflowEvidenceProfile.NoEvidence && workspace.Evidence is null && !workspace.IsImportedProject)
            return
            [
                new(WorkflowPathKind.ImportEvidence, "Import evidence", WorkflowPathState.NeedsSetup, "For document and source review",
                    "Import a project ZIP: its documents and source are analyzed in one upload. No Target Environment is needed.",
                    "Document and source reviews read imported evidence.", "Import evidence", ProjectInputPresentation.ProjectImportRoute,
                    [new("Open a Demo Project", ProjectInputPresentation.SampleProjectsRoute)], []),
                runtime with { StateLabel = "For runtime, integration and testing" },
            ];
        return [Source(inputs, capabilities, profile), Documents(workspace, inputs, capabilities, openDocumentStep), runtime];
    }

    private static WorkflowPath Source(ProjectInputs inputs, IReadOnlyList<ReviewCapability> capabilities, WorkflowEvidenceProfile profile)
    {
        var title = profile == WorkflowEvidenceProfile.InfrastructureOnly ? "Source / infrastructure review" : "Source review";
        var links = capabilities.Where(c => c.Section == ReviewSection.Source && c.IsActionable).Select(c => new WorkflowLink(c.DisplayName, c.Route)).ToList();
        return inputs.Source.Status switch
        {
            ProjectInputStatus.Ready => new(WorkflowPathKind.Source, title, WorkflowPathState.Ready, "Ready now",
                profile == WorkflowEvidenceProfile.InfrastructureOnly ? "Infrastructure and pipeline source is analyzed and ready." : "Source evidence is analyzed and ready.",
                "Source evidence is already analyzed, so source-based reviews can start immediately.",
                "Continue with source review", ProjectInputPresentation.SourceAnalysisRoute, links, []),
            ProjectInputStatus.NeedsAttention => new(WorkflowPathKind.Source, title, WorkflowPathState.NeedsSetup, "Needs refresh",
                inputs.Source.Detail, "The analyzed source is older than the current analyzers.", "Analyze the source again", ProjectInputPresentation.SourceAnalysisRoute, links, []),
            _ => new(WorkflowPathKind.Source, title, WorkflowPathState.Unavailable, "Requires source",
                "No source snapshot is analyzed yet.", "Source reviews read an analyzed source snapshot.", "Import source", ProjectInputPresentation.ProjectImportSourceRoute, [], []),
        };
    }

    private static WorkflowPath Documents(CurrentWorkspaceSnapshot workspace, ProjectInputs inputs, IReadOnlyList<ReviewCapability> capabilities, WorkflowStepViewModel? open)
    {
        var links = capabilities.Where(c => c.Section is ReviewSection.Documentation or ReviewSection.Traceability && c.IsActionable)
            .Select(c => new WorkflowLink(c.DisplayName, c.Route)).ToList();
        if (workspace.AvailableRoleCount > 0 && inputs.Documents.Status == ProjectInputStatus.Ready)
            return new(WorkflowPathKind.Documents, "Document review", WorkflowPathState.Ready, "Ready now",
                $"{workspace.RoleSummary} available.", "Supported project documents are available, so document reviews can start now.",
                open is null ? "Continue with document review" : $"Open {open.Title}", open?.Route ?? links.FirstOrDefault()?.Route ?? ProjectInputPresentation.ExplorerRoute(WorkspaceArtifactType.Specification), links, []);
        if (inputs.Documents.Status == ProjectInputStatus.NeedsAttention)
            return new(WorkflowPathKind.Documents, "Document review", WorkflowPathState.NeedsSetup, inputs.Documents.StatusLabel,
                inputs.Documents.Detail, "The documents exist but need a choice before reviews can read them.", inputs.Documents.ActionLabel, inputs.Documents.Route, links, []);
        return new(WorkflowPathKind.Documents, "Document review", WorkflowPathState.Unavailable, "Requires project documents",
            "No supported project documents are currently available.", "Document reviews read Specification, Plan, Tasks, Constitution or Data Model documents.",
            "Import project documents", ProjectInputPresentation.ProjectImportSourceRoute, [], []);
    }

    private static WorkflowPath Runtime(IReadOnlyList<ReviewCapability> capabilities, IReadOnlyList<RuntimeSetupStep> setup)
    {
        var links = capabilities.Where(c => c.Section == ReviewSection.Runtime && c.IsActionable).Select(c => new WorkflowLink(c.DisplayName, c.Route)).ToList();
        if (setup.FirstOrDefault(s => !s.Met) is { } next)
            return new(WorkflowPathKind.Runtime, "Runtime / integration review", WorkflowPathState.NeedsSetup,
                next.Key == EnvironmentStep ? "Requires Target Environment" : "Setup in progress",
                next.Key == EnvironmentStep ? "A Target Environment is required before runtime-dependent reviews." : $"Next: {next.Label}. {next.Detail}",
                "Runtime reviews observe or execute against the deployed system, so they need where (environment), what (application) and which evidence (project).",
                next.ActionLabel, next.Route, links, setup, RuntimeUnlocks);
        return new(WorkflowPathKind.Runtime, "Runtime / integration review", WorkflowPathState.Ready, "Ready now",
            "The Target Environment, Target Application and Project evidence are in place.", "Runtime setup is complete for the active environment.",
            links.Count > 0 ? $"Open {links[0].Label}" : "Open Target Environments", links.Count > 0 ? links[0].Route : NavigationCatalog.TargetEnvironmentsRoute, links, setup, RuntimeUnlocks);
    }

    /// <summary>
    /// Runtime setup in its fixed order. The environment considered is the active one, else the only one, else none (the user chooses).
    /// Activation is a separate, explicit step at the end: configuring Target Application or Project never activates an environment.
    /// </summary>
    public static IReadOnlyList<RuntimeSetupStep> RuntimeSetup(IReadOnlyList<FrontendAnalysisProfile> profiles, string? activeProfileId,
        Func<string, ProjectEvidence.ProjectEvidenceView?> evidenceOf)
    {
        var steps = new List<RuntimeSetupStep>();
        var active = profiles.FirstOrDefault(p => p.Id == activeProfileId);
        var environment = active ?? (profiles.Count == 1 ? profiles[0] : null);
        if (environment is null)
        {
            steps.Add(new(EnvironmentStep, "Target Environment", false,
                profiles.Count == 0 ? "Where are we testing? No Target Environment exists yet." : $"{profiles.Count} Target Environments exist; choose the one to test and set it active.",
                profiles.Count == 0 ? "Choose Target Environment" : "Choose a Target Environment", NavigationCatalog.TargetEnvironmentsRoute));
            return steps;
        }
        var name = environment.Name is { Length: > 0 } n ? n : "the environment";
        steps.Add(new(EnvironmentStep, "Target Environment", true, $"{name}{(active is null ? " (configured, not active)" : " (active)")}", "Open Target Environments", TabHref(environment.Id, "general")));

        var identity = ProjectEvidence.TargetApplications.Of(environment);
        var urlReady = ProjectInputPresentation.Target(environment).Status == ProjectInputStatus.Ready;
        steps.Add(new(TargetApplicationStep, "Target Application", identity is not null && urlReady,
            identity is null ? $"What application/system does {name} represent? Choose it, then set its URL."
            : urlReady ? $"{identity.Name} · {environment.TargetUrl}" : $"{identity.Name} · set its URL and run the target check.",
            "Configure Target Application", TabHref(environment.Id, "target")));

        if (environment.Project is null)
            steps.Add(new(ProjectStep, identity is null ? "Project evidence" : $"Project evidence for {identity.Name}", false,
                $"Bind the imported evidence (source, documents, contracts, infrastructure) of {identity?.Name ?? $"the application {name} represents"}.",
                "Configure Project evidence", ProjectEvidence.ProjectEvidenceResolver.ProjectTabHref(environment.Id)));
        else if (evidenceOf(environment.Id) is { RequiredReadyAll: false } partial)
            steps.Add(new(ProjectStep, $"Project evidence for {partial.ProjectName}", false,
                $"{partial.RequiredReady} / {partial.RequiredTotal} required ready. Missing: {string.Join(", ", partial.BlockingLabels)}.",
                "Review Project evidence", ProjectEvidence.ProjectEvidenceResolver.ProjectTabHref(environment.Id)));
        else
            steps.Add(new(ProjectStep, $"Project evidence for {environment.Project.ProjectName}", true, "Required evidence is ready.",
                "Review Project evidence", ProjectEvidence.ProjectEvidenceResolver.ProjectTabHref(environment.Id)));

        // Only execution needs an active environment, so activation is the last runtime step and never recommended for static reviews.
        steps.Add(new(ActiveStep, "Active environment", active is not null, active is not null ? $"{name} is active." : $"{name} is configured but not active. Runtime execution uses the active environment.",
            $"Set {name} active", TabHref(environment.Id, "general")));
        return steps;
    }

    /// <summary>
    /// Browser login for an execution mode, appended only when the user is about to run automated browser work. A manual run uses
    /// attended login (unchanged, always available) and adds no step; static reviews never get one.
    /// </summary>
    public static IReadOnlyList<RuntimeSetupStep> WithBrowserLogin(IReadOnlyList<RuntimeSetupStep> setup, bool automated, AutomatedBrowserExecutionReadiness? unattended)
    {
        if (!automated) return setup;
        var ready = unattended?.Available == true;
        return [.. setup, new(UnattendedLoginStep, "Unattended browser login", ready,
            ready ? "Unattended browser login is ready." : unattended?.Reason ?? "Unattended browser login readiness could not be read.",
            "Configure unattended browser login", AuthenticationRoute)];
    }

    /// <summary>
    /// The runtime path for an execution mode: automated execution appends the unattended browser login step (and makes it the primary
    /// action when it is the only one missing); a manual run uses attended login and is unchanged. Other paths are returned as they are.
    /// </summary>
    public static WorkflowPath ForExecution(WorkflowPath path, bool automated, AutomatedBrowserExecutionReadiness? unattended)
    {
        if (path.Kind != WorkflowPathKind.Runtime || !automated) return path;
        var setup = WithBrowserLogin(path.Setup, automated, unattended);
        return setup.FirstOrDefault(s => !s.Met) is { } next
            ? path with
            {
                Setup = setup, State = WorkflowPathState.NeedsSetup,
                StateLabel = next.Key == EnvironmentStep ? "Requires Target Environment" : "Setup in progress",
                Summary = next.Key == EnvironmentStep ? path.Summary : $"Next: {next.Label}. {next.Detail}",
                PrimaryLabel = next.ActionLabel, PrimaryRoute = next.Route,
            }
            : path with { Setup = setup };
    }

    /// <summary>The next step on a chosen path, as the workflow's primary recommendation.</summary>
    public static WorkflowStepViewModel NextStepOf(WorkflowPath path) => new()
    {
        Number = 1,
        Key = path.Kind == WorkflowPathKind.Runtime && path.Setup.FirstOrDefault(s => !s.Met) is { } step ? RuntimeSetupKeyPrefix + step.Key : $"Path:{path.Id}",
        Title = path.Kind == WorkflowPathKind.Runtime && path.Setup.FirstOrDefault(s => !s.Met) is { } s ? s.ActionLabel : path.PrimaryLabel,
        Description = path.Kind == WorkflowPathKind.Runtime && path.Setup.FirstOrDefault(s => !s.Met) is { } d ? d.Detail : path.Summary,
        Route = path.PrimaryRoute,
        ActionLabel = path.PrimaryLabel,
        CanOpen = true,
        IsCurrent = true,
        Status = WorkflowStepStatus.Available,
        Prerequisites = PrerequisiteState.Available,
        ReviewState = ReviewState.NotStarted,
        ApprovalState = ApprovalState.Pending,
    };

    /// <summary>Target Environments opened on one environment's tab.</summary>
    public static string TabHref(string environmentId, string tab) =>
        $"/admin/system-settings?section=target-environments&tab={tab}&profile={Uri.EscapeDataString(environmentId)}";

    /// <summary>How an input applies to the chosen path (or, with none chosen, to the evidence at hand).</summary>
    public static (SetupApplicability Applicability, string Label) InputApplicability(ProjectInput input, WorkflowPathKind? path) => (input.Kind, input.Status, path) switch
    {
        (_, ProjectInputStatus.Ready, _) => (SetupApplicability.Recommended, "Ready"),
        (ProjectInputKind.Target, _, WorkflowPathKind.Runtime) => (SetupApplicability.RequiredNow, "Required for this path"),
        (ProjectInputKind.Target, ProjectInputStatus.Partial, _) => (SetupApplicability.Optional, "Optional · incomplete"),
        (ProjectInputKind.Target, _, _) => (SetupApplicability.Optional, "Optional"),
        (ProjectInputKind.Documents, _, WorkflowPathKind.Documents) => (SetupApplicability.RequiredNow, "Required for this path"),
        (ProjectInputKind.Documents, ProjectInputStatus.NeedsAttention, _) => (SetupApplicability.Recommended, "Needs attention"),
        (ProjectInputKind.Documents, _, _) => (SetupApplicability.Unavailable, "Unavailable"),
        (ProjectInputKind.Source, ProjectInputStatus.NeedsAttention, _) => (SetupApplicability.Recommended, "Needs attention"),
        (ProjectInputKind.Source, _, WorkflowPathKind.Source) => (SetupApplicability.RequiredNow, "Required for this path"),
        (ProjectInputKind.Source, _, _) => (SetupApplicability.Unavailable, "Unavailable"),
        _ => (SetupApplicability.Optional, "Optional"),
    };

    /// <summary>What an input is for, in one line, without implying it is mandatory for every project.</summary>
    public static string InputPurpose(ProjectInputKind kind) => kind switch
    {
        ProjectInputKind.Documents => "Needed for document reviews only.",
        ProjectInputKind.Source => "Needed for source and infrastructure reviews only.",
        _ => "Needed for runtime, integration and performance reviews only.",
    };
}
