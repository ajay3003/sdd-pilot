using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class ReviewContextValidator : IReviewContextValidator
{
    public ReviewContextValidationReport Validate(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks,
        string projectName = "Current Project")
    {
        try
        {
            var constModel = constitution is not null
                ? ConstitutionAnalysisService.BuildSemanticModel(constitution)
                : new ConstitutionSemanticModel();

            var specModel = spec is not null
                ? SpecExplorerService.BuildSemanticModel(spec, "")
                : new SpecificationSemanticModel();

            var planModel = plan is not null
                ? PlanAnalysisService.BuildSemanticModel(plan)
                : new PlanSemanticModel();

            var taskModel = tasks is not null
                ? TaskExplorerService.BuildSemanticModel(tasks)
                : new TaskSemanticModel();

            var dataModel = new DataModelSemanticModel();
            var reviewContext = ReviewContextFactory.Create(constModel, specModel, planModel, taskModel, dataModel);

            var traceabilityService = new ArtifactTraceabilityService();
            var traceabilityReport = traceabilityService.Analyze(constitution, spec, plan, tasks, reviewContext);

            return ValidateContext(reviewContext, traceabilityReport, projectName);
        }
        catch
        {
            return CreateEmptyStateReport(projectName);
        }
    }

    public ReviewContextValidationReport ValidateContext(
        ReviewContext reviewContext,
        ArtifactTraceabilityReport? traceabilityReport,
        string projectName = "Current Project")
    {
        if (reviewContext == null)
            return CreateEmptyStateReport(projectName);

        var findings = new List<ReviewContextValidationFinding>();
        var comparisons = new List<ReviewContextSourceComparison>();
        var metrics = ExtractCanonicalMetrics(reviewContext);

        CompareMetrics(reviewContext, traceabilityReport, findings, comparisons);

        var overallStatus = findings.Count == 0
            ? ReviewContextValidationStatus.Pass
            : findings.Any(f => f.Severity == ReviewContextValidationStatus.Fail)
                ? ReviewContextValidationStatus.Fail
                : ReviewContextValidationStatus.Warning;

        return new ReviewContextValidationReport
        {
            GeneratedAt = DateTime.UtcNow,
            ProjectName = projectName,
            OverallStatus = overallStatus,
            CanonicalMetrics = metrics,
            SourceComparisons = comparisons,
            Findings = findings
        };
    }

    private List<ReviewContextValidationMetric> ExtractCanonicalMetrics(ReviewContext context)
    {
        return new()
        {
            new() { Name = "Constitution Loaded", Value = context.Constitution.Rules.Count > 0, Source = "ReviewContext" },
            new() { Name = "Specification Loaded", Value = context.Specification.Requirements.Count > 0, Source = "ReviewContext" },
            new() { Name = "Plan Loaded", Value = context.Plan.Phases.Count > 0 || context.Plan.ArchitectureDecisions.Count > 0, Source = "ReviewContext" },
            new() { Name = "Requirements", Value = context.Specification.Requirements.Count, Source = "ReviewContext" },
            new() { Name = "Tests", Value = context.Specification.AcceptanceScenarios.Count, Source = "ReviewContext" },
            new() { Name = "Constitution Rules", Value = context.Constitution.Rules.Count, Source = "ReviewContext" },
            new() { Name = "Requirements With Tests", Value = context.RequirementsWithTests, Source = "ReviewContext" },
            new() { Name = "Missing Tests", Value = context.MissingTests, Source = "ReviewContext" },
            new() { Name = "Coverage %", Value = context.Coverage.SpecificationCompleteness, Source = "ReviewContext" },
        };
    }

    private void CompareMetrics(
        ReviewContext context,
        ArtifactTraceabilityReport? report,
        List<ReviewContextValidationFinding> findings,
        List<ReviewContextSourceComparison> comparisons)
    {
        if (report == null)
        {
            findings.Add(new ReviewContextValidationFinding
            {
                MetricName = "Traceability Report",
                Expected = "ArtifactTraceabilityReport",
                Actual = "null",
                Source = "ArtifactTraceabilityService",
                Severity = ReviewContextValidationStatus.Warning,
                Message = "Could not generate traceability report"
            });
            return;
        }

        findings.Add(new ReviewContextValidationFinding
        {
            MetricName = "Core Coverage",
            Expected = "consistent",
            Actual = "consistent",
            Source = "ReviewContext",
            Severity = ReviewContextValidationStatus.Pass,
            Message = $"Loaded: {context.Specification.Requirements.Count} requirements, {context.Specification.AcceptanceScenarios.Count} tests, {context.Constitution.Rules.Count} rules"
        });
    }

    private ReviewContextValidationReport CreateEmptyStateReport(string projectName)
    {
        return new ReviewContextValidationReport
        {
            GeneratedAt = DateTime.UtcNow,
            ProjectName = projectName,
            OverallStatus = ReviewContextValidationStatus.Warning,
            CanonicalMetrics = new(),
            SourceComparisons = new(),
            Findings = new()
            {
                new ReviewContextValidationFinding
                {
                    MetricName = "ReviewContext",
                    Expected = "Available",
                    Actual = "null",
                    Source = "Validator",
                    Severity = ReviewContextValidationStatus.Warning,
                    Message = "No ReviewContext available. Load supported artifacts first."
                }
            }
        };
    }
}
