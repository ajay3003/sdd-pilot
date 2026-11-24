using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// FR traceability on the existing suggester (2026-11-24): real wording from AIAssisted/testdata/M2LB.zip (SkoleService/specs/001-skoleservice,
/// Frontend/specs/002-child-search-profile). Rule level, not word overlap: the same outcome is the same rule, a different outcome is a different
/// rule (FR-015 vs FR-015a); ids keep their suffix; an id means something only inside its own specification; requirement changes are wording or
/// behaviour, with each difference named.
/// </summary>
public sealed class FunctionalRequirementTraceabilityTests
{
    private const string Skole = "SkoleService/specs/001-skoleservice/spec.md";
    private const string Frontend = "Frontend/specs/002-child-search-profile/spec.md";

    private static readonly ProjectRequirementOption Fr014 = new("FR-014", "Duplicate absence codes in one report MUST be merged by summing their hours, without rejection or flag. [DEC-27]", Skole);
    private static readonly ProjectRequirementOption Fr015 = new("FR-015", "When an absence breakdown is present and its sum differs from planned minus actual hours by more than 0.5 hours, the report MUST be accepted and flagged \"inconsistent hour totals\". This check MUST NOT be applied when FR-015a applies. [SR-FR-017]", Skole);
    private static readonly ProjectRequirementOption Fr015a = new("FR-015a", "When actual hours exceed planned hours by any amount, the report MUST be accepted and flagged \"attendance above plan\". [SR-FR-017a, DEC-50]", Skole);
    private static readonly ProjectRequirementOption Fr015b = new("FR-015b", "Accepted absence codes MUST be configurable per schema version (default for v2.0.1: `1`, `2`, `3`, `4`, `5`, `9`, compared as strings without zero-padding). A code outside the configured list MUST be accepted, delivered as received, and the report flagged \"unknown absence code\". [SR-FR-017b, DEC-51, MP-08]", Skole);
    private static readonly ProjectRequirementOption Fr016 = new("FR-016", "A report without an absence breakdown MUST be accepted; it MUST be flagged \"missing absence breakdown\" when planned hours exceed actual hours, and not flagged when they are equal. [SR-FR-019, DEC-28]", Skole);
    private static readonly ProjectRequirementOption SkoleFr001 = new("FR-001", "The system MUST accept from MeldingsutvekslerReceiver one period summary per child and reporter, containing the child's identifier and its type (fødselsnummer or DUF-nummer), reporter type and identity (fødselsnummer or orgnr), period dates, planned and actual hours, absence lines, message id, send time and message-chain information. [SR-FR-001]", Skole);
    private static readonly ProjectRequirementOption FrontendFr001 = new("FR-001", "Users MUST be able to search for children by entering a full or partial name (minimum 2 characters) in the search field", Frontend);

    private static readonly ProjectRequirementOption[] SkoleRequirements = [SkoleFr001, Fr014, Fr015, Fr015a, Fr015b, Fr016];

    // US1 acceptance scenarios 2, 4 and 5, verbatim.
    private const string Scenario2 = "Given a report where `AntallTimerFaktiskNaervaer` is greater than `AntallTimerEtterPlanen`, When it arrives, Then it is accepted and flagged \"attendance above plan\", and the absence-sum check is not applied to it.";
    private const string Scenario4 = "Given a report whose absence hours sum differs from planned minus actual hours by more than 0.5 hours, When it arrives, Then it is accepted and flagged \"inconsistent hour totals\".";
    private const string Scenario5 = "Given a report with no absence breakdown where planned hours exceed actual hours, When it arrives, Then it is accepted and flagged \"missing absence breakdown\"; where planned equals actual, it is accepted with no flag.";

    private static List<RequirementSuggestion> Suggest(string text, IEnumerable<ProjectRequirementOption>? options = null, string? scope = Skole, IEnumerable<string>? explicitIds = null) =>
        ScenarioPlanning.SuggestRequirements(text, explicitIds ?? [], [], [.. options ?? SkoleRequirements], scopePath: scope);

    // 1. Explicit references, suffix kept, embedded ids ignored.

    [Fact]
    public void An_explicit_reference_keeps_its_suffix_and_an_id_inside_another_id_is_not_that_id()
    {
        ScenarioPlanning.RequirementIdsIn("Covers FR-015a and FR-016.").Should().Equal("FR-015a", "FR-016");
        ScenarioPlanning.RequirementIdsIn("[SR-FR-017a, DEC-50]").Should().NotContain(id => id.StartsWith("FR-", StringComparison.Ordinal),
            "SR-FR-017a is the source requirement, not FR-017a");

        var s = Suggest("The report is checked against FR-015a.");
        s.Where(x => x.Level == RequirementSuggestionLevel.Explicit).Select(x => x.RequirementId).Should().Equal("FR-015a");
        s.Should().NotContain(x => x.RequirementId == "FR-015" && x.Level == RequirementSuggestionLevel.Explicit, "FR-015 and FR-015a are distinct");
    }

    // 2 + 6. Suggested, rule level: FR-015 versus FR-015a.

    [Fact]
    public void Attendance_above_plan_suggests_FR_015a_and_never_FR_015_although_both_talk_about_absence_sums()
    {
        var s = Suggest(Scenario2);

        s.Should().ContainSingle(x => x.RequirementId == "FR-015a").Which.Should().Match<RequirementSuggestion>(x =>
            x.Level == RequirementSuggestionLevel.Semantic && x.Reason.Contains("attendance above plan"));
        s.Should().NotContain(x => x.RequirementId == "FR-015", "a different outcome (\"inconsistent hour totals\") is a different rule");
        s.Should().NotContain(x => x.RequirementId == "FR-016");
    }

    [Fact]
    public void The_absence_sum_scenario_suggests_FR_015_and_not_FR_015a()
    {
        var s = Suggest(Scenario4);

        s.Should().Contain(x => x.RequirementId == "FR-015" && x.Level == RequirementSuggestionLevel.Semantic);
        s.Should().NotContain(x => x.RequirementId == "FR-015a");
        Suggest(Scenario5).Select(x => x.RequirementId).Should().Contain("FR-016").And.NotContain("FR-015").And.NotContain("FR-015a");
    }

    // 7. Numbers and negations are read as rule facts.

    [Fact]
    public void Thresholds_outcomes_and_negations_are_rule_facts_and_references_are_not()
    {
        var f = ScenarioPlanning.RuleFactsOf(Fr015.Title);
        f.Numbers.Should().Equal("0.5");
        f.Outcomes.Should().Equal("inconsistent hour totals");
        f.Negated.Should().BeTrue("\"MUST NOT be applied when FR-015a applies\"");
        f.Modals.Should().Contain("MUST").And.Contain("MUST NOT");

        ScenarioPlanning.RuleFactsOf(Fr015a.Title).Numbers.Should().BeEmpty("\"[SR-FR-017a, DEC-50]\" and \"FR-015a\" are references, not thresholds");
    }

    [Fact]
    public void A_scenario_stating_another_threshold_is_not_suggested_as_the_same_behaviour()
    {
        var r = new ProjectRequirementOption("FR-900", "An hour value greater than 24 times the number of days in its date range MUST be rejected with a machine-readable reason.", Skole);
        var other = Suggest("A report with an hour value greater than 30 times the number of days in its date range is rejected with a machine-readable reason.", [r]);
        other.Should().NotContain(x => x.Level == RequirementSuggestionLevel.Semantic, "30 is not the requirement's 24");
        Suggest("A report with an hour value greater than 24 times the number of days in its date range is rejected with a machine-readable reason.", [r])
            .Should().Contain(x => x.RequirementId == "FR-900");
    }

    // 4 + 5. Many-to-many.

    [Fact]
    public void One_scenario_can_name_several_requirements_and_one_requirement_serves_several_scenarios()
    {
        Suggest("Covers FR-015b and FR-016.").Where(x => x.Level == RequirementSuggestionLevel.Explicit).Select(x => x.RequirementId).Should().BeEquivalentTo(["FR-015b", "FR-016"]);

        var scenario3 = "Given a report with an absence code outside the accepted codes configured for its schema version, When it arrives, Then it is accepted, the code is delivered as received, and the report is flagged \"unknown absence code\".";
        var scenario3b = "Given a zero-padded code such as `01`, When it arrives, Then the report is flagged \"unknown absence code\".";
        Suggest(scenario3).Should().Contain(x => x.RequirementId == "FR-015b");
        Suggest(scenario3b).Should().Contain(x => x.RequirementId == "FR-015b");
    }

    // 8. Cross-service id isolation.

    [Fact]
    public void FR_001_of_another_service_is_never_an_explicit_match()
    {
        var s = Suggest("This scenario verifies FR-001.", [SkoleFr001, FrontendFr001], scope: Skole);

        s.Where(x => x.Level == RequirementSuggestionLevel.Explicit).Should().ContainSingle().Which.SpecificationPath.Should().Be(Skole);
        Suggest("This scenario verifies FR-001.", [SkoleFr001, FrontendFr001], scope: Frontend)
            .Where(x => x.Level == RequirementSuggestionLevel.Explicit).Should().ContainSingle().Which.SpecificationPath.Should().Be(Frontend);
        Suggest("This scenario verifies FR-001.", [SkoleFr001, FrontendFr001], scope: null)
            .Where(x => x.Level == RequirementSuggestionLevel.Explicit).Should().HaveCount(2, "without a known source both are candidates, each with its file");
    }

    // 9. Missing / deleted requirements are not suggested.

    [Fact]
    public void A_requirement_that_is_not_in_the_specification_is_never_suggested_and_a_rejected_one_is_not_proposed_again()
    {
        Suggest("Covers FR-099.").Should().BeEmpty("FR-099 does not exist: ids are never invented");
        ScenarioPlanning.SuggestRequirements(Scenario2, [], [], SkoleRequirements, [ScenarioPlanning.Key("FR-015a", Skole)], scopePath: Skole)
            .Should().NotContain(x => x.RequirementId == "FR-015a");
    }

    // 10. Requirement change impact: wording versus behaviour.

    [Fact]
    public void A_changed_threshold_or_outcome_is_behavioural_and_reflowed_or_reworded_text_is_not()
    {
        ScenarioPlanning.ClassifyRequirementChange(Fr015.Title, Fr015.Title.Replace(" ", "  ").Replace("hours,", "hours,\n"))
            .Kind.Should().Be(RequirementChangeKind.Unchanged, "line breaks and spacing are not a change");

        var threshold = ScenarioPlanning.ClassifyRequirementChange(Fr015.Title, Fr015.Title.Replace("0.5 hours", "1 hour"));
        threshold.Kind.Should().Be(RequirementChangeKind.Behavioural);
        threshold.Differences.Should().Contain("Tall fjernet: «0.5»").And.Contain("Tall lagt til: «1»");

        ScenarioPlanning.ClassifyRequirementChange(Fr015a.Title, Fr015a.Title.Replace("attendance above plan", "attendance exceeds plan"))
            .Differences.Should().Contain("Utfall lagt til: «attendance exceeds plan»");
        ScenarioPlanning.ClassifyRequirementChange(Fr016.Title, Fr016.Title.Replace("MUST be accepted", "SHOULD be accepted"))
            .Differences.Should().Contain(d => d.StartsWith("Kravstyrke lagt til", StringComparison.Ordinal));

        var wording = ScenarioPlanning.ClassifyRequirementChange(Fr014.Title, Fr014.Title.Replace("Duplicate absence codes in one report", "Absence codes that repeat within a report"));
        wording.Kind.Should().Be(RequirementChangeKind.WordingOnly);
        wording.Differences.Should().BeEmpty();
    }

    [Fact]
    public void Adding_or_removing_a_negation_is_a_behavioural_change()
    {
        var c = ScenarioPlanning.ClassifyRequirementChange("The report MUST be logged.", "The report MUST be logged without the national identifier.");
        c.Kind.Should().Be(RequirementChangeKind.Behavioural);
        c.Differences.Should().Contain("Nekting lagt til");
    }
}
