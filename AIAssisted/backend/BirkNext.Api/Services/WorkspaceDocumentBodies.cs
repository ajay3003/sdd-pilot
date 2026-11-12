using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>A save that references document bodies this installation does not have. Nothing was written.</summary>
public sealed class WorkspaceDocumentBodiesMissingException(IReadOnlyList<string> missing)
    : InvalidOperationException($"{missing.Count} document bod{(missing.Count == 1 ? "y is" : "ies are")} missing; the workspace was not saved. Upload the missing bodies and save again.")
{
    public IReadOnlyList<string> Missing { get; } = missing;
}

/// <summary>One body upload: the client's content address and the text it addresses.</summary>
public sealed record WorkspaceDocumentBodyUpload(string Sha256, string Content);

/// <summary>Result of an upload batch: how many bodies were new, how many were already stored.</summary>
public sealed record WorkspaceDocumentBodyUploadResult(int Stored, int AlreadyStored);

/// <summary>
/// Content-addressed document bodies of saved workspaces, and the lifecycle conversion around them (the workspace persistence root cause:
/// the whole lifecycle — every document of every import — was posted and stored on every save, until a save exceeded the request limit).
/// <list type="bullet">
/// <item><b>Upload</b>: bodies arrive once each, in bounded batches, addressed by the SHA-256 of their UTF-8 bytes; the address is verified
/// here, so a body can never be stored under a wrong key. Already stored bodies are skipped (identical documents of several imports are one).</item>
/// <item><b>Write</b> (<see cref="PrepareForStorageAsync"/>): a revision's body comes from the request, else from the stored lifecycle (legacy
/// inline bodies), else from the store by its address. Every revision that claims a body must resolve, otherwise nothing is written
/// (<see cref="WorkspaceDocumentBodiesMissingException"/>). The stored lifecycle keeps addresses, not bodies.</item>
/// <item><b>Read</b> (<see cref="WithBodiesAsync"/>): bodies are put back, so every reader (load, bootstrap, documents, export) sees the same
/// complete lifecycle as before. Legacy rows with inline bodies read unchanged.</item>
/// </list>
/// </summary>
public sealed class WorkspaceDocumentBodies(AppDbContext db)
{
    public const string ContentShaKey = "ContentSha256";
    /// <summary>Upper bound of one upload batch in characters (the request limit of the endpoint is larger, with room for JSON escaping).</summary>
    public const int MaxBatchCharacters = 8_000_000;

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static bool IsAddress(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<IReadOnlyList<string>> MissingAsync(IEnumerable<string> addresses, CancellationToken ct = default)
    {
        var wanted = addresses.Where(IsAddress).Distinct(StringComparer.Ordinal).ToList();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(1000))
            present.UnionWith(await db.WorkspaceDocumentBodies.AsNoTracking().Where(b => chunk.Contains(b.Sha256)).Select(b => b.Sha256).ToListAsync(ct));
        return [.. wanted.Where(a => !present.Contains(a))];
    }

    /// <summary>Stores a batch atomically. Every address is verified against its content; one wrong address refuses the whole batch.</summary>
    public async Task<WorkspaceDocumentBodyUploadResult> StoreAsync(IReadOnlyList<WorkspaceDocumentBodyUpload> uploads, CancellationToken ct = default)
    {
        if (uploads.Sum(u => (long)(u.Content?.Length ?? 0)) > MaxBatchCharacters)
            throw new ArgumentException($"An upload batch may hold at most {MaxBatchCharacters:N0} characters; split it.");
        foreach (var upload in uploads)
            if (!IsAddress(upload.Sha256) || upload.Content is null || Hash(upload.Content) != upload.Sha256)
                throw new ArgumentException($"Body {upload.Sha256} does not match its content address; nothing was stored.");
        var distinct = uploads.GroupBy(u => u.Sha256, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var missing = (await MissingAsync(distinct.Select(u => u.Sha256), ct)).ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0) return new(0, distinct.Count);
        var now = DateTimeOffset.UtcNow;
        db.WorkspaceDocumentBodies.AddRange(distinct.Where(u => missing.Contains(u.Sha256))
            .Select(u => new WorkspaceDocumentBodyRecord { Sha256 = u.Sha256, Content = u.Content, Length = u.Content.Length, CreatedAt = now }));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A concurrent upload stored some of the same bodies first: content addresses are identical bodies, so that is success.
            db.ChangeTracker.Clear();
            var stillMissing = await MissingAsync(missing, ct);
            if (stillMissing.Count > 0) throw;
        }
        return new(missing.Count, distinct.Count - missing.Count);
    }

    /// <summary>
    /// The lifecycle as it is stored: bodies resolved (request, stored lifecycle, store), every body moved into the store, addresses kept.
    /// Throws <see cref="WorkspaceDocumentBodiesMissingException"/> before anything is written when a claimed body cannot be resolved.
    /// Unreadable or non-object JSON is returned unchanged (it is opaque frontend state).
    /// </summary>
    public async Task<string?> PrepareForStorageAsync(string? incoming, string? stored, CancellationToken ct = default)
    {
        var filled = WorkspaceLifecycleDocuments.FillMissing(incoming, stored);
        if (Parse(filled) is not { } root || root[WorkspaceLifecycleDocuments.RevisionsKey] is not JsonArray revisions) return filled;
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        foreach (var revision in revisions.OfType<JsonObject>())
        {
            var content = Text(revision, WorkspaceLifecycleDocuments.ContentKey);
            if (!string.IsNullOrEmpty(content))
            {
                var address = Hash(content);
                bodies.TryAdd(address, content);
                revision[ContentShaKey] = address;
                revision[WorkspaceLifecycleDocuments.ContentKey] = "";
            }
            else if (Text(revision, ContentShaKey) is { } claimed)
            {
                if (IsAddress(claimed)) referenced.Add(claimed); else unresolved.Add(Text(revision, WorkspaceLifecycleDocuments.RevisionIdKey) ?? "?");
            }
            else if (revision[WorkspaceLifecycleDocuments.ContentLengthKey] is JsonValue length && length.TryGetValue<int>(out var claimedLength) && claimedLength > 0)
                unresolved.Add(Text(revision, WorkspaceLifecycleDocuments.RevisionIdKey) ?? "?"); // a body was claimed that nothing can provide
            revision.Remove(WorkspaceLifecycleDocuments.ContentLengthKey);
        }
        var missing = (await MissingAsync(referenced.Where(a => !bodies.ContainsKey(a)), ct)).ToList();
        if (missing.Count > 0 || unresolved.Count > 0) throw new WorkspaceDocumentBodiesMissingException([.. missing, .. unresolved.Select(id => $"revision:{id}")]);
        // Bodies that arrived inline (legacy clients, legacy stored rows) move into the store, in bounded batches.
        var batch = new List<WorkspaceDocumentBodyUpload>(); long size = 0;
        foreach (var (address, content) in bodies)
        {
            if (size + content.Length > MaxBatchCharacters && batch.Count > 0) { await StoreAsync(batch, ct); batch = []; size = 0; }
            batch.Add(new(address, content)); size += content.Length;
        }
        if (batch.Count > 0) await StoreAsync(batch, ct);
        return root.ToJsonString();
    }

    /// <summary>The lifecycle with every addressed body put back (legacy inline bodies are left as they are).</summary>
    public async Task<string?> WithBodiesAsync(string? json, CancellationToken ct = default)
    {
        if (Parse(json) is not { } root || root[WorkspaceLifecycleDocuments.RevisionsKey] is not JsonArray revisions) return json;
        var wanted = revisions.OfType<JsonObject>()
            .Where(r => string.IsNullOrEmpty(Text(r, WorkspaceLifecycleDocuments.ContentKey)) && IsAddress(Text(r, ContentShaKey)))
            .Select(r => Text(r, ContentShaKey)!).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return json;
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(500))
            foreach (var b in await db.WorkspaceDocumentBodies.AsNoTracking().Where(b => chunk.Contains(b.Sha256)).Select(b => new { b.Sha256, b.Content }).ToListAsync(ct))
                bodies[b.Sha256] = b.Content;
        foreach (var revision in revisions.OfType<JsonObject>())
            if (string.IsNullOrEmpty(Text(revision, WorkspaceLifecycleDocuments.ContentKey)) && Text(revision, ContentShaKey) is { } address && bodies.TryGetValue(address, out var content))
                revision[WorkspaceLifecycleDocuments.ContentKey] = content;
        return root.ToJsonString();
    }

    private static string? Text(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
