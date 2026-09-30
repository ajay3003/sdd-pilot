using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

public sealed class InvalidSourceSelectionException(string message) : Exception(message);

public sealed class IqrSourceStore(AppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default)
    {
        var records = await db.IqrSourceSnapshots.AsNoTracking().Where(r => r.EnvironmentId == environmentId)
            .OrderByDescending(r => r.AnalyzedAt).Take(50).ToListAsync(ct);
        return records.Select(r => JsonSerializer.Deserialize<IqrSourceSnapshot>(r.EvidenceJson, Json)!).ToList();
    }
    public async Task<IqrSourceSnapshot?> GetAsync(string environmentId, string integrationId, Guid id, CancellationToken ct = default)
    {
        var record = await db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId && r.IntegrationId == integrationId, ct);
        return record is null ? null : JsonSerializer.Deserialize<IqrSourceSnapshot>(record.EvidenceJson, Json);
    }
    public async Task<(IqrSourceSnapshot? Snapshot, string? Error)> AnalyzeAsync(string environmentId, string integrationId, string name, byte[] bytes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(integrationId)) return (null, "Environment and integration identity are required.");
        var (workspace, error) = IqrSourceArchiveReader.Read(name, bytes, ct);
        if (workspace is null) return (null, error);
        ct.ThrowIfCancellationRequested();
        var snapshot = IqrSourceAnalyzer.Analyze(integrationId, workspace, DateTimeOffset.UtcNow, ct);
        // Insert only. Identical archive hashes still create distinct evidence versions when analyzed again.
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = snapshot.Id, EnvironmentId = environmentId, IntegrationId = integrationId,
            AnalyzedAt = snapshot.AnalyzedAt, EvidenceJson = JsonSerializer.Serialize(snapshot, Json) });
        await db.SaveChangesAsync(ct);
        return (snapshot, null);
    }
}
