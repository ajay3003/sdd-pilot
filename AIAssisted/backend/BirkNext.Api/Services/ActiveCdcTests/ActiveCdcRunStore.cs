using System.Collections.Concurrent;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ActiveCdcTests;

/// <summary>
/// Durable run history. A run is inserted as Running before anything is sent, updated while Running, and frozen once completed: an update
/// to a completed run is refused. Each call uses its own scope so the background runner never shares a request's DbContext.
/// </summary>
public sealed class ActiveCdcRunStore(IServiceScopeFactory scopes, ILogger<ActiveCdcRunStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> InsertAsync(ActiveCdcRun run, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ActiveCdcRuns.Add(new ActiveCdcRunRecord
        {
            Id = run.RunId, EnvironmentId = run.EnvironmentId, IntegrationId = run.IntegrationId, SyntheticPersonPk = run.Fixture?.SyntheticPersonPk,
            SyntheticPersonPkControl = run.Messages.Select(m => m.SyntheticPersonPk).Where(pk => pk is not null && pk != run.Fixture?.SyntheticPersonPk).Max(),
            StartedAt = run.StartedAt, CompletedAt = run.CompletedAt, Status = run.Status.ToString(), ResultJson = JsonSerializer.Serialize(run, Json),
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Active CDC run {RunId} could not be recorded.", run.RunId); return false; }
    }

    /// <summary>Replaces the stored run while it is still Running. False when it is already completed (immutable) or cannot be saved.</summary>
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
        catch (DbUpdateException ex) { logger.LogWarning(ex, "Active CDC run {RunId} could not be updated.", run.RunId); return false; }
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
        return first is null ? control : control is null ? first : Math.Max(first.Value, control.Value);
    }
}

/// <summary>
/// Process-wide run control: one in-flight run per environment + integration (lease), the cancellation handle of each running run, and the
/// background task that executes it. A run left Running by a stopped process is not in here and is recovered as interrupted.
/// </summary>
public sealed class ActiveCdcRunCoordinator
{
    private readonly ConcurrentDictionary<string, Guid> _leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cancel, Task Task)> _running = new();

    public static string Key(string environmentId, string integrationId) => $"{environmentId}\n{integrationId}";

    public bool TryAcquire(string key, Guid runId) => _leases.TryAdd(key, runId);
    public void Release(string key, Guid runId) => _leases.TryRemove(new KeyValuePair<string, Guid>(key, runId));
    public Guid? LeaseHolder(string key) => _leases.TryGetValue(key, out var id) ? id : null;
    public bool IsRunning(Guid runId) => _running.ContainsKey(runId);

    public void Launch(Guid runId, string key, Func<CancellationToken, Task> execute)
    {
        var cancel = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(async () =>
        {
            await gate.Task;
            try { await execute(cancel.Token); }
            finally
            {
                _running.TryRemove(runId, out _);
                Release(key, runId);
                cancel.Dispose();
            }
        });
        _running[runId] = (cancel, task);
        gate.SetResult();
    }

    public bool Cancel(Guid runId)
    {
        if (!_running.TryGetValue(runId, out var run)) return false;
        try { run.Cancel.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>Tests and graceful shutdown: the background task of a running run, or a completed task.</summary>
    public Task Completion(Guid runId) => _running.TryGetValue(runId, out var run) ? run.Task : Task.CompletedTask;
}
