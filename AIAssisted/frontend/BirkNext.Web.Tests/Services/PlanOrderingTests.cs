using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Reordering and removing in Plan &amp; prepare: a case moves only among its own scenario's cases, a scenario moves with all its cases as one block
/// among the scenarios of its group (nothing else moves), a step moves with its expected result, and a scenario is removed only when none of its
/// cases was reviewed or exported with a known id (otherwise it is excluded with a reason).
/// </summary>
public sealed class PlanOrderingTests
{
    private static TestCaseDraft Discovered(string id) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, ScenarioId = id },
        Generated = new TestCaseContent { Title = id, Steps = ["a", "b", "c"], StepExpected = ["A", null, "C"] },
    };

    private static TestCaseDraft Written(string id, string scenario) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, ScenarioId = id }, ScenarioKey = scenario, Generated = new TestCaseContent { Title = id },
    };

    // s1 (with m1, m2), s2, s3 (with m3), an unrelated hand-written case u.
    private static TestPlanDraft Plan() => new()
    {
        Cases = [Discovered("s1"), Written("m1", "s1"), Discovered("s2"), Written("m2", "s1"), Discovered("s3"), Written("m3", "s3"),
            new TestCaseDraft { Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, ScenarioId = "u" }, Generated = new TestCaseContent { Title = "u" } }],
    };

    private static List<string> Ids(TestPlanDraft p) => [.. p.Cases.Select(c => c.Source.ScenarioId)];

    [Fact]
    public void A_case_moves_only_among_its_own_scenarios_cases()
    {
        var moved = PlanOrdering.MoveCase(Plan(), "m2", 0);
        Ids(moved).Should().Equal("m2", "s1", "s2", "m1", "s3", "m3", "u");
        ScenarioPlanning.Groups(moved).Single(g => g.Key == "s1").Cases.Select(c => c.Source.ScenarioId).Should().Equal(["s1", "m2", "m1"], "the scenario's own case stays first in the group view");
        PlanOrdering.MoveCase(Plan(), "m1", 99).Should().Match<TestPlanDraft>(p => Ids(p).SequenceEqual(new[] { "s1", "m2", "s2", "m1", "s3", "m3", "u" }), "clamped to the last slot");
        Ids(PlanOrdering.MoveCase(Plan(), "nope", 0)).Should().Equal(Ids(Plan()), "an unknown case changes nothing");
    }

    [Fact]
    public void A_scenario_moves_with_all_its_cases_as_one_block()
    {
        var moved = PlanOrdering.MoveScenario(Plan(), "s3", ["s1", "s2", "s3"], 0);
        ScenarioPlanning.Groups(moved).Where(g => !g.Unresolved).Select(g => g.Key).Should().Equal("s3", "s1", "s2");
        Ids(moved).Should().Equal("s3", "m3", "s1", "m1", "m2", "s2", "u");
        var withinGroup = PlanOrdering.MoveScenario(Plan(), "s1", ["s1", "s3"], 1);
        ScenarioPlanning.Groups(withinGroup).Where(g => !g.Unresolved).Select(g => g.Key).Should().Equal(["s3", "s2", "s1"], "only the group's two scenarios swap; s2 (another group) keeps its slot");
        Ids(withinGroup).Should().Equal("s3", "m3", "s2", "s1", "m1", "m2", "u");
    }

    [Fact]
    public void A_step_moves_with_its_expected_result()
    {
        var c = PlanOrdering.MoveStep(Discovered("s1"), 2, 0);
        c.EffectiveSteps.Should().Equal("c", "a", "b");
        c.EffectiveStepExpected.Should().Equal("C", "A", null);
        c.Generated.Steps.Should().Equal(["a", "b", "c"], "the evidence is kept apart");
        PlanOrdering.MoveStep(Discovered("s1"), 5, 0).Edits.Steps.Should().BeNull("an unknown step changes nothing");
    }

    [Fact]
    public void A_scenario_is_removed_only_when_nothing_of_it_was_reviewed()
    {
        PlanOrdering.RemoveScenarioBlocker(Plan(), "s3").Should().BeNull();
        Ids(PlanOrdering.RemoveScenario(Plan(), "s3")).Should().Equal("s1", "m1", "s2", "m2", "u");

        var reviewed = Plan() with { Cases = [.. Plan().Cases.Select(c => c.Source.ScenarioId == "m3" ? c with { Review = TestCaseReviewState.Accepted } : c)] };
        PlanOrdering.RemoveScenarioBlocker(reviewed, "s3").Should().Contain("exclude the scenario with a reason");
        PlanOrdering.RemoveScenario(reviewed, "s3").Cases.Should().HaveCount(7, "nothing is removed");

        var exported = Plan() with { Cases = [.. Plan().Cases.Select(c => c.Source.ScenarioId == "s2" ? c with { VerifiedAzureDevOpsTestCaseId = "4711" } : c)] };
        PlanOrdering.RemoveScenarioBlocker(exported, "s2").Should().Contain("Azure DevOps id");
        PlanOrdering.RemoveScenarioBlocker(new TestPlanDraft { Cases = [Discovered("only")] }, "only").Should().Contain("at least one test case");
    }
}
