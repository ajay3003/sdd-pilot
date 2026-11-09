using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BirkNext.BlobStorage;
using Path = System.IO.Path;
using BlobProperties = BirkNext.BlobStorage.BlobProperties;

namespace BirkNext.Api.Services.BlobStorage;

/// <summary>Configured Blob stores. Nothing is configured by default; the synthetic local kind is accepted only in Development.</summary>
public sealed class BlobStorageOptions
{
    public const string Section = "BlobStorage";
    public List<BlobStoreOptions> Stores { get; set; } = [];
}

public sealed class BlobStoreOptions
{
    public string Id { get; set; } = "";
    public string EnvironmentId { get; set; } = "";
    public string Name { get; set; } = "";
    public BlobStoreKind Kind { get; set; } = BlobStoreKind.AzureStorageAccount;
    /// <summary>Azure: https://{account}.blob.core.windows.net (identity-based; no key, no connection string, no SAS).</summary>
    public string? AccountUrl { get; set; }
    /// <summary>SyntheticLocal: a folder whose sub-folders are containers. Development only.</summary>
    public string? Root { get; set; }
    /// <summary>Only these containers can be opened (empty = all the identity can list).</summary>
    public List<string> Containers { get; set; } = [];
}

/// <summary>Read-only access to one store. There is deliberately no write, delete, lease, snapshot, tier, metadata-set or SAS method.</summary>
public interface IBlobReadStore
{
    Task<List<BlobContainerView>> ContainersAsync(CancellationToken ct);
    Task<(List<BlobItemView> Items, string? Continuation)> ListAsync(string container, string? prefix, string? continuation, int pageSize, CancellationToken ct, bool flat = false);
    Task<BlobProperties?> PropertiesAsync(string container, string name, CancellationToken ct);
    /// <summary>At most <paramref name="maxBytes"/> from the start; Complete = the whole blob was read.</summary>
    Task<(byte[] Bytes, bool Complete)> ReadAsync(string container, string name, int maxBytes, CancellationToken ct);
}

public sealed class BlobStoreAccessException(string message) : Exception(message);

/// <summary>Azure Storage account through the SDK with a token credential (Storage Blob Data Reader is enough). Read operations only.</summary>
public sealed class AzureBlobReadStore(Uri accountUrl, TokenCredential credential) : IBlobReadStore
{
    private readonly BlobServiceClient _service = new(accountUrl, credential, new BlobClientOptions { Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(30) } });

    public async Task<List<BlobContainerView>> ContainersAsync(CancellationToken ct)
    {
        var result = new List<BlobContainerView>();
        try { await foreach (var c in _service.GetBlobContainersAsync(cancellationToken: ct)) { result.Add(new(c.Name, c.Properties.LastModified)); if (result.Count >= 1000) break; } }
        catch (RequestFailedException ex) { throw Denied(ex); }
        return result;
    }

    public async Task<(List<BlobItemView> Items, string? Continuation)> ListAsync(string container, string? prefix, string? continuation, int pageSize, CancellationToken ct, bool flat = false)
    {
        try
        {
            var client = _service.GetBlobContainerClient(container);
            if (flat)
            {
                // Every blob below the prefix, at any depth (expectation search); browsing uses the one-level hierarchy below.
                await foreach (var flatPage in client.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct).AsPages(continuation, pageSize))
                    return ([.. flatPage.Values.Select(b => new BlobItemView
                    {
                        Name = b.Name, Size = b.Properties.ContentLength, ContentType = b.Properties.ContentType, LastModified = b.Properties.LastModified,
                        ETag = b.Properties.ETag?.ToString(), BlobType = b.Properties.BlobType?.ToString(), AccessTier = b.Properties.AccessTier?.ToString(),
                    })], flatPage.ContinuationToken);
                return ([], null);
            }
            await foreach (var page in client.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", prefix, ct).AsPages(continuation, pageSize))
                return ([.. page.Values.Select(i => i.IsPrefix ? new BlobItemView { Name = i.Prefix, IsPrefix = true } : new BlobItemView
                {
                    Name = i.Blob.Name, Size = i.Blob.Properties.ContentLength, ContentType = i.Blob.Properties.ContentType, LastModified = i.Blob.Properties.LastModified,
                    ETag = i.Blob.Properties.ETag?.ToString(), BlobType = i.Blob.Properties.BlobType?.ToString(), AccessTier = i.Blob.Properties.AccessTier?.ToString(),
                })], page.ContinuationToken);
            return ([], null);
        }
        catch (RequestFailedException ex) { throw Denied(ex); }
    }

    public async Task<BlobProperties?> PropertiesAsync(string container, string name, CancellationToken ct)
    {
        try
        {
            var p = (await _service.GetBlobContainerClient(container).GetBlobClient(name).GetPropertiesAsync(cancellationToken: ct)).Value;
            return new BlobProperties
            {
                Container = container, Name = name, Size = p.ContentLength, ContentType = p.ContentType, ContentEncoding = p.ContentEncoding, LastModified = p.LastModified,
                CreatedOn = p.CreatedOn, ETag = p.ETag.ToString(), BlobType = p.BlobType.ToString(), AccessTier = p.AccessTier, VersionId = p.VersionId,
                Metadata = new Dictionary<string, string>(p.Metadata),
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
        catch (RequestFailedException ex) { throw Denied(ex); }
    }

    public async Task<(byte[] Bytes, bool Complete)> ReadAsync(string container, string name, int maxBytes, CancellationToken ct)
    {
        try
        {
            var blob = _service.GetBlobContainerClient(container).GetBlobClient(name);
            var download = await blob.DownloadStreamingAsync(new BlobDownloadOptions { Range = new HttpRange(0, maxBytes) }, ct);
            using var buffer = new MemoryStream();
            await download.Value.Content.CopyToAsync(buffer, ct);
            var total = download.Value.Details.ContentRange is { } range && range.LastIndexOf('/') is var slash and > 0 && long.TryParse(range[(slash + 1)..], out var t) ? t : buffer.Length;
            return (buffer.ToArray(), buffer.Length >= total);
        }
        catch (RequestFailedException ex) { throw Denied(ex); }
    }

    private static BlobStoreAccessException Denied(RequestFailedException ex) => new(ex.Status is 401 or 403
        ? "Permission denied by the storage account (Storage Blob Data Reader on the container is needed)."
        : $"The storage account answered {ex.Status} ({ex.ErrorCode}).");
}

/// <summary>
/// SYNTHETIC LOCAL store (Development only, never Azure): sub-folders of the configured root are containers, files are blobs; a sidecar
/// "{name}.metadata.json" holds a blob's user metadata and is not listed. Names are validated and resolved under the root (no traversal).
/// </summary>
public sealed class SyntheticLocalBlobStore(string root) : IBlobReadStore
{
    private const string Sidecar = ".metadata.json";
    private readonly string _root = Path.GetFullPath(root);

    public Task<List<BlobContainerView>> ContainersAsync(CancellationToken ct) => Task.FromResult(Directory.Exists(_root)
        ? Directory.EnumerateDirectories(_root).Select(d => new BlobContainerView(Path.GetFileName(d), Directory.GetLastWriteTimeUtc(d))).OrderBy(c => c.Name, StringComparer.Ordinal).ToList()
        : []);

    public Task<(List<BlobItemView> Items, string? Continuation)> ListAsync(string container, string? prefix, string? continuation, int pageSize, CancellationToken ct, bool flat = false)
    {
        var dir = Container(container);
        prefix ??= "";
        var all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(Sidecar, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var entries = all.Select(n => !flat && n[prefix.Length..] is var rest && rest.IndexOf('/') is var slash and >= 0 ? (Name: prefix + rest[..(slash + 1)], Prefix: true) : (Name: n, Prefix: false))
            .Distinct().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var start = continuation is null ? 0 : entries.FindIndex(e => string.CompareOrdinal(e.Name, continuation) > 0);
        if (start < 0) start = entries.Count;
        var page = entries.Skip(start).Take(pageSize).Select(e => e.Prefix ? new BlobItemView { Name = e.Name, IsPrefix = true } : Item(dir, e.Name)).ToList();
        var next = start + pageSize < entries.Count ? page[^1].Name : null;
        return Task.FromResult((page, next));
    }

    public Task<BlobProperties?> PropertiesAsync(string container, string name, CancellationToken ct)
    {
        var dir = Container(container);
        var file = BlobPath(dir, name);
        if (!File.Exists(file)) return Task.FromResult<BlobProperties?>(null);
        var info = new FileInfo(file);
        var meta = File.Exists(file + Sidecar)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file + Sidecar)) ?? [] : new Dictionary<string, string>();
        return Task.FromResult<BlobProperties?>(new BlobProperties
        {
            Container = container, Name = name, Size = info.Length, ContentType = ContentType(name), LastModified = info.LastWriteTimeUtc, CreatedOn = info.CreationTimeUtc,
            ETag = ETag(info), BlobType = "BlockBlob", AccessTier = "Hot", Metadata = meta,
        });
    }

    public async Task<(byte[] Bytes, bool Complete)> ReadAsync(string container, string name, int maxBytes, CancellationToken ct)
    {
        var file = BlobPath(Container(container), name);
        if (!File.Exists(file)) throw new BlobStoreAccessException("The blob does not exist (it may have been removed after it was listed).");
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[(int)Math.Min(maxBytes, stream.Length)];
        var read = 0;
        while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0) read += n;
        return (buffer[..read], stream.Length <= maxBytes);
    }

    private string Container(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains("..")) throw new BlobStoreAccessException("Invalid container name.");
        var dir = Path.GetFullPath(Path.Combine(_root, name));
        if (!dir.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dir)) throw new BlobStoreAccessException("The container does not exist.");
        return dir;
    }

    private static string BlobPath(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Split('/').Any(s => s is "" or "." or "..") || name.EndsWith(Sidecar, StringComparison.Ordinal))
            throw new BlobStoreAccessException("Invalid blob name.");
        var file = Path.GetFullPath(Path.Combine(dir, name));
        if (!file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new BlobStoreAccessException("Invalid blob name.");
        return file;
    }

    private static BlobItemView Item(string dir, string name)
    {
        var info = new FileInfo(Path.Combine(dir, name));
        return new BlobItemView { Name = name, Size = info.Length, ContentType = ContentType(name), LastModified = info.LastWriteTimeUtc, ETag = ETag(info), BlobType = "BlockBlob", AccessTier = "Hot" };
    }

    private static string ETag(FileInfo f) => "\"0x" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{f.Length}|{f.LastWriteTimeUtc.Ticks}")))[..16] + "\"";

    internal static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".json" => "application/json", ".xml" => "application/xml", ".csv" => "text/csv", ".ndjson" or ".jsonl" => "application/x-ndjson", ".txt" => "text/plain",
        ".parquet" => "application/vnd.apache.parquet", _ => "application/octet-stream",
    };
}
