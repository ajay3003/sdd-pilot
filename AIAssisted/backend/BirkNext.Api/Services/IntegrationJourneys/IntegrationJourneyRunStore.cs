using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>Durable, payload-free journey history for every pack (one table, no pack-specific columns).</summary>
public sealed class IntegrationJourneyRunStore(IServiceScopeFactory scopes, ILogger<IntegrationJourneyRunStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> InsertAsync(IntegrationJourneyRun run, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.IntegrationJourneyRuns.Add(new IntegrationJourneyRunRecord
        {
            Id = run.RunId, EnvironmentId = run.EnvironmentId, PackId = run.PackId, JourneyId = run.JourneyId, ScenarioId = run.ScenarioId,
            StartedAt = run.StartedAt, CompletedAt = run.CompletedAt, State = run.OverallState.ToString(), ResultJson = JsonSerializer.Serialize(run, Json),
        });
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
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Integration journey run {RunId} could not be updated.", run.RunId); return false; }
    }

    public async Task<IntegrationJourneyRun?> GetAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.IntegrationJourneyRuns.AsNoTracking().FirstOrDefaultAsync(item => item.Id == runId, ct);
        return record is null ? null : JsonSerializer.Deserialize<IntegrationJourneyRun>(record.ResultJson, Json);
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
        return records
            .Where(item => (query.From is not { } from || item.StartedAt >= from) && (query.To is not { } to || item.StartedAt <= to))
            .OrderByDescending(item => item.StartedAt).Take(take)
            .Select(item => JsonSerializer.Deserialize<IntegrationJourneyRun>(item.ResultJson, Json)).OfType<IntegrationJourneyRun>().ToArray();
    }
}
