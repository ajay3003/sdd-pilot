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
/// Governance (governance/1): findings come from the canonical store with the review queue's own classification; the gate weighs them per action. Real
/// regression cases: shared/foundation work is no defect (A), a removed story reference is a finding (B), an undefined story reference needs review (C),
/// an identity conflict blocks approval / release but only warns a review (D), a derived contract occurrence is no finding (E), candidate-only test
/// links never satisfy confirmed coverage (J), an uninterpreted project is Not assessed, limitations are Informational, other modules never block.
/// </summary>
public sealed class GovernanceGateTests
{
    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    sealed class Store
    {
        public readonly CanonicalInterpretationDbContext Db = new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"gov-{Guid.NewGuid():N}").Options);
        public readonly Dictionary<string, Guid> Ids = [];
        public readonly Guid Snapshot = Guid.NewGuid();
        public Store() => Db.Snapshots.Add(new InterpretationSnapshotRecord { Id = Snapshot, Scope = "proj", CreatedAt = Now });

        public void Entity(CanonicalEntityType type, string id, string name, params (string Key, string Value)[] attributes)
        {
            Ids[id] = Guid.NewGuid();
            Db.Entities.Add(new CanonicalEntityRecord
            {
                Id = Ids[id], Scope = "proj", EntityType = type.ToString(), IdentityKey = id, DisplayName = name,
                AttributesJson = JsonSerializer.Serialize(attributes.ToDictionary(a => a.Key, a => a.Value)),
            });
            Db.SnapshotEntities.Add(new InterpretationSnapshotEntityRecord { SnapshotId = Snapshot, EntityId = Ids[id], DisplayName = name });
        }

        public void Relation(string source, CanonicalRelationType type, string target, RelationResolution resolution, RelationReviewState review = RelationReviewState.NotReviewed,
            string rule = "CR-X") =>
            Db.Relations.Add(new CanonicalRelationRecord
            {
                Id = Guid.NewGuid(), Scope = "proj", SourceEntityId = Ids[source], TargetEntityId = Ids[target], RelationType = type.ToString(), RelationKey = $"{source}-{type}-{target}",
                Resolution = resolution.ToString(), ReviewState = review.ToString(), RuleId = rule,
            });

        public async Task<CanonicalInterpretationService> Service()
        {
            await Db.SaveChangesAsync();
            return new(Db, new NoSources(), TimeProvider.System);
        }
    }

    static async Task<CanonicalInterpretationService> RealCases()
    {
        var s = new Store();
        (string, string) Skole = ("module", "SkoleService"), Person = ("module", "Person");
        s.Entity(CanonicalEntityType.Task, "t-shared", "T001 Set up project", Skole, ("relationship", "SharedFoundational"), ("id", "T001"));
        s.Entity(CanonicalEntityType.Task, "t-removed", "T010 Register absence", Skole, ("relationship", "RemovedStoryReference"), ("id", "T010"), ("storyReference", "US3"));
        s.Entity(CanonicalEntityType.Task, "t-us6", "T020 Frontend", Skole, ("relationship", "UnresolvedReference"), ("id", "T020"), ("storyReference", "US6"));
        s.Entity(CanonicalEntityType.UserStory, "us3", "US3 (removed)", Skole);
        s.Entity(CanonicalEntityType.System, "mu", "Meldingsutveksleren");
        s.Entity(CanonicalEntityType.System, "mu-other", "Buf.Meldingsutveksler.Service");
        s.Entity(CanonicalEntityType.Contract, "fravaer", "Fravaersmelding");
        s.Entity(CanonicalEntityType.ContractOccurrence, "fravaer-v1", "contracts/v1/fravaer.xsd");
        s.Entity(CanonicalEntityType.Requirement, "fr1", "FR-001", Skole, ("id", "FR-001"));
        s.Entity(CanonicalEntityType.Requirement, "fr2", "FR-002", Skole, ("id", "FR-002"));
        s.Entity(CanonicalEntityType.Requirement, "fr3", "FR-003", Skole, ("id", "FR-003"));
        s.Entity(CanonicalEntityType.Requirement, "p1", "Person FR-001", Person, ("id", "FR-001"));
        s.Entity(CanonicalEntityType.Test, "t-explicit", "Fr001Tests");
        s.Entity(CanonicalEntityType.Test, "t-candidate", "Fr002LooksLike");

        s.Relation("t-removed", CanonicalRelationType.References, "us3", RelationResolution.Unresolved, rule: CorrelationRules.TaskGroupRule);
        s.Relation("mu-other", CanonicalRelationType.AliasOf, "mu", RelationResolution.Explicit, RelationReviewState.Conflict, CorrelationRules.DeclaredAliasRule);
        s.Relation("fravaer-v1", CanonicalRelationType.OccurrenceOf, "fravaer", RelationResolution.Derived);
        s.Relation("t-explicit", CanonicalRelationType.Verifies, "fr1", RelationResolution.Explicit);
        s.Relation("t-candidate", CanonicalRelationType.Verifies, "fr2", RelationResolution.Candidate, rule: CorrelationRules.TestCandidateRule);
        return await s.Service();
    }

    [Fact]
    public async Task Real_cases_become_the_right_findings_and_nothing_else()
    {
        var findings = await (await RealCases()).GovernanceFindingsAsync("proj", default);
        findings.Where(f => f.PolicyId == "gov.analysis.limitation").Should().ContainSingle(f => f.Evidence[0].Contains("implementation evidence is not linked"),
            "an analyzer limitation is stated, as information");
        var byPolicy = findings.Where(f => f.PolicyId != "gov.analysis.limitation").GroupBy(f => (f.PolicyId, f.Module)).ToDictionary(g => g.Key, g => g.Sum(f => f.Count));

        byPolicy.Should().BeEquivalentTo(new Dictionary<(string, string?), int>
        {
            [("gov.identity.conflict", null)] = 1,
            [("gov.traceability.removed-story-reference", "SkoleService")] = 1,
            [("gov.traceability.unresolved-story-reference", "SkoleService")] = 1,
            [("gov.coverage.candidate-only", "SkoleService")] = 1,
            [("gov.coverage.requirement-without-test", "SkoleService")] = 1,
            [("gov.coverage.requirement-without-test", "Person")] = 1,
        }, "shared/foundation work (A) and a derived contract occurrence (E) are no finding; the removed-story relation is not counted twice (B)");
        findings.Single(f => f.PolicyId == "gov.traceability.removed-story-reference").Evidence.Should().Equal(["T010 → US3"]);
        findings.Single(f => f.PolicyId == "gov.traceability.unresolved-story-reference").Resolution.Should().Contain("never reassigns");
        findings.Single(f => f.PolicyId == "gov.coverage.candidate-only").Evidence.Should().Equal(["FR-002"], "a suggested link never satisfies confirmed coverage (J)");
        findings.Single(f => f.PolicyId == "gov.identity.conflict").AffectedConsumers.Should().Contain("Integration Quality Review");
        findings.Should().OnlyContain(f => f.PolicyVersion == 1);
    }

    [Fact]
    public async Task One_identity_conflict_blocks_release_and_approval_but_only_warns_a_review()
    {
        var service = await RealCases();
        var release = await service.GovernanceAsync("proj", GateAction.ReleaseReadiness, null, default);
        release.Decision.Should().Be(GateDecision.Blocked);
        release.ExitCode.Should().Be(1);
        release.Findings.First().Finding.PolicyId.Should().Be("gov.identity.conflict", "blocking findings come first");
        (await service.GovernanceAsync("proj", GateAction.Approval, null, default)).Decision.Should().Be(GateDecision.Blocked);

        var review = await service.GovernanceAsync("proj", GateAction.Review, null, default);
        review.Decision.Should().Be(GateDecision.ReadyWithLimitations);
        review.ExitCode.Should().Be(0, "warnings never fail CI");
        review.PolicySetVersion.Should().Be("governance/1");
    }

    [Fact]
    public async Task Module_scope_keeps_its_own_and_global_findings_only()
    {
        var person = await (await RealCases()).GovernanceAsync("proj", GateAction.DeliveryReport, ["Person"], default);
        person.Findings.Select(f => (f.Finding.PolicyId, f.Finding.Module)).Should().BeEquivalentTo(new[]
        {
            ("gov.identity.conflict", (string?)null), ("gov.coverage.requirement-without-test", "Person"), ("gov.analysis.limitation", null),
        }, "SkoleService's story references never weigh on the Person scope; global findings stay");
        person.ModuleScope.Should().Equal("Person");
    }

    [Fact]
    public async Task A_conflict_on_a_system_named_like_a_module_weighs_on_that_module_only()
    {
        // Real case: Utdanning ↔ SkoleAdapter (conflicting declared aliases) must not block the PersonService release.
        var s = new Store();
        s.Entity(CanonicalEntityType.Module, "skoleadapter", "SkoleAdapter");
        s.Entity(CanonicalEntityType.System, "utdanning", "Utdanning");
        s.Entity(CanonicalEntityType.System, "skoleadapter-system", "SkoleAdapter");
        s.Relation("utdanning", CanonicalRelationType.CandidateSameAs, "skoleadapter-system", RelationResolution.Ambiguous, rule: CorrelationRules.AliasConflictRule);
        var service = await s.Service();

        (await service.GovernanceAsync("proj", GateAction.ReleaseReadiness, ["PersonService"], default)).Decision.Should().NotBe(GateDecision.Blocked);
        var skole = await service.GovernanceAsync("proj", GateAction.ReleaseReadiness, ["SkoleAdapter"], default);
        skole.Decision.Should().Be(GateDecision.Blocked);
        skole.Findings.First().Finding.Module.Should().Be("SkoleAdapter");
    }

    [Fact]
    public async Task An_uninterpreted_project_is_not_assessed_and_never_ready()
    {
        var service = new CanonicalInterpretationService(
            new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"gov-empty-{Guid.NewGuid():N}").Options),
            new NoSources(), TimeProvider.System);
        var gate = await service.GovernanceAsync("proj", GateAction.ReleaseReadiness, null, default);
        gate.Decision.Should().Be(GateDecision.NotAssessed);
        gate.Reason.Should().Contain("Nothing has been interpreted");
        gate.ExitCode.Should().Be(0);
    }

    // ── Pure gate semantics ──────────────────────────────────────────────────────────────────────────────────────────

    static GovernanceFinding F(GovernancePolicy p, string? module = null) => new(p.Id, p.Version, p.Title, p.Purpose, 1, ["x"], p.Owner, p.Resolution, [], module);

    [Fact]
    public void A_waiver_passes_with_a_visible_exception_for_its_action_and_module_only_until_it_expires()
    {
        GovernanceFinding[] findings = [F(GovernancePolicies.IdentityConflict, "SkoleService")];
        var waiver = new GovernanceWaiver("gov.identity.conflict", GateAction.ReleaseReadiness, "SkoleService", "Accepted for release 1.4: MU naming fixed next sprint", Now, Now.AddDays(14));

        var passed = GovernanceGate.Evaluate(GateAction.ReleaseReadiness, findings, null, Now, waivers: [waiver]);
        passed.Decision.Should().Be(GateDecision.PassedWithException);
        GovernancePolicies.DecisionLabel(passed.Decision).Should().Be("Passed with reviewed exception", "never plain Ready");
        passed.Findings.Single().Finding.Should().Be(findings[0], "the waiver changes the decision, not the evidence");
        passed.ExitCode.Should().Be(0);

        GovernanceGate.Evaluate(GateAction.Approval, findings, null, Now, waivers: [waiver]).Decision.Should().Be(GateDecision.Blocked, "another action is not waived");
        GovernanceGate.Evaluate(GateAction.ReleaseReadiness, [F(GovernancePolicies.IdentityConflict, "Person")], null, Now, waivers: [waiver]).Decision
            .Should().Be(GateDecision.Blocked, "another module is not waived");
        GovernanceGate.Evaluate(GateAction.ReleaseReadiness, findings, null, Now.AddDays(15), waivers: [waiver]).Decision.Should().Be(GateDecision.Blocked, "expired");
    }

    [Fact]
    public void An_override_changes_the_level_and_says_where_it_comes_from()
    {
        GovernanceFinding[] findings = [F(GovernancePolicies.NoTest)];
        var byDefault = GovernanceGate.Evaluate(GateAction.ReleaseReadiness, findings, null, Now);
        (byDefault.Decision, byDefault.Findings.Single().LevelSource).Should().Be((GateDecision.ReadyWithLimitations, "Policy default (gov.coverage.requirement-without-test v1)"));

        var strict = GovernanceGate.Evaluate(GateAction.ReleaseReadiness, findings, null, Now,
            overrides: [new("gov.coverage.requirement-without-test", GateAction.ReleaseReadiness, FindingLevel.Blocking, "Project override")]);
        (strict.Decision, strict.Findings.Single().LevelSource).Should().Be((GateDecision.Blocked, "Project override"));

        GovernanceGate.Evaluate(GateAction.ReleaseReadiness, findings, null, Now,
            overrides: [new("gov.coverage.requirement-without-test", GateAction.ReleaseReadiness, FindingLevel.NotApplicable, "Project override")]).Decision.Should().Be(GateDecision.Ready);
    }

    [Fact]
    public void Limitations_are_informational_and_policies_have_stable_unique_ids()
    {
        var gate = GovernanceGate.Evaluate(GateAction.ReleaseReadiness, [F(GovernancePolicies.Limitation)], null, Now);
        gate.Decision.Should().Be(GateDecision.Ready);
        gate.Findings.Single().Level.Should().Be(FindingLevel.Informational);
        GovernancePolicies.Limitation.Actionable.Should().BeFalse("nobody can resolve it, so it is never review work");
        GovernancePolicies.All.Select(p => p.Id).Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith("gov."));
        GovernancePolicies.All.Should().OnlyContain(p => p.Levels.Values.All(l => l != FindingLevel.Blocking)
            || p == GovernancePolicies.IdentityConflict, "new policies start as warnings unless identity is contradicted (default safety)");
    }
}
