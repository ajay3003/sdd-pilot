using BirkNext.Web.Pages.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Feature &amp; Menu Visibility presents the sidebar's taxonomy; counts and Menu Synchronization derive from category membership.</summary>
public partial class SystemSettingsEnvironmentDiagnosticsTests
{
    // Mirrors AdminService's Platform/Core/Advanced keys and labels; values as a typical installation (Impact Analysis and AI off).
    private static readonly (string Key, string Label, bool Value)[] PlatformFeatures =
        [("Dashboard", "Dashboard", true), ("UserGuide", "User Guide", true), ("RecommendedWorkflow", "Recommended Workflow", true), ("AdminSystemSettings", "System Settings", true)];

    private static readonly (string Key, string Label, bool Value)[] CoreFeatures =
    [
        ("SpecificationExplorer", "Specification Explorer", true), ("ConstitutionExplorer", "Constitution Explorer", true),
        ("DataModelExplorer", "Data Model Explorer", true), ("PlanExplorer", "Plan Explorer", true), ("TaskExplorer", "Task Explorer", true),
        ("QaArtifactLibrary", "QA Artifact Library", true), ("SampleProjects", "Sample Projects", true),
        ("TraceabilityCoverage", "Traceability & Coverage", true), ("ArtifactTraceability", "Requirements Traceability", true),
        ("QualityReview", "Document Quality Review", true), ("FrontendQualityReview", "Frontend Quality Review", true),
        ("ApiQualityReview", "API Quality Review", true), ("IntegrationQualityReview", "Integration Quality Review", true),
        ("CriticalE2ERegression", "Critical E2E Regression", true), ("BlazorWasmSecurityReview", "WASM Security Review", true),
        ("BlazorWasmPerformanceReview", "WASM Performance Review", true), ("ImplementationReview", "Task Alignment Review", true),
        ("ImplementationTraceability", "Change Traceability", true), ("SourceAnalysis", "Source Analysis", true),
        ("AzureEnvironmentAnalysis", "Azure Environment Analysis", true), ("GeneratedDocumentationHealth", "Generated Documentation Health", true),
    ];

    private static readonly (string Key, string Label, bool Value)[] AdvancedFeatures =
    [
        ("EnableExtractionReview", "Extraction Review", false), ("EnableArchitectureView", "Architecture View", false),
        ("LegacyTraceabilityNavigationEnabled", "Legacy Traceability Navigation", false), ("TraceabilitySuggestions", "Traceability Suggestions", true),
        ("CodeTraceability", "Code Traceability", true), ("ImpactAnalysis", "Impact Analysis", false),
        ("SpecDrift", "Documentation Drift", true), ("AiChangeReview", "AI Change Review", false),
    ];

    private static string Entries((string Key, string Label, bool Value)[] features, bool locked = false) =>
        string.Join(",", features.Select(f => $$"""{ "key": "{{f.Key}}", "label": "{{f.Label}}", "value": {{(f.Value ? "true" : "false")}}, "locked": {{(locked ? "true" : "false")}} }"""));

    private IRenderedComponent<SystemSettings> RenderFeatureVisibility()
    {
        _handler.EditableJson = $$"""
            {
              "featureVisibility": { "platform": [{{Entries(PlatformFeatures, locked: true)}}], "core": [{{Entries(CoreFeatures)}}], "advanced": [{{Entries(AdvancedFeatures)}}] },
              "logging": { "minimumLevel": "Information", "seqUrl": "" },
              "admin": { "showDiagnostics": true }
            }
            """;
        var cut = Render<SystemSettings>();
        cut.WaitForAssertion(() => FindButton(cut, "Feature Visibility").Should().NotBeNull());
        cut.FindAll(".ss-nav-item").First(b => b.TextContent.Contains("Feature Visibility")).Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=fv-section-document-review]").Should().ContainSingle());
        return cut;
    }

    private static IReadOnlyList<string> SectionLabels(IRenderedComponent<SystemSettings> cut, string category) =>
        cut.FindAll($"[data-testid=fv-section-{category}] .fv-label").Select(l => l.TextContent.Trim()).ToList();

    [Fact]
    public void FeatureVisibility_groups_features_like_the_sidebar()
    {
        var cut = RenderFeatureVisibility();

        cut.FindAll(".fv-sections-grid .fv-section-label").Select(l => l.TextContent.Trim()).Should().StartWith(
            ["Document Review", "Library", "Traceability", "Source Review", "Analysis", "Quality & Testing", "AI Review", "Advanced Features", "Admin"]);
        SectionLabels(cut, "document-review").Should().Equal(
            "Specification Explorer", "Constitution Explorer", "Data Model Explorer", "Plan Explorer", "Task Explorer",
            "Document Quality Review", "Task Alignment Review", "Generated Documentation Health", "Documentation Drift");
        SectionLabels(cut, "traceability").Should().Equal("Requirements Traceability", "Change Traceability");
        SectionLabels(cut, "source-review").Should().Equal("Source Analysis", "Azure Environment Analysis");
        SectionLabels(cut, "analysis").Should().Equal("Impact Analysis");
        SectionLabels(cut, "quality-testing").Should().NotContain("Document Quality Review");

        // Each configured label appears once across the whole page's sections.
        cut.FindAll(".fv-sections-grid .fv-label").Select(l => l.TextContent.Trim()).Should().OnlyHaveUniqueItems();
        // Retired features stay retired and outside the categories.
        cut.FindAll(".fv-retired-label").Select(l => l.TextContent.Trim()).Should().Equal("Spec Comparison", "Specification Deltas", "Task Deltas");
    }

    [Fact]
    public void FeatureVisibility_counts_are_derived_from_category_membership_and_preserve_stored_values()
    {
        var cut = RenderFeatureVisibility();
        var values = CoreFeatures.Concat(AdvancedFeatures).ToDictionary(f => f.Key, f => f.Value);

        foreach (var category in FeatureVisibilityTaxonomy.Categories)
        {
            var members = category.Keys.Where(values.ContainsKey).ToList();
            var chip = cut.Find($"[data-testid=fv-count-{category.Id}]");
            chip.QuerySelector(".fv-count-group")!.TextContent.Should().Be(category.Label);
            chip.QuerySelector(".fv-count-on")!.TextContent.Should().Be($"{members.Count(k => values[k])} enabled");
            chip.QuerySelector(".fv-count-off")!.TextContent.Should().Be($"{members.Count(k => !values[k])} disabled");
        }
        // Spot-check the fixture: moved Documentation Drift stays enabled; Impact Analysis stays disabled.
        cut.Find("[data-testid=fv-count-document-review] .fv-count-on").TextContent.Should().Be("9 enabled");
        cut.Find("[data-testid=fv-count-analysis] .fv-count-off").TextContent.Should().Be("1 disabled");
        cut.FindAll("[data-testid=fv-section-analysis] .fv-badge-disabled").Should().ContainSingle();
    }

    [Fact]
    public void FeatureVisibility_menu_synchronization_stays_in_sync()
    {
        var cut = RenderFeatureVisibility();
        var configured = CoreFeatures.Concat(AdvancedFeatures).ToList();

        var sync = cut.Find(".fv-section-sync");
        sync.QuerySelector(".fv-section-hint")!.TextContent.Should().Be("In sync");
        sync.QuerySelectorAll(".fv-sync-warn-row").Should().BeEmpty();
        string Stat(string label) => sync.QuerySelectorAll(".fv-sync-stat").Single(s => s.QuerySelector(".fv-sync-stat-label")!.TextContent == label)
            .QuerySelector(".fv-sync-stat-value")!.TextContent;
        Stat("Configured").Should().Be(configured.Count.ToString());
        Stat("Visible").Should().Be(configured.Count(f => f.Value).ToString());
        Stat("Hidden").Should().Be(configured.Count(f => !f.Value).ToString());
        Stat("Menu Items").Should().Be(FeatureVisibilityTaxonomy.MenuItems.Count.ToString());
    }

    [Fact]
    public void FeatureVisibility_toggling_a_moved_feature_saves_its_stable_key_only()
    {
        var cut = RenderFeatureVisibility();
        FindButton(cut, "Edit Settings")!.Click();

        cut.Find("[data-testid=fv-section-document-review] label[aria-label='Disable Documentation Drift'] input").Change(false);
        cut.Find("[data-testid=fv-count-document-review] .fv-count-off").TextContent.Should().Be("1 disabled");
        FindButton(cut, "Save Settings")!.Click();

        cut.WaitForAssertion(() => _handler.LastSaveBody.Should().NotBeNull());
        _handler.LastSaveBody.Should().Contain("\"SpecDrift\":false", "the stable key is unchanged by the new category")
            .And.NotContain("DocumentationDrift").And.NotContain("ImplementationReview");
    }
}
