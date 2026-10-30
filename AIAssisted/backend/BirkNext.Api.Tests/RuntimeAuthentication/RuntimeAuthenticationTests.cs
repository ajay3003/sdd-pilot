using BirkNext.ApiReview;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.BrowserAuthentication;
using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.RuntimeAuthentication;
using BirkNext.Api.Tests.BrowserAuthentication;
using BirkNext.BrowserAuthentication;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeAuthentication;
using Deque.AxeCore.Commons;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.RuntimeAuthentication;

/// <summary>
/// Shared runtime authentication: the policy (Auto is a fixed selection, never a fallback; unsupported modes are never used), the resolver
/// over the existing services (proxy credential per profile + fingerprint, unattended login per application host), the API review honouring
/// the selected mode, and the accessibility engine's attended/unattended wiring. No browser, login or network call happens here.
/// </summary>
public sealed class RuntimeAuthenticationTests : IDisposable
{
    private const string Env = "dev-env";
    private const string Fingerprint = "fp-dev";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-rta-" + Guid.NewGuid().ToString("N"));
    private readonly UnattendedLoginStore _store;
    private readonly UnattendedLoginService _unattended;
    private readonly Proxy _proxy = new();

    public RuntimeAuthenticationTests()
    {
        _store = new UnattendedLoginStore(_dir);
        _unattended = new UnattendedLoginService(_store, new UnattendedLoginRunner(new NoBrowser(), NullLogger<UnattendedLoginRunner>.Instance), TimeProvider.System, NullLogger<UnattendedLoginService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static RuntimeAuthenticationOperation Op(string key) => RuntimeAuthenticationCatalog.Find(key)!;

    private RuntimeAuthenticationResolution Resolve(string op, RuntimeAuthenticationMode mode, AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy,
        string fingerprint = Fingerprint, string url = "https://app.example.test/", bool? signIn = true) =>
        new RuntimeAuthenticationResolver(_proxy, _unattended).Resolve([new RuntimeAuthenticationRequest
        {
            OperationKey = op, Mode = mode, EnvironmentId = Env, ContextFingerprint = fingerprint, FrontendUrl = url, SignInRequired = signIn, TestingMethod = method,
        }]).Single();

    private void ConfigureUnattended(string loginUrl = "https://app.example.test/sign-in", string? outcome = nameof(UnattendedLoginOutcome.Succeeded))
    {
        _unattended.Save(UnattendedLoginConfigurationTests.Request(loginUrl: loginUrl)).Errors.Should().BeEmpty();
        if (outcome is null) return;
        var settings = _store.Load()!;
        _store.Save(settings with { LastValidation = new StoredValidation(outcome, "fake", DateTimeOffset.UtcNow, UnattendedLoginService.Fingerprint(settings), 10) });
    }

    // ── Policy ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Auto_IsAFixedSelection_FromSupportAndSignIn()
    {
        var facts = new RuntimeAuthenticationFacts { SignInRequired = false };
        RuntimeAuthenticationPolicy.Resolve(Op(RuntimeAuthenticationCatalog.FrontendRuntime), RuntimeAuthenticationMode.Auto, Env, facts).Effective.Should().Be(RuntimeAuthenticationMode.None);
        var required = RuntimeAuthenticationPolicy.Resolve(Op(RuntimeAuthenticationCatalog.FrontendRuntime), RuntimeAuthenticationMode.Auto, Env, facts with { SignInRequired = true });
        required.Effective.Should().Be(RuntimeAuthenticationMode.Attended);
        required.AutoReason.Should().NotBeNullOrEmpty();
        RuntimeAuthenticationPolicy.Resolve(Op(RuntimeAuthenticationCatalog.IntegrationRuntime), RuntimeAuthenticationMode.Auto, Env, facts with { SignInRequired = true })
            .Effective.Should().Be(RuntimeAuthenticationMode.None, "integrations use service authentication, not a browser login");
    }

    [Fact]
    public void AFailedOrExpiredMode_IsReported_NeverReplacedByAnotherModeOrIdentity()
    {
        var facts = new RuntimeAuthenticationFacts
        {
            SignInRequired = true, Attended = RuntimeAuthenticationState.Expired, AttendedReason = "expired",
            Unattended = RuntimeAuthenticationState.Authenticated, UnattendedReason = "validated", UnattendedIdentity = "testbruker01@bufdir.no",
        };
        var auto = RuntimeAuthenticationPolicy.Resolve(Op(RuntimeAuthenticationCatalog.FrontendRuntime), RuntimeAuthenticationMode.Auto, Env, facts);
        auto.Effective.Should().Be(RuntimeAuthenticationMode.Attended);
        auto.State.Should().Be(RuntimeAuthenticationState.Expired);
        auto.Identity.Should().NotBe("testbruker01@bufdir.no");
        auto.CanStart.Should().BeFalse();
    }

    [Fact]
    public void UnsupportedModes_AreUnsupported_WithTheReason()
    {
        var resolution = RuntimeAuthenticationPolicy.Resolve(Op(RuntimeAuthenticationCatalog.ApiAuthenticatedChecks), RuntimeAuthenticationMode.Unattended, Env,
            new RuntimeAuthenticationFacts { Unattended = RuntimeAuthenticationState.Authenticated });
        resolution.State.Should().Be(RuntimeAuthenticationState.Unsupported, "a validated unattended login does not make API checks support it");
        resolution.CanStart.Should().BeFalse();
        resolution.Reason.Should().Contain("not API credentials");
        resolution.Options.Single(o => o.Mode == RuntimeAuthenticationMode.Unattended).Support.Should().Be(RuntimeAuthenticationSupport.Unsupported);
    }

    [Fact]
    public void UnattendedCanStart_OnlyWhenValidated()
    {
        var op = Op(RuntimeAuthenticationCatalog.FrontendAccessibility);
        RuntimeAuthenticationPolicy.Resolve(op, RuntimeAuthenticationMode.Unattended, Env, new() { Unattended = RuntimeAuthenticationState.Configured }).CanStart.Should().BeFalse();
        RuntimeAuthenticationPolicy.Resolve(op, RuntimeAuthenticationMode.Unattended, Env, new() { Unattended = RuntimeAuthenticationState.Authenticated }).CanStart.Should().BeTrue();
        RuntimeAuthenticationPolicy.Resolve(op, RuntimeAuthenticationMode.None, Env, new() { SignInRequired = true }).CanStart.Should().BeTrue();
    }

    // ── Resolver over the existing services ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AttendedApi_IsAuthenticated_OnlyForTheProxyContextOfThisEnvironment()
    {
        _proxy.Status = new LocalHttpsProxyStatus { AuthenticatedCredentialAvailable = true, CredentialObservedHost = "api.example.test" };
        Resolve(RuntimeAuthenticationCatalog.ApiAuthenticatedChecks, RuntimeAuthenticationMode.Attended).State.Should().Be(RuntimeAuthenticationState.Authenticated);
        Resolve(RuntimeAuthenticationCatalog.ApiAuthenticatedChecks, RuntimeAuthenticationMode.Attended, fingerprint: "fp-other").State
            .Should().Be(RuntimeAuthenticationState.Configured, "another environment's proxy context is never used");

        _proxy.Status = new LocalHttpsProxyStatus { CredentialExpired = true };
        Resolve(RuntimeAuthenticationCatalog.ApiAuthenticatedChecks, RuntimeAuthenticationMode.Attended).State.Should().Be(RuntimeAuthenticationState.Expired);
        Resolve(RuntimeAuthenticationCatalog.ApiAuthenticatedChecks, RuntimeAuthenticationMode.Attended, AuthenticatedTestingMethod.ManualOnly).State
            .Should().Be(RuntimeAuthenticationState.NotConfigured);
    }

    [Fact]
    public void AttendedBrowser_IsConfigured_AnOpenSessionIsNeverAssumed()
    {
        var resolution = Resolve(RuntimeAuthenticationCatalog.FrontendRuntime, RuntimeAuthenticationMode.Attended);
        resolution.State.Should().Be(RuntimeAuthenticationState.Configured);
        resolution.Reason.Should().Contain("not assumed");
    }

    [Fact]
    public void Unattended_StatesFollowTheValidatedLogin()
    {
        Resolve(RuntimeAuthenticationCatalog.FrontendRuntime, RuntimeAuthenticationMode.Unattended).State.Should().Be(RuntimeAuthenticationState.NotConfigured);
        ConfigureUnattended(outcome: null);
        Resolve(RuntimeAuthenticationCatalog.FrontendRuntime, RuntimeAuthenticationMode.Unattended).State.Should().Be(RuntimeAuthenticationState.Configured);
        ConfigureUnattended();
        var ready = Resolve(RuntimeAuthenticationCatalog.FrontendRuntime, RuntimeAuthenticationMode.Unattended);
        ready.State.Should().Be(RuntimeAuthenticationState.Authenticated);
        ready.Identity.Should().Be("skolenaervaer-unattended-reporter");
        ready.CanStart.Should().BeTrue();
        ConfigureUnattended(outcome: nameof(UnattendedLoginOutcome.InteractiveChallengeDetected));
        var mfa = Resolve(RuntimeAuthenticationCatalog.FrontendRuntime, RuntimeAuthenticationMode.Unattended);
        mfa.State.Should().Be(RuntimeAuthenticationState.Failed);
        mfa.Reason.Should().Contain("does not bypass");
    }

    [Fact]
    public void Unattended_IsNeverUsedForAnotherApplication()
    {
        ConfigureUnattended(loginUrl: "https://other-app.example.test/sign-in");
        var resolution = Resolve(RuntimeAuthenticationCatalog.FrontendRuntime, RuntimeAuthenticationMode.Unattended);
        resolution.State.Should().Be(RuntimeAuthenticationState.NotConfigured);
        resolution.Reason.Should().Contain("other-app.example.test").And.Contain("never used for another application");
        _unattended.NotApplicableReason("https://app.example.test/page").Should().NotBeNull();
        _unattended.NotApplicableReason("https://other-app.example.test/page").Should().BeNull();
    }

    // ── API Quality Review honours the selected mode ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RuntimeAuthenticationMode.None, "not requested")]
    [InlineData(RuntimeAuthenticationMode.Unattended, "not supported")]
    public void ApiReview_NoneAndUnattended_NeverExecuteAuthenticatedChecks(RuntimeAuthenticationMode mode, string reason)
    {
        var target = new ApiReviewTarget { TargetId = "t", Host = "api.example.test", AuthRequired = true };
        var available = new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, AuthenticatedApi = true, AuthenticatedRest = true };
        var request = new ApiReviewRunRequest { Environment = new ApiReviewEnvironmentSnapshot { AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy }, AuthenticationMode = mode };
        var (access, why, _) = ApiReviewEngine.ResolveAccess(target, request, available);
        access.Should().Be(ApiReviewAccessMode.Unavailable);
        why.Should().Contain(reason);

        ApiReviewEngine.ResolveAccess(target, request with { AuthenticationMode = RuntimeAuthenticationMode.Auto }, available).Mode
            .Should().Be(ApiReviewAccessMode.AuthenticatedHttp, "Auto keeps the environment's method");
    }

    // ── Accessibility: the application constructor gets both sessions; unattended is host-isolated ─────────────────

    [Fact]
    public void Accessibility_DependencyInjectionSuppliesTheAttendedSessionManager()
    {
        using var factory = Utilities.TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFrontendAccessibilityReviewService>();
        typeof(FrontendAccessibilityReviewService).GetField("_authenticatedSessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(service).Should().NotBeNull("without it every attended accessibility run fails as 'identifiers required'");
        typeof(FrontendAccessibilityReviewService).GetField("_unattended", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(service).Should().NotBeNull();
    }

    [Fact]
    public async Task AccessibilityUnattended_ForAnotherApplication_IsRefusedBeforeAnyLogin()
    {
        ConfigureUnattended(loginUrl: "https://other-app.example.test/sign-in");
        var service = new FrontendAccessibilityReviewService(NullLogger<FrontendAccessibilityReviewService>.Instance, new BirkNext.Api.Services.FrontendBrowserRuntime.BrowserTargetValidator(),
            new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer()), new BundledAxeScriptProvider(), new AccessibilityEvidenceSanitizer(), unattended: _unattended);
        var result = await service.ReviewAsync(new AccessibilityExecutionRequest("https://app.example.test/", AccessibilityExecutionMode.UnattendedLoginPage));
        result.ExecutionStatus.Should().Be(AccessibilityExecutionStatus.Skipped);
        result.ExecutionMode.Should().Be(AccessibilityExecutionMode.UnattendedLoginPage);
        result.OutcomeReason.Should().Be(AccessibilityOutcomeReason.UnexpectedOrigin);
        result.EngineError.Should().Contain("never used for another application");
    }

    [Fact]
    public async Task AccessibilityUnattended_NotValidated_FailsAsAuthentication_NeverAsAnAnonymousScan()
    {
        ConfigureUnattended(outcome: null);
        var service = new FrontendAccessibilityReviewService(NullLogger<FrontendAccessibilityReviewService>.Instance, new BirkNext.Api.Services.FrontendBrowserRuntime.BrowserTargetValidator(),
            new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer()), new BundledAxeScriptProvider(), new AccessibilityEvidenceSanitizer(), unattended: _unattended);
        var result = await service.ReviewAsync(new AccessibilityExecutionRequest("https://app.example.test/", AccessibilityExecutionMode.UnattendedLoginPage));
        result.ExecutionStatus.Should().NotBe(AccessibilityExecutionStatus.Assessed);
        result.ExecutionMode.Should().Be(AccessibilityExecutionMode.UnattendedLoginPage);
    }

    [Fact]
    public void AccessibilityModeNumbers_MatchTheFrontend()
    {
        // Serialized as numbers; 2 is the frontend-only BrowserCompanionLivePage.
        ((int)AccessibilityExecutionMode.UnattendedLoginPage).Should().Be(3);
    }

    private sealed class Proxy : ILocalHttpsProxyStatusQuery
    {
        public LocalHttpsProxyStatus? Status { get; set; }
        public LocalHttpsProxyStatus? StatusForProfile(string profileId, string contextFingerprint) =>
            profileId == Env && contextFingerprint == Fingerprint ? Status : null;
    }

    private sealed class NoBrowser : IUnattendedLoginBrowserLauncher
    {
        public Task<(Microsoft.Playwright.IPlaywright Playwright, Microsoft.Playwright.IBrowser Browser)> LaunchAsync(CancellationToken ct) =>
            throw new InvalidOperationException("No browser in these tests.");
    }
}
