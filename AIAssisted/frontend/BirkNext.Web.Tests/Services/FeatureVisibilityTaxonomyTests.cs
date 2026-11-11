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

    // ── Sidebar ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sidebar_document_review_holds_the_explorers_and_every_document_assessment()
    {
        SidebarLabels("document-review").Should().Equal(
            "Specification Explorer", "Constitution Explorer", "Data Model Explorer", "Plan Explorer", "Task Explorer",
            "Document Quality Review", "Task Alignment Review", "Documentation Drift");
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
        // Traceability & Impact: Impact Analysis joined the group (its own key still gates it).
        // Relationship Review (shared canonical review registry) is always available at the end of the group.
        // Without a backend answer the fallback uses the backend's defaults: Impact Analysis is optional and off.
        Visible(null).Should().Equal("Requirements Traceability", "Relationship Review", "Release Readiness", "Snapshots");
        Visible(new FeatureVisibilityDto { ImplementationTraceability = true, ImpactAnalysis = true }).Should().Equal("Requirements Traceability", "Change Traceability", "Impact Analysis", "Relationship Review", "Release Readiness", "Snapshots");
        Visible(new FeatureVisibilityDto { ImplementationTraceability = false, ImpactAnalysis = false }).Should().Equal("Requirements Traceability", "Relationship Review", "Release Readiness", "Snapshots");
        new FeatureVisibilityDto().ImplementationTraceability.Should().BeFalse("no explicit value means disabled");
        SidebarLabels("traceability").Should().NotContain(["Task Alignment Review", "Documentation Drift"], "document reviews stay in Specifications & Planning").And.Contain("Impact Analysis");
    }

    [Fact]
    public void Sidebar_source_review_holds_the_source_snapshot_features_including_generated_documentation_health()
    {
        SidebarLabels("source-review").Should().Equal(
            "Source Analysis", "Schema Studio", "Generated Documentation Explorer", "Generated Documentation Health", "Technology Coverage", "Dependency Review", "Pipeline Review", "Environment Analysis",
            "AI Change Review");
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

    // ── Feature Visibility declarations ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_declared_key_is_an_existing_stable_configuration_key_declared_once()
    {
        var dtoKeys = typeof(FeatureVisibilityDto).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        FeatureVisibilityTaxonomy.Declarations.Select(d => d.Key).Should().OnlyContain(k => dtoKeys.Contains(k), "no new keys: stable keys keep their names")
            .And.OnlyHaveUniqueItems();
        FeatureVisibilityTaxonomy.Declarations.Select(d => d.Key).Should().NotContain(FeatureVisibilityTaxonomy.Retired.Select(r => r.Key), "retired keys are not switches");
    }

    [Fact]
    public void Every_successor_is_a_sidebar_entry()
    {
        var routes = NavigationCatalog.Sections.SelectMany(s => s.Items).Select(i => i.Route).ToHashSet();
        FeatureVisibilityTaxonomy.Declarations.Where(d => d.Successor is not null).Should().OnlyContain(d => routes.Contains(d.Successor!));
        FeatureVisibilityTaxonomy.ContextualRoutes.Where(r => r.Of is not null).Should().OnlyContain(r => routes.Contains(r.Of!));
    }
}
