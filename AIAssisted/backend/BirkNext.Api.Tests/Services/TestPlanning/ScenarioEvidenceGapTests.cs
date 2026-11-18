using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using BirkNext.TestPlanning;
using FluentAssertions;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Clarification accuracy (2026-11-18). A test-pack scenario used to show "Needs clarification — not stated in the evidence" for every scenario,
/// because the rule never read the outcome a pack states and put every open point under one label. Now the stated outcome is the expected
/// behaviour, open points are classified (an unclear expectation is not a missing test prerequisite, a missing observation or a missing run),
/// the association stays a reviewable candidate, and the pack's run history is added as runtime evidence — never as a pass unless a run
/// completed. Synthetic, generic pack: nothing product-specific is encoded in the engine.
/// </summary>
public sealed class ScenarioEvidenceGapTests
{
    private sealed class Pack(params JourneyScenarioDescriptor[] scenarios) : IIntegrationJourneyPack
    {
        public string PackId => "orders";
        public string PackVersion => "3";
        public string DisplayName => "Order intake";
        public string Description => "Synthetic order intake pack";
        public IReadOnlyList<ArchitectureResponsibilityRule> Rules => [];
        public IReadOnlyList<string> Limitations => [];
        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => false;
        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>([]);
        public IReadOnlyList<IntegrationJourneyDefinition> Journeys =>
        [
            new()
            {
                PackId = "orders", JourneyId = "intake", DisplayName = "Order intake",
                Steps = [new("submit", "Submit an order", JourneyStepKind.ExternalSubmission, "Shop", "Order accepted (202)"), new("store", "Store the order", JourneyStepKind.Mapping, "Orders", "Order stored")],
                Scenarios = scenarios,
            },
        ];
    }

    private static readonly JourneyScenarioDescriptor TwoBuyers = new("two-buyers", "Two buyers", "Two buyers order the same item; both orders are kept.",
        JourneyScenarioSupport.NotAssessed, "Not assessed: a second approved buyer and a way to read stored orders are not available.",
        ExpectedOutcome: "Both orders are kept.",
        Gaps: [new(EvidenceGapKind.TestPrerequisiteMissing, "A second approved synthetic buyer is needed.", "Test pack", "Prepare a second buyer in Test Data."),
               new(EvidenceGapKind.VerificationMechanismMissing, "No read contract shows stored orders.", "Test pack")]);

    private static DiscoveredScenario One(JourneyScenarioDescriptor scenario) => D.FromPackJourneys("project-a", new Pack(scenario)).Single();

    [Fact]
    public void A_stated_outcome_is_the_expected_behaviour_so_a_missing_prerequisite_is_not_called_an_unclear_expectation()
    {
        var c = One(TwoBuyers);
        c.ExpectedBehaviour.Should().Be("Both orders are kept.");
        c.ExpectedBasis.Should().Be("Stated by the test pack Order intake 3");
        c.NeedsClarification.Should().BeFalse("the expectation is stated; what is missing is a prerequisite and an observation");
        c.Gaps.Select(g => g.Kind).Should().Equal(EvidenceGapKind.TestPrerequisiteMissing, EvidenceGapKind.VerificationMechanismMissing, EvidenceGapKind.AssociationNeedsReview);
        c.Gaps.Should().NotContain(g => g.Kind == EvidenceGapKind.BusinessExpectationUnclear);
        c.Gaps[0].NextAction.Should().Be("Prepare a second buyer in Test Data.");
    }

    [Fact]
    public void Open_questions_are_classified_and_never_repeat_the_not_assessed_prefix()
    {
        var c = One(TwoBuyers);
        c.OpenQuestions.Should().Equal("Test prerequisite missing: A second approved synthetic buyer is needed.", "Verification mechanism missing: No read contract shows stored orders.");
        var unclassified = One(new("legacy", "Legacy", "Something happens.", JourneyScenarioSupport.NotAssessed, "Not assessed: no fixture."));
        unclassified.OpenQuestions.Should().ContainSingle(q => q.Contains("Not assessed in the pack: no fixture."));
        unclassified.OpenQuestions.Should().NotContain(q => q.Contains("Not assessed in the pack: Not assessed"));
    }

    [Fact]
    public void Without_a_stated_outcome_the_business_expectation_stays_unclear_and_is_not_invented()
    {
        var c = One(new("valid", "Valid order", "A valid order through the shop.", JourneyScenarioSupport.Supported));
        c.ExpectedBehaviour.Should().BeNull("the step evidence is how it is run, not the business outcome");
        c.NeedsClarification.Should().BeTrue();
        c.Gaps.Should().Contain(g => g.Kind == EvidenceGapKind.BusinessExpectationUnclear);
    }

    [Fact]
    public void A_free_text_reason_without_classification_stays_one_unclassified_point()
    {
        var c = One(new("legacy", "Legacy", "Something happens.", JourneyScenarioSupport.NotAssessed, "Not assessed: no fixture."));
        c.Gaps.Should().Contain(g => g.Kind == EvidenceGapKind.EvidenceNotEvaluated && g.Question == "Not assessed in the pack: no fixture.");
        c.Gaps.Should().NotContain(g => g.Kind == EvidenceGapKind.TestPrerequisiteMissing, "nothing is guessed from free text");
    }

    [Fact]
    public void Contradictory_evidence_is_kept_with_its_basis_for_a_person_to_decide()
    {
        var c = One(new("correction", "Correction", "A correction replaces the earlier order.", JourneyScenarioSupport.NotAssessed, "Not assessed.",
            ExpectedOutcome: "A correction replaces the earlier order.",
            Gaps: [new(EvidenceGapKind.ContradictoryEvidence, "A date change creates a separate order instead.", "Source review (not runtime verified)", "Ask the product owner.")]));
        c.ExpectedBehaviour.Should().Be("A correction replaces the earlier order.", "the stated expectation is kept; the conflict is shown, not resolved");
        c.Gaps.Should().ContainSingle(g => g.Kind == EvidenceGapKind.ContradictoryEvidence).Which.Basis.Should().Be("Source review (not runtime verified)");
    }

    [Fact]
    public void The_pack_association_is_always_a_reviewable_candidate()
    {
        var c = One(TwoBuyers);
        c.Confidence.Should().Be(CandidateConfidence.CandidateAssociation);
        c.Gaps.Should().ContainSingle(g => g.Kind == EvidenceGapKind.AssociationNeedsReview);
        c.SourcesChecked.Should().ContainSingle(s => s.Source == "Test pack Order intake 3" && s.Found);
    }

    private static IntegrationJourneyRun Run(string scenario, JourneyRunState state, int day, string reason = "") => new()
    {
        RunId = Guid.NewGuid(), PackId = "orders", JourneyId = "intake", ScenarioId = scenario, EnvironmentId = "env-1", OverallState = state, StateReason = reason,
        StartedAt = new DateTimeOffset(2026, 11, day, 9, 0, 0, TimeSpan.Zero),
    };

    private static DiscoveredScenario WithRuns(JourneyScenarioDescriptor scenario, string? environment, IReadOnlyList<IntegrationJourneyRun>? runs) =>
        D.WithRunEvidence(D.FromPackJourneys("project-a", new Pack(scenario)), new Pack(scenario), environment, "DEV", runs).Single();

    private static readonly JourneyScenarioDescriptor Valid = new("valid", "Valid order", "A valid order.", JourneyScenarioSupport.Supported, ExpectedOutcome: "The order is accepted.");

    [Fact]
    public void Never_run_is_runtime_evidence_missing_with_the_run_as_next_action()
    {
        var c = WithRuns(Valid, "env-1", []);
        c.Gaps.Should().ContainSingle(g => g.Kind == EvidenceGapKind.RuntimeEvidenceMissing).Which.NextAction.Should().Be("Run it from Integration Testing.");
        c.SourcesChecked.Should().Contain(s => s.Source == "Integration journey runs" && !s.Found && s.Detail == "No run in DEV");
    }

    [Fact]
    public void Only_a_completed_run_counts_as_runtime_evidence_and_the_latest_run_decides()
    {
        var completed = WithRuns(Valid, "env-1", [Run("valid", JourneyRunState.Failed, 1, "timeout"), Run("valid", JourneyRunState.Completed, 2)]);
        completed.Gaps.Should().NotContain(g => g.Kind == EvidenceGapKind.RuntimeEvidenceMissing);
        completed.Evidence.Should().Contain(e => e.Kind == "journey-run" && e.Detail!.StartsWith("Completed in DEV"));

        var notVerified = WithRuns(Valid, "env-1", [Run("valid", JourneyRunState.Completed, 1), Run("valid", JourneyRunState.NotVerified, 3, "no read contract")]);
        notVerified.Gaps.Should().ContainSingle(g => g.Kind == EvidenceGapKind.RuntimeEvidenceMissing)
            .Which.Question.Should().Contain("ended NotVerified: no read contract", "a run that could not verify the result is not a pass");
    }

    [Fact]
    public void A_scenario_the_pack_cannot_run_yet_is_not_told_to_run_but_the_history_check_is_recorded()
    {
        var c = WithRuns(TwoBuyers, "env-1", []);
        c.Gaps.Should().NotContain(g => g.Kind == EvidenceGapKind.RuntimeEvidenceMissing, "its prerequisite and observation gaps come first");
        c.SourcesChecked.Should().Contain(s => s.Source == "Integration journey runs" && s.Detail == "No run in DEV");
    }

    [Fact]
    public void Runs_of_another_scenario_or_pack_are_not_used()
    {
        var c = WithRuns(Valid, "env-1", [Run("other", JourneyRunState.Completed, 2), Run("valid", JourneyRunState.Completed, 2) with { PackId = "elsewhere" }]);
        c.Gaps.Should().Contain(g => g.Kind == EvidenceGapKind.RuntimeEvidenceMissing);
    }

    [Fact]
    public void Without_a_Target_Environment_the_run_history_is_reported_as_not_examined_not_as_missing()
    {
        var c = WithRuns(Valid, null, null);
        c.Gaps.Should().Contain(g => g.Kind == EvidenceGapKind.EvidenceNotEvaluated && g.Question.Contains("no Target Environment is active"));
        c.SourcesChecked.Should().Contain(s => s.Source == "Integration journey runs" && !s.Found && s.Detail.StartsWith("Not examined"));
    }

    /// <summary>The reported case (Plan & prepare → Rapportmottak · E2E-09): the pack states the outcome; what is missing is a second approved
    /// reporter (Test Data) and a downstream observation — not the expectation. Pinned against the real pack.</summary>
    [Fact]
    public void The_real_E2E_09_is_a_prerequisite_and_verification_gap_not_an_unclear_expectation()
    {
        var flows = new BirkNext.Api.Tests.Services.IntegrationJourneys.IntegrationJourneyHarness.FakeMessageFlows();
        var kit = new BirkNext.Api.Tests.Services.AltinnTesting.AltinnTestKit();
        var pack = new BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer.SkoleNaervaerJourneyPack(flows, kit.Policy, kit.Contracts(flows));
        var all = D.FromPackJourneys("project-a", pack);
        var e2e09 = all.Single(c => c.Title.EndsWith("E2E-09 · Several reporters"));
        e2e09.ExpectedBehaviour.Should().Be("Both reports are kept.");
        e2e09.NeedsClarification.Should().BeFalse();
        e2e09.Gaps.Select(g => g.Kind).Should().Equal(EvidenceGapKind.TestPrerequisiteMissing, EvidenceGapKind.VerificationMechanismMissing, EvidenceGapKind.AssociationNeedsReview);
        all.Single(c => c.Title.EndsWith("E2E-07 · Correction")).Gaps.Should().Contain(g => g.Kind == EvidenceGapKind.ContradictoryEvidence);
        all.Single(c => c.Title.EndsWith("Duplicate report")).NeedsClarification.Should().BeTrue("the pack states no outcome for an exact repeat");
        all.SelectMany(c => c.OpenQuestions).Should().NotContain(q => q.Contains("Not assessed in the pack: Not assessed"));
    }
}
