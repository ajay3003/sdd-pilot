using System.Text.Json.Serialization;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.ApiReview;

/// <summary>
/// Contracts of the API Quality Review shared by the Blazor frontend and the backend engine. The review is API-centric: it answers
/// whether an API is correct, safe, robust, contractually sound and performant. Targets come from Endpoint Discovery (observed traffic),
/// saved configuration or a published contract — never from guessed paths. Nothing here carries a credential, a header value that could
/// hold one, a request/response body or GraphQL variables: only structural evidence (JSON paths and types, status codes, sizes, timings).
/// The one document carried is an observed GraphQL operation in Endpoint Discovery's normalized form — every literal value redacted, no
/// variables — so client/server compatibility can be checked without the traffic that produced it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewTargetType { Rest, GraphQl }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewTargetSource { DiscoveredTraffic, Configured, Contract }

/// <summary>How a target is (or cannot be) reached by the automated review. Resolved before any request; a missing context fails fast.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewAccessMode
{
    PublicHttp,
    /// <summary>Approved read-only request executed by the backend gateway with the memory-only credential; the engine never sees it.</summary>
    AuthenticatedHttp,
    /// <summary>Authentication required, automated method selected, but its runtime context is missing or expired.</summary>
    Unavailable,
    /// <summary>The environment's method is manual verification only.</summary>
    ManualOnly,
    /// <summary>Execution is not permitted (policy, scope, scheme).</summary>
    Blocked,
}

/// <summary>Result of one check. "Not tested" and "Blocked" are never a pass.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewCheckResult { Pass, Fail, Warning, ManualReview, NotApplicable, NotTested, Blocked }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewSeverity { Critical, High, Medium, Low, Info }

/// <summary>Type column of the results table.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewFindingType { Rest, GraphQl, Contract, Security, Errors, Performance, Documentation, Drift, AccessControl }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewDriftClassification { Breaking, PotentiallyBreaking, NonBreaking, Informational }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewTargetStatus { Completed, PartiallyCompleted, Blocked, NotTested }

/// <summary>One operation of a target: REST method+path or a GraphQL operation (type + client operation name + document variant).</summary>
public sealed record ApiReviewOperation
{
    /// <summary>GraphQL: the observed document, literal values redacted (see Endpoint Discovery). Null when it was not captured.</summary>
    public string? Document { get; init; }
    /// <summary>GraphQL: identity of the document variant. Same name, different selection → different hash.</summary>
    public string? DocumentHash { get; init; }
    /// <summary>GraphQL: why no document is available although the operation was observed (persisted-query hash only, over the retention limit).</summary>
    public GraphQlDocumentOmission DocumentOmission { get; init; }
    /// <summary>
    /// GraphQL: the largest response size Endpoint Discovery observed for this operation in real frontend traffic — declared Content-Length
    /// of 2xx responses that were NOT content-encoded (so the size is the payload, not a compressed transfer). Null when no such sample exists.
    /// </summary>
    public long? ObservedResponseBytes { get; init; }
    /// <summary>How many uncompressed, sized 2xx samples <see cref="ObservedResponseBytes"/> was taken from.</summary>
    public int ObservedResponseSamples { get; init; }
    public DateTimeOffset? FirstObservedAt { get; init; }
    public DateTimeOffset? LastObservedAt { get; init; }
    /// <summary>Only in retained history (an earlier analysis generation), not in the current evidence.</summary>
    public bool Historical { get; init; }
    public string Method { get; init; } = "GET";
    /// <summary>REST path (query stripped). For GraphQL the endpoint path.</summary>
    public string Path { get; init; } = "/";
    public GraphQlOperationType OperationType { get; init; } = GraphQlOperationType.None;
    public string? OperationName { get; init; }
    public int ObservedCount { get; init; }
    public bool AuthObserved { get; init; }
    public int LastStatus { get; init; }
    public ApiReviewTargetSource Source { get; init; } = ApiReviewTargetSource.DiscoveredTraffic;
    public ObservedEndpointConfidence Confidence { get; init; } = ObservedEndpointConfidence.Candidate;
    [JsonIgnore] public string Display => OperationType != GraphQlOperationType.None
        ? $"{OperationType} {OperationName ?? "(anonymous)"}" : $"{Method} {Path}";
    /// <summary>Safe (GET/HEAD/OPTIONS or GraphQL query) → may be executed by the automated review.</summary>
    [JsonIgnore] public bool IsSafe => OperationType != GraphQlOperationType.None
        ? OperationType == GraphQlOperationType.Query
        : Method is "GET" or "HEAD" or "OPTIONS";
}

/// <summary>Normalized API review target (one REST service or one GraphQL endpoint). No credentials.</summary>
public sealed record ApiReviewTarget
{
    /// <summary>Stable safe id: type + origin + base path digest; never contains a query string or credential.</summary>
    public string TargetId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public ApiReviewTargetType ApiType { get; init; }
    public string Scheme { get; init; } = "https";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 443;
    /// <summary>REST: common base path of the grouped operations. GraphQL: the endpoint path (learned, never assumed).</summary>
    public string BasePath { get; init; } = "/";
    public string ServiceName { get; init; } = "";
    public ApiReviewTargetSource Source { get; init; }
    /// <summary>Bearer observed on traffic, or configured authentication required.</summary>
    public bool AuthRequired { get; init; }
    /// <summary>OpenAPI document URL (REST) when known from configuration.</summary>
    public string? ContractSource { get; init; }
    public DateTimeOffset? DiscoveredAt { get; init; }
    public ObservedEndpointConfidence Confidence { get; init; } = ObservedEndpointConfidence.Candidate;
    public List<ApiReviewOperation> Operations { get; init; } = [];
    /// <summary>Default selection: verified/configured targets are selected; low-confidence candidates are not.</summary>
    public bool Selected { get; init; }
    [JsonIgnore] public string Origin => Port is 443 or 80 ? $"{Scheme}://{Host}" : $"{Scheme}://{Host}:{Port}";
    [JsonIgnore] public string Url => $"{Origin}{BasePath}";
}

/// <summary>Immutable description of the active Target Environment captured at Run. No credential; the context identity is a digest.</summary>
public sealed record ApiReviewEnvironmentSnapshot
{
    public string EnvironmentId { get; init; } = "";
    public string Name { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public AuthenticatedTestingMethod AuthenticatedTestingMethod { get; init; } = AuthenticatedTestingMethod.ManagedEdgeCdp;
    public bool RequiresAuthentication { get; init; }
    /// <summary>Digest of the proxy context identity (never configuration values).</summary>
    public string? ContextIdentityDigest { get; init; }
    public bool IsProduction { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
}

/// <summary>
/// Review policy captured at Run and stored in the report, so a result is always shown against the thresholds it was evaluated
/// with — never against whatever the Target Environment says today. Read-only always; production additionally disables active
/// error-handling probes.
///
/// Threshold ownership (Target Environment → Performance Thresholds): per-request response time uses <b>Single Request Latency</b>
/// (the generic per-request API threshold, also used for FQR's own gateway requests); REST payload uses <b>REST Payload</b>; GraphQL
/// payload uses <b>GraphQL Payload</b>. "API Response Warning / Poor" belongs to BirkNext Performance Quality (proxy-observed traffic)
/// and is not read here. Sizes: 1 KB = 1024 bytes, as the settings store them.
/// </summary>
public sealed record ApiReviewPolicy
{
    public bool ReadOnly { get; init; } = true;
    /// <summary>Unknown-route / invalid-query probes (safe GET/OPTIONS/GraphQL query). Off in production by default.</summary>
    public bool ErrorHandlingProbes { get; init; } = true;
    /// <summary>Response time above this is a Warning.</summary>
    public int SlowWarningMs { get; init; } = 500;
    /// <summary>Response time above this is Poor (reported as Fail). Null: the policy has one threshold and no poor tier —
    /// Single Request Latency is one number, so none is invented. Reports recorded before this had 1000.</summary>
    public int? SlowPoorMs { get; init; }
    /// <summary>Which setting the latency thresholds came from, for display.</summary>
    public string? LatencySource { get; init; }
    /// <summary>The Target Environment's threshold profile when the review ran. <see cref="ApiReviewPerformanceProfile.NotRecorded"/> for reports
    /// recorded before profiles were captured — never inferred from the values.</summary>
    public ApiReviewPerformanceProfile PerformanceProfile { get; init; }
    /// <summary>Average API Latency: the mean of this target's real review request timings above this is a Warning. Aggregate only — each
    /// request is still evaluated against <see cref="SlowWarningMs"/>. Null in reports recorded before it was owned (not assessed).</summary>
    public int? AverageLatencyWarningMs { get; init; }
    /// <summary>Compression Minimum Payload: an uncompressed response smaller than this is not expected to be compressed. Null in older
    /// reports (they evaluated every advertised response regardless of size).</summary>
    public long? CompressionMinimumBytes { get; init; }
    /// <summary>Legacy single payload threshold (REST and GraphQL shared the larger of the two). Read only when the per-type values are absent.</summary>
    public long LargePayloadBytes { get; init; } = 1024 * 1024;
    /// <summary>REST response size above this is a Warning (REST Payload setting).</summary>
    public long? RestPayloadWarningBytes { get; init; }
    /// <summary>GraphQL response size above this is a Warning (GraphQL Payload setting). No GraphQL payload check runs today: the review's
    /// only GraphQL request is the safe __typename probe, whose size says nothing; recorded so the policy snapshot is complete.</summary>
    public long? GraphQlPayloadWarningBytes { get; init; }

    public ApiReviewCheckResult LatencyResult(double elapsedMs) =>
        SlowPoorMs is { } poor && elapsedMs > poor ? ApiReviewCheckResult.Fail
        : elapsedMs > SlowWarningMs ? ApiReviewCheckResult.Warning
        : ApiReviewCheckResult.Pass;

    /// <summary>Mean ≤ threshold = Pass, above = Warning. One threshold, so no poor tier.</summary>
    public ApiReviewCheckResult AverageLatencyResult(double meanMs) =>
        AverageLatencyWarningMs is { } limit && meanMs > limit ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass;

    /// <summary>Response size ≤ GraphQL Payload = Pass, above = Warning.</summary>
    public ApiReviewCheckResult GraphQlPayloadResult(long bytes) =>
        GraphQlPayloadWarningBytes is { } limit && bytes > limit ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass;

    /// <summary>Text formats worth compressing. Images, archives and other already-compressed binaries are not compression candidates.</summary>
    public static bool IsCompressible(string? mediaType) => mediaType is { Length: > 0 } m
        && (m.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || m.Contains("json", StringComparison.OrdinalIgnoreCase)
            || m.Contains("xml", StringComparison.OrdinalIgnoreCase) || m.Contains("javascript", StringComparison.OrdinalIgnoreCase) || m.Contains("graphql", StringComparison.OrdinalIgnoreCase));

    /// <summary>"warning > 1500 ms" or "warning > 500 ms · poor > 1000 ms" — the exact values <see cref="LatencyResult"/> used.</summary>
    [JsonIgnore] public string LatencyPolicyText => SlowPoorMs is { } poor ? $"warning > {SlowWarningMs} ms · poor > {poor} ms" : $"warning > {SlowWarningMs} ms";

    [JsonIgnore] public long RestPayloadThreshold => RestPayloadWarningBytes ?? LargePayloadBytes;

    public ApiReviewCheckResult RestPayloadResult(long? bytes) =>
        RestPayloadThreshold <= 0 || bytes is null ? ApiReviewCheckResult.Pass : bytes > RestPayloadThreshold ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass;

    /// <summary>"512,000 bytes (500 KB)". KB = 1024 bytes, as Performance Thresholds stores them.</summary>
    public static string Bytes(long bytes) =>
        bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? $"{bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes ({bytes / (1024 * 1024)} MB)"
        : bytes % 1024 == 0 ? $"{bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes ({bytes / 1024} KB)"
        : $"{bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes";
    /// <summary>Introspection enabled is a policy question: warn only for production-like environments.</summary>
    public bool IntrospectionExpectedDisabled { get; init; }
}

/// <summary>Target Environment → Performance Thresholds profile captured with a review. NotRecorded = the report predates profile capture.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiReviewPerformanceProfile { NotRecorded, Default, Strict, Custom }

/// <summary>Structural JSON shape entry: path (JSONPath-like, arrays as [*]) and observed type. Never a value.</summary>
public sealed record JsonShapeEntry(string Path, string Type, bool Nullable);

/// <summary>Threshold-independent baseline of a target from a previous run, used to detect contract drift. Structural evidence only.</summary>
public sealed record ApiReviewBaseline
{
    public string TargetId { get; init; } = "";
    public DateTimeOffset RecordedAt { get; init; }
    public string? ContractHash { get; init; }
    public int? ContractOperationCount { get; init; }
    public string? GraphQlSchemaHash { get; init; }
    public List<string> GraphQlRootFields { get; init; } = [];
    public List<string> GraphQlDeprecatedFields { get; init; } = [];
    /// <summary>Per safe operation ("GET /api/children"): observed response shape.</summary>
    public Dictionary<string, List<JsonShapeEntry>> OperationShapes { get; init; } = new();
    public Dictionary<string, int> OperationStatuses { get; init; } = new();
}

public sealed record ApiReviewRunRequest
{
    public ApiReviewEnvironmentSnapshot Environment { get; init; } = new();
    public AuthenticatedReviewIdentity Identity { get; init; } = new(AuthenticatedTestingMethod.ManagedEdgeCdp, null, null);
    public List<ApiReviewTarget> Targets { get; init; } = [];
    public ApiReviewPolicy Policy { get; init; } = new();
    public List<ApiReviewBaseline> Baselines { get; init; } = [];
    /// <summary>Frontend origin used as the Origin header of the CORS preflight probe (configuration value, not a secret).</summary>
    public string? FrontendOrigin { get; init; }
}

public sealed record ApiReviewCheck
{
    public string CheckId { get; init; } = "";
    public ApiReviewFindingType Area { get; init; }
    public string Title { get; init; } = "";
    public ApiReviewCheckResult Result { get; init; }
    public string Detail { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
}

public sealed record ApiReviewOperationResult
{
    public string Display { get; init; } = "";
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public ApiReviewAccessMode AccessMode { get; init; }
    public bool Executed { get; init; }
    public int StatusCode { get; init; }
    public string? ContentType { get; init; }
    public double? ElapsedMs { get; init; }
    public long? ContentLength { get; init; }
    public ApiReviewCheckResult Result { get; init; }
    public string? Note { get; init; }
    public bool? ContractMatched { get; init; }
    /// <summary>GraphQL observed operations: contract compatibility, kept apart from execution (<see cref="Executed"/>/<see cref="Result"/>).</summary>
    public GraphQlCompatibilityStatus? Compatibility { get; init; }
    public GraphQlOperationType OperationType { get; init; }
    public int ObservationCount { get; init; }
    public bool Historical { get; init; }
    public int ShapeEntryCount { get; init; }
    public List<ApiReviewCheck> Checks { get; init; } = [];
}

public sealed record ApiReviewFinding
{
    public string Id { get; init; } = "";
    /// <summary>
    /// The typed rule that produced this finding (e.g. "sec-no-hsts"), without the per-observation suffix <see cref="Id"/>
    /// carries. The same rule on the same endpoint from two services is one logical issue with two source observations.
    /// Empty in reports recorded before this field existed.
    /// </summary>
    public string RuleId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public ApiReviewSeverity Severity { get; init; }
    public ApiReviewFindingType Type { get; init; }
    public string Endpoint { get; init; } = "";
    public string Check { get; init; } = "";
    public string Title { get; init; } = "";
    public ApiReviewCheckResult Result { get; init; } = ApiReviewCheckResult.Fail;
    public string Description { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
    public string Recommendation { get; init; } = "";
    public ApiReviewDriftClassification? Drift { get; init; }
}

public sealed record ApiReviewContractSummary
{
    /// <summary>"OpenAPI" | "GraphQL schema".</summary>
    public string Kind { get; init; } = "OpenAPI";
    public string? Source { get; init; }
    public bool Available { get; init; }
    public ApiReviewCheckResult Status { get; init; } = ApiReviewCheckResult.NotTested;
    public string? Version { get; init; }
    public string? Title { get; init; }
    public int OperationCount { get; init; }
    public int TypeCount { get; init; }
    public int MutationCount { get; init; }
    public int SubscriptionCount { get; init; }
    public int DeprecatedCount { get; init; }
    public bool? IntrospectionEnabled { get; init; }
    public string? Hash { get; init; }
    public string Note { get; init; } = "";
}

public sealed record ApiReviewGraphQlOperationMatch(string Operation, string? MatchedRootField, ApiReviewCheckResult Result, string Note);

/// <summary>Where the schema used for client/server compatibility came from. Observed operations are never a schema source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphQlSchemaSource { None, RuntimeIntrospection, ConfiguredArtifact }

/// <summary>
/// Contract compatibility of one observed operation. Deliberately not Pass/Fail: "could not assess" is NotAssessed, never a failure,
/// and a structurally valid operation is Compatible whatever its runtime outcome (auth, business errors) was.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphQlCompatibilityStatus { Compatible, Incompatible, NotAssessed }

/// <summary>One structural violation. Codes are stable: FIELD_NOT_FOUND, ARGUMENT_NOT_FOUND, REQUIRED_ARGUMENT_MISSING, TYPE_MISMATCH,
/// UNKNOWN_TYPE, INVALID_FRAGMENT_TYPE, UNKNOWN_FRAGMENT, SELECTION_NOT_ALLOWED, SELECTION_SET_REQUIRED, ENUM_VALUE_INVALID,
/// VARIABLE_NOT_DEFINED, INPUT_FIELD_NOT_FOUND, REQUIRED_INPUT_FIELD_MISSING, OPERATION_TYPE_NOT_SUPPORTED.</summary>
public sealed record GraphQlValidationIssue(string Code, string Message, string? Path = null);

public sealed record GraphQlOperationCompatibilityResult
{
    /// <summary>Stable identity: endpoint + operation type + name + document hash.</summary>
    public string OperationId { get; init; } = "";
    public string? OperationName { get; init; }
    public GraphQlOperationType OperationType { get; init; }
    public string Endpoint { get; init; } = "";
    public string? DocumentHash { get; init; }
    public GraphQlCompatibilityStatus Status { get; init; } = GraphQlCompatibilityStatus.NotAssessed;
    public string? NotAssessedReason { get; init; }
    public List<GraphQlValidationIssue> Issues { get; init; } = [];
    /// <summary>Root fields the document selects (for schema-change impact).</summary>
    public List<string> RootFields { get; init; } = [];
    public int ObservationCount { get; init; }
    public DateTimeOffset? FirstObservedAt { get; init; }
    public DateTimeOffset? LastObservedAt { get; init; }
    public bool Historical { get; init; }
    public string EvidenceSource { get; init; } = "Endpoint Discovery";
    /// <summary>Deprecated schema members the document uses ("`User.oldName` is deprecated — Use displayName."). Informational only: a
    /// deprecated field is still part of the contract, so this never makes an operation incompatible (removed ≠ deprecated).</summary>
    public List<string> DeprecatedUsage { get; init; } = [];
    [JsonIgnore] public string Display => $"{OperationType} {OperationName ?? "(anonymous)"}";
}

/// <summary>
/// GraphQL client/server compatibility of one endpoint: do the operations the frontend actually sends still validate against the
/// best trusted schema? Separate from schema drift ("did the schema change?") and from runtime execution ("did it succeed?").
/// </summary>
public sealed record ApiReviewGraphQlCompatibility
{
    public GraphQlSchemaSource SchemaSource { get; init; }
    public string? SchemaSourceDetail { get; init; }
    public DateTimeOffset? SchemaRetrievedAt { get; init; }
    /// <summary>Why nothing could be assessed (no schema); null when validation ran.</summary>
    public string? NotAssessedReason { get; init; }
    public List<GraphQlOperationCompatibilityResult> Operations { get; init; } = [];
    public double DurationMs { get; init; }
    /// <summary>Schema changes since the baseline that touch root fields observed operations select ("Removed root field `user` — GetUser").</summary>
    public List<string> SchemaChangeImpact { get; init; } = [];
    /// <summary>Outcome of the runtime introspection attempt this run ("Retrieved", "Rejected — HTTP 400", "Not attempted — target not reachable").</summary>
    public string? RuntimeSchemaOutcome { get; init; }
    /// <summary>The configured schema artifact of this target at run time — used, or available as fallback. Null when none was configured.</summary>
    public GraphQlSchemaArtifactSnapshot? ConfiguredArtifact { get; init; }
    /// <summary>Why the configured artifact could not be used (invalid SDL). Null when it parsed or none is configured.</summary>
    public string? ConfiguredArtifactProblem { get; init; }
    [JsonIgnore] public int Observed => Operations.Count;
    [JsonIgnore] public int Assessed => Operations.Count(o => o.Status != GraphQlCompatibilityStatus.NotAssessed);
    [JsonIgnore] public int Compatible => Operations.Count(o => o.Status == GraphQlCompatibilityStatus.Compatible);
    [JsonIgnore] public int Incompatible => Operations.Count(o => o.Status == GraphQlCompatibilityStatus.Incompatible);
    [JsonIgnore] public int NotAssessed => Operations.Count(o => o.Status == GraphQlCompatibilityStatus.NotAssessed);
    [JsonIgnore] public int Current => Operations.Count(o => !o.Historical);
    [JsonIgnore] public int HistoricalOnly => Operations.Count(o => o.Historical);
}

public sealed record ApiReviewTargetResult
{
    public ApiReviewTarget Target { get; init; } = new();
    public ApiReviewAccessMode AccessMode { get; init; }
    public string AccessReason { get; init; } = "";
    public ApiReviewTargetStatus Status { get; init; }
    public string? RequiredAction { get; init; }
    public List<ApiReviewOperationResult> Operations { get; init; } = [];
    public ApiReviewContractSummary? Contract { get; init; }
    public List<ApiReviewCheck> Checks { get; init; } = [];
    public List<ApiReviewGraphQlOperationMatch> GraphQlOperationMatches { get; init; } = [];
    /// <summary>GraphQL targets only: client/server compatibility of the observed operations. Null for REST.</summary>
    public ApiReviewGraphQlCompatibility? GraphQlCompatibility { get; init; }
    /// <summary>Null when the target was blocked/not tested; 0 when completed with no findings.</summary>
    public int? FindingCount { get; init; }
    public ApiReviewBaseline? Baseline { get; init; }
}

public sealed record ApiReviewCoverage
{
    public int RestOperationsTotal { get; init; }
    public int RestOperationsReviewed { get; init; }
    public int GraphQlOperationsObserved { get; init; }
    public int GraphQlOperationsMatched { get; init; }
    public int ContractChecks { get; init; }
    public int SecurityChecks { get; init; }
    public int AuthenticatedPlanned { get; init; }
    public int AuthenticatedExecuted { get; init; }
    public int PublicPlanned { get; init; }
    public int PublicExecuted { get; init; }
    public int TargetsBlocked { get; init; }
    public int TargetsCompleted { get; init; }
    public int UnsafeOperationsNotExecuted { get; init; }
}

public sealed record ApiReviewReport
{
    public ApiReviewEnvironmentSnapshot Environment { get; init; } = new();
    public ApiReviewPolicy Policy { get; init; } = new();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public AuthenticatedReviewCapabilities Access { get; init; } = new();
    public List<ApiReviewTargetResult> Targets { get; init; } = [];
    public List<ApiReviewFinding> Findings { get; init; } = [];
    public ApiReviewCoverage Coverage { get; init; } = new();
    public List<string> ManualReviewItems { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public string? ErrorMessage { get; init; }
    [JsonIgnore] public int RestServices => Targets.Count(t => t.Target.ApiType == ApiReviewTargetType.Rest);
    [JsonIgnore] public int GraphQlServices => Targets.Count(t => t.Target.ApiType == ApiReviewTargetType.GraphQl);
    [JsonIgnore] public bool AnyCompleted => Targets.Any(t => t.Status is ApiReviewTargetStatus.Completed or ApiReviewTargetStatus.PartiallyCompleted);
}
