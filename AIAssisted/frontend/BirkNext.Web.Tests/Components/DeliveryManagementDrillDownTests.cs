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
        page.WaitForAssertion(() => page.FindAll("[data-testid=do-item], [data-testid=dlr-feature-detail]").Should().NotBeEmpty());
        return page;
    }

    [Fact]
    public void Overview_ReadinessSeparatesChecklistFromVerification_AndFeaturesOpenInTheExplorer()
    {
        var page = Overview();
        var dims = page.FindAll("[data-testid=do-readiness-dim]").ToDictionary(d => d.GetAttribute("data-key")!, d => d.TextContent);
        dims["checklist"].Should().Contain("checked").And.Contain("not delivery progress");
        dims["implementation"].Should().Contain("Not available");
        dims["qa"].Should().Contain("Not available");
        page.FindAll("[data-testid=do-item]").Should().OnlyContain(r => !r.TextContent.Contains("Implemented"));

        page.FindAll("[data-testid=do-feature-open]").First().Click();
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
    public void Overview_FiltersSearchAndSorts()
    {
        var page = Overview();
        page.FindAll("[data-testid=do-item]").Should().HaveCount(2);
        page.Find("[data-testid=do-search]").Input("case");
        page.FindAll("[data-testid=do-item]").Should().ContainSingle().Which.TextContent.Should().Contain("Case overview");
        page.Find("[data-testid=do-search]").Input("");
        page.FindAll("[data-testid=do-sort]").Single(s => s.GetAttribute("data-key") == "title").Click();
        page.FindAll("[data-testid=do-item] th").Select(t => t.TextContent.Trim()).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        page.FindAll("[data-testid=do-sort]").Single(s => s.GetAttribute("data-key") == "title").Click();
        page.FindAll("[data-testid=do-item] th").Select(t => t.TextContent.Trim()).Should().BeInDescendingOrder(StringComparer.OrdinalIgnoreCase);
        page.Find("[data-testid=do-count]").TextContent.Should().Be("2 of 2 features shown");
    }
}
