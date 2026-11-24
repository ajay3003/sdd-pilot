using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using P = BirkNext.Web.Services.PlanPreparePresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// «Tilknyttede krav» (2026-11-24): FRs as supporting context of one case, built on the existing link rows, suggester and prepared-case trace.
/// Real SkoleService wording (AIAssisted/testdata/M2LB.zip). Explicit, confirmed and suggested stay apart; coverage levels are separate; a
/// missing or changed requirement is flagged; another service's FR-001 is never borrowed; saving a link change keeps everything a person saved.
/// </summary>
public sealed class LinkedRequirementsTests
{
    private const string Skole = "SkoleService/specs/001-skoleservice/spec.md";
    private const string Frontend = "Frontend/specs/002-child-search-profile/spec.md";
    private static readonly ProjectRequirementOption Fr015 = new("FR-015", "When an absence breakdown is present and its sum differs from planned minus actual hours by more than 0.5 hours, the report MUST be accepted and flagged \"inconsistent hour totals\". This check MUST NOT be applied when FR-015a applies. [SR-FR-017]", Skole);
    private static readonly ProjectRequirementOption Fr015a = new("FR-015a", "When actual hours exceed planned hours by any amount, the report MUST be accepted and flagged \"attendance above plan\". [SR-FR-017a, DEC-50]", Skole);
    private static readonly ProjectRequirementOption Fr016 = new("FR-016", "A report without an absence breakdown MUST be accepted; it MUST be flagged \"missing absence breakdown\" when planned hours exceed actual hours, and not flagged when they are equal. [SR-FR-019, DEC-28]", Skole);
    private static readonly ProjectRequirementOption Fr034 = new("FR-034", "Code, comments and identifiers MUST be in English; the telemetry names MUST follow the shared naming convention.", Skole);
    private static readonly ProjectRequirementOption FrontendFr001 = new("FR-001", "Users MUST be able to search for children by entering a full or partial name (minimum 2 characters) in the search field", Frontend);
    private static readonly List<ProjectRequirementOption> Options = [Fr015, Fr015a, Fr016, Fr034, FrontendFr001];
    private static readonly CoverageUserStory Us1 = new() { Key = "us1", StoryId = "US1", Title = "Accept a valid report", SpecificationPath = Skole };

    private static TestCaseDraft Case(string id, string then, List<string>? requirementIds = null, TestCaseReviewState review = TestCaseReviewState.Generated,
        List<ManualRequirementLink>? links = null, string? scenarioKey = null) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "m2lb", ScenarioId = id },
        SuiteId = "s", Review = review, ScenarioKey = scenarioKey,
        Generated = new TestCaseContent
        {
            Title = id, Purpose = "", Steps = ["Send the report in as a private institution or a foster home."], StepExpected = [then], ExpectedResult = then,
            Capability = ScenarioOwnership.Specification, Story = Us1,
            Requirements = [.. (requirementIds ?? []).Select(r => new TestCaseRequirement(r, r, null))],
            Evidence = [new CandidateEvidenceRef("specification", $"{Skole}#US1-2")],
        },
        RequirementLinks = links ?? [],
    };

    private static TestPlanDraft Plan(params TestCaseDraft[] cases) => new() { Id = Guid.NewGuid(), ProjectKey = "m2lb", PlanName = "p", Revision = 2, Cases = [.. cases] };

    private const string AboveTheplan = "the report is accepted and flagged \"attendance above plan\", and the absence-sum check is not applied to it";

    [Fact]
    public void Explicit_confirmed_and_suggested_are_three_different_relations()
    {
        var c = Case("c1", AboveTheplan, ["FR-016"], links: [new ManualRequirementLink { RequirementId = "FR-014x", SpecificationPath = Skole, Title = "gone", LinkedBy = "anna", LinkedAt = DateTimeOffset.Parse("2026-11-20T10:00:00Z") }]);
        var rows = P.LinkedRequirements(c, Plan(c), Options);

        rows.Single(r => r.RequirementId == "FR-016").Relation.Should().Be(RequirementRelation.Explicit);
        rows.Single(r => r.RequirementId == "FR-014x").Should().Match<P.LinkedRequirement>(r => r.Relation == RequirementRelation.Confirmed && r.RelationText.Contains("anna") && r.Missing,
            "a confirmed link to a requirement no longer in the specification is kept and flagged, never counted");
        var suggested = rows.Single(r => r.RequirementId == "FR-015a");
        suggested.Relation.Should().Be(RequirementRelation.Suggested);
        suggested.RelationText.Should().Contain("C · Samme regel");
        suggested.Behaviour.Should().Contain("attendance above plan");
        rows.Should().NotContain(r => r.RequirementId == "FR-015", "a different outcome is a different rule");
    }

    [Fact]
    public void A_suggestion_is_not_coverage_and_a_confirmed_case_link_is_while_a_scenario_wide_link_is_not()
    {
        var suggestedOnly = Case("c1", AboveTheplan);
        P.LinkedRequirements(suggestedOnly, Plan(suggestedOnly), Options).Single(r => r.RequirementId == "FR-015a").Coverage
            .Should().Be(new P.RequirementCoverageLevels(true, 0, 0, 0, null, null));

        var confirmed = Case("c2", AboveTheplan, review: TestCaseReviewState.Accepted,
            links: [new ManualRequirementLink { RequirementId = "FR-015a", SpecificationPath = Skole, Title = Fr015a.Title, LinkedBy = "anna", LinkedAt = DateTimeOffset.UtcNow }]);
        var scenarioWide = Case("c3", AboveTheplan,
            links: [new ManualRequirementLink { RequirementId = "FR-015a", SpecificationPath = Skole, Title = Fr015a.Title, Scope = RequirementLinkScope.Scenario, LinkedBy = "anna", LinkedAt = DateTimeOffset.UtcNow }]);
        var plan = Plan(confirmed, scenarioWide);
        var levels = P.LinkedRequirements(confirmed, plan, Options).Single(r => r.RequirementId == "FR-015a").Coverage;
        levels.ApprovedCases.Should().Be(1);
        levels.DraftCases.Should().Be(0, "the scenario-wide link is an association, not this case's test design");
        P.LinkedRequirements(scenarioWide, plan, Options).Single(r => r.RequirementId == "FR-015a").Relation.Should().Be(RequirementRelation.ConfirmedForScenario);
    }

    [Fact]
    public void Coverage_levels_stay_separate_and_execution_comes_only_from_completed_runs()
    {
        var c = Case("c1", "x", ["FR-016"], review: TestCaseReviewState.Accepted);
        var candidates = new List<DiscoveredScenario>
        {
            new() { CandidateId = "s5", RequirementIds = ["FR-016"], Story = Us1 },
            new() { CandidateId = "other", RequirementIds = ["FR-016"], Story = new CoverageUserStory { Key = "x", SpecificationPath = "Person/specs/001/spec.md" } },
        };
        var trace = new PreparedCaseTraceView { Requirements = [new PreparedRequirementTrace { RequirementId = "FR-016", SpecificationPath = Skole, ApprovedCases = 1, ExecutedCases = 1, PassedCases = 0, FailedCases = 1 }] };

        var levels = P.LinkedRequirements(c, Plan(c), Options, trace, candidates).Single(r => r.RequirementId == "FR-016").Coverage;

        levels.Should().Be(new P.RequirementCoverageLevels(true, 1, 0, 1, 1, 0), "the other service's scenario does not count for SkoleService's FR-016");
        P.CoverageChips(levels).Select(x => x.Text).Should().Equal("I spesifikasjonen", "1 akseptansescenario", "Ingen utkast i planen", "1 godkjent i planen", "Utført 1 (bestått 0)");
        P.CoverageChips(levels with { ExecutedCases = null, PassedCases = null }).Last().Text.Should().Be("Utførelse ikke kjent her", "unknown is never shown as not executed");
    }

    [Fact]
    public void Another_services_FR_001_is_never_borrowed_for_this_case()
    {
        var c = Case("c1", "x", ["FR-001"]);
        var row = P.LinkedRequirements(c, Plan(c), Options).Single(r => r.RequirementId == "FR-001");

        row.Missing.Should().BeTrue("SkoleService's FR-001 is not in the requirement list; Frontend's FR-001 is another requirement");
        row.Title.Should().NotContain("search for children");
        row.SpecificationPath.Should().Be(Skole);
    }

    [Fact]
    public void A_requirement_whose_behaviour_changed_since_the_link_is_flagged_with_what_changed()
    {
        var older = Fr016 with { Title = Fr016.Title.Replace("missing absence breakdown", "no absence breakdown") };
        var c = Case("c1", "x", links: [new ManualRequirementLink { RequirementId = "FR-016", SpecificationPath = Skole, Title = older.Title, LinkedBy = "anna", LinkedAt = DateTimeOffset.UtcNow }]);

        var row = P.LinkedRequirements(c, Plan(c), Options).Single(r => r.RequirementId == "FR-016");
        row.Change!.Kind.Should().Be(RequirementChangeKind.Behavioural);
        row.Change.Differences.Should().Contain("Utfall lagt til: «missing absence breakdown»");

        var explicitOnly = Case("c2", "x", ["FR-016"]);
        P.LinkedRequirements(explicitOnly, Plan(explicitOnly), Options).Single(r => r.RequirementId == "FR-016").Change
            .Should().BeNull("a generated link holds only the id, so there is no earlier wording to compare");
    }

    [Fact]
    public void Technical_suggestions_are_left_out_but_a_technical_requirement_linked_to_the_case_stays_visible()
    {
        var c = Case("c1", "Code, comments and identifiers are in English and the telemetry names follow the shared naming convention.");
        P.LinkedRequirements(c, Plan(c), Options).Should().NotContain(r => r.RequirementId == "FR-034");

        var linked = Case("c2", "x", ["FR-034"]);
        P.LinkedRequirements(linked, Plan(linked), Options).Single(r => r.RequirementId == "FR-034").Technical.Should().BeTrue();
    }

    [Fact]
    public void Saving_a_confirmed_link_keeps_the_testers_edits_approval_translation_and_other_cases()
    {
        var c = Case("c1", AboveTheplan, review: TestCaseReviewState.Accepted) with { Edits = new TestCaseEdits { Title = "Edited by Anna" } };
        c = c.WithTranslation(ScenarioPlanning.Norwegian, new TestCaseTranslation { Title = "Norsk tittel", Steps = ["Send inn"], StepExpected = [null], Origin = TranslationOrigin.Manual });
        var other = Case("c2", "y") with { Edits = new TestCaseEdits { ExpectedResult = "Other edit" } };
        var plan = Plan(c, other);
        var suggestion = ScenarioCentricPresentation.Suggestions(c, null, Options).Single(s => s.RequirementId == "FR-015a");

        var request = P.RequirementChangeRequest(plan, "c1", x => ScenarioCentricPresentation.Confirm(x, suggestion, null), Options);

        var sent = request.Cases.Single(x => x.ScenarioId == "c1");
        sent.RequirementLinks.Should().ContainSingle(l => l.RequirementId == "FR-015a" && l.SuggestionLevel == RequirementSuggestionLevel.Semantic && l.Scope == null);
        sent.Edits.Title.Should().Be("Edited by Anna");
        sent.Translations![ScenarioPlanning.Norwegian].Title.Should().Be("Norsk tittel");
        request.Cases.Single(x => x.ScenarioId == "c2").Edits.ExpectedResult.Should().Be("Other edit");
        request.BaseRevision.Should().Be(2);
        request.RequirementCatalog!.Requirements.Should().Contain(Fr015a, "the server checks a new link against the project's requirements");
    }

    [Fact]
    public void Rejecting_removes_the_suggestion_for_this_case_only()
    {
        var c = Case("c1", AboveTheplan);
        var other = Case("c2", AboveTheplan);
        var suggestion = ScenarioCentricPresentation.Suggestions(c, null, Options).Single(s => s.RequirementId == "FR-015a");
        var rejected = ScenarioCentricPresentation.Reject(c, suggestion);

        P.LinkedRequirements(rejected, Plan(rejected, other), Options).Should().NotContain(r => r.RequirementId == "FR-015a");
        P.LinkedRequirements(other, Plan(rejected, other), Options).Should().Contain(r => r.RequirementId == "FR-015a");
    }
}
