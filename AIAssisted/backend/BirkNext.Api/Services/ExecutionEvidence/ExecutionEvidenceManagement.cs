using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.ExecutionEvidence;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// Management of stored execution evidence: storage usage (measured), integrity, protection, deletion (one run, selected runs, every eligible run
/// of an environment, one call's bodies), retention (explicit setting, default Disabled; dry-run preview; applied only by a person) and exports.
/// Every destructive action is scoped, needs a confirmation that matches what was shown, skips protected and running runs with a reason, runs
/// in one database transaction (no evidence files exist, so nothing is left half-deleted) and writes an audit entry. Nothing here executes a call.
/// </summary>
public sealed class ExecutionEvidenceManagement(AppDbContext db, TimeProvider? clock = null)
{
    public const int MaxExportRuns = 20;
    public const long MaxExportBytes = 50L * 1024 * 1024;
    public const int MaxRetentionDays = 3650;
    public const string Location = "PostgreSQL (BirkNext database): execution_runs (run summaries) and execution_calls (one redacted document per logical call, with its attempts, request and response bodies, assertions and outputs).";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly string[] Unfinished = [nameof(ExecutionState.Running), nameof(ExecutionState.Planned), nameof(ExecutionState.Queued)];

    private bool Postgres => db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

    private sealed class RunBytes { public Guid RunId { get; init; } public long Bytes { get; init; } }

    /// <summary>Measured stored bytes per run: UTF-8 bytes of the run summary plus every call document.</summary>
    private async Task<Dictionary<Guid, long>> BytesAsync(IReadOnlyCollection<Guid>? runIds, CancellationToken ct)
    {
        if (Postgres)
        {
            var rows = await db.Database.SqlQueryRaw<RunBytes>("""
                SELECT r."Id" AS "RunId", octet_length(r."SummaryJson")::bigint + COALESCE((SELECT SUM(octet_length(c."DocumentJson"))::bigint FROM execution_calls c WHERE c."RunId" = r."Id"), 0) AS "Bytes"
                FROM execution_runs r
                """).ToListAsync(ct);
            return rows.Where(r => runIds is null || runIds.Contains(r.RunId)).ToDictionary(r => r.RunId, r => r.Bytes);
        }
        var runs = await db.ExecutionRuns.AsNoTracking().Where(r => runIds == null || runIds.Contains(r.Id)).Select(r => new { r.Id, r.SummaryJson }).ToListAsync(ct);
        var ids = runs.Select(r => r.Id).ToList();
        var calls = await db.ExecutionCalls.AsNoTracking().Where(c => ids.Contains(c.RunId)).Select(c => new { c.RunId, c.DocumentJson }).ToListAsync(ct);
        var byRun = calls.GroupBy(c => c.RunId).ToDictionary(g => g.Key, g => g.Sum(c => (long)Encoding.UTF8.GetByteCount(c.DocumentJson)));
        return runs.ToDictionary(r => r.Id, r => Encoding.UTF8.GetByteCount(r.SummaryJson) + byRun.GetValueOrDefault(r.Id));
    }

    public async Task<ExecutionStorageUsage> UsageAsync(string? environmentId, CancellationToken ct)
    {
        var runs = await db.ExecutionRuns.AsNoTracking().Where(r => environmentId == null || r.EnvironmentId == environmentId)
            .Select(r => new { r.Id, r.Protected, r.StartedAt, r.Attempts }).ToListAsync(ct);
        var ids = runs.Select(r => r.Id).ToList();
        var calls = await db.ExecutionCalls.AsNoTracking().CountAsync(c => ids.Contains(c.RunId), ct);
        var bytes = await BytesAsync(ids, ct);
        long? tables = null;
        if (Postgres)
            tables = await db.Database.SqlQueryRaw<long>("""SELECT (pg_total_relation_size('execution_runs') + pg_total_relation_size('execution_calls'))::bigint AS "Value" """).SingleAsync(ct);
        var preview = await PreviewAsync(environmentId, ct);
        return new(runs.Count, calls, runs.Sum(r => r.Attempts), bytes.Values.Sum(), environmentId is null ? tables : null, runs.Count(r => r.Protected),
            runs.Count == 0 ? null : runs.Min(r => r.StartedAt), runs.Count == 0 ? null : runs.Max(r => r.StartedAt), preview.Runs, preview.Bytes, Location,
            "Stored bytes are measured (UTF-8 bytes of the stored JSON). " + (environmentId is null
                ? tables is null ? "The database table size is not available for this database provider." : "The table size is what PostgreSQL reports for both tables, including indexes and TOAST storage."
                : "The table size covers every environment and is shown only without an environment filter.") +
            " No evidence files exist; exports are created on demand and are not stored.");
    }

    public async Task<ExecutionRetentionSettings> RetentionAsync(CancellationToken ct) =>
        await db.ExecutionRetentionSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct) is { } row
            ? new(Enum.Parse<RetentionMode>(row.Mode), row.Days, row.UpdatedAt, row.UpdatedBy) : new(RetentionMode.Disabled, null);

    public async Task<(ExecutionRetentionSettings? Settings, string? Error)> SaveRetentionAsync(ExecutionRetentionSettings settings, string initiator, CancellationToken ct)
    {
        if (settings.Mode == RetentionMode.Days && settings.Days is not (>= 1 and <= MaxRetentionDays)) return (null, $"Choose between 1 and {MaxRetentionDays} days.");
        var row = await db.ExecutionRetentionSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null) { row = new ExecutionRetentionSettingsRow { Id = 1 }; db.ExecutionRetentionSettings.Add(row); }
        (row.Mode, row.Days, row.UpdatedAt, row.UpdatedBy) = (settings.Mode.ToString(), settings.Mode == RetentionMode.Days ? settings.Days : null, _clock.GetUtcNow(), Trim(initiator, 200));
        Audit("RetentionChanged", "retention setting", 0, 0, 0, initiator, settings.Mode == RetentionMode.Days ? $"Keep {settings.Days} days." : "Disabled: nothing is deleted by retention.");
        await db.SaveChangesAsync(ct);
        return (await RetentionAsync(ct), null);
    }

    /// <summary>The runs retention would delete now: finished, unprotected and started before the cutoff. Disabled = none.</summary>
    public async Task<ExecutionRetentionPreview> PreviewAsync(string? environmentId, CancellationToken ct)
    {
        var settings = await RetentionAsync(ct);
        var scope = db.ExecutionRuns.AsNoTracking().Where(r => environmentId == null || r.EnvironmentId == environmentId);
        if (settings.Mode != RetentionMode.Days || settings.Days is not { } days)
            return new(settings, null, 0, 0, 0, await scope.CountAsync(r => r.Protected, ct), await scope.CountAsync(r => Unfinished.Contains(r.State), ct), [],
                "Retention is disabled: no run is deleted by retention. Delete runs yourself, or choose a retention period.");
        var cutoff = _clock.GetUtcNow().AddDays(-days);
        var old = await scope.Where(r => r.StartedAt < cutoff).Select(r => new { r.Id, r.Protected, r.State }).ToListAsync(ct);
        var ids = old.Where(r => !r.Protected && !Unfinished.Contains(r.State)).Select(r => r.Id).ToList();
        var calls = await db.ExecutionCalls.AsNoTracking().CountAsync(c => ids.Contains(c.RunId), ct);
        var bytes = ids.Count == 0 ? 0 : (await BytesAsync(ids, ct)).Values.Sum();
        return new(settings, cutoff, ids.Count, calls, bytes, old.Count(r => r.Protected), old.Count(r => Unfinished.Contains(r.State)), ids,
            $"Runs started before {cutoff:yyyy-MM-dd HH:mm} UTC that are finished and not protected. Nothing is deleted until you apply it.");
    }

    /// <summary>Deletes exactly what the preview shows now; refused when the number differs from the confirmed one (the set changed).</summary>
    public async Task<ExecutionDeletionResult> ApplyRetentionAsync(string? environmentId, int confirm, string initiator, CancellationToken ct)
    {
        var preview = await PreviewAsync(environmentId, ct);
        if (preview.Settings.Mode == RetentionMode.Disabled) return new(0, 0, 0, [], "Retention is disabled; nothing was deleted.");
        if (confirm != preview.Runs) return new(0, 0, 0, [], $"The retention candidates changed ({preview.Runs} now, {confirm} confirmed). Preview again; nothing was deleted.");
        return await DeleteRunsAsync(preview.RunIds, "RetentionApplied", environmentId is null ? "retention (all environments)" : $"retention ({environmentId})", initiator, ct);
    }

    /// <summary>Finished, unprotected runs of an environment (the "delete all eligible" scope), with their size.</summary>
    public async Task<ExecutionRetentionPreview> EligibleAsync(string environmentId, CancellationToken ct)
    {
        var runs = await db.ExecutionRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId).Select(r => new { r.Id, r.Protected, r.State }).ToListAsync(ct);
        var ids = runs.Where(r => !r.Protected && !Unfinished.Contains(r.State)).Select(r => r.Id).ToList();
        var calls = await db.ExecutionCalls.AsNoTracking().CountAsync(c => ids.Contains(c.RunId), ct);
        return new(await RetentionAsync(ct), null, ids.Count, calls, ids.Count == 0 ? 0 : (await BytesAsync(ids, ct)).Values.Sum(), runs.Count(r => r.Protected),
            runs.Count(r => Unfinished.Contains(r.State)), ids, $"Every finished, unprotected run of {environmentId}.");
    }

    public async Task<ExecutionDeletionResult> DeleteAsync(ExecutionDeletionRequest request, string initiator, CancellationToken ct)
    {
        List<Guid> ids;
        string scope;
        var skipped = new List<ExecutionDeletionSkip>();
        if (request.AllEligible)
        {
            if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return new(0, 0, 0, [], "Deleting every eligible run needs an environment.");
            var eligible = await EligibleAsync(request.EnvironmentId, ct);
            ids = eligible.RunIds;
            scope = $"all eligible runs of {request.EnvironmentId}";
        }
        else
        {
            var wanted = request.RunIds.Distinct().ToList();
            if (wanted.Count is 0 or > 500) return new(0, 0, 0, [], "Select 1 to 500 runs.");
            var found = await db.ExecutionRuns.AsNoTracking().Where(r => wanted.Contains(r.Id)).Select(r => new { r.Id, r.Protected, r.State }).ToListAsync(ct);
            skipped.AddRange(wanted.Where(id => found.All(f => f.Id != id)).Select(id => new ExecutionDeletionSkip(id, "Not found.")));
            skipped.AddRange(found.Where(f => f.Protected).Select(f => new ExecutionDeletionSkip(f.Id, "Protected: remove the protection first.")));
            skipped.AddRange(found.Where(f => !f.Protected && Unfinished.Contains(f.State)).Select(f => new ExecutionDeletionSkip(f.Id, "Still running: cancel it first.")));
            ids = [.. found.Where(f => !f.Protected && !Unfinished.Contains(f.State)).Select(f => f.Id)];
            scope = wanted.Count == 1 ? $"run {wanted[0]}" : $"{wanted.Count} selected runs";
        }
        if (request.Confirm != (request.AllEligible ? ids.Count : request.RunIds.Distinct().Count()))
            return new(0, 0, 0, skipped, $"Confirm the number of runs shown ({(request.AllEligible ? ids.Count : request.RunIds.Distinct().Count())}); nothing was deleted.");
        var result = await DeleteRunsAsync(ids, "Deleted", scope, initiator, ct);
        return result with { Skipped = [.. skipped, .. result.Skipped] };
    }

    /// <summary>One transaction: the runs, all their calls (attempts, bodies, assertions, outputs live inside) and the audit entry.</summary>
    private async Task<ExecutionDeletionResult> DeleteRunsAsync(IReadOnlyCollection<Guid> ids, string action, string scope, string initiator, CancellationToken ct)
    {
        if (ids.Count == 0) return new(0, 0, 0, [], "Nothing to delete.");
        var bytes = (await BytesAsync(ids, ct)).Values.Sum();
        var calls = await db.ExecutionCalls.AsNoTracking().CountAsync(c => ids.Contains(c.RunId), ct);
        if (Postgres)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.ExecutionCalls.Where(c => ids.Contains(c.RunId)).ExecuteDeleteAsync(ct);
            await db.ExecutionManifests.Where(m => m.Owner == ExecutionManifestSchema.ExecutionEvidenceOwner && ids.Contains(m.RunId)).ExecuteDeleteAsync(ct);
            var deleted = await db.ExecutionRuns.Where(r => ids.Contains(r.Id) && !r.Protected).ExecuteDeleteAsync(ct);
            if (deleted != ids.Count) { await tx.RollbackAsync(ct); return new(0, 0, 0, [], "The runs changed while deleting (one was protected meanwhile); nothing was deleted."); }
            Audit(action, scope, ids.Count, calls, bytes, initiator, Detail(ids));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            db.ExecutionCalls.RemoveRange(await db.ExecutionCalls.Where(c => ids.Contains(c.RunId)).ToListAsync(ct));
            db.ExecutionManifests.RemoveRange(await db.ExecutionManifests.Where(m => m.Owner == ExecutionManifestSchema.ExecutionEvidenceOwner && ids.Contains(m.RunId)).ToListAsync(ct));
            db.ExecutionRuns.RemoveRange(await db.ExecutionRuns.Where(r => ids.Contains(r.Id)).ToListAsync(ct));
            Audit(action, scope, ids.Count, calls, bytes, initiator, Detail(ids));
            await db.SaveChangesAsync(ct);
        }
        return new(ids.Count, calls, bytes, [], $"Deleted {ids.Count} run(s) with {calls} call(s) ({bytes:N0} bytes of stored evidence).");
    }

    private static string Detail(IReadOnlyCollection<Guid> ids) => Trim("Runs: " + string.Join(", ", ids.Take(40)) + (ids.Count > 40 ? $" and {ids.Count - 40} more" : ""), 2000);

    public async Task<(bool Done, string? Error)> ProtectAsync(Guid runId, bool protect, string initiator, CancellationToken ct)
    {
        if (await db.ExecutionRuns.FirstOrDefaultAsync(r => r.Id == runId, ct) is not { } row) return (false, "Not found.");
        (row.Protected, row.ProtectedAt) = (protect, protect ? _clock.GetUtcNow() : null);
        Audit(protect ? "Protected" : "Unprotected", $"run {runId}", 1, 0, 0, initiator, null);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    /// <summary>Removes the stored request and response bodies of one call; status, headers, sizes, hashes, assertions and outputs stay.</summary>
    public async Task<(int Removed, string? Error)> RemoveBodiesAsync(Guid runId, Guid callId, string initiator, CancellationToken ct)
    {
        if (await db.ExecutionRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct) is not { } run) return (0, "Not found.");
        if (run.Protected) return (0, "The run is protected: remove the protection first.");
        if (Unfinished.Contains(run.State)) return (0, "The run is still running.");
        if (await db.ExecutionCalls.FirstOrDefaultAsync(c => c.RunId == runId && c.Id == callId, ct) is not { } row) return (0, "Not found.");
        var doc = JsonSerializer.Deserialize<ExecutionCallRecord>(row.DocumentJson, ExecutionEvidenceStore.Json)!;
        var removed = 0;
        var before = Encoding.UTF8.GetByteCount(row.DocumentJson);
        var note = $"Body removed on {_clock.GetUtcNow():yyyy-MM-dd} by its owner; its size and the SHA-256 of what was stored are kept.";
        CapturedBody? Strip(CapturedBody? b)
        {
            if (b?.Text is null) return b;
            removed++;
            return b with { Text = null, Completeness = CaptureCompleteness.Removed, Note = note };
        }
        doc = doc with { Attempts = [.. doc.Attempts.Select(a => a with
        {
            Request = a.Request is { } q ? q with { Body = Strip(q.Body) } : null,
            Response = a.Response is { } s ? s with { Body = Strip(s.Body) } : null,
        })] };
        row.DocumentJson = JsonSerializer.Serialize(doc, ExecutionEvidenceStore.Json);
        Audit("BodiesRemoved", $"call {callId} of run {runId}", 1, 1, before - Encoding.UTF8.GetByteCount(row.DocumentJson), initiator, $"{removed} body(ies) removed.");
        await db.SaveChangesAsync(ct);
        return (removed, null);
    }

    public async Task<ExecutionIntegrityReport?> IntegrityAsync(Guid runId, CancellationToken ct)
    {
        if (!await db.ExecutionRuns.AsNoTracking().AnyAsync(r => r.Id == runId, ct)) return null;
        var docs = await db.ExecutionCalls.AsNoTracking().Where(c => c.RunId == runId).OrderBy(c => c.Sequence).Select(c => c.DocumentJson).ToListAsync(ct);
        int checkedBodies = 0, removed = 0;
        var mismatches = new List<string>();
        foreach (var call in docs.Select(d => JsonSerializer.Deserialize<ExecutionCallRecord>(d, ExecutionEvidenceStore.Json)!))
            foreach (var a in call.Attempts)
                foreach (var (what, body) in new[] { ("request", a.Request?.Body), ("response", a.Response?.Body) })
                {
                    if (body is null) continue;
                    if (body.Completeness == CaptureCompleteness.Removed) { removed++; continue; }
                    if (body.Text is null || body.Sha256 is null) continue;
                    checkedBodies++;
                    if (!string.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.Text))), body.Sha256, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add($"Call {call.Sequence}, attempt {a.AttemptNumber}: the stored {what} body does not match its recorded SHA-256.");
                }
        return new(runId, docs.Count, checkedBodies, removed, mismatches);
    }

    public async Task<IReadOnlyList<ExecutionAuditEntry>> AuditAsync(int take, CancellationToken ct) =>
        await db.ExecutionEvidenceAudit.AsNoTracking().OrderByDescending(a => a.At).Take(Math.Clamp(take, 1, 500))
            .Select(a => new ExecutionAuditEntry(a.Id, a.At, a.Action, a.Scope, a.Runs, a.Calls, a.Bytes, a.Initiator, a.Detail)).ToListAsync(ct);

    /// <summary>
    /// A portable export of one or more runs (ZIP), built only from the stored, already redacted documents: manifest.json (export summary), per
    /// run runs/{id}/manifest.json (summary, Test Data / contract / definition / chain references, authentication metadata, correlation scheme,
    /// capture completeness) and runs/{id}/calls/NNNN.json, plus hashes.json (SHA-256 of every file). Never executes anything.
    /// </summary>
    public async Task<(byte[]? Zip, string? Error)> ExportAsync(IReadOnlyList<Guid> runIds, string initiator, CancellationToken ct)
    {
        var ids = runIds.Distinct().ToList();
        if (ids.Count is 0 or > MaxExportRuns) return (null, $"Export 1 to {MaxExportRuns} runs at a time.");
        var runs = await db.ExecutionRuns.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        if (runs.Count != ids.Count) return (null, "Not found.");
        var bytes = (await BytesAsync(ids, ct)).Values.Sum();
        if (bytes > MaxExportBytes) return (null, $"The selected runs hold {bytes / (1024 * 1024)} MB of evidence; export at most {MaxExportBytes / (1024 * 1024)} MB at a time.");
        using var ms = new MemoryStream();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var totalCalls = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string text)
            {
                var data = Encoding.UTF8.GetBytes(text);
                hashes[name] = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); s.Write(data);
            }
            foreach (var run in runs.OrderBy(r => r.StartedAt))
            {
                var calls = await db.ExecutionCalls.AsNoTracking().Where(c => c.RunId == run.Id).OrderBy(c => c.Sequence).Select(c => new { c.Sequence, c.DocumentJson }).ToListAsync(ct);
                totalCalls += calls.Count;
                var docs = calls.Select(c => JsonSerializer.Deserialize<ExecutionCallRecord>(c.DocumentJson, ExecutionEvidenceStore.Json)!).ToList();
                var summary = JsonSerializer.Deserialize<ExecutionRunSummary>(run.SummaryJson, ExecutionEvidenceStore.Json)! with { Protected = run.Protected };
                var bodies = docs.SelectMany(d => d.Attempts).SelectMany(a => new[] { a.Request?.Body, a.Response?.Body }).OfType<CapturedBody>().ToList();
                Add($"runs/{run.Id:N}/manifest.json", JsonSerializer.Serialize(new
                {
                    run = summary,
                    calls = docs.Count,
                    references = new
                    {
                        contracts = summary.ContractRefs,
                        operations = docs.Select(d => d.Definition.OperationRef).OfType<string>().Distinct().Order(),
                        definitions = docs.Select(d => d.Definition.DefinitionVersion).OfType<string>().Distinct().Order(),
                        testData = docs.SelectMany(d => d.Definition.TestDataRefs).Distinct().Order(),
                        datasetRows = docs.Select(d => d.Definition.DatasetRow).OfType<string>().Distinct().Order(),
                        chain = summary.ChainRef,
                    },
                    authentication = new { mode = summary.AuthenticationMode, reference = summary.AuthenticationReference, sessions = summary.AuthenticationSessions,
                        outcomes = docs.GroupBy(d => d.Authentication).ToDictionary(g => g.Key.ToString(), g => g.Count()) },
                    correlation = $"Every attempt carried {ExecutionRunService.CorrelationHeader}: {{run}}.{{sequence}}.{{attempt}} with run {run.Id:N}.",
                    bodies = bodies.GroupBy(b => b.Completeness).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                }, ExecutionEvidenceStore.Json));
                foreach (var c in calls) Add($"runs/{run.Id:N}/calls/{c.Sequence:D4}.json", c.DocumentJson);
            }
            Add("manifest.json", JsonSerializer.Serialize(new
            {
                exportedAt = _clock.GetUtcNow(), runs = runs.OrderBy(r => r.StartedAt).Select(r => new { id = r.Id, r.Name, r.State, r.StartedAt }), calls = totalCalls,
                redactionVersion = ExecutionEvidenceRedaction.Version, storedBytes = bytes,
                notice = "Every request and response was redacted before it was stored; bodies marked Truncated, MetadataOnly, Empty, Unavailable or Removed are not complete. " +
                         "Credentials, cookies, API keys and tokens are never stored or exported. This export was built from stored evidence only; nothing was executed. " +
                         "Equal payloads do not prove equal domain state.",
            }, ExecutionEvidenceStore.Json));
            using var h = zip.CreateEntry("hashes.json").Open(); h.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hashes, ExecutionEvidenceStore.Json)));
        }
        Audit("Exported", ids.Count == 1 ? $"run {ids[0]}" : $"{ids.Count} runs", ids.Count, totalCalls, bytes, initiator, Detail(ids));
        await db.SaveChangesAsync(ct);
        return (ms.ToArray(), null);
    }

    private void Audit(string action, string scope, int runs, int calls, long bytes, string initiator, string? detail) =>
        db.ExecutionEvidenceAudit.Add(new ExecutionEvidenceAuditRow { Id = Guid.NewGuid(), At = _clock.GetUtcNow(), Action = action, Scope = Trim(scope, 300), Runs = runs, Calls = calls,
            Bytes = bytes, Initiator = Trim(string.IsNullOrWhiteSpace(initiator) ? "Local session" : initiator, 200), Detail = detail is null ? null : Trim(detail, 2000) });

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
