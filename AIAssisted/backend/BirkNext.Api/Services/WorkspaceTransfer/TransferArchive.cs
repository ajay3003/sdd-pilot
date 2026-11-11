using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>Limits for one transfer archive. Backups and portable packages use different instances.</summary>
public sealed record TransferArchiveLimits(int MaxEntries, long MaxEntryBytes, long MaxTotalBytes, double MaxCompressionRatio, IReadOnlyList<string> AllowedPrefixes)
{
    public static TransferArchiveLimits Backup { get; } = new(5_000, 8L << 30, 32L << 30, 1_000, ["manifest.json", "database/", "files/", "integrity/"]);
    public static TransferArchiveLimits Package { get; } = new(20_000, 64L << 20, 1L << 30, 200,
        ["manifest.json", "inventory/", "projects/", "sources/", "contracts/", "schemas/", "connected-systems/", "environments/", "topology/", "test-data/",
         "test-definitions/", "integration-suites/", "browser-e2e/", "reviews/", "evidence/", "artifacts/", "integrity/"]);
}

/// <summary>
/// Writes a transfer ZIP with a SHA-256 for every entry (integrity/hashes.json written last). Works on a non-seekable stream (ZipArchiveMode.Create),
/// e.g. straight into <see cref="TransferCrypto"/>, so no plaintext copy of a backup is written to disk.
/// </summary>
public sealed class TransferArchiveWriter : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly SortedDictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private bool _sealed;

    public TransferArchiveWriter(Stream output) => _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

    public IReadOnlyDictionary<string, string> Hashes => _hashes;

    public async Task<(long Bytes, string Sha256)> AddAsync(string path, Func<Stream, Task> write, CompressionLevel level = CompressionLevel.Optimal)
    {
        TransferArchiveReader.Validate(path);
        if (_hashes.ContainsKey(path)) throw new InvalidOperationException($"Duplicate entry {path}.");
        var entry = _zip.CreateEntry(path, level);
        await using var raw = entry.Open();
        await using var hashing = new HashingWriteStream(raw);
        await write(hashing);
        await hashing.FlushAsync();
        var sha = hashing.Finish();
        _hashes[path] = sha;
        return (hashing.Bytes, sha);
    }

    public Task<(long Bytes, string Sha256)> AddJsonAsync<T>(string path, T value, JsonSerializerOptions json) =>
        AddAsync(path, s => JsonSerializer.SerializeAsync(s, value, json));

    public Task<(long Bytes, string Sha256)> AddFileAsync(string path, string fullPath) =>
        AddAsync(path, async s => { await using var f = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); await f.CopyToAsync(s); });

    /// <summary>Writes integrity/hashes.json (every other entry's SHA-256). Call once, last.</summary>
    public async Task SealAsync(JsonSerializerOptions json)
    {
        if (_sealed) return;
        _sealed = true;
        var entry = _zip.CreateEntry(TransferArchiveReader.HashesPath, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await JsonSerializer.SerializeAsync(s, _hashes, json);
    }

    public void Dispose() => _zip.Dispose();

    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Bytes { get; private set; }
        public string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { _hash.AppendData(buffer); Bytes += buffer.Length; inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { _hash.AppendData(buffer.Span); Bytes += buffer.Length; await inner.WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}

/// <summary>
/// Opens a staged transfer ZIP (a file on disk; never a byte array of the whole archive) and validates it before anything is read:
/// entry count, allowed prefixes, path safety (zip slip, rooted, drive, backslash, control characters, "." / ".." segments), duplicates after
/// case folding, symlinks, declared sizes and compression ratio (zip bomb). Reads are bounded by the declared size. Every entry is checked
/// against integrity/hashes.json, and hashes.json must list exactly the other entries. A checksum inside the archive proves integrity of a
/// transfer, not who produced it: authenticity comes from the backup's AEAD (full backups) and is NOT claimed for unsigned packages.
/// </summary>
public sealed class TransferArchiveReader : IDisposable
{
    public const string HashesPath = "integrity/hashes.json";
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly TransferArchiveLimits _limits;

    private TransferArchiveReader(ZipArchive zip, TransferArchiveLimits limits) { _zip = zip; _limits = limits; }

    public IReadOnlyCollection<string> Paths => _entries.Keys;

    public static TransferArchiveReader Open(Stream seekable, TransferArchiveLimits limits)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(seekable, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new TransferFormatException("NOT_A_ZIP", "The file is not a valid ZIP archive."); }
        var reader = new TransferArchiveReader(zip, limits);
        try { reader.Inspect(); }
        catch { zip.Dispose(); throw; }
        return reader;
    }

    private void Inspect()
    {
        if (_zip.Entries.Count > _limits.MaxEntries) throw new TransferFormatException("TOO_MANY_ENTRIES", $"The archive has {_zip.Entries.Count} entries (limit {_limits.MaxEntries}).");
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var e in _zip.Entries)
        {
            if (e.FullName.EndsWith('/') && e.Length == 0) continue; // directory entry
            Validate(e.FullName);
            if (!_limits.AllowedPrefixes.Any(p => p.EndsWith('/') ? e.FullName.StartsWith(p, StringComparison.Ordinal) : e.FullName == p))
                throw new TransferFormatException("UNEXPECTED_ENTRY", $"Unexpected entry \"{e.FullName}\".");
            if (!folded.Add(e.FullName)) throw new TransferFormatException("DUPLICATE_ENTRY", $"Duplicate entry \"{e.FullName}\" (names are compared case-insensitively).");
            // Unix mode in the high 16 bits of the external attributes: 0xA000 = symbolic link.
            if (((e.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new TransferFormatException("SYMLINK_ENTRY", $"Symbolic link entry \"{e.FullName}\" is not allowed.");
            if (e.Length > _limits.MaxEntryBytes) throw new TransferFormatException("ENTRY_TOO_LARGE", $"\"{e.FullName}\" expands to {e.Length:N0} bytes (limit {_limits.MaxEntryBytes:N0}).");
            if (e.Length > 1 << 20 && e.CompressedLength > 0 && (double)e.Length / e.CompressedLength > _limits.MaxCompressionRatio)
                throw new TransferFormatException("COMPRESSION_RATIO", $"\"{e.FullName}\" has a compression ratio above {_limits.MaxCompressionRatio:N0}: refused as a possible zip bomb.");
            total += e.Length;
            if (total > _limits.MaxTotalBytes) throw new TransferFormatException("ARCHIVE_TOO_LARGE", $"The archive expands beyond {_limits.MaxTotalBytes:N0} bytes.");
            _entries[e.FullName] = e;
        }
    }

    /// <summary>Path rules shared by the writer and the reader.</summary>
    public static void Validate(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 400) throw new TransferFormatException("BAD_PATH", "An entry path is empty or too long.");
        if (path.Contains('\\') || path.StartsWith('/') || path.Contains(':') || path.Any(c => char.IsControl(c)))
            throw new TransferFormatException("BAD_PATH", $"Entry path \"{Printable(path)}\" is not a safe relative path.");
        if (path.Split('/').Any(seg => seg is "" or "." or ".."))
            throw new TransferFormatException("BAD_PATH", $"Entry path \"{Printable(path)}\" contains an empty, \".\" or \"..\" segment.");
    }

    private static string Printable(string s) => new(s.Select(c => char.IsControl(c) ? '?' : c).Take(200).ToArray());

    public bool Has(string path) => _entries.ContainsKey(path);

    /// <summary>A read stream bounded by the declared size (a lying header cannot expand further).</summary>
    public Stream OpenRead(string path) => _entries.TryGetValue(path, out var e)
        ? new BoundedReadStream(e.Open(), e.Length, path)
        : throw new TransferFormatException("MISSING_ENTRY", $"The archive has no \"{path}\".");

    public async Task<T> ReadJsonAsync<T>(string path, JsonSerializerOptions json, long maxBytes = 64L << 20)
    {
        if (_entries.TryGetValue(path, out var e) && e.Length > maxBytes) throw new TransferFormatException("ENTRY_TOO_LARGE", $"\"{path}\" is too large to read as JSON.");
        await using var s = OpenRead(path);
        try { return await JsonSerializer.DeserializeAsync<T>(s, json) ?? throw new TransferFormatException("BAD_JSON", $"\"{path}\" is empty."); }
        catch (JsonException ex) { throw new TransferFormatException("BAD_JSON", $"\"{path}\" is not valid JSON: {ex.Message}"); }
    }

    /// <summary>Checks every entry against integrity/hashes.json. Returns the problems (empty = every hash matches and nothing is unlisted).</summary>
    public async Task<IReadOnlyList<string>> VerifyHashesAsync(JsonSerializerOptions json, CancellationToken ct)
    {
        if (!Has(HashesPath)) return ["integrity/hashes.json is missing."];
        var expected = await ReadJsonAsync<Dictionary<string, string>>(HashesPath, json, 16L << 20);
        var problems = new List<string>();
        foreach (var path in _entries.Keys.Where(p => p != HashesPath).Except(expected.Keys, StringComparer.Ordinal)) problems.Add($"\"{path}\" is not listed in the integrity manifest.");
        foreach (var (path, sha) in expected)
        {
            ct.ThrowIfCancellationRequested();
            if (!_entries.ContainsKey(path)) { problems.Add($"\"{path}\" is listed but missing."); continue; }
            await using var s = OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase)) problems.Add($"\"{path}\" does not match its SHA-256 (tampered or corrupt).");
        }
        return problems;
    }

    public void Dispose() => _zip.Dispose();

    private sealed class BoundedReadStream(Stream inner, long limit, string path) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            _read += n;
            if (_read > limit) throw new TransferFormatException("ENTRY_SIZE_MISMATCH", $"\"{path}\" expands beyond its declared size.");
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            _read += n;
            if (_read > limit) throw new TransferFormatException("ENTRY_SIZE_MISMATCH", $"\"{path}\" expands beyond its declared size.");
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => limit;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

public static class TransferJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        MaxDepth = 64,
    };

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
