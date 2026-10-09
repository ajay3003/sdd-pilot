using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BirkNext.Standards;

namespace BirkNext.ApiReview;

// ── Environment safety ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Why active API testing (error probes, safe fuzzing) is or is not permitted. Decided by the backend from the Target Environment
/// classification, the server-held proxy context (when one exists) and host production markers — never from a client boolean alone.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiEnvironmentSafetyState
{
    Allowed,
    /// <summary>The environment is classified Production.</summary>
    ProductionBlocked,
    /// <summary>The environment type is missing or not recognised; intent cannot be established, so active testing fails closed.</summary>
    UnknownBlocked,
    /// <summary>A recognised non-production type that is not on the active-testing allow-list (RC, Custom).</summary>
    NotPermittedBlocked,
    /// <summary>The claimed classification disagrees with the server-held proxy context of the same Target Environment.</summary>
    ConflictBlocked,
    /// <summary>A target or the environment host carries a production marker, whatever the claimed classification.</summary>
    ProductionMarkerBlocked,
    /// <summary>Active API testing is switched off for this BirkNext instance.</summary>
    Disabled,
}

public sealed record ApiEnvironmentSafetyDecision
{
    public ApiEnvironmentSafetyState State { get; init; } = ApiEnvironmentSafetyState.UnknownBlocked;
    /// <summary>The environment type the decision used (claimed type, or the server-held proxy type when they agree).</summary>
    public string? EnvironmentType { get; init; }
    /// <summary>Production, a production marker or a production claim: passive review only (error probes off, introspection expected disabled).</summary>
    public bool ProductionLike { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>What the backend checked (classification, proxy context, host markers). Never a credential or a configuration value.</summary>
    public List<string> Evidence { get; init; } = [];
    [JsonIgnore] public bool ActiveTestingAllowed => State == ApiEnvironmentSafetyState.Allowed;
}

/// <summary>Backend allow-list for active API testing. Narrower than the proxy list: RC and Custom are not included.</summary>
public static class ApiActiveTestingEnvironments
{
    public static readonly IReadOnlyList<string> Allowed = ["Local", "Development", "QA", "Test"];

    public static bool IsAllowed(string? environmentType) =>
        environmentType is not null && Allowed.Contains(environmentType.Trim(), StringComparer.OrdinalIgnoreCase);
}

// ── Authentication enforcement ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Result of the deliberate anonymous probe on a target that requires authentication. Authentication only: a denial says the
/// endpoint will not answer without a credential, nothing about what an authenticated identity may access (authorization).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiAuthenticationEnforcementStatus
{
    /// <summary>An anonymous request was refused (401, 403, or a GraphQL error envelope without data).</summary>
    Verified,
    /// <summary>An anonymous request returned protected content (REST JSON body, GraphQL data).</summary>
    UnexpectedlyPublic,
    /// <summary>The probe ran but its answer proves neither (404 anti-disclosure, redirect, 5xx, non-JSON 2xx), or it could not run.</summary>
    NotVerified,
    /// <summary>The target does not require authentication.</summary>
    NotApplicable,
}

public sealed record ApiAuthenticationEnforcementResult
{
    public ApiAuthenticationEnforcementStatus Status { get; init; } = ApiAuthenticationEnforcementStatus.NotApplicable;
    /// <summary>"GET /api/children" or "query { __typename }" — the anonymous request that was sent (or would have been).</summary>
    public string? Probe { get; init; }
    public int? StatusCode { get; init; }
    /// <summary>Status of the same operation with the credential, when it was executed in this run.</summary>
    public int? AuthenticatedStatusCode { get; init; }
    public string Reason { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
}

// ── Security header expectations ───────────────────────────────────────────────────────────────────────────────────

/// <summary>Where a security header is meaningful. Document headers govern browser-rendered responses; transport headers apply to every response.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityHeaderScope { Transport, Document }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityHeaderOutcome
{
    /// <summary>Expected by the Target Environment and present.</summary>
    Pass,
    /// <summary>Expected by the Target Environment and absent (or, for X-Content-Type-Options, not "nosniff").</summary>
    Missing,
    /// <summary>Not expected; observed for information only.</summary>
    Observed,
    /// <summary>Not expected and absent: nothing to assess.</summary>
    NotAssessed,
    /// <summary>Expected, but a document-level header on an API response: evaluated on the frontend document instead.</summary>
    NotApplicable,
}

public sealed record SecurityHeaderEvaluation(string Header, bool Expected, bool Present, string? ObservedValue, SecurityHeaderOutcome Result, SecurityHeaderScope Scope, string Note);

/// <summary>
/// One evaluator for the Target Environment's <c>ExpectedSecurityHeaders</c>, shared by FQR Static Security and API Quality Review so
/// both read the same policy. Presence only: the expectation model stores header names, not values, so no value policy is invented.
/// When a review carries no expectation list (older clients), the engine's own historical defaults apply — reported as such.
/// </summary>
public static class SecurityHeaderExpectations
{
    /// <summary>The headers this evaluator knows; anything else in the expectation list is evaluated by presence with Document scope.</summary>
    public static readonly IReadOnlyList<(string Header, SecurityHeaderScope Scope)> Known =
    [
        ("Content-Security-Policy", SecurityHeaderScope.Document),
        ("Strict-Transport-Security", SecurityHeaderScope.Transport),
        ("X-Content-Type-Options", SecurityHeaderScope.Transport),
        ("X-Frame-Options", SecurityHeaderScope.Document),
        ("Referrer-Policy", SecurityHeaderScope.Document),
        ("Permissions-Policy", SecurityHeaderScope.Document),
    ];

    public static SecurityHeaderScope ScopeOf(string header) =>
        Known.FirstOrDefault(k => string.Equals(k.Header, header.Trim(), StringComparison.OrdinalIgnoreCase)) is { Header: not null } known ? known.Scope : SecurityHeaderScope.Document;

    public static string Canonical(string header) =>
        Known.FirstOrDefault(k => string.Equals(k.Header, header.Trim(), StringComparison.OrdinalIgnoreCase)) is { Header: { } name } ? name : header.Trim();

    /// <summary>
    /// Evaluates <paramref name="observed"/> (lower- or mixed-case names) against <paramref name="expected"/>. Every known header is listed;
    /// unknown expected headers are added. <paramref name="apiResponse"/>: document-scope headers are NotApplicable instead of Missing.
    /// HSTS on plain HTTP is NotApplicable (browsers ignore it there).
    /// </summary>
    public static List<SecurityHeaderEvaluation> Evaluate(IReadOnlyDictionary<string, string> observed, IEnumerable<string> expected, bool apiResponse, bool https)
    {
        var lookup = new Dictionary<string, string>(observed, StringComparer.OrdinalIgnoreCase);
        var expectedSet = expected.Where(h => !string.IsNullOrWhiteSpace(h)).Select(Canonical).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = Known.Select(k => k.Header).Concat(expectedSet.Where(e => !Known.Any(k => string.Equals(k.Header, e, StringComparison.OrdinalIgnoreCase)))).ToList();
        var result = new List<SecurityHeaderEvaluation>();
        foreach (var name in names)
        {
            var scope = ScopeOf(name);
            var isExpected = expectedSet.Contains(name);
            var present = lookup.TryGetValue(name, out var value);
            // nosniff is the only valid value of X-Content-Type-Options; anything else is equivalent to absent.
            if (present && string.Equals(name, "X-Content-Type-Options", StringComparison.OrdinalIgnoreCase) && !value!.Contains("nosniff", StringComparison.OrdinalIgnoreCase)) present = false;
            var shown = value is { Length: > 200 } ? value[..200] + "…" : value;
            SecurityHeaderEvaluation Row(SecurityHeaderOutcome r, string note) => new(name, isExpected, present, lookup.ContainsKey(name) ? shown : null, r, scope, note);
            if (string.Equals(name, "Strict-Transport-Security", StringComparison.OrdinalIgnoreCase) && !https)
                result.Add(Row(SecurityHeaderOutcome.NotApplicable, "Plain HTTP: browsers ignore HSTS here (TLS is assessed separately)."));
            else if (!isExpected)
                result.Add(Row(present ? SecurityHeaderOutcome.Observed : SecurityHeaderOutcome.NotAssessed, present ? "Observed; not required by the Target Environment." : "Not required by the Target Environment."));
            else if (apiResponse && scope == SecurityHeaderScope.Document)
                result.Add(Row(SecurityHeaderOutcome.NotApplicable, present ? "Observed. Document-level header: required for the frontend document (FQR Static Security), not assessed on API responses." : "Document-level header: required for the frontend document (FQR Static Security), not assessed on API responses."));
            else
                result.Add(Row(present ? SecurityHeaderOutcome.Pass : SecurityHeaderOutcome.Missing, present ? "Present as expected (presence only; no value policy is stored)." : "Required by the Target Environment and absent."));
        }
        return result;
    }
}

// ── Safe fuzzing ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Off: nothing is generated. Contract fuzzing: inputs that violate the published contract (missing required, invalid enum/UUID/date,
/// wrong type, numeric boundaries, GraphQL validation errors). Safe security fuzzing: contract cases plus bounded robustness cases
/// (empty and oversized-but-bounded strings, unexpected parameters, malformed header values, malformed GraphQL syntax). There is no
/// aggressive mode: no attack dictionaries, no write methods, no mutations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFuzzingLevel { Off, ContractFuzzing, SafeSecurityFuzzing }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFuzzProtocol { Rest, GraphQl }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFuzzParameterLocation { Query, Path, Header, GraphQlArgument, GraphQlField, GraphQlDocument }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFuzzMutationType
{
    MissingRequired,
    InvalidEnum,
    InvalidUuid,
    InvalidDate,
    WrongType,
    NumericBelowMinimum,
    NumericAboveMaximum,
    NegativeNotAllowed,
    ZeroNotAllowed,
    StringTooLong,
    EmptyString,
    OversizedString,
    UnexpectedParameter,
    MissingRequiredHeader,
    InvalidHeaderValue,
    MalformedHeaderValue,
    GraphQlUnknownField,
    GraphQlWrongScalarType,
    GraphQlMissingRequiredArgument,
    GraphQlNullForNonNull,
    GraphQlInvalidEnum,
    GraphQlMalformedSyntax,
}

/// <summary>What a well-behaved API should do with the case. Accepting a tolerated case (unknown query parameter) is not a defect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFuzzExpectedBehavior { RejectWithClientError, RejectWithGraphQlError, AcceptOrReject }

/// <summary>Why an operation can or cannot be fuzzed. Only ReadOnlyEligible operations get cases.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFuzzSafetyClassification
{
    ReadOnlyEligible,
    UnsafeMethod,
    Mutation,
    MissingContract,
    UnknownSafety,
    ProductionBlocked,
    /// <summary>The target could not be reached by the review (authenticated context missing, manual-only, blocked scheme).</summary>
    AccessUnavailable,
    /// <summary>Eligible, but the run's request budget is spent before its cases.</summary>
    BudgetExhausted,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFuzzOutcome
{
    HandledValidation,
    Unexpected5xx,
    UnexpectedAcceptance,
    Timeout,
    ConnectionFailure,
    ContractViolation,
    PotentialInformationLeak,
    AuthenticationBlocked,
    AuthorizationBlocked,
    NotExecuted,
    SafetyBlocked,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiFuzzCompleteness { NotRun, Full, Partial, Failed }

/// <summary>
/// Fuzzing limits. Every value is clamped by the backend to <see cref="ApiFuzzingLimits"/>; the client can lower a limit, never raise it.
/// </summary>
public sealed record ApiFuzzingSettings
{
    public bool Enabled { get; init; } = true;
    public ApiFuzzingLevel Level { get; init; } = ApiFuzzingLevel.Off;
    public int MaxCasesPerOperation { get; init; } = ApiFuzzingLimits.DefaultCasesPerOperation;
    public int MaxTotalRequests { get; init; } = ApiFuzzingLimits.DefaultTotalRequests;
    public int MaxParameterLength { get; init; } = ApiFuzzingLimits.DefaultParameterLength;
    public int MaxPayloadBytes { get; init; } = ApiFuzzingLimits.MaxPayloadBytes;
    public int MaxConcurrency { get; init; } = 1;
    public int RequestDelayMs { get; init; } = ApiFuzzingLimits.DefaultDelayMs;
    public int RequestTimeoutSeconds { get; init; } = ApiFuzzingLimits.MaxTimeoutSeconds;
    public bool StopOnUnexpected5xx { get; init; } = true;

    /// <summary>The effective settings: every limit inside the hard bounds; concurrency is always 1.</summary>
    public ApiFuzzingSettings Clamped() => this with
    {
        MaxCasesPerOperation = Math.Clamp(MaxCasesPerOperation, 1, ApiFuzzingLimits.MaxCasesPerOperation),
        MaxTotalRequests = Math.Clamp(MaxTotalRequests, 1, ApiFuzzingLimits.MaxTotalRequests),
        MaxParameterLength = Math.Clamp(MaxParameterLength, 16, ApiFuzzingLimits.MaxParameterLength),
        MaxPayloadBytes = Math.Clamp(MaxPayloadBytes, 256, ApiFuzzingLimits.MaxPayloadBytes),
        MaxConcurrency = 1,
        RequestDelayMs = Math.Clamp(RequestDelayMs, ApiFuzzingLimits.MinDelayMs, ApiFuzzingLimits.MaxDelayMs),
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 1, ApiFuzzingLimits.MaxTimeoutSeconds),
    };
}

/// <summary>Hard bounds the backend enforces whatever the request says.</summary>
public static class ApiFuzzingLimits
{
    public const int MaxTotalRequests = 50;
    public const int DefaultTotalRequests = 40;
    public const int MaxCasesPerOperation = 8;
    public const int DefaultCasesPerOperation = 4;
    public const int MaxParameterLength = 1024;
    public const int DefaultParameterLength = 256;
    public const int MaxPayloadBytes = 4096;
    public const int MaxUrlLength = 2048;
    public const int MinDelayMs = 100;
    public const int DefaultDelayMs = 250;
    public const int MaxDelayMs = 5000;
    public const int MaxTimeoutSeconds = 20;
    public const int MaxTargets = 10;
}

/// <summary>
/// One deterministic fuzz case. Identity is a digest of target, operation, mutation and parameter: the same contract and settings always
/// produce the same cases in the same order. Values are synthetic (never copied from traffic) and bounded.
/// </summary>
public sealed record ApiFuzzCase
{
    public string CaseId { get; init; } = "";
    public ApiFuzzProtocol Protocol { get; init; }
    public string TargetId { get; init; } = "";
    public string OperationId { get; init; } = "";
    /// <summary>"GET /api/children/{id}" or "query children".</summary>
    public string OperationDisplay { get; init; } = "";
    public ApiFuzzMutationType MutationType { get; init; }
    public string Parameter { get; init; } = "";
    public ApiFuzzParameterLocation Location { get; init; }
    /// <summary>Where the contract defines the mutated element (JSON pointer into the OpenAPI document, or schema coordinate).</summary>
    public string SourceContractRef { get; init; } = "";
    public string Method { get; init; } = "GET";
    public ApiFuzzExpectedBehavior ExpectedBehavior { get; init; }
    public ApiFuzzSafetyClassification SafetyClassification { get; init; } = ApiFuzzSafetyClassification.ReadOnlyEligible;
    /// <summary>Short description of the synthetic value ("not-a-uuid", "2048 × 'a' (bounded)"). Never a secret, never real data.</summary>
    public string ValuePreview { get; init; } = "";

    // Request material, synthetic and bounded. Re-validated by the backend guard before anything is sent.
    public string Path { get; init; } = "/";
    public List<KeyValuePair<string, string>> Query { get; init; } = [];
    public List<KeyValuePair<string, string>> Headers { get; init; } = [];
    public string? GraphQlQuery { get; init; }
    /// <summary>The query is deliberately not parseable (malformed-syntax case).</summary>
    public bool ExpectSyntaxError { get; init; }

    public static string IdFor(string targetId, string operationId, ApiFuzzMutationType mutation, ApiFuzzParameterLocation location, string parameter)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{targetId}|{operationId}|{mutation}|{location}|{parameter}"));
        return "fz-" + Convert.ToHexString(digest, 0, 6).ToLowerInvariant();
    }
}

public sealed record ApiFuzzOperationEligibility
{
    public string TargetId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string Display { get; init; } = "";
    public ApiFuzzProtocol Protocol { get; init; }
    public string Method { get; init; } = "";
    public ApiFuzzSafetyClassification Classification { get; init; }
    public string Reason { get; init; } = "";
    public int CaseCount { get; init; }
}

public sealed record ApiFuzzContractFingerprint(string TargetId, string Kind, string? Source, string? Hash);

/// <summary>Eligibility analysis and case preview. Generating it sends no fuzz request (only the contract/schema retrieval the review also does).</summary>
public sealed record ApiFuzzingPlan
{
    public ApiEnvironmentSafetyDecision Safety { get; init; } = new();
    public ApiFuzzingLevel Level { get; init; }
    public ApiFuzzingSettings Settings { get; init; } = new();
    public List<ApiFuzzOperationEligibility> Operations { get; init; } = [];
    public List<ApiFuzzCase> Cases { get; init; } = [];
    public List<ApiFuzzContractFingerprint> Contracts { get; init; } = [];
    /// <summary>Eligible cases that did not fit the request budget (not executed, never silently dropped).</summary>
    public int CasesOverBudget { get; init; }
    /// <summary>Why nothing can run (Off, blocked environment, no contract). Null when at least one case is planned.</summary>
    public string? NotAvailableReason { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    [JsonIgnore] public int EligibleOperations => Operations.Count(o => o.Classification == ApiFuzzSafetyClassification.ReadOnlyEligible);
    [JsonIgnore] public bool CanRun => Safety.ActiveTestingAllowed && Level != ApiFuzzingLevel.Off && Cases.Count > 0;
}

public sealed record ApiFuzzCaseResult
{
    public string CaseId { get; init; } = "";
    public bool Executed { get; init; }
    public int? StatusCode { get; init; }
    public double? ElapsedMs { get; init; }
    public string? ContentType { get; init; }
    public ApiFuzzOutcome Outcome { get; init; } = ApiFuzzOutcome.NotExecuted;
    public int? GraphQlErrors { get; init; }
    public bool? GraphQlHasData { get; init; }
    /// <summary>Names of leak indicators ("stack-trace", "sql"), never the response text.</summary>
    public List<string> LeakIndicators { get; init; } = [];
    public string Note { get; init; } = "";
}

/// <summary>One logical issue: equivalent cases (same rule, same operation; same leak indicators on the same target) are evidence of one finding.</summary>
public sealed record ApiFuzzFinding
{
    public string LogicalKey { get; init; } = "";
    public string RuleId { get; init; } = "";
    public ApiReviewSeverity Severity { get; init; }
    public string TargetId { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public List<string> CaseIds { get; init; } = [];
    /// <summary>Redacted evidence lines: mutation, parameter, status, indicator names. Never a response body or credential.</summary>
    public List<string> Evidence { get; init; } = [];
    public List<StandardReference> StandardsReferences { get; init; } = [];
}

public sealed record ApiFuzzingReport
{
    public string RunId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public ApiEnvironmentSafetyDecision Safety { get; init; } = new();
    public ApiFuzzingLevel Level { get; init; }
    public ApiFuzzingSettings Settings { get; init; } = new();
    public List<ApiFuzzOperationEligibility> Operations { get; init; } = [];
    public List<ApiFuzzCase> Cases { get; init; } = [];
    public List<ApiFuzzCaseResult> Results { get; init; } = [];
    public List<ApiFuzzFinding> Findings { get; init; } = [];
    public List<ApiFuzzContractFingerprint> Contracts { get; init; } = [];
    public int CasesPlanned { get; init; }
    public int CasesExecuted { get; init; }
    public int CasesBlocked { get; init; }
    public int CasesOverBudget { get; init; }
    public Dictionary<ApiFuzzOutcome, int> Outcomes { get; init; } = new();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ApiFuzzCompleteness Completeness { get; init; } = ApiFuzzCompleteness.NotRun;
    public string? CompletenessReason { get; init; }
    public bool Running { get; init; }
    public bool Cancelled { get; init; }
    public List<string> Limitations { get; init; } = [];
}

public sealed record ApiFuzzingRunRequest
{
    public ApiReviewRunRequest Review { get; init; } = new();
    public ApiFuzzingSettings Settings { get; init; } = new();
    /// <summary>Optional subset of eligible operation ids; null = every eligible operation (within budget). Unknown ids are ignored.</summary>
    public List<string>? OperationIds { get; init; }
}

/// <summary>Plain wording shared by the UI, the export and the docs.</summary>
public static class ApiFuzzingWording
{
    public const string NotAPenetrationTest =
        "Safe fuzzing sends a bounded set of deterministic invalid/boundary requests to non-production APIs. It is not a penetration test.";
    public const string Scope =
        "REST GET/HEAD/OPTIONS on operations of the published OpenAPI contract and GraphQL queries from the schema only. No write methods, no GraphQL mutations, no request bodies, no attack dictionaries, never Production.";
}
