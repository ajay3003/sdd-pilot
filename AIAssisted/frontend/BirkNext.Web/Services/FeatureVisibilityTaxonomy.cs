namespace BirkNext.Web.Services;

// ── Feature & Menu Visibility taxonomy ──────────────────────────────────────────────────────────────────────────────────
//
// System Settings › Feature & Menu Visibility groups the configurable feature keys into the same categories as the sidebar
// (NavigationCatalog). A category is presentation only: it is never persisted, never part of a feature key and never a
// workflow prerequisite. Moving a key between categories therefore keeps its stored enabled/disabled value. The feature keys
// are the stable configuration keys (appsettings FeatureVisibility:*), which can differ from the display name
// (ImplementationReview = Task Alignment Review, ImplementationTraceability = Change Traceability, SpecDrift = Documentation Drift).

/// <summary>One user-facing category. <paramref name="NavSectionId"/> is the sidebar section whose rows these keys gate
/// (null when the keys have no single sidebar section, e.g. Library).</summary>
public sealed record FeatureCategory(string Id, string Label, string? NavSectionId, IReadOnlyList<string> Keys)
{
    public bool Contains(string key) => Keys.Contains(key, StringComparer.OrdinalIgnoreCase);
}

public static class FeatureVisibilityTaxonomy
{
    /// <summary>Categories in sidebar order. Each key belongs to exactly one category.</summary>
    public static IReadOnlyList<FeatureCategory> Categories { get; } =
    [
        // What do the documents say, how good are they, how do they align, and how do they compare?
        new("document-review", "Document Review", "document-review",
        [
            "SpecificationExplorer", "ConstitutionExplorer", "DataModelExplorer", "PlanExplorer", "TaskExplorer",
            "QualityReview", "ImplementationReview", "GeneratedDocumentationHealth", "SpecDrift",
        ]),
        new("library", "Library", null, ["QaArtifactLibrary", "SampleProjects"]),
        // How are lifecycle artifacts and tracked delivery changes linked?
        new("traceability", "Traceability", "traceability", ["ArtifactTraceability", "ImplementationTraceability"]),
        // What does the source/implementation-oriented evidence say?
        new("source-review", "Source Review", "source-review", ["SourceAnalysis", "GeneratedDocumentationExplorer", "AzureEnvironmentAnalysis"]),
        new("analysis", "Analysis", "analysis", ["ImpactAnalysis"]),
        // How do we assess or test the solution?
        new("quality-testing", "Quality & Testing", "quality-testing",
        [
            "FrontendQualityReview", "ApiQualityReview", "IntegrationQualityReview", "CriticalE2ERegression", "BlazorWasmPerformanceReview",
        ]),
        new("ai-review", "AI Review", "ai-review", ["AiChangeReview"]),
    ];

    /// <summary>Optional/legacy capabilities shown in their own Advanced Features group (unchanged by the taxonomy).</summary>
    public static IReadOnlyList<string> AdvancedKeys { get; } =
    [
        "LegacyTraceabilityNavigationEnabled", "TraceabilityCoverage", "TraceabilitySuggestions", "CodeTraceability",
        "EnableArchitectureView", "EnableExtractionReview",
    ];

    /// <summary>The category of a stable feature key, or null when the key is advanced, platform or not listed.</summary>
    public static FeatureCategory? CategoryOf(string key) => Categories.FirstOrDefault(c => c.Contains(key));

    /// <summary>Configured entries of a category, in the category's order. Each entry keeps its own stored value.</summary>
    public static IReadOnlyList<FeatureEntryDto> EntriesOf(FeatureCategory category, IEnumerable<FeatureEntryDto> configured)
    {
        var byKey = configured.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return category.Keys.Where(byKey.ContainsKey).Select(k => byKey[k]).ToList();
    }

    /// <summary>
    /// Sidebar rows that carry a feature key, for the Menu Synchronization check: each label is the sidebar label and each key
    /// its visibility key. Platform rows (always on) are included so the total matches the sidebar.
    /// </summary>
    public static IReadOnlyList<(string Label, string Key)> MenuItems { get; } =
    [
        // Getting Started (Platform — always on)
        ("Recommended Workflow",           "RecommendedWorkflow"),
        ("User Guide",                     "UserGuide"),
        ("Dashboard",                      "Dashboard"),

        // Document Review
        ("Specification Explorer",         "SpecificationExplorer"),
        ("Constitution Explorer",          "ConstitutionExplorer"),
        ("Data Model Explorer",            "DataModelExplorer"),
        ("Plan Explorer",                  "PlanExplorer"),
        ("Task Explorer",                  "TaskExplorer"),
        ("Document Quality Review",        "QualityReview"),
        ("Task Alignment Review",          "ImplementationReview"),
        ("Generated Documentation Health", "GeneratedDocumentationHealth"),
        ("Documentation Drift",            "SpecDrift"),

        // Library
        ("Sample Projects",                "SampleProjects"),

        // Traceability
        ("Requirements Traceability",      "ArtifactTraceability"),
        ("Change Traceability",            "ImplementationTraceability"),
        // Legacy traceability — shown only when LegacyTraceabilityNavigationEnabled
        ("Traceability & Coverage",        "TraceabilityCoverage"),
        ("Traceability Suggestions",       "TraceabilitySuggestions"),
        ("Code Traceability",              "CodeTraceability"),

        // Source Review
        ("Source Analysis",                "SourceAnalysis"),
        ("Generated Documentation Explorer", "GeneratedDocumentationExplorer"),
        ("Azure Environment Analysis",     "AzureEnvironmentAnalysis"),

        // Analysis
        ("Impact Analysis",                "ImpactAnalysis"),

        // Quality & Testing
        ("Frontend Quality Review",        "FrontendQualityReview"),
        ("API Quality Review",             "ApiQualityReview"),
        ("Integration Quality Review",     "IntegrationQualityReview"),
        ("Critical E2E Regression",        "CriticalE2ERegression"),
        ("WASM Performance Review",        "BlazorWasmPerformanceReview"),

        // AI Review
        ("AI Change Review",               "AiChangeReview"),

        // Admin (Platform — always on)
        ("System Settings",                "AdminSystemSettings"),
    ];
}
