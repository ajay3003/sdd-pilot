using BirkNext.Web.Pages.Admin;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// System Settings → Feature Visibility: every sidebar entry with its visibility and reason, switches without an entry, other pages and retired
/// keys, with search, filters, details and identity-based navigation health. Values are the shipped configuration (FeatureVisibilityFixture).
/// </summary>
public partial class SystemSettingsEnvironmentDiagnosticsTests
{
    private static string Entries(IEnumerable<(string Key, string Label, bool Value)> features, bool locked = false) =>
        string.Join(",", features.Select(f => $$"""{ "key": "{{f.Key}}", "label": "{{f.Label}}", "value": {{(f.Value ? "true" : "false")}}, "locked": {{(locked ? "true" : "false")}} }"""));

    private IRenderedComponent<SystemSettings> RenderFeatureVisibility(Dictionary<string, bool>? overrides = null)
    {
        var settings = FeatureVisibilityFixture.Settings(overrides);
        _handler.EditableJson = $$"""
            {
              "featureVisibility": { "platform": [{{Entries(FeatureVisibilityFixture.Platform.Select(p => (p.Key, p.Label, true)), locked: true)}}],
                                     "core": [{{Entries(settings.Core.Select(e => (e.Key, e.Label, e.Value)))}}],
                                     "advanced": [{{Entries(settings.Advanced.Select(e => (e.Key, e.Label, e.Value)))}}] },
              "logging": { "minimumLevel": "Information", "seqUrl": "" },
              "admin": { "showDiagnostics": true }
            }
            """;
        var cut = Render<SystemSettings>();
        cut.WaitForAssertion(() => FindButton(cut, "Feature Visibility").Should().NotBeNull());
        cut.FindAll(".ss-nav-item").First(b => b.TextContent.Contains("Feature Visibility")).Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=fv-panel]").Should().ContainSingle());
        return cut;
    }

    private static AngleSharp.Dom.IElement EntryRow(IRenderedComponent<SystemSettings> cut, string route) =>
        cut.FindAll("[data-testid=fv-entry]").Single(r => r.GetAttribute("data-route") == route);

    private static string Metric(IRenderedComponent<SystemSettings> cut, string id) => cut.Find($"[data-testid=fv-metric-{id}] .fvp-mv").TextContent.Trim();

    [Fact]
    public void FeatureVisibility_shows_counts_with_their_units_and_a_consistent_navigation()
    {
        var cut = RenderFeatureVisibility();

        cut.Find(".fvp-title").TextContent.Should().Be("Feature Visibility");
        cut.Find(".fvp-sub").TextContent.Should().Be("Explore available BirkNext capabilities, their visibility and requirements.");
        Metric(cut, "switches").Should().Be("25");
        cut.Find("[data-testid=fv-metric-switches] .fvp-md").TextContent.Should().Contain("each gating a sidebar entry").And.Contain("4 platform").And.Contain("8 retired");
        Metric(cut, "shown").Should().StartWith("49").And.EndWith("of 55");
        Metric(cut, "hidden").Should().Be("6");
        cut.Find("[data-testid=fv-metric-navigation]").GetAttribute("data-state").Should().Be("consistent");
        cut.FindAll("[data-testid=fv-issues]").Should().BeEmpty();
    }

    [Fact]
    public void FeatureVisibility_lists_entries_by_sidebar_group_with_a_reason_for_each_hidden_one()
    {
        var cut = RenderFeatureVisibility();

        cut.FindAll("[data-testid=fv-group-document-review] [data-testid=fv-entry] .fvp-name").Select(n => n.TextContent).Should().Equal(
            "Specification Explorer", "Constitution Explorer", "Data Model Explorer", "Plan Explorer", "Task Explorer",
            "Document Quality Review", "Task Alignment Review", "Documentation Drift");
        EntryRow(cut, "impact-analysis").GetAttribute("data-state").Should().Be("hidden");
        EntryRow(cut, "impact-analysis").QuerySelector("[data-testid=fv-reason]")!.TextContent.Should().Be("Off: Impact Analysis");
        EntryRow(cut, "traceability").QuerySelector("[data-testid=fv-reason]")!.TextContent.Should().Be("Off: Legacy Traceability Navigation");
        EntryRow(cut, "schema-studio").TextContent.Should().Contain("Always shown");
        EntryRow(cut, "azure-environment").TextContent.Should().Contain("Unavailable").And.Contain("Shown");

        cut.FindAll("[data-testid=fv-group-switches] [data-testid=fv-switch]").Select(s => s.GetAttribute("data-key")).Should().Equal("LegacyTraceabilityNavigationEnabled");
        cut.FindAll("[data-testid=fv-retired] .fvp-name").Select(n => n.TextContent).Should().Equal(
            "QA Artifact Library", "WASM Security Review", "WASM Performance Review", "Extraction Review", "Architecture View", "Spec Comparison", "Specification Deltas", "Task Deltas");
    }

    [Fact]
    public void FeatureVisibility_search_and_filters_use_the_actual_state()
    {
        var cut = RenderFeatureVisibility();

        cut.Find("[data-testid=fv-search]").Input("drift");
        cut.FindAll("[data-testid=fv-entry]").Select(r => r.GetAttribute("data-route")).Should().Equal("documentation-drift");
        cut.Find("[data-testid=fv-search]").Input("ImpactAnalysis");
        cut.FindAll("[data-testid=fv-entry]").Select(r => r.GetAttribute("data-route")).Should().Equal("impact-analysis");
        cut.Find("[data-testid=fv-search]").Input("zzz-nothing");
        cut.Find("[data-testid=fv-empty]").TextContent.Should().Contain("zzz-nothing");

        cut.Find("[data-testid=fv-search]").Input("");
        cut.Find("[data-testid=fv-filter-hidden]").Click();
        cut.FindAll("[data-testid=fv-entry]").Should().HaveCount(6).And.OnlyContain(r => r.GetAttribute("data-state") == "hidden");
        cut.Find("[data-testid=fv-filter-hidden]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=fv-filter-unavailable]").Click();
        cut.FindAll("[data-testid=fv-entry]").Select(r => r.GetAttribute("data-route")).Should().Equal("azure-environment");
        cut.Find("[data-testid=fv-filter-shown]").Click();
        cut.FindAll("[data-testid=fv-entry]").Should().HaveCount(48);
    }

    [Fact]
    public void FeatureVisibility_details_explain_visibility_lifecycle_access_and_link_only_to_shown_pages()
    {
        var cut = RenderFeatureVisibility();

        var impact = EntryRow(cut, "impact-analysis");
        impact.QuerySelector("[data-testid=fv-detail-toggle]")!.Click();
        impact = EntryRow(cut, "impact-analysis");
        impact.QuerySelector("[data-testid=fv-detail-toggle]")!.GetAttribute("aria-expanded").Should().Be("true");
        var detail = impact.QuerySelector("[data-testid=fv-detail]")!.TextContent;
        detail.Should().Contain("Shown when Impact Analysis is on").And.Contain("FeatureVisibility:ImpactAnalysis = off").And.Contain("Optional")
            .And.Contain("Use instead: Release Readiness").And.Contain("hiding the entry only removes it from the sidebar");
        impact.QuerySelectorAll("[data-testid=fv-open]").Should().BeEmpty("a hidden entry is never offered as a link");

        EntryRow(cut, "documentation-drift").QuerySelector("[data-testid=fv-detail-toggle]")!.Click();
        var drift = EntryRow(cut, "documentation-drift");
        drift.QuerySelector("[data-testid=fv-open]")!.GetAttribute("href").Should().Be("documentation-drift");
        drift.QuerySelector("[data-testid=fv-help-link]")!.GetAttribute("href").Should().Be("user-guide#documentation-drift");
    }

    [Fact]
    public void FeatureVisibility_editing_is_unavailable_with_the_backends_reason_when_this_session_may_not_change_configuration()
    {
        _handler.ConfigurationAccessJson = """{ "allowed": false, "reason": "Changing configuration is not permitted for this session. Local system configuration is not enabled for this backend." }""";
        var cut = RenderFeatureVisibility();

        var edit = cut.Find("[data-testid=ss-edit]");
        edit.HasAttribute("disabled").Should().BeTrue();
        edit.GetAttribute("title").Should().Contain("not permitted");
        cut.Find("[data-testid=fv-edit-denied]").TextContent.Should().Contain("Local system configuration is not enabled");
    }

    [Fact]
    public void FeatureVisibility_toggling_previews_the_sidebar_and_saves_the_stable_key_only()
    {
        var cut = RenderFeatureVisibility();
        FindButton(cut, "Edit Settings")!.Click();

        cut.FindAll("[data-testid=fv-toggle-QaArtifactLibrary]").Should().BeEmpty("a retired switch is never offered as a toggle");
        cut.Find("[data-testid=fv-toggle-SpecDrift]").Change(false);
        EntryRow(cut, "documentation-drift").GetAttribute("data-state").Should().Be("hidden");
        Metric(cut, "hidden").Should().Be("7");
        FindButton(cut, "Save Settings")!.Click();

        cut.WaitForAssertion(() => _handler.LastSaveBody.Should().NotBeNull());
        _handler.LastSaveBody.Should().Contain("\"SpecDrift\":false").And.NotContain("DocumentationDrift").And.NotContain("ImplementationReview");
    }
}
