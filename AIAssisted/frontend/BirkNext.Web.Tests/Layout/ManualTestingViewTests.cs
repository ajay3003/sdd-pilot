using BirkNext.Web.Layout;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

/// <summary>
/// The built-in Manual Testing workspace (V6, 2026-11-26): the default workspace, FOCUSED — the sidebar shows exactly Testoversikt, Finn og
/// forstå, Planlegg og klargjør, Testplaner and System Settings. Other modules stay registered, enabled and reachable by URL and contextual
/// links; they are listed in the other workspaces. Expectations are written from the brief, not read back from the definition.
/// </summary>
public sealed class ManualTestingViewTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();

    public ManualTestingViewTests()
    {
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddScoped<NavigationSectionState>();
        Services.AddSingleton(_repository);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private NavigationSectionState State => Services.GetRequiredService<NavigationSectionState>();
    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private static readonly (string Href, string Label)[] Destinations =
    [
        ("manual-testing", "Testoversikt"),
        ("manual-testing/discover", "Finn og forstå"),
        ("manual-testing/plan", "Planlegg og klargjør"),
        ("manual-testing/plans", "Testplaner"),
    ];

    private static readonly string[] HiddenGroups = ["Project setup", "Understand the system", "Requirements", "Test preparation", "Coverage & traceability",
        "Quality & follow-up", "Workspace"];

    [Fact]
    public void V01_Manual_Testing_is_the_default_workspace_and_its_start_page_is_Testoversikt()
    {
        new NavigationSectionState().View.Should().Be(WorkspaceView.ManualTesting, "a fresh start without a remembered choice");
        WorkspaceViews.ManualTesting.LandingRoute.Should().Be("manual-testing");
        WorkspaceViews.ManualTesting.HomeRoute.Should().Be("manual-testing");

        Render<Home>();
        Nav.ToBaseRelativePath(Nav.Uri).Should().Be("manual-testing", "the start page opens Testoversikt");
    }

    [Fact]
    public void V02_The_sidebar_shows_exactly_the_four_destinations_and_System_Settings()
    {
        var cut = Render<NavMenu>();

        cut.FindAll("#nav-groups a").Select(a => (a.GetAttribute("href"), a.TextContent.Trim()))
            .Should().Equal([.. Destinations.Select(d => ((string?)d.Href, d.Label)), ("admin/system-settings", "System Settings")]);
        cut.FindAll("[data-testid^=nav-section-]").Select(b => b.TextContent.Trim()).Should().Equal(["Manual Testing", "Help & Settings"], "no other group header, none empty");
        foreach (var hidden in HiddenGroups) cut.Markup.Should().NotContain($">{hidden}</button>", $"{hidden} is not a group of the Manual Testing sidebar");
        cut.FindAll("a[href='all-modules']").Should().BeEmpty("no All modules row and no All modules hint");
        cut.FindAll("a[href='dashboard'], a[href='user-guide'], a[href='project-import'], a[href='specification-explorer'], a[href='test-data']").Should().BeEmpty();
        cut.FindAll("[data-testid=nav-search-input], #nav-search").Should().BeEmpty("no module search in the focused view");
        cut.FindAll("[data-testid^=nav-section-]").Should().OnlyContain(b => b.GetAttribute("aria-expanded") == "true", "both groups are open: nothing hidden behind a header");
        cut.Find("[data-testid=nav-brand]").GetAttribute("href").Should().Be("manual-testing", "the brand leads to Testoversikt");
    }

    [Fact]
    public void V03_Each_destination_is_highlighted_on_its_own_route_only()
    {
        var cut = Render<NavMenu>();
        foreach (var (href, label) in Destinations)
        {
            Nav.NavigateTo(href);
            cut.WaitForAssertion(() => cut.FindAll("#nav-groups a[aria-current=page]").Select(a => a.TextContent.Trim()).Should().Equal([label], $"{href} highlights {label} only"));
            cut.FindAll("#nav-groups a.active").Should().ContainSingle();
        }
        Nav.NavigateTo("admin/system-settings");
        cut.WaitForAssertion(() => cut.FindAll("#nav-groups a[aria-current=page]").Select(a => a.GetAttribute("href")).Should().Equal(["admin/system-settings"]));
    }

    [Fact]
    public void V04_The_step_bar_and_the_sidebar_use_the_same_routes()
    {
        var sidebar = WorkspaceViews.Ordered(WorkspaceView.ManualTesting).Single(s => s.Id == "manual-workspace").Items.Select(i => i.Route).ToList();
        sidebar.Should().Equal([ManualTestingPresentation.HomeRoute, .. ManualTestingPresentation.Stages.Select(s => s.Route)]);
    }

    [Fact]
    public void V05_A_deep_link_to_another_module_opens_without_polluting_the_sidebar_or_switching_the_workspace()
    {
        var cut = Render<NavMenu>();
        Nav.NavigateTo("specification-explorer");

        cut.WaitForAssertion(() => cut.FindAll("#nav-groups a").Select(a => a.GetAttribute("href")).Should().Equal(
            [.. Destinations.Select(d => (string?)d.Href), "admin/system-settings"], "the open page outside the view gets no row"));
        cut.FindAll("#nav-groups a[aria-current=page]").Should().BeEmpty("no Manual Testing page is falsely highlighted");
        State.View.Should().Be(WorkspaceView.ManualTesting, "following a link never switches the workspace");

        var notice = Render<WorkspaceScopeNotice>(p => p.Add(x => x.Sections, State));
        notice.Find("[data-testid=nav-outside-back]").GetAttribute("href").Should().Be("manual-testing", "a clear way back to Testoversikt");
        notice.FindAll("[data-testid=nav-outside-add]").Should().BeEmpty("a focused view takes no additions");
    }

    [Fact]
    public void V06_Other_modules_stay_registered_enabled_and_in_the_other_workspaces()
    {
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        foreach (var route in new[] { "specification-explorer", "sources", "artifact-traceability", "test-data", "test-coverage-review", "dashboard", "user-guide", "documentation-drift" })
        {
            var item = NavigationCatalog.Sections.SelectMany(s => s.Items).Single(i => i.Route == route);
            item.Visible(features).Should().BeTrue($"{route} is hidden from the Manual Testing sidebar, not disabled");
            WorkspaceViews.All.Where(v => v.View != WorkspaceView.ManualTesting && !v.IsAllModules).Should().Contain(v => v.Contains(item), $"{route} has a workspace home");
            WorkspaceViews.Of(WorkspaceView.AllModules).Contains(item).Should().BeTrue();
        }
        State.SetView(WorkspaceView.ManualTesting);
        NavigationModules.All(features, State).Single(m => m.Item.Route == "source-analysis").Reach.Should().Be(ModuleReach.HiddenByWorkspaceView, "outside the view, not forbidden");
    }

    [Fact]
    public void V07_Switching_to_another_workspace_shows_its_own_menu_and_switching_back_restores_the_focused_sidebar()
    {
        _repository.ProjectName = "M2LB";
        Nav.NavigateTo("manual-testing/plan");
        var cut = Render<NavMenu>();

        cut.Find("[data-testid=nav-workspace-view]").Change(WorkspaceView.TestLead.ToString());
        cut.FindAll("#nav-groups a").Select(a => a.GetAttribute("href")).Should().Contain(["dashboard", "test-data", "all-modules"], "Test Management keeps its own menu");
        cut.FindAll("#nav-search, [data-testid=nav-search-input]").Should().NotBeEmpty("search is back in the other workspaces");

        cut.Find("[data-testid=nav-workspace-view]").Change(WorkspaceView.ManualTesting.ToString());
        cut.FindAll("#nav-groups a").Select(a => a.GetAttribute("href")).Should().Equal([.. Destinations.Select(d => (string?)d.Href), "admin/system-settings"]);
        cut.Find("a[href='manual-testing/plan']").GetAttribute("aria-current").Should().Be("page");
        _repository.ProjectName.Should().Be("M2LB", "a workspace switch never changes the project");
        Nav.ToBaseRelativePath(Nav.Uri).Should().Be("manual-testing/plan");
    }

    [Fact]
    public void V08_The_person_s_additions_never_extend_the_focused_view()
    {
        State.SetView(WorkspaceView.ManualTesting);
        State.AddToView("test-data");
        var cut = Render<NavMenu>();
        cut.FindAll("a[href='test-data']").Should().BeEmpty();
    }

    [Fact]
    public void V09_The_remembered_workspace_is_restored_and_an_unknown_one_falls_back_to_Manual_Testing()
    {
        State.SetView(WorkspaceView.Developer);
        var restored = new NavigationSectionState();
        restored.Restore(State.Serialize());
        restored.View.Should().Be(WorkspaceView.Developer, "an explicit choice is respected");
        WorkspaceViews.Of(restored.View).HomeRoute.Should().Be("dashboard", "the other workspaces keep their start page");

        var unknown = new NavigationSectionState();
        unknown.Restore(State.Serialize().Replace("\"View\":4", "\"View\":42"));
        unknown.View.Should().Be(WorkspaceView.ManualTesting);
        new NavigationSectionState().Restore("{not json");
    }

    [Fact]
    public void V10_The_collapsed_sidebar_shows_the_two_groups_without_search_and_with_labels()
    {
        State.SetSidebarCollapsed(true);
        var cut = Render<NavMenu>();
        cut.FindAll("[data-testid^=nav-rail-]").Select(b => b.GetAttribute("data-testid")).Should().Equal(["nav-rail-workspace", "nav-rail-manual-workspace", "nav-rail-manual-settings"]);
        cut.FindAll("[data-testid^=nav-rail-]").Should().OnlyContain(b => !string.IsNullOrWhiteSpace(b.GetAttribute("aria-label")) && !string.IsNullOrWhiteSpace(b.GetAttribute("data-label")));
        cut.Find("[data-testid=nav-rail-manual-workspace]").Click();
        cut.FindAll("[data-testid=nav-flyout-link]").Select(a => a.TextContent.Trim()).Should().Equal(Destinations.Select(d => d.Label));
    }

    [Fact]
    public void V11_The_mobile_drawer_lists_only_the_same_destinations()
    {
        var cut = Render<NavMenu>();
        cut.Find("[data-testid=nav-drawer-toggle]").Click();
        cut.Find("[data-testid=nav-panel]").GetAttribute("role").Should().Be("dialog");
        cut.FindAll("#nav-groups a").Select(a => a.GetAttribute("href")).Should().Equal([.. Destinations.Select(d => (string?)d.Href), "admin/system-settings"]);
    }
}
