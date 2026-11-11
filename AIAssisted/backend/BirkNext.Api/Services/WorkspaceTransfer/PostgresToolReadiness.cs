using System.Globalization;
using System.Text.RegularExpressions;
using BirkNext.BuildInfo;
using BirkNext.WorkspaceTransfer;
using Npgsql;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>
/// The one readiness resolution for Backup &amp; Transfer, on top of <see cref="PostgresTools"/> (no second tool engine):
/// <list type="number">
/// <item>Configuration: the provider comes only from trusted configuration (<c>Backup:PostgresTools</c>), set by the launcher that owns the database
/// (Tester Package: its bundled <c>runtime/postgresql/bin</c>; developer launcher: the compose project's PostgreSQL container). PATH is the fallback
/// for an unconfigured Host. Nothing is taken from imported project data, and no container is chosen by name matching.</item>
/// <item>Execution: pg_dump and pg_restore must actually run (<c>--version</c>); a file that exists is not "available".</item>
/// <item>Identity (container only): the container's server must be the server BirkNext is connected to (same postmaster start time and database
/// OID) — a container that answers but is another server is Blocked.</item>
/// <item>Compatibility: pg_dump's major version must be at least the server's; pg_restore's at least pg_dump's.</item>
/// <item>Restore destination: the database role must be allowed to create a database (isolated restore always creates a new one).</item>
/// </list>
/// </summary>
public static partial class PostgresToolReadiness
{
    [GeneratedRegex(@"\(PostgreSQL\)\s+(?<major>\d+)(?:\.(?<minor>\d+))?")]
    private static partial Regex ToolVersion();

    public static int? Major(string? version) =>
        version is not null && ToolVersion().Match(version) is { Success: true } m ? int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture) : null;

    public static async Task<BackupReadiness> EvaluateAsync(PostgresTools tools, string connectionString, RuntimeProfile profile, TimeProvider clock, CancellationToken ct,
        string? restoreProvisioningConnectionString = null)
    {
        var options = tools.Options;
        var items = new List<BackupReadinessItem>();
        var provider = tools.ContainerMode ? "Container exec"
            : !string.IsNullOrWhiteSpace(options.BinDirectory) ? (profile == RuntimeProfile.TesterNative ? "Tester Package (bundled)" : "Configured directory")
            : "PATH";
        List<string> locations = tools.ContainerMode ? [$"{options.Runtime} container {options.Container}"]
            : !string.IsNullOrWhiteSpace(options.BinDirectory) ? [options.BinDirectory!] : ["PATH"];
        var result = new BackupReadiness
        {
            RuntimeProfile = RuntimeProfiles.DisplayName(profile), ToolProvider = provider, SearchLocations = locations, CheckedAt = clock.GetUtcNow(),
        };

        // 1. Database (the server the backup is taken from and restored into).
        int? serverMajor = null;
        string? serverVersion = null, fingerprint = null;
        bool? canCreateDatabase = null;
        var connection = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = 5 };
        try
        {
            await using var db = new NpgsqlConnection(connection.ConnectionString);
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(Fingerprint + ", current_setting('server_version_num'), current_setting('server_version'), " +
                "(SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user)", db);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            fingerprint = reader.GetString(0);
            serverMajor = int.Parse(reader.GetString(1), CultureInfo.InvariantCulture) / 10000;
            serverVersion = reader.GetString(2);
            canCreateDatabase = !reader.IsDBNull(3) && reader.GetBoolean(3);
            items.Add(new("database", "Database", BackupReadinessStatus.Ready, $"Connected · PostgreSQL {serverVersion}"));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            items.Add(new("database", "Database", BackupReadinessStatus.Unavailable, "BirkNext cannot reach its database, so nothing can be backed up or restored."));
        }

        // 2. Configuration of the tool provider.
        if (tools.ConfigurationProblem() is { } problem)
        {
            items.Add(new("tools", "Backup tools", BackupReadinessStatus.Blocked, problem));
            return Finish(result with { ServerVersion = serverVersion }, items);
        }

        // 3. The tools must actually run.
        var dump = await tools.VersionAsync("pg_dump", ct);
        var restore = await tools.VersionAsync("pg_restore", ct);
        result = result with { DumpToolVersion = dump, RestoreToolVersion = restore, ServerVersion = serverVersion };
        items.Add(dump is null
            ? new("pg_dump", "pg_dump", BackupReadinessStatus.Unavailable, $"pg_dump could not be started ({provider}: {string.Join(", ", locations)}).")
            : new("pg_dump", "pg_dump", BackupReadinessStatus.Ready, dump));
        items.Add(restore is null
            ? new("pg_restore", "pg_restore", BackupReadinessStatus.Unavailable, $"pg_restore could not be started ({provider}: {string.Join(", ", locations)}).")
            : new("pg_restore", "pg_restore", BackupReadinessStatus.Ready, restore));

        // 4. A container must be the database server BirkNext uses (never trusted by its name alone).
        if (tools.ContainerMode && dump is not null && fingerprint is not null)
        {
            var seen = await tools.QueryInContainerAsync(connection, Fingerprint, ct);
            items.Add(seen is null
                ? new("identity", "Container identity", BackupReadinessStatus.Unavailable, $"The container {options.Container} could not be queried, so it is not known to be BirkNext's database server.")
                : string.Equals(seen.Trim(), fingerprint, StringComparison.Ordinal)
                    ? new("identity", "Container identity", BackupReadinessStatus.Ready, $"{options.Container} runs the database server BirkNext is connected to.")
                    : new("identity", "Container identity", BackupReadinessStatus.Blocked, $"{options.Container} is a different PostgreSQL server than the one BirkNext is connected to; it is not used."));
        }

        // 5. Versions.
        var dumpMajor = Major(dump);
        var restoreMajor = Major(restore);
        if (dumpMajor is { } dm && serverMajor is { } sm)
            items.Add(dm >= sm
                ? new("compatibility", "Version compatibility", BackupReadinessStatus.Ready, $"pg_dump {dm} can dump PostgreSQL {sm}" + (restoreMajor is { } rm ? $"; pg_restore {rm}" : ""))
                : new("compatibility", "Version compatibility", BackupReadinessStatus.Incompatible, $"pg_dump {dm} is older than the PostgreSQL {sm} server; pg_dump {sm} or newer is required."));
        else
            items.Add(new("compatibility", "Version compatibility", BackupReadinessStatus.NotChecked, "Not checked: a tool or the database did not answer."));
        if (dumpMajor is { } d && restoreMajor is { } r && r < d)
            items.Add(new("restore-compatibility", "Restore compatibility", BackupReadinessStatus.Incompatible, $"pg_restore {r} is older than pg_dump {d}; it cannot read the backups it would be given."));

        // 6. Isolated restore creates a new database.
        //    The application role creates it when it may; otherwise only a verified, narrowly privileged provisioning role (RestoreProvisioning).
        if (canCreateDatabase is null)
            items.Add(new("restore-destination", "Restore destination", BackupReadinessStatus.NotChecked, "Not checked: the database did not answer."));
        else
        {
            RestoreProvisioning.Decision decision;
            try { decision = await RestoreProvisioning.EvaluateAsync(connectionString, restoreProvisioningConnectionString, ct); }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or InvalidOperationException)
            { decision = new(false, false, "Not checked: the database did not answer."); }
            items.Add(decision.Allowed
                ? new("restore-destination", "Restore destination", BackupReadinessStatus.Ready, decision.Detail)
                : new("restore-destination", "Restore destination", BackupReadinessStatus.Blocked, decision.Detail + " Backups still work."));
        }
        return Finish(result, items);
    }

    /// <summary>Is the backup compatible with the restore tool? (pg_restore cannot read archives written by a newer pg_dump.)</summary>
    public static string? RestoreToolProblem(string? restoreToolVersion, string? backupDumpToolVersion) =>
        Major(restoreToolVersion) is { } r && Major(backupDumpToolVersion) is { } d && r < d
            ? $"pg_restore {r} cannot read this backup: it was written by pg_dump {d}. Use pg_restore {d} or newer."
            : null;

    /// <summary>Identifies a running server and database without privileges: postmaster start time + database OID.</summary>
    private const string Fingerprint = "SELECT pg_postmaster_start_time()::text || '/' || (SELECT oid FROM pg_database WHERE datname = current_database())::text";

    private static readonly string[] BackupKeys = ["database", "tools", "pg_dump", "identity", "compatibility"];
    private static readonly string[] RestoreKeys = ["database", "tools", "pg_restore", "identity", "restore-compatibility", "restore-destination"];

    private static BackupReadiness Finish(BackupReadiness result, List<BackupReadinessItem> items)
    {
        (BackupReadinessStatus, string?) Overall(string[] keys)
        {
            var relevant = items.Where(i => keys.Contains(i.Key)).ToList();
            var worst = relevant.FirstOrDefault(i => i.Status is BackupReadinessStatus.Blocked or BackupReadinessStatus.Unavailable or BackupReadinessStatus.Incompatible)
                ?? relevant.FirstOrDefault(i => i.Status == BackupReadinessStatus.NotChecked);
            return worst is null ? (BackupReadinessStatus.Ready, null) : (worst.Status, worst.Detail);
        }
        var (backup, backupBlocker) = Overall(BackupKeys);
        var (restore, restoreBlocker) = Overall(RestoreKeys);
        return result with { Items = items, Backup = backup, BackupBlocker = backupBlocker, Restore = restore, RestoreBlocker = restoreBlocker };
    }
}
