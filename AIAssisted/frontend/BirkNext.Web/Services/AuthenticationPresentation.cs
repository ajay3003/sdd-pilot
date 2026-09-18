using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// How one detected value relates to the matching configured value. Detected is never the same thing as
/// Configured: a value is only <see cref="Match"/> when the two genuinely agree.
/// </summary>
public enum AuthenticationComparisonStatus
{
    /// <summary>Neither detected nor configured.</summary>
    NotAvailable,
    /// <summary>Detected and configured agree.</summary>
    Match,
    /// <summary>Detected and configured are both present and disagree.</summary>
    Different,
    /// <summary>Detected but not configured. Applying the proposal would add it.</summary>
    NotConfigured,
    /// <summary>Configured but not detected. Detection says nothing about it.</summary>
    NotDetected
}

/// <summary>Where the detected authentication proposal stands relative to the saved configuration.</summary>
public enum AuthenticationMatchState
{
    /// <summary>No usable authentication evidence from detection.</summary>
    NotDetected,
    /// <summary>Detected and available as a proposal; it does not match the current configuration.</summary>
    Detected,
    /// <summary>The proposal has been applied to the unsaved draft.</summary>
    AppliedToDraft,
    /// <summary>The proposal matches the persisted configuration.</summary>
    MatchesSaved,
    /// <summary>The evidence no longer describes the current settings; detection must be repeated.</summary>
    Stale
}

/// <summary>One row of the detected-versus-configured comparison.</summary>
/// <param name="Setting">User-facing name of the setting.</param>
/// <param name="Detected">What detection observed, or "—".</param>
/// <param name="Configured">What is saved (or drafted) in the Target Environment, or "—".</param>
/// <param name="Status">How the two relate.</param>
public sealed record AuthenticationComparisonRow(string Setting, string Detected, string Configured, AuthenticationComparisonStatus Status);

/// <summary>
/// Single source of user-facing authentication wording, status semantics and the detected-versus-configured
/// comparison shared by the Target Application and Authentication tabs. Target Application answers "what did
/// BirkNext detect about this target?"; Authentication answers "how is authentication configured, verified and
/// used for testing?". Both read their labels from here so the two tabs can never disagree.
/// </summary>
public static class AuthenticationPresentation
{
    public const string Unknown = "—";

    /// <summary>Identity provider names as a user reads them, not as the enum spells them.</summary>
    public static string ProviderLabel(FrontendAuthenticationType type) => type switch
    {
        FrontendAuthenticationType.MicrosoftEntraId => "Microsoft Entra ID",
        FrontendAuthenticationType.OpenIdConnect => "OpenID Connect",
        FrontendAuthenticationType.OAuth2 => "OAuth 2.0",
        FrontendAuthenticationType.Custom => "Custom identity provider",
        _ => "None"
    };

    /// <summary>The provider a detection result describes, without ever claiming more than the evidence does.</summary>
    public static string DetectedProviderLabel(TargetEnvironmentDetectionResult? detection) => detection is null
        ? "Not determined"
        : detection.DetectedAuthenticationType != FrontendAuthenticationType.None
            ? ProviderLabel(detection.DetectedAuthenticationType)
            : detection.AuthenticationRequired
                ? "Authentication required (provider not determined)"
                : "Not determined";

    // ── Comparison ───────────────────────────────────────────────────────────

    public static string StatusLabel(AuthenticationComparisonStatus status) => status switch
    {
        AuthenticationComparisonStatus.Match => "Match",
        AuthenticationComparisonStatus.Different => "Different",
        AuthenticationComparisonStatus.NotConfigured => "Not configured",
        AuthenticationComparisonStatus.NotDetected => "Not detected",
        _ => "Not available"
    };

    /// <summary>Text marker shown beside the status label so status is never carried by colour alone.</summary>
    public static string StatusMarker(AuthenticationComparisonStatus status) => status switch
    {
        AuthenticationComparisonStatus.Match => "✓",
        AuthenticationComparisonStatus.Different or AuthenticationComparisonStatus.NotConfigured => "!",
        _ => "·"
    };

    public static string StatusCss(AuthenticationComparisonStatus status) => status switch
    {
        AuthenticationComparisonStatus.Match => "match",
        AuthenticationComparisonStatus.Different or AuthenticationComparisonStatus.NotConfigured => "different",
        _ => "neutral"
    };

    /// <summary>True when the row describes something "Apply authentication" would change.</summary>
    public static bool IsActionable(AuthenticationComparisonStatus status) =>
        status is AuthenticationComparisonStatus.Different or AuthenticationComparisonStatus.NotConfigured;

    /// <summary>
    /// The one comparison table both the status card and the advanced details read from. Detected values come
    /// only from a successful detection; configured values come from the profile (draft while editing). Nothing
    /// here is ever written back — applying the proposal stays an explicit user action.
    /// </summary>
    public static IReadOnlyList<AuthenticationComparisonRow> Compare(
        TargetEnvironmentDetectionResult? detection, FrontendAuthenticationSettings? configured)
    {
        var d = detection is { Success: true } ? detection : null;
        var c = configured;

        var detectedProvider = d is not null && d.DetectedAuthenticationType != FrontendAuthenticationType.None
            ? ProviderLabel(d.DetectedAuthenticationType) : null;
        var configuredProvider = c is not null && c.AuthenticationType != FrontendAuthenticationType.None
            ? ProviderLabel(c.AuthenticationType) : null;
        var detectedTenant = !string.IsNullOrWhiteSpace(d?.DetectedTenantId) ? d!.DetectedTenantId : d?.TenantMode;

        return
        [
            Row("Identity provider", detectedProvider, configuredProvider),
            Row("Expected Authority", d?.DetectedAuthority, c?.ExpectedAuthority),
            Row("Expected Tenant", detectedTenant, c?.ExpectedTenant),
            Row("Expected Client ID", d?.DetectedClientId, c?.ExpectedClientId),
            RedirectRow(d?.DetectedRedirectUrls, c?.AllowedRedirectUrls)
        ];
    }

    private static AuthenticationComparisonRow Row(string setting, string? detected, string? configured)
    {
        var hasDetected = !string.IsNullOrWhiteSpace(detected);
        var hasConfigured = !string.IsNullOrWhiteSpace(configured);
        var status = (hasDetected, hasConfigured) switch
        {
            (true, true) => string.Equals(detected, configured, StringComparison.Ordinal)
                ? AuthenticationComparisonStatus.Match : AuthenticationComparisonStatus.Different,
            (true, false) => AuthenticationComparisonStatus.NotConfigured,
            (false, true) => AuthenticationComparisonStatus.NotDetected,
            _ => AuthenticationComparisonStatus.NotAvailable
        };
        return new(setting, hasDetected ? detected! : Unknown, hasConfigured ? configured! : Unknown, status);
    }

    /// <summary>
    /// Redirect URLs match when every detected URL is already allowed. A configuration that allows more URLs than
    /// were detected is still a Match — this mirrors the apply/compare rule the draft workflow uses.
    /// </summary>
    private static AuthenticationComparisonRow RedirectRow(IReadOnlyList<string>? detected, IReadOnlyList<string>? configured)
    {
        var d = detected ?? [];
        var c = configured ?? [];
        var status = (d.Count > 0, c.Count > 0) switch
        {
            (true, _) => d.All(url => c.Contains(url, StringComparer.OrdinalIgnoreCase))
                ? AuthenticationComparisonStatus.Match
                : c.Count > 0 ? AuthenticationComparisonStatus.Different : AuthenticationComparisonStatus.NotConfigured,
            (false, true) => AuthenticationComparisonStatus.NotDetected,
            _ => AuthenticationComparisonStatus.NotAvailable
        };
        return new("Allowed Redirect URLs", Join(d), Join(c), status);
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? Unknown : string.Join(", ", values);

    // ── Overall state ────────────────────────────────────────────────────────

    /// <summary>CSS state token. Kept identical to the previous card states so status styling stays shared.</summary>
    public static string StateCss(AuthenticationMatchState state) => state switch
    {
        AuthenticationMatchState.Stale => "needs-action",
        AuthenticationMatchState.MatchesSaved => "saved",
        AuthenticationMatchState.AppliedToDraft => "draft",
        AuthenticationMatchState.Detected => "detected",
        _ => "neutral"
    };

    /// <summary>Short badge wording next to the card heading.</summary>
    public static string StateLabel(AuthenticationMatchState state) => state switch
    {
        AuthenticationMatchState.Stale => "Detection stale — run Detect settings again",
        AuthenticationMatchState.MatchesSaved => "Matches saved configuration",
        AuthenticationMatchState.AppliedToDraft => "Applied to draft — Save changes to persist",
        AuthenticationMatchState.Detected => "Detected — proposal available",
        _ => "Not detected"
    };

    /// <summary>The one-sentence status line inside the card.</summary>
    public static string StateSummary(AuthenticationMatchState state) => state switch
    {
        AuthenticationMatchState.Stale => "Detection no longer describes the current settings. Run Detect settings again.",
        AuthenticationMatchState.MatchesSaved => "Detected authentication matches saved configuration",
        AuthenticationMatchState.AppliedToDraft => "Detected authentication applied to the draft. It is not saved yet.",
        AuthenticationMatchState.Detected => "Detected authentication differs from saved configuration",
        _ => "No authentication detected for this target yet"
    };

    /// <summary>Text marker for the status line, so the state never depends on colour alone.</summary>
    public static string StateMarker(AuthenticationMatchState state) => state switch
    {
        AuthenticationMatchState.MatchesSaved => "✓",
        AuthenticationMatchState.Detected or AuthenticationMatchState.Stale or AuthenticationMatchState.AppliedToDraft => "!",
        _ => "·"
    };

    /// <summary>
    /// What "Apply authentication" would do right now. Detected values are never saved automatically, so this
    /// always names the explicit next step and, when values differ, exactly which settings would change.
    /// </summary>
    public static string ApplyExplanation(AuthenticationMatchState state, IReadOnlyList<AuthenticationComparisonRow> comparison) => state switch
    {
        AuthenticationMatchState.Stale => "Detection is stale. Run Detect settings again before applying detected values.",
        AuthenticationMatchState.MatchesSaved => "Detected values already match saved configuration.",
        AuthenticationMatchState.AppliedToDraft => "Save changes to persist this configuration.",
        AuthenticationMatchState.Detected => ChangeList(comparison) is { Length: > 0 } changes
            ? $"Applying copies the detected values into the draft and changes: {changes}. Detected values are not saved automatically."
            : "Applying copies the detected values into the draft. Detected values are not saved automatically.",
        _ => "Detected values are not saved automatically."
    };

    /// <summary>Names of the settings "Apply authentication" would change, for the explicit-action wording.</summary>
    public static string ChangeList(IReadOnlyList<AuthenticationComparisonRow> comparison) =>
        string.Join(", ", comparison.Where(r => IsActionable(r.Status)).Select(r => r.Setting));
}

/// <summary>
/// Whether BirkNext currently has authenticated TESTING access. Deliberately a different question
/// from whether the target application requires sign-in, and a different question from whether the
/// saved authentication configuration was manually verified. All three can disagree, correctly.
/// </summary>
public enum AuthenticatedTestingState
{
    /// <summary>An authenticated context exists and can be used now.</summary>
    Ready,
    /// <summary>Setup has started and the context will appear once authenticated traffic is seen.</summary>
    WaitingForTraffic,
    /// <summary>Some authenticated access exists, but not all of what the method can provide.</summary>
    Partial,
    /// <summary>No authenticated context exists.</summary>
    NotConnected,
    /// <summary>The saved method provides no automated authenticated access at all.</summary>
    ManualOnly,
}

/// <summary>
/// The ONE user-facing vocabulary for authenticated testing. The underlying model carries finer
/// distinctions — no credential, an expired one, no observed REST call, no observed GraphQL query —
/// and those stay in the capability details. Showing four near-synonyms for "no authenticated
/// context" at the same prominence is what this type exists to prevent.
/// </summary>
public static class AuthenticatedTestingStates
{
    public static string Label(AuthenticatedTestingState state) => state switch
    {
        AuthenticatedTestingState.Ready => "Ready",
        AuthenticatedTestingState.WaitingForTraffic => "Waiting for authenticated traffic",
        AuthenticatedTestingState.Partial => "Partial",
        AuthenticatedTestingState.ManualOnly => "Manual only",
        _ => "Not connected",
    };

    public static string Tone(AuthenticatedTestingState state) => state switch
    {
        AuthenticatedTestingState.Ready => "ready",
        AuthenticatedTestingState.ManualOnly or AuthenticatedTestingState.NotConnected => "muted",
        _ => "needs-action",
    };

    /// <summary>Why this matters, in one sentence. It is about testing access, never about the target's own sign-in.</summary>
    public const string Purpose = "Authenticated testing lets BirkNext inspect protected API traffic and authenticated application behaviour. It is separate from whether the target itself requires sign-in.";


    // ── Edge proxy guidance ───────────────────────────────────────────────────
    //
    // BirkNext never reads or writes Edge's proxy settings, so the browser's configuration cannot be queried.
    // What CAN be established is the opposite direction: if a request has reached the local proxy, Edge was
    // routed through it. That is evidence, not inference from the server being up — a listening proxy server
    // says nothing at all about whether any browser points at it.

    /// <summary>
    /// True only when traffic has actually arrived at the proxy, which proves the browser was configured to use
    /// it. Absence proves nothing, so the negative case stays an instruction rather than a claim.
    /// </summary>
    public static bool BrowserRoutedThroughProxy(LocalHttpsProxyStatus proxy) =>
        proxy.AuthenticatedRequestsObserved > 0
        || proxy.AuthenticatedCredentialAvailable
        || proxy.ObservedNetworkEndpoints.Count > 0;

    public static string ProxyGuidanceTitle(LocalHttpsProxyStatus proxy) =>
        BrowserRoutedThroughProxy(proxy) ? "Edge proxy configured" : "Enable proxy in Edge settings";

    public static string ProxyGuidanceText(LocalHttpsProxyStatus proxy) =>
        BrowserRoutedThroughProxy(proxy)
            ? "Authenticated API traffic can be collected through the BirkNext proxy."
            : "Use the BirkNext local HTTPS proxy in managed Edge to collect authenticated API traffic.";

    /// <summary>
    /// The browser-side setup line inside the details. Deliberately manual: nothing in BirkNext changes Edge's
    /// settings, so promising automatic configuration or restoration would be untrue.
    /// </summary>
    public static string EdgeBrowserSetup(LocalHttpsProxyStatus proxy) =>
        BrowserRoutedThroughProxy(proxy)
            ? "Traffic has been observed through the proxy, so managed Edge is routed through it."
            : "Configure managed Edge to use the BirkNext local HTTPS proxy endpoint shown below.";

    public const string EndpointHandoff =
        "Detailed REST, GraphQL and WebSocket observations are available in Endpoint Discovery.";
}
