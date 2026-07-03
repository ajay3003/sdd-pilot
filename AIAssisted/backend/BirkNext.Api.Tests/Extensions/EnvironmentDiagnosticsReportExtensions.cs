using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Tests.Extensions;

/// <summary>
/// Extension methods to provide backward compatibility for test code.
/// These allow tests to work with the new Sections structure while accessing the old interface.
/// </summary>
public static class EnvironmentDiagnosticsReportExtensions
{
    public static List<EnvironmentDiagnosticCheck> GetDatabaseChecks(this EnvironmentDiagnosticsReport report)
    {
        var section = report.Sections.FirstOrDefault(s => s.Title.Contains("Database"));
        return section == null ? [] : ConvertItemsToChecks(section.Items);
    }

    public static List<EnvironmentDiagnosticCheck> GetBackendApiChecks(this EnvironmentDiagnosticsReport report)
    {
        var section = report.Sections.FirstOrDefault(s => s.Title.Contains("Backend"));
        return section == null ? [] : ConvertItemsToChecks(section.Items);
    }

    public static List<EnvironmentDiagnosticCheck> GetWorkspaceChecks(this EnvironmentDiagnosticsReport report)
    {
        var section = report.Sections.FirstOrDefault(s => s.Title.Contains("Workspace"));
        return section == null ? [] : ConvertItemsToChecks(section.Items);
    }

    public static List<EnvironmentDiagnosticCheck> GetReviewContextChecks(this EnvironmentDiagnosticsReport report)
    {
        var section = report.Sections.FirstOrDefault(s => s.Title.Contains("Review Context"));
        return section == null ? [] : ConvertItemsToChecks(section.Items);
    }

    public static List<EnvironmentDiagnosticCheck> GetExportChecks(this EnvironmentDiagnosticsReport report)
    {
        var section = report.Sections.FirstOrDefault(s => s.Title.Contains("Export"));
        return section == null ? [] : ConvertItemsToChecks(section.Items);
    }

    private static List<EnvironmentDiagnosticCheck> ConvertItemsToChecks(List<SettingsItem> items)
    {
        return items.Select(item => new EnvironmentDiagnosticCheck
        {
            Name = item.Name,
            Status = item.Status,
            Details = item.Description,
            Recommendation = item.Recommendation ?? "",
            TechnicalDetails = null
        }).ToList();
    }
}
