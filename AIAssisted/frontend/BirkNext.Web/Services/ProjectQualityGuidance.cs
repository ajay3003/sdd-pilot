namespace BirkNext.Web.Services;

/// <summary>Where the Help Center explains a recommended action. One mapping for the Dashboard and Recommended Workflow.</summary>
public static class RecommendedActionHelp
{
    public static string? TopicFor(DashboardNextStep? next) => next is null ? null : TopicFor(next.Key, next.Href);

    public static string? TopicFor(string key, string? route)
    {
        var r = (route ?? "").TrimStart('/');
        return key switch
        {
            "none" => "recommended-workflow",
            WorkflowReadinessService.LoadWorkspaceKey or WorkflowReadinessService.LoadSampleProjectKey or WorkflowReadinessService.ImportProjectDocumentsKey => "getting-started",
            WorkflowReadinessService.ChooseArtifactsKey => "getting-started",
            WorkflowReadinessService.ConfigureProjectKey or WorkflowReadinessService.BindProjectEvidenceKey => "project-model",
            WorkflowReadinessService.RefreshSourceKey or WorkflowReadinessService.EstablishSourceBindingKey => "source-analysis",
            WorkflowReadinessService.CompleteTargetKey or WorkflowReadinessService.ChooseTargetEnvironmentKey => "target-environment",
            _ when r.StartsWith(DashboardPresentation.DocumentReviewRoute, StringComparison.Ordinal) => "quality-reviews",
            _ when r.StartsWith(DashboardPresentation.TraceabilityRoute, StringComparison.Ordinal) || key.Contains("Traceability", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Implementation", StringComparison.OrdinalIgnoreCase) => "traceability",
            _ when r.StartsWith("api-quality-review/contracts", StringComparison.Ordinal) => "contract-testing",
            _ when r.StartsWith(DashboardPresentation.PerformanceRoute, StringComparison.Ordinal) => "performance-test-review",
            _ when r.EndsWith("-explorer", StringComparison.Ordinal) => "explorers",
            _ => "recommended-workflow",
        };
    }
}

/// <summary>One workflow stage: what it is for, what the evidence says now, and where to go. Never "complete" without a recorded result.</summary>
public sealed record ProjectQualityStage(string Id, int Number, string Title, string Purpose, string Status, string Detail, string? ActionLabel, string? Href, string HelpTopic,
    ProjectQualityStageState State);

/// <summary>NotStarted: nothing recorded yet. InProgress: some evidence or decisions recorded. Recorded: every applicable decision recorded
/// (still not a quality verdict). NeedsSetup: a prerequisite is missing. NotAssessedHere: owned by another module.</summary>
public enum ProjectQualityStageState { NotStarted, InProgress, Recorded, NeedsSetup, NotAssessedHere }

/// <summary>
/// The five workflow stages of Recommended Workflow, derived only from recorded state: the current workspace and its evidence, recorded review
/// decisions, review capabilities and actual execution runs. Opening a page or clicking a button never moves a stage. The stages describe; the
/// single next action still comes from <see cref="WorkflowReadinessService"/> (activities can run in parallel).
/// </summary>
public static class ProjectQualityStages
{
    private static readonly HashSet<string> SetupKeys = new(StringComparer.Ordinal)
    {
        WorkflowReadinessService.LoadWorkspaceKey, WorkflowReadinessService.LoadSampleProjectKey, WorkflowReadinessService.ImportProjectDocumentsKey,
        WorkflowReadinessService.ChooseArtifactsKey, WorkflowReadinessService.ConfigureProjectKey, WorkflowReadinessService.BindProjectEvidenceKey,
        WorkflowReadinessService.RefreshSourceKey, WorkflowReadinessService.EstablishSourceBindingKey,
    };

    public static IReadOnlyList<ProjectQualityStage> For(WorkflowReadiness workflow, ExecutionEvidenceFacts? execution)
    {
        var next = workflow.NextRecommendedAction;
        // 1. Understand the project: inputs present and bound.
        ProjectQualityStage understand = !workflow.WorkspaceLoaded && !workflow.Inputs.AnyProvided
            ? new("understand", 1, "Understand the project", "Import the project and bind its evidence.", "Not started", "No project is loaded.", "Import project", ProjectInputPresentation.ProjectImportRoute, "getting-started", ProjectQualityStageState.NotStarted)
            : next is not null && SetupKeys.Contains(next.Key)
                ? new("understand", 1, "Understand the project", "Import the project and bind its evidence.", "In progress", next.Title, next.ActionLabel, next.Route, RecommendedActionHelp.TopicFor(next.Key, next.Route) ?? "getting-started", ProjectQualityStageState.InProgress)
                : new("understand", 1, "Understand the project", "Import the project and bind its evidence.", "Evidence in place", "The project is loaded and its required evidence is bound. This is not a quality result.", "Open Source Analysis", "source-analysis", "source-analysis", ProjectQualityStageState.Recorded);

        // 2. Review requirements & traceability: recorded review decisions on the current documents.
        var gates = workflow.DocumentReviewGates.Select(ArtifactReviewPresentation.Of).ToList();
        var decided = gates.Count(g => g.IsDecided);
        var gaps = gates.Count(g => g.State == ArtifactReviewState.NeedsChanges);
        var stale = gates.Count(g => g.State == ArtifactReviewState.Stale);
        ProjectQualityStage requirements = gates.Count == 0
            ? new("requirements", 2, "Review requirements & traceability", "Review specifications, plans and tasks and how they trace.", "No review applies", "No document review step applies to the loaded evidence.", null, null, "traceability", ProjectQualityStageState.NotStarted)
            : new("requirements", 2, "Review requirements & traceability", "Review specifications, plans and tasks and how they trace.",
                $"{decided} of {gates.Count} decisions recorded",
                (gaps > 0 ? $"{gaps} reviewed with gaps. " : "") + (stale > 0 ? $"{stale} out of date — the document changed. " : "") + (decided == 0 ? "No review decision recorded yet." : "A decision records a human review; it does not prove runtime behaviour."),
                null, null, "traceability",
                decided == 0 ? ProjectQualityStageState.NotStarted : decided == gates.Count && gaps == 0 && stale == 0 ? ProjectQualityStageState.Recorded : ProjectQualityStageState.InProgress);

        // 3. Assess technical quality: available reviews; completion is not tracked for most of them, and the stage says so.
        var technical = workflow.Capabilities.Where(c => c.Section is ReviewSection.Source or ReviewSection.Change or ReviewSection.Runtime && c.Applicability != ReviewApplicabilityState.NotApplicable).ToList();
        var available = technical.Count(c => c.IsActionable);
        var setup = technical.Count(c => c.Readiness == ReviewReadiness.NeedsSetup);
        ProjectQualityStage assess = new("assess", 3, "Assess technical quality", "Source, runtime and change reviews of the implementation.",
            technical.Count == 0 ? "No review applies" : $"{available} available · {setup} need setup",
            technical.Count == 0 ? "No source, runtime or change review applies to this project." : "These reviews report results in their own pages; BirkNext records no completion for them here.",
            technical.FirstOrDefault(c => c.IsActionable) is { } first ? $"Open {first.DisplayName}" : null, technical.FirstOrDefault(c => c.IsActionable)?.Route, "quality-reviews",
            technical.Count == 0 ? ProjectQualityStageState.NotStarted : available == 0 && setup > 0 ? ProjectQualityStageState.NeedsSetup : ProjectQualityStageState.InProgress);

        // 4. Prepare & run tests: only recorded runs count; a generated test is not an executed one.
        ProjectQualityStage run = execution switch
        {
            null => new("run", 4, "Prepare & run tests", "Contract, integration and browser tests against an approved Target Environment.", "Not read", "Recorded runs were not read here.", "Open Test Execution & Evidence", "execution-results", "execution-results", ProjectQualityStageState.NotStarted),
            { Readable: false } => new("run", 4, "Prepare & run tests", "Contract, integration and browser tests against an approved Target Environment.", "Unavailable", "Test Execution & Evidence could not be read; this is not the same as no runs.", "Open Test Execution & Evidence", "execution-results", "execution-results", ProjectQualityStageState.NotStarted),
            { Runs: 0 } => new("run", 4, "Prepare & run tests", "Contract, integration and browser tests against an approved Target Environment.", "No recorded runs", "Nothing has been executed yet. Generated tests are not executions.", "Open Contract Testing", "api-quality-review/contracts", "contract-testing", ProjectQualityStageState.NotStarted),
            { } e => new("run", 4, "Prepare & run tests", "Contract, integration and browser tests against an approved Target Environment.", $"{e.Runs} recorded run{(e.Runs == 1 ? "" : "s")}",
                $"{e.Passed} passed · {e.Failed} failed or error. A historical result is not evidence for changed definitions.", "Open Test Execution & Evidence", "execution-results", "execution-results", ProjectQualityStageState.InProgress),
        };

        // 5. Evidence & release readiness: owned by Release Readiness; never calculated here.
        ProjectQualityStage readiness = new("readiness", 5, "Review evidence & release readiness", "Decide readiness from the recorded evidence.", "Not assessed here",
            "Release readiness is owned by Release Readiness; this page never calculates it.", "Open Release Readiness", "release-readiness", "readiness", ProjectQualityStageState.NotAssessedHere);

        return [understand, requirements, assess, run, readiness];
    }
}
