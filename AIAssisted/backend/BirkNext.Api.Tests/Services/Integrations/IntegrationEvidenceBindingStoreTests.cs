using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// The canonical IQR source-evidence binding store over the real EF model (in-memory provider): it binds only configured integrations to
/// stored Source Analysis snapshots, keeps the bound identity, rebinds explicitly, unbinds, and is scoped per environment.
/// </summary>
public sealed class IntegrationEvidenceBindingStoreTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (IntegrationEvidenceBindingStore Store, AppDbContext Db, ActiveEventTestHarness.FakeCatalog Catalog) Create()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var catalog = new ActiveEventTestHarness.FakeCatalog();
        return (new IntegrationEvidenceBindingStore(db, new IqrSourceStore(db), catalog), db, catalog);
    }

    private static IqrSourceSnapshot Stored(AppDbContext db, string archive, DateTimeOffset analyzedAt)
    {
        var snapshot = new IqrSourceSnapshot { Id = Guid.NewGuid(), IntegrationId = IqrSourceStore.SourceAnalysisOwner, Archive = new(archive, new string('b', 64), 1), AnalyzedAt = analyzedAt };
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = snapshot.Id, IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = analyzedAt, EvidenceJson = JsonSerializer.Serialize(snapshot, Json) });
        db.SaveChanges();
        return snapshot;
    }

    [Fact]
    public async Task Binds_a_configured_integration_to_a_stored_snapshot_and_keeps_its_identity()
    {
        var (store, db, catalog) = Create();
        var snapshot = Stored(db, "a.zip", DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
        var (binding, error) = await store.SetAsync(ActiveEventTestHarness.Env, new(catalog.Integration.Id, snapshot.Id));
        error.Should().BeNull();
        binding!.SnapshotId.Should().Be(snapshot.Id);
        binding.Fingerprint.Should().Be(snapshot.Archive.Sha256);
        (await store.ListAsync(ActiveEventTestHarness.Env)).Should().ContainSingle().Which.ArchiveName.Should().Be("a.zip");
        (await store.ListAsync("another-env")).Should().BeEmpty("bindings are per Target Environment");
    }

    [Fact]
    public async Task A_newer_snapshot_never_replaces_a_binding_until_it_is_bound_explicitly()
    {
        var (store, db, catalog) = Create();
        var first = Stored(db, "first.zip", DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
        await store.SetAsync(ActiveEventTestHarness.Env, new(catalog.Integration.Id, first.Id));
        var second = Stored(db, "second.zip", DateTimeOffset.Parse("2026-10-02T10:00:00Z"));
        (await store.ListAsync(ActiveEventTestHarness.Env)).Single().SnapshotId.Should().Be(first.Id, "storing a newer snapshot rewrites nothing");

        await store.SetAsync(ActiveEventTestHarness.Env, new(catalog.Integration.Id, second.Id));
        (await store.ListAsync(ActiveEventTestHarness.Env)).Should().ContainSingle().Which.SnapshotId.Should().Be(second.Id);
        await store.SetAsync(ActiveEventTestHarness.Env, new(catalog.Integration.Id, null));
        (await store.ListAsync(ActiveEventTestHarness.Env)).Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_unknown_integrations_and_snapshots()
    {
        var (store, db, catalog) = Create();
        var snapshot = Stored(db, "a.zip", DateTimeOffset.UtcNow);
        (await store.SetAsync(ActiveEventTestHarness.Env, new("not-configured", snapshot.Id))).Error.Should().Contain("not configured");
        (await store.SetAsync(ActiveEventTestHarness.Env, new(catalog.Integration.Id, Guid.NewGuid()))).Error.Should().Contain("not found");
        (await store.ListAsync(ActiveEventTestHarness.Env)).Should().BeEmpty();
    }
}
