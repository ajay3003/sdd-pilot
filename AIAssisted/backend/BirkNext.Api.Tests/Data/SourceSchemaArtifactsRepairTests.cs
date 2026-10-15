using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Tests.ProjectImport;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Data;

/// <summary>
/// Regression for "Source detected, but no snapshot was created — the source snapshot could not be saved": a database that applied
/// <c>AddSourceSchemaArtifacts</c> before the migration gained <c>project_import_id</c> records the migration as applied but lacks the column,
/// so every snapshot save failed and the cause was swallowed. The failure must now name its cause, and the repair migration must fix the schema.
/// Uses a temporary database on the PostgreSQL the API tests use (ConnectionStrings__Default, else the local development server).
/// </summary>
public sealed class SourceSchemaArtifactsRepairTests
{
    private const string EarlyArtifactsMigration = "20261014082517_AddSourceSchemaArtifacts";

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=birknext";

    private static byte[] Archive() => ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Source,
        ("shop/contracts/order.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="Order" type="xs:string"/></xs:schema>""")]);

    private static async Task<IqrSourceSnapshot> Analyze(AppDbContext db)
    {
        var bytes = Archive();
        var workspace = IqrSourceArchiveReader.ReadDetailed("shop.zip", bytes, default).Workspace!;
        return await new IqrSourceStore(db).AnalyzeValidatedAsync(null, IqrSourceStore.SourceAnalysisOwner, "shop.zip", bytes, workspace, default);
    }

    [Fact]
    public async Task EarlyArtifactsSchema_FailsWithAnExplicitSchemaReason_AndTheRepairMigrationRestoresSnapshotSaves()
    {
        await WithDatabase(async target =>
        {
            await using (var early = Context(target))
            {
                await early.GetService<IMigrator>().MigrateAsync(EarlyArtifactsMigration);
                // The exact shape found on a developer database: the table without project_import_id, the migration recorded as applied.
                await early.Database.ExecuteSqlRawAsync("ALTER TABLE source_schema_artifacts DROP COLUMN project_import_id");

                var failure = await Assert.ThrowsAsync<SourceSnapshotPersistenceException>(() => Analyze(early));
                Assert.Equal(PostgresErrorCodes.UndefinedColumn, failure.SqlState);
                Assert.Equal("project_import_id", failure.Column);
                Assert.Contains("database schema is out of date", failure.Reason);
                Assert.Equal(0, await early.IqrSourceSnapshots.CountAsync());
            }

            await using var repaired = Context(target);
            await DatabaseMigrationLock.MigrateAsync(repaired);
            Assert.Empty(await repaired.Database.GetPendingMigrationsAsync());
            var snapshot = await Analyze(repaired);
            Assert.NotNull(await repaired.SourceSchemaArtifacts.FindAsync(snapshot.Id));
            Assert.Equal(1, await repaired.IqrSourceSnapshots.CountAsync());
        });
    }

    [Fact]
    public async Task RepairMigration_IsANoOp_OnACorrectlyCreatedDatabase()
    {
        await WithDatabase(async target =>
        {
            await using var db = Context(target);
            await DatabaseMigrationLock.MigrateAsync(db);
            await using var connection = new NpgsqlConnection(target);
            await connection.OpenAsync();
            await using var count = new NpgsqlCommand(
                "select count(*) from information_schema.columns where table_name = 'source_schema_artifacts' and column_name = 'project_import_id'", connection);
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
            var snapshot = await Analyze(db);
            Assert.NotNull(await db.SourceSchemaArtifacts.FindAsync(snapshot.Id));
        });
    }

    private static AppDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

    private static async Task WithDatabase(Func<string, Task> body)
    {
        var database = "birknext_artifact_repair_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var target = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = database, Pooling = false }.ConnectionString;
        await Execute(admin, $"CREATE DATABASE \"{database}\"");
        try { await body(target); }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
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
