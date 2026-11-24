using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>
/// Process-wide cache of the identity projection (<see cref="IqrSourceStore.IdentityMembers"/>) of stored Source Analysis snapshots.
/// <para>Why it is correct: a stored snapshot is immutable. Source Analysis only inserts rows; a workspace package or a backup restore re-inserts
/// a row under its original id. The same id therefore always holds the same evidence; <c>AnalyzedAt</c> is checked as well, so a row that was
/// replaced in any other way is a miss and is read again.</para>
/// <para>Why it exists: without it every identity read transferred and parsed the full evidence JSON of every stored snapshot (77 MB for
/// eight M2LB snapshots) on every request — the technology-coverage read and Manual Testing's source basis.</para>
/// <para>Bounded by the size of the cached projections (<see cref="DefaultBudgetBytes"/> of identity JSON; the oldest entries leave first), and
/// entries of deleted snapshots are dropped whenever a listing returns every stored row (<see cref="Retain"/>).</para>
/// <para>Cached snapshots are shared between requests: consumers treat a listed snapshot as read-only (they derive new values with <c>with</c>).</para>
/// </summary>
public sealed class SourceIdentityCache(long budgetBytes = SourceIdentityCache.DefaultBudgetBytes)
{
    /// <summary>Identity JSON kept in memory at most (about 55 large M2LB snapshots).</summary>
    public const long DefaultBudgetBytes = 128L * 1024 * 1024;

    private sealed record Entry(DateTimeOffset AnalyzedAt, IqrSourceSnapshot Snapshot, long Bytes, long Order);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private long _bytes;
    private long _order;

    public int Count { get { lock (_gate) return _entries.Count; } }
    public long Bytes { get { lock (_gate) return _bytes; } }

    public bool TryGet(Guid id, DateTimeOffset analyzedAt, out IqrSourceSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.AnalyzedAt == analyzedAt) { snapshot = entry.Snapshot; return true; }
        }
        snapshot = null!;
        return false;
    }

    /// <summary>Caches one projection; <paramref name="bytes"/> is the size of its identity JSON. A projection larger than the whole budget is not cached.</summary>
    public void Add(Guid id, DateTimeOffset analyzedAt, IqrSourceSnapshot snapshot, long bytes)
    {
        if (bytes > budgetBytes) return;
        lock (_gate)
        {
            if (_entries.Remove(id, out var replaced)) _bytes -= replaced.Bytes;
            while (_bytes + bytes > budgetBytes && _entries.Count > 0)
            {
                var oldest = _entries.MinBy(e => e.Value.Order);
                _entries.Remove(oldest.Key);
                _bytes -= oldest.Value.Bytes;
            }
            _entries[id] = new Entry(analyzedAt, snapshot, bytes, ++_order);
            _bytes += bytes;
        }
    }

    /// <summary>Drops every entry whose snapshot is not in <paramref name="stored"/> — called with the complete set of stored rows.</summary>
    public void Retain(IReadOnlyCollection<Guid> stored)
    {
        var keep = stored as IReadOnlySet<Guid> ?? stored.ToHashSet();
        lock (_gate)
        {
            foreach (var id in _entries.Keys.Where(id => !keep.Contains(id)).ToList())
                if (_entries.Remove(id, out var removed)) _bytes -= removed.Bytes;
        }
    }
}
