using System.Runtime.CompilerServices;
using BirkNext.Web.Models;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.Specifications;

/// <summary>
/// Spec.md structural compliance (2026-11-25) on the real, unchanged SkoleService specification (M2LB (8)): the canonical parser keeps the
/// document's own structure — 8 stories, 65 scenarios under the right story, FR sections, edge cases, dated clarifications with the source's
/// supersession markers, success criteria, dependencies, key entities, assumptions and out-of-scope items — and never invents a link. Compound
/// scenarios stay one source record with test-design variants. Numbers follow the task's test list (S01–S26, R27–R37, D51–D64).
/// </summary>
public sealed class SpecStructureComplianceTests
{
    private static string Fixture(string relative, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "TestPlanning", "Fixtures", "m2lb-specs", relative));

    private static readonly Lazy<string> Text = new(() => Fixture("SkoleService/specs/001-skoleservice/spec.md"));
    private static readonly Lazy<SpecificationSemanticModel> Model = new(() => SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(Text.Value), Text.Value));
    private static SpecificationSemanticModel M => Model.Value;
    private static SemanticAcceptanceScenario Scenario(int story, int n) => M.AcceptanceScenarios.Single(s => s.ParentStoryKey == $"US{story}" && s.NumberInStory == n);

    // ── Source structure ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void S01_S05_eight_user_stories_with_priority_description_rationale_and_independent_test()
    {
        M.UserStories.Where(u => u.IsStoryHeading).Select(u => u.AuthoredId).Should().Equal("US1", "US2", "US3", "US4", "US5", "US6", "US7", "US8");
        M.UserStories.Select(u => u.Priority).Should().Equal("P1", "P1", "P1", "P2", "P2", "P2", "P2", "P3");
        M.UserStories.Should().OnlyContain(u => !string.IsNullOrWhiteSpace(u.Description) && !string.IsNullOrWhiteSpace(u.Why) && !string.IsNullOrWhiteSpace(u.IndependentTest));
        M.UserStories[3].Title.Should().Be("User Story 4 - Hold reports for children not yet known (Priority: P2)");
        M.UserStories[1].Description.Should().Contain("Gradering is enforced where the data is read");
    }

    [Fact]
    public void S06_S07_S11_sixty_five_scenarios_each_once_under_its_own_story_with_a_source_location()
    {
        M.AcceptanceScenarios.Should().HaveCount(65);
        M.UserStories.Select(u => M.AcceptanceScenarios.Count(s => s.ParentStoryKey == u.StableKey)).Should().Equal(11, 8, 6, 5, 9, 10, 12, 4);
        M.AcceptanceScenarios.Select(s => (s.ParentStoryKey, s.NumberInStory)).Should().OnlyHaveUniqueItems("US1 scenario 1 and US2 scenario 1 are different source scenarios");
        M.AcceptanceScenarios.Should().OnlyContain(s => s.LineStart > 0);
        Scenario(4, 1).Given.Should().Contain("identifier Person cannot resolve");
    }

    [Fact]
    public void S08_S09_S10_compound_scenarios_keep_all_their_source_text_in_one_record()
    {
        var us2s5 = Scenario(2, 5);
        $"{us2s5.Given} {us2s5.When} {us2s5.Then}".Should().Contain("counting option is off").And.Contain("it is on");
        M.AcceptanceScenarios.Count(s => s.ParentStoryKey == "US2").Should().Be(8, "the second Given/Then of scenario 5 is not a fake ninth scenario");
        var us7s12 = Scenario(7, 12);
        $"{us7s12.Given} {us7s12.Then}".Should().Contain("without a from date").And.Contain("to date is before its from date");
    }

    [Fact]
    public void S12_S13_S14_S15_requirement_sections_are_the_source_areas_with_exact_ids()
    {
        M.Requirements.Should().HaveCount(65);
        M.Requirements.Select(r => r.Category).Distinct().Should().Equal("Intake and identity", "Classification", "Validity control (reject with machine-readable reason)",
            "Quality control (accept and flag)", "Corrections, chains and idempotency", "Period lock", "Expected population and coverage", "Delivery to Bufdata",
            "Education master data (Utdanning)", "Follow-up lists", "Access, audit and retention");
        M.Requirements.Select(r => r.Category).Should().NotContain(["Functional Requirements", "Key Entities", "General"]);
        M.Requirements.Select(r => r.Id).Should().Contain(["FR-015", "FR-015A", "FR-015B", "FR-027", "FR-027A"]).And.OnlyHaveUniqueItems();
        BirkNext.Sdd.RequirementSections.Read(Text.Value).DisplayId("FR-015A").Should().Be("FR-015a", "the source spelling is kept for display");
    }

    [Fact]
    public void S16_R32_prefixed_references_of_other_documents_are_not_this_specifications_requirements()
    {
        // US7 cites "UT-FR-001..005" (Utdanning's requirements); FR text cites "[SR-FR-001]" (the reference document).
        M.UserStories.Single(u => u.AuthoredId == "US7").LinkedRequirements.Should().BeEmpty();
        M.Requirements.Single(r => r.Id == "FR-001").LinkedUserStories.Should().BeEmpty();
    }

    [Fact]
    public void S17_edge_cases_are_extracted_as_supporting_evidence_not_scenarios()
    {
        M.EdgeCases.Should().HaveCount(27);
        M.EdgeCases[0].Title.Should().StartWith("Report arrives during the settling delay before lock");
        M.EdgeCases.Single(e => e.Title.StartsWith("Intake outage around the deadline", StringComparison.Ordinal)).RelatedRequirementIds.Should().Equal("FR-023A", "FR-026A");
        M.AcceptanceScenarios.Should().HaveCount(65, "edge cases never become acceptance scenarios");
    }

    [Fact]
    public void S18_S19_dated_clarifications_keep_the_sources_own_supersession_markers()
    {
        M.Clarifications.Should().HaveCount(48);
        M.Clarifications.Select(c => c.Session).Distinct().Should().Equal("2026-10-02", "2026-10-05", "2026-10-06", "2026-10-07", "2026-10-08", "2026-10-09");
        M.Clarifications.Count(c => c.Status == SemanticClarification.Superseded).Should().Be(3);
        M.Clarifications.Count(c => c.Status == SemanticClarification.Amended).Should().Be(4);
        var copy = M.Clarifications.Single(c => c.Question.StartsWith("How does the service know a child's current gradering", StringComparison.Ordinal));
        (copy.Status, copy.Session).Should().Be((SemanticClarification.Superseded, "2026-10-06"));
        copy.StatusNote.Should().StartWith("Superseded 2026-10-09").And.Contain("DEC-122");
        M.Clarifications.Where(c => c.Status == SemanticClarification.Current).Should().HaveCount(41, "no clarification is marked historical from its position alone");
    }

    [Fact]
    public void S20_success_criteria_are_kept_with_their_identity_and_are_not_requirements()
    {
        M.SuccessCriteria.Select(c => c.Id).Should().Equal(Enumerable.Range(1, 11).Select(i => $"SC-{i:000}"));
        M.Requirements.Should().NotContain(r => r.Id.StartsWith("SC-", StringComparison.Ordinal));
        M.SuccessCriteria.Single(c => c.Id == "SC-010").Text.Should().Contain("load test");
    }

    [Fact]
    public void S21_dependencies_and_open_items_keep_the_documents_own_status()
    {
        M.Dependencies.Should().HaveCount(13);
        M.Dependencies.Single(d => d.Id == "DEP-04 / OQ-09").Closed.Should().BeTrue();
        M.Dependencies.Single(d => d.Id == "EXC-01..05").Withdrawn.Should().BeTrue("the row is struck through in the source");
        M.Dependencies.Single(d => d.Id == "DEP-09").Blocks.Should().Contain("User Story 2");
        M.Dependencies.Single(d => d.Id == "OQ-03").RequirementIds.Should().Equal("FR-009");
        M.Dependencies.Should().OnlyContain(d => d.Line > 0);
    }

    [Fact]
    public void S22_S23_S24_key_entities_assumptions_and_out_of_scope_are_context_not_requirements()
    {
        M.KeyEntities.Select(e => e.Name).Should().Equal("Skolenærværsrapport", "Rapportversjon", "Fraværslinje", "Rapportør", "Holdt identifikator", "Rapporteringsperiode",
            "Dekningsgrunnlag", "Avvisning", "Utdanningsforhold", "ManglendeSkoletilbud", "Ukoblet utdanningspost", "Periodeleveranse");
        M.Assumptions.Should().HaveCount(11);
        M.OutOfScope.Should().HaveCount(7);
        M.OutOfScope[1].Text.Should().Contain("MeldingsutvekslerReceiver's feature", "continuation lines belong to their item");
    }

    // ── Relationships ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void R27_R28_R32_R33_links_come_only_from_explicit_references_never_from_shared_words()
    {
        // No story text names a SkoleService FR; before 2026-11-25 two shared title words linked US1 to 58 of 65 requirements.
        M.UserStories.Should().OnlyContain(u => u.LinkedRequirements.Count == 0);
        // Scenarios that name a requirement in their own Given/When/Then are linked to exactly it.
        M.AcceptanceScenarios.Where(s => s.LinkedRequirements.Count > 0).Select(s => $"{s.ParentStoryKey}-{s.NumberInStory}:{string.Join(",", s.LinkedRequirements.Select(r => r.Id))}")
            .Should().BeEquivalentTo(["US3-4:FR-009", "US6-10:FR-041", "US7-4:FR-039", "US8-3:FR-009"]);
    }

    [Fact]
    public void R35_one_requirement_can_be_named_by_scenarios_of_several_stories()
    {
        M.Requirements.Single(r => r.Id == "FR-009").LinkedAcceptanceScenarios.Select(s => s.ParentStoryKey).Should().BeEquivalentTo(["US3", "US8"]);
    }

    [Fact]
    public void R_whole_identifier_matching_never_confuses_suffixed_requirements()
    {
        var spec = "# F\n\n## Requirements\n\n### Functional Requirements\n\n**A**\n\n- **FR-008**: base.\n- **FR-008a**: suffixed.\n\n## User Scenarios & Testing\n\n### User Story 1 - X (Priority: P1)\n\nSee FR-008a.\n\n**Acceptance Scenarios**:\n\n1. **Given** a, **When** b, **Then** c (FR-0081 is not a requirement here).\n";
        var m = SpecExplorerService.BuildSemanticModel(SpecExplorerService.Parse(spec), spec);
        m.UserStories.Single().LinkedRequirements.Select(r => r.Id).Should().Equal("FR-008A");
        m.AcceptanceScenarios.Single().LinkedRequirements.Should().BeEmpty();
    }

    // ── Test design from compound scenarios (one source scenario, several variants) ─────────────────────────────

    private static TestCaseDraft Draft(SemanticAcceptanceScenario s) => new()
    {
        Generated = new TestCaseContent { Title = $"{s.ParentStoryKey} scenario {s.NumberInStory}", Preconditions = s.Given, Steps = s.When is null ? [] : [s.When], ExpectedResult = s.Then },
    };

    private static List<TestCaseVariantProposal> Variants(int story, int n) => ScenarioPlanning.Variants(Draft(Scenario(story, n)), []);

    [Fact]
    public void D53_US2_scenario_5_proposes_the_counting_option_on_variant_from_the_source()
    {
        var alt = Variants(2, 5).Where(v => v.Dimension == "Source alternative").ToList();
        alt.Should().ContainSingle().Which.Should().Match<TestCaseVariantProposal>(v => v.Preconditions == "It is on" && v.Expected!.Contains("full coverage breakdown"));
    }

    [Fact]
    public void D54_D55_D56_further_given_then_pairs_become_variants_with_their_own_outcomes()
    {
        Variants(5, 7).Where(v => v.Dimension == "Source alternative").Select(v => v.Expected).Should().ContainSingle(e => e!.Contains("rejected as unexpected"));
        Variants(7, 5).Where(v => v.Dimension == "Source alternative").Select(v => v.Preconditions).Should().ContainSingle(p => p!.Contains("update for a record identity the system does not hold"));
        var us7s12 = Variants(7, 12).Where(v => v.Dimension == "Source alternative").ToList();
        us7s12.Should().HaveCount(3);
        us7s12.Select(v => v.Expected).Should().Contain(e => e!.Contains("reason code published in the ingestion contract"))
            .And.Contain(e => e!.Contains("not a permanent rejection")).And.Contain(e => e!.Contains("counts for no period"));
    }

    [Fact]
    public void D51_D52_where_clauses_and_listed_conditions_are_distinguishable_variants()
    {
        Variants(1, 5).Should().Contain(v => v.Title.Contains("where planned equals actual", StringComparison.OrdinalIgnoreCase) || (v.Expected ?? "").Contains("no flag"));
        var listed = Variants(1, 8).Where(v => v.Dimension == "Listed condition").Select(v => v.Preconditions).ToList();
        listed.Should().HaveCount(3);
        listed.Should().Contain(p => p!.Contains("span two periods", StringComparison.OrdinalIgnoreCase)).And.Contain(p => p!.Contains("start after the submission date", StringComparison.OrdinalIgnoreCase)).And.Contain(p => p!.Contains("locked period", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void D57_D58_variants_never_add_source_scenarios_and_are_not_offered_twice()
    {
        var draft = Draft(Scenario(2, 5));
        var once = ScenarioPlanning.Variants(draft, []);
        once.Select(v => v.Key).Should().OnlyHaveUniqueItems();
        var taken = once.First(v => v.Dimension == "Source alternative");
        ScenarioPlanning.Variants(draft, [new TestCaseDraft { FromVariant = taken.Key, Generated = new TestCaseContent { Title = "edited" } }])
            .Should().NotContain(v => v.Key == taken.Key, "a variant already used for a saved case is not proposed again");
        M.AcceptanceScenarios.Should().HaveCount(65);
    }

    [Fact]
    public void D_plain_scenarios_propose_no_invented_alternatives()
    {
        Variants(4, 1).Select(v => v.Dimension).Should().NotContain(["Source alternative", "Listed condition"]);
    }
}
