using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit;

public sealed class EnvironmentDiagnosticsSeverityTests
{
    [Fact]
    public void OptionalTableMissing_DoesNotFailRequiredTablesOrSchema()
    {
        var tables = Tables("project_documents", "saved_workspaces", "saved_workspace_artifacts", "workspace_review_progress", "scenarios");
        var existing = Keys("project_documents", "saved_workspaces", "saved_workspace_artifacts", "workspace_review_progress");

        var requiredTables = EnvironmentDiagnosticsService.EvaluateRequiredTables(tables, existing, appliedMigrationsCount: 1);
        var schemaCurrent = EnvironmentDiagnosticsService.IsSchemaCurrent(
            requiredTables,
            Check("Pending Migrations", EnvironmentDiagnosticStatus.Pass),
            Check("EF Migration Integrity", EnvironmentDiagnosticStatus.Pass));

        requiredTables.Status.Should().Be(EnvironmentDiagnosticStatus.Warning);
        requiredTables.Details.Should().Contain("Optional feature tables missing");
        schemaCurrent.Should().BeTrue();
    }

    [Fact]
    public void RequiredTableMissing_FailsRequiredTablesAndSchema()
    {
        var tables = Tables("project_documents", "saved_workspaces", "saved_workspace_artifacts", "workspace_review_progress", "scenarios");
        var existing = Keys("project_documents", "saved_workspaces", "workspace_review_progress", "scenarios");

        var requiredTables = EnvironmentDiagnosticsService.EvaluateRequiredTables(tables, existing, appliedMigrationsCount: 1);
        var schemaCurrent = EnvironmentDiagnosticsService.IsSchemaCurrent(
            requiredTables,
            Check("Pending Migrations", EnvironmentDiagnosticStatus.Pass),
            Check("EF Migration Integrity", EnvironmentDiagnosticStatus.Pass));

        requiredTables.Status.Should().Be(EnvironmentDiagnosticStatus.Fail);
        requiredTables.Details.Should().Contain("saved_workspace_artifacts");
        schemaCurrent.Should().BeFalse();
    }

    [Fact]
    public void InactiveOrDemoTableMissing_DoesNotFailRequiredTables()
    {
        var tables = Tables("project_documents", "saved_workspaces", "saved_workspace_artifacts", "workspace_review_progress", "demo_seed_samples");
        var existing = Keys("project_documents", "saved_workspaces", "saved_workspace_artifacts", "workspace_review_progress");

        var requiredTables = EnvironmentDiagnosticsService.EvaluateRequiredTables(tables, existing, appliedMigrationsCount: 0);

        requiredTables.Status.Should().Be(EnvironmentDiagnosticStatus.Pass);
        requiredTables.Details.Should().Contain("Inactive/demo tables missing");
    }

    [Fact]
    public void NoSavedWorkspace_MakesReviewContextInfoNotFail()
    {
        var check = EnvironmentDiagnosticsService.EvaluateSavedWorkspaceReviewContext(0, 0);

        check.Status.Should().Be(EnvironmentDiagnosticStatus.Info);
        check.Details.Should().Contain("persisted workspaces");
    }

    [Fact]
    public void SavedCompleteWorkspace_MakesReviewContextSourcePass()
    {
        var check = EnvironmentDiagnosticsService.EvaluateSavedWorkspaceReviewContext(2, 1);

        check.Status.Should().Be(EnvironmentDiagnosticStatus.Pass);
        check.Details.Should().Contain("reconstruct ReviewContext");
    }

    private static EnvironmentDiagnosticCheck Check(string name, EnvironmentDiagnosticStatus status) =>
        new()
        {
            Name = name,
            Status = status,
            Details = "",
            Recommendation = ""
        };

    private static List<EnvironmentDiagnosticsService.SchemaTable> Tables(params string[] names) =>
        names.Select(name => new EnvironmentDiagnosticsService.SchemaTable(name, "public")).ToList();

    private static HashSet<string> Keys(params string[] names) =>
        names.Select(name => $"public.{name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
}
