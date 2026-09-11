using System.Text.Json.Serialization;

namespace BirkNext.ManagedEdge;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManagedEdgeState
{
    NotConfigured, NotConnected, Connecting, Connected, TargetTabNotFound,
    AmbiguousTargetTabs, ConnectedUnproven, ConnectedAuthenticated, Failed, Stale,
    /// <summary>The target tab is open, but the browser refused debugger attachment to it (for example a Defender for Cloud Apps protected session).</summary>
    TargetTabNotInspectable
}

/// <summary>Edge RemoteDebuggingAllowed policy. NotConfigured is not Blocked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EdgeRemoteDebuggingPolicyStatus { Unknown, NotConfigured, Allowed, Blocked }

// Runtime evidence only. Never add these objects to persisted environment profiles.
public sealed record ManagedEdgeConnectRequest(string ProfileId, string TargetUrl, string ContextFingerprint);
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
    public string? TargetOrigin { get; init; }
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
