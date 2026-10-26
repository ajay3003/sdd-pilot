using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>
/// The Dashboard's status vocabulary for testers. Each state names what the tester can do, never an implementation concept:
/// <see cref="Available"/> = the input exists; <see cref="ReadyToReview"/> = a review can be started; <see cref="NotAssessed"/> = no result yet;
/// <see cref="NeedsSetup"/> = a prerequisite is missing; <see cref="AttentionNeeded"/> = a review ran and found something material;
/// <see cref="Passed"/> = only when a review actually supports it; <see cref="NotApplicable"/> = does not apply to this project (never the same as
/// not assessed). Loading, unavailable and failed are their own states — none is shown as a bare dash.
/// </summary>
public enum DashboardStatus
{
    Loading,
    Unavailable,
    Failed,
    NeedsSetup,
    NotAssessed,
    ReadyToReview,
    Available,
    Partial,
    InProgress,
    AttentionNeeded,
    Passed,
    NotApplicable,
}

/// <summary>A Project Health area: plain-language help, one state, why, and what to do. <see cref="Prominent"/> areas earn a full card.</summary>
public sealed record DashboardHealthArea(
    string Key, string Area, string Help, DashboardStatus Status, string Explanation, string? ActionLabel, string? Href,
    string? Result = null, bool Secondary = false)
{
    /// <summary>A large card only when there is something to do or a result to show; an area with nothing yet is a compact row.</summary>
    public bool Prominent => Status is DashboardStatus.ReadyToReview or DashboardStatus.InProgress or DashboardStatus.AttentionNeeded
        or DashboardStatus.Passed or DashboardStatus.Failed && !(Secondary && Status == DashboardStatus.ReadyToReview);
}

/// <summary>One actual condition a tester should act on (or know about), with the action that resolves it.</summary>
public sealed record DashboardAttentionItem(string Key, string Text, string? Detail, string ActionLabel, string Href, bool Informational = false,
    IReadOnlyList<string>? Items = null);

/// <summary>One thing discovery found ("23 feature packs"). Only evidence that exists is listed.</summary>
public sealed record DashboardFoundFact(string Key, string Text);

/// <summary>The recommended next step in tester terms: what, why, what it achieves, and what follows.</summary>
public sealed record DashboardNextStep(string Key, string Title, string Reason, string Achieves, string ActionLabel, string Href,
    IReadOnlyList<string> Then);

/// <summary>A pre-review readiness row: whether a review or test can START with this input. Never a quality result.</summary>
public sealed record DashboardReadinessRow(string Key, string Label, DashboardStatus Status, string StatusLabel, string Text);

/// <summary>One Project Artifacts card: count and pack context first, one status, secondary metadata, files only under Details.</summary>
public sealed record DashboardArtifactCard(
    WorkspaceArtifactType Role, string Title, string Summary, DashboardStatus Status, string StatusLabel, string Meta,
    IReadOnlyList<string> Attention, string ActionLabel, string Href, IReadOnlyList<string> Files, int MoreFiles, IReadOnlyList<string> Provenance,
    IReadOnlyList<string> AttentionItems);

/// <summary>Top Risks: assessed or not. "No significant risks found" only after a review that assesses risks ran.</summary>
public sealed record DashboardRiskState(bool Assessed, int Count, string Message);

/// <summary>Review results of this session that the Dashboard reads (from the shared report snapshot and session services).</summary>
public sealed record DashboardReviewFacts
{
    public AnalysisSessionStatus DocumentReview { get; init; } = AnalysisSessionStatus.NotRun;
    public DateTimeOffset? DocumentReviewAt { get; init; }
    public int DocumentReviewFindings { get; init; }
    public int DocumentReviewSevere { get; init; }
    /// <summary>"Pack Revision System · module Person": what the latest review read, from its recorded inputs.</summary>
    public string? DocumentReviewScope { get; init; }
    public bool QaAssessed { get; init; }
    public double QaScore { get; init; }

    public bool TraceabilityRun { get; init; }
    public double? TraceabilityCoverage { get; init; }
    public bool ComplianceRun { get; init; }
    public double ComplianceScore { get; init; }
    public bool DeliveryRun { get; init; }
    public ReadinessState? DevelopmentDecision { get; init; }
    public ReadinessState? TestingDecision { get; init; }
    public ReadinessState? ReleaseDecision { get; init; }
    /// <summary>True only when a project policy makes the documentation gate a release gate; otherwise the release decision is advisory.</summary>
    public bool ReleaseSignificant { get; init; }
    public bool RisksAssessed { get; init; }
    public int RiskCount { get; init; }

    public RuntimeReviewStatus Performance { get; init; } = RuntimeReviewStatus.NotRun;
    public double? PerformanceScore { get; init; }
    public PerformanceReadinessState? PerformanceState { get; init; }
    /// <summary>A runtime review (frontend, API, integration, performance, security) has a result this session.</summary>
    public bool RuntimeEvidence { get; init; }
}

/// <summary>
/// The SDD Governance Dashboard for testers: what was found, what needs attention, what to do next, and what has actually been reviewed.
/// Every value is derived from existing shared state — the current workspace (with its project-wide pack overview), Recommended Workflow
/// readiness (next action, review gates, Target Environment and source inputs) and this session's review results. No score, percentage or
/// readiness is invented here, and import metadata is never presented as a review result.
/// </summary>
public static class DashboardPresentation
{
    public const string WorkflowRoute = "getting-started";
    public const string DocumentReviewRoute = "quality/document";
    public const string TraceabilityRoute = "artifact-traceability";
    public const string PerformanceRoute = "performance-test-review";

    // ── Vocabulary ────────────────────────────────────────────────────────────

    public static string Label(DashboardStatus status) => status switch
    {
        DashboardStatus.Loading => "Loading…",
        DashboardStatus.Unavailable => "Unavailable",
        DashboardStatus.Failed => "Failed to load",
        DashboardStatus.NeedsSetup => "Needs setup",
        DashboardStatus.NotAssessed => "Not assessed",
        DashboardStatus.ReadyToReview => "Ready to review",
        DashboardStatus.Available => "Available",
        DashboardStatus.Partial => "Partial",
        DashboardStatus.InProgress => "In progress",
        DashboardStatus.AttentionNeeded => "Attention needed",
        DashboardStatus.Passed => "Passed",
        _ => "Not applicable",
    };

    /// <summary>A text glyph beside the label so no state is told by colour alone (the label carries the meaning; the glyph is decorative).</summary>
    public static string Icon(DashboardStatus status) => status switch
    {
        DashboardStatus.Loading => "…",
        DashboardStatus.Unavailable or DashboardStatus.Failed => "✕",
        DashboardStatus.NeedsSetup => "⚙",
        DashboardStatus.ReadyToReview => "▶",
        DashboardStatus.Available => "✓",
        DashboardStatus.Partial => "◑",
        DashboardStatus.InProgress => "◐",
        DashboardStatus.AttentionNeeded => "!",
        DashboardStatus.Passed => "✓",
        DashboardStatus.NotApplicable => "–",
        _ => "○",
    };

    /// <summary>CSS modifier for the status pill.</summary>
    public static string Tone(DashboardStatus status) => status switch
    {
        DashboardStatus.Passed or DashboardStatus.Available => "good",
        DashboardStatus.AttentionNeeded or DashboardStatus.Failed => "attention",
        DashboardStatus.NeedsSetup or DashboardStatus.Partial => "setup",
        DashboardStatus.ReadyToReview or DashboardStatus.InProgress => "ready",
        _ => "neutral",
    };

    /// <summary>"Specification" → "specifications": the plural noun testers read.</summary>
    public static string Plural(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "constitutions",
        WorkspaceArtifactType.Specification => "specifications",
        WorkspaceArtifactType.Plan => "plans",
        WorkspaceArtifactType.Tasks => "task lists",
        WorkspaceArtifactType.DataModel => "data models",
        _ => role.ToString().ToLowerInvariant() + "s",
    };

    public static string Singular(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "constitution",
        WorkspaceArtifactType.Specification => "specification",
        WorkspaceArtifactType.Plan => "plan",
        WorkspaceArtifactType.Tasks => "task list",
        WorkspaceArtifactType.DataModel => "data model",
        _ => role.ToString().ToLowerInvariant(),
    };

    private static string Count(int n, WorkspaceArtifactType role) => $"{n} {(n == 1 ? Singular(role) : Plural(role))}";
    private static string S(int n) => n == 1 ? "" : "s";
    private static string Packs(int n) => $"{n} feature pack{S(n)}";

    // ── Workspace summary ─────────────────────────────────────────────────────

    /// <summary>"14 modules · 23 feature packs · 5 document types" — counts that exist, nothing about repository internals.</summary>
    public static string? WorkspaceSummary(CurrentWorkspaceSnapshot workspace)
    {
        if (!workspace.WorkspaceLoaded) return null;
        var parts = new List<string>();
        if (workspace.Overview is { } o)
        {
            if (o.HasModules) parts.Add($"{o.Modules} modules");
            if (o.PackCount > 0) parts.Add(Packs(o.PackCount));
            var types = o.Roles.Count(r => r.Found && CurrentWorkspaceSnapshot.WorkflowRoles.Contains(r.Role));
            parts.Add($"{types} document type{S(types)}");
        }
        else
        {
            if (workspace.HasModules) parts.Add($"{workspace.ModuleCount} modules");
            if (workspace.PackCount > 0) parts.Add(Packs(workspace.PackCount));
            parts.Add($"{workspace.AvailableRoleCount} document type{S(workspace.AvailableRoleCount)}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>"Test environment: Development" (configured is not tested), or why there is none.</summary>
    public static (DashboardStatus Status, string Text) TestEnvironment(WorkflowReadiness? workflow)
    {
        if (workflow is null) return (DashboardStatus.Loading, "Test environment: Loading…");
        var target = workflow.Inputs.Target;
        var name = target.Facts.FirstOrDefault();
        return target.Status switch
        {
            ProjectInputStatus.Ready => (DashboardStatus.Available, $"Test environment: {name}"),
            ProjectInputStatus.Partial => (DashboardStatus.NeedsSetup, $"Test environment: {name} ({target.StatusLabel.ToLowerInvariant()})"),
            ProjectInputStatus.Unknown => (DashboardStatus.Unavailable, "Test environment: Unavailable"),
            _ => (DashboardStatus.NeedsSetup, "Test environment: Not selected"),
        };
    }

    // ── What BirkNext found ───────────────────────────────────────────────────

    public static IReadOnlyList<DashboardFoundFact> Found(CurrentWorkspaceSnapshot workspace, WorkflowReadiness? workflow)
    {
        var facts = new List<DashboardFoundFact>();
        if (!workspace.WorkspaceLoaded) return facts;
        if (workspace.Overview is { } o)
        {
            if (o.HasModules) facts.Add(new("modules", $"{o.Modules} modules"));
            if (o.PackCount > 0) facts.Add(new("packs", Packs(o.PackCount)));
            foreach (var role in new[] { WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan, WorkspaceArtifactType.Tasks,
                         WorkspaceArtifactType.DataModel, WorkspaceArtifactType.Constitution })
                if (o.Role(role) is { Found: true } coverage) facts.Add(new(role.ToString(), Count(coverage.Documents, role)));
        }
        else
        {
            if (workspace.HasModules) facts.Add(new("modules", $"{workspace.ModuleCount} modules"));
            foreach (var role in workspace.AvailableRoles) facts.Add(new(role.Role.ToString(), Count(role.ArtifactCount, role.Role)));
        }
        if (workflow?.Inputs.Target is { Status: ProjectInputStatus.Ready or ProjectInputStatus.Partial } target)
            facts.Add(new("target", $"1 test environment configured ({target.Facts.FirstOrDefault()})"));
        if (workflow?.Inputs.Source is { Status: ProjectInputStatus.Ready or ProjectInputStatus.NeedsAttention })
            facts.Add(new("source", "Source code analyzed"));
        return facts;
    }

    // ── Needs attention ───────────────────────────────────────────────────────

    /// <summary>
    /// Actual current conditions only, most important first. Several packs are normal and never listed; a same-pack ambiguity is, with a
    /// Resolve action; a pack lacking a core document is, with that pack named — never the whole module or project.
    /// </summary>
    public static IReadOnlyList<DashboardAttentionItem> NeedsAttention(CurrentWorkspaceSnapshot workspace, WorkflowReadiness? workflow, DashboardReviewFacts reviews)
    {
        var items = new List<DashboardAttentionItem>();
        if (workspace.State == CurrentWorkspaceState.Error)
        {
            items.Add(new("workspace-error", "The project's documents could not be read.", workspace.Error, "Open Import Project", ProjectInputPresentation.ProjectImportRoute));
            return items;
        }
        if (!workspace.WorkspaceLoaded) return items;

        foreach (var (role, ambiguities) in Ambiguities(workspace))
        {
            var n = ambiguities.Count;
            var text = n == 1
                ? $"{Cap(ambiguities[0].Where)} has {ambiguities[0].Candidates.Count} possible {Singular(role)} files"
                : $"{n} places have more than one possible {Singular(role)} file";
            items.Add(new($"ambiguous-{role}", text, "Reviews read one file per feature pack. Choose which one to use.", "Resolve",
                ProjectInputPresentation.ExplorerRoute(role),
                Items: [.. ambiguities.Select(a => $"{Cap(a.Where)}: {string.Join(", ", a.Candidates)}")]));
        }

        if (workspace.Overview is { PackCount: > 0 } o)
        {
            foreach (var role in ProjectPackOverview.CoreRoles)
            {
                var missing = o.Role(role).PacksMissing;
                if (missing.Count == 0) continue;
                items.Add(new($"missing-{role}", $"{missing.Count} feature pack{S(missing.Count)} {(missing.Count == 1 ? "is" : "are")} missing a {Singular(role)}",
                    $"{missing.Count} of {o.PackCount} packs have no {Singular(role)} of their own and no shared one applies.",
                    $"Explore {Plural(role)}", ProjectInputPresentation.ExplorerRoute(role),
                    Items: [.. missing.Select(p => $"{ProjectPackOverview.PackName(p)}{(o.HasModules ? $" · module {ArtifactModules.Label(p.Module)}" : "")}")]));
            }
        }

        var gates = workflow?.DocumentReviewGates.Select(ArtifactReviewPresentation.Of).ToList() ?? [];
        var changes = gates.Count(g => g.State == ArtifactReviewState.NeedsChanges);
        if (changes > 0)
            items.Add(new("needs-changes", $"{changes} document review{S(changes)} marked as needing changes", null, "Open Recommended Workflow", WorkflowRoute));
        var stale = gates.Count(g => g.State == ArtifactReviewState.Stale);
        if (stale > 0)
            items.Add(new("stale", $"{stale} document review{S(stale)} out of date", "The document changed after it was reviewed.", "Review again", WorkflowRoute));

        if (reviews.DocumentReview == AnalysisSessionStatus.Completed && reviews.DocumentReviewSevere > 0)
            items.Add(new("severe-findings", $"{reviews.DocumentReviewSevere} critical or high finding{S(reviews.DocumentReviewSevere)} in the latest Document Quality Review",
                reviews.DocumentReviewScope, "Open results", DocumentReviewRoute));
        if (reviews.DocumentReview == AnalysisSessionStatus.Failed)
            items.Add(new("dqr-failed", "The latest Document Quality Review failed", null, "Open Document Quality Review", DocumentReviewRoute));

        if (workflow?.Inputs.Source is { Status: ProjectInputStatus.NeedsAttention } source)
            items.Add(new("source", "Source analysis is out of date", source.Detail, "Analyze source", ProjectInputPresentation.SourceAnalysisRoute));
        if (workflow?.Inputs.Target is { Status: ProjectInputStatus.Partial } partial)
            items.Add(new("target-incomplete", "The test environment is not fully set up", partial.Detail, "Finish setup", partial.Route));

        if (HasDocuments(workspace) && reviews.DocumentReview == AnalysisSessionStatus.NotRun)
            items.Add(new("dqr-not-run", "Document Quality Review has not been run", "Nothing has been reviewed yet; import does not assess quality.",
                "Start review", DocumentReviewRoute, Informational: true));
        if (workflow?.Inputs.Target is { Status: ProjectInputStatus.Absent })
            items.Add(new("target-missing", "No test environment selected", "Needed later for runtime testing (frontend, API, integrations, performance).",
                "Set up test environment", NavigationCatalog.TargetEnvironmentsRoute, Informational: true));
        return items;
    }

    /// <summary>Genuine same-pack (or same shared location) ambiguities per role — from the project overview, else the focused role's own state.</summary>
    public static IReadOnlyList<(WorkspaceArtifactType Role, IReadOnlyList<ProjectAmbiguity> Items)> Ambiguities(CurrentWorkspaceSnapshot workspace)
    {
        if (workspace.Overview is { } o)
            return [.. o.Roles.Where(r => r.Ambiguities.Count > 0).Select(r => (r.Role, r.Ambiguities))];
        // Without the overview only the focused role's own resolution is known: module or pack focus is navigation, not ambiguity.
        return [.. workspace.Roles.Where(r => r.Selection == ArtifactRoleSelection.SelectionRequired && !r.ModuleRequired && !r.PackRequired)
            .Select(r => (r.Role, (IReadOnlyList<ProjectAmbiguity>)[new ProjectAmbiguity(r.Role, r.Module ?? ArtifactModules.ProjectLevel, r.Pack,
                r.Pack is not null ? $"pack {r.Pack}" : r.Module is not null ? $"module {ArtifactModules.Label(r.Module)}" : "the project",
                [$"{r.ArtifactCount} {Plural(r.Role)} and none selected"])]))];
    }

    private static bool HasDocuments(CurrentWorkspaceSnapshot workspace) =>
        workspace.Overview?.Roles.Any(r => r.Found) ?? workspace.AvailableRoleCount > 0;

    private static bool HasRole(CurrentWorkspaceSnapshot workspace, WorkspaceArtifactType role) =>
        workspace.Overview?.Role(role).Found ?? workspace.Has(role);

    private static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ── Recommended next step ─────────────────────────────────────────────────

    /// <summary>
    /// The workflow's own next action (<see cref="WorkflowReadiness.NextRecommendedAction"/>) in tester terms, with why and what it achieves.
    /// Null while readiness loads. Never a fixed step: each project's state decides.
    /// </summary>
    public static DashboardNextStep? NextStep(CurrentWorkspaceSnapshot workspace, WorkflowReadiness? workflow)
    {
        if (workflow is null) return null;
        var next = workflow.NextRecommendedAction;
        var then = Then(workflow, next);
        if (next is null)
        {
            if (!workspace.WorkspaceLoaded) return null;
            return new("none", "No required step is waiting", workflow.CanRelease ? workflow.ReleaseReason
                    : "Every applicable review step is complete or does not apply to this project.",
                "Optional reviews and testing can still add evidence.", "Open Recommended Workflow", WorkflowRoute, then);
        }

        var o = workspace.Overview;
        var key = next.Key;
        if (key is WorkflowReadinessService.LoadSampleProjectKey or WorkflowReadinessService.LoadWorkspaceKey)
            return new(key, "Import your project", next.Description,
                "BirkNext finds the modules, feature packs and documents to review.", "Import project", next.Route, then);

        if (key == WorkflowReadinessService.ChooseArtifactsKey)
        {
            if (workspace.ModuleRequired)
            {
                var packs = o is { PackCount: > 0 } ? $" and {Packs(o.PackCount)}" : "";
                return new(key, "Choose a module to review first",
                    $"{workspace.ProjectDisplay} has {o?.Modules ?? workspace.ModuleCount} modules{packs}. Reviews check one module at a time.",
                    "The explorers and Document Quality Review then read that module's feature packs and its shared documents.",
                    "Choose a module", next.Route, then);
            }
            if (workspace.PackRequired)
                return new(key, "Choose a feature pack to review",
                    $"{(workspace.ModuleLabel is { } m ? $"Module {m}" : workspace.ProjectDisplay)} has {Packs(workspace.PackCount)}. Each pack is reviewed on its own documents.",
                    "The explorers show that pack's documents; Document Quality Review can then check every pack separately.",
                    "Choose a feature pack", next.Route, then);
            var role = workspace.Roles.FirstOrDefault(r => r.Selection == ArtifactRoleSelection.SelectionRequired);
            return new(key, $"Resolve which {(role is null ? "document" : Singular(role.Role))} to use", next.Description,
                "Reviews read exactly the file you choose.", "Resolve", next.Route, then);
        }

        if (key == WorkflowReadinessService.RefreshSourceKey)
            return new(key, "Analyze the source again", next.Description, "Source-based reviews then use current evidence.", "Analyze source", next.Route, then);
        if (key == WorkflowReadinessService.CompleteTargetKey)
            return new(key, "Finish setting up the test environment", next.Description,
                "Runtime reviews can then test the real application.", "Finish setup", next.Route, then);

        if (key.StartsWith(WorkflowReadinessService.OpenReviewKeyPrefix, StringComparison.Ordinal))
        {
            var reviewId = key[WorkflowReadinessService.OpenReviewKeyPrefix.Length..];
            var review = workflow.ApplicableReviews.FirstOrDefault(r => r.ReviewId == reviewId);
            if (next.Route == DocumentReviewRoute)
                return new(key, "Review documentation quality",
                    "The project's documents are available, but Document Quality Review has not checked them yet.",
                    "Checks whether requirements, plans, tasks and data models are consistent within each feature pack.",
                    "Start Document Quality Review", next.Route, then);
            var label = review?.Label ?? next.Title.Replace("Open ", "");
            return new(key, $"Run {label}", next.Description, $"Adds {(review?.Lane ?? "review").ToLowerInvariant()} evidence for this project.",
                $"Start {label}", next.Route, then);
        }

        if (next.StepType == "Explorer" && RoleOf(next) is { } stepRole)
        {
            var state = ArtifactReviewPresentation.Of(next).State;
            var noun = Singular(stepRole);
            var reason = state switch
            {
                ArtifactReviewState.NeedsChanges => $"The {noun} was marked as needing changes.",
                ArtifactReviewState.Stale => $"The {noun} changed since it was last reviewed.",
                ArtifactReviewState.Reviewed => $"The {noun} has been reviewed and is waiting for approval.",
                _ => $"The project was imported, but its {noun} has not been reviewed yet.",
            };
            return new(key, "Review imported documents", reason,
                $"You confirm the {noun} is the right one and reads correctly before deeper checks such as Document Quality Review.",
                ReviewAction(stepRole), next.Route, then);
        }

        if (next.Key.Contains("Traceability", StringComparison.OrdinalIgnoreCase))
            return new(key, "Review requirements and traceability", NotBlank(next.Description, "The documents are reviewed; their links have not been checked."),
                "Shows which requirements are covered by plans and tasks, and which are not.", "Review traceability", next.Route, then);
        if (next.Key.Contains("Implementation", StringComparison.OrdinalIgnoreCase))
            return new(key, "Check tasks against requirements", NotBlank(next.Description, "Tasks have not been compared with the requirements."),
                "Shows whether each requirement has tasks that implement and test it.", "Check task alignment", next.Route, then);

        return new(key, next.Title, NotBlank(next.Description, "This is the next open step of the recommended workflow."),
            "Moves the recommended workflow forward.", NotBlank(next.ActionLabel, "Open"), next.Route, then);
    }

    private static string NotBlank(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static WorkspaceArtifactType? RoleOf(WorkflowStepViewModel step) =>
        step.ArtifactRoles.Concat(step.RequiredArtifacts).Select(n => Enum.TryParse<WorkspaceArtifactType>(n, true, out var r) ? r : (WorkspaceArtifactType?)null)
            .FirstOrDefault(r => r is not null);

    /// <summary>The tester-facing verb for reviewing a role's documents.</summary>
    public static string ReviewAction(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "Review project rules",
        WorkspaceArtifactType.Specification => "Review requirements",
        WorkspaceArtifactType.Plan => "Review the plan",
        WorkspaceArtifactType.Tasks => "Review tasks",
        WorkspaceArtifactType.DataModel => "Review data models",
        _ => "Review documents",
    };

    /// <summary>What follows the recommendation: the next open workflow steps, then the first applicable reviews, optional ones marked.</summary>
    private static IReadOnlyList<string> Then(WorkflowReadiness workflow, WorkflowStepViewModel? next)
    {
        var then = new List<string>();
        foreach (var step in workflow.ReviewGates.Where(s => !ReferenceEquals(s, next)
                     && s.Status is not (WorkflowStepStatus.Approved or WorkflowStepStatus.NotApplicable)))
        {
            var title = step.StepType == "Explorer" && RoleOf(step) is { } role ? ReviewAction(role) : step.Title;
            if (!then.Contains(title)) then.Add(step.IsOptional ? $"{title} (optional)" : title);
            if (then.Count == 2) break;
        }
        foreach (var review in workflow.ApplicableReviews.Where(r => next is null || r.Route != next.Route).Take(3 - Math.Min(then.Count, 2)))
            then.Add(review.Route == DocumentReviewRoute ? "Run Document Quality Review" : $"{review.Label} (optional)");
        return then;
    }

    // ── Project Health ────────────────────────────────────────────────────────

    public static IReadOnlyList<DashboardHealthArea> Health(CurrentWorkspaceSnapshot? workspace, WorkflowReadiness? workflow, DashboardReviewFacts r)
    {
        if (workspace is null)
            return [.. Areas.Select(a => new DashboardHealthArea(a.Key, a.Area, a.Help, DashboardStatus.Loading, "Loading the project…", null, null, Secondary: a.Secondary))];
        if (workspace.State == CurrentWorkspaceState.Error)
            return [.. Areas.Select(a => new DashboardHealthArea(a.Key, a.Area, a.Help, DashboardStatus.Failed, "The project's documents could not be read.", null, null, Secondary: a.Secondary))];
        if (!workspace.WorkspaceLoaded)
            return [.. Areas.Select(a => new DashboardHealthArea(a.Key, a.Area, a.Help, DashboardStatus.NeedsSetup, "Import a project to see this area.",
                "Import project", ProjectInputPresentation.ProjectImportRoute, Secondary: a.Secondary))];

        return [Governance(workspace, workflow), Traceability(workspace, r), Compliance(workspace, r), Qa(workspace, r), Delivery(workspace, r),
            Performance(workflow, r), Workflow(workflow)];
    }

    private static readonly (string Key, string Area, string Help, bool Secondary)[] Areas =
    [
        ("governance", "Governance", "Documentation rules and project policies", false),
        ("traceability", "Traceability", "Requirement coverage and links", false),
        ("compliance", "Compliance", "Required standards and checks", false),
        ("qa", "QA", "Testing and quality evidence", false),
        ("delivery", "Delivery", "Delivery and release evidence", true),
        ("performance", "Performance", "Performance test evidence", true),
        ("workflow", "Workflow", "Recommended workflow progress", false),
    ];

    private static DashboardHealthArea Area(string key, DashboardStatus status, string explanation, string? action, string? href, string? result = null)
    {
        var a = Areas.First(x => x.Key == key);
        return new(key, a.Area, a.Help, status, explanation, action, href, result, a.Secondary);
    }

    private static DashboardStatus ByScore(double score) => score >= 80 ? DashboardStatus.Passed : DashboardStatus.AttentionNeeded;

    private static DashboardHealthArea Governance(CurrentWorkspaceSnapshot workspace, WorkflowReadiness? workflow)
    {
        if (workflow is null) return Area("governance", DashboardStatus.Loading, "Loading review decisions…", null, null);
        var gates = workflow.DocumentReviewGates.Select(ArtifactReviewPresentation.Of).ToList();
        if (gates.Count == 0)
            return HasDocuments(workspace)
                ? Area("governance", DashboardStatus.NotAssessed, "No document review step applies yet.", "Open Recommended Workflow", WorkflowRoute)
                : Area("governance", DashboardStatus.NeedsSetup, "No project documents were found.", "Import project", ProjectInputPresentation.ProjectImportRoute);
        var approved = gates.Count(g => g.State == ArtifactReviewState.Approved);
        var changes = gates.Count(g => g.State == ArtifactReviewState.NeedsChanges);
        var stale = gates.Count(g => g.State == ArtifactReviewState.Stale);
        var decided = gates.Any(g => g.IsDecided || g.State == ArtifactReviewState.Stale);
        var result = decided ? $"{approved} of {gates.Count} document reviews approved" : null;
        if (changes > 0) return Area("governance", DashboardStatus.AttentionNeeded, $"{changes} document review{S(changes)} need{(changes == 1 ? "s" : "")} changes.", "Open Recommended Workflow", WorkflowRoute, result);
        if (stale > 0) return Area("governance", DashboardStatus.AttentionNeeded, $"{stale} review{S(stale)} out of date — the document changed since.", "Review again", WorkflowRoute, result);
        if (approved == gates.Count) return Area("governance", DashboardStatus.Passed, $"All {gates.Count} document reviews approved on the current documents.", "Open Recommended Workflow", WorkflowRoute, result);
        if (decided) return Area("governance", DashboardStatus.InProgress, $"{approved} of {gates.Count} document reviews approved so far.", "Continue reviewing", WorkflowRoute, result);
        return Area("governance", DashboardStatus.ReadyToReview, $"{gates.Count} document{S(gates.Count)} can be reviewed. None reviewed yet.", "Review documents", WorkflowRoute);
    }

    private static DashboardHealthArea Traceability(CurrentWorkspaceSnapshot workspace, DashboardReviewFacts r)
    {
        if (r.TraceabilityRun)
            return r.TraceabilityCoverage is { } coverage
                ? Area("traceability", ByScore(coverage), coverage >= 80 ? "Most requirements are linked to plans and tasks." : "Some requirements are not linked to plans or tasks.",
                    "Open Traceability", TraceabilityRoute, $"{coverage:0}% coverage across assessed links")
                : Area("traceability", DashboardStatus.NotAssessed, "Traceability ran, but no requirement links could be assessed.", "Open Traceability", TraceabilityRoute);
        if (!HasRole(workspace, WorkspaceArtifactType.Specification))
            return Area("traceability", DashboardStatus.NeedsSetup, "No specification found — traceability needs requirements.", "Explore specifications", "specification-explorer");
        var o = workspace.Overview;
        var text = o is { PackCount: > 0 }
            ? $"{o.Role(WorkspaceArtifactType.Specification).PacksWithRole} of {Packs(o.PackCount)} contain a specification. Not checked yet."
            : "Specifications were found. Their links have not been checked yet.";
        return Area("traceability", DashboardStatus.ReadyToReview, text, "Review requirements", TraceabilityRoute);
    }

    private static DashboardHealthArea Compliance(CurrentWorkspaceSnapshot workspace, DashboardReviewFacts r)
    {
        if (r.ComplianceRun)
            return Area("compliance", ByScore(r.ComplianceScore), r.ComplianceScore >= 80 ? "Documents follow most project rules." : "Documents break some project rules.",
                "Open results", DocumentReviewRoute, $"{r.ComplianceScore:0}% compliant with the constitution");
        if (!HasRole(workspace, WorkspaceArtifactType.Constitution))
            return Area("compliance", DashboardStatus.NotApplicable, "No constitution found, so there are no project rules to check against.", null, null);
        return Area("compliance", DashboardStatus.ReadyToReview, "A constitution was found. Document Quality Review checks the documents against it.",
            "Run Document Quality Review", DocumentReviewRoute);
    }

    private static DashboardHealthArea Qa(CurrentWorkspaceSnapshot workspace, DashboardReviewFacts r)
    {
        switch (r.DocumentReview)
        {
            case AnalysisSessionStatus.Running:
                return Area("qa", DashboardStatus.InProgress, "Document Quality Review is running.", "Open Document Quality Review", DocumentReviewRoute);
            case AnalysisSessionStatus.Failed:
                return Area("qa", DashboardStatus.Failed, "The latest Document Quality Review failed.", "Open Document Quality Review", DocumentReviewRoute);
        }
        if (r.QaAssessed)
            return Area("qa", ByScore(r.QaScore),
                r.DocumentReviewFindings > 0 ? $"Latest review: {r.DocumentReviewFindings} finding{S(r.DocumentReviewFindings)}{(r.DocumentReviewSevere > 0 ? $", {r.DocumentReviewSevere} critical or high" : "")}." : "Latest review found no issues.",
                "Open results", DocumentReviewRoute, $"{r.QaScore:0}/100 in the latest review");
        if (r.DocumentReview == AnalysisSessionStatus.Completed)
            return Area("qa", DashboardStatus.NotAssessed, "The latest review had nothing it could assess (no specification in scope).", "Open Document Quality Review", DocumentReviewRoute);
        if (!HasRole(workspace, WorkspaceArtifactType.Specification))
            return Area("qa", DashboardStatus.NeedsSetup, "No specification found to review.", "Explore specifications", "specification-explorer");
        return Area("qa", DashboardStatus.ReadyToReview, "Document Quality Review has not been run.", "Run Document Quality Review", DocumentReviewRoute);
    }

    private static DashboardHealthArea Delivery(CurrentWorkspaceSnapshot workspace, DashboardReviewFacts r)
    {
        if (r.DeliveryRun && r.ReleaseDecision is { } release)
            return Area("delivery", release == ReadinessState.Ready ? DashboardStatus.Passed : DashboardStatus.AttentionNeeded,
                r.ReleaseSignificant
                    ? $"Development: {Readiness(r.DevelopmentDecision)} · Testing: {Readiness(r.TestingDecision)} · Release: {Readiness(release)}."
                    : $"Development: {Readiness(r.DevelopmentDecision)} · Testing: {Readiness(r.TestingDecision)} · Documentation gate: {DocumentationGate.StateLabel(release, false)} (advisory).",
                "Open results", DocumentReviewRoute);
        if (!HasRole(workspace, WorkspaceArtifactType.Plan) && !HasRole(workspace, WorkspaceArtifactType.Tasks))
            return Area("delivery", DashboardStatus.NotApplicable, "No plan or tasks found, so there is no delivery evidence to assess.", null, null);
        return Area("delivery", DashboardStatus.NotAssessed, "Delivery readiness has not been assessed yet.", "Run Document Quality Review", DocumentReviewRoute);
    }

    private static DashboardHealthArea Performance(WorkflowReadiness? workflow, DashboardReviewFacts r)
    {
        if (r.PerformanceScore is { } score)
            return Area("performance", r.PerformanceState == PerformanceReadinessState.Ready ? DashboardStatus.Passed : DashboardStatus.AttentionNeeded,
                $"Latest performance review: {Readiness(r.PerformanceState)}.", "Open Performance Test Review", PerformanceRoute, $"{score:0}/100");
        switch (r.Performance)
        {
            case RuntimeReviewStatus.Running:
                return Area("performance", DashboardStatus.InProgress, "A performance review is running.", "Open Performance Test Review", PerformanceRoute);
            case RuntimeReviewStatus.Failed:
                return Area("performance", DashboardStatus.Failed, "The latest performance review failed.", "Open Performance Test Review", PerformanceRoute);
            case RuntimeReviewStatus.Completed:
                return Area("performance", DashboardStatus.NotAssessed, "The performance review ran but could not score the application.", "Open Performance Test Review", PerformanceRoute);
        }
        return workflow?.Inputs.Target.Status == ProjectInputStatus.Ready
            ? Area("performance", DashboardStatus.NotAssessed, "No performance review has been run.", "Open Performance Test Review", PerformanceRoute)
            : Area("performance", DashboardStatus.NeedsSetup, "Needs a test environment before performance can be tested.", "Set up test environment", NavigationCatalog.TargetEnvironmentsRoute);
    }

    private static DashboardHealthArea Workflow(WorkflowReadiness? workflow)
    {
        if (workflow is null) return Area("workflow", DashboardStatus.Loading, "Loading the recommended workflow…", null, null);
        if (workflow.ReleaseReadinessPercent is not { } percent)
            return Area("workflow", DashboardStatus.NotAssessed, "Recommended workflow not started.", "Start recommended workflow", WorkflowRoute);
        var result = $"{workflow.ApprovedReviewCount} of {workflow.RequiredReviewCount} required reviews approved ({percent}%)";
        return percent == 100
            ? Area("workflow", DashboardStatus.Passed, "All required reviews are approved on the current documents.", "Open Recommended Workflow", WorkflowRoute, result)
            : Area("workflow", DashboardStatus.InProgress, "The recommended workflow has started.", "Continue recommended workflow", WorkflowRoute, result);
    }

    private static string Readiness(ReadinessState? state) => state switch
    {
        ReadinessState.Ready => "ready",
        ReadinessState.MostlyReady => "mostly ready",
        ReadinessState.NotReady => "not ready",
        ReadinessState.Blocked => "blocked",
        _ => "not assessed",
    };

    private static string Readiness(PerformanceReadinessState? state) => state switch
    {
        PerformanceReadinessState.Ready => "ready",
        PerformanceReadinessState.MostlyReady => "mostly ready",
        PerformanceReadinessState.NeedsImprovement => "needs improvement",
        PerformanceReadinessState.HighRisk => "high risk",
        _ => "not assessed",
    };

    // ── Review readiness (to START reviewing/testing) ─────────────────────────

    /// <summary>
    /// Whether reviews and tests can start: inputs per document type (pack-aware), the test environment, runtime evidence and Document Quality
    /// Review. Never a quality result and never release readiness — "Ready" here means a review can begin.
    /// </summary>
    public static IReadOnlyList<DashboardReadinessRow> ReviewReadiness(CurrentWorkspaceSnapshot workspace, WorkflowReadiness? workflow, DashboardReviewFacts r)
    {
        var rows = new List<DashboardReadinessRow>();
        if (!workspace.WorkspaceLoaded) return rows;
        var o = workspace.Overview;
        foreach (var role in new[] { WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan, WorkspaceArtifactType.Tasks, WorkspaceArtifactType.DataModel, WorkspaceArtifactType.Constitution })
        {
            var label = Cap(Plural(role));
            var found = HasRole(workspace, role);
            var optional = role is WorkspaceArtifactType.DataModel or WorkspaceArtifactType.Constitution;
            if (!found)
            {
                rows.Add(new(role.ToString(), label, optional ? DashboardStatus.NotApplicable : DashboardStatus.NeedsSetup, "Not found",
                    optional ? "Optional — reviews that need it do not apply." : $"No {Singular(role)} was found in the project."));
                continue;
            }
            if (o is { PackCount: > 0 })
            {
                var c = o.Role(role);
                var all = c.PacksWithRole == o.PackCount;
                var shared = role == WorkspaceArtifactType.Constitution && c.SharedDocuments > 0 && all;
                rows.Add(new(role.ToString(), label, all ? DashboardStatus.Available : DashboardStatus.Partial, all ? "Ready" : "Partial",
                    shared ? $"Shared · applies to all {Packs(o.PackCount)}" : $"In {c.PacksWithRole} of {Packs(o.PackCount)}{(optional && !all ? " (optional)" : "")}"));
            }
            else
            {
                rows.Add(new(role.ToString(), label, DashboardStatus.Available, "Ready", $"{Count(o?.Role(role).Documents ?? workspace.Role(role).ArtifactCount, role)} found"));
            }
        }

        var (envStatus, envText) = TestEnvironment(workflow);
        rows.Add(new("target", "Test environment", envStatus, envStatus switch
        {
            DashboardStatus.Available => "Configured",
            DashboardStatus.Loading => "Loading…",
            DashboardStatus.Unavailable => "Unavailable",
            _ => workflow?.Inputs.Target.Status == ProjectInputStatus.Partial ? "Incomplete" : "Not selected",
        }, envStatus == DashboardStatus.Available ? $"{envText["Test environment: ".Length..]} — configured, not yet tested" : NotBlank(workflow?.Inputs.Target.Detail, "")));
        rows.Add(new("runtime", "Runtime evidence", r.RuntimeEvidence ? DashboardStatus.Available : DashboardStatus.NotAssessed,
            r.RuntimeEvidence ? "Captured" : "Not assessed",
            r.RuntimeEvidence ? "A runtime review has results in this session." : "No runtime review has run against the test environment yet."));
        rows.Add(DocumentReviewRow(r));
        return rows;
    }

    private static DashboardReadinessRow DocumentReviewRow(DashboardReviewFacts r) => r.DocumentReview switch
    {
        AnalysisSessionStatus.Completed => new("dqr", "Document Quality Review", r.DocumentReviewSevere > 0 ? DashboardStatus.AttentionNeeded : DashboardStatus.Available,
            "Latest review", $"{(r.DocumentReviewAt is { } at ? $"Run {at.ToLocalTime():d MMM HH:mm}" : "Run this session")}{(r.DocumentReviewScope is { } s ? $" · {s}" : "")} · {r.DocumentReviewFindings} finding{S(r.DocumentReviewFindings)}"),
        AnalysisSessionStatus.Running => new("dqr", "Document Quality Review", DashboardStatus.InProgress, "Running", "The review is running."),
        AnalysisSessionStatus.Failed => new("dqr", "Document Quality Review", DashboardStatus.Failed, "Failed", "The latest review failed."),
        _ => new("dqr", "Document Quality Review", DashboardStatus.NotAssessed, "Not run", "No document review has been run yet."),
    };

    // ── Risks ─────────────────────────────────────────────────────────────────

    public static DashboardRiskState Risks(DashboardReviewFacts r) => !r.RisksAssessed
        ? new(false, 0, "Risks have not been assessed yet.")
        : r.RiskCount == 0
            ? new(true, 0, "No significant risks found in the latest review.")
            : new(true, r.RiskCount, $"{r.RiskCount} risk{S(r.RiskCount)} found in the latest review.");

    // ── Project Artifacts ─────────────────────────────────────────────────────

    public static string CardTitle(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "Constitution",
        WorkspaceArtifactType.Specification => "Specifications",
        WorkspaceArtifactType.Plan => "Plans",
        WorkspaceArtifactType.Tasks => "Tasks",
        WorkspaceArtifactType.DataModel => "Data models",
        _ => role.ToString(),
    };

    public const int MaxFiles = 12;

    /// <summary>
    /// One document type across the whole project: how many, across how many modules and packs, one status (ambiguity → review decision →
    /// ready to review), secondary metadata, and the files only for Details. One selected file never stands for the project.
    /// </summary>
    public static DashboardArtifactCard Card(WorkspaceArtifactType role, CurrentWorkspaceSnapshot? workspace, ArtifactReviewStatus? review, bool analyzed,
        IReadOnlyList<string>? reviewFacts = null)
    {
        var title = CardTitle(role);
        var href = ProjectInputPresentation.ExplorerRoute(role);
        var explore = role == WorkspaceArtifactType.Constitution ? "Open constitution" : $"Explore {Plural(role)}";
        if (workspace is null)
            return new(role, title, "Loading…", DashboardStatus.Loading, Label(DashboardStatus.Loading), "", [], explore, href, [], 0, [], []);
        var status = workspace.Role(role);
        if (status.Availability == ArtifactRoleAvailability.Unknown && workspace.Overview is null)
            return new(role, title, "Availability unknown", DashboardStatus.Unavailable, "Availability unknown", status.Error ?? "", [], explore, href, [], 0, [], []);

        var o = workspace.Overview;
        var coverage = o?.Role(role);
        var found = coverage?.Found ?? status.IsAvailable;
        if (!found)
            return new(role, title, workspace.WorkspaceLoaded ? $"No {Singular(role)} found in this project" : "No project loaded",
                DashboardStatus.NotApplicable, "Not found",
                role is WorkspaceArtifactType.DataModel or WorkspaceArtifactType.Constitution ? "Optional · reviews that need it do not apply" : "Reviews that need it do not apply",
                [], explore, href, [], 0, [], []);

        // Summary: count first, then modules and packs.
        var documents = coverage?.Documents ?? status.ArtifactCount;
        var summary = Count(documents, role);
        if (o is { HasModules: true } && coverage is not null) summary += $" across {coverage.ModulesWithRole} module{S(coverage.ModulesWithRole)}";
        else if (o is null && status.HasModules) summary += $" across {status.Modules!.Count(m => m.Candidates > 0)} modules";

        var attention = new List<string>();
        var attentionItems = new List<string>();
        var ambiguities = Ambiguities(workspace).Where(a => a.Role == role).SelectMany(a => a.Items).ToList();
        if (ambiguities.Count == 1) attention.Add($"{Cap(ambiguities[0].Where)} has {ambiguities[0].Candidates.Count} possible files");
        else if (ambiguities.Count > 1) attention.Add($"{ambiguities.Count} places have more than one possible file");
        attentionItems.AddRange(ambiguities.Select(a => $"{Cap(a.Where)}: {string.Join(", ", a.Candidates)}"));

        var packLine = "";
        if (o is { PackCount: > 0 } && coverage is not null)
        {
            var all = coverage.PacksWithRole == o.PackCount;
            packLine = role == WorkspaceArtifactType.Constitution && coverage.SharedDocuments > 0 && all
                ? $"Shared · applies to all {Packs(o.PackCount)}"
                : $"In {coverage.PacksWithRole} of {Packs(o.PackCount)}";
            if (ProjectPackOverview.CoreRoles.Contains(role) && coverage.PacksMissing.Count > 0)
            {
                attention.Add($"{coverage.PacksMissing.Count} pack{S(coverage.PacksMissing.Count)} missing a {Singular(role)}");
                attentionItems.AddRange(coverage.PacksMissing.Select(p => $"Missing in {ProjectPackOverview.PackName(p)}"));
            }
        }

        DashboardStatus state;
        string stateLabel;
        if (ambiguities.Count > 0) (state, stateLabel) = (DashboardStatus.AttentionNeeded, "Needs attention");
        else if (review is { State: ArtifactReviewState.Approved }) (state, stateLabel) = (DashboardStatus.Passed, "Approved");
        else if (review is { State: ArtifactReviewState.NeedsChanges }) (state, stateLabel) = (DashboardStatus.AttentionNeeded, "Needs changes");
        else if (review is { State: ArtifactReviewState.Stale }) (state, stateLabel) = (DashboardStatus.AttentionNeeded, "Review out of date");
        else if (review is { State: ArtifactReviewState.Reviewed or ArtifactReviewState.InReview }) (state, stateLabel) = (DashboardStatus.InProgress, review.Label);
        else (state, stateLabel) = (DashboardStatus.ReadyToReview, "Ready to review");

        var meta = new List<string> { "Available" };
        if (packLine.Length > 0) meta.Add(packLine);
        meta.Add(review is { IsDecided: true } or { State: ArtifactReviewState.Stale } ? $"Review: {review.Label.ToLowerInvariant()}" : "Review not run");
        if (analyzed) meta.Add("Checked by Document Quality Review");
        if (reviewFacts is not null) meta.AddRange(reviewFacts);

        // Details: files and where the current selection comes from. Paths live here only.
        var paths = coverage?.Paths ?? (status.SelectedArtifact is { } sel ? [sel.SourcePath ?? sel.FileName] : []);
        var provenance = new List<string>();
        if (status.PackApplicability is { } applicability) provenance.Add(applicability);
        else if (status.SelectedArtifact is { } selected && status.IsAvailable)
            provenance.Add($"Currently viewed: {selected.SourcePath ?? selected.FileName}{(status.Resolution is { } res ? $" · {res}" : "")}");
        if (workspace.ProjectName is { } project) provenance.Add($"Project: {project}");

        return new(role, title, summary, state, stateLabel, string.Join(" · ", meta), attention,
            ambiguities.Count > 0 ? "Resolve" : explore, href, [.. paths.Take(MaxFiles)], Math.Max(0, paths.Count - MaxFiles), provenance, attentionItems);
    }
}
