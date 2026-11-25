using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Manual or technical (2026-11-25): every scenario starts from an automatic suggestion; a reviewer may choose «Manuell» or «Teknisk» per scenario.
/// The choice is a grouping decision of the scenario's specification (history kept, never a change to the scenario, its cases or its source), the
/// suggestion stays visible beside it, and «Manuell | Teknisk | Alle» decides which scenarios the list shows.
/// </summary>
public sealed class ScenarioTestKindTests
{
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private static readonly CoverageUserStory Us1 = new() { Key = CoverageUserStory.KeyOf(Spec, "us1"), StoryId = "US1", Ordinal = 1, Title = "Story 1", SpecificationPath = Spec };
    private static readonly CoverageUserStory Other = new() { Key = CoverageUserStory.KeyOf("Other/spec.md", "us1"), StoryId = "US1", Ordinal = 1, Title = "Other", SpecificationPath = "Other/spec.md" };

    private static DiscoveredScenario C(string id, CoverageUserStory? story, int n) => new()
    {
        CandidateId = id, ProjectKey = "m2lb", CapabilityId = ScenarioOwnership.Specification, Title = id, Purpose = id, Type = ScenarioCandidateType.BusinessFunctional,
        Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "it is accepted", Story = story, NumberInStory = n,
        Evidence = [new("specification", $"{story?.SpecificationPath ?? "x"}#{id}", "Akseptansescenario")],
    };

    /// <summary>US1-AS1 suggested manual, US1-AS2 suggested technical, and a same-id scenario of another specification.</summary>
    private static List<S.Item> Items()
    {
        var items = S.Items([C("US1-AS1", Us1, 1), C("US1-AS2", Us1, 2), C("US1-AS9", Other, 9)], new Dictionary<string, S.Prepared>());
        return [.. items.Select(i => i.Id == "US1-AS2" ? i with { Relevance = S.Relevance.Technical } : i with { Relevance = S.Relevance.Manual })];
    }

    private static StoryGroupingDocument Decide(StoryGroupingDocument doc, string id, ScenarioTestKind? kind, string by = "anna") =>
        StoryGroupingEngine.Apply(doc, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetScenarioTestKind, ScenarioId = id, StoryKey = Us1.Key, TestKind = kind, DisplayId = id }],
            by, DateTimeOffset.UtcNow, _ => true);

    private static readonly StoryGroupingDocument Empty = new() { ProjectKey = "m2lb", SpecificationPath = Spec };

    [Fact]
    public void A_reviewers_choice_is_a_new_revision_with_history_and_never_touches_other_decisions()
    {
        var withPlacement = StoryGroupingEngine.Apply(Empty, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetPrimaryArea, StoryKey = Us1.Key, PrimaryArea = "Intake" }],
            "anna", DateTimeOffset.UtcNow, _ => true);
        var doc = Decide(withPlacement, "US1-AS1", ScenarioTestKind.Technical);

        doc.Revision.Should().Be(withPlacement.Revision + 1);
        StoryGroupingEngine.TestKindDecision(doc, "US1-AS1").Should().Match<ScenarioTestKindDecision>(d => d.Kind == ScenarioTestKind.Technical && d.By == "anna" && d.StoryKey == Us1.Key);
        doc.History.Last().Action.Should().Be("Endret testtype");
        doc.History.Last().Detail.Should().Contain("US1-AS1: Teknisk").And.Contain("uendret");
        doc.Placements.Should().BeEquivalentTo(withPlacement.Placements, "a test kind never changes a story's placement");
    }

    [Fact]
    public void Returning_to_the_suggestion_keeps_the_history_and_a_missing_scenario_is_refused()
    {
        var doc = Decide(Decide(Empty, "US1-AS1", ScenarioTestKind.Technical), "US1-AS1", null);
        StoryGroupingEngine.TestKindDecision(doc, "US1-AS1").Should().BeNull("null = back to the automatic suggestion");
        doc.TestKinds.Should().HaveCount(2, "both decisions stay in the record");
        doc.History.Last().Detail.Should().Contain("automatisk forslag");

        var act = () => Decide(Empty, " ", ScenarioTestKind.Manual);
        act.Should().Throw<StoryGroupingEngine.GroupingRefusal>().Which.Code.Should().Be("missing-scenario");
    }

    [Fact]
    public void Decisions_apply_to_the_specifications_scenarios_only_and_keep_the_suggestion_beside_them()
    {
        var doc = Decide(Decide(Empty, "US1-AS2", ScenarioTestKind.Manual), "US1-AS9", ScenarioTestKind.Technical);
        var items = S.WithTestKinds(Items(), StoryGroupingEngine.TestKindDecisions(doc), Spec);

        var chosen = items.Single(i => i.Id == "US1-AS2");
        chosen.Relevance.Should().Be(S.Relevance.Manual);
        chosen.KindDecided.Should().BeTrue();
        chosen.Suggested.Should().Be(S.Relevance.Technical, "the automatic suggestion stays visible beside the choice");
        items.Single(i => i.Id == "US1-AS1").KindDecided.Should().BeFalse();
        items.Single(i => i.Id == "US1-AS9").Should().Match<S.Item>(i => !i.KindDecided && i.Relevance == S.Relevance.Manual, "another specification's scenario is never matched by id alone");

        S.WithTestKinds(items, StoryGroupingEngine.TestKindDecisions(doc), Spec).Select(i => (i.Id, i.Relevance, i.Suggested)).Should().Equal(items.Select(i => (i.Id, i.Relevance, i.Suggested)), "applying twice changes nothing");
        S.WithTestKinds(items, StoryGroupingEngine.TestKindDecisions(Decide(doc, "US1-AS2", null)), Spec).Single(i => i.Id == "US1-AS2").Should()
            .Match<S.Item>(i => !i.KindDecided && i.Relevance == S.Relevance.Technical, "back to the suggestion restores it");
    }

    [Fact]
    public void Manuell_Teknisk_and_Alle_show_the_matching_scenarios_and_say_what_is_hidden()
    {
        var items = Items();
        var manual = S.Apply(items, S.WithView(new S.Filter(), S.KindView.Manual));
        manual.Shown.Select(i => i.Id).Should().BeEquivalentTo(["US1-AS1", "US1-AS9"]);
        manual.HiddenTechnical.Should().Be(1);

        S.Apply(items, S.WithView(new S.Filter(), S.KindView.Technical)).Shown.Select(i => i.Id).Should().Equal("US1-AS2");
        S.Apply(items, S.WithView(new S.Filter(), S.KindView.All)).Shown.Should().HaveCount(3);

        foreach (var v in Enum.GetValues<S.KindView>())
        {
            S.ViewOf(S.WithView(new S.Filter(Query: "x"), v)).Should().Be(v);
            S.ViewOf(S.ResetFilters(S.WithView(new S.Filter(Query: "x"), v))).Should().Be(v, "«Nullstill filtre» keeps the chosen view");
        }
    }

    [Fact]
    public void The_suggestion_is_explained_in_the_testers_words()
    {
        var items = Items();
        S.SuggestionReason(items.Single(i => i.Id == "US1-AS1")).Should().Be("Atferd en tester kan observere.");
        S.SuggestionReason(items.Single(i => i.Id == "US1-AS2")).Should().Be("Teknisk kontrakt eller ikke-funksjonell kontroll.");
        S.KindLabel(S.Relevance.Technical).Should().Be("Teknisk");
        StoryGroupingEngine.TestKindLabel(null).Should().Be("automatisk forslag");
    }
}
