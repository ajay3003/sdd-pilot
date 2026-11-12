using System.Text.RegularExpressions;
using BirkNext.Web.Components;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Help Center: body-text search with synonyms, safe excerpts and bounded input; audience areas; article facts (breadcrumb, real feature
/// links, recorded verification only); contextual help links from feature pages that always resolve; and the accuracy rules of the new
/// articles (no confirmed MU → MM incompatibility, two separate kinds of version, no invented release notes).
/// </summary>
public sealed class HelpCenterTests : BunitContext
{
    private IRenderedComponent<UserGuide> Page() { JSInterop.Mode = JSRuntimeMode.Loose; return Render<UserGuide>(); }

    [Fact]
    public void Search_finds_article_body_text_and_returns_a_plain_text_excerpt_around_the_match()
    {
        var hits = UserGuideSearch.Find("exactly one element");
        hits.Should().NotBeEmpty();
        hits[0].Topic.Id.Should().Be("suggested-e2e", "the phrase is only in that article's body");
        hits[0].Match.Should().BeEquivalentTo("exactly one element", "the phrase itself is highlighted");
        (hits[0].Before + hits[0].Match + hits[0].After).Should().Contain("one element").And.NotContain("<").And.NotContain("@");
        UserGuideSearch.Find("mu-mm").Should().BeEmpty("anchors are not text");
        UserGuideSearch.Find("v2.0.1.flat").Select(h => h.Topic.Id).Should().Contain("integration-suites");
    }

    [Fact]
    public void Search_understands_synonyms_and_ranks_titles_first()
    {
        UserGuideSearch.Find("login").Select(h => h.Topic.Id).Should().Contain("browser-authentication");
        UserGuideSearch.Find("historical").Select(h => h.Topic.Id).Should().Contain(["status-reference", "versioning"]);
        UserGuideSearch.Find("Test Data")[0].Topic.Id.Should().Be("test-data");
        UserGuideSearch.Find("glossary")[0].Topic.Id.Should().Be("glossary");
    }

    [Fact]
    public void Hostile_and_oversized_queries_are_inert()
    {
        var cut = Page();
        cut.Find("[data-testid=ug-search]").Input("<script>alert(1)</script><img src=x onerror=alert(1)>");
        cut.FindAll(".ug-search-results script, .ug-search-results img").Should().BeEmpty();
        cut.Find("[data-testid=ug-search-status]").TextContent.Should().StartWith("No guide sections match.");
        var huge = string.Join(' ', Enumerable.Repeat("status", 5000));
        var act = () => UserGuideSearch.Find(huge);
        act.Should().NotThrow();
        UserGuideSearch.Find(huge).Should().NotBeEmpty("the query is cut to its first terms, not rejected");
        cut.Find("[data-testid=ug-search]").GetAttribute("maxlength").Should().Be(UserGuideSearch.MaxQueryLength.ToString());
    }

    [Fact]
    public void Results_show_their_area_and_highlight_the_match_and_Enter_opens_the_best_one()
    {
        var cut = Page();
        cut.Find("[data-testid=ug-search]").Input("pairing");
        var first = cut.FindAll("[data-testid=ug-search-result]")[0];
        first.GetAttribute("data-topic").Should().Be("suggested-e2e");
        cut.FindAll("[data-testid=ug-search-excerpt] mark").Should().NotBeEmpty();
        cut.Find("[data-testid=ug-search]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.Should().EndWith("/user-guide#suggested-e2e");
    }

    private IRenderedComponent<UserGuide> At(string fragment)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/user-guide" + fragment);
        return Render<UserGuide>();
    }

    [Fact]
    public void The_home_shows_search_journey_categories_and_support_but_never_the_whole_directory_or_an_article()
    {
        var cut = Page();
        cut.Find("[data-testid=ug-page]").GetAttribute("data-view").Should().Be("home");
        cut.Find("h1").TextContent.Should().Be("Help Center");
        cut.Find(".page-header, header").TextContent.Should().Contain("Guides, workflows and technical documentation for BirkNext.");
        cut.FindAll("[data-testid=ug-nav]").Should().BeEmpty("one navigation on the home: category cards, not the full directory");
        var journey = cut.FindAll("[data-testid=ug-journey-step]").Select(a => a.GetAttribute("data-topic")).ToList();
        journey.Should().HaveCount(10).And.OnlyContain(id => UserGuideContent.Topics.Any(t => t.Id == id), "every step is an existing article");
        cut.FindAll("[data-testid=ug-category]").Should().OnlyContain(c => c.QuerySelector(".ug-category-icon svg") != null && c.QuerySelector(".ug-category-desc") != null);
        foreach (var card in cut.FindAll("[data-testid=ug-category]"))
            card.QuerySelector(".ug-category-count")!.TextContent.Should().Be(card.QuerySelectorAll("[data-testid=ug-category-link]").Length.ToString(), "the count is the real number of articles shown");
        cut.Find("[data-testid=ug-support]").TextContent.Should().Contain("Troubleshooting").And.Contain("Status meanings");
        cut.Find("[data-testid=ug-recent]").TextContent.Should().Contain("verified against", "only recorded verification, never a file date");
    }

    [Fact]
    public void A_deep_link_opens_one_article_with_breadcrumb_current_navigation_and_paging()
    {
        var cut = At("#contract-testing");
        cut.Find("[data-testid=ug-page]").GetAttribute("data-view").Should().Be("article");
        cut.Find("[data-testid=ug-crumbs]").TextContent.Should().Contain("Help Center").And.Contain("Testing").And.Contain("Contract Testing");
        cut.Find("[data-testid=ug-crumb-home]").GetAttribute("href").Should().Be("/user-guide");
        cut.Find("[data-testid=ug-nav-link][aria-current=page]").GetAttribute("data-topic").Should().Be("contract-testing");
        cut.FindAll("[data-testid=ug-nav-group][open]").Select(g => g.QuerySelector("summary")!.TextContent).Should().Equal("Testing");
        cut.Find("style").TextContent.Should().Contain("[data-topic=\"contract-testing\"]", "only this article is shown");
        cut.FindAll("[data-testid=ug-meta]").Should().HaveCount(UserGuideContent.Topics.Count, "every article stays in the page, so every anchor resolves");
        var (prev, next) = UserGuideContent.Neighbours("contract-testing");
        cut.Find("[data-testid=ug-pager] a[rel=prev]").GetAttribute("href").Should().Be(prev!.Href);
        cut.Find("[data-testid=ug-pager] a[rel=next]").GetAttribute("href").Should().Be(next!.Href);
        cut.FindAll("h1").Should().HaveCount(1);
    }

    [Fact]
    public void An_anchor_inside_an_article_opens_that_article()
    {
        var cut = At("#runtime-profiles");
        cut.Find("[data-testid=ug-nav-link][aria-current=page]").GetAttribute("data-topic").Should().Be("system-settings");
    }

    [Fact]
    public void An_unknown_or_hostile_anchor_shows_not_found_and_never_another_article()
    {
        var unknown = At("#no-such-guide-section");
        unknown.Find("[data-testid=ug-page]").GetAttribute("data-view").Should().Be("missing");
        unknown.Find("[data-testid=ug-missing]").TextContent.Should().Contain("no-such-guide-section").And.Contain("Help Center home");
        unknown.FindAll("[data-testid=ug-nav-link][aria-current]").Should().BeEmpty();

        var hostile = At("#%3Cscript%3Ealert(1)%3C%2Fscript%3E");
        hostile.Find("[data-testid=ug-page]").GetAttribute("data-view").Should().Be("missing");
        hostile.Markup.Should().NotContain("<script>alert", "the anchor is shown as text, never as markup");
        hostile.FindAll("style").Should().BeEmpty("no article rule is built from an unknown anchor");
    }

    [Fact]
    public void Navigating_between_articles_and_back_home_follows_the_url()
    {
        var cut = At("#glossary");
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/user-guide#troubleshooting");
        cut.WaitForAssertion(() => cut.Find("[data-testid=ug-nav-link][aria-current=page]").GetAttribute("data-topic").Should().Be("troubleshooting"));
        nav.NavigateTo("/user-guide");
        cut.WaitForAssertion(() => cut.Find("[data-testid=ug-page]").GetAttribute("data-view").Should().Be("home"));
    }

    [Fact]
    public void Every_topic_belongs_to_exactly_one_audience_area_and_user_guides_are_the_default()
    {
        var cut = Page();
        cut.Find("h1").TextContent.Should().Be("Help Center");
        cut.FindAll("[data-testid=ug-area]").Select(b => b.GetAttribute("aria-pressed")).Should().Equal("true", "false", "false");
        var shown = cut.FindAll("[data-testid=ug-category-link]").Select(a => a.GetAttribute("data-topic")).ToList();
        shown.Should().Contain(["getting-started", "contract-testing", "suggested-e2e", "troubleshooting"]).And.NotContain(["glossary", "workspace-transfer"]);
        cut.FindAll("[data-testid=ug-task-card]").Should().NotBeEmpty("common tasks belong to the user guides");
        cut.FindAll("[data-testid=ug-area]")[1].Click();
        cut.FindAll("[data-testid=ug-category-link]").Select(a => a.GetAttribute("data-topic")).Should().Contain(["connected-systems", "workspace-transfer", "target-environment"]);
        cut.FindAll("[data-testid=ug-task-card]").Should().BeEmpty();
        var all = Enum.GetValues<UserGuideContent.GuideArea>().SelectMany(area => UserGuideContent.Topics.Where(t => UserGuideContent.AreaOf(t) == area)).Select(t => t.Id).ToList();
        all.Should().OnlyHaveUniqueItems().And.HaveCount(UserGuideContent.Topics.Count);
    }

    [Fact]
    public void Every_article_states_where_it_sits_and_only_recorded_verification_is_claimed()
    {
        var cut = Page();
        var metas = cut.FindAll("[data-testid=ug-meta]");
        metas.Select(m => m.GetAttribute("data-topic")).Should().BeEquivalentTo(UserGuideContent.Topics.Select(t => t.Id), "every article has its facts block");
        foreach (var meta in metas)
        {
            var id = meta.GetAttribute("data-topic")!;
            meta.QuerySelector("[data-testid=ug-breadcrumbs]")!.TextContent.Should().StartWith("Help Center");
            if (UserGuideContent.Verified.TryGetValue(id, out var v)) meta.QuerySelector("[data-testid=ug-meta-verified]")!.TextContent.Should().Contain(v.VerifiedOn).And.Contain(v.AppliesTo);
            else meta.QuerySelector("[data-testid=ug-meta-unverified]").Should().NotBeNull($"{id} has no verification record");
        }
        var routes = DocumentationCatalog.Features.Select(f => f.Route).ToHashSet();
        cut.FindAll("[data-testid=ug-meta-open]").Should().OnlyContain(a => routes.Contains(a.GetAttribute("href")!), "Open links come from the catalog's real routes");
        cut.Find("[data-testid=ug-meta][data-topic=execution-results] [data-testid=ug-meta-open]").GetAttribute("href").Should().Be("/execution-results");
    }

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "frontend", "BirkNext.Web"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "frontend", "BirkNext.Web");
    }

    [Fact]
    public void Every_contextual_help_link_in_the_app_resolves_to_a_guide_article()
    {
        var uses = Directory.EnumerateFiles(WebRoot(), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "<(?:BirkNext\\.Web\\.Components\\.)?HelpLink Topic=\"([^\"]+)\"").Select(m => (File: Path.GetFileName(f), Topic: m.Groups[1].Value)))
            .ToList();
        uses.Select(u => u.File).Should().Contain(["ContractTesting.razor", "IntegrationSuitesPage.razor", "CriticalE2ERegression.razor", "ExecutionResults.razor", "WorkspaceTransfer.razor", "ApplicationBuildCard.razor"]);
        // Dynamic topics (@expr) come from RecommendedActionHelp / ProjectQualityStages, whose every topic is checked in ProjectQualityTests.
        uses = [.. uses.Where(u => !u.Topic.StartsWith('@'))];
        uses.Should().OnlyContain(u => UserGuideContent.TopicFor(u.Topic) != null, string.Join(", ", uses.Where(u => UserGuideContent.TopicFor(u.Topic) == null)));
    }

    [Fact]
    public void A_help_link_to_an_unknown_article_renders_nothing_and_a_known_one_deep_links()
    {
        Render<HelpLink>(p => p.Add(x => x.Topic, "no-such-article")).Markup.Trim().Should().BeEmpty();
        var link = Render<HelpLink>(p => p.Add(x => x.Topic, "result-currency")).Find("[data-testid=help-link]");
        link.GetAttribute("href").Should().Be("/user-guide#result-currency");
        link.TextContent.Should().Contain("Guide").And.Contain("Status meanings");
    }

    [Fact]
    public void The_new_articles_state_only_what_the_evidence_supports()
    {
        var cut = Page();
        var risk = cut.Find("#integration-suites").TextContent;
        risk.Should().Contain("v2.0.0").And.Contain("v2.0.1.flat").And.Contain("not as a confirmed production incompatibility").And.Contain("sending Altinn application");
        risk.Should().Contain("Passing separate subflows does not prove one complete end-to-end journey");
        var e2e = cut.Find("#suggested-e2e").TextContent;
        e2e.Should().Contain("never runs unattended").And.Contain("Verify DOM in Edge").And.Contain("Diagnostic");
        var versions = cut.Find("#versioning").TextContent;
        versions.Should().Contain("Historical result").And.Contain("Local build").And.Contain("not who published it");
        cut.Find("#whats-new").TextContent.Should().Contain("Release notes are not available in this build");
        cut.FindAll("[data-testid=ug-outcome-term]").Should().HaveCount(UserGuideContent.ExecutionOutcomes.Count);
        cut.FindAll("[data-testid=ug-currency-term]").Select(r => r.QuerySelector("th")!.TextContent.Trim()).Should().Equal("Current definitions", "Historical result", "Currency unknown");
    }
    // Root cause of the oversized black circles and triangles (2026-11-12): the icon markup and the rules that size and paint it shipped in the
    // same change, but css/user-guide.css is an unversioned static file. A browser that still held the previous copy (heuristic caching, no
    // Cache-Control) rendered the new icons with no size (an SVG then fills its container: 832 px search glass, 224 px category icons) and the
    // default black fill. The icons now carry their own size and paint, and the stylesheet link is versioned.
    [Fact]
    public void Every_help_center_icon_carries_its_own_size_and_stroke_paint_so_a_missing_or_stale_stylesheet_cannot_blow_it_up()
    {
        foreach (var cut in new[] { Page(), At("#manual-testing") })
        {
            var svgs = cut.FindAll("[data-testid=ug-page] svg");
            svgs.Should().NotBeEmpty();
            foreach (var svg in svgs)
            {
                int.Parse(svg.GetAttribute("width")!).Should().BeInRange(12, 24, svg.OuterHtml);
                int.Parse(svg.GetAttribute("height")!).Should().BeInRange(12, 24, svg.OuterHtml);
                svg.GetAttribute("fill").Should().Be("none", "an unpainted outline icon would otherwise be filled black");
                svg.GetAttribute("stroke").Should().Be("currentColor");
            }
        }
    }

    [Fact]
    public void The_help_center_stylesheet_link_is_versioned_so_a_cached_previous_copy_is_not_reused_with_new_markup()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web"));
        var html = File.ReadAllText(Path.Combine(root, "wwwroot", "index.html"));
        Regex.IsMatch(html, @"href=""css/user-guide\.css\?v=[0-9A-Za-z.-]+""").Should().BeTrue();
    }
}
