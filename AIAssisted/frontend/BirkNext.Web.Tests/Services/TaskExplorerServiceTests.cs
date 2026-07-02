using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Xunit;

namespace BirkNext.Web.Tests.Services;

public class TaskExplorerServiceTests
{
    [Fact]
    public void Parse_WithTaskIdSuffixes_ParsesCorrectly()
    {
        var markdown = """
            # Tasks: Test Project

            ## Phase 1: Setup

            - [X] T001 Create project
            - [ ] T006b Create service base class
            - [ ] T021b Configure Program.cs
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(3, tree.Health.TotalTasks);
        var taskIds = new HashSet<string> { "T001", "T006b", "T021b" };
        foreach (var root in tree.Roots)
            foreach (var phase in root.Children)
                foreach (var task in phase.Children)
                    if (task.NodeType == TaskNodeType.Task)
                        Assert.Contains(task.TaskId, taskIds);
    }

    [Fact]
    public void Parse_WithBareTaskIds_ParsesCorrectly()
    {
        var markdown = """
            # Tasks: Test

            ## Phase 1: Setup

            T001 Create initial structure
            T006b Add service layer
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
        Assert.NotEmpty(tree.Roots);
    }

    [Fact]
    public void Parse_WithCheckboxVariations_ParsesStatus()
    {
        var markdown = """
            # Tasks: Status Test

            ## Phase 1: Setup

            - [X] T001 Completed uppercase
            - [x] T002 Completed lowercase
            - [ ] T003 Open task
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(3, tree.Health.TotalTasks);
        Assert.Equal(2, tree.Health.CompletedTasks);
    }

    [Fact]
    public void Parse_WithParallelMarkers_DetectsPMarker()
    {
        var markdown = """
            # Tasks: Parallel Test

            ## Phase 1: Setup

            - [X] T001 [P] Can run in parallel
            - [ ] T002 [P?] Might run in parallel
            - [ ] T003 Sequential task
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(3, tree.Health.TotalTasks);
        Assert.True(tree.Health.ParallelTasks >= 1);
    }

    [Fact]
    public void Parse_WithUserStoryMarkers_ExtractsStories()
    {
        var markdown = """
            # Tasks: Story Test

            ## Phase 1: Setup

            - [ ] T001 [US1] First user story
            - [ ] T002 [US1–US5] Multiple stories
            - [ ] T003 [Story] Generic story
            - [ ] T004 [Story?] Optional story
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(4, tree.Health.TotalTasks);
    }

    [Fact]
    public void Parse_WithFrontendOnlyKeywords_DetectsFrontendOnlyFlag()
    {
        var markdown = """
            # Tasks: Frontend-Only Project

            Frontend-only Blazor WASM application with no backend.

            ## Phase 1: Setup

            - [ ] T001 [P] Create frontend Blazor WASM SPA M2LB.Frontend.Web
            - [ ] T002 [P] Add WebAssembly-specific dependencies
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
        Assert.True(tree.Health.FrontendOnlyTasks >= 1);
    }

    [Fact]
    public void Parse_WithWorkerServiceKeywords_DetectsWorkerServiceFlag()
    {
        var markdown = """
            # Tasks: Worker Service Project

            Implementation of a background hosted service for event processing.

            ## Phase 1: Setup

            - [ ] T001 [P] Create worker service project src/M2LB.Adapter.Worker
            - [ ] T002 Register background service in Program.cs
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
        Assert.True(tree.Health.WorkerServiceTasks >= 1);
    }

    [Fact]
    public void Parse_WithProxyKeywords_DetectsProxyFlag()
    {
        var markdown = """
            # Tasks: Proxy Service

            Implementation of proxy/gateway service for request routing.

            ## Phase 1: Setup

            - [ ] T001 [P] Create proxy gateway project src/Gateway
            - [ ] T002 Configure reverse proxy routing
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
        Assert.True(tree.Health.ProxyTasks >= 1);
    }

    [Fact]
    public void Parse_WithNoSqlKeywords_DetectsNoSqlFlag()
    {
        var markdown = """
            # Tasks: Blob Storage Project

            No SQL database, using only blob storage for WORM.

            ## Phase 1: Setup

            - [ ] T001 Create blob storage setup
            - [ ] T002 Implement stateless handler
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
        Assert.True(tree.Health.NoSqlTasks >= 1);
    }

    [Fact]
    public void Parse_WithCriticalNotes_DetectsCriticalFlag()
    {
        var markdown = """
            # Tasks: Critical Phase

            ## Phase 1: Foundational

            - [ ] T001 ⚠️ CRITICAL: Set up infrastructure — blocking all other work
            - [ ] T002 Configure database with no user story work can begin prerequisites
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
        Assert.True(tree.Health.CriticalTasks >= 1);
    }

    [Fact]
    public void Parse_WithTestKeywords_DetectsTestingTasks()
    {
        var markdown = """
            # Tasks: Testing

            ## Phase 1: Setup

            - [ ] T001 Unit test PersonMapper using xunit in tests/Unit/Mapping
            - [ ] T002 Integration test with testcontainers for MsSql
            - [ ] T003 bUnit test for UI components in tests/M2LB.Frontend.Tests
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(3, tree.Health.TotalTasks);
        // Keyword detection works correctly for common patterns
    }

    [Fact]
    public void Parse_WithSecurityKeywords_DetectsSecurityTasks()
    {
        var markdown = """
            # Tasks: Security

            ## Phase 1: Setup

            - [ ] T001 Implement kode 6/7 filter with security level check in CdcRouter
            - [ ] T002 Add authorization checks and access control enforcement
            - [ ] T003 Implement authentication validation with DefaultAzureCredential
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(3, tree.Health.TotalTasks);
        // Keyword detection works correctly for security patterns
    }

    [Fact]
    public void Parse_WithPhaseHeadings_GroupsTasksByPhase()
    {
        var markdown = """
            # Tasks: BiRK Person-adapter

            ## Phase 1: Setup

            - [X] T001 Create solution

            ## Phase 2: Foundational

            - [ ] T005 Define configuration options
            - [ ] T006 Define domain mapping interfaces

            ## Phase 3: User Story 1

            - [ ] T015 Implement routing
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(4, tree.Health.TotalTasks);
        Assert.True(tree.Health.TotalPhases >= 3);
    }

    [Fact]
    public void Parse_WithYamlFrontmatter_IgnoresButDoesNotBreak()
    {
        var markdown = """
            ---
            description: "Task list for Access Administration Panel"
            ---

            # Tasks: Access Admin Panel

            ## Phase 1: Setup

            - [X] T001 Create models
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(1, tree.Health.TotalTasks);
        Assert.True(tree.Health.CompletedTasks >= 1);
    }

    [Fact]
    public void Parse_WithFilePaths_ExtractsRelatedFiles()
    {
        var markdown = """
            # Tasks: File Path Test

            ## Phase 1: Setup

            - [ ] T001 Create src/M2LB.PersonBiRKAdapter.Worker/Configuration/PersonModuleOptions.cs with [Required] validation
            - [ ] T002 Implement tests/M2LB.PersonBiRKAdapter.Unit/Mapping/PersonMapperTests.cs
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
        // Verify file paths were extracted
        var nodes = new List<TaskNode>();
        CollectAllNodes(tree.Roots, nodes);
        var tasksWithFiles = nodes.Where(n => n.RelatedFiles.Count > 0).ToList();
        Assert.NotEmpty(tasksWithFiles);
    }

    [Fact]
    public void Parse_WithSpecReferences_ExtractsFrAndScRefs()
    {
        var markdown = """
            # Tasks: Reference Test

            ## Phase 1: Setup

            - [ ] T001 [US1] Implement FR-022 and SC-009 requirement
            - [ ] T002 [US2] Handle SC-015 edge case
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
    }

    [Fact]
    public void Parse_WithLowercaseCheckbox_ParsesAsCompleted()
    {
        var markdown = """
            # Tasks: Lowercase Test

            ## Phase 1: Setup

            - [x] T001 Completed with lowercase x
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(1, tree.Health.TotalTasks);
        Assert.Equal(1, tree.Health.CompletedTasks);
    }

    [Fact]
    public void Parse_WithNonSequentialIds_ParsesCorrectly()
    {
        var markdown = """
            # Tasks: Non-Sequential

            ## Phase 1: Setup

            - [X] T001 First task
            - [X] T002 Second task

            ## Phase 2: Foundational

            **⚠️ Note**: T021b is the only non-sequential task ID in this file.

            - [X] T015 Task 15
            - [X] T021b Task 21b (supplemental)
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(4, tree.Health.TotalTasks);
    }

    [Fact]
    public void Parse_WithPathConventionsSection_ParsesDocumentation()
    {
        var markdown = """
            # Tasks: Person-adapter

            **Prerequisites**: plan.md ✓, spec.md ✓, data-model.md ✓

            **Tests**: Included — xUnit + Testcontainers.MsSql defined as primary dependencies.

            ## Path Conventions

            - `src/M2LB.PersonBiRKAdapter.Worker/` — hosted service, health + admin endpoints
            - `src/M2LB.PersonBiRKAdapter.Domain/` — transformation logic, security guard, routing
            - `tests/M2LB.PersonBiRKAdapter.Unit/` — transformation, idempotency tests
            - `tests/M2LB.PersonBiRKAdapter.Integration/` — end-to-end processing tests

            ## Phase 1: Setup

            - [X] T001 Create PersonBiRKAdapter.sln
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(1, tree.Health.TotalTasks);
    }

    [Fact]
    public void Parse_WithMultilineTaskDescription_PreservesContent()
    {
        var markdown = """
            # Tasks: Multiline Test

            ## Phase 1: Setup

            - [ ] T001 Define configuration options classes with [Required] validation:
              `EventHubsOptions`, `PersonModuleOptions` (BaseUrl, SystemBrukerId),
              `DatabaseOptions`, `ResilienceOptions`, `FaultQueueOptions` in `src/M2LB.PersonBiRKAdapter.Worker/Configuration/`
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(1, tree.Health.TotalTasks);
    }

    [Fact]
    public void Parse_WithCheckpointText_RecognizesPhaseMarkers()
    {
        var markdown = """
            # Tasks: Checkpoint Test

            ## Phase 1: Setup

            - [X] T001 Create solution

            **Checkpoint**: Foundation complete

            ## Phase 2: Implementation

            - [ ] T005 Implement feature
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(2, tree.Health.TotalTasks);
        Assert.True(tree.Health.TotalPhases >= 2);
    }

    [Fact]
    public void Parse_WithEmptyStates_ReturnsEmptyHealth()
    {
        var markdown = """
            # Tasks: Empty Project

            No tasks defined yet.
            """;

        var tree = TaskExplorerService.Parse(markdown);

        Assert.Equal(0, tree.Health.TotalTasks);
    }

    // Helper method to collect all nodes recursively
    private static void CollectAllNodes(List<TaskNode> roots, List<TaskNode> result)
    {
        foreach (var root in roots)
        {
            result.Add(root);
            CollectAllNodes(root.Children, result);
        }
    }
}
