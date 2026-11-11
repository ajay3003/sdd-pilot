using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Data.SnapshotRegistry;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>The migrations this build knows, keyed like the backup inventory ("{schema}.{history table}"). Reads the compiled model only (no database call).</summary>
public static class CodeMigrations
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["public.__EFMigrationsHistory"] = sp.GetRequiredService<AppDbContext>().Database.GetMigrations().ToList(),
            [$"{DeliveryReportingDbContext.Schema}.{DeliveryReportingDbContext.HistoryTable}"] = sp.GetRequiredService<DeliveryReportingDbContext>().Database.GetMigrations().ToList(),
            [$"{CanonicalInterpretationDbContext.Schema}.{CanonicalInterpretationDbContext.HistoryTable}"] = sp.GetRequiredService<CanonicalInterpretationDbContext>().Database.GetMigrations().ToList(),
            [$"{SnapshotRegistryDbContext.Schema}.{SnapshotRegistryDbContext.HistoryTable}"] = sp.GetRequiredService<SnapshotRegistryDbContext>().Database.GetMigrations().ToList(),
        };
    }
}
