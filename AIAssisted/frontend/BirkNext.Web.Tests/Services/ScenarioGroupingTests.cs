using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>Scenario grouping (2026-11-26): exclusive groups (a scenario once per tree), specification-scoped stories, honest «unknown» groups,
/// story order by number, and the default/preference rules.</summary>
public sealed class ScenarioGroupingTests
{
    private static S.Item Item(string id, string? spec, string? storyId, ScenarioCandidateType type = ScenarioCandidateType.BusinessFunctional, string area = "") =>
        new(new DiscoveredScenario
        {
            CandidateId = id, Type = type, Area = area,
            Story = spec is null ? null : new CoverageUserStory { Key = CoverageUserStory.KeyOf(spec, storyId!), StoryId = storyId, Title = "T", SpecificationPath = spec },
        }, new ScenarioOwnershipInfo(ScenarioPurpose.Unclassified, "spec", TestingWorkspace.ManualTesting, null, default, ""), S.Relevance.Manual, id, area, default!, null);

    [Fact]
    public void US1_of_two_specifications_are_two_groups_and_scenarios_without_a_story_get_their_own_group_last()
    {
        var items = new[] { Item("a", "A/specs/001-x/spec.md", "US1"), Item("b", "B/specs/001-y/spec.md", "US1"), Item("c", null, null, ScenarioCandidateType.BusinessE2E), Item("d", "A/specs/001-x/spec.md", "US10"), Item("e", "A/specs/001-x/spec.md", "US2") };
        var groups = ScenarioGrouping.Build(items, ScenarioGroupAxis.Story, ScenarioGroupAxis.None);
        groups.Select(g => (g.Label, g.Items.Count)).Should().Equal(("US1 — T", 1), ("US2 — T", 1), ("US10 — T", 1), ("US1 — T", 1), ("Scenarioer uten brukerhistorie", 1));
        groups.SelectMany(g => g.Items).Select(i => i.Id).Should().OnlyHaveUniqueItems().And.HaveCount(5);
    }

    [Fact]
    public void Test_level_keeps_candidates_visible_and_unknown_values_have_honest_groups()
    {
        var items = new[] { Item("e2e", "A/specs/001-x/spec.md", "US1", ScenarioCandidateType.BusinessE2E), Item("int", "A/specs/001-x/spec.md", "US1", ScenarioCandidateType.Integration), Item("ac", null, null) };
        var levels = ScenarioGrouping.Build(items, ScenarioGroupAxis.TestLevel, ScenarioGroupAxis.Area);
        levels.Select(g => g.Label).Should().Equal("Ende-til-ende-tester", "Integrasjonstester", "Uavklart testnivå");
        levels[0].Candidates.Should().Be(1, "a documented flow is an E2E candidate, shown as one");
        levels[0].Children.Single().Label.Should().Be("Uavklart område");
        ScenarioGrouping.Build(items, ScenarioGroupAxis.Module, ScenarioGroupAxis.None).Select(g => g.Label).Should().Equal("A", "Uten modultilknytning");
    }

    private static S.Item Typed(string id, string? spec, string? storyId, ScenarioRequirementType type) =>
        Item(id, spec, storyId) with { TypeResult = new ScenarioTypeResult(type, [], ScenarioTypeBasis.ScenarioContent, "", []) };

    [Fact]
    public void AC01_each_acceptance_scenario_is_grouped_by_its_own_type_under_the_story_and_the_story_only_gets_a_child_summary()
    {
        const string spec = "A/specs/001-x/spec.md";
        var items = new[]
        {
            Typed("ac1", spec, "US2", ScenarioRequirementType.Functional), Typed("ac2", spec, "US2", ScenarioRequirementType.Functional),
            Typed("ac3", spec, "US2", ScenarioRequirementType.NonFunctional), Typed("ac4", spec, "US2", ScenarioRequirementType.Mixed),
            Typed("ac5", spec, "US2", ScenarioRequirementType.Unclassified), Typed("e2e", null, null, ScenarioRequirementType.NonFunctional),
        };
        var (primary, secondary) = ScenarioGrouping.Default();
        var tree = ScenarioGrouping.Build(items, primary, secondary);
        tree.Select(g => g.Label).Should().Equal("US2 — T", "Scenarioer uten brukerhistorie");
        tree[0].Children.Select(g => (g.Label, string.Join(",", g.Items.Select(i => i.Id)))).Should().Equal(
            ("Funksjonelle", "ac1,ac2"), ("Ikke-funksjonelle", "ac3"), ("Blandede", "ac4"), ("Uavklarte", "ac5"));
        tree[1].Children.Single().Label.Should().Be("Ikke-funksjonelle", "a standalone scenario keeps its own type with no story invented");
        // No majority vote, no story type: the summary counts the ACs; nothing classifies US2.
        ScenarioGrouping.TypeSummary(tree[0].Items).Select(x => ScenarioGroupAxes.TypeCount(x.Type, x.Count)).Should().Equal("2 funksjonelle", "1 ikke-funksjonell", "1 blandet", "1 uavklart");
        ScenarioGrouping.TypeSummary([.. tree[0].Items, .. tree[0].Items]).Sum(x => x.Count).Should().Be(5, "an AC is counted once");
        // The dimensions stay independent: the same ACs grouped by test level do not change their type groups.
        ScenarioGrouping.Build(items, ScenarioGroupAxis.TestLevel, ScenarioGroupAxis.ScenarioType).SelectMany(g => g.Children).Sum(g => g.Items.Count).Should().Be(6);
    }

    [Fact]
    public void Requirements_without_a_scenario_are_never_counted_as_scenarios_without_a_story()
    {
        var req = Item("r", null, null) with { Candidate = Item("r", null, null).Candidate with { CanonicalKey = "requirement:M|FR-01" } };
        var groups = ScenarioGrouping.Build([Item("a", "A/specs/001-x/spec.md", "US1"), Item("standalone", null, null), req], ScenarioGroupAxis.Story, ScenarioGroupAxis.None);
        groups.Select(g => (g.Label, string.Join(",", g.Items.Select(i => i.Id)))).Should().Equal(
            ("US1 — T", "a"), ("Scenarioer uten brukerhistorie", "standalone"), ("Krav uten akseptansescenario", "r"));
    }

    [Fact]
    public void Defaults_and_preferences()
    {
        // 2026-11-26 AC-level classification: Brukerhistorie → Scenariotype for every scope unless the tester saved another choice.
        ScenarioGrouping.Default(specificationSelected: true).Should().Be((ScenarioGroupAxis.Story, ScenarioGroupAxis.ScenarioType));
        ScenarioGrouping.Default(specificationSelected: false).Should().Be((ScenarioGroupAxis.Story, ScenarioGroupAxis.ScenarioType));
        ScenarioGrouping.Parse("Module>Story").Should().Be((ScenarioGroupAxis.Module, ScenarioGroupAxis.Story));
        ScenarioGrouping.Parse("Module>Module").Should().Be((ScenarioGroupAxis.Module, ScenarioGroupAxis.None));
        ScenarioGrouping.Parse("garbage").Should().BeNull();
        ScenarioGrouping.Build([Item("a", null, null)], ScenarioGroupAxis.None, ScenarioGroupAxis.Story).Should().BeEmpty();
    }
}
