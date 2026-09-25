using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Pins what each Environment Diagnostics status means: backend-invisible browser state is Info (not evaluated),
/// query failures are Unavailable (never "missing"), and real schema/persistence defects stay Fail.
/// </summary>
public class EnvironmentDiagnosticsStatusSemanticsTests
{
    private static readonly SystemSettingsStatusEngine Engine = new();

    private static readonly string[] AllCoreTables =
    [
        "public.saved_workspaces",
        "public.saved_workspace_artifacts",
        "public.workspace_review_progress",
        "public.project_documents"
    ];

    // ---- Workspace: browser/session state the backend never receives ----

    [Fact]
    public void Active_workspace_loaded_is_not_evaluated_by_backend()
    {
        var check = EnvironmentDiagnosticsService.ActiveWorkspaceLoadedCheck();

        Assert.Equal(SystemSettingsStatus.Info, check.Status);
        Assert.StartsWith("Not evaluated by backend diagnostics", check.Details);
        Assert.Contains("Runtime Diagnostics", check.Details);
    }

    [Fact]
    public void Current_workspace_saved_is_not_evaluated_without_a_workspace_id()
    {
        var check = EnvironmentDiagnosticsService.CurrentWorkspaceSavedCheck();

        Assert.Equal(SystemSettingsStatus.Info, check.Status);
        Assert.Contains("no active browser workspace id", check.Details);
    }

    [Fact]
    public void Not_evaluated_rows_do_not_degrade_overall_status()
    {
        var summary = Summarize(
            Section("Database", Item("Required Tables Exist", SystemSettingsStatus.Pass)),
            Section("Workspace",
                Item("Active Workspace Loaded", SystemSettingsStatus.Info),
                Item("Current Workspace Saved/Unsaved", SystemSettingsStatus.Info),
                Item("Workspace Persistence Tables", SystemSettingsStatus.Pass)));

        Assert.Equal(SystemSettingsStatus.Pass, summary.OverallStatus);
        Assert.Equal(2, summary.PassCount);
        Assert.Equal(2, summary.InfoCount);
    }

    [Fact]
    public void Not_evaluated_rows_are_not_counted_as_unavailable()
    {
        var summary = Summarize(
            Section("Workspace",
                Item("Active Workspace Loaded", SystemSettingsStatus.Info),
                Item("Saved Workspaces", SystemSettingsStatus.Pass)));

        Assert.Equal(0, summary.UnavailableCount);
        Assert.Equal(0, summary.WarningCount);
        Assert.Equal(1, summary.InfoCount);
    }

    [Fact]
    public void Section_of_only_not_evaluated_rows_is_not_evaluated_not_pass()
    {
        var section = Section("ReviewContext", Item("ReviewContext Available", SystemSettingsStatus.Info));
        DiagnosticPageServiceHelpers.ApplySectionStatuses([section], Engine);

        Assert.Equal(SystemSettingsStatus.Info, section.Status);
        Assert.Equal(SystemSettingsStatus.Info, Engine.SummarizeStatuses([SystemSettingsStatus.Info]).OverallStatus);
    }

    [Fact]
    public void Real_schema_failure_still_fails_overall_beside_not_evaluated_rows()
    {
        var summary = Summarize(
            Section("Database", Item("Required Tables Exist", SystemSettingsStatus.Fail)),
            Section("Workspace", Item("Active Workspace Loaded", SystemSettingsStatus.Info)));

        Assert.Equal(SystemSettingsStatus.Fail, summary.OverallStatus);
    }

    [Fact]
    public void Unavailable_still_degrades_overall_to_warning()
    {
        var summary = Summarize(
            Section("Database", Item("Required Tables Exist", SystemSettingsStatus.Unavailable)),
            Section("Workspace", Item("Active Workspace Loaded", SystemSettingsStatus.Info)));

        Assert.Equal(SystemSettingsStatus.Warning, summary.OverallStatus);
    }

    // ---- Workspace persistence: a real backend capability ----

    [Fact]
    public void Workspace_persistence_tables_present_is_pass()
    {
        var check = EnvironmentDiagnosticsService.EvaluateWorkspacePersistenceTables(Keys(AllCoreTables), Keys());

        Assert.Equal(SystemSettingsStatus.Pass, check.Status);
        Assert.Contains("public.saved_workspaces and public.saved_workspace_artifacts exist", check.Details);
    }

    [Fact]
    public void Workspace_persistence_tables_missing_is_fail_naming_the_tables()
    {
        var check = EnvironmentDiagnosticsService.EvaluateWorkspacePersistenceTables(
            Keys("public.workspace_review_progress"), Keys());

        Assert.Equal(SystemSettingsStatus.Fail, check.Status);
        Assert.Contains("required tables are missing: public.saved_workspaces, public.saved_workspace_artifacts", check.Details);
        Assert.DoesNotContain("Could not check", check.Details);
    }

    [Fact]
    public void Workspace_persistence_table_lookup_failure_is_unavailable_not_missing()
    {
        var check = EnvironmentDiagnosticsService.EvaluateWorkspacePersistenceTables(
            Keys("public.saved_workspaces"), Keys("public.saved_workspace_artifacts"));

        Assert.Equal(SystemSettingsStatus.Unavailable, check.Status);
        Assert.Contains("Could not verify", check.Details);
    }

    [Fact]
    public void Workspace_persistence_does_not_depend_on_browser_context()
    {
        // The persistence verdict comes from the database alone; the not-evaluated browser rows are separate checks.
        var persistence = EnvironmentDiagnosticsService.EvaluateWorkspacePersistenceTables(Keys(AllCoreTables), Keys());
        var active = EnvironmentDiagnosticsService.ActiveWorkspaceLoadedCheck();

        Assert.Equal(SystemSettingsStatus.Pass, persistence.Status);
        Assert.Equal(SystemSettingsStatus.Info, active.Status);
    }

    [Fact]
    public void Review_progress_persistence_is_distinct_from_workspace_persistence()
    {
        var existing = Keys("public.workspace_review_progress");

        var workspace = EnvironmentDiagnosticsService.EvaluateWorkspacePersistenceTables(existing, Keys());
        var reviewProgress = EnvironmentDiagnosticsService.EvaluateReviewProgressTable(existing, Keys());

        Assert.Equal(SystemSettingsStatus.Fail, workspace.Status);
        Assert.Equal(SystemSettingsStatus.Pass, reviewProgress.Status);
        Assert.Contains("separate from saved workspaces", reviewProgress.Details);
    }

    [Fact]
    public void Auto_save_configuration_reports_values_without_claiming_storage_works()
    {
        var check = EnvironmentDiagnosticsService.AutoSaveConfigurationCheck(3000, 30000);

        Assert.Equal(SystemSettingsStatus.Info, check.Status);
        Assert.Contains("every 3000ms, throttled to every 30000ms", check.Details);
        Assert.Contains("do not verify auto-save storage", check.Details);
    }

    // ---- Database schema ----

    [Fact]
    public void Migrations_applied_and_required_tables_present_is_schema_up_to_date()
    {
        var tables = EvaluateTables(existing: AllCoreTables, unverified: [], applied: 16, pending: 0);
        var pending = EnvironmentDiagnosticsService.EvaluatePendingMigrations([]);
        var schema = EnvironmentDiagnosticsService.EvaluateSchemaUpToDate(tables, pending, Passing("EF Migration Integrity"));

        Assert.Equal(SystemSettingsStatus.Pass, tables.Status);
        Assert.Equal(SystemSettingsStatus.Pass, pending.Status);
        Assert.Equal(SystemSettingsStatus.Pass, schema.Status);
    }

    [Fact]
    public void Migrations_applied_but_required_tables_missing_fails_tables_and_schema()
    {
        var tables = EvaluateTables(existing: ["public.project_documents"], unverified: [], applied: 16, pending: 0);
        var pending = EnvironmentDiagnosticsService.EvaluatePendingMigrations([]);
        var schema = EnvironmentDiagnosticsService.EvaluateSchemaUpToDate(tables, pending, Passing("EF Migration Integrity"));

        Assert.Equal(SystemSettingsStatus.Fail, tables.Status);
        Assert.Equal(
            "Missing required core tables: public.saved_workspaces, public.saved_workspace_artifacts, public.workspace_review_progress",
            tables.Details);
        Assert.Equal(SystemSettingsStatus.Pass, pending.Status);
        Assert.Equal(SystemSettingsStatus.Fail, schema.Status);
        Assert.Equal("Schema is not current: Required core tables are missing.", schema.Details);
        Assert.Equal("See Required Tables Exist.", schema.Recommendation);
    }

    [Fact]
    public void Pending_migration_fails_pending_and_schema()
    {
        var tables = EvaluateTables(existing: AllCoreTables, unverified: [], applied: 15, pending: 1);
        var pending = EnvironmentDiagnosticsService.EvaluatePendingMigrations(["20260917100742_AddIntegrationQualitySnapshots"]);
        var schema = EnvironmentDiagnosticsService.EvaluateSchemaUpToDate(tables, pending, Passing("EF Migration Integrity"));

        Assert.Equal(SystemSettingsStatus.Fail, pending.Status);
        Assert.Equal(SystemSettingsStatus.Fail, schema.Status);
        Assert.Equal("Schema is not current: Migrations are pending.", schema.Details);
    }

    [Fact]
    public void Migration_integrity_issue_fails_schema_and_names_it()
    {
        var tables = EvaluateTables(existing: AllCoreTables, unverified: [], applied: 16, pending: 0);
        var integrity = new EnvironmentDiagnosticCheck { Name = "EF Migration Integrity", Status = SystemSettingsStatus.Fail };
        var schema = EnvironmentDiagnosticsService.EvaluateSchemaUpToDate(
            tables, EnvironmentDiagnosticsService.EvaluatePendingMigrations([]), integrity);

        Assert.Equal(SystemSettingsStatus.Fail, schema.Status);
        Assert.Equal("Schema is not current: Migration integrity has critical issues.", schema.Details);
        Assert.Equal("See EF Migration Integrity.", schema.Recommendation);
    }

    [Fact]
    public void Several_failed_prerequisites_are_all_named()
    {
        var tables = EvaluateTables(existing: [], unverified: [], applied: 15, pending: 1);
        var pending = EnvironmentDiagnosticsService.EvaluatePendingMigrations(["20260917100742_AddIntegrationQualitySnapshots"]);
        var schema = EnvironmentDiagnosticsService.EvaluateSchemaUpToDate(tables, pending, Passing("EF Migration Integrity"));

        Assert.Equal("Schema is not current: Required core tables are missing; migrations are pending.", schema.Details);
        Assert.Equal("See Required Tables Exist, Pending Migrations.", schema.Recommendation);
    }

    [Fact]
    public void Database_unreachable_fails_reachability_and_leaves_the_rest_unavailable()
    {
        var checks = EnvironmentDiagnosticsService.UnreachableDatabaseChecks();

        Assert.Equal(["Database Reachable", "Database Configuration"], checks.Select(c => c.Name));
        Assert.Equal(SystemSettingsStatus.Fail, checks[0].Status);
        Assert.Equal(SystemSettingsStatus.Unavailable, checks[1].Status);

        var summary = Summarize(Section("Database", checks.Select(ToItem).ToArray()));
        Assert.Equal(SystemSettingsStatus.Fail, summary.OverallStatus);
    }

    [Fact]
    public void Table_lookup_failure_is_unavailable_not_missing_and_schema_is_unverified()
    {
        // The pre-fix probe threw 42703 for every table and reported all of them missing.
        var tables = EvaluateTables(existing: [], unverified: AllCoreTables, applied: 16, pending: 0);
        var schema = EnvironmentDiagnosticsService.EvaluateSchemaUpToDate(
            tables, EnvironmentDiagnosticsService.EvaluatePendingMigrations([]), Passing("EF Migration Integrity"));

        Assert.Equal(SystemSettingsStatus.Unavailable, tables.Status);
        Assert.Contains("not evidence that they are missing", tables.Details);
        Assert.Equal(SystemSettingsStatus.Unavailable, schema.Status);
        Assert.Equal("Schema currency could not be verified: Required core tables could not be verified.", schema.Details);
    }

    [Fact]
    public void Pending_migrations_undeterminable_makes_schema_unverified_not_failed()
    {
        var tables = EvaluateTables(existing: AllCoreTables, unverified: [], applied: 16, pending: null);
        var pending = EnvironmentDiagnosticsService.EvaluatePendingMigrations(null);
        var schema = EnvironmentDiagnosticsService.EvaluateSchemaUpToDate(tables, pending, Passing("EF Migration Integrity"));

        Assert.Equal(SystemSettingsStatus.Unavailable, schema.Status);
        Assert.Contains("pending migrations could not be determined", schema.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applied_migrations_pass_describes_history_not_schema()
    {
        var check = EnvironmentDiagnosticsService.EvaluateAppliedMigrations(16);

        Assert.Equal(SystemSettingsStatus.Pass, check.Status);
        Assert.StartsWith("16 migrations applied", check.Details);
        Assert.Contains("schema objects are verified by Required Tables Exist", check.Details);
    }

    // ---- Remediation copy ----

    [Fact]
    public void Missing_tables_with_no_pending_migrations_does_not_prescribe_ef_update()
    {
        var tables = EvaluateTables(existing: [], unverified: [], applied: 16, pending: 0);

        Assert.Equal(
            "Migration history records 16 applied migrations and none pending, so 'dotnet ef database update' will not recreate these tables. " +
            "The schema has drifted from its migration history (tables dropped or changed outside EF), or this is not the database the migrations were applied to. " +
            "Confirm the connection string, then repair the schema or recreate the database from migrations.",
            tables.Recommendation);
        Assert.DoesNotContain("Run: dotnet ef database update", tables.Recommendation);
        Assert.DoesNotContain("Migrations did not complete successfully", tables.Recommendation);
    }

    [Theory]
    [InlineData(15, 1, "Migrations are pending. Apply them: dotnet ef database update")]
    [InlineData(0, 16, "Migrations are pending. Apply them: dotnet ef database update")]
    [InlineData(0, null, "No migrations have been applied to this database. Run: dotnet ef database update")]
    [InlineData(16, null, "Migration history could not be read. Confirm the connection string targets the expected database, then check Pending Migrations.")]
    public void Missing_tables_remediation_matches_migration_state(int applied, int? pending, string expected)
    {
        Assert.Equal(expected, EnvironmentDiagnosticsService.MissingTablesRemediation(applied, pending));
    }

    // ---- helpers ----

    private static EnvironmentDiagnosticCheck EvaluateTables(string[] existing, string[] unverified, int applied, int? pending)
    {
        var modelTables = AllCoreTables
            .Select(key => key.Split('.'))
            .Select(parts => new EnvironmentDiagnosticsService.SchemaTable(parts[1], parts[0]))
            .ToList();
        return EnvironmentDiagnosticsService.EvaluateRequiredTables(modelTables, Keys(existing), Keys(unverified), applied, pending);
    }

    private static EnvironmentDiagnosticCheck Passing(string name) =>
        new() { Name = name, Status = SystemSettingsStatus.Pass };

    private static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    private static StatusSummary Summarize(params SettingsSection[] sections)
    {
        DiagnosticPageServiceHelpers.ApplySectionStatuses(sections, Engine);
        return DiagnosticPageServiceHelpers.SummarizeSections(sections, Engine);
    }

    private static SettingsSection Section(string title, params SettingsItem[] items) =>
        new() { Title = title, Items = items.ToList() };

    private static SettingsItem Item(string name, SystemSettingsStatus status) =>
        new() { Name = name, Value = name, Description = name, Status = status };

    private static SettingsItem ToItem(EnvironmentDiagnosticCheck check) => Item(check.Name, check.Status);
}
