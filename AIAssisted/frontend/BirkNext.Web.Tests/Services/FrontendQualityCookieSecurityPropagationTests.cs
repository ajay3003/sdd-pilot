using BirkNext.RuntimeSecurity;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Regression (found by the runtime-security browser smoke): the orchestrator rebuilds the FQR report field by field after each engine
/// (accessibility, passive security …), and a field missing from one copy silently disappears. Static Security cookie metadata must reach
/// the final report whichever engines ran after it.
/// </summary>
public sealed class FrontendQualityCookieSecurityPropagationTests
{
    private static readonly CookieSecurityAssessment Cookies = CookieSecurityEvaluator.Evaluate(
        [SetCookieMetadataParser.Parse("Session=v; Path=/; SameSite=None", "app.example.test", CookieEvidenceSource.FrontendDocumentResponse)!],
        new CookieSecurityExpectations { AuthCookieNames = ["Session"] });

    private sealed class Security : ISecurityScanner
    {
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
            Task.FromResult<(WasmSecurityReviewReport?, string?)>((new WasmSecurityReviewReport { TargetUrl = request.TargetUrl, CookieSecurity = Cookies }, null));
    }

    private sealed class Performance : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmPerformanceReviewReport());
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class Preflight : ITargetPreflightService
    {
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) => Task.FromResult(new TargetPreflightResult { Status = PreflightStatus.Ready, Message = "Ready" });
    }

    private sealed class Accessibility : IFrontendAccessibilityReviewApiService
    {
        public Task<AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed));
    }

    [Fact]
    public async Task CookieMetadata_SurvivesTheReportCopiesOfLaterEngines()
    {
        var readiness = new Mock<IFrontendQualityEngineStatusApiService>();
        readiness.Setup(s => s.RevalidateEngineReadinessAsync(It.IsAny<FrontendQualityEngineIdDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineReadinessReportDto { IsAvailable = true, CheckedAtUtc = DateTime.UtcNow });
        var orchestrator = new FrontendQualityReviewOrchestrator(new Security(), new Performance(), new Preflight(), new FrontendQualityReviewService(),
            null, new Accessibility(), null, null, null, readiness.Object);
        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://app.example.test",
            FeatureToggles = new() { EnableSecurityEngine = true, EnableAccessibilityEngine = true, EnablePerformanceEngine = false, EnableBrowserRuntimeEngine = false, EnableLighthouseEngine = false, EnablePassiveSecurityEngine = false },
            ActiveProfile = new() { TargetUrl = "https://app.example.test" },
        };

        var result = await orchestrator.RunAsync("https://app.example.test", context);

        result.QualityReport!.CookieSecurity.Should().NotBeNull();
        result.QualityReport.CookieSecurity!.Cookies.Should().ContainSingle().Which.Classification.Should().Be(CookieClassification.DeclaredAuthOrSession);
    }
}
