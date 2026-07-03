using System.Text.Json.Serialization;

namespace BirkNext.Api.Models.Admin;

/// <summary>
/// A single diagnostic check result.
/// </summary>
public class EnvironmentDiagnosticCheck
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public SystemSettingsStatus Status { get; set; }

    [JsonPropertyName("details")]
    public string Details { get; set; } = "";

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = "";

    [JsonPropertyName("technicalDetails")]
    public string? TechnicalDetails { get; set; }
}

/// <summary>
/// Complete diagnostic report with checks organized by unified settings hierarchy.
/// Uses SettingsSection to eliminate custom report hierarchies.
/// </summary>
public class EnvironmentDiagnosticsReport
{
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "";

    [JsonPropertyName("overallStatus")]
    public SystemSettingsStatus OverallStatus { get; set; } = SystemSettingsStatus.Pass;

    [JsonPropertyName("summary")]
    public StatusSummary? Summary { get; set; }

    [JsonPropertyName("sections")]
    public List<SettingsSection> Sections { get; set; } = [];

    /// <summary>
    /// Get all checks from all sections (for backward compatibility with tests).
    /// </summary>
    public List<EnvironmentDiagnosticCheck> GetAllChecks()
    {
        var all = new List<EnvironmentDiagnosticCheck>();
        foreach (var section in Sections)
        {
            // Convert SettingsItem back to EnvironmentDiagnosticCheck for internal use
            foreach (var item in section.Items)
            {
                all.Add(new EnvironmentDiagnosticCheck
                {
                    Name = item.Name,
                    Status = item.Status,
                    Details = item.Description,
                    Recommendation = item.Recommendation ?? "",
                    TechnicalDetails = null
                });
            }
        }
        return all;
    }
}
