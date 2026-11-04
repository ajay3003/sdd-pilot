using BirkNext.Api.Data.CanonicalInterpretation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>Deletes every table of the canonical_interpretation schema in one transaction, children first (only on a full local data reset).</summary>
public sealed class CanonicalInterpretationDataReset(CanonicalInterpretationDbContext db, ILogger<CanonicalInterpretationDataReset> logger)
{
    public async Task<int> ResetAsync(CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sql = db.GetService<ISqlGenerationHelper>();
        var deleted = 0;
        var tables = db.Model.GetRelationalModel().Tables.ToList();
        var remaining = tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.First(t => !remaining.Any(other => other != t && other.ForeignKeyConstraints.Any(fk => fk.PrincipalTable == t)));
            deleted += await db.Database.ExecuteSqlRawAsync($"DELETE FROM {sql.DelimitIdentifier(next.Name, next.Schema)}", ct);
            remaining.Remove(next);
        }
        await transaction.CommitAsync(ct);
        logger.LogWarning("Canonical interpretation reset: {Deleted} rows removed from the canonical_interpretation schema", deleted);
        return deleted;
    }
}
