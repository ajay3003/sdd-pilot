using BirkNext.DeliveryReporting;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The work-item workspace: Module → Feature → User Story → explicitly linked Tasks with Phase as its own dimension; Hierarchy / List / Board /
/// Phases over one model; a detail panel with header, Parent · Children · Related and source evidence; unassigned and unresolved tasks visible
/// as such; no 0/0 for features without tasks.md; removed features shown as baseline-only; deep-linked selection.
/// </summary>
public sealed class DeliveryWorkspaceTests : BunitContext
{
    private const string Dashboard = "frontend::001-initial-dashboard", DevMode = "frontend::004-settings-devmode", Legacy = "backend::009-legacy", Fresh = "backend::012-new";

    private static WeeklyDeliveryReport Report() => new()
    {
        ProjectName = "M2LB", WeekLabel = "2026-W44", Current = new() { SourceFileName = "M2LB.zip" }, Baseline = new() { SourceFileName = "M2LB (1)(9).zip" },
        Stories =
        [
            new()
            {
                FeatureKey = Dashboard, Title = "Initial dashboard", Module = "Frontend", Movement = DeliveryMovement.Progressed, CurrentStatus = DeliveryStatus.InProgress,
                BaselineTaskCompleted = 1, BaselineTaskTotal = 4, CurrentTaskCompleted = 2, CurrentTaskTotal = 4, TasksCompletedThisPeriod = 1,
                Reasons = ["1 task completed this period"], Attention = ["Tasks reference [US9], which is not a user story"], HasActiveAttention = true,
                Evidence = new() { SpecPath = "Frontend/specs/001-initial-dashboard/spec.md", TasksPath = "Frontend/specs/001-initial-dashboard/tasks.md" },
                Hierarchy = new()
                {
                    Captured = true,
                    Phases = [new("phase-1", "Phase 1: Setup", DeliveryItemChange.Unchanged) { Position = 0, TaskTotal = 2, TaskCompleted = 1 },
                              new("phase-3", "Phase 3: User Story 1", DeliveryItemChange.Changed) { Position = 1, TaskTotal = 2, TaskCompleted = 1, StoryKeys = ["US1"] }],
                    Stories =
                    [
                        new("US1", "Designer toggles operations to verify access-gated UI", DeliveryItemChange.Unchanged)
                        {
                            Priority = "P1", Description = "As a designer I can toggle operations.", PhaseKeys = ["phase-3"], TaskTotal = 1, TaskCompleted = 1, SourceLine = 12,
                            Criteria = [new("AC-1", "Given an operation, When toggled off, Then gated UI is hidden", DeliveryItemChange.Unchanged) { StoryKey = "US1" }],
                        },
                        new("US2", "Viewer sees defaults", DeliveryItemChange.Added),
                    ],
                    Tasks =
                    [
                        new("T001", "T001", "Create project", true, DeliveryTaskChange.Unchanged) { PhaseKey = "phase-1", Parallel = true, BaselineCompleted = true, SourcePath = "Frontend/specs/001-initial-dashboard/tasks.md" },
                        new("T002", "T002", "Shared infrastructure", false, DeliveryTaskChange.Unchanged) { PhaseKey = "phase-1", BaselineCompleted = false },
                        new("T010", "T010", "Build toggle panel", true, DeliveryTaskChange.CompletedThisPeriod) { PhaseKey = "phase-3", UserStoryKey = "US1", BaselineCompleted = false },
                        new("T012", "T012", "Unknown story work", false, DeliveryTaskChange.Unchanged) { PhaseKey = "phase-3", UnresolvedStoryReference = "US9", BaselineCompleted = false },
                    ],
                    FunctionalRequirements = [new("FR-001", "System MUST hide gated UI", DeliveryItemChange.Unchanged)],
                },
            },
            new()
            {
                FeatureKey = DevMode, Title = "Developer Mode Settings Panel", Module = "Frontend", Movement = DeliveryMovement.NoSignificantChange, CurrentStatus = DeliveryStatus.NotAssessed,
                Reasons = ["Unchanged · no task list found (progress cannot be derived from tasks)"], Evidence = new() { SpecPath = "Frontend/specs/004-settings-devmode/spec.md" },
                Hierarchy = new() { Captured = true, HasTaskList = false, Stories = [new("US1", "Designer toggles dev mode", DeliveryItemChange.Unchanged)] },
            },
            new() { FeatureKey = Legacy, Title = "Legacy import", Module = "Backend", Movement = DeliveryMovement.Removed, RemovedFeature = true, BaselineTaskCompleted = 3, BaselineTaskTotal = 3,
                BaselineStatus = DeliveryStatus.TasksComplete, Evidence = new() { TasksPath = "Backend/specs/009-legacy/tasks.md" }, Hierarchy = new() { Captured = true } },
            new() { FeatureKey = Fresh, Title = "New export", Module = "Backend", Movement = DeliveryMovement.NewThisPeriod, NewFeature = true, CurrentTaskCompleted = 2, CurrentTaskTotal = 2,
                Evidence = new() { TasksPath = "Backend/specs/012-new/tasks.md" }, Hierarchy = new() { Captured = true } },
        ],
    };

    private string? _selected;
    private IRenderedComponent<DeliveryWorkspace> Render(string? selected = null) =>
        Render<DeliveryWorkspace>(p => p.Add(x => x.Report, Report()).Add(x => x.SelectedId, selected).Add(x => x.OnSelect, (string? id) => _selected = id));

    private static AngleSharp.Dom.IElement Item(IRenderedComponent<DeliveryWorkspace> cut, string id) =>
        cut.FindAll("[data-testid=dws-row]").Single(r => r.GetAttribute("data-id") == id).QuerySelector("[data-testid=dws-item]")!;

    [Fact]
    public void The_hierarchy_expands_module_feature_story_and_keeps_unassigned_tasks_under_their_feature()
    {
        var cut = Render();
        cut.FindAll("[data-testid=dws-row]").Should().BeEmpty("modules start collapsed");
        cut.FindAll("[data-testid=dws-module]").Select(m => m.GetAttribute("data-module")).Should().Equal("Frontend", "Backend");
        cut.FindAll("[data-testid=dws-module-toggle]")[0].Click();
        cut.FindAll("[data-testid=dws-row]").Select(r => r.GetAttribute("data-id")).Should().Equal(DeliveryWorkItems.FeatureId(Dashboard), DeliveryWorkItems.FeatureId(DevMode));
        cut.FindAll("[data-testid=dws-row]")[0].QuerySelector("[data-testid=dws-toggle]")!.Click();
        cut.FindAll("[data-testid=dws-row]").Select(r => r.GetAttribute("data-id")).Should().Contain(DeliveryWorkItems.StoryId(Dashboard, "US1"));
        cut.Find("[data-testid=dws-unassigned]").TextContent.Should().Contain("Tasks without a user story (3)", "untagged and unresolved tasks stay with their feature");
        cut.FindAll("[data-testid=dws-row]")[0].QuerySelector("[data-testid=dws-toggle]")!.Click();
        cut.FindAll("[data-testid=dws-unassigned]").Should().BeEmpty("collapsing hides the children again");
    }

    [Fact]
    public void Feature_detail_states_status_provenance_measurable_progress_requirements_and_traceability()
    {
        var cut = Render(DeliveryWorkItems.FeatureId(Dashboard));
        cut.Find("[data-testid=dws-detail-type]").TextContent.Should().Be("FEATURE · 001-initial-dashboard");
        cut.Find("[data-testid=dws-detail-status]").TextContent.Should().Be("In progress — derived from task checkboxes (not a delivery decision)");
        cut.Find("[data-testid=dws-feature-progress]").TextContent.Should().Be("2/4 tasks checked");
        cut.Find("[data-testid=dws-feature-unassigned]").TextContent.Should().Be("3");
        cut.Find("[data-testid=dws-requirements]").TextContent.Should().Contain("FR-001").And.Contain("not acceptance criteria");
        cut.Find("[data-testid=dws-feature-warnings]").TextContent.Should().Contain("[US9]");
        cut.Find("[data-testid=dws-evidence]").TextContent.Should().Contain("Frontend/specs/001-initial-dashboard/tasks.md");
    }

    [Fact]
    public void A_feature_without_tasks_md_says_progress_cannot_be_derived_never_zero_of_zero()
    {
        var cut = Render(DeliveryWorkItems.FeatureId(DevMode));
        cut.Find("[data-testid=dws-feature-progress]").TextContent.Should().Be("No task list found — progress cannot be derived from tasks");
        cut.Find("[data-testid=dws-detail-card]").TextContent.Should().NotContain("0/0");
        cut.Find("[data-testid=dws-view-phases]").Change(true);
        cut.FindAll("[data-testid=dws-phase-feature]").Single(f => f.GetAttribute("data-feature") == DevMode).TextContent.Should().Contain("No task list found");
    }

    [Fact]
    public void Story_detail_shows_its_acceptance_criteria_and_explicitly_linked_tasks_with_their_transition()
    {
        var cut = Render(DeliveryWorkItems.StoryId(Dashboard, "US1"));
        cut.Find("[data-testid=dws-detail-type]").TextContent.Should().Be("USER STORY · US1");
        cut.Find("[data-testid=dws-detail-priority]").TextContent.Should().Be("P1");
        cut.Find("[data-testid=dws-detail-parent]").TextContent.Should().Contain("Initial dashboard");
        cut.Find("[data-testid=dws-story-criteria]").TextContent.Should().Contain("Given an operation, When toggled off, Then gated UI is hidden");
        cut.Find("[data-testid=dws-story-tasks]").TextContent.Should().Contain("T010").And.Contain("[ ] → [x]").And.NotContain("T012", "only explicit [US1] links");

        var empty = Render(DeliveryWorkItems.StoryId(Dashboard, "US2"));
        empty.Find("[data-testid=dws-story-no-tasks]").TextContent.Should().Contain("no task is linked by guessing");
    }

    [Fact]
    public void Task_detail_explains_feature_story_phase_and_transition_and_shows_unresolved_links()
    {
        var linked = Render(DeliveryWorkItems.TaskId(Dashboard, "T010"));
        linked.Find("[data-testid=dws-task-explanation]").TextContent.Should().Contain("Linked to US1 through the explicit [US1] reference")
            .And.Contain("Phase 3: User Story 1").And.Contain("[ ] in baseline → [x] in current — completed this period");

        var unresolved = Render(DeliveryWorkItems.TaskId(Dashboard, "T012"));
        unresolved.Find("[data-testid=dws-task-story]").TextContent.Should().Be("Parent story unresolved ([US9])");
        unresolved.Find("[data-testid=dws-broken-link]").TextContent.Should().Contain("[US9]");

        var untagged = Render(DeliveryWorkItems.TaskId(Dashboard, "T002"));
        untagged.Find("[data-testid=dws-task-story]").TextContent.Should().Be("Not assigned");
        untagged.Find("[data-testid=dws-task-explanation]").TextContent.Should().Contain("User Story: Not assigned").And.Contain("No completion transition");

        Render(DeliveryWorkItems.TaskId(Dashboard, "T001")).Find("[data-testid=dws-task-explanation]").TextContent.Should().Contain("Marked [P]");
    }

    [Fact]
    public void Parent_navigation_and_selection_raise_the_deep_link()
    {
        var cut = Render(DeliveryWorkItems.TaskId(Dashboard, "T010"));
        cut.Find("[data-testid=dws-detail-parent] button").Click();
        _selected.Should().Be(DeliveryWorkItems.StoryId(Dashboard, "US1"));
        cut.Find("[data-testid=dws-detail-type]").TextContent.Should().Be("USER STORY · US1");
        cut.FindAll("[data-testid=dws-row]").Single(r => r.GetAttribute("data-id") == DeliveryWorkItems.StoryId(Dashboard, "US1")).ClassList.Should().Contain("is-selected");
    }

    [Fact]
    public void Search_by_id_reveals_the_matching_item_and_filters_narrow_the_hierarchy()
    {
        var cut = Render();
        cut.Find("[data-testid=dws-search]").Input("T012");
        cut.FindAll("[data-testid=dws-row]").Select(r => r.GetAttribute("data-id")).Should().Contain(DeliveryWorkItems.TaskId(Dashboard, "T012"))
            .And.NotContain(DeliveryWorkItems.TaskId(Dashboard, "T010"));
        cut.Find("[data-testid=dws-search]").Input("no-such-item");
        cut.Find("[data-testid=dws-none]").Should().NotBeNull();
    }

    [Fact]
    public void Phases_view_groups_tasks_by_source_phase_and_never_synthesizes_one()
    {
        var cut = Render();
        cut.Find("[data-testid=dws-view-phases]").Change(true);
        var dashboard = cut.FindAll("[data-testid=dws-phase-feature]").Single(f => f.GetAttribute("data-feature") == Dashboard);
        dashboard.QuerySelectorAll("[data-testid=dws-phase]").Select(p => p.GetAttribute("data-phase")).Should().Equal("phase-1", "phase-3");
        dashboard.TextContent.Should().Contain("1/2 tasks checked").And.Contain("US1");
        cut.FindAll("[data-testid=dws-phase-feature]").Single(f => f.GetAttribute("data-feature") == Fresh).TextContent.Should().Contain("never synthesized from task order");
    }

    [Fact]
    public void Board_uses_evidence_based_change_groups_and_removed_items_are_baseline_only()
    {
        var cut = Render();
        cut.Find("[data-testid=dws-view-board]").Change(true);
        string Column(string key) => cut.FindAll("[data-testid=dws-card]").Single(c => c.GetAttribute("data-id") == DeliveryWorkItems.FeatureId(key)).ParentElement!.GetAttribute("data-column")!;
        Column(Fresh).Should().Be(nameof(DeliveryBoardColumn.NewThisPeriod));
        Column(Legacy).Should().Be(nameof(DeliveryBoardColumn.RemovedFromScope));
        Column(Dashboard).Should().Be(nameof(DeliveryBoardColumn.RequiresReview), "an unresolved story reference needs review");
        Column(DevMode).Should().Be(nameof(DeliveryBoardColumn.Unchanged));
        cut.Find("[data-testid=dws-board-note]").TextContent.Should().Contain("not workflow states");
        cut.Markup.Should().NotContain("Ready for test");
    }

    [Fact]
    public void List_is_one_sortable_table_over_the_same_items()
    {
        var cut = Render();
        cut.Find("[data-testid=dws-view-list]").Change(true);
        cut.Find("[data-testid=dws-type]").Change("Task");
        cut.FindAll("[data-testid=dws-list-row]").Should().HaveCount(4);
        cut.Find("[data-testid=dws-sort-title]").Click();
        cut.FindAll("[data-testid=dws-list-row] td:nth-child(3)").Select(c => c.TextContent).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        var removed = Render(DeliveryWorkItems.FeatureId(Legacy));
        removed.Find("[data-testid=dws-removed]").TextContent.Should().Contain("baseline only").And.Contain("not a defect");
        removed.Find("[data-testid=dws-feature-progress]").TextContent.Should().Be("3/3 tasks checked");
    }
}
