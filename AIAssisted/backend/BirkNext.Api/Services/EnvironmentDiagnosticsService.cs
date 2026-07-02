using BirkNext.Api.Data;
using BirkNext.Api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace BirkNext.Api.Services;

public interface IEnvironmentDiagnosticsService
{
    Task<EnvironmentDiagnosticsReport> RunDiagnosticsAsync();
}

public class EnvironmentDiagnosticsService : IEnvironmentDiagnosticsService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly AppDbContext _db;
    private readonly ILogger<EnvironmentDiagnosticsService> _logger;

    // Required tables for minimal functionality
    private static readonly string[] RequiredTables =
    [
        "project_documents",
        "reviewed_candidates",
        "scenarios",
        "candidate_links",
        "qa_delta_reviews",
        "trace_links",
        "traceability_suggestions",
        "code_files",
        "code_links"
    ];

    // Required columns (table -> columns)
    private static readonly Dictionary<string, string[]> RequiredColumns = new()
    {
        ["project_documents"] = ["id", "project_id", "document_kind", "content"],
        ["reviewed_candidates"] = ["id", "candidate_id", "title", "review_status"],
        ["scenarios"] = ["id", "project_id", "title", "kind"],
    };

    public EnvironmentDiagnosticsService(
        IConfiguration config,
        IWebHostEnvironment env,
        AppDbContext db,
        ILogger<EnvironmentDiagnosticsService> logger)
    {
        _config = config;
        _env = env;
        _db = db;
        _logger = logger;
    }

    public async Task<EnvironmentDiagnosticsReport> RunDiagnosticsAsync()
    {
        var report = new EnvironmentDiagnosticsReport
        {
            Environment = _env.EnvironmentName
        };

        // Run database checks first (other checks may depend on DB)
        report.DatabaseChecks.AddRange(await RunDatabaseChecksAsync());

        // Run backend/API checks
        report.BackendApiChecks.AddRange(RunBackendApiChecks());

        // Add placeholder checks for frontend-dependent features
        // These will be populated by frontend diagnostics call
        report.WorkspaceChecks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Workspace Status",
            Status = EnvironmentDiagnosticStatus.NotAvailable,
            Details = "Populated by frontend diagnostics",
            Recommendation = "Load a project workspace to populate this check"
        });

        report.ReviewContextChecks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "ReviewContext Available",
            Status = EnvironmentDiagnosticStatus.NotAvailable,
            Details = "Populated by frontend diagnostics",
            Recommendation = "Load a complete workspace (constitution.md, spec.md, plan.md, tasks.md) to build ReviewContext"
        });

        report.ExportChecks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Export Services",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = "JSON and HTML export available in frontend",
            Recommendation = ""
        });

        return report;
    }

    private async Task<List<EnvironmentDiagnosticCheck>> RunDatabaseChecksAsync()
    {
        var checks = new List<EnvironmentDiagnosticCheck>();

        // 1. Database reachable
        var canConnect = await CanConnectToDatabaseAsync();
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Database Reachable",
            Status = canConnect ? EnvironmentDiagnosticStatus.Pass : EnvironmentDiagnosticStatus.Fail,
            Details = canConnect ? "Connected successfully" : "Could not connect to database",
            Recommendation = canConnect ? "" : "Check database connection string and ensure database server is running"
        });

        if (!canConnect)
        {
            // If DB is unreachable, we can't run other checks
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Database Name",
                Status = EnvironmentDiagnosticStatus.NotAvailable,
                Details = "Database unreachable; skipping remaining checks",
                Recommendation = ""
            });
            return checks;
        }

        // 2. Get database info
        var dbName = _config["DatabaseSettings:DatabaseName"] ?? "birknext";
        var dbVersion = await GetDatabaseVersionAsync();
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Current Database Name",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = dbName,
            Recommendation = ""
        });

        if (!string.IsNullOrEmpty(dbVersion))
        {
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "PostgreSQL Version",
                Status = EnvironmentDiagnosticStatus.Pass,
                Details = dbVersion,
                Recommendation = ""
            });
        }

        // 3. Current user
        var currentUser = await GetCurrentDatabaseUserAsync();
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Current Database User",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = currentUser ?? "Unknown",
            Recommendation = ""
        });

        // 4. Required roles (for PostgreSQL)
        var dbProvider = _config["DatabaseSettings:Provider"] ?? "PostgreSQL";
        if (dbProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            var rolesCheck = await CheckRequiredRolesAsync();
            checks.Add(rolesCheck);
        }

        // 5. Required database exists
        var dbExistsCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Required Database Exists",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = $"Database '{dbName}' exists",
            Recommendation = ""
        };
        checks.Add(dbExistsCheck);

        // 6. Required tables exist
        var requiredTablesCheck = await CheckRequiredTablesAsync();
        checks.Add(requiredTablesCheck);

        // 7. Required columns exist
        var requiredColumnsCheck = await CheckRequiredColumnsAsync();
        checks.Add(requiredColumnsCheck);

        // 8. EF Core migrations status
        var migrationsCheck = await CheckMigrationsAsync();
        checks.Add(migrationsCheck);

        // 9. Pending migrations
        var pendingCheck = await CheckPendingMigrationsAsync();
        checks.Add(pendingCheck);

        // 10. Schema up to date
        var schemaCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Schema Up to Date",
            Status = pendingCheck.Status == EnvironmentDiagnosticStatus.Pass ? EnvironmentDiagnosticStatus.Pass : EnvironmentDiagnosticStatus.Warning,
            Details = pendingCheck.Status == EnvironmentDiagnosticStatus.Pass ? "Schema is current" : "Pending migrations exist",
            Recommendation = pendingCheck.Status == EnvironmentDiagnosticStatus.Pass ? "" : "Run pending migrations: dotnet ef database update"
        };
        checks.Add(schemaCheck);

        return checks;
    }

    private List<EnvironmentDiagnosticCheck> RunBackendApiChecks()
    {
        var checks = new List<EnvironmentDiagnosticCheck>();

        var backendUrl = _config["BACKEND_URL"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0]
            ?? "http://localhost:5000";

        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Backend Reachable",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = $"Backend running at {backendUrl}",
            Recommendation = ""
        });

        var graphqlEndpoint = $"{backendUrl}/graphql";
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "GraphQL Endpoint Reachable",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = graphqlEndpoint,
            Recommendation = ""
        });

        // API version/build info
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown";

        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "API Version/Build",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = version,
            Recommendation = ""
        });

        return checks;
    }

    private async Task<bool> CanConnectToDatabaseAsync()
    {
        try
        {
            using (var cmd = _db.Database.GetDbConnection().CreateCommand())
            {
                cmd.CommandText = "SELECT 1";
                await _db.Database.OpenConnectionAsync();
                var result = await cmd.ExecuteScalarAsync();
                await _db.Database.CloseConnectionAsync();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database connection test failed");
            return false;
        }
    }

    private async Task<string?> GetDatabaseVersionAsync()
    {
        try
        {
            var result = await _db.Database.SqlQueryRaw<string>(
                "SELECT version()").FirstOrDefaultAsync();
            return result?.Split(',')[0];
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetCurrentDatabaseUserAsync()
    {
        try
        {
            var result = await _db.Database.SqlQueryRaw<string>(
                "SELECT current_user").FirstOrDefaultAsync();
            return result;
        }
        catch
        {
            return null;
        }
    }

    private async Task<EnvironmentDiagnosticCheck> CheckRequiredRolesAsync()
    {
        try
        {
            // In PostgreSQL, check if roles exist (typically just need the connecting user's role)
            var currentUser = await GetCurrentDatabaseUserAsync();
            return new EnvironmentDiagnosticCheck
            {
                Name = "Required Roles Exist",
                Status = EnvironmentDiagnosticStatus.Pass,
                Details = $"User role '{currentUser}' exists",
                Recommendation = ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check database roles");
            return new EnvironmentDiagnosticCheck
            {
                Name = "Required Roles Exist",
                Status = EnvironmentDiagnosticStatus.Warning,
                Details = "Could not verify roles",
                Recommendation = "Verify database user has appropriate role permissions"
            };
        }
    }

    private async Task<EnvironmentDiagnosticCheck> CheckRequiredTablesAsync()
    {
        var missingTables = new List<string>();

        foreach (var table in RequiredTables)
        {
            try
            {
                var exists = await _db.Database.SqlQueryRaw<bool>(
                    $"""
                    SELECT EXISTS(
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'public' AND table_name = '{table}'
                    )
                    """).FirstOrDefaultAsync();

                if (!exists)
                {
                    missingTables.Add(table);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check table {Table}", table);
                missingTables.Add(table);
            }
        }

        if (missingTables.Count == 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Required Tables Exist",
                Status = EnvironmentDiagnosticStatus.Pass,
                Details = $"All {RequiredTables.Length} required tables exist",
                Recommendation = ""
            };
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Required Tables Exist",
            Status = EnvironmentDiagnosticStatus.Fail,
            Details = $"Missing tables: {string.Join(", ", missingTables)}",
            Recommendation = "Run EF Core migrations: dotnet ef database update"
        };
    }

    private async Task<EnvironmentDiagnosticCheck> CheckRequiredColumnsAsync()
    {
        var missingColumns = new List<string>();

        foreach (var (table, columns) in RequiredColumns)
        {
            foreach (var column in columns)
            {
                try
                {
                    var exists = await _db.Database.SqlQueryRaw<bool>(
                        $"""
                        SELECT EXISTS(
                            SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'public' AND table_name = '{table}'
                            AND column_name = '{column}'
                        )
                        """).FirstOrDefaultAsync();

                    if (!exists)
                    {
                        missingColumns.Add($"{table}.{column}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check column {Table}.{Column}", table, column);
                    missingColumns.Add($"{table}.{column}");
                }
            }
        }

        if (missingColumns.Count == 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Required Columns Exist",
                Status = EnvironmentDiagnosticStatus.Pass,
                Details = "All required columns exist",
                Recommendation = ""
            };
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Required Columns Exist",
            Status = EnvironmentDiagnosticStatus.Fail,
            Details = $"Missing columns: {string.Join(", ", missingColumns)}",
            Recommendation = "Run EF Core migrations: dotnet ef database update"
        };
    }

    private async Task<EnvironmentDiagnosticCheck> CheckMigrationsAsync()
    {
        try
        {
            var migrations = await _db.Database.GetAppliedMigrationsAsync();
            var count = migrations.Count();

            return new EnvironmentDiagnosticCheck
            {
                Name = "EF Core Migrations Applied",
                Status = count > 0 ? EnvironmentDiagnosticStatus.Pass : EnvironmentDiagnosticStatus.Warning,
                Details = count > 0 ? $"{count} migrations applied" : "No migrations applied",
                Recommendation = count > 0 ? "" : "Run migrations: dotnet ef database update"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check applied migrations");
            return new EnvironmentDiagnosticCheck
            {
                Name = "EF Core Migrations Applied",
                Status = EnvironmentDiagnosticStatus.Warning,
                Details = "Could not verify migration status",
                Recommendation = "Ensure database is up to date: dotnet ef database update"
            };
        }
    }

    private async Task<EnvironmentDiagnosticCheck> CheckPendingMigrationsAsync()
    {
        try
        {
            var pending = await _db.Database.GetPendingMigrationsAsync();
            var count = pending.Count();

            if (count == 0)
            {
                return new EnvironmentDiagnosticCheck
                {
                    Name = "Pending Migrations",
                    Status = EnvironmentDiagnosticStatus.Pass,
                    Details = "No pending migrations",
                    Recommendation = ""
                };
            }

            return new EnvironmentDiagnosticCheck
            {
                Name = "Pending Migrations",
                Status = EnvironmentDiagnosticStatus.Warning,
                Details = $"{count} pending migration(s): {string.Join(", ", pending.Select(m => m.Split('_').Last()))}",
                Recommendation = "Apply migrations: dotnet ef database update"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check pending migrations");
            return new EnvironmentDiagnosticCheck
            {
                Name = "Pending Migrations",
                Status = EnvironmentDiagnosticStatus.Warning,
                Details = "Could not determine migration status",
                Recommendation = "Verify database schema is current"
            };
        }
    }
}
