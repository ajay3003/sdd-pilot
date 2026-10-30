using System.Text.Json.Serialization;

namespace BirkNext.BrowserAuthentication;

// ── Browser authentication modes ───────────────────────────────────────────────────────────────────────────────────────
// BirkNext supports two browser authentication modes side by side; they are never one setting with two values.
//   Attended   — the existing flows (dedicated Edge session, Managed Edge, local HTTPS proxy). A person signs in and completes MFA.
//                Unchanged and still the default for every consumer.
//   Unattended — NEW: an isolated automated browser signs in with a dedicated automation test identity that does not require MFA
//                according to its test configuration. MFA is never automated: an MFA or interactive challenge stops the login.
// Browser login is one authentication hop only. It never satisfies Altinn → MU, BirkNext → Altinn API credentials or any
// service-to-service, Service Bus or Event Hub authentication. The name is deliberately "unattended browser login", distinct from
// the Altinn ID-porten "unattended client" (a token flow without a browser).

/// <summary>Browser authentication a consumer explicitly requests. <see cref="AttendedBrowser"/> is the default and is never replaced.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAuthenticationMode { AttendedBrowser, UnattendedBrowser }

/// <summary>
/// How the unattended login signs in. <see cref="Generic"/> fills a login form on the configured page. <see cref="MicrosoftEntraId"/> opens the
/// application, follows its redirect to Microsoft Entra ID (login.microsoftonline.com), handles the username, account-picker, password and
/// stay-signed-in screens, returns to the application and verifies the signed-in identity and a protected resource.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnattendedLoginProvider { Generic, MicrosoftEntraId }

/// <summary>Result of one verification step of a validation. Steps after the first failure are <see cref="NotReached"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnattendedLoginStepStatus { Passed, Failed, RequiresInteraction, NotReached, NotApplicable }

/// <summary>One verification step (application connectivity, Entra ID redirect, login form, credential submission, authentication completion,
/// test-user identity, protected resource). The detail never contains a credential, token or cookie.</summary>
public sealed record UnattendedLoginStep(string Key, string Label, UnattendedLoginStepStatus Status, string Detail);

/// <summary>What the card shows: Configured (saved, not validated), Connected (validated end to end with the current configuration), Failed,
/// Requires interaction (MFA/conditional access), Disabled, Not configured.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnattendedConnectionStatus { NotConfigured, Configured, Connected, Failed, RequiresInteraction, Disabled }

/// <summary>State of the unattended browser login capability (System Settings → Authentication card).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnattendedLoginState { NotConfigured, Disabled, NotVerified, Ready, ValidationFailed }

/// <summary>Outcome of one unattended login attempt. Each failure keeps its own meaning; none is collapsed into "Blocked".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnattendedLoginOutcome
{
    Succeeded,
    NotConfigured,
    Disabled,
    IdentityMissing,
    CredentialMissing,
    LoginTargetMissing,
    SuccessDetectionMissing,
    LoginPageNotRecognized,
    AuthenticationRejected,
    InteractiveChallengeDetected,
    ConditionalAccessChallenge,
    SuccessNotConfirmed,
    Timeout,
    TargetUnavailable,
    BrowserUnavailable,
    /// <summary>The application did not redirect to Microsoft Entra ID in a fresh browser context: nothing was authenticated.</summary>
    EntraRedirectNotDetected,
    /// <summary>The login reached a screen BirkNext does not automate (passkey/security key, certificate, federation to another provider…).</summary>
    UnsupportedAuthenticationFlow,
    /// <summary>A different account than the configured test identity is signed in or offered.</summary>
    IdentityMismatch,
    /// <summary>The application was reached, but the configured test identity could not be confirmed in it.</summary>
    IdentityNotVerified,
    /// <summary>The protected application resource was not accessible after sign-in.</summary>
    ProtectedResourceDenied,
}

public sealed record UnattendedLoginRequirement(string Key, string Label, bool Met, string Detail);

public sealed record UnattendedLoginValidation(UnattendedLoginOutcome Outcome, string Message, DateTimeOffset ValidatedAt, bool Current, long DurationMs)
{
    public List<UnattendedLoginStep> Steps { get; init; } = [];
}

public sealed record UnattendedLoginAuditEntry(DateTimeOffset At, string Event);

/// <summary>
/// What the API returns about the unattended login. Never contains the password, the raw login name, cookies or tokens: the
/// credential is described by <see cref="CredentialConfigured"/>, a masked login-name hint and when it was last set.
/// </summary>
public sealed record UnattendedLoginView
{
    public bool Configured { get; init; }
    public bool Enabled { get; init; }
    public UnattendedLoginState State { get; init; }
    public string StateLabel { get; init; } = "";
    public string? IdentityAlias { get; init; }
    public bool CredentialConfigured { get; init; }
    /// <summary>Masked login name ("a•••@e•••.test"), only to recognise which account is set.</summary>
    public string? LoginNameHint { get; init; }
    public DateTimeOffset? CredentialUpdatedAt { get; init; }
    public string CredentialStorage { get; init; } = "";
    public string? LoginUrl { get; init; }
    public string? SuccessUrlContains { get; init; }
    public string? SuccessSelector { get; init; }
    public string? UsernameSelector { get; init; }
    public string? PasswordSelector { get; init; }
    public string? SubmitSelector { get; init; }
    public UnattendedLoginProvider Provider { get; init; }
    /// <summary>Microsoft Entra ID only: the selected test identity (a test account name, shown so Edit keeps the selection). Null for Generic,
    /// whose login name stays masked.</summary>
    public string? TestIdentity { get; init; }
    /// <summary>Microsoft Entra ID only: the application resource that must be accessible after sign-in (defaults to the application URL).</summary>
    public string? ProtectedResourceUrl { get; init; }
    public UnattendedConnectionStatus ConnectionStatus { get; init; }
    public string ConnectionStatusLabel { get; init; } = "";
    public UnattendedLoginValidation? LastValidation { get; init; }
    public List<UnattendedLoginRequirement> Requirements { get; init; } = [];
    public List<UnattendedLoginAuditEntry> Audit { get; init; } = [];
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Saves the unattended configuration. <see cref="LoginName"/> and <see cref="Password"/> are write-only: null or empty keeps the stored
/// credential, so editing other fields never needs the secret again. Both are stored only in protected storage.
/// </summary>
public sealed record SaveUnattendedLoginRequest
{
    public string? IdentityAlias { get; init; }
    public string? LoginName { get; init; }
    public string? Password { get; init; }
    public string? LoginUrl { get; init; }
    public string? SuccessUrlContains { get; init; }
    public string? SuccessSelector { get; init; }
    public string? UsernameSelector { get; init; }
    public string? PasswordSelector { get; init; }
    public string? SubmitSelector { get; init; }
    public bool Enabled { get; init; } = true;
    public UnattendedLoginProvider Provider { get; init; }
    public string? ProtectedResourceUrl { get; init; }
}

public sealed record SetUnattendedLoginEnabledRequest(bool Enabled);

public sealed record UnattendedLoginValidationResult(UnattendedLoginOutcome Outcome, string Message, string? Recommendation, long DurationMs, UnattendedLoginView View)
{
    public List<UnattendedLoginStep> Steps { get; init; } = [];
}

/// <summary>
/// Whether automated (unattended) browser execution can authenticate. Browser authentication only: backend/integration authentication,
/// TLS trust and environment safety stay with each consumer and are never resolved here. Never falls back to attended login.
/// </summary>
public sealed record AutomatedBrowserExecutionReadiness(bool Available, string Status, string Reason, UnattendedLoginState Unattended,
    List<UnattendedLoginRequirement> Requirements);

public static class BrowserAuthenticationText
{
    public const string AttendedTitle = "Attended login";
    public const string UnattendedTitle = "Unattended login";
    public const string AttendedPurpose = "Used when a person must complete login or MFA.";
    public const string UnattendedPurpose = "Used for automated browser tests with a dedicated test identity that does not require MFA.";

    /// <summary>The known Microsoft Entra ID test identities offered in the username combo box (a test account, not a secret).</summary>
    public static readonly IReadOnlyList<string> EntraTestIdentities = ["testbruker01@bufdir.no"];
    public const string EntraDefaultApplicationUrl = "https://m2lbdev.bufetat.no";

    public static string Provider(UnattendedLoginProvider provider) => provider switch
    {
        UnattendedLoginProvider.MicrosoftEntraId => "Microsoft Entra ID",
        _ => "Login form",
    };

    public static string Connection(UnattendedConnectionStatus status) => status switch
    {
        UnattendedConnectionStatus.NotConfigured => "Not configured",
        UnattendedConnectionStatus.Configured => "Configured",
        UnattendedConnectionStatus.Connected => "Connected",
        UnattendedConnectionStatus.Failed => "Failed",
        UnattendedConnectionStatus.RequiresInteraction => "Requires interaction",
        UnattendedConnectionStatus.Disabled => "Disabled",
        _ => status.ToString(),
    };

    public static string Step(UnattendedLoginStepStatus status) => status switch
    {
        UnattendedLoginStepStatus.Passed => "Passed",
        UnattendedLoginStepStatus.Failed => "Failed",
        UnattendedLoginStepStatus.RequiresInteraction => "Requires interaction",
        UnattendedLoginStepStatus.NotReached => "Not reached",
        UnattendedLoginStepStatus.NotApplicable => "Not applicable",
        _ => status.ToString(),
    };

    /// <summary>MFA and conditional access need a person; every other failed outcome is a failure.</summary>
    public static bool RequiresInteraction(UnattendedLoginOutcome outcome) =>
        outcome is UnattendedLoginOutcome.InteractiveChallengeDetected or UnattendedLoginOutcome.ConditionalAccessChallenge;

    public static string State(UnattendedLoginState state) => state switch
    {
        UnattendedLoginState.NotConfigured => "Not configured",
        UnattendedLoginState.Disabled => "Configured · Disabled",
        UnattendedLoginState.NotVerified => "Not verified",
        UnattendedLoginState.Ready => "Ready",
        UnattendedLoginState.ValidationFailed => "Validation failed",
        _ => state.ToString(),
    };

    public static string Outcome(UnattendedLoginOutcome outcome) => outcome switch
    {
        UnattendedLoginOutcome.Succeeded => "Ready",
        UnattendedLoginOutcome.NotConfigured => "Not configured",
        UnattendedLoginOutcome.Disabled => "Disabled",
        UnattendedLoginOutcome.IdentityMissing => "Identity missing",
        UnattendedLoginOutcome.CredentialMissing => "Credential missing",
        UnattendedLoginOutcome.LoginTargetMissing => "Login target missing",
        UnattendedLoginOutcome.SuccessDetectionMissing => "Success detection missing",
        UnattendedLoginOutcome.LoginPageNotRecognized => "Login page not recognized",
        UnattendedLoginOutcome.AuthenticationRejected => "Authentication rejected",
        UnattendedLoginOutcome.InteractiveChallengeDetected => "MFA or interactive challenge detected",
        UnattendedLoginOutcome.ConditionalAccessChallenge => "Interactive/conditional access challenge detected",
        UnattendedLoginOutcome.SuccessNotConfirmed => "Success marker not found",
        UnattendedLoginOutcome.Timeout => "Login timed out",
        UnattendedLoginOutcome.TargetUnavailable => "Login target unavailable",
        UnattendedLoginOutcome.BrowserUnavailable => "Browser engine unavailable",
        UnattendedLoginOutcome.EntraRedirectNotDetected => "Entra ID redirect not detected",
        UnattendedLoginOutcome.UnsupportedAuthenticationFlow => "Unsupported authentication flow",
        UnattendedLoginOutcome.IdentityMismatch => "Different account signed in",
        UnattendedLoginOutcome.IdentityNotVerified => "Test identity not verified",
        UnattendedLoginOutcome.ProtectedResourceDenied => "Protected resource not accessible",
        _ => outcome.ToString(),
    };

    /// <summary>What to do next. MFA and conditional access recommend attended login; nothing ever switches mode automatically.</summary>
    public static string? Recommendation(UnattendedLoginOutcome outcome) => outcome switch
    {
        UnattendedLoginOutcome.InteractiveChallengeDetected or UnattendedLoginOutcome.ConditionalAccessChallenge =>
            "This identity needs interactive authentication. Use attended login for it, or configure an automation identity that does not require MFA.",
        UnattendedLoginOutcome.AuthenticationRejected => "Check the login name and password. BirkNext does not retry rejected credentials, so the account is not locked by repeated attempts.",
        UnattendedLoginOutcome.SuccessNotConfirmed => "Check the success detection: the URL after login or the authenticated page marker did not appear.",
        UnattendedLoginOutcome.LoginPageNotRecognized => "Check the login URL, or set the username/password field selectors under Advanced.",
        UnattendedLoginOutcome.Timeout => "The login did not finish in time. Check that the login target responds, then validate again.",
        UnattendedLoginOutcome.TargetUnavailable => "The login target could not be reached. Check the URL, network and TLS trust.",
        UnattendedLoginOutcome.BrowserUnavailable => "The automation browser could not start. Check Frontend Testing Capabilities for the browser engine.",
        UnattendedLoginOutcome.CredentialMissing => "Enter the login name and password of the automation identity.",
        UnattendedLoginOutcome.IdentityMissing => "Enter the automation identity name.",
        UnattendedLoginOutcome.LoginTargetMissing => "Enter the login URL.",
        UnattendedLoginOutcome.SuccessDetectionMissing => "Enter the URL text expected after login or an authenticated page marker.",
        UnattendedLoginOutcome.EntraRedirectNotDetected => "Check the application URL: opened in a fresh browser it must redirect to Microsoft Entra ID sign-in. An existing session is never accepted as proof.",
        UnattendedLoginOutcome.UnsupportedAuthenticationFlow => "This sign-in needs a method BirkNext does not automate (passkey, security key, certificate or another identity provider). Use attended login.",
        UnattendedLoginOutcome.IdentityMismatch => "Another account than the configured test identity was offered or signed in. Check the test identity.",
        UnattendedLoginOutcome.IdentityNotVerified => "Sign-in finished, but the application did not show the configured test identity. Set an authenticated page marker that shows the signed-in user.",
        UnattendedLoginOutcome.ProtectedResourceDenied => "Check the protected resource URL and that the test identity has access to it.",
        _ => null,
    };
}
