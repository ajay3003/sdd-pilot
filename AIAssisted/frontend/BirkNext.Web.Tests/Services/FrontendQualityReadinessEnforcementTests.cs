using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Phase 4 readiness enforcement tests: prove fail-closed semantics.
/// Tests: When readiness infrastructure is unavailable (null), engines MUST NOT execute.
/// </summary>
public sealed class FrontendQualityReadinessEnforcementTests
{
    [Fact]
    public async Task ReadinessServiceNull_BrowserRuntime_DoesNotExecute()
    {
        var runtime = new CallCountRuntime();
        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), runtime, null, null, null, null, null);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new()
            {
                EnableBrowserRuntimeEngine = true,
                EnableSecurityEngine = false,
                EnablePerformanceEngine = false
            }
        };

        await orchestrator.RunAsync("https://example.com", context);

        runtime.CallCount.Should().Be(0, "fail-closed: readiness unavailable means engine MUST NOT execute");
    }

    [Fact]
    public async Task ReadinessServiceNull_Accessibility_DoesNotExecute()
    {
        var accessibility = new CallCountAccessibility();
        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), null, accessibility, null, null, null, null);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new()
            {
                EnableAccessibilityEngine = true,
                EnableSecurityEngine = false,
                EnablePerformanceEngine = false
            }
        };

        await orchestrator.RunAsync("https://example.com", context);

        accessibility.CallCount.Should().Be(0, "fail-closed: readiness unavailable means engine MUST NOT execute");
    }

    [Fact]
    public async Task ReadinessServiceNull_Lighthouse_DoesNotExecute()
    {
        var lighthouse = new CallCountLighthouse();
        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), null, null, lighthouse, null, null, null);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new()
            {
                EnableLighthouseEngine = true,
                EnableSecurityEngine = false,
                EnablePerformanceEngine = false
            }
        };

        await orchestrator.RunAsync("https://example.com", context);

        lighthouse.CallCount.Should().Be(0, "fail-closed: readiness unavailable means engine MUST NOT execute");
    }

    [Fact]
    public async Task ReadinessServiceNull_PassiveSecurity_DoesNotExecute()
    {
        var passive = new CallCountPassiveSecurity();
        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), null, null, null, passive, null, null);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new()
            {
                EnablePassiveSecurityEngine = true,
                EnableSecurityEngine = false,
                EnablePerformanceEngine = false
            }
        };

        await orchestrator.RunAsync("https://example.com", context);

        passive.CallCount.Should().Be(0, "fail-closed: readiness unavailable means engine MUST NOT execute");
    }

    [Fact]
    public async Task ReadinessServiceNull_AllEngines_NoneExecute()
    {
        var runtime = new CallCountRuntime();
        var accessibility = new CallCountAccessibility();
        var lighthouse = new CallCountLighthouse();
        var passive = new CallCountPassiveSecurity();

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), runtime, accessibility, lighthouse, passive, null, null);

        var context = new FrontendAnalysisContext
        {
            TargetUrl = "https://example.com",
            FeatureToggles = new()
            {
                EnableBrowserRuntimeEngine = true,
                EnableAccessibilityEngine = true,
                EnableLighthouseEngine = true,
                EnablePassiveSecurityEngine = true,
                EnableSecurityEngine = false,
                EnablePerformanceEngine = false
            }
        };

        await orchestrator.RunAsync("https://example.com", context);

        runtime.CallCount.Should().Be(0);
        accessibility.CallCount.Should().Be(0);
        lighthouse.CallCount.Should().Be(0);
        passive.CallCount.Should().Be(0);
    }

    private sealed class CallCountRuntime : IFrontendBrowserRuntimeReviewApiService
    {
        public int CallCount { get; private set; }
        public Task<BrowserRuntimeResultDto> ReviewAsync(string targetUrl, int navigationTimeoutMs = 30000, int startupObservationMs = 5000, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed));
        }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class CallCountAccessibility : IFrontendAccessibilityReviewApiService
    {
        public int CallCount { get; private set; }
        public Task<AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed));
        }
    }

    private sealed class CallCountLighthouse : IFrontendLighthouseReviewApiService
    {
        public int CallCount { get; private set; }
        public Task<LighthouseResultDto> ReviewAsync(string targetUrl, bool requiresAuthentication, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new LighthouseResultDto(LighthouseExecutionStatusDto.Assessed));
        }
    }

    private sealed class CallCountPassiveSecurity : IFrontendPassiveSecurityApiService
    {
        public int CallCount { get; private set; }
        public Task<PassiveSecurityResultDto> ReviewAsync(string targetUrl, string profileId, string configuredBaseUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new PassiveSecurityResultDto(
                PassiveSecurityExecutionStatusDto.Assessed, "ZAP", "Passive", null,
                targetUrl, null, null, null, null, 0, 0, 0, 0, [], [], null,
                "Configured target only", null));
        }
    }

    private sealed class MockSecurity : ISecurityScanner
    {
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
            Task.FromResult<(WasmSecurityReviewReport?, string?)>((new(), null));
    }

    private sealed class MockPerformance : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WasmPerformanceReviewReport());
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class MockPreflight : ITargetPreflightService
    {
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) =>
            Task.FromResult(new TargetPreflightResult { Status = PreflightStatus.Ready, Message = "Ready" });
    }

    private sealed class MockQuality : IFrontendQualityReviewService
    {
        public FrontendQualityReviewReport BuildReport(string targetUrl, WasmSecurityReviewReport? security, WasmPerformanceReviewReport? performance) =>
            new() { TargetUrl = targetUrl };
    }
}
