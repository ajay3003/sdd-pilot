using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Moq;

namespace BirkNext.Web.Tests.Services;

internal static class OrchestrationTestHelpers
{
    /// <summary>
    /// Create a mock readiness service that always reports engines as ready.
    /// Used in tests NOT concerned with Layer 3 readiness.
    /// Does NOT modify production fail-closed behavior.
    /// </summary>
    public static IFrontendQualityEngineStatusApiService CreateAlwaysReadyMockService()
    {
        var mock = new Mock<IFrontendQualityEngineStatusApiService>();
        mock.Setup(s => s.RevalidateEngineReadinessAsync(It.IsAny<FrontendQualityEngineIdDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineReadinessReportDto { IsAvailable = true, CheckedAtUtc = DateTime.UtcNow });
        return mock.Object;
    }

    /// <summary>
    /// Create an orchestrator with default AlwaysReady readiness.
    /// For tests NOT specifically testing readiness infrastructure.
    /// </summary>
    public static FrontendQualityReviewOrchestrator CreateOrchestrator(
        ISecurityScanner? security = null,
        IBlazorWasmPerformanceReviewService? performance = null,
        ITargetPreflightService? preflight = null,
        IFrontendQualityReviewService? quality = null,
        IFrontendBrowserRuntimeReviewApiService? runtime = null,
        IFrontendAccessibilityReviewApiService? accessibility = null,
        IFrontendLighthouseReviewApiService? lighthouse = null,
        IFrontendPassiveSecurityApiService? passiveSecurity = null,
        IAuthenticatedBrowserSessionService? authenticatedSessions = null,
        IFrontendQualityEngineStatusApiService? readiness = null)
    {
        return new FrontendQualityReviewOrchestrator(
            security ?? new MockSecurityScanner(),
            performance ?? new MockPerformanceScanner(),
            preflight ?? new MockPreflightService(),
            quality ?? new MockQualityReviewService(),
            runtime,
            accessibility,
            lighthouse,
            passiveSecurity,
            authenticatedSessions,
            readiness ?? CreateAlwaysReadyMockService());
    }

    private sealed class MockSecurityScanner : ISecurityScanner
    {
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
            Task.FromResult<(WasmSecurityReviewReport?, string?)>((new WasmSecurityReviewReport(), null));
    }

    private sealed class MockPerformanceScanner : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WasmPerformanceReviewReport());
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class MockPreflightService : ITargetPreflightService
    {
        private readonly PreflightStatus _status;
        public MockPreflightService(PreflightStatus status = PreflightStatus.Ready) => _status = status;
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) =>
            Task.FromResult(new TargetPreflightResult { Status = _status, Message = "test" });
    }

    private sealed class MockQualityReviewService : IFrontendQualityReviewService
    {
        public FrontendQualityReviewReport BuildReport(string targetUrl, WasmSecurityReviewReport? security, WasmPerformanceReviewReport? performance) =>
            new() { TargetUrl = targetUrl };
    }
}
