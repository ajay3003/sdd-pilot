using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.Scopes;
using BirkNext.Snapshots;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Requirement identity (identity/1): unscoped source-only requirement ids become proposals only from the feature packs a Delivery snapshot of the same
/// analysis defines (one = candidate, several = ambiguous, none = unresolved); only a person's confirmation of one specific pack makes the scope catalog
/// and its checks resolve the scoped key. Decisions survive reanalysis, wrong targets are refused, and nothing leaks across projects.
/// </summary>
public sealed class RequirementIdentityTests
{
    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    const string SrcId = "11111111-1111-1111-1111-111111111111";
    static readonly SnapshotReference Src = new(SnapshotType.SourceAnalysis, SrcId, "TaFixture.zip");
    static SnapshotReference Delivery(string id) => new(SnapshotType.Delivery, id, $"Delivery {id}");

    readonly string _name = $"identity-{Guid.NewGuid():N}";
    CanonicalInterpretationDbContext Db() => new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase(_name).Options);
    static CanonicalInterpretationService Service(CanonicalInterpretationDbContext db) => new(db, new NoSources(), TimeProvider.System);

    static WorkItemStructureInput Pack(string module, string feature, string delivery, params string[] requirements) =>
        new(module, feature, module, [.. requirements.Select(r => new WorkItemRequirementInput(r, $"- **{r}**: The system MUST do {r} in {module}."))], [], [], $"delivery:{delivery}");

    /// <summary>The source names FR-001, FR-002 and FR-009 (no module or feature); with a Delivery snapshot, Payments defines FR-001 and FR-002 and
    /// Billing defines FR-001.</summary>
    static CorrelationResult Correlate(string scope, bool withDelivery, string delivery = "d1", bool billing = true) =>
        CorrelationRules.Correlate(CorrelationInput.Empty(scope) with
        {
            Symbols =
            [
                new("ta", "Payments/src/Payments/PaymentService.cs", "PaymentService", "Payments", "Class", null, null, ["FR-001", "FR-002"], [], null, SrcId),
                new("ta", "Payments/src/Payments/Ledger.cs", "Ledger", "Payments", "Class", null, null, ["FR-009"], [], null, SrcId),
            ],
            Structures = Packs(withDelivery, delivery, billing),
        });

    static IReadOnlyList<WorkItemStructureInput>? Packs(bool withDelivery, string delivery, bool billing)
    {
        if (!withDelivery) return null;
        var packs = new List<WorkItemStructureInput> { Pack("Payments", "001-payments", delivery, "FR-001", "FR-002") };
        if (billing) packs.Add(Pack("Billing", "001-billing", delivery, "FR-001"));
        return packs;
    }

    static Task Persist(CanonicalInterpretationService service, string scope, bool withDelivery, string delivery = "d1", bool billing = true) =>
        service.PersistAsync(scope, Correlate(scope, withDelivery, delivery, billing), withDelivery ? new[] { Src, Delivery(delivery) } : new[] { Src }, [], "test", default);

    const string Fr1 = "Requirement|FR-001", Fr2 = "Requirement|FR-002", Fr9 = "Requirement|FR-009";
    const string PayFr1 = "Requirement|payments/001-payments/FR-001", BillFr1 = "Requirement|billing/001-billing/FR-001", PayFr2 = "Requirement|payments/001-payments/FR-002";

    static ReviewDecisionRequest Decide(string unscoped, string scoped, ReviewDecisionOutcome outcome, string? notes = "Named by PaymentService in the Payments module") =>
        new(ReviewTargetKind.Identity, RequirementIdentity.DecisionKey(unscoped, scoped), outcome, "Tester", notes);

    [Fact]
    public async Task Source_only_ids_are_unresolved_until_a_Delivery_snapshot_is_analyzed()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: false);

        var view = await service.RequirementIdentitiesAsync("proj", default);

        view.DeliveryIncluded.Should().BeFalse();
        view.Rows.Select(r => r.UnscopedKey).Should().BeEquivalentTo([Fr1, Fr2, Fr9]);
        view.Rows.Should().OnlyContain(r => r.State == RequirementIdentityState.Unresolved && r.Targets.Count == 0);
        view.Rows.Single(r => r.UnscopedKey == Fr1).Explanation.Should().Contain("no Delivery snapshot");
        view.Rows.Single(r => r.UnscopedKey == Fr1).FoundIn.Should().Contain(f => f.Reference.Contains("PaymentService"), "where the source names the id is evidence");
    }

    [Fact]
    public async Task A_Delivery_snapshot_proposes_candidate_ambiguous_and_unresolved_by_exact_id_only()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: true);

        var view = await service.RequirementIdentitiesAsync("proj", default);

        view.DeliveryIncluded.Should().BeTrue();
        var fr2 = view.Rows.Single(r => r.UnscopedKey == Fr2);
        fr2.State.Should().Be(RequirementIdentityState.Candidate, "only Payments/001-payments defines FR-002");
        fr2.Targets.Should().ContainSingle().Which.Should().Match<RequirementIdentityTarget>(t => t.TargetKey == PayFr2 && t.Module == "Payments" && t.Feature == "001-payments"
            && t.State == IdentityTargetState.Candidate && t.Evidence.Any(e => e.SourceSnapshotId == "delivery:d1"));
        fr2.ConfirmedKey.Should().BeNull("a candidate is never applied");

        var fr1 = view.Rows.Single(r => r.UnscopedKey == Fr1);
        fr1.State.Should().Be(RequirementIdentityState.Ambiguous);
        fr1.Targets.Select(t => t.TargetKey).Should().BeEquivalentTo([PayFr1, BillFr1], "every pack that defines the id is listed, none is chosen");

        var fr9 = view.Rows.Single(r => r.UnscopedKey == Fr9);
        fr9.State.Should().Be(RequirementIdentityState.Unresolved, "no pack defines FR-009");
        fr9.Explanation.Should().Contain("No feature pack");
    }

    [Fact]
    public async Task Only_a_confirmation_makes_the_scope_catalog_resolve_the_scoped_key()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: true);
        var catalog = new ScopeMemberCatalogService(service);

        // Candidate: the unscoped id stays as it is everywhere.
        (await service.ConfirmedRequirementIdentitiesAsync("proj", default)).Should().BeEmpty();
        (await Requirements(catalog)).Should().Contain(Fr2).And.Contain(PayFr2);
        (await service.ResolveRequirementIdentitiesAsync("proj", [Fr2], default))[Fr2].Should().Be(Fr2);

        var decision = await service.DecideAsync("proj", Decide(Fr2, PayFr2, ReviewDecisionOutcome.Confirmed), null, default);
        decision.TargetKind.Should().Be(ReviewTargetKind.Identity);
        decision.Reviewer.Should().Be("Tester");

        (await service.ConfirmedRequirementIdentitiesAsync("proj", default)).Should().Equal(new Dictionary<string, string> { [Fr2] = PayFr2 });
        (await service.ResolveRequirementIdentitiesAsync("proj", [Fr2, Fr1], default)).Should().Equal(new Dictionary<string, string> { [Fr2] = PayFr2, [Fr1] = Fr1 });
        var items = await Requirements(catalog);
        items.Should().Contain(PayFr2).And.NotContain(Fr2, "the confirmed scoped key is the identity the catalog offers");
        items.Should().Contain(Fr1, "an ambiguous id is not resolved by a decision on another id");
        (await service.LatestEntityNamesAsync("proj", [Fr2], default))![Fr2].Should().Contain("payments/001-payments/FR-002").And.Contain("confirmed identity");

        var row = (await service.RequirementIdentitiesAsync("proj", default)).Rows.Single(r => r.UnscopedKey == Fr2);
        row.State.Should().Be(RequirementIdentityState.Confirmed);
        row.ConfirmedKey.Should().Be(PayFr2);
    }

    [Fact]
    public async Task An_ambiguous_id_needs_a_specific_target_and_one_confirmation_at_a_time()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: true);

        await service.DecideAsync("proj", Decide(Fr1, BillFr1, ReviewDecisionOutcome.Confirmed), null, default);
        var again = () => service.DecideAsync("proj", Decide(Fr1, PayFr1, ReviewDecisionOutcome.Confirmed), null, default);
        await again.Should().ThrowAsync<CanonicalRequestException>().WithMessage("*already confirmed*");

        (await service.ConfirmedRequirementIdentitiesAsync("proj", default))[Fr1].Should().Be(BillFr1);
        var row = (await service.RequirementIdentitiesAsync("proj", default)).Rows.Single(r => r.UnscopedKey == Fr1);
        row.State.Should().Be(RequirementIdentityState.Confirmed);
        row.Targets.Single(t => t.TargetKey == PayFr1).State.Should().Be(IdentityTargetState.Candidate);
    }

    [Fact]
    public async Task Rejection_persists_and_never_resolves()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: true);

        await service.DecideAsync("proj", Decide(Fr2, PayFr2, ReviewDecisionOutcome.Confirmed), null, default);
        await service.DecideAsync("proj", Decide(Fr2, PayFr2, ReviewDecisionOutcome.Rejected, "PaymentService refers to the refund in Billing, not this pack"), null, default);

        (await service.ConfirmedRequirementIdentitiesAsync("proj", default)).Should().BeEmpty();
        var row = (await service.RequirementIdentitiesAsync("proj", default)).Rows.Single(r => r.UnscopedKey == Fr2);
        row.State.Should().Be(RequirementIdentityState.Rejected);
        row.Targets.Single().Decision!.Notes.Should().Contain("refund");
        var history = await service.DecisionsAsync("proj", RequirementIdentity.DecisionKey(Fr2, PayFr2), 10, default);
        history.Select(h => h.Outcome).Should().Equal([ReviewDecisionOutcome.Rejected, ReviewDecisionOutcome.Confirmed], "append-only: the confirmation is superseded, not edited");
        history.Last().SupersededById.Should().Be(history.First().Id);
    }

    [Fact]
    public async Task Reanalysis_keeps_decisions_and_applies_them_while_the_target_is_proposed()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: true);
        await service.DecideAsync("proj", Decide(Fr2, PayFr2, ReviewDecisionOutcome.Confirmed), null, default);
        await service.DecideAsync("proj", Decide(Fr1, BillFr1, ReviewDecisionOutcome.Rejected, "Billing does not record payments"), null, default);

        // Same evidence again, then a newer Delivery snapshot: the decisions stay and still apply.
        await Persist(service, "proj", withDelivery: true);
        await Persist(service, "proj", withDelivery: true, delivery: "d2");
        (await service.ConfirmedRequirementIdentitiesAsync("proj", default))[Fr2].Should().Be(PayFr2);
        (await service.RequirementIdentitiesAsync("proj", default)).Rows.Single(r => r.UnscopedKey == Fr1).Targets.Single(t => t.TargetKey == BillFr1).State
            .Should().Be(IdentityTargetState.Rejected);

        // A source-only reanalysis proposes nothing: the confirmation is kept but not applied (and reported).
        await Persist(service, "proj", withDelivery: false);
        (await service.ConfirmedRequirementIdentitiesAsync("proj", default)).Should().BeEmpty();
        var view = await service.RequirementIdentitiesAsync("proj", default);
        view.Notices.Should().Contain(n => n.Contains("FR-002") && n.Contains("not applied"));
        (await db.Decisions.CountAsync(d => d.TargetKind == "Identity")).Should().Be(2, "no reanalysis erases a decision");

        // With the Delivery snapshot again, it applies again.
        await Persist(service, "proj", withDelivery: true, delivery: "d2");
        (await service.ConfirmedRequirementIdentitiesAsync("proj", default))[Fr2].Should().Be(PayFr2);
    }

    [Fact]
    public async Task A_wrong_module_feature_or_id_target_is_refused_and_nothing_is_applied()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: true);

        foreach (var wrong in new[] { "Requirement|billing/001-billing/FR-002", "Requirement|payments/002-other/FR-002", PayFr1, "Requirement|FR-002" })
        {
            var act = () => service.DecideAsync("proj", Decide(Fr2, wrong, ReviewDecisionOutcome.Confirmed), null, default);
            await act.Should().ThrowAsync<CanonicalRequestException>();
        }
        var noRationale = () => service.DecideAsync("proj", Decide(Fr2, PayFr2, ReviewDecisionOutcome.Confirmed, notes: " "), null, default);
        await noRationale.Should().ThrowAsync<CanonicalRequestException>().WithMessage("*rationale*");
        var malformed = () => service.DecideAsync("proj", new(ReviewTargetKind.Identity, "Requirement|FR-002", ReviewDecisionOutcome.Confirmed, null, "x"), null, default);
        await malformed.Should().ThrowAsync<CanonicalRequestException>();

        (await db.Decisions.CountAsync()).Should().Be(0);
        (await service.ConfirmedRequirementIdentitiesAsync("proj", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_Delivery_snapshot_that_no_longer_defines_the_target_stops_the_confirmation_applying()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: true);
        await service.DecideAsync("proj", Decide(Fr1, BillFr1, ReviewDecisionOutcome.Confirmed), null, default);

        await Persist(service, "proj", withDelivery: true, delivery: "d3", billing: false);

        (await service.ConfirmedRequirementIdentitiesAsync("proj", default)).Should().BeEmpty("Billing is not in the analyzed Delivery snapshot any more");
        var row = (await service.RequirementIdentitiesAsync("proj", default)).Rows.Single(r => r.UnscopedKey == Fr1);
        row.State.Should().Be(RequirementIdentityState.Candidate, "only Payments defines FR-001 now — a new proposal, not a silent re-point");
        row.ConfirmedKey.Should().BeNull();
    }

    [Fact]
    public async Task Decisions_are_isolated_per_project()
    {
        await using var db = Db();
        var service = Service(db);
        await Persist(service, "proj", withDelivery: true);
        await Persist(service, "other", withDelivery: true);
        await service.DecideAsync("proj", Decide(Fr2, PayFr2, ReviewDecisionOutcome.Confirmed), null, default);

        (await service.ConfirmedRequirementIdentitiesAsync("other", default)).Should().BeEmpty();
        (await service.RequirementIdentitiesAsync("other", default)).Rows.Single(r => r.UnscopedKey == Fr2).State.Should().Be(RequirementIdentityState.Candidate);
        (await Requirements(new ScopeMemberCatalogService(service), "other")).Should().Contain(Fr2);

        // A project without the Delivery snapshot cannot decide on a target only another project's analysis proposes.
        await Persist(service, "third", withDelivery: false);
        var act = () => service.DecideAsync("third", Decide(Fr2, PayFr2, ReviewDecisionOutcome.Confirmed), null, default);
        await act.Should().ThrowAsync<CanonicalRequestException>();
    }

    [Fact]
    public async Task An_id_an_earlier_source_only_analysis_held_is_proposed_and_resolves_for_existing_scope_members_once_confirmed()
    {
        await using var db = Db();
        var service = Service(db);
        CorrelationResult WithTest(bool delivery) => CorrelationRules.Correlate(CorrelationInput.Empty("proj") with
        {
            Tests = [new("ta", "Payments/tests/PaymentServiceTests.cs", "PaymentServiceTests.Refund", "PaymentServiceTests", ["FR-002"], [], SrcId, "ta|Payments.Tests|PaymentServiceTests.Refund")],
            Structures = Packs(delivery, "d1", billing: true),
        });
        await service.PersistAsync("proj", WithTest(false), [Src], [], "source only", default);
        (await service.RequirementIdentitiesAsync("proj", default)).Rows.Should().ContainSingle(r => r.UnscopedKey == Fr2 && r.State == RequirementIdentityState.Unresolved);

        // With the Delivery snapshot the test's explicit link goes to the only pack that defines FR-002, so the latest analysis no longer holds the
        // unscoped id — but a scope chosen from the source-only analysis still references it.
        await service.PersistAsync("proj", WithTest(true), [Src, Delivery("d1")], [], "with delivery", default);
        (await service.LatestEntityNamesAsync("proj", [Fr2], default))!.Should().NotContainKey(Fr2, "not in the latest analysis and not confirmed");
        var row = (await service.RequirementIdentitiesAsync("proj", default)).Rows.Should().ContainSingle(r => r.UnscopedKey == Fr2).Subject;
        row.State.Should().Be(RequirementIdentityState.Candidate);
        row.InLatestAnalysis.Should().BeFalse();
        row.FoundIn.Should().Contain(f => f.Reference.Contains("PaymentServiceTests"));

        await service.DecideAsync("proj", Decide(Fr2, PayFr2, ReviewDecisionOutcome.Confirmed, "The refund test belongs to Payments"), null, default);

        (await service.LatestEntityNamesAsync("proj", [Fr2], default))![Fr2].Should().Contain("payments/001-payments/FR-002", "the existing member resolves to the confirmed identity");
        (await service.ResolveRequirementIdentitiesAsync("proj", [Fr2], default))[Fr2].Should().Be(PayFr2);
    }

    [Fact]
    public void State_rules_never_default_an_ambiguous_choice()
    {
        RequirementIdentity.StateOf([]).Should().Be(RequirementIdentityState.Unresolved);
        RequirementIdentity.StateOf([IdentityTargetState.Candidate]).Should().Be(RequirementIdentityState.Candidate);
        RequirementIdentity.StateOf([IdentityTargetState.Candidate, IdentityTargetState.Candidate]).Should().Be(RequirementIdentityState.Ambiguous);
        RequirementIdentity.StateOf([IdentityTargetState.Rejected, IdentityTargetState.Candidate]).Should().Be(RequirementIdentityState.Ambiguous, "rejecting one never confirms the other");
        RequirementIdentity.StateOf([IdentityTargetState.Rejected, IdentityTargetState.Rejected]).Should().Be(RequirementIdentityState.Rejected);
        RequirementIdentity.StateOf([IdentityTargetState.Rejected, IdentityTargetState.Confirmed]).Should().Be(RequirementIdentityState.Confirmed);
        RequirementIdentity.ParseDecisionKey(RequirementIdentity.DecisionKey(Fr1, PayFr1)).Should().Be((Fr1, PayFr1));
        RequirementIdentity.ParseDecisionKey("Requirement|FR-001").Should().BeNull();
    }

    static async Task<IReadOnlyList<string>> Requirements(ScopeMemberCatalogService catalog, string scope = "proj") =>
        [.. (await catalog.SearchAsync(ScopeMemberKind.Requirement, scope, null, 0, 100, default)).Items.Select(i => i.TargetId)];
}
