using System.Text.Json.Serialization;

namespace BirkNext.Api.Models.Admin;

/// <summary>
/// Configuration Health Report containing required and optional checks
/// </summary>
public class ConfigurationHealthReport
{
    [JsonPropertyName("overallStatus")]
    public string OverallStatus { get; set; } = "Pass";

    [JsonPropertyName("passCount")]
    public int PassCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("failCount")]
    public int FailCount { get; set; }

    [JsonPropertyName("unavailableCount")]
    public int UnavailableCount { get; set; }

    [JsonPropertyName("requiredChecks")]
    public List<ConfigurationHealthCheck> RequiredChecks { get; set; } = new();

    [JsonPropertyName("optionalChecks")]
    public List<ConfigurationHealthCheck> OptionalChecks { get; set; } = new();
}

/// <summary>
/// Individual configuration health check result
/// </summary>
public class ConfigurationHealthCheck
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Pass";  // Pass, Warning, Fail, Unavailable

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("details")]
    public string Details { get; set; } = "";

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; }
}
