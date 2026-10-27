using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>
/// The canonical source-evidence bindings of Integration Quality Review: one immutable Source Analysis snapshot per configured integration.
/// A binding stores the snapshot's identity as it was when bound, so a newer snapshot never replaces it silently; it changes only when a
/// user binds another snapshot. Test packs and providers read these bindings and never select a snapshot of their own.
/// </summary>
public interface IIntegrationEvidenceBindingStore
{
    Task<IReadOnlyList<IntegrationEvidenceBinding>> ListAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Binds (or with a null snapshot, removes) one integration's evidence. The integration must be configured in the environment and
    /// the snapshot must be a stored Source Analysis snapshot.</summary>
    Task<(IntegrationEvidenceBinding? Binding, string? Error)> SetAsync(string environmentId, IntegrationEvidenceBindingRequest request, CancellationToken ct = default);
}

public sealed class IntegrationEvidenceBindingStore(AppDbContext db, IqrSourceStore sources, IIntegrationCatalogService catalog, TimeProvider? clock = null)
    : IIntegrationEvidenceBindingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IReadOnlyList<IntegrationEvidenceBinding>> ListAsync(string environmentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return [];
        var rows = await db.IntegrationEvidenceBindings.AsNoTracking().Where(r => r.EnvironmentId == environmentId).OrderBy(r => r.IntegrationId).ToListAsync(ct);
        return rows.Select(r => JsonSerializer.Deserialize<IntegrationEvidenceBinding>(r.DocumentJson, Json)).OfType<IntegrationEvidenceBinding>().ToList();
    }

    public async Task<(IntegrationEvidenceBinding? Binding, string? Error)> SetAsync(string environmentId, IntegrationEvidenceBindingRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(request.IntegrationId)) return (null, "Environment and integration are required.");
        var configured = await catalog.GetAsync(environmentId, null, null, ct);
        if (configured.Integrations.All(item => item.Id != request.IntegrationId))
            return (null, "The integration is not configured for this Target Environment; configure it under Target Environment → Integration Catalog first.");
        var row = await db.IntegrationEvidenceBindings.FirstOrDefaultAsync(r => r.EnvironmentId == environmentId && r.IntegrationId == request.IntegrationId, ct);
        if (request.SnapshotId is not { } snapshotId)
        {
            if (row is not null) { db.IntegrationEvidenceBindings.Remove(row); await db.SaveChangesAsync(ct); }
            return (null, null);
        }
        var snapshot = await sources.GetAsync(environmentId, BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore.SourceAnalysisOwner, snapshotId, ct)
            ?? (await sources.ListAsync(environmentId, ct)).FirstOrDefault(item => item.Id == snapshotId);
        if (snapshot is null) return (null, "The Source Analysis snapshot was not found; choose a stored snapshot.");
        var binding = IntegrationEvidenceBinding.For(environmentId, request.IntegrationId, snapshot, _clock.GetUtcNow());
        var json = JsonSerializer.Serialize(binding, Json);
        if (row is null)
            db.IntegrationEvidenceBindings.Add(new IntegrationEvidenceBindingRecord { EnvironmentId = environmentId, IntegrationId = request.IntegrationId, SnapshotId = snapshotId, DocumentJson = json, UpdatedAt = binding.BoundAt });
        else { row.SnapshotId = snapshotId; row.DocumentJson = json; row.UpdatedAt = binding.BoundAt; }
        await db.SaveChangesAsync(ct);
        return (binding, null);
    }
}
