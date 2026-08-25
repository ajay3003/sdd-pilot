using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Services;

public sealed class AccessibilityExportTrustTests
{
    [Fact]
    public void Export_IncludesAutomatedMetadata_WithoutClaimingConformance()
    {
        var report = new FrontendQualityReviewReport
        {
            AccessibilityReport = new AccessibilityResultDto(
                AccessibilityExecutionStatusDto.Assessed,
                AxeVersion: "4.13.0",
                BrowserName: "Chromium",
                BrowserVersion: "130.0.6723.31",
                RuleTags: ["wcag2a", "wcag22aa"],
                ViolationCount: 0,
                IncompleteCount: 1,
                Findings: [],
                Limitations: ["Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required."])
        };

        var html = new ReportExportService().ExportFrontendQualityReview(report, "test");

        Assert.Contains("Automated Accessibility Checks", html);
        Assert.Contains("axe-core", html);
        Assert.Contains("Needs manual review", html);
        Assert.Contains("Manual accessibility testing is still required", html);
        Assert.Contains("Zero automated violations does not establish WCAG conformance", html);
        Assert.DoesNotContain("WCAG compliant", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Accessibility passed", html, StringComparison.OrdinalIgnoreCase);
    }
}
