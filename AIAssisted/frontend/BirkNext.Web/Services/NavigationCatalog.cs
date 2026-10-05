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

    public bool IsCurrent(string relativePath) =>
        Match == NavLinkMatch.All
            ? string.Equals(relativePath, Route, StringComparison.OrdinalIgnoreCase)
            : relativePath.Equals(Route, StringComparison.OrdinalIgnoreCase) || relativePath.StartsWith(Route + "/", StringComparison.OrdinalIgnoreCase);
}

public sealed record NavSection(string Id, string Label, Func<FeatureVisibilityService, bool> Visible, IReadOnlyList<NavItem> Items);

public static class NavigationCatalog
{
    private static bool Always(FeatureVisibilityService _) => true;

    public static IReadOnlyList<NavSection> Sections { get; } =
    [
        new("getting-started", "Getting Started", f => f.ShowSectionGettingStarted,
        [
            new("getting-started", "Recommended Workflow", "nav-icon-workflow", f => f.RecommendedWorkflow),
            new("user-guide", "User Guide", "nav-icon-user-guide", f => f.UserGuide),
        ]),
        new("review", "Review", f => f.ShowSectionReview,
        [
            new("dashboard", "Dashboard", "nav-icon-dashboard", f => f.Dashboard, NavLinkMatch.Prefix),
            new("specification-explorer", "Specification Explorer", "nav-icon-specification-explorer", f => f.SpecificationExplorer),
            new("constitution-explorer", "Constitution Explorer", "nav-icon-constitution", f => f.ConstitutionExplorer),
            new("data-model-explorer", "Data Model Explorer", "nav-icon-data-model-explorer", f => f.DataModelExplorer),
            new("plan-explorer", "Plan Explorer", "nav-icon-plan-explorer", f => f.PlanExplorer),
            new("task-explorer", "Task Explorer", "nav-icon-task-explorer", f => f.TaskExplorer),
        ]),
        new("library", "Library", f => f.ShowSectionLibrary,
        [
            new("sample-projects", "Sample Projects", "nav-icon-sample-projects", f => f.SampleProjects),
        ]),
        new("traceability", "Traceability", f => f.ShowSectionTraceability,
        [
            new("traceability", "Traceability & Coverage", "nav-icon-traceability", f => f.TraceabilityCoverage, OptionalByDefault: true),
            new("traceability/suggestions", "Traceability Suggestions", "nav-icon-suggestions", f => f.TraceabilitySuggestions, OptionalByDefault: true),
            new("code-traceability", "Code Traceability", "nav-icon-code-trace", f => f.CodeTraceability, OptionalByDefault: true),
        ]),
        new("analysis", "Analysis", f => f.ShowSectionAnalysis,
        [
            new("spec-drift", "Spec Drift", "nav-icon-spec-drift", f => f.SpecDrift),
            new("impact-analysis", "Impact Analysis", "nav-icon-impact-analysis", f => f.ImpactAnalysis),
            new("artifact-traceability", "Requirements Traceability", "nav-icon-artifact-traceability", f => f.ArtifactTraceability),
            new("task-alignment", "Implementation Review", "nav-icon-task-alignment", f => f.ImplementationReview,
                Hint: "tasks checked against the specification"),
            new("implementation-traceability", "Implementation Traceability", "nav-icon-task-alignment", f => f.ImplementationTraceability,
                Hint: "work items linked to pull requests and changed files"),
            new("source-analysis", "Source Analysis", "nav-icon-source-analysis", f => f.SourceAnalysis),
            new("technology-coverage", "Technology Coverage", "nav-icon-source-analysis", Always),
            // The page is the Azure provider of environment analysis; the menu stays generic and names the provider in the status.
            new("azure-environment", "Environment Analysis", "nav-icon-source-analysis", f => f.AzureEnvironmentAnalysis,
                ReviewId: "azure-environment", Hint: "read-only cloud environment inventory (provider: Azure)", ProviderWhenApplicable: "Azure", FeatureId: "environment-analysis"),
        ]),
        new("quality", "Quality", f => f.ShowSectionQuality,
        [
            new("quality-review", "Quality Review", "nav-icon-qa-auditor", f => f.QualityReview, ReviewId: "quality-review"),
            new("frontend-quality-review", "Frontend Quality Review", "nav-icon-constitution-compliance", f => f.FrontendQualityReview, ReviewId: "frontend-quality-review"),
            new("api-quality-review", "API Quality Review", "nav-icon-constitution-compliance", f => f.ApiQualityReview, ReviewId: "api-quality-review"),
            new("integration-quality-review", "Integration Quality Review", "nav-icon-constitution-compliance", f => f.IntegrationQualityReview, ReviewId: "integration-quality-review"),
            new("performance-test-review", "Performance Test Review", "nav-icon-constitution-compliance", Always, ReviewId: "performance-test-review"),
            new("security-classification-review", "Security Classification", "nav-icon-constitution-compliance", Always, ReviewId: "security-classification-review"),
            new("dependency-review", "Dependency Review", "nav-icon-constitution-compliance", Always, ReviewId: "dependency-review"),
            new("pipeline-review", "Pipeline Review", "nav-icon-task-alignment", Always, ReviewId: "pipeline-review"),
            new("critical-e2e-regression", "Critical E2E Regression", "nav-icon-constitution-compliance", f => f.CriticalE2ERegression, ReviewId: "critical-e2e-regression"),
        ]),
        new("ai-review", "AI REVIEW", f => f.ShowSectionAiReview,
        [
            new("ai-change-auditor", "AI Change Review", "nav-icon-ai-auditor", f => f.AiChangeReview, OptionalByDefault: true),
        ]),
        new("admin", "Admin", f => f.ShowSectionAdmin,
        [
            new("admin/system-settings", "System Settings", "nav-icon-settings", f => f.AdminSystemSettings),
        ]),
    ];

    /// <summary>The section holding the row for <paramref name="relativePath"/> (base-relative, no query), if any.</summary>
    public static NavSection? SectionFor(string relativePath) =>
        Sections.FirstOrDefault(s => s.Items.Any(i => i.IsCurrent(relativePath)));

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
