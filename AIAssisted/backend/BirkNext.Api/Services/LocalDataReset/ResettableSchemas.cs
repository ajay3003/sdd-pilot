using BirkNext.Api.Data.TaskAlignment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BirkNext.Api.Services.LocalDataReset;

/// <summary>
/// One application data schema with its own DbContext (and its own migrations history table) on the LOCAL database. A full local data reset
/// deletes every row of every table in the context's model — the migrations history table is never part of a model, so it is never touched.
/// </summary>
/// <param name="Domain">Domain name in the reset result (<see cref="BirkNext.Maintenance.DataResetDomainResult"/>) and the reset plan.</param>
/// <param name="Schema">The only schema the reset may delete from; a model table in any other schema refuses the whole step.</param>
/// <param name="ContextType">The registered DbContext type, resolved from a fresh DI scope.</param>
/// <param name="Description">The line shown in the plan's / result's "removed" list.</param>
/// <param name="OnlyWhenResettingEverything">True for user-authored, reviewed data (shared test areas): cleared only by the explicitly typed
/// "Reset everything" scope, kept by "Reset everything except protected data".</param>
public sealed record ResettableSchema(string Domain, string Schema, Type ContextType, string Description, bool OnlyWhenResettingEverything = false);

/// <summary>
/// The registry of separate-schema stores a full local data reset clears (after the application database transaction and the epoch advance,
/// like Delivery Management, Canonical interpretation and the Snapshot Registry, which keep their own dedicated reset steps).
/// To make a new schema resettable, add ONE line here, e.g.
/// <c>new("Integration Journeys", IntegrationJourneysDbContext.Schema, typeof(IntegrationJourneysDbContext), "Declared Integration Journeys (integration_journeys schema)"),</c>
/// — the plan, the result's cleared/domain lists and the deletion follow from it.
/// </summary>
public static class ResettableSchemas
{
    public static readonly IReadOnlyList<ResettableSchema> All =
    [
        new("Task Alignment", TaskAlignmentDbContext.Schema, typeof(TaskAlignmentDbContext), "Task Alignment run history (persisted Task Alignment results; task_alignment schema)"),
        new("Integration Journeys", BirkNext.Api.Data.IntegrationJourneys.IntegrationJourneysDbContext.Schema, typeof(BirkNext.Api.Data.IntegrationJourneys.IntegrationJourneysDbContext),
            "Declared Integration Journeys and their revisions (integration_journeys schema)"),
        // Shared test areas hold reviewed, possibly approved test boundaries: an ordinary reset keeps them; only "Reset everything" removes them.
        new("Shared test areas", BirkNext.Api.Data.ScopeRegistry.ScopeRegistryDbContext.Schema, typeof(BirkNext.Api.Data.ScopeRegistry.ScopeRegistryDbContext),
            "Shared test areas (scopes) with all revisions, approvals and review history (scope_registry schema)", OnlyWhenResettingEverything: true),
    ];

    /// <summary>The stores a reset clears: everything in the registry for "Reset everything", otherwise only the stores without the flag.</summary>
    public static IEnumerable<ResettableSchema> For(bool includeProtected) => All.Where(s => includeProtected || !s.OnlyWhenResettingEverything);

    /// <summary>
    /// Deletes every row of every table in <paramref name="db"/>'s model, dependents first, in one transaction (relational) or one SaveChanges
    /// (in-memory tests). Refuses — deleting nothing — when a model table lies outside <paramref name="schema"/>. Returns the rows removed.
    /// </summary>
    public static async Task<int> DeleteAllRowsAsync(DbContext db, string schema, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;
            var removed = 0;
            foreach (var entity in db.Model.GetEntityTypes().Where(e => !e.IsOwned()))
            {
                var rows = await ((IQueryable<object>)set.MakeGenericMethod(entity.ClrType).Invoke(db, null)!).ToListAsync(ct);
                db.RemoveRange(rows);
                removed += rows.Count;
            }
            await db.SaveChangesAsync(ct);
            return removed;
        }

        var tables = db.Model.GetRelationalModel().Tables.ToList();
        if (tables.FirstOrDefault(t => !string.Equals(t.Schema, schema, StringComparison.Ordinal)) is { } foreign)
            throw new InvalidOperationException($"Refusing to reset: table {foreign.Schema}.{foreign.Name} is outside the {schema} schema.");
        var sql = db.GetService<ISqlGenerationHelper>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = 0;
        var remaining = tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.First(t => !remaining.Any(other => other != t && other.ForeignKeyConstraints.Any(fk => fk.PrincipalTable == t)));
            deleted += await db.Database.ExecuteSqlRawAsync($"DELETE FROM {sql.DelimitIdentifier(next.Name, next.Schema)}", ct);
            remaining.Remove(next);
        }
        await transaction.CommitAsync(ct);
        return deleted;
    }
}
