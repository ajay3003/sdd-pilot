namespace BirkNext.Web.Models;

/// <summary>
/// Overall status of ReviewContext validation.
/// </summary>
public enum ReviewContextValidationStatus
{
    Pass,
    Warning,
    Fail
}

/// <summary>
/// Single metric captured from ReviewContext.
/// </summary>
public sealed class ReviewContextValidationMetric
{
    public required string Name { get; init; }
    public required object? Value { get; init; }
    public required string Source { get; init; } = "ReviewContext";
}

/// <summary>
/// Single validation finding comparing ReviewContext to a downstream result model.
/// </summary>
public sealed class ReviewContextValidationFinding
{
    public required string MetricName { get; init; }
    public required object? Expected { get; init; }
    public required object? Actual { get; init; }
    public required string Source { get; init; }
    public required ReviewContextValidationStatus Severity { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Source comparison entry showing metric values from ReviewContext and downstream sources.
/// </summary>
public sealed class ReviewContextSourceComparison
{
    public required string Metric { get; init; }
    public required object? ReviewContextValue { get; init; }
    public required Dictionary<string, object?> OtherSources { get; init; } = [];
    public required bool AllMatch { get; init; }
}

/// <summary>
/// Complete validation report for ReviewContext consistency.
/// </summary>
public sealed class ReviewContextValidationReport
{
    public required DateTime GeneratedAt { get; init; }
    public required string ProjectName { get; init; }
    public required ReviewContextValidationStatus OverallStatus { get; init; }

    public required List<ReviewContextValidationMetric> CanonicalMetrics { get; init; } = [];
    public required List<ReviewContextSourceComparison> SourceComparisons { get; init; } = [];
    public required List<ReviewContextValidationFinding> Findings { get; init; } = [];

    public string Summary
    {
        get
        {
            var passCount = Findings.Count(f => f.Severity == ReviewContextValidationStatus.Pass);
            var warnCount = Findings.Count(f => f.Severity == ReviewContextValidationStatus.Warning);
            var failCount = Findings.Count(f => f.Severity == ReviewContextValidationStatus.Fail);
            return $"{passCount} passed, {warnCount} warnings, {failCount} failures";
        }
    }
}
