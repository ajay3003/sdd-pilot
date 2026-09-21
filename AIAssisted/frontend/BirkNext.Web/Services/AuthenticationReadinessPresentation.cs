using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The readiness of proxy-assisted authenticated testing. Never "authentication verified".</summary>
public enum AuthenticationReadiness
{
    /// <summary>Every prerequisite the configured method needs is in place. Says nothing about whether sign-in has been verified.</summary>
    Ready,
    /// <summary>Something concrete is missing and the user can fix it.</summary>
    ActionRequired,
    /// <summary>A prerequisite's state is unknown, so readiness cannot be claimed either way.</summary>
    Limited,
    /// <summary>No authentication is configured for this environment.</summary>
    NotConfigured,
    /// <summary>Backend state has not arrived yet. Rendering anything else here would be a guess.</summary>
    Checking,
}

/// <summary>How one authentication prerequisite stands. Deliberately not a boolean: Unknown is not Missing.</summary>
public enum AuthPrerequisiteState { Ok, NeedsAttention, Unknown, Failed, NotRequired, Checking }

/// <summary>
/// One prerequisite row: what it is, where it stands, one sentence, and at most one action.
/// </summary>
public sealed record AuthenticationPrerequisite(
    string Id,
    string Title,
    string StatusLabel,
    AuthPrerequisiteState State,
    string Explanation,
    string? ActionLabel = null,
    string? ActionId = null,
    IReadOnlyList<(string Label, string Value)>? Facts = null)
{
    public bool Blocks => State is AuthPrerequisiteState.NeedsAttention or AuthPrerequisiteState.Failed;
    public string Tone => State switch
    {
        AuthPrerequisiteState.Ok => "ready",
        AuthPrerequisiteState.Failed => "error",
        AuthPrerequisiteState.NeedsAttention => "needs-action",
        _ => "muted",
    };
    /// <summary>A short glyph name the page maps to its existing icon set. Text always carries the meaning as well.</summary>
    public string Icon => Id switch
    {
        "certificate" => State == AuthPrerequisiteState.Ok ? "shield-check" : "shield-warning",
        "proxy" => State == AuthPrerequisiteState.Ok ? "network-check" : "network-warning",
        _ => State == AuthPrerequisiteState.Ok ? "browser-check" : "browser-warning",
    };
}

public sealed record AuthenticationReadinessSummary(
    AuthenticationReadiness State,
    string Label,
    string Detail,
    IReadOnlyList<AuthenticationPrerequisite> Prerequisites)
{
    public int Attention => Prerequisites.Count(p => p.Blocks);
    public bool CanVerify => State == AuthenticationReadiness.Ready;
    public string Tone => State switch
    {
        AuthenticationReadiness.Ready => "ready",
        AuthenticationReadiness.ActionRequired => "needs-action",
        AuthenticationReadiness.Limited => "muted",
        _ => "muted",
    };
}

/// <summary>
/// The one derivation of authentication readiness, and the one place that decides what each prerequisite says.
///
/// The distinctions it exists to hold: a certificate being trusted is not the proxy running, the proxy running is not
/// the browser running, the browser running is not the browser using this proxy, and none of it is authentication
/// having been verified. Collapsing any pair of those produces a page that says Ready when a request would fail.
///
/// Pure over its inputs — no clock, no services — so the rules can be tested as rules.
/// </summary>
public static class AuthenticationReadinessPresentation
{
    public const string CertificateId = "certificate";
    public const string ProxyId = "proxy";
    public const string BrowserId = "browser";

    public static AuthenticationReadinessSummary Summarize(
        bool authenticationConfigured,
        AuthenticatedTestingMethod method,
        LocalHttpsProxyStatus? proxy,
        ProxyCertificateStatus? certificate,
        bool loaded)
    {
        // Nothing is claimed before the backend has answered. A page that renders "Not running" during its own first
        // fetch teaches the reader to distrust it.
        if (!loaded)
            return new(AuthenticationReadiness.Checking, "Checking…", "Reading certificate, proxy and browser state.",
            [
                Checking(CertificateId, "HTTPS inspection certificate"),
                Checking(ProxyId, "Local HTTPS Proxy"),
                Checking(BrowserId, "Dedicated Edge browser"),
            ]);

        if (!authenticationConfigured)
            return new(AuthenticationReadiness.NotConfigured, "Not configured",
                "No authentication is configured for this Target Environment.", []);

        // Only the proxy method has proxy prerequisites. Showing three blocked cards for a method that never needed
        // them would be inventing work.
        if (method != AuthenticatedTestingMethod.LocalHttpsProxy)
            return new(AuthenticationReadiness.Limited, "Manual only",
                "The saved authenticated testing method provides no proxy-assisted access.", []);

        var prerequisites = new List<AuthenticationPrerequisite>
        {
            Certificate(certificate),
            Proxy(proxy),
            Browser(proxy),
        };

        var state =
            prerequisites.Any(p => p.State == AuthPrerequisiteState.Failed) ? AuthenticationReadiness.ActionRequired
            : prerequisites.Any(p => p.State == AuthPrerequisiteState.NeedsAttention) ? AuthenticationReadiness.ActionRequired
            // Unknown is not failure. It is a reason not to promise Ready, which is a different thing to say.
            : prerequisites.Any(p => p.State == AuthPrerequisiteState.Unknown) ? AuthenticationReadiness.Limited
            : AuthenticationReadiness.Ready;

        var attention = prerequisites.Count(p => p.Blocks);
        return new(state, Label(state), state switch
        {
            AuthenticationReadiness.Ready => "Authentication configuration and proxy prerequisites are available.",
            AuthenticationReadiness.ActionRequired => $"{attention} prerequisite{(attention == 1 ? "" : "s")} need{(attention == 1 ? "s" : "")} attention.",
            _ => "One or more prerequisites could not be checked.",
        }, prerequisites);
    }

    private static string Label(AuthenticationReadiness state) => state switch
    {
        AuthenticationReadiness.Ready => "Ready",
        AuthenticationReadiness.ActionRequired => "Action required",
        AuthenticationReadiness.Limited => "Limited",
        AuthenticationReadiness.Checking => "Checking…",
        _ => "Not configured",
    };

    private static AuthenticationPrerequisite Checking(string id, string title) =>
        new(id, title, "Checking…", AuthPrerequisiteState.Checking, "");

    private static AuthenticationPrerequisite Certificate(ProxyCertificateStatus? certificate) => certificate?.State switch
    {
        ProxyCertificateTrustState.Trusted => new(CertificateId, "HTTPS inspection certificate", "Trusted", AuthPrerequisiteState.Ok,
            "The local proxy certificate is installed and trusted.", "Recheck", "certificate-recheck",
            Facts(("Installed", "Yes"), ("Expires", certificate.NotAfter?.ToLocalTime().ToString("d MMM yyyy") ?? "—"))),
        ProxyCertificateTrustState.NotTrusted => new(CertificateId, "HTTPS inspection certificate", "Needs attention", AuthPrerequisiteState.NeedsAttention,
            "The certificate is installed but not trusted, so HTTPS interception will fail.",
            certificate.InstallSupported ? "Install certificate" : "View setup instructions", "certificate-install",
            Facts(("Installed", "Yes"), ("Trust", "Not trusted"))),
        ProxyCertificateTrustState.NotGenerated => new(CertificateId, "HTTPS inspection certificate", "Not installed", AuthPrerequisiteState.NeedsAttention,
            "No local proxy certificate has been created yet.",
            certificate.InstallSupported ? "Install certificate" : "View setup instructions", "certificate-install"),
        ProxyCertificateTrustState.Expired => new(CertificateId, "HTTPS inspection certificate", "Expired", AuthPrerequisiteState.NeedsAttention,
            "The local proxy certificate has expired and must be replaced.",
            certificate.InstallSupported ? "Install certificate" : "View setup instructions", "certificate-install",
            Facts(("Expired", certificate.NotAfter?.ToLocalTime().ToString("d MMM yyyy") ?? "—"))),
        // Unknown means we could not look. Reporting it as missing would send the user to fix something that may be fine.
        _ => new(CertificateId, "HTTPS inspection certificate", "Unknown", AuthPrerequisiteState.Unknown,
            "BirkNext could not determine the certificate's trust state.", "Recheck", "certificate-recheck"),
    };

    private static AuthenticationPrerequisite Proxy(LocalHttpsProxyStatus? proxy)
    {
        if (proxy is null)
            return new(ProxyId, "Local HTTPS Proxy", "Unknown", AuthPrerequisiteState.Unknown, "The proxy runtime could not be read.", "Recheck", "proxy-recheck");
        if (proxy.RuntimeStatus == LocalHttpsProxyRuntimePhase.Failed || proxy.State == LocalHttpsProxyState.Failed)
            return new(ProxyId, "Local HTTPS Proxy", "Failed", AuthPrerequisiteState.Failed,
                string.IsNullOrWhiteSpace(proxy.FailureReason) ? "The proxy stopped unexpectedly." : proxy.FailureReason!, "Retry", "proxy-start");
        if (proxy.RuntimeStatus == LocalHttpsProxyRuntimePhase.Starting)
            return new(ProxyId, "Local HTTPS Proxy", "Starting…", AuthPrerequisiteState.Checking, "The proxy is starting.");
        if (!AuthenticatedTestingStates.ProxyServerRunning(proxy))
            // Stopped is an ordinary resting state, not an error: nothing is broken, it simply has not been started.
            return new(ProxyId, "Local HTTPS Proxy", "Not running", AuthPrerequisiteState.NeedsAttention,
                "Authenticated HTTPS traffic cannot be captured until the proxy is started.", "Start proxy", "proxy-start");

        return new(ProxyId, "Local HTTPS Proxy", "Running", AuthPrerequisiteState.Ok,
            "The proxy is listening and ready to capture authenticated traffic.", "Stop proxy", "proxy-stop",
            Facts(("Port", proxy.Port > 0 ? proxy.Port.ToString() : "—"), ("Started", proxy.StartedAt?.ToLocalTime().ToString("HH:mm") ?? "—")));
    }

    private static AuthenticationPrerequisite Browser(LocalHttpsProxyStatus? proxy)
    {
        if (proxy is null)
            return new(BrowserId, "Dedicated Edge browser", "Unknown", AuthPrerequisiteState.Unknown, "The browser state could not be read.");
        // A browser cannot be pointed at a proxy that is not there, so this is not the user's next problem to solve.
        if (!AuthenticatedTestingStates.ProxyServerRunning(proxy))
            return new(BrowserId, "Dedicated Edge browser", "Waiting for the proxy", AuthPrerequisiteState.NotRequired,
                "Start the local proxy first; the browser is opened with its port.");

        var endpoint = proxy.ExpectedProxyPort is { } expected ? $"127.0.0.1:{expected}" : "—";
        var profile = string.IsNullOrWhiteSpace(proxy.EdgeProfileDirectory) ? "LocalHttpsProxyEdgeProfile" : System.IO.Path.GetFileName(proxy.EdgeProfileDirectory!);

        return proxy.EdgeVerification switch
        {
            DedicatedBrowserVerification.Confirmed => new(BrowserId, "Dedicated Edge browser", "Proxy active", AuthPrerequisiteState.Ok,
                "The dedicated browser is running and using this proxy.", "Open browser", "edge-open",
                Facts(("Browser", "Running"), ("Proxy", "Active"), ("Proxy endpoint", endpoint), ("Profile", profile))),

            DedicatedBrowserVerification.Mismatch => new(BrowserId, "Dedicated Edge browser", "Proxy not active", AuthPrerequisiteState.NeedsAttention,
                "The browser is running, but it was started with a different proxy than the one running now.",
                "Restart browser with proxy", "edge-restart",
                Facts(("Browser", "Running"), ("Expected proxy", endpoint),
                      ("Started with", proxy.EdgeProxyPort is { } was ? $"127.0.0.1:{was}" : "Unknown"), ("Profile", profile))),

            DedicatedBrowserVerification.NotConfirmed => new(BrowserId, "Dedicated Edge browser", "Proxy not confirmed", AuthPrerequisiteState.NeedsAttention,
                "The browser is running, but BirkNext cannot confirm that it is using the current local proxy.",
                "Restart browser with proxy", "edge-restart",
                Facts(("Browser", "Running"), ("Expected proxy", endpoint), ("Profile", profile))),

            DedicatedBrowserVerification.NotRunning => new(BrowserId, "Dedicated Edge browser", "Not running", AuthPrerequisiteState.NeedsAttention,
                "The proxy is running, but the dedicated Edge session is not open.", "Open browser", "edge-open",
                Facts(("Expected proxy", endpoint), ("Profile", profile))),

            _ => new(BrowserId, "Dedicated Edge browser", "Unknown", AuthPrerequisiteState.Unknown,
                "BirkNext cannot determine the dedicated browser's state."),
        };
    }

    private static IReadOnlyList<(string, string)> Facts(params (string, string)[] facts) => facts;

    /// <summary>
    /// Why the Verify action is unavailable, so a disabled button is never unexplained. Empty when it is available.
    /// </summary>
    public static string VerificationBlockedReason(AuthenticationReadinessSummary summary) => summary.State switch
    {
        AuthenticationReadiness.Ready => "",
        AuthenticationReadiness.NotConfigured => "Configure authentication for this environment first.",
        AuthenticationReadiness.Checking => "Checking prerequisites…",
        _ => "Requires " + string.Join(", ", summary.Prerequisites.Where(p => p.State != AuthPrerequisiteState.Ok)
            .Select(p => p.Title.ToLowerInvariant())) + ".",
    };
}

/// <summary>
/// The Endpoint Discovery hand-off. Endpoint Discovery holds observed traffic from more than one source, so it is
/// never disabled here — an incomplete proxy setup limits what will arrive next, and says so, rather than locking a
/// surface that already has history to show.
/// </summary>
public static class EndpointDiscoveryCta
{
    public const string Title = "Endpoint Discovery";
    public const string Description = "Observe REST, GraphQL, authentication and other backend communication.";

    public static (string Status, string Tone, string? Note) Status(AuthenticationReadinessSummary summary, int knownEndpoints) => summary.State switch
    {
        AuthenticationReadiness.Ready => ("Ready", "ready", null),
        AuthenticationReadiness.Checking => ("Checking…", "muted", null),
        _ when knownEndpoints > 0 => ("Authentication traffic setup incomplete", "needs-action",
            "Previously discovered endpoints are still available."),
        _ => ("Authentication traffic setup incomplete", "needs-action", null),
    };
}
