using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

internal static class DiagnosticPageServiceHelpers
{
    public static void ApplySectionStatuses(
        IEnumerable<SettingsSection> sections,
        ISystemSettingsStatusEngine statusEngine)
    {
        foreach (var section in sections)
        {
            section.Status = statusEngine.CalculateOverallStatus(section.Items);
        }
    }

    public static StatusSummary SummarizeSections(
        IEnumerable<SettingsSection> sections,
        ISystemSettingsStatusEngine statusEngine)
    {
        return statusEngine.SummarizeStatuses(
            sections.SelectMany(section => section.Items).Select(item => item.Status));
    }
}
