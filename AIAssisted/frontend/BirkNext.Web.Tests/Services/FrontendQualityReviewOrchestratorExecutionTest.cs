using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// REAL FEATURE TOGGLE AND PREFLIGHT EXECUTION PROOF TESTS
/// Tests actual call counts to verify disabled scanners are not invoked.
/// Uses hand-written spies on real production services.
/// </summary>
public sealed class FrontendQualityReviewOrchestratorExecutionTest
{
    private sealed class SpySecurityScanner : ISecurityScanner
    {
        public int CallCount { get; private set; }

        public async Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request)
        {
            CallCount++;
            return (new WasmSecurityReviewReport { TargetUrl = request.TargetUrl }, null);
        }
    }

    private sealed class SpyPerformanceService : IBlazorWasmPerformanceReviewService
    {
        public int CallCount { get; private set; }

        public async Task<WasmPerformanceReviewReport> RunReviewAsync(
            string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return new WasmPerformanceReviewReport
            {
                TargetUrl = targetUrl,
                ReviewedAt = DateTime.UtcNow,
                IsBlazorWasm = false,
                Assets = []
            };
        }

        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(
            string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class FakePreflightService : ITargetPreflightService
    {
        public PreflightStatus Status { get; set; } = PreflightStatus.Ready;
        public string Message { get; set; } = "Ready";

        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) =>
            Task.FromResult(new TargetPreflightResult
            {
                Status = Status,
                Message = Message,
                IsBlazorWasm = false,
                FinalUrl = targetUrl
            });
    }

    [Fact]
    public async Task Orchestrate_SecurityToggleDisabled_SecurityScannerCallCountZero()
    {
        var spy = new SpySecurityScanner();
        var perfSpy = new SpyPerformanceService();
        var preflightFake = new FakePreflightService { Status = PreflightStatus.Ready };

        var mockQuality = new MockQualityService();
        ISecurityScanner scannerAdapter = spy; // Cast to interface
        var orchestrator = new FrontendQualityReviewOrchestrator(scannerAdapter, perfSpy, preflightFake, mockQuality);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new() { EnableSecurityEngine = false, EnablePerformanceEngine = true },
            ActiveProfile = new() { Performance = new() },
            AllowedBackendDomains = [],
            AllowedRestHosts = [],
            AllowedGraphQlEndpoints = [],
            AllowedCdnHosts = [],
            SecuritySettings = new(),
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        // Actual call count assertions
        spy.CallCount.Should().Be(0, "security scanner should NOT be invoked when toggle is disabled");
        perfSpy.CallCount.Should().Be(1, "performance scanner SHOULD be invoked when toggle is enabled");

        // State assertions
        result.SkippedEngines.Should().Contain("Security", "Security should be in SkippedEngines");
        result.SecurityReport.Should().BeNull("SecurityReport should be null when scanner skipped");
    }

    [Fact]
    public async Task Orchestrate_PerformanceToggleDisabled_PerformanceScannerCallCountZero()
    {
        var spy = new SpySecurityScanner();
        var perfSpy = new SpyPerformanceService();
        var preflightFake = new FakePreflightService { Status = PreflightStatus.Ready };

        var mockQuality = new MockQualityService();
        ISecurityScanner scannerAdapter = spy;
        var orchestrator = new FrontendQualityReviewOrchestrator(scannerAdapter, perfSpy, preflightFake, mockQuality);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new() { EnableSecurityEngine = true, EnablePerformanceEngine = false },
            ActiveProfile = new() { Performance = new() },
            AllowedBackendDomains = [],
            AllowedRestHosts = [],
            AllowedGraphQlEndpoints = [],
            AllowedCdnHosts = [],
            SecuritySettings = new(),
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        spy.CallCount.Should().Be(1, "security scanner SHOULD be invoked when toggle is enabled");
        perfSpy.CallCount.Should().Be(0, "performance scanner should NOT be invoked when toggle is disabled");

        result.SkippedEngines.Should().Contain("Performance", "Performance should be in SkippedEngines");
        result.PerformanceReport.Should().BeNull("PerformanceReport should be null when scanner skipped");
    }

    [Fact]
    public async Task Orchestrate_BothToggleDisabled_BothScannersCallCountZero()
    {
        var spy = new SpySecurityScanner();
        var perfSpy = new SpyPerformanceService();
        var preflightFake = new FakePreflightService { Status = PreflightStatus.Ready };

        var mockQuality = new MockQualityService();
        ISecurityScanner scannerAdapter = spy;
        var orchestrator = new FrontendQualityReviewOrchestrator(scannerAdapter, perfSpy, preflightFake, mockQuality);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new() { EnableSecurityEngine = false, EnablePerformanceEngine = false },
            ActiveProfile = new() { Performance = new() },
            AllowedBackendDomains = [],
            AllowedRestHosts = [],
            AllowedGraphQlEndpoints = [],
            AllowedCdnHosts = [],
            SecuritySettings = new(),
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        spy.CallCount.Should().Be(0, "security scanner should NOT be invoked");
        perfSpy.CallCount.Should().Be(0, "performance scanner should NOT be invoked");

        result.SkippedEngines.Should().Contain("Security");
        result.SkippedEngines.Should().Contain("Performance");
        result.SecurityReport.Should().BeNull();
        result.PerformanceReport.Should().BeNull();
    }

    [Fact]
    public async Task Orchestrate_PreflightUnreachable_BothScannersCallCountZero()
    {
        var spy = new SpySecurityScanner();
        var perfSpy = new SpyPerformanceService();
        var preflightFake = new FakePreflightService
        {
            Status = PreflightStatus.Unreachable,
            Message = "Target unreachable"
        };

        var mockQuality = new MockQualityService();
        ISecurityScanner scannerAdapter = spy;
        var orchestrator = new FrontendQualityReviewOrchestrator(scannerAdapter, perfSpy, preflightFake, mockQuality);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://unreachable.invalid",
            FeatureToggles = new() { EnableSecurityEngine = true, EnablePerformanceEngine = true },
            ActiveProfile = new() { Performance = new() },
            AllowedBackendDomains = [],
            AllowedRestHosts = [],
            AllowedGraphQlEndpoints = [],
            AllowedCdnHosts = [],
            SecuritySettings = new(),
        };

        var result = await orchestrator.RunAsync("https://unreachable.invalid", context);

        spy.CallCount.Should().Be(0, "security scanner should NOT be invoked when preflight blocks");
        perfSpy.CallCount.Should().Be(0, "performance scanner should NOT be invoked when preflight blocks");

        result.PreflightBlocked.Should().BeTrue("preflight should block");
        result.PreflightStatus.Should().Be(PreflightStatus.Unreachable);
    }

    [Fact]
    public async Task Orchestrate_PreflightInvalidTarget_BothScannersCallCountZero()
    {
        var spy = new SpySecurityScanner();
        var perfSpy = new SpyPerformanceService();
        var preflightFake = new FakePreflightService
        {
            Status = PreflightStatus.InvalidTarget,
            Message = "Invalid URL"
        };

        var mockQuality = new MockQualityService();
        ISecurityScanner scannerAdapter = spy;
        var orchestrator = new FrontendQualityReviewOrchestrator(scannerAdapter, perfSpy, preflightFake, mockQuality);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "not-a-url",
            FeatureToggles = new() { EnableSecurityEngine = true, EnablePerformanceEngine = true },
            ActiveProfile = new() { Performance = new() },
            AllowedBackendDomains = [],
            AllowedRestHosts = [],
            AllowedGraphQlEndpoints = [],
            AllowedCdnHosts = [],
            SecuritySettings = new(),
        };

        var result = await orchestrator.RunAsync("not-a-url", context);

        spy.CallCount.Should().Be(0);
        perfSpy.CallCount.Should().Be(0);
        result.PreflightBlocked.Should().BeTrue();
        result.PreflightStatus.Should().Be(PreflightStatus.InvalidTarget);
    }

    [Fact]
    public async Task Orchestrate_PreflightAuthRequired_BothScannersCallCountZero()
    {
        var spy = new SpySecurityScanner();
        var perfSpy = new SpyPerformanceService();
        var preflightFake = new FakePreflightService
        {
            Status = PreflightStatus.AuthenticationRequired,
            Message = "Target requires authentication"
        };

        var mockQuality = new MockQualityService();
        ISecurityScanner scannerAdapter = spy;
        var orchestrator = new FrontendQualityReviewOrchestrator(scannerAdapter, perfSpy, preflightFake, mockQuality);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://protected.example.com",
            FeatureToggles = new() { EnableSecurityEngine = true, EnablePerformanceEngine = true },
            ActiveProfile = new() { Performance = new() },
            AllowedBackendDomains = [],
            AllowedRestHosts = [],
            AllowedGraphQlEndpoints = [],
            AllowedCdnHosts = [],
            SecuritySettings = new(),
        };

        var result = await orchestrator.RunAsync("https://protected.example.com", context);

        spy.CallCount.Should().Be(0);
        perfSpy.CallCount.Should().Be(0);
        result.PreflightBlocked.Should().BeTrue();
        result.PreflightStatus.Should().Be(PreflightStatus.AuthenticationRequired);
    }

    [Fact]
    public async Task Orchestrate_PreflightScannerUnavailable_BothScannersCallCountZero()
    {
        var spy = new SpySecurityScanner();
        var perfSpy = new SpyPerformanceService();
        var preflightFake = new FakePreflightService
        {
            Status = PreflightStatus.ScannerUnavailable,
            Message = "Scanner service unavailable"
        };

        var mockQuality = new MockQualityService();
        ISecurityScanner scannerAdapter = spy;
        var orchestrator = new FrontendQualityReviewOrchestrator(scannerAdapter, perfSpy, preflightFake, mockQuality);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new() { EnableSecurityEngine = true, EnablePerformanceEngine = true },
            ActiveProfile = new() { Performance = new() },
            AllowedBackendDomains = [],
            AllowedRestHosts = [],
            AllowedGraphQlEndpoints = [],
            AllowedCdnHosts = [],
            SecuritySettings = new(),
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        spy.CallCount.Should().Be(0);
        perfSpy.CallCount.Should().Be(0);
        result.PreflightBlocked.Should().BeTrue();
        result.PreflightStatus.Should().Be(PreflightStatus.ScannerUnavailable);
    }

    [Fact]
    public async Task Orchestrate_PreflightReadyWithWarnings_ScannersInvoked()
    {
        var spy = new SpySecurityScanner();
        var perfSpy = new SpyPerformanceService();
        var preflightFake = new FakePreflightService
        {
            Status = PreflightStatus.ReadyWithWarnings,
            Message = "Ready with some warnings"
        };

        var mockQuality = new MockQualityService();
        ISecurityScanner scannerAdapter = spy;
        var orchestrator = new FrontendQualityReviewOrchestrator(scannerAdapter, perfSpy, preflightFake, mockQuality);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new() { EnableSecurityEngine = true, EnablePerformanceEngine = true },
            ActiveProfile = new() { Performance = new() },
            AllowedBackendDomains = [],
            AllowedRestHosts = [],
            AllowedGraphQlEndpoints = [],
            AllowedCdnHosts = [],
            SecuritySettings = new(),
        };

        var result = await orchestrator.RunAsync("https://example.com", context);

        spy.CallCount.Should().Be(1, "security scanner SHOULD be invoked despite warnings");
        perfSpy.CallCount.Should().Be(1, "performance scanner SHOULD be invoked despite warnings");
        result.PreflightBlocked.Should().BeFalse("preflight should NOT block with warnings");
        result.PreflightStatus.Should().Be(PreflightStatus.ReadyWithWarnings);
    }

    private sealed class MockQualityService : IFrontendQualityReviewService
    {
        public FrontendQualityReviewReport BuildReport(
            string targetUrl,
            WasmSecurityReviewReport? security,
            WasmPerformanceReviewReport? performance) =>
            new() { TargetUrl = targetUrl };
    }
}
