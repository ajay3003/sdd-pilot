using System.Diagnostics;
using System.Text;
using Npgsql;

namespace BirkNext.Launcher;

/// <summary>What BirkNext knows about its own cluster (config/cluster.json). No secret is stored here.</summary>
public sealed record ClusterRecord
{
    public string SystemIdentifier { get; init; } = "";
    public int Major { get; init; }
    public string Version { get; init; } = "";
    public string DataDirectory { get; init; } = "";
    public int Port { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedByBuild { get; init; } = "";
}

public enum ClusterState { NotInitialized, Initialized, InterruptedInitialization, UnknownFiles, MissingFiles, IncompatibleVersion }

/// <summary>
/// The TesterNative PostgreSQL lifecycle: one cluster per Windows user in the data folder, created once with the bundled initdb (SCRAM
/// authentication, a non-superuser application role, loopback-only listening), started and stopped with the bundled pg_ctl as this user's
/// process (no Windows service, no elevation). It never runs initdb over existing files, never deletes database files, never stops a server
/// whose identity it has not verified, and leaves crash recovery to PostgreSQL itself.
/// </summary>
public sealed class PostgresManager(PackageLayout package, DataLayout data, LauncherLog log)
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(180);

    // ── State ────────────────────────────────────────────────────────────────────────────────────────────────

    public ClusterRecord? Record => StateFile.Read<ClusterRecord>(data.ClusterFile);

    public ClusterState State(out string detail)
    {
        var record = Record;
        var versionFile = Path.Combine(data.Database, "PG_VERSION");
        var hasFiles = Directory.Exists(data.Database) && Directory.EnumerateFileSystemEntries(data.Database).Any();
        if (record is null)
        {
            if (!hasFiles) { detail = "No local database yet."; return ClusterState.NotInitialized; }
            if (File.Exists(data.InitializationFile)) { detail = "A previous first-run setup was interrupted before it finished."; return ClusterState.InterruptedInitialization; }
            detail = "Database files exist that were not created by this BirkNext launcher."; return ClusterState.UnknownFiles;
        }
        if (!File.Exists(versionFile)) { detail = "BirkNext's database files are missing from the data folder."; return ClusterState.MissingFiles; }
        var major = int.TryParse(File.ReadAllText(versionFile).Trim(), out var m) ? m : 0;
        if (major != package.Manifest.PostgreSql.Major)
        {
            detail = major > package.Manifest.PostgreSql.Major
                ? $"The database was created by PostgreSQL {major}, newer than the bundled PostgreSQL {package.Manifest.PostgreSql.Major}."
                : $"The database was created by PostgreSQL {major} and needs a PostgreSQL upgrade before the bundled PostgreSQL {package.Manifest.PostgreSql.Major} can open it.";
            return ClusterState.IncompatibleVersion;
        }
        detail = $"PostgreSQL {major} database created {record.CreatedAt:yyyy-MM-dd}.";
        return ClusterState.Initialized;
    }

    // ── Process helpers ────────────────────────────────────────────────────────────────────────────────────

    public sealed record ToolResult(int ExitCode, string Output);

    /// <summary>Runs a bundled tool to completion with captured output (never for pg_ctl start: the server would inherit the pipes).</summary>
    internal ToolResult Run(string tool, IEnumerable<string> args, TimeSpan timeout, IDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(package.Tool(tool)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment.Remove("PGPASSWORD");
        if (env is not null) foreach (var (k, v) in env) info.Environment[k] = v;
        using var p = new Process { StartInfo = info };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new LauncherException("Unable to start the local database tools.", "Windows or an application-control policy blocked a bundled program. Ask IT, and send the diagnostics.",
                $"{tool}: {e.Message}");
        }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ToolResult(-1, output + $"[{tool} did not finish within {timeout.TotalSeconds:0} s]");
        }
        p.WaitForExit();
        lock (output) return new ToolResult(p.ExitCode, output.ToString());
    }

    // ── First run ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the cluster, starts it, creates the application role and database, and records the cluster identity — in that order, with an
    /// "initializing" marker so an interruption is recognised next time. Files from an interrupted first run are moved aside, never deleted.
    /// </summary>
    public (ClusterRecord Record, DatabaseCredentials Credentials) Initialize(int port, string build, Action<string> step)
    {
        var state = State(out var detail);
        if (state == ClusterState.InterruptedInitialization)
        {
            var aside = data.Database + ".incomplete-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            Directory.Move(data.Database, aside);
            log.Warn($"Interrupted first-run database moved aside (kept, not deleted): {aside}");
        }
        else if (state != ClusterState.NotInitialized)
            throw new InvalidOperationException($"Initialize called in state {state}: {detail}");

        StateFile.Write(data.InitializationFile, new { state = "Initializing", startedAt = DateTimeOffset.UtcNow, build });
        var credentials = DatabaseCredentials.Load(data.SecretsFile) ?? DatabaseCredentials.New();
        credentials.Save(data.SecretsFile);

        step("Preparing local database");
        var pwfile = Path.Combine(data.Temporary, "initdb-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(pwfile, credentials.AdminPassword);
        ToolResult init;
        try
        {
            init = Run("initdb", ["-D", data.Database, "-U", credentials.AdminUser, "--pwfile=" + pwfile, "--auth=scram-sha-256", "--encoding=UTF8",
                "--locale-provider=icu", "--icu-locale=en-US", "--locale=C", "--no-instructions"], TimeSpan.FromMinutes(5));
        }
        finally { File.Delete(pwfile); }
        if (init.ExitCode != 0)
            throw new LauncherException("Unable to create the local database.", "Check free disk space and that antivirus allows BirkNext, then start BirkNext again. Nothing was deleted.", init.Output);
        log.Info("initdb completed.");
        WriteConfiguration(port);

        Start(port, step);
        step("Creating BirkNext database");
        using (var admin = Open(port, credentials.AdminUser, credentials.AdminPassword, "postgres"))
        {
            Execute(admin, $"""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{credentials.AppUser}') THEN
                    CREATE ROLE {credentials.AppUser} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '{credentials.AppPassword}';
                  END IF;
                END $$;
                """);
            if (Scalar(admin, $"SELECT count(*) FROM pg_database WHERE datname = '{credentials.Database}'") is 0L)
                Execute(admin, $"CREATE DATABASE {credentials.Database} OWNER {credentials.AppUser} ENCODING 'UTF8' TEMPLATE template0");
            Execute(admin, "REVOKE CONNECT ON DATABASE postgres FROM PUBLIC");
        }
        using (var adminDb = Open(port, credentials.AdminUser, credentials.AdminPassword, credentials.Database))
            Execute(adminDb, $"ALTER SCHEMA public OWNER TO {credentials.AppUser}");
        var record = new ClusterRecord
        {
            SystemIdentifier = SystemIdentifier(port, credentials), Major = package.Manifest.PostgreSql.Major, Version = package.Manifest.PostgreSql.Version,
            DataDirectory = data.Database, Port = port, CreatedAt = DateTimeOffset.UtcNow, CreatedByBuild = build,
        };
        StateFile.Write(data.ClusterFile, record);
        File.Delete(data.InitializationFile);
        log.Info($"Local database created (system identifier {record.SystemIdentifier}, port {port}).");
        return (record, credentials);
    }

    /// <summary>BirkNext's settings in an included file (loopback only, SCRAM, bounded logs) and a pg_hba.conf allowing only the two BirkNext roles from 127.0.0.1.</summary>
    internal void WriteConfiguration(int port)
    {
        File.WriteAllText(Path.Combine(data.Database, "birknext.conf"), Configuration(port, Path.Combine(data.Logs, "postgresql")));
        var conf = Path.Combine(data.Database, "postgresql.conf");
        if (!File.ReadAllText(conf).Contains("include_if_exists = 'birknext.conf'", StringComparison.Ordinal))
            File.AppendAllText(conf, Environment.NewLine + "include_if_exists = 'birknext.conf'" + Environment.NewLine);
        File.WriteAllText(Path.Combine(data.Database, "pg_hba.conf"), HostBasedAuthentication());
    }

    internal static string Configuration(int port, string logDirectory) => $"""
        # Written by the BirkNext launcher (TesterNative). Changes here are replaced on the next start.
        listen_addresses = '127.0.0.1'
        port = {port}
        unix_socket_directories = ''
        password_encryption = 'scram-sha-256'
        max_connections = 100
        logging_collector = on
        log_directory = '{logDirectory.Replace('\\', '/')}'
        log_filename = 'postgresql-%a.log'
        log_truncate_on_rotation = on
        log_rotation_age = 1d
        log_rotation_size = 20MB
        """;

    internal static string HostBasedAuthentication() => """
        # Written by the BirkNext launcher (TesterNative): local TCP only, password (SCRAM) required, BirkNext roles only.
        # TYPE  DATABASE   USER             ADDRESS        METHOD
        host    birknext   birknext_app     127.0.0.1/32   scram-sha-256
        host    "/^birknext_restore_[0-9a-f]{12}$"   birknext_app   127.0.0.1/32   scram-sha-256
        host    all        birknext_admin   127.0.0.1/32   scram-sha-256
        host    postgres   birknext_restore_provisioner   127.0.0.1/32   scram-sha-256
        """;

    /// <summary>The SQL that creates or re-asserts the restore-provisioning role (idempotent). Narrow by construction: LOGIN CREATEDB only, never
    /// SUPERUSER/CREATEROLE/REPLICATION/BYPASSRLS; membership in the application role only so CREATE DATABASE … OWNER app is allowed (no inherited
    /// privileges); CONNECT only to the maintenance database.</summary>
    internal static IReadOnlyList<string> ProvisionerStatements(string appUser, string password, int major) =>
    [
        $"""
        DO $$ BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{DatabaseCredentials.ProvisionerUser}') THEN
            CREATE ROLE {DatabaseCredentials.ProvisionerUser} LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '{password}';
          END IF;
        END $$;
        """,
        $"ALTER ROLE {DatabaseCredentials.ProvisionerUser} WITH LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD '{password}'",
        major >= 16 ? $"GRANT {appUser} TO {DatabaseCredentials.ProvisionerUser} WITH INHERIT FALSE, SET TRUE" : $"GRANT {appUser} TO {DatabaseCredentials.ProvisionerUser}",
        $"GRANT CONNECT ON DATABASE postgres TO {DatabaseCredentials.ProvisionerUser}",
    ];

    /// <summary>
    /// Ensures the restore-provisioning role exists with this installation's stored password (created on first need, password kept in the DPAPI
    /// credential file) and that pg_hba allows it to the maintenance database only. Never changes the application role or any database.
    /// Returns the (possibly updated) credentials; on failure the caller keeps running and restore reports BLOCKED.
    /// </summary>
    public DatabaseCredentials EnsureRestoreProvisioner(int port, DatabaseCredentials credentials)
    {
        var updated = credentials.ProvisionerPassword is { Length: > 0 } ? credentials : credentials with { ProvisionerPassword = DatabaseCredentials.NewPassword() };
        using (var admin = Open(port, credentials.AdminUser, credentials.AdminPassword, "postgres"))
        {
            foreach (var sql in ProvisionerStatements(credentials.AppUser, updated.ProvisionerPassword!, package.Manifest.PostgreSql.Major)) Execute(admin, sql);
            File.WriteAllText(Path.Combine(data.Database, "pg_hba.conf"), HostBasedAuthentication());
            Execute(admin, "SELECT pg_reload_conf()");
        }
        if (!ReferenceEquals(updated, credentials)) updated.Save(data.SecretsFile);
        log.Info($"Restore-provisioning role {DatabaseCredentials.ProvisionerUser} is in place (CREATEDB, not a superuser).");
        return updated;
    }

    // ── Start / status / stop ──────────────────────────────────────────────────────────────────────────────

    /// <summary>pg_ctl's view: is a postmaster running on OUR data directory (postmaster.pid + live process)?</summary>
    public bool IsRunning() => Run("pg_ctl", ["status", "-D", data.Database], TimeSpan.FromSeconds(30)).ExitCode == 0;

    /// <summary>
    /// Starts the cluster as a detached server of this user. pg_ctl gets its own hidden console (no inherited handles), so the server keeps running
    /// independently of this window. A stale postmaster.pid is handled by PostgreSQL itself; crash recovery is PostgreSQL's own.
    /// </summary>
    public void Start(int port, Action<string> step)
    {
        step("Starting local database");
        WriteConfiguration(port);
        var startupLog = Path.Combine(data.Logs, "postgresql-startup.log");
        // ShellExecute with a hidden window: the server inherits no handle of this launcher (no pipes, no redirected files), so it is fully
        // independent of this window and of whoever started the launcher.
        var info = new ProcessStartInfo(package.Tool("pg_ctl")) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in new[] { "start", "-D", data.Database, "-l", startupLog, "-w", "-t", ((int)StartTimeout.TotalSeconds).ToString() }) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        if (!p.WaitForExit((int)StartTimeout.TotalMilliseconds + 30_000) || p.ExitCode != 0)
            throw new LauncherException("Unable to start the local database.", "Start BirkNext again. If it keeps failing, send the diagnostics (Start-BirkNext.exe --diagnostics).",
                Tail(startupLog) + Environment.NewLine + Tail(LatestServerLog()));
        log.Info($"PostgreSQL started on 127.0.0.1:{port}.");
    }

    /// <summary>Stops OUR server (fast shutdown: committed data is safe), only on our own data directory.</summary>
    public bool Stop()
    {
        if (!IsRunning()) return true;
        var r = Run("pg_ctl", ["stop", "-D", data.Database, "-m", "fast", "-w", "-t", "60"], TimeSpan.FromSeconds(90));
        log.Info($"PostgreSQL stop: exit {r.ExitCode}.");
        return r.ExitCode == 0;
    }

    /// <summary>
    /// Identity, not just a port: the server on <paramref name="port"/> must accept our credentials AND report our cluster's system identifier
    /// and data directory. A TCP answer from another PostgreSQL never passes.
    /// </summary>
    public (bool Ok, string Detail) VerifyIdentity(int port, DatabaseCredentials credentials, ClusterRecord record)
    {
        try
        {
            using var c = Open(port, credentials.AdminUser, credentials.AdminPassword, "postgres");
            var id = Convert.ToString(Scalar(c, "SELECT system_identifier FROM pg_control_system()"));
            var dir = Convert.ToString(Scalar(c, "SELECT current_setting('data_directory')")) ?? "";
            if (id != record.SystemIdentifier) return (false, $"system identifier {id} is not BirkNext's {record.SystemIdentifier}");
            if (!SamePath(dir, data.Database)) return (false, "the server's data directory is not BirkNext's");
            return (true, $"PostgreSQL {Scalar(c, "SHOW server_version")} on port {port}, cluster {id}");
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException) { return (false, e.GetType().Name + ": " + e.Message); }
    }

    /// <summary>The application role can connect to its database (readiness as BirkNext itself will use it).</summary>
    public bool Ready(int port, DatabaseCredentials credentials)
    {
        try { using var c = Open(port, credentials.AppUser, credentials.AppPassword, credentials.Database); return Scalar(c, "SELECT 1") is 1; }
        catch (NpgsqlException) { return false; }
    }

    private string SystemIdentifier(int port, DatabaseCredentials credentials)
    {
        using var c = Open(port, credentials.AdminUser, credentials.AdminPassword, "postgres");
        return Convert.ToString(Scalar(c, "SELECT system_identifier FROM pg_control_system()"))!;
    }

    // ── Backup ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A logical backup (pg_dump custom format, consistent snapshot of a running database) into the data folder's backups, verified readable with
    /// pg_restore --list. The password reaches pg_dump only through its process environment.
    /// </summary>
    public string Backup(int port, DatabaseCredentials credentials, string reason)
    {
        Directory.CreateDirectory(data.Backups);
        var file = Path.Combine(data.Backups, $"birknext-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{reason}.dump");
        var env = new Dictionary<string, string> { ["PGPASSWORD"] = credentials.AppPassword };
        var dump = Run("pg_dump", ["-h", "127.0.0.1", "-p", port.ToString(), "-U", credentials.AppUser, "-d", credentials.Database, "-Fc", "--no-owner", "-f", file], TimeSpan.FromMinutes(30), env);
        if (dump.ExitCode != 0 || !File.Exists(file) || new FileInfo(file).Length == 0)
        {
            if (File.Exists(file)) File.Move(file, file + ".failed", overwrite: true);
            throw new LauncherException("The backup of the local database failed.", "Nothing was changed. Check free disk space and start BirkNext again.", dump.Output);
        }
        var list = Run("pg_restore", ["--list", file], TimeSpan.FromMinutes(5));
        if (list.ExitCode != 0 || !list.Output.Contains("TABLE", StringComparison.Ordinal))
        {
            File.Move(file, file + ".unreadable", overwrite: true);
            throw new LauncherException("The backup of the local database could not be verified.", "Nothing was changed. Start BirkNext again.", list.Output);
        }
        log.Info($"Backup written and verified: {Path.GetFileName(file)} ({new FileInfo(file).Length} bytes).");
        return file;
    }

    /// <summary>The automatic pre-upgrade backups beyond the newest <paramref name="keep"/> (manual backups are never selected).</summary>
    public static IReadOnlyList<string> PrunablePreUpgradeBackups(IEnumerable<string> files, int keep = 5) =>
        files.Where(f => Path.GetFileName(f).EndsWith("-pre-upgrade.dump", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(keep).ToList();

    public long DatabaseSize(int port, DatabaseCredentials credentials)
    {
        using var c = Open(port, credentials.AppUser, credentials.AppPassword, credentials.Database);
        return Convert.ToInt64(Scalar(c, $"SELECT pg_database_size('{credentials.Database}')"));
    }

    // ── SQL helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static NpgsqlConnection Open(int port, string user, string password, string database)
    {
        var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = port, Username = user, Password = password, Database = database, Pooling = false, Timeout = 15, CommandTimeout = 60,
        }.ConnectionString);
        c.Open();
        return c;
    }

    private static void Execute(NpgsqlConnection c, string sql) { using var cmd = new NpgsqlCommand(sql, c); cmd.ExecuteNonQuery(); }
    private static object? Scalar(NpgsqlConnection c, string sql) { using var cmd = new NpgsqlCommand(sql, c); return cmd.ExecuteScalar(); }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a.Replace('/', '\\')).TrimEnd('\\'), Path.GetFullPath(b.Replace('/', '\\')).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private string? LatestServerLog()
    {
        var dir = Path.Combine(data.Logs, "postgresql");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "postgresql-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
    }

    public static string Tail(string? path, int lines = 30)
    {
        if (path is null || !File.Exists(path)) return "";
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s);
            return string.Join(Environment.NewLine, r.ReadToEnd().Split('\n').TakeLast(lines)).TrimEnd();
        }
        catch (IOException) { return ""; }
    }
}
