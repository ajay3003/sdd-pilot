using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Routing;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The configured switches as the backend lists them (AdminService Platform/Core/Advanced, labels included) with the shipped appsettings.json
/// values. Written out here, independently of the frontend, so the inventory is checked against the backend's lists, not against itself.
/// </summary>
public static class FeatureVisibilityFixture
{
    public static readonly (string Key, string Label)[] Platform =
        [("Dashboard", "Dashboard"), ("UserGuide", "User Guide"), ("RecommendedWorkflow", "Recommended Workflow"), ("AdminSystemSettings", "System Settings")];

    public static readonly (string Key, string Label, bool Value)[] Core =
    [
        ("SpecificationExplorer", "Specification Explorer", true), ("ConstitutionExplorer", "Constitution Explorer", true),
        ("DataModelExplorer", "Data Model Explorer", true), ("PlanExplorer", "Plan Explorer", true), ("TaskExplorer", "Task Explorer", true),
        ("SampleProjects", "Sample Projects", true),
        ("TraceabilityCoverage", "Traceability & Coverage", true), ("ArtifactTraceability", "Requirements Traceability", true),
        ("QualityReview", "Document Quality Review", true), ("FrontendQualityReview", "Frontend Quality Review", true),
        ("ApiQualityReview", "API Quality Review", true), ("IntegrationQualityReview", "Integration Quality Review", true),
        ("CriticalE2ERegression", "Critical E2E Regression", true), ("ImplementationReview", "Task Alignment Review", true),
        ("ImplementationTraceability", "Change Traceability", false), ("SourceAnalysis", "Source Analysis", true),
        ("AzureEnvironmentAnalysis", "Azure Environment Analysis", true), ("GeneratedDocumentationHealth", "Generated Documentation Health", true),
        ("GeneratedDocumentationExplorer", "Generated Documentation Explorer", true),
    ];

    public static readonly (string Key, string Label, bool Value)[] Advanced =
    [
        ("LegacyTraceabilityNavigationEnabled", "Legacy Traceability Navigation", false), ("TraceabilitySuggestions", "Traceability Suggestions", false),
        ("CodeTraceability", "Code Traceability", false), ("ImpactAnalysis", "Impact Analysis", false),
        ("SpecDrift", "Documentation Drift", true), ("AiChangeReview", "AI Change Review", false),
    ];

    public static EditableFeatureVisibilityDto Settings(Dictionary<string, bool>? overrides = null, bool withAdvanced = true) => new()
    {
        Platform = [.. Platform.Select(p => new FeatureEntryDto { Key = p.Key, Label = p.Label, Value = true, Locked = true })],
        Core = [.. Core.Select(c => Entry(c, overrides))],
        Advanced = withAdvanced ? [.. Advanced.Select(c => Entry(c, overrides))] : [],
    };

    private static FeatureEntryDto Entry((string Key, string Label, bool Value) c, Dictionary<string, bool>? overrides) =>
        new() { Key = c.Key, Label = c.Label, Value = overrides?.TryGetValue(c.Key, out var v) == true ? v : c.Value };
}

/// <summary>
/// Feature Visibility's inventory and Menu Synchronization are derived from the real sidebar rules and compared by identity. Fixtures follow
/// the task's A–R list where they apply; each expectation is stated independently (routes, labels and keys written out, not read back).
/// </summary>
public sealed class FeatureVisibilityInventoryTests
{
    private static FeatureInventory Real(Dictionary<string, bool>? overrides = null) => FeatureVisibilityInventory.Build(FeatureVisibilityFixture.Settings(overrides));

    private static SidebarEntry Entry(FeatureInventory inv, string route) => inv.Entries.Single(e => e.Route == route);

    // ── Real installation (shipped configuration) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shipped_configuration_is_consistent_and_its_counts_have_defined_units()
    {
        var inv = Real();

        inv.Issues.Should().BeEmpty("every entry opens a page, every switch with an effect gates an entry, every page is in the sidebar or classified");
        inv.SwitchCount.Should().Be(25, "19 core + 6 advanced configurable switches (platform keys excluded; 5 no-effect switches retired)");
        inv.Switches.Where(s => !s.Platform).Should().OnlyContain(s => s.HasEffect, "every configurable switch gates a sidebar entry");
        inv.PlatformCount.Should().Be(4);
        inv.Entries.Should().HaveCount(53, "sidebar rows, including deep links and shared entries");
        inv.HiddenEntries.Should().Be(6);
        inv.Entries.Where(e => !e.Shown).Select(e => e.Route).Should().BeEquivalentTo(
            "implementation-traceability", "impact-analysis", "traceability", "traceability/suggestions", "code-traceability", "ai-change-auditor");
        inv.AlwaysShownEntries.Should().Be(20);
    }

    [Theory] // A: a switch maps to exactly the entry it gates
    [InlineData("SpecificationExplorer", "specification-explorer")]
    [InlineData("ImplementationReview", "task-alignment")]
    [InlineData("SpecDrift", "documentation-drift")]
    [InlineData("AzureEnvironmentAnalysis", "azure-environment")]
    [InlineData("GeneratedDocumentationExplorer", "generated-documentation-explorer")]
    public void A_switch_gates_its_own_entry(string key, string route)
    {
        var inv = Real();
        Entry(inv, route).PrimaryKey.Should().Be(key);
        inv.Switch(key)!.GatedRoutes.Should().Contain(route);
    }

    [Fact] // B, C: disabled by configuration → hidden with the switch named as the reason
    public void A_switch_that_is_off_hides_its_entry_and_names_itself_as_the_reason()
    {
        var inv = Real();
        Entry(inv, "impact-analysis").Shown.Should().BeFalse();
        Entry(inv, "impact-analysis").HiddenReason.Should().Be("Off: Impact Analysis");
        Entry(inv, "implementation-traceability").HiddenReason.Should().Be("Off: Change Traceability");
        Real(new() { ["ImpactAnalysis"] = true }).Entries.Single(e => e.Route == "impact-analysis").Shown.Should().BeTrue();
    }

    [Fact] // a switch that is on but hidden by another one is reported, not counted as shown
    public void A_legacy_entry_whose_own_switch_is_on_is_hidden_by_the_master_switch_and_says_so()
    {
        var coverage = Entry(Real(), "traceability");
        coverage.RequiredKeys.Should().BeEquivalentTo("LegacyTraceabilityNavigationEnabled", "TraceabilityCoverage");
        coverage.PrimaryKey.Should().Be("TraceabilityCoverage");
        coverage.Shown.Should().BeFalse();
        coverage.HiddenReason.Should().Be("Off: Legacy Traceability Navigation");
    }

    [Fact] // D: implemented but runtime-unavailable is shown, never counted hidden
    public void An_unavailable_capability_stays_shown_and_carries_its_availability()
    {
        var environment = Entry(Real(), "azure-environment");
        environment.Shown.Should().BeTrue();
        environment.Availability!.IsUnavailable.Should().BeTrue();
    }

    [Fact] // G: switches without any page were retired from the configurable catalog; a backend that still lists one is an issue
    public void Switches_without_a_page_are_retired_and_reported_if_still_configurable()
    {
        FeatureVisibilityTaxonomy.Retired.Select(r => r.Key).Should().Contain(
            ["QaArtifactLibrary", "BlazorWasmSecurityReview", "BlazorWasmPerformanceReview", "EnableExtractionReview", "EnableArchitectureView"]);
        var old = FeatureVisibilityFixture.Settings();
        old.Core.Add(new FeatureEntryDto { Key = "QaArtifactLibrary", Label = "QA Artifact Library", Value = true });
        var inv = FeatureVisibilityInventory.Build(old);
        inv.Issues.Should().Contain(i => i.Kind == "Retired switch still configurable" && i.Subject == "QA Artifact Library")
            .And.Contain(i => i.Kind == "Switch gates nothing" && i.Subject == "QA Artifact Library");
    }

    [Fact] // H: one entry shared by several switches
    public void Import_Project_is_shown_while_either_Demo_Projects_or_Source_Analysis_is_on()
    {
        Entry(Real(), "project-import").AnyOfKeys.Should().BeEquivalentTo("SampleProjects", "SourceAnalysis");
        Entry(Real(new() { ["SampleProjects"] = false }), "project-import").Shown.Should().BeTrue();
        var both = Entry(Real(new() { ["SampleProjects"] = false, ["SourceAnalysis"] = false }), "sources");
        both.Shown.Should().BeFalse();
        both.HiddenReason.Should().Contain("Sample Projects").And.Contain("Source Analysis");
    }

    [Theory] // I: always-visible entries have no switch
    [InlineData("schema-studio")]
    [InlineData("execution-results")]
    [InlineData("performance-test-review")]
    [InlineData("extensions/skolenaervaer")]
    public void Entries_without_a_switch_are_always_shown(string route)
    {
        var entry = Entry(Real(), route);
        entry.AlwaysShown.Should().BeTrue();
        entry.Shown.Should().BeTrue();
    }

    [Fact] // M: retired keys and their old addresses
    public void Retired_features_are_listed_and_their_old_addresses_are_classified_redirects()
    {
        FeatureVisibilityTaxonomy.Retired.Select(r => r.Key).Should().Contain(["SpecComparison", "SpecificationDeltas", "TaskDeltas"]);
        var inv = Real();
        inv.OtherPages.Single(p => p.Template == "task-deltas").Kind.Should().Be(ContextualRouteKind.Redirect);
        inv.OtherPages.Single(p => p.Template == "compare").Of.Should().Be("documentation-drift");
        inv.Switches.Should().NotContain(s => s.Key == "TaskDeltas", "a retired key is not a switch");
    }

    [Fact] // contextual page with no sidebar entry (Implementation Evidence Review)
    public void Implementation_Evidence_Review_is_a_classified_contextual_page()
    {
        Real().OtherPages.Single(p => p.Template == "implementation-review").Kind.Should().Be(ContextualRouteKind.Contextual);
    }

    // ── Synthetic sidebars and pages: each inconsistency becomes a named issue ────────────────────────────────────────────

    private static readonly IReadOnlyList<string> Pages = ["specification-explorer", "constitution-explorer", "plan-explorer", "schema-studio"];

    private static NavSection Section(params NavItem[] items) => new("s", "Section", items);
    private static NavItem Row(string route, string label, Func<FeatureVisibilityService, bool> visible) => new(route, label, "i", visible, NavLinkMatch.All);

    private static FeatureInventory Synthetic(EditableFeatureVisibilityDto settings, IReadOnlyList<string> pages, params NavItem[] rows) =>
        FeatureVisibilityInventory.Build(settings, routes: pages, sections: [Section(rows)]);

    private static EditableFeatureVisibilityDto Switches(params string[] keys) =>
        new() { Core = [.. keys.Select(k => new FeatureEntryDto { Key = k, Label = k, Value = true })] };

    [Fact] // K: entry points to a route no page serves
    public void An_entry_without_a_page_is_an_issue()
    {
        var inv = Synthetic(Switches("SpecificationExplorer"), Pages, Row("nowhere", "Nowhere", f => f.SpecificationExplorer));
        inv.Issues.Should().Contain(i => i.Kind == "Entry opens no page" && i.Subject == "Nowhere");
    }

    [Fact] // L: an entry wired to the wrong switch is visible as such — the right switch then gates nothing
    public void An_entry_wired_to_the_wrong_switch_is_found_by_identity()
    {
        var inv = Synthetic(Switches("ConstitutionExplorer", "PlanExplorer"), Pages,
            Row("constitution-explorer", "Constitution Explorer", f => f.PlanExplorer), Row("plan-explorer", "Plan Explorer", f => f.PlanExplorer));
        Entry(inv, "constitution-explorer").RequiredKeys.Should().Equal("PlanExplorer");
        inv.Issues.Should().Contain(i => i.Kind == "Switch gates nothing" && i.Subject == "ConstitutionExplorer");
    }

    [Fact] // N: a new page that is neither in the sidebar nor classified
    public void A_new_page_missing_from_the_sidebar_is_an_issue()
    {
        var inv = Synthetic(Switches("SpecificationExplorer"), [.. Pages, "brand-new-module"], Row("specification-explorer", "Specification Explorer", f => f.SpecificationExplorer));
        inv.Issues.Should().Contain(i => i.Kind == "Page without a sidebar entry" && i.Subject == "/brand-new-module");
    }

    [Fact] // O: a configured switch nothing reads
    public void A_switch_that_gates_nothing_is_an_issue_unless_declared()
    {
        var inv = Synthetic(Switches("SpecificationExplorer", "MadeUpFeature"), Pages, Row("specification-explorer", "Specification Explorer", f => f.SpecificationExplorer));
        inv.Issues.Should().Contain(i => i.Kind == "Switch gates nothing" && i.Subject == "MadeUpFeature");
        inv.Issues.Should().NotContain(i => i.Subject == "SpecificationExplorer");
    }

    [Fact] // P: an entry that no switch combination can show
    public void An_entry_that_can_never_be_shown_is_an_issue()
    {
        var inv = Synthetic(Switches("SpecificationExplorer"), Pages, Row("schema-studio", "Schema Studio", _ => false), Row("specification-explorer", "Specification Explorer", f => f.SpecificationExplorer));
        inv.Issues.Should().Contain(i => i.Kind == "Entry can never be shown" && i.Subject == "Schema Studio");
    }

    [Fact] // J: duplicate identities
    public void Duplicate_entries_and_duplicate_switches_are_issues()
    {
        var settings = Switches("SpecificationExplorer", "SpecificationExplorer");
        var inv = Synthetic(settings, Pages, Row("specification-explorer", "A", f => f.SpecificationExplorer), Row("specification-explorer", "B", f => f.SpecificationExplorer));
        inv.Issues.Should().Contain(i => i.Kind == "Duplicate entry").And.Contain(i => i.Kind == "Duplicate switch");
    }

    [Fact] // R: a partial configuration (advanced switches missing) is reported, never silently "in sync"
    public void A_partial_configuration_reports_the_declared_switches_it_lacks()
    {
        var inv = FeatureVisibilityInventory.Build(FeatureVisibilityFixture.Settings(withAdvanced: false));
        inv.Issues.Should().Contain(i => i.Kind == "Declared switch not configured" && i.Subject == "ImpactAnalysis");
    }

    [Fact] // the frontend fallback when the backend does not answer uses the backend's defaults
    public void Without_a_backend_answer_optional_and_legacy_entries_stay_hidden()
    {
        var fallback = new FeatureVisibilityService();
        var rows = NavigationCatalog.Sections.SelectMany(s => s.Items).Where(i => i.Visible(fallback)).Select(i => i.Route).ToList();
        rows.Should().NotContain(["impact-analysis", "implementation-traceability", "ai-change-auditor", "code-traceability", "traceability/suggestions"]);
        rows.Should().Contain(["documentation-drift", "source-analysis", "specification-explorer"]);
    }

    [Fact] // editing previews the sidebar without saving
    public void Edited_values_preview_the_sidebar()
    {
        var inv = FeatureVisibilityInventory.Build(FeatureVisibilityFixture.Settings(), values: new Dictionary<string, bool> { ["AiChangeReview"] = true, ["SpecDrift"] = false });
        Entry(inv, "ai-change-auditor").Shown.Should().BeTrue();
        Entry(inv, "documentation-drift").Shown.Should().BeFalse();
    }
}
