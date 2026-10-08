using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.ProjectImport;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.ProjectImport;

/// <summary>
/// One uploaded, validated project archive waiting for commit (or for a source retry). The bytes and the validated workspace are the ones
/// the preview produced (or, after a backend restart, the same staged bytes validated again): commit never reads the client file.
/// </summary>
public sealed class StagedProjectImport(Guid stagingId, string importId, string fileName, byte[] bytes, IqrSourceArchiveReader.Workspace workspace,
    ProjectImportSourceDetection source, DateTimeOffset stagedAt, DateTimeOffset expiresAt)
{
    public Guid StagingId { get; } = stagingId;
    public string ImportId { get; } = importId;
    public string FileName { get; } = fileName;
    public byte[] Bytes { get; } = bytes;
    public IqrSourceArchiveReader.Workspace Workspace { get; } = workspace;
    public ProjectImportSourceDetection Source { get; } = source;
    public DateTimeOffset StagedAt { get; } = stagedAt;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    /// <summary>The settled commit result. Set once the source part is settled; a repeated commit returns it instead of committing again.</summary>
    public ProjectImportCommitResult? Committed { get; internal set; }
    /// <summary>Serialises commits of this staging so two clicks cannot create two snapshots from one upload.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// Bounded staging for Project Import. Each upload gets its own random staging id (an unguessable capability: concurrent imports, even of
/// the same archive, never share state). Entries expire after <see cref="TimeToLive"/>; at most <see cref="MaxStaged"/> are kept (the oldest
/// is evicted first).
///
/// With a staging directory the stage is durable across backend restarts: the exact accepted archive bytes (<c>{id}.zip</c>) and a small
/// metadata record (<c>{id}.json</c>: import id, archive fingerprint, display name, times, source detection, settled commit result) are
/// written there. File names come only from the staging id — never from the client file name or path. After a restart the bytes are
/// validated again by the same archive reader and must still match the recorded fingerprint. A settled commit keeps its result (the
/// bytes are deleted) until expiry, so a retried commit returns the same result. Expired and orphan files are deleted at startup and
/// whenever the store is used. Without a directory (tests) the store is in memory only.
/// </summary>
public sealed class ProjectImportStagingStore
{
    public const int MaxStaged = 4;
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time;
    private readonly string? _directory;
    private readonly ILogger? _logger;
    private readonly Dictionary<Guid, StagedProjectImport> _staged = [];
    private readonly object _gate = new();

    public ProjectImportStagingStore(TimeProvider? time = null, string? directory = null, ILogger<ProjectImportStagingStore>? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _directory = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
        _logger = logger;
        if (_directory is not null)
        {
            Directory.CreateDirectory(_directory);
            lock (_gate) RemoveExpired();
        }
    }

    /// <summary>The default durable location: a BirkNext folder under the machine's temporary directory.</summary>
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "birknext", "project-import-staging");

    public bool IsDurable => _directory is not null;

    public DateTimeOffset Now => _time.GetUtcNow();

    public int Count { get { lock (_gate) { RemoveExpired(); return _staged.Count + DurableIdsNotLoaded().Count(); } } }

    public StagedProjectImport Add(string importId, string fileName, byte[] bytes, IqrSourceArchiveReader.Workspace workspace, ProjectImportSourceDetection source)
    {
        var now = Now;
        var staged = new StagedProjectImport(Guid.NewGuid(), importId, fileName, bytes, workspace, source, now, now + TimeToLive);
        lock (_gate)
        {
            RemoveExpired();
            var all = AllRecords();
            foreach (var oldest in all.OrderBy(r => r.StagedAt).Take(Math.Max(0, all.Count - MaxStaged + 1)))
                Delete(oldest.StagingId);
            _staged[staged.StagingId] = staged;
            Persist(staged, bytes);
        }
        return staged;
    }

    /// <summary>The live stage, loaded again from the staging directory after a restart; null when unknown or expired.</summary>
    public StagedProjectImport? Find(Guid stagingId)
    {
        lock (_gate)
        {
            RemoveExpired();
            if (_staged.TryGetValue(stagingId, out var staged)) return staged;
            return Load(stagingId);
        }
    }

    /// <summary>Records the settled commit result. The archive bytes are no longer needed and are deleted; the record stays until expiry.</summary>
    public void MarkCommitted(StagedProjectImport staged, ProjectImportCommitResult result)
    {
        lock (_gate)
        {
            staged.Committed = result;
            if (_directory is null) return;
            TryDelete(ArchivePath(staged.StagingId));
            WriteRecord(staged);
        }
    }

    public bool Remove(Guid stagingId)
    {
        lock (_gate)
        {
            var known = _staged.ContainsKey(stagingId) || (_directory is not null && File.Exists(RecordPath(stagingId)));
            Delete(stagingId);
            return known;
        }
    }

    /// <summary>Local data reset: staged archives belong to the project being reset.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _staged.Clear();
            if (_directory is null) return;
            foreach (var file in StagingFiles()) TryDelete(file);
        }
    }

    // ── Durable records ─────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record StagedRecord
    {
        public Guid StagingId { get; init; }
        public string ImportId { get; init; } = "";
        public string FileName { get; init; } = "";
        public string ArchiveSha256 { get; init; } = "";
        public DateTimeOffset StagedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public ProjectImportSourceDetection Source { get; init; } = new();
        public ProjectImportCommitResult? Committed { get; init; }
    }

    private string RecordPath(Guid id) => Path.Combine(_directory!, id.ToString("N") + ".json");
    private string ArchivePath(Guid id) => Path.Combine(_directory!, id.ToString("N") + ".zip");

    private void Persist(StagedProjectImport staged, byte[] bytes)
    {
        if (_directory is null) return;
        try
        {
            // Record first: an archive without a record is an orphan that cleanup deletes; a record without its archive is discarded on load.
            WriteRecord(staged);
            var temp = ArchivePath(staged.StagingId) + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, ArchivePath(staged.StagingId), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging stays usable in memory; only restart recovery is lost for this upload.
            _logger?.LogWarning("Project import staging could not be written to disk ({ExceptionType}); the stage is kept in memory only.", ex.GetType().Name);
            TryDelete(ArchivePath(staged.StagingId));
            TryDelete(RecordPath(staged.StagingId));
        }
    }

    private void WriteRecord(StagedProjectImport staged)
    {
        var record = new StagedRecord
        {
            StagingId = staged.StagingId, ImportId = staged.ImportId, FileName = staged.FileName, ArchiveSha256 = staged.Workspace.Archive.Sha256,
            StagedAt = staged.StagedAt, ExpiresAt = staged.ExpiresAt, Source = staged.Source, Committed = staged.Committed,
        };
        var path = RecordPath(staged.StagingId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        File.Move(temp, path, overwrite: true);
    }

    private StagedRecord? ReadRecord(Guid id)
    {
        if (_directory is null || !File.Exists(RecordPath(id))) return null;
        try { return JsonSerializer.Deserialize<StagedRecord>(File.ReadAllText(RecordPath(id)), Json); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Rebuilds a stage after a restart. The staged bytes are validated again and must match the recorded fingerprint; anything else is discarded.</summary>
    private StagedProjectImport? Load(Guid id)
    {
        var record = ReadRecord(id);
        if (record is null || record.StagingId != id || record.ExpiresAt <= Now) { Delete(id); return null; }
        StagedProjectImport staged;
        if (record.Committed is not null)
        {
            // Settled: only the result is kept. An empty workspace stands in for the deleted bytes; commit returns the stored result.
            var empty = new IqrSourceArchiveReader.Workspace(new BirkNext.Integrations.SourceArchive(record.FileName, record.ArchiveSha256, 0), [], [], []);
            staged = new StagedProjectImport(id, record.ImportId, record.FileName, [], empty, record.Source, record.StagedAt, record.ExpiresAt) { Committed = record.Committed };
        }
        else
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(ArchivePath(id)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Delete(id); return null; }
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), record.ArchiveSha256, StringComparison.OrdinalIgnoreCase)) { Delete(id); return null; }
            var validation = IqrSourceArchiveReader.ReadDetailed(record.FileName, bytes, captureDocuments: true);
            if (!validation.IsValid) { Delete(id); return null; }
            staged = new StagedProjectImport(id, record.ImportId, record.FileName, bytes, validation.Workspace!, record.Source, record.StagedAt, record.ExpiresAt);
        }
        _staged[id] = staged;
        _logger?.LogInformation("Project import stage restored after a restart. Settled {Settled}", staged.Committed is not null);
        return staged;
    }

    private List<(Guid StagingId, DateTimeOffset StagedAt)> AllRecords()
    {
        var all = _staged.Values.Select(s => (s.StagingId, s.StagedAt)).ToList();
        foreach (var id in DurableIdsNotLoaded())
            if (ReadRecord(id) is { } record) all.Add((id, record.StagedAt));
        return all;
    }

    private IEnumerable<Guid> DurableIdsNotLoaded() => _directory is null ? []
        : Directory.EnumerateFiles(_directory, "*.json").Select(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty && !_staged.ContainsKey(id)).ToList();

    private IEnumerable<string> StagingFiles() => Directory.EnumerateFiles(_directory!).Where(f => f.EndsWith(".zip") || f.EndsWith(".json") || f.EndsWith(".tmp")).ToList();

    private void RemoveExpired()
    {
        var now = Now;
        foreach (var id in _staged.Values.Where(s => s.ExpiresAt <= now).Select(s => s.StagingId).ToList()) Delete(id);
        if (_directory is null) return;
        foreach (var file in StagingFiles())
        {
            var name = Path.GetFileName(file);
            if (!Guid.TryParseExact(name[..Math.Min(32, name.Length)], "N", out var id)) { TryDelete(file); continue; }
            if (_staged.ContainsKey(id)) continue;
            var record = file.EndsWith(".json") ? ReadRecord(id) : (File.Exists(RecordPath(id)) ? ReadRecord(id) : null);
            // Expired, unreadable or orphaned (an archive without its record, or a temp file left by an interrupted write) → deleted.
            // A temp file is only stale after a minute: another BirkNext process may be writing it right now.
            if (file.EndsWith(".tmp")) { if (File.GetLastWriteTimeUtc(file) < now.UtcDateTime.AddMinutes(-1)) TryDelete(file); continue; }
            if (record is null || record.ExpiresAt <= now) TryDelete(file);
        }
    }

    private void Delete(Guid id)
    {
        _staged.Remove(id);
        if (_directory is null) return;
        TryDelete(ArchivePath(id));
        TryDelete(RecordPath(id));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
