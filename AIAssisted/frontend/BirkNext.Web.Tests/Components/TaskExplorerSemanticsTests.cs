using System.Text;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Task Explorer keeps five things apart: the Task artifact's checkboxes, planning ([P]), traceability links, implementation
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

    // ── Planning ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParallelCount_HeaderAndParallelViewUseTheSameList()
    {
        var cut = Render();

        Item(cut, "planning").Should().Contain("2 parallelizable tasks [P]");
        cut.FindAll(".te-view-btn").Single(b => b.TextContent.Trim() == "Parallel").Click();
        cut.Find("[data-testid=te-parallel-summary]").TextContent.Should().StartWith("2 parallelizable tasks [P]");
    }

    [Fact]
    public void ParallelBadge_IsReadable_NeverABareP()
    {
        var cut = Render();

        var chip = Row(cut, "T001").QuerySelector("[data-testid=te-parallel-chip]")!;
        chip.TextContent.Should().Be("⇉ Parallel");
        chip.GetAttribute("title").Should().Contain("Marked [P] in the Task artifact");
        cut.FindAll(".te-chip").Should().NotContain(c => c.TextContent.Trim() == "P");
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
        Item(cut, "traceability").Should().Contain("5 linked · 1 with no links");
        Click(cut, "NoLinks");
        VisibleMatches(cut).Should().Equal("T004");
        cut.Find("[data-testid=te-filter-status]").TextContent.Should().Contain("Showing 1 of 6 tasks");
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

        Item(cut, "implementation").Should().Contain("Not assessed");
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

        Item(cut, "tests").Should().Contain("Not assessed");
        cut.Find("[data-testid=te-tests-not-assessed]").TextContent.Should().Be("Not assessed");
        cut.FindAll("[data-testid=te-test-chip]").Should().BeEmpty();
    }

    // ── Readability ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LongTaskText_IsRenderedInFull_AndTheLegendExplainsTheMarks()
    {
        var cut = Render();

        Row(cut, "T003").QuerySelector(".te-node-title")!.TextContent.Should().Contain("AbcdefghijklmnopqrstuvwxyzAbcdefghijklmnopqrstuvwxyz");
        var legend = cut.Find("[data-testid=te-legend]");
        legend.TagName.Should().Be("DETAILS");
        legend.TextContent.Should().Contain("Marked done in the Task artifact").And.Contain("Not implementation or test evidence")
            .And.Contain("planning, not progress");
    }

    [Fact]
    public void Details_SeparateTaskArtifactStatusFromEvidence()
    {
        var cut = Render(evidence: Index(AnalysedSource(Code("FR-001")), GraphRow("FR-001", [Code("FR-001")])));

        Row(cut, "T001").Click();

        cut.Markup.Should().Contain("Task artifact status").And.NotContain("Implementation Status");
        cut.Find("[data-testid=te-detail-implementation]").TextContent.Should().StartWith("Current implementation evidence (via FR-001)");
        cut.Find("[data-testid=te-detail-tests]").TextContent.Should().StartWith("Not assessed");
    }
}
