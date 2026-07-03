using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

/// <summary>
/// Shared status calculation engine used by all System Settings pages.
/// Ensures consistent PASS/WARNING/FAIL/UNAVAILABLE classification across the entire subsystem.
/// </summary>
public interface ISystemSettingsStatusEngine
{
    /// <summary>
    /// Calculate overall status from a collection of individual statuses.
    /// Hierarchy: FAIL > WARNING > PASS (UNAVAILABLE treated as WARNING)
    /// </summary>
    SystemSettingsStatus CalculateOverallStatus(params SystemSettingsStatus[] statuses);

    /// <summary>
    /// Calculate overall status from a collection of items.
    /// </summary>
    SystemSettingsStatus CalculateOverallStatus(IEnumerable<SettingsItem> items);

    /// <summary>
    /// Calculate overall status from multiple sections.
    /// </summary>
    SystemSettingsStatus CalculateOverallStatus(IEnumerable<SettingsSection> sections);

    /// <summary>
    /// Summarize statuses into counts.
    /// </summary>
    StatusSummary SummarizeStatuses(IEnumerable<SystemSettingsStatus> statuses);

    /// <summary>
    /// Create a passing item.
    /// </summary>
    SettingsItem CreatePassItem(string name, string value, string description, string? recommendation = null);

    /// <summary>
    /// Create a warning item.
    /// </summary>
    SettingsItem CreateWarningItem(string name, string value, string description, string? recommendation = null, bool isRequired = false);

    /// <summary>
    /// Create a fail item.
    /// </summary>
    SettingsItem CreateFailItem(string name, string value, string description, string? recommendation = null, bool isRequired = true);

    /// <summary>
    /// Create an unavailable item.
    /// </summary>
    SettingsItem CreateUnavailableItem(string name, string description, string? recommendation = null);
}

public class SystemSettingsStatusEngine : ISystemSettingsStatusEngine
{
    public SystemSettingsStatus CalculateOverallStatus(params SystemSettingsStatus[] statuses)
    {
        return CalculateOverallStatus(statuses.AsEnumerable());
    }

    public SystemSettingsStatus CalculateOverallStatus(IEnumerable<SettingsItem> items)
    {
        return CalculateOverallStatus(items.Select(i => i.Status));
    }

    public SystemSettingsStatus CalculateOverallStatus(IEnumerable<SettingsSection> sections)
    {
        var allStatuses = sections
            .SelectMany(s => s.Items)
            .Select(i => i.Status)
            .ToList();

        return CalculateOverallStatus(allStatuses);
    }

    public StatusSummary SummarizeStatuses(IEnumerable<SystemSettingsStatus> statuses)
    {
        var summary = new StatusSummary();

        foreach (var status in statuses)
        {
            summary.AddStatus(status);
        }

        return summary;
    }

    public SettingsItem CreatePassItem(string name, string value, string description, string? recommendation = null)
    {
        return new SettingsItem
        {
            Name = name,
            Value = value,
            Status = SystemSettingsStatus.Pass,
            Description = description,
            Recommendation = recommendation,
            IsRequired = true
        };
    }

    public SettingsItem CreateWarningItem(string name, string value, string description, string? recommendation = null, bool isRequired = false)
    {
        return new SettingsItem
        {
            Name = name,
            Value = value,
            Status = SystemSettingsStatus.Warning,
            Description = description,
            Recommendation = recommendation,
            IsRequired = isRequired
        };
    }

    public SettingsItem CreateFailItem(string name, string value, string description, string? recommendation = null, bool isRequired = true)
    {
        return new SettingsItem
        {
            Name = name,
            Value = value,
            Status = SystemSettingsStatus.Fail,
            Description = description,
            Recommendation = recommendation,
            IsRequired = isRequired
        };
    }

    public SettingsItem CreateUnavailableItem(string name, string description, string? recommendation = null)
    {
        return new SettingsItem
        {
            Name = name,
            Value = "Not Available",
            Status = SystemSettingsStatus.Unavailable,
            Description = description,
            Recommendation = recommendation,
            IsRequired = false
        };
    }

    private SystemSettingsStatus CalculateOverallStatus(IEnumerable<SystemSettingsStatus> statuses)
    {
        var statusList = statuses.ToList();

        // FAIL is worst
        if (statusList.Any(s => s == SystemSettingsStatus.Fail))
            return SystemSettingsStatus.Fail;

        // WARNING is second worst (UNAVAILABLE counts as warning)
        if (statusList.Any(s => s == SystemSettingsStatus.Warning || s == SystemSettingsStatus.Unavailable))
            return SystemSettingsStatus.Warning;

        // All good
        return SystemSettingsStatus.Pass;
    }
}
