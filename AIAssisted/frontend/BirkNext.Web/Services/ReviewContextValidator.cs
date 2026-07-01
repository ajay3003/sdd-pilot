namespace BirkNext.Web.Services;

using BirkNext.Web.Models;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Validates that ReviewContext metrics match the metrics displayed on each review page.
/// Detects metric drift caused by:
/// - Independent parsing in pages
/// - Duplicate calculations
/// - Fallback code execution
/// - Semantic model mismatches
/// </summary>
public sealed class ReviewContextValidator
{
    /// <summary>
    /// Metrics extracted from ReviewContext (single source of truth).
    /// </summary>
    public sealed class CanonicalMetrics
    {
        public int TotalUserStories { get; init; }
        public int TotalRequirements { get; init; }
        public int TotalSuccessCriteria { get; init; }
        public int TotalAcceptanceScenarios { get; init; }
        public int TotalClarifications { get; init; }
        public int TotalConstitutionRules { get; init; }
        public int TotalTasks { get; init; }
        public int TotalDataEntities { get; init; }
        public int TotalEdgeCases { get; init; }
        public int TotalAssumptions { get; init; }
        public int TotalSecurityConsiderations { get; init; }

        // Coverage metrics
        public int SpecificationCompleteness { get; init; }
        public int TraceabilityCompleteness { get; init; }
        public int GovernanceCompleteness { get; init; }
        public int ImplementationCompleteness { get; init; }
        public int OverallCompleteness { get; init; }

        // Traceability
        public int SpecToConstitutionLinks { get; init; }
        public int SpecToPlanLinks { get; init; }
        public int SpecToTasksLinks { get; init; }
        public int SpecToDataModelLinks { get; init; }
        public int PlanToTasksLinks { get; init; }
        public int ConstitutionToTasksLinks { get; init; }
    }

    /// <summary>
    /// Metrics captured from a single page.
    /// </summary>
    public sealed class PageMetrics
    {
        public string PageName { get; init; } = string.Empty;
        public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
        public Dictionary<string, int?> Metrics { get; init; } = [];
    }

    /// <summary>
    /// Result of comparing ReviewContext against a page's metrics.
    /// </summary>
    public sealed class MetricMismatch
    {
        public string MetricName { get; init; } = string.Empty;
        public int? ExpectedValue { get; init; }
        public int? ActualValue { get; init; }
        public string PageName { get; init; } = string.Empty;
        public string? RootCause { get; init; }
    }

    /// <summary>
    /// Complete validation report.
    /// </summary>
    public sealed class ValidationReport
    {
        public DateTime RunAt { get; init; } = DateTime.UtcNow;
        public string Status { get; init; } = "PENDING"; // PASS, FAIL, ERROR
        public CanonicalMetrics? CanonicalValues { get; init; }
        public List<PageMetrics> PageResults { get; init; } = [];
        public List<MetricMismatch> Mismatches { get; init; } = [];
        public string? ErrorMessage { get; init; }

        public string SummaryText
        {
            get
            {
                if (Status == "ERROR") return $"Validation Error: {ErrorMessage}";
                if (Mismatches.Count == 0) return "✓ All metrics match ReviewContext";
                return $"✗ {Mismatches.Count} metric mismatches found across {PageResults.Select(p => p.PageName).Distinct().Count()} pages";
            }
        }
    }

    /// <summary>
    /// Extract ReviewContext metrics into canonical form.
    /// </summary>
    public CanonicalMetrics ExtractCanonical(ReviewContext context)
    {
        return new CanonicalMetrics
        {
            TotalUserStories = context.Specification.UserStories.Count,
            TotalRequirements = context.Specification.Requirements.Count,
            TotalSuccessCriteria = context.Specification.SuccessCriteria.Count,
            TotalAcceptanceScenarios = context.Specification.AcceptanceScenarios.Count,
            TotalClarifications = context.Specification.Clarifications.Count,
            TotalConstitutionRules = context.Constitution.Rules.Count,
            TotalTasks = context.Tasks.AllTasks.Count,
            TotalDataEntities = context.DataModel.Entities.Count,
            TotalEdgeCases = context.Specification.EdgeCases.Count,
            TotalAssumptions = context.Specification.Assumptions.Count,
            TotalSecurityConsiderations = context.Specification.SecurityConsiderations.Count,

            SpecificationCompleteness = context.Coverage.SpecificationCompleteness,
            TraceabilityCompleteness = context.Coverage.TraceabilityCompleteness,
            GovernanceCompleteness = context.Coverage.GovernanceCompleteness,
            ImplementationCompleteness = context.Coverage.ImplementationCompleteness,
            OverallCompleteness = context.Coverage.OverallCompleteness,

            SpecToConstitutionLinks = context.SpecToConstitution.Count,
            SpecToPlanLinks = context.SpecToPlan.Count,
            SpecToTasksLinks = context.SpecToTasks.Count,
            SpecToDataModelLinks = context.SpecToDataModel.Count,
            PlanToTasksLinks = context.PlanToTasks.Count,
            ConstitutionToTasksLinks = context.ConstitutionToTasks.Count,
        };
    }

    /// <summary>
    /// Compare canonical metrics against page metrics.
    /// </summary>
    public List<MetricMismatch> FindMismatches(
        CanonicalMetrics canonical,
        PageMetrics pageMetrics)
    {
        var mismatches = new List<MetricMismatch>();
        var canonicalDict = ExtractDictionary(canonical);

        foreach (var (metricName, expected) in canonicalDict)
        {
            pageMetrics.Metrics.TryGetValue(metricName, out var actual);

            if (expected != actual)
            {
                mismatches.Add(new MetricMismatch
                {
                    MetricName = metricName,
                    ExpectedValue = expected,
                    ActualValue = actual,
                    PageName = pageMetrics.PageName,
                    RootCause = DiagnoseRootCause(metricName, pageMetrics.PageName),
                });
            }
        }

        return mismatches;
    }

    /// <summary>
    /// Generate validation report comparing ReviewContext against all pages.
    /// </summary>
    public ValidationReport GenerateReport(
        ReviewContext canonical,
        List<PageMetrics> pageResults)
    {
        try
        {
            var canonicalMetrics = ExtractCanonical(canonical);
            var allMismatches = new List<MetricMismatch>();

            foreach (var pageMetrics in pageResults)
            {
                allMismatches.AddRange(FindMismatches(canonicalMetrics, pageMetrics));
            }

            return new ValidationReport
            {
                Status = allMismatches.Count == 0 ? "PASS" : "FAIL",
                CanonicalValues = canonicalMetrics,
                PageResults = pageResults,
                Mismatches = allMismatches,
            };
        }
        catch (Exception ex)
        {
            return new ValidationReport
            {
                Status = "ERROR",
                ErrorMessage = ex.Message,
                PageResults = pageResults,
            };
        }
    }

    /// <summary>
    /// Diagnose the root cause of a metric mismatch.
    /// </summary>
    private string DiagnoseRootCause(string metricName, string pageName)
    {
        return pageName switch
        {
            "Constitution Explorer" => $"Local semantic model build in ConstitutionExplorer.BuildSemanticModel()",
            "Specification Review" => $"Independent parsing in QualityReviewService.Parse()",
            "Flow View" => $"FlowModelBuilder.Build() rebuilds from markdown instead of using SemanticModel parameter",
            "Artifact Traceability" => $"Independent semantic model build before ReviewContext.Create()",
            "Dashboard" => $"Aggregation from service snapshots instead of ReviewContext metrics",
            _ => "Unknown cause — page not recognized",
        };
    }

    /// <summary>
    /// Convert CanonicalMetrics to dictionary for comparison.
    /// </summary>
    private Dictionary<string, int> ExtractDictionary(CanonicalMetrics metrics)
    {
        return new Dictionary<string, int>
        {
            { "TotalUserStories", metrics.TotalUserStories },
            { "TotalRequirements", metrics.TotalRequirements },
            { "TotalSuccessCriteria", metrics.TotalSuccessCriteria },
            { "TotalAcceptanceScenarios", metrics.TotalAcceptanceScenarios },
            { "TotalClarifications", metrics.TotalClarifications },
            { "TotalConstitutionRules", metrics.TotalConstitutionRules },
            { "TotalTasks", metrics.TotalTasks },
            { "TotalDataEntities", metrics.TotalDataEntities },
            { "TotalEdgeCases", metrics.TotalEdgeCases },
            { "TotalAssumptions", metrics.TotalAssumptions },
            { "TotalSecurityConsiderations", metrics.TotalSecurityConsiderations },
            { "SpecificationCompleteness", metrics.SpecificationCompleteness },
            { "TraceabilityCompleteness", metrics.TraceabilityCompleteness },
            { "GovernanceCompleteness", metrics.GovernanceCompleteness },
            { "ImplementationCompleteness", metrics.ImplementationCompleteness },
            { "OverallCompleteness", metrics.OverallCompleteness },
            { "SpecToConstitutionLinks", metrics.SpecToConstitutionLinks },
            { "SpecToPlanLinks", metrics.SpecToPlanLinks },
            { "SpecToTasksLinks", metrics.SpecToTasksLinks },
            { "SpecToDataModelLinks", metrics.SpecToDataModelLinks },
            { "PlanToTasksLinks", metrics.PlanToTasksLinks },
            { "ConstitutionToTasksLinks", metrics.ConstitutionToTasksLinks },
        };
    }

    /// <summary>
    /// Export validation report as JSON.
    /// </summary>
    public string ExportAsJson(ValidationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"status\": \"{report.Status}\",");
        sb.AppendLine($"  \"runAt\": \"{report.RunAt:O}\",");
        sb.AppendLine($"  \"summary\": \"{EscapeJson(report.SummaryText)}\",");
        sb.AppendLine($"  \"mismatchCount\": {report.Mismatches.Count},");
        sb.AppendLine("  \"mismatches\": [");

        for (int i = 0; i < report.Mismatches.Count; i++)
        {
            var m = report.Mismatches[i];
            sb.AppendLine("    {");
            sb.AppendLine($"      \"metric\": \"{m.MetricName}\",");
            sb.AppendLine($"      \"page\": \"{m.PageName}\",");
            sb.AppendLine($"      \"expected\": {m.ExpectedValue},");
            sb.AppendLine($"      \"actual\": {m.ActualValue},");
            sb.AppendLine($"      \"rootCause\": \"{EscapeJson(m.RootCause ?? "Unknown")}\"");
            sb.Append("    }");
            if (i < report.Mismatches.Count - 1) sb.AppendLine(",");
            else sb.AppendLine();
        }

        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Export validation report as CSV.
    /// </summary>
    public string ExportAsCsv(ValidationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Status,RunAt,Metric,Page,Expected,Actual,RootCause");

        if (report.Mismatches.Count == 0)
        {
            sb.AppendLine($"{report.Status},{report.RunAt:O},All metrics match,N/A,N/A,N/A,N/A");
        }
        else
        {
            foreach (var m in report.Mismatches)
            {
                sb.AppendLine($"{report.Status},{report.RunAt:O},{EscapeCsv(m.MetricName)},{EscapeCsv(m.PageName)},{m.ExpectedValue},{m.ActualValue},{EscapeCsv(m.RootCause ?? "Unknown")}");
            }
        }

        return sb.ToString();
    }

    private string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private string EscapeCsv(string value)
    {
        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}

/// <summary>
/// Interface that every review page must implement to expose its displayed metrics.
/// </summary>
public interface IMetricsProvider
{
    /// <summary>
    /// Return the exact metrics currently displayed to the user.
    /// Names must match ReviewContextValidator metric names.
    /// </summary>
    Dictionary<string, int?> GetDisplayedMetrics();
}
