using BirkNext.Api.Data;
using BirkNext.Api.Services;
using BirkNext.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// The application-database reset against a real (temporary, isolated) PostgreSQL database. Regression for the reported "Reset failed. See server
/// logs for details.": the running build's model had a table (integration_suites) that the database did not have, so the DELETE of every model
/// table failed with 42P01 half way. A schema mismatch is now refused before anything is deleted, with an actionable message; any database
/// failure rolls back completely and reports a reference instead of internal details. Never touches the person's real database.
/// </summary>
public sealed class AdminServiceResetDatabaseTests : IAsyncLifetime
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_reset");
    private string Target => TestPostgres.Temporary(_database).ConnectionString;
    private static readonly Guid SuiteRow = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid RunRow = Guid.Parse("66666666-7777-8888-9999-000000000000");

    public async Task InitializeAsync()
    {
        await Execute(TestPostgres.Admin().ConnectionString, $"CREATE DATABASE \"{_database}\"");
        await using var db = Db();
        await DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute(TestPostgres.Admin().ConnectionString, $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Target).Options);

    private AdminService Service(AppDbContext db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseSettings:Mode"] = "Local", ["ConnectionStrings:Default"] = Target, ["AdminSettings:AllowLocalDatabaseReset"] = "true",
        }).Build();
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        return new AdminService(config, env.Object, db, NullLogger<AdminService>.Instance);
    }

    private async Task SeedAsync(bool suites = true)
    {
        if (suites)
            await Execute(Target, $"""INSERT INTO integration_suites ("Id","SuiteKey","Revision","SnapshotId","ScopeId","GeneratedAt","Fingerprint","DefinitionJson") VALUES ('{SuiteRow}','synthetic:k',1,'{Guid.Empty}','api',now(),'f','[]')""");
        await Execute(Target, $"""INSERT INTO integration_suite_runs ("Id","SuiteKey","SuiteId","RunId","Kind","StartedAt","Initiator","TestIdsJson") VALUES ('{RunRow}','synthetic:k','{SuiteRow}','{Guid.Empty}','contract-tests',now(),'Tester','[]')""");
    }

    private async Task<long> CountAsync(string table) => (long)(await Scalar(Target, $"SELECT count(*) FROM {table}"))!;

    [Fact]
    public async Task A_complete_schema_resets_every_application_table()
    {
        await SeedAsync();
        await using var db = Db();
        var (success, message, deleted, at) = await Service(db).ResetLocalDatabaseAsync();
        success.Should().BeTrue(message);
        deleted.Should().BeGreaterThanOrEqualTo(2);
        at.Should().NotBeNull();
        (await CountAsync("integration_suites")).Should().Be(0);
        (await CountAsync("integration_suite_runs")).Should().Be(0);
        (await CountAsync("\"__EFMigrationsHistory\"")).Should().BeGreaterThan(0, "migration history is preserved");
    }

    [Fact]
    public async Task A_model_table_missing_in_the_database_is_refused_before_anything_is_deleted()
    {
        // The reported failure: the build's model is ahead of the database (42P01 relation "integration_suites" does not exist).
        await SeedAsync(suites: false);
        await Execute(Target, "DROP TABLE integration_suites");
        await using var db = Db();
        var (success, message, deleted, _) = await Service(db).ResetLocalDatabaseAsync();
        success.Should().BeFalse();
        message.Should().StartWith("Reset blocked: the database schema does not match this BirkNext build")
            .And.Contain("integration_suites").And.Contain("Nothing was deleted").And.Contain("Restart BirkNext");
        message.Should().NotContain("42P01").And.NotContain("Npgsql").And.NotContain("See server logs");
        deleted.Should().Be(0);
        (await CountAsync("integration_suite_runs")).Should().Be(1, "nothing is deleted when the reset is refused");
    }

    [Fact]
    public async Task A_pending_migration_is_refused_before_anything_is_deleted()
    {
        await SeedAsync();
        await Execute(Target, """DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = (SELECT max("MigrationId") FROM "__EFMigrationsHistory")""");
        await using var db = Db();
        var (success, message, _, _) = await Service(db).ResetLocalDatabaseAsync();
        success.Should().BeFalse();
        message.Should().Contain("pending migration").And.Contain("Nothing was deleted");
        (await CountAsync("integration_suites")).Should().Be(1);
    }

    [Fact]
    public async Task A_database_failure_mid_reset_rolls_back_everything_and_reports_a_reference_not_internals()
    {
        await SeedAsync();
        await Execute(Target, """
            CREATE FUNCTION refuse_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic failure'; END $$;
            CREATE TRIGGER refuse_delete BEFORE DELETE ON integration_suite_runs FOR EACH ROW EXECUTE FUNCTION refuse_delete();
            """);
        await using var db = Db();
        var (success, message, deleted, _) = await Service(db).ResetLocalDatabaseAsync();
        success.Should().BeFalse();
        message.Should().MatchRegex(@"^Reset failed: the database operation could not be completed\. Nothing was deleted \(the change was rolled back\)\. Reference: [0-9a-f]{8} \(server log\)\.$");
        message.Should().NotContain("synthetic failure").And.NotContain("P0001");
        deleted.Should().Be(0);
        (await CountAsync("integration_suites")).Should().Be(1, "tables deleted before the failure are restored by the rollback");
        (await CountAsync("integration_suite_runs")).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_resets_run_one_at_a_time()
    {
        await SeedAsync();
        await using var db1 = Db();
        await using var db2 = Db();
        var results = await Task.WhenAll(Service(db1).ResetLocalDatabaseAsync(), Service(db2).ResetLocalDatabaseAsync());
        results.Count(r => r.Success).Should().BeGreaterThanOrEqualTo(1);
        results.Where(r => !r.Success).Should().OnlyContain(r => r.Message == "A local database reset is already in progress.");
        (await CountAsync("integration_suites")).Should().Be(0);
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
