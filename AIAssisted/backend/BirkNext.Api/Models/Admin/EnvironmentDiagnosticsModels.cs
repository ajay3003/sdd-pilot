using System.Text.Json.Serialization;

namespace BirkNext.Api.Models.Admin;

/// <summary>
/// Overall diagnostic status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentDiagnosticStatus
{
    Pass,          // Check passed
    Info,          // Informational - feature not configured or not needed (not an error)
    Warning,       // Check passed with warnings
    Fail,          // Check failed - something is broken
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
    /// Overall status calculation based on classification rules:
    /// - FAIL if any check has Fail status
    /// - WARNING if no Fail statuses but has Warning or NotAvailable statuses
    /// - PASS if all checks are Pass (or no checks at all)
    /// </summary>
    [JsonPropertyName("overallStatus")]
    public EnvironmentDiagnosticStatus OverallStatus
    {
        get
        {
            var allChecks = GetAllChecks();

            // Fail is worst - if any check fails, overall status is Fail
            if (allChecks.Any(c => c.Status == EnvironmentDiagnosticStatus.Fail))
                return EnvironmentDiagnosticStatus.Fail;

            // Warning is second worst - if any warning or unavailable but no failures
            if (allChecks.Any(c =>
                c.Status == EnvironmentDiagnosticStatus.Warning ||
                c.Status == EnvironmentDiagnosticStatus.NotAvailable))
                return EnvironmentDiagnosticStatus.Warning;

            // All checks pass or no checks
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
