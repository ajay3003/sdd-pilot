using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Persistence;

/// <summary>
/// Serialises one check-then-write per logical key (for example "freeze a release report for this scope" or "promote a baseline for this
/// definition"). An in-process gate always applies; on PostgreSQL the work also runs in a transaction holding a transaction-scoped advisory lock
/// on the key, so two API processes on the same database cannot interleave the check and the insert either. Readers are never blocked.
/// </summary>
public static class ScopedWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static async Task<T> RunAsync<T>(DbContext db, string key, Func<Task<T>> work, CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!db.Database.IsRelational()) return await work();
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockId(key)})", ct);
                var result = await work();
                await transaction.CommitAsync(ct);
                return result;
            });
        }
        finally { gate.Release(); }
    }

    /// <summary>A stable 64-bit advisory lock id for the key (the same key always maps to the same lock in every process).</summary>
    public static long LockId(string key) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("birknext|" + key)), 0);
}
