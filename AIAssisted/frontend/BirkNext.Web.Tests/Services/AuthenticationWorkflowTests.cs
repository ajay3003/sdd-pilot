using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The guided authentication workflow: configuration, session and testing readiness are three answers; the steps follow certificate → proxy →
/// browser → sign-in → verify → continue with ONE next action; a launched browser is not a sign-in, a configured provider is not a session,
/// and the browser waits for the proxy. Unattended shows only its own prerequisites.
/// </summary>
public sealed class AuthenticationWorkflowTests
{
    private static AuthenticationPrerequisite P(string id, AuthPrerequisiteState state, string? action = null) =>
        new(id, id, state.ToString(), state, $"{id} explanation", action is null ? null : $"Do {id}", action);

    private static AuthenticationPaneState Pane(AuthConfigurationState config = AuthConfigurationState.Configured, AuthenticatedTestingState testing = AuthenticatedTestingState.NotConnected,
        bool api = false, AuthPrerequisiteState cert = AuthPrerequisiteState.Ok, AuthPrerequisiteState proxy = AuthPrerequisiteState.Idle, AuthPrerequisiteState browser = AuthPrerequisiteState.Idle,
        AuthVerificationState verification = AuthVerificationState.NotApplicable) =>
        new(config, AuthDetectionState.Detected, AuthenticationMatchState.NotDetected, verification, testing, api, false,
            new(AuthenticationReadiness.NotStarted, "", "", [P("certificate", cert, "certificate-install"), P("proxy", proxy, "proxy-start"), P("browser", browser, "edge-open")]));

    private static readonly IReadOnlyList<AuthenticationAdoptionItem> Agrees =
        [new(AuthenticationAdoption.ClientId, "x", "x", AuthenticationAdoptionOutcome.Equal, "")];

    private static AuthenticationWorkflowState Derive(AuthenticationPaneState pane, IReadOnlyList<AuthenticationAdoptionItem>? adoption = null, LocalHttpsProxyStatus? proxy = null,
        bool routed = false, bool observed = false, bool detected = true, AuthenticationMode mode = AuthenticationMode.Attended, UnattendedIdentityStatus? unattended = null) =>
        AuthenticationWorkflow.Derive(mode, pane, adoption ?? Agrees, AuthenticatedTestingMethod.LocalHttpsProxy, proxy, routed, observed, detected, unattended);

    [Fact]
    public void A_configured_target_with_a_stopped_proxy_asks_to_start_the_proxy_and_the_browser_waits_for_it()
    {
        var w = Derive(Pane());
        w.Configuration.Should().Be(AuthConfigurationDimension.Validated);
        w.Session.Should().Be(AuthSessionDimension.NotStarted, "a saved configuration is not a session");
        w.Next!.Id.Should().Be("proxy");
        w.Next.ActionId.Should().Be("proxy-start");
        w.Steps.Single(s => s.Id == "browser").Should().Match<AuthWorkflowStep>(s => s.State == AuthWorkflowStepState.Waiting && s.ActionId == null && s.DisabledReason!.Contains("Start the proxy first"));
        w.PrimaryStatus.Should().Contain("Local HTTPS proxy");
        w.Steps.Select(s => s.Id).Should().Equal(["configuration", "certificate", "proxy", "browser", "sign-in", "verify", "discovery"]);
    }

    [Fact]
    public void A_later_step_is_never_done_while_an_earlier_one_is_not()
    {
        var w = Derive(Pane(proxy: AuthPrerequisiteState.Idle, browser: AuthPrerequisiteState.NotRequired));
        w.Steps.Single(s => s.Id == "browser").State.Should().Be(AuthWorkflowStepState.Waiting, "a browser cannot be observed without the proxy");
    }

    [Fact]
    public void A_saved_configuration_never_checked_against_the_target_asks_for_the_check_first()
    {
        var w = Derive(Pane() with { Detection = AuthDetectionState.NotRun }, adoption: [], detected: false);
        w.Next!.ActionId.Should().Be(AuthenticationSettingsChecks.CheckActionId);
        w.Steps[0].StatusLabel.Should().Be("Saved — not checked against the target");
    }

    [Fact]
    public void An_untrusted_certificate_blocks_the_proxy_with_its_own_recovery_action()
    {
        var w = Derive(Pane(cert: AuthPrerequisiteState.NeedsAttention));
        w.Next!.Id.Should().Be("certificate");
        w.Steps.Single(s => s.Id == "proxy").DisabledReason.Should().Contain("certificate");
    }

    [Fact]
    public void A_launched_browser_is_waiting_for_sign_in_not_signed_in()
    {
        var w = Derive(Pane(proxy: AuthPrerequisiteState.Ok, browser: AuthPrerequisiteState.Ok), proxy: new() { State = LocalHttpsProxyState.WaitingForAuthenticatedTraffic }, routed: true);
        w.Session.Should().Be(AuthSessionDimension.InProgress);
        w.PrimaryStatus.Should().Be("Waiting for sign-in");
        w.Steps.Single(s => s.Id == "sign-in").Explanation.Should().Contain("never asks for your password or MFA code");
        w.CanContinueToDiscovery.Should().BeFalse();
    }

    [Fact]
    public void A_detected_session_must_be_verified_and_only_then_reads_session_verified()
    {
        var connected = Derive(Pane(testing: AuthenticatedTestingState.Ready, api: true, proxy: AuthPrerequisiteState.Ok, browser: AuthPrerequisiteState.Ok));
        connected.Session.Should().Be(AuthSessionDimension.Authenticated);
        connected.Testing.Should().Be(AuthTestingDimension.Connected);
        connected.Next!.ActionId.Should().Be(AuthenticationWorkflow.VerifyAction);
        connected.PrimaryStatus.Should().Be("Signed in — Verify the session");
        connected.CanContinueToDiscovery.Should().BeTrue();

        var verified = Derive(Pane(testing: AuthenticatedTestingState.Ready, api: true, proxy: AuthPrerequisiteState.Ok, browser: AuthPrerequisiteState.Ok), observed: true);
        verified.Testing.Should().Be(AuthTestingDimension.Verified);
        verified.PrimaryStatus.Should().Be("Signed in — Session verified");
        verified.Next!.ActionId.Should().Be(AuthenticationWorkflow.DiscoveryAction);
    }

    [Fact]
    public void An_expired_session_asks_to_sign_in_again_and_blocks_testing()
    {
        var w = Derive(Pane(testing: AuthenticatedTestingState.Expired, proxy: AuthPrerequisiteState.Ok, browser: AuthPrerequisiteState.Ok), proxy: new() { CredentialExpired = true });
        w.Session.Should().Be(AuthSessionDimension.Expired);
        w.Testing.Should().Be(AuthTestingDimension.Blocked);
        w.PrimaryStatus.Should().Be("Session expired — Sign in again");
    }

    [Fact]
    public void A_conflict_is_the_first_thing_to_resolve_and_nothing_is_changed()
    {
        var w = Derive(Pane(), [new(AuthenticationAdoption.ClientId, "a", "b", AuthenticationAdoptionOutcome.Conflict, "differs")]);
        w.Configuration.Should().Be(AuthConfigurationDimension.Conflict);
        w.Next!.ActionId.Should().Be(AuthenticationWorkflow.ReviewAction);
        w.PrimaryStatus.Should().Be("Configuration needs review");
    }

    [Fact]
    public void Without_a_detection_the_first_action_is_to_detect_and_nothing_is_claimed_configured()
    {
        var w = Derive(Pane(AuthConfigurationState.NotConfigured) with { Detection = AuthDetectionState.NotRun }, adoption: [], detected: false);
        w.Configuration.Should().Be(AuthConfigurationDimension.Missing);
        w.Next!.ActionId.Should().Be(AuthenticationSettingsChecks.CheckActionId);
    }

    [Fact]
    public void Unattended_shows_only_its_own_prerequisites_and_never_reuses_the_browser_application()
    {
        var missing = Derive(Pane(), mode: AuthenticationMode.Unattended);
        missing.Steps.Select(s => s.Id).Should().Equal(["configuration", "identity", "verify", "discovery"], "no certificate, proxy or browser for unattended");
        missing.Next!.ActionId.Should().Be(AuthenticationWorkflow.UnattendedSettingsAction);
        missing.Steps.Single(s => s.Id == "identity").Explanation.Should().Contain("client ID is not a service identity");

        var denied = Derive(Pane(), mode: AuthenticationMode.Unattended, unattended: new(true, "Managed identity", "kv://ref", "Target answered 403", false));
        denied.Steps.Single(s => s.Id == "verify").State.Should().Be(AuthWorkflowStepState.Blocked);
        var ok = Derive(Pane(), mode: AuthenticationMode.Unattended, unattended: new(true, "Managed identity", "kv://ref", "Target accepted the identity", true));
        ok.Steps.Single(s => s.Id == "verify").State.Should().Be(AuthWorkflowStepState.Done);
    }
}
