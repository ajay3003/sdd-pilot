using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.SourceEvidence;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.SourceAnalysis;

/// <summary>
/// Remembered "same repository" declarations between two repository identities (e.g. M2LB-language and M2LB, imported as separate ZIPs). A
/// declaration is made by a person — never detected, never inferred from names — and is recorded with who and when. Revoking ends it for future
/// comparisons; nothing is deleted, and results recorded with it keep their stored relation.
/// </summary>
public sealed class SourceRepositoryAssociationService(AppDbContext db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IReadOnlyList<SourceRepositoryAssociation>> ActiveAsync(CancellationToken ct = default) =>
        [.. (await db.SourceRepositoryAssociations.AsNoTracking().Where(r => r.RevokedAt == null).OrderBy(r => r.DeclaredAt).ToListAsync(ct)).Select(View)];

    public async Task<SourceRepositoryAssociation?> FindAsync(string keyA, string keyB, CancellationToken ct = default) =>
        (await ActiveAsync(ct)).FirstOrDefault(a => a.Links(keyA, keyB));

    /// <summary>Declares (or returns the existing active) association. The same key twice is not an association.</summary>
    public async Task<(SourceRepositoryAssociation? Association, string? Error)> DeclareAsync(DeclareRepositoryAssociationRequest request, string declaredBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryKeyA) || string.IsNullOrWhiteSpace(request.RepositoryKeyB))
            return (null, "Both repository identities are required.");
        if (string.Equals(request.RepositoryKeyA, request.RepositoryKeyB, StringComparison.Ordinal))
            return (null, "These snapshots already have the same repository identity; no declaration is needed.");
        if (await FindAsync(request.RepositoryKeyA, request.RepositoryKeyB, ct) is { } existing) return (existing, null);
        var record = new SourceRepositoryAssociationRecord
        {
            Id = Guid.NewGuid(), RepositoryKeyA = request.RepositoryKeyA.Trim(), RepositoryA = Bound(request.RepositoryA), RepositoryKeyB = request.RepositoryKeyB.Trim(),
            RepositoryB = Bound(request.RepositoryB), DeclaredBy = Bound(string.IsNullOrWhiteSpace(declaredBy) ? "local user" : declaredBy, 200), DeclaredAt = _time.GetUtcNow(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : Bound(request.Note, 1000),
        };
        db.SourceRepositoryAssociations.Add(record);
        await db.SaveChangesAsync(ct);
        return (View(record), null);
    }

    /// <summary>Ends an association for future comparisons (the record stays, with who revoked it and when).</summary>
    public async Task<bool> RevokeAsync(Guid id, string revokedBy, CancellationToken ct = default)
    {
        var record = await db.SourceRepositoryAssociations.FirstOrDefaultAsync(r => r.Id == id && r.RevokedAt == null, ct);
        if (record is null) return false;
        (record.RevokedAt, record.RevokedBy) = (_time.GetUtcNow(), Bound(string.IsNullOrWhiteSpace(revokedBy) ? "local user" : revokedBy, 200));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Bound(string value, int max = 300) => value.Trim() is var v && v.Length > max ? v[..max] : value.Trim();

    private static SourceRepositoryAssociation View(SourceRepositoryAssociationRecord r) =>
        new(r.Id, r.RepositoryKeyA, r.RepositoryA, r.RepositoryKeyB, r.RepositoryB, r.DeclaredBy, r.DeclaredAt, r.Note);
}
