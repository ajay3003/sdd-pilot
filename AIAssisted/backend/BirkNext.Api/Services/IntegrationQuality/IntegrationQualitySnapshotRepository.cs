using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// History storage for Integration Quality Review snapshots. Owns persistence only; contract
/// difference semantics stay in ContractComparer.
/// </summary>
public interface IIntegrationQualitySnapshotRepository
{
    Task SaveAsync(IntegrationQualitySnapshot snapshot, CancellationToken ct = default);

    /// <summary>Most recent complete snapshot for an environment, or null when there is no history.</summary>
    Task<IntegrationQualitySnapshot?> GetLatestAsync(string environmentId, CancellationToken ct = default);

    /// <summary>Most recent entry for one integration, resolved by baseline key within one environment.</summary>
    Task<IntegrationSnapshotEntry?> GetLatestForIntegrationAsync(
        string environmentId, string baselineKey, CancellationToken ct = default);

    Task<IntegrationQualitySnapshot?> GetByIdAsync(Guid snapshotId, CancellationToken ct = default);
}

public sealed class IntegrationQualitySnapshotRepository : IIntegrationQualitySnapshotRepository
{
    private readonly AppDbContext _db;
    private readonly ILogger<IntegrationQualitySnapshotRepository> _logger;

    public IntegrationQualitySnapshotRepository(
        AppDbContext db,
        ILogger<IntegrationQualitySnapshotRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SaveAsync(IntegrationQualitySnapshot snapshot, CancellationToken ct = default)
    {
        var record = new IntegrationQualitySnapshotRecord
        {
            Id = snapshot.SnapshotId,
            EnvironmentId = snapshot.EnvironmentId,
            CapturedAt = snapshot.CapturedAt,
            SnapshotVersion = snapshot.SnapshotVersion,
            BaselineIdentityVersion = snapshot.BaselineIdentityVersion,
            PreviousSnapshotId = snapshot.PreviousSnapshotId,
            Completeness = snapshot.Completeness.ToString(),
            IntegrationsJson = JsonSerializer.Serialize(snapshot.Integrations)
        };

        // Insert only. Snapshots are immutable, so nothing is ever updated in place, and the
        // entity is detached afterwards so later SaveChanges cannot rewrite it.
        _db.IntegrationQualitySnapshots.Add(record);
        await _db.SaveChangesAsync(ct);
        _db.Entry(record).State = EntityState.Detached;
    }

    public async Task<IntegrationQualitySnapshot?> GetLatestAsync(
        string environmentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            return null;

        var record = await _db.IntegrationQualitySnapshots
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId
                        && s.Completeness == nameof(SnapshotCompleteness.Complete))
            // CapturedAt then Id: deterministic even if two rows share a timestamp.
            .OrderByDescending(s => s.CapturedAt)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

        return Materialise(record);
    }

    public async Task<IntegrationSnapshotEntry?> GetLatestForIntegrationAsync(
        string environmentId, string baselineKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(baselineKey))
            return null;

        // Walk back from the newest snapshot: an integration may be absent from the most recent
        // review yet present earlier, and a malformed row must not hide the rest of the history.
        var records = await _db.IntegrationQualitySnapshots
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId
                        && s.Completeness == nameof(SnapshotCompleteness.Complete))
            .OrderByDescending(s => s.CapturedAt)
            .ThenByDescending(s => s.Id)
            .Take(50)
            .ToListAsync(ct);

        foreach (var record in records)
        {
            var snapshot = Materialise(record);
            var entry = snapshot?.Integrations
                .FirstOrDefault(e => string.Equals(e.BaselineKey, baselineKey, StringComparison.Ordinal));

            if (entry is not null)
                return entry;
        }

        return null;
    }

    public async Task<IntegrationQualitySnapshot?> GetByIdAsync(
        Guid snapshotId, CancellationToken ct = default) =>
        Materialise(await _db.IntegrationQualitySnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct));

    /// <summary>
    /// Rehydrates a stored row. A row whose payload cannot be parsed is skipped rather than
    /// thrown, so one corrupt snapshot cannot take down the rest of the history.
    /// </summary>
    private IntegrationQualitySnapshot? Materialise(IntegrationQualitySnapshotRecord? record)
    {
        if (record is null)
            return null;

        List<IntegrationSnapshotEntry>? integrations;

        try
        {
            integrations = JsonSerializer.Deserialize<List<IntegrationSnapshotEntry>>(record.IntegrationsJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Snapshot {SnapshotId} has an unreadable payload and was skipped", record.Id);
            return null;
        }

        return new IntegrationQualitySnapshot
        {
            SnapshotId = record.Id,
            EnvironmentId = record.EnvironmentId,
            CapturedAt = record.CapturedAt,
            SnapshotVersion = record.SnapshotVersion,
            BaselineIdentityVersion = record.BaselineIdentityVersion,
            PreviousSnapshotId = record.PreviousSnapshotId,
            Completeness = Enum.TryParse<SnapshotCompleteness>(record.Completeness, out var c)
                ? c
                : SnapshotCompleteness.Partial,
            Integrations = integrations ?? []
        };
    }
}

/// <summary>
/// In-memory history, used by tests and by deployments without a database. Same immutability and
/// ordering rules as the persistent implementation.
/// </summary>
public sealed class InMemoryIntegrationQualitySnapshotRepository : IIntegrationQualitySnapshotRepository
{
    private readonly List<IntegrationQualitySnapshot> _snapshots = [];
    private readonly object _gate = new();

    public Task SaveAsync(IntegrationQualitySnapshot snapshot, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_snapshots.Any(s => s.SnapshotId == snapshot.SnapshotId))
                throw new InvalidOperationException(
                    $"Snapshot {snapshot.SnapshotId} already exists; snapshots are immutable");

            _snapshots.Add(snapshot);
        }

        return Task.CompletedTask;
    }

    public Task<IntegrationQualitySnapshot?> GetLatestAsync(
        string environmentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Ordered(environmentId).FirstOrDefault());
        }
    }

    public Task<IntegrationSnapshotEntry?> GetLatestForIntegrationAsync(
        string environmentId, string baselineKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var entry = Ordered(environmentId)
                .Select(s => s.Integrations
                    .FirstOrDefault(e => string.Equals(e.BaselineKey, baselineKey, StringComparison.Ordinal)))
                .FirstOrDefault(e => e is not null);

            return Task.FromResult(entry);
        }
    }

    public Task<IntegrationQualitySnapshot?> GetByIdAsync(Guid snapshotId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_snapshots.FirstOrDefault(s => s.SnapshotId == snapshotId));
        }
    }

    private IEnumerable<IntegrationQualitySnapshot> Ordered(string environmentId) =>
        string.IsNullOrWhiteSpace(environmentId)
            ? []
            : _snapshots
                .Where(s => string.Equals(s.EnvironmentId, environmentId, StringComparison.Ordinal)
                            && s.Completeness == SnapshotCompleteness.Complete)
                .OrderByDescending(s => s.CapturedAt)
                .ThenByDescending(s => s.SnapshotId);
}
