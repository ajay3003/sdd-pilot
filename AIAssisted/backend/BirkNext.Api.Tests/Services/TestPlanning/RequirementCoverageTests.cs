using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using BirkNext.SystemUnderstanding;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// MT-7 independent acceptance (synthetic fixtures A–R): scenario → specification requirement associations in the shared canonical registry and the
/// reviewed-coverage rule. Expected outcomes are stated from the rule ("reviewed by tester" only with a verified association to a scenario whose
/// current revision a tester accepted, on unchanged evidence), not read back from the implementation.
/// </summary>
public sealed class RequirementCoverageTests
{
    const string Scope = "env-skole";
    const string Guide = "skolenaervaer";
    static readonly SnapshotReference Src = new(SnapshotType.SourceAnalysis, "11111111-1111-1111-1111-111111111111", "M2LB (6).zip");

    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now = Now.AddSeconds(1);
    }

    /// <summary>Test Plan Suggestions as the coverage rule sees it: reviews per scenario and the guide's current fingerprints.</summary>
    sealed class Reviews : IScenarioReviewSource
    {
        public Dictionary<(string, string), ScenarioReviewFact> Items { get; } = [];
        public Dictionary<string, string> Current { get; } = new(StringComparer.Ordinal);
        public Task<IReadOnlyDictionary<(string, string), ScenarioReviewFact>> ScenarioReviewsAsync(IReadOnlyCollection<string> guideKeys, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<(string, string), ScenarioReviewFact>>(Items);
        public Task<Dictionary<string, string>> ScenarioFingerprintsAsync(string guideKey, CancellationToken ct) => Task.FromResult(new Dictionary<string, string>(Current));
        public void Accept(string scenario, string fingerprint, bool current = true) =>
            Items[(Guide, scenario)] = new(Guide, scenario, Guid.NewGuid(), TestCaseReviewState.Accepted, fingerprint, current, "tester@bufdir", new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    }

    readonly string _name = $"mt7-{Guid.NewGuid():N}";
    readonly Clock _clock = new();
    readonly Reviews _reviews = new();
    CanonicalInterpretationDbContext Db() => new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase(_name).Options);

    static WorkItemStructureInput Pack(string module, string feature, params (string Key, string Text)[] requirements) =>
        new(module, feature, feature, [.. requirements.Select(r => new WorkItemRequirementInput(r.Key, $"- **{r.Key}**: {r.Text}"))], [], [], "delivery:d1");

    static GuideScenarioInput Scenario(string id, string title, string fingerprint = "fp-1", bool published = true, string[]? tests = null, params GuideRequirementReference[] refs) =>
        new(Guide, 2, published, id, title, fingerprint, ["r-" + id], tests ?? [], refs);

    static GuideRequirementReference Ref(string id, string? spec = "Skole/specs/001-skoletjenesten/spec.md") => new(id, spec);

    static readonly WorkItemStructureInput Skole = Pack("Skole", "001-skoletjenesten",
        ("FR-006", "A held report is deleted when the report is withdrawn."), ("FR-017", "The message id is the idempotency key."),
        ("FR-021", "The newest submission time wins."), ("FR-040", "Duplicate reports are flagged."));

    async Task<InterpretationSnapshotView> Interpret(IReadOnlyList<GuideScenarioInput> scenarios, params WorkItemStructureInput[] packs)
    {
        var result = CorrelationRules.Correlate(CorrelationInput.Empty(Scope) with
        {
            Structures = packs.Length == 0 ? [Skole] : packs, GuideScenarios = scenarios,
            Tests = [new("Skole.Tests", "tests/HeldReportTests.cs", "HeldReportTests.Withdrawal_deletes_held_report", "HeldReportTests", ["FR-006"], [], Src.Id),
                new("Skole.Tests", "tests/SuccessCriteriaTests.cs", "SuccessCriteriaTests.Coverage_adds_up", "SuccessCriteriaTests", ["SC-003"], [], Src.Id)],
        });
        await using var db = Db();
        return await new CanonicalInterpretationService(db, new NoSources(), _clock).PersistAsync(Scope, result, [Src], result.Limitations, "test", default);
    }

    async Task<RequirementCoverageView> Coverage()
    {
        await using var db = Db();
        return await new RequirementCoverageService(db, _reviews).CoverageAsync(Scope, default);
    }

    async Task<ReviewDecisionView> Decide(string relationKey, ReviewDecisionOutcome outcome, string? notes = null)
    {
        await using var db = Db();
        return await new CanonicalInterpretationService(db, new NoSources(), _clock).DecideAsync(Scope, new(ReviewTargetKind.Relation, relationKey, outcome, "reviewer@bufdir", notes), null, default);
    }

    static RequirementCoverageRow Row(RequirementCoverageView v, string id) => v.Rows.Single(r => r.RequirementId == id);

    [Fact]
    public async Task Without_an_interpretation_or_without_guide_scenarios_coverage_is_unknown_never_zero()
    {
        var none = await Coverage();
        none.Known.Should().BeFalse();
        none.UnknownReason.Should().Contain("No relationship interpretation");

        await Interpret([]);
        var noGuide = await Coverage();
        noGuide.Known.Should().BeFalse("requirements alone give no scenario-coverage denominator");
        noGuide.UnknownReason.Should().Contain("no System Understanding scenarios");
        noGuide.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_explicit_reference_in_a_published_guide_is_verified_and_counts_once_the_tester_accepted_the_current_scenario()
    {
        _reviews.Current["duplikat-melding"] = "fp-1";
        await Interpret([Scenario("duplikat-melding", "Duplikat melding", refs: Ref("FR-017"))]);

        var before = await Coverage();
        Row(before, "FR-017").State.Should().Be(RequirementCoverageState.AssociationVerifiedScenarioNotReviewed, "the association is explicit but no tester accepted the scenario");
        Row(before, "FR-017").Associations.Single().Trust.Should().Be(AssociationTrust.ExplicitSourceReference);

        _reviews.Accept("duplikat-melding", "fp-1");
        var after = await Coverage();
        Row(after, "FR-017").State.Should().Be(RequirementCoverageState.ReviewedScenarioCoverage);
        after.Reviewed.Should().Be(1);
        after.Requirements.Should().Be(4, "the denominator is every requirement of the latest interpretation");
        after.Boundary.Should().Contain("not test execution");

        await using var db = Db();
        var summary = await new RequirementCoverageService(db, _reviews).CoverageAsync(Scope, default, rows: false);
        summary.Rows.Should().BeEmpty("the summary form carries no row per requirement");
        (summary.Reviewed, summary.Requirements, summary.Associations.Count).Should().Be((after.Reviewed, after.Requirements, after.Associations.Count));
    }

    [Fact]
    public async Task B_C_a_draft_guide_reference_is_only_suggested_until_confirmed_in_Relationship_Review()
    {
        _reviews.Current["korrigert-rapport"] = "fp-1";
        _reviews.Accept("korrigert-rapport", "fp-1");
        await Interpret([Scenario("korrigert-rapport", "Korrigert rapport", published: false, refs: Ref("FR-021"))]);

        var suggested = await Coverage();
        Row(suggested, "FR-021").State.Should().Be(RequirementCoverageState.NeedsRelationshipReview, "a tester accepting the Test Case never approves the association");
        suggested.Reviewed.Should().Be(0);
        var relation = Row(suggested, "FR-021").Associations.Single();
        relation.Trust.Should().Be(AssociationTrust.SuggestedAssociation);

        await Decide(relation.RelationKey, ReviewDecisionOutcome.Confirmed);
        var confirmed = await Coverage();
        var a = Row(confirmed, "FR-021").Associations.Single();
        a.Trust.Should().Be(AssociationTrust.ReviewedCanonicalAssociation);
        a.Reviewer.Should().Be("reviewer@bufdir");
        Row(confirmed, "FR-021").State.Should().Be(RequirementCoverageState.ReviewedScenarioCoverage);
    }

    [Fact]
    public async Task D_G_H_similar_titles_never_associate_scenarios_without_requirements_and_requirements_without_scenarios_are_listed()
    {
        // The scenario title repeats the requirement text word for word, but the guide names no requirement and no test: no association.
        await Interpret([Scenario("idempotens", "The message id is the idempotency key")]);
        var v = await Coverage();
        v.Associations.Should().BeEmpty("text similarity is never evidence");
        Row(v, "FR-017").State.Should().Be(RequirementCoverageState.NoDocumentedScenarioCoverage);
        v.NoScenario.Should().Be(4);
        v.ScenariosWithoutRequirement.Should().Equal($"{Guide}/idempotens");
    }

    [Fact]
    public async Task E_F_one_scenario_can_cover_several_requirements_and_several_scenarios_one_requirement()
    {
        await Interpret([
            Scenario("manglende-barn", "Barnet finnes ikke", refs: [Ref("FR-006"), Ref("FR-040")]),
            Scenario("trekk", "Trukket rapport", refs: Ref("FR-006")),
        ]);
        var v = await Coverage();
        v.Associations.Where(a => a.ScenarioId == "manglende-barn").Select(a => a.RequirementId).Should().BeEquivalentTo(["FR-006", "FR-040"]);
        Row(v, "FR-006").Associations.Select(a => a.ScenarioId).Should().BeEquivalentTo(["manglende-barn", "trekk"]);
    }

    [Fact]
    public async Task Via_test_a_named_test_that_explicitly_verifies_a_requirement_is_a_suggestion_only()
    {
        await Interpret([Scenario("manglende-barn", "Barnet finnes ikke", tests: ["HeldReportTests, HeldAtLockTests", "SuccessCriteriaTests"])]);
        var v = await Coverage();
        // SC-003 is known only because a test names it (no specification feature pack): no verified identity, never proposed, never counted.
        v.Associations.Should().NotContain(x => x.RequirementId == "SC-003");
        v.Requirements.Should().Be(4);
        var a = v.Associations.Single();
        a.RequirementId.Should().Be("FR-006");
        a.RuleId.Should().Be(CorrelationRules.GuideScenarioViaTestRule);
        a.Trust.Should().Be(AssociationTrust.SuggestedAssociation);
    }

    [Fact]
    public void Via_test_an_informal_test_reference_is_a_low_confidence_suggestion_and_an_ambiguous_one_is_never_followed()
    {
        var mm = Pack("MM", "001-meldingsutvekslermottak", ("FR-006", "Receipts are sent within 5 minutes."));
        var result = CorrelationRules.Correlate(CorrelationInput.Empty(Scope) with
        {
            Structures = [Skole, mm],
            Tests = [
                // The id written in the test source (CR-M-CANDIDATE): followed, but weak.
                new("Skole.Tests", "SkoleService/tests/ReportIntakeIdempotencyTests.cs", "M2LB.Skole.ReportIntakeIdempotencyTests.Same_message_twice", "M2LB.Skole.ReportIntakeIdempotencyTests", [], ["FR-017"], Src.Id),
                // FR-006 is defined by two feature packs and the test says nothing about which: ambiguous, never followed.
                new("Other.Tests", "Other/tests/HeldReportTests.cs", "M2LB.Other.HeldReportTests.Held", "M2LB.Other.HeldReportTests", ["FR-006"], [], Src.Id),
            ],
            GuideScenarios = [Scenario("duplikat-melding", "Duplikat melding", tests: ["ReportIntakeIdempotencyTests"]), Scenario("manglende-barn", "Barnet finnes ikke", tests: ["HeldReportTests"])],
        });
        var fromGuide = result.Relations.Where(r => r.RuleId == CorrelationRules.GuideScenarioViaTestRule).ToList();
        var weak = fromGuide.Single();
        weak.TargetKey.Should().Contain("FR-017");
        weak.Resolution.Should().Be(RelationResolution.Candidate);
        weak.Confidence.Should().Be(CorrelationConfidence.Low);
        weak.Explanation.Should().Contain("informal reference");
        fromGuide.Should().NotContain(r => r.SourceKey.Contains("manglende-barn"), "an ambiguous test link is never followed");
    }

    [Fact]
    public async Task I_J_Q_a_rejected_association_never_counts_a_later_decision_supersedes_and_history_is_kept()
    {
        _reviews.Current["trekk"] = "fp-1";
        _reviews.Accept("trekk", "fp-1");
        await Interpret([Scenario("trekk", "Trukket rapport", published: false, refs: Ref("FR-006"))]);
        var key = (await Coverage()).Associations.Single().RelationKey;

        await Decide(key, ReviewDecisionOutcome.Confirmed);
        await Decide(key, ReviewDecisionOutcome.Rejected, "Withdrawal is FR-006 only for held reports; this scenario is not about that.");
        var v = await Coverage();
        Row(v, "FR-006").State.Should().Be(RequirementCoverageState.NoDocumentedScenarioCoverage);
        Row(v, "FR-006").Associations.Single().Trust.Should().Be(AssociationTrust.Rejected);
        v.Reviewed.Should().Be(0);

        await using var db = Db();
        var history = await new CanonicalInterpretationService(db, new NoSources(), _clock).DecisionsAsync(Scope, key, 10, default);
        history.Should().HaveCount(2, "the earlier confirmation is kept as history");
        history.Single(d => d.Outcome == ReviewDecisionOutcome.Confirmed).SupersededById.Should().Be(history.Single(d => d.Outcome == ReviewDecisionOutcome.Rejected).Id);
    }

    [Fact]
    public async Task K_a_changed_scenario_revision_needs_revalidation_and_an_earlier_tester_review_no_longer_counts()
    {
        _reviews.Current["korrigert-rapport"] = "fp-1";
        _reviews.Accept("korrigert-rapport", "fp-1");
        await Interpret([Scenario("korrigert-rapport", "Korrigert rapport", published: false, refs: Ref("FR-021"))]);
        await Decide((await Coverage()).Associations.Single().RelationKey, ReviewDecisionOutcome.Confirmed);
        (await Coverage()).Reviewed.Should().Be(1);

        // The guide scenario changes; until re-interpreted the association is flagged outdated, after re-interpretation the decision needs revalidation.
        _reviews.Current["korrigert-rapport"] = "fp-2";
        var outdated = await Coverage();
        outdated.Associations.Single().InterpretationOutdated.Should().BeTrue();
        Row(outdated, "FR-021").State.Should().Be(RequirementCoverageState.NeedsRelationshipReview);

        _reviews.Accept("korrigert-rapport", "fp-1", current: false);
        await Interpret([Scenario("korrigert-rapport", "Korrigert rapport", fingerprint: "fp-2", published: false, refs: Ref("FR-021"))]);
        var revalidate = await Coverage();
        var a = revalidate.Associations.Single();
        a.ReviewState.Should().Be(nameof(RelationReviewState.RevalidationRequired));
        a.ScenarioReview.Should().Be("Accepted for an earlier scenario revision");
        Row(revalidate, "FR-021").State.Should().Be(RequirementCoverageState.NeedsRelationshipReview);
    }

    [Fact]
    public async Task L_a_changed_requirement_text_needs_revalidation()
    {
        _reviews.Current["duplikat-melding"] = "fp-1";
        await Interpret([Scenario("duplikat-melding", "Duplikat melding", published: false, refs: Ref("FR-017"))]);
        await Decide((await Coverage()).Associations.Single().RelationKey, ReviewDecisionOutcome.Confirmed);

        var changed = Pack("Skole", "001-skoletjenesten", ("FR-006", "A held report is deleted when the report is withdrawn."), ("FR-017", "The message id AND the reporter form the idempotency key."),
            ("FR-021", "The newest submission time wins."), ("FR-040", "Duplicate reports are flagged."));
        await Interpret([Scenario("duplikat-melding", "Duplikat melding", published: false, refs: Ref("FR-017"))], changed);
        var v = await Coverage();
        v.Associations.Single().ReviewState.Should().Be(nameof(RelationReviewState.RevalidationRequired));
        Row(v, "FR-017").State.Should().Be(RequirementCoverageState.NeedsRelationshipReview);
    }

    [Fact]
    public async Task M_a_baseline_without_the_requirement_carries_no_association_and_the_old_decision_stays_as_history()
    {
        _reviews.Current["bufdata-fil"] = "fp-1";
        await Interpret([Scenario("bufdata-fil", "Bufdata-fil", published: false, refs: Ref("FR-040"))]);
        var key = (await Coverage()).Associations.Single().RelationKey;
        await Decide(key, ReviewDecisionOutcome.Confirmed);

        // Another analysis baseline (e.g. an English-generation branch) whose specification has no FR-040.
        var otherBranch = Pack("Skole", "001-skoletjenesten", ("FR-006", "A held report is deleted when the report is withdrawn."), ("FR-017", "The message id is the idempotency key."));
        await Interpret([Scenario("bufdata-fil", "Bufdata-fil", published: false, refs: Ref("FR-040"))], otherBranch);
        var v = await Coverage();
        v.Associations.Should().BeEmpty("no association crosses into a baseline whose specification lacks the requirement");
        v.Rows.Should().NotContain(r => r.RequirementId == "FR-040");
        v.Requirements.Should().Be(2);
        await using var db = Db();
        (await new CanonicalInterpretationService(db, new NoSources(), _clock).DecisionsAsync(Scope, key, 10, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task N_O_an_id_two_feature_packs_define_is_ambiguous_without_a_spec_file_and_an_unknown_id_is_never_invented()
    {
        var mm = Pack("MM", "001-meldingsutvekslermottak", ("FR-006", "Receipts are sent within 5 minutes."));
        await Interpret([
            Scenario("a", "Ambiguous", refs: Ref("FR-006", spec: null)),
            Scenario("b", "Scoped", refs: Ref("FR-006")),
            Scenario("c", "Missing", refs: Ref("FR-999")),
        ], Skole, mm);
        var v = await Coverage();
        v.Associations.Where(a => a.ScenarioId == "a").Should().HaveCount(2).And.OnlyContain(a => a.Trust == AssociationTrust.SuggestedAssociation && a.RuleId == CorrelationRules.GuideScenarioAmbiguousRule);
        v.Associations.Single(a => a.ScenarioId == "b").RequirementKey.Should().Contain("skole/001-skoletjenesten/FR-006");
        v.Associations.Should().NotContain(a => a.ScenarioId == "c");
        v.ScenariosWithoutRequirement.Should().Contain($"{Guide}/c");

        var limitations = CorrelationRules.Correlate(CorrelationInput.Empty(Scope) with { Structures = [Skole], GuideScenarios = [Scenario("c", "Missing", refs: Ref("FR-999"))] }).Limitations;
        limitations.Should().Contain(l => l.Contains("FR-999"));
    }

    [Fact]
    public async Task P_successive_decisions_on_one_association_leave_exactly_one_current_decision()
    {
        // Two reviewers deciding the same association one after the other. (Truly concurrent writes are refused on PostgreSQL by the unique
        // (scope, sequence) index of the shared registry; the in-memory provider used here cannot enforce it, so that case is not claimed here.)
        await Interpret([Scenario("trekk", "Trukket rapport", published: false, refs: Ref("FR-006"))]);
        var key = (await Coverage()).Associations.Single().RelationKey;
        await Decide(key, ReviewDecisionOutcome.Confirmed);
        await Decide(key, ReviewDecisionOutcome.Deferred);
        await using var db = Db();
        var all = await db.Decisions.Where(d => d.Scope == Scope && d.TargetKey == key).ToListAsync();
        all.Should().HaveCount(2);
        all.Count(d => d.SupersededById == null).Should().Be(1, "one decision is current; the other is history");
    }

    [Fact]
    public void Trust_follows_the_review_state_and_never_counts_a_suggestion()
    {
        RequirementCoverageService.Trust(RelationResolution.Explicit, RelationReviewState.NotReviewed).Should().Be(AssociationTrust.ExplicitSourceReference);
        RequirementCoverageService.Trust(RelationResolution.Candidate, RelationReviewState.Confirmed).Should().Be(AssociationTrust.ReviewedCanonicalAssociation);
        RequirementCoverageService.Trust(RelationResolution.Candidate, RelationReviewState.NotReviewed).Should().Be(AssociationTrust.SuggestedAssociation);
        RequirementCoverageService.Trust(RelationResolution.Candidate, RelationReviewState.RevalidationRequired).Should().Be(AssociationTrust.SuggestedAssociation);
        RequirementCoverageService.Trust(RelationResolution.Explicit, RelationReviewState.Conflict).Should().Be(AssociationTrust.SuggestedAssociation);
        RequirementCoverageService.Trust(RelationResolution.Candidate, RelationReviewState.Rejected).Should().Be(AssociationTrust.Rejected);
    }

    [Fact]
    public void The_guide_reader_passes_only_what_the_guide_states()
    {
        var content = new SystemGuideContent
        {
            Rules = [new GuideRule { Id = "r-ukjent", Title = "Ukjent barn", TestScenarioIds = ["manglende-barn"],
                Evidence = [new GuideEvidence { TestId = "HeldReportTests, HeldAtLockTests" }, new GuideEvidence { RequirementId = "FR-006", File = "Skole/specs/001-skoletjenesten/spec.md" }] }],
            Scenarios = [new GuideTestScenario { Id = "manglende-barn", Title = "Barnet finnes ikke", ExistingTests = ["E2E-02"] }, new GuideTestScenario { Id = "annet", Title = "Annet" }],
        };
        var inputs = SystemGuideCanonicalReader.Inputs(new SystemGuideRevision { GuideKey = Guide, Revision = 3, State = GuideRevisionState.Published, Content = content }).ToList();
        var s = inputs.Single(i => i.ScenarioId == "manglende-barn");
        s.Published.Should().BeTrue();
        s.RuleIds.Should().Equal("r-ukjent");
        s.TestReferences.Should().BeEquivalentTo(["E2E-02", "HeldReportTests, HeldAtLockTests"]);
        s.RequirementReferences.Should().Equal(new GuideRequirementReference("FR-006", "Skole/specs/001-skoletjenesten/spec.md"));
        inputs.Single(i => i.ScenarioId == "annet").RequirementReferences.Should().BeEmpty();
        CorrelationRules.SpecScope("Skole/specs/001-skoletjenesten/spec.md").Should().Be(("Skole", "001-skoletjenesten"));
        CorrelationRules.SpecScope("Skole/src/Program.cs").Should().BeNull();
    }
}
