using Npgsql;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>Configuration <c>Backup:RestoreProvisioning</c>: a separate, narrowly privileged login used ONLY to create the new isolated restore
/// database. The application role keeps NOCREATEDB. The Windows Tester Package launcher provisions this role with its own admin credentials.</summary>
public sealed class RestoreProvisioningOptions
{
    /// <summary>Connection to the maintenance database (<c>postgres</c>) of the SAME server as the application database, as the provisioning role.</summary>
    public string? ConnectionString { get; set; }
}

/// <summary>
/// Who may create the isolated restore database, decided from the real server — never from configuration alone:
/// <list type="bullet">
/// <item><b>Application role</b> when it may create databases itself (developer containers).</item>
/// <item><b>Provisioning role</b> otherwise, only when ALL hold: it connects to the same running server (postmaster start time), it is NOT a superuser,
/// it has CREATEDB, and it may SET ROLE to the application role (so the new database is created OWNED BY the application role). The application
/// role then restores into the database it owns, exactly as before; the provisioning connection is used for nothing else.</item>
/// <item>Otherwise <b>blocked</b>, naming the exact required change.</item>
/// </list>
/// </summary>
public static class RestoreProvisioning
{
    public sealed record Decision(bool Allowed, bool UsesProvisioningRole, string Detail);

    /// <summary>The exact change an installation needs when neither path is available (shown when restore is blocked).</summary>
    public static string RequiredChange(string? appRole) =>
        $"Configure Backup:RestoreProvisioning:ConnectionString with a separate login that has CREATEDB, is not a superuser and may SET ROLE to {appRole ?? "the application role"} " +
        "(the Windows Tester Package launcher creates this role, birknext_restore_provisioner, when it starts). The application role is never given CREATEDB.";

    public static async Task<Decision> EvaluateAsync(string appConnectionString, string? provisioningConnectionString, CancellationToken ct)
    {
        var app = new NpgsqlConnectionStringBuilder(appConnectionString) { Pooling = false, Timeout = 5 };
        string appServer;
        await using (var db = new NpgsqlConnection(app.ConnectionString))
        {
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT pg_postmaster_start_time()::text, current_user, (SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user)", db);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            appServer = r.GetString(0);
            var appRole = r.GetString(1);
            if (!r.IsDBNull(2) && r.GetBoolean(2)) return new(true, false, "The database role may create the new, isolated restore database.");
            if (string.IsNullOrWhiteSpace(provisioningConnectionString))
                return new(false, false, "The application role may not create databases (by design), and no restore-provisioning role is configured. " + RequiredChange(appRole));
            app.Username = appRole;
        }
        try
        {
            var provisioning = new NpgsqlConnectionStringBuilder(provisioningConnectionString) { Pooling = false, Timeout = 5 };
            await using var db = new NpgsqlConnection(provisioning.ConnectionString);
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT pg_postmaster_start_time()::text, r.rolsuper, r.rolcreatedb, current_user,
                       pg_has_role(current_user, @app, CASE WHEN current_setting('server_version_num')::int >= 160000 THEN 'SET' ELSE 'MEMBER' END)
                FROM pg_roles r WHERE r.rolname = current_user
                """, db);
            cmd.Parameters.AddWithValue("app", app.Username!);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var (server, super, createDb, role, canSetApp) = (r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetString(3), r.GetBoolean(4));
            if (server != appServer) return new(false, true, $"The restore-provisioning login connects to a different PostgreSQL server than BirkNext's database; refused. {RequiredChange(app.Username)}");
            if (super) return new(false, true, $"The restore-provisioning role {role} is a superuser; that is broader than restore needs, so it is not used. {RequiredChange(app.Username)}");
            if (!createDb) return new(false, true, $"The restore-provisioning role {role} cannot create databases. {RequiredChange(app.Username)}");
            if (!canSetApp) return new(false, true, $"The restore-provisioning role {role} may not create databases owned by {app.Username}. {RequiredChange(app.Username)}");
            return new(true, true, $"A dedicated restore-provisioning role ({role}: CREATEDB, not a superuser) creates the new database owned by {app.Username}; the application role cannot create databases.");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
        {
            return new(false, true, $"The restore-provisioning login could not be used ({ex.GetType().Name}). {RequiredChange(app.Username)}");
        }
    }

    /// <summary>Creates the new database as the provisioning role, OWNED BY the application role. Refuses an existing name; touches nothing else.</summary>
    public static async Task CreateOwnedDatabaseAsync(string provisioningConnectionString, string target, string owner, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(provisioningConnectionString) { Pooling = false }.ConnectionString);
        await db.OpenAsync(ct);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", db))
        {
            exists.Parameters.AddWithValue("n", target);
            if (await exists.ExecuteScalarAsync(ct) is not null) throw new TransferRequestException("TARGET_EXISTS", $"Database {target} already exists; it is never overwritten.");
        }
        await using var create = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(target)} OWNER {QuoteIdentifier(owner)}", db);
        await create.ExecuteNonQueryAsync(ct);
    }

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
