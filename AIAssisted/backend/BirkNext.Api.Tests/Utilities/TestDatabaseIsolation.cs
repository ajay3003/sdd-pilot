using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BirkNext.Api.Configuration;
using BirkNext.Api.Tests.TestInfrastructure;
using Npgsql;

namespace BirkNext.Api.Tests.Utilities;

/// <summary>
/// Keeps every test host away from the person's real BirkNext database (2026-11-25). Before, a WebApplicationFactory host inherited
/// <c>appsettings.json</c>'s <c>Database=birknext</c>: it migrated, read and wrote the developer's own database (plans, imports, settings).
/// Now, before any host starts:
/// <list type="bullet">
/// <item>This test run gets its own database <c>birknext_testrun_&lt;12 hex&gt;</c> on the same server. A comment marks it as this run's
/// (<c>birknext-test-run pid=… started=…</c>).</item>
/// <item><c>ConnectionStrings__Default</c> names that database. Environment variables override <c>appsettings.json</c>, so every host and every
/// service that reads the configuration directly (Admin, configuration health) sees it.</item>
/// <item><see cref="DatabaseConnection.TestDatabaseGuard"/> makes a host refuse anything else that is not a guarded temporary database. The run
/// fails closed: when the run database cannot be created, hosts get a database that does not exist and fail. They never fall back to the real
/// one.</item>
/// </list>
/// The run database is dropped at exit. Run databases left by a killed run (marker comment, owning process gone, no connections) are dropped by the
/// next run. Nothing without the marker is ever dropped.
/// </summary>
internal static partial class TestDatabaseIsolation
{
    public const string Marker = "birknext-test-run";

    /// <summary>The database named by the configuration before this run (the person's real one, e.g. <c>birknext</c>).</summary>
    public static string? UserDatabase { get; private set; }

    /// <summary>This run's own database.</summary>
    public static string RunDatabase { get; private set; } = string.Empty;

    /// <summary>Null when the run database exists; otherwise why it could not be created (hosts then fail closed).</summary>
    public static string? Unavailable { get; private set; }

    /// <summary>The connection every host of this run gets (the run database).</summary>
    public static string ConfiguredConnection => Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? string.Empty;

    /// <summary>Stale run databases dropped at startup (marker, owner gone, no connections).</summary>
    public static IReadOnlyList<string> DroppedStale { get; private set; } = [];

    [ModuleInitializer]
    internal static void UseThisRunsOwnDatabase()
    {
        var configured = new NpgsqlConnectionStringBuilder(TestPostgres.Admin().ConnectionString);
        UserDatabase = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? TestPostgres.DefaultConnection).Database;
        RunDatabase = TestPostgres.TemporaryName("birknext_testrun");
        try
        {
            using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(configured.ConnectionString) { Timeout = 5, CommandTimeout = 30 }.ConnectionString);
            admin.Open();
            DroppedStale = DropStale(admin);
            Execute(admin, $"CREATE DATABASE \"{RunDatabase}\"");
            Execute(admin, $"COMMENT ON DATABASE \"{RunDatabase}\" IS '{Marker} pid={Environment.ProcessId} started={DateTimeOffset.UtcNow:O}'");
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            Unavailable = $"The test database could not be created ({e.GetType().Name}: {e.Message}). Tests that need PostgreSQL fail; the real database is never used instead.";
        }

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", new NpgsqlConnectionStringBuilder(configured.ConnectionString) { Database = RunDatabase, Pooling = true }.ConnectionString);
        DatabaseConnection.TestDatabaseGuard = Refusal;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DropRunDatabase();
    }

    /// <summary>The guard: only this run's database or a guarded temporary one (<see cref="TestPostgres.IsTemporaryName"/>); never the real one.</summary>
    internal static string? Refusal(string connectionString)
    {
        string? database;
        try { database = new NpgsqlConnectionStringBuilder(connectionString).Database; }
        catch (ArgumentException) { return "A test host was given an unreadable connection string."; }
        if (string.Equals(database, RunDatabase, StringComparison.Ordinal)) return null;
        if (database is not null && !string.Equals(database, UserDatabase, StringComparison.OrdinalIgnoreCase) && TestPostgres.IsTemporaryName(database)) return null;
        return $"A test host may not open the database \"{database}\": tests use only their own run database ({RunDatabase}) or a temporary one.";
    }

    private static IReadOnlyList<string> DropStale(NpgsqlConnection admin)
    {
        var candidates = new List<(string Name, string Comment)>();
        using (var cmd = new NpgsqlCommand("SELECT datname, coalesce(shobj_description(oid, 'pg_database'), '') FROM pg_database WHERE datname LIKE 'birknext\\_testrun\\_%'", admin))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) candidates.Add((r.GetString(0), r.GetString(1)));
        var dropped = new List<string>();
        foreach (var (name, comment) in candidates)
        {
            if (!RunName().IsMatch(name) || OwnerPid().Match(comment) is not { Success: true } m || !comment.StartsWith(Marker, StringComparison.Ordinal)) continue;
            if (IsAlive(int.Parse(m.Groups["pid"].Value))) continue; // conservative: a live process (even a reused pid) keeps its database
            using var busy = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @n", admin);
            busy.Parameters.AddWithValue("n", name);
            if ((long)busy.ExecuteScalar()! > 0) continue;
            try { Execute(admin, $"DROP DATABASE \"{name}\""); dropped.Add(name); }
            catch (PostgresException) { /* someone connected meanwhile: keep it */ }
        }
        return dropped;
    }

    private static void DropRunDatabase()
    {
        if (Unavailable is not null || !RunName().IsMatch(RunDatabase)) return;
        try
        {
            NpgsqlConnection.ClearAllPools();
            using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(TestPostgres.Admin().ConnectionString) { Timeout = 5, CommandTimeout = 15 }.ConnectionString);
            admin.Open();
            Execute(admin, $"DROP DATABASE IF EXISTS \"{RunDatabase}\"");
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // Still connected (a host not disposed): the next run drops it (marker + this process gone).
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    [GeneratedRegex(@"^birknext_testrun_[0-9a-f]{12}$")]
    internal static partial Regex RunName();

    [GeneratedRegex(@"\bpid=(?<pid>\d+)\b")]
    private static partial Regex OwnerPid();
}
