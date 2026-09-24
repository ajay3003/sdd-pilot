using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// A canonical metric is an observation of a source document. With no document it was not evaluated; with a present
/// but empty document it is a warning; a Pass finding is a confirmation and never lowers the verdict.
/// </summary>
public sealed class ReviewContextValidatorStateTests
{
    private readonly ReviewContextValidator _validator = new();
    private readonly ArtifactParserService _parser = new(new ConstitutionAnalysisService(), new PlanAnalysisService());

    private static string Sample(string file) => File.ReadAllText(TestDataHelper.ResolveSampleDataPath("autorisasjon", file));

    [Fact]
    public void WithNoSourceDocumentsEveryMetricIsNotEvaluated_AndNothingPasses()
    {
        var report = _validator.Validate(null, null, null, null, "none");

        report.HasSourceDocuments.Should().BeFalse();
        report.CanonicalMetrics.Should().OnlyContain(m => m.State == ReviewContextMetricState.NotEvaluated);
        report.Findings.Should().BeEmpty("coverage cannot be derived without a specification");
        report.OverallStatus.Should().Be(ReviewContextValidationStatus.Warning);
    }

    [Fact]
    public void TheRealAutorisasjonDocumentsAreEvaluated_AndAPassFindingDoesNotMakeItAWarning()
    {
        var parsed = _parser.Parse(Sample("constitution.md"), Sample("spec.md"), Sample("plan.md"), Sample("tasks.md"));
        var report = _validator.Validate(parsed.Constitution, parsed.Spec, parsed.Plan, parsed.Tasks, "autorisasjon");

        report.HasSourceDocuments.Should().BeTrue();
        report.CanonicalMetrics.Should().NotContain(m => m.State == ReviewContextMetricState.NotEvaluated);
        report.Findings.Should().OnlyContain(f => f.Severity == ReviewContextValidationStatus.Pass);
        report.OverallStatus.Should().Be(
            report.CanonicalMetrics.Any(m => m.State == ReviewContextMetricState.EmptySource) ? ReviewContextValidationStatus.Warning : ReviewContextValidationStatus.Pass,
            "only real warnings lower the verdict — the always-present Core Coverage confirmation used to force Warning");
    }

    [Fact]
    public void MetricsFollowTheirOwnSourceDocument()
    {
        var parsed = _parser.Parse(Sample("constitution.md"), null, null, null);
        var report = _validator.Validate(parsed.Constitution, null, null, null, "partial");

        report.CanonicalMetrics.Single(m => m.Name == "Constitution Rules").State.Should().Be(ReviewContextMetricState.Evaluated);
        report.CanonicalMetrics.Single(m => m.Name == "Requirements").State.Should().Be(ReviewContextMetricState.NotEvaluated);
        report.CanonicalMetrics.Single(m => m.Name == "Coverage %").State.Should().Be(ReviewContextMetricState.NotEvaluated);
        report.CanonicalMetrics.Single(m => m.Name == "Plan Loaded").State.Should().Be(ReviewContextMetricState.NotEvaluated);
    }
}
