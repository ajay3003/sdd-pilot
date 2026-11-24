using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Tests.Services.AiCodeReview;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// The identity-only read of stored Source Analysis snapshots (Test Plan Suggestions performance): it materializes exactly the identity and
/// source-recognition members, equal to the full read, and skips the large evidence sections. The source context computed from it equals the
/// context computed from the full read.
/// </summary>
public sealed class SourceIdentityReadTests
{
    [Fact]
    public async Task The_identity_read_equals_the_full_read_for_every_identity_member_and_skips_the_large_sections()
    {
        await using var db = AiCodeReviewFixture.Db();
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop-v2.zip", AiCodeReviewFixture.Current());
        var store = new IqrSourceStore(db);
        var full = await store.ListSourceAnalysisAsync(500);
        var light = await store.ListSourceAnalysisIdentitiesAsync(500);

        light.Select(s => s.Id).Should().Equal(full.Select(s => s.Id), "same rows, same order");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        for (var i = 0; i < full.Count; i++)
        {
            var (f, l) = (JsonSerializer.SerializeToElement(full[i], web), JsonSerializer.SerializeToElement(light[i], web));
            foreach (var member in IqrSourceStore.IdentityMembers)
                l.GetProperty(member).GetRawText().Should().Be(f.GetProperty(member).GetRawText(), $"{member} is an identity member");
        }
        full.Should().Contain(s => s.TestInventory != null || s.CodeRiskEvidence != null, "the fixture has large sections to skip");
        light.Should().OnlyContain(s => s.TestInventory == null && s.CodeRiskEvidence == null && s.DatabaseArchitecture == null && s.Tests.Count == 0);
    }

    [Fact]
    public async Task The_source_context_from_the_identity_read_equals_the_context_from_the_full_read()
    {
        await using var db = AiCodeReviewFixture.Db();
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop-v2.zip", AiCodeReviewFixture.Current());
        var store = new IqrSourceStore(db);
        var service = new SourceBaselineService(db, store);
        var fromLight = await service.ContextAsync(default);
        var fromFull = await service.ContextAsync(await store.ListSourceAnalysisAsync(SourceBaselineService.Window), default);
        JsonSerializer.Serialize(fromLight).Should().Be(JsonSerializer.Serialize(fromFull));
        fromLight.Sources.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_cached_identity_read_equals_the_uncached_read()
    {
        await using var db = AiCodeReviewFixture.Db();
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop-v2.zip", AiCodeReviewFixture.Current());
        var uncached = await new IqrSourceStore(db).ListSourceAnalysisIdentitiesAsync(500);
        var cache = new SourceIdentityCache();
        var store = new IqrSourceStore(db, null, cache);

        var first = await store.ListSourceAnalysisIdentitiesAsync(500);
        var second = await store.ListSourceAnalysisIdentitiesAsync(500);

        JsonSerializer.Serialize(first).Should().Be(JsonSerializer.Serialize(uncached));
        JsonSerializer.Serialize(second).Should().Be(JsonSerializer.Serialize(uncached));
        cache.Count.Should().Be(2);
        (await store.ListSourceAnalysisIdentitiesAsync(1)).Select(s => s.Id).Should().Equal(uncached[0].Id);
    }

    /// <summary>The 28 s technology-coverage read after a reload: every identity read used to transfer and parse every stored evidence document.
    /// Known snapshots are not read again: a stored document that could no longer be parsed is never touched by a cached read.</summary>
    [Fact]
    public async Task A_known_snapshot_is_not_read_again()
    {
        await using var db = AiCodeReviewFixture.Db();
        var known = await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
        var cache = new SourceIdentityCache();
        var store = new IqrSourceStore(db, null, cache);
        await store.ListSourceAnalysisIdentitiesAsync(500);

        var row = await db.IqrSourceSnapshots.SingleAsync(r => r.Id == known.Id);
        db.IqrSourceSnapshots.Remove(row);
        await db.SaveChangesAsync();
        db.IqrSourceSnapshots.Add(new BirkNext.Api.Models.IqrSourceSnapshotRecord { Id = row.Id, EnvironmentId = row.EnvironmentId,
            IntegrationId = row.IntegrationId, AnalyzedAt = row.AnalyzedAt, EvidenceJson = "not json" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await store.ListSourceAnalysisIdentitiesAsync(500)).Select(s => s.Id).Should().Equal(known.Id);
        await FluentActions.Awaiting(() => new IqrSourceStore(db).ListSourceAnalysisIdentitiesAsync(500)).Should().ThrowAsync<JsonException>("the uncached read parses every document");
    }

    [Fact]
    public async Task The_cached_read_lists_new_snapshots_and_drops_deleted_ones()
    {
        await using var db = AiCodeReviewFixture.Db();
        var first = await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
        var store = new IqrSourceStore(db, null, new SourceIdentityCache());
        (await store.ListSourceAnalysisIdentitiesAsync(500)).Select(s => s.Id).Should().Equal(first.Id);

        var second = await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop-v2.zip", AiCodeReviewFixture.Current());
        (await store.ListSourceAnalysisIdentitiesAsync(500)).Select(s => s.Id).Should().Equal(second.Id, first.Id);

        db.IqrSourceSnapshots.Remove(await db.IqrSourceSnapshots.SingleAsync(r => r.Id == first.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await store.ListSourceAnalysisIdentitiesAsync(500)).Select(s => s.Id).Should().Equal(second.Id);
    }

    [Fact]
    public void The_cache_is_bounded_by_the_size_of_what_it_holds_and_the_oldest_entries_leave_first()
    {
        var cache = new SourceIdentityCache(budgetBytes: 1000);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids) cache.Add(id, DateTimeOffset.UnixEpoch, new BirkNext.Integrations.IqrSourceSnapshot { Id = id }, 300);

        cache.Bytes.Should().BeLessThanOrEqualTo(1000);
        cache.Count.Should().Be(3);
        cache.TryGet(ids[0], DateTimeOffset.UnixEpoch, out _).Should().BeFalse("the oldest entries leave first");
        cache.TryGet(ids[4], DateTimeOffset.UnixEpoch, out _).Should().BeTrue();

        cache.Add(Guid.NewGuid(), DateTimeOffset.UnixEpoch, new BirkNext.Integrations.IqrSourceSnapshot(), 5000);
        cache.Count.Should().Be(3, "a projection larger than the whole budget is not cached (and evicts nothing)");
    }

    [Fact]
    public void A_snapshot_read_again_with_another_analysis_time_is_a_miss()
    {
        var cache = new SourceIdentityCache();
        var id = Guid.NewGuid();
        cache.Add(id, DateTimeOffset.UnixEpoch, new BirkNext.Integrations.IqrSourceSnapshot { Id = id }, 10);

        cache.TryGet(id, DateTimeOffset.UnixEpoch.AddSeconds(1), out _).Should().BeFalse();
        cache.Add(id, DateTimeOffset.UnixEpoch.AddSeconds(1), new BirkNext.Integrations.IqrSourceSnapshot { Id = id }, 10);
        cache.Count.Should().Be(1, "one entry per snapshot id");
        cache.Bytes.Should().Be(10);
    }

    [Fact]
    public async Task Deleted_snapshots_leave_the_cache_when_a_listing_returns_every_stored_row()
    {
        await using var db = AiCodeReviewFixture.Db();
        var first = await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
        var second = await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop-v2.zip", AiCodeReviewFixture.Current());
        var cache = new SourceIdentityCache();
        var store = new IqrSourceStore(db, null, cache);
        await store.ListSourceAnalysisIdentitiesAsync(500);
        cache.Count.Should().Be(2);

        db.IqrSourceSnapshots.Remove(await db.IqrSourceSnapshots.SingleAsync(r => r.Id == first.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await store.ListSourceAnalysisIdentitiesAsync(1);
        cache.Count.Should().Be(2, "a partial listing (take reached) says nothing about the rows it did not return");
        await store.ListSourceAnalysisIdentitiesAsync(500);
        cache.Count.Should().Be(1);
        cache.TryGet(second.Id, second.AnalyzedAt, out _).Should().BeTrue();
    }

    /// <summary>Concurrent requests (one store and DbContext per request, one shared cache) all get the full, correct listing.</summary>
    [Fact]
    public async Task Concurrent_requests_share_the_cache_safely()
    {
        var name = Guid.NewGuid().ToString();
        BirkNext.Api.Data.AppDbContext Db() => new(new DbContextOptionsBuilder<BirkNext.Api.Data.AppDbContext>().UseInMemoryDatabase(name).Options);
        await using (var seed = Db())
        {
            await AiCodeReviewFixture.AnalyzeAsync(seed, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
            await AiCodeReviewFixture.AnalyzeAsync(seed, "contoso-shop-v2.zip", AiCodeReviewFixture.Current());
        }
        IReadOnlyList<Guid> expected;
        await using (var reference = Db()) expected = [.. (await new IqrSourceStore(reference).ListSourceAnalysisIdentitiesAsync(500)).Select(s => s.Id)];
        var cache = new SourceIdentityCache();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await using var db = Db();
            return (await new IqrSourceStore(db, null, cache).ListSourceAnalysisIdentitiesAsync(500)).Select(s => s.Id).ToList();
        })));

        results.Should().OnlyContain(ids => ids.SequenceEqual(expected));
        cache.Count.Should().Be(2);
    }
}
