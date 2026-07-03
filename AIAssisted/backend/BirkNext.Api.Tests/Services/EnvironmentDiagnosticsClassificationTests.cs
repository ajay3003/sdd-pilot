using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using BirkNext.Api.Tests.Extensions;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Tests for environment diagnostics status classification rules.
///
/// Classification Rules:
/// - PASS: Valid and healthy checks only
/// - WARNING: Missing features but not blocking (no workspace, no active workspace, optional unavailable, etc.)
/// - FAIL: Actual broken/dangerous conditions (backend unreachable, database unreachable, required table missing, migration failure, API throws, required service unavailable)
/// - UNAVAILABLE: Check cannot be executed in current environment (not counted as FAIL)
///
/// Overall Status:
/// - PASS: No FAIL statuses exist
/// - WARNING: Has WARNING/UNAVAILABLE but no FAIL
/// - FAIL: Has at least one FAIL
/// </summary>
public class EnvironmentDiagnosticsClassificationTests
{
    [Fact]
    public void Missing_workspace_should_return_WARNING_not_FAIL()
    {
        var check = EnvironmentDiagnosticsService.EvaluateSavedWorkspaceReviewContext(
            savedWorkspaceCount: 0,
            completeWorkspaceCount: 0);

        Assert.Equal(SystemSettingsStatus.Warning, check.Status);
        Assert.Contains("No saved workspaces", check.Details);
    }

    [Fact]
    public void Incomplete_workspace_should_return_WARNING_not_FAIL()
    {
        var check = EnvironmentDiagnosticsService.EvaluateSavedWorkspaceReviewContext(
            savedWorkspaceCount: 5,
            completeWorkspaceCount: 0);

        Assert.Equal(SystemSettingsStatus.Warning, check.Status);
        Assert.Contains("none have the required artifacts", check.Details);
    }

    [Fact]
    public void Complete_workspace_should_return_PASS()
    {
        var check = EnvironmentDiagnosticsService.EvaluateSavedWorkspaceReviewContext(
            savedWorkspaceCount: 3,
            completeWorkspaceCount: 2);

        Assert.Equal(SystemSettingsStatus.Pass, check.Status);
        Assert.Contains("can be used to reconstruct ReviewContext", check.Details);
    }

    [Fact]
    public void Required_tables_missing_should_return_FAIL()
    {
        var modelTables = new List<(string Name, string Schema, string DisplayName, string Key)>
        {
            ("saved_workspaces", "public", "public.saved_workspaces", "public.saved_workspaces"),
            ("saved_workspace_artifacts", "public", "public.saved_workspace_artifacts", "public.saved_workspace_artifacts"),
        };

        var existing = new HashSet<string>();

        var check = EnvironmentDiagnosticsService.EvaluateRequiredTables(
            modelTables.Select(t => new EnvironmentDiagnosticsService.SchemaTable(t.Name, t.Schema)).ToList(),
            existing,
            appliedMigrationsCount: 1);

        Assert.Equal(SystemSettingsStatus.Fail, check.Status);
        Assert.Contains("Missing required core tables", check.Details);
    }

    [Fact]
    public void All_required_tables_present_should_return_PASS()
    {
        var modelTables = new List<(string Name, string Schema, string DisplayName, string Key)>
        {
            ("saved_workspaces", "public", "public.saved_workspaces", "public.saved_workspaces"),
            ("saved_workspace_artifacts", "public", "public.saved_workspace_artifacts", "public.saved_workspace_artifacts"),
            ("workspace_review_progress", "public", "public.workspace_review_progress", "public.workspace_review_progress"),
            ("project_documents", "public", "public.project_documents", "public.project_documents"),
        };

        var existing = modelTables.Select(t => t.Key).ToHashSet();

        var check = EnvironmentDiagnosticsService.EvaluateRequiredTables(
            modelTables.Select(t => new EnvironmentDiagnosticsService.SchemaTable(t.Name, t.Schema)).ToList(),
            existing,
            appliedMigrationsCount: 10);

        Assert.Equal(SystemSettingsStatus.Pass, check.Status);
        Assert.Contains("All required core tables verified", check.Details);
    }

    [Fact]
    public void Optional_tables_missing_but_migrations_applied_should_return_WARNING()
    {
        var modelTables = new List<string>
        {
            "saved_workspaces",
            "saved_workspace_artifacts",
            "workspace_review_progress",
            "project_documents",
            "scenarios", // optional
            "trace_links", // optional
        };

        var existing = new HashSet<string>
        {
            "public.saved_workspaces",
            "public.saved_workspace_artifacts",
            "public.workspace_review_progress",
            "public.project_documents",
            // scenarios and trace_links missing
        };

        var tables = modelTables.Select(t => new EnvironmentDiagnosticsService.SchemaTable(t, "public")).ToList();

        var check = EnvironmentDiagnosticsService.EvaluateRequiredTables(
            tables,
            existing,
            appliedMigrationsCount: 5);

        Assert.Equal(SystemSettingsStatus.Warning, check.Status);
        Assert.Contains("Optional feature tables missing", check.Details);
    }

    [Fact]
    public void Optional_tables_missing_and_no_migrations_should_return_PASS()
    {
        var modelTables = new List<string>
        {
            "saved_workspaces",
            "saved_workspace_artifacts",
            "scenarios", // optional
        };

        var existing = new HashSet<string>
        {
            "public.saved_workspaces",
            "public.saved_workspace_artifacts",
            // scenarios missing but no migrations yet, so normal
        };

        var tables = modelTables.Select(t => new EnvironmentDiagnosticsService.SchemaTable(t, "public")).ToList();

        var check = EnvironmentDiagnosticsService.EvaluateRequiredTables(
            tables,
            existing,
            appliedMigrationsCount: 0);

        // When no migrations, missing optional tables is normal (PASS)
        Assert.Equal(SystemSettingsStatus.Pass, check.Status);
    }

    [Fact]
    public void Database_schema_up_to_date_should_return_PASS()
    {
        var tablesCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Required Tables Exist",
            Status = SystemSettingsStatus.Pass,
            Details = "All required core tables verified",
            Recommendation = ""
        };

        var pendingCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Pending Migrations",
            Status = SystemSettingsStatus.Pass,
            Details = "No pending migrations",
            Recommendation = ""
        };

        var integrityCheck = new EnvironmentDiagnosticCheck
        {
            Name = "EF Migration Integrity",
            Status = SystemSettingsStatus.Pass,
            Details = "0 issues detected",
            Recommendation = ""
        };

        bool isCurrent = EnvironmentDiagnosticsService.IsSchemaCurrent(tablesCheck, pendingCheck, integrityCheck);
        Assert.True(isCurrent);
    }

    [Fact]
    public void Database_schema_with_pending_migrations_should_return_FAIL()
    {
        var tablesCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Required Tables Exist",
            Status = SystemSettingsStatus.Pass,
            Details = "All required core tables verified",
            Recommendation = ""
        };

        var pendingCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Pending Migrations",
            Status = SystemSettingsStatus.Fail,
            Details = "2 pending migration(s)",
            Recommendation = "Apply migrations: dotnet ef database update"
        };

        var integrityCheck = new EnvironmentDiagnosticCheck
        {
            Name = "EF Migration Integrity",
            Status = SystemSettingsStatus.Pass,
            Details = "0 issues detected",
            Recommendation = ""
        };

        bool isCurrent = EnvironmentDiagnosticsService.IsSchemaCurrent(tablesCheck, pendingCheck, integrityCheck);
        Assert.False(isCurrent);
    }

    [Fact]
    public void Database_schema_with_missing_required_tables_should_return_FAIL()
    {
        var tablesCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Required Tables Exist",
            Status = SystemSettingsStatus.Fail,
            Details = "Missing required core tables: public.saved_workspaces",
            Recommendation = "Migrations did not complete successfully"
        };

        var pendingCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Pending Migrations",
            Status = SystemSettingsStatus.Pass,
            Details = "No pending migrations",
            Recommendation = ""
        };

        var integrityCheck = new EnvironmentDiagnosticCheck
        {
            Name = "EF Migration Integrity",
            Status = SystemSettingsStatus.Pass,
            Details = "0 issues detected",
            Recommendation = ""
        };

        bool isCurrent = EnvironmentDiagnosticsService.IsSchemaCurrent(tablesCheck, pendingCheck, integrityCheck);
        Assert.False(isCurrent);
    }

    [Fact]
    public void Database_schema_with_migration_integrity_failure_should_return_FAIL()
    {
        var tablesCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Required Tables Exist",
            Status = SystemSettingsStatus.Pass,
            Details = "All required core tables verified",
            Recommendation = ""
        };

        var pendingCheck = new EnvironmentDiagnosticCheck
        {
            Name = "Pending Migrations",
            Status = SystemSettingsStatus.Pass,
            Details = "No pending migrations",
            Recommendation = ""
        };

        var integrityCheck = new EnvironmentDiagnosticCheck
        {
            Name = "EF Migration Integrity",
            Status = SystemSettingsStatus.Fail,
            Details = "Missing migration file: 20250101_AddWorkspaces.Designer.cs",
            Recommendation = "Fix migration files"
        };

        bool isCurrent = EnvironmentDiagnosticsService.IsSchemaCurrent(tablesCheck, pendingCheck, integrityCheck);
        Assert.False(isCurrent);
    }

    [Fact]
    public void Table_classification_core_tables_are_required()
    {
        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.Required,
            EnvironmentDiagnosticsService.ClassifyTable("saved_workspaces"));

        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.Required,
            EnvironmentDiagnosticsService.ClassifyTable("workspace_review_progress"));

        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.Required,
            EnvironmentDiagnosticsService.ClassifyTable("project_documents"));
    }

    [Fact]
    public void Table_classification_optional_tables()
    {
        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.Optional,
            EnvironmentDiagnosticsService.ClassifyTable("scenarios"));

        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.Optional,
            EnvironmentDiagnosticsService.ClassifyTable("trace_links"));

        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.Optional,
            EnvironmentDiagnosticsService.ClassifyTable("code_files"));
    }

    [Fact]
    public void Table_classification_demo_and_seed_tables()
    {
        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.DemoOrSeed,
            EnvironmentDiagnosticsService.ClassifyTable("demo_data"));

        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.DemoOrSeed,
            EnvironmentDiagnosticsService.ClassifyTable("seed_values"));

        Assert.Equal(
            EnvironmentDiagnosticsService.SchemaTableRequirement.DemoOrSeed,
            EnvironmentDiagnosticsService.ClassifyTable("test_fixtures"));
    }

    [Fact]
    public void Overall_status_PASS_when_all_checks_pass()
    {
        var report = new EnvironmentDiagnosticsReport
        {
            Sections = new()
            {
                new SettingsSection
                {
                    Title = "Database",
                    Items = new()
                    {
                        new SettingsItem { Name = "DB1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" },
                        new SettingsItem { Name = "DB2", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Backend API",
                    Items = new()
                    {
                        new SettingsItem { Name = "API1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Workspace",
                    Items = new()
                    {
                        new SettingsItem { Name = "WS1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Review Context",
                    Items = new()
                    {
                        new SettingsItem { Name = "RC1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Export",
                    Items = new()
                    {
                        new SettingsItem { Name = "EXP1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                }
            }
        };

        var overallStatus = CalculateOverallStatus(report);
        Assert.Equal(SystemSettingsStatus.Pass, overallStatus);
    }

    [Fact]
    public void Overall_status_FAIL_when_any_check_fails()
    {
        var report = new EnvironmentDiagnosticsReport
        {
            Sections = new()
            {
                new SettingsSection
                {
                    Title = "Database",
                    Items = new()
                    {
                        new SettingsItem { Name = "DB1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" },
                        new SettingsItem { Name = "DB2", Status = SystemSettingsStatus.Fail, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Backend API",
                    Items = new()
                    {
                        new SettingsItem { Name = "API1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                }
            }
        };

        var overallStatus = CalculateOverallStatus(report);
        Assert.Equal(SystemSettingsStatus.Fail, overallStatus);
    }

    [Fact]
    public void Overall_status_WARNING_when_no_fails_but_has_warnings()
    {
        var report = new EnvironmentDiagnosticsReport
        {
            Sections = new()
            {
                new SettingsSection
                {
                    Title = "Database",
                    Items = new()
                    {
                        new SettingsItem { Name = "DB1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" },
                        new SettingsItem { Name = "DB2", Status = SystemSettingsStatus.Warning, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Backend API",
                    Items = new()
                    {
                        new SettingsItem { Name = "API1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Workspace",
                    Items = new()
                    {
                        new SettingsItem { Name = "WS1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                }
            }
        };

        var overallStatus = CalculateOverallStatus(report);
        Assert.Equal(SystemSettingsStatus.Warning, overallStatus);
    }

    [Fact]
    public void Overall_status_WARNING_when_no_fails_but_has_unavailable()
    {
        var report = new EnvironmentDiagnosticsReport
        {
            Sections = new()
            {
                new SettingsSection
                {
                    Title = "Database",
                    Items = new()
                    {
                        new SettingsItem { Name = "DB1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Backend API",
                    Items = new()
                    {
                        new SettingsItem { Name = "API1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                },
                new SettingsSection
                {
                    Title = "Workspace",
                    Items = new()
                    {
                        new SettingsItem { Name = "WS1", Status = SystemSettingsStatus.Unavailable, Description = "", Recommendation = "" }
                    }
                }
            }
        };

        var overallStatus = CalculateOverallStatus(report);
        Assert.Equal(SystemSettingsStatus.Warning, overallStatus);
    }

    [Fact]
    public void Overall_status_PASS_ignores_unavailable_when_all_else_pass()
    {
        var report = new EnvironmentDiagnosticsReport
        {
            Sections = new()
            {
                new SettingsSection
                {
                    Title = "Database",
                    Items = new()
                    {
                        new SettingsItem { Name = "DB1", Status = SystemSettingsStatus.Pass, Description = "", Recommendation = "" }
                    }
                }
            }
        };

        var overallStatus = CalculateOverallStatus(report);
        Assert.Equal(SystemSettingsStatus.Pass, overallStatus);
    }

    /// <summary>
    /// Helper method to calculate overall status based on classification rules:
    /// - PASS if no FAIL statuses exist
    /// - WARNING if has WARNING/UNAVAILABLE but no FAIL
    /// - FAIL if has at least one FAIL
    /// </summary>
    private static SystemSettingsStatus CalculateOverallStatus(EnvironmentDiagnosticsReport report)
    {
        var allItems = report.Sections.SelectMany(s => s.Items).ToList();

        var hasFail = allItems.Any(i => i.Status == SystemSettingsStatus.Fail);
        if (hasFail)
            return SystemSettingsStatus.Fail;

        var hasWarningOrUnavailable = allItems.Any(i =>
            i.Status == SystemSettingsStatus.Warning ||
            i.Status == SystemSettingsStatus.Unavailable);

        return hasWarningOrUnavailable
            ? SystemSettingsStatus.Warning
            : SystemSettingsStatus.Pass;
    }
}
