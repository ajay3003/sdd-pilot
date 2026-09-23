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

    public static string SessionLabel(LocalHttpsProxyStatus proxy) => IsActive(proxy) ? "Active" : "Inactive";
    public static bool IsActive(LocalHttpsProxyStatus proxy) => proxy.ProxyListening && proxy.RuntimeStatus == LocalHttpsProxyRuntimePhase.Running;
    public static string CorrelationLabel(LocalHttpsProxyStatus proxy) => LiveDiscoveryState.From(proxy, DateTimeOffset.UtcNow, false).PageCorrelationState;
    public const string CorrelationHint = "Page correlation is observed request attribution, not verified integration ownership.";
    public static string AuthenticatedContextLabel(LocalHttpsProxyStatus proxy) => LiveDiscoveryState.From(proxy, DateTimeOffset.UtcNow, false).AuthenticatedTrafficState;
    public static int ObservedHostCount(IReadOnlyList<ObservedNetworkEndpoint> endpoints) =>
        endpoints.Where(NetworkEvidencePolicy.IsApplicationTraffic).Select(e => e.Host).Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// The observed auth mechanism on a row. A bearer token being present is an observation about a request;
    /// it is not a statement that the application requires sign-in — that is Authentication's to say.
    /// </summary>
    public static string ObservedAuthLabel(bool authObserved) => authObserved ? "Bearer observed" : "—";

}
