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
    private Task<IReadOnlyList<IqrSourceSnapshot>>? _pending;
    private string? _selectedSnapshotId;
    private long _generation;

    public Task<IReadOnlyList<IqrSourceSnapshot>> GetOrLoadAsync(
        SourceAnalysisSnapshotKey key,
        Func<Task<IReadOnlyList<IqrSourceSnapshot>>> load)
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

    /// <summary>Force the next read to use the server after an analysis/import has produced new evidence.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _key = null;
            _pending = null;
            _selectedSnapshotId = null;
        }
    }

    private async Task<IReadOnlyList<IqrSourceSnapshot>> LoadAndStoreAsync(
        SourceAnalysisSnapshotKey key,
        long generation,
        Func<Task<IReadOnlyList<IqrSourceSnapshot>>> load)
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
