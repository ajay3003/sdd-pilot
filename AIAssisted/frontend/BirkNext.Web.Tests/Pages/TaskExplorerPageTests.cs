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
        Services.AddSingleton<MarkdownRenderingService>();
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
    public void TaskDetails_ShowsInTreeWhenTaskSelected()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, SampleTasks));

        ClickTaskRow(cut, "T001");

        cut.Find("[data-testid='te-task-details']").TextContent.Should().Contain("Task Details");
        cut.Find(".te-main").GetAttribute("class").Should().Contain("has-details");
    }

    [Fact]
    public void TaskDetails_HidesWhenSwitchingTreeToImpact()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, SampleTasks));

        ClickTaskRow(cut, "T001");
        ClickTab(cut, "Impact");

        cut.FindAll("[data-testid='te-task-details']").Should().BeEmpty();
        cut.Find(".te-main").GetAttribute("class").Should().NotContain("has-details");
    }

    [Fact]
    public void TaskDetails_HidesInDependencies()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, SampleTasks));

        ClickTaskRow(cut, "T001");
        ClickTab(cut, "Dependencies");

        cut.FindAll("[data-testid='te-task-details']").Should().BeEmpty();
        cut.Find(".te-main").GetAttribute("class").Should().NotContain("has-details");
    }

    [Fact]
    public void TaskDetails_HidesInParallelByDefault()
    {
        var parallelTasks = """
            # Phase 1

            - [ ] T001 Implement login [P]
            - [ ] T002 Implement logout
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, parallelTasks));

        ClickTaskRow(cut, "T001");
        ClickTab(cut, "Parallel");

        cut.FindAll("[data-testid='te-task-details']").Should().BeEmpty();
        cut.Find(".te-main").GetAttribute("class").Should().NotContain("has-details");
    }

    [Fact]
    public void TaskDetails_HidesInChanges()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, SampleTasks));

        ClickTaskRow(cut, "T001");
        ClickTab(cut, "Changes");

        cut.FindAll("[data-testid='te-task-details']").Should().BeEmpty();
        cut.Find(".te-main").GetAttribute("class").Should().NotContain("has-details");
    }

    [Fact]
    public void Changes_UsesFullWidthWhenDrawerHidden()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, SampleTasks));

        ClickTaskRow(cut, "T001");
        ClickTab(cut, "Changes");

        cut.Find("[data-testid='te-changes-view']").Should().NotBeNull();
        cut.Find(".te-main").GetAttribute("class").Should().Be("te-main ");
    }

    [Fact]
    public void ReturningToTree_PreservesSelection()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, SampleTasks));

        ClickTaskRow(cut, "T001");
        ClickTab(cut, "Impact");
        ClickTab(cut, "Tree");

        cut.Find("[data-testid='te-task-details']").TextContent.Should().Contain("Task Details");
        cut.Find(".te-main").GetAttribute("class").Should().Contain("has-details");
        cut.FindAll(".te-row.is-selected").Should().ContainSingle(row => row.TextContent.Contains("T001"));
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

    // ── Dependencies Tab Tests ──────────────────────────────────────

    [Fact]
    public void DependenciesTab_RenderExplicitDependencies()
    {
        var tasksWithDeps = """
            # Tasks

            - [ ] T001 Test 1
            - [ ] T002 Test 2
            - [ ] T003 Service

            ## Dependencies & Execution Order

            ### User Story Internal Dependencies

            - **US1**: T001/T002 [P] → T003
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksWithDeps));

        // Switch to Dependencies tab
        var depTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depTabBtn.Should().NotBeNull("Dependencies tab button should exist");
        depTabBtn!.Click();

        // Verify dependencies are rendered
        var depView = cut.Find("[data-testid='te-dependencies-view']");
        depView.Should().NotBeNull("Dependencies view should be visible");

        // Should show task IDs (as badges or in relationships)
        var taskIdBadges = cut.FindAll(".te-dep-task-id-badge").Select(e => e.TextContent.Trim()).ToList();
        var relatedBadges = cut.FindAll(".te-dep-related-badge").Select(e => e.TextContent.Trim()).ToList();
        var allTaskIds = taskIdBadges.Union(relatedBadges).ToList();
        allTaskIds.Should().NotBeEmpty("Task IDs should be rendered");
        allTaskIds.Should().Contain("T001", "T001 should appear in dependencies");
        allTaskIds.Should().Contain("T002", "T002 should appear in dependencies");
        allTaskIds.Should().Contain("T003", "T003 should appear in dependencies");
    }

    [Fact]
    public void DependenciesTab_NoDependencies_ShowsEmptyState()
    {
        var tasksNoDeps = """
            # Tasks

            - [ ] T001 Test 1
            - [ ] T002 Test 2
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksNoDeps));

        // Switch to Dependencies tab
        var depTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depTabBtn!.Click();

        // Should show empty state
        var emptyMsg = cut.Find(".te-empty-message");
        emptyMsg.Should().NotBeNull("Empty state should be shown");
        emptyMsg.TextContent.Should().Contain("No explicit task dependencies found", "Should show correct empty state message");
    }

    [Fact]
    public void DependenciesTab_MultipleUserStories_GroupsCorrectly()
    {
        var tasksMultipleUS = """
            # Tasks

            ## Phase 1

            ### US1
            - [ ] T018 Test 1
            - [ ] T019 Test 2
            - [ ] T020 Service
            - [ ] T021 Endpoint

            ### US2
            - [ ] T024 Auth
            - [ ] T025 Config

            ## Dependencies & Execution Order

            ### User Story Internal Dependencies

            - **US1**: T018/T019 [P] → T020 → T021
            - **US2**: T024 [P] → T025
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksMultipleUS));

        // Switch to Dependencies tab
        var depTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depTabBtn!.Click();

        // Should render US1 and US2 story cards
        var storyCards = cut.FindAll(".te-dep-story-card");
        storyCards.Should().NotBeEmpty("Story cards should be rendered");

        // Should show task IDs from both user stories (as badges or in relationships)
        var taskIdBadges = cut.FindAll(".te-dep-task-id-badge").Select(e => e.TextContent.Trim()).ToList();
        var relatedBadges = cut.FindAll(".te-dep-related-badge").Select(e => e.TextContent.Trim()).ToList();
        var allTaskIds = taskIdBadges.Union(relatedBadges).ToList();
        allTaskIds.Should().Contain("T018", "T018 should appear");
        allTaskIds.Should().Contain("T019", "T019 should appear");
        allTaskIds.Should().Contain("T020", "T020 should appear");
        allTaskIds.Should().Contain("T021", "T021 should appear");
        allTaskIds.Should().Contain("T024", "T024 should appear");
        allTaskIds.Should().Contain("T025", "T025 should appear");
    }

    // ── Parallel Work Tab Tests ──────────────────────────────────

    [Fact]
    public void ParallelWork_RendersParallelTasks()
    {
        var tasksWithParallel = """
            # Tasks

            ## Phase 1

            ### US1
            - [x] T001 [P] Setup base structure
            - [x] T002 [P] Create config
            - [ ] T003 Integration layer
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksWithParallel));

        // Switch to Parallel Work tab
        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn.Should().NotBeNull("Parallel Work tab button should exist");
        parallelTabBtn!.Click();

        // Should show parallel tasks
        var parallelView = cut.Find(".te-parallel-list");
        parallelView.Should().NotBeNull("Parallel view should be visible");

        var taskIds = cut.FindAll(".te-ptask-id").Select(e => e.TextContent.Trim()).ToList();
        taskIds.Should().Contain("T001", "T001 should be marked [P]");
        taskIds.Should().Contain("T002", "T002 should be marked [P]");
        taskIds.Should().NotContain("T003", "T003 is not marked [P]");
    }

    [Fact]
    public void ParallelWork_GroupsByPhase()
    {
        var tasksGrouped = """
            # Tasks

            ## Phase 1
            - [x] T001 [P] Setup
            - [x] T002 [P] Config

            ## Phase 2
            - [ ] T003 [P] Endpoint
            - [ ] T004 [P] Service
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksGrouped));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        // Should group by phase
        var phaseGroups = cut.FindAll(".te-parallel-group");
        phaseGroups.Should().HaveCountGreaterThan(0, "Phase groups should be rendered");

        // Each group should have a header
        var groupHeaders = cut.FindAll(".te-parallel-group-header");
        groupHeaders.Should().NotBeEmpty("Phase group headers should be rendered");
    }

    [Fact]
    public void ParallelWork_RendersCorrectTotalCount()
    {
        var tasksWithParallel = """
            # Tasks

            ## Phase 1
            - [x] T001 [P] Task 1
            - [x] T002 [P] Task 2

            ## Phase 2
            - [ ] T003 [P] Task 3
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksWithParallel));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        // Should show overall count
        var summary = cut.Find(".te-parallel-summary");
        summary.Should().NotBeNull("Summary should be shown");
        summary.TextContent.Should().Contain("3 parallel tasks", "Should show correct total count");
    }

    [Fact]
    public void ParallelWork_RendersSuffixedTaskId()
    {
        var tasksWithSuffix = """
            # Tasks

            ## Phase 1
            - [ ] T033 [P] Main task
            - [ ] T033a [P] Sub task
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksWithSuffix));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        // Should render both T033 and T033a
        var taskIds = cut.FindAll(".te-ptask-id").Select(e => e.TextContent.Trim()).ToList();
        taskIds.Should().Contain("T033", "T033 should appear");
        taskIds.Should().Contain("T033a", "T033a should appear as separate task");
    }

    [Fact]
    public void ParallelWork_DoesNotRenderNonParallelTasks()
    {
        var tasksWithoutParallel = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup (no parallel marker)
            - [ ] T002 [P] Config (marked parallel)
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksWithoutParallel));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var taskIds = cut.FindAll(".te-ptask-id").Select(e => e.TextContent.Trim()).ToList();
        taskIds.Should().Contain("T002", "T002 is marked [P]");
        taskIds.Should().NotContain("T001", "T001 is not marked [P]");
    }

    [Fact]
    public void ParallelWork_NoParallelTasks_ShowsEmptyState()
    {
        var tasksNonParallel = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            - [ ] T002 Config
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksNonParallel));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        // Should show empty state
        var emptyMsg = cut.Find(".te-empty-message");
        emptyMsg.Should().NotBeNull("Empty state should be shown");
        emptyMsg.TextContent.Should().Contain("No parallelizable tasks found", "Should show correct empty message");
    }

    [Fact]
    public void ParallelWork_RendersTaskTitles()
    {
        var tasksWithTitles = """
            # Tasks

            ## Phase 1
            - [ ] T001 [P] Feature A
            - [ ] T002 [P] Feature B
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksWithTitles));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        // Should show task titles
        var titles = cut.FindAll(".te-ptask-primary");
        titles.Should().NotBeEmpty("Task titles should be rendered");
        var titleTexts = titles.Select(t => t.TextContent.Trim()).ToList();
        titleTexts.Should().Contain(s => s.Contains("Feature"), "Features should appear");
    }

    [Fact]
    public void ParallelWork_RendersCompletionStatus()
    {
        var tasksWithStatus = """
            # Tasks

            ## Phase 1
            - [x] T001 [P] Done task
            - [ ] T002 [P] Open task
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksWithStatus));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        // Completed tasks should show status checkmark
        var statuses = cut.FindAll(".te-ptask-status");
        statuses.Should().NotBeEmpty("Completion status should be shown for completed tasks");
    }

    // ── Changes Tab Tests ──────────────────────────────────────

    [Fact]
    public void ChangesTab_NoData_ShowsEmptyState()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        // Switch to Changes tab
        var changesTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Changes"));
        changesTabBtn.Should().NotBeNull("Changes tab button should exist");
        changesTabBtn!.Click();

        // Should show empty state message
        var changesView = cut.Find(".te-changes-view");
        changesView.TextContent.Should().Contain("No comparison available", "Should show comparison unavailable message");
    }

    [Fact]
    public void ChangesTab_DoesNotRenderBlankContent()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var changesTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Changes"));
        changesTabBtn!.Click();

        // Should NOT show a blank/empty area - should show instructional content
        var changesView = cut.Find(".te-changes-view");
        changesView.TextContent.Trim().Should().NotBeEmpty("Changes view should not be completely empty");

        // Should show compact guidance
        changesView.TextContent.Should().Contain("No comparison available", "Should show comparison unavailable message");
    }

    [Fact]
    public void ChangesTab_WithCurrentTask_HasInputPanels()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var changesTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Changes"));
        changesTabBtn!.Click();

        // The component should render input areas when no report exists
        var changesView = cut.Find(".te-changes-view");
        changesView.TextContent.Should().Contain("Previous", "Should show previous version input");
        changesView.TextContent.Should().Contain("Current", "Should show current version input");
        changesView.TextContent.Should().Contain("Analyze", "Should show analyze button");
    }

    [Fact]
    public void ChangesTab_NoBaseline_ShowsComparisonUnavailableMessage()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var changesTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Changes"));
        changesTabBtn!.Click();

        // Should show no-comparison message
        var changesView = cut.Find(".te-changes-view");
        changesView.TextContent.Should().Contain("No comparison available", "Should distinguish no-baseline case");
        changesView.TextContent.Should().Contain("previous", "Should explain what's needed");
        var noBaselineHint = cut.Find(".delta-no-baseline-hint");
        noBaselineHint.Should().NotBeNull("Should show compact baseline hint");
    }

    // ── Map View Tests ──────────────────────────────────────

    [Fact]
    public void MapTab_Click_SelectsMapView()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        // Click Map tab
        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn.Should().NotBeNull("Map tab button should exist");
        mapTabBtn!.Click();

        // Verify Map view is rendered
        var mapView = cut.Find(".te-map");
        mapView.Should().NotBeNull("Map view should be rendered when Map tab is selected");
    }

    [Fact]
    public void MapView_DoesNotRenderExpandButtons()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [x] T001 Setup
            - [ ] T002 Config
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        // Map view should NOT have expand/collapse buttons
        var expandButtons = cut.FindAll(".te-expand-btn");
        expandButtons.Should().BeEmpty("Map view should not render expand/collapse buttons");
    }

    [Fact]
    public void MapView_RendersMapContainer()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Task

            ## Dependencies
            ### User Story Internal Dependencies
            - **US1**: T001 [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        // Should render .te-map container (not .te-tree)
        var mapContainer = cut.FindAll(".te-map");
        mapContainer.Should().NotBeEmpty("Map container should be rendered");

        var treeContainers = cut.FindAll(".te-tree");
        treeContainers.Should().BeEmpty("Tree container should not be rendered in Map view");
    }

    [Fact]
    public void TreeView_StillRendersTreeContainer()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        // Default should be Tree view
        var treeContainer = cut.FindAll(".te-tree");
        treeContainer.Should().NotBeEmpty("Tree container should be rendered in default Tree view");
    }

    [Fact]
    public void Tree_RendersCompletedTotalProgress()
    {
        var tasks = """
            # Tasks

            ## Phase 1
            - [x] T001 Complete task
            - [ ] T002 Incomplete task
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var treeText = cut.Find(".te-tree").TextContent;
        treeText.Should().Contain("1 / 2", "Should show completed/total count");
    }

    [Fact]
    public void Tree_DoesNotRenderSeparatePercentageBadge()
    {
        var tasks = """
            # Tasks

            ## Phase 1
            - [x] T001 Complete
            - [x] T002 Complete
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var phaseBadges = cut.FindAll(".te-phase-badge");
        phaseBadges.Should().BeEmpty("Tree View should not render percentage badge");

        var progressBars = cut.FindAll(".te-phase-progress");
        progressBars.Should().BeEmpty("Tree View should not render progress bar");
    }

    [Fact]
    public void Tree_ShowsCompletedTotalForPhase()
    {
        var tasks = """
            # Tasks

            ## Phase 1
            - [x] T001 Task
            - [x] T002 Task
            - [x] T003 Task
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var treeText = cut.Find(".te-tree").TextContent;
        treeText.Should().Contain("3 / 3", "Should show 3 completed out of 3");
    }

    [Fact]
    public void Tree_ShowsCompletedTotalForTaskGroup()
    {
        var tasks = """
            # Tasks

            ## Phase 1

            ### Infrastructure
            - [x] T001 Task A
            - [x] T002 Task B
            - [ ] T003 Task C
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var treeText = cut.Find(".te-tree").TextContent;
        // Root: 2/3, Phase: 2/3, Group: 2/3
        treeText.Should().Contain("2 / 3", "Should show task group progress");
    }

    [Fact]
    public void Tree_ShowsPartialProgress()
    {
        var tasks = """
            # Tasks

            ## Phase 1
            - [x] T001 Complete
            - [ ] T002 Open
            - [ ] T003 Open

            ## Phase 2
            - [x] T004 Complete
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var treeText = cut.Find(".te-tree").TextContent;
        treeText.Should().Contain("1 / 3", "Phase 1 should show 1 of 3");
        treeText.Should().Contain("1 / 1", "Phase 2 should show 1 of 1");
    }

    [Fact]
    public void MapView_SwitchingBetweenTabsPreservesContent()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [x] T001 Setup
            - [ ] T002 Config
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        // Click Map
        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();
        var mapView = cut.Find(".te-map");
        mapView.TextContent.Should().Contain("T001");

        // Click Tree
        var treeTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Tree"));
        treeTabBtn!.Click();
        var treeView = cut.Find(".te-tree");
        treeView.TextContent.Should().Contain("T001");

        // Click Map again
        mapTabBtn!.Click();
        var mapView2 = cut.Find(".te-map");
        mapView2.TextContent.Should().Contain("T001", "Content should still be present when switching back to Map");
    }

    [Fact]
    public void DependencyChain_ThreePredecessorConvergence_RendersSuffixedIdCorrectly()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T032 Task
            - [ ] T033 Task
            - [ ] T033a Task Variant
            - [ ] T034 Task
            - [ ] T035 Task

            ## Dependencies
            ### User Story Internal Dependencies
            - **US4**: T032 / T033 / T033a → T034 → T035
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        // Click Dependencies tab
        var depsTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depsTabBtn.Should().NotBeNull("Dependencies tab should exist");
        depsTabBtn!.Click();

        // Find US4 section
        var depsView = cut.Find(".te-dependencies-view");
        var depChains = cut.FindAll(".te-dep-chain");
        depChains.Should().NotBeEmpty("Should have dependency chains");

        // Find the chain containing T032
        var us4Chain = depChains.FirstOrDefault(c => c.TextContent.Contains("T032"));
        us4Chain.Should().NotBeNull("Should have T032 in dependency chain");

        var chainText = us4Chain!.TextContent;

        // The rendered output should be: "T032 + T033 + T033a → T034 → T035"
        // Extract task IDs in order they appear
        var positions = new Dictionary<string, int>
        {
            { "T032", chainText.IndexOf("T032") },
            { "T033", chainText.IndexOf("T033") },
            { "T033a", chainText.IndexOf("T033a") },
            { "T034", chainText.IndexOf("T034") },
            { "T035", chainText.IndexOf("T035") }
        };

        // All should be found
        foreach (var (task, pos) in positions)
        {
            pos.Should().BeGreaterThanOrEqualTo(0, $"{task} should be in chain");
        }

        // Count arrows to ensure we have exactly 2 separators (one between group and T034, one between T034 and T035)
        var arrowCount = new System.Text.RegularExpressions.Regex("→").Matches(chainText).Count;
        arrowCount.Should().Be(2, "Should have exactly 2 arrows: one after predecessor group, one after T034");

        // All predecessors should appear before T034
        positions["T032"].Should().BeLessThan(positions["T034"], "T032 < T034");
        positions["T033"].Should().BeLessThan(positions["T034"], "T033 < T034");
        positions["T033a"].Should().BeLessThan(positions["T034"], "T033a < T034");
        positions["T034"].Should().BeLessThan(positions["T035"], "T034 < T035");

        // Critical: T033a should NOT appear after T034 (which was the bug)
        // The bug would render as: "T032 + T033 → T033a + T034 → T035"
        // which means T033a position > T034 position (WRONG)
        // We want: "T032 + T033 + T033a → T034 → T035"
        // which means T033a position < T034 position
        positions["T033a"].Should().BeLessThan(positions["T034"],
            "T033a should appear with predecessors, not after the arrow to T034 (this was the bug). Text: " + chainText);
    }

    [Fact]
    public void DependencyChain_LinearSequence()
    {
        var sampleTasks = """
            # Tasks
            ## Phase 1
            - [ ] T001 Task A
            - [ ] T002 Task B
            - [ ] T003 Task C

            ## Dependencies
            ### User Story Internal Dependencies
            - **US1**: T001 → T002 → T003
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var depsTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depsTabBtn!.Click();

        var depChain = cut.Find(".te-dep-chain");
        var text = depChain.TextContent;

        // Should render: T001 → T002 → T003
        text.Should().Contain("T001");
        text.Should().Contain("T002");
        text.Should().Contain("T003");
        var pos1 = text.IndexOf("T001");
        var pos2 = text.IndexOf("T002");
        var pos3 = text.IndexOf("T003");
        pos1.Should().BeLessThan(pos2);
        pos2.Should().BeLessThan(pos3);
    }

    [Fact]
    public void DependencyChain_TwoPredecessorConvergence()
    {
        var sampleTasks = """
            # Tasks
            ## Phase 1
            - [ ] T001 Task A
            - [ ] T002 Task B
            - [ ] T003 Task C
            - [ ] T004 Task D

            ## Dependencies
            ### User Story Internal Dependencies
            - **US1**: T001 / T002 → T003 → T004
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var depsTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depsTabBtn!.Click();

        var depChain = cut.Find(".te-dep-chain");
        var text = depChain.TextContent;

        // Should render: T001 + T002 → T003 → T004
        var arrowCount = new System.Text.RegularExpressions.Regex("→").Matches(text).Count;
        arrowCount.Should().Be(2, "Should have 2 arrows");

        text.IndexOf("T001").Should().BeLessThan(text.IndexOf("T003"), "T001 < T003");
        text.IndexOf("T002").Should().BeLessThan(text.IndexOf("T003"), "T002 < T003");
        text.IndexOf("T003").Should().BeLessThan(text.IndexOf("T004"), "T003 < T004");
    }

    [Fact]
    public void DependencyChain_AllRealUserStories()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T018 Task
            - [ ] T019 Task
            - [ ] T020 Task
            - [ ] T021 Task
            - [ ] T022 Task
            - [ ] T023 Task

            ## Phase 2
            - [ ] T024 Task
            - [ ] T025 Task
            - [ ] T026 Task
            - [ ] T027 Task

            ## Phase 3
            - [ ] T028 Task
            - [ ] T029 Task
            - [ ] T030 Task
            - [ ] T031 Task

            ## Phase 4
            - [ ] T032 Task
            - [ ] T033 Task
            - [ ] T033a Task Variant
            - [ ] T034 Task
            - [ ] T035 Task

            ## Dependencies
            ### User Story Internal Dependencies
            - **US1**: T018 / T019 → T020 → T021 → T022 → T023
            - **US2**: T024 → T025 → T026 → T027
            - **US3**: T028 → T029 → T030 → T031
            - **US4**: T032 / T033 / T033a → T034 → T035
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var depsTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depsTabBtn!.Click();

        var depChains = cut.FindAll(".te-dep-chain");
        depChains.Should().HaveCount(4, "Should have 4 dependency chains (one per user story)");

        // Check each chain
        var us1Chain = depChains.FirstOrDefault(c => c.TextContent.Contains("T018"));
        us1Chain.Should().NotBeNull();
        us1Chain!.TextContent.Should().Contain("T018");
        us1Chain.TextContent.Should().Contain("T019");
        us1Chain.TextContent.Should().Contain("T020");
        us1Chain.TextContent.Should().Contain("T023");

        var us4Chain = depChains.FirstOrDefault(c => c.TextContent.Contains("T032"));
        us4Chain.Should().NotBeNull();
        us4Chain!.TextContent.Should().Contain("T032");
        us4Chain.TextContent.Should().Contain("T033");
        us4Chain.TextContent.Should().Contain("T033a");
        us4Chain.TextContent.Should().Contain("T034");
        us4Chain.TextContent.Should().Contain("T035");

        // Most importantly: T033a should appear before T034
        var us4Text = us4Chain.TextContent;
        us4Text.IndexOf("T033a").Should().BeLessThan(us4Text.IndexOf("T034"),
            "T033a is a predecessor, should appear before T034 target");
    }

    [Fact]
    public void DependencyChain_VerifyBugFixWithRealStructure()
    {
        // This test verifies the bug is fixed by checking that:
        // - T032, T033, T033a are NOT grouped with T034
        // - They ARE all grouped before T034
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T032 Task
            - [ ] T033 Task
            - [ ] T033a Task Variant
            - [ ] T034 Task
            - [ ] T035 Task

            ## Dependencies
            ### User Story Internal Dependencies
            - **US4**: T032 / T033 / T033a → T034 → T035
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var depsTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depsTabBtn!.Click();

        var depChain = cut.Find(".te-dep-chain");

        // Get all task ID elements in order
        var taskIds = cut.FindAll(".te-dep-task-id").Where(e => depChain.Contains(e)).Select(e => e.TextContent.Trim()).ToList();

        // The bug would have rendered as: [T032, T033, T033a, T034, T035] where T033a appears after T033 but before T034
        // With grouping based on arrows, the rendering would show T033a with T034 (wrong)

        // The fix renders as: [T032, T033, T033a, T034, T035] with proper grouping
        // Where the first arrow comes AFTER all predecessors

        // Verify the order
        taskIds.Should().Contain("T032");
        taskIds.Should().Contain("T033");
        taskIds.Should().Contain("T033a");
        taskIds.Should().Contain("T034");
        taskIds.Should().Contain("T035");

        // Find positions
        var pos032 = taskIds.IndexOf("T032");
        var pos033 = taskIds.IndexOf("T033");
        var pos033a = taskIds.IndexOf("T033a");
        var pos034 = taskIds.IndexOf("T034");
        var pos035 = taskIds.IndexOf("T035");

        // All predecessors should come before the target
        pos032.Should().BeLessThan(pos034);
        pos033.Should().BeLessThan(pos034);
        pos033a.Should().BeLessThan(pos034);

        // T034 should come before T035
        pos034.Should().BeLessThan(pos035);

        // Verify the structure by counting arrows
        var arrows = cut.FindAll(".te-dep-arrow").Where(a => depChain.Contains(a)).Count();
        arrows.Should().Be(2, "Should have exactly 2 arrows in the US4 chain");
    }

    [Fact]
    public void ParallelWork_TaskIdSeparatedFromTitle()
    {
        var sampleTasks = """
            # Phase 1

            - [ ] T001 Task A [P]
            - [ ] T002 Task B [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        // Click Parallel tab
        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn.Should().NotBeNull("Parallel tab should exist");
        parallelTabBtn!.Click();

        // Verify task rows and elements exist
        var taskIds = cut.FindAll(".te-ptask-id");
        var titles = cut.FindAll(".te-ptask-primary");

        taskIds.Should().HaveCountGreaterThanOrEqualTo(2, "Should have task ID elements");
        titles.Should().HaveCountGreaterThanOrEqualTo(2, "Should have title elements");

        // Verify they contain expected content
        taskIds[0].TextContent.Trim().Should().Match("T00*", "ID should be formatted as task ID");
        titles[0].TextContent.Should().Contain("Task", "Title should contain task description");
    }

    [Fact]
    public void ParallelWork_RendersUserStorySeparately()
    {
        var sampleTasks = """
            # Phase 1

            - [ ] T001 Task A [P] [US1]
            - [ ] T002 Task B [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var tags = cut.FindAll(".te-ptask-tag");
        tags.Should().HaveCountGreaterThanOrEqualTo(1, "Should have at least 1 user story tag");

        // Verify US1 is present
        var tagTexts = tags.Select(t => t.TextContent.Trim()).ToList();
        tagTexts.Should().Contain("US1", "Should render US1 user story");
    }

    [Fact]
    public void ParallelWork_RendersCompletionStatusSeparately()
    {
        var sampleTasks = """
            # Phase 1

            - [x] T001 Completed Task [P]
            - [ ] T002 Open Task [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var statuses = cut.FindAll(".te-ptask-status");
        statuses.Should().NotBeEmpty("Should have status elements for completed tasks");

        // Verify checkmark is present
        var statusTexts = statuses.Select(s => s.TextContent.Trim()).ToList();
        statusTexts.Should().Contain("✓", "Status should show checkmark for completed tasks");
    }

    [Fact]
    public void ParallelWork_UntaggedTaskDoesNotRenderEmptyStoryBadge()
    {
        var sampleTasks = """
            # Phase 1

            - [ ] T001 Task Without Story [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var tags = cut.FindAll(".te-ptask-tag");
        tags.Should().BeEmpty("Untagged task should not render any tag element");
    }

    [Fact]
    public void ParallelWork_RealTasksRenderCorrectly()
    {
        var sampleTasks = """
            # Phase 1

            - [ ] T004 KjentBruker Configuration [P]
            - [ ] T006 Event Publisher [P]

            # Phase 2

            - [ ] T018 Scim User Service Tests [P] [US1]
            - [ ] T019 Scim Group Service Tests [P] [US1]
            - [ ] T024 ADO Integration Tests [P] [US2]

            # Phase 4

            - [ ] T033a Special Task Variant [P] [US4]
            - [ ] T036 Final Task [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        // Verify all tasks are rendered
        var taskIds = cut.FindAll(".te-ptask-id");
        var taskTexts = taskIds.Select(t => t.TextContent.Trim()).ToList();

        taskTexts.Should().Contain("T033a", "Should render T033a");
        taskTexts.Should().Contain("T004", "Should render T004");
        taskTexts.Should().HaveCountGreaterThanOrEqualTo(7, "Should have at least 7 tasks");

        // Verify tags are rendered
        var tags = cut.FindAll(".te-ptask-tag");
        var tagTexts = tags.Select(t => t.TextContent.Trim()).ToList();
        tagTexts.Should().Contain("US4", "Should render US4 tag");
    }

    [Fact]
    public void Header_UsesTaskCompletionTerminology()
    {
        var allCompletedTasks = """
            # Phase 1
            - [x] T001 Task A
            - [x] T002 Task B
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, allCompletedTasks));

        var headerContent = cut.FindAll(".te-header-status");
        headerContent.Should().NotBeEmpty("Should have header status");

        var statusText = headerContent[0].TextContent;
        statusText.Should().Contain("task completion", "Should use 'task completion' terminology");
        statusText.Should().NotContain("complete", "Should not use bare 'complete' without qualifier");
    }

    [Fact]
    public void Impact_UsesRequirementImplementationCoverageLabel()
    {
        var sampleTasks = """
            # Phase 1
            - [x] T001 Task A
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var impactTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Impact"));
        impactTabBtn!.Click();

        var impactSection = cut.Find("[data-testid='te-implementation-coverage']");
        impactSection.TextContent.Should().Contain("Requirement Implementation Coverage",
            "Should use clear terminology that specifies 'requirement' coverage");
    }

    [Fact]
    public void Impact_DoesNotUseAmbiguousImplementationCoverageLabelAlone()
    {
        var sampleTasks = """
            # Phase 1
            - [x] T001 Task A
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var impactTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Impact"));
        impactTabBtn!.Click();

        var implementationLabel = cut.FindAll(".te-section-kicker");
        var hasAmbiguousLabel = implementationLabel.Any(l => l.TextContent.Trim() == "Implementation Coverage");
        hasAmbiguousLabel.Should().BeFalse("Should not use bare 'Implementation Coverage' without 'Requirement' qualifier");
    }

    [Fact]
    public void TaskCompletionAndRequirementCoverageCanDiffer()
    {
        // Test that the UI can show 100% task completion but <100% requirement coverage
        var tasksMissingRequirements = """
            # Phase 1
            - [x] T001 Task A
            - [x] T002 Task B
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasksMissingRequirements));

        // Header should show task completion
        var headerStatus = cut.FindAll(".te-header-status");
        var taskCompletionText = headerStatus.FirstOrDefault()?.TextContent ?? "";
        taskCompletionText.Should().Contain("task completion",
            "Header should show task completion status");

        // Impact tab should show requirement coverage (potentially different)
        var impactTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Impact"));
        if (impactTabBtn != null)
        {
            impactTabBtn.Click();
            var requirementCoverageText = cut.Find("[data-testid='te-implementation-coverage']").TextContent;
            requirementCoverageText.Should().Contain("Requirement Implementation Coverage",
                "Impact should show requirement coverage with distinct terminology");
        }
    }

    [Fact]
    public void Impact_ClarifyingTextDistinguishesBothMetrics()
    {
        var sampleTasks = """
            # Phase 1
            - [x] T001 Task A
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));
        var impactTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Impact"));
        impactTabBtn!.Click();

        var impactContent = cut.Find("[data-testid='te-impact-view']").TextContent;
        impactContent.Should().Contain("Task completion", "Should mention task completion explicitly");
        impactContent.Should().Contain("Requirement implementation coverage", "Should mention requirement coverage explicitly");
        impactContent.Should().Contain("requirement-to-task",
            "Should clarify what requirement coverage measures");
    }

    [Fact]
    public void ChangesTab_CompactHintAvoidsSpatialLanguage()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var changesTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Changes"));
        changesTabBtn!.Click();

        var hint = cut.Find(".delta-no-baseline-hint");
        var hintText = hint.TextContent;
        hintText.Should().NotContain("above", "Should not use spatial 'above' language");
        hintText.Should().NotContain("below", "Should not use spatial 'below' language");
    }

    [Fact]
    public void ChangesTab_ShowsCompactGuidanceWhenNoBaseline()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var changesTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Changes"));
        changesTabBtn!.Click();

        var hint = cut.Find(".delta-no-baseline-hint");
        hint.Should().NotBeNull("Should show compact hint when no baseline");
        hint.TextContent.Should().Contain("upload", "Should mention upload option");
    }

    [Fact]
    public void ChangesTab_RendersPreviousAndCurrentInputsImmediately()
    {
        var sampleTasks = """
            # Tasks

            ## Phase 1
            - [ ] T001 Setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var changesTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Changes"));
        changesTabBtn!.Click();

        // Input panels should be visible immediately without extra cards
        var inputPanels = cut.FindAll(".delta-input-panel");
        inputPanels.Should().HaveCountGreaterThanOrEqualTo(2, "Should have Previous and Current panels");

        var textareas = cut.FindAll(".delta-textarea");
        textareas.Should().HaveCountGreaterThanOrEqualTo(2, "Should have textareas for both versions");
    }

    [Fact]
    public void CompactSummary_RendersAllMetrics()
    {
        var sampleTasks = """
            # Phase 1

            - [ ] T001 Task A [P]
            - [x] T002 Task B
            - [ ] T003 Task C [P]

            # Phase 2

            - [ ] T004 Task D
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var header = cut.Find(".te-header-left");
        var summary = header.TextContent;

        // Verify all metrics are shown
        summary.Should().Contain("task", "Should show task count");
        summary.Should().Contain("phase", "Should show phase count");
        summary.Should().Contain("parallel", "Should show parallel count");
        summary.Should().Contain("task completion", "Should show task completion percentage");
    }

    [Fact]
    public void CompactSummary_UsesTaskCompletionTerminology()
    {
        var completedTasks = """
            # Phase 1

            - [x] T001 Task A
            - [x] T002 Task B
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, completedTasks));

        var header = cut.Find(".te-header-summary");
        var headerText = header.TextContent;

        headerText.Should().Contain("task completion",
            "Should use 'task completion' terminology instead of just 'complete'");
    }

    [Fact]
    public void CompactSummary_CalculatesCompletionPercentageCorrectly()
    {
        var partialTasks = """
            # Phase 1

            - [x] T001 Task A
            - [x] T002 Task B
            - [ ] T003 Task C
            - [ ] T004 Task D
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, partialTasks));

        var header = cut.Find(".te-header-summary");
        var headerText = header.TextContent;

        // 2 out of 4 tasks complete = 50%
        headerText.Should().Contain("50% task completion",
            "Should calculate completion percentage correctly");
    }

    [Fact]
    public void CompactSummary_HandlesSingularAndPluralGrammar()
    {
        var singleTask = """
            ## Phase 1

            - [ ] T001 Task A
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, singleTask));

        var header = cut.Find(".te-header-summary");
        var headerText = header.TextContent.Replace("\n", "").Replace("\r", "").Replace(" ", "");

        // Should use singular "task" and "phase"
        headerText.Should().Contain("1task·1phase",
            "Should use singular grammar for single items");
    }

    [Fact]
    public void CompactSummary_WorksWithMultipleTasks()
    {
        var multipleTasks = """
            ## Phase 1

            - [ ] T001 Task A
            - [ ] T002 Task B
            - [ ] T003 Task C

            ## Phase 2

            - [ ] T004 Task D
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, multipleTasks));

        var header = cut.Find(".te-header-summary");
        var headerText = header.TextContent.Replace("\n", "").Replace("\r", "").Replace(" ", "");

        // Should use plural forms
        headerText.Should().Contain("4tasks",
            "Should use plural 'tasks' for multiple items");
        headerText.Should().Contain("2phases",
            "Should use plural 'phases' for multiple items");
    }

    [Fact]
    public void CompactSummary_ParallelCountUsesAllTaskNodes()
    {
        var withParallel = """
            ## Phase 1

            - [ ] T001 Task A [P]
            - [ ] T002 Task B [P]
            - [ ] T003 Task C

            ## Phase 2

            - [ ] T004 Task D [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, withParallel));

        var header = cut.Find(".te-header-summary");
        var headerText = header.TextContent.Replace("\n", "").Replace("\r", "").Replace(" ", "");

        // 3 tasks with [P] tags
        headerText.Should().Contain("3parallel",
            "Should count all tasks with [P] tags");
    }

    [Fact]
    public void MapView_RenderClickHandlerWithoutException()
    {
        var sampleTasks = """
            ## Phase 1

            - [x] T001 Setup
            - [ ] T002 Config
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        // Click Map tab to trigger RenderMapView
        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn.Should().NotBeNull("Map tab button should exist");

        // This should not throw an unboxing exception
        mapTabBtn!.Click();

        // Verify Map view is rendered
        var mapView = cut.Find(".te-map");
        mapView.Should().NotBeNull("Map view should be rendered without exception");
    }

    [Fact]
    public void MapView_ClickHandlerSelectsNode()
    {
        var sampleTasks = """
            ## Phase 1

            - [ ] T001 Task A
            - [ ] T002 Task B
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        // Switch to Map view
        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        // Click on a map task
        var mapTasks = cut.FindAll(".te-map-task");
        mapTasks.Should().NotBeEmpty("Map should render task rows");

        // Click first task - should not throw exception
        mapTasks[0].Click();
    }

    [Fact]
    public void MapView_RendersSevenPhases()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, ReadRealScimTasks()));

        ClickTab(cut, "Map");

        cut.FindAll(".te-map-phase-card").Should().HaveCount(7);
        cut.Find(".te-map-summary").TextContent.Should().Contain("7 phases");
        cut.Find(".te-map-summary").TextContent.Should().Contain("38 tasks");
    }

    [Fact]
    public void MapView_RendersPhaseTaskCounts()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, ReadRealScimTasks()));

        ClickTab(cut, "Map");

        var phaseCards = cut.FindAll(".te-map-phase-card");
        phaseCards.Any(card =>
            card.TextContent.Contains("Phase 1") && card.QuerySelector(".te-map-phase-count")?.TextContent.Contains("2 tasks") == true)
            .Should().BeTrue();
        phaseCards.Any(card =>
            card.TextContent.Contains("Phase 2") && card.QuerySelector(".te-map-phase-count")?.TextContent.Contains("15 tasks") == true)
            .Should().BeTrue();
        phaseCards.Any(card =>
            card.TextContent.Contains("Phase 7") && card.QuerySelector(".te-map-phase-count")?.TextContent.Contains("2 tasks") == true)
            .Should().BeTrue();
    }

    [Fact]
    public void MapView_RendersTaskGroupsWithLabeledCounts()
    {
        var tasks = """
            ## Phase 1

            ### Infrastructure changes

            - [ ] T001 Setup database
            - [ ] T002 Configure API
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        ClickTab(cut, "Map");

        var group = cut.Find(".te-map-group-header");
        group.QuerySelector(".te-map-group-title")!.TextContent.Should().Contain("Infrastructure changes");
        group.QuerySelector(".te-map-group-count")!.TextContent.Trim().Should().Be("2 tasks");
        group.TextContent.Should().NotContain("(2)");
    }

    [Fact]
    public void MapView_TaskIdAndTitleAreSeparateElements()
    {
        var tasks = """
            ## Phase 1

            - [ ] T001 ScimAdapter project setup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        ClickTab(cut, "Map");

        var task = cut.Find(".te-map-task");
        task.QuerySelector(".te-map-task-id")!.TextContent.Trim().Should().Be("T001");
        task.QuerySelector(".te-map-task-title")!.TextContent.Should().Contain("ScimAdapter project setup");
        task.QuerySelector(".te-map-task-title")!.TextContent.Should().NotContain("T001");
    }

    [Fact]
    public void MapView_RendersT033a()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, ReadRealScimTasks()));

        ClickTab(cut, "Map");

        var taskIds = cut.FindAll(".te-map-task-id").Select(id => id.TextContent.Trim()).ToList();
        taskIds.Should().Contain("T033");
        taskIds.Should().Contain("T033a");
    }

    [Fact]
    public void MapView_DoesNotRenderDocumentationSupportSections()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, ReadRealScimTasks()));

        ClickTab(cut, "Map");

        var mapText = cut.Find(".te-map").TextContent;
        mapText.Should().NotContain("Phase Dependencies");
        mapText.Should().NotContain("Within Each User Story");
        mapText.Should().NotContain("Parallel Execution Examples");
        mapText.Should().NotContain("Implementation Strategy");
        mapText.Should().NotContain("Summary");
        mapText.Should().NotContain("Format:");
    }

    [Fact]
    public void MapView_DoesNotRenderPhaseMetadata()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, ReadRealScimTasks()));

        ClickTab(cut, "Map");

        var mapText = cut.Find(".te-map").TextContent;
        mapText.Should().NotContain("Purpose");
        mapText.Should().NotContain("Independent Test");
        mapText.Should().NotContain("Checkpoint");
        mapText.Should().NotContain("CRITICAL");
    }

    [Fact]
    public void MapView_DoesNotRenderRawParallelMarkerText()
    {
        var tasks = """
            ## Phase 1

            - [x] T001 Setup [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        ClickTab(cut, "Map");

        cut.Find(".te-map").TextContent.Should().NotContain("[P]");
        cut.FindAll(".te-map-task-chips span").Should().NotContain(chip => chip.TextContent.Trim() == "P");
        cut.Find(".te-map-parallel-badge").TextContent.Trim().Should().Be("Parallel");
    }

    [Fact]
    public void MapView_UsesSharedExplorerStyleClasses()
    {
        var tasks = """
            ## Phase 1

            - [ ] T001 Task A
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        ClickTab(cut, "Map");

        cut.Find(".te-map-header").ClassList.Should().Contain("pe-section-block");
        cut.Find(".te-map-kicker").ClassList.Should().Contain("pe-section-kicker");
        cut.Find(".te-map-summary").ClassList.Should().Contain("ce-map-child-count");
        cut.Find(".te-map-phase-card").ClassList.Should().Contain("pe-section-block");
    }

    [Fact]
    public void MapView_DoesNotRenderDocumentationSections()
    {
        var withDocs = """
            ## Phase 1

            - [x] T001 Task A [P]
            - [ ] T002 Task B [P]

            ## Phase 2

            - [x] T003 Task C [P]

            ## Dependencies & Execution Order

            This section describes the dependency graph...

            ## Parallel Execution Examples

            Some examples of parallel work...

            ## Implementation Strategy

            The implementation approach...
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, withDocs));

        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        var mapText = cut.Find(".te-map").TextContent;

        // Map should show phases and tasks
        mapText.Should().Contain("Phase 1", "Should show Phase 1");
        mapText.Should().Contain("Phase 2", "Should show Phase 2");
        mapText.Should().Contain("T001", "Should show tasks");

        // Map should NOT show documentation sections
        mapText.Should().NotContain("Dependencies & Execution Order", "Should not render documentation section");
        mapText.Should().NotContain("Parallel Execution Examples", "Should not render documentation section");
        mapText.Should().NotContain("Implementation Strategy", "Should not render documentation section");
    }

    [Fact]
    public void MapView_FiltersDocumentationButKeepsPhaseTasks()
    {
        var scimLike = """
            ## Phase 1: Setup

            - [x] T001 Project setup [P]

            ## Phase 2: Foundation

            - [x] T002 Database setup [P]
            - [x] T003 EF configuration

            ## Dependencies & Execution Order

            Phase 1 must complete before Phase 2...

            ## Summary

            Implementation completed in 3 phases.
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, scimLike));

        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        var mapText = cut.Find(".te-map").TextContent;

        // Phases and tasks
        mapText.Should().Contain("Phase 1", "Should show Phase 1");
        mapText.Should().Contain("Phase 2", "Should show Phase 2");
        mapText.Should().Contain("T001", "Should show T001");
        mapText.Should().Contain("T002", "Should show T002");
        mapText.Should().Contain("T003", "Should show T003");

        // Documentation sections hidden
        mapText.Should().NotContain("Dependencies & Execution Order");
        mapText.Should().NotContain("Summary");
        mapText.Should().NotContain("Implementation completed");
    }

    [Fact]
    public void MapView_RendersTaskIdsSeparately()
    {
        var tasks = """
            ## Phase 1

            - [ ] T001 Task A
            - [ ] T002 Task B
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        var mapTasks = cut.FindAll(".te-map-task");
        mapTasks.Should().NotBeEmpty();

        var taskIdBadges = cut.FindAll(".te-map-task-id");
        taskIdBadges.Should().HaveCount(2);
        taskIdBadges[0].TextContent.Should().Contain("T001");
        taskIdBadges[1].TextContent.Should().Contain("T002");
    }

    [Fact]
    public void MapView_RendersT033aSuffixedId()
    {
        var tasks = """
            ## Phase 1

            - [ ] T033 Original task
            - [ ] T033a Task variant
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        var mapText = cut.Find(".te-map").TextContent;
        mapText.Should().Contain("T033a", "Should render suffixed task IDs");

        var taskIdBadges = cut.FindAll(".te-map-task-id");
        var t033a = taskIdBadges.FirstOrDefault(b => b.TextContent.Contains("T033a"));
        t033a.Should().NotBeNull("T033a should be rendered as separate badge");
    }

    [Fact]
    public void MapView_RendersTaskGroups()
    {
        var tasks = """
            ## Phase 1

            ### Infrastructure changes

            - [ ] T001 Setup database
            - [ ] T002 Configure API

            ### Core implementation

            - [ ] T003 Main service
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        var mapText = cut.Find(".te-map").TextContent;
        mapText.Should().Contain("Infrastructure changes", "Should show task group");
        mapText.Should().Contain("Core implementation", "Should show task group");

        var groupHeaders = cut.FindAll(".te-map-group-header");
        groupHeaders.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void MapView_ShowsPhaseCardCounters()
    {
        var tasks = """
            ## Phase 1

            - [ ] T001 Task
            - [ ] T002 Task
            - [ ] T003 Task

            ## Phase 2

            - [ ] T004 Task
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        var phaseCards = cut.FindAll(".te-map-phase-card");
        phaseCards.Should().HaveCount(2);

        var phaseCounts = cut.FindAll(".te-map-phase-count");
        phaseCounts[0].TextContent.Should().Contain("3 tasks");
        phaseCounts[1].TextContent.Should().Contain("1 task");
    }

    [Fact]
    public void MapView_RemainsDistinctFromTree()
    {
        var tasks = """
            ## Phase 1

            - [ ] T001 Task
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, tasks));

        // Map view has phase cards
        var mapTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Map"));
        mapTabBtn!.Click();

        var mapHasPhaseCards = cut.FindAll(".te-map-phase-card");
        mapHasPhaseCards.Should().NotBeEmpty("Map view should have phase cards");

        // Switch to Tree view - should not have phase cards
        var treeTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Tree"));
        treeTabBtn!.Click();

        var treeHasPhaseCards = cut.FindAll(".te-map-phase-card");
        treeHasPhaseCards.Should().BeEmpty("Tree view should not have Map-style phase cards");
    }

    [Fact]
    public void ParallelRow_HasSeparateTaskIdElement()
    {
        var withParallel = """
            ## Phase 1

            - [x] T001 Task A [P]
            - [ ] T033a Task B [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, withParallel));

        // Switch to Parallel view
        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        // Find task ID elements
        var taskIdElements = cut.FindAll(".te-ptask-id");
        taskIdElements.Should().NotBeEmpty("Should have separate task ID elements");

        var ids = taskIdElements.Select(e => e.TextContent.Trim()).ToList();
        ids.Should().Contain("T001", "Should render T001 ID");
        ids.Should().Contain("T033a", "Should render T033a ID with suffix");
    }

    [Fact]
    public void ParallelRow_HasSeparateTitleElement()
    {
        var withParallel = """
            ## Phase 1

            - [x] T001 Task A [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, withParallel));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var titleElements = cut.FindAll(".te-ptask-primary");
        titleElements.Should().NotBeEmpty("Should have separate title elements");
        titleElements[0].TextContent.Should().Contain("Task", "Title should be separate from task ID");
    }

    [Fact]
    public void ParallelRow_HasSeparateStoryElement()
    {
        var withStory = """
            ## Phase 1

            - [ ] T001 Task [US1] [P]
            - [ ] T002 Task [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, withStory));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var storyElements = cut.FindAll(".te-ptask-tag");
        storyElements.Should().HaveCount(1, "Only one task should have a story tag");
        storyElements[0].TextContent.Should().Contain("US1", "Story tag should be separate");
    }

    [Fact]
    public void ParallelRow_HasSeparateStatusElement()
    {
        var mixed = """
            ## Phase 1

            - [x] T001 Completed [P]
            - [ ] T002 Open [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, mixed));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var statusElements = cut.FindAll(".te-ptask-status");
        statusElements.Should().HaveCount(1, "Only completed task should have status indicator");
        statusElements[0].TextContent.Should().Contain("✓", "Status should show checkmark for completed");
    }

    [Fact]
    public void ParallelWork_GroupCountShowsLabeledCount()
    {
        var sampleTasks = """
            ## Phase 1
            - [x] T001 Task A [P]

            ## Phase 2
            - [ ] T002 Task B [P]
            - [ ] T003 Task C [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, sampleTasks));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var groupCounts = cut.FindAll(".te-parallel-group-count");
        groupCounts.Should().NotBeEmpty("Should have group count elements");

        var countTexts = groupCounts.Select(g => g.TextContent.Trim()).ToList();
        countTexts.Should().Contain("1 parallel task", "Single task should use singular form");
        countTexts.Should().Contain("2 parallel tasks", "Multiple tasks should use plural form");
    }

    [Fact]
    public void ParallelRow_UntaggedTaskHasNoStoryBadge()
    {
        var untagged = """
            ## Phase 1

            - [ ] T001 Task without story [P]
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, untagged));

        var parallelTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Parallel"));
        parallelTabBtn!.Click();

        var storyElements = cut.FindAll(".te-ptask-tag");
        storyElements.Should().BeEmpty("Task without story tag should not render story element");
    }

    // ── Dependencies View Tests ──
    [Fact]
    public void Dependencies_TabRendersWithoutCrash()
    {
        var minimal = """
            ## Phase 1

            - [ ] T001 Task 1
            - [ ] T002 Task 2
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, minimal));

        var depsTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depsTabBtn.Should().NotBeNull("Dependencies tab should exist");

        // Click should not throw
        depsTabBtn!.Click();

        var depsView = cut.Find(".te-dependencies-view-content");
        depsView.Should().NotBeNull("Dependencies view should render");
    }

    [Fact]
    public void Dependencies_ShowsEmptyStateWhenNoDependencies()
    {
        var minimal = """
            ## Phase 1

            - [ ] T001 Task 1
            - [ ] T002 Task 2
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, minimal));

        var depsTabBtn = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains("Dependencies"));
        depsTabBtn!.Click();

        var depsContent = cut.Find(".te-dependencies-view-content").TextContent;
        depsContent.Should().Contain("No explicit task dependencies", "Should show empty state when no dependencies");
    }

    private static void ClickTaskRow(IRenderedComponent<TaskExplorerPanel> cut, string taskId)
    {
        var row = cut.FindAll(".te-row").FirstOrDefault(r => r.TextContent.Contains(taskId));
        row.Should().NotBeNull($"task row {taskId} should be rendered in Tree view");
        row!.Click();
    }

    private static void ClickTab(IRenderedComponent<TaskExplorerPanel> cut, string label)
    {
        var tab = cut.FindAll(".te-view-btn").FirstOrDefault(b => b.TextContent.Contains(label));
        tab.Should().NotBeNull($"{label} tab button should exist");
        tab!.Click();
    }

    private static string ReadRealScimTasks()
    {
        var scimTasksPath = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "SampleData",
            "autorisasjon",
            "tasks.md");

        File.Exists(scimTasksPath).Should().BeTrue($"real SCIM tasks fixture should exist at {scimTasksPath}");
        return File.ReadAllText(scimTasksPath);
    }

}
