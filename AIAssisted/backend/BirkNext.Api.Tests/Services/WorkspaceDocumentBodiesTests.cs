using System.Text;
using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Workspace persistence of document bodies (root cause of the auto-save failure above ~30 MB: every save posted and stored the whole lifecycle
/// with every document of every import). Expected behaviour is written from the requirement: bodies once, by verified content address;
/// complete round trip; a save that cannot resolve a claimed body writes nothing; legacy inline rows stay readable.
/// </summary>
public sealed class WorkspaceDocumentBodiesTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() => _db.Dispose();
    private WorkspaceDocumentBodies Bodies => new(_db);

    private static string Lifecycle(params (Guid Id, string Fingerprint, string Content)[] revisions) => new JsonObject
    {
        ["SchemaVersion"] = 1,
        ["Revisions"] = new JsonArray([.. revisions.Select(r => (JsonNode)new JsonObject { ["RevisionId"] = r.Id.ToString(), ["Fingerprint"] = r.Fingerprint, ["Role"] = "Specification", ["Content"] = r.Content })]),
        ["CurrentProjectImportId"] = "import-1",
    }.ToJsonString();

    private static JsonArray Revisions(string json) => (JsonArray)JsonNode.Parse(json)!["Revisions"]!;

    [Fact]
    public async Task Bodies_are_stored_once_by_address_and_the_lifecycle_round_trips_exactly()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var spec = "# Spec\nFR-001 æøå — the same document in two imports\n";
        var incoming = Lifecycle((a, "fa", spec), (b, "fa", spec), (c, "fc", "# Plan\n"));

        var stored = await Bodies.PrepareForStorageAsync(incoming, null);

        Revisions(stored!).Should().OnlyContain(r => (string)r!["Content"]! == "" && ((string)r!["ContentSha256"]!).Length == 64, "the stored lifecycle keeps addresses, not bodies");
        _db.WorkspaceDocumentBodies.Count().Should().Be(2, "identical documents of several imports are one body");
        _db.WorkspaceDocumentBodies.Single(x => x.Sha256 == WorkspaceDocumentBodies.Hash(spec)).Content.Should().Be(spec);

        var restored = Revisions((await Bodies.WithBodiesAsync(stored))!);
        restored.Select(r => (string)r!["Content"]!).Should().Equal(spec, spec, "# Plan\n");
    }

    [Fact]
    public async Task A_manifest_save_resolves_bodies_from_the_store_and_an_unknown_address_writes_nothing()
    {
        var id = Guid.NewGuid();
        var stored = await Bodies.PrepareForStorageAsync(Lifecycle((id, "f", "body")), null);
        var address = (string)Revisions(stored!)[0]!["ContentSha256"]!;

        var manifest = new JsonObject { ["Revisions"] = new JsonArray(new JsonObject { ["RevisionId"] = id.ToString(), ["Fingerprint"] = "f", ["Content"] = "", ["ContentSha256"] = address, ["ContentLength"] = 4 }) }.ToJsonString();
        (await Bodies.PrepareForStorageAsync(manifest, stored)).Should().NotBeNull();

        var unknown = manifest.Replace(address, new string('a', 64));
        var act = () => Bodies.PrepareForStorageAsync(unknown, null);
        (await act.Should().ThrowAsync<WorkspaceDocumentBodiesMissingException>()).Which.Missing.Should().ContainSingle(new string('a', 64));
    }

    [Fact]
    public async Task A_claimed_body_that_nothing_can_provide_is_refused_never_stored_empty()
    {
        // Before the fix, FillMissing dropped ContentLength and the revision was stored with an empty body (silent loss).
        var claim = new JsonObject { ["Revisions"] = new JsonArray(new JsonObject { ["RevisionId"] = Guid.NewGuid().ToString(), ["Fingerprint"] = "f", ["Content"] = "", ["ContentLength"] = 120 }) }.ToJsonString();
        var act = () => Bodies.PrepareForStorageAsync(claim, Lifecycle((Guid.NewGuid(), "other", "x")));
        (await act.Should().ThrowAsync<WorkspaceDocumentBodiesMissingException>()).Which.Missing.Should().ContainSingle(m => m.StartsWith("revision:"));
        _db.WorkspaceDocumentBodies.Count().Should().Be(0, "nothing is written when the save is refused");
    }

    [Fact]
    public async Task A_legacy_row_with_inline_bodies_reads_unchanged_and_moves_into_the_store_on_the_next_save()
    {
        var id = Guid.NewGuid();
        var legacy = Lifecycle((id, "f", "legacy body"));
        (await Bodies.WithBodiesAsync(legacy)).Should().Be(legacy, "a legacy row needs nothing");

        var clientManifest = new JsonObject { ["Revisions"] = new JsonArray(new JsonObject { ["RevisionId"] = id.ToString(), ["Fingerprint"] = "f", ["Content"] = "", ["ContentLength"] = 11 }) }.ToJsonString();
        var stored = await Bodies.PrepareForStorageAsync(clientManifest, legacy);
        Revisions(stored!)[0]!["ContentSha256"]!.GetValue<string>().Should().Be(WorkspaceDocumentBodies.Hash("legacy body"));
        Revisions((await Bodies.WithBodiesAsync(stored))!)[0]!["Content"]!.GetValue<string>().Should().Be("legacy body");
    }

    [Fact]
    public async Task An_upload_under_a_wrong_address_refuses_the_whole_batch()
    {
        var act = () => Bodies.StoreAsync([new(WorkspaceDocumentBodies.Hash("good"), "good"), new(WorkspaceDocumentBodies.Hash("x"), "tampered")]);
        await act.Should().ThrowAsync<ArgumentException>();
        _db.WorkspaceDocumentBodies.Count().Should().Be(0);
        (await Bodies.StoreAsync([new(WorkspaceDocumentBodies.Hash("good"), "good")])).Should().Be(new WorkspaceDocumentBodyUploadResult(1, 0));
        (await Bodies.StoreAsync([new(WorkspaceDocumentBodies.Hash("good"), "good")])).Should().Be(new WorkspaceDocumentBodyUploadResult(0, 1), "a retried upload is idempotent");
    }

    [Theory] // Sizes around the diagnosed failure (the lifecycle hit Kestrel's 30 000 000-byte request limit at 7 large imports)
    [InlineData(5)]
    [InlineData(35)]
    [InlineData(75)]
    public async Task Large_workspaces_persist_in_bounded_batches_and_round_trip_byte_for_byte(int megabytes)
    {
        const int documentSize = 400_000;
        var count = megabytes * 1_000_000 / documentSize;
        var documents = Enumerable.Range(0, count).Select(i => (Id: Guid.NewGuid(), Fingerprint: $"f{i}", Content: Document(i, documentSize))).ToList();
        // The client uploads only missing bodies, at most MaxBatchCharacters per request.
        var missing = (await Bodies.MissingAsync(documents.Select(d => WorkspaceDocumentBodies.Hash(d.Content)).ToList())).ToHashSet();
        var batch = new List<WorkspaceDocumentBodyUpload>(); long size = 0; var requests = 0;
        foreach (var d in documents.Where(d => missing.Contains(WorkspaceDocumentBodies.Hash(d.Content))))
        {
            if (size + d.Content.Length > WorkspaceDocumentBodies.MaxBatchCharacters) { await Bodies.StoreAsync(batch); requests++; batch = []; size = 0; }
            batch.Add(new(WorkspaceDocumentBodies.Hash(d.Content), d.Content)); size += d.Content.Length;
        }
        if (batch.Count > 0) { await Bodies.StoreAsync(batch); requests++; }
        requests.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(megabytes / 8 + 1);

        var manifest = new JsonObject
        {
            ["Revisions"] = new JsonArray([.. documents.Select(d => (JsonNode)new JsonObject { ["RevisionId"] = d.Id.ToString(), ["Fingerprint"] = d.Fingerprint, ["Content"] = "",
                ["ContentSha256"] = WorkspaceDocumentBodies.Hash(d.Content), ["ContentLength"] = d.Content.Length })]),
        }.ToJsonString();
        manifest.Length.Should().BeLessThan(30_000_000 / 10, "the save request itself stays small however large the workspace is");

        var stored = await Bodies.PrepareForStorageAsync(manifest, null);
        stored!.Length.Should().BeLessThan(manifest.Length + 1000);
        var restored = Revisions((await Bodies.WithBodiesAsync(stored))!);
        restored.Select(r => WorkspaceDocumentBodies.Hash((string)r!["Content"]!)).Should().Equal(documents.Select(d => WorkspaceDocumentBodies.Hash(d.Content)), "no truncation, no reordering");
    }

    private static string Document(int seed, int size)
    {
        var sb = new StringBuilder(size);
        var line = $"Document {seed}: requirement text with æøå and \"quotes\"\n";
        while (sb.Length < size) sb.Append(line);
        return sb.ToString(0, size);
    }
}
