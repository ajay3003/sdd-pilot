using System.Text.RegularExpressions;
using BirkNext.Api.Services.AuthenticatedReview;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>
/// How long the diagnostic watches the target after the navigation call returns, and why.
///
/// None of these durations is a success condition. Success is always a positive observation — a probe that worked,
/// with no termination signal in between. The durations only bound how long the diagnostic keeps looking, so a slow
/// target cannot hold it open and a late failure is not missed by looking too early.
/// </summary>
/// <param name="QuietPeriod">
/// How long main-frame navigation must stay quiet before the location is read. Single-page applications that use
/// MSAL (M2LB is Blazor WebAssembly) redirect to their identity provider from script, AFTER the framework has booted —
/// not in the HTTP response the navigation call waits for. Reading the URL at commit would read the app shell and
/// miss the redirect entirely, which is how a redirect to Entra used to be reported as "Target application PASS".
/// </param>
/// <param name="SettleBound">
/// The most the diagnostic waits for that quiet period. Hitting it is recorded ("did not settle"), not treated as a
/// pass or a failure: a page that keeps navigating is still observed at the end.
/// </param>
/// <param name="StabilityWindow">
/// How long the page and context must stay alive between the first and second probe. The spike's failure was a page
/// closed underneath the automation shortly after it loaded, before any sign-in; this window is sized to span the
/// period after load in which in-browser protection and session control act. It ends early the moment a close, crash
/// or disconnect is reported, and passing it still requires a second successful probe. A close after the window
/// cannot be excluded, and the report says so.
/// </param>
public sealed record BrowserAutomationTargetObservationTiming(TimeSpan QuietPeriod, TimeSpan SettleBound, TimeSpan StabilityWindow)
{
    public static readonly BrowserAutomationTargetObservationTiming Default =
        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
}

/// <summary>
/// Where a URL is, relative to the target — and what of it may be written down. Pure, so every rule is testable.
/// </summary>
public static class BrowserAutomationTargetLocationPolicy
{
    /// <summary>
    /// Microsoft Entra sign-in hosts. Recognised without configuration because they are the platform's, not the
    /// tenant's; a configured Target Environment authority is recognised in addition.
    /// </summary>
    public static readonly IReadOnlyList<string> EntraSignInHosts =
        ["login.microsoftonline.com", "login.microsoft.com", "login.windows.net"];

    /// <summary>Defender for Cloud Apps session-control proxy suffixes.</summary>
    public static readonly IReadOnlyList<string> SessionControlHostSuffixes = [".mcas.ms", ".mcas-gov.us", ".mcas-gov.ms"];

    /// <summary>scheme://host[:port], with the port only when it is not the scheme's default. Null for anything that is not http(s).</summary>
    public static string? CanonicalOrigin(Uri uri) =>
        uri.Scheme is "http" or "https"
            ? $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}"
            : null;

    public static string? CanonicalOrigin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? CanonicalOrigin(uri) : null;

    /// <summary>
    /// Same origin by scheme, host and EFFECTIVE port — never a string prefix. <c>https://host</c> and
    /// <c>https://host:443/admin</c> are the same origin; <c>https://host.evil.test</c> is not.
    /// </summary>
    public static bool IsExpectedOrigin(Uri candidate, Uri target) =>
        candidate.Scheme is "http" or "https" && AuthenticationOriginPolicy.SameOrigin(candidate, target);

    public static bool IsAuthenticationAuthority(Uri candidate, string? configuredAuthority)
    {
        if (candidate.Scheme != Uri.UriSchemeHttps) return false;
        if (EntraSignInHosts.Any(h => candidate.IdnHost.Equals(h, StringComparison.OrdinalIgnoreCase))) return true;
        return Uri.TryCreate(configuredAuthority, UriKind.Absolute, out var authority)
               && authority.Scheme == Uri.UriSchemeHttps
               && AuthenticationOriginPolicy.SameOrigin(candidate, authority);
    }

    public static bool IsSessionControlProxy(Uri candidate) =>
        candidate.Scheme == Uri.UriSchemeHttps &&
        SessionControlHostSuffixes.Any(s => candidate.IdnHost.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Classifies one observed URL. The target origin wins over everything: it is the answer the run is looking for.</summary>
    public static BrowserAutomationFinalLocation Classify(string? url, Uri target, string? configuredAuthority)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return BrowserAutomationFinalLocation.Unknown;
        if (IsExpectedOrigin(uri, target)) return BrowserAutomationFinalLocation.TargetOrigin;
        if (IsAuthenticationAuthority(uri, configuredAuthority)) return BrowserAutomationFinalLocation.AuthenticationAuthority;
        if (IsSessionControlProxy(uri)) return BrowserAutomationFinalLocation.SessionControlProxy;
        return BrowserAutomationFinalLocation.OtherOrigin;
    }

    private static readonly Regex SafeSegment = new("^[A-Za-z0-9._~-]{1,40}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A URL that is safe to show, log and paste into a ticket.
    ///
    /// The whole query and fragment are replaced, never filtered parameter by parameter: an authorize URL carries
    /// <c>state</c>, <c>nonce</c>, <c>code_challenge</c> and <c>login_hint</c>, a callback carries <c>code</c>,
    /// <c>id_token</c> and <c>session_state</c>, a SAML post carries <c>SAMLResponse</c> and <c>RelayState</c> — and an
    /// allow-list of "harmless" names is one new parameter away from leaking one of them. Path segments are kept only
    /// when they are short plain tokens (<c>oauth2</c>, <c>v2.0</c>, <c>authorize</c>); identifiers become <c>[id]</c>
    /// (<c>[tenant]</c> on an authority) and anything else — e-mail addresses, encoded blobs — becomes <c>[redacted]</c>.
    /// User info is dropped entirely.
    /// </summary>
    public static string Sanitize(string? raw, bool authority = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return raw.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ? "about:blank" : "about:[redacted]";
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || CanonicalOrigin(uri) is not { } origin)
            return "[non-web URL]";

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select((segment, index) =>
                Guid.TryParse(segment, out _) ? (authority && index == 0 ? "[tenant]" : "[id]")
                : SafeSegment.IsMatch(segment) && !LooksLikeAnIdentifier(segment) ? segment
                : "[redacted]");
        var path = "/" + string.Join('/', segments);
        return origin + path
            + (uri.Query.Length > 1 ? "?[redacted]" : "")
            + (uri.Fragment.Length > 1 ? "#[redacted]" : "");
    }

    /// <summary>Maps a DeveloperToolsAvailability DWORD. Values outside the documented set are Unknown.</summary>
    public static EdgeDeveloperToolsPolicyStatus DeveloperToolsPolicy(int value) => value switch
    {
        0 => EdgeDeveloperToolsPolicyStatus.AllowedExceptForceInstalledExtensions,
        1 => EdgeDeveloperToolsPolicyStatus.Allowed,
        2 => EdgeDeveloperToolsPolicyStatus.Disallowed,
        _ => EdgeDeveloperToolsPolicyStatus.Unknown,
    };

    /// <summary>Long digit runs and long mixed alphanumerics are ids or tokens, not route names.</summary>
    private static bool LooksLikeAnIdentifier(string segment) =>
        segment.Count(char.IsDigit) >= 6 ||
        (segment.Length >= 20 && segment.Any(char.IsDigit) && segment.Any(char.IsLetter));
}
