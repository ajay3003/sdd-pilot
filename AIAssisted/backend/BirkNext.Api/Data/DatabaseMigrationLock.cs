using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Data;

/// <summary>
/// Applies pending EF Core migrations one process at a time. EF Core 8 does not lock migrations, so two processes starting
/// against the same database — two API instances, or the parallel test hosts in CI, which always start from an empty
/// database — ran the same migration concurrently and failed with "relation … already exists", duplicate
/// __EFMigrationsHistory keys or a missing index. A PostgreSQL session-level advisory lock, taken on the connection that the
/// migrator itself uses, makes the others wait; when they get the lock there is nothing left to apply.
/// </summary>
public static class DatabaseMigrationLock
{
    /// <summary>Advisory-lock key reserved for BirkNext schema migrations ("BirkNext" as eight ASCII bytes).</summary>
    public const long LockKey = 0x4269726B4E657874;

    public static async Task MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsNpgsql())
        {
            await db.Database.MigrateAsync(ct);
            return;
        }

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", [LockKey], ct);
            try
            {
                await db.Database.MigrateAsync(ct);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", [LockKey], CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
