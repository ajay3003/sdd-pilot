using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Governance;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// NEXT49 on the M2LB-language case: the latest relationship analysis (run from Relationship Review) held only Source Analysis snapshots, so the
/// release scope had 0 feature packs — and Requirements read "Ready with limitations" and Traceability "Ready" over nothing. An empty scope is now
/// "Release scope not resolved" with the reason, and nothing to assess is never Ready.
/// </summary>
public sealed class ReleaseScopeResolutionTests
{
    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    static async Task<CanonicalInterpretationService> M2lbLanguage(bool earlierWithDelivery)
    {
        var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"scope-{Guid.NewGuid():N}").Options);
        var sources = JsonSerializer.Serialize(new[] { new SnapshotReference(SnapshotType.SourceAnalysis, Guid.NewGuid().ToString(), "M2LB-language.zip") }, Web);
        if (earlierWithDelivery)
            db.Snapshots.Add(new InterpretationSnapshotRecord { Id = Guid.NewGuid(), Scope = "proj", CreatedAt = new(2026, 11, 4, 14, 29, 0, TimeSpan.Zero), SourceSnapshotsJson =
                JsonSerializer.Serialize(new[] { new SnapshotReference(SnapshotType.SourceAnalysis, Guid.NewGuid().ToString()), new SnapshotReference(SnapshotType.Delivery, Guid.NewGuid().ToString(), "M2LB-language 4 Nov") }, Web) });
        var latest = Guid.NewGuid();
        db.Snapshots.Add(new InterpretationSnapshotRecord { Id = latest, Scope = "proj", CreatedAt = new(2026, 11, 5, 14, 37, 0, TimeSpan.Zero), SourceSnapshotsJson = sources });
        var system = Guid.NewGuid();
        db.Entities.Add(new CanonicalEntityRecord { Id = system, Scope = "proj", EntityType = "System", IdentityKey = "utdanning", DisplayName = "Utdanning", AttributesJson = "{}" });
        db.SnapshotEntities.Add(new InterpretationSnapshotEntityRecord { SnapshotId = latest, EntityId = system, DisplayName = "Utdanning" });
        await db.SaveChangesAsync();
        return new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System);
    }

    [Fact]
    public async Task An_analysis_without_a_Delivery_snapshot_is_an_unresolved_scope_with_its_reason()
    {
        var service = await M2lbLanguage(earlierWithDelivery: true);
        var assessment = await service.AssessReleaseAsync("proj", null, null, default);
        var scope = assessment.Dimensions.Single(d => d.Key == ReadinessDimensionKey.ScopeClarity);

        scope.State.Should().Be(GateDecision.NotAssessed);
        scope.Summary.Should().StartWith("Release scope not resolved:").And.Contain("2026-11-05 14:37 UTC) includes no Delivery snapshot")
            .And.Contain("the analysis of 2026-11-04 14:29 UTC did");
        scope.NextAction.Should().Contain("Delivery snapshot included");
    }

    [Fact]
    public async Task Without_any_Delivery_analysis_the_reason_says_feature_packs_come_from_one()
    {
        var service = await M2lbLanguage(earlierWithDelivery: false);
        var scope = (await service.AssessReleaseAsync("proj", null, null, default)).Dimensions.Single(d => d.Key == ReadinessDimensionKey.ScopeClarity);
        scope.Summary.Should().Contain("feature packs come only from an interpreted Delivery snapshot").And.NotContain("did");
    }

    [Fact]
    public async Task Nothing_in_scope_is_never_ready_requirements_traceability_and_test_design_are_not_assessed()
    {
        var service = await M2lbLanguage(earlierWithDelivery: true);
        var assessment = await service.AssessReleaseAsync("proj", null, null, default);
        var dims = assessment.Dimensions.ToDictionary(d => d.Key);

        dims[ReadinessDimensionKey.Requirements].State.Should().Be(GateDecision.NotAssessed, "no requirement is in an unresolved scope — was 'Ready with limitations'");
        dims[ReadinessDimensionKey.Requirements].Summary.Should().Contain("release scope is not resolved");
        dims[ReadinessDimensionKey.Traceability].State.Should().Be(GateDecision.NotAssessed, "zero tasks is not full traceability — was 'Ready'");
        dims[ReadinessDimensionKey.TestDesign].State.Should().Be(GateDecision.NotAssessed);
        assessment.Decision.Should().Be(GateDecision.NotAssessed, "an unresolved scope is never Ready (no governance block in this fixture)");
        assessment.Dimensions.Should().NotContain(d => d.Applicability == DimensionApplicability.Required && d.State == GateDecision.Ready && d.Key != ReadinessDimensionKey.Governance
            && d.Key != ReadinessDimensionKey.OpenDecisions, "only governance and decisions can be assessed without a scope");
    }
}
