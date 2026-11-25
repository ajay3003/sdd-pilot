using System.Security.Cryptography;
using BirkNext.Api.Configuration;
using BirkNext.Api.Data;
using BirkNext.Api.Services;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Infrastructure;

/// <summary>
/// Test runs never touch the person's BirkNext (2026-11-25): their own database (<see cref="TestDatabaseIsolation"/>) and their own App_Data
/// (<see cref="TestAppDataIsolation"/>). User-data fingerprints before and after a full run are compared outside the suite (no test opens the real
/// database, not even to read it).
/// </summary>
public sealed class TestIsolationTests
{
    private static string Db(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString).Database!;

    [Fact]
    public void I01_the_run_database_is_not_the_users_database()
    {
        TestDatabaseIsolation.UserDatabase.Should().NotBeNullOrEmpty();
        TestDatabaseIsolation.RunDatabase.Should().MatchRegex("^birknext_testrun_[0-9a-f]{12}$").And.NotBe(TestDatabaseIsolation.UserDatabase);
        Db(TestDatabaseIsolation.ConfiguredConnection).Should().Be(TestDatabaseIsolation.RunDatabase,
            "the environment overrides appsettings.json for every host and every service reading the configuration");
    }

    [Fact]
    public void I02_a_missing_run_database_fails_closed_it_never_falls_back_to_the_real_one()
    {
        // Whether or not the run database could be created, the configuration names it; a host then fails to connect instead of using birknext.
        Db(TestDatabaseIsolation.ConfiguredConnection).Should().Be(TestDatabaseIsolation.RunDatabase);
        TestDatabaseIsolation.Refusal(NamedDb(TestDatabaseIsolation.UserDatabase!)).Should().Contain("may not open");
        TestDatabaseIsolation.Refusal(NamedDb("birknext")).Should().NotBeNull();
        TestDatabaseIsolation.Refusal(NamedDb("postgres")).Should().NotBeNull();
        TestDatabaseIsolation.Refusal(NamedDb(TestDatabaseIsolation.RunDatabase)).Should().BeNull();
        TestDatabaseIsolation.Refusal(NamedDb(TestPostgres.TemporaryName("birknext_guard"))).Should().BeNull("a guarded temporary database of a test");
    }

    [Fact]
    public void I03_a_host_given_the_real_database_refuses_to_start_so_nothing_can_reset_it()
    {
        // Program.cs calls this right after resolving the connection, before any DbContext (migration, Admin reset, Local Data Reset) exists.
        FluentActions.Invoking(() => DatabaseConnection.EnsureAllowed(NamedDb(TestDatabaseIsolation.UserDatabase!)))
            .Should().Throw<InvalidOperationException>().Where(e => e.Message.Contains("may not open"));
        FluentActions.Invoking(() => DatabaseConnection.EnsureAllowed(TestDatabaseIsolation.ConfiguredConnection)).Should().NotThrow();
        var program = File.ReadAllText(Path.Combine(TestAppDataIsolation.ApiProjectFolder!, "Program.cs"));
        var resolved = program.IndexOf("var databaseConnectionString = DatabaseConnection.GetConnectionString(", StringComparison.Ordinal);
        var guarded = program.IndexOf("DatabaseConnection.EnsureAllowed(databaseConnectionString);", StringComparison.Ordinal);
        var firstContext = program.IndexOf("AddDbContext<AppDbContext>", StringComparison.Ordinal);
        (resolved >= 0 && guarded > resolved && guarded < firstContext).Should().BeTrue("the guard runs before any context is registered");
    }

    /// <summary>
    /// Why the environment variable (not an in-memory override) isolates hosts: Program.cs reads the connection before WithWebHostBuilder's
    /// configuration is applied, so an in-memory ConnectionStrings:Default never reached the contexts. Every host was on birknext before 2026-11-25.
    /// </summary>
    [Fact]
    public void I03b_an_in_memory_override_does_not_reach_the_contexts_the_run_database_does()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = NamedDb("birknext") })));
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetDbConnection().Database.Should().Be(TestDatabaseIsolation.RunDatabase);
    }

    [Fact]
    public async Task I04_a_host_migrates_and_uses_only_the_run_database()
    {
        TestDatabaseIsolation.Unavailable.Should().BeNull("PostgreSQL is available on this machine");
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.GetDbConnection().Database.Should().Be(TestDatabaseIsolation.RunDatabase);
        (await db.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty("startup migrated the run database");
    }

    [Fact]
    public void I05_two_hosts_both_stay_on_the_run_database_and_off_the_real_App_Data()
    {
        using var a = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var b = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        foreach (var f in new[] { a, b })
        {
            using var scope = f.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetDbConnection().Database.Should().Be(TestDatabaseIsolation.RunDatabase);
            Path.GetFullPath(AppDataLocation.Root(f.Services.GetRequiredService<IWebHostEnvironment>())).Should().NotStartWith(RealAppData);
        }
    }

    [Theory]
    [InlineData("birknext", false)]
    [InlineData("birknext_xfersrc_0d62b061a10d", false)] // another test's temporary database: not a run database, never dropped by the run cleanup
    [InlineData("birknext_testrun", false)]
    [InlineData("birknext_testrun_0123456789ab", true)]
    [InlineData("birknext_testrun_0123456789ab; DROP", false)]
    public void I08_stale_run_cleanup_only_considers_marked_run_databases(string name, bool candidate) =>
        TestDatabaseIsolation.RunName().IsMatch(name).Should().Be(candidate);

    [Fact]
    public void I09_test_App_Data_is_not_the_running_applications()
    {
        TestAppDataIsolation.Redirected.Should().NotBeNull();
        Path.GetFullPath(TestAppDataIsolation.Redirected!).Should().NotStartWith(RealAppData);
        Path.GetFullPath(TestAppDataIsolation.Redirected!).Should().Contain(Environment.ProcessId.ToString(), "each test process has its own folder");
    }

    [Fact]
    public void I10_starting_and_stopping_hosts_leaves_the_real_login_code_and_App_Data_files_unchanged()
    {
        var before = Fingerprint();
        using (var f = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled())
        {
            var session = f.Services.GetRequiredService<BirkNext.Api.Services.LocalSecurity.LocalSessionService>();
            Path.GetFullPath(session.CodePath).Should().NotStartWith(RealAppData);
        }
        Fingerprint().Should().Equal(before, "a host neither writes, rotates nor removes the developer's App_Data (launch code included)");
    }

    private static string RealAppData => Path.GetFullPath(Path.Combine(TestAppDataIsolation.ApiProjectFolder!, "App_Data"));

    /// <summary>Path and hash of every file of the real local session (the launch code); contents are never printed. Only this folder: a running
    /// BirkNext may legitimately write its other App_Data files meanwhile, a test host must never write any of them.</summary>
    private static List<string> Fingerprint()
    {
        var session = Path.Combine(RealAppData, "local-session");
        if (!Directory.Exists(session)) return [];
        return [.. Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => { try { return $"{f}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"; } catch (IOException) { return $"{f}|locked"; } })];
    }

    private static string NamedDb(string database) => new NpgsqlConnectionStringBuilder(TestDatabaseIsolation.ConfiguredConnection) { Database = database }.ConnectionString;
}

/// <summary>App_Data isolation does not depend on where the tests were built (2026-11-25): the anchor is WebApplicationFactory's own content root.</summary>
public sealed class TestAppDataAnchorTests
{
    [Xunit.Fact]
    public void The_api_folder_is_found_from_the_factorys_content_root_wherever_the_tests_were_built()
    {
        TestAppDataIsolation.ApiProjectFolder.Should().NotBeNull("without it, hosts would write the developer's real App_Data");
        File.Exists(Path.Combine(TestAppDataIsolation.ApiProjectFolder!, "BirkNext.Api.csproj")).Should().BeTrue();
        BirkNext.Api.Services.AppDataLocation.Root(TestAppDataIsolation.ApiProjectFolder!).Should().StartWith(TestAppDataIsolation.Redirected!);
    }
}
