using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// All modules: every module of the one navigation registry appears exactly once, grouped by area, whatever workspace is selected; modules outside
/// the workspace are marked (never hidden), modules turned off in Feature Visibility are listed as off and not linked, and search uses the
/// sidebar's own ranking.
/// </summary>
public sealed class AllModulesPageTests : BunitContext
{
    private readonly NavigationSectionState _state = new();

    public AllModulesPageTests()
    {
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddSingleton(_state);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static List<NavItem> Catalog => [.. NavigationCatalog.Sections.SelectMany(s => s.Items)];

    [Fact]
    public void Every_registry_module_is_listed_once_grouped_by_area_in_every_workspace()
    {
        foreach (var view in WorkspaceViews.All.Select(v => v.View))
        {
            _state.SetView(view);
            var cut = Render<AllModules>();
            var routes = cut.FindAll("[data-testid=am-module]").Select(m => m.GetAttribute("data-route")).ToList();
            routes.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Catalog.Select(i => i.Route), $"{view}: a workspace never removes a module from All modules");
            cut.FindAll("[data-testid=am-group]").Select(g => g.GetAttribute("data-group")).Should().Equal(
                NavigationCatalog.Sections.Where(s => s.Items.Count > 0).Select(s => s.Id), "grouped by the registry's own areas, in its order");
            cut.Find("[data-testid=am-count]").TextContent.Should().StartWith($"{Catalog.Count} of {Catalog.Count} modules");
        }
    }

    [Fact]
    public void In_a_focused_workspace_other_modules_are_marked_with_the_workspaces_that_hold_them_and_stay_linked()
    {
        _state.SetView(WorkspaceView.ManualTesting);
        var cut = Render<AllModules>();

        cut.Find("[data-testid=am-workspace]").TextContent.Should().Contain("Manual Testing");
        var plan = cut.Find("[data-route='manual-testing/plan']");
        plan.GetAttribute("data-reach").Should().Be(nameof(ModuleReach.InView));
        plan.QuerySelector("[data-testid=am-in-view]").Should().NotBeNull();
        var api = cut.Find("[data-route='api-quality-review']");
        api.GetAttribute("data-reach").Should().Be(nameof(ModuleReach.HiddenByWorkspaceView));
        api.QuerySelector("[data-testid=am-outside]")!.TextContent.Should().Contain("Technical Testing");
        api.QuerySelector("[data-testid=am-module-link]")!.GetAttribute("href").Should().Be("api-quality-review", "outside the workspace is not forbidden");
    }

    [Fact]
    public void A_module_turned_off_in_Feature_Visibility_is_listed_as_off_and_not_linked()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto { SpecDrift = false, SourceAnalysis = true });
        var cut = Render<AllModules>();

        var drift = cut.Find("[data-route='documentation-drift']");
        drift.GetAttribute("data-enabled").Should().Be("false");
        drift.QuerySelector("[data-testid=am-module-link]").Should().BeNull();
        drift.QuerySelector("[data-testid=am-disabled]")!.TextContent.Should().Contain("Feature Visibility");
    }

    [Fact]
    public void Search_finds_modules_by_name_earlier_name_or_purpose_and_says_when_nothing_matches()
    {
        var cut = Render<AllModules>();

        cut.Find("[data-testid=am-search]").Input("Test Plan Suggestions");
        cut.FindAll("[data-testid=am-module]").Select(m => m.GetAttribute("data-route")).Should().Contain("manual-testing/plan", "an earlier name finds Plan & prepare (the Test Plan Suggestions page is its advanced tools)");
        cut.Find("[data-testid=am-search]").Input("declared journeys");
        cut.FindAll("[data-testid=am-module]").Select(m => m.GetAttribute("data-route")).Should().Contain("integration-journeys");
        cut.Find("[data-testid=am-search]").Input("zzzz-nothing");
        cut.Find("[data-testid=am-none]").TextContent.Should().Contain("No module matches");
        cut.Find("[data-testid=am-search]").Closest("label")!.TextContent.Should().Contain("Find a module");
    }

    [Fact]
    public void Each_area_has_a_jump_button_with_its_count_that_reveals_and_focuses_the_area()
    {
        var cut = Render<AllModules>();
        var jumps = cut.FindAll("[data-testid=am-jump-area]");
        var groups = cut.FindAll("[data-testid=am-group]");
        jumps.Select(j => j.GetAttribute("data-group")).Should().Equal(groups.Select(g => g.GetAttribute("data-group")), "one jump per area, in the catalog's order");
        jumps[1].TextContent.Should().Contain(groups[1].QuerySelectorAll("[data-testid=am-module]").Length.ToString());

        jumps[2].Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextRevealSection" && (string)i.Arguments[0]! == $"am-g-{groups[2].GetAttribute("data-group")}");
        cut.Find($"#am-g-{groups[2].GetAttribute("data-group")}").GetAttribute("tabindex").Should().Be("-1", "the area heading takes focus");

        cut.Find("[data-testid=am-search]").Input("zzzz-no-module");
        cut.FindAll("[data-testid=am-jump]").Should().BeEmpty("no areas to jump to when nothing matches");
    }
}
