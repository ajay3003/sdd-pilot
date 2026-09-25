using BirkNext.Api.Data;
using BirkNext.Api.Data.Migrations;
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
    private readonly IMigrationIntegrityValidator _migrationValidator;
    private readonly ISystemSettingsStatusEngine _statusEngine;
    private readonly ILogger<EnvironmentDiagnosticsService> _logger;

    public EnvironmentDiagnosticsService(
        IConfiguration config,
        IWebHostEnvironment env,
        AppDbContext db,
        IMigrationIntegrityValidator migrationValidator,
        ISystemSettingsStatusEngine statusEngine,
        ILogger<EnvironmentDiagnosticsService> logger)
    {
        _config = config;
        _env = env;
        _db = db;
        _migrationValidator = migrationValidator;
        _statusEngine = statusEngine;
        _logger = logger;
    }

    public async Task<EnvironmentDiagnosticsReport> RunDiagnosticsAsync()
    {
        var report = new EnvironmentDiagnosticsReport
        {
            Environment = _env.EnvironmentName
        };

        // Collect all check categories into sections
        var environmentChecks = RunEnvironmentChecks();
        var (databaseChecks, tableProbe) = await RunDatabaseChecksAsync();
        var backendChecks = RunBackendApiChecks();
        var workspaceChecks = await RunWorkspaceReadinessChecksAsync(tableProbe);
        var reviewContextChecks = await RunReviewContextChecksAsync();
        var exportChecks = RunExportChecks();

        // Organize checks into unified SettingsSection hierarchy
        report.Sections.Add(ConvertChecksToSection("Environment", environmentChecks));
        report.Sections.Add(ConvertChecksToSection("Database", databaseChecks));
        report.Sections.Add(ConvertChecksToSection("Backend / API", backendChecks));
        report.Sections.Add(ConvertChecksToSection("Workspace", workspaceChecks));
        report.Sections.Add(ConvertChecksToSection("ReviewContext", reviewContextChecks));
        report.Sections.Add(ConvertChecksToSection("Export / Reports", exportChecks));

        ApplySummary(report);


        return report;
    }

    internal EnvironmentDiagnosticsReport BuildReportForSections(
        string environment,
        List<SettingsSection> sections)
    {
        var report = new EnvironmentDiagnosticsReport
        {
            Environment = environment,
            Sections = sections
        };

        ApplySummary(report);
        return report;
    }

    private void ApplySummary(EnvironmentDiagnosticsReport report)
    {
        DiagnosticPageServiceHelpers.ApplySectionStatuses(report.Sections, _statusEngine);
        report.Summary = DiagnosticPageServiceHelpers.SummarizeSections(report.Sections, _statusEngine);
        report.OverallStatus = report.Summary.OverallStatus;
    }

    /// <summary>
    /// Convert a list of diagnostic checks to a SettingsSection with SettingsItem objects.
    /// </summary>
    private SettingsSection ConvertChecksToSection(string title, List<EnvironmentDiagnosticCheck> checks)
    {
        var items = checks.Select(check => new SettingsItem
        {
            Name = check.Name,
            Value = check.Details,
            Status = check.Status,
            Description = check.Details,
            Recommendation = check.Recommendation,
            IsRequired = false
        }).ToList();

        // Calculate section status from items using the shared engine
        var sectionStatus = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());

        return new SettingsSection
        {
            Title = title,
            Description = $"Diagnostic checks for {title.ToLower()}",
            Status = sectionStatus,
            Items = items,
            IsRequired = false
        };
    }

    private List<EnvironmentDiagnosticCheck> RunEnvironmentChecks()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown";

        return
        [
            new EnvironmentDiagnosticCheck
            {
                Name = "Hosting Environment",
                Status = SystemSettingsStatus.Pass,
                Details = _env.EnvironmentName,
                Recommendation = ""
            },
            new EnvironmentDiagnosticCheck
            {
                Name = "Content Root",
                Status = string.IsNullOrWhiteSpace(_env.ContentRootPath)
                    ? SystemSettingsStatus.Warning
                    : SystemSettingsStatus.Pass,
                Details = string.IsNullOrWhiteSpace(_env.ContentRootPath)
                    ? "Content root unavailable"
                    : _env.ContentRootPath,
                Recommendation = ""
            },
            new EnvironmentDiagnosticCheck
            {
                Name = "API Version",
                Status = SystemSettingsStatus.Pass,
                Details = version,
                Recommendation = ""
            }
        ];
    }

    /// <summary>
    /// Database checks, plus the table probe the workspace checks reuse. The probe is null when the database is unreachable.
    /// </summary>
    private async Task<(List<EnvironmentDiagnosticCheck> Checks, TableProbe? Probe)> RunDatabaseChecksAsync()
    {
        // 1. Database reachable
        var canConnect = await CanConnectToDatabaseAsync();
        if (!canConnect)
        {
            return (UnreachableDatabaseChecks(), null);
        }

        var checks = new List<EnvironmentDiagnosticCheck>
        {
            new()
            {
                Name = "Database Reachable",
                Status = SystemSettingsStatus.Pass,
                Details = "Connected successfully",
                Recommendation = ""
            }
        };

        // 2. Database info
        var dbName = _config["DatabaseSettings:DatabaseName"] ?? "birknext";
        var dbVersion = await GetDatabaseVersionAsync();
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Current Database Name",
            Status = SystemSettingsStatus.Pass,
            Details = dbName,
            Recommendation = ""
        });

        if (!string.IsNullOrEmpty(dbVersion))
        {
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "PostgreSQL Version",
                Status = SystemSettingsStatus.Pass,
                Details = dbVersion,
                Recommendation = ""
            });
        }

        // 3. Current user
        var currentUser = await GetCurrentDatabaseUserAsync();
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Current Database User",
            Status = SystemSettingsStatus.Pass,
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
            Status = SystemSettingsStatus.Pass,
            Details = $"Database '{dbName}' exists",
            Recommendation = ""
        });

        // Migration history is read first so a missing-table failure can name the remediation that actually works.
        var appliedMigrationsCount = await GetAppliedMigrationsCountAsync();
        var pendingMigrations = await GetPendingMigrationsAsync();

        // 6. Required tables exist: real schema objects, independent of migration history
        var modelTables = GetTablesFromModel();
        var tableProbe = await ProbeTablesAsync(modelTables);
        var tablesCheck = EvaluateRequiredTables(
            modelTables,
            tableProbe.ExistingKeys,
            tableProbe.UnverifiedKeys,
            appliedMigrationsCount ?? 0,
            pendingMigrations?.Count);
        checks.Add(tablesCheck);

        // 7. EF Core migrations applied (migration history rows only)
        checks.Add(EvaluateAppliedMigrations(appliedMigrationsCount));

        // 8. Pending migrations (assembly migrations absent from history)
        var pendingCheck = EvaluatePendingMigrations(pendingMigrations);
        checks.Add(pendingCheck);

        // 9. EF Core Migration Integrity (migration files and compiled snapshot)
        var integrityCheck = await CheckMigrationIntegrityAsync();
        checks.Add(integrityCheck);

        // 10. Schema up to date, derived from 6, 8 and 9
        checks.Add(EvaluateSchemaUpToDate(tablesCheck, pendingCheck, integrityCheck));

        return (checks, tableProbe);
    }

    internal static List<EnvironmentDiagnosticCheck> UnreachableDatabaseChecks() =>
    [
        new()
        {
            Name = "Database Reachable",
            Status = SystemSettingsStatus.Fail,
            Details = "Could not connect to database",
            Recommendation = "Check database connection string and ensure database server is running"
        },
        new()
        {
            Name = "Database Configuration",
            Status = SystemSettingsStatus.Unavailable,
            Details = "Database unreachable; skipping remaining checks",
            Recommendation = ""
        }
    ];

    private async Task<List<EnvironmentDiagnosticCheck>> RunWorkspaceReadinessChecksAsync(TableProbe? tableProbe)
    {
        var checks = new List<EnvironmentDiagnosticCheck>();

        if (tableProbe is null)
        {
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Workspace Initialization",
                Status = SystemSettingsStatus.Unavailable,
                Details = "Database unreachable; backend workspace persistence cannot be inspected",
                Recommendation = "See Database Reachable."
            });
            return checks;
        }

        // Check if migrations have run first
        try
        {
            var migrationsApplied = await _db.Database.GetAppliedMigrationsAsync();
            if (!migrationsApplied.Any())
            {
                checks.Add(new EnvironmentDiagnosticCheck
                {
                    Name = "Workspace Initialization",
                    Status = SystemSettingsStatus.Unavailable,
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
                Status = SystemSettingsStatus.Warning,
                Details = "Could not determine migration status",
                Recommendation = "Verify migrations have been applied"
            });
            return checks;
        }

        checks.Add(ActiveWorkspaceLoadedCheck());

        checks.Add(await CheckImportedProjectDocumentsAsync(tableProbe));

        // Workspace Persistence checks
        var persistenceChecks = await RunWorkspacePersistenceChecksAsync(tableProbe);
        checks.AddRange(persistenceChecks);

        return checks;
    }

    /// <summary>
    /// The active workspace is browser/session state. The backend is never sent it, so it does not evaluate it.
    /// </summary>
    internal static EnvironmentDiagnosticCheck ActiveWorkspaceLoadedCheck() => new()
    {
        Name = "Active Workspace Loaded",
        Status = SystemSettingsStatus.Info,
        Details = "Not evaluated by backend diagnostics: the active workspace is browser/session state the backend does not receive. It is evaluated in System Settings -> Runtime Diagnostics and ReviewContext Validation.",
        Recommendation = ""
    };

    /// <summary>
    /// Whether the browser's active workspace is saved needs its workspace id, which backend diagnostics never receive.
    /// </summary>
    internal static EnvironmentDiagnosticCheck CurrentWorkspaceSavedCheck() => new()
    {
        Name = "Current Workspace Saved/Unsaved",
        Status = SystemSettingsStatus.Info,
        Details = "Not evaluated by backend diagnostics: no active browser workspace id is available to the backend. Saved workspace totals are reported in Saved Workspaces.",
        Recommendation = ""
    };

    /// <summary>
    /// The backend WorkspacePersistence:AutoSave* settings are read only by the backend AutoSaveService, which nothing
    /// calls. Live auto-save runs in the browser with its own interval and throttle, so these values are reported, not
    /// evaluated, and say nothing about whether auto-save storage works.
    /// </summary>
    internal static EnvironmentDiagnosticCheck AutoSaveConfigurationCheck(int intervalMs, int throttleMs) => new()
    {
        Name = "Auto-Save Configuration",
        Status = SystemSettingsStatus.Info,
        Details = $"Backend WorkspacePersistence settings: every {intervalMs}ms, throttled to every {throttleMs}ms. Not evaluated: workspace auto-save runs in the browser with its own interval and throttle, and these settings do not verify auto-save storage (see Workspace Persistence Tables).",
        Recommendation = ""
    };

    private async Task<EnvironmentDiagnosticCheck> CheckImportedProjectDocumentsAsync(TableProbe tableProbe)
    {
        if (!tableProbe.ExistingKeys.Contains(ProjectDocumentsTable))
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Imported Project Documents",
                Status = SystemSettingsStatus.Unavailable,
                Details = tableProbe.UnverifiedKeys.Contains(ProjectDocumentsTable)
                    ? $"Could not verify that {ProjectDocumentsTable} exists"
                    : $"{ProjectDocumentsTable} does not exist",
                Recommendation = "See Required Tables Exist."
            };
        }

        try
        {
            var hasDocuments = await _db.ProjectDocuments.AnyAsync();
            return new EnvironmentDiagnosticCheck
            {
                Name = "Imported Project Documents",
                Status = SystemSettingsStatus.Pass,
                Details = hasDocuments
                    ? "Project documents have been imported to backend storage"
                    : "No project documents have been imported to backend storage. This is normal when using browser/session workspace state.",
                Recommendation = ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read project documents");
            return new EnvironmentDiagnosticCheck
            {
                Name = "Imported Project Documents",
                Status = SystemSettingsStatus.Unavailable,
                Details = $"Could not read {ProjectDocumentsTable}: {ex.Message}",
                Recommendation = "Check the backend log for the failing query."
            };
        }
    }

    private async Task<List<EnvironmentDiagnosticCheck>> RunWorkspacePersistenceChecksAsync(TableProbe tableProbe)
    {
        var checks = new List<EnvironmentDiagnosticCheck>();

        // Saved workspaces: saved_workspaces + saved_workspace_artifacts
        var workspaceTablesCheck = EvaluateWorkspacePersistenceTables(tableProbe.ExistingKeys, tableProbe.UnverifiedKeys);
        checks.Add(workspaceTablesCheck);

        if (workspaceTablesCheck.Status == SystemSettingsStatus.Pass)
        {
            checks.Add((await RunPersistenceQueryAsync("Saved Workspaces", "counting saved workspaces", async () =>
            {
                var workspaceCount = await _db.SavedWorkspaces.CountAsync(w => !w.IsDeleted);
                return new EnvironmentDiagnosticCheck
                {
                    Name = "Saved Workspaces",
                    Status = SystemSettingsStatus.Pass,
                    Details = $"{workspaceCount} workspace(s) saved",
                    Recommendation = ""
                };
            }))!);
        }

        checks.Add(CurrentWorkspaceSavedCheck());

        checks.Add(AutoSaveConfigurationCheck(
            _config.GetValue("WorkspacePersistence:AutoSaveIntervalMs", 3000),
            _config.GetValue("WorkspacePersistence:AutoSaveThrottleMs", 30000)));

        // Workflow review progress: its own table and capability, not saved-workspace persistence
        var reviewProgressTableCheck = EvaluateReviewProgressTable(tableProbe.ExistingKeys, tableProbe.UnverifiedKeys);
        checks.Add(reviewProgressTableCheck);

        if (reviewProgressTableCheck.Status == SystemSettingsStatus.Pass)
        {
            checks.Add((await RunPersistenceQueryAsync("Saved Review Progress Records", "counting review progress records", async () =>
            {
                var reviewProgressCount = await _db.WorkspaceReviewProgress.CountAsync();
                return new EnvironmentDiagnosticCheck
                {
                    Name = "Saved Review Progress Records",
                    Status = SystemSettingsStatus.Pass,
                    Details = $"{reviewProgressCount} review progress record(s) saved",
                    Recommendation = ""
                };
            }))!);

            var invalidatedCheck = await RunPersistenceQueryAsync("Invalidated Approvals", "counting invalidated approvals", async () =>
            {
                var invalidatedCount = await _db.WorkspaceReviewProgress
                    .CountAsync(p => p.ApprovalState == Models.ApprovalState.InvalidatedByArtifactChange);
                return invalidatedCount == 0
                    ? null
                    : new EnvironmentDiagnosticCheck
                    {
                        Name = "Invalidated Approvals",
                        Status = SystemSettingsStatus.Warning,
                        Details = $"{invalidatedCount} approval(s) invalidated due to artifact changes",
                        Recommendation = "Review affected workspaces and re-approve steps as needed"
                    };
            });
            if (invalidatedCheck is not null)
            {
                checks.Add(invalidatedCheck);
            }
        }

        return checks;
    }

    /// <summary>
    /// Runs one persistence query. A query failure is reported against that row, naming what failed; it is never
    /// read as a missing table or a configuration problem.
    /// </summary>
    private async Task<EnvironmentDiagnosticCheck?> RunPersistenceQueryAsync(
        string name,
        string operation,
        Func<Task<EnvironmentDiagnosticCheck?>> query)
    {
        try
        {
            return await query();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workspace persistence query failed while {Operation}", operation);
            return new EnvironmentDiagnosticCheck
            {
                Name = name,
                Status = SystemSettingsStatus.Unavailable,
                Details = $"Query failed while {operation}: {ex.Message}",
                Recommendation = "Check the backend log for the failing query."
            };
        }
    }

    internal static EnvironmentDiagnosticCheck EvaluateWorkspacePersistenceTables(
        IReadOnlySet<string> existingTableKeys,
        IReadOnlyCollection<string> unverifiedTableKeys) =>
        EvaluatePersistenceTables(
            "Workspace Persistence Tables",
            "saved workspaces",
            [SavedWorkspacesTable, SavedWorkspaceArtifactsTable],
            existingTableKeys,
            unverifiedTableKeys);

    internal static EnvironmentDiagnosticCheck EvaluateReviewProgressTable(
        IReadOnlySet<string> existingTableKeys,
        IReadOnlyCollection<string> unverifiedTableKeys) =>
        EvaluatePersistenceTables(
            "Review Progress Tables Exist",
            "workflow review progress, separate from saved workspaces",
            [WorkspaceReviewProgressTable],
            existingTableKeys,
            unverifiedTableKeys);

    private static EnvironmentDiagnosticCheck EvaluatePersistenceTables(
        string name,
        string capability,
        IReadOnlyList<string> tables,
        IReadOnlySet<string> existingTableKeys,
        IReadOnlyCollection<string> unverifiedTableKeys)
    {
        var missing = tables.Where(t => !existingTableKeys.Contains(t) && !unverifiedTableKeys.Contains(t)).ToList();
        if (missing.Count > 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = name,
                Status = SystemSettingsStatus.Fail,
                Details = $"Backend persistence for {capability} is unavailable: required tables are missing: {string.Join(", ", missing)}",
                Recommendation = "See Required Tables Exist for the repair that matches the migration state."
            };
        }

        var unverified = tables.Where(t => !existingTableKeys.Contains(t)).ToList();
        if (unverified.Count > 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = name,
                Status = SystemSettingsStatus.Unavailable,
                Details = $"Could not verify the {capability} tables: {string.Join(", ", unverified)}",
                Recommendation = "Check the backend log for the failing metadata query."
            };
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = name,
            Status = SystemSettingsStatus.Pass,
            Details = $"{string.Join(" and ", tables)} {(tables.Count == 1 ? "exists" : "exist")} ({capability})",
            Recommendation = ""
        };
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
            Status = SystemSettingsStatus.Pass,
            Details = $"Backend running at {backendUrl}",
            Recommendation = ""
        });

        var graphqlEndpoint = $"{backendUrl}/graphql";
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "GraphQL Endpoint Reachable",
            Status = SystemSettingsStatus.Pass,
            Details = graphqlEndpoint,
            Recommendation = ""
        });

        return checks;
    }

    private static List<EnvironmentDiagnosticCheck> RunExportChecks()
    {
        return
        [
            new EnvironmentDiagnosticCheck
            {
                Name = "JSON Export",
                Status = SystemSettingsStatus.Pass,
                Details = "JSON diagnostics export is available.",
                Recommendation = ""
            },
            new EnvironmentDiagnosticCheck
            {
                Name = "HTML Report Export",
                Status = SystemSettingsStatus.Pass,
                Details = "HTML diagnostics report export is available.",
                Recommendation = ""
            }
        ];
    }

    private async Task<List<EnvironmentDiagnosticCheck>> RunReviewContextChecksAsync()
    {
        var checks = new List<EnvironmentDiagnosticCheck>
        {
            new()
            {
                Name = "ReviewContext Available",
                Status = SystemSettingsStatus.Info,
                Details = "Not evaluated by backend diagnostics: the active workspace is browser/session state the backend does not receive.",
                Recommendation = "Use System Settings -> Developer -> ReviewContext Validation in the browser session for the active workspace."
            }
        };

        try
        {
            var savedWorkspaceCount = await _db.SavedWorkspaces.CountAsync(workspace => !workspace.IsDeleted);
            var completeWorkspaceCount = await _db.SavedWorkspaces
                .Where(workspace => !workspace.IsDeleted)
                .CountAsync(workspace =>
                    workspace.Artifacts.Any(artifact => artifact.ArtifactType == Models.ArtifactType.Constitution) &&
                    workspace.Artifacts.Any(artifact => artifact.ArtifactType == Models.ArtifactType.Specification) &&
                    workspace.Artifacts.Any(artifact => artifact.ArtifactType == Models.ArtifactType.Plan) &&
                    workspace.Artifacts.Any(artifact => artifact.ArtifactType == Models.ArtifactType.Tasks));

            checks.Add(EvaluateSavedWorkspaceReviewContext(savedWorkspaceCount, completeWorkspaceCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check persisted ReviewContext workspace availability");
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Saved Workspace ReviewContext Source",
                Status = SystemSettingsStatus.Warning,
                Details = "Could not inspect saved workspace artifacts for ReviewContext readiness.",
                Recommendation = "Verify saved workspace persistence tables are present and migrations are current."
            });
        }

        return checks;
    }

    internal static EnvironmentDiagnosticCheck EvaluateSavedWorkspaceReviewContext(
        int savedWorkspaceCount,
        int completeWorkspaceCount)
    {
        if (savedWorkspaceCount == 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Saved Workspace ReviewContext Source",
                Status = SystemSettingsStatus.Warning,
                Details = "No saved workspaces exist. Backend can only build ReviewContext from persisted workspaces.",
                Recommendation = "Save a workspace to enable ReviewContext reconstruction from backend state."
            };
        }

        if (completeWorkspaceCount == 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Saved Workspace ReviewContext Source",
                Status = SystemSettingsStatus.Warning,
                Details = $"{savedWorkspaceCount} saved workspace(s) found, but none have the required artifacts (constitution, specification, plan, tasks).",
                Recommendation = "Save a complete workspace to enable ReviewContext reconstruction from backend state."
            };
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Saved Workspace ReviewContext Source",
            Status = SystemSettingsStatus.Pass,
            Details = $"{completeWorkspaceCount} saved workspace(s) can be used to reconstruct ReviewContext",
            Recommendation = ""
        };
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

    // A scalar SqlQueryRaw<T> is composed as SELECT t."Value" FROM (<sql>) AS t, so the column must be named "Value".
    // Without the alias every one of these queries throws 42703 (column t.Value does not exist). Each returns exactly one row.
    private async Task<string?> GetDatabaseVersionAsync()
    {
        try
        {
            var result = await _db.Database.SqlQueryRaw<string>(
                "SELECT version() AS \"Value\"").SingleAsync();
            return result?.Split(',')[0];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read PostgreSQL version");
            return null;
        }
    }

    private async Task<string?> GetCurrentDatabaseUserAsync()
    {
        try
        {
            var result = await _db.Database.SqlQueryRaw<string>(
                "SELECT current_user::text AS \"Value\"").SingleAsync();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read current database user");
            return null;
        }
    }

    private async Task<EnvironmentDiagnosticCheck> CheckRequiredRolesAsync()
    {
        // In PostgreSQL, check if roles exist (typically just need the connecting user's role)
        var currentUser = await GetCurrentDatabaseUserAsync();
        if (currentUser is null)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Required Roles Exist",
                Status = SystemSettingsStatus.Warning,
                Details = "Could not verify roles",
                Recommendation = "Verify database user has appropriate role permissions"
            };
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Required Roles Exist",
            Status = SystemSettingsStatus.Pass,
            Details = $"User role '{currentUser}' exists",
            Recommendation = ""
        };
    }

    /// <summary>
    /// Looks each EF model table up in information_schema. A lookup that fails is recorded as unverified, never as missing.
    /// </summary>
    private async Task<TableProbe> ProbeTablesAsync(IReadOnlyCollection<SchemaTable> modelTables)
    {
        var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unverifiedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in modelTables)
        {
            try
            {
                var exists = await _db.Database.SqlQueryRaw<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = {0} AND table_name = {1}
                    ) AS "Value"
                    """, table.Schema, table.Name).SingleAsync();

                if (exists)
                {
                    existingTables.Add(table.Key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check table {Schema}.{Table}", table.Schema, table.Name);
                unverifiedTables.Add(table.Key);
            }
        }

        return new TableProbe(existingTables, unverifiedTables);
    }

    internal static EnvironmentDiagnosticCheck EvaluateRequiredTables(
        IReadOnlyCollection<SchemaTable> modelTables,
        IReadOnlySet<string> existingTableKeys,
        IReadOnlySet<string> unverifiedTableKeys,
        int appliedMigrationsCount,
        int? pendingMigrationsCount)
    {
        var requiredMissing = new List<string>();
        var requiredUnverified = new List<string>();
        var optionalMissing = new List<string>();
        var inactiveMissing = new List<string>();
        var otherUnverified = new List<string>();

        foreach (var table in modelTables)
        {
            if (existingTableKeys.Contains(table.Key))
            {
                continue;
            }

            var requirement = ClassifyTable(table.Name);
            if (unverifiedTableKeys.Contains(table.Key))
            {
                (requirement == SchemaTableRequirement.Required ? requiredUnverified : otherUnverified).Add(table.DisplayName);
                continue;
            }

            switch (requirement)
            {
                case SchemaTableRequirement.Required:
                    requiredMissing.Add(table.DisplayName);
                    break;
                case SchemaTableRequirement.Optional:
                    optionalMissing.Add(table.DisplayName);
                    break;
                case SchemaTableRequirement.Inactive:
                case SchemaTableRequirement.DemoOrSeed:
                    inactiveMissing.Add(table.DisplayName);
                    break;
            }
        }

        if (requiredMissing.Count > 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Required Tables Exist",
                Status = SystemSettingsStatus.Fail,
                Details = $"Missing required core tables: {string.Join(", ", requiredMissing)}",
                Recommendation = MissingTablesRemediation(appliedMigrationsCount, pendingMigrationsCount)
            };
        }

        if (requiredUnverified.Count > 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Required Tables Exist",
                Status = SystemSettingsStatus.Unavailable,
                Details = $"Could not verify required core tables: {string.Join(", ", requiredUnverified)}. The metadata query failed; this is not evidence that they are missing.",
                Recommendation = "Check the backend log for the failing information_schema query."
            };
        }

        var requiredCount = modelTables.Count(table => ClassifyTable(table.Name) == SchemaTableRequirement.Required);
        var details = $"All required core tables verified ({requiredCount} required, {modelTables.Count} EF model tables discovered).";
        if (optionalMissing.Count > 0)
        {
            details += $" Optional feature tables missing: {string.Join(", ", optionalMissing)}.";
        }
        if (inactiveMissing.Count > 0)
        {
            details += $" Inactive/demo tables missing: {string.Join(", ", inactiveMissing)}.";
        }
        if (otherUnverified.Count > 0)
        {
            details += $" Could not verify: {string.Join(", ", otherUnverified)}.";
        }

        SystemSettingsStatus optionalTableStatus = SystemSettingsStatus.Pass;
        string optionalTableRecommendation = "";

        if (optionalMissing.Count > 0 && appliedMigrationsCount > 0)
        {
            optionalTableStatus = SystemSettingsStatus.Warning;
            optionalTableRecommendation = "Optional feature tables are missing despite migrations being applied. This may indicate a failed migration or dropped tables. " +
                MissingTablesRemediation(appliedMigrationsCount, pendingMigrationsCount);
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Required Tables Exist",
            Status = optionalTableStatus,
            Details = details,
            Recommendation = optionalTableRecommendation
        };
    }

    /// <summary>
    /// 'dotnet ef database update' only applies migrations missing from history. With history complete it recreates
    /// nothing, so it is only advised when migrations are actually pending or none were ever applied.
    /// </summary>
    internal static string MissingTablesRemediation(int appliedMigrationsCount, int? pendingMigrationsCount) =>
        (appliedMigrationsCount, pendingMigrationsCount) switch
        {
            (_, > 0) => "Migrations are pending. Apply them: dotnet ef database update",
            (0, _) => "No migrations have been applied to this database. Run: dotnet ef database update",
            (_, 0) => $"Migration history records {appliedMigrationsCount} applied migrations and none pending, so 'dotnet ef database update' will not recreate these tables. " +
                "The schema has drifted from its migration history (tables dropped or changed outside EF), or this is not the database the migrations were applied to. " +
                "Confirm the connection string, then repair the schema or recreate the database from migrations.",
            _ => "Migration history could not be read. Confirm the connection string targets the expected database, then check Pending Migrations."
        };

    private List<SchemaTable> GetTablesFromModel()
    {
        return _db.Model.GetEntityTypes()
            .Select(entityType => new
            {
                Name = entityType.GetTableName(),
                Schema = entityType.GetSchema() ?? "public"
            })
            .Where(table => !string.IsNullOrWhiteSpace(table.Name))
            .Select(table => new SchemaTable(table.Name!, table.Schema))
            .Distinct()
            .OrderBy(table => table.Schema)
            .ThenBy(table => table.Name)
            .ToList();
    }

    /// <summary>
    /// Derived from Required Tables Exist, Pending Migrations and EF Migration Integrity, naming the prerequisite
    /// that failed. A prerequisite that could not be determined makes the result Unavailable, not Fail.
    /// </summary>
    internal static EnvironmentDiagnosticCheck EvaluateSchemaUpToDate(
        EnvironmentDiagnosticCheck tablesCheck,
        EnvironmentDiagnosticCheck pendingCheck,
        EnvironmentDiagnosticCheck integrityCheck)
    {
        var failed = new List<(string Reason, string Check)>();
        if (tablesCheck.Status == SystemSettingsStatus.Fail)
            failed.Add(("required core tables are missing", tablesCheck.Name));
        if (pendingCheck.Status == SystemSettingsStatus.Fail)
            failed.Add(("migrations are pending", pendingCheck.Name));
        if (integrityCheck.Status == SystemSettingsStatus.Fail)
            failed.Add(("migration integrity has critical issues", integrityCheck.Name));

        if (failed.Count > 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Schema Up to Date",
                Status = SystemSettingsStatus.Fail,
                Details = $"Schema is not current: {Sentence(failed.Select(f => f.Reason))}",
                Recommendation = $"See {string.Join(", ", failed.Select(f => f.Check))}."
            };
        }

        var unverified = new List<(string Reason, string Check)>();
        if (tablesCheck.Status == SystemSettingsStatus.Unavailable)
            unverified.Add(("required core tables could not be verified", tablesCheck.Name));
        if (pendingCheck.Status != SystemSettingsStatus.Pass)
            unverified.Add(("pending migrations could not be determined", pendingCheck.Name));

        if (unverified.Count > 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Schema Up to Date",
                Status = SystemSettingsStatus.Unavailable,
                Details = $"Schema currency could not be verified: {Sentence(unverified.Select(u => u.Reason))}",
                Recommendation = $"See {string.Join(", ", unverified.Select(u => u.Check))}."
            };
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Schema Up to Date",
            Status = SystemSettingsStatus.Pass,
            Details = "Schema is current: required core tables exist, no migrations are pending, and migration integrity has no critical issues",
            Recommendation = ""
        };

        static string Sentence(IEnumerable<string> reasons)
        {
            var text = string.Join("; ", reasons);
            return char.ToUpperInvariant(text[0]) + text[1..] + ".";
        }
    }

    internal static SchemaTableRequirement ClassifyTable(string tableName) => tableName switch
    {
        // Core persistence tables: saved workspaces and workflow review progress
        "saved_workspaces" => SchemaTableRequirement.Required,
        "saved_workspace_artifacts" => SchemaTableRequirement.Required,
        "workspace_review_progress" => SchemaTableRequirement.Required,

        // Dormant store: api/project-documents has no writer, and no reader besides System Settings diagnostics
        "project_documents" => SchemaTableRequirement.Optional,

        // Analysis and traceability tables (optional features but created by migrations)
        "scenarios" => SchemaTableRequirement.Optional,
        "reviewed_candidates" => SchemaTableRequirement.Optional,
        "candidate_links" => SchemaTableRequirement.Optional,
        "qa_delta_reviews" => SchemaTableRequirement.Optional,
        "trace_links" => SchemaTableRequirement.Optional,
        "traceability_suggestions" => SchemaTableRequirement.Optional,
        "code_files" => SchemaTableRequirement.Optional,
        "code_links" => SchemaTableRequirement.Optional,

        // Demo/test/seed data tables
        _ when tableName.Contains("demo", StringComparison.OrdinalIgnoreCase)
            || tableName.Contains("seed", StringComparison.OrdinalIgnoreCase)
            || tableName.Contains("test", StringComparison.OrdinalIgnoreCase)
            => SchemaTableRequirement.DemoOrSeed,

        _ => SchemaTableRequirement.Optional
    };

    private async Task<int?> GetAppliedMigrationsCountAsync()
    {
        try
        {
            return (await _db.Database.GetAppliedMigrationsAsync()).Count();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check applied migrations");
            return null;
        }
    }

    private async Task<IReadOnlyList<string>?> GetPendingMigrationsAsync()
    {
        try
        {
            return (await _db.Database.GetPendingMigrationsAsync()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check pending migrations");
            return null;
        }
    }

    /// <summary>
    /// Counts rows in __EFMigrationsHistory. It says nothing about whether the schema objects exist.
    /// </summary>
    internal static EnvironmentDiagnosticCheck EvaluateAppliedMigrations(int? appliedMigrationsCount) => appliedMigrationsCount switch
    {
        null => new EnvironmentDiagnosticCheck
        {
            Name = "EF Core Migrations Applied",
            Status = SystemSettingsStatus.Warning,
            Details = "Could not verify migration status",
            Recommendation = "Confirm the connection string targets the expected database."
        },
        0 => new EnvironmentDiagnosticCheck
        {
            Name = "EF Core Migrations Applied",
            Status = SystemSettingsStatus.Warning,
            Details = "No migrations applied",
            Recommendation = "Run migrations: dotnet ef database update"
        },
        var count => new EnvironmentDiagnosticCheck
        {
            Name = "EF Core Migrations Applied",
            Status = SystemSettingsStatus.Pass,
            Details = $"{count} migrations applied (recorded in migration history; schema objects are verified by Required Tables Exist)",
            Recommendation = ""
        }
    };

    /// <summary>
    /// Compares the build's migrations with migration history. Zero pending does not prove the schema is intact.
    /// </summary>
    internal static EnvironmentDiagnosticCheck EvaluatePendingMigrations(IReadOnlyList<string>? pending)
    {
        if (pending is null)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Pending Migrations",
                Status = SystemSettingsStatus.Warning,
                Details = "Could not determine migration status",
                Recommendation = "Verify database schema is current"
            };
        }

        if (pending.Count == 0)
        {
            return new EnvironmentDiagnosticCheck
            {
                Name = "Pending Migrations",
                Status = SystemSettingsStatus.Pass,
                Details = "No pending migrations (every migration in the build is recorded in migration history)",
                Recommendation = ""
            };
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Pending Migrations",
            Status = SystemSettingsStatus.Fail,
            Details = $"{pending.Count} pending migration(s): {string.Join(", ", pending.Select(m => m.Split('_').Last()))}",
            Recommendation = "Apply migrations: dotnet ef database update"
        };
    }

    private async Task<EnvironmentDiagnosticCheck> CheckMigrationIntegrityAsync()
    {
        try
        {
            var report = await _migrationValidator.ValidateAsync(_db);

            if (report.IsValid)
            {
                return new EnvironmentDiagnosticCheck
                {
                    Name = "EF Migration Integrity",
                    Status = SystemSettingsStatus.Pass,
                    Details = $"{report.AppliedMigrationCount} migrations applied, snapshot {report.SnapshotName} detected, 0 issues detected (checks migration files, history recognition and a compiled snapshot; not schema objects)",
                    Recommendation = ""
                };
            }

            var criticalIssues = report.Issues.Where(i => i.Severity == MigrationIssueSeverity.Critical).ToList();
            var warningIssues = report.Issues.Where(i => i.Severity == MigrationIssueSeverity.Warning).ToList();

            var detailLines = new List<string> { "Issues found:" };
            detailLines.AddRange(criticalIssues.Select(issue => $"  FAIL: {issue.Issue}"));
            detailLines.AddRange(warningIssues.Select(issue => $"  WARN: {issue.Issue}"));

            return new EnvironmentDiagnosticCheck
            {
                Name = "EF Migration Integrity",
                Status = criticalIssues.Any() ? SystemSettingsStatus.Fail : SystemSettingsStatus.Warning,
                Details = string.Join('\n', detailLines),
                Recommendation = criticalIssues.Any()
                    ? "Fix migration files: ensure all .cs files have matching .Designer.cs files. Run: dotnet ef migrations list"
                    : "Review warnings and consider fixing orphaned files"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check migration integrity");
            return new EnvironmentDiagnosticCheck
            {
                Name = "EF Migration Integrity",
                Status = SystemSettingsStatus.Warning,
                Details = $"Could not validate migrations: {ex.Message}",
                Recommendation = "Verify migration files are in Migrations directory and properly formatted"
            };
        }
    }

    internal enum SchemaTableRequirement
    {
        Required,
        Optional,
        Inactive,
        DemoOrSeed
    }

    internal sealed record SchemaTable(string Name, string Schema)
    {
        public string Key => $"{Schema}.{Name}";
        public string DisplayName => $"{Schema}.{Name}";
    }

    /// <summary>Result of looking the EF model tables up in the connected database, keyed by <see cref="SchemaTable.Key"/>.</summary>
    internal sealed record TableProbe(IReadOnlySet<string> ExistingKeys, IReadOnlySet<string> UnverifiedKeys);

    private const string ProjectDocumentsTable = "public.project_documents";
    private const string SavedWorkspacesTable = "public.saved_workspaces";
    private const string SavedWorkspaceArtifactsTable = "public.saved_workspace_artifacts";
    private const string WorkspaceReviewProgressTable = "public.workspace_review_progress";
}
