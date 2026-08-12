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

    // ── Phase Metadata Parsing and Service Tests ──────────────────────────

    [Fact]
    public void PhasePurpose_IsParsedFromMarkdown()
    {
        var markdown = """
            # Tasks

            ## Phase 1

            **Purpose**: Setup the initial project structure

            - [x] T001 Create project
            """;

        var tree = TaskExplorerService.Parse(markdown);
        var phase = tree.Roots.FirstOrDefault()?.Children.FirstOrDefault(c => c.NodeType == TaskNodeType.Phase);

        phase.Should().NotBeNull("Phase should be parsed");
        phase!.PhasePurpose.Should().NotBeNullOrEmpty("Purpose should be extracted from markdown");
        phase.PhasePurpose.Should().Contain("Setup the initial");
    }

    [Fact]
    public void PhaseGoal_IsParsedFromMarkdown()
    {
        var markdown = """
            # Tasks

            ## Phase 1

            **Goal**: Complete all foundational tasks

            - [x] T001 Create project
            """;

        var tree = TaskExplorerService.Parse(markdown);
        var phase = tree.Roots.FirstOrDefault()?.Children.FirstOrDefault(c => c.NodeType == TaskNodeType.Phase);

        phase.Should().NotBeNull();
        phase!.PhaseGoal.Should().NotBeNullOrEmpty("Goal should be extracted from markdown");
        phase.PhaseGoal.Should().Contain("foundational");
    }

    [Fact]
    public void PhaseIndependentTest_IsParsedFromMarkdown()
    {
        var markdown = """
            # Tasks

            ## Phase 1

            **Independent Test**: Verify project builds successfully

            - [x] T001 Create project
            """;

        var tree = TaskExplorerService.Parse(markdown);
        var phase = tree.Roots.FirstOrDefault()?.Children.FirstOrDefault(c => c.NodeType == TaskNodeType.Phase);

        phase.Should().NotBeNull();
        phase!.PhaseIndependentTest.Should().NotBeNullOrEmpty("Independent Test should be extracted");
        phase.PhaseIndependentTest.Should().Contain("builds");
    }

    [Fact]
    public void PhaseCheckpoint_IsParsedFromMarkdown()
    {
        var markdown = """
            # Tasks

            ## Phase 1

            **Checkpoint**: All foundational setup complete

            - [x] T001 Create project
            """;

        var tree = TaskExplorerService.Parse(markdown);
        var phase = tree.Roots.FirstOrDefault()?.Children.FirstOrDefault(c => c.NodeType == TaskNodeType.Phase);

        phase.Should().NotBeNull();
        phase!.PhaseCheckpoint.Should().NotBeNullOrEmpty("Checkpoint should be extracted");
        phase.PhaseCheckpoint.Should().Contain("foundational");
    }

    [Fact]
    public void MissingMetadata_IsNotStored()
    {
        var markdown = """
            # Tasks

            ## Phase 1

            - [x] T001 Create project
            """;

        var tree = TaskExplorerService.Parse(markdown);
        var phase = tree.Roots.FirstOrDefault()?.Children.FirstOrDefault(c => c.NodeType == TaskNodeType.Phase);

        phase.Should().NotBeNull();
        phase!.PhasePurpose.Should().BeNullOrEmpty("Purpose should not be stored when missing");
        phase.PhaseGoal.Should().BeNullOrEmpty("Goal should not be stored when missing");
        phase.PhaseIndependentTest.Should().BeNullOrEmpty("Independent Test should not be stored when missing");
        phase.PhaseCheckpoint.Should().BeNullOrEmpty("Checkpoint should not be stored when missing");
    }

    [Fact]
    public void AllMetadata_IsParsedWhenPresent()
    {
        var markdown = """
            # Tasks

            ## Phase 1

            **Purpose**: Setup phase

            **Goal**: Complete setup

            **Independent Test**: Verify it works

            - [x] T001 Create project
            - [x] T002 Configure

            **Checkpoint**: Setup complete
            """;

        var tree = TaskExplorerService.Parse(markdown);
        var phase = tree.Roots.FirstOrDefault()?.Children.FirstOrDefault(c => c.NodeType == TaskNodeType.Phase);

        phase.Should().NotBeNull();
        phase!.PhasePurpose.Should().Contain("Setup");
        phase.PhaseGoal.Should().Contain("Complete");
        phase.PhaseIndependentTest.Should().Contain("Verify");
        phase.PhaseCheckpoint.Should().Contain("complete");
    }

    [Fact]
    public void Metadata_HandlesMarkdownFormatting()
    {
        var markdown = """
            # Tasks

            ## Phase 1

            **Purpose**: Create a **bold** and *italic* project structure with `code`

            - [x] T001 Create project
            """;

        var tree = TaskExplorerService.Parse(markdown);
        var phase = tree.Roots.FirstOrDefault()?.Children.FirstOrDefault(c => c.NodeType == TaskNodeType.Phase);

        phase.Should().NotBeNull();
        phase!.PhasePurpose.Should().NotBeNullOrEmpty("Markdown formatting should be preserved in metadata");
        phase.PhasePurpose.Should().Contain("**bold**");
        phase.PhasePurpose.Should().Contain("*italic*");
        phase.PhasePurpose.Should().Contain("`code`");
    }
}
