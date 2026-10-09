using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.RuntimeSecurity;

// Bounded runtime security checks that extend API Quality Review (documentation exposure, cross-origin CORS, authorization scenarios,
// opt-in body fuzzing) and Frontend Quality Review (cookie metadata). Expectations live in Target Environment → Security Expectations;
// every rule here is pure and deterministic so the backend, the UI and the export read the same semantics. None of these checks is a
// penetration test, and none of them proves that an application is secure.

public static class RuntimeSecurityWording
{
    public const string NotAPenetrationTest =
        "BirkNext performs bounded non-production security checks. These checks are not a penetration test and do not establish that an application is secure.";
}

// ── Expectations (Target Environment → Security Expectations) ─────────────────────────────────────────────────────────────────────

/// <summary>What the target owner says about API documentation (Swagger UI / OpenAPI document) reachability. Never inferred.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiDocumentationExposureExpectation { NotSpecified, Allowed, ExpectedProtected, ExpectedUnavailable }

public sealed class CorsExpectations
{
    /// <summary>Origins that may call the API from a browser (scheme://host[:port]). Empty = not specified.</summary>
    [JsonPropertyName("allowedOrigins")] public List<string> AllowedOrigins { get; set; } = [];
    /// <summary>Whether credentialed cross-origin requests are expected. Null = not specified.</summary>
    [JsonPropertyName("allowCredentials")] public bool? AllowCredentials { get; set; }
    [JsonPropertyName("allowedMethods")] public List<string> AllowedMethods { get; set; } = [];
    [JsonPropertyName("allowedHeaders")] public List<string> AllowedHeaders { get; set; } = [];
}

/// <summary>
/// Cookie rules apply strictly only to cookies the owner declares as authentication/session cookies (by exact name). Other cookies are
/// observations: their purpose cannot be known, so a missing HttpOnly on them is never an automatic defect.
/// </summary>
public sealed class CookieSecurityExpectations
{
    [JsonPropertyName("authCookieNames")] public List<string> AuthCookieNames { get; set; } = [];
    [JsonPropertyName("requireSecure")] public bool RequireSecure { get; set; } = true;
    [JsonPropertyName("requireHttpOnly")] public bool RequireHttpOnly { get; set; } = true;
    /// <summary>Allowed SameSite values for auth cookies (Strict/Lax/None). Empty = not specified.</summary>
    [JsonPropertyName("allowedSameSite")] public List<string> AllowedSameSite { get; set; } = [];
    /// <summary>Allowed Domain attributes for auth cookies (host-only cookies always pass). Empty = any explicit Domain is reported.</summary>
    [JsonPropertyName("allowedDomains")] public List<string> AllowedDomains { get; set; } = [];
    [JsonPropertyName("allowPersistentAuthCookies")] public bool AllowPersistentAuthCookies { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthorizationExpectation { Allow, Deny }

/// <summary>One identity of a scenario. Built-in aliases: <c>anonymous</c> (no credential) and <c>proxy-session</c> (the memory-only
/// credential of the Local HTTPS proxy session). Other aliases need a server-side identity provider; without one they are reported as
/// ExecutionUnavailable — passwords and tokens are never stored by BirkNext.</summary>
public sealed class AuthorizationScenarioIdentity
{
    [JsonPropertyName("alias")] public string Alias { get; set; } = AuthorizationIdentityAliases.Anonymous;
    /// <summary>Role label shown in results (e.g. "Caseworker"). A label, never a credential.</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("expected")] public AuthorizationExpectation Expected { get; set; } = AuthorizationExpectation.Deny;
    /// <summary>HTTP 404 counts as denial only when the owner says the API answers unauthorized access with anti-disclosure 404.</summary>
    [JsonPropertyName("notFoundMeansDeny")] public bool NotFoundMeansDeny { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthorizationScenarioApiType { Rest, GraphQl }

/// <summary>An explicit allow/deny expectation for one safe request. Count differences between identities are never used as evidence.</summary>
public sealed class AuthorizationScenario
{
    [JsonPropertyName("scenarioId")] public string ScenarioId { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("apiType")] public AuthorizationScenarioApiType ApiType { get; set; }
    /// <summary>API origin + path (REST) or the GraphQL endpoint URL. Must belong to a reviewed target and to the trusted target registry.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    /// <summary>REST method: GET or HEAD only.</summary>
    [JsonPropertyName("method")] public string Method { get; set; } = "GET";
    /// <summary>GraphQL: one query document without variables. Mutations are rejected.</summary>
    [JsonPropertyName("graphQlQuery")] public string? GraphQlQuery { get; set; }
    /// <summary>Status codes that count as allowed (default: any 2xx).</summary>
    [JsonPropertyName("successStatusCodes")] public List<int> SuccessStatusCodes { get; set; } = [];
    [JsonPropertyName("identities")] public List<AuthorizationScenarioIdentity> Identities { get; set; } = [];
}

/// <summary>Opt-in states for request-body fuzzing per operation. Unknown operations are always blocked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BodyFuzzingPolicy
{
    Disabled,
    /// <summary>The owner states the operation does not change state for invalid input (e.g. a POST search or validation endpoint).</summary>
    ReadOnlyBodySafe,
    /// <summary>Allowed only with a registered server-side cleanup strategy; without one the operation is blocked.</summary>
    StateChangingWithCleanup,
    NotAllowed,
}

public sealed class BodyFuzzOperationOptIn
{
    /// <summary>POST, PUT or PATCH. DELETE is never fuzzed.</summary>
    [JsonPropertyName("method")] public string Method { get; set; } = "POST";
    /// <summary>The OpenAPI path template of the operation ("/api/search").</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("policy")] public BodyFuzzingPolicy Policy { get; set; } = BodyFuzzingPolicy.Disabled;
    [JsonPropertyName("cleanupStrategyId")] public string? CleanupStrategyId { get; set; }

    [JsonIgnore] public string Key => $"{Method.Trim().ToUpperInvariant()} {Path.Trim()}";
}

/// <summary>Runtime security expectations of one Target Environment. Copied into each run so history never re-binds to newer settings.</summary>
public sealed class RuntimeSecurityExpectations
{
    [JsonPropertyName("apiDocumentationExposure")] public ApiDocumentationExposureExpectation ApiDocumentationExposure { get; set; }
    /// <summary>Explicit documentation paths or same-origin URLs (e.g. "/swagger/v1/swagger.json"). Optional; a small default list is used otherwise.</summary>
    [JsonPropertyName("apiDocumentationPaths")] public List<string> ApiDocumentationPaths { get; set; } = [];
    [JsonPropertyName("cors")] public CorsExpectations Cors { get; set; } = new();
    [JsonPropertyName("cookies")] public CookieSecurityExpectations Cookies { get; set; } = new();
    [JsonPropertyName("authorizationScenarios")] public List<AuthorizationScenario> AuthorizationScenarios { get; set; } = [];
    [JsonPropertyName("bodyFuzzOperations")] public List<BodyFuzzOperationOptIn> BodyFuzzOperations { get; set; } = [];

    private static readonly JsonSerializerOptions CopyOptions = new();
    public RuntimeSecurityExpectations Copy() => JsonSerializer.Deserialize<RuntimeSecurityExpectations>(JsonSerializer.Serialize(this, CopyOptions), CopyOptions)!;

    /// <summary>Stable digest of the expectations a run used (history binding; contains no secret because none is stored here).</summary>
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, CopyOptions))), 0, 8).ToLowerInvariant();
}

/// <summary>
/// The part of the runtime security expectations an API Quality Review run reads (documentation exposure and CORS), copied into the
/// run's policy so a stored report is always shown against the expectations it was evaluated with.
/// </summary>
public sealed record ApiRuntimeSecurityPolicy
{
    public ApiDocumentationExposureExpectation ApiDocumentationExposure { get; init; }
    public List<string> ApiDocumentationPaths { get; init; } = [];
    public CorsExpectations Cors { get; init; } = new();

    public static ApiRuntimeSecurityPolicy From(RuntimeSecurityExpectations? expectations)
    {
        var copy = (expectations ?? new RuntimeSecurityExpectations()).Copy();
        return new() { ApiDocumentationExposure = copy.ApiDocumentationExposure, ApiDocumentationPaths = copy.ApiDocumentationPaths, Cors = copy.Cors };
    }
}

/// <summary>Which reviews read each runtime security expectation — shown next to every field so no setting exists without a consumer.</summary>
public static class RuntimeSecuritySettingsUsage
{
    public const string ApiDocumentationExposure = "API Quality Review → Runtime security: API documentation exposure";
    public const string Cors = "API Quality Review → Runtime security: CORS (allowed-origin and foreign-origin preflights)";
    public const string Cookies = "Frontend Quality Review → Static Security: cookie security (frontend document Set-Cookie and Local HTTPS proxy observed cookies)";
    public const string AuthorizationScenarios = "API Quality Review → Authorization scenarios (trusted non-production targets only)";
    public const string BodyFuzzOperations = "API Quality Review → Safe fuzzing: request-body cases (trusted non-production targets only)";
}

// ── Trusted target registry (backend authority for the checks that cross the read-only boundary) ─────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrustedTargetState { Trusted, NotRegistered, ProductionBlocked, TypeNotPermitted, OriginNotRegistered }

/// <summary>
/// Server-side resolution of a Target Environment for authorization scenarios and body fuzzing. The client's environment type, production
/// flag and URLs are never the decision: the profile must be registered by the server (<c>SecurityTesting:TrustedTargets</c>) with a
/// Local/Development/QA/Test classification, and every destination origin must be one of its registered API origins.
/// </summary>
public sealed record TrustedTargetDecision
{
    public TrustedTargetState State { get; init; } = TrustedTargetState.NotRegistered;
    public string ProfileId { get; init; } = "";
    public string? ServerEnvironmentType { get; init; }
    public List<string> RegisteredOrigins { get; init; } = [];
    public string Reason { get; init; } = "";
    [JsonIgnore] public bool Allowed => State == TrustedTargetState.Trusted;
}

// ── API documentation exposure ─────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiDocumentationProbeState { ReachablePublic, ReachableProtected, NotFound, Redirected, NotVerified }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiDocumentationAssessment { NoExpectation, AsExpected, UnexpectedExposure, UnexpectedAbsence, NotVerified }

public sealed record ApiDocumentationProbe
{
    public string Path { get; init; } = "";
    /// <summary>Configured (Security Expectations), Contract source (the target's OpenAPI URL) or Default candidate.</summary>
    public string Source { get; init; } = "";
    public int? StatusCode { get; init; }
    public ApiDocumentationProbeState State { get; init; } = ApiDocumentationProbeState.NotVerified;
    /// <summary>Swagger UI page, OpenAPI document or null when the 2xx body is not documentation (e.g. an SPA fallback).</summary>
    public string? DocumentKind { get; init; }
    /// <summary>Result of the existing OpenAPI parser on a retrieved document ("Valid OpenAPI 3.x (12 operations)", "Malformed: …"). Exposure ≠ contract quality.</summary>
    public string? DocumentValidation { get; init; }
    public string Note { get; init; } = "";
}

public sealed record ApiDocumentationExposureResult
{
    public string TargetId { get; init; } = "";
    public string Origin { get; init; } = "";
    public List<ApiDocumentationProbe> Probes { get; init; } = [];
    public ApiDocumentationProbeState Observed { get; init; } = ApiDocumentationProbeState.NotVerified;
    public ApiDocumentationExposureExpectation Expectation { get; init; }
    public ApiDocumentationAssessment Assessment { get; init; } = ApiDocumentationAssessment.NotVerified;
    public string Reason { get; init; } = "";
}

/// <summary>Deterministic rules for documentation probing: a small candidate list, per-response classification and expectation assessment.</summary>
public static partial class ApiDocumentationExposureRules
{
    public const int MaxProbes = 6;
    public static readonly string[] DefaultCandidates = ["/swagger", "/swagger/index.html", "/swagger/v1/swagger.json", "/openapi.json", "/api-docs"];

    /// <summary>Same-origin candidate paths in priority order: configured paths, the target's contract URL, then defaults. Never other origins.</summary>
    public static List<(string Path, string Source)> Candidates(string origin, IEnumerable<string> configured, string? contractSource)
    {
        var result = new List<(string, string)>();
        void Add(string? raw, string source)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var value = raw.Trim();
            string path;
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                if (!string.Equals(absolute.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase) || absolute.UserInfo.Length > 0) return;
                path = absolute.PathAndQuery;
            }
            else if (value.StartsWith('/')) path = value;
            else return;
            if (path.Contains("..", StringComparison.Ordinal) || path.Length > 256) return;
            if (!result.Any(r => string.Equals(r.Item1, path, StringComparison.OrdinalIgnoreCase))) result.Add((path, source));
        }
        foreach (var c in configured) Add(c, "Configured");
        Add(contractSource, "Contract source");
        foreach (var d in DefaultCandidates) Add(d, "Default candidate");
        return result.Take(MaxProbes).ToList();
    }

    /// <summary>What one anonymous GET showed. 401/403 is protection evidence; 404 is not-found (not a security pass by itself).</summary>
    public static (ApiDocumentationProbeState State, string? Kind, string Note) Classify(int? status, string? contentType, string? bodyPrefix)
    {
        if (status is null) return (ApiDocumentationProbeState.NotVerified, null, "No response.");
        if (status is 401 or 403) return (ApiDocumentationProbeState.ReachableProtected, null, $"HTTP {status}: the endpoint exists and refuses anonymous access.");
        if (status == 404 || status == 410) return (ApiDocumentationProbeState.NotFound, null, $"HTTP {status}: not found at this path (another path may still exist).");
        if (status is >= 300 and < 400) return (ApiDocumentationProbeState.Redirected, null, $"HTTP {status}: redirected (often to sign-in); redirects are not followed.");
        if (status is >= 200 and < 300)
        {
            var kind = DocumentKind(contentType, bodyPrefix);
            return kind is null
                ? (ApiDocumentationProbeState.NotVerified, null, $"HTTP {status}, but the body is not API documentation (for example an SPA fallback page).")
                : (ApiDocumentationProbeState.ReachablePublic, kind, $"HTTP {status}: {kind} served without authentication.");
        }
        return (ApiDocumentationProbeState.NotVerified, null, $"HTTP {status}: neither documentation nor a denial.");
    }

    public static string? DocumentKind(string? contentType, string? bodyPrefix)
    {
        if (string.IsNullOrEmpty(bodyPrefix)) return null;
        if (OpenApiMarker().IsMatch(bodyPrefix)) return "OpenAPI document";
        if (bodyPrefix.Contains("swagger-ui", StringComparison.OrdinalIgnoreCase) || bodyPrefix.Contains("SwaggerUIBundle", StringComparison.Ordinal)
            || bodyPrefix.Contains("redoc", StringComparison.OrdinalIgnoreCase)) return "Swagger UI page";
        return null;
    }

    /// <summary>Public beats protected beats redirected beats not found; nothing answered = NotVerified.</summary>
    public static ApiDocumentationProbeState Aggregate(IReadOnlyCollection<ApiDocumentationProbe> probes) =>
        probes.Any(p => p.State == ApiDocumentationProbeState.ReachablePublic) ? ApiDocumentationProbeState.ReachablePublic
        : probes.Any(p => p.State == ApiDocumentationProbeState.ReachableProtected) ? ApiDocumentationProbeState.ReachableProtected
        : probes.Any(p => p.State == ApiDocumentationProbeState.Redirected) ? ApiDocumentationProbeState.Redirected
        : probes.Count > 0 && probes.All(p => p.State == ApiDocumentationProbeState.NotFound) ? ApiDocumentationProbeState.NotFound
        : ApiDocumentationProbeState.NotVerified;

    /// <summary>The expectation decides; public documentation is never a defect by itself.</summary>
    public static (ApiDocumentationAssessment Assessment, string Reason) Assess(ApiDocumentationProbeState observed, ApiDocumentationExposureExpectation expectation) => (expectation, observed) switch
    {
        (_, ApiDocumentationProbeState.NotVerified) => (ApiDocumentationAssessment.NotVerified, "No probe produced documentation or a clear denial; exposure is not verified."),
        (ApiDocumentationExposureExpectation.NotSpecified, _) => (ApiDocumentationAssessment.NoExpectation, $"Observed: {Label(observed)}. No exposure expectation is set for this Target Environment, so this is an observation, not a finding."),
        (ApiDocumentationExposureExpectation.Allowed, ApiDocumentationProbeState.ReachablePublic) => (ApiDocumentationAssessment.AsExpected, "Public documentation is allowed for this Target Environment."),
        (ApiDocumentationExposureExpectation.Allowed, ApiDocumentationProbeState.NotFound) => (ApiDocumentationAssessment.UnexpectedAbsence, "Documentation is expected to be available but no candidate path returned it (it may live at another path)."),
        (ApiDocumentationExposureExpectation.Allowed, _) => (ApiDocumentationAssessment.AsExpected, $"Observed: {Label(observed)}; documentation is allowed, so no exposure concern."),
        (ApiDocumentationExposureExpectation.ExpectedProtected, ApiDocumentationProbeState.ReachablePublic) => (ApiDocumentationAssessment.UnexpectedExposure, "Documentation is expected to require authentication but is served anonymously."),
        (ApiDocumentationExposureExpectation.ExpectedProtected, ApiDocumentationProbeState.ReachableProtected) => (ApiDocumentationAssessment.AsExpected, "Documentation refuses anonymous access, as expected."),
        (ApiDocumentationExposureExpectation.ExpectedProtected, _) => (ApiDocumentationAssessment.AsExpected, $"Observed: {Label(observed)}; no anonymous documentation was served."),
        (ApiDocumentationExposureExpectation.ExpectedUnavailable, ApiDocumentationProbeState.ReachablePublic) => (ApiDocumentationAssessment.UnexpectedExposure, "Documentation is expected to be unavailable but is served anonymously."),
        (ApiDocumentationExposureExpectation.ExpectedUnavailable, ApiDocumentationProbeState.ReachableProtected) => (ApiDocumentationAssessment.UnexpectedExposure, "Documentation is expected to be unavailable; a protected documentation endpoint exists (401/403)."),
        (ApiDocumentationExposureExpectation.ExpectedUnavailable, _) => (ApiDocumentationAssessment.AsExpected, $"Observed: {Label(observed)} at the probed paths, as expected (other paths were not probed)."),
        _ => (ApiDocumentationAssessment.NotVerified, "Not assessed."),
    };

    public static string Label(ApiDocumentationProbeState state) => state switch
    {
        ApiDocumentationProbeState.ReachablePublic => "reachable without authentication",
        ApiDocumentationProbeState.ReachableProtected => "reachable but protected (401/403)",
        ApiDocumentationProbeState.NotFound => "not found",
        ApiDocumentationProbeState.Redirected => "redirected",
        _ => "not verified",
    };

    [GeneratedRegex("\"(openapi|swagger)\"\\s*:\\s*\"\\d|^\\s*(openapi|swagger)\\s*:\\s*['\"]?\\d", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OpenApiMarker();
}

// ── CORS ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorsProbeKind { FrontendOrigin, ConfiguredAllowedOrigin, ForeignOrigin }

/// <summary>What the preflight response granted the probing origin. NoCorsHeaders is not a network failure and not a denial defect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorsObservedBehavior { AllowedExplicit, ReflectedOrigin, Wildcard, Denied, NoCorsHeaders, NotVerified }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorsAssessment { AsExpected, Observation, UnexpectedAllow, UnexpectedDeny, Misconfiguration, NotVerified }

public sealed record CorsProbeObservation
{
    public CorsProbeKind Kind { get; init; }
    public string Origin { get; init; } = "";
    public int? StatusCode { get; init; }
    public string? AllowOrigin { get; init; }
    public bool AllowCredentials { get; init; }
    public string? AllowMethods { get; init; }
    public string? AllowHeaders { get; init; }
    public bool VaryOrigin { get; init; }
    /// <summary>Allow / Deny / Unspecified, from the Target Environment expectation.</summary>
    public string Expected { get; init; } = "Unspecified";
    public CorsObservedBehavior Observed { get; init; } = CorsObservedBehavior.NotVerified;
    public CorsAssessment Assessment { get; init; } = CorsAssessment.NotVerified;
    /// <summary>Finding rule id when the assessment is a finding (cors-reflected-origin …); null otherwise.</summary>
    public string? RuleId { get; init; }
    public string Note { get; init; } = "";
}

public static class CorsProbeRules
{
    /// <summary>Synthetic foreign origin under the reserved .invalid TLD: never a real site.</summary>
    public const string ForeignOrigin = "https://foreign.birknext.invalid";
    public const int MaxConfiguredOriginProbes = 3;
    public const string RequestedMethod = "GET";
    public const string RequestedHeaders = "authorization, content-type";

    public static string? NormalizeOrigin(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.UserInfo.Length == 0
            ? u.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : null;

    /// <summary>
    /// Classifies one preflight. Observed behaviour and the expectation are kept apart; a reflected foreign origin is a finding whose
    /// severity depends on credentials and authentication context; absence of CORS headers is never a failure by itself.
    /// </summary>
    public static CorsProbeObservation Evaluate(CorsProbeKind kind, string origin, int? statusCode, IReadOnlyDictionary<string, string>? headers, CorsExpectations expectations, bool authRequired)
    {
        string? H(string name) => headers is not null && headers.TryGetValue(name, out var v) ? v.Trim() : null;
        var allowOrigin = H("access-control-allow-origin");
        var credentials = string.Equals(H("access-control-allow-credentials"), "true", StringComparison.OrdinalIgnoreCase);
        var vary = H("vary") is { } varyValue && varyValue.Split(',').Any(v => v.Trim().Equals("origin", StringComparison.OrdinalIgnoreCase));
        var allowed = expectations.AllowedOrigins.Select(NormalizeOrigin).Where(o => o is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = kind switch
        {
            CorsProbeKind.ForeignOrigin => "Deny",
            CorsProbeKind.ConfiguredAllowedOrigin => "Allow",
            _ => allowed.Count == 0 ? "Unspecified" : allowed.Contains(origin) ? "Allow" : "Deny",
        };
        var observed = statusCode is null ? CorsObservedBehavior.NotVerified
            : allowOrigin is null ? CorsObservedBehavior.NoCorsHeaders
            : allowOrigin == "*" ? CorsObservedBehavior.Wildcard
            : string.Equals(allowOrigin, origin, StringComparison.OrdinalIgnoreCase)
                ? (kind == CorsProbeKind.ForeignOrigin ? CorsObservedBehavior.ReflectedOrigin : CorsObservedBehavior.AllowedExplicit)
                : CorsObservedBehavior.Denied;
        CorsProbeObservation Result(CorsAssessment assessment, string note, string? rule = null) => new()
        {
            Kind = kind, Origin = origin, StatusCode = statusCode, AllowOrigin = allowOrigin, AllowCredentials = credentials,
            AllowMethods = H("access-control-allow-methods"), AllowHeaders = H("access-control-allow-headers"), VaryOrigin = vary,
            Expected = expected, Observed = observed, Assessment = assessment, Note = note, RuleId = rule,
        };

        if (observed == CorsObservedBehavior.NotVerified) return Result(CorsAssessment.NotVerified, "The preflight did not complete; nothing is concluded.");
        if (observed == CorsObservedBehavior.Wildcard && credentials)
            return Result(CorsAssessment.Misconfiguration, "Access-Control-Allow-Origin: * together with Allow-Credentials: true.", "cors-wildcard-credentials");
        if (expectations.AllowCredentials == false && credentials && observed is CorsObservedBehavior.AllowedExplicit or CorsObservedBehavior.ReflectedOrigin)
            return Result(CorsAssessment.UnexpectedAllow, "Credentialed cross-origin requests are allowed although the Target Environment expects no credentials.", "cors-credentials-unexpected");
        switch (kind)
        {
            case CorsProbeKind.ForeignOrigin:
                return observed switch
                {
                    CorsObservedBehavior.ReflectedOrigin when credentials => Result(CorsAssessment.UnexpectedAllow,
                        "The synthetic foreign origin was reflected with Allow-Credentials: true: any site could make credentialed browser requests.", "cors-reflected-origin-credentials"),
                    CorsObservedBehavior.ReflectedOrigin => Result(CorsAssessment.UnexpectedAllow,
                        authRequired ? "The synthetic foreign origin was reflected (without credentials) on an authenticated API." : "The synthetic foreign origin was reflected (without credentials) on a public API.",
                        authRequired ? "cors-reflected-origin" : "cors-reflected-origin-public"),
                    CorsObservedBehavior.Wildcard => Result(authRequired ? CorsAssessment.UnexpectedAllow : CorsAssessment.Observation,
                        authRequired ? "Any origin is allowed (wildcard) on an authenticated API." : "Any origin is allowed (wildcard) on a public API without credentials.", authRequired ? "cors-wildcard-authenticated" : null),
                    CorsObservedBehavior.Denied => Result(CorsAssessment.AsExpected, "The foreign origin was not granted (another origin is allowlisted)."),
                    _ => Result(CorsAssessment.AsExpected, "No CORS headers for the foreign origin: browsers will not let it read responses."),
                };
            case CorsProbeKind.ConfiguredAllowedOrigin:
                return observed switch
                {
                    CorsObservedBehavior.AllowedExplicit or CorsObservedBehavior.Wildcard => Result(CorsAssessment.AsExpected, "The configured allowed origin is granted."),
                    _ => Result(CorsAssessment.UnexpectedDeny, "The configured allowed origin is not granted by the preflight; a browser frontend on that origin would be blocked.", "cors-allowed-origin-denied"),
                };
            default:
                if (expected == "Unspecified")
                    return Result(CorsAssessment.Observation, observed == CorsObservedBehavior.NoCorsHeaders
                        ? "No CORS headers for the frontend origin; if the frontend calls this API cross-origin, verify the policy manually."
                        : $"Observed {observed} for the frontend origin; no allowed-origin expectation is set.");
                return expected == "Allow"
                    ? observed is CorsObservedBehavior.AllowedExplicit or CorsObservedBehavior.Wildcard
                        ? Result(CorsAssessment.AsExpected, "The frontend origin is granted.")
                        : Result(CorsAssessment.UnexpectedDeny, "The frontend origin is in the allowed list but is not granted.", "cors-allowed-origin-denied")
                    : observed is CorsObservedBehavior.AllowedExplicit or CorsObservedBehavior.Wildcard
                        ? Result(CorsAssessment.UnexpectedAllow, "The frontend origin is granted although it is not in the allowed list.", "cors-origin-not-in-allowlist")
                        : Result(CorsAssessment.AsExpected, "The frontend origin is not granted, consistent with the allowed list.");
        }
    }

    /// <summary>Methods/headers granted beyond the expectation lists (only when lists are configured).</summary>
    public static List<string> Excess(string? granted, IEnumerable<string> expected)
    {
        var allowed = expected.Select(e => e.Trim()).Where(e => e.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0 || string.IsNullOrWhiteSpace(granted)) return [];
        return granted.Split(',').Select(g => g.Trim()).Where(g => g.Length > 0 && !allowed.Contains(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

// ── Cookies ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CookieSameSite { NotSet, Strict, Lax, None, Invalid }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CookieEvidenceSource { FrontendDocumentResponse, LocalHttpsProxyObserved }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CookieClassification { DeclaredAuthOrSession, Unclassified }

/// <summary>Metadata of one Set-Cookie. The cookie VALUE is never stored, logged, displayed or exported.</summary>
public sealed record CookieObservation
{
    /// <summary>The cookie name, or a "cookie-…" digest when the name itself looks secret-shaped or is unusually long.</summary>
    public string Name { get; init; } = "";
    public bool NameRedacted { get; init; }
    public string Host { get; init; } = "";
    public bool Secure { get; init; }
    public bool HttpOnly { get; init; }
    public CookieSameSite SameSite { get; init; }
    /// <summary>Explicit Domain attribute (lower-case, leading dot removed) or null for a host-only cookie.</summary>
    public string? Domain { get; init; }
    public string Path { get; init; } = "/";
    public bool Persistent { get; init; }
    public bool Deletion { get; init; }
    public CookieEvidenceSource Source { get; init; }
    public CookieClassification Classification { get; init; } = CookieClassification.Unclassified;
}

public sealed record CookieSecurityFinding(string RuleId, string Severity, string Cookie, string Title, string Detail);

public sealed record CookieSecurityAssessment
{
    public List<CookieObservation> Cookies { get; init; } = [];
    public List<CookieSecurityFinding> Findings { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public int AuthCookies => Cookies.Count(c => c.Classification == CookieClassification.DeclaredAuthOrSession);
}

/// <summary>Parses Set-Cookie attributes. The value after '=' is discarded before anything is returned.</summary>
public static partial class SetCookieMetadataParser
{
    public static CookieObservation? Parse(string? setCookie, string host, CookieEvidenceSource source)
    {
        if (string.IsNullOrWhiteSpace(setCookie)) return null;
        var parts = setCookie.Split(';');
        var pair = parts[0];
        var eq = pair.IndexOf('=');
        if (eq <= 0) return null;
        var rawName = pair[..eq].Trim();
        if (rawName.Length == 0 || rawName.Any(char.IsControl)) return null;
        var (name, redacted) = SafeName(rawName);
        bool secure = false, httpOnly = false, persistent = false, deletion = false;
        var sameSite = CookieSameSite.NotSet;
        string? domain = null;
        var path = "/";
        foreach (var attribute in parts.Skip(1))
        {
            var kv = attribute.Split('=', 2);
            var key = kv[0].Trim();
            var val = kv.Length > 1 ? kv[1].Trim() : "";
            switch (key.ToLowerInvariant())
            {
                case "secure": secure = true; break;
                case "httponly": httpOnly = true; break;
                case "samesite":
                    sameSite = val.ToLowerInvariant() switch { "strict" => CookieSameSite.Strict, "lax" => CookieSameSite.Lax, "none" => CookieSameSite.None, _ => CookieSameSite.Invalid };
                    break;
                case "domain": domain = val.TrimStart('.').ToLowerInvariant() is { Length: > 0 } d && d.Length <= 253 ? d : null; break;
                case "path": path = val.StartsWith('/') && val.Length <= 256 ? val : "/"; break;
                case "max-age":
                    persistent = true;
                    if (long.TryParse(val, out var maxAge) && maxAge <= 0) deletion = true;
                    break;
                case "expires":
                    persistent = true;
                    if (DateTimeOffset.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var expires) && expires < DateTimeOffset.UtcNow.AddMinutes(-1)) deletion = true;
                    break;
            }
        }
        return new CookieObservation
        {
            Name = name, NameRedacted = redacted, Host = host.ToLowerInvariant(), Secure = secure, HttpOnly = httpOnly, SameSite = sameSite, Domain = domain, Path = path,
            Persistent = persistent, Deletion = deletion, Source = source,
        };
    }

    /// <summary>Cookie names are kept unless they look secret-shaped, contain unusual characters or are very long.</summary>
    public static (string Name, bool Redacted) SafeName(string name) =>
        name.Length <= 64 && SafeNamePattern().IsMatch(name) && !SecretShaped().IsMatch(name)
            ? (name, false)
            : ("cookie-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 4).ToLowerInvariant(), true);

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")]
    private static partial Regex SafeNamePattern();

    [GeneratedRegex(@"(?i)(eyJ[A-Za-z0-9_-]{8}|[A-Fa-f0-9]{32,}|bearer|password|secret)")]
    private static partial Regex SecretShaped();
}

/// <summary>
/// Cookie rules. Declared auth/session cookies are evaluated strictly against the expectations; unclassified cookies get only the
/// unconditional browser rule (SameSite=None requires Secure) and are otherwise observations.
/// </summary>
public static class CookieSecurityEvaluator
{
    public static CookieSecurityAssessment Evaluate(IEnumerable<CookieObservation> observations, CookieSecurityExpectations? expectations)
    {
        expectations ??= new CookieSecurityExpectations();
        var authNames = expectations.AuthCookieNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        var allowedSameSite = expectations.AllowedSameSite.Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedDomains = expectations.AllowedDomains.Select(d => d.Trim().TrimStart('.').ToLowerInvariant()).Where(d => d.Length > 0).ToHashSet(StringComparer.Ordinal);
        var cookies = observations.Where(c => !c.Deletion)
            .Select(c => c with { Classification = authNames.Contains(c.Name) ? CookieClassification.DeclaredAuthOrSession : CookieClassification.Unclassified })
            .GroupBy(c => (c.Name, c.Host, c.Path, c.Domain, c.Secure, c.HttpOnly, c.SameSite, c.Persistent, c.Source))
            .Select(g => g.First()).OrderBy(c => c.Host, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        var findings = new List<CookieSecurityFinding>();
        var notes = new List<string>();
        foreach (var c in cookies)
        {
            if (c.SameSite == CookieSameSite.None && !c.Secure)
                findings.Add(new("cookie-samesite-none-insecure", "Medium", c.Name, "SameSite=None without Secure", "Browsers reject SameSite=None cookies that are not Secure; the cookie is either dropped or sent insecurely."));
            if (c.Classification != CookieClassification.DeclaredAuthOrSession) continue;
            if (expectations.RequireSecure && !c.Secure)
                findings.Add(new("cookie-auth-missing-secure", "High", c.Name, "Auth/session cookie missing Secure", "The declared auth/session cookie can be sent over plain HTTP."));
            if (expectations.RequireHttpOnly && !c.HttpOnly)
                findings.Add(new("cookie-auth-missing-httponly", "Medium", c.Name, "Auth/session cookie missing HttpOnly", "Script running in the page can read the declared auth/session cookie."));
            if (allowedSameSite.Count > 0 && !allowedSameSite.Contains(c.SameSite.ToString()))
                findings.Add(new("cookie-auth-samesite", "Low", c.Name, $"Auth/session cookie SameSite={c.SameSite}", $"Expected one of: {string.Join(", ", allowedSameSite)}."));
            if (c.Domain is { } domain && !allowedDomains.Contains(domain))
                findings.Add(new("cookie-auth-broad-domain", allowedDomains.Count == 0 ? "Low" : "Medium", c.Name, $"Auth/session cookie scoped to Domain={domain}",
                    allowedDomains.Count == 0 ? "An explicit Domain shares the cookie with every subdomain; no allowed domains are configured." : $"The domain is not in the allowed list ({string.Join(", ", allowedDomains)})."));
            if (c.Persistent && !expectations.AllowPersistentAuthCookies)
                findings.Add(new("cookie-auth-persistent", "Low", c.Name, "Persistent auth/session cookie", "The declared auth/session cookie has Max-Age/Expires and survives browser restarts."));
        }
        if (authNames.Count == 0)
            notes.Add("No auth/session cookie names are declared in Security Expectations: cookies are reported as observations; only SameSite=None without Secure is assessed.");
        else if (!cookies.Any(c => c.Classification == CookieClassification.DeclaredAuthOrSession))
            notes.Add("None of the declared auth/session cookies was observed in the inspected responses; they were not assessed.");
        return new CookieSecurityAssessment { Cookies = cookies, Findings = findings, Notes = notes };
    }
}

// ── Authorization scenarios ─────────────────────────────────────────────────────────────────────────────────────────────────────

public static class AuthorizationIdentityAliases
{
    public const string Anonymous = "anonymous";
    public const string ProxySession = "proxy-session";
    public static bool IsBuiltIn(string alias) => alias is Anonymous or ProxySession;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthorizationOutcome { VerifiedAllow, VerifiedDeny, UnexpectedAllow, UnexpectedDeny, NotVerified, AuthenticationFailed, ExecutionUnavailable }

public sealed record AuthorizationObservation
{
    public string ScenarioId { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string IdentityAlias { get; init; } = "";
    public string Role { get; init; } = "";
    public AuthorizationExpectation Expected { get; init; }
    public int? StatusCode { get; init; }
    public bool? GraphQlAuthorizationError { get; init; }
    public bool? GraphQlHasData { get; init; }
    public AuthorizationOutcome Outcome { get; init; } = AuthorizationOutcome.NotVerified;
    public string Reason { get; init; } = "";
}

public sealed record AuthorizationRunRequest
{
    public BirkNext.ApiReview.ApiReviewRunRequest Review { get; init; } = new();
    public List<AuthorizationScenario> Scenarios { get; init; } = [];
}

public sealed record AuthorizationRunReport
{
    public string RunId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public TrustedTargetDecision Trust { get; init; } = new();
    public BirkNext.ApiReview.ApiEnvironmentSafetyDecision Safety { get; init; } = new();
    public List<AuthorizationScenario> Scenarios { get; init; } = [];
    public List<AuthorizationObservation> Observations { get; init; } = [];
    public string ExpectationFingerprint { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string? BlockedReason { get; init; }
    public List<string> Limitations { get; init; } = [];
    public Dictionary<AuthorizationOutcome, int> Outcomes => Observations.GroupBy(o => o.Outcome).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
}

public static partial class AuthorizationScenarioRules
{
    public const int MaxScenarios = 20;
    public const int MaxIdentitiesPerScenario = 4;
    public const int MaxGraphQlBytes = 4096;
    private static readonly string[] AuthErrorCodes = ["AUTH_NOT_AUTHORIZED", "AUTH_NOT_AUTHENTICATED", "UNAUTHENTICATED", "UNAUTHORIZED", "FORBIDDEN", "ACCESS_DENIED", "NOT_AUTHORIZED"];

    /// <summary>Null when the scenario is well-formed; otherwise why it cannot run. Destination trust is checked separately by the backend.</summary>
    public static string? Validate(AuthorizationScenario s)
    {
        if (string.IsNullOrWhiteSpace(s.ScenarioId) || s.ScenarioId.Length > 64) return "A scenario id (≤ 64 characters) is required.";
        if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            return "The URL must be an absolute http(s) URL without user info or fragment.";
        if (s.Url.Length > 2048 || s.Url.Contains("..", StringComparison.Ordinal)) return "The URL is too long or contains a traversal sequence.";
        if (s.Identities.Count is 0 or > MaxIdentitiesPerScenario) return $"A scenario needs 1–{MaxIdentitiesPerScenario} identities with explicit expectations.";
        if (s.Identities.Any(i => string.IsNullOrWhiteSpace(i.Alias) || i.Alias.Length > 64)) return "Every identity needs an alias.";
        if (s.ApiType == AuthorizationScenarioApiType.Rest)
        {
            if (s.Method.Trim().ToUpperInvariant() is not ("GET" or "HEAD")) return "Authorization scenarios send safe requests only: GET or HEAD.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(s.GraphQlQuery)) return "A GraphQL scenario needs a query document.";
            if (Encoding.UTF8.GetByteCount(s.GraphQlQuery) > MaxGraphQlBytes) return $"The GraphQL document exceeds {MaxGraphQlBytes} bytes.";
            if (MutationKeyword().IsMatch(s.GraphQlQuery)) return "GraphQL mutations and subscriptions are never sent.";
        }
        return null;
    }

    public static bool IsAuthorizationErrorCode(string? code) => code is not null && AuthErrorCodes.Contains(code.Trim().ToUpperInvariant());

    /// <summary>
    /// Compares one response with the explicit expectation. Allow needs a configured success (2xx by default; GraphQL data without an
    /// authorization error). Deny needs 401/403, a GraphQL authorization error, or 404 when anti-disclosure is declared. Data volume is never used.
    /// </summary>
    public static (AuthorizationOutcome Outcome, string Reason) Classify(AuthorizationScenario scenario, AuthorizationScenarioIdentity identity, bool executed, int? status,
        bool? graphQlAuthError, bool? graphQlHasData, int? graphQlErrors, string? failure = null)
    {
        if (!executed) return (AuthorizationOutcome.NotVerified, failure ?? "The request did not complete.");
        var allowStatus = status is { } s && (scenario.SuccessStatusCodes.Count > 0 ? scenario.SuccessStatusCodes.Contains(s) : s is >= 200 and < 300);
        bool allowed, denied;
        string evidence;
        if (scenario.ApiType == AuthorizationScenarioApiType.GraphQl)
        {
            denied = status is 401 or 403 || graphQlAuthError == true || (status == 404 && identity.NotFoundMeansDeny);
            allowed = !denied && allowStatus && graphQlHasData == true && graphQlErrors is null or 0;
            evidence = $"HTTP {status}{(graphQlAuthError == true ? ", GraphQL authorization error" : graphQlHasData == true ? ", GraphQL data" : graphQlErrors is > 0 ? ", GraphQL errors" : "")}";
        }
        else
        {
            denied = status is 401 or 403 || (status == 404 && identity.NotFoundMeansDeny);
            allowed = !denied && allowStatus;
            evidence = $"HTTP {status}";
        }
        if (identity.Expected == AuthorizationExpectation.Allow)
        {
            if (allowed) return (AuthorizationOutcome.VerifiedAllow, $"{evidence}: allowed, as expected.");
            if (status == 401) return (AuthorizationOutcome.AuthenticationFailed, $"{evidence}: the identity was not authenticated, so authorization was not reached.");
            if (denied) return (AuthorizationOutcome.UnexpectedDeny, $"{evidence}: denied although this identity is expected to be allowed.");
            return (AuthorizationOutcome.NotVerified, status == 404 ? $"{evidence}: not found; 404 is not treated as a result for an expected allow." : $"{evidence}: neither the configured success nor a denial.");
        }
        if (denied) return (AuthorizationOutcome.VerifiedDeny, $"{evidence}: denied, as expected.");
        if (allowed) return (AuthorizationOutcome.UnexpectedAllow, $"{evidence}: allowed although this identity is expected to be denied.");
        return (AuthorizationOutcome.NotVerified, status == 404
            ? $"{evidence}: not found. 404 counts as denial only when anti-disclosure is declared for this identity."
            : $"{evidence}: neither a denial nor the configured success.");
    }

    [GeneratedRegex(@"\b(mutation|subscription)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MutationKeyword();
}

// ── Capability matrix (docs and UI read the same truth) ────────────────────────────────────────────────────────────────────────────

public sealed record SecurityCapability(string Area, string State, string Where);

public static class SecurityCapabilityMatrix
{
    public static readonly IReadOnlyList<SecurityCapability> Entries =
    [
        new("Security headers", "Runnable", "FQR Static Security, AQR"),
        new("CORS", "Runnable with bounded configured-origin and synthetic foreign-origin preflights", "AQR"),
        new("Cookies", "Runnable metadata inspection (no values)", "FQR Static Security, Local HTTPS proxy observation"),
        new("Swagger/OpenAPI exposure", "Runnable", "AQR"),
        new("Authentication enforcement", "Runnable", "AQR"),
        new("Role-based authorization", "Runnable with explicit scenarios and available test identities", "AQR → Authorization scenarios"),
        new("Query/path/header fuzzing", "Runnable", "AQR → Safe fuzzing"),
        new("Body fuzzing", "Runnable only on explicitly opted-in operations of trusted non-production targets", "AQR → Safe fuzzing"),
        new("GraphQL mutation fuzzing", "Not implemented", "—"),
        new("ZAP", "Optional dependency", "FQR Passive Security"),
        new("Full penetration test", "Not implemented", "—"),
    ];
}

/// <summary>Reads GraphQL error extension codes only (never messages). Capped and restricted to code-shaped tokens.</summary>
public static partial class GraphQlErrorCodeReader
{
    public static List<string> Codes(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return [];
        var codes = new List<string>();
        foreach (var error in errors.EnumerateArray())
        {
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object
                && ext.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() is { } value && CodeShape().IsMatch(value))
                codes.Add(value.ToUpperInvariant());
            if (codes.Count >= 10) break;
        }
        return codes.Distinct(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex CodeShape();
}

/// <summary>
/// What the backend allows for protected security execution (authorization scenarios, body fuzzing) and whether the Target Environment
/// is server-registered. Read by the UI to disable runs truthfully; the backend enforces the same rules on every run.
/// </summary>
public sealed record SecurityExecutionStatus
{
    public bool RequireAuthenticatedUser { get; init; }
    /// <summary>This BirkNext instance has a user authentication scheme for protected security endpoints. False: none is configured.</summary>
    public bool UserAuthenticationAvailable { get; init; }
    public string? Message { get; init; }
    public TrustedTargetDecision Trust { get; init; } = new();
    public List<SecurityCapability> Capabilities { get; init; } = [.. SecurityCapabilityMatrix.Entries];
    [JsonIgnore] public bool ProtectedExecutionAvailable => !RequireAuthenticatedUser || UserAuthenticationAvailable;

    public const string AuthenticationRequiredMessage = "Security execution requires Entra authentication configuration.";
}
