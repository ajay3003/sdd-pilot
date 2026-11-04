using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Persistence of the canonical interpretation: stable ids, idempotent reinterpretation, the shared review decision registry, immutable
/// interpretation snapshots, scope isolation and the consumers that read shared aliases (Topology, IQR).
/// </summary>
public sealed class CanonicalInterpretationServiceTests
{
    sealed class NoSources : ICanonicalSourceReader
    {
        public Dictionary<Guid, IqrSourceSnapshot> Items { get; } = [];
        public Task<IqrSourceSnapshot?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(id));
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now = Now.AddSeconds(1);
    }

    readonly string _name = $"canonical-{Guid.NewGuid():N}";
    CanonicalInterpretationDbContext Db() => new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase(_name).Options);
    readonly Clock _clock = new();
    CanonicalInterpretationService Service(CanonicalInterpretationDbContext db) => new(db, new NoSources(), _clock);

    static readonly SnapshotReference Src = new(SnapshotType.SourceAnalysis, "11111111-1111-1111-1111-111111111111", "M2LB.zip");

    static CorrelationResult Correlate(string displaySuffix = "", string path = "src/SkoleService/AbsenceApi.cs", bool candidate = true) =>
        CorrelationRules.Correlate(CorrelationInput.Empty("proj") with
        {
            Operations = [new("SkoleService", "GET", "/api/absence/{id}", "openapi", Src.Id)],
            Symbols = [new("m2lb", path, "AbsenceApi.Get", "Buf.Skole", "Controller", "GET", "/api/absence/{id}", ["FR-1"], [], "SkoleService", Src.Id)],
            Tests = [new("m2lb", "tests/AbsenceTests.cs", "T.Reports_absence" + displaySuffix, null, ["FR-1"], candidate ? ["FR-2"] : [], Src.Id, "m2lb|Tests|T.Reports_absence")],
            Systems = [new(CanonicalEntityType.System, "Meldingsutveksleren", ["Buf.Meldingsutveksler.Service"], "profile"), new(CanonicalEntityType.System, "MU", [], "catalog")],
        });

    [Fact]
    public async Task Ids_are_stable_across_reinterpretation_display_changes_and_file_moves()
    {
        await using var db = Db();
        var service = Service(db);
        await service.PersistAsync("proj", Correlate(), [Src], [], "first", default);
        var before = await db.Entities.AsNoTracking().ToDictionaryAsync(e => (e.EntityType, e.IdentityKey), e => e.Id);

        await service.PersistAsync("proj", Correlate(displaySuffix: "", path: "moved/elsewhere/AbsenceApi.cs"), [Src], [], "moved", default);
        var after = await db.Entities.AsNoTracking().ToDictionaryAsync(e => (e.EntityType, e.IdentityKey), e => e.Id);

        after.Should().BeEquivalentTo(before, "identity keys never include a path or a display label, so every id is found again");
        (await db.Occurrences.CountAsync(o => o.OccurrenceKey.StartsWith("moved/"))).Should().BeGreaterThan(0, "the new location is recorded as an additional occurrence");
        (await db.Occurrences.CountAsync(o => o.OccurrenceKey.StartsWith("src/"))).Should().BeGreaterThan(0, "the old occurrence is preserved");
    }

    [Fact]
    public async Task Reinterpreting_unchanged_evidence_is_idempotent_and_reuses_the_snapshot()
    {
        await using var db = Db();
        var service = Service(db);
        var first = await service.PersistAsync("proj", Correlate(), [Src], [], "first", default);
        var counts = (await db.Entities.CountAsync(), await db.Relations.CountAsync(), await db.Occurrences.CountAsync(), await db.Aliases.CountAsync());
        var second = await service.PersistAsync("proj", Correlate(), [Src], [], "again", default);

        second.Reused.Should().BeTrue();
        second.Id.Should().Be(first.Id);
        (await db.Entities.CountAsync(), await db.Relations.CountAsync(), await db.Occurrences.CountAsync(), await db.Aliases.CountAsync()).Should().Be(counts);
        (await db.Snapshots.CountAsync()).Should().Be(1);
        first.RuleSetVersions.Should().Equal(CorrelationRules.RuleSetVersion);
        first.SourceSnapshots.Should().Equal(Src);
    }

    [Fact]
    public async Task A_rejected_candidate_stays_rejected_on_reimport_and_a_confirmed_one_is_shared()
    {
        await using var db = Db();
        var service = Service(db);
        await service.PersistAsync("proj", Correlate(), [Src], [], "first", default);
        var candidate = (await service.RelationsAsync("proj", RelationResolution.Candidate, null, CorrelationRules.TestCandidateRule, false, 0, 10, default)).Items.Single();
        candidate.Counts.Should().BeFalse("a candidate is never traceability");

        await service.DecideAsync("proj", new(ReviewTargetKind.Relation, candidate.RelationKey, ReviewDecisionOutcome.Rejected, "tester", "Wrong requirement"), null, default);
        var reimported = await service.PersistAsync("proj", Correlate(), [Src], [], "reimport", default);
        reimported.Reused.Should().BeFalse("the decision context changed");

        var after = (await service.RelationsAsync("proj", null, null, CorrelationRules.TestCandidateRule, false, 0, 10, default)).Items.Single();
        after.ReviewState.Should().Be(RelationReviewState.Rejected);
        after.Effective.Should().Be(RelationResolution.Rejected, "a heuristic never brings back a rejected link");
        (await service.RelationsAsync("proj", null, null, null, needsReview: true, 0, 50, default)).Items.Should().NotContain(r => r.Id == after.Id);

        // A correction supersedes, never edits: history keeps both.
        await service.DecideAsync("proj", new(ReviewTargetKind.Relation, candidate.RelationKey, ReviewDecisionOutcome.Confirmed, "lead", "Re-checked"), null, default);
        var history = await service.DecisionsAsync("proj", candidate.RelationKey, 10, default);
        history.Should().HaveCount(2);
        history[1].SupersededById.Should().Be(history[0].Id);
        history[0].SupersedesId.Should().Be(history[1].Id);
        (await service.RelationsAsync("proj", null, null, CorrelationRules.TestCandidateRule, false, 0, 10, default)).Items.Single().Effective.Should().Be(RelationResolution.Confirmed);
    }

    [Fact]
    public async Task Changed_evidence_requires_revalidation_instead_of_silently_applying_an_old_confirmation()
    {
        await using var db = Db();
        var service = Service(db);
        await service.PersistAsync("proj", Correlate(), [Src], [], "first", default);
        var candidate = (await service.RelationsAsync("proj", RelationResolution.Candidate, null, CorrelationRules.TestCandidateRule, false, 0, 10, default)).Items.Single();
        await service.DecideAsync("proj", new(ReviewTargetKind.Relation, candidate.RelationKey, ReviewDecisionOutcome.Confirmed), null, default);

        var moved = new SnapshotReference(SnapshotType.SourceAnalysis, "33333333-3333-3333-3333-333333333333");
        var changed = CorrelationRules.Correlate(CorrelationInput.Empty("proj") with
        {
            Tests = [new("m2lb", "tests/Renamed/AbsenceTests.cs", "T.Reports_absence", null, ["FR-1"], ["FR-2"], moved.Id, "m2lb|Tests|T.Reports_absence")],
        });
        await service.PersistAsync("proj", changed, [moved], [], "new evidence", default);
        var after = (await service.RelationsAsync("proj", null, null, CorrelationRules.TestCandidateRule, false, 0, 10, default)).Items.Single();
        after.ReviewState.Should().Be(RelationReviewState.RevalidationRequired);
        after.Counts.Should().BeFalse();
        (await service.RelationsAsync("proj", null, null, null, needsReview: true, 0, 50, default)).Items.Should().Contain(r => r.Id == after.Id);
    }

    [Fact]
    public async Task Interpretation_snapshots_are_frozen_and_comparable()
    {
        await using var db = Db();
        var service = Service(db);
        var first = await service.PersistAsync("proj", Correlate(), [Src], [], "first", default);
        var second = await service.PersistAsync("proj", Correlate(candidate: false), [Src], [], "candidate gone", default);

        second.PreviousId.Should().Be(first.Id);
        var frozen = await service.SnapshotRelationsAsync("proj", first.Id, 0, 200, default);
        frozen!.Items.Should().Contain(r => r.RuleId == CorrelationRules.TestCandidateRule, "the first snapshot still says what it said");
        (await service.SnapshotRelationsAsync("proj", second.Id, 0, 200, default))!.Items.Should().NotContain(r => r.RuleId == CorrelationRules.TestCandidateRule);

        var comparison = await service.CompareAsync("proj", first.Id, second.Id, default);
        comparison!.Removed.Should().ContainSingle(c => c.Type == CanonicalRelationType.Verifies);
        comparison.Added.Should().BeEmpty();
        (await db.Relations.SingleAsync(r => r.RuleId == CorrelationRules.TestCandidateRule)).Active.Should().BeFalse("kept for history, not deleted");
    }

    [Fact]
    public async Task Scopes_are_isolated_and_aliases_resolve_only_when_explicit_or_confirmed()
    {
        await using var db = Db();
        var service = Service(db);
        await service.PersistAsync("proj", Correlate(), [Src], [], "first", default);
        await service.PersistAsync("other", CorrelationRules.Correlate(CorrelationInput.Empty("other") with
            { Systems = [new(CanonicalEntityType.System, "Mottak Utland", [], "catalog"), new(CanonicalEntityType.System, "MU", [], "catalog")] }), [], [], "other", default);

        var mu = await service.ResolveAliasAsync("proj", "MU", default);
        mu.Candidates.Should().ContainSingle("MU is only proposed for Meldingsutveksleren here");
        mu.Resolved.Should().ContainSingle(e => e.DisplayName == "MU", "only the MU entity itself resolves; the proposal does not");
        (await service.ResolveAliasAsync("proj", "Buf.Meldingsutveksler.Service", default)).Resolved.Should().ContainSingle(e => e.DisplayName == "Meldingsutveksleren");
        (await service.ResolveAliasAsync("other", "Buf.Meldingsutveksler.Service", default)).Resolved.Should().BeEmpty("aliases never cross projects");
        (await service.EntitiesAsync("other", null, null, 0, 50, default)).Items.Should().NotContain(e => e.DisplayName == "Meldingsutveksleren");

        var key = CorrelationPrecedence.AliasTargetKey(CanonicalKeys.EntityKey(CanonicalEntityType.System, "meldingsutveksleren"), "MU");
        await service.DecideAsync("proj", new(ReviewTargetKind.Alias, key, ReviewDecisionOutcome.Confirmed, "tester"), null, default);
        (await service.ResolveAliasAsync("proj", "MU", default)).Resolved.Should().Contain(e => e.DisplayName == "Meldingsutveksleren");
        (await service.ResolveAliasAsync("other", "MU", default)).Resolved.Should().ContainSingle(e => e.DisplayName == "MU", "the confirmation is scoped to its project");

        // A confirmed alias survives the next import: ingestion never downgrades a human state.
        await service.PersistAsync("proj", Correlate(), [Src], [], "reimport", default);
        (await db.Aliases.SingleAsync(a => a.Scope == "proj" && a.NormalizedAlias == "mu")).State.Should().Be(nameof(AliasState.Confirmed));
    }

    [Fact]
    public async Task Consumers_read_only_applicable_aliases_through_the_shared_alias_source()
    {
        await using var db = Db();
        var service = Service(db);
        await service.PersistAsync("env-dev", Correlate(), [Src], [], "first", default);
        await using var app = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_name + "-app").Options);
        var source = new CanonicalAliasSource(db, app, service);

        var applicable = await source.AliasesAsync("env-dev", default);
        applicable.Should().ContainSingle(a => a.Alias == "Buf.Meldingsutveksler.Service" && a.State == AliasState.Explicit);
        applicable.Should().NotContain(a => a.Alias == "MU", "a proposal is never applied by Topology or IQR");

        var builder = new BirkNext.Api.Services.IntegrationTopology.TopologyBuilder();
        await new BirkNext.Api.Services.IntegrationTopology.CanonicalAliasTopologyProvider(source)
            .ContributeAsync(new("env-dev", null, "", new IntegrationCatalog(), [], new(null, false)), builder, default);
        builder.ComponentKey("Buf.Meldingsutveksler.Service").Should().Be(builder.ComponentKey("Meldingsutveksleren"), "Topology merges the shared explicit alias");
        builder.ComponentKey("MU").Should().NotBe(builder.ComponentKey("Meldingsutveksleren"));
    }

    [Fact]
    public async Task Chains_are_partial_and_operations_resolve_from_observed_paths()
    {
        await using var db = Db();
        var service = Service(db);
        await service.PersistAsync("proj", Correlate(), [Src], [], "first", default);
        var ops = await service.OperationsAsync("proj", "get", "/api/absence/12345", default);
        var op = ops.Should().ContainSingle("an observed path is templated the way Endpoint Discovery templates it").Subject;
        op.Incoming.Should().Contain(r => r.Type == CanonicalRelationType.Exposes && r.Source.DisplayName == "SkoleService");
        op.Incoming.Should().Contain(r => r.Type == CanonicalRelationType.Implements && r.RuleId == CorrelationRules.SymbolRouteRule);

        var service2 = (await service.EntitiesAsync("proj", CanonicalEntityType.Service, "skole", 0, 5, default)).Items.Single();
        var chain = await service.ChainAsync("proj", service2.Id, null, default);
        chain!.Steps.Should().Contain(s => s.Type == CanonicalEntityType.ApiOperation && !s.Missing);
        chain.Steps.Should().Contain(s => s.Type == CanonicalEntityType.ContractVersion && s.Missing, "no contract evidence: reported missing, not invented");
    }

    [Fact]
    public async Task Unknown_scope_or_target_is_a_correctable_request_error()
    {
        await using var db = Db();
        var service = Service(db);
        await FluentActions.Awaiting(() => service.EntitiesAsync("../etc", null, null, 0, 10, default)).Should().ThrowAsync<CanonicalRequestException>();
        await FluentActions.Awaiting(() => service.DecideAsync("proj", new(ReviewTargetKind.Relation, "nope", ReviewDecisionOutcome.Confirmed), null, default))
            .Should().ThrowAsync<CanonicalRequestException>();
        await FluentActions.Awaiting(() => service.InterpretAsync("proj", new([Guid.NewGuid()]), default)).Should().ThrowAsync<CanonicalRequestException>();
    }

    [Fact]
    public void The_schema_is_self_contained()
    {
        var options = new DbContextOptionsBuilder<CanonicalInterpretationDbContext>();
        CanonicalInterpretationDbContext.Configure(options, "Host=localhost;Database=unused");
        using var db = new CanonicalInterpretationDbContext(options.Options);
        db.Model.GetEntityTypes().Should().OnlyContain(e => e.GetSchema() == CanonicalInterpretationDbContext.Schema);
        db.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).Should().OnlyContain(fk => fk.PrincipalEntityType.GetSchema() == CanonicalInterpretationDbContext.Schema);
    }
}
