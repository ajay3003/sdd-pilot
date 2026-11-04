using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Interpretation;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>
/// The one place Topology and Integration Quality Review read shared aliases: explicit and human-confirmed aliases of systems and services in the
/// project scope. A consumer never adds its own alias store. When the canonical store is unavailable (no relational database, e.g. a test host,
/// or the schema not migrated yet) consumers keep their existing declared aliases and nothing is invented.
/// </summary>
public sealed class CanonicalAliasSource(CanonicalInterpretationDbContext canonical, AppDbContext app, CanonicalInterpretationService service,
    ILogger<CanonicalAliasSource>? logger = null)
{
    public async Task<IReadOnlyList<(string Canonical, string Alias, AliasState State)>> AliasesAsync(string scope, CancellationToken ct)
    {
        // A test host swaps the application database for an in-memory one but leaves this context on PostgreSQL: there is no database to read.
        if (canonical.Database.IsRelational() ? !app.Database.IsRelational() : !IsInMemory(canonical)) return [];
        try { return await service.ApplicableAliasesAsync(scope, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Canonical aliases unavailable for {Scope}; consumers use their declared aliases only", scope);
            return [];
        }
    }

    static bool IsInMemory(DbContext db) => db.Database.ProviderName?.Contains("InMemory", StringComparison.Ordinal) == true;
}
