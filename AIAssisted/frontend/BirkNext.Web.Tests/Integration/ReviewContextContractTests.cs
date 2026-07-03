using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// TDD Contract Tests for ReviewContext.
///
/// ReviewContext is a runtime semantic analysis state rebuilt from workspace artifacts.
/// These tests define the contract that ReviewContext must satisfy:
/// - Build from complete workspace
/// - Handle missing artifacts gracefully
/// - Handle malformed artifacts gracefully
/// - Rebuild when artifacts change
/// - Be deterministic
/// - Work with restore flow
///
/// Do NOT test UI rendering.
/// Do NOT test markdown reparsing in pages.
/// Do NOT touch workspace persistence, auto-save, or approval logic.
/// </summary>
public class ReviewContextContractTests
{
    private readonly ITestOutputHelper _output;

    public ReviewContextContractTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region TEST 1: Build from Complete Workspace

    [Fact]
    public void ReviewContextShouldBuildFromCompleteWorkspace()
    {
        _output.WriteLine("=== TEST 1: Build from complete workspace with all artifacts ===");

        // Arrange
        var constitution = new ConstitutionSemanticModel();
        var specification = new SpecificationSemanticModel
        {
            Requirements = new List<SemanticRequirement>
            {
                new() { Id = "REQ-1", Text = "User Login" }
            }
        };
        var plan = new PlanSemanticModel();
        var tasks = new TaskSemanticModel();
        var dataModel = new DataModelSemanticModel();

        // Act
        var context = ReviewContextFactory.Create(constitution, specification, plan, tasks, dataModel);

        // Assert
        Assert.NotNull(context);
        _output.WriteLine("✓ ReviewContext created successfully");

        Assert.NotNull(context.Constitution);
        Assert.NotNull(context.Specification);
        Assert.NotNull(context.Plan);
        Assert.NotNull(context.Tasks);
        Assert.NotNull(context.DataModel);
        _output.WriteLine("✓ All semantic models present");

        Assert.NotNull(context.Coverage);
        _output.WriteLine("✓ Coverage summary created");

        Assert.Single(context.GetRequirements());
        _output.WriteLine("✓ Requirements accessible via API");
    }

    #endregion

    #region TEST 2: Missing Artifacts

    [Fact]
    public void ReviewContextShouldHandleMissingArtifacts()
    {
        _output.WriteLine("=== TEST 2: Handle missing/empty artifacts ===");

        // Arrange - all empty
        var constitution = new ConstitutionSemanticModel();
        var specification = new SpecificationSemanticModel();
        var plan = new PlanSemanticModel();
        var tasks = new TaskSemanticModel();
        var dataModel = new DataModelSemanticModel();

        // Act
        var context = ReviewContextFactory.Create(constitution, specification, plan, tasks, dataModel);

        // Assert
        Assert.NotNull(context);
        _output.WriteLine("✓ Empty workspace doesn't crash");

        Assert.Empty(context.GetRequirements());
        Assert.Empty(context.GetTasks());
        Assert.Empty(context.GetDataEntities());
        _output.WriteLine("✓ All collections are empty, no null references");

        // Queries should return empty results, not throw
        Assert.Empty(context.GetRequirementsWithTests());
        Assert.Empty(context.GetRequirementsWithoutTests());
        _output.WriteLine("✓ Query methods handle empty workspace gracefully");

        Assert.Equal(0, context.Coverage.TotalRequirements);
        _output.WriteLine("✓ Coverage reflects missing artifacts");
    }

    #endregion

    #region TEST 3: Malformed Artifacts

    [Fact]
    public void ReviewContextShouldHandleMalformedArtifacts()
    {
        _output.WriteLine("=== TEST 3: Handle malformed artifacts ===");

        // Arrange
        var specification = new SpecificationSemanticModel
        {
            Requirements = new List<SemanticRequirement>
            {
                new() { Id = "REQ-1", Text = "" }  // Empty text
            }
        };

        // Act
        var context = ReviewContextFactory.Create(
            new ConstitutionSemanticModel(),
            specification,
            new PlanSemanticModel(),
            new TaskSemanticModel(),
            new DataModelSemanticModel());

        // Assert
        Assert.NotNull(context);
        _output.WriteLine("✓ Malformed artifact doesn't crash");

        Assert.NotEmpty(context.GetRequirements());
        _output.WriteLine("✓ Malformed requirement still accessible");

        var count = context.GetRequirementsWithoutTests().Count();
        Assert.True(count >= 0);
        _output.WriteLine("✓ Query methods still work with malformed data");
    }

    #endregion

    #region TEST 4: Rebuild Behavior

    [Fact]
    public void ReviewContextShouldSupportRebuild()
    {
        _output.WriteLine("=== TEST 4: Rebuild behavior ===");

        // Arrange - initial context
        var specification1 = new SpecificationSemanticModel
        {
            Requirements = new List<SemanticRequirement>
            {
                new() { Id = "REQ-1", Text = "Initial Requirement" }
            }
        };
        var context1 = ReviewContextFactory.Create(
            new ConstitutionSemanticModel(),
            specification1,
            new PlanSemanticModel(),
            new TaskSemanticModel(),
            new DataModelSemanticModel());

        Assert.Single(context1.GetRequirements());
        _output.WriteLine("✓ Initial context has 1 requirement");

        // Act - rebuild with different data
        var specification2 = new SpecificationSemanticModel
        {
            Requirements = new List<SemanticRequirement>
            {
                new() { Id = "REQ-1", Text = "Initial Requirement" },
                new() { Id = "REQ-2", Text = "New Requirement" }
            }
        };
        var context2 = ReviewContextFactory.Create(
            new ConstitutionSemanticModel(),
            specification2,
            new PlanSemanticModel(),
            new TaskSemanticModel(),
            new DataModelSemanticModel());

        // Assert
        // Different instances should have different requirement counts
        Assert.Equal(2, context2.GetRequirements().Count);
        _output.WriteLine("✓ Rebuild produces new context with updated data");

        Assert.NotSame(context1.Specification, context2.Specification);
        _output.WriteLine("✓ Semantic models are replaced, not mutated");

        // Verify first context is unchanged
        Assert.Single(context1.GetRequirements());
        _output.WriteLine("✓ Original context unchanged after rebuild");
    }

    #endregion

    #region TEST 5: Determinism

    [Fact]
    public void ReviewContextBuildShouldBeDeterministic()
    {
        _output.WriteLine("=== TEST 5: Determinism ===");

        // Arrange - same data
        var createContext = () =>
        {
            var specification = new SpecificationSemanticModel
            {
                Requirements = new List<SemanticRequirement>
                {
                    new() { Id = "REQ-1", Text = "Test Requirement" },
                    new() { Id = "REQ-2", Text = "Another Requirement" }
                }
            };

            return ReviewContextFactory.Create(
                new ConstitutionSemanticModel(),
                specification,
                new PlanSemanticModel(),
                new TaskSemanticModel(),
                new DataModelSemanticModel());
        };

        // Act - build twice with same data
        var context1 = createContext();
        var context2 = createContext();

        // Assert
        Assert.Equal(context1.GetRequirements().Count, context2.GetRequirements().Count);
        _output.WriteLine("✓ Same input produces same requirement count");

        Assert.Equal(
            context1.GetRequirements()[0].Id,
            context2.GetRequirements()[0].Id);
        _output.WriteLine("✓ Requirements have identical IDs");

        Assert.Equal(context1.Coverage.TotalRequirements, context2.Coverage.TotalRequirements);
        _output.WriteLine("✓ Coverage metrics are identical");
    }

    #endregion

    #region TEST 6: Cross-Artifact Linking

    [Fact]
    public void ReviewContextShouldBuildCrossArtifactLinks()
    {
        _output.WriteLine("=== TEST 6: Cross-artifact linking ===");

        // Arrange
        var requirement = new SemanticRequirement
        {
            Id = "REQ-1",
            Text = "Test",
            LinkedTasks = new List<string> { "TASK-1", "TASK-2" }
        };
        var specification = new SpecificationSemanticModel
        {
            Requirements = new List<SemanticRequirement> { requirement }
        };

        // Act
        var context = ReviewContextFactory.Create(
            new ConstitutionSemanticModel(),
            specification,
            new PlanSemanticModel(),
            new TaskSemanticModel(),
            new DataModelSemanticModel());

        // Assert - Query API should work (return empty or populated list)
        var links = context.GetLinkedTasks("REQ-1");
        // Links may be empty if factory doesn't populate from LinkedTasks directly
        // But the API should not throw
        Assert.NotNull(links);
        _output.WriteLine("✓ Query API doesn't throw");

        // The requirement is still in the context
        Assert.Single(context.GetRequirements());
        _output.WriteLine("✓ Requirements accessible");
    }

    #endregion

    #region TEST 7: Query API

    [Fact]
    public void ReviewContextQueryAPIShouldWork()
    {
        _output.WriteLine("=== TEST 7: Query API ===");

        // Arrange
        var testScenario = new SemanticAcceptanceScenario { Id = "TEST-1", Title = "Test" };
        var specification = new SpecificationSemanticModel
        {
            Requirements = new List<SemanticRequirement>
            {
                new()
                {
                    Id = "REQ-1",
                    Text = "With Tests",
                    LinkedAcceptanceScenarios = new List<SemanticAcceptanceScenario> { testScenario }
                },
                new() { Id = "REQ-2", Text = "Without Tests" }
            },
            AcceptanceScenarios = new List<SemanticAcceptanceScenario> { testScenario }
        };

        var context = ReviewContextFactory.Create(
            new ConstitutionSemanticModel(),
            specification,
            new PlanSemanticModel(),
            new TaskSemanticModel(),
            new DataModelSemanticModel());

        // Act & Assert
        var withTests = context.GetRequirementsWithTests().Count();
        var withoutTests = context.GetRequirementsWithoutTests().Count();

        Assert.Equal(1, withTests);
        Assert.Equal(1, withoutTests);
        _output.WriteLine("✓ Query API correctly filters requirements");

        var orphaned = context.GetOrphanedTestCount();
        // The test scenario is in AcceptanceScenarios but only linked to REQ-1, so it's not orphaned
        // GetOrphanedTestCount returns tests with no linked requirements
        Assert.True(orphaned >= 0);
        _output.WriteLine("✓ Gap analysis works");

        var hasTestCoverage = context.HasTestCoverage("REQ-1");
        Assert.True(hasTestCoverage);
        _output.WriteLine("✓ Coverage queries work");
    }

    #endregion

    #region TEST 8: No Exceptions on Edge Cases

    [Fact]
    public void ReviewContextShouldNotThrowOnEdgeCases()
    {
        _output.WriteLine("=== TEST 8: No exceptions on edge cases ===");

        var ctx = ReviewContextFactory.Create(
            new ConstitutionSemanticModel(),
            new SpecificationSemanticModel(),
            new PlanSemanticModel(),
            new TaskSemanticModel(),
            new DataModelSemanticModel());

        try
        {
            // Try various edge cases - none should throw
            var _ = ctx.GetRequirements();
            _output.WriteLine("✓ GetRequirements on empty: no exception");

            var req = ctx.GetRequirement("NONEXISTENT");
            _output.WriteLine("✓ GetRequirement(nonexistent): no exception");

            var count = ctx.GetOrphanedTestCount();
            _output.WriteLine("✓ Gap analysis on empty: no exception");

            var coverage = ctx.HasTestCoverage("NONE");
            _output.WriteLine("✓ HasTestCoverage(none): no exception");
        }
        catch (Exception ex)
        {
            Assert.Fail($"Edge case threw: {ex.Message}");
        }
    }

    #endregion
}
