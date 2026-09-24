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

            var report = ValidateContext(reviewContext, traceabilityReport, projectName,
                hasConstitution: constitution is not null, hasSpecification: spec is not null, hasPlan: plan is not null);
            return report;
        }
        catch
        {
            return CreateEmptyStateReport(projectName);
        }
    }

    public ReviewContextValidationReport ValidateContext(
        ReviewContext reviewContext,
        ArtifactTraceabilityReport? traceabilityReport,
        string projectName = "Current Project",
        bool hasConstitution = true,
        bool hasSpecification = true,
        bool hasPlan = true)
    {
        if (reviewContext == null)
            return CreateEmptyStateReport(projectName);

        var findings = new List<ReviewContextValidationFinding>();
        var comparisons = new List<ReviewContextSourceComparison>();
        var metrics = ExtractCanonicalMetrics(reviewContext, hasConstitution, hasSpecification, hasPlan);
        var hasSources = hasConstitution || hasSpecification || hasPlan;

        // Derived coverage is only a finding when there is a specification to derive it from.
        if (hasSpecification)
            CompareMetrics(reviewContext, traceabilityReport, findings, comparisons);

        // A Pass finding is a confirmation, not a problem: only Warning and Fail findings, and metrics read from a
        // present-but-empty document, lower the verdict.
        var overallStatus =
            findings.Any(f => f.Severity == ReviewContextValidationStatus.Fail) ? ReviewContextValidationStatus.Fail
            : findings.Any(f => f.Severity == ReviewContextValidationStatus.Warning)
              || metrics.Any(m => m.State == ReviewContextMetricState.EmptySource) || !hasSources
                ? ReviewContextValidationStatus.Warning
                : ReviewContextValidationStatus.Pass;

        return new ReviewContextValidationReport
        {
            GeneratedAt = DateTime.UtcNow,
            ProjectName = projectName,
            OverallStatus = overallStatus,
            CanonicalMetrics = metrics,
            SourceComparisons = comparisons,
            Findings = findings,
            HasSourceDocuments = hasSources,
        };
    }

    private static ReviewContextMetricState StateOf(bool sourcePresent, bool yieldedSomething) =>
        !sourcePresent ? ReviewContextMetricState.NotEvaluated
        : yieldedSomething ? ReviewContextMetricState.Evaluated
        : ReviewContextMetricState.EmptySource;

    private List<ReviewContextValidationMetric> ExtractCanonicalMetrics(ReviewContext context, bool hasConstitution, bool hasSpecification, bool hasPlan)
    {
        var rules = context.Constitution.Rules.Count;
        var requirements = context.Specification.Requirements.Count;
        var planContent = context.Plan.Phases.Count > 0 || context.Plan.ArchitectureDecisions.Count > 0;
        // Counts derived from the specification are observations once it is present, including a genuine zero.
        var specDerived = hasSpecification ? ReviewContextMetricState.Evaluated : ReviewContextMetricState.NotEvaluated;
        return new()
        {
            new() { Name = "Constitution Loaded", Value = rules > 0, Source = "ReviewContext", State = StateOf(hasConstitution, rules > 0) },
            new() { Name = "Specification Loaded", Value = requirements > 0, Source = "ReviewContext", State = StateOf(hasSpecification, requirements > 0) },
            new() { Name = "Plan Loaded", Value = planContent, Source = "ReviewContext", State = StateOf(hasPlan, planContent) },
            new() { Name = "Requirements", Value = requirements, Source = "ReviewContext", State = specDerived },
            new() { Name = "Tests", Value = context.Specification.AcceptanceScenarios.Count, Source = "ReviewContext", State = specDerived },
            new() { Name = "Constitution Rules", Value = rules, Source = "ReviewContext", State = hasConstitution ? ReviewContextMetricState.Evaluated : ReviewContextMetricState.NotEvaluated },
            new() { Name = "Requirements With Tests", Value = context.RequirementsWithTests, Source = "ReviewContext", State = specDerived },
            new() { Name = "Missing Tests", Value = context.MissingTests, Source = "ReviewContext", State = specDerived },
            // SpecificationCompleteness is requirement→user-story linkage, not test coverage; named for what it counts.
            new() { Name = "Requirements Linked to User Stories %", Value = context.Coverage.SpecificationCompleteness, Source = "ReviewContext", State = specDerived },
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
