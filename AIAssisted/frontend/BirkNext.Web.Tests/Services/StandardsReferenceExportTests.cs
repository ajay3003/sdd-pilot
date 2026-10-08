using BirkNext.ApiReview;
using BirkNext.Integrations;
using BirkNext.Standards;
using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Services;

public sealed class StandardsReferenceExportTests
{
    [Fact]
    public void FrontendExportKeepsWcagReferenceOnOneLogicalFinding()
    {
        var report = new FrontendQualityReviewReport
        {
            Findings =
            [
                new FrontendQualityFinding
                {
                    Id = "axe-contrast",
                    Title = "Contrast (Minimum)",
                    Category = FrontendQualityCategory.Accessibility,
                    StandardsReferences = [StandardsReferenceMappings.Wcag("1.4.3", "Contrast (Minimum)", StandardsEvidenceScope.BrowserRuntime)],
                },
            ],
        };

        var html = new ReportExportService().ExportFrontendQualityReview(report, "Fixture");

        Assert.Equal(1, Count(html, "Contrast (Minimum)"));
        Assert.Contains("WCAG 2.2 1.4.3", html);
        Assert.Contains("Direct; BrowserRuntime", html);
    }

    [Fact]
    public void ApiExportKeepsOwaspReferenceOnOneLogicalFinding()
    {
        var report = new ApiReviewReport
        {
            Findings =
            [
                new ApiReviewFinding
                {
                    Id = "api-header",
                    RuleId = "sec-no-hsts",
                    Title = "Missing HSTS",
                    StandardsReferences = [StandardsReferenceMappings.ForApiRule("sec-no-hsts")[0]],
                },
            ],
        };

        var html = new ReportExportService().ExportApiReview(report, "Fixture");

        Assert.Single(report.Findings);
        Assert.Equal(1, Count(html, "OWASP Top 10 A05:2021"));
        Assert.Contains("OWASP Top 10 A05:2021", html);
        Assert.Contains("Related; ApiRuntime", html);
    }

    [Fact]
    public void IntegrationExportKeepsIsoReferenceOnOneLogicalFinding()
    {
        var report = new IntegrationReviewResult
        {
            Findings =
            [
                new IntegrationReviewFinding
                {
                    Key = "contract-compatibility",
                    RuleId = "contract-incompatible",
                    Title = "Contract incompatibility",
                    StandardsReferences = [StandardsReferenceMappings.ForIntegrationRule("contract-incompatible")[0]],
                },
            ],
        };

        var html = new ReportExportService().ExportIntegrationReview(report, "Fixture");

        Assert.Equal(1, Count(html, "Contract incompatibility"));
        Assert.Contains("ISO/IEC 25010 Compatibility", html);
        Assert.Contains("Informational; IntegrationContract", html);
    }

    private static int Count(string value, string fragment) =>
        (value.Length - value.Replace(fragment, "", StringComparison.Ordinal).Length) / fragment.Length;
}
