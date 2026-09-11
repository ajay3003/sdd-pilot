using BirkNext.ManagedEdge;

namespace BirkNext.Web.Models;

/// <summary>
/// User-facing wording for the saved Browser delivery trust policy and the transient runtime trust decision.
/// Kept in one place so the Authentication configuration, the runtime Browser delivery panel and the Validation tab agree.
/// </summary>
public static class BrowserDeliveryTrustLabels
{
    public const string ExactOriginOption = "Exact origin";
    public const string ApprovedProxyOption = "Exact origin + approved Microsoft Defender for Cloud Apps proxy";

    /// <summary>Full policy wording for configuration views.</summary>
    public static string Policy(ManagedEdgeTrustModel model) => model == ManagedEdgeTrustModel.ApprovedMcasProxyOrigin ? ApprovedProxyOption : ExactOriginOption;

    /// <summary>Short policy wording for runtime status ("Configured trust").</summary>
    public static string PolicyShort(ManagedEdgeTrustModel model) => model == ManagedEdgeTrustModel.ApprovedMcasProxyOrigin ? "Approved MCAS proxy permitted (exact origin still preferred)" : "Exact origin only";

    /// <summary>Helper text under the trust-model control. Never implies that MCAS is required.</summary>
    public static string Help(ManagedEdgeTrustModel model) => model == ManagedEdgeTrustModel.ApprovedMcasProxyOrigin
        ? "Direct delivery at the configured target origin is always preferred. A Microsoft Defender for Cloud Apps proxied delivery may also be accepted when BirkNext can strongly correlate it to this target."
        : "Only an authenticated browser tab at the configured target origin is accepted.";

    /// <summary>Observed delivery for runtime views: what the browser actually delivered, or that nothing was observed.</summary>
    public static string Delivery(ManagedEdgeStatus status) => status.DeliveryOrigin is null ? "Not observed" : status.BrowserDelivery;

    /// <summary>Runtime trust decision wording for the Browser delivery panel.</summary>
    public static string Decision(ManagedEdgeStatus status) => status.State switch
    {
        ManagedEdgeState.Stale => "Not trusted — runtime is stale, reconnect",
        ManagedEdgeState.NotConnected or ManagedEdgeState.NotConfigured or ManagedEdgeState.Connecting => "Not evaluated",
        _ => status.TrustDecision switch
        {
            ManagedEdgeTrustDecision.ExactOriginTrusted => "Exact origin — trusted",
            ManagedEdgeTrustDecision.ApprovedProxyTrusted => "Approved correlated MCAS proxy — trusted",
            ManagedEdgeTrustDecision.ProxyCorrelationFailed => "Proxy correlation failed",
            ManagedEdgeTrustDecision.ProxyNotPermitted => "Not trusted — proxied delivery is not permitted by this environment (exact origin only)",
            ManagedEdgeTrustDecision.TargetNotInspectable => "Target not inspectable",
            ManagedEdgeTrustDecision.NotTrusted => "Not trusted",
            _ => "Not evaluated"
        }
    };

    /// <summary>Correlation outcome for Validation and runtime views.</summary>
    public static string Correlation(ManagedEdgeStatus status) => status.TrustDecision switch
    {
        ManagedEdgeTrustDecision.ApprovedProxyTrusted => "Verified",
        ManagedEdgeTrustDecision.ProxyCorrelationFailed => "Failed",
        ManagedEdgeTrustDecision.ProxyNotPermitted => "Not evaluated — proxied delivery not permitted",
        ManagedEdgeTrustDecision.ExactOriginTrusted => "Not required (direct delivery)",
        _ => "—"
    };

    /// <summary>Validation-tab trust decision: combines the trust decision with whether authenticated access was actually verified.</summary>
    public static string ValidationDecision(ManagedEdgeStatus status) => status.TrustDecision switch
    {
        ManagedEdgeTrustDecision.ExactOriginTrusted when status.AuthenticatedBrowserAvailable => "Verified",
        ManagedEdgeTrustDecision.ApprovedProxyTrusted when status.AuthenticatedBrowserAvailable => "Verified via approved proxy",
        ManagedEdgeTrustDecision.ExactOriginTrusted or ManagedEdgeTrustDecision.ApprovedProxyTrusted when status.State == ManagedEdgeState.Stale => "Not trusted — runtime stale",
        ManagedEdgeTrustDecision.ExactOriginTrusted or ManagedEdgeTrustDecision.ApprovedProxyTrusted => "Trusted tab — authenticated access not yet verified",
        ManagedEdgeTrustDecision.ProxyCorrelationFailed or ManagedEdgeTrustDecision.ProxyNotPermitted or ManagedEdgeTrustDecision.TargetNotInspectable or ManagedEdgeTrustDecision.NotTrusted => "Not trusted",
        _ => "Not connected"
    };

    /// <summary>Configuration validity of the saved trust policy against the profile's Frontend URL.</summary>
    public static string Configuration(FrontendAnalysisProfile profile)
    {
        if (!Uri.TryCreate(profile.TargetUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return "Frontend URL not configured";
        if (profile.Authentication.BrowserDeliveryTrust == ManagedEdgeTrustModel.ApprovedMcasProxyOrigin && uri.Scheme != Uri.UriSchemeHttps)
            return "Warning — approved MCAS proxy trust requires an HTTPS Frontend URL";
        return "Valid";
    }

    public static string? ConfiguredTarget(FrontendAnalysisProfile? profile) =>
        Uri.TryCreate(profile?.TargetUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.GetLeftPart(UriPartial.Authority) : null;
}
