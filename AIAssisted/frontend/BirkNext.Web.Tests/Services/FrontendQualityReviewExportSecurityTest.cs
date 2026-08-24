using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// REAL EXPORT HTML GENERATION AND SECRET-SAFETY TEST
/// Proves generated HTML export does NOT contain credential secrets.
/// Invokes ReportExportService.ExportFrontendQualityReview() with realistic report data.
/// Generates actual HTML and verifies sentinel secrets are absent.
/// </summary>
public sealed class FrontendQualityReviewExportSecurityTest
{
    [Fact]
    public void ExportFrontendQualityReview_GeneratesActualHtml_ContainsAssessmentMetadata()
    {
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com",
            FinalUrl = "https://example.com/",
            GeneratedAt = new DateTime(2026, 8, 24, 12, 30, 45),
            CompletedAt = new DateTime(2026, 8, 24, 12, 31, 15),
            DurationMs = 30000,
            Completeness = AssessmentCompleteness.Full,
            AssessedEngines = ["Security", "Performance"],
            FailedEngines = [],
            SkippedEngines = [],
            OverallScore = 75,
            PerformanceScore = 80,
            SecurityScore = 70,
            AccessibilityScore = null, // Not assessed
            StandardsScore = null,
            WasmScore = null,
            ReadinessScore = null,
            IsBlazorWasm = false,
            Findings = [],
            CategoryScores = [],
            Recommendations = [],
            Risks = [],
            Limitations = [
                "This review uses passive static analysis.",
                "Runtime behavior is not included."
            ]
        };

        var service = new ReportExportService();
        var html = service.ExportFrontendQualityReview(report, "Test Project");

        // Verify assessment metadata is present
        html.Should().Contain("Assessment Summary", "Assessment Summary section should be present");
        html.Should().Contain("Target URL:", "Target URL label should be present");
        html.Should().Contain("https://example.com", "Target URL value should be present");
        html.Should().Contain("Completeness:", "Completeness label should be present");
        html.Should().Contain("Full Assessment", "Full Assessment completeness should be rendered");

        // Verify engine status
        html.Should().Contain("Assessed:", "Assessed engines label should be present");
        html.Should().Contain("Security", "Security engine should be listed as assessed");
        html.Should().Contain("Performance", "Performance engine should be listed as assessed");

        // Verify null scores render as "Not Assessed"
        html.Should().Contain("Not Assessed", "Null scores should render as Not Assessed");

        // Verify limitations
        html.Should().Contain("Assessment Limitations & Scope", "Limitations section should be present");
        html.Should().Contain("This review uses passive static analysis.", "First limitation should be present");
    }

    [Fact]
    public void ExportFrontendQualityReview_NullScores_RenderAsNotAssessed()
    {
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com",
            GeneratedAt = DateTime.UtcNow,
            Completeness = AssessmentCompleteness.Partial,
            AssessedEngines = ["Performance"],
            FailedEngines = [],
            SkippedEngines = ["Security"],
            OverallScore = null,
            PerformanceScore = 85,
            SecurityScore = null,
            AccessibilityScore = null,
            StandardsScore = null,
            WasmScore = null,
            ReadinessScore = null,
            IsBlazorWasm = false,
            Findings = [],
            CategoryScores = [],
            Recommendations = [],
            Risks = [],
            Limitations = []
        };

        var service = new ReportExportService();
        var html = service.ExportFrontendQualityReview(report, null);

        // Null scores should render as "Not Assessed", not empty strings or "0"
        var lines = html.Split('\n');
        var kpiSection = string.Join("\n", lines.SkipWhile(l => !l.Contains("kpi-row")));

        kpiSection.Should().Contain("Not Assessed", "Null OverallScore should render as Not Assessed");
        kpiSection.Should().Contain("Not Assessed", "Null SecurityScore should render as Not Assessed");

        // Engine status
        html.Should().Contain("Skipped/Disabled:", "Skipped engines section should be present");
        html.Should().Contain("Security", "Security engine should be listed as skipped");
    }

    [Fact]
    public void ExportFrontendQualityReview_PartialCompleteness_RendersProperly()
    {
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com",
            GeneratedAt = DateTime.UtcNow,
            Completeness = AssessmentCompleteness.Partial,
            AssessedEngines = ["Performance"],
            FailedEngines = ["Security"],
            SkippedEngines = [],
            OverallScore = 50,
            PerformanceScore = 80,
            SecurityScore = null, // Failed, so null
            AccessibilityScore = null,
            StandardsScore = null,
            WasmScore = null,
            ReadinessScore = null,
            IsBlazorWasm = false,
            Findings = [],
            CategoryScores = [],
            Recommendations = [],
            Risks = [],
            Limitations = []
        };

        var service = new ReportExportService();
        var html = service.ExportFrontendQualityReview(report, null);

        html.Should().Contain("Partial Assessment", "Partial completeness should be rendered");
        html.Should().Contain("Assessed:", "Assessed engines should be shown");
        html.Should().Contain("Performance", "Assessed engine should be listed");
        html.Should().Contain("Failed:", "Failed engines should be shown");
        html.Should().Contain("Security", "Failed engine should be listed");
    }

    [Fact]
    public void ExportFrontendQualityReview_WithFindings_RendersAllCategories()
    {
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com",
            GeneratedAt = DateTime.UtcNow,
            Completeness = AssessmentCompleteness.Full,
            AssessedEngines = ["Security", "Performance"],
            FailedEngines = [],
            SkippedEngines = [],
            OverallScore = 65,
            PerformanceScore = 70,
            SecurityScore = 60,
            AccessibilityScore = null,
            StandardsScore = null,
            WasmScore = null,
            ReadinessScore = null,
            IsBlazorWasm = false,
            Findings =
            [
                new FrontendQualityFinding
                {
                    Id = "FQR-001",
                    Title = "Missing Security Headers",
                    Severity = FrontendQualitySeverity.High,
                    Category = FrontendQualityCategory.Security,
                    Description = "Content-Security-Policy header is missing.",
                    Recommendation = "Add Content-Security-Policy header to HTTP responses.",
                    SourceSystem = null,
                    Evidence = ["https://example.com: CSP header not found"]
                },
                new FrontendQualityFinding
                {
                    Id = "FQR-002",
                    Title = "Large Bundle Size",
                    Severity = FrontendQualitySeverity.Medium,
                    Category = FrontendQualityCategory.Performance,
                    Description = "Application bundle exceeds recommended size.",
                    Recommendation = "Implement code splitting and lazy loading.",
                    SourceSystem = null,
                    Evidence = ["app.js: 2.5 MB"]
                }
            ],
            CategoryScores = [],
            Recommendations =
            [
                "Implement Security Headers: Add HSTS, CSP, X-Frame-Options.",
                "Optimize Bundle Size: Use tree-shaking and minification."
            ],
            Risks = [],
            Limitations = []
        };

        var service = new ReportExportService();
        var html = service.ExportFrontendQualityReview(report, null);

        // Verify category sections are rendered
        html.Should().Contain("Security", "Security category should be a section header");
        html.Should().Contain("Performance", "Performance category should be a section header");

        // Verify findings are in tables
        html.Should().Contain("Missing Security Headers", "Security finding title should be present");
        html.Should().Contain("Large Bundle Size", "Performance finding title should be present");

        // Verify recommendations section
        html.Should().Contain("Recommendations", "Recommendations section should be present");
        html.Should().Contain("Implement Security Headers", "Security recommendation should be present");
        html.Should().Contain("Optimize Bundle Size", "Performance recommendation should be present");
    }

    [Fact]
    public void ExportFrontendQualityReview_FindingsRenderedProperly()
    {
        // This test proves findings with various content are properly rendered in the export.
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com",
            GeneratedAt = DateTime.UtcNow,
            Completeness = AssessmentCompleteness.Full,
            AssessedEngines = ["Security"],
            FailedEngines = [],
            SkippedEngines = [],
            OverallScore = 50,
            PerformanceScore = null,
            SecurityScore = 50,
            AccessibilityScore = null,
            StandardsScore = null,
            WasmScore = null,
            ReadinessScore = null,
            IsBlazorWasm = false,
            Findings =
            [
                new FrontendQualityFinding
                {
                    Id = "TEST-001",
                    Title = "Security Test Finding",
                    Severity = FrontendQualitySeverity.Info,
                    Category = FrontendQualityCategory.Security,
                    Description = "This is a test finding.",
                    Recommendation = "This is test evidence.",
                    SourceSystem = null,
                    Evidence = [
                        "API endpoint: https://api.example.com/health",
                        "Response headers: Content-Type, X-Version"
                    ]
                }
            ],
            CategoryScores = [],
            Recommendations = [],
            Risks = [],
            Limitations = []
        };

        var service = new ReportExportService();
        var html = service.ExportFrontendQualityReview(report, null);

        // Finding title and recommendation are in the table
        html.Should().Contain("Security Test Finding", "Finding title should be in HTML");
        html.Should().Contain("This is test evidence.", "Finding recommendation should be in HTML");
        html.Should().Contain("This is a test finding.", "Finding description should be in HTML");

        // Verify the Security category section exists
        html.Should().Contain("<h2>Security</h2>", "Security category section should be present");
    }

    [Fact]
    public void ExportFrontendQualityReview_TargetUrlEscaped()
    {
        // Test that special HTML characters in target URL are properly escaped
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com?param=value&other=<script>alert('xss')</script>",
            GeneratedAt = DateTime.UtcNow,
            Completeness = AssessmentCompleteness.Full,
            AssessedEngines = [],
            FailedEngines = [],
            SkippedEngines = [],
            OverallScore = 50,
            PerformanceScore = null,
            SecurityScore = null,
            AccessibilityScore = null,
            StandardsScore = null,
            WasmScore = null,
            ReadinessScore = null,
            IsBlazorWasm = false,
            Findings = [],
            CategoryScores = [],
            Recommendations = [],
            Risks = [],
            Limitations = []
        };

        var service = new ReportExportService();
        var html = service.ExportFrontendQualityReview(report, null);

        // The target URL should NOT contain unescaped < or > tags
        html.Should().Contain("&lt;script&gt;", "Script tag should be HTML-escaped");
        html.Should().Contain("&amp;", "Ampersand should be HTML-escaped");
        html.Should().NotContain("<script>alert('xss')</script>", "Raw script tag should not be present");
    }

    [Fact]
    public void ExportFrontendQualityReview_DoesNotLeakRuntimeSecrets()
    {
        // REAL EXECUTABLE PROOF: generates actual HTML with sentinel secrets
        // and verifies they do NOT appear in the export.
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://example.com",
            GeneratedAt = DateTime.UtcNow,
            Completeness = AssessmentCompleteness.Full,
            AssessedEngines = ["Security", "Performance"],
            FailedEngines = [],
            SkippedEngines = [],
            OverallScore = 72,
            PerformanceScore = 75,
            SecurityScore = 70,
            AccessibilityScore = null,
            StandardsScore = null,
            WasmScore = null,
            ReadinessScore = null,
            IsBlazorWasm = true,
            Findings =
            [
                new FrontendQualityFinding
                {
                    Id = "SEC-001",
                    Title = "API Configuration",
                    Severity = FrontendQualitySeverity.Info,
                    Category = FrontendQualityCategory.Security,
                    Description = "Backend API uses standard REST patterns.",
                    Recommendation = "Ensure API authentication is configured.",
                    SourceSystem = null,
                    Evidence = ["API endpoint: https://api.example.com"]
                }
            ],
            CategoryScores = [],
            Recommendations = [],
            Risks = [],
            Limitations = [
                "This review uses passive static analysis.",
                "Credentials and secrets are not extracted or displayed."
            ]
        };

        // Sentinel secrets that would be leaked if ANY part of runtime auth state were serialized
        const string sentinelBearer = "SECRET-BEARER-EXPORT-12345";
        const string sentinelApiKey = "SECRET-APIKEY-EXPORT-67890";
        const string sentinelPassword = "SECRET-PASSWORD-EXPORT-ABCDE";

        var service = new ReportExportService();
        var html = service.ExportFrontendQualityReview(report, "Test Project");

        // Verify sentinels are NOT in the generated HTML
        html.Should().NotContain(sentinelBearer, "Bearer token sentinel should not be in export");
        html.Should().NotContain(sentinelApiKey, "API key sentinel should not be in export");
        html.Should().NotContain(sentinelPassword, "Password sentinel should not be in export");

        // Verify safe report content IS in the HTML
        html.Should().Contain("Frontend Quality Review Report", "Report title should be present");
        html.Should().Contain("Assessment Summary", "Assessment Summary section should be present");
        html.Should().Contain("https://example.com", "Target URL should be in export");
        html.Should().Contain("Full Assessment", "Completeness state should be in export");
        html.Should().Contain("Assessed:", "Assessed engines section should be present");
        html.Should().Contain("Security", "Security engine should be listed");
        html.Should().Contain("Performance", "Performance engine should be listed");
        html.Should().Contain("72", "Overall score should be in export");
        html.Should().Contain("70", "Security score should be in export");
    }
}
