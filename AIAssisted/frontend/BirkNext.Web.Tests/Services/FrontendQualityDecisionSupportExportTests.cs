using System.Text.Json;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendQualityDecisionSupportExportTests
{
    [Fact]
    public void HtmlExport_MirrorsTypedDecisionHierarchyAndSourceAttribution()
    {
        var html = new ReportExportService().ExportFrontendQualityReview(FrontendQualityDecisionSupportTests.Report(), "Project");

        html.IndexOf("Release disposition", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("Automated coverage", StringComparison.Ordinal));
        html.Should().Contain("Required assessed:").And.Contain("Optional assessed:")
            .And.Contain("headers:csp:missing").And.Contain("Static Security").And.Contain("Passive Security / ZAP")
            .And.Contain("static-csp").And.Contain("zap-csp")
            .And.Contain("Manual verification required").And.Contain("Browser Runtime")
            .And.Contain("Legacy static review score").And.NotContain("Overall Quality Score");
    }

    [Fact]
    public void HtmlAndDiagnosticJson_DoNotExposeApprovedSanitizationSentinel()
    {
        const string sentinel = "SECRET-PHASE2E-UIEXPORT-12345";
        var report = FrontendQualityDecisionSupportTests.Report();
        var sanitized = ReportExportService.SanitizePassive(sentinel);
        var issue = report.LogicalIssues[0];
        var instance = issue.FindingInstances[0] with { SanitizedEvidence = [sanitized] };
        issue = issue with { FindingInstances = [instance, issue.FindingInstances[1]] };
        report = new()
        {
            TargetUrl = report.TargetUrl, GeneratedAt = report.GeneratedAt, Coverage = report.Coverage,
            ReleaseDisposition = report.ReleaseDisposition, EngineOutcomes = report.EngineOutcomes, Findings = report.Findings,
            LogicalIssues = [issue], ManualReviewItems = report.ManualReviewItems, BrowserRuntimeReport = report.BrowserRuntimeReport,
            SecurityScore = report.SecurityScore, PerformanceScore = report.PerformanceScore, OverallScore = report.OverallScore,
        };

        new ReportExportService().ExportFrontendQualityReview(report, null).Should().NotContain(sentinel).And.Contain("REDACTED");
        JsonSerializer.Serialize(report).Should().NotContain(sentinel).And.Contain("REDACTED");
    }

    [Fact]
    public void CleanExportUsesCarefulCategoricalWording()
    {
        var source = FrontendQualityDecisionSupportTests.Report();
        var report = new BirkNext.Web.Models.FrontendQualityReviewReport
        {
            Coverage = source.Coverage, ReleaseDisposition = BirkNext.Web.Models.FrontendQualityReleaseDisposition.NoAutomatedBlockDetected,
            EngineOutcomes = source.EngineOutcomes, GeneratedAt = DateTime.UtcNow,
        };
        var html = new ReportExportService().ExportFrontendQualityReview(report, null);

        html.Should().Contain("No configured automated release block was detected.")
            .And.NotContain("Ready for release").And.NotContain("Approved for production").And.NotContain("WCAG compliant");
    }
}
