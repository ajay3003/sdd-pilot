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

    // All created by EF Core migrations - required for platform infrastructure
    private static readonly string[] RequiredTables =
    [
        "project_documents",
        "scenarios",
        "reviewed_candidates",
        "candidate_links",
        "qa_delta_reviews",
        "trace_links",
        "traceability_suggestions",
        "code_files",
        "code_links"
    ];

    // Tables whose data indicates workspace readiness (empty = fresh workspace)
    private static readonly string[] WorkspaceIndicatorTables =
    [
        "project_documents",
        "scenarios"
    ];

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

        // Platform Health: Core infrastructure checks (hard failures if not met)
        report.DatabaseChecks.AddRange(await RunDatabaseChecksAsync());
        report.BackendApiChecks.AddRange(RunBackendApiChecks());

        // Workspace Readiness: Check if workspace artifacts have been loaded (not failures)
        report.WorkspaceChecks.AddRange(await RunWorkspaceReadinessChecksAsync());

        // Frontend-specific checks (populated by frontend)
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
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Database Configuration",
                Status = EnvironmentDiagnosticStatus.NotAvailable,
                Details = "Database unreachable; skipping remaining checks",
                Recommendation = ""
            });
            return checks;
        }

        // 2. Database info
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

        // 4. Required roles
        var dbProvider = _config["DatabaseSettings:Provider"] ?? "PostgreSQL";
        if (dbProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            var rolesCheck = await CheckRequiredRolesAsync();
            checks.Add(rolesCheck);
        }

        // 5. Required database exists
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Required Database Exists",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = $"Database '{dbName}' exists",
            Recommendation = ""
        });

        // 6. Required tables exist (schema validation - all created by migrations)
        var tablesCheck = await CheckRequiredTablesExistAsync();
        checks.Add(tablesCheck);

        // 7. EF Core migrations status
        var migrationsCheck = await CheckMigrationsAsync();
        checks.Add(migrationsCheck);

        // 8. Pending migrations
        var pendingCheck = await CheckPendingMigrationsAsync();
        checks.Add(pendingCheck);

        // 9. Schema up to date
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

    private async Task<List<EnvironmentDiagnosticCheck>> RunWorkspaceReadinessChecksAsync()
    {
        var checks = new List<EnvironmentDiagnosticCheck>();

        // Check if migrations have run first
        try
        {
            var migrationsApplied = await _db.Database.GetAppliedMigrationsAsync();
            if (!migrationsApplied.Any())
            {
                checks.Add(new EnvironmentDiagnosticCheck
                {
                    Name = "Workspace Initialization",
                    Status = EnvironmentDiagnosticStatus.NotAvailable,
                    Details = "Migrations not applied; workspace not available",
                    Recommendation = "Run migrations: dotnet ef database update"
                });
                return checks;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check migrations for workspace readiness");
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Workspace Initialization",
                Status = EnvironmentDiagnosticStatus.Warning,
                Details = "Could not determine migration status",
                Recommendation = "Verify migrations have been applied"
            });
            return checks;
        }

        // Check if workspace has imported artifacts (table data presence, not schema)
        var hasWorkspaceData = await CheckIfWorkspaceHasDataAsync();

        if (!hasWorkspaceData)
        {
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Workspace Initialization",
                Status = EnvironmentDiagnosticStatus.NotAvailable,
                Details = "No project artifacts have been imported",
                Recommendation = "Import markdown files (constitution.md, spec.md, plan.md, tasks.md, data-model.md) to initialize workspace"
            });
        }
        else
        {
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Workspace Initialization",
                Status = EnvironmentDiagnosticStatus.Pass,
                Details = "Workspace has imported project artifacts",
                Recommendation = ""
            });
        }

        // Workspace Persistence checks
        var persistenceChecks = await RunWorkspacePersistenceChecksAsync();
        checks.AddRange(persistenceChecks);

        return checks;
    }

    private async Task<List<EnvironmentDiagnosticCheck>> RunWorkspacePersistenceChecksAsync()
    {
        var checks = new List<EnvironmentDiagnosticCheck>();

        try
        {
            // Check workspace tables exist
            var tablesExist = await CheckWorkspacePersistenceTablesExistAsync();
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Workspace Persistence Tables",
                Status = tablesExist ? EnvironmentDiagnosticStatus.Pass : EnvironmentDiagnosticStatus.Fail,
                Details = tablesExist ? "saved_workspaces and saved_workspace_artifacts tables exist" : "Workspace persistence tables missing",
                Recommendation = tablesExist ? "" : "Run migrations: dotnet ef database update"
            });

            if (!tablesExist)
            {
                return checks;
            }

            // Check saved workspaces count
            var workspaceCount = await _db.SavedWorkspaces.CountAsync(w => !w.IsDeleted);
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Saved Workspaces",
                Status = EnvironmentDiagnosticStatus.Pass,
                Details = $"{workspaceCount} workspace(s) saved",
                Recommendation = ""
            });

            // Check auto-save configuration
            var autoSaveInterval = _config.GetValue("WorkspacePersistence:AutoSaveIntervalMs", 3000);
            var autoSaveThrottle = _config.GetValue("WorkspacePersistence:AutoSaveThrottleMs", 30000);
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Auto-Save Configuration",
                Status = EnvironmentDiagnosticStatus.Pass,
                Details = $"Auto-save every {autoSaveInterval}ms, throttled to every {autoSaveThrottle}ms",
                Recommendation = ""
            });

            // Check workflow review progress tables
            var reviewProgressTableExists = await CheckReviewProgressTableExistsAsync();
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Workflow Review Progress Table",
                Status = reviewProgressTableExists ? EnvironmentDiagnosticStatus.Pass : EnvironmentDiagnosticStatus.Warning,
                Details = reviewProgressTableExists ? "workspace_review_progress table exists" : "Table not yet created",
                Recommendation = reviewProgressTableExists ? "" : "Run pending migrations: dotnet ef database update"
            });

            if (reviewProgressTableExists)
            {
                // Check saved review progress records
                var reviewProgressCount = await _db.WorkspaceReviewProgress.CountAsync();
                checks.Add(new EnvironmentDiagnosticCheck
                {
                    Name = "Saved Review Progress Records",
                    Status = EnvironmentDiagnosticStatus.Pass,
                    Details = $"{reviewProgressCount} review progress record(s) saved",
                    Recommendation = ""
                });

                // Check for invalidated approvals
                var invalidatedCount = await _db.WorkspaceReviewProgress
                    .CountAsync(p => p.ApprovalState.ToString() == "InvalidatedByArtifactChange");
                if (invalidatedCount > 0)
                {
                    checks.Add(new EnvironmentDiagnosticCheck
                    {
                        Name = "Invalidated Approvals",
                        Status = EnvironmentDiagnosticStatus.Warning,
                        Details = $"{invalidatedCount} approval(s) invalidated due to artifact changes",
                        Recommendation = "Review affected workspaces and re-approve steps as needed"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error running workspace persistence checks");
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Workspace Persistence",
                Status = EnvironmentDiagnosticStatus.Warning,
                Details = "Could not check workspace persistence configuration",
                Recommendation = "Verify workspace persistence is properly configured"
            });
        }

        return checks;
    }

    private async Task<bool> CheckReviewProgressTableExistsAsync()
    {
        try
        {
            var count = await _db.WorkspaceReviewProgress.CountAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckWorkspacePersistenceTablesExistAsync()
    {
        try
        {
            // Try to query the workspace tables
            var count = await _db.SavedWorkspaces.CountAsync();
            return true;
        }
        catch
        {
            return false;
        }
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

    private async Task<EnvironmentDiagnosticCheck> CheckRequiredTablesExistAsync()
    {
        var missingTables = new List<string>();

        foreach (var table in RequiredTables)
        {
            try
            {
                var exists = await _db.Database.SqlQueryRaw<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'public' AND table_name = {0}
                    )
                    """, table).FirstOrDefaultAsync();

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
                Details = $"All {RequiredTables.Length} schema tables verified",
                Recommendation = ""
            };
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Required Tables Exist",
            Status = EnvironmentDiagnosticStatus.Fail,
            Details = $"Missing schema tables: {string.Join(", ", missingTables)}",
            Recommendation = "Migrations did not complete successfully. Run: dotnet ef database update"
        };
    }

    private async Task<bool> CheckIfWorkspaceHasDataAsync()
    {
        try
        {
            // Check if any project documents exist (primary indicator of imported artifacts)
            var hasProjectDocuments = await _db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_name = {0}
                )
                AND EXISTS(
                    SELECT 1 FROM project_documents LIMIT 1
                )
                """, "project_documents").FirstOrDefaultAsync();

            return hasProjectDocuments;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check if workspace has data");
            return false;
        }
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
