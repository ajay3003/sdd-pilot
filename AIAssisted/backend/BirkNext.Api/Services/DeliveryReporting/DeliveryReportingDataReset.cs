using BirkNext.Api.Data.DeliveryReporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>
/// Delivery Management owns how its data is deleted: every table of its own <c>delivery_reporting</c> schema (reporting projects, report sources,
/// snapshots with their features / stories / criteria / phases / tasks / artifacts / evidence, comparisons, frozen reports and their items) in one
/// transaction, children first by foreign key, plus imports staged in memory. Nothing outside the schema: Delivery Reporting never owns project
/// imports, Source Analysis, Test Data or any other domain, and it keeps no archive bytes (they are never stored).
/// </summary>
public sealed class DeliveryReportingDataReset(DeliveryReportingDbContext db, DeliveryImportStagingStore staging, ILogger<DeliveryReportingDataReset> logger)
{
    public async Task<(int Projects, int Snapshots, int Reports)> CountsAsync(CancellationToken ct) =>
        (await db.Projects.CountAsync(ct), await db.Snapshots.CountAsync(ct), await db.WeeklyReports.CountAsync(ct));

    /// <summary>Deletes all Delivery Management data; returns the deleted row count. Throws (and rolls back) on failure.</summary>
    public async Task<int> ResetAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var sql = db.GetService<ISqlGenerationHelper>();
        var deleted = 0;
        foreach (var table in ChildFirst(db))
            deleted += await db.Database.ExecuteSqlRawAsync($"DELETE FROM {sql.DelimitIdentifier(table.Name, table.Schema)}", ct);
        await transaction.CommitAsync(ct);
        staging.Clear();
        logger.LogWarning("Delivery Management reset: {Deleted} rows removed from the delivery_reporting schema", deleted);
        return deleted;
    }

    /// <summary>The schema's tables ordered so that every table is deleted before the tables it references (deterministic, never relying on cascades).</summary>
    public static IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.ITable> ChildFirst(DeliveryReportingDbContext context)
    {
        var tables = context.Model.GetRelationalModel().Tables.Where(t => t.Name != "__EFMigrationsHistory").OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var ordered = new List<Microsoft.EntityFrameworkCore.Metadata.ITable>();
        var remaining = tables.ToList();
        while (remaining.Count > 0)
        {
            // A table can go when no other remaining table references it.
            var next = remaining.First(t => !remaining.Any(other => other != t && other.ForeignKeyConstraints.Any(fk => fk.PrincipalTable == t)));
            ordered.Add(next);
            remaining.Remove(next);
        }
        return ordered;
    }
}
