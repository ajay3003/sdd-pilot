using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;

namespace BirkNext.Web.Models;

/// <summary>Coverage of one authenticated surface, kept explicit so Security, REST and GraphQL never over-claim what a method enables.</summary>
public sealed record AuthenticatedCapabilitySummary(string Method, string PublicSurface, string AuthenticatedApiSurface, string AuthenticatedBrowserDomSurface, string Rest, string GraphQlQuery);

/// <summary>
/// User-facing wording for the saved Authenticated testing method, the transient Local HTTPS proxy state and the resulting
/// capability matrix. Kept in one place so the Authentication configuration, the proxy panel and the Validation tab agree.
/// </summary>
public static class AuthenticatedTestingMethodLabels
{
    public const string CdpOption = "Managed Edge browser context (CDP)";
    public const string ProxyOption = "Local HTTPS proxy";
    public const string ManualOption = "Manual verification only";

    public const string ProxySecurityWarning =
        "This DEV-only mode intercepts approved HTTPS traffic locally to enable authenticated API testing. Authentication credentials may pass through BirkNext memory but are never displayed, logged or saved.";

    public const string CdpBlockedProxyHint =
        "CDP blocked by enterprise browser protection. Local HTTPS proxy is available as an alternative authenticated API testing method: edit this environment, choose Local HTTPS proxy under Authenticated testing method, and save. BirkNext never switches the method automatically.";

    public static string Option(AuthenticatedTestingMethod method) => method switch
    {
        AuthenticatedTestingMethod.LocalHttpsProxy => ProxyOption,
        AuthenticatedTestingMethod.ManualOnly => ManualOption,
        _ => CdpOption
    };

    public static string Help(AuthenticatedTestingMethod method) => method switch
    {
        AuthenticatedTestingMethod.LocalHttpsProxy => "Captures approved DEV API traffic through a BirkNext-managed localhost HTTPS proxy. Authentication material is held only in memory and is never displayed or saved.",
        AuthenticatedTestingMethod.ManualOnly => "No authenticated automation. Public reviews remain available; authenticated REST, GraphQL and browser checks are unavailable.",
        _ => "Uses the authenticated browser context without reading authentication credentials. Preferred when enterprise browser policy allows debugger attachment."
    };

    public static string ProxyState(LocalHttpsProxyState state) => state switch
    {
        LocalHttpsProxyState.NotStarted => "Not started",
        LocalHttpsProxyState.Starting => "Starting",
        LocalHttpsProxyState.WaitingForCertificateTrust => "Listening - certificate trust required",
        LocalHttpsProxyState.Listening => "Listening",
        LocalHttpsProxyState.WaitingForAuthenticatedTraffic => "Listening - waiting for authenticated traffic",
        LocalHttpsProxyState.AuthenticatedTrafficDetected => "Authenticated traffic detected",
        LocalHttpsProxyState.Ready => "Ready",
        LocalHttpsProxyState.Failed => "Failed",
        LocalHttpsProxyState.Stopped => "Stopped",
        LocalHttpsProxyState.Stale => "Stale - environment changed",
        _ => state.ToString()
    };

    public static string Certificate(ProxyCertificateTrustState state) => state switch
    {
        ProxyCertificateTrustState.Trusted => "Trusted",
        ProxyCertificateTrustState.NotTrusted => "Not trusted",
        ProxyCertificateTrustState.NotGenerated => "Not generated yet",
        ProxyCertificateTrustState.Expired => "Expired",
        _ => "Unknown"
    };

    /// <summary>"Detected" only after BirkNext observed authenticated API traffic on an approved host; never implies the credential was accepted.</summary>
    public static string AuthenticatedTraffic(LocalHttpsProxyStatus status) =>
        status.AuthenticatedRequestsObserved > 0 || status.AuthenticatedCredentialAvailable ? "Detected" : status.SessionId is null ? "—" : "Not detected yet";

    public static string Credential(LocalHttpsProxyStatus status) =>
        status.AuthenticatedCredentialAvailable ? "Available - memory only"
        : status.CredentialExpired ? "Expired - perform an authenticated action in the browser again"
        : "Not available";

    /// <summary>Capability matrix for the selected method and the current runtime observations. The proxy never enables DOM inspection.</summary>
    public static AuthenticatedCapabilitySummary Capabilities(AuthenticatedTestingMethod method, ManagedEdgeStatus edge, LocalHttpsProxyStatus proxy)
    {
        const string publicSurface = "Available";
        switch (method)
        {
            case AuthenticatedTestingMethod.LocalHttpsProxy:
            {
                var api = proxy.AuthenticatedCredentialAvailable ? "Available via Local HTTPS Proxy" : "Unavailable - no authenticated API context yet (start the proxy and sign in)";
                var dom = edge.State == ManagedEdgeState.TargetTabNotInspectable
                    ? "Unavailable - CDP blocked by enterprise browser protection"
                    : "Unavailable - the Local HTTPS proxy does not enable browser DOM inspection";
                return new(ProxyOption, publicSurface, api, dom,
                    proxy.RestAvailable ? "Authenticated GET/HEAD/OPTIONS available" : "Authenticated REST unavailable",
                    proxy.GraphQlQueryAvailable ? "Authenticated query available (mutations blocked)" : "Authenticated GraphQL query unavailable");
            }
            case AuthenticatedTestingMethod.ManualOnly:
                return new(ManualOption, publicSurface, "Unavailable - manual verification only", "Unavailable - manual verification only", "Authenticated REST unavailable", "Authenticated GraphQL unavailable");
            default:
            {
                var dom = edge.SecurityBrowserAvailable ? "Available via Managed Edge (approved browser-context checks)"
                    : edge.State == ManagedEdgeState.TargetTabNotInspectable ? "Unavailable - CDP blocked by enterprise browser protection"
                    : "Unavailable - connect and verify the managed Edge tab";
                return new(CdpOption, publicSurface,
                    edge.RestAvailable ? "Available via Managed Edge same-origin checks" : "Unavailable - no verified authenticated browser context",
                    dom,
                    edge.RestAvailable ? "Authenticated same-origin GET available" : "Authenticated REST unavailable",
                    edge.GraphQlAvailable ? "Authenticated query available" : "Authenticated GraphQL query unavailable (semantic success not proven)");
            }
        }
    }
}
