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
        return new AlwaysReadyService();
    }

    private sealed class AlwaysReadyService : IFrontendQualityEngineStatusApiService
    {
        public Task<FrontendQualityEngineStatusReportDto?> GetStatusAsync(
            ReviewAuthenticationModeDto authMode = ReviewAuthenticationModeDto.Anonymous,
            ReviewEngineSelectionDto? selection = null,
            CancellationToken ct = default) => Task.FromResult<FrontendQualityEngineStatusReportDto?>(null);

        public Task<FrontendQualityEngineReadinessReportDto?> RevalidateEngineReadinessAsync(
            FrontendQualityEngineIdDto engineId,
            CancellationToken ct = default) =>
            Task.FromResult<FrontendQualityEngineReadinessReportDto?>(
                new FrontendQualityEngineReadinessReportDto { IsAvailable = true, CheckedAtUtc = DateTime.UtcNow });
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
        IFrontendQualityEngineStatusApiService? readiness = null,
        IBrowserQualityEvidenceSource? browserQuality = null,
        IPerformanceQualityEvidenceSource? performanceQuality = null)
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
            readiness ?? CreateAlwaysReadyMockService(),
            accessResolver: null,
            apiSurface: null,
            browserQuality: browserQuality,
            performanceQuality: performanceQuality);
    }

    /// <summary>Performance Quality evidence source that reports one page with complete browser + API evidence and no findings.</summary>
    public sealed class AssessedPerformanceQualitySource : IPerformanceQualityEvidenceSource
    {
        public int CallCount { get; private set; }
        public Task<PerformanceQualityReviewResult> CollectAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var coverage = new PerformanceCoverage
            {
                Browser = PerformanceCoverageState.Complete, Runtime = PerformanceCoverageState.Complete, Resources = PerformanceCoverageState.Complete,
                Api = PerformanceCoverageState.Complete, Blazor = PerformanceCoverageState.Complete,
            };
            return Task.FromResult(new PerformanceQualityReviewResult
            {
                CompanionState = BirkNext.BrowserCompanion.BrowserCompanionState.Connected, CompanionMessage = "1 page(s) with performance evidence (Complete assessment).",
                ProxyEvidenceAvailable = true, Coverage = coverage, EvaluatedAt = DateTimeOffset.UtcNow, BrowserName = "Microsoft Edge",
                Pages = [new PagePerformanceSnapshot { PageId = context.TargetUrl.TrimEnd('/') + "/", PageTitle = "/", Generation = 1, Coverage = coverage }],
            });
        }
    }

    /// <summary>Performance Quality evidence source with no evidence at all: the engine must report "not assessed", never "no findings".</summary>
    public sealed class NoEvidencePerformanceQualitySource : IPerformanceQualityEvidenceSource
    {
        public Task<PerformanceQualityReviewResult> CollectAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PerformanceQualityReviewResult
            {
                CompanionState = BirkNext.BrowserCompanion.BrowserCompanionState.NotPaired, CompanionMessage = PerformanceQualityEvidenceSource.NoEvidenceMessage,
                Coverage = new PerformanceCoverage { Reasons = ["No performance evidence collected for any page."] }, EvaluatedAt = DateTimeOffset.UtcNow,
            });
    }

    /// <summary>Browser Quality evidence source that reports a connected companion with one assessed page (no findings).</summary>
    public sealed class AssessedBrowserQualitySource : IBrowserQualityEvidenceSource
    {
        public int CallCount { get; private set; }
        public Task<BrowserQualityReviewResult> CollectAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new BrowserQualityReviewResult
            {
                CompanionState = BirkNext.BrowserCompanion.BrowserCompanionState.Connected, CompanionMessage = "Browser Companion connected; 1 page(s) with evidence.",
                PagesWithEvidence = 1, PageIdentities = [context.TargetUrl.TrimEnd('/') + "/"], Findings = [], EvaluatedAt = DateTimeOffset.UtcNow, BrowserName = "Microsoft Edge",
            });
        }
    }

    /// <summary>Browser Quality evidence source that reports "not connected" (the accurate blocker, never a timeout).</summary>
    public sealed class DisconnectedBrowserQualitySource : IBrowserQualityEvidenceSource
    {
        public Task<BrowserQualityReviewResult> CollectAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowserQualityReviewResult { CompanionState = BirkNext.BrowserCompanion.BrowserCompanionState.NotPaired, CompanionMessage = BrowserQualityEvidenceSource.NotConnectedMessage, EvaluatedAt = DateTimeOffset.UtcNow });
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
