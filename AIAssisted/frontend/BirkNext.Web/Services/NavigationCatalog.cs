using BirkNext.Applicability;
using BirkNext.Technology;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

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

/// <summary>A sidebar section. It is shown when at least one of its rows is visible. <paramref name="Icon"/> stands for the group in the
/// collapsed (icon-only) sidebar; it comes from the same icon set as the rows.</summary>
public sealed record NavSection(string Id, string Label, IReadOnlyList<NavItem> Items, string Icon = "nav-icon-dashboard");

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
            new("dashboard", "Project Quality", "nav-icon-dashboard", f => f.Dashboard, NavLinkMatch.Prefix,
                Hint: "Project Quality Dashboard: evidence, specification quality, traceability, reviews and verification", Aliases: ["Dashboard", "Project Quality Dashboard"]),
            new("getting-started", "Recommended Workflow", "nav-icon-workflow", f => f.RecommendedWorkflow, Aliases: ["Getting Started"]),
            new("project-import", "Import Project", "nav-icon-import", f => f.SampleProjects || f.SourceAnalysis,
                Hint: "primary input: one project ZIP becomes project artifacts and a Source Analysis snapshot", Aliases: ["Project Inputs", "Upload ZIP"]),
            new("sources", "Source Management", "nav-icon-import", f => f.SampleProjects || f.SourceAnalysis,
                Hint: "imported ZIPs and linked Azure DevOps repositories: roles, updates, earlier imports", Aliases: ["Git Sources", "Linked repositories"]),
            new("demo-projects", "Demo Projects", "nav-icon-sample-projects", f => f.SampleProjects,
                Hint: "optional built-in example projects", Aliases: ["Sample Projects"]),
            new(TargetEnvironmentsRoute, "Target Environments", "nav-icon-target", f => f.AdminSystemSettings,
                Hint: "runtime configuration of the deployed system, used by runtime reviews (opens System Settings)", FeatureId: "target-environments"),
        ], "nav-icon-dashboard"),
        // DELIVERY MANAGEMENT: for Project Leaders and Scrum Masters — its own projects, snapshots and frozen reports (never current BirkNext state).
        new("delivery-reporting", "Delivery Management",
        [
            new(DeliveryOverviewRoute, "Delivery Overview", "nav-icon-dashboard", Always,
                Hint: "virtual SDD backlog: features, user stories and acceptance criteria with their delivery status from imported snapshots",
                Aliases: ["SDD backlog", "Backlog", "Delivery status"]),
            new("delivery-reporting", "Delivery Reports", "nav-icon-workflow", Always,
                Hint: "weekly story/feature progress from imported repository snapshots: what changed, attention items, next-week focus",
                Aliases: ["Delivery Reporting", "Weekly report", "Azure DevOps import"]),
        ], "nav-icon-workflow"),
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
        ], "nav-icon-task-explorer"),
        // TRACEABILITY & IMPACT: how lifecycle artifacts and tracked delivery changes are linked, and what a change affects.
        new("traceability", "Traceability & Impact",
        [
            new("artifact-traceability", "Requirements Traceability", "nav-icon-artifact-traceability", f => f.ArtifactTraceability, Aliases: ["Artifact Traceability"]),
            // Stable route and visibility key (ImplementationTraceability); also reachable at /change-traceability.
            new("implementation-traceability", "Change Traceability", "nav-icon-task-alignment", f => f.ImplementationTraceability,
                Hint: "Azure DevOps work items → pull requests → commits → changed files (optional)", Aliases: ["Implementation Traceability"]),
            // Cross-cutting impact of requirement and source changes (off by default).
            new("impact-analysis", "Impact Analysis", "nav-icon-impact-analysis", f => f.ImpactAnalysis, Aliases: ["Unified Impact Analysis"]),
            // Shared canonical correlation: one review registry for aliases, capability candidates, contract versions and test VERIFIES links.
            new("correlation-review", "Relationship Review", "nav-icon-traceability", Always,
                Hint: "canonical entities, typed relations and the shared review decisions every module uses", Aliases: ["Correlation Review", "Canonical interpretation", "Aliases"]),
            // Test lead's release decision for a project or modules: dimensions, governance, frozen reports and sign-off.
            new("release-readiness", "Release Readiness", "nav-icon-traceability", Always,
                Hint: "Ready / Ready with limitations / Blocked / Not assessed for a release scope, with reasons, frozen reports and sign-off",
                Aliases: ["Test lead", "Release decision", "Go/no-go"]),
            // Shared snapshot registry: every stored snapshot as metadata, with lineage, current/approved pointers and retention.
            new("snapshots", "Snapshots", "nav-icon-traceability", Always, Hint: "stored snapshots across modules: lineage, current/latest, retention", Aliases: ["Snapshot registry", "Lineage"]),
            new("traceability", "Traceability & Coverage", "nav-icon-traceability", f => f.LegacyTraceabilityNavigationEnabled && f.TraceabilityCoverage, OptionalByDefault: true),
            new("traceability/suggestions", "Traceability Suggestions", "nav-icon-suggestions", f => f.LegacyTraceabilityNavigationEnabled && f.TraceabilitySuggestions, OptionalByDefault: true),
            new("code-traceability", "Code Traceability", "nav-icon-code-trace", f => f.LegacyTraceabilityNavigationEnabled && f.CodeTraceability, OptionalByDefault: true),
        ], "nav-icon-traceability"),
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
        ], "nav-icon-source-analysis"),
        // Planning → preparation → target-bound runs. Source-only assessments (test inventory, code risk) say where to focus; Test Data
        // is the prerequisite of the runs that need test identities/assets (Performance, Critical E2E; readiness shown in API/Integration);
        // Topology precedes the IQR it reads bindings from; Critical E2E is the end-to-end release check.
        new("quality-testing", "Quality & Testing",
        [
            new("test-coverage-review", "Test Coverage & Overlap Review", "nav-icon-task-alignment", Always),
            new("code-risk-review", "Code Risk Review", "nav-icon-constitution-compliance", Always, ReviewId: "code-risk-review"),
            new("test-data", "Test Data", "nav-icon-task-alignment", Always, Hint: "approved typed test data: values, payloads, files, entity references, data sets, expected results"),
            new("api-quality-review/contracts", "Contract Testing", "nav-icon-task-alignment", Always, Hint: "stored REST/GraphQL contracts: versions, tests, readiness, runs, impact and coverage",
                Aliases: ["Contract-Driven Testing", "Contracts"]),
            new("integration-suites", "Integration Test Suites", "nav-icon-task-alignment", Always, WrapLabel: true,
                Hint: "from an analysed source snapshot: contracts, tests, test data, flow, Kode 6/7 scenarios, readiness and what remains before a run",
                Aliases: ["Integration Suites", "Generate Suite", "ATG"]),
            new("execution-results", "Test Execution & Evidence", "nav-icon-task-alignment", Always, Hint: "actual results of every test module: runs, outcomes, request/response, timelines, traceability, retention"),
            new("parquet-viewer", "Parquet Quality Review", "nav-icon-task-alignment", Always, Hint: "validate a Parquet file against its source contract: schema, generated rules, coverage, profile, baseline, evidence",
                Aliases: ["Parquet"]),
            new("frontend-quality-review", "Frontend Quality Review", "nav-icon-constitution-compliance", f => f.FrontendQualityReview, ReviewId: "frontend-quality-review", Aliases: ["FQR"]),
            new("api-quality-review", "API Quality Review", "nav-icon-constitution-compliance", f => f.ApiQualityReview, ReviewId: "api-quality-review", Aliases: ["AQR", "API & Integration Quality"]),
            new("integration-topology", "Integration Topology", "nav-icon-source-analysis", Always, Hint: "components, interfaces (REST, GraphQL, messaging, file/storage) and connections with their sources",
                Aliases: ["API & Integration Quality"]),
            new("integration-quality-review", "Integration Quality Review", "nav-icon-constitution-compliance", f => f.IntegrationQualityReview, ReviewId: "integration-quality-review",
                Aliases: ["IQR", "API & Integration Quality"]),
            new("performance-test-review", "Performance Test Review", "nav-icon-constitution-compliance", Always, ReviewId: "performance-test-review", Aliases: ["PTR", "k6"]),
            new("critical-e2e-regression", "Critical E2E Regression", "nav-icon-constitution-compliance", f => f.CriticalE2ERegression, ReviewId: "critical-e2e-regression", Aliases: ["E2E", "Regression"]),
        ], "nav-icon-constitution-compliance"),
        // PROJECT EXTENSIONS: project-specific domain extensions, shown with their applicability (N/A unless the extension is enabled).
        new("extensions", "Project Extensions",
        [
            new("security-classification-review", "M01 – Gradert tilgang (Kode 6/7)", "nav-icon-constitution-compliance", Always, ReviewId: "security-classification-review",
                Hint: "M01 graded access and Kode 6 / Kode 7 classification in M2LB (formerly Security Classification); its shared evidence is also used by Skolenærvær. Not a generic security review", WrapLabel: true,
                Aliases: ["Security Classification", "Kode 6", "Kode 7"]),
            new("extensions/skolenaervaer", "Skolenærvær Testing", "nav-icon-constitution-compliance", Always,
                Hint: "domain view over the shared CDC and integration journey engines", Aliases: ["Skolenaervaer"]),
        ], "nav-icon-import"),
        // HELP & SETTINGS.
        new("admin", "Help & Settings",
        [
            new("user-guide", "User Guide", "nav-icon-user-guide", f => f.UserGuide, Aliases: ["Help"]),
            new("admin/system-settings", "System Settings", "nav-icon-settings", f => f.AdminSystemSettings, Aliases: ["Admin", "Feature Visibility", "Authentication"]),
            new("workspace-transfer", "Backup & Transfer", "nav-icon-import", f => f.AdminSystemSettings,
                Hint: "full backup and isolated restore; export and import projects and integrations (no credentials travel)", Aliases: ["Backup", "Export", "Import package", "Restore"]),
        ], "nav-icon-settings"),
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
        new("traceability-impact", "Traceability & Impact", ["artifact-traceability", "implementation-traceability", "impact-analysis", "correlation-review", "release-readiness", "snapshots"]),
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
    public static IReadOnlyList<(NavSection Section, NavItem Item)> Search(string? text, FeatureVisibilityService features) =>
        Rank(text, Sections.SelectMany(s => s.Items.Where(i => i.Visible(features)).Select(i => (s, i))));

    /// <summary>Ranks rows for a search text (label prefix, label, alias, group name, then hint/description). Empty text finds nothing.</summary>
    internal static IReadOnlyList<(NavSection Section, NavItem Item)> Rank(string? text, IEnumerable<(NavSection Section, NavItem Item)> rows)
    {
        var q = (text ?? "").Trim();
        if (q.Length == 0) return [];
        return [.. rows
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
/// Navigation emphasis presets. A Workspace View is a navigation preference only — never a security role or permission. It picks the modules
/// a role usually needs, puts their groups first and suggests a starting page (switching never navigates). Modules outside the view are
/// only left out of the sidebar: search lists them under "Available in other workspaces", "Show other modules" reveals them, the person can
/// add one to the view, and their URLs always open. Feature Visibility alone decides which modules exist. Independent of the selected
/// project in both directions. Stored as numbers: 4 was "Technical Reviewer" and is now Developer (same source-first focus).
/// </summary>
public enum WorkspaceView { AllModules = 0, ProjectLeader = 1, ScrumMaster = 2, TestLead = 3, Developer = 4, Architect = 5 }

/// <param name="FocusSections">Group order: these groups first, the others after in catalog order.</param>
/// <param name="Modules">Routes of the modules in the view (All Modules: every module).</param>
public sealed record WorkspaceViewDefinition(WorkspaceView View, string Label, string Description, string LandingRoute, IReadOnlyList<string> FocusSections,
    IReadOnlyList<string> Modules)
{
    public bool IsAllModules => View == WorkspaceView.AllModules;

    /// <summary>The module is part of the view's preset (Dashboard, User Guide and System Settings are part of every view).</summary>
    public bool Contains(NavItem item) => IsAllModules || WorkspaceViews.EveryView.Contains(item.Route) || Modules.Contains(item.Route);
}

public static class WorkspaceViews
{
    /// <summary>Rows every view keeps: the project overview, help and settings.</summary>
    public static IReadOnlyList<string> EveryView { get; } = ["dashboard", "user-guide", "admin/system-settings"];

    public static IReadOnlyList<WorkspaceViewDefinition> All { get; } =
    [
        new(WorkspaceView.AllModules, "All Modules", "Every module, in catalog order.", "dashboard", [], []),
        new(WorkspaceView.ProjectLeader, "Project Leader", "Delivery status, release decision, snapshots and shared review decisions.", NavigationCatalog.DeliveryOverviewRoute,
            ["delivery-reporting", "getting-started", "traceability"],
            [NavigationCatalog.DeliveryOverviewRoute, "delivery-reporting", "getting-started", "release-readiness", "snapshots", "correlation-review", "artifact-traceability"]),
        new(WorkspaceView.ScrumMaster, "Scrum Master", "Weekly delivery progress, plans, tasks and their alignment.", "delivery-reporting",
            ["delivery-reporting", "document-review", "getting-started", "traceability"],
            ["delivery-reporting", NavigationCatalog.DeliveryOverviewRoute, "getting-started", "specification-explorer", "plan-explorer", "task-explorer", "task-alignment",
             "implementation-traceability"]),
        new(WorkspaceView.TestLead, "Test Lead", "Requirement coverage, test data, runtime reviews and the release decision.", "test-coverage-review",
            ["quality-testing", "traceability", "delivery-reporting", "getting-started"],
            ["artifact-traceability", "release-readiness", NavigationCatalog.DeliveryOverviewRoute, "test-coverage-review", "test-data", "frontend-quality-review",
             "api-quality-review", "integration-quality-review", "performance-test-review", "critical-e2e-regression", NavigationCatalog.TargetEnvironmentsRoute]),
        new(WorkspaceView.Developer, "Developer", "Source, specifications, schemas, APIs, dependencies and pipelines.", "source-analysis",
            ["source-review", "document-review", "quality-testing", "getting-started"],
            ["source-analysis", "specification-explorer", "schema-studio", "api-quality-review", "technology-coverage", "dependency-review", "pipeline-review",
             "code-risk-review", "sources", "project-import"]),
        new(WorkspaceView.Architect, "Architect", "Source structure, integrations, schemas, data models and environment architecture.", "integration-topology",
            ["source-review", "quality-testing", "document-review", "getting-started"],
            ["source-analysis", "integration-topology", "integration-quality-review", "schema-studio", "data-model-explorer", "constitution-explorer", "sources",
             "technology-coverage", NavigationCatalog.GeneratedDocumentationExplorerRoute, "azure-environment"]),
    ];

    public static WorkspaceViewDefinition Of(WorkspaceView view) => All.FirstOrDefault(v => v.View == view) ?? All[0];

    /// <summary>The sections in the view's order: its focus groups first, then the others in catalog order. Nothing is removed.</summary>
    public static IReadOnlyList<NavSection> Ordered(WorkspaceView view)
    {
        var focus = Of(view).FocusSections;
        return [.. NavigationCatalog.Sections.Where(s => focus.Contains(s.Id)).OrderBy(s => focus.ToList().IndexOf(s.Id)), .. NavigationCatalog.Sections.Where(s => !focus.Contains(s.Id))];
    }

    /// <summary>The focused views (not All Modules) whose preset holds the module, for "Available in other workspaces".</summary>
    public static IReadOnlyList<WorkspaceViewDefinition> Holding(NavItem item) =>
        [.. All.Where(v => !v.IsAllModules && !WorkspaceViews.EveryView.Contains(item.Route) && v.Modules.Contains(item.Route))];
}

/// <summary>Why a module is or is not a sidebar row in the current Workspace View.</summary>
public enum ModuleReach
{
    /// <summary>A row of the current view.</summary>
    InView,
    /// <summary>Outside the view's preset, added to the view by the person.</summary>
    AddedByYou,
    /// <summary>Outside the view: not a row (search and "Show other modules" find it; its URL opens). Not a permission.</summary>
    HiddenByWorkspaceView,
    /// <summary>Turned off in System Settings → Feature Visibility: no row in any view.</summary>
    DisabledByFeatureVisibility,
}

/// <summary>One module's navigation state. <see cref="Unavailable"/> is product availability (<see cref="FeatureAvailabilityCatalog"/>): the
/// row stays and says so.</summary>
public sealed record NavModule(NavSection Section, NavItem Item, ModuleReach Reach, bool Unavailable)
{
    public bool Enabled => Reach != ModuleReach.DisabledByFeatureVisibility;
    public bool IsRow => Reach is ModuleReach.InView or ModuleReach.AddedByYou;
}

public enum NavSearchBucket { CurrentWorkspace, OtherWorkspaces, Disabled }

public sealed record NavSearchResult(NavModule Module, NavSearchBucket Bucket, IReadOnlyList<WorkspaceViewDefinition> OtherViews)
{
    public NavSection Section => Module.Section;
    public NavItem Item => Module.Item;
}

/// <summary>
/// The one resolution model of the sidebar, in precedence order: (1) authorization — BirkNext has no per-module authorization (every person
/// who can open BirkNext may open every module), so nothing resolves to access denied; (2) Feature Visibility (System Settings) — a disabled
/// module is no row anywhere and search marks it disabled; (3) Workspace View presentation — a module outside the view is no row; (4) the
/// person's own additions to the view. Product availability (<see cref="FeatureAvailabilityCatalog"/>) is shown on the row, never hidden.
/// </summary>
public static class NavigationModules
{
    public static NavModule Resolve(NavSection section, NavItem item, FeatureVisibilityService features, NavigationSectionState state)
    {
        var view = WorkspaceViews.Of(state.View);
        var reach = !item.Visible(features) ? ModuleReach.DisabledByFeatureVisibility
            : view.Contains(item) ? ModuleReach.InView
            : state.IsAdded(item.Route) ? ModuleReach.AddedByYou
            : ModuleReach.HiddenByWorkspaceView;
        return new(section, item, reach, FeatureAvailabilityCatalog.IsUnavailable(item.FeatureId));
    }

    public static IReadOnlyList<NavModule> All(FeatureVisibilityService features, NavigationSectionState state) =>
        [.. NavigationCatalog.Sections.SelectMany(s => s.Items.Select(i => Resolve(s, i, features, state)))];

    /// <summary>The module of the current location (deep link first), if the location is a module at all.</summary>
    public static NavModule? Current(string relativeLocation, FeatureVisibilityService features, NavigationSectionState state) =>
        All(features, state).FirstOrDefault(m => m.Item.IsCurrentLocation(relativeLocation));

    /// <summary>
    /// Workspace-aware search over every module (labels, earlier names, group names, descriptions): rows of the current view first, then
    /// modules of other views (with the views that hold them), then modules turned off in Feature Visibility (marked, not opened).
    /// </summary>
    public static IReadOnlyList<NavSearchResult> Search(string? text, FeatureVisibilityService features, NavigationSectionState state)
    {
        var modules = All(features, state).ToDictionary(m => m.Item.Route);
        return [.. NavigationCatalog.Rank(text, modules.Values.Select(m => (m.Section, m.Item)))
            .Select(r => modules[r.Item.Route])
            .Select(m => new NavSearchResult(m, m.Reach switch
            {
                ModuleReach.DisabledByFeatureVisibility => NavSearchBucket.Disabled,
                ModuleReach.HiddenByWorkspaceView => NavSearchBucket.OtherWorkspaces,
                _ => NavSearchBucket.CurrentWorkspace,
            }, m.Reach == ModuleReach.HiddenByWorkspaceView ? WorkspaceViews.Holding(m.Item) : []))
            .OrderBy(r => r.Bucket)];
    }
}

/// <summary>
/// The sidebar's one state manager (navigation preferences only, remembered in this browser — see <see cref="Serialize"/>): the Workspace
/// View; per view, which groups the person collapsed, whether other modules are shown and which modules the person added; and whether the
/// desktop sidebar is collapsed to icons. The mobile drawer is not part of it. Groups start expanded; the current page's group is always
/// expanded. It holds no project state and project selection holds none of it.
/// </summary>
public sealed class NavigationSectionState
{
    public const string PreferencesKey = "birknext.navigation.v1";

    private readonly Dictionary<WorkspaceView, Dictionary<string, bool>> _choices = [];
    private readonly HashSet<WorkspaceView> _showOther = [];
    private readonly Dictionary<WorkspaceView, HashSet<string>> _added = [];

    public WorkspaceView View { get; private set; } = WorkspaceView.AllModules;

    /// <summary>The desktop sidebar shows icons only (a preference; independent of the mobile drawer).</summary>
    public bool SidebarCollapsed { get; private set; }

    /// <summary>Remembered preferences were read (or could not be); changes are saved from then on.</summary>
    public bool IsRestored { get; private set; }

    public event Action? Changed;

    private Dictionary<string, bool> Choices => _choices.TryGetValue(View, out var c) ? c : _choices[View] = new(StringComparer.Ordinal);

    public bool IsCollapsed(string sectionId) => Choices.TryGetValue(sectionId, out var collapsed) && collapsed;

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

    /// <summary>Switches the navigation emphasis. Changes nothing else: not the page, the project, running work or any page state.</summary>
    public void SetView(WorkspaceView view)
    {
        if (View == view || !Enum.IsDefined(view)) return;
        View = view;
        Changed?.Invoke();
    }

    public void SetSidebarCollapsed(bool collapsed)
    {
        if (SidebarCollapsed == collapsed) return;
        SidebarCollapsed = collapsed;
        Changed?.Invoke();
    }

    /// <summary>Modules outside the current view are shown as rows too (marked outside the view).</summary>
    public bool ShowOtherModules => _showOther.Contains(View);

    public void ToggleShowOtherModules()
    {
        if (!_showOther.Remove(View)) _showOther.Add(View);
        Changed?.Invoke();
    }

    public bool IsAdded(string route) => _added.TryGetValue(View, out var routes) && routes.Contains(route);

    /// <summary>The person's own addition to the current view (precedence 4: never shows a module Feature Visibility turned off).</summary>
    public void AddToView(string route)
    {
        if (!(_added.TryGetValue(View, out var routes) ? routes : _added[View] = new(StringComparer.Ordinal)).Add(route)) return;
        Changed?.Invoke();
    }

    public void RemoveFromView(string route)
    {
        if (!_added.TryGetValue(View, out var routes) || !routes.Remove(route)) return;
        Changed?.Invoke();
    }

    private sealed record Stored(WorkspaceView View, Dictionary<WorkspaceView, Dictionary<string, bool>> Choices, bool SidebarCollapsed = false,
        WorkspaceView[]? ShowOther = null, Dictionary<WorkspaceView, string[]>? Added = null);

    public string Serialize() => System.Text.Json.JsonSerializer.Serialize(new Stored(View, _choices, SidebarCollapsed, [.. _showOther],
        _added.ToDictionary(a => a.Key, a => a.Value.ToArray())));

    /// <summary>Restores remembered preferences; unreadable or unknown values are ignored (defaults stay). Earlier preferences stay valid:
    /// fields added later default, and the former "TechnicalReviewer" view key reads as Developer.</summary>
    public void Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            json = json.Replace("\"TechnicalReviewer\"", "\"Developer\"", StringComparison.Ordinal);
            if (System.Text.Json.JsonSerializer.Deserialize<Stored>(json) is not { } stored || !Enum.IsDefined(stored.View)) return;
            View = stored.View;
            SidebarCollapsed = stored.SidebarCollapsed;
            _choices.Clear();
            foreach (var (view, choices) in stored.Choices ?? []) if (Enum.IsDefined(view)) _choices[view] = new(choices, StringComparer.Ordinal);
            _showOther.Clear();
            foreach (var view in stored.ShowOther ?? []) if (Enum.IsDefined(view)) _showOther.Add(view);
            _added.Clear();
            foreach (var (view, routes) in stored.Added ?? []) if (Enum.IsDefined(view)) _added[view] = new(routes ?? [], StringComparer.Ordinal);
            Changed?.Invoke();
        }
        catch (System.Text.Json.JsonException) { }
    }

    /// <summary>
    /// Reads remembered preferences once, synchronously where the runtime allows (WebAssembly), so the first render already has the right
    /// sidebar width and view — no flash of the default. Storage that is unavailable leaves the defaults; navigation never depends on it.
    /// </summary>
    public void EnsureRestored(IJSRuntime? js)
    {
        if (IsRestored || js is not IJSInProcessRuntime inProcess) return;
        try { Restore(inProcess.Invoke<string?>("localStorage.getItem", PreferencesKey)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        IsRestored = true;
    }

    /// <summary>The asynchronous fallback of <see cref="EnsureRestored"/> for runtimes without synchronous interop.</summary>
    public async Task EnsureRestoredAsync(IJSRuntime? js)
    {
        if (IsRestored || js is null) return;
        try { Restore(await js.InvokeAsync<string?>("localStorage.getItem", PreferencesKey)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        IsRestored = true;
    }

    public async Task SaveAsync(IJSRuntime? js)
    {
        if (!IsRestored || js is null) return;
        try { await js.InvokeVoidAsync("localStorage.setItem", PreferencesKey, Serialize()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
