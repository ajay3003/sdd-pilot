using System.Text.Json.Serialization;

namespace BirkNext.LocalHttpsProxy;

/// <summary>
/// Saved Target Environment choice (Authentication section) of HOW authenticated testing is performed for that environment.
/// Persisted as <c>authentication.authenticatedTestingMethod</c>; a missing field deserializes to <see cref="ManagedEdgeCdp"/> so legacy
/// profiles keep today's behaviour. BirkNext never switches the method automatically: a CDP attachment that the browser refuses only
/// reports that the Local HTTPS proxy exists as an alternative; the user must edit and save the environment to use it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthenticatedTestingMethod
{
    /// <summary>Managed Edge browser context over the Chrome DevTools Protocol. Uses the signed-in browser without reading credentials.</summary>
    ManagedEdgeCdp,
    /// <summary>BirkNext-managed loopback HTTPS interception proxy for approved DEV API hosts. Credentials pass through backend memory only.</summary>
    LocalHttpsProxy,
    /// <summary>No authenticated automation; manual verification only.</summary>
    ManualOnly
}

/// <summary>Transient runtime state of the loopback proxy session. Never persisted, never mixed with <c>ManagedEdgeState</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalHttpsProxyState
{
    NotStarted, Starting, WaitingForCertificateTrust, Listening, WaitingForAuthenticatedTraffic,
    AuthenticatedTrafficDetected, Ready, Failed, Stopped, Stale
}

/// <summary>Trust state of the dedicated BirkNext DEV inspection root certificate on this workstation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyCertificateTrustState { NotGenerated, NotTrusted, Trusted, Expired, Unknown }

/// <summary>
/// Environment gate for the Local HTTPS proxy. Interception of authenticated traffic is a DEV/non-production testing method only.
/// Production is never allowed; Custom is not allowed because its intent cannot be established (fail closed).
/// The same list is enforced by the backend and mirrored by the frontend so both agree.
/// </summary>
public static class LocalHttpsProxyEnvironmentPolicy
{
    public static readonly IReadOnlyList<string> AllowedEnvironmentTypes = ["Local", "Development", "QA", "Test", "RC"];

    public static bool IsAllowed(string? environmentType) =>
        environmentType is not null && AllowedEnvironmentTypes.Contains(environmentType.Trim(), StringComparer.Ordinal);

    public const string EnvironmentBlockedReason =
        "Local HTTPS proxy is available only for Local, Development, QA, Test and RC environments. Production (and Custom) environments are never intercepted.";
    public const string RemoteDeploymentReason =
        "Local HTTPS proxy unavailable in this deployment mode: BirkNext.Api is not running as a local workstation runtime, so no browser proxy is started on the backend host.";
}

/// <summary>
/// Identity of the Target Environment a proxy session serves. ApprovedHosts are the explicitly configured hosts of that environment
/// (target origin, REST base, GraphQL endpoint, allowlisted REST/GraphQL hosts); entries are <c>host</c> or <c>host:port</c>.
/// The context fingerprint is a digest of the environment's target-relevant configuration and binds every runtime request.
/// </summary>
public sealed record LocalHttpsProxyScopeRequest(string ProfileId, string ContextFingerprint, string EnvironmentType, string TargetUrl,
    IReadOnlyList<string> ApprovedHosts, string? ExpectedTenant = null);

public sealed record LocalHttpsProxySessionRequest(string SessionId, string ProfileId, string ContextFingerprint);

/// <summary>Certificate trust changes are explicit user actions; <see cref="Confirmed"/> must be true.</summary>
public sealed record LocalHttpsProxyCertificateRequest(bool Confirmed);

public sealed record LocalHttpsProxyEdgeLaunchRequest(string SessionId, string ProfileId, string ContextFingerprint);

public sealed record AuthenticatedRestRequest(string SessionId, string ProfileId, string ContextFingerprint, string Method, string Url);
public sealed record AuthenticatedGraphQlRequest(string SessionId, string ProfileId, string ContextFingerprint, string EndpointUrl, string Query);

/// <summary>Sanitized outcome of an approved authenticated request. No headers, no body text, no credential.</summary>
public sealed record AuthenticatedApiExecutionResult
{
    public int StatusCode { get; init; }
    public string? ContentType { get; init; }
    public long ContentLength { get; init; }
    public double ElapsedMs { get; init; }
    /// <summary>HTTP success (2xx). For GraphQL this is transport success only.</summary>
    public bool Succeeded => StatusCode is >= 200 and < 300;
    public bool AuthenticationRejected => StatusCode is 401 or 403;
    public int? GraphQlErrorCount { get; init; }
    public bool? GraphQlHasData { get; init; }
    public string Outcome { get; init; } = "";
}

public sealed record ProxyCertificateStatus
{
    public ProxyCertificateTrustState State { get; init; } = ProxyCertificateTrustState.Unknown;
    public string? Subject { get; init; }
    public string? Thumbprint { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
    /// <summary>True when BirkNext can add/remove the root in the current user's trust store after explicit confirmation.</summary>
    public bool InstallSupported { get; init; }
    public string Guidance { get; init; } = "";
}

/// <summary>Runtime evidence only. Never contains a credential; never persisted with environment profiles.</summary>
public sealed record LocalHttpsProxyStatus
{
    public string? SessionId { get; init; }
    public LocalHttpsProxyState State { get; init; } = LocalHttpsProxyState.NotStarted;
    public int Port { get; init; }
    public string Endpoint => Port > 0 ? $"127.0.0.1:{Port}" : "Not started";
    public bool LocalIntegrationAvailable { get; init; }
    public bool EnvironmentAllowed { get; init; }
    public string? EnvironmentType { get; init; }
    public bool PortAvailable { get; init; }
    public ProxyCertificateStatus Certificate { get; init; } = new();
    public IReadOnlyList<string> ApprovedHosts { get; init; } = [];
    public string? TargetOrigin { get; init; }
    public bool CanStart { get; init; }

    public int InterceptedRequests { get; init; }
    public int PassThroughConnections { get; init; }
    public int TlsHandshakeFailures { get; init; }
    public int AuthenticatedRequestsObserved { get; init; }
    public string? LastInterceptedHost { get; init; }

    /// <summary>An in-memory Bearer credential bound to this environment is available for approved API requests. The credential itself is never returned.</summary>
    public bool AuthenticatedCredentialAvailable { get; init; }
    public bool CredentialExpired { get; init; }
    public string? CredentialObservedHost { get; init; }
    public DateTimeOffset? CredentialObservedAt { get; init; }
    public DateTimeOffset? CredentialExpiresAt { get; init; }
    /// <summary>"JWT" or "Opaque"; no claim values.</summary>
    public string? CredentialFormat { get; init; }

    public bool RestAvailable => AuthenticatedCredentialAvailable;
    public bool GraphQlQueryAvailable => AuthenticatedCredentialAvailable;
    /// <summary>The proxy never enables browser DOM inspection.</summary>
    public bool BrowserDomAvailable => false;

    public string Evidence { get; init; } = "Start the local HTTPS proxy, then sign in manually in Microsoft Edge.";
    public string? FailureReason { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}
