using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Tests that verify the orchestrator correctly calls (or doesn't call) the browser runtime engine
/// based on feature toggles and preflight status.
/// </summary>
public sealed class BrowserRuntimeOrchestrationCallCountTests
{
    [Fact]
    public async Task Orchestrator_BrowserRuntimeDisabled_SkipsExecution()
    {
        var mockSecurity = new MockSecurityScanner();
        var mockPerformance = new MockPerformanceScanner();
        var mockPreflight = new MockPreflightService(PreflightStatus.Ready);
        var mockQuality = new MockQualityReviewService();
        var mockRuntime = new MockBrowserRuntimeService();

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            mockSecurity,
            mockPerformance,
            mockPreflight,
            mockQuality,
            mockRuntime);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            EngineRequirements = new() { BrowserRuntime = FrontendQualityEngineRequirement.Required },
            FeatureToggles = new FrontendAnalysisFeatureToggles
            {
                EnableBrowserRuntimeEngine = false,
                EnableSecurityEngine = false,
                EnablePerformanceEngine = false
            }
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        Assert.Equal(0, mockRuntime.CallCount);
        Assert.Contains("BrowserRuntime", result.SkippedEngines);
    }

    [Fact]
    public async Task Orchestrator_BrowserRuntimeEnabled_InvokesOnce()
    {
        var mockSecurity = new MockSecurityScanner();
        var mockPerformance = new MockPerformanceScanner();
        var mockPreflight = new MockPreflightService(PreflightStatus.Ready);
        var mockQuality = new MockQualityReviewService();
        var mockRuntime = new MockBrowserRuntimeService();

        var orchestrator = new FrontendQualityReviewOrchestrator(
            mockSecurity,
            mockPerformance,
            mockPreflight,
            mockQuality,
            mockRuntime,
            null,
            null,
            null,
            null,
            OrchestrationTestHelpers.CreateAlwaysReadyMockService());

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new FrontendAnalysisFeatureToggles
            {
                EnableBrowserRuntimeEngine = true,
                EnableSecurityEngine = false,
                EnablePerformanceEngine = false
            }
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        Assert.Equal(1, mockRuntime.CallCount);
        Assert.NotNull(result.BrowserRuntimeReport);
    }

    [Fact]
    public async Task Orchestrator_PreflightBlocked_SkipsBrowserRuntime()
    {
        var mockSecurity = new MockSecurityScanner();
        var mockPerformance = new MockPerformanceScanner();
        var mockPreflight = new MockPreflightService(PreflightStatus.Unreachable);
        var mockQuality = new MockQualityReviewService();
        var mockRuntime = new MockBrowserRuntimeService();

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            mockSecurity,
            mockPerformance,
            mockPreflight,
            mockQuality,
            mockRuntime);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new FrontendAnalysisFeatureToggles
            {
                EnableBrowserRuntimeEngine = true,
                EnableSecurityEngine = true,
                EnablePerformanceEngine = true
            }
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        Assert.True(result.PreflightBlocked);
        Assert.Equal(0, mockRuntime.CallCount);
    }

    [Fact]
    public async Task Orchestrator_AllEnginesEnabled_InvokesAll()
    {
        var mockSecurity = new MockSecurityScanner();
        var mockPerformance = new MockPerformanceScanner();
        var mockPreflight = new MockPreflightService(PreflightStatus.Ready);
        var mockQuality = new MockQualityReviewService();
        var mockRuntime = new MockBrowserRuntimeService();

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            mockSecurity,
            mockPerformance,
            mockPreflight,
            mockQuality,
            mockRuntime);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new FrontendAnalysisFeatureToggles
            {
                EnableBrowserRuntimeEngine = true,
                EnableSecurityEngine = true,
                EnablePerformanceEngine = true
            },
            ActiveProfile = new FrontendAnalysisProfile
            {
                Performance = new FrontendPerformanceThresholds()
            }
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        Assert.NotNull(result.SecurityReport);
        Assert.NotNull(result.PerformanceReport);
        Assert.NotNull(result.BrowserRuntimeReport);
        Assert.Equal(1, mockRuntime.CallCount);
    }

    [Fact]
    public async Task RuntimeEngineError_PreservesSuccessfulEngines_AndProducesPartialAssessment()
    {
        var security = new MockSecurityScanner();
        var performance = new MockPerformanceScanner();
        var runtime = new MockBrowserRuntimeService(BrowserRuntimeEngineStatusDto.EngineError);
        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            security,
            performance,
            new MockPreflightService(PreflightStatus.Ready),
            new FrontendQualityReviewService(),
            runtime);
        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            EngineRequirements = new() { BrowserRuntime = FrontendQualityEngineRequirement.Required },
            FeatureToggles = new FrontendAnalysisFeatureToggles
            {
                EnableSecurityEngine = true,
                EnablePerformanceEngine = true,
                EnableBrowserRuntimeEngine = true
            }
        };

        var result = await orchestrator.RunAsync(context.TargetUrl, context);

        Assert.Equal(1, security.CallCount);
        Assert.Equal(1, performance.CallCount);
        Assert.Equal(1, runtime.CallCount);
        Assert.NotNull(result.SecurityReport);
        Assert.NotNull(result.PerformanceReport);
        Assert.Equal(BrowserRuntimeEngineStatusDto.EngineError, result.BrowserRuntimeReport?.Status);
        Assert.Equal(AssessmentCompleteness.Partial, result.QualityReport?.Completeness);
        Assert.Contains("Browser Runtime", result.QualityReport!.FailedEngines);
        Assert.NotNull(result.QualityReport.SecurityScore);
        Assert.NotNull(result.QualityReport.PerformanceScore);
    }

    private sealed class MockBrowserRuntimeService : IFrontendBrowserRuntimeReviewApiService
    {
        private readonly BrowserRuntimeEngineStatusDto _status;
        public MockBrowserRuntimeService(BrowserRuntimeEngineStatusDto status = BrowserRuntimeEngineStatusDto.Assessed) =>
            _status = status;
        public int CallCount { get; private set; }

        public Task<BrowserRuntimeResultDto> ReviewAsync(
            string targetUrl,
            int navigationTimeoutMs = 30000,
            int startupObservationMs = 5000,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new BrowserRuntimeResultDto(
                Status: _status,
                EngineError: _status == BrowserRuntimeEngineStatusDto.EngineError ? "deterministic runtime failure" : null,
                RequestedUrl: targetUrl));
        }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class MockSecurityScanner : ISecurityScanner
    {
        public int CallCount { get; private set; }
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
            Task.FromResult<(WasmSecurityReviewReport?, string?)>((CreateReport(), null));

        private WasmSecurityReviewReport CreateReport()
        {
            CallCount++;
            return new WasmSecurityReviewReport { Health = new WasmSecurityHealth { Score = 90 } };
        }
    }

    private sealed class MockPerformanceScanner : IBlazorWasmPerformanceReviewService
    {
        public int CallCount { get; private set; }
        public Task<WasmPerformanceReviewReport> RunReviewAsync(
            string targetUrl,
            FrontendPerformanceThresholds? thresholds = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateReport());

        private WasmPerformanceReviewReport CreateReport()
        {
            CallCount++;
            return new WasmPerformanceReviewReport();
        }

        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(
            string targetUrl,
            FrontendPerformanceThresholds? thresholds = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WasmAssetDiscoveryResult());

        public WasmPerformanceReviewReport? GetCached() => null;

        public void ClearCache() { }
    }

    private sealed class MockPreflightService : ITargetPreflightService
    {
        private readonly PreflightStatus _status;

        public MockPreflightService(PreflightStatus status) => _status = status;

        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) =>
            Task.FromResult(new TargetPreflightResult { Status = _status, Message = "Test" });
    }

    private sealed class MockQualityReviewService : IFrontendQualityReviewService
    {
        public Task<FrontendQualityReviewReport> RunReviewAsync(
            string targetUrl,
            FrontendAnalysisContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FrontendQualityReviewReport());

        public FrontendQualityReviewReport BuildReport(
            string targetUrl,
            WasmSecurityReviewReport? securityReport,
            WasmPerformanceReviewReport? performanceReport) =>
            new FrontendQualityReviewReport();
    }
}
