using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The sidebar (NavigationCatalog) and System Settings › Feature &amp; Menu Visibility (FeatureVisibilityTaxonomy) present one taxonomy:
/// Document Review = documents and document-to-document assessment, Traceability = how lifecycle artifacts and tracked changes are
/// linked, Source Review = source-derived evidence. Categories are presentation only: stable keys and stored values never change.
/// </summary>
public sealed class FeatureVisibilityTaxonomyTests
{
    private static IReadOnlyList<string> SidebarLabels(string sectionId) =>
        NavigationCatalog.Sections.Single(s => s.Id == sectionId).Items.Select(i => i.Label).ToList();

    private static string CategoryIdOf(string key) => FeatureVisibilityTaxonomy.CategoryOf(key)!.Id;

    // ── Sidebar ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sidebar_document_review_holds_the_explorers_and_every_document_assessment()
    {
        SidebarLabels("document-review").Should().Equal(
            "Specification Explorer", "Constitution Explorer", "Data Model Explorer", "Plan Explorer", "Task Explorer",
            "Document Quality Review", "Task Alignment Review", "Generated Documentation Health", "Documentation Drift");
    }

    [Fact]
    public void Sidebar_traceability_is_requirements_and_change_traceability_only()
    {
        // Legacy rows stay behind the (off by default) legacy flag. Change Traceability is off by default; explicit values always win.
        IEnumerable<string> Visible(FeatureVisibilityDto? flags)
        {
            var visibility = new FeatureVisibilityService();
            if (flags is not null) visibility.ApplyLocalFlags(flags);
            return NavigationCatalog.Sections.Single(s => s.Id == "traceability").Items.Where(i => i.Visible(visibility)).Select(i => i.Label);
        }
        Visible(null).Should().Equal("Requirements Traceability");
        Visible(new FeatureVisibilityDto { ImplementationTraceability = true }).Should().Equal("Requirements Traceability", "Change Traceability");
        Visible(new FeatureVisibilityDto { ImplementationTraceability = false }).Should().Equal("Requirements Traceability");
        new FeatureVisibilityDto().ImplementationTraceability.Should().BeFalse("no explicit value means disabled");
        SidebarLabels("traceability").Should().NotContain(["Task Alignment Review", "Documentation Drift", "Impact Analysis"]);
    }

    [Fact]
    public void Sidebar_source_review_keeps_source_features_without_generated_documentation_health()
    {
        SidebarLabels("source-review").Should().Equal(
            "Source Analysis", "Schema Studio", "Generated Documentation Explorer", "Technology Coverage", "Dependency Review", "Pipeline Review", "Environment Analysis");
    }

    [Theory]
    [InlineData("Specification Explorer")]
    [InlineData("Constitution Explorer")]
    [InlineData("Data Model Explorer")]
    [InlineData("Plan Explorer")]
    [InlineData("Task Explorer")]
    [InlineData("Document Quality Review")]
    [InlineData("Task Alignment Review")]
    [InlineData("Generated Documentation Health")]
    [InlineData("Documentation Drift")]
    [InlineData("Requirements Traceability")]
    [InlineData("Change Traceability")]
    public void Each_moved_or_kept_feature_appears_exactly_once_in_the_sidebar(string label)
    {
        NavigationCatalog.Sections.SelectMany(s => s.Items).Count(i => i.Label == label).Should().Be(1);
    }

    [Fact]
    public void Routes_stay_stable_after_the_move()
    {
        var routes = NavigationCatalog.Sections.SelectMany(s => s.Items).ToDictionary(i => i.Label, i => i.Route);
        routes["Task Alignment Review"].Should().Be("task-alignment");
        routes["Change Traceability"].Should().Be("implementation-traceability");
        routes["Documentation Drift"].Should().Be("documentation-drift");
        routes["Generated Documentation Health"].Should().Be(NavigationCatalog.GeneratedDocumentationHealthRoute);
        routes.Values.Should().OnlyHaveUniqueItems();
    }

    // ── Feature & Menu Visibility categories ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("QualityReview", "document-review")]          // Document Quality Review
    [InlineData("ImplementationReview", "document-review")]   // Task Alignment Review
    [InlineData("GeneratedDocumentationHealth", "document-review")]
    [InlineData("SpecDrift", "document-review")]              // Documentation Drift
    [InlineData("ArtifactTraceability", "traceability")]      // Requirements Traceability
    [InlineData("ImplementationTraceability", "traceability")] // Change Traceability
    [InlineData("SourceAnalysis", "source-review")]
    [InlineData("AzureEnvironmentAnalysis", "source-review")]
    [InlineData("ImpactAnalysis", "analysis")]
    public void Stable_keys_resolve_to_the_new_categories(string key, string category)
    {
        CategoryIdOf(key).Should().Be(category);
    }

    [Fact]
    public void Analysis_is_no_longer_a_catch_all_and_quality_holds_no_document_review()
    {
        var analysis = FeatureVisibilityTaxonomy.Categories.Single(c => c.Id == "analysis");
        analysis.Keys.Should().Equal("ImpactAnalysis");
        FeatureVisibilityTaxonomy.Categories.Single(c => c.Id == "quality-testing").Keys.Should().NotContain("QualityReview");
        FeatureVisibilityTaxonomy.Categories.Select(c => c.Label).Should().Contain(["Document Review", "Traceability", "Source Review", "Quality & Testing"])
            .And.NotContain("Review", "the explorers' category is Document Review")
            .And.NotContain("Quality", "the quality category is Quality & Testing, as in the sidebar");
    }

    [Fact]
    public void Every_key_has_one_category_and_none_is_also_advanced_or_retired()
    {
        var keys = FeatureVisibilityTaxonomy.Categories.SelectMany(c => c.Keys).ToList();
        keys.Select(k => k.ToUpperInvariant()).Should().OnlyHaveUniqueItems("a feature is never duplicated across categories");
        keys.Intersect(FeatureVisibilityTaxonomy.AdvancedKeys, StringComparer.OrdinalIgnoreCase).Should().BeEmpty();
        keys.Should().NotContain(["SpecComparison", "SpecificationDeltas", "TaskDeltas"], "retired features stay retired");
    }

    [Fact]
    public void Every_key_is_an_existing_stable_configuration_key()
    {
        var dtoKeys = typeof(FeatureVisibilityDto).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        FeatureVisibilityTaxonomy.Categories.SelectMany(c => c.Keys).Concat(FeatureVisibilityTaxonomy.AdvancedKeys)
            .Concat(FeatureVisibilityTaxonomy.MenuItems.Select(m => m.Key))
            .Should().OnlyContain(k => dtoKeys.Contains(k), "no new keys: ImplementationReview, ImplementationTraceability and SpecDrift keep their names");
    }

    /// <summary>
    /// The decisive consistency check: switching a key off hides exactly the sidebar rows it gates, and those rows sit in the sidebar
    /// section of the key's Feature Visibility category. A key can therefore never be shown in one category and gate a row in another.
    /// </summary>
    [Fact]
    public void Each_category_gates_rows_only_in_its_matching_sidebar_section()
    {
        var allOn = new FeatureVisibilityDto();
        foreach (var flag in typeof(FeatureVisibilityDto).GetProperties().Where(p => p.PropertyType == typeof(bool))) flag.SetValue(allOn, true);
        var all = new FeatureVisibilityService();
        all.ApplyLocalFlags(allOn);
        var rows = NavigationCatalog.Sections.SelectMany(s => s.Items.Select(i => (Section: s.Id, Item: i))).ToList();

        foreach (var category in FeatureVisibilityTaxonomy.Categories.Where(c => c.NavSectionId is not null))
        foreach (var key in category.Keys)
        {
            var off = new FeatureVisibilityDto();
            foreach (var flag in typeof(FeatureVisibilityDto).GetProperties().Where(p => p.PropertyType == typeof(bool))) flag.SetValue(off, true);
            typeof(FeatureVisibilityDto).GetProperty(key)!.SetValue(off, false);
            var without = new FeatureVisibilityService();
            without.ApplyLocalFlags(off);

            var gated = rows.Where(r => r.Item.Visible(all) && !r.Item.Visible(without)).ToList();
            gated.Should().OnlyContain(r => r.Section == category.NavSectionId, $"{key} is listed under {category.Label}");
            if (key != "BlazorWasmPerformanceReview") // a configurable review without a sidebar row
                gated.Should().NotBeEmpty($"{key} gates a sidebar row");
        }
    }

    // ── Counts ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Category_entries_and_counts_derive_from_membership_and_keep_each_stored_value()
    {
        var configured = new List<FeatureEntryDto>
        {
            new() { Key = "SpecificationExplorer", Label = "Specification Explorer", Value = true },
            new() { Key = "SpecDrift", Label = "Documentation Drift", Value = false },
            new() { Key = "ImplementationReview", Label = "Task Alignment Review", Value = true },
            new() { Key = "ArtifactTraceability", Label = "Requirements Traceability", Value = true },
            new() { Key = "ImpactAnalysis", Label = "Impact Analysis", Value = false },
        };
        var document = FeatureVisibilityTaxonomy.Categories.Single(c => c.Id == "document-review");

        var entries = FeatureVisibilityTaxonomy.EntriesOf(document, configured);

        entries.Select(e => e.Label).Should().Equal("Specification Explorer", "Task Alignment Review", "Documentation Drift");
        entries.Count(e => e.Value).Should().Be(2);
        entries.Single(e => e.Key == "SpecDrift").Value.Should().BeFalse("moving a category never resets a disabled feature");
        FeatureVisibilityTaxonomy.Categories.Sum(c => FeatureVisibilityTaxonomy.EntriesOf(c, configured).Count).Should().Be(configured.Count, "each feature counted once");
    }

    // ── Menu synchronization ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Menu_registry_lists_each_feature_once_and_covers_every_category_key()
    {
        var menu = FeatureVisibilityTaxonomy.MenuItems;
        menu.Select(m => m.Key).Should().OnlyHaveUniqueItems();
        menu.Select(m => m.Label).Should().OnlyHaveUniqueItems();
        menu.Select(m => m.Key).Should().Contain(FeatureVisibilityTaxonomy.Categories.Where(c => c.NavSectionId is not null).SelectMany(c => c.Keys),
            "every key that gates a sidebar section is a menu item");
    }
}
