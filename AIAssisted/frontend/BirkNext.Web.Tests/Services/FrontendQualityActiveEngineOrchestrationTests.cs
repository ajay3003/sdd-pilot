using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Orchestration runs on the ACTIVE engine set only: saved configuration → active set → capability preflight → execution.
/// Disabled engines are never preflighted, never revalidated for readiness, never executed, and never counted.
/// </summary>
public sealed class FrontendQualityActiveEngineOrchestrationTests
{
    private const string Target = "https://m2lbdev.example.test/";

    private sealed class Fixture
    {
        public SpySecurity Security { get; } = new();
        public SpyPerformance Performance { get; } = new();
        public SpyPreflight Preflight { get; } = new();
        public SpyRuntime Runtime { get; } = new();
        public SpyAccessibility Accessibility { get; } = new();
        public SpyReadiness Readiness { get; } = new();
        public FrontendAnalysisContext Context { get; }
        public FrontendQualityTargetAccessContext? Access { get; set; }

        public Fixture(Action<FrontendAnalysisFeatureToggles>? toggles = null, Action<ReviewEngineSelection>? selection = null, bool requiresAuth = false, AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp)
        {
            var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Target, Performance = new() };
            profile.Authentication.RequiresAuthentication = requiresAuth;
            profile.Authentication.AuthenticatedTestingMethod = method;
            toggles?.Invoke(profile.Features);
            selection?.Invoke(profile.ReviewEngineSelection);
            Context = new FrontendAnalysisContext
            {
                ActiveProfile = profile, TargetUrl = Target, RequiresAuthentication = requiresAuth,
                FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
                AllowedBackendDomains = [], AllowedRestHosts = [], AllowedGraphQlEndpoints = [], AllowedCdnHosts = [], SecuritySettings = new(),
            };
        }

        public FrontendQualityReviewOrchestrator Orchestrator => new(
            Security, Performance, Preflight, new MockQuality(), Runtime, Accessibility, null, null, null, Readiness, new FakeResolver(Access));

        /// <summary>UI-captured snapshot: every backend layer allowed/enabled/auth-supported; selection = saved rule.</summary>
        public FrontendQualityEngineExecutionSnapshot Snapshot()
        {
            var active = FrontendQualityActiveEngines.Resolve(Context);
            var snapshot = new FrontendQualityEngineExecutionSnapshot { AuthMode = ReviewAuthenticationModeDto.Anonymous };
            foreach (var (engineId, dto) in FrontendQualityActiveEngines.BackendEngineIds)
            {
                snapshot.Layer1Allowed[dto] = true; snapshot.Layer2Enabled[dto] = true; snapshot.AuthModeSupported[dto] = true;
                snapshot.SelectedEngines[dto] = active.IsActive(engineId);
            }
            return snapshot;
        }
    }

    private static FrontendQualityEngineOutcome Outcome(FrontendQualityReviewOrchestrationResult result, FrontendQualityEngineId id) =>
        result.QualityReport!.EngineOutcomes.Single(o => o.EngineId == id);

    [Fact]
    public async Task TwoRequiredActive_FourDisabled_OnlyActiveEnginesEnterPreflightAndExecution()
    {
        var fixture = new Fixture();

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.Snapshot());

        result.ActiveEngines!.ActiveCount.Should().Be(2);
        result.AccessDecisions!.Keys.Should().HaveCount(6, "decisions are computed cheaply for all engines but only active ones are acted upon");
        fixture.Preflight.Calls.Should().Be(1, "one shared target reachability probe per review");
        fixture.Security.Calls.Should().Be(1);
        fixture.Performance.Calls.Should().Be(1);
        fixture.Runtime.Calls.Should().Be(0);
        fixture.Accessibility.Calls.Should().Be(0);
        fixture.Readiness.Calls.Should().BeEmpty("disabled engines are never readiness-probed");
        var coverage = result.QualityReport!.Coverage!;
        coverage.RequiredAssessed.Should().Be(2);
        coverage.RequiredTotal.Should().Be(2);
        coverage.OptionalTotal.Should().Be(0);
        coverage.InactiveCount.Should().Be(4);
        result.QualityReport.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.NoAutomatedBlockDetected);
        result.QualityReport.ActiveEngines.Should().BeSameAs(result.ActiveEngines);
        foreach (var id in new[] { FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineId.Accessibility, FrontendQualityEngineId.Lighthouse, FrontendQualityEngineId.PassiveSecurity })
        {
            var outcome = Outcome(result, id);
            outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Disabled);
            outcome.FindingCount.Should().BeNull();
            FrontendQualityEngineOutcomePresentation.AssessmentLabel(outcome).Should().Be("Disabled");
        }
    }

    [Fact]
    public async Task ZeroActiveEngines_NoRequestNoEngine_ExplicitBlockedResult()
    {
        var fixture = new Fixture(toggles: t => { t.EnableSecurityEngine = false; t.EnablePerformanceEngine = false; });

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        fixture.Preflight.Calls.Should().Be(0);
        fixture.Security.Calls.Should().Be(0);
        fixture.Performance.Calls.Should().Be(0);
        fixture.Readiness.Calls.Should().BeEmpty();
        result.PreflightBlocked.Should().BeTrue();
        result.PreflightBlockReason.Should().Contain(FrontendQualityActiveEngines.NoActiveEnginesMessage);
        result.QualityReport!.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.Blocked);
        result.QualityReport.ErrorMessage.Should().Contain("No review engines are enabled");
        result.QualityReport.EngineOutcomes.Should().OnlyContain(o => o.ExecutionState != FrontendQualityEngineExecutionState.Assessed);
        result.QualityReport.Findings.Should().BeEmpty();
        result.QualityReport.ActiveEngines!.HasActiveEngines.Should().BeFalse();
        result.QualityReport.ActiveEngines.RequiredButDisabled.Should().HaveCount(2);
    }

    [Fact]
    public async Task DisabledOptionalDomEngine_WithDomUnavailable_DoesNotBlockRunOrCoverage()
    {
        var fixture = new Fixture(requiresAuth: true, method: AuthenticatedTestingMethod.LocalHttpsProxy);
        fixture.Access = FrontendQualityTargetAccess.Build(fixture.Context, new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true }, false, null, LocalHttpsProxyState.Ready);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.Snapshot());

        fixture.Security.Calls.Should().Be(1);
        fixture.Performance.Calls.Should().Be(1);
        fixture.Accessibility.Calls.Should().Be(0);
        fixture.Readiness.Calls.Should().BeEmpty();
        var accessibility = Outcome(result, FrontendQualityEngineId.Accessibility);
        accessibility.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Disabled);
        accessibility.OutcomeReason.Should().NotBe(FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod, "a disabled engine is reported as disabled, not as blocked work");
        result.QualityReport!.Coverage!.OptionalTotal.Should().Be(0);
        result.QualityReport.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.NoAutomatedBlockDetected);
    }

    [Fact]
    public async Task EnablingOptionalDomEngine_MakesItActive_CapabilityPreflightApplies_OptionalDenominatorIsOne()
    {
        var fixture = new Fixture(toggles: t => t.EnableAccessibilityEngine = true, requiresAuth: true, method: AuthenticatedTestingMethod.LocalHttpsProxy);
        fixture.Access = FrontendQualityTargetAccess.Build(fixture.Context, new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true }, false, null, LocalHttpsProxyState.Ready);
        var snapshot = fixture.Snapshot();
        snapshot.AuthMode = ReviewAuthenticationModeDto.Authenticated;

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, snapshot);

        result.ActiveEngines!.IsActive(FrontendQualityEngineId.Accessibility).Should().BeTrue();
        fixture.Accessibility.Calls.Should().Be(0, "the access preflight blocks DOM engines under the proxy method before any request");
        fixture.Readiness.Calls.Should().BeEmpty("no readiness revalidation for an engine the access preflight already blocked");
        var accessibility = Outcome(result, FrontendQualityEngineId.Accessibility);
        accessibility.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod);
        var coverage = result.QualityReport!.Coverage!;
        coverage.OptionalTotal.Should().Be(1);
        coverage.OptionalAssessed.Should().Be(0);
        coverage.RequiredAssessed.Should().Be(2);
        result.QualityReport.ReleaseDisposition.Should().NotBe(FrontendQualityReleaseDisposition.Blocked, "an optional active engine that could not run follows the optional policy, never required incompleteness");
        result.QualityReport.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
    }

    [Fact]
    public async Task EnabledOptionalEngineOnPublicTarget_IsReadinessProbedOnceAndExecuted()
    {
        var fixture = new Fixture(toggles: t => t.EnableBrowserRuntimeEngine = true);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.Snapshot());

        fixture.Readiness.Calls.Should().Equal(FrontendQualityEngineIdDto.BrowserRuntime);
        fixture.Runtime.Calls.Should().Be(1);
        fixture.Preflight.Calls.Should().Be(1);
        var coverage = result.QualityReport!.Coverage!;
        coverage.OptionalTotal.Should().Be(1);
        coverage.OptionalAssessed.Should().Be(1);
        coverage.RequiredAssessed.Should().Be(2);
    }

    [Fact]
    public async Task DeselectedEnabledOptionalEngine_IsNotSelected_NotProbed_ExcludedFromDenominator()
    {
        var fixture = new Fixture(toggles: t => t.EnableBrowserRuntimeEngine = true, selection: s => s.BrowserRuntimeSelected = false);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.Snapshot());

        fixture.Runtime.Calls.Should().Be(0);
        fixture.Readiness.Calls.Should().BeEmpty();
        Outcome(result, FrontendQualityEngineId.BrowserRuntime).OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.NotSelected);
        result.QualityReport!.Coverage!.OptionalTotal.Should().Be(0);
        result.QualityReport.Coverage.InactiveCount.Should().Be(4);
    }

    [Fact]
    public async Task RequiredEngineDisabled_KeepsRequiredCoverageIncompleteAndReleaseBlocked()
    {
        var fixture = new Fixture(toggles: t => t.EnablePerformanceEngine = false);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.Snapshot());

        fixture.Performance.Calls.Should().Be(0, "a disabled required engine is never silently run");
        var coverage = result.QualityReport!.Coverage!;
        coverage.RequiredTotal.Should().Be(2);
        coverage.RequiredAssessed.Should().Be(1);
        result.QualityReport.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.Blocked);
        result.ActiveEngines!.RequiredButDisabled.Select(e => e.EngineId).Should().Equal(FrontendQualityEngineId.PassivePerformance);
    }

    [Fact]
    public async Task RequiredActiveBlocked_IsOneOfTwoAndBlocked()
    {
        var fixture = new Fixture();
        fixture.Performance.Report = new WasmPerformanceReviewReport { TargetUrl = Target, ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = Target, Type = AssetType.Index, StatusCode = 503 }] };

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.Snapshot());

        var coverage = result.QualityReport!.Coverage!;
        coverage.RequiredAssessed.Should().Be(1);
        coverage.RequiredTotal.Should().Be(2);
        result.QualityReport.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.Blocked);
    }

    [Fact]
    public async Task ActiveEngineSet_IsSnapshottedAtStart_SettingsChangedMidRunApplyToNextReviewOnly()
    {
        var fixture = new Fixture();
        var gate = new TaskCompletionSource<(WasmSecurityReviewReport?, string?)>();
        fixture.Security.Gate = gate.Task;

        var running = fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.Snapshot());
        await Task.Delay(50);
        // Settings edited while the review runs: enable Accessibility on the same saved profile object.
        fixture.Context.FeatureToggles.EnableAccessibilityEngine = true;
        gate.SetResult((fixture.Security.Report, null));
        var first = await running;

        first.ActiveEngines!.ActiveCount.Should().Be(2, "the running review keeps the engine set captured at start");
        first.QualityReport!.ActiveEngines!.IsActive(FrontendQualityEngineId.Accessibility).Should().BeFalse();
        fixture.Accessibility.Calls.Should().Be(0);

        var second = await fixture.Orchestrator.RunAsync(Target, fixture.Context, fixture.Snapshot());
        second.ActiveEngines!.ActiveCount.Should().Be(3, "the next review uses the saved configuration");
        second.ActiveEngines.IsActive(FrontendQualityEngineId.Accessibility).Should().BeTrue();
    }

    [Fact]
    public async Task DefaultSnapshot_UsesActivationRule_NotSelectionAlone()
    {
        // Selection says "yes" for Browser Runtime (default), but the saved toggle is off: no snapshot passed → orchestrator derives
        // selection itself and must not run the disabled engine.
        var fixture = new Fixture(selection: s => s.BrowserRuntimeSelected = true);

        var result = await fixture.Orchestrator.RunAsync(Target, fixture.Context);

        fixture.Runtime.Calls.Should().Be(0);
        fixture.Readiness.Calls.Should().BeEmpty();
        Outcome(result, FrontendQualityEngineId.BrowserRuntime).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Disabled);
    }

    // ── Spies ─────────────────────────────────────────────────────────────────

    private sealed class SpySecurity : ISecurityScanner
    {
        public int Calls { get; private set; }
        public Task<(WasmSecurityReviewReport?, string?)>? Gate { get; set; }
        public WasmSecurityReviewReport Report { get; set; } = new() { TargetUrl = Target, ScannedAt = DateTime.UtcNow, Assets = [new WasmDiscoveredAsset { Url = Target, AssetType = "HTML", Status = "200 OK", Analyzed = true }] };
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) { Calls++; return Gate ?? Task.FromResult<(WasmSecurityReviewReport?, string?)>((Report, null)); }
    }

    private sealed class SpyPerformance : IBlazorWasmPerformanceReviewService
    {
        public int Calls { get; private set; }
        public WasmPerformanceReviewReport Report { get; set; } = new() { TargetUrl = Target, ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = Target, Type = AssetType.Index, StatusCode = 200 }] };
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(Report); }
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class SpyPreflight : ITargetPreflightService
    {
        public int Calls { get; private set; }
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) { Calls++; return Task.FromResult(new TargetPreflightResult { Status = PreflightStatus.Ready, Message = "Target is reachable (HTTP 200).", ResponseStatusCode = 200 }); }
    }

    private sealed class SpyRuntime : IFrontendBrowserRuntimeReviewApiService
    {
        public int Calls { get; private set; }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<BrowserRuntimeResultDto> ReviewAsync(string targetUrl, int timeout = 30000, int shutdownTimeout = 5000, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed, RequestedUrl: targetUrl, Findings: [])); }
        public Task<BrowserRuntimeResultDto> ReviewAsync(BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.Assessed, RequestedUrl: request.TargetUrl, Findings: [])); }
    }

    private sealed class SpyAccessibility : IFrontendAccessibilityReviewApiService
    {
        public int Calls { get; private set; }
        public Task<AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed, RequestedUrl: targetUrl, Findings: [])); }
        public Task<AccessibilityResultDto> ReviewAsync(AccessibilityApiExecutionRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed, RequestedUrl: request.TargetUrl, Findings: [])); }
    }

    private sealed class SpyReadiness : IFrontendQualityEngineStatusApiService
    {
        public List<FrontendQualityEngineIdDto> Calls { get; } = [];
        public Task<FrontendQualityEngineStatusReportDto?> GetStatusAsync(ReviewAuthenticationModeDto authMode = ReviewAuthenticationModeDto.Anonymous, ReviewEngineSelectionDto? selection = null, CancellationToken ct = default) =>
            Task.FromResult<FrontendQualityEngineStatusReportDto?>(null);
        public Task<FrontendQualityEngineReadinessReportDto?> RevalidateEngineReadinessAsync(FrontendQualityEngineIdDto engineId, CancellationToken ct = default)
        { Calls.Add(engineId); return Task.FromResult<FrontendQualityEngineReadinessReportDto?>(new() { EngineId = engineId, IsAvailable = true, CheckedAtUtc = DateTime.UtcNow }); }
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
