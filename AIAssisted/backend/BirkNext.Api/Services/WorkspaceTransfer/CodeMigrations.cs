using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Data.IntegrationJourneys;
using BirkNext.Api.Data.ScopeRegistry;
using BirkNext.Api.Data.SnapshotRegistry;
using BirkNext.Api.Data.TaskAlignment;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>The migrations this build knows, keyed like the backup inventory ("{schema}.{history table}"). Reads the compiled model only (no database call).</summary>
public static class CodeMigrations
{
    /// <summary>Every persistence context of the API; PersistenceOwnershipTests checks this against the contexts the host registers.</summary>
    public static readonly IReadOnlyList<Type> Contexts =
    [
        typeof(AppDbContext), typeof(DeliveryReportingDbContext), typeof(CanonicalInterpretationDbContext), typeof(SnapshotRegistryDbContext),
        typeof(ScopeRegistryDbContext), typeof(TaskAlignmentDbContext), typeof(IntegrationJourneysDbContext),
    ];

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
            // Audit 2026-11-15 ARCH-01: these three schemas were dumped by the backup but missing here, so a restore could not compare their migrations.
            [$"{ScopeRegistryDbContext.Schema}.{ScopeRegistryDbContext.HistoryTable}"] = sp.GetRequiredService<ScopeRegistryDbContext>().Database.GetMigrations().ToList(),
            [$"{TaskAlignmentDbContext.Schema}.{TaskAlignmentDbContext.HistoryTable}"] = sp.GetRequiredService<TaskAlignmentDbContext>().Database.GetMigrations().ToList(),
            [$"{IntegrationJourneysDbContext.Schema}.{IntegrationJourneysDbContext.HistoryTable}"] = sp.GetRequiredService<IntegrationJourneysDbContext>().Database.GetMigrations().ToList(),
        };
    }
}
