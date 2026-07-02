using Xunit;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BirkNext.Web.Tests;

/// <summary>
/// Runtime validation of ReviewContext.
/// Loads sample artifacts, builds ReviewContext, and compares metrics.
/// </summary>
public class ReviewContextRuntimeValidationTest
{
    private const string SampleDataPath = @"BirkNext/SampleData/person-adapter";
    private ReviewContext? _reviewContext;
    private SpecificationSemanticModel? _spec;

    [Fact]
    public void Validate_ReviewContext_IsCanonicalSourceOfTruth()
    {
        // ARRANGE: Load sample artifacts
        var constitutionText = LoadArtifact("constitution.md");
        var specText = LoadArtifact("spec.md");
        var planText = LoadArtifact("plan.md");
        var tasksText = LoadArtifact("tasks.md");
        var dataModelText = LoadArtifact("data-model.md");

        // Parse artifacts
        var constitution = new ConstitutionAnalysisService().Parse(constitutionText);
        var spec = SpecExplorerService.Parse(specText);
        var plan = new PlanAnalysisService().Parse(planText);
        var tasks = TaskExplorerService.Parse(tasksText);

        // Build semantic models
        var constitutionModel = ConstitutionAnalysisService.BuildSemanticModel(constitution);
        _spec = SpecExplorerService.BuildSemanticModel(spec, specText);
        var planModel = PlanAnalysisService.BuildSemanticModel(plan);
        var taskModel = TaskExplorerService.BuildSemanticModel(tasks);
        var dataModel = new DataModelSemanticModel();

        // ACT: Build ReviewContext
        _reviewContext = ReviewContextFactory.Create(
            constitutionModel,
            _spec,
            planModel,
            taskModel,
            dataModel);

        // ASSERT: Validate all metrics
        var results = new List<ValidationResult>();

        // Core Artifact Counts
        ValidateCoreMetric(results, "User Stories",
            _reviewContext.Specification.UserStories.Count,
            _spec.UserStories.Count);

        ValidateCoreMetric(results, "Requirements",
            _reviewContext.Specification.Requirements.Count,
            _spec.Requirements.Count);

        ValidateCoreMetric(results, "Success Criteria",
            _reviewContext.Specification.SuccessCriteria.Count,
            _spec.SuccessCriteria.Count);

        ValidateCoreMetric(results, "Tests (Acceptance Scenarios)",
            _reviewContext.Specification.AcceptanceScenarios.Count,
            _spec.AcceptanceScenarios.Count);

        ValidateCoreMetric(results, "Clarifications",
            _reviewContext.Specification.Clarifications.Count,
            _spec.Clarifications.Count);

        ValidateCoreMetric(results, "Constitution Rules",
            _reviewContext.Constitution.Rules.Count,
            constitutionModel.Rules.Count);

        ValidateCoreMetric(results, "Tasks",
            _reviewContext.Tasks.AllTasks.Count,
            taskModel.AllTasks.Count);

        ValidateCoreMetric(results, "Data Entities",
            _reviewContext.DataModel.Entities.Count,
            dataModel.Entities.Count);

        // Coverage Metrics
        ValidateCoreMetric(results, "Requirements With Tests",
            _reviewContext.RequirementsWithTests,
            _spec.RequirementsWithTests);

        ValidateCoreMetric(results, "Requirements With Success Criteria",
            _reviewContext.RequirementsWithSuccessCriteria,
            _spec.RequirementsWithSuccessCriteria);

        ValidateCoreMetric(results, "Missing Tests",
            _reviewContext.MissingTests,
            _spec.TotalRequirements - _spec.RequirementsWithTests);

        ValidateCoreMetric(results, "Missing Success Criteria",
            _reviewContext.MissingSuccessCriteria,
            _spec.TotalRequirements - _spec.RequirementsWithSuccessCriteria);

        // Coverage Percentage
        var expectedCoveragePct = _spec.TotalRequirements == 0 ? 0 :
            (_spec.RequirementsWithTests * 100) / _spec.TotalRequirements;
        ValidateCoreMetric(results, "Coverage %",
            _reviewContext.Coverage.SpecificationCompleteness,
            expectedCoveragePct);

        // Traceability Links
        ValidateCoreMetric(results, "Spec → Constitution Links",
            _reviewContext.SpecToConstitution.Count,
            _spec.Requirements.Count(r => r.LinkedConstitutionRules.Count > 0));

        ValidateCoreMetric(results, "Spec → Plan Links",
            _reviewContext.SpecToPlan.Count,
            _spec.Requirements.Count(r => r.LinkedArchitectureDecisions.Count > 0));

        ValidateCoreMetric(results, "Spec → Tasks Links",
            _reviewContext.SpecToTasks.Count,
            _spec.Requirements.Count(r => r.LinkedTasks.Count > 0));

        ValidateCoreMetric(results, "Spec → DataModel Links",
            _reviewContext.SpecToDataModel.Count,
            _spec.Requirements.Count(r => r.LinkedDataEntities.Count > 0));

        ValidateCoreMetric(results, "Plan → Tasks Links",
            _reviewContext.PlanToTasks.Count,
            planModel.Phases.Count > 0 ? planModel.Phases.Count : 0);

        // Gap Metrics
        ValidateCoreMetric(results, "Orphaned Tests",
            _reviewContext.GetOrphanedTestCount(),
            _spec.AcceptanceScenarios.Count(a => a.LinkedRequirements.Count == 0));

        ValidateCoreMetric(results, "Requirements Without Coverage",
            _reviewContext.GetRequirementsWithoutCoverageCount(),
            _spec.Requirements.Count(r => r.LinkedAcceptanceScenarios.Count == 0));

        // Health Scores
        ValidateCoreMetric(results, "Specification Completeness %",
            _reviewContext.Coverage.SpecificationCompleteness,
            expectedCoveragePct);

        // Generate Report
        PrintValidationReport(results);

        // Assert all passed
        var failed = results.Where(r => !r.Passed).ToList();
        if (failed.Count > 0)
        {
            var failureMessage = string.Join("\n",
                failed.Select(f => $"FAIL: {f.Metric} - Expected {f.Expected}, Got {f.Actual}"));
            Assert.False(true, $"Validation failed:\n{failureMessage}");
        }
    }

    private void ValidateCoreMetric(List<ValidationResult> results, string metric, int actual, int expected)
    {
        var passed = actual == expected;
        results.Add(new ValidationResult
        {
            Metric = metric,
            ReviewContextValue = actual,
            Expected = expected,
            Actual = actual,
            Passed = passed
        });
    }

    private void PrintValidationReport(List<ValidationResult> results)
    {
        Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                  REVIEWCONTEXT RUNTIME VALIDATION REPORT                    ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════════════╝\n");

        var passed = results.Count(r => r.Passed);
        var failed = results.Count - passed;

        Console.WriteLine($"Status: {(failed == 0 ? "✓ PASSED" : "✗ FAILED")}");
        Console.WriteLine($"Results: {passed} passed, {failed} failed\n");

        Console.WriteLine("┌─────────────────────────────────────┬──────────────┬──────────────┬─────────┐");
        Console.WriteLine("│ Metric                              │ ReviewContext│ Expected     │ Match   │");
        Console.WriteLine("├─────────────────────────────────────┼──────────────┼──────────────┼─────────┤");

        foreach (var result in results)
        {
            var match = result.Passed ? "✓" : "✗";
            var metricCol = result.Metric.PadRight(35);
            var rcCol = result.ReviewContextValue.ToString().PadLeft(12);
            var expCol = result.Expected.ToString().PadLeft(12);
            Console.WriteLine($"│ {metricCol} │ {rcCol} │ {expCol} │ {match,-7}│");
        }

        Console.WriteLine("└─────────────────────────────────────┴──────────────┴──────────────┴─────────┘\n");

        if (failed == 0)
        {
            Console.WriteLine("✓✓✓ VALIDATION PASSED ✓✓✓");
            Console.WriteLine("\nConclusion: ReviewContext is now the canonical runtime source of truth.\n");
        }
        else
        {
            Console.WriteLine("✗✗✗ VALIDATION FAILED ✗✗✗");
            Console.WriteLine("\nFailed Metrics:");
            foreach (var result in results.Where(r => !r.Passed))
            {
                Console.WriteLine($"  - {result.Metric}: Expected {result.Expected}, Got {result.Actual}");
            }
            Console.WriteLine();
        }
    }

    private string LoadArtifact(string filename)
    {
        var path = Path.Combine(SampleDataPath, filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Sample artifact not found: {path}");
        }
        return File.ReadAllText(path);
    }

    private class ValidationResult
    {
        public string Metric { get; set; } = string.Empty;
        public int ReviewContextValue { get; set; }
        public int Expected { get; set; }
        public int Actual { get; set; }
        public bool Passed { get; set; }
    }
}
