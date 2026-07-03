using System.Text.Json.Serialization;

namespace BirkNext.Api.Models.Admin;

/// <summary>
/// Shared status enum used across all System Settings pages.
/// Rules:
/// - PASS: correctly configured, healthy, expected
/// - WARNING: optional missing, default used, workspace not created, not configured
/// - FAIL: required missing, backend/database unavailable, migration failure
/// - UNAVAILABLE: cannot check in current environment (never counts as FAIL)
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemSettingsStatus
{
    Pass,
    Warning,
    Fail,
    Unavailable
}

/// <summary>
/// Single validated item in a settings section.
/// Every value shown in System Settings must be wrapped in this model.
/// </summary>
public class SettingsItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("status")]
    public SystemSettingsStatus Status { get; set; } = SystemSettingsStatus.Pass;

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; set; }

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; } = true;
}

/// <summary>
/// Structured diagnostic item shown in diagnostic pages.
/// </summary>
public class DiagnosticItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public SystemSettingsStatus Status { get; set; } = SystemSettingsStatus.Pass;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; set; }
}

/// <summary>
/// Grouped section of settings or diagnostics.
/// Every page should organize its content into sections.
/// </summary>
public class SettingsSection
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("status")]
    public SystemSettingsStatus Status { get; set; } = SystemSettingsStatus.Pass;

    [JsonPropertyName("items")]
    public List<SettingsItem> Items { get; set; } = new();

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; } = true;
}

/// <summary>
/// Summary counts for overall status calculation.
/// Used by every page to summarize its state.
/// </summary>
public class StatusSummary
{
    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("unavailableCount")]
    public int UnavailableCount { get; set; }

    /// <summary>
    /// Calculated overall status based on hierarchy:
    /// FAIL > WARNING > PASS. Empty summaries are unavailable.
    /// </summary>
    [JsonPropertyName("overallStatus")]
    public SystemSettingsStatus OverallStatus => CalculateOverallStatus();

    public void AddStatus(SystemSettingsStatus status)
    {
        switch (status)
        {
            case SystemSettingsStatus.Pass:
                PassCount++;
                break;
            case SystemSettingsStatus.Warning:
                WarningCount++;
                break;
            case SystemSettingsStatus.Fail:
                FailCount++;
                break;
            case SystemSettingsStatus.Unavailable:
                UnavailableCount++;
                break;
        }
    }

    private SystemSettingsStatus CalculateOverallStatus()
    {
        if (FailCount > 0)
            return SystemSettingsStatus.Fail;

        if (WarningCount > 0 || UnavailableCount > 0)
            return SystemSettingsStatus.Warning;

        if (PassCount == 0)
            return SystemSettingsStatus.Unavailable;

        return SystemSettingsStatus.Pass;
    }
}

/// <summary>
/// Base interface for all System Settings pages.
/// Every page must implement this contract.
/// </summary>
public interface ISystemSettingsPage
{
    /// <summary>
    /// Get all sections for this page.
    /// </summary>
    Task<List<SettingsSection>> GetSectionsAsync();

    /// <summary>
    /// Get status summary for this page.
    /// </summary>
    Task<StatusSummary> GetStatusSummaryAsync();

    /// <summary>
    /// Get overall page status.
    /// </summary>
    Task<SystemSettingsStatus> GetOverallStatusAsync();
}
