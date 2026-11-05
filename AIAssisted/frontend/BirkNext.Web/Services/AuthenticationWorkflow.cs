using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Attended: a real person signs in (MFA possible) in a browser BirkNext can observe. Unattended: a separately authorized,
/// non-interactive identity configured in System Settings → Authentication. Never interchangeable.</summary>
public enum AuthenticationMode { Attended, Unattended }

public enum AuthConfigurationDimension { Missing, Detected, Validated, Configured, Conflict }
public enum AuthSessionDimension { NotStarted, InProgress, Authenticated, Expired, Failed }
public enum AuthTestingDimension { NotConnected, Connected, Verified, Blocked }
public enum AuthWorkflowStepState { Done, Current, Waiting, Blocked, NotApplicable }

/// <summary>One step of the guided flow: its state, one sentence, and at most one action (an existing prerequisite action id, or one of
/// the workflow's own: verify, discovery, check, review).</summary>
public sealed record AuthWorkflowStep(string Id, string Title, AuthWorkflowStepState State, string StatusLabel, string Explanation,
    string? ActionLabel = null, string? ActionId = null, string? DisabledReason = null);

/// <summary>The unattended identity as the Authentication tab can see it (System Settings owns it). Never a secret.</summary>
public sealed record UnattendedIdentityStatus(bool Configured, string IdentityType, string? CredentialReference, string? LastVerification, bool? LastVerificationPassed);

public sealed record AuthenticationWorkflowState(
    AuthenticationMode Mode,
    AuthConfigurationDimension Configuration,
    AuthSessionDimension Session,
    AuthTestingDimension Testing,
    string PrimaryStatus,
    string PrimaryDetail,
    IReadOnlyList<AuthWorkflowStep> Steps)
{
    /// <summary>The single next action: the first current step that has one.</summary>
    public AuthWorkflowStep? Next => Steps.FirstOrDefault(s => s.State == AuthWorkflowStepState.Current && s.ActionId is not null);
    public bool CanContinueToDiscovery => Session == AuthSessionDimension.Authenticated || Testing is AuthTestingDimension.Connected or AuthTestingDimension.Verified;
}

/// <summary>
/// Configuration → (certificate → proxy → dedicated browser) → sign-in with MFA → verify session → continue to Endpoint Discovery, derived
/// from the existing pane state and prerequisite cards — never a second readiness model. Configuration readiness, session readiness and
/// testing readiness stay three answers: a configured provider is not a session, and a launched browser is not a sign-in.
/// </summary>
public static class AuthenticationWorkflow
{
    public const string VerifyAction = "workflow-verify", DiscoveryAction = "workflow-discovery", ReviewAction = "workflow-review",
        UnattendedSettingsAction = "workflow-unattended-settings";

    public static AuthConfigurationDimension ConfigurationOf(AuthenticationPaneState pane, IReadOnlyList<AuthenticationAdoptionItem> adoption)
    {
        if (adoption.Any(a => a.Outcome == AuthenticationAdoptionOutcome.Conflict)) return AuthConfigurationDimension.Conflict;
        var agrees = adoption.Any(a => a.Outcome is AuthenticationAdoptionOutcome.Equal && a.Setting is AuthenticationAdoption.Authority or AuthenticationAdoption.ClientId);
        return pane.Configuration switch
        {
            AuthConfigurationState.Configured => agrees ? AuthConfigurationDimension.Validated : AuthConfigurationDimension.Configured,
            _ when pane.Detection is AuthDetectionState.Detected or AuthDetectionState.PartiallyDetected => AuthConfigurationDimension.Detected,
            AuthConfigurationState.Partial => AuthConfigurationDimension.Detected,
            _ => AuthConfigurationDimension.Missing,
        };
    }

    public static AuthSessionDimension SessionOf(AuthenticationPaneState pane, LocalHttpsProxyStatus? proxy, bool browserRouted, AuthenticatedTestingMethod method)
    {
        if (pane.TestingContext == AuthenticatedTestingState.Expired || proxy?.CredentialExpired == true) return AuthSessionDimension.Expired;
        if (pane.AuthenticatedApiAvailable || pane.AuthenticatedDomAvailable) return AuthSessionDimension.Authenticated;
        if (pane.Verification == AuthVerificationState.Failed) return AuthSessionDimension.Failed;
        if (method == AuthenticatedTestingMethod.LocalHttpsProxy && proxy?.State is LocalHttpsProxyState.Listening or LocalHttpsProxyState.WaitingForAuthenticatedTraffic && browserRouted) return AuthSessionDimension.InProgress;
        if (pane.TestingContext == AuthenticatedTestingState.WaitingForTraffic) return AuthSessionDimension.InProgress;
        return AuthSessionDimension.NotStarted;
    }

    public static AuthTestingDimension TestingOf(AuthenticationPaneState pane, bool authenticatedRequestObserved) => pane.TestingContext switch
    {
        AuthenticatedTestingState.ManualOnly or AuthenticatedTestingState.Expired => AuthTestingDimension.Blocked,
        AuthenticatedTestingState.Ready or AuthenticatedTestingState.Partial => authenticatedRequestObserved ? AuthTestingDimension.Verified : AuthTestingDimension.Connected,
        _ => AuthTestingDimension.NotConnected,
    };

    public static AuthenticationWorkflowState Derive(AuthenticationMode mode, AuthenticationPaneState pane, IReadOnlyList<AuthenticationAdoptionItem> adoption,
        AuthenticatedTestingMethod method, LocalHttpsProxyStatus? proxy, bool browserRouted, bool authenticatedRequestObserved, bool detectionRun,
        UnattendedIdentityStatus? unattended = null)
    {
        var configuration = ConfigurationOf(pane, adoption);
        var session = SessionOf(pane, proxy, browserRouted, method);
        var testing = TestingOf(pane, authenticatedRequestObserved);
        var steps = new List<AuthWorkflowStep>();
        var conflicts = adoption.Count(a => a.NeedsReview);

        var configOk = configuration is AuthConfigurationDimension.Validated or AuthConfigurationDimension.Configured;
        steps.Add(configuration switch
        {
            AuthConfigurationDimension.Conflict => new("configuration", "Configuration", AuthWorkflowStepState.Current, "Needs review",
                $"{conflicts} detected value{(conflicts == 1 ? "" : "s")} differ{(conflicts == 1 ? "s" : "")} from the saved configuration. Nothing was changed.", "Review configuration", ReviewAction),
            _ when configOk && !detectionRun => new("configuration", "Configuration", AuthWorkflowStepState.Current, "Saved — not checked against the target",
                "Compare the saved configuration with the target's public sign-in configuration. Saved values are never overwritten.", "Check against the target", AuthenticationSettingsChecks.CheckActionId),
            _ when configOk => new("configuration", "Configuration", AuthWorkflowStepState.Done, configuration == AuthConfigurationDimension.Validated ? "Detected and validated" : "Configured",
                "The authentication configuration this environment expects is saved."),
            _ when !detectionRun => new("configuration", "Configuration", AuthWorkflowStepState.Current, "Not checked",
                "Read the target's public sign-in configuration. Safe missing values are filled in; nothing that is saved is overwritten.", "Detect authentication", AuthenticationSettingsChecks.CheckActionId),
            _ when conflicts > 0 => new("configuration", "Configuration", AuthWorkflowStepState.Current, "Needs review",
                $"{conflicts} detected value{(conflicts == 1 ? "" : "s")} could not be validated automatically and need{(conflicts == 1 ? "s" : "")} a decision.", "Review configuration", ReviewAction),
            _ => new("configuration", "Configuration", AuthWorkflowStepState.Current, configuration == AuthConfigurationDimension.Detected ? "Partially detected" : "Missing",
                "The target did not expose enough to configure authentication automatically. Enter the expected values under Advanced.", "Check again", AuthenticationSettingsChecks.CheckActionId),
        });

        if (mode == AuthenticationMode.Unattended)
        {
            var u = unattended;
            steps.Add(u is { Configured: true }
                ? new("identity", "Unattended identity", AuthWorkflowStepState.Done, $"{u.IdentityType} configured",
                    $"Credential reference: {u.CredentialReference ?? "configured"} (the secret itself is never shown). Its permissions are granted separately from the browser application.")
                : new("identity", "Unattended identity", configOk ? AuthWorkflowStepState.Current : AuthWorkflowStepState.Waiting, "Not configured",
                    "An unattended run needs its own authorized identity (configured in System Settings → Authentication). The browser application's client ID is not a service identity.",
                    "Open System Settings → Authentication", UnattendedSettingsAction));
            steps.Add(u is { LastVerificationPassed: true }
                ? new("verify", "Verify access", AuthWorkflowStepState.Done, "Verified", u.LastVerification ?? "The target accepted the identity.")
                : u is { LastVerificationPassed: false }
                    ? new("verify", "Verify access", AuthWorkflowStepState.Blocked, "Failed", u.LastVerification ?? "The target did not accept the identity.")
                    : new("verify", "Verify access", u is { Configured: true } ? AuthWorkflowStepState.Current : AuthWorkflowStepState.Waiting, "Not verified",
                        "Verified only when the target accepts the identity with the required authorization (a safe, read-only request)."));
            steps.Add(new("discovery", "Endpoint Discovery", AuthWorkflowStepState.Waiting, "Source-only available",
                "Source-derived endpoints never need a sign-in. Authenticated runtime discovery needs a verified identity.", "Open Endpoint Discovery", DiscoveryAction));
            return Finish(mode, configuration, session, testing, steps);
        }

        var prereqs = pane.Readiness.Prerequisites.ToDictionary(p => p.Id, StringComparer.Ordinal);
        AuthWorkflowStep FromPrereq(string id, string title, string? blockedBy, string waitingText)
        {
            if (!prereqs.TryGetValue(id, out var p)) return new(id, title, AuthWorkflowStepState.NotApplicable, "Not needed", "Not part of the selected method.");
            // A step is never complete while the step it depends on is not (a browser "ready" without a running proxy cannot be observed).
            if (blockedBy is not null) return new(id, title, AuthWorkflowStepState.Waiting, "Waiting", waitingText, p.ActionLabel, null, blockedBy);
            if (p.State is AuthPrerequisiteState.Ok or AuthPrerequisiteState.NotRequired) return new(id, title, AuthWorkflowStepState.Done, p.StatusLabel, p.Explanation);
            return new(id, title, p.State == AuthPrerequisiteState.Failed ? AuthWorkflowStepState.Blocked : AuthWorkflowStepState.Current, p.StatusLabel, p.Explanation, p.ActionLabel, p.ActionId);
        }

        if (method == AuthenticatedTestingMethod.LocalHttpsProxy)
        {
            var cert = FromPrereq(AuthenticationReadinessPresentation.CertificateId, "HTTPS inspection certificate", null, "");
            steps.Add(cert);
            var proxyStep = FromPrereq(AuthenticationReadinessPresentation.ProxyId, "Local HTTPS proxy",
                cert.State is AuthWorkflowStepState.Done or AuthWorkflowStepState.NotApplicable ? null : "Trust the HTTPS inspection certificate first.", "Starts after the certificate is trusted.");
            steps.Add(proxyStep);
            steps.Add(FromPrereq(AuthenticationReadinessPresentation.BrowserId, "Dedicated browser",
                proxyStep.State is AuthWorkflowStepState.Done or AuthWorkflowStepState.NotApplicable ? null : "Start the proxy first — a browser without it cannot be observed.", "Opens once the proxy is listening."));
        }
        else if (method == AuthenticatedTestingMethod.ManagedEdgeCdp && prereqs.ContainsKey(AuthenticationReadinessPresentation.BrowserId))
            steps.Add(FromPrereq(AuthenticationReadinessPresentation.BrowserId, "Managed browser", null, ""));

        var setupDone = steps.Skip(1).All(s => s.State is AuthWorkflowStepState.Done or AuthWorkflowStepState.NotApplicable);
        if (method == AuthenticatedTestingMethod.ManualOnly)
        {
            steps.Add(new("sign-in", "Manual sign-in check", pane.Verification == AuthVerificationState.Verified ? AuthWorkflowStepState.Done : AuthWorkflowStepState.Current,
                pane.Verification == AuthVerificationState.Verified ? "Recorded" : "Not recorded",
                "A person signs in and records what they observed. This method gives no automated authenticated access."));
            steps.Add(new("discovery", "Endpoint Discovery", AuthWorkflowStepState.Waiting, "Source-only and unauthenticated",
                "Authenticated runtime discovery needs an automated method (local HTTPS proxy or managed browser).", "Open Endpoint Discovery", DiscoveryAction));
            return Finish(mode, configuration, session, testing, steps);
        }

        steps.Add(session switch
        {
            AuthSessionDimension.Authenticated => new("sign-in", "Sign in with MFA", AuthWorkflowStepState.Done, "Authentication session detected", "An authenticated session was observed through the dedicated browser."),
            AuthSessionDimension.Expired => new("sign-in", "Sign in with MFA", AuthWorkflowStepState.Current, "Session expired", "Sign in again in the dedicated browser."),
            AuthSessionDimension.InProgress => new("sign-in", "Sign in with MFA", AuthWorkflowStepState.Current, "Waiting for sign-in",
                "Complete the Microsoft sign-in (and MFA) in the dedicated browser. BirkNext never asks for your password or MFA code."),
            AuthSessionDimension.Failed => new("sign-in", "Sign in with MFA", AuthWorkflowStepState.Blocked, "Sign-in failed", "The recorded sign-in check failed. Sign in again."),
            _ => new("sign-in", "Sign in with MFA", setupDone ? AuthWorkflowStepState.Current : AuthWorkflowStepState.Waiting, "Sign-in not started",
                setupDone ? "Sign in in the dedicated browser. A browser launch alone is not a sign-in." : "Available once the steps above are done."),
        });
        steps.Add(testing switch
        {
            AuthTestingDimension.Verified => new("verify", "Verify session", AuthWorkflowStepState.Done, "Session verified", "An authenticated request succeeded with the captured context."),
            AuthTestingDimension.Connected => new("verify", "Verify session", AuthWorkflowStepState.Current, "Session detected — not verified",
                "Verify with an authorized, non-destructive request. A redirect or a cookie alone is not proof of authorization.", "Verify session", VerifyAction),
            AuthTestingDimension.Blocked => new("verify", "Verify session", AuthWorkflowStepState.Blocked, "Blocked", session == AuthSessionDimension.Expired ? "The session expired." : "This method gives no automated authenticated access."),
            _ => new("verify", "Verify session", AuthWorkflowStepState.Waiting, "Not verified", "Available after sign-in."),
        });
        var canContinue = session == AuthSessionDimension.Authenticated || testing is AuthTestingDimension.Connected or AuthTestingDimension.Verified;
        steps.Add(new("discovery", "Continue to Endpoint Discovery", canContinue ? (testing == AuthTestingDimension.Verified ? AuthWorkflowStepState.Current : AuthWorkflowStepState.Waiting) : AuthWorkflowStepState.Waiting,
            canContinue ? "Authenticated discovery available" : "Source-only available",
            canContinue ? "Authenticated runtime discovery can use this session." : "Source-derived endpoints never need a sign-in; authenticated discovery waits for a session.",
            "Open Endpoint Discovery", DiscoveryAction));
        return Finish(mode, configuration, session, testing, steps);
    }

    private static AuthenticationWorkflowState Finish(AuthenticationMode mode, AuthConfigurationDimension configuration, AuthSessionDimension session, AuthTestingDimension testing,
        List<AuthWorkflowStep> steps)
    {
        var next = steps.FirstOrDefault(s => s.State is AuthWorkflowStepState.Current or AuthWorkflowStepState.Blocked);
        var (status, detail) = (configuration, session, testing) switch
        {
            (AuthConfigurationDimension.Conflict, _, _) => ("Configuration needs review", "A detected value differs from the saved configuration. Nothing was changed."),
            (_, AuthSessionDimension.Authenticated, AuthTestingDimension.Verified) => ("Signed in — Session verified", "Authenticated testing can use this session."),
            (_, AuthSessionDimension.Authenticated, _) => ("Signed in — Verify the session", "A session was observed; verify it before relying on it."),
            (_, AuthSessionDimension.Expired, _) => ("Session expired — Sign in again", "The captured session is no longer valid."),
            (_, AuthSessionDimension.InProgress, _) => ("Waiting for sign-in", "Complete the sign-in in the dedicated browser."),
            (AuthConfigurationDimension.Missing, _, _) => ("Authentication not configured", next?.Explanation ?? ""),
            _ when mode == AuthenticationMode.Unattended => ("Unattended authentication", next?.Explanation ?? "Unattended identity verified."),
            _ when next is { } n => ($"{ConfigLabel(configuration)} — {n.Title}: {n.StatusLabel}", n.Explanation),
            _ => ($"{ConfigLabel(configuration)} — Sign-in not started", ""),
        };
        return new(mode, configuration, session, testing, status, detail, steps);
    }

    public static string ConfigLabel(AuthConfigurationDimension c) => c switch
    {
        AuthConfigurationDimension.Validated => "Configuration validated",
        AuthConfigurationDimension.Configured => "Configuration saved",
        AuthConfigurationDimension.Detected => "Configuration detected",
        AuthConfigurationDimension.Conflict => "Configuration conflict",
        _ => "Configuration missing",
    };

    public static string SessionLabel(AuthSessionDimension s) => s switch
    {
        AuthSessionDimension.InProgress => "Sign-in in progress",
        AuthSessionDimension.Authenticated => "Authenticated",
        AuthSessionDimension.Expired => "Expired",
        AuthSessionDimension.Failed => "Failed",
        _ => "Not started",
    };

    public static string TestingLabel(AuthTestingDimension t) => t switch
    {
        AuthTestingDimension.Connected => "Connected",
        AuthTestingDimension.Verified => "Verified",
        AuthTestingDimension.Blocked => "Blocked",
        _ => "Not connected",
    };

    public static SemanticStatus StepStatus(AuthWorkflowStepState s) => s switch
    {
        AuthWorkflowStepState.Done => SemanticStatus.Completed,
        AuthWorkflowStepState.Current => SemanticStatus.NeedsReview,
        AuthWorkflowStepState.Blocked => SemanticStatus.Blocked,
        AuthWorkflowStepState.NotApplicable => SemanticStatus.NotApplicable,
        _ => SemanticStatus.NotRun,
    };
}
