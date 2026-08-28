using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Moq;

namespace BirkNext.Web.Tests.Services;

public sealed class AccessibilityOrchestrationCallCountTests
{
    [Fact]
    public async Task AccessibilityDisabled_DoesNotInvokeEngine()
    {
        var accessibility = new AccessibilitySpy();
        await Create(accessibility).RunAsync("https://example.com", Context(accessibilityEnabled: false));
        Assert.Equal(0, accessibility.CallCount);
    }

    [Fact]
    public async Task AccessibilityEnabled_PreflightReady_InvokesOnce()
    {
        var accessibility = new AccessibilitySpy();
        var result = await Create(accessibility).RunAsync("https://example.com", Context());
        Assert.Equal(1, accessibility.CallCount);
        Assert.Equal(AccessibilityExecutionStatusDto.Assessed, result.AccessibilityReport?.ExecutionStatus);
    }

    [Fact]
    public async Task AccessibilityEnabled_PreflightBlocked_DoesNotInvokeEngine()
    {
        var accessibility = new AccessibilitySpy();
        await Create(accessibility, PreflightStatus.Unreachable).RunAsync("https://example.com", Context());
        Assert.Equal(0, accessibility.CallCount);
    }

    [Fact]
    public async Task AccessibilityEngineError_PreservesOtherEngines_AndProducesPartialAssessment()
    {
        var accessibility = new AccessibilitySpy(AccessibilityExecutionStatusDto.EngineError);
        var result = await Create(accessibility).RunAsync("https://example.com", Context());
        Assert.Equal(1, accessibility.CallCount);
        Assert.NotNull(result.SecurityReport);
        Assert.NotNull(result.PerformanceReport);
        Assert.Equal(AccessibilityExecutionStatusDto.EngineError, result.AccessibilityReport?.ExecutionStatus);
        Assert.Contains("Accessibility", result.QualityReport!.FailedEngines);
        Assert.Equal(AssessmentCompleteness.Partial, result.QualityReport.Completeness);
        Assert.Null(result.QualityReport.AccessibilityScore);
        Assert.NotNull(result.QualityReport.SecurityScore);
        Assert.NotNull(result.QualityReport.PerformanceScore);
    }

    private static FrontendQualityReviewOrchestrator Create(AccessibilitySpy accessibility, PreflightStatus preflight = PreflightStatus.Ready)
    {
        var readinessService = new Mock<IFrontendQualityEngineStatusApiService>();
        readinessService.Setup(s => s.RevalidateEngineReadinessAsync(It.IsAny<FrontendQualityEngineIdDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineReadinessReportDto { IsAvailable = true });

        return new(new Security(), new Performance(), new Preflight(preflight), new FrontendQualityReviewService(), null, accessibility, null, null, null, readinessService.Object);
    }

    private static FrontendAnalysisContext Context(bool accessibilityEnabled = true) => new()
    {
        TargetUrl = "https://example.com",
        EngineRequirements = new() { Accessibility = FrontendQualityEngineRequirement.Required },
        FeatureToggles = new FrontendAnalysisFeatureToggles
        {
            EnableSecurityEngine = true,
            EnablePerformanceEngine = true,
            EnableBrowserRuntimeEngine = false,
            EnableAccessibilityEngine = accessibilityEnabled
        }
    };

    private sealed class AccessibilitySpy(AccessibilityExecutionStatusDto status = AccessibilityExecutionStatusDto.Assessed) : IFrontendAccessibilityReviewApiService
    {
        public int CallCount { get; private set; }
        public Task<AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AccessibilityResultDto(
                status,
                AxeVersion: status == AccessibilityExecutionStatusDto.Assessed ? "4.13.0" : null,
                RequestedUrl: targetUrl,
                Limitations: ["Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required."],
                EngineError: status == AccessibilityExecutionStatusDto.EngineError ? "deterministic axe failure" : null));
        }
    }

    private sealed class Security : ISecurityScanner
    {
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
            Task.FromResult<(WasmSecurityReviewReport?, string?)>((new WasmSecurityReviewReport { Health = new WasmSecurityHealth { Score = 90 } }, null));
    }

    private sealed class Performance : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmPerformanceReviewReport());
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class Preflight(PreflightStatus status) : ITargetPreflightService
    {
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) => Task.FromResult(new TargetPreflightResult { Status = status, Message = "test" });
    }
}
