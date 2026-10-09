using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ActiveCdcTests;

/// <summary>
/// LEGACY, read-only: history of runs recorded by the retired Active CDC runner (<c>active_cdc_runs</c>). Nothing new is written here —
/// every new run goes through the generic Active Event lifecycle and <c>active_event_runs</c>. Kept for stored history, the recovery of a
/// row a stopped process left Running, and the highest synthetic PersonPK already used (so the Person provider never reuses one).
/// </summary>
public sealed class ActiveCdcRunStore(IServiceScopeFactory scopes, ILogger<ActiveCdcRunStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Completes a run that a stopped process left Running (recovery only; nothing new is ever inserted).</summary>
    public async Task<bool> UpdateAsync(ActiveCdcRun run, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.ActiveCdcRuns.FirstOrDefaultAsync(r => r.Id == run.RunId, ct);
        if (record is null || record.Status != nameof(ActiveCdcRunStatus.Running)) return false;
        record.Status = run.Status.ToString();
        record.CompletedAt = run.CompletedAt;
        record.ResultJson = JsonSerializer.Serialize(run, Json);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Legacy Active CDC run {RunId} could not be updated.", run.RunId); return false; }
    }

    public async Task<ActiveCdcRun?> GetAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.ActiveCdcRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return record is null ? null : JsonSerializer.Deserialize<ActiveCdcRun>(record.ResultJson, Json);
    }

    public async Task<IReadOnlyList<ActiveCdcRun>> ListAsync(string environmentId, string? integrationId, int take, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = db.ActiveCdcRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId);
        if (!string.IsNullOrWhiteSpace(integrationId)) query = query.Where(r => r.IntegrationId == integrationId);
        var records = await query.OrderByDescending(r => r.StartedAt).Take(take).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<ActiveCdcRun>(r.ResultJson, Json)).OfType<ActiveCdcRun>().ToList();
    }

    /// <summary>Highest synthetic PersonPK already used in this environment within the reserved range (any run, any status, either key).</summary>
    public async Task<int?> MaxPersonPkAsync(string environmentId, int min, int max, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = db.ActiveCdcRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId);
        var first = await runs.Where(r => r.SyntheticPersonPk >= min && r.SyntheticPersonPk <= max).MaxAsync(r => r.SyntheticPersonPk, ct);
        var control = await runs.Where(r => r.SyntheticPersonPkControl >= min && r.SyntheticPersonPkControl <= max).MaxAsync(r => r.SyntheticPersonPkControl, ct);
        var fromRuns = first is null ? control : control is null ? first : Math.Max(first.Value, control.Value);
        // A local data reset deletes the run rows but keeps the highest synthetic PK already sent per environment, so a later run never
        // reuses a key that exists in the real Event Hub / downstream system.
        var floor = scope.ServiceProvider.GetService<BirkNext.Api.Services.LocalDataReset.LocalDataResetState>()?.CdcPersonPkFloor(environmentId);
        if (floor is not { } f || f < min || f > max) return fromRuns;
        return fromRuns is null ? f : Math.Max(fromRuns.Value, f);
    }
}
