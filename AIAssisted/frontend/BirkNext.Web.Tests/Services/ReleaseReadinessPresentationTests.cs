using BirkNext.Governance;
using BirkNext.Interpretation;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// NEXT49: what blocks the release is listed once per root cause (the M2LB identity conflict appears in Governance and Open review decisions — one
/// cause, both dimensions, a link to the exact review item); an unresolved scope is its own cause; next steps follow prerequisites; imported TRX
/// results are reported as found, never as execution of the release scope.
/// </summary>
public sealed class ReleaseReadinessPresentationTests
{
    static ReadinessDimension Dim(ReadinessDimensionKey key, string title, GateDecision state, string? next = null, IReadOnlyList<string>? evidence = null,
        DimensionApplicability applicability = DimensionApplicability.Required) =>
        new(key, title, applicability, state, $"{title} summary", evidence ?? [], next, "somewhere");

    static ReleaseReadinessAssessment M2lbLanguage() => new(new ReleaseScopeReference("ci-live", "Whole project", [], Guid.NewGuid()), GateDecision.Blocked, "Blocked by governance.",
        [
            Dim(ReadinessDimensionKey.ScopeClarity, "Scope", GateDecision.NotAssessed, "Analyze relationships with the Delivery snapshot included."),
            Dim(ReadinessDimensionKey.Requirements, "Requirements", GateDecision.NotAssessed, "Resolve the release scope first."),
            Dim(ReadinessDimensionKey.Traceability, "Traceability", GateDecision.NotAssessed),
            Dim(ReadinessDimensionKey.TestDesign, "Test design", GateDecision.NotAssessed),
            Dim(ReadinessDimensionKey.Governance, "Governance", GateDecision.Blocked, "Open the governance findings in Relationship Review.", ["Blocking · Identity conflict — SkoleAdapter"]),
            Dim(ReadinessDimensionKey.OpenDecisions, "Open review decisions", GateDecision.ReadyWithLimitations, "Answer them in Relationship Review.", ["Identity conflict — SkoleAdapter: Utdanning → SkoleAdapter"]),
            Dim(ReadinessDimensionKey.Performance, "Performance", GateDecision.NotAssessed, applicability: DimensionApplicability.Optional),
        ], ["Identity conflict — SkoleAdapter: Utdanning → SkoleAdapter"], [], "release/1", "governance/1", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Each_root_cause_is_listed_once_with_every_dimension_it_affects()
    {
        var causes = ReleaseReadinessPresentation.BlockingCauses(M2lbLanguage());

        causes.Select(c => c.Key).Should().Equal("scope", "identity-conflict");
        var conflict = causes.Single(c => c.Key == "identity-conflict");
        conflict.Dimensions.Should().Equal("Governance", "Open review decisions");
        conflict.Href.Should().Be("correlation-review?tab=review&type=IdentityConflict", "the link opens the exact review item");
        conflict.Detail.Should().Contain("deferring does not clear it");
        causes.Single(c => c.Key == "scope").Dimensions.Should().Equal("Scope", "Requirements", "Traceability", "Test design");
    }

    [Fact]
    public void Gaps_an_unresolved_scope_explains_are_not_repeated_and_optional_dimensions_are_never_required_gaps()
    {
        ReleaseReadinessPresentation.RequiredGaps(M2lbLanguage()).Should().BeEmpty("the scope cause explains them; Performance is optional");
    }

    [Fact]
    public void Next_steps_follow_prerequisites_scope_first()
    {
        var steps = ReleaseReadinessPresentation.NextSteps(M2lbLanguage());
        steps.Select(s => s.Title).Should().Equal("Scope", "Governance", "Open review decisions", "Requirements");
        steps.Single(s => s.Title == "Governance").Href.Should().Be("correlation-review?tab=review&type=IdentityConflict");
    }

    [Fact]
    public void Imported_results_are_counted_as_found_and_none_means_no_evidence()
    {
        ReleaseReadinessPresentation.Execution([], null).Should().BeNull();
        var run = ReleaseReadinessPresentation.Execution(
        [
            new SddTestExecutionEvidence { Result = "Passed" }, new SddTestExecutionEvidence { Result = "Passed" }, new SddTestExecutionEvidence { Result = "Failed" },
            new SddTestExecutionEvidence { Result = "Skipped" }, new SddTestExecutionEvidence { Result = "Passed", EvidenceKind = "Discovered" },
        ], null);
        run.Should().Be(new ReleaseExecutionEvidence(4, 2, 1, 1, null), "a discovered test is not an executed one");
    }

    // ── NEXT57: executive summary, Required/Optional groups, history comparison; the corrected M2LB scope as regression fixture ──

    /// <summary>The corrected M2LB scope (NEXT49): 38 feature packs and 676 requirements once the Delivery snapshot is kept; still Blocked by the
    /// undecided Utdanning ↔ SkoleAdapter identity conflict.</summary>
    static ReleaseReadinessAssessment M2lbCorrected()
    {
        ReadinessDimension[] dims =
        [
            Dim(ReadinessDimensionKey.ScopeClarity, "Scope", GateDecision.Ready),
            Dim(ReadinessDimensionKey.Requirements, "Requirements", GateDecision.ReadyWithLimitations, "Link tests to requirements without a test."),
            Dim(ReadinessDimensionKey.Traceability, "Traceability", GateDecision.Ready),
            Dim(ReadinessDimensionKey.TestDesign, "Test design", GateDecision.NotAssessed, "Confirm test links in Relationship Review."),
            Dim(ReadinessDimensionKey.Governance, "Governance", GateDecision.Blocked, "Open the governance findings in Relationship Review.", ["Blocking · Identity conflict — SkoleAdapter"]),
            Dim(ReadinessDimensionKey.OpenDecisions, "Open review decisions", GateDecision.ReadyWithLimitations, "Answer them in Relationship Review."),
            Dim(ReadinessDimensionKey.Performance, "Performance", GateDecision.NotAssessed, applicability: DimensionApplicability.Optional),
            Dim(ReadinessDimensionKey.CriticalE2E, "Critical E2E", GateDecision.Ready, applicability: DimensionApplicability.Optional),
        ];
        var (decision, reason) = ReleaseReadinessRules.Decide(dims);
        return new(new ReleaseScopeReference("ci-live", "Whole project", [], Guid.NewGuid()), decision, reason, dims, ["Identity conflict — SkoleAdapter"], [], "release/1", "governance/1",
            DateTimeOffset.UnixEpoch) { Facts = new(38, 676, 0, 31, 645, 1306, 0, 1, 0, 14) };
    }

    [Fact]
    public void The_corrected_M2LB_scope_shows_38_packs_and_676_requirements_and_stays_Blocked_by_the_identity_conflict()
    {
        var a = M2lbCorrected();
        a.Decision.Should().Be(GateDecision.Blocked, "an unresolved blocking governance decision never lets the release be Ready");
        var metrics = ReleaseReadinessPresentation.Metrics(a, null).ToDictionary(m => m.Key);
        metrics["packs"].Value.Should().Be("38");
        metrics["requirements"].Value.Should().Be("676");
        metrics["tested"].Value.Should().Be("0");
        metrics["tested"].Detail.Should().Contain("31 more with a suggested test only — not counted");
        metrics["executed"].Value.Should().Be("Not assessed", "discovered or linked tests are never executed tests");
        metrics["blocking"].Value.Should().Be("1");
        metrics["decisions"].Value.Should().Be("14");
        metrics["gaps"].Value.Should().Be("1", "Test design is a required gap");
    }

    [Fact]
    public void Dimensions_are_grouped_Required_then_Optional_worst_state_first_with_a_tally()
    {
        var groups = ReleaseReadinessPresentation.Groups(M2lbCorrected());
        groups.Select(g => g.Applicability).Should().Equal(DimensionApplicability.Required, DimensionApplicability.Optional);
        groups[0].Dimensions.First().Key.Should().Be(ReadinessDimensionKey.Governance, "blocked first");
        groups[0].Tally.Should().Be("1 Blocked · 1 Not assessed · 2 Ready with limitations · 2 Ready");
        groups[1].Tally.Should().Be("1 Not assessed · 1 Ready");
    }

    [Fact]
    public void Missing_required_scope_or_evidence_is_never_Ready_and_optional_gaps_never_block()
    {
        ReadinessDimension R(ReadinessDimensionKey k, GateDecision s) => Dim(k, k.ToString(), s);
        ReadinessDimension O(ReadinessDimensionKey k, GateDecision s) => Dim(k, k.ToString(), s, applicability: DimensionApplicability.Optional);
        ReleaseReadinessRules.Decide([R(ReadinessDimensionKey.ScopeClarity, GateDecision.NotAssessed), R(ReadinessDimensionKey.Governance, GateDecision.Ready)]).Decision
            .Should().Be(GateDecision.NotAssessed, "an unresolved release scope is never Ready");
        ReleaseReadinessRules.Decide([R(ReadinessDimensionKey.ScopeClarity, GateDecision.Ready), R(ReadinessDimensionKey.TestDesign, GateDecision.NotAssessed)]).Decision
            .Should().Be(GateDecision.NotAssessed, "missing mandatory evidence is never Ready");
        ReleaseReadinessRules.Decide([R(ReadinessDimensionKey.ScopeClarity, GateDecision.Ready), R(ReadinessDimensionKey.Governance, GateDecision.Blocked)]).Decision
            .Should().Be(GateDecision.Blocked, "an unresolved blocking governance decision blocks");
        ReleaseReadinessRules.Decide([R(ReadinessDimensionKey.ScopeClarity, GateDecision.Ready), O(ReadinessDimensionKey.Performance, GateDecision.Blocked)]).Decision
            .Should().Be(GateDecision.ReadyWithLimitations, "an optional dimension never blocks");
        ReleaseReadinessRules.Decide([O(ReadinessDimensionKey.Performance, GateDecision.Ready)]).Decision
            .Should().Be(GateDecision.NotAssessed, "no required dimension: nothing is Ready by default");
    }

    [Fact]
    public void A_frozen_assessment_is_compared_per_dimension_with_the_current_draft()
    {
        var frozen = M2lbCorrected();
        var now = frozen with { Dimensions = [.. frozen.Dimensions.Select(d => d.Key == ReadinessDimensionKey.Governance ? d with { State = GateDecision.Ready } : d)] };
        var changes = ReleaseReadinessPresentation.Compare(frozen, now);
        changes.Where(c => c.Changed).Should().ContainSingle().Which.Should().Match<ReleaseDimensionChange>(c =>
            c.Key == ReadinessDimensionKey.Governance && c.Then == GateDecision.Blocked && c.Now == GateDecision.Ready);
        changes.First().Changed.Should().BeTrue("changes are listed first");
        ReleaseReadinessPresentation.FreezeSummary(frozen).Should().Contain(("Feature packs · requirements", "38 · 676")).And.Contain(("Result", "Blocked"));
    }

    // ── NEXT57 follow-up: findings table and per-requirement test evidence ─────────────────────────────────────────────────

    [Fact]
    public void Findings_list_each_root_cause_once_worst_first_and_name_unassessed_required_evidence()
    {
        var findings = ReleaseReadinessPresentation.Findings(M2lbCorrected());
        findings.Select(f => f.Severity).Should().BeInAscendingOrder();
        findings.Select(f => f.Key).Should().OnlyHaveUniqueItems();
        findings.First().Should().Match<ReleaseFinding>(f => f.Severity == ReleaseFindingSeverity.Blocking && f.Key == "identity-conflict");
        findings.Should().Contain(f => f.Severity == ReleaseFindingSeverity.RequiredGap && f.Title == "Test design not assessed");
        findings.Should().Contain(f => f.Severity == ReleaseFindingSeverity.OptionalGap && f.Title == "Performance");
        findings.Should().NotContain(f => f.Title == "Scope" || f.Title == "Critical E2E", "ready dimensions are not findings");
    }

    static CanonicalEntityView E(CanonicalEntityType type, string id, string name) =>
        new(Guid.NewGuid(), type, id, name, name, "explicit id", null, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    static TraceLink Link(CanonicalEntityView test, RelationResolution resolution) => new(test, new CanonicalRelationView(Guid.NewGuid(), "r", test, test, CanonicalRelationType.Verifies,
        resolution, resolution, CorrelationConfidence.Medium, "CR", "correlation/4", [], "fp", "", RelationReviewState.NotReviewed, null, true));

    static SddTestExecutionEvidence Result(string name, string result, params string[] requirements) =>
        new() { TestName = name, FullyQualifiedTestName = name, Result = result, RequirementReferences = [.. requirements] };

    [Fact]
    public void Imported_results_map_to_a_requirement_only_by_an_explicit_reference_or_a_confirmed_test_name_never_a_candidate()
    {
        var t1 = E(CanonicalEntityType.Test, "Absence.Register", "Absence.Register");
        var candidate = E(CanonicalEntityType.Test, "Absence.Maybe", "Absence.Maybe");
        var t4 = E(CanonicalEntityType.Test, "Report.Send", "Report.Send");
        var detail = new TraceFeatureDetail(E(CanonicalEntityType.Feature, "f", "Feature"),
        [
            new(E(CanonicalEntityType.Requirement, "FR-001", "FR-001 Register"), [Link(t1, RelationResolution.Explicit)]),
            new(E(CanonicalEntityType.Requirement, "FR-002", "FR-002 Maybe"), [Link(candidate, RelationResolution.Candidate)]),
            new(E(CanonicalEntityType.Requirement, "FR-003", "FR-003 Nothing"), []),
            new(E(CanonicalEntityType.Requirement, "FR-004", "FR-004 Send"), [Link(t4, RelationResolution.Confirmed)]),
            new(E(CanonicalEntityType.Requirement, "FR-005", "FR-005 Linked"), [Link(E(CanonicalEntityType.Test, "X.Y", "X.Y"), RelationResolution.Derived)]),
        ], [], [], null);
        var results = new[] { Result("Other.Test", "Passed", "FR-001"), Result("Report.Send", "Failed"), Result("Absence.Maybe", "Passed") };

        var rows = ReleaseReadinessPresentation.RequirementEvidence(detail, results).ToDictionary(r => r.Requirement);
        rows["FR-001"].State.Should().Be(RequirementEvidenceState.Passed);
        rows["FR-001"].Mapping.Should().Be("1 by requirement reference");
        rows["FR-002"].State.Should().Be(RequirementEvidenceState.CandidateOnly, "a passing result of a candidate test is never coverage");
        rows["FR-002"].Passed.Should().Be(0);
        rows["FR-003"].State.Should().Be(RequirementEvidenceState.NoTest);
        rows["FR-004"].State.Should().Be(RequirementEvidenceState.Failed);
        rows["FR-004"].Mapping.Should().Be("1 by confirmed test name");
        rows["FR-005"].State.Should().Be(RequirementEvidenceState.NotExecuted, "a confirmed test without a result is not executed");
    }
}
