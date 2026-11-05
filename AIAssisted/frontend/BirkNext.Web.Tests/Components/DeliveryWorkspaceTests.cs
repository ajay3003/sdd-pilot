using BirkNext.DeliveryReporting;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

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
        cut.Find("[data-testid=dws-board-note]").TextContent.Should().Contain("not workflow statuses");
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
    private static readonly Guid Project = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private IRenderedComponent<DeliveryWorkspace> RenderMode(DeliveryExplorerOptions options, string? selected = null) =>
        Render<DeliveryWorkspace>(p => p.Add(x => x.Report, Report()).Add(x => x.Options, options).Add(x => x.ProjectId, Project)
            .Add(x => x.SelectedId, selected).Add(x => x.OnSelect, (string? id) => _selected = id));

    /// <summary>Current state (Delivery Overview): the current inventory only — no removed items, no Change Board, no change filter or badges — and
    /// "Needs review" means a genuine review need, never "changed between snapshots".</summary>
    [Fact]
    public void Current_state_mode_shows_the_current_inventory_without_comparison_dimensions()
    {
        var cut = RenderMode(DeliveryExplorerOptions.CurrentState);
        cut.Find("[data-testid=dlr-workspace]").GetAttribute("data-mode").Should().Be("CurrentState");
        cut.FindAll("[data-testid=dws-view-board]").Should().BeEmpty("the Change Board is a comparison view");
        cut.FindAll("[data-testid=dws-change]").Should().BeEmpty();
        cut.Find("[data-testid=dws-count]").TextContent.Should().Contain("in the current snapshot");

        cut.Find("[data-testid=dws-view-list]").Change(true);
        var ids = cut.FindAll("[data-testid=dws-list-row]").Select(r => r.GetAttribute("data-id")).ToList();
        ids.Should().NotContain(DeliveryWorkItems.FeatureId(Legacy), "a removed feature exists only in the baseline");
        ids.Should().Contain(DeliveryWorkItems.FeatureId(Fresh));
        cut.FindAll("[data-testid=dws-list] th").Select(h => h.TextContent).Should().Equal(["ID", "Type", "Title", "Parent", "Module", "Phase", "Progress", "Review"]);
        cut.FindAll("[data-testid=dws-change-chip]").Should().BeEmpty();

        cut.Find("[data-testid=dws-attention]").Change(true);
        cut.FindAll("[data-testid=dws-list-row]").Select(r => r.GetAttribute("data-id")).Should().BeEquivalentTo(
            [DeliveryWorkItems.FeatureId(Dashboard), DeliveryWorkItems.TaskId(Dashboard, "T012")],
            "the feature with attention and the unresolved [US9] task need review; a story added this period does not");
        cut.Find("[data-testid=dws-chip]").TextContent.Should().Contain("Needs review");
        cut.Find("[data-testid=dws-clear]").Click();
        cut.FindAll("[data-testid=dws-chip]").Should().BeEmpty();
    }

    [Fact]
    public void Current_state_details_show_checklist_state_and_link_to_the_changes_of_the_same_canonical_item()
    {
        var task = RenderMode(DeliveryExplorerOptions.CurrentState, DeliveryWorkItems.TaskId(Dashboard, "T010"));
        task.Find("[data-testid=dws-task-state]").TextContent.Should().Be("[x] checked in source", "checklist state only — no baseline transition");
        task.FindAll("[data-testid=dws-detail-change]").Should().BeEmpty();
        task.Find("[data-testid=dws-evidence-snapshot]").TextContent.Should().Be("M2LB.zip (current)");
        var feature = RenderMode(DeliveryExplorerOptions.CurrentState, DeliveryWorkItems.FeatureId(Dashboard));
        feature.FindAll("[data-testid=dws-feature-changes]").Should().BeEmpty("period changes belong to Delivery Reports");
        feature.Find("[data-testid=dws-cross-page]").GetAttribute("href").Should()
            .Be($"delivery-reporting?project={Project}&tab=stories&report={Guid.Empty}&item={Uri.EscapeDataString(DeliveryWorkItems.FeatureId(Dashboard))}");
    }

    [Fact]
    public void Comparison_mode_keeps_changes_and_links_to_the_current_state_of_the_same_item()
    {
        var cut = RenderMode(DeliveryExplorerOptions.Comparison, DeliveryWorkItems.TaskId(Dashboard, "T010"));
        cut.Find("[data-testid=dws-task-state]").TextContent.Should().Contain("[ ] → [x]");
        cut.Find("[data-testid=dws-detail-change]").TextContent.Should().NotBeEmpty();
        cut.Find("[data-testid=dws-evidence-snapshot]").TextContent.Should().Contain("(baseline)");
        cut.Find("[data-testid=dws-cross-page]").GetAttribute("href").Should()
            .Be($"delivery-overview?project={Project}&item={Uri.EscapeDataString(DeliveryWorkItems.TaskId(Dashboard, "T010"))}");
        var removed = RenderMode(DeliveryExplorerOptions.Comparison, DeliveryWorkItems.FeatureId(Legacy));
        removed.FindAll("[data-testid=dws-cross-page]").Should().ContainSingle("comparison mode links to the current state even for a removed item, where Overview says it is not current");
        cut.Find("[data-testid=dws-view-list]").Change(true);
        cut.FindAll("[data-testid=dws-list] th").Select(h => h.TextContent).Should().Contain("Period change");
    }

    [Fact]
    public void Both_modes_build_the_same_canonical_identities_and_checklist_state_for_current_items()
    {
        var report = Report();
        var all = DeliveryWorkItems.Build(report);
        var current = all.Where(DeliveryExplorerOptions.CurrentState.Includes).ToList();
        var comparison = all.Where(DeliveryExplorerOptions.Comparison.Includes).ToList();
        comparison.Should().HaveCount(all.Count);
        current.Select(i => i.Id).Should().BeSubsetOf(comparison.Select(i => i.Id));
        current.Should().OnlyContain(i => !DeliveryWorkItems.IsRemoved(i));
        current.Where(i => i.Task is not null).Select(i => (i.Id, i.Task!.Completed)).Should()
            .BeEquivalentTo(comparison.Where(i => i.Task is not null && !DeliveryWorkItems.IsRemoved(i)).Select(i => (i.Id, i.Task!.Completed)));
    }

    [Fact]
    public void The_list_virtualizes_long_inventories_instead_of_cutting_them_off()
    {
        var report = Report();
        var h = report.Stories[0].Hierarchy!;
        var many = report with { Stories = [report.Stories[0] with { Hierarchy = h with { Tasks = [.. Enumerable.Range(1, 2500).Select(n => new DeliveryTaskRow($"X{n:0000}", $"X{n:0000}", $"Task {n}", n % 2 == 0, DeliveryTaskChange.Unchanged))] } }] };
        var cut = Render<DeliveryWorkspace>(p => p.Add(x => x.Report, many).Add(x => x.Options, DeliveryExplorerOptions.CurrentState));
        cut.Find("[data-testid=dws-view-list]").Change(true);
        cut.Find("[data-testid=dws-type]").Change("Task");
        // Every matching item is in the list (no cut-off, no paging) and rendered through Virtualize with the fixed row height the CSS enforces;
        // the browser renders only the rows in view (verified in the real-browser run — bUnit's Virtualize handler renders all).
        var virtualize = cut.FindComponent<Microsoft.AspNetCore.Components.Web.Virtualization.Virtualize<DeliveryWorkItem>>();
        virtualize.Instance.Items.Should().HaveCount(2500);
        virtualize.Instance.ItemSize.Should().Be(40);
        cut.Find("[data-testid=dws-list]").ClassList.Should().Contain("dws-row-fixed");
        cut.FindAll("[data-testid=dws-show-more]").Should().BeEmpty();
        cut.Find("[data-testid=dws-count]").TextContent.Should().StartWith("2500 of ");
        cut.Find("[data-testid=dws-list-total]").TextContent.Should().StartWith("2500 work items");
    }

    /// <summary>Report v4: tasks hang under requirement-model groups — never a "Tasks without a user story" bucket — with explained badges.</summary>
    [Fact]
    public void Report_v4_groups_replace_the_unassigned_bucket_with_explained_badges()
    {
        const string Auth = "autorisasjonservice::001-auth";
        var shared = new DeliveryWorkGroupRow("shared", DeliveryTaskRelationship.SharedFoundational, "Shared & Foundational Tasks")
            { Explanation = "Setup, foundational, polish and cross-cutting work not owned by one story.", Provenance = "tasks.md phases", TaskTotal = 1, TaskCompleted = 1 };
        var functional = new DeliveryWorkGroupRow("US1", DeliveryTaskRelationship.FunctionalGroup, "US1 — Functional group")
            { Explanation = "Derived from stable [USn] references in tasks.md.", Provenance = "[US1] tags in tasks.md", Confidence = DeliveryMappingConfidence.GroupLevel, TaskTotal = 1 };
        var report = new WeeklyDeliveryReport
        {
            ProjectName = "M2LB", Current = new() { SourceFileName = "M2LB-language.zip" }, Baseline = new() { SourceFileName = "M2LB -main.zip" },
            Stories =
            [
                new()
                {
                    FeatureKey = Auth, Title = "Autorisasjon", Module = "AutorisasjonService", CurrentTaskTotal = 2, CurrentTaskCompleted = 1,
                    Evidence = new() { TasksPath = "AutorisasjonService/specs/001-auth/tasks.md" },
                    Hierarchy = new()
                    {
                        Captured = true, RequirementModel = "Functional groups", Groups = [functional, shared],
                        Tasks =
                        [
                            new("T001", "T001", "Create the solution", true, DeliveryTaskChange.Unchanged) { Relationship = DeliveryTaskRelationship.SharedFoundational, GroupKey = "shared", MappingConfidence = DeliveryMappingConfidence.None, RelationshipNote = "No story tag, in phase \"Setup\"." },
                            new("T011", "T011", "Validate the token", false, DeliveryTaskChange.Unchanged) { Relationship = DeliveryTaskRelationship.FunctionalGroup, GroupKey = "US1", MappingConfidence = DeliveryMappingConfidence.GroupLevel, RelationshipNote = "Derived from [US1] references in tasks.md." },
                        ],
                    },
                },
            ],
        };
        var cut = Render<DeliveryWorkspace>(p => p.Add(x => x.Report, report).Add(x => x.SelectedId, DeliveryWorkItems.TaskId(Auth, "T011")));

        cut.FindAll("[data-testid=dws-unassigned]").Should().BeEmpty("v4 reports group every task");
        var badges = cut.FindAll("[data-testid=dws-badge]");
        badges.Select(b => b.GetAttribute("data-kind")).Should().BeEquivalentTo(["FunctionalGroup", "SharedFoundational"]);
        badges.First(b => b.GetAttribute("data-kind") == "FunctionalGroup").GetAttribute("title").Should().Contain("Derived from stable [USn]").And.Contain("Source: [US1] tags in tasks.md").And.Contain("Mapping: Group-level");
        cut.Find("[data-testid=dws-task-relationship]").TextContent.Should().Be("Functional group · Group-level");
        cut.Find("[data-testid=dws-detail-parent]").TextContent.Should().Contain("US1 · Functional group", "the id column carries the key; the title does not repeat it");
        cut.Find("[data-testid=dws-legend]").HasAttribute("open").Should().BeFalse("the legend is available but collapsed");
        cut.FindAll("[data-testid=dws-legend] dt").Select(d => d.TextContent).Should().Contain(["Functional group", "Shared & Foundational", "Unresolved reference"]);

        cut.Render(p => p.Add(x => x.SelectedId, DeliveryWorkItems.FeatureId(Auth)));
        cut.Find("[data-testid=dws-feature-model]").TextContent.Should().Be("Functional groups");
        cut.Find("[data-testid=dws-feature-groups]").TextContent.Should().Be("Functional group 1 · Shared & Foundational 1");
        cut.FindAll("[data-testid=dws-feature-unassigned]").Should().BeEmpty();
    }

    // ── Gap 1: explorer state in the URL and in the session; Back / Forward; stale filters ───────────────────────────────────────────

    private static readonly Guid ProjectA = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private IRenderedComponent<DeliveryWorkspace> RenderAt(string url, DeliveryExplorerOptions? options = null, WeeklyDeliveryReport? report = null, string? selected = null)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<DeliveryWorkspace>(p => p.Add(x => x.Report, report ?? Report()).Add(x => x.Options, options ?? DeliveryExplorerOptions.Comparison)
            .Add(x => x.ProjectId, ProjectA).Add(x => x.SelectedId, selected).Add(x => x.OnSelect, (string? id) => _selected = id));
    }

    [Fact]
    public void Filters_view_and_sort_live_in_the_url_and_are_restored_when_the_explorer_is_opened_again_without_them()
    {
        Services.AddSingleton(new ExplorerViewStateStore());
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderAt($"delivery-reporting?project={ProjectA}&tab=stories");
        cut.Find("[data-testid=dws-view-list]").Change(true);
        cut.Find("[data-testid=dws-search]").Input("toggle");
        cut.Find("[data-testid=dws-type]").Change("Task");
        cut.Find("[data-testid=dws-change]").Change("changed");
        cut.Find("[data-testid=dws-sort-title]").Click();
        nav.Uri.Should().Contain("wi-view=list").And.Contain("wi-q=toggle").And.Contain("wi-type=Task").And.Contain("wi-change=changed").And.Contain("wi-sort=title")
            .And.Contain("tab=stories", "other query keys are kept");
        cut.FindAll("[data-testid=dws-list-row]").Select(r => r.GetAttribute("data-id")).Should().Equal($"t:{Dashboard}|T010");
        cut.Dispose();

        // Back from another tab: the link has no explorer state; the session remembers it for this project, report and mode.
        var again = RenderAt($"delivery-reporting?project={ProjectA}&tab=stories");
        again.Find("[data-testid=dws-master]").GetAttribute("data-view").Should().Be("list");
        again.Find("[data-testid=dws-search]").GetAttribute("value").Should().Be("toggle");
        again.FindAll("[data-testid=dws-list-row]").Select(r => r.GetAttribute("data-id")).Should().Equal($"t:{Dashboard}|T010");
        nav.Uri.Should().Contain("wi-q=toggle", "the restored state is written back to the address");
        again.Dispose();

        // Another report (or mode) never inherits it.
        var other = RenderAt($"delivery-reporting?project={ProjectA}&tab=stories", report: Report() with { ReportId = Guid.NewGuid() });
        other.Find("[data-testid=dws-master]").GetAttribute("data-view").Should().Be("hierarchy");
        other.Find("[data-testid=dws-search]").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Back_and_forward_re_read_the_state_from_the_url()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderAt($"delivery-reporting?project={ProjectA}&tab=stories&wi-view=list&wi-type=Task");
        cut.FindAll("[data-testid=dws-list-row]").Should().HaveCount(4);
        nav.NavigateTo($"delivery-reporting?project={ProjectA}&tab=stories&wi-view=list&wi-type=Task&wi-q=T012");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dws-list-row]").Select(r => r.GetAttribute("data-id")).Should().Equal($"t:{Dashboard}|T012"));
        nav.NavigateTo($"delivery-reporting?project={ProjectA}&tab=stories");
        cut.WaitForAssertion(() => cut.Find("[data-testid=dws-master]").GetAttribute("data-view").Should().Be("hierarchy"));
        cut.FindAll("[data-testid=dws-chip]").Should().BeEmpty();
    }

    [Fact]
    public void A_stale_or_incompatible_filter_in_the_url_is_dropped_with_a_note_instead_of_an_unexplained_empty_list()
    {
        var cut = RenderAt($"delivery-overview?project={ProjectA}&wi-module=Gone&wi-change=changed&wi-view=list", DeliveryExplorerOptions.CurrentState);
        cut.FindAll("[data-testid=dws-dropped-filter]").Select(p => p.TextContent).Should().Contain(t => t.Contains("Gone")).And.Contain(t => t.Contains("comparisons only"));
        cut.FindAll("[data-testid=dws-list-row]").Should().NotBeEmpty();
    }

    // ── Gap 7: keyboard, stable sort ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_list_is_a_keyboard_grid_arrows_move_the_active_row_and_enter_opens_it()
    {
        var cut = RenderAt($"delivery-reporting?project={ProjectA}&tab=stories&wi-view=list&wi-type=Task");
        var grid = cut.Find("[data-testid=dws-list]");
        grid.GetAttribute("role").Should().Be("grid");
        grid.GetAttribute("tabindex").Should().Be("0");
        grid.Focus();
        var ids = cut.FindAll("[data-testid=dws-list-row]").Select(r => r.GetAttribute("id")).ToList();
        cut.Find("[data-testid=dws-list]").GetAttribute("aria-activedescendant").Should().Be(ids[0], "focus starts on the first row");
        cut.Find("[data-testid=dws-list]").KeyDown("ArrowDown");
        cut.Find("[data-testid=dws-list]").KeyDown("ArrowDown");
        cut.Find("[data-testid=dws-list]").KeyDown("ArrowUp");
        cut.Find("[data-testid=dws-list]").GetAttribute("aria-activedescendant").Should().Be(ids[1]);
        cut.Find($"#{ids[1]}").ClassList.Should().Contain("is-active");
        _selected.Should().BeNull("moving is not selecting");
        cut.Find("[data-testid=dws-list]").KeyDown("Enter");
        _selected.Should().Be(cut.Find($"#{ids[1]}").GetAttribute("data-id"));
        cut.Find("[data-testid=dws-detail-card]").GetAttribute("data-id").Should().Be(_selected, "Enter opens the details panel");
        cut.Find("[data-testid=dws-list]").KeyDown("End");
        cut.Find("[data-testid=dws-list]").GetAttribute("aria-activedescendant").Should().Be(ids[^1]);
        cut.Find("[data-testid=dws-list]").KeyDown(" ");
        _selected.Should().Be(cut.Find($"#{ids[^1]}").GetAttribute("data-id"));
    }

    [Fact]
    public void Sorting_is_stable_equal_keys_keep_the_canonical_order_in_both_directions()
    {
        var cut = RenderAt($"delivery-reporting?project={ProjectA}&tab=stories&wi-view=list&wi-type=Task&wi-sort=module");
        var asc = cut.FindAll("[data-testid=dws-list-row]").Select(r => r.GetAttribute("data-id")).ToList();
        asc.Should().Equal($"t:{Dashboard}|T001", $"t:{Dashboard}|T002", $"t:{Dashboard}|T010", $"t:{Dashboard}|T012");
        cut.Find("[data-testid=dws-sort-module]").Click();
        cut.FindAll("[data-testid=dws-list-row]").Select(r => r.GetAttribute("data-id")).Should().Equal(asc, "one module: descending keeps equal keys in canonical order");
        cut.Find("[data-testid=dws-sort-title]").Click();
        cut.FindAll("[data-testid=dws-list-row]").Select(r => r.QuerySelector("[data-testid=dws-list-title]")!.TextContent).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        cut.Find("[data-testid=dws-sort-title]").Click();
        cut.FindAll("[data-testid=dws-list-row]").Select(r => r.QuerySelector("[data-testid=dws-list-title]")!.TextContent).Should().BeInDescendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    // ── Gap 6: the Overview's backlog is the explorer's Features view ───────────────────────────────────────────────────────────────

    private static DeliveryExplorerOptions Backlog(params string[] onlyFeatures)
    {
        static DeliveryFeatureGridRow Row(string key, string title, string module, BacklogStatus status, int done, int total, int attention, int priority) =>
            new(new BacklogItem(key, title, module, status, $"{done} of {total} tasks checked", 1, 1, done, total, 0, null) { Attention = attention }, "Unchanged",
                DeliveryDashboardTone.Neutral, "2 User Stories · 1 criteria", null, priority);
        var rows = new Dictionary<string, DeliveryFeatureGridRow>
        {
            [Dashboard] = Row(Dashboard, "Initial dashboard", "Frontend", BacklogStatus.InProgress, 2, 4, 1, 1),
            [DevMode] = Row(DevMode, "Developer Mode Settings Panel", "Frontend", BacklogStatus.NotAssessed, 0, 0, 0, 5),
            [Fresh] = Row(Fresh, "New export", "Backend", BacklogStatus.ChecklistComplete, 2, 2, 0, 2),
        };
        return DeliveryExplorerOptions.CurrentState with
        {
            Backlog = rows, Why = new Dictionary<string, string> { [Dashboard] = "Owned by module Frontend" },
            FeatureKeys = onlyFeatures.Length == 0 ? null : onlyFeatures.ToHashSet(StringComparer.Ordinal),
        };
    }

    [Fact]
    public void The_features_view_keeps_backlog_status_progress_and_priority_and_shares_selection_and_details()
    {
        string? opened = null;
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-overview?project={ProjectA}");
        var cut = Render<DeliveryWorkspace>(p => p.Add(x => x.Report, Report()).Add(x => x.Options, Backlog()).Add(x => x.ProjectId, ProjectA)
            .Add(x => x.OnSelect, (string? id) => _selected = id).Add(x => x.OnOpenFeature, (string key) => opened = key));
        cut.Find("[data-testid=dws-master]").GetAttribute("data-view").Should().Be("features", "the backlog opens on Features");
        cut.FindAll("[data-testid=dws-view-board]").Should().BeEmpty();
        var rows = cut.FindAll("[data-testid=dws-feature-row]");
        rows.Select(r => r.GetAttribute("data-id")).Should().Equal([DeliveryWorkItems.FeatureId(Dashboard), DeliveryWorkItems.FeatureId(Fresh), DeliveryWorkItems.FeatureId(DevMode)],
            "priority order: needs review, new, the rest; removed features are not in the current state");
        rows.Select(r => r.QuerySelector("[data-testid=dws-feature-status]")!.TextContent).Should().Equal(["In progress", "Task checklist complete", "Not assessed"],
            "checkbox completion is a checklist state, never a workflow status such as Done");
        rows[0].QuerySelector("[data-testid=dws-feature-checklist]")!.TextContent.Should().Be("2/4 · 50%");
        rows[0].QuerySelector("[data-testid=dws-feature-attention]")!.TextContent.Should().Be("1 issue");
        rows[0].QuerySelector("[data-testid=dws-feature-why]")!.TextContent.Should().Contain("Owned by module Frontend");

        rows[0].QuerySelector("[data-testid=dws-feature-open]")!.Click();
        _selected.Should().Be(DeliveryWorkItems.FeatureId(Dashboard));
        cut.Find("[data-testid=dws-detail-backlog]").TextContent.Should().StartWith("In progress");
        cut.Find("[data-testid=dws-detail-model]").TextContent.Should().Be("2 User Stories · 1 criteria");
        cut.Find("[data-testid=dws-open-feature]").Click();
        opened.Should().Be(Dashboard);

        cut.Find("[data-testid=dws-view-hierarchy]").Change(true);
        cut.Find("[data-testid=dws-row].is-selected").GetAttribute("data-id").Should().Be(DeliveryWorkItems.FeatureId(Dashboard), "one selection across views");
    }

    [Fact]
    public void The_pages_feature_filter_limits_every_view_to_the_same_features()
    {
        var cut = RenderAt($"delivery-overview?project={ProjectA}&wi-view=list", Backlog(Fresh));
        cut.FindAll("[data-testid=dws-list-row]").Should().OnlyContain(r => r.GetAttribute("data-id")!.Contains(Fresh));
        cut.Find("[data-testid=dws-view-features]").Change(true);
        cut.FindAll("[data-testid=dws-feature-row]").Should().ContainSingle();
    }

    [Fact]
    public void A_deep_link_to_a_task_opens_the_hierarchy_where_it_can_be_seen()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var task = $"t:{Dashboard}|T010";
        var cut = RenderAt($"delivery-overview?project={ProjectA}&item={Uri.EscapeDataString(task)}", Backlog(), selected: task);
        cut.Find("[data-testid=dws-master]").GetAttribute("data-view").Should().Be("hierarchy");
        nav.Uri.Should().Contain("wi-view=hierarchy");
        cut.Find("[data-testid=dws-detail-card]").GetAttribute("data-id").Should().Be(task);
    }
}
