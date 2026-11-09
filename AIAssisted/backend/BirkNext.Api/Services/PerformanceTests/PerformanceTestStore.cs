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

    /// <summary>Every active (not archived) definition across environments: Test Data usage and reference replacement read them.</summary>
    public async Task<List<PerformanceTestDefinition>> AllActiveDefinitionsAsync(CancellationToken ct = default)
    {
        var rows = await db.PerformanceTestDefinitions.AsNoTracking().Where(r => !r.Archived).ToListAsync(ct);
        return [.. rows.Select(r => JsonSerializer.Deserialize<PerformanceTestDefinition>(r.DocumentJson, Json)!).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Id)];
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
        var fingerprint = Fingerprint(WithoutReferenceIds(normalized) with { UpdatedAt = default, CreatedAt = default });
        if (previous is not null && previous.Fingerprint == fingerprint)
        {
            // Only stable Test Data ids changed (migration or re-selection of the same asset): stored on the same version, never a new one.
            if (JsonSerializer.Serialize(previous.Scenario, Json) == JsonSerializer.Serialize(normalized.Scenario, Json)) return previous;
            var updated = previous with { Scenario = normalized.Scenario };
            row!.DocumentJson = JsonSerializer.Serialize(updated, Json);
            await db.SaveChangesAsync(ct);
            return updated;
        }
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

    /// <summary>The definition without stable Test Data ids: ids are reference bookkeeping, not part of what the definition is or measures.</summary>
    public static PerformanceTestDefinition WithoutReferenceIds(PerformanceTestDefinition d) => d with
    {
        Scenario = d.Scenario with
        {
            Steps = [.. d.Scenario.Steps.Select(s => s.DataMappings is null ? s : s with { DataMappings = [.. s.DataMappings.Select(m => m with { AssetIds = null })] })],
            Variables = d.Scenario.Variables is null ? null : [.. d.Scenario.Variables.Select(v => v with { AssetId = null })],
        },
    };

    public static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)))).ToLowerInvariant()[..32];

    /// <summary>What makes runs comparable: target, type, executed steps (their data mappings included, their provenance and discovery bookkeeping excluded), workload,
    /// environment class, test data profile and selection. Not thresholds, drift policies or names. A legacy definition keeps its exact value.</summary>
    public static string ComparisonFingerprint(PerformanceTestDefinition d)
    {
        d = WithoutReferenceIds(d); // runs stay comparable when only stable Test Data ids were recorded
        var legacy = Fingerprint(new
        {
            Target = d.TargetOrigin.TrimEnd('/').ToLowerInvariant(), d.TargetType, Steps = d.Scenario.Steps.Where(s => !s.Disabled).Select(s => s with { Source = null, Discovery = null }).ToList(), d.Workload,
            Environment = d.EnvironmentType.Trim().ToLowerInvariant(), d.Scenario.TestDataProfileId,
        });
        return d.Scenario.DataSelection is { } selection ? Fingerprint(new { legacy, selection }) : legacy;
    }

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

    /// <summary>Every run of the environment that still claims to be active, however old (reconciliation must not depend on recency).</summary>
    public async Task<List<PerformanceTestRun>> ActiveRunsAsync(string environmentId, CancellationToken ct = default)
    {
        var active = new[] { nameof(PerformanceRunState.Queued), nameof(PerformanceRunState.Preparing), nameof(PerformanceRunState.Running), nameof(PerformanceRunState.Cancelling) };
        var rows = await db.PerformanceTestRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId && active.Contains(r.State)).ToListAsync(ct);
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
    /// Proposes a completed run as the scope's next baseline. The eligibility review is recorded; a blocking failure refuses the proposal, a
    /// non-blocking one (failed required thresholds, too few samples for P95, dropped iterations) needs explicit acceptance. A proposal is never used
    /// for comparison and does not touch the Active baseline until it is approved.
    /// </summary>
    public async Task<PerformanceBaselineDecisionResult> ProposeAsync(string environmentId, PerformanceBaselineProposal request, string initiator, DateTimeOffset now, CancellationToken ct = default)
    {
        var run = await RunAsync(request.RunId, ct);
        if (run is null || run.EnvironmentId != environmentId) return new(null, null, "The run does not exist in this environment.");
        var checks = PerformanceRegressionRules.Eligibility(run);
        if (PerformanceRegressionRules.EligibilityProblem(checks, request.AcceptWarnings) is { } problem) return new(null, null, problem);
        return await Persistence.ScopedWriteLock.RunAsync(db, ScopeKey(environmentId, run), async () =>
        {
            var scope = await ScopeRowsAsync(environmentId, run, ct);
            var version = scope.Count == 0 ? 1 : scope.Max(r => r.Version) + 1;
            var baseline = new PerformanceBaseline
            {
                EnvironmentId = environmentId, ProjectId = run.ProjectId, DefinitionId = run.DefinitionId, ComparisonFingerprint = run.ComparisonFingerprint, RunId = run.RunId,
                Version = version, Name = string.IsNullOrWhiteSpace(request.Name) ? $"Baseline v{version}" : request.Name.Trim(), Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                CreatedAt = now, EffectiveFrom = now, Status = PerformanceBaselineStatus.Proposed, Approval = BaselineApprovalState.Proposed, ProposedBy = initiator,
                Eligibility = checks, AcceptedWarnings = checks.Any(c => !c.Blocking && !c.Passed), VersionLabel = run.VersionLabel, DefinitionVersion = run.DefinitionVersion,
            };
            db.PerformanceBaselines.Add(Row(baseline));
            await db.SaveChangesAsync(ct);
            return new PerformanceBaselineDecisionResult(baseline, null, null);
        }, ct);
    }

    /// <summary>
    /// Approves a proposal: it becomes the scope's Active baseline and the previous Active one is superseded (kept). Approved baselines are
    /// immutable — only their status moves on (Superseded, Historical); a new reference is a new baseline record.
    /// </summary>
    public async Task<PerformanceBaselineDecisionResult> ApproveAsync(string environmentId, string baselineId, PerformanceBaselineDecision decision, string approver, DateTimeOffset now, CancellationToken ct = default)
    {
        var proposed = await BaselineAsync(baselineId, ct);
        if (proposed is null || proposed.EnvironmentId != environmentId) return new(null, null, "The baseline does not exist in this environment.");
        if (proposed.Status != PerformanceBaselineStatus.Proposed) return new(null, null, $"Only a proposed baseline can be approved; this one is {proposed.Status}.");
        var run = await RunAsync(proposed.RunId, ct);
        if (run is null) return new(null, null, "The run this proposal refers to no longer exists.");
        if (PerformanceRegressionRules.EligibilityProblem(PerformanceRegressionRules.Eligibility(run), proposed.AcceptedWarnings) is { } problem) return new(null, null, problem);
        return await Persistence.ScopedWriteLock.RunAsync(db, ScopeKey(environmentId, run), async () =>
        {
            var scope = await ScopeRowsAsync(environmentId, run, ct);
            var row = scope.FirstOrDefault(r => r.Id == baselineId && r.Status == nameof(PerformanceBaselineStatus.Proposed));
            if (row is null) return new PerformanceBaselineDecisionResult(null, null, "The proposal was decided meanwhile.");
            var approved = proposed with
            {
                Status = PerformanceBaselineStatus.Active, Approval = BaselineApprovalState.Approved, ApprovedBy = approver, ApprovedAt = now, EffectiveFrom = now,
                DecisionNote = string.IsNullOrWhiteSpace(decision.Reason) ? null : decision.Reason.Trim(),
            };
            var superseded = Supersede(scope, approved.BaselineId, now);
            row.Status = approved.Status.ToString(); row.DocumentJson = JsonSerializer.Serialize(approved, Json);
            await db.SaveChangesAsync(ct);
            return new PerformanceBaselineDecisionResult(approved, superseded, null);
        }, ct);
    }

    /// <summary>Rejects a proposal (kept, with who and why). Never affects the Active baseline.</summary>
    public async Task<PerformanceBaselineDecisionResult> RejectAsync(string environmentId, string baselineId, PerformanceBaselineDecision decision, string by, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Reason)) return new(null, null, "Give a reason for the rejection.");
        var row = await db.PerformanceBaselines.FirstOrDefaultAsync(r => r.Id == baselineId && r.EnvironmentId == environmentId, ct);
        if (row is null) return new(null, null, "The baseline does not exist in this environment.");
        var baseline = JsonSerializer.Deserialize<PerformanceBaseline>(row.DocumentJson, Json)!;
        if (baseline.Status != PerformanceBaselineStatus.Proposed) return new(null, null, $"Only a proposed baseline can be rejected; this one is {baseline.Status}.");
        var rejected = baseline with { Status = PerformanceBaselineStatus.Rejected, Approval = BaselineApprovalState.Rejected, RejectedBy = by, RejectedAt = now, DecisionNote = decision.Reason.Trim() };
        row.Status = rejected.Status.ToString(); row.DocumentJson = JsonSerializer.Serialize(rejected, Json);
        await db.SaveChangesAsync(ct);
        return new(rejected, null, null);
    }

    /// <summary>
    /// One-step promotion (propose and approve by the same person), kept for existing clients. Same eligibility review; failed required thresholds need
    /// <see cref="PerformanceBaselinePromotion.AcceptThresholdFailures"/>; other non-blocking findings are recorded as accepted.
    /// </summary>
    public async Task<PerformanceBaselinePromotionResult> PromoteAsync(string environmentId, PerformanceBaselinePromotion request, DateTimeOffset now, CancellationToken ct = default, string initiator = "Local session")
    {
        var run = await RunAsync(request.RunId, ct);
        if (run is null || run.EnvironmentId != environmentId) return new(null, null, "The run does not exist in this environment.");
        if (run.State != PerformanceRunState.Completed)
            return new(null, null, $"A {run.State} run cannot become a baseline; only a completed run with full metrics can.");
        if (run.Metrics is null || run.MetricsPartial) return new(null, null, "The run has no complete metrics.");
        if (run.Verdict == PerformanceQualityVerdict.Fail && !request.AcceptThresholdFailures)
            return new(null, null, "Required thresholds failed in this run. Confirm explicitly to use it as the baseline anyway.");
        var proposed = await ProposeAsync(environmentId, new PerformanceBaselineProposal { RunId = request.RunId, Name = request.Name, Reason = request.Reason, AcceptWarnings = true }, initiator, now, ct);
        if (proposed.Baseline is null) return new(null, null, proposed.Error);
        var approved = await ApproveAsync(environmentId, proposed.Baseline.BaselineId, new() { Reason = "Proposed and approved in one step." }, initiator, now, ct);
        return new(approved.Baseline, approved.Superseded, approved.Error);
    }

    /// <summary>Archives (Historical) an approved baseline — never deletes it; runs that referenced it keep the reference.</summary>
    public async Task<PerformanceBaseline?> ArchiveBaselineAsync(string environmentId, string baselineId, DateTimeOffset now, CancellationToken ct = default)
    {
        var row = await db.PerformanceBaselines.FirstOrDefaultAsync(r => r.Id == baselineId && r.EnvironmentId == environmentId, ct);
        if (row is null) return null;
        var current = JsonSerializer.Deserialize<PerformanceBaseline>(row.DocumentJson, Json)!;
        if (current.Status is PerformanceBaselineStatus.Proposed or PerformanceBaselineStatus.Rejected) return null;
        var baseline = current with { Status = PerformanceBaselineStatus.Historical, SupersededAt = now };
        row.Status = baseline.Status.ToString(); row.DocumentJson = JsonSerializer.Serialize(baseline, Json);
        await db.SaveChangesAsync(ct);
        return baseline;
    }

    /// <summary>
    /// Deletes one finished run after an explicit confirmation. A run that is the source of a proposed or approved baseline (active, superseded or
    /// historical) is protected: a baseline is a reference to its run's evidence, so the run is never removed underneath it.
    /// </summary>
    public async Task<string?> DeleteRunAsync(string environmentId, Guid runId, PerformanceRunDeletion request, CancellationToken ct = default)
    {
        if (request.Confirm != 1) return "Confirm the deletion of exactly one run.";
        var row = await db.PerformanceTestRuns.FirstOrDefaultAsync(r => r.Id == runId && r.EnvironmentId == environmentId, ct);
        if (row is null) return "The run does not exist in this environment.";
        if (JsonSerializer.Deserialize<PerformanceTestRun>(row.DocumentJson, Json)!.IsActive) return "The run is still active; cancel it first.";
        var references = (await db.PerformanceBaselines.AsNoTracking().Where(b => b.EnvironmentId == environmentId && b.Status != nameof(PerformanceBaselineStatus.Rejected)).ToListAsync(ct))
            .Select(b => JsonSerializer.Deserialize<PerformanceBaseline>(b.DocumentJson, Json)!).Where(b => b.RunId == runId).ToList();
        if (references.Count > 0)
            return $"Protected: the run is the source of {string.Join(", ", references.Select(b => $"{b.Name} (v{b.Version}, {b.Status})"))}. A baseline always keeps its run.";
        db.PerformanceTestRuns.Remove(row);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static string ScopeKey(string environmentId, PerformanceTestRun run) => $"performance-baseline|{environmentId}|{run.DefinitionId}|{run.ComparisonFingerprint}";

    /// <summary>The scope's rows as stored now: rows tracked by an earlier call on this context are dropped first (EF would otherwise return their
    /// stale values, and a concurrent writer's Active baseline would not be superseded).</summary>
    private Task<List<PerformanceBaselineRecord>> ScopeRowsAsync(string environmentId, PerformanceTestRun run, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return db.PerformanceBaselines.Where(r => r.EnvironmentId == environmentId && r.DefinitionId == run.DefinitionId && r.ComparisonFingerprint == run.ComparisonFingerprint).ToListAsync(ct);
    }

    private static PerformanceBaseline? Supersede(List<PerformanceBaselineRecord> scope, string byBaselineId, DateTimeOffset now)
    {
        PerformanceBaseline? superseded = null;
        foreach (var row in scope.Where(r => r.Status == nameof(PerformanceBaselineStatus.Active)))
        {
            var old = JsonSerializer.Deserialize<PerformanceBaseline>(row.DocumentJson, Json)! with
                { Status = PerformanceBaselineStatus.Superseded, SupersededAt = now, SupersededByBaselineId = byBaselineId };
            row.Status = old.Status.ToString(); row.DocumentJson = JsonSerializer.Serialize(old, Json);
            superseded = old;
        }
        return superseded;
    }

    private static PerformanceBaselineRecord Row(PerformanceBaseline b) => new()
    {
        Id = b.BaselineId, EnvironmentId = b.EnvironmentId, DefinitionId = b.DefinitionId, ComparisonFingerprint = b.ComparisonFingerprint,
        Status = b.Status.ToString(), Version = b.Version, CreatedAt = b.CreatedAt, DocumentJson = JsonSerializer.Serialize(b, Json),
    };
}
