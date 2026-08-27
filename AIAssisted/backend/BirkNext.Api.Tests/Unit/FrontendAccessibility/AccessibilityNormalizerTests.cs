using System.Text.Json;
using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Unit.FrontendAccessibility;

public sealed class AccessibilityNormalizerTests
{
    [Theory]
    [InlineData("critical", AccessibilityFindingSeverity.Critical)]
    [InlineData("serious", AccessibilityFindingSeverity.High)]
    [InlineData("moderate", AccessibilityFindingSeverity.Medium)]
    [InlineData("minor", AccessibilityFindingSeverity.Low)]
    [InlineData(null, AccessibilityFindingSeverity.Info)]
    public void Impact_MapsDeterministically(string? impact, AccessibilityFindingSeverity expected) =>
        Assert.Equal(expected, AccessibilityNormalizer.MapSeverity(impact));

    [Fact]
    public void Normalize_AggregatesNodes_BoundsEvidence_AndSanitizesSecrets()
    {
        var longText = new string('x', 600);
        using var json = JsonDocument.Parse("""
        [{"id":"button-name","impact":"critical","help":"Buttons must have discernible text","description":"test","tags":["wcag2a"],"helpUrl":"https://deque.test/rule","nodes":[
          {"target":["#secret-button"],"html":"<button value='SECRET-AXE-DOM-12345'></button>","failureSummary":"SECRET-AXE-DOM-12345"},
          {"target":["button.second"],"html":"<button></button>","failureSummary":"REPLACE_LONG"}
        ]}]
        """.Replace("REPLACE_LONG", longText));

        var finding = Assert.Single(new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer())
            .Normalize(json.RootElement, AccessibilityFindingKind.Violation));

        Assert.Equal(2, finding.AffectedNodeCount);
        Assert.Contains("#secret-button", finding.Selectors);
        Assert.DoesNotContain("SECRET-AXE-DOM-12345", string.Join(" ", finding.HtmlSnippets.Concat(finding.FailureSummaries)));
        Assert.All(finding.FailureSummaries, value => Assert.True(value.Length <= AccessibilityEvidenceSanitizer.MaxSummaryLength + 1));
    }

    [Fact]
    public void Normalize_Incomplete_RemainsNeedsManualReview()
    {
        using var json = JsonDocument.Parse("[{\"id\":\"color-contrast\",\"impact\":null,\"help\":\"review\",\"description\":\"review\",\"tags\":[],\"nodes\":[]}]");
        var finding = Assert.Single(new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer())
            .Normalize(json.RootElement, AccessibilityFindingKind.NeedsManualReview));
        Assert.Equal(AccessibilityFindingKind.NeedsManualReview, finding.Kind);
    }

    [Fact]
    public void TrustWording_RequiresManualTesting() =>
        Assert.Equal("Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required.",
            FrontendAccessibilityReviewService.ManualTestingLimitation);

    [Fact]
    public async Task AccessibilityNavigation_UsesBrowserTargetSafetyPolicy()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FrontendAccessibilityOptions { Enabled = true });
        var service = new FrontendAccessibilityReviewService(
            NullLogger<FrontendAccessibilityReviewService>.Instance,
            new BrowserTargetValidator(),
            new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer()),
            options);
        var result = await service.ReviewAsync("file:///etc/passwd");
        Assert.Equal(AccessibilityExecutionStatus.Skipped, result.ExecutionStatus);
        Assert.Contains("not allowed", result.EngineError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticatedTarget_IsNotMisreportedAsAssessed()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FrontendAccessibilityOptions { Enabled = true });
        var service = new FrontendAccessibilityReviewService(
            NullLogger<FrontendAccessibilityReviewService>.Instance,
            new BrowserTargetValidator(),
            new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer()),
            options);
        var result = await service.ReviewAsync("https://example.com", requiresAuthentication: true);
        Assert.Equal(AccessibilityExecutionStatus.AuthenticationRequired, result.ExecutionStatus);
        Assert.Null(result.AxeVersion);
    }
}
