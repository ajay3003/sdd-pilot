using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>Persists generic, body-free Active Event lifecycle results independently of the legacy Active CDC history table.</summary>
public sealed class ActiveEventRunStore(IServiceScopeFactory scopes, ILogger<ActiveEventRunStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> InsertRunningAsync(ActiveEventRunResult result, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ActiveEventRuns.Add(new ActiveEventRunRecord
        {
            Id = result.RunId,
            EnvironmentId = result.Target.TargetEnvironmentId,
            IntegrationId = result.Target.IntegrationId,
            StartedAt = result.StartedAt,
            Status = ActiveEventRunStatus.Running.ToString(),
            ResultJson = JsonSerializer.Serialize(result with { Status = ActiveEventRunStatus.Running, CompletedAt = null }, Json),
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Active Event run {RunId} could not be recorded before execution.", result.RunId); return false; }
    }

    public async Task<bool> CompleteAsync(ActiveEventRunResult result, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.ActiveEventRuns.FirstOrDefaultAsync(item => item.Id == result.RunId, ct);
        if (record is null || record.Status != nameof(ActiveEventRunStatus.Running)) return false;
        record.Status = result.Status.ToString();
        record.CompletedAt = result.CompletedAt;
        record.ResultJson = JsonSerializer.Serialize(result, Json);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Active Event run {RunId} could not be completed in history.", result.RunId); return false; }
    }

    public async Task<bool> UpdateRunningAsync(ActiveEventRunResult result, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.ActiveEventRuns.FirstOrDefaultAsync(item => item.Id == result.RunId, ct);
        if (record is null || record.Status != nameof(ActiveEventRunStatus.Running)) return false;
        record.ResultJson = JsonSerializer.Serialize(result with { Status = ActiveEventRunStatus.Running, CompletedAt = null }, Json);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Active Event run {RunId} progress could not be saved.", result.RunId); return false; }
    }

    public async Task<ActiveEventRunResult?> GetAsync(Guid runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.ActiveEventRuns.AsNoTracking().FirstOrDefaultAsync(item => item.Id == runId, ct);
        return record is null ? null : Read(record.ResultJson);
    }

    public async Task<IReadOnlyList<ActiveEventRunResult>> ListAsync(string environmentId, string? integrationId, int take, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = db.ActiveEventRuns.AsNoTracking().Where(item => item.EnvironmentId == environmentId);
        if (!string.IsNullOrWhiteSpace(integrationId)) query = query.Where(item => item.IntegrationId == integrationId);
        var records = await query.OrderByDescending(item => item.StartedAt).Take(take).ToListAsync(ct);
        return records.Select(item => Read(item.ResultJson)).OfType<ActiveEventRunResult>().ToArray();
    }

    /// <summary>Rows written before the stage was renamed (DownstreamPersistenceVerified → DownstreamVerified) keep their meaning.</summary>
    private static ActiveEventRunResult? Read(string json) =>
        JsonSerializer.Deserialize<ActiveEventRunResult>(json.Replace("\"DownstreamPersistenceVerified\"", "\"DownstreamVerified\"", StringComparison.Ordinal), Json);
}
