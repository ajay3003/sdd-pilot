using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Frontend Quality Review orchestration against the selected Target Environment's real access model: capability preflight
/// before any request, fail-fast blockers, accurate per-engine reasons (401/403/404/timeout are never confused), honest
/// required-coverage counts and release blocking, and public-HTTP engines that keep working for a protected application.
/// </summary>
public sealed class FrontendQualityAccessOrchestrationTests
{
    private const string Target = "https://m2lbdev.example.test/";

    // ── Fixture ────────────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        public SpySecurity Security { get; } = new();
        public SpyPerformance Performance { get; } = new();
        public SpyPreflight Preflight { get; } = new();
        public SpyRuntime Runtime { get; } = new();
        public FrontendQualityTargetAccessContext? Access { get; set; }
        public FrontendAnalysisContext Context { get; }

        public Fixture(bool requiresAuth, AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy, bool enableSecurity = true, bool enablePerformance = true, bool enableRuntime = false, bool sessionAvailable = false)
        {
            var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Target, Performance = new() };
            profile.Authentication.RequiresAuthentication = requiresAuth;
            profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
            profile.Authentication.AuthenticatedTestingMethod = method;
            Context = new FrontendAnalysisContext
            {
                ActiveProfile = profile, TargetUrl = Target, RequiresAuthentication = requiresAuth, IsAuthenticatedSessionAvailable = sessionAvailable,
                AuthenticationType = profile.Authentication.AuthenticationType,
                FeatureToggles = new() { EnableSecurityEngine = enableSecurity, EnablePerformanceEngine = enablePerformance, EnableBrowserRuntimeEngine = enableRuntime },
                AllowedBackendDomains = [], AllowedRestHosts = [], AllowedGraphQlEndpoints = [], AllowedCdnHosts = [], SecuritySettings = new(),
            };
        }

        public FrontendQualityReviewOrchestrator Orchestrator => new(
            Security, Performance, Preflight, new MockQuality(), Runtime, null, null, null, new SessionSpy(),
            OrchestrationTestHelpers.CreateAlwaysReadyMockService(), new FakeResolver(Access));

        public FrontendQualityEngineExecutionSnapshot AuthenticatedSnapshot()
        {
            var snapshot = new FrontendQualityEngineExecutionSnapshot { AuthMode = ReviewAuthenticationModeDto.Authenticated };
            foreach (var engine in Enum.GetValues<FrontendQualityEngineIdDto>())
            {
                snapshot.Layer1Allowed[engine] = true; snapshot.Layer2Enabled[engine] = true;
                snapshot.SelectedEngines[engine] = engine == FrontendQualityEngineIdDto.BrowserRuntime;
                snapshot.AuthModeSupported[engine] = engine is FrontendQualityEngineIdDto.BrowserRuntime or FrontendQualityEngineIdDto.Accessibility;
            }
            return snapshot;
        }
    }

    private static FrontendQualityTargetAccessContext ProxyAccess(FrontendAnalysisContext context, AuthenticatedApiContextStatus status) =>
        FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = status, AuthenticatedApi = status == AuthenticatedApiContextStatus.Available,
            Reason = status == AuthenticatedApiContextStatus.Available ? "Authenticated via Local HTTPS Proxy (memory only)." : "Start the local proxy and sign in.",
        }, false, null, status == AuthenticatedApiContextStatus.Available ? LocalHttpsProxyState.Ready : LocalHttpsProxyState.Stopped);

    private static FrontendQualityEngineOutcome Outcome(FrontendQualityReviewOrchestrationResult result, FrontendQualityEngineId id) =>
        result.QualityReport!.EngineOutcomes.Single(o => o.EngineId == id);

    // ── Public target (regression) ─────────────────────────────────────────────

    [Fact]
    public async Task PublicTarget_DirectPath_BothRequiredEnginesRunAndCountAsAssessed()
    {
        var fixture = new Fixture(requiresAuth: false);
        fixture.Access = null; // resolver unavailable → configuration-only fallback

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        fixture.Preflight.Calls.Should().Be(1);
        fixture.Security.Calls.Should().Be(1);
        fixture.Performance.Calls.Should().Be(1);
        result.PreflightBlocked.Should().BeFalse();
        result.AccessContext!.Mode.Should().Be(FrontendQualityTargetAccessMode.PublicDirect);
        Outcome(result, FrontendQualityEngineId.StaticSecurity).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        Outcome(result, FrontendQualityEngineId.StaticSecurity).AccessLabel.Should().Be("Public HTTP");
        result.QualityReport!.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        result.QualityReport.TargetAccess.Should().NotBeNull();
    }

    // ── Auth target + proxy ────────────────────────────────────────────────────

    [Fact]
    public async Task AuthTarget_ProxyContextAvailable_PublicHttpEnginesRunAndDomEnginesUnsupported()
    {
        var fixture = new Fixture(requiresAuth: true, enableRuntime: true);
        fixture.Access = ProxyAccess(fixture.Context, AuthenticatedApiContextStatus.Available);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.AuthenticatedSnapshot());

        fixture.Security.Calls.Should().Be(1, "Static Security needs public HTTP to the frontend shell only");
        fixture.Performance.Calls.Should().Be(1);
        fixture.Runtime.Calls.Should().Be(0, "the proxy never provides an authenticated DOM");
        result.PreflightBlocked.Should().BeFalse();
        Outcome(result, FrontendQualityEngineId.StaticSecurity).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        Outcome(result, FrontendQualityEngineId.StaticSecurity).AccessLabel.Should().Contain("Public HTTP");
        var runtime = Outcome(result, FrontendQualityEngineId.BrowserRuntime);
        runtime.ExecutionState.Should().NotBe(FrontendQualityEngineExecutionState.Assessed);
        runtime.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod);
        runtime.SanitizedFailureReason.Should().Contain("Authenticated DOM unavailable with Local HTTPS Proxy");
        FrontendQualityEngineOutcomePresentation.StateLabel(runtime).Should().Be("Unsupported");
        result.QualityReport!.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        result.QualityReport.TargetAccess!.Mode.Should().Be(FrontendQualityTargetAccessMode.LocalHttpsProxy);
    }

    [Fact]
    public async Task AuthTarget_ProxyContextMissing_OnlyDomEngineSelected_FailsFastWithoutAnyRequest()
    {
        var fixture = new Fixture(requiresAuth: true, enableSecurity: false, enablePerformance: false, enableRuntime: true);
        fixture.Access = ProxyAccess(fixture.Context, AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic);

        var started = DateTime.UtcNow;
        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.AuthenticatedSnapshot());

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        fixture.Preflight.Calls.Should().Be(0, "no request is issued when every selected engine is access-blocked");
        fixture.Runtime.Calls.Should().Be(0);
        result.PreflightBlocked.Should().BeTrue();
        result.PreflightBlockReason.Should().NotContain("timeout");
        var runtime = Outcome(result, FrontendQualityEngineId.BrowserRuntime);
        runtime.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod);
        result.QualityReport!.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.Blocked);
    }

    // ── Reachability preflight outcomes ────────────────────────────────────────

    [Fact]
    public async Task RealPreflightTimeout_ReportsTimedOutWithExactWording()
    {
        var fixture = new Fixture(requiresAuth: false);
        fixture.Preflight.Result = new TargetPreflightResult { Status = PreflightStatus.TimedOut, Message = TargetPreflightService.TimeoutMessage };

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        fixture.Security.Calls.Should().Be(0);
        fixture.Performance.Calls.Should().Be(0);
        result.PreflightBlocked.Should().BeTrue();
        foreach (var id in new[] { FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassivePerformance })
        {
            var outcome = Outcome(result, id);
            outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.TimedOut);
            outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.TimedOut);
            outcome.SanitizedFailureReason.Should().Be(TargetPreflightService.TimeoutMessage);
            FrontendQualityEngineOutcomePresentation.StateLabel(outcome).Should().Be("Timed out");
        }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task HttpErrorFromPreflight_IsNeverReportedAsTimeout(int status)
    {
        var fixture = new Fixture(requiresAuth: false);
        fixture.Preflight.Result = new TargetPreflightResult { Status = PreflightStatus.Unreachable, Message = $"Target returned HTTP {status}.", ResponseStatusCode = status };

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        var outcome = Outcome(result, FrontendQualityEngineId.StaticSecurity);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.TargetUnreachable);
        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Unavailable);
        outcome.SanitizedFailureReason.Should().Be($"Target returned HTTP {status}.");
        FrontendQualityEngineOutcomePresentation.StateLabel(outcome).Should().Be("Blocked");
        FrontendQualityEngineOutcomePresentation.GetLabel(outcome.OutcomeReason).Should().Be("Target unreachable");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task HostLevelAuthGate_WithoutSession_BlocksPublicHttpEnginesWithAuthenticationRequired(int status)
    {
        var fixture = new Fixture(requiresAuth: true);
        fixture.Access = ProxyAccess(fixture.Context, AuthenticatedApiContextStatus.Available);
        fixture.Preflight.Result = new TargetPreflightResult { Status = PreflightStatus.AuthenticationRequired, Message = $"Target returned HTTP {status}. The frontend host requires authentication.", ResponseStatusCode = status };

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        fixture.Security.Calls.Should().Be(0);
        result.PreflightBlocked.Should().BeTrue();
        var outcome = Outcome(result, FrontendQualityEngineId.StaticSecurity);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.AuthenticationRequired);
        outcome.SanitizedFailureReason.Should().Contain($"HTTP {status}").And.NotContain("timeout");
    }

    [Fact]
    public async Task BackendUnavailableForPreflight_IsEngineUnavailableNotTimeout()
    {
        var fixture = new Fixture(requiresAuth: false);
        fixture.Preflight.Result = new TargetPreflightResult { Status = PreflightStatus.ScannerUnavailable, Message = TargetPreflightService.BackendUnavailableMessage };

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        var outcome = Outcome(result, FrontendQualityEngineId.PassivePerformance);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.EngineUnavailable);
        outcome.SanitizedFailureReason.Should().Be(TargetPreflightService.BackendUnavailableMessage);
    }

    // ── Engine-level HTTP evidence ─────────────────────────────────────────────

    [Fact]
    public async Task StaticSecurity401OnDocument_NotAssessed_RequiredCountIsOneOfTwo_ReleaseBlocked()
    {
        var fixture = new Fixture(requiresAuth: false);
        fixture.Security.Report = new WasmSecurityReviewReport
        {
            TargetUrl = Target, ScannedAt = DateTime.UtcNow,
            Assets = [new WasmDiscoveredAsset { Url = Target, AssetType = "HTML", Status = "Unauthorized", Analyzed = false }],
        };

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        var security = Outcome(result, FrontendQualityEngineId.StaticSecurity);
        security.ExecutionState.Should().NotBe(FrontendQualityEngineExecutionState.Assessed);
        security.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.AuthenticationRequired);
        security.SanitizedFailureReason.Should().Contain("HTTP 401");
        security.FindingCount.Should().BeNull("zero findings from an unfetched document is not evidence");
        Outcome(result, FrontendQualityEngineId.PassivePerformance).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        result.QualityReport!.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed);
        result.QualityReport.EngineOutcomes.Count(o => o.Requirement == FrontendQualityEngineRequirement.Required && o.ExecutionState == FrontendQualityEngineExecutionState.Assessed).Should().Be(1);
        result.QualityReport.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.Blocked);
    }

    [Fact]
    public async Task PassivePerformanceIndexTimeout_IsTimedOutForThatEngineOnly()
    {
        var fixture = new Fixture(requiresAuth: false);
        fixture.Performance.Report = new WasmPerformanceReviewReport
        {
            TargetUrl = Target, ReviewedAt = DateTime.UtcNow,
            Assets = [new DiscoveredAsset { Url = Target, Type = AssetType.Index, StatusCode = 0, Error = "Request timed out" }],
        };

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        var perf = Outcome(result, FrontendQualityEngineId.PassivePerformance);
        perf.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.TimedOut);
        perf.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.TimedOut);
        Outcome(result, FrontendQualityEngineId.StaticSecurity).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
    }

    [Fact]
    public async Task ZeroFindingsCompleted_IsDistinctFromNotAssessed()
    {
        var fixture = new Fixture(requiresAuth: false);
        fixture.Security.Report = new WasmSecurityReviewReport
        {
            TargetUrl = Target, ScannedAt = DateTime.UtcNow, Findings = [],
            Assets = [new WasmDiscoveredAsset { Url = Target, AssetType = "HTML", Status = "200 OK", Analyzed = true }],
        };
        fixture.Performance.Report = new WasmPerformanceReviewReport { TargetUrl = Target, ReviewedAt = DateTime.UtcNow, ErrorMessage = "Could not reach the backend. Check that the server is running." };

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        var security = Outcome(result, FrontendQualityEngineId.StaticSecurity);
        security.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        security.FindingCount.Should().Be(0);
        FrontendQualityEngineOutcomePresentation.StateLabel(security).Should().Be("Completed — no findings");
        FrontendQualityEngineOutcomePresentation.AssessmentLabel(security).Should().Be("Assessed");

        var perf = Outcome(result, FrontendQualityEngineId.PassivePerformance);
        perf.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.EngineError);
        perf.FindingCount.Should().BeNull();
        perf.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.EngineError, "an engine that did not assess must never present as assessed");
        FrontendQualityEngineOutcomePresentation.StateLabel(perf).Should().Be("Failed");
        FrontendQualityEngineOutcomePresentation.AssessmentLabel(perf).Should().Be("Not assessed");
    }

    // ── CDP / manual ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CdpMethodWithAuthenticatedSession_DomEngineRunsInAuthenticatedBrowserSession()
    {
        var fixture = new Fixture(requiresAuth: true, AuthenticatedTestingMethod.ManagedEdgeCdp, enableRuntime: true, sessionAvailable: true);
        fixture.Access = FrontendQualityTargetAccess.Build(fixture.Context, null, true, ManagedEdgeState.ConnectedAuthenticated, null);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.AuthenticatedSnapshot());

        fixture.Runtime.Calls.Should().Be(1);
        fixture.Runtime.LastRequest!.Mode.Should().Be(BrowserRuntimeExecutionModeDto.AuthenticatedSessionPage);
        var runtime = Outcome(result, FrontendQualityEngineId.BrowserRuntime);
        runtime.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        runtime.AccessKind.Should().Be(FrontendQualityEngineAccessKind.AuthenticatedBrowserSession);
    }

    [Fact]
    public async Task CdpEnterpriseBlock_ReportsEnterpriseProtectionWithoutRunningOrTimingOut()
    {
        var fixture = new Fixture(requiresAuth: true, AuthenticatedTestingMethod.ManagedEdgeCdp, enableRuntime: true);
        fixture.Access = FrontendQualityTargetAccess.Build(fixture.Context, null, false, ManagedEdgeState.TargetTabNotInspectable, null);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.AuthenticatedSnapshot());

        fixture.Runtime.Calls.Should().Be(0);
        var runtime = Outcome(result, FrontendQualityEngineId.BrowserRuntime);
        runtime.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.EnterpriseBrowserProtectionBlocked);
        runtime.SanitizedFailureReason.Should().Contain("enterprise browser protection").And.NotContain("timeout");
        FrontendQualityEngineOutcomePresentation.GetLabel(runtime.OutcomeReason).Should().Be("Blocked by enterprise browser protection");
        result.QualityReport!.TargetAccess!.Mode.Should().Be(FrontendQualityTargetAccessMode.EnterpriseBlocked);
        fixture.Security.Calls.Should().Be(1, "public-HTTP engines are unaffected by the browser attach block");
    }

    [Fact]
    public async Task ManualOnly_DomEngineUnsupportedWithManualReason_PublicEnginesRun()
    {
        var fixture = new Fixture(requiresAuth: true, AuthenticatedTestingMethod.ManualOnly, enableRuntime: true);
        fixture.Access = FrontendQualityTargetAccess.Build(fixture.Context, null, false, null, null);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.AuthenticatedSnapshot());

        fixture.Runtime.Calls.Should().Be(0);
        var runtime = Outcome(result, FrontendQualityEngineId.BrowserRuntime);
        runtime.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.ManualOnlyMethod);
        runtime.SanitizedFailureReason.Should().Contain("manual verification only");
        FrontendQualityEngineOutcomePresentation.StateLabel(runtime).Should().Be("Unsupported");
        fixture.Security.Calls.Should().Be(1);
        result.QualityReport!.TargetAccess!.Mode.Should().Be(FrontendQualityTargetAccessMode.ManualOnly);
    }

    [Fact]
    public async Task Outcomes_NeverCarryCredentialMaterial()
    {
        var fixture = new Fixture(requiresAuth: true, enableRuntime: true);
        fixture.Access = ProxyAccess(fixture.Context, AuthenticatedApiContextStatus.Available);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.AuthenticatedSnapshot());

        var json = System.Text.Json.JsonSerializer.Serialize(result.QualityReport);
        json.Should().NotContainAny("Authorization", "Bearer ", "Cookie", "eyJ");
    }

    // ── Spies ─────────────────────────────────────────────────────────────────

    private sealed class SpySecurity : ISecurityScanner
    {
        public int Calls { get; private set; }
        public WasmSecurityReviewReport Report { get; set; } = new() { ScannedAt = DateTime.UtcNow, Assets = [new WasmDiscoveredAsset { Url = Target, AssetType = "HTML", Status = "200 OK", Analyzed = true }] };
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) { Calls++; return Task.FromResult<(WasmSecurityReviewReport?, string?)>((Report, null)); }
    }

    private sealed class SpyPerformance : IBlazorWasmPerformanceReviewService
    {
        public int Calls { get; private set; }
        public WasmPerformanceReviewReport Report { get; set; } = new() { ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = Target, Type = AssetType.Index, StatusCode = 200 }] };
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(Report); }
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class SpyPreflight : ITargetPreflightService
    {
        public int Calls { get; private set; }
        public TargetPreflightResult Result { get; set; } = new() { Status = PreflightStatus.Ready, Message = "Target is reachable (HTTP 200).", ResponseStatusCode = 200 };
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) { Calls++; return Task.FromResult(Result); }
    }

    private sealed class SpyRuntime : IFrontendBrowserRuntimeReviewApiService
    {
        public int Calls { get; private set; }
        public BrowserRuntimeApiExecutionRequest? LastRequest { get; private set; }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<BrowserRuntimeResultDto> ReviewAsync(string targetUrl, int timeout = 30000, int shutdownTimeout = 5000, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed, RequestedUrl: targetUrl, Findings: [])); }
        public Task<BrowserRuntimeResultDto> ReviewAsync(BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken = default)
        { Calls++; LastRequest = request; return Task.FromResult(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed, RequestedUrl: request.TargetUrl, Findings: [], ExecutionMode: BrowserRuntimeExecutionModeDto.AuthenticatedSessionPage)); }
    }

    private sealed class SessionSpy : IAuthenticatedBrowserSessionService
    {
        public Task<AuthenticatedBrowserExecutionReference?> GetExecutionReferenceAsync(FrontendAnalysisContext context) =>
            Task.FromResult<AuthenticatedBrowserExecutionReference?>(new("session", "review", "dev", Target));
        public Task<AuthenticatedBrowserSessionStatus> GetStatusAsync() => Task.FromResult(AuthenticatedBrowserSessionStatus.Authenticated);
        public Task<AuthenticatedBrowserSession> GetOrCreateSessionAsync(FrontendAnalysisContext context) => throw new NotSupportedException();
        public Task<AuthenticatedBrowserSession?> GetCurrentSessionAsync() => throw new NotSupportedException();
        public Task<AuthenticatedBrowserSession> BeginAuthenticationAsync(FrontendAnalysisContext context) => throw new NotSupportedException();
        public Task ClearSessionAsync() => Task.CompletedTask;
    }

    private sealed class MockQuality : IFrontendQualityReviewService
    {
        public FrontendQualityReviewReport BuildReport(string targetUrl, WasmSecurityReviewReport? security, WasmPerformanceReviewReport? performance) => new() { TargetUrl = targetUrl };
    }

    private sealed class FakeResolver(FrontendQualityTargetAccessContext? access) : IFrontendQualityTargetAccessResolver
    {
        public Task<FrontendQualityTargetAccessContext> ResolveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default) =>
            access is null ? throw new InvalidOperationException("resolver unavailable") : Task.FromResult(access);
    }
}
