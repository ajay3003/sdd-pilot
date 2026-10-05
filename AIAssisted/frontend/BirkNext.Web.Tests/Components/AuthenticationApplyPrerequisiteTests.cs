using System.Text.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Regression: after Reset Local Data, then configuring and saving ("Apply authentication") an environment's authentication, the
/// Authentication tab showed the HTTPS inspection certificate as Unknown, the Local HTTPS Proxy as Not running and the dedicated Edge as
/// waiting — although nothing had touched the certificate or the proxy.
///
/// Root cause: the runtime observer bound itself to the backend's REMEMBERED last session (RuntimeId + old context fingerprint, kept after a
/// stop and across a reset), skipped the compatibility check for the current profile, and returned a blank Stale record for every other
/// configuration — no certificate, no session, no capability. Saving authentication changes the configuration fingerprint, so it always hit.
/// </summary>
public sealed class AuthenticationApplyPrerequisiteTests
{
    private readonly Mock<ILocalHttpsProxyApiService> _api = new();
    private static readonly ProxyCertificateStatus Trusted = new() { State = ProxyCertificateTrustState.Trusted, NotAfter = DateTimeOffset.UtcNow.AddMonths(6) };

    private static FrontendAnalysisProfile Profile(string id = "dev", string tenant = "11111111-1111-1111-1111-111111111111")
    {
        var profile = new FrontendAnalysisProfile { Id = id, Name = "DEV", TargetUrl = "https://app.example.test/", EnvironmentType = FrontendEnvironmentType.Development, RestBaseUrl = "https://api.example.test/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
        profile.Authentication.ExpectedAuthority = $"https://login.microsoftonline.com/{tenant}";
        profile.Authentication.ExpectedTenant = tenant;
        profile.Authentication.ExpectedClientId = "22222222-2222-2222-2222-222222222222";
        return profile;
    }

    private static FrontendAnalysisProfile Clone(FrontendAnalysisProfile p) => JsonSerializer.Deserialize<FrontendAnalysisProfile>(JsonSerializer.Serialize(p))!;

    /// <summary>What "Apply authentication" + Save does to a profile: a different authentication configuration on the same environment.</summary>
    private static FrontendAnalysisProfile Applied(FrontendAnalysisProfile p)
    {
        var next = Clone(p);
        next.Authentication.ExpectedClientId = "33333333-3333-3333-3333-333333333333";
        next.Authentication.AllowedRedirectUrls = ["https://app.example.test/authentication/login-callback"];
        return next;
    }

    private static LocalHttpsProxyStatus Compatible(FrontendAnalysisProfile p) => new()
    {
        State = LocalHttpsProxyState.Stopped, RuntimeStatus = LocalHttpsProxyRuntimePhase.Stopped, LocalIntegrationAvailable = true, EnvironmentAllowed = true,
        PortAvailable = true, CanStart = true, Certificate = Trusted, ApprovedHosts = LocalHttpsProxyScope.ApprovedHosts(p),
    };

    private static LocalHttpsProxyStatus Running(FrontendAnalysisProfile p) => new()
    {
        RuntimeId = "run-1", SessionId = "run-1", ProfileId = p.Id, ContextFingerprint = LocalHttpsProxyScope.Fingerprint(p), State = LocalHttpsProxyState.Ready,
        RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, Port = 8888, StartedAt = DateTimeOffset.UtcNow, LocalIntegrationAvailable = true, EnvironmentAllowed = true,
        PortAvailable = true, Certificate = Trusted, AuthenticatedCredentialAvailable = true, AuthenticatedRequestsObserved = 4,
    };

    /// <summary>The backend after a stop: no session, but the last session's identity is remembered.</summary>
    private static LocalHttpsProxyStatus RememberedStopped(FrontendAnalysisProfile p) => Running(p) with
    {
        SessionId = null, State = LocalHttpsProxyState.Stopped, RuntimeStatus = LocalHttpsProxyRuntimePhase.Stopped, AuthenticatedCredentialAvailable = false,
        AuthenticatedRequestsObserved = 0, StopReason = "user",
    };

    private AuthenticationReadinessSummary Summarize(LocalHttpsProxyRuntime runtime, FrontendAnalysisProfile p)
    {
        var status = runtime.For(p);
        return AuthenticationReadinessPresentation.Summarize(AuthConfigurationState.Configured, AuthenticatedTestingMethod.LocalHttpsProxy, status, status.Certificate,
            runtime.Loaded, LocalHttpsProxyScope.Fingerprint(p), LocalHttpsProxyScope.EnvironmentAllowed(p), p.Id);
    }

    private static AuthenticationPrerequisite Card(AuthenticationReadinessSummary s, string id) => s.Prerequisites.Single(p => p.Id == id);

    [Fact]
    public async Task ApplyingAuthentication_AfterAStoppedSession_KeepsTheCertificateAndAStartableProxy()
    {
        var before = Profile();
        var after = Applied(before);
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(RememberedStopped(before));
        _api.Setup(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync((LocalHttpsProxyScopeRequest r) => Compatible(r.ProfileId == after.Id ? after : before));
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);

        await runtime.SynchronizeAsync(before);
        await runtime.SynchronizeAsync(after);
        await runtime.RefreshAsync(); // the 3-second poll must not re-bind to the remembered session either

        var status = runtime.For(after);
        status.State.Should().NotBe(LocalHttpsProxyState.Stale);
        status.Certificate.State.Should().Be(ProxyCertificateTrustState.Trusted);
        var summary = Summarize(runtime, after);
        Card(summary, "certificate").StatusLabel.Should().Be("Trusted");
        Card(summary, "proxy").Should().Match<AuthenticationPrerequisite>(c => c.StatusLabel == "Not running" && c.ActionId == "proxy-start");
        Card(summary, "browser").StatusLabel.Should().Be("Waiting for the proxy");
        summary.State.Should().Be(AuthenticationReadiness.NotStarted, "nothing is broken; the proxy is simply not started");
        _api.Verify(a => a.CheckCompatibilityAsync(It.Is<LocalHttpsProxyScopeRequest>(r => r.ContextFingerprint == LocalHttpsProxyScope.Fingerprint(after))), Times.Once,
            "the certificate is re-probed for the current configuration");
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
    }

    [Fact]
    public async Task ApplyingChangedAuthentication_WhileRunning_SaysRestartRequired_KeepsTheCertificate_AndRestartIsExplicit()
    {
        var before = Profile();
        var after = Applied(before);
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(Running(before));
        _api.Setup(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(RememberedStopped(before));
        _api.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync((LocalHttpsProxyScopeRequest r) => Running(after));
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.SynchronizeAsync(before);

        await runtime.SynchronizeAsync(after);
        var stale = runtime.For(after);
        stale.Should().Match<LocalHttpsProxyStatus>(s => s.State == LocalHttpsProxyState.Stale && !s.AuthenticatedCredentialAvailable && s.AuthenticatedRequestsObserved == 0,
            "a credential captured for the old configuration is never offered to the new one");
        stale.Certificate.State.Should().Be(ProxyCertificateTrustState.Trusted, "the certificate is machine state, not session state");
        var summary = Summarize(runtime, after);
        Card(summary, "certificate").StatusLabel.Should().Be("Trusted");
        Card(summary, "proxy").Should().Match<AuthenticationPrerequisite>(c => c.StatusLabel == "Restart required" && c.ActionId == "proxy-restart");
        Card(summary, "proxy").Explanation.Should().Contain("certificate and proxy port are unchanged");
        Card(summary, "browser").StatusLabel.Should().Be("Waiting for the proxy");
        runtime.RunningForOtherConfiguration(after).Should().BeTrue();
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never, "saving a configuration never stops the proxy by itself");

        await runtime.RestartAsync(after);
        _api.Verify(a => a.StopAsync(It.Is<LocalHttpsProxySessionRequest>(r => r.SessionId == "run-1")), Times.Once);
        _api.Verify(a => a.StartAsync(It.Is<LocalHttpsProxyScopeRequest>(r => r.ContextFingerprint == LocalHttpsProxyScope.Fingerprint(after))), Times.Once);
        runtime.For(after).State.Should().Be(LocalHttpsProxyState.Ready);
        Card(Summarize(runtime, after), "proxy").StatusLabel.Should().Be("Running");
    }

    [Fact]
    public async Task ApplyingIdenticalAuthentication_IsIdempotent_AndTheProxyKeepsRunning()
    {
        var profile = Profile();
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(Running(profile));
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.SynchronizeAsync(profile);
        var first = JsonSerializer.Serialize(runtime.For(profile));

        for (var i = 0; i < 5; i++) await runtime.SynchronizeAsync(Clone(profile));

        JsonSerializer.Serialize(runtime.For(Clone(profile))).Should().Be(first);
        Card(Summarize(runtime, profile), "proxy").StatusLabel.Should().Be("Running");
        _api.Verify(a => a.GetRuntimeAsync(), Times.Once, "an unchanged configuration is not re-read");
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
        _api.Verify(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>()), Times.Never);
    }

    [Fact]
    public async Task AfterLocalDataReset_ANewEnvironment_SeesTheCertificate_AndTheOldSessionIsNamed()
    {
        var deleted = Profile("old-m2lb-dev");
        var fresh = Profile("new-dev");
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(Running(deleted));
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.SynchronizeAsync(deleted);
        runtime.ResetForLocalDataReset();
        runtime.Loaded.Should().BeFalse("nothing is claimed until the backend answers again");

        // A session the backend still runs (an older backend, or one started elsewhere) is named, never shown as this environment's.
        await runtime.SynchronizeAsync(fresh);
        var summary = Summarize(runtime, fresh);
        Card(summary, "certificate").StatusLabel.Should().Be("Trusted");
        Card(summary, "proxy").Should().Match<AuthenticationPrerequisite>(c => c.StatusLabel == "Running for another environment" && c.ActionId == "proxy-stop");

        // The backend reset stopped the project-bound session: the fresh environment can start its own.
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(new LocalHttpsProxyStatus { State = LocalHttpsProxyState.Stopped });
        _api.Setup(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(Compatible(fresh));
        runtime.ResetForLocalDataReset();
        await runtime.SynchronizeAsync(fresh);
        Summarize(runtime, fresh).Should().Match<AuthenticationReadinessSummary>(s => s.State == AuthenticationReadiness.NotStarted
            && s.Prerequisites.Single(p => p.Id == "certificate").StatusLabel == "Trusted" && s.Prerequisites.Single(p => p.Id == "proxy").ActionId == "proxy-start");
    }

    [Fact]
    public async Task CertificateRecheck_WithoutASession_ReprobesInsteadOfRepeatingTheLastAnswer()
    {
        var profile = Profile();
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(new LocalHttpsProxyStatus { State = LocalHttpsProxyState.Stopped });
        _api.SetupSequence(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>()))
            .ReturnsAsync(Compatible(profile) with { Certificate = new() { State = ProxyCertificateTrustState.Unknown } })
            .ReturnsAsync(Compatible(profile));
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.SynchronizeAsync(profile);
        runtime.For(profile).Certificate.State.Should().Be(ProxyCertificateTrustState.Unknown);

        await runtime.RecheckAsync(profile);

        runtime.For(profile).Certificate.State.Should().Be(ProxyCertificateTrustState.Trusted);
    }

    [Fact]
    public async Task OneFailedProbe_DoesNotZeroOutTheOthers()
    {
        var profile = Profile();
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(Running(profile));
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.SynchronizeAsync(profile);
        _api.Setup(a => a.GetRuntimeAsync()).ThrowsAsync(new HttpRequestException("backend restarting"));

        await runtime.RefreshAsync();

        var status = runtime.For(profile);
        status.Certificate.State.Should().Be(ProxyCertificateTrustState.Trusted);
        status.Port.Should().Be(8888);
        status.FailureReason.Should().Contain("Reconnecting");
    }

    [Fact]
    public async Task ATargetThePolicyRefuses_ShowsTheReason_NotAStartThatWouldFail_AndThePollKeepsIt()
    {
        var profile = Profile();
        const string refused = "Host example-dev.local cannot be intercepted: only explicitly configured DNS application hosts are allowed.";
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(new LocalHttpsProxyStatus { State = LocalHttpsProxyState.Stopped });
        _api.Setup(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(Compatible(profile) with { CanStart = false, PortAvailable = false, FailureReason = refused });
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.SynchronizeAsync(profile);
        await runtime.RefreshAsync();

        var proxy = Card(Summarize(runtime, profile), "proxy");
        proxy.Should().Match<AuthenticationPrerequisite>(c => c.StatusLabel == "Cannot start for this target" && c.Explanation == refused && c.ActionId == "proxy-recheck");
        Card(Summarize(runtime, profile), "certificate").StatusLabel.Should().Be("Trusted");
    }

    [Fact]
    public void VerifyBlockers_NameEachPrerequisiteAndItsState()
    {
        var summary = AuthenticationReadinessPresentation.Summarize(AuthConfigurationState.Configured, AuthenticatedTestingMethod.LocalHttpsProxy,
            Compatible(Profile()), Trusted, loaded: true, LocalHttpsProxyScope.Fingerprint(Profile()), environmentAllowed: true, "dev");
        summary.CanVerify.Should().BeFalse();
        summary.VerifyBlockers.Should().Equal("Local HTTPS Proxy: Not running");
        AuthenticationReadinessPresentation.VerificationBlockedReason(summary).Should().Contain("Local HTTPS Proxy (Not running)");
    }
}
