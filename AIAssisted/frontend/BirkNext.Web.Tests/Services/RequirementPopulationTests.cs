using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using P = BirkNext.Web.Services.TestPlanSuggestionsPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// R1: one authoritative requirement population — the selected specification's requirements. Relationship-analysis coverage is counted over that
/// population (normalised ids), what the analysis does not reach is reported, and analysed requirements outside the selection are never counted.
/// </summary>
public sealed class RequirementPopulationTests
{
    private static RequirementCoverageView Analysis()
    {
        static RequirementCoverageRow Row(string id, RequirementCoverageState s) => new() { RequirementKey = $"Requirement|m/f/{id}", RequirementId = id, Title = id, State = s };
        return new RequirementCoverageView
        {
            Scope = "dev", Known = true, Requirements = 5, Reviewed = 1, NeedsReview = 1,
            Rows = [Row("FR-021", RequirementCoverageState.ReviewedScenarioCoverage), Row("FR-006", RequirementCoverageState.NeedsRelationshipReview),
                Row("FR-017", RequirementCoverageState.AssociationVerifiedScenarioNotReviewed), Row("FR-040", RequirementCoverageState.NoDocumentedScenarioCoverage),
                Row("FR-099", RequirementCoverageState.ReviewedScenarioCoverage)],
        };
    }

    private static readonly IReadOnlyList<string> Specification = RequirementPopulation.Of(["FR-21", "FR-006", "FR-050"]);

    [Fact]
    public void Relationship_coverage_is_counted_over_the_selected_specification_only()
    {
        var r = RequirementPopulation.Relationship(Analysis(), Specification);

        r.Should().Be(new RequirementPopulation.RelationshipCoverage(Population: 3, Reviewed: 1, NeedsReview: 1, NotAnalysed: 1, OutsideSelection: 3),
            "FR-21 is FR-021 (shared grammar); FR-050 is not in the analysis; FR-017/040/099 are analysed but not in the selected specification");
    }

    [Fact]
    public void The_focus_card_uses_the_specification_population_and_never_counts_requirements_outside_it()
    {
        var focus = P.Focus(new TestPlanWorkspace { GuideKey = "g" }, Analysis(), Specification);

        var reviewed = focus.Facts.Single(f => f.Id == "req-reviewed");
        reviewed.Label.Should().Contain("of 3 in the selected specification");
        reviewed.Count.Should().Be(1, "FR-099 is reviewed in the analysis but is not a requirement of the selected specification");
        focus.Facts.Single(f => f.Id == "req-not-analysed").Count.Should().Be(1);
        focus.Facts.Single(f => f.Id == "req-needs").Count.Should().Be(1);
        focus.Actions.Single(a => a.Id == "req-associations").Why.Should().StartWith("1 requirement(s)");
    }

    [Fact]
    public void Without_a_selected_specification_the_numbers_say_they_are_the_analysiss_own()
    {
        var focus = P.Focus(new TestPlanWorkspace { GuideKey = "g" }, Analysis(), population: null);

        focus.Facts.Single(f => f.Id == "req-reviewed").Label.Should().Contain("of 5 in the last relationship analysis");
        focus.Facts.Should().NotContain(f => f.Id == "req-not-analysed");
    }
}
