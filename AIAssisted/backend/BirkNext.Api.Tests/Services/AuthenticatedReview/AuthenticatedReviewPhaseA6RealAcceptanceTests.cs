extern alias WebProject;

using ApiAccessibility = BirkNext.Api.Services.FrontendAccessibility;
using ApiRuntime = BirkNext.Api.Services.FrontendBrowserRuntime;
using Web = WebProject::BirkNext.Web.Models;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Tests.TestInfrastructure;
using WebProject::BirkNext.Web.Services;
using Deque.AxeCore.Commons;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Text.Json;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

[Trait("Category", "AuthenticatedReviewPhaseA6RealAcceptance")]
public sealed class AuthenticatedReviewPhaseA6RealAcceptanceTests
{
    private const string Review = "phase-a6-review";
    private const string Profile = "phase-a6-profile";

    [Fact]
    public async Task AuthenticatedSession_PageClosedBeforeLease_IsRejected()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await AuthenticatedReviewPhaseA4RealAcceptanceTests.SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        await using (var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl))
        {
            await lease.Page.CloseAsync();
        }

        var act = () => manager.AcquirePageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await act.Should().ThrowAsync<AuthenticatedResourceUnavailableException>();

        var status = await manager.GetStatusAsync(session.SessionId, Review, Profile);
        status!.Status.Should().Be(AuthenticatedBrowserSessionStatus.Failed);
        status.FailureCategory.Should().Be("page_closed");
    }

    [Fact]
    public async Task AuthenticatedReview_PageClosedBetweenEngines_AccessibilityNotInvoked()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await AuthenticatedReviewPhaseA4RealAcceptanceTests.SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        var harness = CreateHarness(manager, session.SessionId, fixture.TargetUrl);
        var page = lease.Page;

        harness.Orchestrator.AuthenticatedObserver = new CallbackObserver(async () =>
        {
            await page.CloseAsync();
            await Task.Delay(100);
        });

        var result = await harness.Orchestrator.RunAsync(fixture.TargetUrl, harness.Context, harness.Snapshot);

        result.BrowserRuntimeReport!.Status.Should().Be(Web.BrowserRuntimeEngineStatusDto.Assessed);
        harness.Accessibility.Calls.Should().Be(0);
        result.QualityReport!.EngineOutcomes.Single(x => x.EngineId == Web.FrontendQualityEngineId.Accessibility)
            .OutcomeReason.Should().Be(Web.FrontendQualityEngineOutcomeReason.ResourceUnavailable);
        result.AccessibilityReport!.ExecutionStatus.Should().Be(Web.AccessibilityExecutionStatusDto.Skipped);
        result.AccessibilityReport.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthenticatedSession_BrowserDisconnectedBeforeLease_IsRejected()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await AuthenticatedReviewPhaseA4RealAcceptanceTests.SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        await using (var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl))
        {
            await lease.Context.Browser!.CloseAsync();
        }

        await Task.Delay(100);

        var act = () => manager.AcquirePageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await act.Should().ThrowAsync<AuthenticatedResourceUnavailableException>();

        var status = await manager.GetStatusAsync(session.SessionId, Review, Profile);
        status!.Status.Should().Be(AuthenticatedBrowserSessionStatus.Failed);
        status.FailureCategory.Should().Be("browser_disconnected");
    }

    [Fact]
    public async Task AuthenticatedReview_BrowserDisconnectedBetweenEngines_AccessibilityNotInvoked()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await AuthenticatedReviewPhaseA4RealAcceptanceTests.SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        var harness = CreateHarness(manager, session.SessionId, fixture.TargetUrl);
        var browser = lease.Context.Browser!;

        harness.Orchestrator.AuthenticatedObserver = new CallbackObserver(async () =>
        {
            await browser.CloseAsync();
            await Task.Delay(100);
        });

        var result = await harness.Orchestrator.RunAsync(fixture.TargetUrl, harness.Context, harness.Snapshot);

        result.BrowserRuntimeReport!.Status.Should().Be(Web.BrowserRuntimeEngineStatusDto.Assessed);
        harness.Accessibility.Calls.Should().Be(0);
        result.QualityReport!.EngineOutcomes.Single(x => x.EngineId == Web.FrontendQualityEngineId.Accessibility)
            .OutcomeReason.Should().Be(Web.FrontendQualityEngineOutcomeReason.ResourceUnavailable);
        result.AccessibilityReport!.ExecutionStatus.Should().Be(Web.AccessibilityExecutionStatusDto.Skipped);
        result.AccessibilityReport.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthenticatedSession_NormalExplicitCancellation_NotMisclassifiedAsResourceFailure()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await AuthenticatedReviewPhaseA4RealAcceptanceTests.SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        (await manager.CancelAsync(session.SessionId, Review, Profile)).Should().BeTrue();

        var status = await manager.GetStatusAsync(session.SessionId, Review, Profile);
        status!.Status.Should().Be(AuthenticatedBrowserSessionStatus.Cancelled);
        status.FailureCategory.Should().BeNull();
    }

    [Fact]
    public async Task AuthenticatedSession_NormalExpiry_NotMisclassifiedAsResourceFailure()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        var time = new MutableTimeProvider();
        await using var fixture = await AuthenticatedReviewPhaseA4RealAcceptanceTests.SyntheticFixture.StartAsync();
        await using var manager = CreateManager(time);
        var session = await ReachAuthenticatedAsync(manager, fixture);

        time.Advance(TimeSpan.FromMinutes(11));

        var status = await manager.GetStatusAsync(session.SessionId, Review, Profile);
        status!.Status.Should().Be(AuthenticatedBrowserSessionStatus.Expired);
        status.FailureCategory.Should().BeNull();
    }

    [Fact]
    public async Task AuthenticatedSession_ContextClosedBeforeLease_IsRejected()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await AuthenticatedReviewPhaseA4RealAcceptanceTests.SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        await using (var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl))
        {
            await lease.Context.CloseAsync();
        }

        await Task.Delay(100);

        var act = () => manager.AcquirePageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await act.Should().ThrowAsync<AuthenticatedResourceUnavailableException>();
    }

    private static Harness CreateHarness(AuthenticatedBrowserSessionManager manager, string sessionId, string targetUrl)
    {
        var resourceClassifier = new ApiRuntime.BrowserResourceClassifier();
        var runtimeService = new ApiRuntime.FrontendBrowserRuntimeReviewService(
            NullLogger<ApiRuntime.FrontendBrowserRuntimeReviewService>.Instance,
            new ApiRuntime.BrowserTargetValidator(allowLoopback: true),
            new ApiRuntime.BrowserRuntimeFindingClassifier(resourceClassifier), resourceClassifier,
            new ApiRuntime.BrowserEvidenceSanitizer(),
            Options.Create(new ApiRuntime.FrontendBrowserRuntimeOptions { Enabled = true }), manager);
        var accessibilitySanitizer = new ApiAccessibility.AccessibilityEvidenceSanitizer();
        var accessibilityService = new ApiAccessibility.FrontendAccessibilityReviewService(
            NullLogger<ApiAccessibility.FrontendAccessibilityReviewService>.Instance,
            new ApiRuntime.BrowserTargetValidator(allowLoopback: true),
            new ApiAccessibility.AccessibilityNormalizer(accessibilitySanitizer),
            new RealAxeScriptProvider(), accessibilitySanitizer, manager, true);
        var runtime = new RuntimeAdapter(runtimeService, manager);
        var accessibility = new AccessibilityAdapter(accessibilityService, manager);
        var sessions = new SessionAdapter(manager, new(sessionId, Review, Profile, targetUrl));
        var orchestrator = new FrontendQualityReviewOrchestrator(
            new NeverSecurity(), new NeverPerformance(), new ReadyPreflight(), new EmptyQuality(),
            runtime, accessibility, new NeverLighthouse(), new NeverPassiveSecurity(), sessions, new AlwaysReady());
        var context = new Web.FrontendAnalysisContext
        {
            TargetUrl = targetUrl, RequiresAuthentication = true, IsAuthenticatedSessionAvailable = true,
            ActiveProfile = new() { Id = Profile, TargetUrl = targetUrl, Performance = new() },
            FeatureToggles = new() { EnableBrowserRuntimeEngine = true, EnableAccessibilityEngine = true, EnableLighthouseEngine = true, EnablePassiveSecurityEngine = true },
            AllowedBackendDomains = [], AllowedRestHosts = [], AllowedGraphQlEndpoints = [], AllowedCdnHosts = [], SecuritySettings = new()
        };
        var snapshot = new Web.FrontendQualityEngineExecutionSnapshot { AuthMode = Web.ReviewAuthenticationModeDto.Authenticated };
        foreach (var engine in Enum.GetValues<Web.FrontendQualityEngineIdDto>())
        {
            snapshot.Layer1Allowed[engine] = true; snapshot.Layer2Enabled[engine] = true; snapshot.SelectedEngines[engine] = true;
            snapshot.AuthModeSupported[engine] = engine is Web.FrontendQualityEngineIdDto.BrowserRuntime or Web.FrontendQualityEngineIdDto.Accessibility;
        }
        return new(orchestrator, runtime, accessibility, context, snapshot);
    }

    private static AuthenticatedBrowserSessionManager CreateManager(TimeProvider? time = null) => new(
        new PlaywrightAuthenticatedBrowserHost(),
        Options.Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation", AllowSyntheticHttpOrigins = true, AbsoluteLifetimeMinutes = 10, InactivityTimeoutMinutes = 15 }),
        time ?? TimeProvider.System, NullLogger<AuthenticatedBrowserSessionManager>.Instance);

    private static async Task<AuthenticatedBrowserSessionDescriptor> ReachAuthenticatedAsync(
        AuthenticatedBrowserSessionManager manager,
        AuthenticatedReviewPhaseA4RealAcceptanceTests.SyntheticFixture fixture)
    {
        var session = await manager.StartAsync(new AuthenticatedBrowserSessionRequest(Review, Profile, fixture.TargetUrl));
        await manager.BeginAuthenticationAsync(new(session.SessionId, Review, Profile, fixture.EntraOrigin, fixture.McasOrigin));
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.ClickAsync("#synthetic-sign-in");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AwaitingUserContinuation);
        await lease.Page.ClickAsync("#synthetic-continue");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.Authenticated);
        return session;
    }

    private static async Task WaitForStatusAsync(AuthenticatedBrowserSessionManager manager, string sessionId, AuthenticatedBrowserSessionStatus expected)
    {
        for (var i = 0; i < 100; i++)
        {
            if ((await manager.GetStatusAsync(sessionId, Review, Profile))?.Status == expected) return;
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException($"Expected authenticated status {expected}.");
    }

    private sealed record Harness(FrontendQualityReviewOrchestrator Orchestrator, RuntimeAdapter Runtime, AccessibilityAdapter Accessibility, Web.FrontendAnalysisContext Context, Web.FrontendQualityEngineExecutionSnapshot Snapshot);

    private sealed class RuntimeAdapter(ApiRuntime.FrontendBrowserRuntimeReviewService inner, AuthenticatedBrowserSessionManager manager) : IFrontendBrowserRuntimeReviewApiService
    {
        public IPage? ObservedPage { get; private set; } public IBrowserContext? ObservedContext { get; private set; }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Web.BrowserRuntimeResultDto> ReviewAsync(string targetUrl, int timeout = 30000, int shutdownTimeout = 5000, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Anonymous fallback forbidden.");
        public async Task<Web.BrowserRuntimeResultDto> ReviewAsync(BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            await using (var lease = await manager.AcquireAuthenticationPageLeaseAsync(request.AuthenticatedSessionId!, request.ReviewSessionId!, request.ProfileId!, request.TargetUrl, cancellationToken)) { ObservedPage = lease.Page; ObservedContext = lease.Context; }
            var value = await inner.ReviewAsync(new(request.TargetUrl, ApiRuntime.BrowserRuntimeExecutionMode.AuthenticatedSessionPage, request.ReviewSessionId, request.ProfileId, request.AuthenticatedSessionId), cancellationToken);
            return new((Web.BrowserRuntimeEngineStatusDto)value.Status, value.EngineName, value.BrowserName, value.BrowserVersion, value.RequestedUrl, value.FinalUrl, value.StartedAt, value.CompletedAt, value.DurationMs,
                (Web.BrowserStartupStateDto)value.StartupState, value.ConsoleErrorCount, value.PageErrorCount, value.CriticalResourceFailureCount,
                (value.Findings ?? []).Select(x => new Web.BrowserRuntimeFindingDto(x.Id, x.Title, (Web.BrowserRuntimeFindingSeverityDto)x.Severity, x.Category, x.Description, x.Recommendation, x.Evidence ?? [])).ToList(),
                value.EngineError, value.Limitations, Web.BrowserRuntimeExecutionModeDto.AuthenticatedSessionPage, (Web.BrowserRuntimeOutcomeReasonDto)value.OutcomeReason, value.DeliveryContext);
        }
    }

    private sealed class AccessibilityAdapter(ApiAccessibility.FrontendAccessibilityReviewService inner, AuthenticatedBrowserSessionManager manager) : IFrontendAccessibilityReviewApiService
    {
        public int Calls { get; private set; } public IPage? ObservedPage { get; private set; } public IBrowserContext? ObservedContext { get; private set; }
        public Task<Web.AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Anonymous fallback forbidden.");
        public async Task<Web.AccessibilityResultDto> ReviewAsync(AccessibilityApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            await using (var lease = await manager.AcquireAuthenticationPageLeaseAsync(request.SessionId!, request.ReviewSessionId!, request.ProfileId!, request.TargetUrl, cancellationToken)) { ObservedPage = lease.Page; ObservedContext = lease.Context; }
            var value = await inner.ReviewAsync(new(request.TargetUrl, ApiAccessibility.AccessibilityExecutionMode.AuthenticatedSessionPage, request.ReviewSessionId, request.ProfileId, request.SessionId), cancellationToken);
            return new((Web.AccessibilityExecutionStatusDto)value.ExecutionStatus, value.EngineName, value.AxeVersion, value.BrowserName, value.BrowserVersion, value.RequestedUrl, value.FinalUrl, value.StartedAt, value.CompletedAt, value.DurationMs, value.RuleTags,
                value.ViolationCount, value.IncompleteCount, value.PassCount, value.InapplicableCount,
                value.Findings.Select(x => new Web.AccessibilityFindingDto(x.RuleId, (Web.AccessibilityFindingKindDto)x.Kind, (Web.FrontendQualitySeverity)x.Severity, x.Impact, x.Title, x.Description, x.WcagTags, x.AffectedNodeCount, x.Selectors, x.HtmlSnippets, x.FailureSummaries, x.HelpUrl, x.Recommendation)).ToList(),
                value.Limitations, value.EngineError, Web.AccessibilityExecutionModeDto.AuthenticatedSessionPage, (Web.AccessibilityOutcomeReasonDto)value.OutcomeReason);
        }
    }

    private sealed class SessionAdapter(AuthenticatedBrowserSessionManager manager, AuthenticatedBrowserExecutionReference reference) : IAuthenticatedBrowserSessionService
    {
        public Task<AuthenticatedBrowserExecutionReference?> GetExecutionReferenceAsync(Web.FrontendAnalysisContext context) => Task.FromResult<AuthenticatedBrowserExecutionReference?>(reference);
        public async Task<Web.AuthenticatedBrowserSessionStatus> GetStatusAsync(AuthenticatedBrowserExecutionReference value, CancellationToken cancellationToken = default) =>
            (Web.AuthenticatedBrowserSessionStatus)((await manager.GetStatusAsync(value.SessionId, value.ReviewSessionId, value.ProfileId, cancellationToken))?.Status ?? AuthenticatedBrowserSessionStatus.Disposed);
        public Task<Web.AuthenticatedBrowserSessionStatus> GetStatusAsync() => GetStatusAsync(reference);
        public Task<Web.AuthenticatedBrowserSession> GetOrCreateSessionAsync(Web.FrontendAnalysisContext context) => throw new NotSupportedException();
        public Task<Web.AuthenticatedBrowserSession?> GetCurrentSessionAsync() => throw new NotSupportedException();
        public Task<Web.AuthenticatedBrowserSession> BeginAuthenticationAsync(Web.FrontendAnalysisContext context) => throw new NotSupportedException();
        public Task ClearSessionAsync() => Task.CompletedTask;
    }

    private sealed class CallbackObserver(Func<Task> callback) : IAuthenticatedReviewOrchestrationObserver { public Task BetweenEnginesAsync(Web.FrontendQualityEngineId completed, Web.FrontendQualityEngineId next) => callback(); }
    private sealed class MutableTimeProvider : TimeProvider { private DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T00:00:00Z"); public override DateTimeOffset GetUtcNow() => _now; public void Advance(TimeSpan value) => _now += value; }
    private sealed class RealAxeScriptProvider : IAxeScriptProvider { public string GetScript() => (string)(Activator.CreateInstance(typeof(IAxeScriptProvider).Assembly.GetType("Deque.AxeCore.Commons.BundledAxeScriptProvider")!)!.GetType().GetMethod("GetScript")!.Invoke(Activator.CreateInstance(typeof(IAxeScriptProvider).Assembly.GetType("Deque.AxeCore.Commons.BundledAxeScriptProvider")!)!, null)!); }
    private sealed class AlwaysReady : IFrontendQualityEngineStatusApiService { public Task<Web.FrontendQualityEngineStatusReportDto?> GetStatusAsync(Web.ReviewAuthenticationModeDto authMode, Web.ReviewEngineSelectionDto? selection = null, CancellationToken cancellationToken = default) => throw new NotSupportedException(); public Task<Web.FrontendQualityEngineReadinessReportDto?> RevalidateEngineReadinessAsync(Web.FrontendQualityEngineIdDto engineId, CancellationToken cancellationToken = default) => Task.FromResult<Web.FrontendQualityEngineReadinessReportDto?>(new() { EngineId = engineId, IsAvailable = true, CheckedAtUtc = DateTime.UtcNow }); }
    private sealed class ReadyPreflight : ITargetPreflightService { public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) => Task.FromResult(new TargetPreflightResult { Status = Web.PreflightStatus.Ready }); }
    private sealed class EmptyQuality : IFrontendQualityReviewService { public Web.FrontendQualityReviewReport BuildReport(string targetUrl, Web.WasmSecurityReviewReport? security, Web.WasmPerformanceReviewReport? performance) => new() { TargetUrl = targetUrl, GeneratedAt = DateTime.UtcNow }; }
    private sealed class NeverSecurity : ISecurityScanner { public Task<(Web.WasmSecurityReviewReport?, string?)> ScanAsync(Web.WasmScanRequest request) => throw new InvalidOperationException("Static Security must not execute for authenticated review."); }
    private sealed class NeverPerformance : IBlazorWasmPerformanceReviewService { public Task<Web.WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, Web.FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException(); public Task<Web.WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, Web.FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException(); public Web.WasmPerformanceReviewReport? GetCached() => null; public void ClearCache() { } }
    private sealed class NeverLighthouse : IFrontendLighthouseReviewApiService { public Task<Web.LighthouseResultDto> ReviewAsync(string targetUrl, bool requiresAuthentication, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Authenticated Lighthouse forbidden."); }
    private sealed class NeverPassiveSecurity : IFrontendPassiveSecurityApiService { public Task<Web.PassiveSecurityResultDto> ReviewAsync(string targetUrl, string profileId, string configuredBaseUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Authenticated ZAP forbidden."); }
}
