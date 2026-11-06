using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.TestData;
using BirkNext.DeliveryReporting;
using BirkNext.Evidence;
using BirkNext.Interpretation;
using BirkNext.PerformanceTests;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.Services.Persistence;

/// <summary>
/// Cross-context consistency hardening: serialised check-then-write (baseline promotion), idempotent retries (Delivery commit), file stores that
/// never report or keep what they could not persist, the shared evidence freshness rule, and the shared checklist metric.
/// </summary>
public sealed class ConsistencyHardeningTests
{
    // ── Baseline promotion race ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_baseline_promotions_leave_exactly_one_active_baseline_with_distinct_versions()
    {
        var name = $"pb-{Guid.NewGuid():N}";
        AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);
        var runs = new List<PerformanceTestRun>();
        await using (var seed = Db())
            for (var i = 0; i < 4; i++)
            {
                var run = new PerformanceTestRun { EnvironmentId = "env", DefinitionId = "def", ComparisonFingerprint = "cf", State = PerformanceRunState.Completed,
                    Metrics = new PerformanceMetrics { RequestCount = 10 }, CreatedAt = DateTimeOffset.UtcNow };
                await new PerformanceTestStore(seed).SaveRunAsync(run);
                runs.Add(run);
            }

        var contexts = runs.Select(_ => Db()).ToList();
        var results = await Task.WhenAll(runs.Select((r, i) => Task.Run(() =>
            new PerformanceTestStore(contexts[i]).PromoteAsync("env", new PerformanceBaselinePromotion { RunId = r.RunId }, DateTimeOffset.UtcNow))));
        results.Should().OnlyContain(r => r.Baseline != null, results.Select(r => r.Error).FirstOrDefault(e => e != null) ?? "");

        await using var check = Db();
        var rows = await check.PerformanceBaselines.AsNoTracking().Where(b => b.EnvironmentId == "env").ToListAsync();
        rows.Count(b => b.Status == nameof(PerformanceBaselineStatus.Active)).Should().Be(1, "promotion is serialised per scope");
        rows.Select(b => b.Version).Should().OnlyHaveUniqueItems();
        foreach (var c in contexts) await c.DisposeAsync();
    }

    // ── Delivery commit idempotency ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_retried_delivery_commit_returns_the_stored_snapshot_and_never_creates_a_second()
    {
        await using var db = new DeliveryReportingDbContext(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseInMemoryDatabase($"dci-{Guid.NewGuid():N}").Options);
        var store = new DeliverySnapshotStore(db, TimeProvider.System);
        var imports = new DeliveryReportingImportService(db, new DeliveryImportStagingStore(TimeProvider.System), TimeProvider.System);
        var project = (await store.CreateProjectAsync(new("M2LB", null), default)).Project!.Id;
        var preview = (await imports.PreviewAsync(project, "M2LB.zip", Zip(ChildSearch(8, 3)), default)).Preview!;
        var request = new DeliveryImportCommitRequest { ProjectId = project, EffectiveAt = Friday, Role = DeliverySnapshotRole.Current };

        var first = await imports.CommitAsync(preview.StagingId, request, default);
        var retry = await imports.CommitAsync(preview.StagingId, request, default);

        first.StatusCode.Should().Be(200);
        retry.StatusCode.Should().Be(200, "a lost response is retried safely");
        retry.Result.Snapshot!.Id.Should().Be(first.Result.Snapshot!.Id);
        (await db.Snapshots.CountAsync()).Should().Be(1);
        (await db.ReportSources.CountAsync()).Should().Be(1);
    }

    // ── File stores never keep or report what they could not persist ────────────────────────────────────────────────

    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "bn-arch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    private static JsonObject Fixture() => JsonNode.Parse("""
        {"testData":{"alias":"skolenaervaer-valid-report","intent":"positive","facts":{"capabilities":["skolenaervaer-report"],"validationStatus":"Valid"}}}
        """)!.AsObject();

    private static readonly List<TestDataCanonicalFixture> Canonical = [new("skolenaervaer", "skolenaervaer-valid-report", "skolenaervaer-report", "Valid report", "test")];

    [Fact]
    public void A_failed_fixture_write_creates_no_version_and_the_retry_gets_the_next_real_version()
    {
        var dir = TempDir();
        try
        {
            var store = new CanonicalFixtureStore(dir, NullLogger<CanonicalFixtureStore>.Instance);
            // The temporary file path is occupied by a directory: the write fails, as a full disk or a locked file would.
            Directory.CreateDirectory(Path.Combine(dir, "canonical-fixtures.json.tmp"));
            var (failed, error) = store.Add(Fixture(), Canonical, DateTimeOffset.UtcNow);
            failed.Should().BeNull();
            error.Should().Contain("could not be stored").And.Contain("no version was created");
            store.Versions().Should().BeEmpty("nothing that was not written is kept in memory");

            Directory.Delete(Path.Combine(dir, "canonical-fixtures.json.tmp"));
            store.Add(Fixture(), Canonical, DateTimeOffset.UtcNow).Version.Should().Be(1, "the failed attempt left no ghost version 1");
            new CanonicalFixtureStore(dir, NullLogger<CanonicalFixtureStore>.Instance).Versions().Should().Equal([("skolenaervaer-valid-report", 1)], "reload sees exactly what was written");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_approval_that_cannot_be_written_is_undone_and_reported()
    {
        var dir = TempDir();
        try
        {
            var registry = new TestDataRegistryStore(dir, NullLogger<TestDataRegistryStore>.Instance);
            registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = "a1" }).Should().BeTrue();
            Directory.CreateDirectory(Path.Combine(dir, "registry.json.tmp"));
            registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = "a1", ApprovedFingerprint = "fp", ApprovedAt = DateTimeOffset.UtcNow }).Should().BeFalse();
            registry.Lifecycle("a1")!.ApprovedFingerprint.Should().BeNull("an approval that was not persisted is not kept in memory either");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Deferred never clears a conflict or a stale decision ─────────────────────────────────────────────────────────

    private static ReviewDecisionFact Decision(ReviewDecisionOutcome outcome, string fingerprint) =>
        new(Guid.NewGuid(), ReviewTargetKind.Relation, "a->Verifies->b", outcome, fingerprint, "tester", DateTimeOffset.UtcNow, null, "proj");

    [Fact]
    public void Deferring_keeps_a_conflict_and_a_stale_confirmation_visible()
    {
        var rejected = Decision(ReviewDecisionOutcome.Rejected, "fp-1");
        var confirmed = Decision(ReviewDecisionOutcome.Confirmed, "fp-1");
        var deferred = Decision(ReviewDecisionOutcome.Deferred, "fp-2");

        CorrelationPrecedence.Apply(RelationResolution.Explicit, "fp-2", rejected).Review.Should().Be(RelationReviewState.Conflict);
        CorrelationPrecedence.Apply(RelationResolution.Explicit, "fp-2", deferred, rejected).Review.Should().Be(RelationReviewState.Conflict,
            "deferring postpones the question; the conflict and its blocker stay");
        CorrelationPrecedence.Apply(RelationResolution.Candidate, "fp-2", deferred, confirmed).Review.Should().Be(RelationReviewState.RevalidationRequired);
        CorrelationPrecedence.Apply(RelationResolution.Ambiguous, "fp-2", deferred).Review.Should().Be(RelationReviewState.Deferred, "nothing to keep: a plain deferral");
        CorrelationPrecedence.Apply(RelationResolution.Candidate, "fp-1", deferred, Decision(ReviewDecisionOutcome.Rejected, "fp-1")).Review
            .Should().Be(RelationReviewState.Deferred, "re-opening an unchallenged rejection is simply deferred");
        CorrelationPrecedence.Apply(RelationResolution.Candidate, "fp-1", deferred, Decision(ReviewDecisionOutcome.Rejected, "fp-0")).Review
            .Should().Be(RelationReviewState.RevalidationRequired, "a rejection made on evidence that has since changed stays a stale decision to recheck");
        CorrelationPrecedence.Counts(CorrelationPrecedence.Apply(RelationResolution.Candidate, "fp", deferred).Effective).Should().BeFalse("a deferred candidate is never coverage");
    }

    // ── Shared evidence freshness and delivery metric ────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_current_evidence_supports_a_positive_conclusion()
    {
        EvidenceFreshnessRules.Of("a", "a").Should().Be(EvidenceFreshness.Current);
        EvidenceFreshnessRules.Of("a", "b").Should().Be(EvidenceFreshness.Stale);
        EvidenceFreshnessRules.Of("a", "a", ownerHolds: false).Should().Be(EvidenceFreshness.Missing);
        EvidenceFreshnessRules.Of(null, "a").Should().Be(EvidenceFreshness.Unknown, "no recorded identity is never assumed current");
        new[] { EvidenceFreshness.Stale, EvidenceFreshness.Missing, EvidenceFreshness.Unknown }.Should().OnlyContain(f => !EvidenceFreshnessRules.CanSupportPositive(f));
    }

    [Fact]
    public void The_checklist_metric_is_one_definition_truncated_and_never_zero_for_no_tasks()
    {
        DeliveryMetrics.ChecklistPercent(0, 0).Should().BeNull("no checklist is not 0 %");
        DeliveryMetrics.ChecklistPercent(999, 1000).Should().Be(99, "never 100 % before every task is checked");
        DeliveryMetrics.ChecklistPercent(12, 14).Should().Be(85);
        DeliveryMetrics.ChecklistPercent(14, 14).Should().Be(100);
    }
}
