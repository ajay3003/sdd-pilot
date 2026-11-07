using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using BirkNext.Governance;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// The whole Delivery → Release Readiness chain through the real services and stores: a Spec-Kit ZIP imported through Delivery Reporting's preview
/// and commit, read back by a fresh context from the delivery store (the authority for work items), interpreted into a canonical snapshot that
/// references it, assessed, frozen; then a newer import and re-interpretation. The live assessment follows the newest interpretation; the frozen
/// report keeps exactly what it assessed. Each store is its own context, as in the application (no shared transaction).
/// </summary>
public sealed class ReleaseReadinessDeliveryChainTests
{
    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    const string Scope = "g7-release-chain";
    readonly string _deliveryDb = $"chain-dr-{Guid.NewGuid():N}", _canonicalDb = $"chain-ci-{Guid.NewGuid():N}";
    DeliveryReportingDbContext DeliveryDb() => new(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseInMemoryDatabase(_deliveryDb).Options);
    CanonicalInterpretationDbContext CanonicalDb() => new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase(_canonicalDb).Options);

    static ReadinessDimension Dim(ReleaseReadinessAssessment a, ReadinessDimensionKey key) => a.Dimensions.Single(d => d.Key == key);

    [Fact]
    public async Task Release_readiness_reads_the_interpreted_stored_delivery_snapshot_and_a_frozen_report_keeps_what_it_assessed()
    {
        var clock = new Clock(Monday);
        Guid projectId, baselineId, currentId;
        await using (var delivery = DeliveryDb())
        {
            var store = new DeliverySnapshotStore(delivery, clock);
            var imports = new DeliveryReportingImportService(delivery, new DeliveryImportStagingStore(TimeProvider.System), clock);
            projectId = (await store.CreateProjectAsync(new("G7 synthetic release", null), default)).Project!.Id;
            async Task<Guid> Import(byte[] zip, DateTimeOffset effectiveAt, DeliverySnapshotRole role)
            {
                var preview = (await imports.PreviewAsync(projectId, "g7-release.zip", zip, default)).Preview!;
                var outcome = await imports.CommitAsync(preview.StagingId, new DeliveryImportCommitRequest { ProjectId = projectId, EffectiveAt = effectiveAt, Role = role }, default);
                outcome.StatusCode.Should().BeInRange(200, 299, outcome.Result.Error);
                return outcome.Result.Snapshot!.Id;
            }
            baselineId = await Import(Zip(ChildSearch(8, 3)), Monday, DeliverySnapshotRole.Baseline);
            clock.Now = Friday;
            // The newer export gains a second feature in another module.
            currentId = await Import(Zip([.. ChildSearch(8, 7), .. ChildSearch(4, 1, "Backend")]), Friday, DeliverySnapshotRole.Current);
        }

        // Interpretation reads the work items from the delivery store through a fresh context: what was persisted, not what was imported in memory.
        await using var deliveryRead = DeliveryDb();
        await using var canonical = CanonicalDb();
        var service = new CanonicalInterpretationService(canonical, new NoSources(), clock, new DeliveryCanonicalWorkItemReader(deliveryRead));

        (await service.AssessReleaseAsync(Scope, null, null, default)).Decision.Should().Be(GateDecision.NotAssessed, "nothing is interpreted yet");

        var first = await service.InterpretAsync(Scope, new InterpretRequest([], baselineId), default);
        first.SourceSnapshots.Should().ContainSingle(r => r.Type == SnapshotType.Delivery && r.Id == baselineId.ToString(), "the interpretation records which stored snapshot it read");
        var before = await service.AssessReleaseAsync(Scope, null, null, default);
        before.ScopeReference.InterpretationId.Should().Be(first.Id);
        Dim(before, ReadinessDimensionKey.ScopeClarity).State.Should().Be(GateDecision.Ready);
        Dim(before, ReadinessDimensionKey.ScopeClarity).Evidence.Should().Equal("Child search");
        Dim(before, ReadinessDimensionKey.Requirements).State.Should().Be(GateDecision.Ready, "FR-001 is in the stored snapshot");
        Dim(before, ReadinessDimensionKey.FunctionalExecution).State.Should().Be(GateDecision.NotAssessed, "a linked or checked task is never an executed test");
        Dim(before, ReadinessDimensionKey.TestDesign).State.Should().NotBe(GateDecision.Ready, "no test covers FR-001; nothing confirmed it");
        before.Decision.Should().NotBe(GateDecision.Ready, "a stored Delivery snapshot alone never makes a release ready");

        var report = await service.FreezeReleaseAsync(Scope, new FreezeReleaseReadinessRequest("Baseline release", null), default);
        var frozenJson = JsonSerializer.Serialize((await service.ReleaseReportAsync(Scope, report.Id, default))!.Assessment);
        var storedJson = (await canonical.ReleaseReports.AsNoTracking().SingleAsync()).AssessmentJson;

        // The newer stored snapshot, interpreted later: the live assessment follows it ("latest" is the newest interpretation time).
        clock.Now = Friday.AddMinutes(30);
        var second = await service.InterpretAsync(Scope, new InterpretRequest([], currentId), default);
        second.Id.Should().NotBe(first.Id);
        second.SourceSnapshots.Should().ContainSingle(r => r.Type == SnapshotType.Delivery && r.Id == currentId.ToString());
        var after = await service.AssessReleaseAsync(Scope, null, null, default);
        after.ScopeReference.InterpretationId.Should().Be(second.Id);
        Dim(after, ReadinessDimensionKey.ScopeClarity).Evidence.Should().HaveCount(2, "the newer snapshot's second feature is in scope");
        after.Decision.Should().NotBe(GateDecision.Ready);

        // The frozen report is unchanged: same interpretation, same evidence, same stored bytes.
        await using var canonicalRead = CanonicalDb();
        var reread = await new CanonicalInterpretationService(canonicalRead, new NoSources(), clock).ReleaseReportAsync(Scope, report.Id, default);
        reread!.Assessment.ScopeReference.InterpretationId.Should().Be(first.Id);
        Dim(reread.Assessment, ReadinessDimensionKey.ScopeClarity).Evidence.Should().Equal("Child search");
        JsonSerializer.Serialize(reread.Assessment).Should().Be(frozenJson);
        (await canonicalRead.ReleaseReports.AsNoTracking().SingleAsync()).AssessmentJson.Should().Be(storedJson);
        (await service.ReleaseReportsAsync(Scope, 10, default)).Should().ContainSingle();

        // The first interpretation is kept as history, not rewritten.
        (await canonicalRead.Snapshots.AsNoTracking().SingleAsync(s => s.Id == first.Id)).SourceSnapshotsJson.Should().Contain(baselineId.ToString());
    }

    [Fact]
    public async Task Interpreting_a_delivery_snapshot_that_is_not_stored_fails_and_records_nothing()
    {
        await using var delivery = DeliveryDb();
        await using var canonical = CanonicalDb();
        var service = new CanonicalInterpretationService(canonical, new NoSources(), TimeProvider.System, new DeliveryCanonicalWorkItemReader(delivery));
        var missing = Guid.NewGuid();

        var act = () => service.InterpretAsync(Scope, new InterpretRequest([], missing), default);
        await act.Should().ThrowAsync<CanonicalRequestException>().WithMessage($"*{missing}*not stored*");
        (await canonical.Snapshots.CountAsync()).Should().Be(0, "no interpretation points at a snapshot that does not exist");
        (await service.AssessReleaseAsync(Scope, null, null, default)).Decision.Should().Be(GateDecision.NotAssessed);
    }
}
