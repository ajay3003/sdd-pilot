using System.Text.RegularExpressions;
using BirkNext.Web.Layout;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

/// <summary>
/// The product is called BirkNext ("Quality &amp; Testing Workspace") wherever a person sees its name: sidebar brand, browser titles, Dashboard,
/// diagnostics and the Help Center. The former name "QA Review Studio" is not shown anywhere. Namespaces, folders, routes, databases and stored
/// ids are not product names and are unchanged. The story behind BirkNext lives in the Help Center only — never on start-up, a splash screen or a
/// Dashboard banner.
/// </summary>
public sealed class ProductBrandingTests : BunitContext
{
    private const string FormerName = "QA Review Studio";
    private const string StoryLine = "Ajay imagined it. Claude helped build it. Testers make it better.";

    public ProductBrandingTests()
    {
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddScoped<NavigationSectionState>();
        Services.AddSingleton(new WorkspaceArtifactRepository());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static string WebProject
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "frontend", "BirkNext.Web")) && Directory.Exists(Path.Combine(dir.FullName, "backend")))
                    return Path.Combine(dir.FullName, "frontend", "BirkNext.Web");
            throw new DirectoryNotFoundException("frontend/BirkNext.Web was not found above the test output directory.");
        }
    }

    private static IEnumerable<string> UserFacingFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal)
                        || f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".md", StringComparison.Ordinal))
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj|node_modules)[\\/]"));

    [Fact]
    public void The_sidebar_brand_is_BirkNext_with_its_subtitle()
    {
        var cut = Render<NavMenu>();
        var brand = cut.Find("[data-testid=nav-brand]");
        cut.Find("[data-testid=nav-brand-name]").TextContent.Should().Be("BirkNext");
        cut.Find("[data-testid=nav-brand-sub]").TextContent.Should().Be("Quality & Testing Workspace");
        brand.GetAttribute("aria-label").Should().Be("BirkNext — Dashboard");
        brand.GetAttribute("title").Should().Be("BirkNext — Quality & Testing Workspace");
        cut.Markup.Should().NotContain(FormerName);
    }

    [Fact]
    public void The_former_product_name_is_not_shown_anywhere_in_the_frontend()
    {
        var hits = UserFacingFiles(WebProject).Where(f => File.ReadAllText(f).Contains(FormerName, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(WebProject, f)).ToList();
        hits.Should().BeEmpty("the product is called BirkNext wherever its name is shown");
    }

    [Fact]
    public void The_browser_title_before_the_app_loads_is_BirkNext()
    {
        var index = File.ReadAllText(Path.Combine(WebProject, "wwwroot", "index.html"));
        Regex.Match(index, "<title>(?<t>[^<]*)</title>").Groups["t"].Value.Should().Be("BirkNext — Quality &amp; Testing Workspace");
    }

    [Fact]
    public void The_Help_Center_is_titled_for_BirkNext()
    {
        var cut = Render<UserGuide>();
        cut.Markup.Should().Contain("Help Center").And.Contain("Guides, workflows and technical documentation for BirkNext.").And.NotContain(FormerName);
    }

    [Fact]
    public void The_story_is_in_the_Help_Center_with_the_requested_words()
    {
        var story = Regex.Replace(Render<UserGuide>().Find("#about").TextContent, "\\s+", " ");
        story.Should().Contain("The Story Behind BirkNext").And.Contain(StoryLine)
            .And.Contain("A tool shaped by requirements, curiosity, AI-assisted development and the belief that quality should be built in—not inspected in at the end.")
            .And.Contain("If you find a bug, congratulations. You're using the tool correctly.")
            .And.Contain("Ajay found the requirements. Claude found the semicolons. The testers found the bugs.");
        UserGuideContent.TopicFor("story")!.Id.Should().Be("about", "the story keeps its place and its /user-guide#story link inside About");
    }

    [Theory]
    [InlineData("story behind BirkNext")]
    [InlineData("found the semicolons")]
    [InlineData("quality should be built in")]
    public void Help_Center_search_finds_the_story(string query) =>
        UserGuideSearch.Find(query).Select(h => h.Topic.Id).Should().Contain("about");

    [Fact]
    public void The_story_is_never_on_start_up_a_splash_screen_or_the_Dashboard()
    {
        foreach (var file in new[] { Path.Combine("wwwroot", "index.html"), Path.Combine("Pages", "Dashboard.razor"), Path.Combine("Pages", "Home.razor"),
                                     Path.Combine("Layout", "MainLayout.razor"), Path.Combine("Layout", "NavMenu.razor"), "App.razor" })
        {
            var path = Path.Combine(WebProject, file);
            if (!File.Exists(path)) continue;
            File.ReadAllText(path).Should().NotContain("Ajay imagined it").And.NotContain("Story Behind", $"{file} is shown on start-up or as a banner");
        }
        var guideSources = Directory.EnumerateFiles(WebProject, "*.razor", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj)[\\/]") && File.ReadAllText(f).Contains("Ajay imagined it", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)).ToList();
        guideSources.Should().Equal(["GuideReference.razor"], "the story is written once, in the Help Center");
    }
}
