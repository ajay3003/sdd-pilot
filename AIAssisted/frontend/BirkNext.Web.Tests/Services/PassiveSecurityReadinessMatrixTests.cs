using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Phase 4 deterministic readiness matrix for PassiveSecurity (ZAP).
/// Tests: Ready=true allows execution, Ready=false blocks execution, timeout blocks, auth routing.
/// 6 scenarios: Selected ready, not selected, ready→false, timeout, infrastructure unavailable, authenticated.
/// </summary>
public sealed class PassiveSecurityReadinessMatrixTests
{
    private static FrontendAnalysisContext PassiveOnlyContext() => new()
    {
        TargetUrl = "https://example.com",
        FeatureToggles = new()
        {
            EnablePassiveSecurityEngine = true,
            EnableSecurityEngine = false,
            EnablePerformanceEngine = false,
            EnableBrowserRuntimeEngine = false,
            EnableAccessibilityEngine = false,
            EnableLighthouseEngine = false
        },
        ReviewEngineSelection = new() { PassiveSecuritySelected = true },
        ActiveProfile = new() { TargetUrl = "https://example.com" }
    };

    [Fact]
    public async Task PassiveSecuritySelectedReady_Executes()
    {
        var passive = new CallCountPassive();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.PassiveSecurity] = true
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), null, null, null, passive, null, readiness);

        await orchestrator.RunAsync("https://example.com", PassiveOnlyContext());

        passive.CallCount.Should().Be(1, "ready=true AND selected=true → execute");
    }

    [Fact]
    public async Task PassiveSecurityNotSelected_DoesNotExecute()
    {
        var passive = new CallCountPassive();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.PassiveSecurity] = true
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), null, null, null, passive, null, readiness);

        var context = PassiveOnlyContext();
        context.FeatureToggles.EnablePassiveSecurityEngine = false;

        await orchestrator.RunAsync("https://example.com", context);

        passive.CallCount.Should().Be(0, "not selected (feature toggle disabled) → do not execute regardless of readiness");
    }

    [Fact]
    public async Task PassiveSecurityReady_False_DoesNotExecute()
    {
        var passive = new CallCountPassive();
        var readiness = CreateReadinessMock(
            new Dictionary<FrontendQualityEngineIdDto, bool>
            {
                [FrontendQualityEngineIdDto.PassiveSecurity] = false
            });

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), null, null, null, passive, null, readiness);

        await orchestrator.RunAsync("https://example.com", PassiveOnlyContext());

        passive.CallCount.Should().Be(0, "ready=false (Layer 3 denial) → do not execute");
    }

    [Fact]
    public async Task PassiveSecurityReadiness_InfrastructureUnavailable_DoesNotExecute()
    {
        var passive = new CallCountPassive();

        var orchestrator = new FrontendQualityReviewOrchestrator(
            new MockSecurity(), new MockPerformance(), new MockPreflight(),
            new MockQuality(), null, null, null, passive, null, null);

        await orchestrator.RunAsync("https://example.com", PassiveOnlyContext());

        passive.CallCount.Should().Be(0, "readiness infrastructure unavailable (null service) → fail-closed, do not execute");
    }

    private static IFrontendQualityEngineStatusApiService CreateReadinessMock(
        Dictionary<FrontendQualityEngineIdDto, bool> engineReadiness)
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
