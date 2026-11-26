using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Adaptive AC readiness (2026-11-26): applicability, evaluation and evidence per criterion of ONE scenario; only required criteria form the
/// denominator; optional never blocks; «Ikke relevant» is never «Oppfylt»; undecided is never «Klar»; cases are optional evidence; decisions are
/// stored append-only in the grouping document with history and never touch the scenario, cases, approvals or results.
/// </summary>
public sealed class ScenarioPreparationTests
{
    private const string Id = "tss-ac1";
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-11-26T10:00:00Z");

    private static ScenarioPreparationDecision D(PreparationCriterion c, CriterionApplicability? a = null, CriterionEvaluation? e = null, string? reason = null, string? fp = "fp1", int minute = 0) =>
        new() { ScenarioId = Id, Criterion = c, Applicability = a, Evaluation = e, Reason = reason, ScenarioFingerprint = fp, By = "tester", At = T.AddMinutes(minute) };

    private static PreparationSummary Eval(params ScenarioPreparationDecision[] d) => ScenarioPreparationEngine.Evaluate(new PreparationFacts { HasExpectedOutcome = true }, d, Id, "fp1");

    /// <summary>Every conditional criterion decided: test data required, trigger optional, environment not applicable.</summary>
    private static ScenarioPreparationDecision[] Decided(params ScenarioPreparationDecision[] more) =>
    [
        D(PreparationCriterion.TestData, CriterionApplicability.Required), D(PreparationCriterion.Trigger, CriterionApplicability.Optional),
        D(PreparationCriterion.Environment, CriterionApplicability.NotApplicable, reason: "Kan gjennomføres med ordinær testtilgang."), .. more,
    ];

    [Fact]
    public void Without_decisions_expected_and_verification_are_required_case_review_optional_and_the_rest_undecided_so_nothing_is_ready()
    {
        var s = Eval();
        s.Criteria.Select(c => (c.Criterion, c.Applicability)).Should().Equal(
            (PreparationCriterion.ExpectedBehaviour, CriterionApplicability.Required), (PreparationCriterion.TestData, CriterionApplicability.Undecided),
            (PreparationCriterion.Trigger, CriterionApplicability.Undecided), (PreparationCriterion.Verification, CriterionApplicability.Required),
            (PreparationCriterion.Environment, CriterionApplicability.Undecided), (PreparationCriterion.CaseReview, CriterionApplicability.Optional));
        (s.Status, s.Required, s.SatisfiedRequired, s.Undecided).Should().Be((PreparationStatus.NeedsAssessment, 2, 0, 3), "unknown is never ready, and nothing is «missing» yet");
        s.Criteria.Where(c => c.Applicability == CriterionApplicability.Undecided).Should().OnlyContain(c => c.Suggested != null && c.ApplicabilityBasis == CriterionBasis.Proposed);
        ScenarioPreparationEngine.CountText(s).Should().Be("0 av 2 påkrevde punkter oppfylt");
    }

    [Fact]
    public void Only_required_criteria_count_optional_never_blocks_and_not_applicable_is_never_satisfied()
    {
        var ready = Eval(Decided(
            D(PreparationCriterion.ExpectedBehaviour, e: CriterionEvaluation.Satisfied), D(PreparationCriterion.Verification, e: CriterionEvaluation.Satisfied),
            D(PreparationCriterion.TestData, e: CriterionEvaluation.Satisfied), D(PreparationCriterion.Trigger, e: CriterionEvaluation.Unsatisfied)));
        (ready.Status, ready.Required, ready.SatisfiedRequired, ready.OptionalOpen).Should().Be((PreparationStatus.Ready, 3, 3, 2), "an unmet optional trigger and the optional case review never block");
        ScenarioPreparationEngine.CountText(ready).Should().Be("Alle påkrevde punkter oppfylt (3)");
        var env = ready.Criteria.Single(c => c.Criterion == PreparationCriterion.Environment);
        (env.Applicability, env.Evaluation).Should().Be((CriterionApplicability.NotApplicable, CriterionEvaluation.NotEvaluated), "«Ikke relevant» is not «Oppfylt»");
        ScenarioPreparationEngine.StateText(env).Should().Be("Ikke relevant");
        env.Reason.Should().Be("Kan gjennomføres med ordinær testtilgang.");
        ScenarioPreparationEngine.StateText(ready.Criteria.Single(c => c.Criterion == PreparationCriterion.Trigger)).Should().Be("Valgfritt · Mangler");

        var missing = Eval(Decided(D(PreparationCriterion.ExpectedBehaviour, e: CriterionEvaluation.Satisfied), D(PreparationCriterion.TestData, e: CriterionEvaluation.Unsatisfied)));
        (missing.Status, missing.MissingRequired, missing.UnevaluatedRequired).Should().Be((PreparationStatus.NeedsPreparation, 1, 1));
        ScenarioPreparationEngine.CountText(missing).Should().Be("1 av 3 påkrevde punkter oppfylt");

        var unevaluated = Eval(Decided(D(PreparationCriterion.ExpectedBehaviour, e: CriterionEvaluation.Satisfied), D(PreparationCriterion.TestData, e: CriterionEvaluation.Satisfied)));
        unevaluated.Status.Should().Be(PreparationStatus.NeedsAssessment, "verification is required and not evaluated");
    }

    [Fact]
    public void No_required_criteria_is_said_plainly_never_0_of_0()
    {
        var s = Eval(D(PreparationCriterion.ExpectedBehaviour, CriterionApplicability.Optional), D(PreparationCriterion.Verification, CriterionApplicability.Optional),
            D(PreparationCriterion.TestData, CriterionApplicability.Optional), D(PreparationCriterion.Trigger, CriterionApplicability.Optional), D(PreparationCriterion.Environment, CriterionApplicability.Optional));
        (s.Status, s.Required).Should().Be((PreparationStatus.Ready, 0));
        ScenarioPreparationEngine.CountText(s).Should().Be("Ingen påkrevde punkter");
    }

    [Fact]
    public void Cases_are_optional_evidence_an_approved_case_never_makes_a_scenario_ready_and_no_case_never_makes_it_unready()
    {
        var noCase = new PreparationFacts { HasExpectedOutcome = true };
        var approved = noCase with { Cases = 2, ApprovedCases = 1, CaseStatesVerification = true, CaseHasTestData = true };
        var withCase = ScenarioPreparationEngine.Evaluate(approved, [], Id, "fp1");
        withCase.Status.Should().Be(PreparationStatus.NeedsAssessment, "an approved case is not a ready scenario");
        withCase.Criteria.Single(c => c.Criterion == PreparationCriterion.ExpectedBehaviour).Should().Match<CriterionState>(c => c.Evaluation == CriterionEvaluation.NotEvaluated && c.EvaluationBasis == CriterionBasis.Case && c.Evidence!.Contains("bekreft"));
        withCase.Criteria.Single(c => c.Criterion == PreparationCriterion.TestData).Evaluation.Should().Be(CriterionEvaluation.NotEvaluated, "linked data is not proof it exists in the environment");
        withCase.Criteria.Single(c => c.Criterion == PreparationCriterion.CaseReview).Should().Match<CriterionState>(c => c.Applicability == CriterionApplicability.Optional && c.Evaluation == CriterionEvaluation.Satisfied);
        ScenarioPreparationEngine.Evaluate(noCase with { Cases = 1 }, [], Id, "fp1").Criteria.Single(c => c.Criterion == PreparationCriterion.CaseReview).Evaluation.Should().Be(CriterionEvaluation.Unsatisfied);

        var decided = Decided(D(PreparationCriterion.ExpectedBehaviour, e: CriterionEvaluation.Satisfied), D(PreparationCriterion.Verification, e: CriterionEvaluation.Satisfied), D(PreparationCriterion.TestData, e: CriterionEvaluation.Satisfied));
        ScenarioPreparationEngine.Evaluate(noCase, decided, Id, "fp1").Status.Should().Be(PreparationStatus.Ready, "a scenario with no case can be ready");
        ScenarioPreparationEngine.Evaluate(approved with { ApprovedCases = 0 }, decided, Id, "fp1").Status.Should().Be(PreparationStatus.Ready, "an unapproved optional case never blocks");
    }

    [Fact]
    public void Each_part_keeps_its_newest_decision_a_changed_scenario_asks_for_a_new_look_and_other_scenarios_are_untouched()
    {
        var history = new[]
        {
            D(PreparationCriterion.TestData, CriterionApplicability.Required, CriterionEvaluation.Unsatisfied, minute: 1),
            D(PreparationCriterion.TestData, e: CriterionEvaluation.Satisfied, minute: 2) with { Evidence = "Barn med manglende personkobling i TT02" },
            D(PreparationCriterion.TestData, CriterionApplicability.NotApplicable, reason: "Ikke scenariospesifikke data", minute: 3) with { ScenarioId = "tss-other" },
        };
        var current = ScenarioPreparationEngine.Current(history, Id)[PreparationCriterion.TestData];
        (current.Applicability, current.Evaluation, current.Evidence).Should().Be((CriterionApplicability.Required, CriterionEvaluation.Satisfied, "Barn med manglende personkobling i TT02"),
            "a later evaluation keeps the earlier applicability");
        ScenarioPreparationEngine.Current(history, "tss-other")[PreparationCriterion.TestData].Applicability.Should().Be(CriterionApplicability.NotApplicable, "decisions are per scenario identity");

        var decided = Decided(D(PreparationCriterion.ExpectedBehaviour, e: CriterionEvaluation.Satisfied), D(PreparationCriterion.Verification, e: CriterionEvaluation.Satisfied), D(PreparationCriterion.TestData, e: CriterionEvaluation.Satisfied));
        var changed = ScenarioPreparationEngine.Evaluate(new PreparationFacts { HasExpectedOutcome = true }, decided, Id, "fp2");
        changed.Status.Should().Be(PreparationStatus.NeedsAssessment, "decisions made on an earlier scenario text are kept but not trusted for «Klar»");
        changed.Stale.Should().BeGreaterThan(0);
        changed.Criteria.Where(c => c.Stale).Should().OnlyContain(c => c.Decision != null, "the earlier decisions are kept and shown, nothing is silently changed");
        changed.Criteria.Single(c => c.Criterion == PreparationCriterion.TestData).Evaluation.Should().Be(CriterionEvaluation.Satisfied, "the decision itself is unchanged");
    }

    [Fact]
    public void Decisions_are_validated_stored_with_history_and_never_touch_types_cases_or_anything_else()
    {
        var doc = new StoryGroupingDocument { ProjectKey = "m2lb", SpecificationPath = "M/specs/001-x/spec.md" };
        var types = new ScenarioTypeDecision("tss-ac1", null, ScenarioRequirementType.Functional, [], null, "lead", T);
        doc = doc with { ScenarioTypes = [types] };
        StoryGroupingOperation Op(PreparationCriterion c, CriterionApplicability? a, CriterionEvaluation? e = null, string? reason = null, string? evidence = null) =>
            new() { Kind = StoryGroupingOperationKind.SetScenarioPreparation, ScenarioId = Id, DisplayId = "US2 · AC 1", Criterion = c, Applicability = a, Evaluation = e, Reason = reason, Evidence = evidence, ScenarioFingerprint = "fp1" };

        FluentActions.Invoking(() => StoryGroupingEngine.Apply(doc, [Op(PreparationCriterion.Environment, CriterionApplicability.NotApplicable)], "tester", T, _ => true))
            .Should().Throw<StoryGroupingEngine.GroupingRefusal>().Which.Message.Should().Contain("hvorfor", "«Ikke relevant» needs a reason");
        FluentActions.Invoking(() => StoryGroupingEngine.Apply(doc, [Op(PreparationCriterion.Environment, CriterionApplicability.Required, evidence: "password: hunter2")], "tester", T, _ => true))
            .Should().Throw<StoryGroupingEngine.GroupingRefusal>("no secret is ever stored");
        FluentActions.Invoking(() => StoryGroupingEngine.Apply(doc, [Op(PreparationCriterion.Environment, CriterionApplicability.NotApplicable, CriterionEvaluation.Satisfied, "x")], "tester", T, _ => true))
            .Should().Throw<StoryGroupingEngine.GroupingRefusal>("not applicable is never satisfied");

        var next = StoryGroupingEngine.Apply(doc, [Op(PreparationCriterion.Environment, CriterionApplicability.NotApplicable, reason: "Ordinær testtilgang"),
            Op(PreparationCriterion.TestData, CriterionApplicability.Optional, CriterionEvaluation.NotEvaluated, evidence: "Representative barn i TT02")], "tester", T, _ => true);
        next.Revision.Should().Be(doc.Revision + 1);
        next.Preparation.Should().HaveCount(2).And.OnlyContain(d => d.By == "tester" && d.At == T && d.ScenarioFingerprint == "fp1");
        next.History.Select(h => h.Detail).Should().Contain(d => d.Contains("US2 · AC 1: Miljø og tilgang") && d.Contains("→ Ikke relevant") && d.Contains("resultater er uendret"));
        next.ScenarioTypes.Should().Equal(types);
        next.Groups.Should().BeEquivalentTo(doc.Groups);
        next.Placements.Should().BeEquivalentTo(doc.Placements);
        next.Decisions.Should().BeEquivalentTo(doc.Decisions);
        next.TestKinds.Should().BeEquivalentTo(doc.TestKinds);
    }

    [Fact]
    public void Facts_come_from_the_scenario_itself_and_a_standalone_test_keys_its_decisions_to_its_own_document()
    {
        var ac = new DiscoveredScenario
        {
            CandidateId = "c1", CapabilityId = "specification", CanonicalKey = "scenario:M|AC1", Title = "AC1", ExpectedBehaviour = "Rapporten avvises", Fingerprint = "fp1",
            Template = [new("given", "Given", "et barn uten personkobling"), new("when", "When", "saksbehandler åpner rapporten"), new("then", "Then", "Rapporten avvises")],
            Story = new CoverageUserStory { Key = "k", StoryId = "US2", Title = "T", SpecificationPath = "M/specs/001-x/spec.md" },
        };
        var f = ScenarioPreparationEngine.FactsOf(ac, []);
        (f.HasExpectedOutcome, f.SourceNamesData, f.NeedsTrigger, f.Cases, f.Standalone).Should().Be((true, true, false, 0, false));
        ScenarioPreparationEngine.DocumentKey(ac).Should().Be("M/specs/001-x/spec.md");
        var e2e = new DiscoveredScenario { CandidateId = "c2", CapabilityId = "test-specification", CanonicalKey = "testspec:HendelseService/docs/Hendelser – Testspesifikasjon.md#TEST-E-01",
            Type = ScenarioCandidateType.BusinessE2E, Template = [new("when", "When", "PUT /innmating/inngrep/42")], ExpectedBehaviour = "Hendelsen er synlig" };
        ScenarioPreparationEngine.DocumentKey(e2e).Should().Be("HendelseService/docs/Hendelser – Testspesifikasjon.md", "no story is invented; the document holds its decisions");
        var ef = ScenarioPreparationEngine.FactsOf(e2e, []);
        (ef.NeedsTrigger, ef.Standalone).Should().Be((true, true));
        ScenarioPreparationEngine.Evaluate(ef, [], e2e.CandidateId, null).Criteria.Count(c => c.Applicability == CriterionApplicability.Required).Should().Be(2,
            "an E2E label proposes, it does not impose: only the two standard criteria are required until a tester decides");
    }
}
