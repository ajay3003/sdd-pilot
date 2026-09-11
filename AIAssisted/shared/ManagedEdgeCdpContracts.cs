using System.Text.Json.Serialization;

namespace BirkNext.ManagedEdge;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManagedEdgeState
{
    NotConfigured, NotConnected, Connecting, Connected, TargetTabNotFound,
    AmbiguousTargetTabs, ConnectedUnproven, ConnectedAuthenticated, Failed, Stale,
    /// <summary>The target tab is open, but the browser refused debugger attachment to it (for example a Defender for Cloud Apps protected session).</summary>
    TargetTabNotInspectable,
    /// <summary>A target-correlated MCAS proxy tab exists, but the required correlation signals for approved proxied delivery were not all present.</summary>
    ProxiedDeliveryUncorrelated,
    /// <summary>Browser delivery is proxied by Defender for Cloud Apps, but the saved environment permits exact-origin delivery only (<see cref="ManagedEdgeTrustModel.ExactOrigin"/>).</summary>
    ProxiedDeliveryNotPermitted
}

/// <summary>Edge RemoteDebuggingAllowed policy. NotConfigured is not Blocked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EdgeRemoteDebuggingPolicyStatus { Unknown, NotConfigured, Allowed, Blocked }

/// <summary>
/// Saved Target Environment policy ("Browser delivery trust") for binding the browser tab to the configured target.
/// Persisted with the environment's authentication configuration; a missing field deserializes to <see cref="ExactOrigin"/>.
/// <list type="bullet">
/// <item><see cref="ExactOrigin"/> (default): only a tab whose origin equals the configured target origin is accepted.</item>
/// <item><see cref="ApprovedMcasProxyOrigin"/>: "exact origin OR approved correlated MCAS proxy". The exact target origin is ALWAYS
/// accepted and preferred; additionally a Microsoft Defender for Cloud Apps reverse-proxy delivery of the same application may be
/// accepted when every correlation signal ties it to the configured target. It never requires MCAS and is never "any *.access.mcas.ms".</item>
/// </list>
/// Observed delivery (direct or proxied) is runtime evidence and is never persisted.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManagedEdgeTrustModel { ExactOrigin, ApprovedMcasProxyOrigin }

/// <summary>Runtime outcome of applying the saved <see cref="ManagedEdgeTrustModel"/> to the tabs actually observed. Transient.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManagedEdgeTrustDecision
{
    NotEvaluated,
    /// <summary>A tab at the exact configured target origin was selected (always preferred, under either trust model).</summary>
    ExactOriginTrusted,
    /// <summary>No inspectable exact-origin tab; a target-correlated Defender for Cloud Apps delivery passed every correlation check under ApprovedMcasProxyOrigin.</summary>
    ApprovedProxyTrusted,
    /// <summary>A target-correlated proxy candidate exists but live origin or navigation correlation failed.</summary>
    ProxyCorrelationFailed,
    /// <summary>Delivery is proxied, but the saved trust model is ExactOrigin.</summary>
    ProxyNotPermitted,
    /// <summary>The exact target tab is advertised but the browser refuses debugger attachment and no approved proxy fallback applies.</summary>
    TargetNotInspectable,
    /// <summary>No trusted tab (not found, ambiguous, stale, failed).</summary>
    NotTrusted
}

// Runtime evidence only. Never add these objects to persisted environment profiles.
// TrustModel carries the environment's SAVED Browser delivery trust policy; it is not a per-connection opt-in.
public sealed record ManagedEdgeConnectRequest(string ProfileId, string TargetUrl, string ContextFingerprint, ManagedEdgeTrustModel TrustModel = ManagedEdgeTrustModel.ExactOrigin);
public sealed record ManagedEdgeSessionRequest(string SessionId, string ProfileId, string ContextFingerprint);
public sealed record ManagedEdgeFetchRequest(string SessionId, string ProfileId, string ContextFingerprint, string Path, string? GraphQlQuery = null);
public sealed record ManagedEdgeProbeResult(int StatusCode, string ContentType, double ElapsedMs);
public sealed record ManagedEdgePreflightRequest(string? TargetUrl);
public sealed record ManagedEdgeLaunchRequest(string? TargetUrl);

public sealed record ManagedEdgeStatus
{
    public string? SessionId { get; init; }
    public ManagedEdgeState State { get; init; } = ManagedEdgeState.NotConnected;
    public string Endpoint { get; init; } = "http://127.0.0.1:9222";
    /// <summary>Configured Target Environment origin. Never replaced by a proxy origin.</summary>
    public string? TargetOrigin { get; init; }
    /// <summary>Origin the authenticated browser session is actually delivered from. Equals TargetOrigin for direct delivery.</summary>
    public string? DeliveryOrigin { get; init; }
    /// <summary>The saved trust policy the connect request was evaluated under. Echo of configuration, never changed by what was observed.</summary>
    public ManagedEdgeTrustModel TrustModel { get; init; } = ManagedEdgeTrustModel.ExactOrigin;
    /// <summary>What the policy decided for the tabs actually observed. Transient runtime evidence.</summary>
    public ManagedEdgeTrustDecision TrustDecision { get; init; } = ManagedEdgeTrustDecision.NotEvaluated;
    /// <summary>Non-sensitive summary of the correlation signals that bound a proxied delivery to the target.</summary>
    public string? CorrelationEvidence { get; init; }
    public bool ProxiedDelivery => DeliveryOrigin is not null && TargetOrigin is not null && !string.Equals(DeliveryOrigin, TargetOrigin, StringComparison.OrdinalIgnoreCase);
    public string BrowserDelivery => ProxiedDelivery ? "Microsoft Defender for Cloud Apps proxy" : "Direct";
    public int ContextCount { get; init; }
    public int PageCount { get; init; }
    /// <summary>Target-origin page targets advertised by the browser, whether or not they are inspectable.</summary>
    public int DiscoveredTargetTabs { get; init; }
    public bool OriginMatched { get; init; }
    public string Evidence { get; init; } = "Connect to the manually signed-in Edge tab.";
    public ManagedEdgeProbeResult? Probe { get; init; }
    public bool AuthenticatedBrowserAvailable => State == ManagedEdgeState.ConnectedAuthenticated;
    public bool PublicCoverageAvailable => true;
    public bool SecurityBrowserAvailable => AuthenticatedBrowserAvailable;
    public bool RestAvailable { get; init; }
    public bool GraphQlAvailable { get; init; }
}

/// <summary>Transient Edge compatibility result. Never persisted with environment profiles.</summary>
public sealed record ManagedEdgePreflightResult
{
    /// <summary>False when BirkNext.Api does not run in the tester's own workstation session; local Edge launch/attach is then invalid.</summary>
    public bool LocalBrowserIntegrationAvailable { get; init; }
    public bool EdgeInstalled { get; init; }
    public string? EdgeExecutablePath { get; init; }
    public string? EdgeVersion { get; init; }
    public EdgeRemoteDebuggingPolicyStatus RemoteDebuggingPolicyStatus { get; init; } = EdgeRemoteDebuggingPolicyStatus.Unknown;
    public string CdpEndpoint { get; init; } = "http://127.0.0.1:9222";
    public bool CdpEndpointReachable { get; init; }
    public bool CdpProtocolValid { get; init; }
    /// <summary>Loopback port answers but not with a valid CDP /json/version document.</summary>
    public bool PortInUseByOtherService { get; init; }
    public bool RemoteDebuggingRuntimeActive => CdpEndpointReachable && CdpProtocolValid;
    public string? BrowserProduct { get; init; }
    public string? BrowserVersion { get; init; }
    public string? TargetOrigin { get; init; }
    public bool TargetTabFound { get; init; }
    public int TargetTabCount { get; init; }
    /// <summary>True only after BirkNext itself started a dedicated Edge instance in this launch operation.</summary>
    public bool EdgeStarted { get; init; }
    public bool CanConnect { get; init; }
    public bool CanLaunchTestEdge { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}
