using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Scope-aware execution end to end. Reported situation: Public + authenticated scope, HTTP Security and HTTP Performance Required with a
/// Public HTTP prerequisite, Accessibility and Lighthouse Optional, Local HTTPS proxy method with no authenticated context. The public pass
/// completes the required engines; the authenticated pass must never override that completion (it used to: the merge picked the pass that
/// did NOT assess, so required coverage read 0 of 2 and the review was Blocked). Mocked engines only; no target is contacted.
/// </summary>
public sealed class FrontendQualityScopePlanningOrchestrationTests
{
    private const string Target = "https://app.example.test/";

    internal sealed class Fixture
    {
        public FrontendAnalysisContext Context { get; }
        public FrontendQualityTargetAccessContext Access { get; set; }
        public AccessibilityFake Accessibility { get; } = new();
        public LighthouseFake Lighthouse { get; } = new();
        public IFrontendQualityEngineStatusApiService Readiness { get; set; } = OrchestrationTestHelpers.CreateAlwaysReadyMockService();
        public ITargetPreflightService Preflight { get; set; } = new Preflight(PreflightStatus.Ready);
        public IBrowserCompanionAccessibilitySource? Companion { get; set; }

        public Fixture(bool apiContext = false, AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy)
        {
            var profile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Target, Performance = new() };
            profile.Authentication.RequiresAuthentication = true;
            profile.Authentication.AuthenticatedTestingMethod = method;
            profile.Features.EnableSecurityEngine = true; profile.Features.EnablePerformanceEngine = true;
            profile.Features.EnableAccessibilityEngine = true; profile.Features.EnableLighthouseEngine = true;
            profile.Features.EnableBrowserRuntimeEngine = false; profile.Features.EnablePassiveSecurityEngine = false;
            profile.Features.EnableBrowserQualityEngine = false; profile.Features.EnablePerformanceQualityEngine = false;
            Context = new FrontendAnalysisContext
            {
                ActiveProfile = profile, TargetUrl = Target, RequiresAuthentication = true, AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
                FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
                ReviewIdentity = ReviewAuthenticationIdentity.For(profile), AllowedBackendDomains = [], AllowedRestHosts = [], AllowedGraphQlEndpoints = [], AllowedCdnHosts = [], SecuritySettings = new(),
            };
            Access = FrontendQualityTargetAccess.Build(Context, method == AuthenticatedTestingMethod.LocalHttpsProxy
                ? new AuthenticatedReviewCapabilities { Method = method, ContextStatus = apiContext ? AuthenticatedApiContextStatus.Available : AuthenticatedApiContextStatus.NotApplicable, AuthenticatedApi = apiContext }
                : null, false, null, null);
        }

        public Task<FrontendQualityReviewOrchestrationResult> RunAsync(FrontendReviewAccessScope scope) =>
            new FrontendQualityReviewOrchestrator(new OkSecurity(), new OkPerformance(), Preflight, new MockQuality(), null, Accessibility, Lighthouse, null, null,
                Readiness, new FixedResolver(Access), null, null, null, Companion).RunAsync(Target, Context.ForReviewScope(scope));
    }

    private static FrontendQualityEngineOutcome Outcome(FrontendQualityReviewOrchestrationResult result, FrontendQualityEngineId id) =>
        result.QualityReport!.EngineOutcomes.Single(o => o.EngineId == id);

    // ── The reported run ────────────────────────────────────────────────────────

    [Fact]
    public async Task PublicAndAuthenticated_RequiredPublicHttpEngines_CompleteOnPublic_AuthenticatedPassNeverOverridesThem()
    {
        var result = await new Fixture().RunAsync(FrontendReviewAccessScope.PublicAndAuthenticated);
        var report = result.QualityReport!;
        foreach (var id in new[] { FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassivePerformance })
        {
            var outcome = Outcome(result, id);
            outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed, $"{id} completed on the public surface");
            outcome.Surfaces.Single(s => s.Surface == FrontendQualityReviewSurface.Public).Should().Match<FrontendQualitySurfaceResult>(s => s.Completed && s.Primary);
            outcome.Surfaces.Single(s => s.Surface == FrontendQualityReviewSurface.Authenticated).Should().Match<FrontendQualitySurfaceResult>(s =>
                !s.Completed && !s.Primary && s.Result == "Not assessed" && s.Reason == FrontendQualityTargetAccess.ProxyContextMissingReason);
        }
        report.Coverage!.RequiredTotal.Should().Be(2);
        report.Coverage.RequiredAssessed.Should().Be(2);
        report.Coverage.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        report.ReleaseBlockers.Should().NotContain(b => b.Type == FrontendQualityBlockerType.Coverage, "missing authenticated access cannot block Public HTTP engines");
        report.ReleaseDisposition.Should().NotBe(FrontendQualityReleaseDisposition.Blocked);
        report.ExecutionPlan!.Scope.Should().Be(FrontendReviewAccessScope.PublicAndAuthenticated);
    }

    [Fact]
    public async Task PublicAndAuthenticated_Accessibility_CompletesOnPublic_AuthenticatedSurfaceIsUnsupported_NotNoEvidence()
    {
        var result = await new Fixture().RunAsync(FrontendReviewAccessScope.PublicAndAuthenticated);
        var accessibility = Outcome(result, FrontendQualityEngineId.Accessibility);
        accessibility.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        accessibility.Surfaces.Single(s => s.Surface == FrontendQualityReviewSurface.Authenticated).Result.Should().Be("Unsupported");
        result.QualityReport!.Coverage!.OptionalTotal.Should().Be(2);
        var view = FrontendQualityResultPresentation.Build(result.QualityReport, null);
        view.Domains.Single(d => d.Category == FrontendQualityCategory.Accessibility).State.Should().NotBe(FrontendQualityDomainResultState.NoEvidence);
    }

    [Fact]
    public async Task PublicOnly_RequiresNoAuthenticatedCapability()
    {
        var result = await new Fixture().RunAsync(FrontendReviewAccessScope.PublicOnly);
        result.QualityReport!.EngineOutcomes.SelectMany(o => o.Surfaces).Should().NotContain(s => s.Surface == FrontendQualityReviewSurface.Authenticated);
        result.QualityReport.Coverage!.RequiredAssessed.Should().Be(2);
        result.QualityReport.ReleaseBlockers.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthenticatedOnly_PublicOnlyEngine_IsNotApplicable_AndRequiredEngineWithoutContextIsAnExactBlocker()
    {
        var result = await new Fixture().RunAsync(FrontendReviewAccessScope.AuthenticatedOnly);
        var lighthouse = Outcome(result, FrontendQualityEngineId.Lighthouse);
        lighthouse.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.NotApplicable);
        FrontendQualityEngineOutcomePresentation.StateLabel(lighthouse).Should().Be("Not applicable");
        result.QualityReport!.Coverage!.OptionalTotal.Should().Be(1, "Lighthouse is not applicable to an authenticated-only review and leaves the denominator");
        var blocker = result.QualityReport.ReleaseBlockers.Should().Contain(b => b.Type == FrontendQualityBlockerType.Coverage && b.Entity.StartsWith("HTTP Security")).Which;
        blocker.Reason.Should().NotBeNullOrWhiteSpace();
        blocker.Entity.Should().Be("HTTP Security (Authenticated)");
        result.QualityReport.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.Blocked);
        result.QualityReport.EngineOutcomes.Where(o => o.EngineId is FrontendQualityEngineId.StaticSecurity)
            .SelectMany(o => o.Surfaces).Should().NotContain(s => s.Surface == FrontendQualityReviewSurface.Public && s.Completed, "no false public claim");
    }

    [Fact]
    public async Task RealBlocker_PublicHttpUnreachable_KeepsTheReviewBlockedWithTheExactCause()
    {
        var fixture = new Fixture { Preflight = new Preflight(PreflightStatus.Unreachable) };
        var result = await fixture.RunAsync(FrontendReviewAccessScope.PublicOnly);
        result.QualityReport!.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.Blocked);
        result.QualityReport.ReleaseBlockers.Where(b => b.Type == FrontendQualityBlockerType.Coverage).Should().NotBeEmpty()
            .And.OnlyContain(b => !string.IsNullOrWhiteSpace(b.Reason) && !string.IsNullOrWhiteSpace(b.Entity));
    }

    // ── Issue policy vs severity ────────────────────────────────────────────────

    [Fact]
    public async Task IssuePolicy_BlocksOnlyThroughTheConfiguredRule_SeverityAloneNever()
    {
        var fixture = new Fixture();
        var baseline = await fixture.RunAsync(FrontendReviewAccessScope.PublicOnly);
        var issue = baseline.QualityReport!.LogicalIssues.FirstOrDefault();
        baseline.QualityReport.ReleaseBlockers.Should().NotContain(b => b.Type == FrontendQualityBlockerType.IssuePolicy, "no rule names an issue, whatever its severity");
        if (issue is null) return;
        fixture.Context.ReleasePolicy = new FrontendQualityReleasePolicySettings { BlockingLogicalIssueIds = [issue.LogicalId] };
        var blocked = await fixture.RunAsync(FrontendReviewAccessScope.PublicOnly);
        var blocker = blocked.QualityReport!.ReleaseBlockers.Should().ContainSingle(b => b.Type == FrontendQualityBlockerType.IssuePolicy).Which;
        blocker.Rule.Should().Be(FrontendQualityDecisionSupportService.IssuePolicyRule);
        blocked.QualityReport.ReleaseDisposition.Should().Be(FrontendQualityReleaseDisposition.Blocked);
        blocked.QualityReport.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed, "coverage is satisfied; the issue policy is the blocker");
    }

    // ── Lighthouse runtime facts ────────────────────────────────────────────────

    [Theory]
    [InlineData(false, null, FrontendQualityRuntime.NodeUnavailable)]
    [InlineData(true, false, FrontendQualityRuntime.CdpUnavailable)]
    public async Task Lighthouse_NotRunnable_SaysWhichPrerequisiteIsMissing(bool node, bool? cdp, string expected)
    {
        var fixture = new Fixture { Readiness = new ComponentReadiness(node, cdp) };
        var result = await fixture.RunAsync(FrontendReviewAccessScope.PublicOnly);
        var lighthouse = Outcome(result, FrontendQualityEngineId.Lighthouse);
        lighthouse.ExecutionState.Should().NotBe(FrontendQualityEngineExecutionState.Assessed);
        lighthouse.SanitizedFailureReason.Should().Be(expected);
        fixture.Lighthouse.Calls.Should().Be(0);
        result.QualityReport!.ReleaseBlockers.Should().NotContain(b => b.Entity.StartsWith("Lighthouse"), "an optional engine never blocks");
        result.QualityReport.ExecutionPlan!.For(FrontendQualityEngineId.Lighthouse)!.Reason.Should().Be(expected, "the plan is re-resolved with the runtime facts of this run");
    }

    // ── Browser Companion DOM provider ──────────────────────────────────────────

    [Fact]
    public async Task AuthenticatedOnly_ProxyMethod_AccessibilityRunsOnTheLiveCompanionPage_AsItsOwnSurface()
    {
        var fixture = new Fixture { Companion = new CompanionFake(live: true) };
        var result = await fixture.RunAsync(FrontendReviewAccessScope.AuthenticatedOnly);
        var accessibility = Outcome(result, FrontendQualityEngineId.Accessibility);
        accessibility.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        accessibility.AccessKind.Should().Be(FrontendQualityEngineAccessKind.BrowserCompanion);
        accessibility.Provider.Should().Be(FrontendQualityCapabilityResolver.CompanionProvider);
        accessibility.Surfaces.Single(s => s.Surface == FrontendQualityReviewSurface.CompanionPage).Completed.Should().BeTrue();
        accessibility.Surfaces.Single(s => s.Surface == FrontendQualityReviewSurface.Authenticated).Completed.Should().BeFalse("a Companion page is never counted as authenticated coverage");
        fixture.Accessibility.Calls.Should().Be(0, "the backend browser is not used when the Companion supplies the DOM");
        result.QualityReport!.Findings.Should().Contain(f => f.EngineId == FrontendQualityEngineId.Accessibility && f.SourceRuleId == "color-contrast");
        result.QualityReport.EngineOutcomes.Should().NotContain(o => (o.SanitizedFailureReason ?? "").Contains("Browser DOM unavailable"));
    }

    [Fact]
    public async Task AuthenticatedOnly_CompanionWithoutLiveAxe_IsNotAssessedWithTheExactReason()
    {
        var fixture = new Fixture { Companion = new CompanionFake(live: true, axe: false) };
        var result = await fixture.RunAsync(FrontendReviewAccessScope.AuthenticatedOnly);
        var accessibility = Outcome(result, FrontendQualityEngineId.Accessibility);
        accessibility.ExecutionState.Should().NotBe(FrontendQualityEngineExecutionState.Assessed);
        accessibility.SanitizedFailureReason.Should().Be(BrowserCompanionAccessibility.NoLiveAxe);
        result.QualityReport!.ReleaseBlockers.Should().NotContain(b => b.Entity.StartsWith("Accessibility"));
    }

    [Fact]
    public void CompanionAccessibility_UsesOnlyAxeCapturedForAPageThatIsLiveNow()
    {
        var registered = DateTimeOffset.UtcNow.AddMinutes(-1);
        BrowserCompanionStatus Status(DateTimeOffset captured) => new()
        {
            Live = new() { ExtensionConnected = true, LivePages = [new() { PageId = "t1-a", Origin = "https://app.example.test", Route = "/admin/roles", RegisteredAt = registered }] },
            Pages = [new() { PageOrigin = "https://app.example.test", PagePath = "/admin/roles", CapturedAt = captured, BrowserName = "Microsoft Edge",
                Accessibility = new() { Axe = new() { State = "Completed", Version = "4.11.1", Rules =
                [
                    new() { RuleId = "color-contrast", Outcome = "Fail", Count = 3, CriterionIds = ["1.4.3"], Impact = "serious" },
                    new() { RuleId = "document-title", Outcome = "Pass", Count = 1, CriterionIds = ["2.4.2"] },
                ] } } }],
        };
        var live = BrowserCompanionAccessibility.FromLive(Status(registered.AddSeconds(5)), ["https://app.example.test"], Target);
        var finding = live.Result!.Findings!.Should().ContainSingle().Which;
        finding.Severity.Should().Be(FrontendQualitySeverity.High, "axe 'serious' → High");
        finding.AffectedNodeCount.Should().Be(3);
        finding.HtmlSnippets.Should().BeEmpty("no element HTML leaves the browser");
        live.Result.ExecutionMode.Should().Be(AccessibilityExecutionModeDto.BrowserCompanionLivePage);
        var stale = BrowserCompanionAccessibility.FromLive(Status(registered.AddSeconds(-30)), ["https://app.example.test"], Target);
        stale.Result.Should().BeNull("evidence from before this page registered is recorded evidence, not a live run");
        stale.Reason.Should().Be(BrowserCompanionAccessibility.NoLiveAxe);
        stale.RecordedPagesWithAxe.Should().Be(1, "recorded evidence is counted separately");
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────

    public sealed class AccessibilityFake : IFrontendAccessibilityReviewApiService
    {
        public int Calls { get; private set; }
        public Task<AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed, AxeVersion: "4.11.1", RequestedUrl: targetUrl, PassCount: 12)); }
    }

    public sealed class LighthouseFake : IFrontendLighthouseReviewApiService
    {
        public int Calls { get; private set; }
        public Task<LighthouseResultDto> ReviewAsync(string targetUrl, bool requiresAuthentication, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new LighthouseResultDto(LighthouseExecutionStatusDto.Assessed, LighthouseVersion: "12.2.1", PerformanceScore: 90, Metrics: [])); }
    }

    private sealed class CompanionFake(bool live, bool axe = true) : IBrowserCompanionAccessibilitySource
    {
        private static readonly DateTimeOffset Registered = DateTimeOffset.UtcNow.AddMinutes(-2);
        private BrowserCompanionStatus Status() => new()
        {
            Live = new() { ExtensionConnected = live, LivePages = live ? [new() { PageId = "t9-x", Origin = "https://app.example.test", Route = "/admin/child-specific-roles", RegisteredAt = Registered }] : [] },
            Pages = axe ? [new() { PageOrigin = "https://app.example.test", PagePath = "/admin/child-specific-roles", CapturedAt = Registered.AddSeconds(10),
                Accessibility = new() { Axe = new() { State = "Completed", Version = "4.11.1", Rules = [new() { RuleId = "color-contrast", Outcome = "Fail", Count = 2, CriterionIds = ["1.4.3"], Impact = "serious" }] } } }] : [],
        };
        public Task<BrowserCompanionLiveState> LiveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowserCompanionLiveState(Status().EffectiveLive, ["https://app.example.test"]));
        public Task<BrowserCompanionAccessibilityResult> CollectLiveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(BrowserCompanionAccessibility.FromLive(Status(), ["https://app.example.test"], Target));
    }

    private sealed class ComponentReadiness(bool node, bool? cdp) : IFrontendQualityEngineStatusApiService
    {
        public Task<FrontendQualityEngineStatusReportDto?> GetStatusAsync(ReviewAuthenticationModeDto authMode = ReviewAuthenticationModeDto.Anonymous, ReviewEngineSelectionDto? selection = null, CancellationToken ct = default) =>
            Task.FromResult<FrontendQualityEngineStatusReportDto?>(null);
        public Task<FrontendQualityEngineReadinessReportDto?> RevalidateEngineReadinessAsync(FrontendQualityEngineIdDto engineId, CancellationToken ct = default) =>
            Task.FromResult<FrontendQualityEngineReadinessReportDto?>(engineId == FrontendQualityEngineIdDto.Lighthouse
                ? new() { IsAvailable = false, CheckedAtUtc = DateTime.UtcNow, RuntimeComponents = [new() { Kind = "NodeRuntime", Available = node, Provider = "Node.js" }, new() { Kind = "CdpTarget", Available = cdp, Provider = "Bundled Chromium" }] }
                : new() { IsAvailable = true, CheckedAtUtc = DateTime.UtcNow });
    }

    private sealed class OkSecurity : ISecurityScanner
    {
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) => Task.FromResult<(WasmSecurityReviewReport?, string?)>((new WasmSecurityReviewReport { TargetUrl = Target, ScannedAt = DateTime.UtcNow, Assets = [new WasmDiscoveredAsset { Url = Target, AssetType = "HTML", Status = "200 OK", Analyzed = true }] }, null));
    }

    private sealed class OkPerformance : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WasmPerformanceReviewReport { TargetUrl = Target, ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = Target, Type = AssetType.Index, StatusCode = 200 }] });
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class Preflight(PreflightStatus status) : ITargetPreflightService
    {
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) => Task.FromResult(new TargetPreflightResult
        {
            Status = status, Message = status == PreflightStatus.Ready ? "Target is reachable (HTTP 200)." : "Target unreachable: the connection could not be established.",
            ResponseStatusCode = status == PreflightStatus.Ready ? 200 : null,
        });
    }

    private sealed class MockQuality : IFrontendQualityReviewService
    {
        public FrontendQualityReviewReport BuildReport(string targetUrl, WasmSecurityReviewReport? security, WasmPerformanceReviewReport? performance) => new()
        {
            TargetUrl = targetUrl,
            Findings = [new FrontendQualityFinding { Id = "csp-missing", Title = "Content-Security-Policy header missing", Severity = FrontendQualitySeverity.Critical,
                Category = FrontendQualityCategory.Security, EngineId = FrontendQualityEngineId.StaticSecurity, SourceRuleId = "SEC-CSP", Recommendation = "Configure a restrictive CSP.", SourceSystem = "HTTP Security" }],
        };
    }

    private sealed class FixedResolver(FrontendQualityTargetAccessContext access) : IFrontendQualityTargetAccessResolver
    {
        public Task<FrontendQualityTargetAccessContext> ResolveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default) => Task.FromResult(access);
    }
}
