namespace BirkNext.Api.Models.Admin;

/// <summary>
/// Overall diagnostic status.
/// </summary>
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
    public string Name { get; set; } = "";
    public EnvironmentDiagnosticStatus Status { get; set; }
    public string Details { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string? TechnicalDetails { get; set; }
}

/// <summary>
/// Complete diagnostic report with checks organized by category.
/// </summary>
public class EnvironmentDiagnosticsReport
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string Environment { get; set; } = "";

    public List<EnvironmentDiagnosticCheck> DatabaseChecks { get; set; } = [];
    public List<EnvironmentDiagnosticCheck> BackendApiChecks { get; set; } = [];
    public List<EnvironmentDiagnosticCheck> WorkspaceChecks { get; set; } = [];
    public List<EnvironmentDiagnosticCheck> ReviewContextChecks { get; set; } = [];
    public List<EnvironmentDiagnosticCheck> ExportChecks { get; set; } = [];

    /// <summary>
    /// Overall status: Pass only if all checks pass.
    /// </summary>
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
