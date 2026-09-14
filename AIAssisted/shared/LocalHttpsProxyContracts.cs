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

    /// <summary>
    /// Environment types for which a NEWLY created (or reset) profile defaults to <see cref="AuthenticatedTestingMethod.LocalHttpsProxy"/>.
    /// A deliberate subset of <see cref="AllowedEnvironmentTypes"/>: Local is proxy-eligible but not defaulted (local targets are usually
    /// plain-HTTP loopback the proxy cannot intercept), so it keeps the CDP default. This governs only the initial choice for new profiles;
    /// it never migrates existing profiles and never changes the persisted-model default used when deserializing legacy JSON.
    /// </summary>
    public static readonly IReadOnlyList<string> ProxyDefaultEnvironmentTypes = ["Development", "QA", "Test", "RC"];

    public static bool IsAllowed(string? environmentType) =>
        environmentType is not null && AllowedEnvironmentTypes.Contains(environmentType.Trim(), StringComparer.Ordinal);

    /// <summary>
    /// The authenticated testing method a NEW or reset profile of this environment type should start with: Local HTTPS proxy for
    /// non-production DEV/QA/Test/RC environments, and the production-safe <see cref="AuthenticatedTestingMethod.ManagedEdgeCdp"/> default
    /// for every other type (Local, Production, Custom). Never returns a proxy default for an environment where the proxy is disallowed.
    /// </summary>
    public static AuthenticatedTestingMethod DefaultMethodFor(string? environmentType) =>
        environmentType is not null && ProxyDefaultEnvironmentTypes.Contains(environmentType.Trim(), StringComparer.Ordinal)
            ? AuthenticatedTestingMethod.LocalHttpsProxy
            : AuthenticatedTestingMethod.ManagedEdgeCdp;

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

/// <summary>Which API surface an observed authenticated request belongs to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservedEndpointType { Rest, GraphQl }

/// <summary>
/// How strongly the observed traffic proves an authenticated API endpoint. <see cref="Verified"/> requires an authenticated request
/// with an API-compatible (non-HTML, non-static) response; <see cref="Candidate"/> has some but not all signals; <see cref="Rejected"/>
/// is an SPA document, static asset or otherwise not an API. Never inferred from mere credential presence.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservedEndpointConfidence { Rejected, Candidate, Verified }

/// <summary>GraphQL operation kind derived transiently from an observed request body. <see cref="None"/> means "not a GraphQL request".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphQlOperationType { None, Query, Mutation, Subscription }

/// <summary>
/// Safe, non-secret evidence of one authenticated API endpoint that BirkNext actually observed in intercepted browser traffic on an
/// approved host. Traffic-driven, so discovery never assumes <c>/health</c> or <c>/graphql</c>. Deliberately carries no credential:
/// no bearer token, Authorization value, cookie, request body or response body is ever represented here. Runtime-only, never persisted.
/// </summary>
public sealed record ObservedAuthenticatedEndpoint
{
    public ObservedEndpointType EndpointType { get; init; }
    /// <summary>Scheme + host [+ non-default port], e.g. <c>https://api-dev.example.no</c>. Always HTTPS (only TLS-terminated approved hosts are observed).</summary>
    public string Origin { get; init; } = "";
    /// <summary>Request path only, without query string (a query string can carry secrets and is never captured).</summary>
    public string Path { get; init; } = "";
    public string Method { get; init; } = "";
    public int ResponseStatus { get; init; }
    public string? RequestContentType { get; init; }
    public string? ResponseContentType { get; init; }
    /// <summary>An <c>Authorization: Bearer</c> was present on the request. The token value itself is never captured.</summary>
    public bool BearerObserved { get; init; }
    public ObservedEndpointConfidence Confidence { get; init; }
    /// <summary>For GraphQL only, the operation kind parsed transiently from the request body; <see cref="GraphQlOperationType.None"/> for REST.</summary>
    public GraphQlOperationType OperationType { get; init; }
    /// <summary>For GraphQL only, the operation name if the body named one; null for anonymous operations and for REST.</summary>
    public string? OperationName { get; init; }
    /// <summary>How many times an identical endpoint (origin+path+method+type) was observed; repeated requests are collapsed, not flooded.</summary>
    public int Count { get; init; } = 1;
    public DateTimeOffset LastObservedAt { get; init; }
    /// <summary>User-facing endpoint URL (origin + path), never a credential.</summary>
    public string Display => $"{Origin}{Path}";
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

    /// <summary>A credential is available, so an approved read-only REST check <em>can be executed</em>. This is execution capability, not proof that a REST endpoint was observed.</summary>
    public bool RestAvailable => AuthenticatedCredentialAvailable;
    /// <summary>A credential is available, so an approved GraphQL query check <em>can be executed</em>. Not proof that a GraphQL endpoint was observed.</summary>
    public bool GraphQlQueryAvailable => AuthenticatedCredentialAvailable;
    /// <summary>The proxy never enables browser DOM inspection.</summary>
    public bool BrowserDomAvailable => false;

    /// <summary>
    /// Authenticated API endpoints discovered from observed traffic on approved hosts, most-trustworthy and most-recent first.
    /// Runtime-only, no credential. Empty until real authenticated traffic is seen; discovery never assumes <c>/health</c> or <c>/graphql</c>.
    /// </summary>
    public IReadOnlyList<ObservedAuthenticatedEndpoint> ObservedEndpoints { get; init; } = [];

    /// <summary>The best verified authenticated REST endpoint observed in traffic, or null when none was observed. Never the SPA HTML document.</summary>
    public ObservedAuthenticatedEndpoint? VerifiedRestEndpoint =>
        ObservedEndpoints.Where(e => e is { EndpointType: ObservedEndpointType.Rest, Confidence: ObservedEndpointConfidence.Verified }).MaxBy(e => (e.Count, e.LastObservedAt));
    /// <summary>The best verified authenticated GraphQL <em>query</em> endpoint observed in traffic, or null. A mutation/subscription endpoint never counts as a verified query.</summary>
    public ObservedAuthenticatedEndpoint? VerifiedGraphQlQueryEndpoint =>
        ObservedEndpoints.Where(e => e is { EndpointType: ObservedEndpointType.GraphQl, Confidence: ObservedEndpointConfidence.Verified, OperationType: GraphQlOperationType.Query }).MaxBy(e => (e.Count, e.LastObservedAt));
    /// <summary>An authenticated REST endpoint was actually observed and verified from traffic (independent of credential availability).</summary>
    public bool AuthenticatedRestObserved => VerifiedRestEndpoint is not null;
    /// <summary>An authenticated GraphQL query endpoint was actually observed and verified from traffic.</summary>
    public bool AuthenticatedGraphQlQueryObserved => VerifiedGraphQlQueryEndpoint is not null;

    public string Evidence { get; init; } = "Start the local HTTPS proxy, then sign in manually in Microsoft Edge.";
    public string? FailureReason { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

// ── Authenticated review consumption ──────────────────────────────────────────
// Types shared between the backend authenticated-review gateway and the frontend review pages so the two agree on how an
// authenticated API context is consumed by API / Integration / Front-end Quality Reviews. None of these ever carries a credential.

/// <summary>
/// Status of the transient authenticated API context for a review, separate from the proxy listener state (<see cref="LocalHttpsProxyState"/>).
/// The proxy can be Listening while the context is Expired or WaitingForAuthenticatedTraffic. No credential value is ever represented here.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthenticatedApiContextStatus
{
    /// <summary>No authenticated automation configured for the environment (ManualOnly), or the method is not proxy-based.</summary>
    NotApplicable,
    /// <summary>Proxy method selected but no context has been captured yet.</summary>
    WaitingForAuthenticatedTraffic,
    /// <summary>A valid, non-expired in-memory context exists and reviews may execute authenticated API checks.</summary>
    Available,
    /// <summary>A context existed but its credential expired; it has been wiped. The proxy may still be listening for a fresh one.</summary>
    Expired,
    /// <summary>The environment/target configuration changed so any prior context no longer applies.</summary>
    Stale
}

/// <summary>How a single review check was executed. Recorded as safe provenance so reports distinguish public from authenticated results.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewExecutionMode
{
    Public,
    AuthenticatedViaManagedEdgeCdp,
    AuthenticatedViaLocalHttpsProxy,
    /// <summary>An authenticated check that could not run because no authenticated context was available (never silently downgraded to public).</summary>
    AuthenticatedUnavailable,
    /// <summary>No authenticated automation (ManualOnly).</summary>
    ManualNotExecuted
}

/// <summary>
/// Capability matrix a review resolves for the active Target Environment. Each surface is decided independently (never inferred from
/// another). Carries only non-secret status, the observed approved host and expiry; never a token.
/// </summary>
public sealed record AuthenticatedReviewCapabilities
{
    public AuthenticatedTestingMethod Method { get; init; } = AuthenticatedTestingMethod.ManagedEdgeCdp;
    public AuthenticatedApiContextStatus ContextStatus { get; init; } = AuthenticatedApiContextStatus.NotApplicable;
    public bool PublicApi { get; init; } = true;
    public bool AuthenticatedApi { get; init; }
    public bool AuthenticatedRest { get; init; }
    public bool AuthenticatedGraphQlQuery { get; init; }
    public bool AuthenticatedBrowserDom { get; init; }
    public bool AuthenticatedBrowserRuntime { get; init; }
    public string? ObservedHost { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>Non-secret, user-facing explanation of the current authenticated availability (e.g. why DOM checks are unavailable).</summary>
    public string Reason { get; init; } = "";
}

/// <summary>Identity a review passes so the backend can resolve the active environment's authenticated context. Never carries a token.</summary>
public sealed record AuthenticatedReviewIdentity(AuthenticatedTestingMethod Method, string? ProfileId, string? ContextFingerprint);

/// <summary>Typed outcome of an authenticated API execution requested by a review. Distinguishes real HTTP results from auth-unavailable states so a review never silently downgrades.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthenticatedExecutionStatus
{
    Executed,
    NoContext,
    Expired,
    Invalidated,
    OutOfScope,
    MethodNotProxy,
    Rejected
}

/// <summary>Result of a review-issued authenticated request: either an executed <see cref="AuthenticatedApiExecutionResult"/> or a typed reason it did not run. No credential, no headers, no body.</summary>
public sealed record AuthenticatedReviewExecutionOutcome
{
    public AuthenticatedExecutionStatus Status { get; init; }
    public ReviewExecutionMode Mode { get; init; } = ReviewExecutionMode.AuthenticatedUnavailable;
    public AuthenticatedApiExecutionResult? Result { get; init; }
    public string Message { get; init; } = "";
    public bool Executed => Status == AuthenticatedExecutionStatus.Executed && Result is not null;
}
