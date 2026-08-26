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
    private sealed class SpyPassiveSecurity : IFrontendPassiveSecurityApiService
    {
        public int CallCount { get; private set; }
        public PassiveSecurityResultDto Result { get; set; } = Passive(PassiveSecurityExecutionStatusDto.Assessed);
        public Task<PassiveSecurityResultDto> ReviewAsync(string targetUrl, string profileId, string configuredBaseUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(Result); }
    }

    private sealed class FixedRuntime : IFrontendBrowserRuntimeReviewApiService
    { public Task<bool> IsReadyAsync(CancellationToken cancellationToken=default)=>Task.FromResult(true); public Task<BrowserRuntimeResultDto> ReviewAsync(string u,int n=30000,int s=5000,CancellationToken cancellationToken=default)=>Task.FromResult(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed)); }
    private sealed class FixedAccessibility : IFrontendAccessibilityReviewApiService
    { public Task<AccessibilityResultDto> ReviewAsync(string u,string e,bool a,CancellationToken cancellationToken=default)=>Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed)); }
    private sealed class FixedLighthouse : IFrontendLighthouseReviewApiService
    { public Task<LighthouseResultDto> ReviewAsync(string u,bool a,CancellationToken cancellationToken=default)=>Task.FromResult(new LighthouseResultDto(LighthouseExecutionStatusDto.Assessed)); }

    private static PassiveSecurityResultDto Passive(PassiveSecurityExecutionStatusDto status, string? error=null) =>
        new(status,"ZAP Passive","Passive",null,"https://example.com",null,null,null,null,0,0,0,0,[],[],error,"Configured target only",null);

    private static FrontendAnalysisContext PassiveContext(bool enabled) => new()
    {
        TargetUrl="https://example.com", ActiveProfile=new() { Id="trusted", TargetUrl="https://example.com", Performance=new() },
        FeatureToggles=new() { EnableSecurityEngine=true, EnablePerformanceEngine=true, EnableBrowserRuntimeEngine=true,
            EnableAccessibilityEngine=true, EnableLighthouseEngine=true, EnablePassiveSecurityEngine=enabled }, SecuritySettings=new()
    };

    [Fact]
    public async Task Orchestrate_PassiveSecurityDisabled_CallCountZero()
    {
        var passive=new SpyPassiveSecurity(); var result=await new FrontendQualityReviewOrchestrator(new SpySecurityScanner(),new SpyPerformanceService(),new FakePreflightService(),new MockQualityService(),passiveSecurity:passive).RunAsync("https://example.com",PassiveContext(false));
        passive.CallCount.Should().Be(0); result.SkippedEngines.Should().Contain("Passive Security");
    }

    [Fact]
    public async Task Orchestrate_PassiveSecurityEnabledReady_CallCountOne()
    {
        var passive=new SpyPassiveSecurity(); await new FrontendQualityReviewOrchestrator(new SpySecurityScanner(),new SpyPerformanceService(),new FakePreflightService(),new MockQualityService(),passiveSecurity:passive).RunAsync("https://example.com",PassiveContext(true));
        passive.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Orchestrate_PreflightBlocked_PassiveSecurityCallCountZero()
    {
        var passive=new SpyPassiveSecurity(); var preflight=new FakePreflightService { Status=PreflightStatus.InvalidTarget };
        await new FrontendQualityReviewOrchestrator(new SpySecurityScanner(),new SpyPerformanceService(),preflight,new MockQualityService(),passiveSecurity:passive).RunAsync("https://example.com",PassiveContext(true));
        passive.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Orchestrate_PassiveEngineError_RetainsOtherResultsAndMarksPartialWithoutZapScore()
    {
        var passive=new SpyPassiveSecurity { Result=Passive(PassiveSecurityExecutionStatusDto.EngineError,"Docker unavailable") };
        var result=await new FrontendQualityReviewOrchestrator(new SpySecurityScanner(),new SpyPerformanceService(),new FakePreflightService(),new MockQualityService(),new FixedRuntime(),new FixedAccessibility(),new FixedLighthouse(),passive)
            .RunAsync("https://example.com",PassiveContext(true));
        result.SecurityReport.Should().NotBeNull(); result.PerformanceReport.Should().NotBeNull(); result.BrowserRuntimeReport.Should().NotBeNull();
        result.AccessibilityReport.Should().NotBeNull(); result.LighthouseReport.Should().NotBeNull(); result.PassiveSecurityReport!.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatusDto.EngineError);
        result.QualityReport!.Completeness.Should().Be(AssessmentCompleteness.Partial); result.QualityReport.FailedEngines.Should().Contain("Passive Security");
        result.QualityReport.PassiveSecurityReport.Should().NotBeNull(); result.QualityReport.SecurityScore.Should().BeNull("ZAP alert counts are never converted into a score");
    }
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
