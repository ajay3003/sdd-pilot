using System.Text;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Task Explorer keeps five things apart: the Task artifact's checkboxes, planning (parallelizable), traceability links, implementation
/// evidence and test evidence. The last two come from the SDD lifecycle graph through each task's linked requirements and
/// say "Not assessed" when nothing was looked at.
/// </summary>
public sealed class TaskExplorerSemanticsTests : BunitContext
{
    public TaskExplorerSemanticsTests()
    {
        Services.AddSingleton<MarkdownRenderingService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private const string Fixture = """
        ## Phase 1: Setup

        **Purpose**: Prepare the project

        - [x] T001 [P] Create the solution (FR-001)
        - [x] T002 [P] Add the data model (FR-002, SC-001)
        - [ ] T003 Write the importer for the extremely-long-identifier-without-any-spaces-AbcdefghijklmnopqrstuvwxyzAbcdefghijklmnopqrstuvwxyz (FR-003)

        ## Phase 2: Status work

        - [x] T004 Update the status page
        - [ ] T005 [US1] Search by name
        - [x] T006 Add integration test for search (FR-001)
        """;

    private IRenderedComponent<TaskExplorerPanel> Render(string text = Fixture, TaskEvidenceIndex? evidence = null) =>
        Render<TaskExplorerPanel>(p =>
        {
            p.Add(x => x.TasksText, text);
            if (evidence is not null) p.Add(x => x.Evidence, evidence);
        });

    private static string Item(IRenderedComponent<TaskExplorerPanel> cut, string id) => cut.Find($"[data-testid=te-summary-{id}]").TextContent;

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<TaskExplorerPanel> cut, string taskId) =>
        cut.FindAll(".te-row").First(r => r.QuerySelector(".te-task-id")?.TextContent == taskId);

    private static void Click(IRenderedComponent<TaskExplorerPanel> cut, string filterKey) =>
        cut.Find($".te-filter-chip[data-filter={filterKey}]").Click();

    private static IReadOnlyList<string> VisibleMatches(IRenderedComponent<TaskExplorerPanel> cut) =>
        cut.FindAll(".te-row.is-match .te-task-id").Select(e => e.TextContent).ToList();

    // ── Evidence fixtures (the shared lifecycle projection) ──────────────────────────────────────────────────────────────

    private static SddRequirementGraphRow GraphRow(string id, IEnumerable<SddImplementationEvidence>? implementation = null,
        IEnumerable<SddTestEvidence>? designed = null, IEnumerable<SddTestExecutionEvidence>? executions = null) =>
        new(new SemanticRequirement { Id = id, Text = id }, [], [], [.. implementation ?? []], [.. designed ?? []], [.. executions ?? []], false);

    private static TaskEvidenceIndex Index(SddLifecycleState lifecycle, params SddRequirementGraphRow[] rows) =>
        TaskEvidenceIndex.Build(new SddLifecycleProjection(
            new LifecycleProjectionMetadata(LifecycleProjectionMode.ActiveWorkspace, null, [], [], DateTimeOffset.UtcNow, []), rows), lifecycle);

    private static SddImplementationEvidence Code(string requirement, string currentness = "Current") =>
        new() { RequirementId = requirement, EvidenceType = "CodeLink", Currentness = currentness, ProviderId = "CodeTraceability", Confidence = "Confirmed" };

    private static SddLifecycleState AnalysedSource(params SddImplementationEvidence[] evidence)
    {
        var state = new SddLifecycleState();
        state.SourceSnapshots.Add(new SddSourceSnapshotReference { SnapshotId = "s1", AnalysisStatus = "Ready", Currentness = "Current" });
        state.ImplementationEvidence.AddRange(evidence);
        return state;
    }

    // ── Task status: marked done in the Task artifact ────────────────────────────────────────────────────────────────

    [Fact]
    public void TaskStatus_64Of65_IsMarkedDoneInTheTaskArtifact_NotImplementationCompletion()
    {
        var text = new StringBuilder("## Phase 1: Work\n\n");
        for (var i = 1; i <= 65; i++) text.Append($"- [{(i == 65 ? " " : "x")}] T{i:000} Task {i}\n");

        var cut = Render(text.ToString());

        Item(cut, "status").Should().Contain("Marked done in Task artifact").And.Contain("64 / 65").And.Contain("(98%)");
        cut.Find("[data-testid=te-summary]").TextContent.Should().NotContainAny("task completion", "implemented", "Completed");
        Item(cut, "implementation").Should().Contain("Not assessed", "a checkbox is not implementation evidence");
        Item(cut, "tests").Should().Contain("Not assessed", "a checkbox is not test evidence");
    }

    [Fact]
    public void CheckMark_IsLabelledAsTaskArtifactStatus()
    {
        var cut = Render();

        Row(cut, "T001").QuerySelector("[data-testid=te-task-check]")!.GetAttribute("aria-label").Should().Be("Marked done in Task artifact");
        Row(cut, "T003").QuerySelector("[data-testid=te-task-check]")!.GetAttribute("aria-label").Should().Be("Open in Task artifact");
    }

    [Fact]
    public void PhaseProgress_SaysMarkedDone_WithAnAccessibleExplanation()
    {
        var cut = Render();

        var chip = cut.FindAll("[data-testid=te-group-done]").First();
        chip.TextContent.Should().Be("2 / 3 done in the Task artifact");
        chip.GetAttribute("title").Should().Be("2 of 3 tasks marked done in the Task artifact");
    }

    [Fact]
    public void PhaseProgressBars_AreProgressbarsNamedAsTasksMarkedDoneInTheTaskArtifact()
    {
        var cut = Render();
        cut.FindAll(".te-view-btn").Single(b => b.TextContent.Trim() == "Impact").Click();

        var bars = cut.FindAll("[data-testid=te-phase-progress]");
        bars.Should().NotBeEmpty();
        var setup = bars.Single(b => b.GetAttribute("aria-label")!.StartsWith("Phase 1"));
        setup.GetAttribute("role").Should().Be("progressbar");
        setup.GetAttribute("aria-label").Should().EndWith("2 of 3 tasks marked done in Task artifact");
        setup.GetAttribute("aria-valuenow").Should().Be("2");
        setup.GetAttribute("aria-valuemax").Should().Be("3");
        cut.Markup.Should().Contain("Partly marked done").And.NotContain("In Progress", "a checkbox share is not implementation progress");
    }

    // ── Phases nested under a document title (e.g. "# Tasks: X" → "## Phase 1") ─────────────────────────────────────────

    private const string TitledFixture = """
        # Tasks: Adapter

        ## Format

        Notes.

        ## Phase 1: Setup

        - [x] T001 Create
        - [ ] T002 Configure

        ## Phase 2: Core

        ### User Story 1

        - [x] T003 [US1] Sync

        ## Phase 10: Polish

        - [x] T004 Docs

        ## Dependencies

        ### Phase Dependencies

        Text.
        """;

    [Fact]
    public void PhaseNodes_FindsPhasesUnderADocumentTitle_InDocumentOrder()
    {
        var tree = TaskExplorerService.Parse(TitledFixture);

        TaskExplorerService.PhaseNodes(tree.Roots).Select(p => p.Title).Should().Equal("Phase 1: Setup", "Phase 2: Core", "Phase 10: Polish");
        TaskExplorerService.PhaseNodes(TaskExplorerService.Parse(Fixture).Roots).Should().HaveCount(2, "flat layouts keep working");
        var model = TaskExplorerService.BuildSemanticModel(tree);
        model.Phases.Select(p => (p.Title, p.CompletedCount, p.TotalCount))
            .Should().Equal(("Phase 1: Setup", 1, 2), ("Phase 2: Core", 1, 1), ("Phase 10: Polish", 1, 1));
        model.PhaseProgress.Values.Sum(p => p.TotalTasks).Should().Be(4);
    }

    [Fact]
    public void PhaseProgress_RendersForPhasesUnderADocumentTitle()
    {
        var cut = Render(TitledFixture);
        cut.FindAll(".te-view-btn").Single(b => b.TextContent.Trim() == "Impact").Click();

        var bars = cut.FindAll("[data-testid=te-phase-progress]");
        bars.Select(b => b.GetAttribute("aria-label")).Should().Equal(
            "Phase 1: Setup: 1 of 2 tasks marked done in Task artifact",
            "Phase 2: Core: 1 of 1 task marked done in Task artifact",
            "Phase 10: Polish: 1 of 1 task marked done in Task artifact");
    }

    // ── Map view accessibility ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MapView_IsAFocusableNamedRegionOfListsOfButtons_NotOrphanTreeItems()
    {
        var cut = Render(TitledFixture);
        cut.FindAll(".te-view-btn").Single(b => b.TextContent.Trim() == "Map").Click();

        var map = cut.Find("[data-testid=te-map]");
        map.GetAttribute("tabindex").Should().Be("0", "the scroll region must be reachable by keyboard");
        map.GetAttribute("role").Should().Be("region");
        map.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        map.QuerySelectorAll("[role=treeitem]").Should().BeEmpty("the Map is not an ARIA tree");
        map.QuerySelectorAll(".te-map-phase-title").Select(h => h.TextContent).Should().Equal("Phase 1: Setup", "Phase 2: Core", "Phase 10: Polish");

        var items = map.QuerySelectorAll(".te-map-task");
        items.Should().HaveCount(4);
        items.Should().OnlyContain(b => b.TagName == "BUTTON" && b.GetAttribute("type") == "button"
            && b.ParentElement!.TagName == "LI" && b.ParentElement.ParentElement!.TagName == "UL");

        cut.Find(".te-map-task[data-task-id=T003]").Click();
        cut.Find(".te-map-task[data-task-id=T003]").GetAttribute("aria-current").Should().Be("true");
        cut.FindAll(".te-map-task[aria-current]").Should().ContainSingle();
    }

    [Fact]
    public void MapView_TasksAreOneRovingTabStop_WithArrowKeyHintAndPhaseIndex()
    {
        var cut = Render(TitledFixture);
        cut.FindAll(".te-view-btn").Single(b => b.TextContent.Trim() == "Map").Click();

        var map = cut.Find("[data-testid=te-map]");
        var hint = cut.Find("[data-testid=te-map-keys]");
        map.GetAttribute("aria-describedby").Should().Be(hint.Id);
        hint.ClassList.Should().Contain("visually-hidden");
        hint.TextContent.Should().Contain("arrow keys").And.Contain("Home").And.Contain("Page Down");

        var items = cut.FindAll(".te-map-task");
        items.Count(b => b.GetAttribute("tabindex") == "0").Should().Be(1, "the Map's tasks are a single tab stop");
        items[0].GetAttribute("tabindex").Should().Be("0", "with no selection the first task in reading order is the tab stop");
        items.Skip(1).Should().OnlyContain(b => b.GetAttribute("tabindex") == "-1");
        items.Select(b => b.GetAttribute("data-map-phase")).Should().Equal("0", "0", "1", "2");

        cut.Find(".te-map-task[data-task-id=T003]").Click();
        var after = cut.FindAll(".te-map-task");
        after.Single(b => b.GetAttribute("tabindex") == "0").GetAttribute("data-task-id").Should().Be("T003", "the selected task becomes the tab stop");

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextRovingAttach",
            "arrow-key movement is attached to the Map region");
    }

    // ── Planning ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParallelCount_HeaderAndParallelViewUseTheSameList()
    {
        var cut = Render();

        Item(cut, "planning").Should().Contain("2 parallelizable tasks").And.NotContain("[P]");
        cut.Find(".te-filter-chip[data-filter=Parallel]").TextContent.Should().Be("Parallelizable (2)");
        cut.FindAll(".te-view-btn").Single(b => b.TextContent.Trim() == "Parallelizable").Click();
        cut.Find("[data-testid=te-parallel-summary]").TextContent.Should().StartWith("2 parallelizable tasks").And.NotContain("[P]");
        cut.FindAll(".te-parallel-task").Should().HaveCount(2);
    }

    [Fact]
    public void ParallelBadge_IsReadable_NeverABareP()
    {
        var cut = Render();

        var chip = Row(cut, "T001").QuerySelector("[data-testid=te-parallel-chip]")!;
        chip.TextContent.Should().Be("Parallelizable");
        chip.GetAttribute("title").Should().Contain("can run in parallel with other tasks").And.Contain("Planning, not progress");
        cut.FindAll(".te-chip").Should().NotContain(c => c.TextContent.Trim() == "P");
        // [P] is source syntax: it stays in the task text but never appears as a badge, summary or chip label.
        cut.FindAll(".te-chip, .te-meta-chip, .te-filter-chip, [data-testid^=te-summary-]").Should().NotContain(e => e.TextContent.Contains("[P]"));
    }

    // ── Traceability ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoTraceabilityLinks_MeansNoLinkOfAnyKind()
    {
        var tree = TaskExplorerService.Parse(Fixture);
        var tasks = TaskExplorerService.ParallelizableTasks(tree.Roots); // warm-up; the predicate is tested directly below
        tasks.Should().HaveCount(2);
        var all = new List<TaskNode>();
        void Walk(IEnumerable<TaskNode> nodes) { foreach (var n in nodes) { if (n.NodeType == TaskNodeType.Task) all.Add(n); Walk(n.Children); } }
        Walk(tree.Roots);
        TaskNode Task(string id) => all.Single(t => t.TaskId == id);

        TaskExplorerService.HasNoTraceabilityLinks(Task("T001"), []).Should().BeFalse("FR only, no SC — still linked");
        TaskExplorerService.HasNoTraceabilityLinks(Task("T005"), []).Should().BeFalse("a user-story tag is a traceability link");
        TaskExplorerService.HasNoTraceabilityLinks(Task("T004"), ["T004"]).Should().BeFalse("a traceability-table row is a link");
        TaskExplorerService.HasNoTraceabilityLinks(Task("T004"), []).Should().BeTrue("nothing links it");

        var cut = Render();
        Item(cut, "traceability").Should().Contain("5 with traceability links · 1 with no traceability links");
        Click(cut, "NoLinks");
        VisibleMatches(cut).Should().Equal("T004");
        cut.Find("[data-testid=te-filter-status]").TextContent.Should().Contain("Showing 1 of 6 tasks");
        Click(cut, "HasLinks");
        VisibleMatches(cut).Should().BeEquivalentTo(["T001", "T002", "T003", "T005", "T006"]);
        cut.Find("[data-testid=te-filter-status]").TextContent.Should().Contain("Showing 5 of 6 tasks");
    }

    [Fact]
    public void UserStoriesFilter_DoesNotMatchTitlesThatMerelyContainUs()
    {
        var cut = Render();

        Click(cut, "OnlyUserStories");

        VisibleMatches(cut).Should().Equal("T005").And.NotContain("T004", "\"Status\" contains \"us\" but is no user story");
    }

    [Fact]
    public void Filters_AreButtonsWithPressedState_InSeparateGroups()
    {
        var cut = Render();

        cut.FindAll("[data-testid^=te-filters-] .te-filter-group-label").Select(l => l.TextContent)
            .Should().Equal("Task status", "Traceability", "Implementation evidence", "Test evidence", "Task topics & planning");
        Click(cut, "Completed");
        cut.Find(".te-filter-chip[data-filter=Completed]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find(".te-filter-chip[data-filter=All]").GetAttribute("aria-pressed").Should().Be("false");
        cut.Find(".te-filter-chip[data-filter=TestingTasks]").TextContent.Should().Be("Testing work");
        cut.Find(".te-filter-chip[data-filter=TestingTasks]").GetAttribute("title").Should().Contain("Not test results");
    }

    // ── Implementation evidence (shared lifecycle graph) ─────────────────────────────────────────────────────────────

    [Fact]
    public void NoSourceAnalysis_ImplementationIsNotAssessed_NeverMissingEverywhere()
    {
        var cut = Render(evidence: Index(new SddLifecycleState(), GraphRow("FR-001"), GraphRow("FR-002"), GraphRow("FR-003")));

        Item(cut, "implementation").Should().Contain("Not assessed").And.Contain("no current Source Analysis").And.NotContain("0 ");
        cut.Find("[data-testid=te-summary-implementation] a").GetAttribute("href").Should().Be("implementation-review");
        cut.Find("[data-testid=te-summary-implementation] a").TextContent.Should().Be("Implementation Evidence Review",
            "/implementation-review is Implementation Evidence Review; \"Implementation Review\" is the task-alignment page");
        cut.Find("[data-testid=te-implementation-not-assessed]").TextContent.Should().Be("Not assessed");
        cut.FindAll("[data-testid=te-impl-chip]").Should().BeEmpty();
        TaskExplorerService.MatchesFilter(TaskExplorerService.ParallelizableTasks(TaskExplorerService.Parse(Fixture).Roots)[0],
            "MissingImplementation", [], Index(new SddLifecycleState(), GraphRow("FR-001"))).Should().BeFalse("not assessed is not missing");
    }

    [Fact]
    public void CurrentStaleAndNoEvidence_AreDistinct_AndComeFromLinkedRequirements()
    {
        var lifecycle = AnalysedSource(Code("FR-001"), Code("FR-002", "PotentiallyStale"));
        var cut = Render(evidence: Index(lifecycle,
            GraphRow("FR-001", [Code("FR-001")]), GraphRow("FR-002", [Code("FR-002", "PotentiallyStale")]), GraphRow("FR-003")));

        Row(cut, "T001").QuerySelector("[data-testid=te-impl-chip]")!.TextContent.Should().Contain("Current");
        Row(cut, "T002").QuerySelector("[data-testid=te-impl-chip]")!.TextContent.Should().Contain("Stale");
        Row(cut, "T003").QuerySelector("[data-testid=te-impl-chip]")!.TextContent.Should().Contain("None");
        Row(cut, "T004").QuerySelector("[data-testid=te-impl-chip]").Should().BeNull("no linked requirement: nothing to resolve");
        // T001 and T006 both reference FR-001 (current); T002 stale; T003 none — never inferred from the checkbox.
        Item(cut, "implementation").Should().Contain("2 current · 1 stale · 1 none");

        Click(cut, "ImplStale");
        VisibleMatches(cut).Should().Equal("T002");
        Click(cut, "ImplNone");
        VisibleMatches(cut).Should().Equal("T003");
    }

    // ── Test evidence ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TestEvidence_DesignedExecutedPassedAndFailed_StayDistinct()
    {
        var lifecycle = AnalysedSource();
        lifecycle.TestEvidence.Add(new SddTestEvidence { RequirementId = "FR-002" });
        var designed = new SddTestEvidence { RequirementId = "FR-002", TestReference = "search-spec" };
        var passed = new SddTestExecutionEvidence { TestId = "t1", ExecutionState = "Completed", Result = "Passed" };
        var failed = new SddTestExecutionEvidence { TestId = "t2", ExecutionState = "Completed", Result = "Failed" };
        var cut = Render(evidence: Index(lifecycle,
            GraphRow("FR-001", executions: [passed]), GraphRow("FR-002", designed: [designed]), GraphRow("FR-003", executions: [failed])));

        Row(cut, "T001").QuerySelector("[data-testid=te-test-chip]")!.TextContent.Should().Contain("1 passed");
        Row(cut, "T002").QuerySelector("[data-testid=te-test-chip]")!.TextContent.Should().Contain("1 designed", "designed is not executed");
        Row(cut, "T003").QuerySelector("[data-testid=te-test-chip]")!.TextContent.Should().Contain("1 failed");
        Item(cut, "tests").Should().Contain("1 designed · 3 executed · 2 passed · 1 failing");

        Click(cut, "TestsDesigned");
        VisibleMatches(cut).Should().Equal("T002");
    }

    [Fact]
    public void WithoutTestEvidence_TestsAreNotAssessed()
    {
        var cut = Render(evidence: Index(AnalysedSource(), GraphRow("FR-001")));

        Item(cut, "tests").Should().Contain("Not assessed").And.Contain("no designed tests or test executions recorded").And.NotContain("0 ");
        cut.Find("[data-testid=te-summary-tests] a").GetAttribute("href").Should().Be("implementation-review", "test results are imported in Implementation Evidence Review");
        cut.Find("[data-testid=te-summary-tests] a").TextContent.Should().Be("Import test results in Implementation Evidence Review");
        cut.Find("[data-testid=te-tests-not-assessed]").TextContent.Should().Be("Not assessed");
        cut.FindAll("[data-testid=te-test-chip]").Should().BeEmpty();
    }

    // ── Readability ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LongTaskText_IsRenderedInFull_AndMarksExplainThemselves_WithoutALegend()
    {
        var cut = Render();

        Row(cut, "T003").QuerySelector(".te-node-title")!.TextContent.Should().Contain("AbcdefghijklmnopqrstuvwxyzAbcdefghijklmnopqrstuvwxyz");
        cut.FindAll("[data-testid=te-legend]").Should().BeEmpty("the labels say what they mean; the legend only repeated them");
        Row(cut, "T001").QuerySelector("[data-testid=te-task-check]")!.GetAttribute("title").Should().Be("Marked done in Task artifact");
    }

    [Fact]
    public void DetailTitle_ShowsTheFullTaskTextOnce_AndLongTextGetsShowMore()
    {
        var longTask = "Implement the importer that reads every person record from the upstream register, normalises names, addresses and "
            + "identifiers, de-duplicates by national identity number, and writes an audit entry for each rejected record so operators can follow up";
        var cut = Render($"## Phase 1: Work\n\n- [ ] T001 {longTask} (FR-001)\n- [ ] T002 Short task\n");

        // The parser keeps the whole text (it used to cut it silently at 200 characters).
        static IEnumerable<TaskNode> Flatten(IEnumerable<TaskNode> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
        Flatten(TaskExplorerService.Parse($"## Phase 1: Work\n\n- [ ] T001 {longTask}\n").Roots)
            .Single(n => n.TaskId == "T001").Title.Should().Be(longTask);
        // The tree previews very long text and says where the rest is.
        Row(cut, "T001").QuerySelector("[data-testid=te-node-preview]")!.TextContent.Should().Contain("full text in details");
        Row(cut, "T002").QuerySelector("[data-testid=te-node-preview]").Should().BeNull();

        Row(cut, "T001").Click();
        var title = cut.Find("[data-testid=te-details-title]");
        title.TextContent.Should().Contain("so operators can follow up", "the whole task text is in the title, never truncated");
        title.ClassList.Should().Contain("is-clamped");
        cut.FindAll(".te-details-desc").Should().BeEmpty("the full text is not repeated below the title");
        var toggle = cut.Find("[data-testid=te-details-title-toggle]");
        toggle.TextContent.Should().Be("Show more");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        cut.Find("[data-testid=te-details-title]").ClassList.Should().NotContain("is-clamped");
        cut.Find("[data-testid=te-details-title-toggle]").TextContent.Should().Be("Show less");

        Row(cut, "T002").Click();
        cut.Find("[data-testid=te-details-title]").TextContent.Should().Be("Short task");
        cut.FindAll("[data-testid=te-details-title-toggle]").Should().BeEmpty("short text needs no toggle");
    }

    [Fact]
    public void Details_SeparateTaskArtifactStatusFromEvidence()
    {
        var cut = Render(evidence: Index(AnalysedSource(Code("FR-001")), GraphRow("FR-001", [Code("FR-001")])));

        Row(cut, "T001").Click();

        cut.Find("[data-testid=te-detail-status]").TextContent.Should().Contain("Marked done in Task artifact");
        cut.Markup.Should().NotContain("Implementation Status");
        cut.Find("[data-testid=te-detail-implementation]").TextContent.Should().StartWith("Current implementation evidence (via FR-001)");
        cut.FindAll("[data-testid=te-detail-implementation-cta]").Should().BeEmpty("implementation was assessed");
        cut.Find("[data-testid=te-detail-tests]").TextContent.Should().StartWith("Not assessed");
    }

    [Fact]
    public void DetailStatus_HoldsOnlyTaskArtifactState_TopicsHaveTheirOwnSection()
    {
        var cut = Render("## Phase 1: Work\n\n- [x] T001 [P] Add integration test for authorization of the search endpoint (FR-001)\n");

        Row(cut, "T001").Click();

        var status = cut.Find("[data-testid=te-detail-status]");
        status.QuerySelectorAll(".te-chip").Should().BeEmpty("Testing, Security and Parallelizable are topics, not status");
        status.TextContent.Should().Contain("Marked done in Task artifact");
        var topics = cut.Find("[data-testid=te-detail-topics]");
        topics.QuerySelectorAll(".te-chip").Select(c => c.TextContent).Should().StartWith(["Parallelizable", "Testing", "Security"]);
        topics.TextContent.Should().Contain("not traceability links or evidence").And.NotContain("[P]");
    }

    [Fact]
    public void DetailGroups_TraceabilityTestAssetsImplementation_WithInformativeNotAssessedStates()
    {
        var cut = Render();

        Row(cut, "T006").Click(); // a testing task: "Add integration test for search (FR-001)"

        cut.FindAll("[data-testid=te-task-details] .te-detail-group-title").Select(h => h.TextContent)
            .Should().Equal("Traceability", "Test assets", "Implementation");
        var details = cut.Find("[data-testid=te-task-details]").TextContent;
        details.Should().NotContain("Linked Test Assets", "a task that mentions testing is a topic, not a linked test asset")
            .And.NotContain("Linked Architecture Notes").And.NotContain(" 0 ");
        cut.Find("[data-testid=te-detail-tests]").TextContent.Should().StartWith("Not assessed: no designed tests or test executions are recorded");
        cut.Find("[data-testid=te-detail-tests-cta]").GetAttribute("href").Should().Be("implementation-review");
        cut.Find("[data-testid=te-detail-tests-cta]").TextContent.Should().Be("Import test results in Implementation Evidence Review");
        cut.Find("[data-testid=te-detail-implementation]").TextContent.Should().StartWith("Not");
        cut.Find("[data-testid=te-detail-implementation-cta]").GetAttribute("href").Should().Be("implementation-review");
        cut.Find("[data-testid=te-detail-implementation-cta]").TextContent.Should().Be("Open Implementation Evidence Review");
        cut.Markup.Replace("Implementation Evidence Review", "").Should().NotContain("Implementation Review");
    }

    [Fact]
    public void TestingTopic_DoesNotCountAsTraceability()
    {
        var cut = Render("## Phase 1: Work\n\n- [ ] T001 Add unit test for the mapper\n- [ ] T002 Map the payload (FR-001)\n");

        Item(cut, "traceability").Should().Contain("1 with traceability links · 1 with no traceability links");
        Row(cut, "T001").Click();
        cut.Find("[data-testid=te-detail-no-links]").TextContent.Should().StartWith("No traceability links");
        cut.Find("[data-testid=te-detail-topics]").TextContent.Should().Contain("Testing");
    }

    // ── Count invariants: one predicate behind the summary, the filter badge and the filtered tree ───────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("person-adapter")]
    public void Counts_SummaryFilterBadgeAndFilteredTree_Agree(string? sample)
    {
        var text = sample is null ? Fixture : File.ReadAllText(TestDataHelper.ResolveSampleDataPath(sample, "tasks.md"));
        var cut = Render(text);
        var health = TaskExplorerService.ComputeEnrichedHealth(TaskExplorerService.Parse(text));

        (health.LinkedTasks + health.UnlinkedTasks).Should().Be(health.TotalTasks, "linked + unlinked == total");
        Item(cut, "traceability").Should().Contain($"{health.LinkedTasks} with traceability links · {health.UnlinkedTasks} with no traceability links");
        Item(cut, "artifact").Should().Contain($"{health.TotalTasks} task");
        Item(cut, "planning").Should().Contain($"{health.ParallelTasks} parallelizable task");

        void Expect(string key, int count)
        {
            if (count == 0) return;
            cut.Find($".te-filter-chip[data-filter={key}]").TextContent.Should().Contain($"({count})", key);
            Click(cut, key);
            cut.Find("[data-testid=te-filter-status]").TextContent.Should().Contain($"Showing {count} of {health.TotalTasks} tasks", key);
            VisibleMatches(cut).Should().HaveCount(count, key);
        }
        Expect("HasLinks", health.LinkedTasks);
        Expect("NoLinks", health.UnlinkedTasks);
        Expect("Parallel", health.ParallelTasks);

        Click(cut, "Completed");
        var done = VisibleMatches(cut).Count;
        Click(cut, "Open");
        var open = VisibleMatches(cut).Count;
        done.Should().Be(health.CompletedTasks);
        (done + open).Should().Be(health.TotalTasks, "marked done + open == total");
    }
}
