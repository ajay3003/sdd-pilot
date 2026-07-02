using System.Text.Json.Serialization;

namespace BirkNext.Api.Models.Admin;

/// <summary>
/// Overall diagnostic status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentDiagnosticStatus
{
    Pass,          // Check passed
    Warning,       // Check passed with warnings
    Fail,          // Check failed
    NotAvailable   // Check could not run (e.g., service not available)
}

/// <summary>
/// A single diagnostic check result.
/// </summary>
public class EnvironmentDiagnosticCheck
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public EnvironmentDiagnosticStatus Status { get; set; }

    [JsonPropertyName("details")]
    public string Details { get; set; } = "";

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = "";

    [JsonPropertyName("technicalDetails")]
    public string? TechnicalDetails { get; set; }
}

/// <summary>
/// Complete diagnostic report with checks organized by category.
/// </summary>
public class EnvironmentDiagnosticsReport
{
    [JsonPropertyName("generatedAt")]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "";

    [JsonPropertyName("databaseChecks")]
    public List<EnvironmentDiagnosticCheck> DatabaseChecks { get; set; } = [];

    [JsonPropertyName("backendApiChecks")]
    public List<EnvironmentDiagnosticCheck> BackendApiChecks { get; set; } = [];

    [JsonPropertyName("workspaceChecks")]
    public List<EnvironmentDiagnosticCheck> WorkspaceChecks { get; set; } = [];

    [JsonPropertyName("reviewContextChecks")]
    public List<EnvironmentDiagnosticCheck> ReviewContextChecks { get; set; } = [];

    [JsonPropertyName("exportChecks")]
    public List<EnvironmentDiagnosticCheck> ExportChecks { get; set; } = [];

    /// <summary>
    /// Overall status: Pass only if all checks pass.
    /// </summary>
    [JsonPropertyName("overallStatus")]
    public EnvironmentDiagnosticStatus OverallStatus
    {
        get
        {
            var allChecks = GetAllChecks();
            if (allChecks.Any(c => c.Status == EnvironmentDiagnosticStatus.Fail))
                return EnvironmentDiagnosticStatus.Fail;
            if (allChecks.Any(c => c.Status == EnvironmentDiagnosticStatus.Warning))
                return EnvironmentDiagnosticStatus.Warning;
            return EnvironmentDiagnosticStatus.Pass;
        }
    }

    public List<EnvironmentDiagnosticCheck> GetAllChecks()
    {
        var all = new List<EnvironmentDiagnosticCheck>();
        all.AddRange(DatabaseChecks);
        all.AddRange(BackendApiChecks);
        all.AddRange(WorkspaceChecks);
        all.AddRange(ReviewContextChecks);
        all.AddRange(ExportChecks);
        return all;
    }
}
