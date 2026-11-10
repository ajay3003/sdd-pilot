using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ExecutionEvidence;
using BirkNext.Parquet;
using Microsoft.EntityFrameworkCore;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.Parquet;

/// <summary>
/// Parquet data explorer sessions. An opened file is copied to a temporary, BirkNext-named file under App_Data/parquet-sessions (never the uploaded
/// name, never a caller path) and expires after <see cref="SessionLifetime"/>; closing removes it. Durable evidence holds metadata, fingerprints,
/// rule results and comparison counts — never row values. Every scan is bounded; filters on a page are labelled as page-only.
/// </summary>
public sealed partial class ParquetInspectionService(AppDbContext db, ExecutionEvidenceStore executions, IHostEnvironment environment, TimeProvider? clock = null)
{
    public const long MaxFileBytes = 64L * 1024 * 1024;
    public const long MaxScanRows = 2_000_000;
    public const int MaxPageRows = 200;
    public const int MaxDistinctTracked = 100_000;
    public const int MaxRecordDiffs = 500;
    /// <summary>Live temporary sessions kept on disk; opening one more closes the oldest (disk is bounded: at most 20 x 64 MB).</summary>
    public const int MaxLiveSessions = 20;
    /// <summary>Decoded files kept in memory (each holds its bytes): the cache never grows past this.</summary>
    public const int MaxCachedFiles = 4;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ConcurrentDictionary<Guid, (ParquetFile File, ParquetOverview Overview)> Cache = new();
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private string Root => Path.Combine(environment.ContentRootPath, "App_Data", "parquet-sessions");
    private string FilePath(Guid id) => Path.Combine(Root, id.ToString("N") + ".parquet");
    private string MetaPath(Guid id) => Path.Combine(Root, id.ToString("N") + ".json");

    // ── Sessions ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<(ParquetOverview? Overview, string? Error)> OpenAsync(string fileName, Stream content, long? declaredLength, CancellationToken ct)
    {
        Cleanup();
        if (declaredLength > MaxFileBytes) return (null, $"The file exceeds {MaxFileBytes / 1024 / 1024} MB.");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes) return (null, $"The file exceeds {MaxFileBytes / 1024 / 1024} MB.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        ParquetFile file;
        try { file = ParquetFile.Open(bytes); }
        catch (ParquetFormatException ex) { return (null, ex.Message); }
        var id = Guid.NewGuid();
        var name = DisplayName(fileName);
        var overview = Overview(id, name, bytes, file, Now);
        Directory.CreateDirectory(Root);
        EnforceSessionCap();
        await File.WriteAllBytesAsync(FilePath(id), bytes, ct);
        await File.WriteAllTextAsync(MetaPath(id), JsonSerializer.Serialize(new SessionMeta(name, overview.OpenedAt, overview.ExpiresAt), Json), ct);
        Evict();
        Cache[id] = (file, overview);
        return (overview, null);
    }

    /// <summary>Keeps at most <see cref="MaxCachedFiles"/> decoded files in memory (a reload from disk restores an evicted one).</summary>
    private static void Evict()
    {
        while (Cache.Count >= MaxCachedFiles)
        {
            var oldest = Cache.OrderBy(kv => kv.Value.Overview.OpenedAt).First().Key;
            Cache.TryRemove(oldest, out _);
        }
    }

    /// <summary>Closes the oldest live sessions so that a new one stays within <see cref="MaxLiveSessions"/>.</summary>
    private void EnforceSessionCap()
    {
        var live = LiveSessions();
        foreach (var (id, _) in live.OrderBy(s => s.Meta.OpenedAt).Take(Math.Max(0, live.Count - (MaxLiveSessions - 1)))) Close(id);
    }

    private List<(Guid Id, SessionMeta Meta)> LiveSessions()
    {
        var result = new List<(Guid, SessionMeta)>();
        if (!Directory.Exists(Root)) return result;
        foreach (var meta in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(meta), Json) is { } m && m.ExpiresAt > Now
                    && Guid.TryParseExact(Path.GetFileNameWithoutExtension(meta), "N", out var id)) result.Add((id, m));
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return result;
    }

    private sealed record SessionMeta(string FileName, DateTimeOffset OpenedAt, DateTimeOffset ExpiresAt);

    public (ParquetFile File, ParquetOverview Overview)? Session(Guid id)
    {
        if (Cache.TryGetValue(id, out var cached))
        {
            if (cached.Overview.ExpiresAt > Now) return cached;
            Close(id);
            return null;
        }
        if (!File.Exists(FilePath(id)) || !File.Exists(MetaPath(id))) return null;
        var meta = JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(MetaPath(id)), Json)!;
        if (meta.ExpiresAt <= Now) { Close(id); return null; }
        var bytes = File.ReadAllBytes(FilePath(id));
        var file = ParquetFile.Open(bytes);
        var overview = Overview(id, meta.FileName, bytes, file, meta.OpenedAt) with { ExpiresAt = meta.ExpiresAt };
        Evict();
        Cache[id] = (file, overview);
        return (file, overview);
    }

    /// <summary>Removes the session's temporary copy. Never touches the tester's original file (BirkNext never had its path).</summary>
    public bool Close(Guid id)
    {
        Cache.TryRemove(id, out _);
        var existed = File.Exists(FilePath(id));
        foreach (var p in new[] { FilePath(id), MetaPath(id), ManifestPath(id) }) try { File.Delete(p); } catch (IOException) { }
        return existed;
    }

    private void Cleanup()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var meta in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                var m = JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(meta), Json);
                if (m is null || m.ExpiresAt <= Now) Close(Guid.ParseExact(Path.GetFileNameWithoutExtension(meta), "N"));
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException) { }
        }
    }

    private static string DisplayName(string? name)
    {
        var last = (name ?? "").Split('/', '\\').Last();
        var clean = new string(last.Where(c => !char.IsControl(c) && c is not ('<' or '>' or '"')).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "upload.parquet" : clean.Length > 200 ? clean[..200] : clean;
    }

    // ── Overview and schema ──────────────────────────────────────────────────────────────────────────────────────────────

    private static ParquetOverview Overview(Guid id, string name, byte[] bytes, ParquetFile file, DateTimeOffset openedAt)
    {
        var columns = file.Leaves.Select(leaf =>
        {
            var chunks = file.RowGroups.Select(g => g.Chunks[leaf.Index]).ToList();
            var withStats = chunks.Where(c => c?.Stats is not null).ToList();
            var nullsKnown = chunks.All(c => c?.Stats?.NullCount is not null) && chunks.Count > 0;
            var mins = withStats.Select(c => file.StatValue(leaf.Index, c!.Stats!.Min)).ToList();
            var maxs = withStats.Select(c => file.StatValue(leaf.Index, c!.Stats!.Max)).ToList();
            var minMaxKnown = chunks.Count > 0 && withStats.Count == chunks.Count && mins.All(v => v is not null) && maxs.All(v => v is not null);
            return new ParquetColumnSummary(leaf.Path, ParquetFile.PhysicalNames[leaf.Physical], ParquetFile.LogicalName(leaf.Node), leaf.MaxDefinition > 0,
                nullsKnown ? chunks.Sum(c => c!.Stats!.NullCount!.Value) : null, nullsKnown ? ParquetFigure.FromMetadata : ParquetFigure.NotAvailable,
                minMaxKnown ? ParquetFile.Format(mins.Min(Comparer)) : null, minMaxKnown ? ParquetFile.Format(maxs.Max(Comparer)) : null,
                minMaxKnown ? ParquetFigure.FromMetadata : ParquetFigure.NotAvailable,
                chunks.Where(c => c is not null).Select(c => ParquetFile.CodecNames.GetValueOrDefault(c!.Codec, c.Codec.ToString(CultureInfo.InvariantCulture))).Distinct().ToList(),
                chunks.Where(c => c is not null).SelectMany(c => c!.Encodings).Distinct().Select(e => ParquetFile.EncodingNames.GetValueOrDefault(e, e.ToString(CultureInfo.InvariantCulture))).ToList(),
                chunks.Sum(c => c?.Compressed ?? 0), chunks.Sum(c => c?.Uncompressed ?? 0), leaf.NotReadable is null, leaf.NotReadable);
        }).ToList();
        var limitations = new List<string>
        {
            "Statistics shown on Overview come from the file's metadata (as written by its producer); Profile computes exact figures with a full scan.",
        };
        if (columns.Any(c => !c.Readable)) limitations.Add($"{columns.Count(c => !c.Readable)} column(s) cannot be read by this explorer (see Schema); their metadata is shown.");
        return new ParquetOverview
        {
            SessionId = id, FileName = name, SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), FormatVersion = file.Version,
            CreatedBy = file.CreatedBy, Rows = file.Rows, RowGroups = [.. file.RowGroups.Select(g => new ParquetRowGroupSummary(g.Index, g.Rows, g.TotalBytes, g.Chunks.Sum(c => c?.Compressed ?? 0)))],
            Columns = columns, Schema = SchemaNode(file, file.Root), SchemaFingerprint = Fingerprint(file),
            KeyValueMetadata = [.. file.KeyValues.Take(50).Select(kv => new ParquetKeyValue(kv.Key, kv.Value is { Length: > 300 } v ? v[..300] + "…" : kv.Value))],
            Limitations = limitations, OpenedAt = openedAt, ExpiresAt = openedAt + SessionLifetime,
        };
    }

    private static ParquetSchemaNode SchemaNode(ParquetFile file, ParquetFile.Node n)
    {
        var leaf = n.IsGroup ? null : file.Leaves.FirstOrDefault(l => l.Path == n.Path);
        return new ParquetSchemaNode
        {
            Path = n.Path, Name = n.Name, PhysicalType = n.Physical is { } p && p is >= 0 and <= 7 ? ParquetFile.PhysicalNames[p] : null, LogicalType = ParquetFile.LogicalName(n),
            Repetition = n.Repetition switch { 0 => "REQUIRED", 1 => "OPTIONAL", _ => "REPEATED" }, TypeLength = n.TypeLength, IsGroup = n.IsGroup,
            Readable = leaf is { NotReadable: null }, NotReadableReason = n.IsGroup ? null : leaf?.NotReadable,
            Children = [.. n.Children.Select(c => SchemaNode(file, c))],
        };
    }

    private static string Fingerprint(ParquetFile file)
    {
        var text = string.Join("\n", file.Leaves.Select(l => $"{l.Path}|{ParquetFile.PhysicalNames[l.Physical]}|{ParquetFile.LogicalName(l.Node)}|{l.Node.Repetition}|{l.MaxDefinition}|{l.MaxRepetition}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }

    // ── Rows, filters ────────────────────────────────────────────────────────────────────────────────────────────────────

    public (ParquetRowsResult? Result, string? Error) Rows(Guid id, ParquetRowsRequest request, CancellationToken ct)
    {
        if (Session(id) is not { } s) return (null, "The Parquet session has expired; open the file again.");
        var file = s.File;
        var count = Math.Clamp(request.Count, 1, MaxPageRows);
        var columns = request.Columns.Count > 0 ? request.Columns : file.Leaves.Where(l => l.NotReadable is null).Take(12).Select(l => l.Path).ToList();
        var leaves = new List<ParquetFile.Leaf>();
        foreach (var c in columns.Distinct())
        {
            if (file.Leaves.FirstOrDefault(l => l.Path == c) is not { } leaf) return (null, $"Unknown column {Clip(c)}.");
            if (leaf.NotReadable is { } why) return (null, $"{leaf.Path}: {why}");
            leaves.Add(leaf);
        }
        var filterLeaves = new Dictionary<string, ParquetFile.Leaf>();
        foreach (var f in request.Filters)
        {
            if (file.Leaves.FirstOrDefault(l => l.Path == f.Column) is not { } leaf || leaf.NotReadable is not null) return (null, $"The filter column {Clip(f.Column)} cannot be read.");
            filterLeaves[f.Column] = leaf;
        }
        try
        {
            if (request.Filters.Count == 0 || !request.FullScan)
            {
                var offset = Math.Clamp(request.Offset, 0, Math.Max(0, file.Rows - 1));
                var block = Read(file, leaves.Concat(filterLeaves.Values).DistinctBy(l => l.Index).ToList(), offset, count, ct);
                var keep = Enumerable.Range(0, block.Rows).Where(r => request.Filters.All(f => Matches(block.Values[filterLeaves[f.Column].Index][r], f))).ToList();
                return (new ParquetRowsResult
                {
                    Columns = [.. leaves.Select(l => l.Path)], Rows = [.. keep.Select(r => leaves.Select(l => ParquetFile.Format(block.Values[l.Index][r])).ToList())],
                    RowNumbers = [.. keep.Select(r => offset + r + 1)], TotalRows = file.Rows, ScannedRows = block.Rows,
                    MatchingRows = request.Filters.Count == 0 ? null : keep.Count,
                    Scope = request.Filters.Count == 0 ? $"Rows {offset + 1}–{offset + block.Rows} of {file.Rows}"
                        : $"Filters applied to rows {offset + 1}–{offset + block.Rows} only (this page), not to the whole file",
                }, null);
            }
            // Explicit full scan: every row group in order, counting every match; returns the first page of matches after Offset.
            if (file.Rows > MaxScanRows) return (null, $"A full scan is limited to {N(MaxScanRows)} rows; this file has {N(file.Rows)}.");
            var needed = leaves.Concat(filterLeaves.Values).DistinctBy(l => l.Index).ToList();
            var result = new List<List<string?>>(); var numbers = new List<long>();
            long matches = 0, scanned = 0, rowBase = 0;
            foreach (var group in file.RowGroups)
            {
                ct.ThrowIfCancellationRequested();
                var values = needed.ToDictionary(l => l.Index, l => file.ReadColumn(group.Index, l.Index));
                for (var r = 0; r < group.Rows; r++)
                {
                    if (!request.Filters.All(f => Matches(values[filterLeaves[f.Column].Index][r], f))) continue;
                    if (matches >= request.Offset && result.Count < count)
                    {
                        result.Add([.. leaves.Select(l => ParquetFile.Format(values[l.Index][r]))]);
                        numbers.Add(rowBase + r + 1);
                    }
                    matches++;
                }
                scanned += group.Rows; rowBase += group.Rows;
            }
            return (new ParquetRowsResult
            {
                Columns = [.. leaves.Select(l => l.Path)], Rows = result, RowNumbers = numbers, TotalRows = file.Rows, ScannedRows = scanned, MatchingRows = matches,
                Scope = $"Full scan of all {N(scanned)} rows: {N(matches)} match", Truncated = matches > request.Offset + result.Count,
            }, null);
        }
        catch (ParquetFormatException ex) { return (null, ex.Message); }
    }

    private sealed record Block(int Rows, Dictionary<int, object?[]> Values);

    /// <summary>Reads [offset, offset+count) of the given columns, decoding only the row groups that overlap the range.</summary>
    private static Block Read(ParquetFile file, List<ParquetFile.Leaf> leaves, long offset, int count, CancellationToken ct)
    {
        var values = leaves.ToDictionary(l => l.Index, _ => new List<object?>());
        long start = 0;
        var taken = 0;
        foreach (var group in file.RowGroups)
        {
            var end = start + group.Rows;
            if (end > offset && start < offset + count && group.Rows > 0)
            {
                ct.ThrowIfCancellationRequested();
                var from = (int)Math.Max(0, offset - start);
                var to = (int)Math.Min(group.Rows, offset + count - start);
                foreach (var leaf in leaves)
                {
                    var column = file.ReadColumn(group.Index, leaf.Index);
                    values[leaf.Index].AddRange(column[from..to]);
                }
                taken += to - from;
            }
            start = end;
            if (start >= offset + count) break;
        }
        return new Block(taken, values.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    /// <summary>Typed comparison: numbers as numbers, dates and timestamps chronologically, decimals exactly, strings ordinally.</summary>
    internal static bool Matches(object? value, ParquetFilter f)
    {
        switch (f.Operator)
        {
            case ParquetFilterOperator.IsNull: return value is null;
            case ParquetFilterOperator.NotNull: return value is not null;
        }
        if (value is null) return (f.Operator == ParquetFilterOperator.NotEquals && f.Value is not null) || f.Operator == ParquetFilterOperator.NotIn;
        return f.Operator switch
        {
            ParquetFilterOperator.Equals => Compare(value, f.Value) == 0,
            ParquetFilterOperator.NotEquals => Compare(value, f.Value) != 0,
            ParquetFilterOperator.Contains => f.Value is { Length: > 0 } needle && ParquetFile.Format(value)!.Contains(needle, StringComparison.OrdinalIgnoreCase),
            ParquetFilterOperator.GreaterThan => Compare(value, f.Value) > 0,
            ParquetFilterOperator.LessThan => Compare(value, f.Value) is < 0 and not int.MinValue,
            ParquetFilterOperator.Between => Compare(value, f.Value) is >= 0 and not int.MinValue && Compare(value, f.Value2) is <= 0 and not int.MinValue,
            ParquetFilterOperator.In => (f.Values ?? []).Any(v => Compare(value, v) == 0),
            ParquetFilterOperator.NotIn => !(f.Values ?? []).Any(v => Compare(value, v) == 0),
            _ => false,
        };
    }

    /// <summary>-1/0/1, or int.MinValue when the text cannot be read as the column's type (never a match).</summary>
    internal static int Compare(object value, string? text)
    {
        if (text is null) return int.MinValue;
        var inv = CultureInfo.InvariantCulture;
        return value switch
        {
            int or long or float or double when double.TryParse(text, NumberStyles.Float, inv, out var d) => Convert.ToDouble(value, inv).CompareTo(d),
            decimal m when decimal.TryParse(text, NumberStyles.Number, inv, out var dm) => m.CompareTo(dm),
            DateOnly date when DateOnly.TryParse(text, inv, DateTimeStyles.None, out var dd) => date.CompareTo(dd),
            DateTimeOffset t when DateTimeOffset.TryParse(text, inv, DateTimeStyles.AssumeUniversal, out var dt) => t.CompareTo(dt),
            bool b when bool.TryParse(text, out var bb) => b.CompareTo(bb),
            string s => Math.Sign(string.CompareOrdinal(s, text)),
            int or long or float or double or decimal or DateOnly or DateTimeOffset or bool => int.MinValue,
            _ => Math.Sign(string.CompareOrdinal(ParquetFile.Format(value), text)),
        };
    }

    private static readonly IComparer<object?> Comparer = Comparer<object?>.Create((a, b) =>
        a is null ? (b is null ? 0 : -1) : b is null ? 1 : a is IComparable ca && a.GetType() == b.GetType() ? ca.CompareTo(b) : string.CompareOrdinal(ParquetFile.Format(a), ParquetFile.Format(b)));

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Clip(string? s) => new((s ?? "").Where(c => !char.IsControl(c)).Take(100).ToArray());

    // ── Profile (explicit full scan) ─────────────────────────────────────────────────────────────────────────────────────

    public (ParquetProfile? Profile, string? Error) Profile(Guid id, List<string>? columns, CancellationToken ct)
    {
        if (Session(id) is not { } s) return (null, "The Parquet session has expired; open the file again.");
        var file = s.File;
        if (file.Rows > MaxScanRows) return (null, $"A full scan is limited to {N(MaxScanRows)} rows; this file has {N(file.Rows)}.");
        var leaves = file.Leaves.Where(l => l.NotReadable is null && (columns is not { Count: > 0 } || columns.Contains(l.Path))).ToList();
        var watch = Stopwatch.StartNew();
        var acc = leaves.ToDictionary(l => l.Index, _ => new Acc());
        try
        {
            foreach (var group in file.RowGroups)
                foreach (var leaf in leaves)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var v in file.ReadColumn(group.Index, leaf.Index)) acc[leaf.Index].Add(v);
                }
        }
        catch (ParquetFormatException ex) { return (null, ex.Message); }
        var profiles = leaves.Select(l =>
        {
            var a = acc[l.Index];
            return new ParquetColumnProfile(l.Path, a.Rows, a.Nulls, a.Rows - a.Nulls, a.DistinctCapped ? null : a.Distinct.Count,
                a.DistinctCapped ? $"More than {N(MaxDistinctTracked)} (counting stopped)" : "Exact", ParquetFile.Format(a.Min), ParquetFile.Format(a.Max),
                a.NumericCount > 0 ? Math.Round(a.Sum / a.NumericCount, 6) : null,
                l.Physical is 6 or 7 && ParquetFile.LogicalName(l.Node) is not ("STRING" or "ENUM" or "JSON") ? "Binary values: counts only." : "Exact (full scan).");
        }).ToList();
        return (new ParquetProfile(profiles, file.Rows, $"Full scan of all {N(file.Rows)} rows", Math.Round(watch.Elapsed.TotalSeconds, 2)), null);
    }

    private sealed class Acc
    {
        public long Rows, Nulls, NumericCount;
        public double Sum;
        public object? Min, Max;
        public readonly HashSet<string> Distinct = new(StringComparer.Ordinal);
        public bool DistinctCapped;
        public void Add(object? v)
        {
            Rows++;
            if (v is null) { Nulls++; return; }
            if (!DistinctCapped) { Distinct.Add(ParquetFile.Format(v) ?? ""); if (Distinct.Count > MaxDistinctTracked) { DistinctCapped = true; Distinct.Clear(); } }
            if (v is not byte[])
            {
                if (Min is null || Comparer.Compare(v, Min) < 0) Min = v;
                if (Max is null || Comparer.Compare(v, Max) > 0) Max = v;
            }
            if (v is int or long or float or double or decimal) { Sum += Convert.ToDouble(v, CultureInfo.InvariantCulture); NumericCount++; }
        }
    }

    // ── Validation ───────────────────────────────────────────────────────────────────────────────────────────────────────

    // Validation lives in ParquetInspectionService.Quality.cs (one engine for user, schema- and contract-derived rules).

    // ── Comparison ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public (ParquetComparison? Comparison, string? Error) Compare(Guid baselineId, Guid currentId, string? keyColumn, CancellationToken ct) =>
        CompareByKeys(baselineId, currentId, string.IsNullOrWhiteSpace(keyColumn) ? [] : [keyColumn], ct);

    /// <summary>Record comparison by an explicit (composite) key; row order is never identity; duplicate keys are Ambiguous.</summary>
    public (ParquetComparison? Comparison, string? Error) CompareByKeys(Guid baselineId, Guid currentId, IReadOnlyList<string> keyColumns, CancellationToken ct)
    {
        if (Session(baselineId) is not { } b || Session(currentId) is not { } c) return (null, "A Parquet session has expired; open the files again.");
        var schema = SchemaChanges(b.File, c.File);
        var keys = keyColumns.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
        var limitations = new List<string> { "Row order is not identity: records are matched only by the chosen key column(s)." };
        var result = new ParquetComparison
        {
            BaselineSessionId = baselineId, CurrentSessionId = currentId, BaselineFile = b.Overview.FileName, CurrentFile = c.Overview.FileName,
            SchemaChanges = schema, BaselineRows = b.File.Rows, CurrentRows = c.File.Rows, KeyColumn = keys.Count == 1 ? keys[0] : null, KeyColumns = keys,
        };
        if (keys.Count == 0)
            return (result with { Limitations = [.. limitations, "No key column chosen: schema and row counts only; record-level differences are not available."] }, null);
        var bKeys = new List<ParquetFile.Leaf>(); var cKeys = new List<ParquetFile.Leaf>();
        foreach (var k in keys)
        {
            var bk = b.File.Leaves.FirstOrDefault(l => l.Path == k); var ck = c.File.Leaves.FirstOrDefault(l => l.Path == k);
            if (bk is null || ck is null) return (null, $"The key column {Clip(k)} is not in both files.");
            if (bk.NotReadable is not null || ck.NotReadable is not null) return (null, $"The key column {Clip(k)} cannot be read.");
            bKeys.Add(bk); cKeys.Add(ck);
        }
        if (b.File.Rows > MaxScanRows / 4 || c.File.Rows > MaxScanRows / 4) return (null, $"Record comparison is limited to {N(MaxScanRows / 4)} rows per file.");
        var shared = b.File.Leaves.Where(l => l.NotReadable is null && !keys.Contains(l.Path))
            .Select(l => (Baseline: l, Current: c.File.Leaves.FirstOrDefault(x => x.Path == l.Path && x.NotReadable is null))).Where(p => p.Current is not null).ToList();
        try
        {
            var (bRows, bDup) = Records(b.File, bKeys, shared.Select(p => p.Baseline).ToList(), ct);
            var (cRows, cDup) = Records(c.File, cKeys, shared.Select(p => p.Current!).ToList(), ct);
            var counts = new Dictionary<string, long> { ["Added"] = 0, ["Removed"] = 0, ["Changed"] = 0, ["Unchanged"] = 0, ["Ambiguous"] = 0 };
            var records = new List<ParquetRecordDiff>();
            void Add(string key, ParquetRecordChange change, List<string> fields)
            {
                counts[change.ToString()]++;
                if (change != ParquetRecordChange.Unchanged && records.Count < MaxRecordDiffs) records.Add(new ParquetRecordDiff(key, change, fields));
            }
            foreach (var (key, row) in bRows.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (bDup.Contains(key) || cDup.Contains(key)) { Add(key, ParquetRecordChange.Ambiguous, []); continue; }
                if (!cRows.TryGetValue(key, out var other)) { Add(key, ParquetRecordChange.Removed, []); continue; }
                var changed = shared.Select((p, i) => (p.Baseline.Path, i)).Where(x => !string.Equals(row[x.i], other[x.i], StringComparison.Ordinal)).Select(x => x.Path).ToList();
                Add(key, changed.Count == 0 ? ParquetRecordChange.Unchanged : ParquetRecordChange.Changed, changed);
            }
            foreach (var key in cRows.Keys.Where(k => !bRows.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
                Add(key, cDup.Contains(key) ? ParquetRecordChange.Ambiguous : ParquetRecordChange.Added, []);
            if (records.Count >= MaxRecordDiffs) limitations.Add($"Only the first {MaxRecordDiffs} differing records are listed; the counts are complete.");
            limitations.Add($"Fields compared: {shared.Count} column(s) present and readable in both files; columns added or removed are schema changes, not record changes. Values are not shown.");
            return (result with { RecordCounts = counts, Records = records, DuplicateKeysBaseline = bDup.Count, DuplicateKeysCurrent = cDup.Count, Limitations = limitations }, null);
        }
        catch (ParquetFormatException ex) { return (null, ex.Message); }
    }

    private static (Dictionary<string, string?[]> Rows, HashSet<string> Duplicates) Records(ParquetFile file, List<ParquetFile.Leaf> key, List<ParquetFile.Leaf> fields, CancellationToken ct)
    {
        var rows = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in file.RowGroups)
        {
            ct.ThrowIfCancellationRequested();
            var keys = key.Select(k => file.ReadColumn(group.Index, k.Index)).ToList();
            var columns = fields.Select(f => file.ReadColumn(group.Index, f.Index)).ToList();
            for (var r = 0; r < group.Rows; r++)
            {
                var k = string.Join(" | ", keys.Select(col => ParquetFile.Format(col[r]) ?? "(null)"));
                if (!rows.TryAdd(k, [.. columns.Select(col => ParquetFile.Format(col[r]))])) duplicates.Add(k);
            }
        }
        return (rows, duplicates);
    }

    private static List<ParquetSchemaChange> SchemaChanges(ParquetFile baseline, ParquetFile current)
    {
        string Type(ParquetFile.Leaf l) => $"{ParquetFile.PhysicalNames[l.Physical]}{(ParquetFile.LogicalName(l.Node) is { } lt ? " / " + lt : "")}";
        string Rep(ParquetFile.Leaf l) => l.Node.Repetition switch { 0 => "REQUIRED", 1 => "OPTIONAL", _ => "REPEATED" };
        var changes = new List<ParquetSchemaChange>();
        var b = baseline.Leaves.ToDictionary(l => l.Path); var c = current.Leaves.ToDictionary(l => l.Path);
        foreach (var path in b.Keys.Union(c.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!c.TryGetValue(path, out var cl)) { changes.Add(new(path, "Removed", Type(b[path]), null)); continue; }
            if (!b.TryGetValue(path, out var bl)) { changes.Add(new(path, "Added", null, Type(cl))); continue; }
            if (Type(bl) != Type(cl)) changes.Add(new(path, "Type changed", Type(bl), Type(cl)));
            if (Rep(bl) != Rep(cl)) changes.Add(new(path, "Nullability changed", Rep(bl), Rep(cl)));
        }
        return changes;
    }

    // ── Evidence (durable; no row values) ────────────────────────────────────────────────────────────────────────────────

    public async Task<List<ParquetInspectionEvidence>> EvidenceAsync(string environmentId, CancellationToken ct) =>
        (await db.ParquetInspections.AsNoTracking().Where(r => r.EnvironmentId == environmentId).OrderByDescending(r => r.SavedAt).Take(200).ToListAsync(ct))
            .Select(r => JsonSerializer.Deserialize<ParquetInspectionEvidence>(r.DocumentJson, Json)!).ToList();

    public async Task<ParquetInspectionEvidence?> EvidenceAsync(string environmentId, Guid id, CancellationToken ct) =>
        await db.ParquetInspections.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct) is { } row
            ? JsonSerializer.Deserialize<ParquetInspectionEvidence>(row.DocumentJson, Json) : null;

    /// <summary>Deletes saved evidence only; no source file is ever touched.</summary>
    public async Task<bool> DeleteEvidenceAsync(string environmentId, Guid id, CancellationToken ct)
    {
        var row = await db.ParquetInspections.FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        if (row is null) return false;
        db.ParquetInspections.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
