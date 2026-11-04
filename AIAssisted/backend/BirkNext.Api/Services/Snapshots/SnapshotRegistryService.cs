using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Data.SnapshotRegistry;
using BirkNext.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Snapshots;

/// <summary>One owner store the registry syncs metadata from. It reads each payload at most once (when the snapshot is first registered).</summary>
public interface ISnapshotSource
{
    string Name { get; }
    /// <summary>Snapshots not yet registered, and registered ones to refresh (still Running, or registered before their summary was stored),
    /// with the lineage edges their owner stored.</summary>
    Task<IReadOnlyList<(SnapshotMetadata Metadata, IReadOnlyList<SnapshotLineageEdge> Edges)>> ReadAsync(IReadOnlySet<string> registered, IReadOnlySet<string> running, CancellationToken ct);

    /// <summary>
    /// The registry keys ("Type|Id") whose payloads this source currently holds, for the snapshot types it is authoritative for (ids only, never a
    /// payload). Null when the source cannot enumerate them cheaply; then payload availability of its entries is left as registered.
    /// </summary>
    Task<(IReadOnlySet<string> Types, IReadOnlySet<string> Keys)?> InventoryAsync(CancellationToken ct) =>
        Task.FromResult<(IReadOnlySet<string>, IReadOnlySet<string>)?>(null);
}

/// <summary>
/// The shared Snapshot Registry over the existing domain stores: metadata, typed lineage, current/latest pointers and retention, persisted in its
/// own schema. Payloads are never copied. Listing, pointers and lineage are answered from registry rows only — no payload is loaded to populate a
/// selector. Registration is an idempotent incremental sync (adapters per owner), so existing snapshots keep their ids and every module keeps its
/// own creation path.
/// </summary>
public sealed class SnapshotRegistryService(SnapshotRegistryDbContext db, IEnumerable<ISnapshotSource> sources, TimeProvider clock, ILogger<SnapshotRegistryService>? logger = null)
{
    public const int MetadataVersion = 1;
    private static readonly SemaphoreSlim SyncGate = new(1, 1);
    private static DateTimeOffset _lastSync = DateTimeOffset.MinValue;
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);

    static string Key(SnapshotType type, string id) => $"{type}|{id}";

    /// <summary>Registers what is new in every owner store (idempotent). A failing source is reported and the others still sync.</summary>
    public async Task<SnapshotSyncResult> SyncAsync(bool force, CancellationToken ct)
    {
        if (!force && clock.GetUtcNow() - _lastSync < SyncInterval) return new(0, 0, 0, [], []);
        await SyncGate.WaitAsync(ct);
        try
        {
            var existing = await db.Entries.ToListAsync(ct);
            var registered = existing.Select(e => $"{e.Type}|{e.Id}").ToHashSet(StringComparer.Ordinal);
            var running = existing.Where(e => e.Status == nameof(SnapshotProcessingStatus.Running)
                    || (e.Type == nameof(SnapshotType.SourceAnalysis) && e.SummaryJson is null && e.PayloadAvailable))
                .Select(e => $"{e.Type}|{e.Id}").ToHashSet(StringComparer.Ordinal);
            var byKey = existing.ToDictionary(e => $"{e.Type}|{e.Id}", StringComparer.Ordinal);
            var edgeKeys = (await db.Edges.Select(e => new { e.FromType, e.FromId, e.Kind, e.ToType, e.ToId }).ToListAsync(ct))
                .Select(e => $"{e.FromType}|{e.FromId}|{e.Kind}|{e.ToType}|{e.ToId}").ToHashSet(StringComparer.Ordinal);
            int added = 0, updated = 0, edges = 0;
            var names = new List<string>();
            var problems = new List<string>();
            var now = clock.GetUtcNow();
            foreach (var source in sources)
            {
                IReadOnlyList<(SnapshotMetadata Metadata, IReadOnlyList<SnapshotLineageEdge> Edges)> items;
                try { items = await source.ReadAsync(registered, running, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "Snapshot registry: source {Source} could not be read", source.Name);
                    problems.Add($"{source.Name}: could not be read ({ex.GetType().Name}). Its snapshots are not listed until it can be read.");
                    continue;
                }
                names.Add(source.Name);
                // A registered entry whose payload its owner no longer holds is marked unavailable (never deleted: its metadata and lineage stay, and
                // a frozen report that references it shows "historical evidence unavailable" instead of the latest snapshot).
                try
                {
                    if (await source.InventoryAsync(ct) is { } inventory)
                        foreach (var row in existing.Where(e => inventory.Types.Contains(e.Type) && e.PayloadAvailable && !inventory.Keys.Contains($"{e.Type}|{e.Id}")))
                        {
                            row.PayloadAvailable = false;
                            row.UpdatedAt = now;
                            updated++;
                            problems.Add($"{source.Name}: {row.Label} ({row.Id}) is registered but its payload is no longer stored; marked unavailable.");
                        }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "Snapshot registry: source {Source} inventory could not be read", source.Name);
                }
                foreach (var (m, lineage) in items)
                {
                    var key = Key(m.Type, m.Id);
                    if (byKey.TryGetValue(key, out var row))
                    {
                        // Only the processing status of a still-running snapshot (and a summary not stored yet) change; identity, timestamps and
                        // retention never do.
                        if (row.Status != m.Status.ToString()) { row.Status = m.Status.ToString(); row.UpdatedAt = now; updated++; }
                        if (row.SummaryJson is null && m.SummaryJson is not null) { row.SummaryJson = m.SummaryJson; row.UpdatedAt = now; updated++; }
                    }
                    else
                    {
                        row = new SnapshotEntryRecord
                        {
                            Type = m.Type.ToString(), Id = m.Id, Project = m.Project, Module = m.Module, Label = Trim(m.Label, 300), CreatedAt = m.CreatedAt, EffectiveAt = m.EffectiveAt,
                            SourceRevision = m.SourceRevision, AnalysisVersion = m.AnalysisVersion, StorageReference = m.StorageReference, ContentFingerprint = m.ContentFingerprint,
                            Status = m.Status.ToString(), Retention = m.Retention.ToString(), Frozen = m.Frozen, MetadataVersion = m.MetadataVersion,
                            PayloadAvailable = m.PayloadAvailable, SummaryJson = m.SummaryJson, RegisteredAt = now, UpdatedAt = now,
                        };
                        db.Entries.Add(row);
                        byKey[key] = row;
                        registered.Add(key);
                        added++;
                    }
                    foreach (var edge in lineage)
                    {
                        var edgeKey = $"{edge.From.Type}|{edge.From.Id}|{edge.Kind}|{edge.To.Type}|{edge.To.Id}";
                        if (!edgeKeys.Add(edgeKey)) continue;
                        db.Edges.Add(new SnapshotEdgeRecord
                        {
                            FromType = edge.From.Type.ToString(), FromId = edge.From.Id, Kind = edge.Kind.ToString(), ToType = edge.To.Type.ToString(), ToId = edge.To.Id,
                            RecordedBy = edge.RecordedBy, RecordedAt = now,
                        });
                        edges++;
                    }
                }
            }
            await db.SaveChangesAsync(ct);
            _lastSync = clock.GetUtcNow();
            return new(added, updated, edges, names, problems);
        }
        finally { SyncGate.Release(); }
    }

    /// <summary>
    /// Source Analysis selector list from registry rows only (newest first): each snapshot's stored summary plus the registry pointers it holds.
    /// No snapshot payload is loaded. Null when a registered snapshot has no stored summary yet (the caller then uses the legacy path).
    /// </summary>
    public async Task<IReadOnlyList<BirkNext.Integrations.IqrSourceSnapshotSummary>?> SourceAnalysisSummariesAsync(int take, CancellationToken ct)
    {
        await SyncAsync(true, ct); // a snapshot uploaded a moment ago must be listed at once (sync reads ids; only new payloads, once)
        var type = nameof(SnapshotType.SourceAnalysis);
        var rows = await db.Entries.AsNoTracking().Where(e => e.Type == type && e.PayloadAvailable)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(Math.Clamp(take, 1, 200)).Select(e => new { e.Id, e.Project, e.Module, e.SummaryJson }).ToListAsync(ct);
        if (rows.Any(r => r.SummaryJson is null)) return null;
        var pointers = await PointerMapAsync(rows.Select(r => (r.Project ?? "", type, r.Module)).Distinct().ToList(), ct);
        return [.. rows.Select(r => System.Text.Json.JsonSerializer.Deserialize<BirkNext.Integrations.IqrSourceSnapshotSummary>(r.SummaryJson!, SummaryJson)! with
        {
            RegistryPointers = [.. pointers.Where(p => p.Key.Type == type && p.Key.Module == r.Module && p.Key.Project == (r.Project ?? "") && p.Value == r.Id).Select(p => p.Key.Kind.ToString())],
        })];
    }

    private static readonly System.Text.Json.JsonSerializerOptions SummaryJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Test hook: the next <see cref="SyncAsync"/> with force=false runs again.</summary>
    internal static void ResetThrottle() => _lastSync = DateTimeOffset.MinValue;

    /// <summary>Metadata-only listing with filters, paging, sorting and the pointers each row currently holds. No payload is read.</summary>
    public async Task<SnapshotListPage> ListAsync(string? project, SnapshotType? type, string? module, SnapshotProcessingStatus? status, DateTimeOffset? from, DateTimeOffset? to,
        string? sort, int skip, int take, CancellationToken ct)
    {
        await SyncAsync(false, ct);
        var q = db.Entries.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(project)) q = q.Where(e => e.Project == project);
        if (type is { } t) q = q.Where(e => e.Type == t.ToString());
        if (!string.IsNullOrWhiteSpace(module)) q = q.Where(e => e.Module == module);
        if (status is { } s) q = q.Where(e => e.Status == s.ToString());
        if (from is { } f) q = q.Where(e => e.CreatedAt >= f);
        if (to is { } u) q = q.Where(e => e.CreatedAt <= u);
        var total = await q.CountAsync(ct);
        q = sort switch
        {
            "created" => q.OrderBy(e => e.CreatedAt),
            "effective" => q.OrderByDescending(e => e.EffectiveAt ?? e.CreatedAt),
            "label" => q.OrderBy(e => e.Label),
            _ => q.OrderByDescending(e => e.CreatedAt),
        };
        var rows = await q.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        var pointers = await PointerMapAsync(rows.Select(r => (r.Project ?? "", r.Type, r.Module)).Distinct().ToList(), ct);
        var ids = rows.Select(r => r.Id).ToList();
        var inbound = (await db.Edges.AsNoTracking().Where(e => ids.Contains(e.ToId)).Select(e => new { e.ToType, e.ToId }).ToListAsync(ct))
            .GroupBy(e => $"{e.ToType}|{e.ToId}").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new([.. rows.Select(r => new SnapshotListItem(View(r),
            [.. pointers.Where(p => p.Key.Project == (r.Project ?? "") && p.Key.Type == r.Type && p.Key.Module == r.Module && p.Value == r.Id).Select(p => p.Key.Kind)],
            inbound.GetValueOrDefault($"{r.Type}|{r.Id}")))], total, skip, take);
    }

    public async Task<SnapshotMetadata?> GetAsync(SnapshotType type, string id, CancellationToken ct) =>
        await FindAsync(type, id, ct) is { } row ? View(row) : null;

    /// <summary>An entry by id; an id not registered yet forces one sync first (a snapshot created seconds ago is never reported unknown).</summary>
    private async Task<SnapshotEntryRecord?> FindAsync(SnapshotType type, string id, CancellationToken ct)
    {
        var row = await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Type == type.ToString() && e.Id == id, ct);
        if (row is not null) return row;
        await SyncAsync(true, ct);
        return await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Type == type.ToString() && e.Id == id, ct);
    }

    /// <summary>Lineage both ways (upstream: what it was built from; downstream: what was built from it), walking stored edges only.</summary>
    public async Task<SnapshotLineageView?> LineageAsync(SnapshotType type, string id, int depth, CancellationToken ct)
    {
        await SyncAsync(false, ct);
        if (await GetAsync(type, id, ct) is not { } snapshot) return null;
        var up = new List<SnapshotLineageEdge>();
        var down = new List<SnapshotLineageEdge>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { Key(type, id) };
        var upFrontier = new List<(string Type, string Id)> { (type.ToString(), id) };
        var downFrontier = new List<(string Type, string Id)> { (type.ToString(), id) };
        for (var level = 0; level < Math.Clamp(depth, 1, 6); level++)
        {
            var upIds = upFrontier.Select(f => f.Id).ToList();
            var upEdges = await db.Edges.AsNoTracking().Where(e => upIds.Contains(e.FromId)).ToListAsync(ct);
            upEdges = [.. upEdges.Where(e => upFrontier.Contains((e.FromType, e.FromId)))];
            up.AddRange(upEdges.Select(Edge));
            upFrontier = [.. upEdges.Select(e => (e.ToType, e.ToId)).Where(x => seen.Add($"{x.ToType}|{x.ToId}"))];

            var downIds = downFrontier.Select(f => f.Id).ToList();
            var downEdges = await db.Edges.AsNoTracking().Where(e => downIds.Contains(e.ToId)).ToListAsync(ct);
            downEdges = [.. downEdges.Where(e => downFrontier.Contains((e.ToType, e.ToId)))];
            down.AddRange(downEdges.Select(Edge));
            downFrontier = [.. downEdges.Select(e => (e.FromType, e.FromId)).Where(x => seen.Add($"{x.FromType}|{x.FromId}"))];
            if (upFrontier.Count == 0 && downFrontier.Count == 0) break;
        }
        var relatedKeys = up.Select(e => e.To).Concat(down.Select(e => e.From)).Select(r => (r.Type.ToString(), r.Id)).Distinct().ToList();
        var relatedIds = relatedKeys.Select(k => k.Id).ToList();
        var related = (await db.Entries.AsNoTracking().Where(e => relatedIds.Contains(e.Id)).ToListAsync(ct))
            .Where(e => relatedKeys.Contains((e.Type, e.Id))).Select(View).ToList();
        return new(snapshot, up, down, related);
    }

    /// <summary>
    /// Pointers of one project/type/module: LatestCreated and LatestCompleted are computed from the registry (never assumed valid or approved);
    /// CurrentSelected and CurrentApproved exist only when a person set them. Viewing an older snapshot never moves a pointer.
    /// </summary>
    public async Task<IReadOnlyList<SnapshotPointerView>> PointersAsync(string? project, SnapshotType type, string module, CancellationToken ct)
    {
        await SyncAsync(false, ct);
        var p = project ?? "";
        var entries = db.Entries.AsNoTracking().Where(e => (e.Project ?? "") == p && e.Type == type.ToString() && e.Module == module);
        var latest = await entries.OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct);
        var completed = await entries.Where(e => e.Status == nameof(SnapshotProcessingStatus.Completed)).OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct);
        var stored = await db.Pointers.AsNoTracking().Where(x => x.Project == p && x.Type == type.ToString() && x.Module == module).ToListAsync(ct);
        SnapshotPointerView Stored(SnapshotPointerKind kind) => stored.FirstOrDefault(x => x.Kind == kind.ToString()) is { } x
            ? new(project, type, module, kind, new(type, x.SnapshotId), x.SetAt, x.SetBy) : new(project, type, module, kind, null, null, null);
        return
        [
            new(project, type, module, SnapshotPointerKind.LatestCreated, latest is null ? null : View(latest).Reference, latest?.CreatedAt, "computed"),
            new(project, type, module, SnapshotPointerKind.LatestCompleted, completed is null ? null : View(completed).Reference, completed?.CreatedAt, "computed"),
            Stored(SnapshotPointerKind.CurrentSelected),
            Stored(SnapshotPointerKind.CurrentApproved),
        ];
    }

    /// <summary>Sets CurrentSelected / CurrentApproved explicitly. The snapshot must exist in the same project/type/module and (for approval) be Completed.</summary>
    public async Task<string?> SetPointerAsync(SnapshotPointerRequest request, string by, CancellationToken ct)
    {
        if (request.Kind is SnapshotPointerKind.LatestCreated or SnapshotPointerKind.LatestCompleted) return "Latest pointers are computed and cannot be set.";
        var p = request.Project ?? "";
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Type == request.Type.ToString() && e.Id == request.SnapshotId, ct);
        if (entry is null) return "Unknown snapshot.";
        if ((entry.Project ?? "") != p || entry.Module != request.Module) return "The snapshot belongs to another project or module; pointers never cross scopes.";
        if (request.Kind == SnapshotPointerKind.CurrentApproved && entry.Status != nameof(SnapshotProcessingStatus.Completed))
            return "Only a completed snapshot can be approved as current.";
        var row = await db.Pointers.FirstOrDefaultAsync(x => x.Project == p && x.Type == entry.Type && x.Module == entry.Module && x.Kind == request.Kind.ToString(), ct);
        if (row is null) db.Pointers.Add(row = new SnapshotPointerRecord { Project = p, Type = entry.Type, Module = entry.Module, Kind = request.Kind.ToString() });
        row.SnapshotId = entry.Id;
        row.SetAt = clock.GetUtcNow();
        row.SetBy = Trim(by, 200);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Sets a retention class explicitly. Frozen report evidence cannot be downgraded; Protected is set and removed only here.</summary>
    public async Task<string?> SetRetentionAsync(SnapshotType type, string id, SnapshotRetentionClass retention, CancellationToken ct)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Type == type.ToString() && e.Id == id, ct);
        if (entry is null) return "Unknown snapshot.";
        if (entry.Retention == nameof(SnapshotRetentionClass.FrozenReportEvidence) && retention != SnapshotRetentionClass.Protected)
            return "Frozen report evidence keeps its retention class (it can only be protected further).";
        if (retention == SnapshotRetentionClass.Temporary) return "A stored snapshot cannot be made temporary.";
        entry.Retention = retention.ToString();
        entry.RetentionSetByUser = true;
        entry.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// The deletion check every delete path consults: Protected or frozen snapshots and inbound references from frozen evidence block; other inbound
    /// references are listed. <paramref name="extraNotChecked"/> names owner stores whose references cannot be checked (never "unreferenced").
    /// </summary>
    public async Task<SnapshotDeletionCheck?> DeletionCheckAsync(SnapshotType type, string id, IReadOnlyList<string>? extraNotChecked, CancellationToken ct)
    {
        await SyncAsync(false, ct);
        var entry = await FindAsync(type, id, ct);
        if (entry is null) return null;
        var blocking = new List<string>();
        var notes = new List<string>();
        if (entry.Retention == nameof(SnapshotRetentionClass.Protected)) blocking.Add("Protected: remove the protection first.");
        if (entry.Frozen && type is SnapshotType.GeneratedReport or SnapshotType.Interpretation) blocking.Add("Frozen evidence is never deleted through the registry.");
        var inbound = await db.Edges.AsNoTracking().Where(e => e.ToType == entry.Type && e.ToId == entry.Id).ToListAsync(ct);
        var fromIds = inbound.Select(e => e.FromId).ToList();
        var referrers = await db.Entries.AsNoTracking().Where(e => fromIds.Contains(e.Id)).ToListAsync(ct);
        foreach (var edge in inbound)
        {
            var from = referrers.FirstOrDefault(r => r.Type == edge.FromType && r.Id == edge.FromId);
            var label = from is null ? $"{edge.FromType} {edge.FromId}" : $"{from.Label} ({edge.FromType})";
            if (from?.Retention == nameof(SnapshotRetentionClass.FrozenReportEvidence))
                notes.Add($"{label} {Verb(edge.Kind)} it; the report keeps its frozen copy, its live link becomes empty.");
            else if (from?.Retention == nameof(SnapshotRetentionClass.Protected) || from?.Frozen == true)
                blocking.Add($"{label} {Verb(edge.Kind)} it and is frozen/protected.");
            else notes.Add($"{label} {Verb(edge.Kind)} it.");
        }
        return new(View(entry).Reference, blocking.Count == 0, blocking, notes, extraNotChecked ?? []);
    }

    static string Verb(string kind) => kind switch
    {
        nameof(SnapshotLineageKind.ComparedAgainst) => "compares against",
        nameof(SnapshotLineageKind.GeneratedReportFrom) => "was generated from",
        nameof(SnapshotLineageKind.InterpretedUsing) => "was interpreted from",
        nameof(SnapshotLineageKind.ImportedFrom) => "was imported from",
        nameof(SnapshotLineageKind.Supersedes) => "supersedes",
        _ => "is derived from",
    };

    async Task<Dictionary<(string Project, string Type, string Module, SnapshotPointerKind Kind), string>> PointerMapAsync(
        IReadOnlyList<(string Project, string Type, string Module)> scopes, CancellationToken ct)
    {
        var map = new Dictionary<(string, string, string, SnapshotPointerKind), string>();
        foreach (var (project, type, module) in scopes)
        {
            var entries = db.Entries.AsNoTracking().Where(e => (e.Project ?? "") == project && e.Type == type && e.Module == module);
            if (await entries.OrderByDescending(e => e.CreatedAt).Select(e => e.Id).FirstOrDefaultAsync(ct) is { } latest) map[(project, type, module, SnapshotPointerKind.LatestCreated)] = latest;
            if (await entries.Where(e => e.Status == nameof(SnapshotProcessingStatus.Completed)).OrderByDescending(e => e.CreatedAt).Select(e => e.Id).FirstOrDefaultAsync(ct) is { } done)
                map[(project, type, module, SnapshotPointerKind.LatestCompleted)] = done;
            foreach (var p in await db.Pointers.AsNoTracking().Where(x => x.Project == project && x.Type == type && x.Module == module).ToListAsync(ct))
                if (Enum.TryParse<SnapshotPointerKind>(p.Kind, out var kind)) map[(project, type, module, kind)] = p.SnapshotId;
        }
        return map;
    }

    static SnapshotLineageEdge Edge(SnapshotEdgeRecord e) =>
        new(new(Enum.Parse<SnapshotType>(e.FromType), e.FromId), Enum.Parse<SnapshotLineageKind>(e.Kind), new(Enum.Parse<SnapshotType>(e.ToType), e.ToId), e.RecordedBy);

    public static SnapshotMetadata View(SnapshotEntryRecord e) => new(Enum.Parse<SnapshotType>(e.Type), e.Id, e.Project, e.Module, e.Label, e.CreatedAt, e.EffectiveAt,
        e.SourceRevision, e.AnalysisVersion, e.StorageReference, e.ContentFingerprint, Enum.Parse<SnapshotProcessingStatus>(e.Status), Enum.Parse<SnapshotRetentionClass>(e.Retention),
        e.Frozen, e.MetadataVersion, e.PayloadAvailable);

    static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Reads only the named properties of a stored JSON payload (case-insensitive), without materialising the domain model.</summary>
internal static class JsonMetadata
{
    public static JsonElement? Get(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            var found = false;
            foreach (var p in current.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { current = p.Value; found = true; break; }
            if (!found) return null;
        }
        return current;
    }

    public static string? String(JsonElement root, params string[] path) =>
        Get(root, path) is { } v ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    public static IEnumerable<JsonElement> Array(JsonElement root, params string[] path) =>
        Get(root, path) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
}
