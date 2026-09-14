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
        "DEV-only authenticated API testing. BirkNext temporarily processes approved bearer credentials in memory but never displays, logs or saves them.";

    public const string CdpBlockedProxyHint =
        "Local HTTPS proxy is available as an alternative for authenticated API testing.";

    public static string Connection(ManagedEdgeState state) => state switch
    {
        ManagedEdgeState.TargetTabNotInspectable => "Blocked by enterprise browser protection",
        ManagedEdgeState.TargetTabNotFound => "Target application not found",
        ManagedEdgeState.ConnectedUnproven => "Connected — authenticated access not yet verified",
        ManagedEdgeState.ConnectedAuthenticated => "Authenticated access verified",
        ManagedEdgeState.ProxiedDeliveryUncorrelated => "Proxy delivery detected but target correlation failed",
        ManagedEdgeState.ProxiedDeliveryNotPermitted => "Proxy delivery detected but not allowed by this environment",
        ManagedEdgeState.Connecting => "Connecting",
        ManagedEdgeState.Connected => "Connected — authenticated access not yet verified",
        ManagedEdgeState.Stale => "Stale — re-check target application",
        ManagedEdgeState.Failed => "Connection failed",
        ManagedEdgeState.AmbiguousTargetTabs => "Multiple target tabs — keep exactly one",
        _ => "Not connected"
    };

    public static string Option(AuthenticatedTestingMethod method) => method switch
    {
        AuthenticatedTestingMethod.LocalHttpsProxy => ProxyOption,
        AuthenticatedTestingMethod.ManualOnly => ManualOption,
        _ => CdpOption
    };

    /// <summary>Names of the methods other than the selected one, for the read-only "Other available methods" line.</summary>
    public static string OtherMethods(AuthenticatedTestingMethod selected) =>
        string.Join(", ", Enum.GetValues<AuthenticatedTestingMethod>().Where(m => m != selected).Select(Option));

    /// <summary>The edit-mode option label, appending a recommendation hint on the Local HTTPS proxy choice for its default environments.</summary>
    public static string EditOption(AuthenticatedTestingMethod method) =>
        method == AuthenticatedTestingMethod.LocalHttpsProxy ? $"{ProxyOption} (recommended for DEV/QA/Test/RC)" : Option(method);

    public static string Help(AuthenticatedTestingMethod method) => method switch
    {
        AuthenticatedTestingMethod.LocalHttpsProxy => ProxySecurityWarning,
        AuthenticatedTestingMethod.ManualOnly => "User verifies access manually. Authenticated automation is unavailable.",
        _ => "Uses an authenticated Edge browser context without reading or storing tokens. Preferred when browser policy permits debugger attachment."
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
                var dom = "Unavailable - the Local HTTPS proxy does not enable browser DOM inspection";
                return new(ProxyOption, publicSurface, api, dom,
                    proxy.AuthenticatedRestObserved ? "Authenticated REST endpoint verified from traffic" : "Authenticated REST endpoint not observed yet",
                    proxy.AuthenticatedGraphQlQueryObserved ? "Authenticated GraphQL query endpoint verified from traffic" : "Authenticated GraphQL query endpoint not observed yet");
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
