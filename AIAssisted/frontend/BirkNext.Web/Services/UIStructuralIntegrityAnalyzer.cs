using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IUIStructuralIntegrityAnalyzer
{
    UIStructuralIntegrityReport Analyze();
}

public sealed class UIStructuralIntegrityAnalyzer : IUIStructuralIntegrityAnalyzer
{
    private static readonly IReadOnlyList<UIStructuralFinding> _registry = BuildRegistry();

    public UIStructuralIntegrityReport Analyze() => new() { PageResults = _registry };

    // ── Registry ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<UIStructuralFinding> BuildRegistry() =>
    [
        // ── Fully Consistent ──────────────────────────────────────────────────
        // All sections use only shared components. No page-specific CSS replaces
        // or wraps shared component boundaries.

        P("Compare Review Detail", "/compare/reviews/{id}",
            S(UIPageSection.PageHeader,   UISectionStatus.Consistent),
            S(UIPageSection.ContentCards, UISectionStatus.Consistent),
            S(UIPageSection.EmptyStates,  UISectionStatus.Consistent)),

        P("Artifact Traceability", "/artifact-traceability",
            S(UIPageSection.PageHeader,      UISectionStatus.Consistent),
            S(UIPageSection.ContentCards,    UISectionStatus.Consistent),
            S(UIPageSection.Recommendations, UISectionStatus.Consistent)),

        // ── Legacy Contaminated — HYBRID sections detected ────────────────────
        // At least one section mixes shared components with custom div structures.

        P("WASM Security Review", "/wasm-security-review",
            S(UIPageSection.PageHeader,   UISectionStatus.Consistent),
            S(UIPageSection.ContentCards, UISectionStatus.Hybrid,
                H("WASM Security Review", "Content Cards",
                    "Custom div panel + shared action buttons",
                    ".wsr-target-card div wraps SecondaryButton and PrimaryButton — custom container replaces Card component.",
                    "wsr-target-card, wsr-target-header",
                    "Card"),
                H("WASM Security Review", "Content Cards",
                    "Legacy badge span + missing StatusChip",
                    ".wsr-env-badge span renders environment type without using StatusChip.",
                    "wsr-env-badge",
                    "StatusChip")),
            S(UIPageSection.InputFilterPanels, UISectionStatus.PartiallyMigrated)),

        P("WASM Performance Review", "/wasm-performance-review",
            S(UIPageSection.PageHeader,   UISectionStatus.Consistent),
            S(UIPageSection.ContentCards, UISectionStatus.Hybrid,
                H("WASM Performance Review", "Content Cards",
                    "Custom div panel + shared action buttons",
                    ".wpr-target-card div wraps shared button components — custom container replaces Card component.",
                    "wpr-target-card, wpr-target-header",
                    "Card")),
            S(UIPageSection.KpiMetrics,        UISectionStatus.PartiallyMigrated),
            S(UIPageSection.InputFilterPanels, UISectionStatus.PartiallyMigrated)),

        P("Task Deltas", "/task-deltas",
            S(UIPageSection.PageHeader,   UISectionStatus.Legacy),
            S(UIPageSection.KpiMetrics,   UISectionStatus.Legacy),
            S(UIPageSection.ContentCards, UISectionStatus.Legacy),
            S(UIPageSection.InputFilterPanels, UISectionStatus.Hybrid,
                H("Task Deltas", "Input / Filter Panels",
                    "Legacy filter chips + shared component coexistence",
                    ".delta-filter-chip custom spans used alongside imported shared ButtonGroup elements, creating a mixed filter panel.",
                    "delta-filter-chip, delta-filter-bar",
                    "BadgeList or shared filter chips"))),

        P("Implementation Review", "/task-alignment",
            S(UIPageSection.PageHeader,   UISectionStatus.Legacy),
            S(UIPageSection.KpiMetrics,   UISectionStatus.Legacy),
            S(UIPageSection.ContentCards, UISectionStatus.Hybrid,
                H("Implementation Review", "Content Cards",
                    "Legacy card div + isolated shared component imports",
                    ".align-card custom divs host imported shared button components without using Card wrapper — hybrid container pattern.",
                    "align-card, align-body",
                    "Card")),
            S(UIPageSection.Recommendations, UISectionStatus.Legacy)),

        P("Traceability", "/traceability",
            S(UIPageSection.PageHeader, UISectionStatus.Hybrid,
                H("Traceability", "Page Header",
                    "Legacy hero + PageHeader coexistence",
                    ".traceability-hero custom div block present alongside the page title — hero not replaced by PageHeader component.",
                    "traceability-hero, tx-page-header div",
                    "PageHeader")),
            S(UIPageSection.ContentCards,     UISectionStatus.Legacy),
            S(UIPageSection.InputFilterPanels, UISectionStatus.Legacy)),

        P("Impact Analysis", "/impact-analysis",
            S(UIPageSection.PageHeader, UISectionStatus.PartiallyMigrated),
            S(UIPageSection.KpiMetrics, UISectionStatus.Hybrid,
                H("Impact Analysis", "KPI / Metrics",
                    "KPI strip hybrid rendering",
                    ".impact-kpi-* custom grid renders score values alongside possible MetricCard usage — duplicates KPI strip behavior.",
                    "impact-kpi-grid, impact-kpi-card, impact-kpi-value",
                    "MetricCard")),
            S(UIPageSection.ContentCards, UISectionStatus.Legacy)),

        P("Spec Drift", "/spec-drift",
            S(UIPageSection.PageHeader,   UISectionStatus.Legacy),
            S(UIPageSection.ContentCards, UISectionStatus.Hybrid,
                H("Spec Drift", "Content Cards",
                    "Legacy card structure + shared component usage",
                    ".drift-card and .drift-finding custom divs coexist with imported shared components — mixed content card rendering.",
                    "drift-card, drift-finding, drift-detail-grid",
                    "Card")),
            S(UIPageSection.InputFilterPanels, UISectionStatus.Legacy)),

        P("Code Traceability", "/code-traceability",
            S(UIPageSection.PageHeader,   UISectionStatus.Legacy),
            S(UIPageSection.ContentCards, UISectionStatus.Hybrid,
                H("Code Traceability", "Content Cards",
                    "Custom split-panel + shared component buttons",
                    ".code-file-panel and .code-impact-panel act as Card replacements while hosting shared action buttons inside them.",
                    "code-file-panel, code-impact-panel, code-trace-layout",
                    "Card")),
            S(UIPageSection.EmptyStates,  UISectionStatus.PartiallyMigrated)),

        P("AI Change Review", "/ai-change-auditor",
            S(UIPageSection.PageHeader, UISectionStatus.Consistent),
            S(UIPageSection.KpiMetrics, UISectionStatus.Hybrid,
                H("AI Change Review", "KPI / Metrics",
                    "KPI strip hybrid rendering",
                    ".qa-score-banner occupies the KPI zone alongside correctly used PageHeader — banner duplicates score display that MetricCard should own.",
                    "qa-score-banner, qa-kpi-grid",
                    "MetricCard")),
            S(UIPageSection.ContentCards,    UISectionStatus.PartiallyMigrated),
            S(UIPageSection.Recommendations, UISectionStatus.PartiallyMigrated)),

        P("Compare Reviews", "/compare/reviews",
            S(UIPageSection.PageHeader, UISectionStatus.Hybrid,
                H("Compare Reviews", "Page Header",
                    "Legacy hero + PageHeader coexistence",
                    ".delta-reviews-hero custom block present; PageHeader not used for the reviews list title.",
                    "delta-reviews-hero",
                    "PageHeader")),
            S(UIPageSection.ContentCards, UISectionStatus.PartiallyMigrated)),

        P("Scenario Extraction", "/extract",
            S(UIPageSection.PageHeader, UISectionStatus.Hybrid,
                H("Scenario Extraction", "Page Header",
                    "Legacy hero + PageHeader coexistence",
                    ".review-wizard-hero occupies the header region; step progress uses .review-step-badge span instead of StatusChip.",
                    "review-wizard-hero, review-step-badge",
                    "PageHeader + StatusChip")),
            S(UIPageSection.ContentCards,     UISectionStatus.PartiallyMigrated),
            S(UIPageSection.InputFilterPanels, UISectionStatus.Consistent)),

        P("Compare Specs", "/compare/specs",
            S(UIPageSection.PageHeader, UISectionStatus.Hybrid,
                H("Compare Specs", "Page Header",
                    "Legacy hero + PageHeader coexistence",
                    ".compare-specs-hero custom block used; no PageHeader component in the header region.",
                    "compare-specs-hero",
                    "PageHeader")),
            S(UIPageSection.ContentCards, UISectionStatus.PartiallyMigrated)),

        P("New Scenario", "/scenarios/new",
            S(UIPageSection.PageHeader, UISectionStatus.Hybrid,
                H("New Scenario", "Page Header",
                    "Legacy hero + PageHeader coexistence",
                    ".create-scenario-hero custom div in header position; PageHeader not used.",
                    "create-scenario-hero",
                    "PageHeader")),
            S(UIPageSection.InputFilterPanels, UISectionStatus.Consistent),
            S(UIPageSection.ContentCards,      UISectionStatus.PartiallyMigrated)),

        P("User Guide", "/user-guide",
            S(UIPageSection.PageHeader, UISectionStatus.Hybrid,
                H("User Guide", "Page Header",
                    "Legacy hero + PageHeader coexistence",
                    ".ug-hero custom block present alongside some shared layout components.",
                    "ug-hero",
                    "PageHeader")),
            S(UIPageSection.ContentCards, UISectionStatus.Legacy)),

        // ── Partially Migrated — PartiallyMigrated sections, no Hybrid ────────

        P("Quality Review", "/quality-review",
            S(UIPageSection.PageHeader,        UISectionStatus.Consistent),
            S(UIPageSection.InputFilterPanels, UISectionStatus.PartiallyMigrated),
            S(UIPageSection.ContentCards,      UISectionStatus.PartiallyMigrated)),

        P("Dashboard", "/dashboard",
            S(UIPageSection.PageHeader,   UISectionStatus.Consistent),
            S(UIPageSection.KpiMetrics,   UISectionStatus.PartiallyMigrated),
            S(UIPageSection.ContentCards, UISectionStatus.PartiallyMigrated)),

        P("System Settings", "/admin/system-settings",
            S(UIPageSection.PageHeader,      UISectionStatus.Consistent),
            S(UIPageSection.ContentCards,    UISectionStatus.PartiallyMigrated),
            S(UIPageSection.InputFilterPanels, UISectionStatus.PartiallyMigrated)),

        P("Traceability Suggestions", "/traceability/suggestions",
            S(UIPageSection.ContentCards,    UISectionStatus.PartiallyMigrated),
            S(UIPageSection.Recommendations, UISectionStatus.PartiallyMigrated)),

        P("Recommended Workflow", "/getting-started",
            S(UIPageSection.PageHeader,   UISectionStatus.PartiallyMigrated),
            S(UIPageSection.ContentCards, UISectionStatus.PartiallyMigrated),
            S(UIPageSection.FooterAuxiliary, UISectionStatus.Consistent)),

        P("Scenarios", "/scenarios",
            S(UIPageSection.PageHeader,        UISectionStatus.Consistent),
            S(UIPageSection.ContentCards,      UISectionStatus.PartiallyMigrated),
            S(UIPageSection.InputFilterPanels, UISectionStatus.PartiallyMigrated)),

        // ── Not Migrated — no shared component adoption ───────────────────────

        P("Home",                   "/"),
        P("Constitution Explorer",  "/constitution-explorer"),
        P("Plan Explorer",          "/plan-explorer"),
        P("Task Explorer",          "/task-explorer"),
    ];

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static UIStructuralFinding P(string name, string route, params UISectionFinding[] sections) =>
        new() { PageName = name, PageRoute = route, Sections = sections };

    private static UISectionFinding S(UIPageSection section, UISectionStatus status, params UIHybridPatternDetection[] patterns) =>
        new() { Section = section, Status = status, Patterns = patterns };

    private static UIHybridPatternDetection H(
        string pageName, string sectionName, string patternType,
        string description, string? legacy = null, string? shared = null) =>
        new()
        {
            PageName      = pageName,
            SectionName   = sectionName,
            PatternType   = patternType,
            Description   = description,
            LegacyElement = legacy,
            SharedElement = shared
        };
}
