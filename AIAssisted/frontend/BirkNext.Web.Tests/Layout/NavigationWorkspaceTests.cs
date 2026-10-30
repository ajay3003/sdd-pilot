using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

/// <summary>
/// Navigation modernization: Workspace Views are navigation preferences (never permissions) independent of the selected project in both
/// directions; every module stays reachable in every view; search finds current and former names; collapse is remembered per view; shared
/// workspaces link sibling pages without changing routes.
/// </summary>
public sealed class NavigationWorkspaceTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();

    public NavigationWorkspaceTests()
    {
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddScoped<NavigationSectionState>();
        Services.AddSingleton(_repository);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private NavigationSectionState State => Services.GetRequiredService<NavigationSectionState>();
    private string Location => Services.GetRequiredService<NavigationManager>().ToBaseRelativePath(Services.GetRequiredService<NavigationManager>().Uri);

    [Theory]
    [InlineData(WorkspaceView.ProjectLeader, "delivery-overview")]
    [InlineData(WorkspaceView.ScrumMaster, "delivery-reporting")]
    [InlineData(WorkspaceView.TestLead, "test-coverage-review")]
    [InlineData(WorkspaceView.TechnicalReviewer, "source-analysis")]
    [InlineData(WorkspaceView.AllModules, "dashboard")]
    public void SwitchingTheView_OpensItsLandingPage_AndNeverChangesTheSelectedProject(WorkspaceView view, string landing)
    {
        _repository.ProjectName = "M2LB";
        var cut = Render<NavMenu>();

        cut.Find("[data-testid=nav-workspace-view]").Change(view.ToString());

        State.View.Should().Be(view);
        Location.Should().Be(landing);
        _repository.ProjectName.Should().Be("M2LB", "a Workspace View is a navigation preference only");
        _repository.SddLifecycle.CurrentProjectImportId.Should().BeNull();
    }

    [Fact]
    public void SelectingAnotherProject_NeverChangesTheView()
    {
        State.SetView(WorkspaceView.TestLead);
        _repository.ProjectName = "M2LB";
        _repository.ProjectName = "Skolenærvær";
        _repository.SddLifecycle.CurrentProjectImportId = "import-1";
        State.View.Should().Be(WorkspaceView.TestLead);
        typeof(NavigationSectionState).GetConstructors().Single().GetParameters().Should().BeEmpty("the navigation state depends on no project state");
    }

    [Theory]
    [InlineData(WorkspaceView.AllModules)]
    [InlineData(WorkspaceView.ProjectLeader)]
    [InlineData(WorkspaceView.ScrumMaster)]
    [InlineData(WorkspaceView.TestLead)]
    [InlineData(WorkspaceView.TechnicalReviewer)]
    public void EveryVisibleModule_IsReachableInEveryView_AsALinkInItsGroup_AndBySearch(WorkspaceView view)
    {
        State.SetView(view);
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        var cut = Render<NavMenu>();

        foreach (var section in NavigationCatalog.Sections)
        foreach (var item in section.Items.Where(i => i.Visible(features)))
        {
            cut.FindAll($"#nav-section-{section.Id} a[href='{item.Route}']").Should().ContainSingle($"{item.Label} stays in the {section.Label} group in {view}");
            cut.Find($"[data-testid=nav-section-{section.Id}]").Should().NotBeNull("a collapsed group can always be expanded");
            NavigationCatalog.Search(item.Label, features).Select(r => r.Item.Route).Should().Contain(item.Route);
        }
    }

    [Fact]
    public void FocusedView_PutsItsGroupsFirst_CollapsesTheRest_AllModulesExpandsEverything()
    {
        State.SetView(WorkspaceView.TestLead);
        var cut = Render<NavMenu>();

        cut.FindAll("button.nav-section").Select(b => b.GetAttribute("data-testid")).Take(3).Should().Equal(
            "nav-section-quality-testing", "nav-section-traceability", "nav-section-getting-started");
        cut.Find("[data-testid=nav-more-modules]").TextContent.Should().Be("More modules");
        cut.Find("#nav-section-quality-testing").HasAttribute("hidden").Should().BeFalse();
        cut.Find("#nav-section-delivery-reporting").HasAttribute("hidden").Should().BeTrue("outside the focus, collapsed — not removed");

        cut.Find("[data-testid=nav-workspace-view]").Change(nameof(WorkspaceView.AllModules));
        cut.FindAll(".nav-group[hidden]").Should().BeEmpty();
        cut.FindAll("[data-testid=nav-more-modules]").Should().BeEmpty();
    }

    [Fact]
    public void CollapseChoices_AreRememberedPerView_AndSurviveSerialization()
    {
        State.SetView(WorkspaceView.ScrumMaster);
        State.Toggle("delivery-reporting");
        State.IsCollapsed("delivery-reporting").Should().BeTrue();
        State.SetView(WorkspaceView.AllModules);
        State.IsCollapsed("delivery-reporting").Should().BeFalse("each view keeps its own choices");

        var restored = new NavigationSectionState();
        restored.Restore(State.Serialize());
        restored.View.Should().Be(WorkspaceView.AllModules);
        restored.SetView(WorkspaceView.ScrumMaster);
        restored.IsCollapsed("delivery-reporting").Should().BeTrue();

        var broken = new NavigationSectionState();
        broken.Restore("{not json");
        broken.Restore("""{"View":99,"Choices":{}}""");
        broken.View.Should().Be(WorkspaceView.AllModules, "unreadable preferences fall back to the defaults");
    }

    [Fact]
    public void Preferences_AreSavedToThisBrowser_WhenChanged()
    {
        var cut = Render<NavMenu>();
        cut.Find("[data-testid=nav-section-quality-testing]").Click();
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "localStorage.setItem"));
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "localStorage.getItem");
    }

    [Fact]
    public void TheCurrentPagesGroup_IsExpanded_EvenWhenTheViewCollapsesIt()
    {
        State.SetView(WorkspaceView.TestLead);
        Services.GetRequiredService<NavigationManager>().NavigateTo("delivery-reporting");
        var cut = Render<NavMenu>();
        cut.Find("#nav-section-delivery-reporting").HasAttribute("hidden").Should().BeFalse();
        cut.Find("a[href='delivery-reporting']").GetAttribute("aria-current").Should().Be("page");
    }

    [Theory]
    [InlineData("Schema Studio", "schema-studio")]
    [InlineData("Implementation Review", "task-alignment")]
    [InlineData("Spec Drift", "documentation-drift")]
    [InlineData("Security Classification", "security-classification-review")]
    [InlineData("Sample Projects", "demo-projects")]
    [InlineData("Delivery Reporting", "delivery-reporting")]
    [InlineData("QA Auditor", "quality/document")]
    [InlineData("backlog", "delivery-overview")]
    public void Search_FindsCurrentAndFormerNames(string text, string route) =>
        NavigationCatalog.Search(text, Services.GetRequiredService<FeatureVisibilityService>()).Select(r => r.Item.Route).Should().Contain(route);

    [Fact]
    public void SearchBox_ShowsResultsWithTheirGroup_EnterOpensTheFirst_EscapeClears()
    {
        var cut = Render<NavMenu>();
        var search = cut.Find("[data-testid=nav-search]");
        search.GetAttribute("type").Should().Be("search");
        cut.Find("label[for=nav-search]").TextContent.Should().Be("Search modules");

        search.Input("task alignment");
        cut.FindAll("[data-testid=nav-search-result]").First().TextContent.Should().Contain("Task Alignment Review").And.Contain("Specifications & Planning");
        cut.Find("[data-testid=nav-search-count]").GetAttribute("aria-live").Should().Be("polite");
        cut.Find("[data-testid=nav-search]").KeyDown("Enter");
        Location.Should().Be("task-alignment");

        cut.Find("[data-testid=nav-search]").Input("zzz-nothing");
        cut.Find("[data-testid=nav-search-count]").TextContent.Should().Be("No module found");
        cut.Find("[data-testid=nav-search]").KeyDown("Escape");
        cut.FindAll("[data-testid=nav-search-results]").Should().BeEmpty();
    }

    [Fact]
    public void TaskAlignmentReview_StaysDiscoverable_InItsGroup_ItsWorkspaceTabs_AndSearch()
    {
        NavigationCatalog.SectionFor("task-alignment")!.Label.Should().Be("Specifications & Planning");
        NavigationCatalog.WorkspaceFor("task-alignment")!.Label.Should().Be("Plan & Tasks");
        NavigationCatalog.Search("Task Alignment", Services.GetRequiredService<FeatureVisibilityService>()).First().Item.Route.Should().Be("task-alignment");
    }

    [Fact]
    public void SharedWorkspaceTabs_LinkSiblingPages_WithoutChangingRoutes()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("plan-explorer");
        var cut = Render<SharedWorkspaceTabs>();

        cut.Find("[data-testid=shared-workspace-tabs]").GetAttribute("aria-label").Should().Be("Plan & Tasks workspace");
        cut.FindAll("[data-testid=shared-workspace-tab]").Select(a => a.GetAttribute("href")).Should().Equal("plan-explorer", "task-explorer", "task-alignment");
        cut.Find("[data-testid=shared-workspace-tab][data-route=plan-explorer]").GetAttribute("aria-current").Should().Be("page");

        Services.GetRequiredService<NavigationManager>().NavigateTo("dashboard");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=shared-workspace-tabs]").Should().BeEmpty());

        foreach (var workspace in NavigationCatalog.SharedWorkspaces)
        foreach (var route in workspace.Routes)
            NavigationCatalog.Sections.SelectMany(s => s.Items).Should().Contain(i => i.Route == route, $"{workspace.Label} only links existing sidebar routes");
    }

    [Fact]
    public void SharedWorkspaceTabs_LeaveOutRowsHiddenByFeatureVisibility()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto { ImplementationReview = false, PlanExplorer = true, TaskExplorer = true });
        Services.GetRequiredService<NavigationManager>().NavigateTo("plan-explorer");
        Render<SharedWorkspaceTabs>().FindAll("[data-testid=shared-workspace-tab]").Select(a => a.GetAttribute("href")).Should().Equal("plan-explorer", "task-explorer");
    }
}
