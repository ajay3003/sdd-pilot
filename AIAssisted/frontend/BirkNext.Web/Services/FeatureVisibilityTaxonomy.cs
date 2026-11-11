namespace BirkNext.Web.Services;

// ── Feature Visibility declarations ─────────────────────────────────────────────────────────────────────────────────────
//
// The sidebar (NavigationCatalog) is the one registry of what a user can navigate to, and the backend's AdminService lists are the one
// registry of configurable switches (FeatureVisibility:* keys). Which entries a switch gates is never written down by hand: it is derived by
// evaluating the sidebar's own visibility rules (FeatureVisibilityInventory). This file only declares what cannot be derived from code:
// switches that intentionally gate no sidebar entry and why, the lifecycle of optional and legacy switches, retired keys, and pages that are
// reached from other pages instead of the sidebar. Declarations are checked against the code at runtime, so a stale one becomes an issue.

/// <summary>Lifecycle of a switch, from the code and the decisions recorded in it (not from whether a route exists).</summary>
public enum FeatureLifecycle
{
    /// <summary>A working page that is part of the normal product.</summary>
    Implemented,
    /// <summary>Working, but off by default because it depends on an optional integration or is not part of the normal workflow.</summary>
    Optional,
    /// <summary>Kept for compatibility; a newer page replaces it.</summary>
    Legacy,
    /// <summary>The switch exists in configuration but no page or sidebar entry reads it.</summary>
    NoEffect,
}

/// <summary>What the code cannot say about one switch. <paramref name="MasterSwitch"/>: gates other switches' entries rather than its own.</summary>
public sealed record FeatureDeclaration(string Key, FeatureLifecycle Lifecycle, string Note, string? Successor = null, bool MasterSwitch = false);

/// <summary>A page that is intentionally not a sidebar entry. <paramref name="Of"/> is the sidebar route it belongs to (null for infrastructure).</summary>
public sealed record ContextualRoute(string Template, ContextualRouteKind Kind, string? Of, string Note);

public enum ContextualRouteKind { Alias, Redirect, Contextual, Infrastructure }

/// <summary>A key that was removed from the product; its configuration value is ignored.</summary>
public sealed record RetiredFeature(string Key, string Label, string Now);

public static class FeatureVisibilityTaxonomy
{
    /// <summary>Switches that are not plain implemented pages. Every other configured switch is <see cref="FeatureLifecycle.Implemented"/>.</summary>
    public static IReadOnlyList<FeatureDeclaration> Declarations { get; } =
    [
        new("ImplementationTraceability", FeatureLifecycle.Optional,
            "Depends on the optional Azure DevOps integration and never counts toward workflow, coverage or readiness, so it is off by default."),
        new("ImpactAnalysis", FeatureLifecycle.Optional, "Off by default. Release Readiness and Relationship Review use the shared canonical relations.",
            Successor: "release-readiness"),
        new("AiChangeReview", FeatureLifecycle.Optional, "Off by default in every shipped configuration."),
        new("LegacyTraceabilityNavigationEnabled", FeatureLifecycle.Legacy,
            "Shows the three legacy traceability entries (Traceability & Coverage, Traceability Suggestions, Code Traceability) when their own switches are on too.",
            Successor: "artifact-traceability", MasterSwitch: true),
        new("TraceabilityCoverage", FeatureLifecycle.Legacy, "Renders the same graph as Requirements Traceability.", Successor: "artifact-traceability"),
        new("TraceabilitySuggestions", FeatureLifecycle.Legacy, "Relationship Review holds the shared review decisions.", Successor: "correlation-review"),
        new("CodeTraceability", FeatureLifecycle.Legacy, "Change Traceability and Source Analysis cover code links.", Successor: "implementation-traceability"),
        new("QaArtifactLibrary", FeatureLifecycle.NoEffect, "No page or sidebar entry reads this switch."),
        new("BlazorWasmSecurityReview", FeatureLifecycle.NoEffect, "Its separate page was retired; no page reads this switch."),
        new("BlazorWasmPerformanceReview", FeatureLifecycle.NoEffect,
            "There is no separate page; Frontend Quality Review runs the performance checks.", Successor: "frontend-quality-review"),
        new("EnableExtractionReview", FeatureLifecycle.NoEffect, "Switches a mode of a component that no page shows."),
        new("EnableArchitectureView", FeatureLifecycle.NoEffect, "Switches a mode of a component that no page shows.", Successor: "integration-topology"),
    ];

    /// <summary>Removed keys. They are not listed by the backend and their configuration values are ignored.</summary>
    public static IReadOnlyList<RetiredFeature> Retired { get; } =
    [
        new("SpecComparison", "Spec Comparison", "Documentation Drift → Changes (the old /compare links redirect there)"),
        new("SpecificationDeltas", "Specification Deltas", "Documentation Drift"),
        new("TaskDeltas", "Task Deltas", "Task Explorer → Changes (the old /task-deltas link redirects there)"),
    ];

    /// <summary>Pages with no sidebar entry, by design. Every page must be a sidebar entry or listed here (checked by the inventory).</summary>
    public static IReadOnlyList<ContextualRoute> ContextualRoutes { get; } =
    [
        new("", ContextualRouteKind.Redirect, "dashboard", "The start address opens Project Quality."),
        new("authentication/{action}", ContextualRouteKind.Infrastructure, null, "Microsoft sign-in callback."),
        new("ai-generated-code-review", ContextualRouteKind.Alias, "code-risk-review", "Earlier address of Code Risk Review."),
        new("change-traceability", ContextualRouteKind.Alias, "implementation-traceability", "Alternative address of Change Traceability."),
        new("quality-review", ContextualRouteKind.Alias, "quality/document", "Earlier address of Document Quality Review."),
        new("sample-projects", ContextualRouteKind.Alias, "demo-projects", "Earlier address of Demo Projects."),
        new("spec-drift", ContextualRouteKind.Alias, "documentation-drift", "Earlier address of Documentation Drift."),
        new("task-alignment-review", ContextualRouteKind.Alias, "task-alignment", "Alternative address of Task Alignment Review."),
        new("traceability-suggestions", ContextualRouteKind.Alias, "traceability/suggestions", "Earlier address of Traceability Suggestions."),
        new("compare", ContextualRouteKind.Redirect, "documentation-drift", "Retired Spec Comparison; redirects to Documentation Drift."),
        new("compare/reviews", ContextualRouteKind.Redirect, "documentation-drift", "Retired list; redirects to Documentation Drift."),
        new("compare/reviews/{id}", ContextualRouteKind.Contextual, "documentation-drift", "Detail of one stored comparison review, opened from its list."),
        new("task-deltas", ContextualRouteKind.Redirect, "task-explorer", "Retired Task Deltas; redirects to Task Explorer → Changes."),
        new("quality/source", ContextualRouteKind.Redirect, "source-analysis", "Earlier source quality page; redirects to Source Analysis."),
        new("source-analysis/application-messaging", ContextualRouteKind.Contextual, "source-analysis", "Application messaging detail, opened from Source Analysis."),
        new("implementation-review", ContextualRouteKind.Contextual, "task-explorer",
            "Implementation Evidence Review, opened from Task Explorer and requirement evidence."),
    ];

    public static FeatureDeclaration? Declaration(string key) =>
        Declarations.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static FeatureLifecycle Lifecycle(string key) => Declaration(key)?.Lifecycle ?? FeatureLifecycle.Implemented;

    public static string LifecycleLabel(FeatureLifecycle lifecycle) => lifecycle switch
    {
        FeatureLifecycle.Optional => "Optional",
        FeatureLifecycle.Legacy => "Legacy",
        FeatureLifecycle.NoEffect => "No effect",
        _ => "Implemented",
    };
}
