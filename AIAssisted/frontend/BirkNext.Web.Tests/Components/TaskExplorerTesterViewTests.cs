using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Task Explorer for testers: deterministic Testing/Verification categories (nothing guessed), a first-class Testing work view, task-file
/// completion that never reads as verified, linkage that never reads as implementation, separate test and implementation evidence, and
/// Changes that reuse stored revisions of the same Task artifact only.
/// </summary>
public class TaskExplorerTesterViewTests : BunitContext
{
    private static TaskNode Task(string line) =>
        TaskExplorerService.Parse($"# Tasks\n\n## Phase 1: Core\n\n{line}\n").Roots.SelectMany(Flatten).First(n => n.NodeType == TaskNodeType.Task);

    private static IEnumerable<TaskNode> Flatten(TaskNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));

    [Theory]
    [InlineData("- [ ] T010 Create PersonService class in src/PersonService.Domain/PersonService.cs", TaskCategory.Implementation)]
    [InlineData("- [ ] T011 Write xUnit tests for PersonService in tests/PersonService.Tests/PersonServiceTests.cs", TaskCategory.Testing)]
    [InlineData("- [ ] T012 Verify focus handling when the dialog closes", TaskCategory.Verification)]
    [InlineData("- [ ] T013 Add SecurityClassificationServiceTests for Kode 6/7 children", TaskCategory.Security)]
    [InlineData("- [ ] T014 Verify accessibility of the access table with a screen reader", TaskCategory.Accessibility)]
    [InlineData("- [ ] T015 Write integration tests for the GraphQL endpoint", TaskCategory.Integration)]
    [InlineData("- [ ] T016 Add xUnit tests for search in the PersonService.Tests project", TaskCategory.Testing)]
    public void Categories_ComeFromExplicitWording(string line, TaskCategory expected) =>
        TaskCategories.Of(Task(line)).Should().Contain(expected);

    [Fact]
    public void CombinedCategories_AreKept()
    {
        TaskCategories.Of(Task("- [ ] T013 Add SecurityClassificationServiceTests for Kode 6/7 children")).Should().Contain([TaskCategory.Testing, TaskCategory.Security]);
        TaskCategories.Of(Task("- [ ] T014 Verify accessibility of the access table with a screen reader")).Should().Contain([TaskCategory.Verification, TaskCategory.Accessibility]);
        TaskCategories.Of(Task("- [ ] T015 Write integration tests for the GraphQL endpoint")).Should().Contain([TaskCategory.Testing, TaskCategory.Integration]);
    }

    [Theory]
    [InlineData("- [x] T001 Create .NET 10 solution PersonService.sln with 4 src projects and PersonService.Tests")]
    [InlineData("- [x] T002 Configure NuGet package references: HotChocolate, xUnit, Testcontainers")]
    public void SetupOfTestProjectsOrPackages_IsSetup_NotTestingWork(string line)
    {
        var task = Task(line);
        TaskCategories.Of(task).Should().Equal(TaskCategory.Setup);
        TaskCategories.IsTestingWork(task).Should().BeFalse("creating a test project is setup, not a test a tester runs");
    }

    [Fact]
    public void UnclearWording_IsOther_NeverGuessed() =>
        TaskCategories.Of(Task("- [ ] T020 Coordinate with the product owner about the rollout")).Should().Equal(TaskCategory.Other);

    [Theory]
    [InlineData("- [ ] T030 Add unit test for the mapper")]
    [InlineData("- [ ] T031 Write unit tests for the mapper")]
    [InlineData("- [ ] T032 Add integration test for search")]
    [InlineData("- [ ] T033 Add integration tests for search")]
    public void Task002_SingularAndPluralTests_AreTestingWork(string line)
    {
        var task = Task(line);
        task.IsTestingTask.Should().BeTrue("TASK-002 counts this flag; a test task must never be missed");
        TaskCategories.IsTestingWork(task).Should().BeTrue();
    }

    private const string Tasks = """
        # Tasks: Person Module Core

        ## Phase 3: User Story 1 - Search children (Priority: P1)

        - [x] T010 [US1] Create PersonSearchService in src/Person/PersonSearchService.cs (FR-001)
        - [x] T011 [US1] Write xUnit tests for PersonSearchService in tests/Person/PersonSearchServiceTests.cs (FR-001)
        - [ ] T012 [US1] Verify Kode 6/7 children are excluded from search results
        - [x] T013 Write unit tests for the age filter
        """;

    [Fact]
    public void TestingWorkView_GroupsTestingAndVerification_AndNeverImpliesExecution()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, Tasks).Add(x => x.InitialView, "testing"));

        cut.Find("[data-testid=te-testing-count]").TextContent.Should().Be("3 tasks · 2 tests · 1 verification");
        cut.FindAll("[data-testid=te-testing-item]").Select(i => i.TextContent).Should().NotContain(t => t.Contains("PersonSearchService in src"),
            "implementation tasks stay under All tasks");
        cut.FindAll("[data-testid=te-category-chip]").Select(c => c.TextContent).Should().Contain(["Test", "Verification", "Security"]);
        cut.Find("[data-testid=te-testing-view]").TextContent.Should().Contain("does not mean the test was executed")
            .And.NotContain("Testing complete");
        cut.FindAll("[data-testid=te-testing-filter][data-key=Unlinked]").Should().ContainSingle("T013 has no requirement or user-story link");
    }

    [Fact]
    public void MarkedComplete_IsDeclared_NotVerified_AndTestExecutionIsNotAssessed()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, Tasks).Add(x => x.InitialView, "testing"));

        cut.Find("[data-testid=te-summary-status]").TextContent.Should().Contain("3 of 4").And.Contain("declared status only — not independently verified");
        cut.Find("[data-testid=te-summary-implementation]").TextContent.Should().Contain("Not assessed");
        cut.Find("[data-testid=te-summary-tests]").TextContent.Should().Contain("Not assessed");

        cut.FindAll(".te-testing-open").First(b => b.TextContent.Contains("T011")).Click();
        cut.Find("[data-testid=te-detail-status]").TextContent.Should().Contain("Marked done in Task artifact");
        cut.Find("[data-testid=te-detail-test-execution]").TextContent.Should().StartWith("Test execution: Not assessed")
            .And.Contain("not an executed test");
        cut.Find("[data-testid=te-detail-category]").TextContent.Should().Contain("Test");
    }

    [Fact]
    public void Coverage_IsLinkage_NeverImplementation()
    {
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, Tasks + "- [ ] T014 Wire FR-003 handler in src/Person/Handler.cs (FR-003)\n").Add(x => x.InitialView, "impact"));
        var coverage = cut.Find("[data-testid=te-impact-view]").TextContent;
        coverage.Should().Contain("Requirements linked to implementation tasks").And.Contain("of requirements have linked implementation tasks");
        coverage.Should().Contain("has no linked implementation task").And.Contain("This does not mean it is not implemented");
        coverage.Should().NotContain("Implementation Gaps").And.NotContain("Requirement Implementation Coverage").And.NotContain("Covered");
        cut.Find(".te-phase-dashboard-header").TextContent.Should().Contain("Declared task progress").And.Contain("not independently verified");
    }

    // ── Changes: stored revisions of the same Task artifact only ───────────────────────────────

    private static ArtifactExplorerState State(string path) =>
        new(WorkspaceArtifactType.Tasks, ExplorerArtifactStatus.Loaded, "import:new", "M2LB",
            [new ExplorerArtifact($"workspace:{path}", WorkspaceArtifactType.Tasks, "Tasks", path, path, ExplorerArtifactSource.Workspace, "File", 2, 2, "Unknown",
                ExplorerArtifactCurrentness.Current, null)],
            Selected: new ExplorerArtifact($"workspace:{path}", WorkspaceArtifactType.Tasks, "Tasks", path, path, ExplorerArtifactSource.Workspace, "File", 2, 2, "Unknown",
                ExplorerArtifactCurrentness.Current, null));

    private static SddLifecycleState Lifecycle(params SddArtifactRevision[] revisions)
    {
        var lifecycle = new SddLifecycleState();
        lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "old", ProjectName = "M2LB" });
        lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "new", ProjectName = "M2LB" });
        lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "other", ProjectName = "Other" });
        lifecycle.Revisions.AddRange(revisions);
        return lifecycle;
    }

    private static SddArtifactRevision Rev(string scope, string path, string content, int revision = 1) => new()
    {
        Role = "Tasks", WorkspaceScope = scope, SourceReference = path, FileName = path, Content = content, Revision = revision,
        CapturedAt = DateTimeOffset.UtcNow.AddDays(-revision),
    };

    [Fact]
    public void PreviousRevision_OfTheSameTaskArtifact_IsOffered_OtherPacksAndProjectsNever()
    {
        const string pack001 = "Person/specs/001-person-module/tasks.md";
        var lifecycle = Lifecycle(
            Rev("import:old", pack001, "- [ ] T001 old"),                                  // same project, same path, older content → offered
            Rev("import:new", pack001, "- [x] T001 current"),                              // the current content → not offered
            Rev("import:old", "Person/specs/002-barn-parent-feilkode/tasks.md", "- [ ] X"), // sibling pack → never
            Rev("import:other", pack001, "- [ ] T001 other project"));                     // another project → never

        var options = TaskRevisionHistory.Previous(lifecycle, State(pack001), "- [x] T001 current");
        options.Should().ContainSingle().Which.Content.Should().Be("- [ ] T001 old");

        TaskRevisionHistory.Previous(lifecycle, State("Person/specs/002-barn-parent-feilkode/tasks.md"), "- [ ] Y")
            .Should().ContainSingle().Which.Content.Should().Be("- [ ] X", "pack 002 sees only its own lineage");
    }

    [Fact]
    public void ChangesPanel_ComparesWithAStoredRevision_WithoutUpload_AndKeepsManualFallback()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<TaskChangesPanel>(p => p.Add(x => x.CurrentTasksText, "# Tasks\n\n- [x] T001 Do it\n- [ ] T002 New\n")
            .Add(x => x.PreviousRevisions, [new TaskRevisionOption("Revision 1", "# Tasks\n\n- [ ] T001 Do it\n")]));

        cut.Find("[data-testid=tc-revision-compare]").TextContent.Should().Contain("Compare with previous revision");
        cut.FindAll("textarea").Should().NotBeEmpty("manual upload/paste remains a fallback");
        cut.Find("[data-testid=tc-revision-compare-btn]").Click();
        cut.Markup.Should().Contain("T002");
    }
}
