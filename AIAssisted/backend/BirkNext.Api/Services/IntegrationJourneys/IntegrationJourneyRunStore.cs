using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>Durable journey history for every pack (one table, no pack-specific columns). It holds no unredacted payload: a step can carry only a
/// redacted payload evidence snapshot, redacted before it reaches this store.</summary>
public sealed class IntegrationJourneyRunStore(IServiceScopeFactory scopes, ILogger<IntegrationJourneyRunStore> logger, TimeProvider? time = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string InterruptedReason = "Interrupted: the backend stopped before this run finished. Its outcome is unknown; run the journey again.";

    /// <summary>Runs started by this process and not finished yet. A Running row that is not here belongs to a process that is gone.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _inFlight = new();

    public async Task<bool> InsertAsync(IntegrationJourneyRun run, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.IntegrationJourneyRuns.Add(new IntegrationJourneyRunRecord
        {
            Id = run.RunId, EnvironmentId = run.EnvironmentId, PackId = run.PackId, JourneyId = run.JourneyId, ScenarioId = run.ScenarioId,
            StartedAt = run.StartedAt, CompletedAt = run.CompletedAt, State = run.OverallState.ToString(), ResultJson = JsonSerializer.Serialize(run, Json),
        });
        if (run.OverallState == JourneyRunState.Running) _inFlight[run.RunId] = 0;
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Integration journey run {RunId} could not be recorded.", run.RunId); return false; }
    }

    /// <summary>Updates a Running row only; a completed run is immutable.</summary>
    public async Task<bool> UpdateAsync(IntegrationJourneyRun run, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.IntegrationJourneyRuns.FirstOrDefaultAsync(item => item.Id == run.RunId, ct);
        if (record is null || record.State != nameof(JourneyRunState.Running)) return false;
        record.State = run.OverallState.ToString();
        record.CompletedAt = run.CompletedAt;
        record.ResultJson = JsonSerializer.Serialize(run, Json);
        if (run.OverallState != JourneyRunState.Running) _inFlight.TryRemove(run.RunId, out _);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Integration journey run {RunId} could not be updated.", run.RunId); return false; }
    }

    public async Task<IntegrationJourneyRun?> GetAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.IntegrationJourneyRuns.AsNoTracking().FirstOrDefaultAsync(item => item.Id == runId, ct);
        var run = record is null ? null : JsonSerializer.Deserialize<IntegrationJourneyRun>(record.ResultJson, Json);
        return run is null ? null : await RecoverAsync(run, ct);
    }

    /// <summary>
    /// A Running row whose process is gone (crash or restart) is recorded as NotVerified with the reason — never left Running forever and never
    /// shown as "never run". Rows of runs still executing in this process are returned unchanged.
    /// </summary>
    private async Task<IntegrationJourneyRun> RecoverAsync(IntegrationJourneyRun run, CancellationToken ct)
    {
        if (run.OverallState != JourneyRunState.Running || _inFlight.ContainsKey(run.RunId)) return run;
        var recovered = run with { OverallState = JourneyRunState.NotVerified, StateReason = InterruptedReason, CompletedAt = (time ?? TimeProvider.System).GetUtcNow() };
        if (!await UpdateAsync(recovered, ct)) logger.LogWarning("Interrupted integration journey run {RunId} could not be marked; it is shown as interrupted.", run.RunId);
        return recovered;
    }

    public async Task<IReadOnlyList<IntegrationJourneyRun>> ListAsync(IntegrationJourneyHistoryQuery query, int take, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = db.IntegrationJourneyRuns.AsNoTracking().Where(item => item.EnvironmentId == query.EnvironmentId);
        if (!string.IsNullOrWhiteSpace(query.PackId)) rows = rows.Where(item => item.PackId == query.PackId);
        if (!string.IsNullOrWhiteSpace(query.JourneyId)) rows = rows.Where(item => item.JourneyId == query.JourneyId);
        if (!string.IsNullOrWhiteSpace(query.ScenarioId)) rows = rows.Where(item => item.ScenarioId == query.ScenarioId);
        if (query.State is { } state) rows = rows.Where(item => item.State == state.ToString());
        var records = await rows.ToListAsync(ct);
        var recovered = records
            .Where(item => (query.From is not { } from || item.StartedAt >= from) && (query.To is not { } to || item.StartedAt <= to))
            .OrderByDescending(item => item.StartedAt).Take(take)
            .Select(item => JsonSerializer.Deserialize<IntegrationJourneyRun>(item.ResultJson, Json)).OfType<IntegrationJourneyRun>().ToArray();
        var result = new List<IntegrationJourneyRun>(records.Count);
        foreach (var run in recovered) result.Add(await RecoverAsync(run, ct));
        return result;
    }
}
