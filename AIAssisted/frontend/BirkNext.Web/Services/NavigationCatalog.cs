using BirkNext.Applicability;
using BirkNext.Technology;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace BirkNext.Web.Services;

// ── Sidebar navigation model ─────────────────────────────────────────────────────────────────────────────────────────
//
// The sidebar answers "where can I go?". Each row is [icon] [label] [status]; the status is a short applicability summary
// from the shared ApplicabilityEvaluator (via ProjectApplicabilityState) — never a review result and never a copy of the
// Technology Coverage page. Feature Visibility (System Settings) decides which rows exist; collapsing a section is UI
// state only and never hides a feature.

/// <summary>One navigation row. <paramref name="ReviewId"/> links the row to its <see cref="ReviewCatalog"/> applicability.</summary>
public sealed record NavItem(
    string Route,
    string Label,
    string Icon,
    Func<FeatureVisibilityService, bool> Visible,
    NavLinkMatch Match = NavLinkMatch.All,
    string? ReviewId = null,
    string? Hint = null,
    string? ProviderWhenApplicable = null,
    string? FeatureId = null,
    bool OptionalByDefault = false,
    bool WrapLabel = false,
    string[]? Aliases = null)
{
    /// <summary>Earlier names and common words the sidebar search also finds (e.g. "Implementation Review" → Task Alignment Review).</summary>
    public IReadOnlyList<string> SearchTerms => [Label, .. Aliases ?? [], .. Hint is null ? Array.Empty<string>() : [Hint]];

    /// <summary>Tooltip for the row: the full label (it may be truncated) plus a short purpose where the label alone is ambiguous.</summary>
    public string Title => Hint is null ? Label : $"{Label}: {Hint}";

    /// <summary>A deep link (a route with a query, e.g. a System Settings section): current only on that page with that exact query.</summary>
    public bool IsDeepLink => Route.Contains('?');

    /// <summary>Current for a base-relative location including its query: a deep link needs its exact query; a page row is current
    /// on its path unless a deep link in the catalog claims the location (then that shortcut is the one current row).</summary>
    public bool IsCurrentLocation(string relativeLocation)
    {
        var (path, query) = NavigationCatalog.Split(relativeLocation);
        if (IsDeepLink)
        {
            var (routePath, routeQuery) = NavigationCatalog.Split(Route);
            return path.Equals(routePath, StringComparison.OrdinalIgnoreCase) && NavigationCatalog.SameQuery(query, routeQuery);
        }
        return IsCurrent(path) && !NavigationCatalog.Sections.SelectMany(s => s.Items).Any(i => i.IsDeepLink && i.IsCurrentLocation(relativeLocation));
    }

    public bool IsCurrent(string relativePath) =>
        IsDeepLink ? false
        : Match == NavLinkMatch.All
            ? string.Equals(relativePath, Route, StringComparison.OrdinalIgnoreCase)
            : relativePath.Equals(Route, StringComparison.OrdinalIgnoreCase) || relativePath.StartsWith(Route + "/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A sidebar section. It is shown when at least one of its rows is visible.</summary>
public sealed record NavSection(string Id, string Label, IReadOnlyList<NavItem> Items);

/// <summary>Sibling pages shown as tabs of one workspace (see <see cref="NavigationCatalog.SharedWorkspaces"/>).</summary>
public sealed record SharedWorkspace(string Id, string Label, IReadOnlyList<string> Routes);

public static class NavigationCatalog
{
    private static bool Always(FeatureVisibilityService _) => true;

    /// <summary>Route of the Target Environments section of System Settings. The sidebar row is a deep link, not a second page.</summary>
    public const string TargetEnvironmentsRoute = "admin/system-settings?section=target-environments";

    /// <summary>Canonical route of Generated Documentation Health (project analysis). The former System Settings section redirects here.</summary>
    public const string GeneratedDocumentationHealthRoute = "generated-documentation-health";
    /// <summary>Structured, read-only browsing of an import's generated (implementation-derived) documentation.</summary>
    public const string GeneratedDocumentationExplorerRoute = "generated-documentation-explorer";

    /// <summary>
    /// Sections (groups) in the order of the delivery conversation: the workspace (current project and its inputs), delivery management,
    /// specifications and planning, traceability and impact, source and architecture, quality and testing, project extensions, help and
    /// settings. A section shows when at least one of its rows is visible; Feature Visibility decides rows. Section IDs are stable
    /// identities, never display text (they predate the current labels). Routes never change with the grouping.
    /// </summary>
    public static IReadOnlyList<NavSection> Sections { get; } =
    [
        // WORKSPACE: where the current project comes from. Import Project is the primary path (one ZIP → project artifacts + source snapshot);
        // Demo Projects are optional examples; Target Environments is runtime configuration of the deployed system.
        new("getting-started", "Workspace",
        [
            new("dashboard", "Dashboard", "nav-icon-dashboard", f => f.Dashboard, NavLinkMatch.Prefix),
            new("getting-started", "Recommended Workflow", "nav-icon-workflow", f => f.RecommendedWorkflow, Aliases: ["Getting Started"]),
            new("project-import", "Import Project", "nav-icon-import", f => f.SampleProjects || f.SourceAnalysis,
                Hint: "primary input: one project ZIP becomes project artifacts and a Source Analysis snapshot", Aliases: ["Project Inputs", "Upload ZIP"]),
            new("demo-projects", "Demo Projects", "nav-icon-sample-projects", f => f.SampleProjects,
                Hint: "optional built-in example projects", Aliases: ["Sample Projects"]),
            new(TargetEnvironmentsRoute, "Target Environments", "nav-icon-target", f => f.AdminSystemSettings,
                Hint: "runtime configuration of the deployed system, used by runtime reviews (opens System Settings)", FeatureId: "target-environments"),
        ]),
        // DELIVERY MANAGEMENT: for Project Leaders and Scrum Masters — its own projects, snapshots and frozen reports (never current BirkNext state).
        new("delivery-reporting", "Delivery Management",
        [
            new(DeliveryOverviewRoute, "Delivery Overview", "nav-icon-dashboard", Always,
                Hint: "virtual SDD backlog: features, user stories and acceptance criteria with their delivery status from imported snapshots",
                Aliases: ["SDD backlog", "Backlog", "Delivery status"]),
            new("delivery-reporting", "Delivery Reports", "nav-icon-workflow", Always,
                Hint: "weekly story/feature progress from imported repository snapshots: what changed, attention items, next-week focus",
                Aliases: ["Delivery Reporting", "Weekly report", "Azure DevOps import"]),
        ]),
        // SPECIFICATIONS & PLANNING: what the documents say, how good they are, how tasks align and how revisions compare.
        new("document-review", "Specifications & Planning",
        [
            new("specification-explorer", "Specification Explorer", "nav-icon-specification-explorer", f => f.SpecificationExplorer, Aliases: ["Spec Explorer", "Document Review"]),
            new("constitution-explorer", "Constitution Explorer", "nav-icon-constitution", f => f.ConstitutionExplorer),
            new("data-model-explorer", "Data Model Explorer", "nav-icon-data-model-explorer", f => f.DataModelExplorer, Aliases: ["Data Models & Schemas"]),
            new("plan-explorer", "Plan Explorer", "nav-icon-plan-explorer", f => f.PlanExplorer, Aliases: ["Plan & Tasks"]),
            new("task-explorer", "Task Explorer", "nav-icon-task-explorer", f => f.TaskExplorer, Aliases: ["Plan & Tasks"]),
            new("quality/document", "Document Quality Review", "nav-icon-qa-auditor", f => f.QualityReview, ReviewId: "quality-review", Aliases: ["QA Auditor", "Quality Review"]),
            // Stable route and visibility key (ImplementationReview); also reachable at /task-alignment-review.
            new("task-alignment", "Task Alignment Review", "nav-icon-task-alignment", f => f.ImplementationReview,
                Hint: "tasks checked against the Specification and Success Criteria; no source code is read", Aliases: ["Implementation Review", "Plan & Tasks"]),
            // Document ↔ document only (authored and generated revisions); it never reads source, so it stays with the documents.
            new("documentation-drift", "Documentation Drift", "nav-icon-spec-drift", f => f.SpecDrift,
                Hint: "compares documents and revisions: authored ↔ authored, generated ↔ generated, authored ↔ generated", Aliases: ["Spec Drift", "Specification Comparison"]),
        ]),
        // TRACEABILITY & IMPACT: how lifecycle artifacts and tracked delivery changes are linked, and what a change affects.
        new("traceability", "Traceability & Impact",
        [
            new("artifact-traceability", "Requirements Traceability", "nav-icon-artifact-traceability", f => f.ArtifactTraceability, Aliases: ["Artifact Traceability"]),
            // Stable route and visibility key (ImplementationTraceability); also reachable at /change-traceability.
            new("implementation-traceability", "Change Traceability", "nav-icon-task-alignment", f => f.ImplementationTraceability,
                Hint: "Azure DevOps work items → pull requests → commits → changed files (optional)", Aliases: ["Implementation Traceability"]),
            // Cross-cutting impact of requirement and source changes (off by default).
            new("impact-analysis", "Impact Analysis", "nav-icon-impact-analysis", f => f.ImpactAnalysis, Aliases: ["Unified Impact Analysis"]),
            new("traceability", "Traceability & Coverage", "nav-icon-traceability", f => f.LegacyTraceabilityNavigationEnabled && f.TraceabilityCoverage, OptionalByDefault: true),
            new("traceability/suggestions", "Traceability Suggestions", "nav-icon-suggestions", f => f.LegacyTraceabilityNavigationEnabled && f.TraceabilitySuggestions, OptionalByDefault: true),
            new("code-traceability", "Code Traceability", "nav-icon-code-trace", f => f.LegacyTraceabilityNavigationEnabled && f.CodeTraceability, OptionalByDefault: true),
        ]),
        // SOURCE & ARCHITECTURE: consumers of the source snapshot (SBOM import also feeds Dependency Review; Environment Analysis adds a cloud provider).
        new("source-review", "Source & Architecture",
        [
            new("source-analysis", "Source Analysis", "nav-icon-source-analysis", f => f.SourceAnalysis,
                Hint: "inspect the current source snapshot (from Import Project or a source-only upload)"),
            new("schema-studio", "Schema Studio", "nav-icon-source-analysis", Always,
                Hint: "inspect, compare and validate XSD schemas from uploaded files or the project source snapshot", Aliases: ["XSD", "Data Models & Schemas"]),
            new(GeneratedDocumentationExplorerRoute, "Generated Documentation Explorer", "nav-icon-source-analysis", f => f.GeneratedDocumentationExplorer,
                Hint: "browse generated (implementation-derived) documentation by module, structured by document type", WrapLabel: true, Aliases: ["Generated Documentation", "Autodoc"]),
            // Runs over the import's Source Analysis snapshot (generated-doc evidence detected there) and checks it against source and
            // authored intent: discovery (Explorer) first, then its health.
            new(GeneratedDocumentationHealthRoute, "Generated Documentation Health", "nav-icon-source-analysis", f => f.GeneratedDocumentationHealth,
                Hint: "generated documentation in the current project: availability, provenance, freshness and consistency", WrapLabel: true, Aliases: ["Generated Documentation"]),
            new("technology-coverage", "Technology Coverage", "nav-icon-source-analysis", Always,
                Hint: "what the source contains and what BirkNext can assess"),
            new("dependency-review", "Dependency Review", "nav-icon-constitution-compliance", Always, ReviewId: "dependency-review"),
            new("pipeline-review", "Pipeline Review", "nav-icon-task-alignment", Always, ReviewId: "pipeline-review"),
            // The page is the Azure provider of environment analysis; the menu stays generic and names the provider in the status.
            new("azure-environment", "Environment Analysis", "nav-icon-source-analysis", f => f.AzureEnvironmentAnalysis,
                ReviewId: "azure-environment", Hint: "read-only cloud environment inventory (provider: Azure)", ProviderWhenApplicable: "Azure", FeatureId: "environment-analysis",
                Aliases: ["Azure Environment Analysis"]),
            new("ai-change-auditor", "AI Change Review", "nav-icon-ai-auditor", f => f.AiChangeReview, OptionalByDefault: true, Aliases: ["AI Review", "AI Change Auditor"]),
        ]),
        // Planning → preparation → target-bound runs. Source-only assessments (test inventory, code risk) say where to focus; Test Data
        // is the prerequisite of the runs that need test identities/assets (Performance, Critical E2E; readiness shown in API/Integration);
        // Topology precedes the IQR it reads bindings from; Critical E2E is the end-to-end release check.
        new("quality-testing", "Quality & Testing",
        [
            new("test-coverage-review", "Test Coverage & Overlap Review", "nav-icon-task-alignment", Always),
            new("code-risk-review", "Code Risk Review", "nav-icon-constitution-compliance", Always, ReviewId: "code-risk-review"),
            new("test-data", "Test Data", "nav-icon-task-alignment", Always, Hint: "approved typed test data: values, payloads, files, entity references, data sets, expected results"),
            new("frontend-quality-review", "Frontend Quality Review", "nav-icon-constitution-compliance", f => f.FrontendQualityReview, ReviewId: "frontend-quality-review", Aliases: ["FQR"]),
            new("api-quality-review", "API Quality Review", "nav-icon-constitution-compliance", f => f.ApiQualityReview, ReviewId: "api-quality-review", Aliases: ["AQR", "API & Integration Quality"]),
            new("integration-topology", "Integration Topology", "nav-icon-source-analysis", Always, Hint: "components, interfaces (REST, GraphQL, messaging, file/storage) and connections with their sources",
                Aliases: ["API & Integration Quality"]),
            new("integration-quality-review", "Integration Quality Review", "nav-icon-constitution-compliance", f => f.IntegrationQualityReview, ReviewId: "integration-quality-review",
                Aliases: ["IQR", "API & Integration Quality"]),
            new("performance-test-review", "Performance Test Review", "nav-icon-constitution-compliance", Always, ReviewId: "performance-test-review", Aliases: ["PTR", "k6"]),
            new("critical-e2e-regression", "Critical E2E Regression", "nav-icon-constitution-compliance", f => f.CriticalE2ERegression, ReviewId: "critical-e2e-regression", Aliases: ["E2E", "Regression"]),
        ]),
        // PROJECT EXTENSIONS: project-specific domain extensions, shown with their applicability (N/A unless the extension is enabled).
        new("extensions", "Project Extensions",
        [
            new("security-classification-review", "M01 – Gradert tilgang (Kode 6/7)", "nav-icon-constitution-compliance", Always, ReviewId: "security-classification-review",
                Hint: "M01 graded access and Kode 6 / Kode 7 classification in M2LB (formerly Security Classification); its shared evidence is also used by Skolenærvær. Not a generic security review", WrapLabel: true,
                Aliases: ["Security Classification", "Kode 6", "Kode 7"]),
            new("extensions/skolenaervaer", "Skolenærvær Testing", "nav-icon-constitution-compliance", Always,
                Hint: "domain view over the shared CDC and integration journey engines", Aliases: ["Skolenaervaer"]),
        ]),
        // HELP & SETTINGS.
        new("admin", "Help & Settings",
        [
            new("user-guide", "User Guide", "nav-icon-user-guide", f => f.UserGuide, Aliases: ["Help"]),
            new("admin/system-settings", "System Settings", "nav-icon-settings", f => f.AdminSystemSettings, Aliases: ["Admin", "Feature Visibility", "Authentication"]),
        ]),
    ];

    public const string DeliveryOverviewRoute = "delivery-overview";

    /// <summary>
    /// Shared workspaces: sibling pages shown as tabs above each other (links between the existing routes — no route changes). A page
    /// can be in one workspace. Rows stay in the sidebar too; the tabs only make the siblings one click apart.
    /// </summary>
    public static IReadOnlyList<SharedWorkspace> SharedWorkspaces { get; } =
    [
        new("delivery", "Delivery Management", [DeliveryOverviewRoute, "delivery-reporting"]),
        new("plan-tasks", "Plan & Tasks", ["plan-explorer", "task-explorer", "task-alignment"]),
        new("data-models-schemas", "Data Models & Schemas", ["data-model-explorer", "schema-studio"]),
        new("api-integration-quality", "API & Integration Quality", ["api-quality-review", "integration-topology", "integration-quality-review"]),
        new("generated-documentation", "Generated Documentation", [GeneratedDocumentationExplorerRoute, GeneratedDocumentationHealthRoute]),
        new("traceability-impact", "Traceability & Impact", ["artifact-traceability", "implementation-traceability", "impact-analysis"]),
    ];

    /// <summary>The visible rows of a shared workspace, in its order (rows hidden by Feature Visibility are left out).</summary>
    public static IReadOnlyList<NavItem> WorkspaceItems(SharedWorkspace workspace, FeatureVisibilityService features) =>
        [.. workspace.Routes.Select(r => Sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Route == r)).OfType<NavItem>().Where(i => i.Visible(features))];

    /// <summary>The shared workspace of a page (base-relative path without query), if any.</summary>
    public static SharedWorkspace? WorkspaceFor(string relativePath) =>
        SharedWorkspaces.FirstOrDefault(w => w.Routes.Any(r => Sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Route == r) is { } item && item.IsCurrent(relativePath)));

    /// <summary>
    /// Sidebar search over every row the person may open (Feature Visibility), whatever the Workspace View: current and earlier names, hints
    /// and the group name. Ranked: label starts with the text, label contains it, then alias/hint/group matches.
    /// </summary>
    public static IReadOnlyList<(NavSection Section, NavItem Item)> Search(string? text, FeatureVisibilityService features)
    {
        var q = (text ?? "").Trim();
        if (q.Length == 0) return [];
        return [.. Sections.SelectMany(s => s.Items.Where(i => i.Visible(features)).Select(i => (Section: s, Item: i)))
            .Select(x => (x.Section, x.Item, Rank:
                x.Item.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0
                : x.Item.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1
                : (x.Item.Aliases ?? []).Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)) ? 2
                : x.Section.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ? 3
                : x.Item.Hint?.Contains(q, StringComparison.OrdinalIgnoreCase) == true ? 4 : -1))
            .Where(x => x.Rank >= 0).OrderBy(x => x.Rank).Select(x => (x.Section, x.Item)).DistinctBy(x => x.Item.Route)];
    }

    /// <summary>The section holding the row for <paramref name="relativePath"/> (base-relative, no query), if any.</summary>
    public static NavSection? SectionFor(string relativePath) =>
        Sections.FirstOrDefault(s => s.Items.Any(i => i.IsCurrent(relativePath)));

    /// <summary>The section holding the current row for a location with its query (a deep link's section wins over its page's).</summary>
    public static NavSection? SectionForLocation(string relativeLocation) =>
        Sections.FirstOrDefault(s => s.Items.Any(i => i.IsCurrentLocation(relativeLocation)));

    /// <summary>Base-relative location with its query (no fragment), for deep-link rows.</summary>
    public static string RelativeLocation(NavigationManager navigation)
    {
        var location = navigation.ToBaseRelativePath(navigation.Uri);
        var hash = location.IndexOf('#');
        return hash >= 0 ? location[..hash] : location;
    }

    internal static (string Path, string Query) Split(string location)
    {
        var q = location.IndexOf('?');
        return q < 0 ? (location.TrimEnd('/'), "") : (location[..q].TrimEnd('/'), location[(q + 1)..]);
    }

    /// <summary>Every parameter of the deep link's query is present with the same value (other parameters may follow).</summary>
    internal static bool SameQuery(string actual, string expected)
    {
        static Dictionary<string, string> Parse(string query) => query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).GroupBy(p => Uri.UnescapeDataString(p[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last().ElementAtOrDefault(1) ?? ""), StringComparer.OrdinalIgnoreCase);
        var a = Parse(actual);
        return Parse(expected).All(e => a.TryGetValue(e.Key, out var v) && string.Equals(v, e.Value, StringComparison.OrdinalIgnoreCase));
    }

    public static string RelativePath(NavigationManager navigation)
    {
        var path = navigation.ToBaseRelativePath(navigation.Uri);
        var cut = path.IndexOfAny(['?', '#']);
        return (cut >= 0 ? path[..cut] : path).TrimEnd('/');
    }
}

public enum NavStatusKind { Partial, Setup, NeedsRefresh, NoEvidence, NotApplicable, Unsupported, Extension, Provider, Unavailable }

/// <summary>Compact sidebar status. <see cref="Text"/> is the short badge, <see cref="Spoken"/> its full words for screen readers,
/// <see cref="Tooltip"/> the reason.</summary>
public sealed record NavStatus(NavStatusKind Kind, string Text, string Spoken, string Tooltip);

/// <summary>
/// Sidebar status from the shared applicability evaluation. Precedence is the evaluator's: each review has exactly one
/// <see cref="ApplicabilityStatus"/>, and that status is always shown when it is not Applicable (Partial, N/A, Unsupported,
/// Setup, No evidence stay distinct). Only an Applicable review can show an overlay instead of no badge:
/// "Extension" when the review is provided by an explicitly enabled domain extension, or the provider name when the row
/// names one (Environment Analysis → "Azure"). An Applicable review without an overlay shows nothing.
/// </summary>
public static class NavStatusPresentation
{
    public static NavStatus? For(NavItem item, ReviewApplicability? applicability)
    {
        // Product availability outranks applicability, setup and provider overlays: an unavailable feature never reads as "Setup" or "Azure".
        if (FeatureAvailabilityCatalog.Find(item.FeatureId) is { IsUnavailable: true } unavailable)
            return new(NavStatusKind.Unavailable, "Unavailable", "Feature unavailable", unavailable.Summary);
        if (applicability is null) return null;
        var reason = applicability.Reason;
        if (applicability.Status == ApplicabilityStatus.Applicable)
        {
            if (item.ReviewId is { } id && ReviewCatalog.Find(id)?.DomainExtensionId is not null)
                return new(NavStatusKind.Extension, "Extension", "Project extension enabled",
                    Join("Provided by a project-specific extension enabled for this project.", reason));
            if (item.ProviderWhenApplicable is { } provider)
                return new(NavStatusKind.Provider, provider, $"Provider: {provider}", Join($"Provider: {provider}.", reason));
            return null;
        }

        return applicability.Status switch
        {
            ApplicabilityStatus.PartiallyApplicable => new(NavStatusKind.Partial, "Partial", ScoreSemantics.Label(applicability.Status),
                Join("Partial: the review covers part of the project.", reason)),
            ApplicabilityStatus.NotApplicable => new(NavStatusKind.NotApplicable, "N/A", ScoreSemantics.Label(applicability.Status),
                Join("Not applicable to this project.", reason)),
            ApplicabilityStatus.Unsupported => new(NavStatusKind.Unsupported, "Unsupported", ScoreSemantics.Label(applicability.Status),
                Join("Unsupported technology — a tool limitation, not a project finding.", reason)),
            ApplicabilityStatus.NeedsConfiguration => new(NavStatusKind.Setup, "Setup", ScoreSemantics.Label(applicability.Status),
                Join("Needs configuration before it can run.", reason)),
            ApplicabilityStatus.NeedsRefresh => new(NavStatusKind.NeedsRefresh, "Needs refresh", ScoreSemantics.Label(applicability.Status),
                Join("Applies, but the evidence is outdated: analyze the source again.", reason)),
            _ => new(NavStatusKind.NoEvidence, "No evidence", ScoreSemantics.Label(applicability.Status),
                Join("Not enough evidence yet to decide whether it applies.", reason)),
        };
    }

    private static string Join(string lead, string reason) => string.IsNullOrWhiteSpace(reason) ? lead : $"{lead} {reason}";
}

/// <summary>
/// Navigation emphasis presets. A Workspace View is a navigation preference only — never a security role or permission: it orders and
/// expands the groups a role usually needs and picks a landing page. Every module stays reachable in every view (collapsed groups, search,
/// All Modules), and Feature Visibility alone decides which rows exist. Independent of the selected project in both directions.
/// </summary>
public enum WorkspaceView { AllModules, ProjectLeader, ScrumMaster, TestLead, TechnicalReviewer }

public sealed record WorkspaceViewDefinition(WorkspaceView View, string Label, string Description, string LandingRoute, IReadOnlyList<string> FocusSections);

public static class WorkspaceViews
{
    public static IReadOnlyList<WorkspaceViewDefinition> All { get; } =
    [
        new(WorkspaceView.AllModules, "All Modules", "Every group expanded in catalog order.", "dashboard", []),
        new(WorkspaceView.ProjectLeader, "Project Leader", "Delivery status, scope and traceability first.", NavigationCatalog.DeliveryOverviewRoute,
            ["delivery-reporting", "getting-started", "traceability"]),
        new(WorkspaceView.ScrumMaster, "Scrum Master", "Weekly delivery progress, plans and tasks first.", "delivery-reporting",
            ["delivery-reporting", "document-review", "getting-started"]),
        new(WorkspaceView.TestLead, "Test Lead", "Quality, testing and requirement coverage first.", "test-coverage-review",
            ["quality-testing", "traceability", "getting-started"]),
        new(WorkspaceView.TechnicalReviewer, "Technical Reviewer", "Source, architecture and specifications first.", "source-analysis",
            ["source-review", "document-review", "quality-testing"]),
    ];

    public static WorkspaceViewDefinition Of(WorkspaceView view) => All.First(v => v.View == view);

    /// <summary>The sections in the view's order: its focus groups first, then the others in catalog order. Nothing is removed.</summary>
    public static IReadOnlyList<NavSection> Ordered(WorkspaceView view)
    {
        var focus = Of(view).FocusSections;
        return [.. NavigationCatalog.Sections.Where(s => focus.Contains(s.Id)).OrderBy(s => focus.ToList().IndexOf(s.Id)), .. NavigationCatalog.Sections.Where(s => !focus.Contains(s.Id))];
    }
}

/// <summary>
/// The sidebar's one state manager: the Workspace View and, per view, which groups the person collapsed or expanded. In a focused view the
/// groups outside its focus start collapsed (still visible and searchable); All Modules starts with everything expanded. The current page's
/// group is always expanded. Remembered in this browser (see <see cref="Serialize"/>); it holds no project state and project selection holds
/// none of it.
/// </summary>
public sealed class NavigationSectionState
{
    private readonly Dictionary<WorkspaceView, Dictionary<string, bool>> _choices = [];

    public WorkspaceView View { get; private set; } = WorkspaceView.AllModules;

    public event Action? Changed;

    private Dictionary<string, bool> Choices => _choices.TryGetValue(View, out var c) ? c : _choices[View] = new(StringComparer.Ordinal);

    private bool DefaultCollapsed(string sectionId) => View != WorkspaceView.AllModules && !WorkspaceViews.Of(View).FocusSections.Contains(sectionId);

    public bool IsCollapsed(string sectionId) => Choices.TryGetValue(sectionId, out var collapsed) ? collapsed : DefaultCollapsed(sectionId);

    public void Toggle(string sectionId)
    {
        Choices[sectionId] = !IsCollapsed(sectionId);
        Changed?.Invoke();
    }

    public void Expand(string sectionId)
    {
        if (!IsCollapsed(sectionId)) return;
        Choices[sectionId] = false;
        Changed?.Invoke();
    }

    /// <summary>Switches the navigation emphasis. Changes nothing else: not the project, not running work, not any page state.</summary>
    public void SetView(WorkspaceView view)
    {
        if (View == view) return;
        View = view;
        Changed?.Invoke();
    }

    private sealed record Stored(WorkspaceView View, Dictionary<WorkspaceView, Dictionary<string, bool>> Choices);

    public string Serialize() => System.Text.Json.JsonSerializer.Serialize(new Stored(View, _choices));

    /// <summary>Restores remembered preferences; unreadable or unknown values are ignored (defaults stay).</summary>
    public void Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            if (System.Text.Json.JsonSerializer.Deserialize<Stored>(json) is not { } stored || !Enum.IsDefined(stored.View)) return;
            View = stored.View;
            _choices.Clear();
            foreach (var (view, choices) in stored.Choices ?? []) if (Enum.IsDefined(view)) _choices[view] = new(choices, StringComparer.Ordinal);
            Changed?.Invoke();
        }
        catch (System.Text.Json.JsonException) { }
    }
}
