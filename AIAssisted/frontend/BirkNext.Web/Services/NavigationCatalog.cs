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
    bool OptionalByDefault = false)
{
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

public static class NavigationCatalog
{
    private static bool Always(FeatureVisibilityService _) => true;

    /// <summary>Route of the Target Environments section of System Settings. The sidebar row is a deep link, not a second page.</summary>
    public const string TargetEnvironmentsRoute = "admin/system-settings?section=target-environments";

    /// <summary>
    /// Sections follow how BirkNext gathers and uses evidence: the three project inputs (documents, source, target) first, then
    /// the pages that consume each input, in prerequisite-to-consumer order. A section shows when at least one of its rows is
    /// visible; Feature Visibility decides rows. Section IDs are stable identities, never display text.
    /// </summary>
    public static IReadOnlyList<NavSection> Sections { get; } =
    [
        // Start here. Dashboard is cross-cutting (documents, source and runtime evidence), so it is not a document page.
        new("getting-started", "Getting Started",
        [
            new("getting-started", "Recommended Workflow", "nav-icon-workflow", f => f.RecommendedWorkflow),
            new("user-guide", "User Guide", "nav-icon-user-guide", f => f.UserGuide),
            new("dashboard", "Dashboard", "nav-icon-dashboard", f => f.Dashboard, NavLinkMatch.Prefix),
        ]),
        // The three inputs: project documents (Sample Projects is the easiest start; explorers also import), source, target.
        new("project-inputs", "Project Inputs",
        [
            new("project-import", "Import Project", "nav-icon-sample-projects", f => f.SampleProjects || f.SourceAnalysis,
                Hint: "one project ZIP: documents become project artifacts and source becomes a Source Analysis snapshot"),
            new("sample-projects", "Sample Projects", "nav-icon-sample-projects", f => f.SampleProjects,
                Hint: "ready-made example projects; documents can also be imported in any explorer"),
            new("source-analysis", "Source Analysis", "nav-icon-source-analysis", f => f.SourceAnalysis,
                Hint: "source input: the current source snapshot (from Import Project or a source-only upload)"),
            new(TargetEnvironmentsRoute, "Target Environments", "nav-icon-target", f => f.AdminSystemSettings,
                Hint: "runtime input: application, API and integration targets (opens System Settings)", FeatureId: "target-environments"),
        ]),
        new("document-review", "Document Review",
        [
            new("specification-explorer", "Specification Explorer", "nav-icon-specification-explorer", f => f.SpecificationExplorer),
            new("constitution-explorer", "Constitution Explorer", "nav-icon-constitution", f => f.ConstitutionExplorer),
            new("data-model-explorer", "Data Model Explorer", "nav-icon-data-model-explorer", f => f.DataModelExplorer),
            new("plan-explorer", "Plan Explorer", "nav-icon-plan-explorer", f => f.PlanExplorer),
            new("task-explorer", "Task Explorer", "nav-icon-task-explorer", f => f.TaskExplorer),
        ]),
        // Documents linked to each other and to implementation evidence.
        new("traceability", "Traceability",
        [
            new("artifact-traceability", "Requirements Traceability", "nav-icon-artifact-traceability", f => f.ArtifactTraceability),
            new("task-alignment", "Implementation Review", "nav-icon-task-alignment", f => f.ImplementationReview,
                Hint: "tasks checked against the specification"),
            new("implementation-traceability", "Implementation Traceability", "nav-icon-task-alignment", f => f.ImplementationTraceability,
                Hint: "work items linked to pull requests and changed files"),
            new("spec-drift", "Spec Drift", "nav-icon-spec-drift", f => f.SpecDrift),
            new("impact-analysis", "Impact Analysis", "nav-icon-impact-analysis", f => f.ImpactAnalysis),
            new("traceability", "Traceability & Coverage", "nav-icon-traceability", f => f.LegacyTraceabilityNavigationEnabled && f.TraceabilityCoverage, OptionalByDefault: true),
            new("traceability/suggestions", "Traceability Suggestions", "nav-icon-suggestions", f => f.LegacyTraceabilityNavigationEnabled && f.TraceabilitySuggestions, OptionalByDefault: true),
            new("code-traceability", "Code Traceability", "nav-icon-code-trace", f => f.LegacyTraceabilityNavigationEnabled && f.CodeTraceability, OptionalByDefault: true),
        ]),
        // Consumers of the source snapshot (SBOM import also feeds Dependency Review; Environment Analysis adds a cloud provider).
        new("source-review", "Source Review",
        [
            new("technology-coverage", "Technology Coverage", "nav-icon-source-analysis", Always,
                Hint: "what the source contains and what BirkNext can assess"),
            new("dependency-review", "Dependency Review", "nav-icon-constitution-compliance", Always, ReviewId: "dependency-review"),
            new("pipeline-review", "Pipeline Review", "nav-icon-task-alignment", Always, ReviewId: "pipeline-review"),
            // The page is the Azure provider of environment analysis; the menu stays generic and names the provider in the status.
            new("azure-environment", "Environment Analysis", "nav-icon-source-analysis", f => f.AzureEnvironmentAnalysis,
                ReviewId: "azure-environment", Hint: "read-only cloud environment inventory (provider: Azure)", ProviderWhenApplicable: "Azure", FeatureId: "environment-analysis"),
        ]),
        // Quality Review is cross-cutting (documents); the rest test a configured target or integrations at runtime.
        new("quality-testing", "Quality & Testing",
        [
            new("quality-review", "Quality Review", "nav-icon-qa-auditor", f => f.QualityReview, ReviewId: "quality-review"),
            new("frontend-quality-review", "Frontend Quality Review", "nav-icon-constitution-compliance", f => f.FrontendQualityReview, ReviewId: "frontend-quality-review"),
            new("api-quality-review", "API Quality Review", "nav-icon-constitution-compliance", f => f.ApiQualityReview, ReviewId: "api-quality-review"),
            new("integration-quality-review", "Integration Quality Review", "nav-icon-constitution-compliance", f => f.IntegrationQualityReview, ReviewId: "integration-quality-review"),
            new("performance-test-review", "Performance Test Review", "nav-icon-constitution-compliance", Always, ReviewId: "performance-test-review"),
            new("critical-e2e-regression", "Critical E2E Regression", "nav-icon-constitution-compliance", f => f.CriticalE2ERegression, ReviewId: "critical-e2e-regression"),
        ]),
        // Project-specific domain extensions: shown with their applicability (N/A unless the extension is enabled).
        new("extensions", "Extensions",
        [
            new("security-classification-review", "Security Classification", "nav-icon-constitution-compliance", Always, ReviewId: "security-classification-review",
                Hint: "domain extension (M2LB child security classification), not a generic security review"),
        ]),
        new("ai-review", "AI Review",
        [
            new("ai-change-auditor", "AI Change Review", "nav-icon-ai-auditor", f => f.AiChangeReview, OptionalByDefault: true),
        ]),
        new("admin", "Admin",
        [
            new("admin/system-settings", "System Settings", "nav-icon-settings", f => f.AdminSystemSettings),
        ]),
    ];

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

public enum NavStatusKind { Partial, Setup, NeedsRefresh, NoEvidence, NotApplicable, Unsupported, Extension, Provider }

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

/// <summary>Which sidebar sections are collapsed, for this browser session. Everything starts expanded.</summary>
public sealed class NavigationSectionState
{
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    public bool IsCollapsed(string sectionId) => _collapsed.Contains(sectionId);

    public void Toggle(string sectionId)
    {
        if (!_collapsed.Remove(sectionId)) _collapsed.Add(sectionId);
    }

    public void Expand(string sectionId) => _collapsed.Remove(sectionId);
}
