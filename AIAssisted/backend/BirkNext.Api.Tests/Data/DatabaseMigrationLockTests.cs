using BirkNext.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Data;

/// <summary>
/// Concurrent first-time migrations of one fresh database — what the parallel test hosts do on every CI run, and what two API
/// instances do on a new environment. Without the lock they raced ("relation … already exists", duplicate history keys).
/// Uses the same PostgreSQL the API tests use (ConnectionStrings__Default, else the local development database).
/// </summary>
public sealed class DatabaseMigrationLockTests
{
    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=birknext";

    [Fact]
    public async Task ConcurrentMigrationsOfAFreshDatabase_AllSucceed_AndApplyEveryMigrationOnce()
    {
        var database = "birknext_migration_lock_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = "postgres", Pooling = false };
        var target = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = database, Pooling = false }.ConnectionString;
        await Execute(admin.ConnectionString, $"CREATE DATABASE \"{database}\"");
        try
        {
            var runs = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(target).Options);
                await DatabaseMigrationLock.MigrateAsync(db);
            }));
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromMinutes(5));

            await using var check = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(target).Options);
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
            Assert.Equal(check.Database.GetMigrations().Count(), (await check.Database.GetAppliedMigrationsAsync()).Count());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute(admin.ConnectionString, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
