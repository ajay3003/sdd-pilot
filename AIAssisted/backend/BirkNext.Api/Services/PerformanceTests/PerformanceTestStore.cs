using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.PerformanceTests;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// Persistence of performance test definitions (versioned), test data profiles, immutable runs and versioned baselines. A run never changes
/// after it finished; a definition edit creates a new version and never rewrites a run; a baseline is superseded, never deleted.
/// </summary>
public sealed class PerformanceTestStore(AppDbContext db)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Definitions ──────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<List<PerformanceTestDefinition>> DefinitionsAsync(string environmentId, bool includeArchived = false, CancellationToken ct = default)
    {
        var rows = await db.PerformanceTestDefinitions.AsNoTracking().Where(r => r.EnvironmentId == environmentId && (includeArchived || !r.Archived)).ToListAsync(ct);
        return rows.Select(r => JsonSerializer.Deserialize<PerformanceTestDefinition>(r.DocumentJson, Json)!).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id).ToList();
    }

    public async Task<PerformanceTestDefinition?> DefinitionAsync(string environmentId, string id, CancellationToken ct = default)
    {
        var row = await db.PerformanceTestDefinitions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        return row is null ? null : JsonSerializer.Deserialize<PerformanceTestDefinition>(row.DocumentJson, Json);
    }

    /// <summary>Creates or updates. An update with changed content increments the version and recomputes both fingerprints.</summary>
    public async Task<PerformanceTestDefinition> SaveDefinitionAsync(string environmentId, PerformanceTestDefinition definition, DateTimeOffset now, CancellationToken ct = default)
    {
        var row = await db.PerformanceTestDefinitions.FirstOrDefaultAsync(r => r.Id == definition.Id && r.EnvironmentId == environmentId, ct);
        var previous = row is null ? null : JsonSerializer.Deserialize<PerformanceTestDefinition>(row.DocumentJson, Json);
        var normalized = definition with { EnvironmentId = environmentId, Archived = previous?.Archived ?? false, Fingerprint = "", ComparisonFingerprint = "", Version = 0,
            CreatedAt = previous?.CreatedAt ?? now, UpdatedAt = now };
        var fingerprint = Fingerprint(normalized with { UpdatedAt = default, CreatedAt = default });
        if (previous is not null && previous.Fingerprint == fingerprint) return previous;
        var saved = normalized with { Fingerprint = fingerprint, ComparisonFingerprint = ComparisonFingerprint(normalized), Version = (previous?.Version ?? 0) + 1 };
        if (row is null) db.PerformanceTestDefinitions.Add(row = new PerformanceTestDefinitionRecord { Id = saved.Id, EnvironmentId = environmentId });
        row.Version = saved.Version; row.Archived = saved.Archived; row.UpdatedAt = now; row.DocumentJson = JsonSerializer.Serialize(saved, Json);
        await db.SaveChangesAsync(ct);
        return saved;
    }

    /// <summary>Deletes a definition without runs; archives one that runs reference (history is never cascade-deleted).</summary>
    public async Task<string?> DeleteDefinitionAsync(string environmentId, string id, CancellationToken ct = default)
    {
        var row = await db.PerformanceTestDefinitions.FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        if (row is null) return null;
        if (await db.PerformanceTestRuns.AnyAsync(r => r.DefinitionId == id && r.EnvironmentId == environmentId, ct))
        {
            var definition = JsonSerializer.Deserialize<PerformanceTestDefinition>(row.DocumentJson, Json)! with { Archived = true };
            row.Archived = true; row.DocumentJson = JsonSerializer.Serialize(definition, Json);
            await db.SaveChangesAsync(ct);
            return "Archived";
        }
        db.PerformanceTestDefinitions.Remove(row);
        await db.SaveChangesAsync(ct);
        return "Deleted";
    }

    public static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)))).ToLowerInvariant()[..32];

    /// <summary>What makes runs comparable: target, type, steps, workload, environment class, test data profile. Not thresholds, drift policies or names.</summary>
    public static string ComparisonFingerprint(PerformanceTestDefinition d) => Fingerprint(new
    {
        Target = d.TargetOrigin.TrimEnd('/').ToLowerInvariant(), d.TargetType, Steps = d.Scenario.Steps, d.Workload, Environment = d.EnvironmentType.Trim().ToLowerInvariant(),
        d.Scenario.TestDataProfileId,
    });

    // ── Test data profiles ───────────────────────────────────────────────────────────────────────────────────────────

    public async Task<List<PerformanceTestDataProfile>> DataProfilesAsync(string environmentId, CancellationToken ct = default) =>
        (await db.PerformanceTestDataProfiles.AsNoTracking().Where(r => r.EnvironmentId == environmentId).ToListAsync(ct))
            .Select(r => JsonSerializer.Deserialize<PerformanceTestDataProfile>(r.DocumentJson, Json)!).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<PerformanceTestDataProfile?> DataProfileAsync(string environmentId, string? id, CancellationToken ct = default)
    {
        if (id is null) return null;
        var row = await db.PerformanceTestDataProfiles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        return row is null ? null : JsonSerializer.Deserialize<PerformanceTestDataProfile>(row.DocumentJson, Json);
    }

    public async Task<PerformanceTestDataProfile> SaveDataProfileAsync(string environmentId, PerformanceTestDataProfile profile, DateTimeOffset now, CancellationToken ct = default)
    {
        var saved = profile with { EnvironmentId = environmentId, UpdatedAt = now };
        var row = await db.PerformanceTestDataProfiles.FirstOrDefaultAsync(r => r.Id == saved.Id && r.EnvironmentId == environmentId, ct);
        if (row is null) db.PerformanceTestDataProfiles.Add(row = new PerformanceTestDataProfileRecord { Id = saved.Id, EnvironmentId = environmentId });
        row.UpdatedAt = now; row.DocumentJson = JsonSerializer.Serialize(saved, Json);
        await db.SaveChangesAsync(ct);
        return saved;
    }

    // ── Runs ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PerformanceTestRun> SaveRunAsync(PerformanceTestRun run, CancellationToken ct = default)
    {
        var row = await db.PerformanceTestRuns.FirstOrDefaultAsync(r => r.Id == run.RunId, ct);
        if (row is null) db.PerformanceTestRuns.Add(row = new PerformanceTestRunRecord { Id = run.RunId, EnvironmentId = run.EnvironmentId, DefinitionId = run.DefinitionId, CreatedAt = run.CreatedAt });
        else if (!JsonSerializer.Deserialize<PerformanceTestRun>(row.DocumentJson, Json)!.IsActive)
            throw new InvalidOperationException("A finished performance test run is immutable.");
        row.State = run.State.ToString(); row.DocumentJson = JsonSerializer.Serialize(run, Json);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<PerformanceTestRun?> RunAsync(Guid runId, CancellationToken ct = default)
    {
        var row = await db.PerformanceTestRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return row is null ? null : JsonSerializer.Deserialize<PerformanceTestRun>(row.DocumentJson, Json);
    }

    public async Task<List<PerformanceTestRun>> RunsAsync(string environmentId, string? definitionId = null, int take = 200, CancellationToken ct = default)
    {
        var rows = await db.PerformanceTestRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId && (definitionId == null || r.DefinitionId == definitionId))
            .OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync(ct);
        return rows.Select(r => JsonSerializer.Deserialize<PerformanceTestRun>(r.DocumentJson, Json)!).ToList();
    }

    public Task<bool> HasActiveRunAsync(string environmentId, CancellationToken ct = default)
    {
        var active = new[] { nameof(PerformanceRunState.Queued), nameof(PerformanceRunState.Preparing), nameof(PerformanceRunState.Running), nameof(PerformanceRunState.Cancelling) };
        return db.PerformanceTestRuns.AnyAsync(r => r.EnvironmentId == environmentId && active.Contains(r.State), ct);
    }

    // ── Baselines ────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<List<PerformanceBaseline>> BaselinesAsync(string environmentId, string? definitionId = null, CancellationToken ct = default) =>
        (await db.PerformanceBaselines.AsNoTracking().Where(r => r.EnvironmentId == environmentId && (definitionId == null || r.DefinitionId == definitionId)).ToListAsync(ct))
            .Select(r => JsonSerializer.Deserialize<PerformanceBaseline>(r.DocumentJson, Json)!).OrderByDescending(b => b.Version).ThenByDescending(b => b.CreatedAt).ToList();

    public async Task<PerformanceBaseline?> BaselineAsync(string baselineId, CancellationToken ct = default)
    {
        var row = await db.PerformanceBaselines.AsNoTracking().FirstOrDefaultAsync(r => r.Id == baselineId, ct);
        return row is null ? null : JsonSerializer.Deserialize<PerformanceBaseline>(row.DocumentJson, Json);
    }

    /// <summary>The Active baseline of a scope (definition + environment + comparison fingerprint), or null.</summary>
    public async Task<PerformanceBaseline?> ActiveBaselineAsync(string environmentId, string definitionId, string comparisonFingerprint, CancellationToken ct = default)
    {
        var active = nameof(PerformanceBaselineStatus.Active);
        var row = await db.PerformanceBaselines.AsNoTracking().Where(r => r.EnvironmentId == environmentId && r.DefinitionId == definitionId
            && r.ComparisonFingerprint == comparisonFingerprint && r.Status == active).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        return row is null ? null : JsonSerializer.Deserialize<PerformanceBaseline>(row.DocumentJson, Json);
    }

    /// <summary>
    /// Explicit promotion of a run to the scope's new Active baseline. Only a Completed run with complete metrics qualifies; a run whose required
    /// thresholds failed needs <see cref="PerformanceBaselinePromotion.AcceptThresholdFailures"/>. The previous Active baseline is superseded (kept).
    /// </summary>
    public async Task<PerformanceBaselinePromotionResult> PromoteAsync(string environmentId, PerformanceBaselinePromotion request, DateTimeOffset now, CancellationToken ct = default)
    {
        var run = await RunAsync(request.RunId, ct);
        if (run is null || run.EnvironmentId != environmentId) return new(null, null, "The run does not exist in this environment.");
        if (run.State != PerformanceRunState.Completed)
            return new(null, null, $"A {run.State} run cannot become a baseline; only a completed run with full metrics can.");
        if (run.Metrics is null || run.MetricsPartial) return new(null, null, "The run has no complete metrics.");
        if (run.Verdict == PerformanceQualityVerdict.Fail && !request.AcceptThresholdFailures)
            return new(null, null, "Required thresholds failed in this run. Confirm explicitly to use it as the baseline anyway.");
        var scope = await db.PerformanceBaselines.Where(r => r.EnvironmentId == environmentId && r.DefinitionId == run.DefinitionId && r.ComparisonFingerprint == run.ComparisonFingerprint).ToListAsync(ct);
        var version = scope.Count == 0 ? 1 : scope.Max(r => r.Version) + 1;
        var baseline = new PerformanceBaseline
        {
            EnvironmentId = environmentId, ProjectId = run.ProjectId, DefinitionId = run.DefinitionId, ComparisonFingerprint = run.ComparisonFingerprint, RunId = run.RunId,
            Version = version, Name = string.IsNullOrWhiteSpace(request.Name) ? $"Baseline v{version}" : request.Name.Trim(), Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CreatedAt = now, EffectiveFrom = now, Status = PerformanceBaselineStatus.Active,
        };
        PerformanceBaseline? superseded = null;
        foreach (var row in scope.Where(r => r.Status == nameof(PerformanceBaselineStatus.Active)))
        {
            var old = JsonSerializer.Deserialize<PerformanceBaseline>(row.DocumentJson, Json)! with
                { Status = PerformanceBaselineStatus.Superseded, SupersededAt = now, SupersededByBaselineId = baseline.BaselineId };
            row.Status = old.Status.ToString(); row.DocumentJson = JsonSerializer.Serialize(old, Json);
            superseded = old;
        }
        db.PerformanceBaselines.Add(new PerformanceBaselineRecord
        {
            Id = baseline.BaselineId, EnvironmentId = environmentId, DefinitionId = baseline.DefinitionId, ComparisonFingerprint = baseline.ComparisonFingerprint,
            Status = baseline.Status.ToString(), Version = version, CreatedAt = now, DocumentJson = JsonSerializer.Serialize(baseline, Json),
        });
        await db.SaveChangesAsync(ct);
        return new(baseline, superseded, null);
    }

    /// <summary>Archives (Historical) a baseline — never deletes it; runs that referenced it keep the reference.</summary>
    public async Task<PerformanceBaseline?> ArchiveBaselineAsync(string environmentId, string baselineId, DateTimeOffset now, CancellationToken ct = default)
    {
        var row = await db.PerformanceBaselines.FirstOrDefaultAsync(r => r.Id == baselineId && r.EnvironmentId == environmentId, ct);
        if (row is null) return null;
        var baseline = JsonSerializer.Deserialize<PerformanceBaseline>(row.DocumentJson, Json)! with { Status = PerformanceBaselineStatus.Historical, SupersededAt = now };
        row.Status = baseline.Status.ToString(); row.DocumentJson = JsonSerializer.Serialize(baseline, Json);
        await db.SaveChangesAsync(ct);
        return baseline;
    }
}
