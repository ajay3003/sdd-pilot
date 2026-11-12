using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

/// <summary>
/// The built-in Manual Testing workspace view: a navigation preference over existing modules, ordered as the functional testing workflow.
/// Expectations are written from the requirement (groups, order, real destinations, Feature Visibility first, no permission or project change),
/// not read back from the definition.
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

    private static readonly (string Group, string[] Routes)[] Expected =
    [
        ("Project setup", ["project-import", "sources"]),
        ("Understand the system", ["system-understanding"]),
        ("Requirements", ["specification-explorer", "quality/document", "documentation-drift"]),
        ("Test preparation", ["system-understanding?tab=tests", "test-plan-suggestions", "test-data"]),
        ("Coverage & traceability", ["artifact-traceability", "test-coverage-review"]),
        ("Quality & follow-up", ["dashboard", "getting-started", "user-guide"]),
    ];

    [Fact]
    public void The_view_is_built_in_and_orders_existing_modules_as_the_testing_workflow()
    {
        WorkspaceViews.All.Should().Contain(v => v.View == WorkspaceView.ManualTesting && v.Label == "Manual Testing");
        var ordered = WorkspaceViews.Ordered(WorkspaceView.ManualTesting);
        ordered.Take(Expected.Length).Select(s => (s.Label, s.Items.Select(i => i.Route).ToArray()))
            .Should().BeEquivalentTo(Expected, o => o.WithStrictOrdering());

        var catalog = NavigationCatalog.Sections.SelectMany(s => s.Items).ToList();
        foreach (var route in Expected.SelectMany(e => e.Routes).Where(r => r != NavigationCatalog.TestScenariosRoute))
            catalog.Should().ContainSingle(i => i.Route == route, $"{route} must be an existing catalog row, not an invented route");
        catalog.Should().NotContain(i => i.Route == NavigationCatalog.TestScenariosRoute, "tabs stay nested in their page: the scenario link is a view shortcut, not a module");
        ordered.SelectMany(s => s.Items).Select(i => i.Route).Where(r => r != NavigationCatalog.TestScenariosRoute).Should().OnlyHaveUniqueItems("no module appears twice")
            .And.BeEquivalentTo(catalog.Select(i => i.Route), "every module stays in the sidebar model (search, Show other modules, additions)");
    }

    [Fact]
    public void Functional_test_scenarios_is_a_view_shortcut_to_the_canonical_scenario_page()
    {
        var item = WorkspaceViews.Ordered(WorkspaceView.ManualTesting).SelectMany(s => s.Items).Single(i => i.Route == NavigationCatalog.TestScenariosRoute);
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        item.Visible(features).Should().Be(NavigationCatalog.Sections.SelectMany(s => s.Items).Single(i => i.Route == "system-understanding").Visible(features),
            "it follows the visibility of the page it opens");
        WorkspaceViews.Ordered(WorkspaceView.AllModules).SelectMany(s => s.Items).Should().NotContain(i => i.Route == NavigationCatalog.TestScenariosRoute, "it exists only in its view");
        item.Label.Should().Be("Functional Test Scenarios");
        item.IsDeepLink.Should().BeTrue("it opens System Understanding → What to test, not a second scenario module");
        item.Title.Should().Contain("not Azure DevOps Test Cases").And.Contain("not executed tests");
        item.IsCurrentLocation("system-understanding?tab=tests").Should().BeTrue();
        var page = NavigationCatalog.Sections.SelectMany(s => s.Items).Single(i => i.Route == "system-understanding");
        WorkspaceViews.IsCurrent(WorkspaceView.ManualTesting, page, "system-understanding?tab=tests").Should().BeFalse("one current row: the shortcut claims its location");
        WorkspaceViews.IsCurrent(WorkspaceView.ManualTesting, item, "system-understanding?tab=tests").Should().BeTrue();
        WorkspaceViews.IsCurrent(WorkspaceView.AllModules, page, "system-understanding?tab=tests").Should().BeTrue("without the shortcut the page row is current");
    }

    [Fact]
    public void The_sidebar_renders_the_workflow_groups_and_switching_changes_nothing_else()
    {
        _repository.ProjectName = "M2LB";
        Services.GetRequiredService<NavigationManager>().NavigateTo("source-analysis");
        var cut = Render<NavMenu>();

        cut.Find("[data-testid=nav-workspace-view]").Change(WorkspaceView.ManualTesting.ToString());

        State.View.Should().Be(WorkspaceView.ManualTesting);
        cut.FindAll("[data-testid^=nav-section-]").Take(6).Select(b => b.TextContent.Trim()).Should().Equal(Expected.Select(e => e.Group));
        cut.Find("[data-testid=nav-section-manual-prepare] + .nav-group").QuerySelectorAll("a").Select(a => a.GetAttribute("href"))
            .Should().Equal("system-understanding?tab=tests", "test-plan-suggestions", "test-data");
        cut.Find("[data-testid=nav-workspace-view-help]").TextContent.Should().Contain("prepare scenarios and test data");
        Services.GetRequiredService<NavigationManager>().ToBaseRelativePath(Services.GetRequiredService<NavigationManager>().Uri).Should().Be("source-analysis",
            "switching the view never navigates");
        cut.Find("a[href='source-analysis']").GetAttribute("aria-current").Should().Be("page", "the open technical page keeps its row");
        _repository.ProjectName.Should().Be("M2LB", "a view never changes the project");
    }

    [Fact]
    public void Feature_Visibility_outranks_the_view_and_hidden_technical_modules_stay_reachable()
    {
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        features.ApplyLocalFlags(new FeatureVisibilityDto { SpecDrift = false, SourceAnalysis = true });
        State.SetView(WorkspaceView.ManualTesting);
        Module("documentation-drift").Reach.Should().Be(ModuleReach.DisabledByFeatureVisibility, "a disabled module never appears because a view lists it");
        Module("test-data").Reach.Should().Be(ModuleReach.InView);
        Module("source-analysis").Reach.Should().Be(ModuleReach.HiddenByWorkspaceView, "a technical module is outside the view, not forbidden");
        NavigationModules.Search("source analysis", features, State).Single(r => r.Item.Route == "source-analysis").Bucket.Should().Be(NavSearchBucket.OtherWorkspaces);

        NavModule Module(string route) => NavigationModules.All(features, State).Single(m => m.Item.Route == route);
    }

    [Fact]
    public void Every_enabled_module_is_reachable_in_Manual_Testing_by_Show_other_modules()
    {
        State.SetView(WorkspaceView.ManualTesting);
        State.ToggleShowOtherModules();
        var cut = Render<NavMenu>();
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        foreach (var item in NavigationCatalog.Sections.SelectMany(s => s.Items).Where(i => i.Visible(features)))
            cut.FindAll($"#nav-groups a[href='{item.Route}']").Should().ContainSingle($"{item.Label} is reachable exactly once in Manual Testing");
    }

    [Fact]
    public void The_stored_view_restores_and_an_unknown_stored_view_falls_back_to_All_Modules()
    {
        State.SetView(WorkspaceView.ManualTesting);
        var restored = new NavigationSectionState();
        restored.Restore(State.Serialize());
        restored.View.Should().Be(WorkspaceView.ManualTesting);

        var unknown = new NavigationSectionState();
        unknown.Restore(State.Serialize().Replace("\"View\":6", "\"View\":42"));
        unknown.View.Should().Be(WorkspaceView.AllModules, "a removed or unknown view never breaks the sidebar");
    }
}
