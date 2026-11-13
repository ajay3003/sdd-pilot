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
    [InlineData(WorkspaceView.ProjectLeader)]
    [InlineData(WorkspaceView.ScrumMaster)]
    [InlineData(WorkspaceView.TestLead)]
    [InlineData(WorkspaceView.Developer)]
    [InlineData(WorkspaceView.Architect)]
    [InlineData(WorkspaceView.ManualTesting)]
    [InlineData(WorkspaceView.AllModules)]
    public void SwitchingTheView_StaysOnTheCurrentPage_AndNeverChangesTheSelectedProject(WorkspaceView view)
    {
        _repository.ProjectName = "M2LB";
        Services.GetRequiredService<NavigationManager>().NavigateTo("delivery-reporting?week=2026-W44");
        var cut = Render<NavMenu>();

        cut.Find("[data-testid=nav-workspace-view]").Change(view.ToString());

        State.View.Should().Be(view);
        Location.Should().Be("delivery-reporting?week=2026-W44", "switching the view never navigates");
        cut.Find("a[href='delivery-reporting']").GetAttribute("aria-current").Should().Be("page", "the open page keeps its row, in or outside the view");
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
    [InlineData(WorkspaceView.Developer)]
    [InlineData(WorkspaceView.Architect)]
    [InlineData(WorkspaceView.ManualTesting)]
    [InlineData(WorkspaceView.TechnicalTester)]
    public void TheSidebarListsOnlyTheViewsModules_AndEveryOtherVisibleModuleIsFoundBySearch(WorkspaceView view)
    {
        // Matrix B-I + J: a specialized view's sidebar is its preset (plus Dashboard, User Guide, System Settings) and nothing else - no "Other view"
        // rows, no toggle that appends the catalogue. Every other visible module stays reachable by search, under "Outside current view".
        State.SetView(view);
        var definition = WorkspaceViews.Of(view);
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        var cut = Render<NavMenu>();
        cut.FindAll("[data-testid=nav-show-other]").Should().BeEmpty("there is no control that appends other modules to the sidebar");
        cut.FindAll("[data-testid=nav-outside-badge]").Should().BeEmpty("no Other view badges");
        cut.FindAll("[data-outside=true]").Should().BeEmpty("the open page (dashboard) is in every view");
        var rendered = cut.FindAll("[id^='nav-section-'] a").Select(a => a.GetAttribute("href")).ToList();
        rendered.Should().OnlyHaveUniqueItems();

        foreach (var section in NavigationCatalog.Sections)
        foreach (var item in section.Items.Where(i => i.Visible(features)))
        {
            if (definition.Contains(item))
                rendered.Should().ContainSingle(r => r == item.Route, $"{item.Label} is part of {view}");
            else
            {
                rendered.Should().NotContain(item.Route, $"{item.Label} is outside {view}: not a sidebar row");
                NavigationModules.Search(item.Label, features, State).Should().Contain(r => r.Item.Route == item.Route && r.Bucket == NavSearchBucket.OtherWorkspaces,
                    $"{item.Label} is still found by search in {view}");
            }
        }
    }

    [Theory]
    [InlineData(WorkspaceView.ProjectLeader)]
    [InlineData(WorkspaceView.ScrumMaster)]
    [InlineData(WorkspaceView.TestLead)]
    [InlineData(WorkspaceView.Developer)]
    [InlineData(WorkspaceView.Architect)]
    [InlineData(WorkspaceView.ManualTesting)]
    [InlineData(WorkspaceView.TechnicalTester)]
    public void EveryPreset_NamesRealModules_AndRealGroups(WorkspaceView view)
    {
        var definition = WorkspaceViews.Of(view);
        var routes = NavigationCatalog.Sections.SelectMany(s => s.Items).Select(i => i.Route).ToHashSet();
        definition.Modules.Should().NotBeEmpty().And.OnlyHaveUniqueItems().And.OnlyContain(r => routes.Contains(r), "a preset never invents a module");
        definition.FocusSections.Should().OnlyContain(id => NavigationCatalog.Sections.Any(s => s.Id == id));
        routes.Should().Contain(definition.LandingRoute);
        definition.Description.Should().NotBeNullOrWhiteSpace();
        WorkspaceViews.All.Select(v => v.Label).Should().OnlyHaveUniqueItems("no duplicate presets");
    }

    [Theory]
    [InlineData(WorkspaceView.TestLead, new[] { "artifact-traceability", "test-data", "test-plan-suggestions", "system-understanding", "specification-explorer", "test-coverage-review", "api-quality-review", "api-quality-review/contracts", "integration-quality-review", "performance-test-review", "critical-e2e-regression", "execution-results", "release-readiness" })]
    [InlineData(WorkspaceView.TechnicalTester, new[] { "api-quality-review/contracts", "api-quality-review", "integration-suites", "execution-results", "critical-e2e-regression", "integration-quality-review", "test-data", "test-coverage-review", "performance-test-review", "integration-topology", "getting-started" })]
    [InlineData(WorkspaceView.ManualTesting, new[] { "system-understanding", "specification-explorer", "quality/document", "test-plan-suggestions", "test-data", "artifact-traceability", "test-coverage-review", "getting-started" })]
    [InlineData(WorkspaceView.ScrumMaster, new[] { "delivery-reporting", "delivery-overview", "task-alignment", "getting-started", "release-readiness" })]
    [InlineData(WorkspaceView.ProjectLeader, new[] { "dashboard", "delivery-overview", "delivery-reporting", "getting-started", "release-readiness", "snapshots", "correlation-review" })]
    [InlineData(WorkspaceView.Developer, new[] { "source-analysis", "specification-explorer", "schema-studio", "api-quality-review", "technology-coverage", "dependency-review", "pipeline-review" })]
    [InlineData(WorkspaceView.Architect, new[] { "source-analysis", "integration-topology", "integration-quality-review", "schema-studio", "data-model-explorer" })]
    public void Presets_HoldTheirRolesModules(WorkspaceView view, string[] routes)
    {
        var definition = WorkspaceViews.Of(view);
        foreach (var route in routes)
            definition.Contains(NavigationCatalog.Sections.SelectMany(s => s.Items).Single(i => i.Route == route)).Should().BeTrue($"{route} belongs to {definition.Label}");
    }

    [Fact]
    public void All_Modules_is_the_complete_eligible_catalogue_derived_from_the_registry()
    {
        // The invariant (no allow-list): every catalogue row that Feature Visibility allows is exactly one row of All Modules, in catalogue order,
        // never marked "other view"; a row Feature Visibility turns off is absent. A newly registered module therefore appears without any change here.
        WorkspaceViews.Of(WorkspaceView.AllModules).Modules.Should().BeEmpty("All Modules keeps no list of its own");
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        State.SetView(WorkspaceView.AllModules);
        var cut = Render<NavMenu>();
        var catalogue = NavigationCatalog.Sections.SelectMany(s => s.Items).ToList();
        var eligible = catalogue.Where(i => i.Visible(features)).Select(i => i.Route).ToList();
        var rendered = cut.FindAll("[id^='nav-section-'] a").Select(a => a.GetAttribute("href")).ToList();
        rendered.Should().Equal(eligible, "every eligible module once, in catalogue order");
        rendered.Should().OnlyHaveUniqueItems();
        rendered.Should().Contain(["test-data", "test-plan-suggestions", "system-understanding", "dashboard", "getting-started", "artifact-traceability",
            "correlation-review", "critical-e2e-regression", "integration-suites", "execution-results", "performance-test-review"]);
        cut.FindAll("[data-testid=nav-outside-badge]").Should().BeEmpty("nothing is outside All Modules");
        cut.FindAll("[data-outside=true]").Should().BeEmpty();
        catalogue.Where(i => !i.Visible(features)).Select(i => i.Route).Should().NotIntersectWith(rendered, "Feature Visibility still decides what exists");
        // Every specialized view stays a strict subset of the eligible catalogue.
        foreach (var view in WorkspaceViews.All.Where(v => !v.IsAllModules))
            view.Modules.Where(r => catalogue.Single(i => i.Route == r).Visible(features)).Should().BeSubsetOf(eligible, $"{view.Label} only names catalogue modules");
    }

    [Fact]
    public void Eight_built_in_views_in_selector_order_with_stable_ids_and_audience_labels()
    {
        WorkspaceViews.All.Select(v => (v.View, v.Label)).Should().Equal(
            (WorkspaceView.AllModules, "All Modules"), (WorkspaceView.ProjectLeader, "Project Manager"), (WorkspaceView.ScrumMaster, "Scrum Master"),
            (WorkspaceView.TestLead, "Test Lead"), (WorkspaceView.ManualTesting, "Manual Tester"), (WorkspaceView.TechnicalTester, "Technical Tester"),
            (WorkspaceView.Developer, "Developer"), (WorkspaceView.Architect, "Architect"));
        // Stable stored ids: numbers and names of the existing views never change.
        ((int)WorkspaceView.ProjectLeader, (int)WorkspaceView.ManualTesting, (int)WorkspaceView.TechnicalTester).Should().Be((1, 6, 7));
        WorkspaceViews.Of(WorkspaceView.TechnicalTester).Description.Should().Be("Verify APIs, contracts, integrations, E2E flows and performance.");
        WorkspaceViews.Of(WorkspaceView.ManualTesting).Description.Should().Be("Review requirements, prepare functional tests and test data.");
        WorkspaceViews.Of((WorkspaceView)99).View.Should().Be(WorkspaceView.AllModules, "an unknown view falls back to All Modules");
    }

    [Fact]
    public void Specialized_views_are_distinct_and_reduce_navigation()
    {
        var all = NavigationCatalog.Sections.SelectMany(s => s.Items).Count();
        HashSet<string> Of(WorkspaceView v) => [.. WorkspaceViews.Of(v).Modules];
        foreach (var view in WorkspaceViews.All.Where(v => !v.IsAllModules))
            view.Modules.Count.Should().BeLessThan(all / 2, $"{view.Label} narrows the sidebar");
        var (tech, dev, manual, lead) = (Of(WorkspaceView.TechnicalTester), Of(WorkspaceView.Developer), Of(WorkspaceView.ManualTesting), Of(WorkspaceView.TestLead));
        tech.SetEquals(dev).Should().BeFalse(); tech.SetEquals(manual).Should().BeFalse();
        tech.Should().Contain(["integration-suites", "critical-e2e-regression", "execution-results"]).And.NotContain(["plan-explorer", "task-alignment", "pipeline-review"]);
        dev.Should().Contain(["pipeline-review", "task-explorer", "source-analysis"]).And.NotContain(["integration-suites", "critical-e2e-regression", "test-plan-suggestions"]);
        manual.Should().NotContain(["source-analysis", "api-quality-review", "pipeline-review", "code-risk-review"], "no developer diagnostics as manual tasks");
        lead.Should().Contain(["test-plan-suggestions", "test-data"], "Test Lead covers functional preparation")
            .And.Contain(["critical-e2e-regression", "execution-results", "api-quality-review"], "and technical verification");
        Of(WorkspaceView.ProjectLeader).Should().NotContain(["source-analysis", "integration-suites", "api-quality-review"], "no low-level runners for the project manager");
        Of(WorkspaceView.ScrumMaster).SetEquals(Of(WorkspaceView.ProjectLeader)).Should().BeFalse();
        Of(WorkspaceView.Architect).SetEquals(dev).Should().BeFalse();
    }

    [Fact]
    public void A_saved_preference_keeps_its_view_after_the_label_changes_and_Technical_Tester_round_trips()
    {
        var state = new NavigationSectionState();
        state.Restore("""{"View":1,"Choices":{"ProjectLeader":{"pm-status":true}}}""");
        state.View.Should().Be(WorkspaceView.ProjectLeader, "the stored id is the number/name, never the label");
        state.SetView(WorkspaceView.TechnicalTester);
        var copy = new NavigationSectionState(); copy.Restore(state.Serialize());
        copy.View.Should().Be(WorkspaceView.TechnicalTester);
        var invalid = new NavigationSectionState(); invalid.Restore("""{"View":42,"Choices":{}}""");
        invalid.View.Should().Be(WorkspaceView.AllModules, "an invalid saved view fails safely to the default");
    }

    [Fact]
    public void The_baseline_risk_has_one_identity_in_every_view_and_audience_wording()
    {
        var context = new BirkNext.SourceBaselines.SourceContextView([new("m2lb", "M2LB", "Archive", BirkNext.SourceBaselines.SourceBaselineState.NotSelected, null, null, "x", 2, 2, [], "BASELINE_NOT_SELECTED", null)], 1, 2, 1, "fp");
        var risks = WorkspaceViews.All.Select(v => WorkspaceFocus.Risks(context, v.View).Single()).ToList();
        risks.Select(r => (r.Code, r.Route)).Distinct().Should().ContainSingle("same issue, same destination in every view");
        WorkspaceFocus.Risks(context, WorkspaceView.ManualTesting).Single().Text.Should().StartWith("Select the correct project version before preparing tests.");
        WorkspaceFocus.Risks(context, WorkspaceView.TechnicalTester).Single().Text.Should().StartWith("Confirm the source and environment baseline");
        WorkspaceFocus.Risks(context, WorkspaceView.TestLead).Single().Text.Should().StartWith("Resolve the baseline ambiguity");
        WorkspaceFocus.Risks(context, WorkspaceView.Architect).Single().Text.Should().StartWith("Verify the authoritative");
    }

    [Fact]
    public void FocusedView_PutsItsGroupsFirst_ShowsOnlyItsModules_AndAllModulesShowsTheRest()
    {
        State.SetView(WorkspaceView.TestLead);
        var cut = Render<NavMenu>();

        cut.FindAll("button.nav-section").Select(b => b.GetAttribute("data-testid")).Take(3).Should().Equal(
            "nav-section-tl-overview", "nav-section-tl-understand", "nav-section-tl-prepare");
        cut.FindAll("[data-testid=nav-more-modules]").Should().BeEmpty("no More modules heading");
        cut.FindAll("a[href='api-quality-review']").Should().ContainSingle();
        cut.FindAll("a[href='source-analysis']").Should().BeEmpty("outside the Test Lead view: hidden from the sidebar, not removed");
        cut.FindAll("a[href='admin/system-settings']").Should().ContainSingle("settings are in every view");
        cut.FindAll(".nav-group[hidden]").Should().BeEmpty("groups start expanded");

        cut.FindAll("[data-testid=nav-show-other]").Should().BeEmpty("no Show other modules control");

        cut.Find("[data-testid=nav-workspace-view]").Change(nameof(WorkspaceView.AllModules));
        cut.FindAll(".nav-item-outside").Should().BeEmpty();
        cut.FindAll("a[href='source-analysis']").Should().ContainSingle("All Modules lists every eligible module");
    }

    [Fact]
    public void ResolutionModel_FeatureVisibilityOutranksTheView_AndAdditionsNeverShowADisabledModule()
    {
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        features.ApplyLocalFlags(new FeatureVisibilityDto { ApiQualityReview = false, SourceAnalysis = true });
        State.SetView(WorkspaceView.TestLead);
        Module("api-quality-review").Reach.Should().Be(ModuleReach.DisabledByFeatureVisibility, "Feature Visibility outranks the Workspace View");
        Module("source-analysis").Reach.Should().Be(ModuleReach.HiddenByWorkspaceView);
        Module("test-data").Reach.Should().Be(ModuleReach.InView);

        State.AddToView("source-analysis");
        State.AddToView("api-quality-review");
        Module("source-analysis").Reach.Should().Be(ModuleReach.AddedByYou);
        Module("api-quality-review").Reach.Should().Be(ModuleReach.DisabledByFeatureVisibility, "an addition never shows a disabled module");
        Module("azure-environment").Unavailable.Should().BeTrue("product availability is shown on the row, not hidden");
        State.SetView(WorkspaceView.Developer);
        Module("source-analysis").Reach.Should().Be(ModuleReach.InView, "additions are per view");

        NavModule Module(string route) => NavigationModules.All(features, State).Single(m => m.Item.Route == route);
    }

    [Fact]
    public void Search_IsWorkspaceAware_CurrentFirst_ThenOtherWorkspaces_ThenDisabled()
    {
        var features = Services.GetRequiredService<FeatureVisibilityService>();
        features.ApplyLocalFlags(new FeatureVisibilityDto { ApiQualityReview = true, IntegrationQualityReview = false, SourceAnalysis = true });
        State.SetView(WorkspaceView.Developer);

        var results = NavigationModules.Search("quality review", features, State);
        results.Select(r => r.Bucket).Should().BeInAscendingOrder();
        results.Single(r => r.Item.Route == "api-quality-review").Bucket.Should().Be(NavSearchBucket.CurrentWorkspace);
        var other = results.Single(r => r.Item.Route == "frontend-quality-review");
        other.Bucket.Should().Be(NavSearchBucket.OtherWorkspaces);
        other.OtherViews.Select(v => v.View).Should().Contain(WorkspaceView.TestLead);
        results.Single(r => r.Item.Route == "integration-quality-review").Bucket.Should().Be(NavSearchBucket.Disabled);

        NavigationModules.Search("QUALITY rev", features, State).Should().NotBeEmpty("case-insensitive, partial");
        NavigationModules.Search("FQR", features, State).First().Item.Route.Should().Be("frontend-quality-review", "aliases");
        NavigationModules.Search("Quality & Testing", features, State).Should().Contain(r => r.Item.Route == "test-data", "group names");
        NavigationModules.Search("k6", features, State).First().Item.Route.Should().Be("performance-test-review");
        NavigationModules.Search("   ", features, State).Should().BeEmpty();
    }

    [Fact]
    public void SearchBox_GroupsResultsByWorkspace_ArrowKeysMoveTheActiveResult_DisabledIsNotOpened()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto { ApiQualityReview = true, IntegrationQualityReview = false });
        State.SetView(WorkspaceView.Developer);
        var cut = Render<NavMenu>();

        cut.Find("[data-testid=nav-search]").Input("quality review");
        cut.FindAll("[data-testid=nav-search-bucket]").Select(h => h.TextContent).Should().Equal(
            "In current workspace (Developer)", "Outside current view", "Disabled in Feature Visibility");
        var input = cut.Find("[data-testid=nav-search]");
        input.GetAttribute("role").Should().Be("combobox");
        input.GetAttribute("aria-expanded").Should().Be("true");
        var first = input.GetAttribute("aria-activedescendant");
        first.Should().NotBeNull();

        cut.Find("[data-testid=nav-search]").KeyDown("ArrowDown");
        var second = cut.Find("[data-testid=nav-search]").GetAttribute("aria-activedescendant");
        second.Should().NotBe(first);
        cut.Find($"#{second}").GetAttribute("aria-selected").Should().Be("true");
        var disabled = cut.Find("[data-testid=nav-search-result][data-bucket=Disabled]");
        disabled.TagName.Should().Be("SPAN", "a disabled module is marked, not a link");
        disabled.GetAttribute("aria-disabled").Should().Be("true");

        var route = cut.Find($"#{second}").GetAttribute("data-route");
        cut.Find("[data-testid=nav-search]").KeyDown("Enter");
        Location.Should().Be(route);
        State.View.Should().Be(WorkspaceView.Developer, "opening a module of another workspace never switches the view");
    }

    [Fact]
    public void CollapsedSidebar_ShowsGroupIconsWithFlyouts_CompactSearch_AndIsRemembered()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("api-quality-review");
        var cut = Render<NavMenu>();
        cut.Find("[data-testid=nav-collapse-toggle]").GetAttribute("aria-label").Should().Be("Collapse sidebar");

        cut.Find("[data-testid=nav-collapse-toggle]").Click();

        State.SidebarCollapsed.Should().BeTrue();
        cut.Find("[data-testid=nav-collapse-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=nav-collapse-toggle]").GetAttribute("aria-label").Should().Be("Expand sidebar");
        cut.FindAll("[data-testid=nav-workspace-view]").Should().BeEmpty("the rail shows icons, not the full tools");
        var quality = cut.Find("[data-testid=nav-rail-quality-testing]");
        quality.GetAttribute("aria-label").Should().Be("Quality & Testing");
        quality.ClassList.Should().Contain("active", "the current page's group carries the active indicator");
        quality.GetAttribute("aria-expanded").Should().Be("false");

        quality.Click();
        cut.Find("[data-testid=nav-rail-quality-testing]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=nav-flyout][data-flyout=quality-testing]").GetAttribute("role").Should().Be("menu");
        cut.Find("[data-testid=nav-flyout] a[href='api-quality-review']").GetAttribute("aria-current").Should().Be("page");
        cut.Find("[data-testid=nav-flyout-backdrop]").Click();
        cut.FindAll("[data-testid=nav-flyout]").Should().BeEmpty("click outside closes the flyout");

        cut.Find("[data-testid=nav-rail-source-review]").Click();
        cut.Find("[data-testid=nav-panel]").KeyDown("Escape");
        cut.FindAll("[data-testid=nav-flyout]").Should().BeEmpty("Escape closes the flyout");

        cut.Find("[data-testid=nav-rail-search]").Click();
        cut.Find("[data-testid=nav-flyout][data-flyout=search] [data-testid=nav-search]").Input("schema");
        cut.Find("[data-testid=nav-search-result][data-route=schema-studio]").Should().NotBeNull();

        cut.Find("[data-testid=nav-rail-workspace]").Click();
        cut.Find("[data-testid=nav-flyout-view][data-view=TestLead]").Click();
        State.View.Should().Be(WorkspaceView.TestLead);
        Location.Should().Be("api-quality-review");

        var restored = new NavigationSectionState();
        restored.Restore(State.Serialize());
        restored.SidebarCollapsed.Should().BeTrue();
        restored.View.Should().Be(WorkspaceView.TestLead);
    }

    [Fact]
    public void MobileDrawer_OpensAsADialog_ShowsLabelsEvenWhenTheDesktopSidebarIsCollapsed_AndEscapeCloses()
    {
        State.SetSidebarCollapsed(true);
        var cut = Render<NavMenu>();
        cut.Find("[data-testid=nav-drawer-toggle]").GetAttribute("aria-expanded").Should().Be("false");

        cut.Find("[data-testid=nav-drawer-toggle]").Click();
        var panel = cut.Find("[data-testid=nav-panel]");
        panel.GetAttribute("role").Should().Be("dialog");
        panel.GetAttribute("aria-modal").Should().Be("true");
        panel.ClassList.Should().Contain("nav-drawer-open");
        cut.Find("[data-testid=nav-workspace-view]").Should().NotBeNull("the drawer has workspace, search and groups");
        cut.Find("[data-testid=nav-search]").Should().NotBeNull();
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextNav.openDrawer"));

        cut.Find("[data-testid=nav-panel]").KeyDown("Escape");
        cut.Find("[data-testid=nav-panel]").ClassList.Should().NotContain("nav-drawer-open");
        State.SidebarCollapsed.Should().BeTrue("the drawer never changes the desktop preference");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextNav.closeDrawer");
    }

    [Fact]
    public void OutsideViewNotice_KeepsThePageOpen_OffersAddAndSwitch()
    {
        State.SetView(WorkspaceView.ProjectLeader);
        Services.GetRequiredService<NavigationManager>().NavigateTo("schema-studio");
        var cut = Render<WorkspaceScopeNotice>(p => p.Add(x => x.Sections, State));

        cut.Find("[data-testid=nav-outside-view]").TextContent.Should().Contain("Current module is outside your selected workspace view (Project Manager).");
        cut.Find("[data-testid=nav-outside-add]").Click();
        State.IsAdded("schema-studio").Should().BeTrue();
        cut.FindAll("[data-testid=nav-outside-view]").Should().BeEmpty();
        Location.Should().Be("schema-studio");

        Services.GetRequiredService<NavigationManager>().NavigateTo("pipeline-review");
        cut.WaitForAssertion(() => cut.Find("[data-testid=nav-outside-view]"));
        cut.Find("[data-testid=nav-outside-all]").Click();
        State.View.Should().Be(WorkspaceView.AllModules);
        cut.FindAll("[data-testid=nav-outside-view]").Should().BeEmpty();

        Services.GetRequiredService<NavigationManager>().NavigateTo("dashboard");
        State.SetView(WorkspaceView.TestLead);
        cut.FindAll("[data-testid=nav-outside-view]").Should().BeEmpty("Dashboard is in every view");
    }

    [Fact]
    public void EarlierPreferences_StayValid_TechnicalReviewerReadsAsDeveloper()
    {
        var state = new NavigationSectionState();
        state.Restore("""{"View":4,"Choices":{"TechnicalReviewer":{"source-review":true},"AllModules":{}}}""");
        state.View.Should().Be(WorkspaceView.Developer);
        state.IsCollapsed("source-review").Should().BeTrue();
        state.SidebarCollapsed.Should().BeFalse("fields added later default");
    }

    [Fact]
    public void CollapseChoices_AreRememberedPerView_AndSurviveSerialization()
    {
        State.SetView(WorkspaceView.ScrumMaster);
        State.AddToView("schema-studio");
        State.Toggle("delivery-reporting");
        State.IsCollapsed("delivery-reporting").Should().BeTrue();
        State.SetView(WorkspaceView.AllModules);
        State.IsCollapsed("delivery-reporting").Should().BeFalse("each view keeps its own choices");

        var restored = new NavigationSectionState();
        restored.Restore(State.Serialize());
        restored.View.Should().Be(WorkspaceView.AllModules);
        restored.SetView(WorkspaceView.ScrumMaster);
        restored.IsCollapsed("delivery-reporting").Should().BeTrue();
        State.Serialize().Should().NotContain("\"ShowOther\":[", "the former Show other modules choice is no longer written");
        restored.IsAdded("schema-studio").Should().BeTrue();

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
    public void TheCurrentPagesGroup_IsExpanded_AndAPageOutsideTheViewKeepsOneUnbadgedRowWhileOpen()
    {
        State.SetView(WorkspaceView.TestLead);
        State.Toggle("delivery-reporting");
        Services.GetRequiredService<NavigationManager>().NavigateTo("delivery-reporting");
        var cut = Render<NavMenu>();
        cut.Find("#nav-section-delivery-reporting").HasAttribute("hidden").Should().BeFalse();
        var row = cut.Find("a[href='delivery-reporting']");
        row.GetAttribute("aria-current").Should().Be("page");
        row.ParentElement!.ClassList.Should().Contain("nav-item-outside");
        row.QuerySelector("[data-testid=nav-outside-badge]").Should().BeNull("no Other view badge");
        row.TextContent.Should().Contain("open page outside this workspace view", "screen readers hear why the row is there");
        cut.FindAll("a.active").Should().ContainSingle();
        cut.FindAll("[data-outside=true]").Should().ContainSingle("only the open page, no other module outside the view");

        Services.GetRequiredService<NavigationManager>().NavigateTo("dashboard");
        cut.WaitForAssertion(() => cut.FindAll("a[href='delivery-reporting']").Should().BeEmpty("the row leaves with the page"));
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

    [Fact] // The reported defect: a browser that once chose "Show other modules" kept the whole catalogue under the view, marked "Other view".
    public void A_saved_Show_other_modules_choice_no_longer_appends_the_catalogue_to_a_specialized_view()
    {
        State.Restore("""{"View":6,"Choices":{},"SidebarCollapsed":false,"ShowOther":[6,3],"Added":{}}""");
        State.View.Should().Be(WorkspaceView.ManualTesting, "the saved view itself is kept");
        var cut = Render<NavMenu>();
        cut.FindAll("[data-testid=nav-outside-badge]").Should().BeEmpty();
        cut.FindAll("[data-outside=true]").Should().BeEmpty();
        foreach (var route in new[] { NavigationCatalog.DeliveryOverviewRoute, "delivery-reporting", "constitution-explorer", "data-model-explorer", "plan-explorer",
                     "task-explorer", "correlation-review", "release-readiness", "snapshots", "source-analysis", "schema-studio", "api-quality-review/contracts",
                     "integration-suites", "api-quality-review", "integration-quality-review" })
            cut.FindAll($"#nav-groups a[href='{route}']").Should().BeEmpty($"{route} is outside Manual Tester (the modules from the defect report)");
    }
}
