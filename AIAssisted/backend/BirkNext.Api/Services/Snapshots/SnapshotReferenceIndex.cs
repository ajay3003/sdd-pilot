using BirkNext.Api.Data;
using BirkNext.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Snapshots;

/// <summary>
/// Read-only reference index for Source Analysis snapshots: which reviews, comparisons and bindings point at a snapshot id. It queries the id columns
/// the owning stores already keep (no new table, no payload loads) and names the stores it cannot check, so a future delete or retention rule can refuse
/// to remove a referenced snapshot instead of silently breaking a historical review.
/// </summary>
public sealed class SnapshotReferenceIndex(AppDbContext db)
{
    /// <summary>Stores that keep Source Analysis snapshot ids only inside JSON payloads or browser storage (not checked here).</summary>
    public static readonly IReadOnlyList<string> UncheckedSourceAnalysisReferrers =
    [
        "Integration Quality Review runs (ids inside the result payload)",
        "Integration journey runs (bound snapshot ids inside the result payload)",
        "Performance test runs (source snapshot id inside the run document)",
        "Dependency Review runs (primary/related snapshot ids inside the result payload)",
        "Azure environment snapshots (source snapshot id inside the summary payload)",
        "Project Evidence and SDD lifecycle references (browser storage / saved workspace)",
    ];

    public async Task<SnapshotReferenceReport> SourceAnalysisAsync(Guid id, CancellationToken ct = default)
    {
        var snapshot = new SnapshotReference(SnapshotType.SourceAnalysis, id.ToString());
        var exists = await db.IqrSourceSnapshots.AsNoTracking().AnyAsync(s => s.Id == id, ct);
        var refs = new List<SnapshotReferrer>();

        foreach (var r in await db.ImpactAnalysisRuns.AsNoTracking().Where(r => r.CurrentSnapshotId == id || r.BaselineSnapshotId == id)
                     .Select(r => new { r.Id, r.ProjectDisplayName, r.CurrentSnapshotId, r.CreatedAt }).ToListAsync(ct))
            refs.Add(new(new(SnapshotType.Review, r.Id.ToString(), r.ProjectDisplayName), r.CurrentSnapshotId == id ? SnapshotReferenceRole.Current : SnapshotReferenceRole.Baseline,
                "Impact Analysis", r.CreatedAt));
        foreach (var r in await db.AiCodeReviewRuns.AsNoTracking().Where(r => r.CurrentSnapshotId == id || r.BaselineSnapshotId == id)
                     .Select(r => new { r.Id, r.Label, r.CurrentSnapshotId, r.CompletedAt }).ToListAsync(ct))
            refs.Add(new(new(SnapshotType.Review, r.Id.ToString(), r.Label), r.CurrentSnapshotId == id ? SnapshotReferenceRole.Current : SnapshotReferenceRole.Baseline,
                "Code Risk Review", r.CompletedAt));
        foreach (var r in await db.TestCoverageReviewRuns.AsNoTracking().Where(r => r.CurrentSnapshotId == id || r.BaselineSnapshotId == id)
                     .Select(r => new { r.Id, r.Label, r.CurrentSnapshotId, r.CompletedAt }).ToListAsync(ct))
            refs.Add(new(new(SnapshotType.Review, r.Id.ToString(), r.Label), r.CurrentSnapshotId == id ? SnapshotReferenceRole.Current : SnapshotReferenceRole.Baseline,
                "Test Coverage Review", r.CompletedAt));
        foreach (var r in await db.SecurityExpectationDiscoveries.AsNoTracking().Where(r => r.SourceSnapshotId == id)
                     .Select(r => new { r.Id, r.EnvironmentId, r.Revision, r.CreatedAt }).ToListAsync(ct))
            refs.Add(new(new(SnapshotType.Review, r.Id.ToString(), r.EnvironmentId, r.Revision.ToString()), SnapshotReferenceRole.Current, "Security expectations", r.CreatedAt));
        foreach (var r in await db.IntegrationEvidenceBindings.AsNoTracking().Where(r => r.SnapshotId == id)
                     .Select(r => new { r.EnvironmentId, r.IntegrationId, r.UpdatedAt }).ToListAsync(ct))
            refs.Add(new(new(SnapshotType.ProjectEvidence, $"{r.EnvironmentId}/{r.IntegrationId}", r.IntegrationId), SnapshotReferenceRole.Bound, "Integration evidence binding", r.UpdatedAt));
        foreach (var r in await db.SourceSchemaArtifacts.AsNoTracking().Where(r => r.SnapshotId == id)
                     .Select(r => new { r.SnapshotId, r.SourceArchiveName, r.CapturedAt }).ToListAsync(ct))
            refs.Add(new(new(SnapshotType.SourceAnalysis, r.SnapshotId.ToString(), $"Schema package · {r.SourceArchiveName}"), SnapshotReferenceRole.Derived, "Schema Studio", r.CapturedAt));

        // Deterministic order: owner, then time, then id (never store iteration order).
        return new SnapshotReferenceReport(snapshot, exists,
            [.. refs.OrderBy(r => r.Owner, StringComparer.Ordinal).ThenBy(r => r.RecordedAt).ThenBy(r => r.Referrer.Id, StringComparer.Ordinal)],
            UncheckedSourceAnalysisReferrers);
    }
}
