using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Phase 4 all-four-engines Layer 3 readiness enforcement.
/// Tests: For each engine, Ready=true allows execution, Ready=false blocks execution.
/// Proves fail-closed semantics for Browser Runtime, Accessibility, Lighthouse, PassiveSecurity.
/// </summary>
public sealed class AllEnginesReadinessEnforcementTests
{
    private static IFrontendQualityEngineStatusApiService CreateReadinessMock(Dictionary<FrontendQualityEngineIdDto, bool> engineReadiness)
    {
        var mock = new Mock<IFrontendQualityEngineStatusApiService>();
        mock.Setup(s => s.RevalidateEngineReadinessAsync(It.IsAny<FrontendQualityEngineIdDto>(), It.IsAny<CancellationToken>()))
            .Returns<FrontendQualityEngineIdDto, CancellationToken>((engineId, _) =>
                Task.FromResult(new FrontendQualityEngineReadinessReportDto
                {
                    IsAvailable = engineReadiness.TryGetValue(engineId, out var ready) ? ready : true,
                    CheckedAtUtc = DateTime.UtcNow
                }));
        return mock.Object;
    }

    [Fact]
    public async Task BrowserRuntimeReadyTrue_Executes()
    {
        var runtime = new CallCountEngine<BrowserRuntimeResultDto>(
            () => new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed));
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.BrowserRuntime] = true
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            runtime, null, null, null, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        runtime.CallCount.Should().Be(1, "BrowserRuntime ready=true → execute");
    }

    [Fact]
    public async Task BrowserRuntimeReadyFalse_DoesNotExecute()
    {
        var runtime = new CallCountEngine<BrowserRuntimeResultDto>(
            () => new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed));
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.BrowserRuntime] = false
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            runtime, null, null, null, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        runtime.CallCount.Should().Be(0, "BrowserRuntime ready=false → do not execute");
    }

    [Fact]
    public async Task AccessibilityReadyTrue_Executes()
    {
        var accessibility = new CallCountAccessibility();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.Accessibility] = true
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            null, accessibility, null, null, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        accessibility.CallCount.Should().Be(1, "Accessibility ready=true → execute");
    }

    [Fact]
    public async Task AccessibilityReadyFalse_DoesNotExecute()
    {
        var accessibility = new CallCountAccessibility();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.Accessibility] = false
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            null, accessibility, null, null, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        accessibility.CallCount.Should().Be(0, "Accessibility ready=false → do not execute");
    }

    [Fact]
    public async Task LighthouseReadyTrue_Executes()
    {
        var lighthouse = new CallCountLighthouse();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.Lighthouse] = true
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            null, null, lighthouse, null, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        lighthouse.CallCount.Should().Be(1, "Lighthouse ready=true → execute");
    }

    [Fact]
    public async Task LighthouseReadyFalse_DoesNotExecute()
    {
        var lighthouse = new CallCountLighthouse();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.Lighthouse] = false
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            null, null, lighthouse, null, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        lighthouse.CallCount.Should().Be(0, "Lighthouse ready=false → do not execute");
    }

    [Fact]
    public async Task PassiveSecurityReadyTrue_Executes()
    {
        var passive = new CallCountPassive();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.PassiveSecurity] = true
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            null, null, null, passive, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        passive.CallCount.Should().Be(1, "PassiveSecurity ready=true → execute");
    }

    [Fact]
    public async Task PassiveSecurityReadyFalse_DoesNotExecute()
    {
        var passive = new CallCountPassive();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.PassiveSecurity] = false
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            null, null, null, passive, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        passive.CallCount.Should().Be(0, "PassiveSecurity ready=false → do not execute");
    }

    [Fact]
    public async Task AllEnginesReadyTrue_AllExecute()
    {
        var runtime = new CallCountEngine<BrowserRuntimeResultDto>(
            () => new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed));
        var accessibility = new CallCountAccessibility();
        var lighthouse = new CallCountLighthouse();
        var passive = new CallCountPassive();

        var readiness = CreateReadinessMock(new Dictionary<FrontendQualityEngineIdDto, bool>
        {
            [FrontendQualityEngineIdDto.BrowserRuntime] = true,
            [FrontendQualityEngineIdDto.Accessibility] = true,
            [FrontendQualityEngineIdDto.Lighthouse] = true,
            [FrontendQualityEngineIdDto.PassiveSecurity] = true
        });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            runtime, accessibility, lighthouse, passive, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        runtime.CallCount.Should().Be(1);
        accessibility.CallCount.Should().Be(1);
        lighthouse.CallCount.Should().Be(1);
        passive.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task AllEnginesReadyFalse_NoneExecute()
    {
        var runtime = new CallCountEngine<BrowserRuntimeResultDto>(
            () => new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed));
        var accessibility = new CallCountAccessibility();
        var lighthouse = new CallCountLighthouse();
        var passive = new CallCountPassive();

        var readiness = CreateReadinessMock(new Dictionary<FrontendQualityEngineIdDto, bool>
        {
            [FrontendQualityEngineIdDto.BrowserRuntime] = false,
            [FrontendQualityEngineIdDto.Accessibility] = false,
            [FrontendQualityEngineIdDto.Lighthouse] = false,
            [FrontendQualityEngineIdDto.PassiveSecurity] = false
        });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(), new MockQuality(),
            runtime, accessibility, lighthouse, passive, null, readiness);

        await orchestrator.RunAsync("https://example.com", AllEnabledContext());

        runtime.CallCount.Should().Be(0);
        accessibility.CallCount.Should().Be(0);
        lighthouse.CallCount.Should().Be(0);
        passive.CallCount.Should().Be(0);
    }

    private static FrontendAnalysisContext AllEnabledContext() => new()
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
        },
        ActiveProfile = new() { TargetUrl = "https://example.com" }
    };

    private sealed class CallCountEngine<T> : IFrontendBrowserRuntimeReviewApiService
    {
        private readonly Func<T> _resultFactory;
        public int CallCount { get; private set; }

        public CallCountEngine(Func<T> resultFactory) => _resultFactory = resultFactory;

        public async Task<BrowserRuntimeResultDto> ReviewAsync(string targetUrl, int navigationTimeoutMs = 30000, int startupObservationMs = 5000, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _resultFactory() as BrowserRuntimeResultDto ?? new(BrowserRuntimeEngineStatusDto.Assessed);
        }

        public async Task<BrowserRuntimeResultDto> ReviewAsync(BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _resultFactory() as BrowserRuntimeResultDto ?? new(BrowserRuntimeEngineStatusDto.Assessed);
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

    private sealed class CallCountPassive : IFrontendPassiveSecurityApiService
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
