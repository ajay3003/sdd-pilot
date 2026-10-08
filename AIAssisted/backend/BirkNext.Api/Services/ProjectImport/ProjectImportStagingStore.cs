using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.ProjectImport;

namespace BirkNext.Api.Services.ProjectImport;

/// <summary>
/// One uploaded, validated project archive waiting for commit (or for a source retry). The bytes and the validated workspace are the ones
/// the preview produced: commit never reads another copy and never re-reads the client file.
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
    /// <summary>Serialises commits of this staging so two clicks cannot create two snapshots from one upload.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// Bounded in-memory staging for Project Import (never on disk, like Source Analysis). Each upload gets its own random staging id, so
/// concurrent imports — even of the same archive — never share state. Entries expire after <see cref="TimeToLive"/>; at most
/// <see cref="MaxStaged"/> are kept (the oldest is evicted first). Committing an archive whose source part is done releases it.
/// </summary>
public sealed class ProjectImportStagingStore(TimeProvider? time = null)
{
    public const int MaxStaged = 4;
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Guid, StagedProjectImport> _staged = [];
    private readonly object _gate = new();

    public DateTimeOffset Now => _time.GetUtcNow();

    public int Count { get { lock (_gate) { RemoveExpired(); return _staged.Count; } } }

    public StagedProjectImport Add(string importId, string fileName, byte[] bytes, IqrSourceArchiveReader.Workspace workspace, ProjectImportSourceDetection source)
    {
        var now = Now;
        var staged = new StagedProjectImport(Guid.NewGuid(), importId, fileName, bytes, workspace, source, now, now + TimeToLive);
        lock (_gate)
        {
            RemoveExpired();
            while (_staged.Count >= MaxStaged)
                _staged.Remove(_staged.Values.OrderBy(s => s.StagedAt).First().StagingId);
            _staged[staged.StagingId] = staged;
        }
        return staged;
    }

    public StagedProjectImport? Find(Guid stagingId)
    {
        lock (_gate)
        {
            RemoveExpired();
            return _staged.GetValueOrDefault(stagingId);
        }
    }

    public bool Remove(Guid stagingId)
    {
        lock (_gate) return _staged.Remove(stagingId);
    }

    /// <summary>Local data reset: staged archives belong to the project being reset.</summary>
    public void Clear()
    {
        lock (_gate) _staged.Clear();
    }

    private void RemoveExpired()
    {
        var now = Now;
        foreach (var id in _staged.Values.Where(s => s.ExpiresAt <= now).Select(s => s.StagingId).ToList()) _staged.Remove(id);
    }
}
