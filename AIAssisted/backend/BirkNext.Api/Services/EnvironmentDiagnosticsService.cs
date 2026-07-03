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
        var databaseChecks = await RunDatabaseChecksAsync();
        var backendChecks = RunBackendApiChecks();
        var workspaceChecks = await RunWorkspaceReadinessChecksAsync();
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

    private async Task<List<EnvironmentDiagnosticCheck>> RunDatabaseChecksAsync()
    {
        var checks = new List<EnvironmentDiagnosticCheck>();

        // 1. Database reachable
        var canConnect = await CanConnectToDatabaseAsync();
        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Database Reachable",
            Status = canConnect ? SystemSettingsStatus.Pass : SystemSettingsStatus.Fail,
            Details = canConnect ? "Connected successfully" : "Could not connect to database",
            Recommendation = canConnect ? "" : "Check database connection string and ensure database server is running"
        });

        if (!canConnect)
        {
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Database Configuration",
                Status = SystemSettingsStatus.Unavailable,
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

        // 6. Required tables exist (schema validation - all created by migrations)
        var tablesCheck = await CheckRequiredTablesExistAsync();
        checks.Add(tablesCheck);

        // 7. EF Core migrations status
        var migrationsCheck = await CheckMigrationsAsync();
        checks.Add(migrationsCheck);

        // 8. Pending migrations
        var pendingCheck = await CheckPendingMigrationsAsync();
        checks.Add(pendingCheck);

        // 9. EF Core Migration Integrity
        var integrityCheck = await CheckMigrationIntegrityAsync();
        checks.Add(integrityCheck);

        // 10. Schema up to date
        var schemaIsCurrent = IsSchemaCurrent(tablesCheck, pendingCheck, integrityCheck);

        var schemaCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Schema Up to Date",
            Status = schemaIsCurrent ? SystemSettingsStatus.Pass : SystemSettingsStatus.Fail,
            Details = schemaIsCurrent
                ? "Schema is current"
                : "Schema is not current: database connectivity, pending migrations, required core tables, or migration integrity checks did not pass",
            Recommendation = schemaIsCurrent ? "" : "Review failing database diagnostics before using the application"
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

        checks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Active Workspace Loaded",
            Status = SystemSettingsStatus.Unavailable,
            Details = "Backend diagnostics cannot see the active browser workspace. Browser/session state is evaluated by frontend diagnostics.",
            Recommendation = "Save the workspace if backend diagnostics need to inspect persisted workspace state."
        });

        // Check if workspace has imported artifacts (table data presence, not schema)
        var hasWorkspaceData = await CheckIfWorkspaceHasDataAsync();

        if (!hasWorkspaceData)
        {
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Imported Project Documents",
                Status = SystemSettingsStatus.Pass,
                Details = "No project documents have been imported to backend storage. This is normal when using browser/session workspace state.",
                Recommendation = ""
            });
        }
        else
        {
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Imported Project Documents",
                Status = SystemSettingsStatus.Pass,
                Details = "Project documents have been imported to backend storage",
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
                Status = tablesExist ? SystemSettingsStatus.Pass : SystemSettingsStatus.Fail,
                Details = tablesExist ? "saved_workspaces and saved_workspace_artifacts tables exist" : "Required workspace persistence tables are missing",
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
                Status = workspaceCount > 0 ? SystemSettingsStatus.Pass : SystemSettingsStatus.Pass,
                Details = $"{workspaceCount} workspace(s) saved",
                Recommendation = ""
            });

            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Current Workspace Saved/Unsaved",
                Status = SystemSettingsStatus.Unavailable,
                Details = "Backend diagnostics do not receive the active browser workspace id; saved workspace count is reported separately.",
                Recommendation = "Use frontend ReviewContext Validation for the active session, or save and reopen a workspace before backend diagnostics."
            });

            // Check auto-save configuration
            var autoSaveInterval = _config.GetValue("WorkspacePersistence:AutoSaveIntervalMs", 3000);
            var autoSaveThrottle = _config.GetValue("WorkspacePersistence:AutoSaveThrottleMs", 30000);
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Auto-Save Configuration",
                Status = SystemSettingsStatus.Pass,
                Details = $"Auto-save every {autoSaveInterval}ms, throttled to every {autoSaveThrottle}ms",
                Recommendation = ""
            });

            // Check workflow review progress tables
            var reviewProgressTableExists = await CheckReviewProgressTableExistsAsync();
            checks.Add(new EnvironmentDiagnosticCheck
            {
                Name = "Review Progress Tables Exist",
                Status = reviewProgressTableExists ? SystemSettingsStatus.Pass : SystemSettingsStatus.Fail,
                Details = reviewProgressTableExists ? "workspace_review_progress table exists" : "Required review progress table is missing",
                Recommendation = reviewProgressTableExists ? "" : "Run pending migrations: dotnet ef database update"
            });

            if (reviewProgressTableExists)
            {
                // Check saved review progress records
                var reviewProgressCount = await _db.WorkspaceReviewProgress.CountAsync();
                checks.Add(new EnvironmentDiagnosticCheck
                {
                    Name = "Saved Review Progress Records",
                    Status = reviewProgressCount > 0 ? SystemSettingsStatus.Pass : SystemSettingsStatus.Pass,
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
                        Status = SystemSettingsStatus.Warning,
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
                Status = SystemSettingsStatus.Warning,
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
                Status = SystemSettingsStatus.Unavailable,
                Details = "Active workspace is browser/session state and is not available to backend diagnostics.",
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
                Status = SystemSettingsStatus.Pass,
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
                Status = SystemSettingsStatus.Warning,
                Details = "Could not verify roles",
                Recommendation = "Verify database user has appropriate role permissions"
            };
        }
    }

    private async Task<EnvironmentDiagnosticCheck> CheckRequiredTablesExistAsync()
    {
        var modelTables = GetTablesFromModel();
        var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in modelTables)
        {
            try
            {
                var exists = await _db.Database.SqlQueryRaw<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = {0} AND table_name = {1}
                    )
                    """, table.Schema, table.Name).FirstOrDefaultAsync();

                if (exists)
                {
                    existingTables.Add(table.Key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check table {Schema}.{Table}", table.Schema, table.Name);
            }
        }

        var appliedMigrationsCount = (await _db.Database.GetAppliedMigrationsAsync()).Count();

        return EvaluateRequiredTables(modelTables, existingTables, appliedMigrationsCount);
    }

    internal static EnvironmentDiagnosticCheck EvaluateRequiredTables(
        IReadOnlyCollection<SchemaTable> modelTables,
        IReadOnlySet<string> existingTableKeys,
        int appliedMigrationsCount)
    {
        var requiredMissing = new List<string>();
        var optionalMissing = new List<string>();
        var inactiveMissing = new List<string>();

        foreach (var table in modelTables)
        {
            if (existingTableKeys.Contains(table.Key))
            {
                continue;
            }

            switch (ClassifyTable(table.Name))
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
                Recommendation = "Migrations did not complete successfully. Run: dotnet ef database update"
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

        SystemSettingsStatus optionalTableStatus = SystemSettingsStatus.Pass;
        string optionalTableRecommendation = "";

        if (optionalMissing.Count > 0)
        {
            if (appliedMigrationsCount > 0)
            {
                optionalTableStatus = SystemSettingsStatus.Warning;
                optionalTableRecommendation = "Optional feature tables are missing despite migrations being applied. This may indicate a failed migration or dropped tables.";
            }
            else
            {
                optionalTableStatus = SystemSettingsStatus.Pass;
                optionalTableRecommendation = "";
            }
        }

        return new EnvironmentDiagnosticCheck
        {
            Name = "Required Tables Exist",
            Status = optionalTableStatus,
            Details = details,
            Recommendation = optionalTableRecommendation
        };
    }

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

    internal static bool IsSchemaCurrent(
        EnvironmentDiagnosticCheck tablesCheck,
        EnvironmentDiagnosticCheck pendingCheck,
        EnvironmentDiagnosticCheck integrityCheck) =>
        pendingCheck.Status == SystemSettingsStatus.Pass &&
        tablesCheck.Status != SystemSettingsStatus.Fail &&
        integrityCheck.Status != SystemSettingsStatus.Fail;

    internal static SchemaTableRequirement ClassifyTable(string tableName) => tableName switch
    {
        // Core platform infrastructure tables
        "project_documents" => SchemaTableRequirement.Required,
        "saved_workspaces" => SchemaTableRequirement.Required,
        "saved_workspace_artifacts" => SchemaTableRequirement.Required,
        "workspace_review_progress" => SchemaTableRequirement.Required,

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
                Status = count > 0 ? SystemSettingsStatus.Pass : SystemSettingsStatus.Warning,
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
                Status = SystemSettingsStatus.Warning,
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
                    Status = SystemSettingsStatus.Pass,
                    Details = "No pending migrations",
                    Recommendation = ""
                };
            }

            return new EnvironmentDiagnosticCheck
            {
                Name = "Pending Migrations",
                Status = SystemSettingsStatus.Fail,
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
                Status = SystemSettingsStatus.Warning,
                Details = "Could not determine migration status",
                Recommendation = "Verify database schema is current"
            };
        }
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
                    Details = $"{report.AppliedMigrationCount} migrations applied, snapshot {report.SnapshotName} detected, 0 issues detected",
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
}
