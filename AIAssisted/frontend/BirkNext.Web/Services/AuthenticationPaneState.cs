using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// What is SAVED for this Target Environment. Nothing to do with what the deployed target turned out to use, whether a
/// person confirmed the sign-in workflow, or whether BirkNext can currently reach a protected endpoint.
/// </summary>
public enum AuthConfigurationState
{
    /// <summary>There is no profile to read yet.</summary>
    Unknown,
    /// <summary>No provider and no expected identifiers are saved.</summary>
    NotConfigured,
    /// <summary>Something is saved, but not enough to describe the provider completely.</summary>
    Partial,
    /// <summary>A provider is saved together with the identifiers it needs.</summary>
    Configured,
}

/// <summary>What detection observed about the deployed target. Never a statement about the saved configuration.</summary>
public enum AuthDetectionState
{
    /// <summary>Detect settings has not been run for this environment.</summary>
    NotRun,
    /// <summary>Detection ran but could not answer — unreachable, blocked, or an error.</summary>
    Unavailable,
    /// <summary>Detection ran and found no supported authentication.</summary>
    NotDetected,
    /// <summary>Detection ran and found authentication, but not the identifiers that describe it.</summary>
    PartiallyDetected,
    /// <summary>Detection ran and identified the provider and its identifiers.</summary>
    Detected,
}

/// <summary>
/// Whether a person has confirmed that the configured sign-in workflow actually works. Passing this creates no runtime
/// context, and a runtime context appearing never marks it passed.
/// </summary>
public enum AuthVerificationState
{
    /// <summary>Nothing about this environment asks for a verification.</summary>
    NotApplicable,
    /// <summary>A verification is required and has not been recorded, or the recorded one no longer applies.</summary>
    Required,
    /// <summary>A verification was recorded as passed and still describes the current settings.</summary>
    Verified,
    /// <summary>A verification was recorded as failed.</summary>
    Failed,
}

/// <summary>
/// The one canonical description of the Authentication pane, assembled once and read by every card.
///
/// The five concepts it keeps apart are the whole reason it exists:
/// <list type="bullet">
/// <item>Configuration — what is saved.</item>
/// <item>Detection — what the deployed target turned out to use.</item>
/// <item>Verification — whether a person confirmed the sign-in workflow.</item>
/// <item>Testing context — whether an authenticated request could be made right now.</item>
/// <item>Readiness — whether the prerequisites for authenticated testing are in place.</item>
/// </list>
/// All five can disagree, correctly. A card that derives its own answer to one of them from another is how a page ends
/// up saying "No authentication is configured" directly above "Matches saved configuration".
/// </summary>
public sealed record AuthenticationPaneState(
    AuthConfigurationState Configuration,
    AuthDetectionState Detection,
    AuthenticationMatchState Match,
    AuthVerificationState Verification,
    AuthenticatedTestingState TestingContext,
    bool AuthenticatedApiAvailable,
    bool AuthenticatedDomAvailable,
    AuthenticationReadinessSummary Readiness)
{
    /// <summary>The prerequisites standing between the user and authenticated testing. Empty when there are none.</summary>
    public IReadOnlyList<AuthenticationPrerequisite> ReadinessIssues =>
        Readiness.Prerequisites.Where(p => p.Blocks).ToList();
}

/// <summary>
/// The derivations and the wording for the four saved/observed/confirmed/available concepts.
///
/// Pure over its inputs — no clock, no services, no runtime lookups — so each rule can be tested as a rule.
/// </summary>
public static class AuthenticationPaneStates
{
    // ── Configuration ────────────────────────────────────────────────────────
    //
    // "Requires authentication" describes the TARGET's own access rule and is deliberately not read here. A target that
    // needs no sign-in can still have a fully configured provider, and a target that needs one can have nothing saved.

    /// <summary>
    /// What is saved for this environment. A provider on its own is Partial: Entra ID with no authority and no client id
    /// does not describe anything BirkNext could check against.
    /// </summary>
    public static AuthConfigurationState Configuration(FrontendAuthenticationSettings? settings)
    {
        if (settings is null) return AuthConfigurationState.Unknown;

        var hasIdentifiers = !string.IsNullOrWhiteSpace(settings.ExpectedAuthority)
            || !string.IsNullOrWhiteSpace(settings.ExpectedTenant)
            || !string.IsNullOrWhiteSpace(settings.ExpectedClientId)
            || settings.AllowedRedirectUrls.Count > 0;

        if (settings.AuthenticationType == FrontendAuthenticationType.None)
            // Identifiers without a provider, or a sign-in requirement with nothing behind it, is half a configuration.
            return hasIdentifiers || settings.RequiresAuthentication
                ? AuthConfigurationState.Partial
                : AuthConfigurationState.NotConfigured;

        return RequiresIdentifiers(settings.AuthenticationType)
            ? (!string.IsNullOrWhiteSpace(settings.ExpectedAuthority) && !string.IsNullOrWhiteSpace(settings.ExpectedClientId)
                ? AuthConfigurationState.Configured : AuthConfigurationState.Partial)
            : AuthConfigurationState.Configured;
    }

    /// <summary>Providers whose configuration is only meaningful with an authority and a client id.</summary>
    private static bool RequiresIdentifiers(FrontendAuthenticationType type) => type
        is FrontendAuthenticationType.MicrosoftEntraId
        or FrontendAuthenticationType.OpenIdConnect
        or FrontendAuthenticationType.OAuth2;

    public static string ConfigurationLabel(AuthConfigurationState state) => state switch
    {
        AuthConfigurationState.Configured => "Configured",
        AuthConfigurationState.Partial => "Partially configured",
        AuthConfigurationState.NotConfigured => "Not configured",
        _ => "Unknown",
    };

    public static string ConfigurationTone(AuthConfigurationState state) => state switch
    {
        AuthConfigurationState.Configured => "ready",
        AuthConfigurationState.Partial => "needs-action",
        _ => "muted",
    };

    // ── Detection ────────────────────────────────────────────────────────────

    /// <summary>
    /// What detection observed. <paramref name="hasDetectedSettings"/> is the caller's existing "the result carries
    /// usable authentication values" rule, passed in so this stays the only place the STATE is named.
    /// </summary>
    public static AuthDetectionState Detection(TargetEnvironmentDetectionResult? detection, bool hasDetectedSettings) => detection switch
    {
        null => AuthDetectionState.NotRun,
        { Success: true } when hasDetectedSettings => AuthDetectionState.Detected,
        { Success: true, AuthenticationRequired: true } => AuthDetectionState.PartiallyDetected,
        { Success: true } => AuthDetectionState.NotDetected,
        _ => AuthDetectionState.Unavailable,
    };

    public static string DetectionLabel(AuthDetectionState state) => state switch
    {
        AuthDetectionState.Detected => "Detected",
        AuthDetectionState.PartiallyDetected => "Partially detected",
        AuthDetectionState.NotDetected => "No supported authentication detected",
        AuthDetectionState.Unavailable => "Not detected",
        _ => "Not run",
    };

    // ── Verification ─────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the configured sign-in workflow has been confirmed by a person. Takes the page's existing
    /// "a verification is required here" rule and the recorded outcome; it never inspects runtime evidence.
    /// </summary>
    public static AuthVerificationState Verification(bool required, ManualAuthenticationVerificationStatus status) =>
        !required ? AuthVerificationState.NotApplicable
        : status switch
        {
            ManualAuthenticationVerificationStatus.Passed => AuthVerificationState.Verified,
            ManualAuthenticationVerificationStatus.Failed => AuthVerificationState.Failed,
            _ => AuthVerificationState.Required,
        };

    public static string VerificationLabel(AuthVerificationState state) => state switch
    {
        AuthVerificationState.Verified => "Verified",
        AuthVerificationState.Failed => "Verification failed",
        AuthVerificationState.Required => "Manual verification required",
        _ => "Not required",
    };

    public static string VerificationTone(AuthVerificationState state) => state switch
    {
        AuthVerificationState.Verified => "ready",
        AuthVerificationState.Failed => "error",
        AuthVerificationState.Required => "needs-action",
        _ => "muted",
    };

    /// <summary>
    /// HOW a verification is performed — not how authentication was detected. The two were the same string on this page
    /// once, which is why a card could report "Manual verification required" with the method "Automated detection".
    /// </summary>
    public static string VerificationMethod(AuthVerificationState state) =>
        state == AuthVerificationState.NotApplicable ? "Not applicable" : "Manual verification in a signed-in browser";

    // ── Testing context ──────────────────────────────────────────────────────

    /// <summary>
    /// Whether an authenticated context exists right now, in the vocabulary <see cref="AuthenticatedTestingStates"/>
    /// already owns — so the summary row and the card badge cannot say two different things about one fact.
    ///
    /// <paramref name="observed"/> is the page's existing runtime derivation. The only thing added here is Expired,
    /// which the underlying states cannot express: a credential that has run out looks like never having had one.
    /// </summary>
    public static AuthenticatedTestingState TestingContext(AuthenticatedTestingState observed, bool credentialExpired) =>
        credentialExpired && observed is AuthenticatedTestingState.NotConnected or AuthenticatedTestingState.WaitingForTraffic
            ? AuthenticatedTestingState.Expired
            : observed;

    /// <summary>Availability of one capability, kept distinct from "not assessed".</summary>
    public static string Availability(bool available) => available ? "Available" : "Not available";

    // ── Assembly ─────────────────────────────────────────────────────────────

    /// <summary>The one place the pane's state is assembled. Cards read this; they never recompute a part of it.</summary>
    public static AuthenticationPaneState Derive(
        FrontendAuthenticationSettings? settings,
        TargetEnvironmentDetectionResult? detection,
        bool hasDetectedSettings,
        AuthenticationMatchState match,
        bool verificationRequired,
        ManualAuthenticationVerificationStatus verificationStatus,
        AuthenticatedTestingState observedTestingState,
        bool apiAvailable,
        bool domAvailable,
        bool credentialExpired,
        AuthenticationReadinessSummary readiness) =>
        new(Configuration(settings),
            Detection(detection, hasDetectedSettings),
            match,
            Verification(verificationRequired, verificationStatus),
            TestingContext(observedTestingState, credentialExpired),
            apiAvailable,
            domAvailable,
            readiness);
}
