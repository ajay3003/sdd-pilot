using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Phase 4 deterministic orchestrator tests for snapshot-based engine eligibility.
/// Tests: Available/Selected/Layer1/Layer2/AuthSupport eligibility rules.
/// Tests: Partial execution when some engines become unavailable.
/// </summary>
public sealed class FrontendQualityReviewOrchestratorSnapshotEligibilityTest
{
    private sealed class FakePassiveSecurity : IFrontendPassiveSecurityApiService
    {
        public int CallCount { get; private set; }

        public Task<PassiveSecurityResultDto> ReviewAsync(
            string targetUrl, string profileId, string configuredBaseUrl, string environmentType,
            bool requiresAuthentication, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new PassiveSecurityResultDto(
                PassiveSecurityExecutionStatusDto.Assessed, "ZAP", "Passive", null,
                "https://example.com", null, null, null, null, 0, 0, 0, 0, [], [], null,
                "Configured target only", null));
        }
    }

    private sealed class FakeLighthouse : IFrontendLighthouseReviewApiService
    {
        public int CallCount { get; private set; }

        public Task<LighthouseResultDto> ReviewAsync(
            string targetUrl, bool requiresAuthentication, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new LighthouseResultDto(LighthouseExecutionStatusDto.Assessed));
        }
    }

    private sealed class FakeAccessibility : IFrontendAccessibilityReviewApiService
    {
        public int CallCount { get; private set; }

        public Task<AccessibilityResultDto> ReviewAsync(
            string targetUrl, string environmentType, bool requiresAuthentication,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed));
        }
    }

    private sealed class FakeBrowserRuntime : IFrontendBrowserRuntimeReviewApiService
    {
        public int CallCount { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<BrowserRuntimeResultDto> ReviewAsync(
            string targetUrl, int timeout = 30000, int shutdownTimeout = 5000,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed));
        }

        public Task<BrowserRuntimeResultDto> ReviewAsync(
            BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed));
        }
    }

    private static FrontendAnalysisContext BaseContext(ReviewEngineSelection? selection = null) => new()
    {
        TargetUrl = "https://example.com",
        ActiveProfile = new() { Id = "test-profile", TargetUrl = "https://example.com", Performance = new() },
        FeatureToggles = new()
        {
            EnableBrowserRuntimeEngine = true,
            EnableAccessibilityEngine = true,
            EnableLighthouseEngine = true,
            EnablePassiveSecurityEngine = true,
        },
        ReviewEngineSelection = selection ?? new(),
        AllowedBackendDomains = [],
        AllowedRestHosts = [],
        AllowedGraphQlEndpoints = [],
        AllowedCdnHosts = [],
        SecuritySettings = new(),
    };

    private static FrontendQualityEngineExecutionSnapshot SnapshotWith(
        Dictionary<FrontendQualityEngineIdDto, bool>? layer1 = null,
        Dictionary<FrontendQualityEngineIdDto, bool>? layer2 = null,
        Dictionary<FrontendQualityEngineIdDto, bool>? selected = null,
        Dictionary<FrontendQualityEngineIdDto, bool>? authSupported = null)
    {
        var snapshot = new FrontendQualityEngineExecutionSnapshot();

        if (layer1 != null)
            foreach (var kvp in layer1)
                snapshot.Layer1Allowed[kvp.Key] = kvp.Value;

        if (layer2 != null)
            foreach (var kvp in layer2)
                snapshot.Layer2Enabled[kvp.Key] = kvp.Value;

        if (selected != null)
            foreach (var kvp in selected)
                snapshot.SelectedEngines[kvp.Key] = kvp.Value;

        if (authSupported != null)
            foreach (var kvp in authSupported)
                snapshot.AuthModeSupported[kvp.Key] = kvp.Value;

        // Set defaults for any missing
        foreach (var engine in new[] { FrontendQualityEngineIdDto.BrowserRuntime, FrontendQualityEngineIdDto.Accessibility,
                 FrontendQualityEngineIdDto.Lighthouse, FrontendQualityEngineIdDto.PassiveSecurity })
        {
            snapshot.Layer1Allowed.TryAdd(engine, true);
            snapshot.Layer2Enabled.TryAdd(engine, true);
            snapshot.SelectedEngines.TryAdd(engine, true);
            snapshot.AuthModeSupported.TryAdd(engine, true);
        }

        return snapshot;
    }

    [Fact]
    public async Task Snapshot_AvailableSelected_Executes()
    {
        var passive = new FakePassiveSecurity();
        var snapshot = SnapshotWith(
            selected: new() { [FrontendQualityEngineIdDto.PassiveSecurity] = true });

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            new FakeSecurityScanner(), new FakePerformanceService(), new FakePreflightService(),
            new FakeQualityService(), passiveSecurity: passive);

        await orchestrator.RunAsync("https://example.com", BaseContext(), snapshot);

        passive.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Snapshot_AvailableNotSelected_DoesNotExecute()
    {
        var passive = new FakePassiveSecurity();
        var snapshot = SnapshotWith(
            selected: new() { [FrontendQualityEngineIdDto.PassiveSecurity] = false });

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            new FakeSecurityScanner(), new FakePerformanceService(), new FakePreflightService(),
            new FakeQualityService(), passiveSecurity: passive);

        var result = await orchestrator.RunAsync("https://example.com", BaseContext(), snapshot);

        passive.CallCount.Should().Be(0);
        result.SkippedEngines.Should().Contain("Passive Security");
    }

    [Fact]
    public async Task Snapshot_Layer1Denied_DoesNotExecute()
    {
        var passive = new FakePassiveSecurity();
        var snapshot = SnapshotWith(
            layer1: new() { [FrontendQualityEngineIdDto.PassiveSecurity] = false },
            selected: new() { [FrontendQualityEngineIdDto.PassiveSecurity] = true });

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            new FakeSecurityScanner(), new FakePerformanceService(), new FakePreflightService(),
            new FakeQualityService(), passiveSecurity: passive);

        await orchestrator.RunAsync("https://example.com", BaseContext(), snapshot);

        passive.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_Layer2Disabled_DoesNotExecute()
    {
        var passive = new FakePassiveSecurity();
        var snapshot = SnapshotWith(
            layer2: new() { [FrontendQualityEngineIdDto.PassiveSecurity] = false },
            selected: new() { [FrontendQualityEngineIdDto.PassiveSecurity] = true });

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            new FakeSecurityScanner(), new FakePerformanceService(), new FakePreflightService(),
            new FakeQualityService(), passiveSecurity: passive);

        await orchestrator.RunAsync("https://example.com", BaseContext(), snapshot);

        passive.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_AuthNotSupported_DoesNotExecute()
    {
        var passive = new FakePassiveSecurity();
        var snapshot = SnapshotWith(
            authSupported: new() { [FrontendQualityEngineIdDto.PassiveSecurity] = false },
            selected: new() { [FrontendQualityEngineIdDto.PassiveSecurity] = true });

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            new FakeSecurityScanner(), new FakePerformanceService(), new FakePreflightService(),
            new FakeQualityService(), passiveSecurity: passive);

        await orchestrator.RunAsync("https://example.com", BaseContext(), snapshot);

        passive.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_OnlySelectedEnginesExecute()
    {
        var lighthouse = new FakeLighthouse();
        var accessibility = new FakeAccessibility();
        var passive = new FakePassiveSecurity();

        var snapshot = SnapshotWith(
            selected: new()
            {
                [FrontendQualityEngineIdDto.Lighthouse] = true,
                [FrontendQualityEngineIdDto.Accessibility] = false,
                [FrontendQualityEngineIdDto.PassiveSecurity] = true,
            });

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            new FakeSecurityScanner(), new FakePerformanceService(), new FakePreflightService(),
            new FakeQualityService(), accessibility: accessibility, lighthouse: lighthouse,
            passiveSecurity: passive);

        await orchestrator.RunAsync("https://example.com", BaseContext(), snapshot);

        lighthouse.CallCount.Should().Be(1);
        accessibility.CallCount.Should().Be(0);
        passive.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Snapshot_PartialUnavailableOthersStillExecute()
    {
        var lighthouse = new FakeLighthouse();
        var accessibility = new FakeAccessibility();
        var passive = new FakePassiveSecurity();

        var snapshot = SnapshotWith(
            layer1: new() { [FrontendQualityEngineIdDto.Lighthouse] = false },
            selected: new()
            {
                [FrontendQualityEngineIdDto.Lighthouse] = true,
                [FrontendQualityEngineIdDto.Accessibility] = true,
                [FrontendQualityEngineIdDto.PassiveSecurity] = true,
            });

        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(
            new FakeSecurityScanner(), new FakePerformanceService(), new FakePreflightService(),
            new FakeQualityService(), accessibility: accessibility, lighthouse: lighthouse,
            passiveSecurity: passive);

        await orchestrator.RunAsync("https://example.com", BaseContext(), snapshot);

        lighthouse.CallCount.Should().Be(0, "lighthouse should not execute due to layer1 denial");
        accessibility.CallCount.Should().Be(1, "accessibility should execute");
        passive.CallCount.Should().Be(1, "passive should execute");
    }

    private sealed class FakeSecurityScanner : ISecurityScanner
    {
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
            Task.FromResult<(WasmSecurityReviewReport?, string?)>((new() { TargetUrl = request.TargetUrl }, null));
    }

    private sealed class FakePerformanceService : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(
            string targetUrl, FrontendPerformanceThresholds? thresholds = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WasmPerformanceReviewReport
            {
                TargetUrl = targetUrl,
                ReviewedAt = DateTime.UtcNow,
                IsBlazorWasm = false,
                Assets = []
            });

        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(
            string targetUrl, FrontendPerformanceThresholds? thresholds = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class FakePreflightService : ITargetPreflightService
    {
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) =>
            Task.FromResult(new TargetPreflightResult
            {
                Status = PreflightStatus.Ready,
                Message = "Ready",
                IsBlazorWasm = false,
                FinalUrl = targetUrl
            });
    }

    private sealed class FakeQualityService : IFrontendQualityReviewService
    {
        public FrontendQualityReviewReport BuildReport(
            string targetUrl, WasmSecurityReviewReport? security,
            WasmPerformanceReviewReport? performance) =>
            new() { TargetUrl = targetUrl };
    }
}
