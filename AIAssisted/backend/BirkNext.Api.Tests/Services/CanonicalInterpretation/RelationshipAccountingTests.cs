using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Governance;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// NEXT48 relationship accounting on the M2LB case: the Overview showed "Conflicts 0" while governance blocked the release on the Utdanning ↔
/// SkoleAdapter identity conflict (an alias declared for two entities, stored as an ambiguous relation, not a Conflict state). The summary now counts
/// identity conflicts with the review queue's own classifier; established + candidates + not counted = every relation; an identity-conflict
/// decision needs a rationale.
/// </summary>
public sealed class RelationshipAccountingTests
{
    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    sealed class Store
    {
        public readonly CanonicalInterpretationDbContext Db = new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"acct-{Guid.NewGuid():N}").Options);
        public readonly Dictionary<string, Guid> Ids = [];
        public readonly Guid Snapshot = Guid.NewGuid();
        public Store() => Db.Snapshots.Add(new InterpretationSnapshotRecord { Id = Snapshot, Scope = "proj", CreatedAt = DateTimeOffset.UtcNow, RuleSetVersions = "correlation/4", LimitationsJson = "[]" });

        public void Entity(CanonicalEntityType type, string id, string name)
        {
            Ids[id] = Guid.NewGuid();
            Db.Entities.Add(new CanonicalEntityRecord { Id = Ids[id], Scope = "proj", EntityType = type.ToString(), IdentityKey = id, DisplayName = name, AttributesJson = "{}" });
            Db.SnapshotEntities.Add(new InterpretationSnapshotEntityRecord { SnapshotId = Snapshot, EntityId = Ids[id], DisplayName = name });
        }

        public string Relation(string source, CanonicalRelationType type, string target, RelationResolution resolution, RelationReviewState review = RelationReviewState.NotReviewed, string rule = "CR-X")
        {
            var id = Guid.NewGuid();
            var key = $"{source}-{type}-{target}";
            Db.Relations.Add(new CanonicalRelationRecord
            {
                Id = id, Scope = "proj", SourceEntityId = Ids[source], TargetEntityId = Ids[target], RelationType = type.ToString(), RelationKey = key,
                Resolution = resolution.ToString(), ReviewState = review.ToString(), RuleId = rule, Active = true, Confidence = nameof(CorrelationConfidence.Low),
                RuleSetVersion = "correlation/4", EvidenceFingerprint = "fp", Explanation = "x", EvidenceJson = "[]",
            });
            Db.SnapshotRelations.Add(new InterpretationSnapshotRelationRecord { SnapshotId = Snapshot, RelationId = id, Resolution = resolution.ToString(), ReviewState = review.ToString(), RuleId = rule, Confidence = "Low", EvidenceFingerprint = "fp" });
            return key;
        }

        public async Task<CanonicalInterpretationService> Service()
        {
            await Db.SaveChangesAsync();
            return new(Db, new NoSources(), TimeProvider.System);
        }
    }

    static (Store Store, string ConflictKey) M2lb()
    {
        var s = new Store();
        s.Entity(CanonicalEntityType.Module, "skoleadapter", "SkoleAdapter");
        s.Entity(CanonicalEntityType.System, "utdanning", "Utdanning");
        s.Entity(CanonicalEntityType.System, "skoleadapter-system", "SkoleAdapter");
        s.Entity(CanonicalEntityType.Requirement, "fr1", "FR-001");
        s.Entity(CanonicalEntityType.Requirement, "fr6", "FR-006");
        s.Entity(CanonicalEntityType.Test, "t1", "AuditTests.Writes_audit_record");
        s.Entity(CanonicalEntityType.Test, "t2", "EventTests.Publishes_event");
        s.Entity(CanonicalEntityType.TestSuite, "suite", "AuditTests");
        var conflict = s.Relation("utdanning", CanonicalRelationType.CandidateSameAs, "skoleadapter-system", RelationResolution.Ambiguous, rule: CorrelationRules.AliasConflictRule);
        s.Relation("suite", CanonicalRelationType.Contains, "t1", RelationResolution.Explicit, rule: CorrelationRules.TestSuiteRule);
        s.Relation("t1", CanonicalRelationType.Verifies, "fr1", RelationResolution.Explicit, rule: CorrelationRules.TestExplicitRule);
        s.Relation("t1", CanonicalRelationType.Verifies, "fr6", RelationResolution.Candidate, rule: CorrelationRules.TestCandidateRule);
        s.Relation("t2", CanonicalRelationType.Verifies, "fr1", RelationResolution.Candidate, RelationReviewState.Rejected, CorrelationRules.TestCandidateRule);
        return (s, conflict);
    }

    [Fact]
    public async Task The_overview_counts_the_identity_conflict_the_governance_gate_blocks_on()
    {
        var (store, _) = M2lb();
        var service = await store.Service();

        var summary = await service.SummaryAsync("proj", default);
        var gate = await service.GovernanceAsync("proj", GateAction.ReleaseReadiness, null, default);

        gate.Findings.Should().Contain(f => f.Finding.PolicyId == GovernancePolicies.IdentityConflict.Id);
        summary.Conflicts.Should().Be(1, "the conflict governance blocks on is never 0 on the Overview");
        summary.Decisions.Should().Be(1, "an identity conflict is a decision; conflicts are a subset of decisions, not an addition");
    }

    [Fact]
    public async Task Established_candidates_and_not_counted_relations_add_up_to_every_relation()
    {
        var (store, _) = M2lb();
        var service = await store.Service();

        var summary = await service.SummaryAsync("proj", default);

        summary.EstablishedRelations.Should().Be(2, "suite membership and the explicit test link");
        summary.CandidateRelations.Should().Be(1, "only the unreviewed mention; a rejected candidate is not a candidate any more");
        summary.NotCounted.Should().Be(2, "the ambiguous identity relation and the rejected candidate");
        (summary.EstablishedRelations + summary.CandidateRelations + summary.NotCounted).Should().Be(summary.RelationsByType.Values.Sum());
    }

    [Fact]
    public async Task An_identity_conflict_decision_needs_a_rationale_but_deferring_does_not()
    {
        var (store, conflict) = M2lb();
        var service = await store.Service();

        var confirm = () => service.DecideAsync("proj", new ReviewDecisionRequest(ReviewTargetKind.Relation, conflict, ReviewDecisionOutcome.Confirmed), null, default);
        await confirm.Should().ThrowAsync<CanonicalRequestException>().WithMessage("*needs a rationale*");
        var reject = () => service.DecideAsync("proj", new ReviewDecisionRequest(ReviewTargetKind.Relation, conflict, ReviewDecisionOutcome.Rejected, null, "  "), null, default);
        await reject.Should().ThrowAsync<CanonicalRequestException>();

        var deferred = await service.DecideAsync("proj", new ReviewDecisionRequest(ReviewTargetKind.Relation, conflict, ReviewDecisionOutcome.Deferred), null, default);
        deferred.Outcome.Should().Be(ReviewDecisionOutcome.Deferred);
        (await service.GovernanceAsync("proj", GateAction.ReleaseReadiness, null, default)).Decision.Should().Be(GateDecision.Blocked, "deferring is no proof; the block stays");

        var decided = await service.DecideAsync("proj", new ReviewDecisionRequest(ReviewTargetKind.Relation, conflict, ReviewDecisionOutcome.Rejected, null,
            "Utdanning is the external system; SkoleAdapter is our adapter. Separate entities."), null, default);
        decided.Notes.Should().Contain("Separate entities");
        decided.SupersedesId.Should().Be(deferred.Id, "a new auditable decision supersedes the earlier one");
    }

    [Fact]
    public void Candidate_test_links_say_what_they_are_based_on()
    {
        var input = CorrelationInput.Empty("proj") with { Tests = [new TestInput("pkg", "tests/AuditTests.cs", "AuditTests.Writes_audit_record", "AuditTests", [], ["FR-006"])] };
        var result = CorrelationRules.Correlate(input);
        var candidate = result.Relations.Single(r => r.RuleId == CorrelationRules.TestCandidateRule);
        candidate.Explanation.Should().Contain("only mentioned near the test").And.NotContain("by naming only").And.Contain("never counted as traceability or coverage");
    }
}
