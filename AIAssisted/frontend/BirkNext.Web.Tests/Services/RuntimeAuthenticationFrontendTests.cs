using BirkNext.RuntimeAuthentication;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Shared runtime authentication in the frontend: the selector (supported/unsupported modes, per-environment choices, no fallback, offline),
/// and the Frontend Quality Review orchestrator routing an explicitly selected Unattended run to the unattended engines without touching
/// the attended session.
/// </summary>
public sealed class RuntimeAuthenticationFrontendTests : BunitContext
{
    private static FrontendAnalysisProfile Profile(string id, string url = "https://app.example.test/") => new() { Id = id, Name = id, TargetUrl = url };

    /// <summary>Resolves with the shared policy and fixed facts, recording each request.</summary>
    private sealed class FakeApi(RuntimeAuthenticationFacts facts) : IRuntimeAuthenticationApiService
    {
        public List<RuntimeAuthenticationRequest> Requests { get; } = [];
        public Task<IReadOnlyList<RuntimeAuthenticationResolution>?> ResolveAsync(IReadOnlyList<RuntimeAuthenticationRequest> requests, CancellationToken ct = default)
        {
            Requests.AddRange(requests);
            return Task.FromResult<IReadOnlyList<RuntimeAuthenticationResolution>?>(requests
                .Select(r => RuntimeAuthenticationPolicy.Resolve(RuntimeAuthenticationCatalog.Find(r.OperationKey)!, r.Mode, r.EnvironmentId, facts with { SignInRequired = r.SignInRequired ?? facts.SignInRequired })).ToList());
        }
    }

    private FakeApi Register(RuntimeAuthenticationFacts? facts = null)
    {
        var api = new FakeApi(facts ?? new RuntimeAuthenticationFacts
        {
            SignInRequired = true, Attended = RuntimeAuthenticationState.Configured, AttendedReason = "Sign in when the run starts.",
            Unattended = RuntimeAuthenticationState.Authenticated, UnattendedReason = "Validated.", UnattendedIdentity = "automation-id",
        });
        Services.AddSingleton<IRuntimeAuthenticationApiService>(api);
        Services.AddSingleton<RuntimeAuthenticationChoices>();
        return api;
    }

    [Fact]
    public void ApiChecks_ShowUnattendedAsUnsupported_WithTheReason()
    {
        Register();
        var cut = Render<RuntimeAuthenticationSelector>(p => p.Add(x => x.OperationKey, RuntimeAuthenticationCatalog.ApiAuthenticatedChecks).Add(x => x.Profile, Profile("dev")));
        cut.WaitForAssertion(() => cut.Find("[data-testid=rta-effective]").GetAttribute("data-effective").Should().Be("Attended"));
        cut.Find("[data-testid=rta-mode-unattended]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-mode=Unattended]").TextContent.Should().Contain("Unsupported").And.Contain("not API credentials");
        cut.Find("[data-testid=rta-auto-reason]").TextContent.Should().Contain("Attended");
        cut.Find("[data-testid=rta-no-fallback]").TextContent.Should().Contain("Never switches");
    }

    [Fact]
    public void Choices_ArePerEnvironment_AndAreRestoredWhenReturning()
    {
        var api = Register();
        RuntimeAuthenticationResolution? last = null;
        var cut = Render<RuntimeAuthenticationSelector>(p => p.Add(x => x.OperationKey, RuntimeAuthenticationCatalog.FrontendRuntime).Add(x => x.Profile, Profile("dev"))
            .Add(x => x.ResolutionChanged, r => last = r));
        cut.WaitForAssertion(() => last.Should().NotBeNull());
        cut.Find("[data-testid=rta-mode-unattended]").Change("Unattended");
        cut.WaitForAssertion(() => last!.Effective.Should().Be(RuntimeAuthenticationMode.Unattended));
        last!.Identity.Should().Be("automation-id");
        last.CanStart.Should().BeTrue();

        cut.Render(p => p.Add(x => x.Profile, Profile("qa", "https://qa.example.test/")));
        cut.WaitForAssertion(() => last!.EnvironmentId.Should().Be("qa"));
        last!.Requested.Should().Be(RuntimeAuthenticationMode.Auto, "a choice never carries over to another environment");

        cut.Render(p => p.Add(x => x.Profile, Profile("dev")));
        cut.WaitForAssertion(() => last!.EnvironmentId.Should().Be("dev"));
        last!.Requested.Should().Be(RuntimeAuthenticationMode.Unattended);
        api.Requests.Should().OnlyContain(r => r.EnvironmentId == "dev" || r.EnvironmentId == "qa");
        api.Requests.Last().FrontendUrl.Should().Be("https://app.example.test/");
    }

    [Fact]
    public void AnUnusableSelectedMode_IsShownAsNotUsable_NotSwitched()
    {
        Register(new RuntimeAuthenticationFacts { SignInRequired = true, Attended = RuntimeAuthenticationState.Configured, Unattended = RuntimeAuthenticationState.Failed, UnattendedReason = "MFA required." });
        RuntimeAuthenticationResolution? last = null;
        var cut = Render<RuntimeAuthenticationSelector>(p => p.Add(x => x.OperationKey, RuntimeAuthenticationCatalog.FrontendAccessibility).Add(x => x.Profile, Profile("dev"))
            .Add(x => x.ResolutionChanged, r => last = r));
        cut.WaitForAssertion(() => last.Should().NotBeNull());
        cut.Find("[data-testid=rta-mode-unattended]").Change("Unattended");
        cut.WaitForAssertion(() => cut.Find("[data-testid=rta-blocked]").TextContent.Should().Contain("not usable"));
        last!.Effective.Should().Be(RuntimeAuthenticationMode.Unattended);
        last.State.Should().Be(RuntimeAuthenticationState.Failed);
    }

    [Fact]
    public void ServiceAuthenticatedOperations_AreReadOnly_AndNameTheirServiceAuthentication()
    {
        Register();
        var cut = Render<RuntimeAuthenticationSelector>(p => p.Add(x => x.OperationKey, RuntimeAuthenticationCatalog.IntegrationRuntime).Add(x => x.Profile, Profile("dev")).Add(x => x.ReadOnly, true));
        cut.WaitForAssertion(() => cut.Find("[data-testid=rta-service]").TextContent.Should().Contain("service credentials"));
        cut.FindAll("input[type=radio]").Should().BeEmpty();
        cut.Find("[data-mode=Attended]").GetAttribute("data-support").Should().Be("NotApplicable");
        cut.Find("[data-mode=Unattended]").GetAttribute("data-support").Should().Be("NotApplicable");
    }

    [Fact]
    public void WithoutTheBackend_TheStateIsReportedUnavailable_NotGuessed()
    {
        var cut = Render<RuntimeAuthenticationSelector>(p => p.Add(x => x.OperationKey, RuntimeAuthenticationCatalog.ApiAuthenticatedChecks).Add(x => x.Profile, Profile("dev")));
        cut.Find("[data-testid=rta-offline]").TextContent.Should().Contain("unavailable");
        cut.FindAll("[data-testid=rta-effective]").Should().BeEmpty();
    }

    [Fact]
    public void Choices_SetAutoRemovesTheChoice_AndIgnoreBlankEnvironments()
    {
        var choices = new RuntimeAuthenticationChoices();
        choices.Set("dev", "op", RuntimeAuthenticationMode.None);
        choices.Get("dev", "op").Should().Be(RuntimeAuthenticationMode.None);
        choices.Get("qa", "op").Should().Be(RuntimeAuthenticationMode.Auto);
        choices.Set("dev", "op", RuntimeAuthenticationMode.Auto);
        choices.Get("dev", "op").Should().Be(RuntimeAuthenticationMode.Auto);
        choices.Set("", "op", RuntimeAuthenticationMode.Unattended);
        choices.Get("", "op").Should().Be(RuntimeAuthenticationMode.Auto);
    }

    // ── Frontend Quality Review orchestrator ─────────────────────────────────────────────────────────────────────

    private sealed class RecordingRuntime : IFrontendBrowserRuntimeReviewApiService
    {
        public List<BrowserRuntimeExecutionModeDto> Modes { get; } = [];
        public Task<BrowserRuntimeResultDto> ReviewAsync(string targetUrl, int navigationTimeoutMs = 30000, int startupObservationMs = 5000, CancellationToken cancellationToken = default)
        {
            Modes.Add(BrowserRuntimeExecutionModeDto.AnonymousOwnedBrowser);
            return Task.FromResult(new BrowserRuntimeResultDto(Status: BrowserRuntimeEngineStatusDto.Assessed, RequestedUrl: targetUrl));
        }
        public Task<BrowserRuntimeResultDto> ReviewAsync(BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Modes.Add(request.ExecutionMode);
            return Task.FromResult(new BrowserRuntimeResultDto(Status: BrowserRuntimeEngineStatusDto.Assessed, RequestedUrl: request.TargetUrl));
        }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingAccessibility : IFrontendAccessibilityReviewApiService
    {
        public List<AccessibilityExecutionModeDto> Modes { get; } = [];
        public Task<AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        {
            Modes.Add(AccessibilityExecutionModeDto.AnonymousOwnedBrowser);
            return Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed, RequestedUrl: targetUrl));
        }
        public Task<AccessibilityResultDto> ReviewAsync(AccessibilityApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Modes.Add(request.ExecutionMode);
            return Task.FromResult(new AccessibilityResultDto(AccessibilityExecutionStatusDto.Assessed, RequestedUrl: request.TargetUrl));
        }
    }

    private static FrontendAnalysisContext Context(RuntimeAuthenticationMode mode) => new()
    {
        TargetUrl = "https://app.example.test/",
        RequiresAuthentication = true,
        RuntimeAuthentication = mode,
        ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Performance = new FrontendPerformanceThresholds() },
        FeatureToggles = new FrontendAnalysisFeatureToggles { EnableBrowserRuntimeEngine = true, EnableAccessibilityEngine = true, EnableSecurityEngine = false, EnablePerformanceEngine = false },
    };

    [Fact]
    public async Task UnattendedRun_SendsUnattendedRequests_AndNeverAsksForTheAttendedSession()
    {
        var runtime = new RecordingRuntime();
        var accessibility = new RecordingAccessibility();
        var sessions = new Mock<IAuthenticatedBrowserSessionService>(MockBehavior.Strict);
        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(runtime: runtime, accessibility: accessibility, authenticatedSessions: sessions.Object);

        var result = await orchestrator.RunAsync("https://app.example.test/", Context(RuntimeAuthenticationMode.Unattended));

        runtime.Modes.Should().Equal([BrowserRuntimeExecutionModeDto.UnattendedLoginPage]);
        accessibility.Modes.Should().Equal([AccessibilityExecutionModeDto.UnattendedLoginPage]);
        result.AccessDecisions![FrontendQualityEngineId.BrowserRuntime].AccessLabel.Should().Be(FrontendQualityReviewOrchestrator.UnattendedAccessLabel);
        sessions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AutoRun_NeverUsesTheUnattendedLogin()
    {
        var runtime = new RecordingRuntime();
        var accessibility = new RecordingAccessibility();
        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(runtime: runtime, accessibility: accessibility);

        await orchestrator.RunAsync("https://app.example.test/", Context(RuntimeAuthenticationMode.Auto));

        runtime.Modes.Should().NotContain(BrowserRuntimeExecutionModeDto.UnattendedLoginPage);
        accessibility.Modes.Should().NotContain(AccessibilityExecutionModeDto.UnattendedLoginPage);
    }
}
