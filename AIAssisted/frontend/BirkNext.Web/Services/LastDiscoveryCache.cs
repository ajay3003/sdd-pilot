using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// The last parsed discovery result per project and snapshot, with the backend's ETag for it. Every Manual Testing page reads the last discovery
/// (8 MB for 1,752 M2LB scenarios); parsing it again on each page took seconds of the browser's only thread. The client now revalidates: the
/// backend answers 304 Not Modified while the result is byte-identical, and this parsed instance is reused. Any change gives a new ETag and a
/// fresh read. The result type is immutable (init-only), so sharing one instance between pages is safe. At most <see cref="Capacity"/> entries.
/// </summary>
public sealed class LastDiscoveryCache
{
    public const int Capacity = 4;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string ETag, ScenarioDiscoveryResult Result)> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public static string KeyOf(string projectKey, Guid? snapshotId) => $"{projectKey}\n{snapshotId}";

    public bool TryGet(string key, out string etag, out ScenarioDiscoveryResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry)) { (etag, result) = entry; return true; }
        }
        (etag, result) = ("", null!);
        return false;
    }

    public void Set(string key, string etag, ScenarioDiscoveryResult result)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(key))
            {
                _order.Enqueue(key);
                while (_order.Count > Capacity) _entries.Remove(_order.Dequeue());
            }
            _entries[key] = (etag, result);
        }
    }

    /// <summary>The project has no last discovery (204) or the read failed: nothing stale is kept for it.</summary>
    public void Remove(string key)
    {
        lock (_gate) _entries.Remove(key);
    }
}
