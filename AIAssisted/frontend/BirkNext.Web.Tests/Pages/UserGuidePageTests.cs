using System.Text.RegularExpressions;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// User Guide: task-oriented structure (grouped navigation, search, task cards), anchors that really exist, and the evidence semantics the
/// guide must state — logical Project, per-hop authentication, manual vs automated readiness, Test Data ownership, performance runtime
/// values, Results vs Compare, Documentation Drift lineage, Observed vs Verified and Unavailable vs Not configured.
/// </summary>
public sealed class UserGuidePageTests : BunitContext
{
    private static readonly string Source = string.Join("\n", DocumentationCatalog.ReadGuideSource(typeof(UserGuide).Assembly)!);
    private static readonly HashSet<string> Anchors = Regex.Matches(Source, "\\bid=\"([^\"@]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Routes = DocumentationCatalog.DiscoverRoutes(typeof(UserGuide).Assembly);

    private static string Normalize(string text) => Regex.Replace(text, "\\s+", " ");

    [Fact]
    public void Guide_sources_include_every_section_component()
    {
        DocumentationCatalog.ReadGuideSource(typeof(UserGuide).Assembly).Should().HaveCountGreaterThan(1, "the section components are embedded beside the page");
        Anchors.Should().Contain(["overview", "project-model", "connected-systems", "test-data", "performance-test-review", "documentation-drift", "status-reference"]);
    }

    [Fact]
    public void Every_topic_and_anchor_named_by_the_guide_content_exists_exactly_once()
    {
        var all = Regex.Matches(Source, "\\bid=\"([^\"@]+)\"").Select(m => m.Groups[1].Value).ToArray();
        all.Should().OnlyHaveUniqueItems();
        foreach (var topic in UserGuideContent.Topics)
        {
            Anchors.Should().Contain(topic.Id, $"topic {topic.Title} needs its section");
            foreach (var anchor in topic.Anchors) Anchors.Should().Contain(anchor, $"anchors owned by {topic.Title} must exist");
        }
        Anchors.Should().Contain(UserGuideContent.TaskCards.Select(c => c.GuideId));
        Anchors.Should().Contain(DocumentationCatalog.Features.Select(f => f.UserGuideAnchor), "every feature's guide link resolves");
        foreach (Match link in Regex.Matches(Source, "UserGuideContent\\.Href\\(\"([^\"]+)\"\\)"))
            Anchors.Should().Contain(link.Groups[1].Value);
    }

    [Fact]
    public void Feature_learn_more_links_and_existing_deep_links_target_real_sections()
    {
        foreach (var anchor in new[] { "documentation-drift", "performance-test-review", "feature-availability", "connected-systems", "project-model", "test-data" })
            Anchors.Should().Contain(anchor);
    }

    [Fact]
    public void Task_cards_and_troubleshooting_open_real_features()
    {
        foreach (var href in UserGuideContent.TaskCards.Select(c => c.Href).Concat(UserGuideContent.Troubleshooting.Select(t => t.Href)))
            Routes.Should().Contain(DocumentationCatalog.NormalizeRoute(href), $"{href} must be a current route");
    }

    [Fact]
    public void Landing_shows_task_cards_with_purpose_when_and_open_action()
    {
        var cut = Render<UserGuide>();

        cut.Find("h1").TextContent.Should().Be("User Guide");
        var cards = cut.FindAll("[data-testid=ug-task-card]");
        cards.Select(c => c.QuerySelector(".ug-task-title")!.TextContent).Should().Contain(["Import a project", "Configure Project", "Set up Target Environment",
            "Review Connected Systems", "Prepare Test Data", "Build a performance scenario", "Review Documentation Drift"]);
        cards.Should().OnlyContain(c => c.QuerySelector(".ug-task-purpose") != null && c.QuerySelector(".ug-task-when") != null && c.QuerySelector(".ug-open-link") != null);
        cut.Find("[data-testid=ug-task-card] .ug-open-link[href='/admin/system-settings?section=target-environments&tab=project']").TextContent.Should().Contain("Open Project");
    }

    [Fact]
    public void Navigation_is_grouped_compact_and_links_every_topic()
    {
        var cut = Render<UserGuide>();

        cut.FindAll("[data-testid=ug-nav-group] summary").Select(s => s.TextContent).Should().Equal(UserGuideContent.Groups);
        cut.FindAll("[data-testid=ug-nav-link]").Should().HaveCount(UserGuideContent.Topics.Count);
        UserGuideContent.Groups.Should().OnlyContain(g => UserGuideContent.Topics.Count(t => t.Group == g) <= 10, "no group becomes a long ungrouped list");
        cut.FindAll("[data-testid=ug-nav-link]").Should().OnlyContain(a => a.GetAttribute("href")!.StartsWith("/user-guide#"));
    }

    [Fact]
    public void Narrow_navigation_toggle_reports_its_state()
    {
        var cut = Render<UserGuide>();
        var toggle = cut.Find("[data-testid=ug-nav-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.GetAttribute("aria-controls").Should().Be("ug-nav-panel");

        toggle.Click();

        cut.Find("[data-testid=ug-nav-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("#ug-nav-panel").ClassList.Should().Contain("is-open");
    }

    [Fact]
    public void Search_matches_titles_and_keywords_and_announces_the_result()
    {
        UserGuideContent.Search("").Should().BeEmpty();
        UserGuideContent.Search("baseline").Select(t => t.Id).Should().Contain("performance-test-review");
        UserGuideContent.Search("reporter child").Select(t => t.Id).Should().Contain("test-data");
        UserGuideContent.Search("drift").First().Id.Should().Be("documentation-drift", "title matches rank first");

        var cut = Render<UserGuide>();
        cut.Find("[data-testid=ug-search]").Input("unavailable");
        cut.FindAll("[data-testid=ug-search-result]").Select(a => a.GetAttribute("data-topic")).Should().Contain("feature-availability");
        cut.Find("[data-testid=ug-search-status]").TextContent.Should().MatchRegex("^\\d+ guide sections? match\\.$");
        cut.Find("label[for=ug-search-input]").TextContent.Should().Be("Search guide");

        cut.Find("[data-testid=ug-search]").Input("zzzz-nothing");
        cut.FindAll("[data-testid=ug-search-result]").Should().BeEmpty();
        cut.Find("[data-testid=ug-search-status]").TextContent.Should().Be("No guide sections match.");
    }

    [Fact]
    public void Project_is_documented_as_evidence_for_the_Target_Application()
    {
        var text = Normalize(Render<UserGuide>().Find("#project-model").TextContent);

        text.Should().Contain("Target Application is the system under test; Project is its evidence.");
        text.Should().Contain("Target Application required").And.Contain("archives the previous application's evidence (never deletes it)");
        text.Should().ContainAll("Source only", "Docs only", "Contracts/schema only", "Infrastructure only", "Supporting/shared source", "multi-package project");
        text.Should().Contain("Missing code never makes a package invalid");
        text.Should().Contain("Bound is not Ready").And.Contain("Optional evidence never blocks");
        text.Should().Contain("never deletes an imported package or a Source Analysis snapshot");
    }

    [Fact]
    public void Connected_systems_document_per_hop_authentication_and_two_readiness_answers()
    {
        var text = Normalize(Render<UserGuide>().Find("#connected-systems").TextContent);

        text.Should().ContainAll("Reporter → Altinn app", "Altinn app → integration consumer (MU)", "BirkNext → Altinn");
        text.Should().Contain("There is no single “authentication”");
        text.Should().ContainAll("Blocked", "Review required", "does not block manual preparation");
        text.Should().ContainAll("Manual test readiness", "Automated BirkNext execution", "Manual readiness is not automated readiness");
        text.Should().Contain("Detection does not save").And.Contain("never applied silently");
        text.Should().Contain("Observed sign-in is evidence, not verification");
    }

    [Fact]
    public void Test_data_separates_reporter_child_assets_and_payload_from_field_values()
    {
        var text = Normalize(Render<UserGuide>().Find("#test-data").TextContent);

        text.Should().Contain("Reporter is not child").And.Contain("Not part of the XML payload");
        text.Should().Contain("Klient → Identifikator → Fodselsnummer");
        text.Should().Contain("Skolenærvær default child").And.Contain("skolenaervaer-child-default");
        text.Should().Contain("A whole XML or JSON payload asset is a payload, never a field value");
        text.Should().ContainAll("Test Data asset", "Fixed value", "Generated value", "Schema default");
        text.Should().Contain("never silently changes an approved fixture");
        text.Should().NotMatchRegex("\\b\\d{11}\\b", "no raw identifiers in examples");
    }

    [Fact]
    public void Performance_guide_keeps_runtime_values_ordering_and_results_vs_compare_apart()
    {
        var text = Normalize(Render<UserGuide>().Find("#performance-test-review").TextContent);

        text.Should().ContainAll("Scenario · Workload · Thresholds · Resources", "Readiness · Run", "Results · Compare · History");
        text.Should().ContainAll("Recommended", "OpenAPI / Swagger", "GraphQL", "Live Discovery", "Manual");
        text.Should().Contain("{id}").And.Contain("Test Data is optional").And.Contain("Use as Test Data").And.Contain("never promoted automatically");
        text.Should().ContainAll("Auto-filled value", "Previous-step output", "Scenario variable", "Fixed value", "Generated value", "Observed sample");
        text.Should().ContainAll("Move up", "Move down", "cannot run before it");
        text.Should().Contain("an operation name alone is not executable");
        text.Should().Contain("A readiness check never runs a load test");
        text.Should().Contain("Results analyzes a single run").And.Contain("Full baseline comparison");
        text.Should().Contain("Response body not retained");
    }

    [Fact]
    public void Documentation_drift_lists_its_four_tabs_lineage_rule_and_neutral_absence()
    {
        var text = Normalize(Render<UserGuide>().Find("#documentation-drift").TextContent);

        text.Should().ContainAll("Overview", "Compare", "Changes", "Drift");
        text.Should().Contain("Authored intent ↔ generated documentation");
        text.Should().Contain("Revisions of the same document lineage across imports of the same project");
        text.Should().Contain("Missing evidence is not a difference");
        text.Should().ContainAll("Consistent", "Difference", "Conflict", "Ready to compare", "Not comparable");
    }

    [Fact]
    public void Unavailable_is_separate_from_not_configured_and_environment_analysis_is_unavailable()
    {
        var cut = Render<UserGuide>();
        var text = Normalize(cut.Find("#feature-availability").TextContent);

        text.Should().Contain("Configuration alone does not enable it");
        text.Should().Contain("Configuring Entra alone will not make the analysis usable");
        cut.Find("#environment-analysis").TextContent.Should().Contain("Unavailable");
        cut.Find("[data-kind=Unavailable] .ug-callout-label").TextContent.Should().Be("○Unavailable");
        Normalize(cut.Find("#feature-matrix").TextContent).Should().Contain("Unavailable");
        Source.Should().NotContain("Azure Environment Analysis", "legacy title, flagged by Documentation Health");
    }

    [Fact]
    public void Status_reference_defines_observed_verified_unavailable_and_not_configured_separately()
    {
        var cut = Render<UserGuide>();
        var terms = cut.FindAll("[data-testid=ug-status-term] th").Select(th => th.TextContent.Trim()).ToArray();

        terms.Should().Contain(["Configured", "Detected", "Bound", "Ready", "Observed", "Verified", "Partial", "Blocked", "Missing", "Unavailable", "Not configured", "Not applicable (N/A)"]);
        Normalize(cut.Find("#observed-vs-verified").ParentElement!.TextContent).Should().Contain("Observed is not verified");
        UserGuideContent.StatusTerms.Should().OnlyContain(t => t.Meaning.Length > 0 && t.NotTheSameAs.Length > 0);
    }

    [Fact]
    public void Callouts_carry_a_visible_label_and_note_semantics()
    {
        var cut = Render<UserGuide>();

        var callouts = cut.FindAll("aside.ug-callout");
        callouts.Should().NotBeEmpty();
        callouts.Should().OnlyContain(c => c.GetAttribute("role") == "note" && !string.IsNullOrWhiteSpace(c.QuerySelector(".ug-callout-label")!.TextContent));
    }

    [Fact]
    public void Sections_have_one_h2_and_focusable_titles_for_anchor_navigation()
    {
        var cut = Render<UserGuide>();

        cut.FindAll("h1").Should().HaveCount(1);
        foreach (var section in cut.FindAll("section.ug-topic"))
        {
            var title = section.QuerySelector("h2.ug-section-title");
            title.Should().NotBeNull($"section {section.Id} needs its heading");
            title!.GetAttribute("tabindex").Should().Be("-1");
            section.GetAttribute("data-topic").Should().Be(section.Id);
        }
    }

    [Fact]
    public void Guide_documentation_health_has_no_failures_and_no_legacy_terms()
    {
        var findings = DocumentationCatalog.Check(DocumentationCatalog.CurrentNavigation, DocumentationCatalog.Features, Routes, [],
            DocumentationCatalog.SettingsSections, DocumentationCatalog.TargetEnvironmentTabs, DocumentationCatalog.SourceAnalysisDomains,
            DocumentationCatalog.ReadGuideSource(typeof(UserGuide).Assembly));

        findings.Should().NotContain(f => f.Severity == "Fail");
        findings.Should().NotContain(f => f.Category == "Legacy terminology");
    }
}
