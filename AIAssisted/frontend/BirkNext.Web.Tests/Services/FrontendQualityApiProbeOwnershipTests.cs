using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// An absence found by probing says only what was probed, and it belongs to whoever owns the thing probed.
///
/// "No REST API documentation found" appeared under frontend Performance. Two things were wrong with it. It asserted
/// something the evidence does not support — four well-known paths not serving an OpenAPI document does not establish
/// that an API is undocumented; the document may be published elsewhere or require authentication. And it is a fact
/// about the API surface, which API Quality Review owns, not about how this frontend performs.
///
/// It is not deleted: the probe result is real evidence this review happens to hold. It is carried as a derived
/// readiness indicator, so it stays inspectable without counting as a frontend performance observation.
/// </summary>
public sealed class FrontendQualityApiProbeOwnershipTests
{
    private static PerformanceFinding Probe() => new()
    {
        Id = "API-R001",
        Title = "No OpenAPI document detected at probed paths",
        Severity = PerformanceSeverity.Info,
        Category = PerformanceCategory.ApiCalls,
        // Verbatim from WasmApiAnalysisService, which authors this text; the assertion below is that FQR passes it
        // through unchanged rather than restating the claim in its own words.
        Description = "No OpenAPI (Swagger) document answered at the common paths probed. This does not establish " +
                      "that the API is undocumented: the document may be published elsewhere or require authentication. " +
                      "API documentation is assessed by API Quality Review.",
        Recommendation = "Add OpenAPI documentation using Swashbuckle.AspNetCore.",
        Evidence = ["Probed: /swagger/v1/swagger.json, /swagger.json, /openapi.json"],
    };

    private static PerformanceFinding RealPerformanceFinding() => new()
    {
        Id = "PERF-001",
        Title = "Large application JavaScript payload",
        Severity = PerformanceSeverity.High,
        Category = PerformanceCategory.ApiCalls,
        Description = "The main bundle is large.",
        Recommendation = "Split the bundle.",
    };

    private static FrontendQualityReviewReport Report(params PerformanceFinding[] findings) =>
        new FrontendQualityReviewService().BuildReport(
            "https://m2lbdev.example.test/",
            securityReport: null,
            performanceReport: new WasmPerformanceReviewReport
            {
                ReviewedAt = DateTime.UtcNow,
                Findings = findings.ToList(),
            });

    // 21, 73. Not a frontend Performance finding.
    [Fact]
    public void TheOpenApiProbeIsNotAFrontendPerformanceFinding()
    {
        var report = Report(Probe());

        var probe = report.Findings.Should().ContainSingle(f => f.SourceRuleId == "API-R001").Subject;
        probe.Category.Should().NotBe(FrontendQualityCategory.Performance,
            "API documentation is not a property of this frontend's performance");
        probe.Category.Should().Be(FrontendQualityCategory.Readiness);
    }

    // 8, 21. And it never counts as something this review observed about the frontend.
    [Fact]
    public void TheOpenApiProbeIsDerivedAndStaysOutOfTheSourceFindingCount()
    {
        var report = Report(Probe(), RealPerformanceFinding());

        var probe = report.Findings.Single(f => f.SourceRuleId == "API-R001");
        probe.Origin.Should().Be(FrontendQualityFindingOrigin.Derived);

        var view = FrontendQualityResultPresentation.Build(report);
        view.SourceFindingCount.Should().Be(1, "only the performance observation is a source finding");
        view.DerivedIndicatorCount.Should().Be(1);
        // 30, 82. Kept, not removed.
        report.Findings.Should().HaveCount(2);
    }

    // 22. If retained, the wording says what was actually established.
    [Fact]
    public void TheProbeWordingClaimsOnlyWhatWasProbed()
    {
        var probe = Report(Probe()).Findings.Single(f => f.SourceRuleId == "API-R001");

        probe.Title.Should().Be("No OpenAPI document detected at probed paths");
        probe.Title.Should().NotContain("No REST API documentation found");
        probe.Description.Should().Contain("does not establish");
        probe.Description.Should().Contain("API Quality Review", "the owner is named");
    }

    // A genuine performance finding in the same source category is unaffected.
    [Fact]
    public void OrdinaryPerformanceFindingsAreStillPerformanceObservations()
    {
        var finding = Report(RealPerformanceFinding()).Findings.Single();

        finding.Category.Should().Be(FrontendQualityCategory.Performance);
        finding.Origin.Should().Be(FrontendQualityFindingOrigin.Source);
    }
}
