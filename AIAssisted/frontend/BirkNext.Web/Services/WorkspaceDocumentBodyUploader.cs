using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Prepares the lifecycle for a workspace save without sending every document body each time (root cause of the auto-save failure: the whole
/// lifecycle — about 5 MB of documents per M2LB import — was posted on every save until the request exceeded the server's 30 MB limit).
/// Each body is addressed by the SHA-256 of its UTF-8 bytes (cached per revision), the backend says which addresses it does not have, only
/// those are uploaded, in bounded batches, and the lifecycle is then written with addresses instead of bodies. The live lifecycle is never
/// changed. A failed step returns its response, so the caller reports a failed save instead of a saved one.
/// </summary>
public sealed class WorkspaceDocumentBodyUploader(HttpClient http)
{
    /// <summary>Characters per upload request (the backend accepts up to 8 000 000 per batch).</summary>
    public const int MaxBatchCharacters = 7_500_000;

    private readonly Dictionary<string, string> _addresses = new(StringComparer.Ordinal);

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>Bytes of bodies uploaded by the last <see cref="PrepareAsync"/> (diagnostics and tests).</summary>
    public long LastUploadedCharacters { get; private set; }

    public sealed record Prepared(string? Json, HttpResponseMessage? Failure, string? Error)
    {
        public bool Succeeded => Json is not null;
    }

    public async Task<Prepared> PrepareAsync(SddLifecycleState lifecycle, int? resetEpoch, CancellationToken ct = default)
    {
        LastUploadedCharacters = 0;
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var revisions = new List<SddArtifactRevision>(lifecycle.Revisions.Count);
        foreach (var revision in lifecycle.Revisions)
        {
            if (string.IsNullOrEmpty(revision.Content)) { revisions.Add(revision); continue; } // body not loaded: its address (or stored body) is used
            var key = $"{revision.RevisionId:N}:{revision.Fingerprint}:{revision.Content.Length}";
            if (!_addresses.TryGetValue(key, out var address)) _addresses[key] = address = Hash(revision.Content);
            bodies.TryAdd(address, revision.Content);
            revisions.Add(revision.WithoutBody(address));
        }
        if (bodies.Count > 0)
        {
            var missingResponse = await http.PostAsJsonAsync("api/workspace-persistence/bodies/missing", new { addresses = bodies.Keys.ToList() }, ct);
            if (!missingResponse.IsSuccessStatusCode) return new(null, missingResponse, $"Checking stored documents failed ({(int)missingResponse.StatusCode}).");
            var missing = (await missingResponse.Content.ReadFromJsonAsync<MissingResponse>(cancellationToken: ct))?.Missing ?? [];
            var batch = new List<object>(); long size = 0;
            async Task<HttpResponseMessage?> Flush()
            {
                if (batch.Count == 0) return null;
                var response = await http.PostAsJsonAsync("api/workspace-persistence/bodies", new { bodies = batch, resetEpoch }, ct);
                LastUploadedCharacters += size;
                batch = []; size = 0;
                return response.IsSuccessStatusCode ? null : response;
            }
            foreach (var address in missing)
            {
                if (!bodies.TryGetValue(address, out var content)) continue;
                if (content.Length > MaxBatchCharacters)
                    return new(null, null, $"A document of {content.Length:N0} characters is larger than one upload ({MaxBatchCharacters:N0}); the workspace was not saved.");
                if (size + content.Length > MaxBatchCharacters && await Flush() is { } failed) return new(null, failed, $"Uploading documents failed ({(int)failed.StatusCode}).");
                batch.Add(new { sha256 = address, content }); size += content.Length;
            }
            if (await Flush() is { } last) return new(null, last, $"Uploading documents failed ({(int)last.StatusCode}).");
        }
        return new(JsonSerializer.Serialize(lifecycle.WithRevisions(revisions)), null, null);
    }

    private sealed class MissingResponse
    {
        public List<string> Missing { get; set; } = [];
    }
}
