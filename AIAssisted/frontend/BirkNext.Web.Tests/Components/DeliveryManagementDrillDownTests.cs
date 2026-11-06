using BirkNext.DeliveryReporting;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Operations;
using BirkNext.Web.Tests.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Delivery Management drill-down: the Phases tab is a table of every extracted phase (filterable, expandable to its actual tasks, linking to
/// its stories); the Tasks tab finds individual tasks by text, phase, story and state and pages large features; Delivery Overview opens a
/// feature in the same explorer and keeps the project; checklist completion is never shown as implemented or verified.
/// </summary>
public sealed class DeliveryManagementDrillDownTests : BunitContext
{
    public DeliveryManagementDrillDownTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>A feature with <paramref name="phases"/> phases of <paramref name="perPhase"/> tasks, two stories, and the last task open.</summary>
    private static DeliveryStoryRow Feature(int phases, int perPhase)
    {
        var tasks = Enumerable.Range(0, phases * perPhase).Select(i => new DeliveryTaskRow($"T{i:0000}", $"T{i:0000}", $"Task number {i}", i != phases * perPhase - 1, DeliveryTaskChange.Unchanged)
        { PhaseKey = $"phase-{i / perPhase + 1}", UserStoryKey = i % 3 == 0 ? "US1" : i % 3 == 1 ? "US2" : null }).ToList();
        var h = new DeliveryFeatureHierarchy
        {
            Captured = true,
            Phases = [.. Enumerable.Range(1, phases).Select(p => new DeliveryPhaseRow($"phase-{p}", $"Phase {p}: Work package {p}", DeliveryItemChange.Unchanged)
            {
                Position = p - 1, TaskTotal = perPhase, TaskCompleted = tasks.Count(t => t.PhaseKey == $"phase-{p}" && t.Completed), StoryKeys = p == 1 ? ["US1", "US2"] : [],
            })],
            Stories = [new("US1", "Register child", DeliveryItemChange.Unchanged) { PhaseKeys = ["phase-1"], TaskTotal = tasks.Count(t => t.UserStoryKey == "US1") },
                       new("US2", "Audit", DeliveryItemChange.Unchanged) { PhaseKeys = ["phase-1"], TaskTotal = tasks.Count(t => t.UserStoryKey == "US2") }],
            Tasks = tasks,
        };
        return new DeliveryStoryRow
        {
            FeatureKey = "skoleservice::001-skoleservice", Title = "School attendance reporting", Module = "SkoleService", Movement = DeliveryMovement.NoSignificantChange,
            CurrentStatus = DeliveryStatus.InProgress, CurrentTaskCompleted = tasks.Count(t => t.Completed), CurrentTaskTotal = tasks.Count, Hierarchy = h,
        };
    }

    private IRenderedComponent<DeliveryFeatureExplorer> Open(DeliveryStoryRow row, string tab)
    {
        var report = new WeeklyDeliveryReport { Version = 2, ProjectName = "M2LB", WeekLabel = "2026-W44", Stories = [row] };
        var cut = Render<DeliveryFeatureExplorer>(p => p.Add(x => x.Report, report).Add(x => x.OpenFeatureKey, row.FeatureKey));
        cut.FindAll("[data-testid=dlr-feature-tab]").Single(t => t.GetAttribute("data-tab") == tab).Click();
        return cut;
    }

    [Fact]
    public void PhasesTab_ShowsEveryPhase_WithChecklistCounts_AndExpandsToItsTasks()
    {
        var cut = Open(Feature(13, 5), "phases");
        cut.FindAll("[data-testid=dlr-phase]").Should().HaveCount(13, "a non-zero phase count always has its rows");
        cut.Find("[data-testid=dlr-phase-count]").TextContent.Should().StartWith("13 of 13 phases");
        cut.Find("[data-testid=dlr-phase][data-phase=phase-13] [data-testid=dlr-phase-open]").TextContent.Should().Be("1");
        cut.Find("[data-testid=dlr-phase][data-phase=phase-13]").TextContent.Should().Contain("Not verified", "a checked task is not verified implementation");

        cut.Find("[data-testid=dlr-phase][data-phase=phase-13] [data-testid=dlr-phase-toggle]").Click();
        var detail = cut.Find("[data-testid=dlr-phase-detail][data-phase=phase-13]");
        detail.QuerySelectorAll("[data-testid=dlr-task]").Should().HaveCount(5);
        cut.Find("[data-testid=dlr-phase][data-phase=phase-13] [data-testid=dlr-phase-toggle]").GetAttribute("aria-expanded").Should().Be("true");

        cut.FindAll("[data-testid=dlr-phase-filter]").Single(f => f.GetAttribute("data-filter") == "open").Change(true);
        cut.FindAll("[data-testid=dlr-phase]").Select(p => p.GetAttribute("data-phase")).Should().Equal("phase-13");
    }

    [Fact]
    public void PhaseStory_NavigatesToTheStory()
    {
        var cut = Open(Feature(2, 3), "phases");
        cut.FindAll("[data-testid=dlr-phase-story]").First(b => b.TextContent.StartsWith("US2")).Click();
        cut.Find("[data-testid=dlr-feature-panel]").GetAttribute("data-tab").Should().Be("stories");
        cut.Find("[data-testid=dlr-breadcrumb]").TextContent.Should().Contain("US2");
    }

    [Fact]
    public void TasksTab_HandlesMoreThan1000Tasks_InPages_AndReconcilesWithTheSummary()
    {
        var row = Feature(12, 100); // 1,200 tasks
        var cut = Open(row, "tasks");
        cut.Find("[data-testid=dlr-task-totals]").TextContent.Should().StartWith("1199 / 1200 checked · 1 open");
        cut.FindAll("[data-testid=dlr-task]").Should().HaveCount(100, "large features never render every task at once");
        cut.Find("[data-testid=dlr-task-count]").TextContent.Should().StartWith("100 of 1200");
        cut.Find("[data-testid=dlr-task-more]").Click();
        cut.FindAll("[data-testid=dlr-task]").Should().HaveCount(200);
    }

    [Fact]
    public void TasksTab_FiltersByPhaseStorySearchAndState()
    {
        var cut = Open(Feature(3, 6), "tasks");
        cut.Find("[data-testid=dlr-task-phase]").Change("phase-2");
        cut.FindAll("[data-testid=dlr-task]").Should().HaveCount(6);
        cut.Find("[data-testid=dlr-task-story]").Change("US1");
        cut.FindAll("[data-testid=dlr-task]").Should().HaveCount(2);
        cut.Find("[data-testid=dlr-task-story]").Change("(none)");
        cut.FindAll("[data-testid=dlr-task]").Should().HaveCount(2, "tasks without a user story stay visible");
        cut.Find("[data-testid=dlr-task-phase]").Change("");
        cut.Find("[data-testid=dlr-task-story]").Change("");
        cut.Find("[data-testid=dlr-task-search]").Input("number 17");
        cut.FindAll("[data-testid=dlr-task]").Should().ContainSingle().Which.GetAttribute("data-completed").Should().Be("false");
        cut.Find("[data-testid=dlr-task-search]").Input("");
        cut.FindAll("[data-testid=dlr-task-filter]").Single(f => f.GetAttribute("data-filter") == "unlinked").Change(true);
        cut.FindAll("[data-testid=dlr-task]").Should().HaveCount(6);
    }

    [Fact]
    public void MissingFeature_UnderIncompleteCoverage_IsLabelledMissing_NotRemoved()
    {
        var row = new DeliveryStoryRow { FeatureKey = "tjeneste::014-x", Title = "Children in placements", Module = "Tjeneste", Movement = DeliveryMovement.NeedsReview, RemovedFeature = true, RemovalUnconfirmed = true };
        var report = new WeeklyDeliveryReport { Version = 2, Stories = [row], ScopeChanges = [new("Missing", row.FeatureKey, row.Title, "Tjeneste", "Not found …")] };
        var cut = Render<DeliveryFeatureExplorer>(p => p.Add(x => x.Report, report));
        cut.Find("[data-testid=dlr-scope-change]").TextContent.Should().Contain("Missing — coverage incomplete").And.NotContain("New this period");
    }

    // ── Delivery Overview ─────────────────────────────────────────────────────────────────────────────────────────

    private IRenderedComponent<BirkNext.Web.Pages.DeliveryOverview> Overview(string query = "")
    {
        Services.AddSingleton<IDeliveryReportingApiService>(new DeliveryReportingPageTests.FakeApi());
        Services.AddSingleton(new BackgroundOperationService());
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-overview?project={DeliveryReportingPageTests.ProjectId}{query}");
        var page = Render<BirkNext.Web.Pages.DeliveryOverview>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=dd-header], [data-testid=dlr-feature-detail]").Should().NotBeEmpty());
        return page;
    }

    private void Show(string section) => Services.GetRequiredService<NavigationManager>().NavigateTo(
        Services.GetRequiredService<NavigationManager>().GetUriWithQueryParameter("section", section));

    [Fact]
    public void Overview_ReadinessSeparatesChecklistFromVerification_AndFeaturesOpenInTheExplorer()
    {
        var page = Overview();
        page.Find("[data-testid=dd-checklist-note]").TextContent.Should().Contain("checkbox state in tasks.md").And.Contain("not QA verification, acceptance or deployment");
        var quality = page.FindAll("[data-testid=dd-quality-row]").ToDictionary(d => d.GetAttribute("data-key")!, d => d.QuerySelector("[data-testid=dd-quality-state]")!.TextContent);
        quality["implementation"].Should().Be("Not recorded", "checked tasks are not implementation verification");
        quality["qa"].Should().Be("Not assessed", "no QA evidence is linked — never 0%");
        quality["acceptance"].Should().Be("Not recorded");
        page.FindAll("[data-testid=dd-kpi]").Select(k => k.GetAttribute("data-key")).Should().Contain(["checklist", "open", "in-progress", "review", "blocked"]);
        page.Markup.Should().NotContain("On track").And.NotContain("Health score");
        Show("features");
        page.FindAll("[data-testid=dfl-card]").Should().HaveCount(2).And.OnlyContain(r => !r.TextContent.Contains("Implemented"));
        page.FindAll("[data-testid=dfl-evidence]").Should().OnlyContain(e => e.TextContent == "Not verified", "a checked checklist is never verification");

        // Tasks live in Technical details: the shared explorer's Features view opens the same details panel, and from there the feature explorer.
        Show("technical");
        page.FindAll("[data-testid=dws-feature-open]").First().Click();
        page.WaitForAssertion(() => page.Find("[data-testid=dws-detail-backlog]").TextContent.Should().NotContain("Implemented"));
        page.Find("[data-testid=dws-open-feature]").Click();
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"project={DeliveryReportingPageTests.ProjectId}").And.Contain("feature=");
    }

    [Fact]
    public void Overview_WithAFeatureQuery_ShowsItsDetail_AndBackKeepsTheProject()
    {
        var page = Overview("&feature=frontend%3A%3A001-child-search");
        page.WaitForAssertion(() => page.Find("[data-testid=dlr-feature-detail]"));
        page.Find("#dfx-detail-h").TextContent.Should().Be("Child search");
        page.Find("[data-testid=dlr-feature-back]").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().Contain($"project={DeliveryReportingPageTests.ProjectId}").And.NotContain("feature=");
    }

    [Fact]
    public void Overview_FiltersSearch_AndOrdersByPriority()
    {
        var page = Overview("&section=features");
        page.WaitForAssertion(() => page.FindAll("[data-testid=dfl-card]").Should().HaveCount(2));
        page.Find("[data-testid=do-search]").Input("case");
        page.WaitForAssertion(() => page.FindAll("[data-testid=dfl-card]").Should().ContainSingle().Which.TextContent.Should().Contain("Case overview"));
        page.Find("[data-testid=do-search]").Input("");
        page.WaitForAssertion(() => page.Find("[data-testid=do-count]").TextContent.Should().StartWith("2 of 2 features"));
        page.FindAll("[data-testid=dfl-status]").Select(c => c.TextContent).Should().OnlyContain(s => s.Length > 0 && s != "—", "every feature shows its delivery status");
    }

    [Fact]
    public void Overview_states_its_purpose_and_current_snapshot_scope_and_checklist_is_not_verification()
    {
        var page = Overview();
        page.Find("h1").TextContent.Should().Be("Delivery Overview");
        page.Find("[data-testid=do-lead]").TextContent.Should().Be("Where delivery stands now: features, verified completion, risks and what needs a decision.");
        page.Find("[data-testid=do-checklist-clarification]").TextContent.Should().Contain("source checkboxes, not verified implementation or QA completion");
        page.Find("[data-testid=do-context]").TextContent.Should().StartWith("Current snapshot: ").And.EndWith("2 features");
        page.FindAll(".do-filter-note").Should().BeEmpty("no paragraph explaining the filter implementation");
    }

    [Fact]
    public void Overview_work_items_use_the_shared_explorer_in_current_state_mode_and_keep_filters_and_selection_in_the_url()
    {
        Services.AddSingleton<IDeliveryReportingApiService>(new DeliveryReportingPageTests.FakeApi());
        Services.AddSingleton(new BackgroundOperationService());
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"delivery-overview?project={DeliveryReportingPageTests.ProjectId}&section=technical&state=InProgress");
        var page = Render<BirkNext.Web.Pages.DeliveryOverview>();
        page.WaitForAssertion(() => page.Find("[data-testid=dlr-workspace]").GetAttribute("data-mode").Should().Be("CurrentState"));
        page.Find("[data-testid=dws-master]").GetAttribute("data-view").Should().Be("features", "the backlog opens on the Features view of the shared explorer");
        page.FindAll("[data-testid=dws-view-board]").Should().BeEmpty();
        page.Find("[data-testid=dws-view-hierarchy]").Change(true);
        nav.Uri.Should().Contain("wi-view=hierarchy").And.Contain("state=InProgress", "switching the view keeps the page's filters");
        page.FindAll("[data-testid=dws-module-toggle]")[0].Click();
        page.FindAll("[data-testid=dws-item]")[0].Click();
        nav.Uri.Should().Contain("item=f%3A", "the selection is the canonical id, deep-linkable and shared with Delivery Reports").And.Contain("wi-view=hierarchy");
        page.WaitForAssertion(() => page.Find("[data-testid=dws-cross-page]").GetAttribute("href").Should().StartWith("delivery-reporting?").And.Contain("tab=stories"));

        page.Find("[data-testid=dws-view-features]").Change(true);
        nav.Uri.Should().NotContain("wi-view=").And.Contain("item=f%3A").And.Contain("state=InProgress");
        page.Find("[data-testid=dws-feature-row].is-selected, [data-testid=dws-feature-row][aria-selected=true]").Should().NotBeNull("the selection carries over between views");
    }
}
