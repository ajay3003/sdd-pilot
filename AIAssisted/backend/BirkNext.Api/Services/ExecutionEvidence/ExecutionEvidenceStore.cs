using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.ExecutionEvidence;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExecutionEvidence;

public sealed record ExecutionRunQuery(string? EnvironmentId = null, int Take = 50, ExecutionState? State = null, string? Search = null, DateTimeOffset? From = null,
    DateTimeOffset? To = null, bool? Protected = null);

public sealed record ExecutionCallQuery(int Skip = 0, int Take = 50, ExecutionState? State = null, ExecutionProtocol? Protocol = null, string? Method = null,
    string? Search = null, string Sort = "sequence");

/// <summary>
/// Persistence of execution evidence in AppDbContext (where every execution run lives). Only redacted documents are written. A run is
/// created with all planned calls before the first request, each call is saved when it ends, and a run left Running by a restart is marked
/// Interrupted (its unfinished calls too) — nothing is resent. Completed runs are immutable except for deletion by their owner.
/// </summary>
public sealed class ExecutionEvidenceStore(AppDbContext db, TimeProvider? clock = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly ExecutionState[] Final =
        [ExecutionState.Passed, ExecutionState.Failed, ExecutionState.Error, ExecutionState.Skipped, ExecutionState.Blocked, ExecutionState.Cancelled, ExecutionState.TimedOut, ExecutionState.Interrupted, ExecutionState.NotAssessed];

    public async Task SaveCallAsync(Guid runId, ExecutionCallRecord call, CancellationToken ct)
    {
        var row = await db.ExecutionCalls.FirstAsync(c => c.RunId == runId && c.Id == call.CallId, ct);
        var fresh = Row(call);
        (row.State, row.HttpStatus, row.DurationMs, row.AttemptCount, row.DocumentJson) = (fresh.State, fresh.HttpStatus, fresh.DurationMs, fresh.AttemptCount, fresh.DocumentJson);
        await db.SaveChangesAsync(ct);
    }

    private static ExecutionCallRow Row(ExecutionCallRecord c)
    {
        var last = c.Attempts.LastOrDefault();
        return new()
        {
            Id = c.CallId, RunId = Guid.Empty, Sequence = c.Sequence, Name = Trim(c.Definition.Name, 300), Protocol = c.Definition.Protocol.ToString(),
            Method = c.Definition.Protocol == ExecutionProtocol.GraphQl ? "POST" : c.Definition.Method.ToUpperInvariant(), State = c.State.ToString(),
            HttpStatus = last?.Response?.StatusCode, DurationMs = last?.Response?.DurationMs, AttemptCount = c.Attempts.Count, DocumentJson = JsonSerializer.Serialize(c, Json),
            DefinitionRef = c.Definition.DefinitionVersion is { Length: > 0 and <= 200 } d ? d : null,
        };
    }

    /// <summary>Creates the run with every planned call (and its immutable execution manifest, when given) before the first request is sent.</summary>
    public async Task<ExecutionRunSummary> CreateAsync(ExecutionRunSummary run, IReadOnlyList<ExecutionCallRecord> calls, CancellationToken ct, ExecutionManifest? manifest = null)
    {
        if (manifest is not null) db.ExecutionManifests.Add(ExecutionManifestService.Row(manifest));
        var runId = run.RunId;
        db.ExecutionRuns.Add(new ExecutionRunRow
        {
            Id = runId, EnvironmentId = run.EnvironmentId, Name = Trim(run.Name, 300), State = run.State.ToString(), StartedAt = run.StartedAt, Planned = calls.Count,
            SummaryJson = JsonSerializer.Serialize(run, Json),
        });
        foreach (var c in calls) { var r = Row(c); db.ExecutionCalls.Add(new ExecutionCallRow { Id = r.Id, RunId = runId, Sequence = r.Sequence, Name = r.Name, Protocol = r.Protocol,
            Method = r.Method, State = r.State, HttpStatus = r.HttpStatus, DurationMs = r.DurationMs, AttemptCount = r.AttemptCount, DocumentJson = r.DocumentJson, DefinitionRef = r.DefinitionRef }); }
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Final state and counts reconciled from the stored calls (never from what the runner believes).</summary>
    public async Task<ExecutionRunSummary?> CompleteAsync(Guid runId, ExecutionState? forced, CancellationToken ct)
    {
        var row = await db.ExecutionRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (row is null) return null;
        var calls = await db.ExecutionCalls.AsNoTracking().Where(c => c.RunId == runId).ToListAsync(ct);
        var docs = calls.Select(c => JsonSerializer.Deserialize<ExecutionCallRecord>(c.DocumentJson, Json)!).ToList();
        var counts = Counts(docs);
        var state = forced ?? (counts.Interrupted > 0 ? ExecutionState.Interrupted
            : counts.Cancelled > 0 ? ExecutionState.Cancelled
            : counts.Errors + counts.TimedOut > 0 ? ExecutionState.Error
            : counts.Failed > 0 ? ExecutionState.Failed
            : counts.Blocked == counts.Planned ? ExecutionState.Blocked
            : counts.Passed == counts.Planned ? ExecutionState.Passed
            : ExecutionState.NotAssessed);
        var summary = JsonSerializer.Deserialize<ExecutionRunSummary>(row.SummaryJson, Json)! with
        {
            State = state, CompletedAt = _clock.GetUtcNow(), Counts = counts,
            WireTransmissions = docs.Sum(d => d.Attempts.Sum(a => a.Transmissions)),
            AuthenticationSessions = [.. docs.OrderBy(d => d.Sequence).SelectMany(d => d.Attempts).Select(a => a.AuthenticationSession).OfType<string>().Distinct(StringComparer.Ordinal)],
            Definitions = docs.Select(d => d.Definition.DefinitionVersion ?? $"call:{d.Sequence}").Distinct(StringComparer.Ordinal).Count(),
            DatasetRows = docs.Count(d => d.Definition.DatasetRow is not null),
            CallsWithCompleteEvidence = docs.Count(d => d.Attempts.All(a => a.Transport == TransportResult.NotSent || a.Request is not null && (a.Transport != TransportResult.ResponseReceived || a.Response is not null))),
        };
        (row.State, row.CompletedAt, row.Passed, row.Failed, row.Errors, row.Attempts, row.SummaryJson) =
            (state.ToString(), summary.CompletedAt, counts.Passed, counts.Failed, counts.Errors + counts.TimedOut, counts.Attempts, JsonSerializer.Serialize(summary, Json));
        await db.SaveChangesAsync(ct);
        return summary;
    }

    public static ExecutionRunCounts Counts(IReadOnlyList<ExecutionCallRecord> c) => new(
        c.Count, c.Count(x => x.Attempts.Any(a => a.Transport != TransportResult.NotSent)),
        c.Count(x => x.State == ExecutionState.Passed), c.Count(x => x.State == ExecutionState.Failed), c.Count(x => x.State == ExecutionState.Error),
        c.Count(x => x.State == ExecutionState.Skipped), c.Count(x => x.State == ExecutionState.Blocked), c.Count(x => x.State == ExecutionState.Cancelled),
        c.Count(x => x.State == ExecutionState.TimedOut), c.Count(x => x.State is ExecutionState.Interrupted or ExecutionState.Running or ExecutionState.Planned or ExecutionState.Queued),
        c.Count(x => x.State == ExecutionState.NotAssessed), c.Sum(x => x.Attempts.Count(a => a.Transport != TransportResult.NotSent)));

    /// <summary>After a restart: runs still Running become Interrupted, and so do their unfinished calls. No request is sent again.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct)
    {
        var running = await db.ExecutionRuns.Where(r => r.State == nameof(ExecutionState.Running)).Select(r => r.Id).ToListAsync(ct);
        foreach (var id in running)
        {
            foreach (var row in await db.ExecutionCalls.Where(c => c.RunId == id && (c.State == nameof(ExecutionState.Running) || c.State == nameof(ExecutionState.Planned) || c.State == nameof(ExecutionState.Queued))).ToListAsync(ct))
            {
                var doc = JsonSerializer.Deserialize<ExecutionCallRecord>(row.DocumentJson, Json)!;
                doc = doc with { State = ExecutionState.Interrupted, Reason = doc.State == ExecutionState.Running
                    ? "BirkNext stopped while this call was running; its outcome is unknown and it was not sent again."
                    : "BirkNext stopped before this call started; it was never sent." };
                (row.State, row.DocumentJson) = (doc.State.ToString(), JsonSerializer.Serialize(doc, Json));
            }
            await db.SaveChangesAsync(ct);
            await CompleteAsync(id, ExecutionState.Interrupted, ct);
        }
        return running.Count;
    }

    /// <summary>Run history, newest first, filtered by environment, state, name, start date range and protection. Read-only.</summary>
    public async Task<IReadOnlyList<ExecutionRunSummary>> ListAsync(ExecutionRunQuery q, CancellationToken ct)
    {
        var query = db.ExecutionRuns.AsNoTracking().Where(r => q.EnvironmentId == null || r.EnvironmentId == q.EnvironmentId);
        if (q.State is { } state) query = query.Where(r => r.State == state.ToString());
        if (!string.IsNullOrWhiteSpace(q.Search)) { var term = q.Search.Trim().ToLower(); query = query.Where(r => r.Name.ToLower().Contains(term)); }
        if (q.From is { } from) query = query.Where(r => r.StartedAt >= from);
        if (q.To is { } to) query = query.Where(r => r.StartedAt <= to);
        if (q.Protected is { } protect) query = query.Where(r => r.Protected == protect);
        return (await query.OrderByDescending(r => r.StartedAt).Take(Math.Clamp(q.Take, 1, 500)).Select(r => new { r.SummaryJson, r.Protected }).ToListAsync(ct))
            .Select(r => JsonSerializer.Deserialize<ExecutionRunSummary>(r.SummaryJson, Json)! with { Protected = r.Protected }).ToList();
    }

    public async Task<ExecutionRunDetail?> GetAsync(Guid runId, ExecutionCallQuery q, CancellationToken ct)
    {
        var run = await db.ExecutionRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return null;
        var query = db.ExecutionCalls.AsNoTracking().Where(c => c.RunId == runId);
        if (q.State is { } state) query = query.Where(c => c.State == state.ToString());
        if (q.Protocol is { } protocol) query = query.Where(c => c.Protocol == protocol.ToString());
        if (!string.IsNullOrWhiteSpace(q.Method)) query = query.Where(c => c.Method == q.Method.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(q.Search)) { var term = q.Search.Trim().ToLower(); query = query.Where(c => c.Name.ToLower().Contains(term)); }
        query = q.Sort switch
        {
            "duration" => query.OrderByDescending(c => c.DurationMs).ThenBy(c => c.Sequence),
            "status" => query.OrderBy(c => c.State).ThenBy(c => c.Sequence),
            _ => query.OrderBy(c => c.Sequence),
        };
        var total = await query.CountAsync(ct);
        var rows = await query.Skip(Math.Max(0, q.Skip)).Take(Math.Clamp(q.Take, 1, 200)).Select(c => c.DocumentJson).ToListAsync(ct);
        return new(JsonSerializer.Deserialize<ExecutionRunSummary>(run.SummaryJson, Json)! with { Protected = run.Protected }, [.. rows.Select(r => JsonSerializer.Deserialize<ExecutionCallRecord>(r, Json)!)], total, q.Skip, q.Take);
    }

    public async Task<ExecutionCallRecord?> CallAsync(Guid runId, Guid callId, CancellationToken ct) =>
        await db.ExecutionCalls.AsNoTracking().Where(c => c.RunId == runId && c.Id == callId).Select(c => c.DocumentJson).FirstOrDefaultAsync(ct) is { } doc
            ? JsonSerializer.Deserialize<ExecutionCallRecord>(doc, Json) : null;

    /// <summary>The newest call whose definition reference starts with <paramref name="definitionPrefix"/> (any revision of one definition),
    /// with its run id and start time. Read-only; null when that definition never ran.</summary>
    public async Task<(Guid RunId, DateTimeOffset StartedAt, ExecutionCallRecord Call)?> LatestByDefinitionAsync(string definitionPrefix, CancellationToken ct)
    {
        var hit = await db.ExecutionCalls.AsNoTracking().Where(c => c.DefinitionRef != null && c.DefinitionRef.StartsWith(definitionPrefix))
            .Join(db.ExecutionRuns.AsNoTracking(), c => c.RunId, r => r.Id, (c, r) => new { c.RunId, r.StartedAt, c.Sequence, c.DocumentJson })
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        return hit is null ? null : (hit.RunId, hit.StartedAt, JsonSerializer.Deserialize<ExecutionCallRecord>(hit.DocumentJson, Json)!);
    }

    /// <summary>Differences between two stored attempts (redacted evidence only); equal payloads never prove equal domain state.</summary>
    public async Task<AttemptComparison?> CompareAsync(Guid runA, Guid callA, int attemptA, Guid runB, Guid callB, int attemptB, CancellationToken ct)
    {
        var a = (await CallAsync(runA, callA, ct))?.Attempts.FirstOrDefault(x => x.AttemptNumber == attemptA);
        var b = (await CallAsync(runB, callB, ct))?.Attempts.FirstOrDefault(x => x.AttemptNumber == attemptB);
        if (a is null || b is null) return null;
        var diffs = new List<string>();
        void D(string what, object? x, object? y) { if (!Equals(x, y)) diffs.Add($"{what}: {x ?? "—"} → {y ?? "—"}"); }
        D("Transport", a.Transport, b.Transport);
        D("HTTP status", a.Response?.StatusCode, b.Response?.StatusCode);
        D("Request URL", a.Request?.Url, b.Request?.Url);
        D("Request body hash", a.Request?.Body?.Sha256, b.Request?.Body?.Sha256);
        D("Response body hash", a.Response?.Body?.Sha256, b.Response?.Body?.Sha256);
        D("Response bytes", a.Response?.Body?.ByteCount, b.Response?.Body?.ByteCount);
        D("GraphQL errors", a.Response?.GraphQlErrorCount, b.Response?.GraphQlErrorCount);
        D("Duration (ms)", a.Response?.DurationMs, b.Response?.DurationMs);
        var ha = (a.Response?.Headers ?? []).Select(h => h.Name.ToLowerInvariant()).ToHashSet(); var hb = (b.Response?.Headers ?? []).Select(h => h.Name.ToLowerInvariant()).ToHashSet();
        foreach (var h in hb.Except(ha)) diffs.Add($"Response header added: {h}");
        foreach (var h in ha.Except(hb)) diffs.Add($"Response header removed: {h}");
        return new(runA, callA, attemptA, runB, callB, attemptB, diffs);
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
