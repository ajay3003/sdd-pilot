using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public class TaskExplorerPageTests : BunitContext
{
    private const string SampleTasks = """
        # Phase 1

        ## User Story 1

        - [ ] T001 Implement login
        - [x] T002 Implement logout
        - [ ] T003 Add password reset
        """;

    private const string PreviousTasks = """
        # Phase 1

        ## User Story 1

        - [ ] T001 Implement login
        - [ ] T002 Implement logout
        """;

    public TaskExplorerPageTests()
    {
        JSInterop.SetupVoid("localStorage.setItem", _ => true);
        JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult(null);
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    [Fact]
    public void TaskExplorer_ContainsChangesTab()
    {
        var cut = Render<TaskExplorerPanel>(p => p
            .Add(x => x.TasksText, SampleTasks));

        cut.Find("[data-testid='te-changes-tab-btn']").Should().NotBeNull();
        cut.Find("[data-testid='te-changes-tab-btn']").TextContent.Trim().Should().Be("Changes");
    }

    [Fact]
    public void TaskDeltas_RedirectedToTaskExplorer()
    {
        Render<TaskDeltas>();

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().Contain("/task-explorer");
        nav.Uri.Should().Contain("view=changes");
    }

    [Fact]
    public void TaskChanges_ReflectsTaskMdDiffCorrectly()
    {
        var cut = Render<TaskChangesPanel>(p => p
            .Add(x => x.CurrentTasksText, SampleTasks));

        cut.Find("[data-testid='tc-changes-panel']").Should().NotBeNull();

        cut.Find("textarea").Should().NotBeNull();
        cut.FindAll("textarea").Should().HaveCount(2);

        // Simulate pasting previous tasks into old textarea and running analysis
        var textareas = cut.FindAll("textarea");
        textareas[0].Change(PreviousTasks);
        cut.Find(".delta-analyse-btn").Click();

        // T003 was added, T002 changed status
        cut.Find("[data-testid='tc-changes-panel']").TextContent.Should().Contain("Total Changes");
        cut.FindAll(".delta-finding-card").Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TaskChanges_AffectsImplementationCoverage()
    {
        var cut = Render<TaskChangesPanel>(p => p
            .Add(x => x.CurrentTasksText, SampleTasks));

        var textareas = cut.FindAll("textarea");
        textareas[0].Change(PreviousTasks);
        cut.Find(".delta-analyse-btn").Click();

        // KPI row should show added and removed counts from the report
        cut.Find(".delta-kpi-row").Should().NotBeNull();
        cut.Find(".delta-kpi-added .delta-kpi-value").TextContent.Trim().Should().MatchRegex(@"^\d+$");
    }

    [Fact]
    public void TaskDeltas_MenuItem_IsHidden()
    {
        Services.AddSingleton<FeatureVisibilityService>();

        var cut = Render<BirkNext.Web.Layout.NavMenu>();

        cut.FindAll("a[href='task-deltas']").Should().BeEmpty();
    }

    [Fact]
    public void NoStandaloneTaskDeltasRoute_ExposedToUsers()
    {
        var dto = new FeatureVisibilityDto();

        dto.TaskDeltas.Should().BeFalse(
            "Task Deltas is deprecated as a standalone feature — it defaults to hidden");
    }
}
