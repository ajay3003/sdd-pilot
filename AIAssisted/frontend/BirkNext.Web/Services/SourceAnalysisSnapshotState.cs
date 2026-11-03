using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Reuses the current workspace's immutable Source Analysis response for page navigation within this browser session.
/// This is a bounded UI state cache, not another evidence store; successful analysis explicitly invalidates it.
/// </summary>
public sealed class SourceAnalysisSnapshotState
{
    private readonly object _gate = new();
    private SourceAnalysisSnapshotKey? _key;
    private Task<IReadOnlyList<IqrSourceSnapshotSummary>>? _pending;
    private string? _selectedSnapshotId;
    private long _generation;

    public Task<IReadOnlyList<IqrSourceSnapshotSummary>> GetOrLoadAsync(
        SourceAnalysisSnapshotKey key,
        Func<Task<IReadOnlyList<IqrSourceSnapshotSummary>>> load)
    {
        lock (_gate)
        {
            if (_key == key && _pending is not null) return _pending;

            _key = key;
            _selectedSnapshotId = null;
            var generation = ++_generation;
            _pending = LoadAndStoreAsync(key, generation, load);
            return _pending;
        }
    }

    public string? SelectedSnapshotId(SourceAnalysisSnapshotKey key)
    {
        lock (_gate) return _key == key ? _selectedSnapshotId : null;
    }

    public void SelectSnapshot(SourceAnalysisSnapshotKey key, string? snapshotId)
    {
        lock (_gate)
        {
            if (_key == key) _selectedSnapshotId = snapshotId;
        }
    }

    // Full snapshots opened on this page, by id: a stored snapshot is immutable, so an entry never goes stale. Bounded (a snapshot can be tens of
    // megabytes in memory); a failed fetch is not kept, so opening it again retries.
    private const int MaxDetails = 3;
    private readonly List<(Guid Id, Task<IqrSourceSnapshot?> Detail)> _details = [];

    /// <summary>One full snapshot, fetched once while it stays among the last few opened.</summary>
    public Task<IqrSourceSnapshot?> GetDetailAsync(Guid id, Func<Guid, Task<IqrSourceSnapshot?>> load)
    {
        lock (_gate)
        {
            var index = _details.FindIndex(d => d.Id == id && !d.Detail.IsFaulted && !d.Detail.IsCanceled);
            if (index >= 0)
            {
                var hit = _details[index];
                _details.RemoveAt(index);
                _details.Add(hit);
                return hit.Detail;
            }
            _details.RemoveAll(d => d.Id == id);
            var detail = load(id);
            _details.Add((id, detail));
            while (_details.Count > MaxDetails) _details.RemoveAt(0);
            return detail;
        }
    }

    /// <summary>Force the next read to use the server after an analysis/import has produced new evidence.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _details.Clear();
            _generation++;
            _key = null;
            _pending = null;
            _selectedSnapshotId = null;
        }
    }

    private async Task<IReadOnlyList<IqrSourceSnapshotSummary>> LoadAndStoreAsync(
        SourceAnalysisSnapshotKey key,
        long generation,
        Func<Task<IReadOnlyList<IqrSourceSnapshotSummary>>> load)
    {
        try
        {
            var snapshots = await load();
            lock (_gate)
            {
                if (_generation == generation && _key == key) _pending = Task.FromResult(snapshots);
            }
            return snapshots;
        }
        catch
        {
            lock (_gate)
            {
                if (_generation == generation && _key == key)
                {
                    _key = null;
                    _pending = null;
                    _selectedSnapshotId = null;
                }
            }
            throw;
        }
    }
}

public sealed record SourceAnalysisSnapshotKey(string EnvironmentId, Guid? WorkspaceId, string? ProjectSlug, long WorkspaceVersion);
