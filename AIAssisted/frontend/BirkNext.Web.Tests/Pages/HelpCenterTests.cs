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
        cut.Find("[data-testid=ug-search-status]").TextContent.Should().Be("No guide sections match.");
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
}
