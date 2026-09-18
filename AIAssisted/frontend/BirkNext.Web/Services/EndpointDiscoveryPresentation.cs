using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure projection of the Endpoint Discovery tab's state into its user-facing wording.
///
/// This surface reports EVIDENCE. Four words are used deliberately and never interchanged:
///
/// <list type="bullet">
/// <item><b>Observed</b> — seen in runtime traffic.</item>
/// <item><b>Correlated</b> — evidence associated across network, page and service data. Association, not ownership.</item>
/// <item><b>Configured</b> — saved under Target Environment → Integrations. Never evidence of anything running.</item>
/// <item><b>Detected</b> — belongs to the target-detection workflow, and does not appear here at all.</item>
/// </list>
///
/// Nothing here interprets the evidence: no pass, fail, compatibility or drift verdict belongs on this page.
/// </summary>
public static class EndpointDiscoveryPresentation
{
    /// <summary>Where configured integrations are owned and edited. This page only links there.</summary>
    public const string IntegrationsHref = "/admin/system-settings?section=target-environments&tab=integrations";

    public const string Introduction = "Observed network communication for this Target Environment.";

    public const string IntroductionDetail =
        "REST, GraphQL, authentication and background traffic are shown from observed evidence, correlated to the application pages that produced it.";

    /// <summary>
    /// Live capture state. One vocabulary: a session is Active or Stopped, and "live" is a qualifier on the
    /// label rather than a second status system running alongside it.
    /// </summary>
    public static string SessionLabel(LocalHttpsProxyStatus proxy) => IsActive(proxy) ? "Active" : "Stopped";

    public static bool IsActive(LocalHttpsProxyStatus proxy) =>
        proxy.State is LocalHttpsProxyState.Listening or LocalHttpsProxyState.WaitingForAuthenticatedTraffic
            or LocalHttpsProxyState.AuthenticatedTrafficDetected or LocalHttpsProxyState.Ready;

    /// <summary>
    /// Whether traffic can currently be attributed to pages and services. Technical correlation only: it says
    /// nothing about whether an integration relationship is real, owned or verified.
    /// </summary>
    public static string CorrelationLabel(LocalHttpsProxyStatus proxy) => IsActive(proxy) ? "Available" : "Unavailable";

    public const string CorrelationHint = "Traffic can be attributed to the page and service that produced it. This is evidence correlation, not verified integration ownership.";

    /// <summary>Whether authenticated traffic can currently be captured. Reported here, owned by Authentication.</summary>
    public static string AuthenticatedContextLabel(LocalHttpsProxyStatus proxy) =>
        proxy.AuthenticatedCredentialAvailable ? "Available" : "Not available";

    /// <summary>
    /// Observed backend communication: distinct hosts seen in traffic. Counted from observations only, so a
    /// configured integration nothing has been seen talking to never appears here.
    /// </summary>
    public static int ObservedHostCount(IReadOnlyList<ObservedNetworkEndpoint> endpoints) =>
        endpoints.Select(e => e.Host).Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// The observed auth mechanism on a row. A bearer token being present is an observation about a request;
    /// it is not a statement that the application requires sign-in — that is Authentication's to say.
    /// </summary>
    public static string ObservedAuthLabel(bool authObserved) => authObserved ? "Bearer" : "—";

    public const string ConfiguredBackendIntroduction =
        "Integrations saved for this Target Environment that the network proxy cannot observe. These are configured values, not observed traffic.";

    public const string ConfiguredBackendEmpty =
        "No backend integrations are configured for this Target Environment.";

    /// <summary>Stated on the observed table so its absence of these rows cannot read as their absence entirely.</summary>
    public const string ConfiguredBackendPointer =
        "Message-based integrations the proxy cannot observe are listed under Configured backend integrations; they are configuration, not observed traffic.";
}
