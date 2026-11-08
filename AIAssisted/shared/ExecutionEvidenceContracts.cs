using System.Text.Json.Serialization;

namespace BirkNext.ExecutionEvidence;

// Execution Evidence: one shared, locally persisted record of what an authorized test execution actually sent and received.
// Run → Logical Call → Physical Attempt → captured Request / Response → Assertions. Test Data owns the inputs (referenced by alias and
// version); Execution Evidence owns the history. Everything here is redacted before it is persisted; nothing here is ever replayed
// automatically. A request preview is never evidence: requests and responses are captured at the HTTP transport boundary.

/// <summary>Final or current state. Failed = the exchange happened and an assertion failed; Error = a technical problem prevented normal
/// evaluation; Blocked = refused before anything was sent; Interrupted = started but ended without an authoritative result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionState { Planned, Queued, Running, Passed, Failed, Error, Skipped, Blocked, Cancelled, TimedOut, Interrupted, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionProtocol { Rest, GraphQl }

/// <summary>What happened on the wire for one attempt. NotSent = refused before transmission (no request evidence exists).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransportResult { ResponseReceived, ConnectionFailed, TimedOut, Cancelled, NotSent }

/// <summary>How complete a captured body is. Never "original": every persisted body has passed the redaction policy first.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureCompleteness { ExactRedacted, Truncated, MetadataOnly, Empty, Unavailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapturePolicy { MetadataOnly, BoundedBody, FullPermittedBody }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssertionKind { StatusCode, Header, JsonPath, ResponseTime, GraphQlErrorCount, GraphQlErrorCode, GraphQlErrorPath, GraphQlDataPath, GraphQlHasData }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssertionOperator { Equals, NotEquals, Exists, NotExists, Contains, LessThan, GreaterThan, In }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssertionOutcome { Passed, Failed, NotAssessed }

public sealed record CapturedHeader(string Name, string Value, bool Redacted = false);

/// <summary>
/// How a run authenticates. AttendedSession: the credential the Local HTTPS proxy observed from the tester's own sign-in (delegated, memory only,
/// HTTPS + approved hosts of the Target Environment). ClientCredentials / ApiKey: the Target Environment's unattended API service credential
/// (encrypted at rest, applied in the backend only). Attended and unattended are never substituted for each other.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionAuthenticationKind { None, AttendedSession, ClientCredentials, ApiKey }

/// <summary>An unattended API service credential as the UI reads it: configuration only — the secret is write-only.</summary>
public sealed record ApiServiceCredentialView(string EnvironmentId, ExecutionAuthenticationKind Kind, string Reference, string? TokenUrl, string? ClientId, string? Scope,
    string? HeaderName, bool SecretStored, int Version, DateTimeOffset UpdatedAt);

/// <summary>Save request. <see cref="Secret"/> (client secret or API key) is required on create and when the kind changes; empty keeps the stored one.</summary>
public sealed record ApiServiceCredentialSave
{
    public string EnvironmentId { get; init; } = "";
    public ExecutionAuthenticationKind Kind { get; init; }
    public string? TokenUrl { get; init; }
    public string? ClientId { get; init; }
    public string? Scope { get; init; }
    public string? HeaderName { get; init; }
    public string? Secret { get; init; }
    public override string ToString() => $"ApiServiceCredentialSave({EnvironmentId}, {Kind}, secret redacted)";
}

/// <summary>A body after redaction. <see cref="ByteCount"/> is the size actually transmitted/received; <see cref="Sha256"/> hashes the stored
/// (redacted) text so an export can be verified; <see cref="Note"/> says what was omitted and why.</summary>
public sealed record CapturedBody
{
    public string? ContentType { get; init; }
    public long ByteCount { get; init; }
    public string? Encoding { get; init; }
    public string? Text { get; init; }
    public string? Sha256 { get; init; }
    public CaptureCompleteness Completeness { get; init; }
    public int RedactedFields { get; init; }
    public string? Note { get; init; }
}

public sealed record CapturedRequest
{
    public string Method { get; init; } = "";
    /// <summary>Scheme, host, port, path and query — credential-bearing query parameters masked.</summary>
    public string Url { get; init; } = "";
    public List<CapturedHeader> Headers { get; init; } = [];
    public CapturedBody? Body { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public string? GraphQlOperationName { get; init; }
    public string? GraphQlOperationType { get; init; }
}

public sealed record CapturedResponse
{
    public int StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public List<CapturedHeader> Headers { get; init; } = [];
    public CapturedBody? Body { get; init; }
    /// <summary>Client elapsed time from sending to the end of reading the (bounded) body. Other timing phases are not captured.</summary>
    public long DurationMs { get; init; }
    public bool? GraphQlHasData { get; init; }
    public int? GraphQlErrorCount { get; init; }
    public List<string> GraphQlErrorMessages { get; init; } = [];
    public List<string> GraphQlErrorPaths { get; init; } = [];
    public List<string> GraphQlErrorCodes { get; init; } = [];
    /// <summary>Body is not valid JSON although JSON was expected or declared (distinct from empty, null JSON or 204).</summary>
    public bool InvalidJson { get; init; }
}

public sealed record ExecutionAttempt
{
    public int AttemptNumber { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public TransportResult Transport { get; init; }
    public CapturedRequest? Request { get; init; }
    public CapturedResponse? Response { get; init; }
    public string? Error { get; init; }
    public string? RetryReason { get; init; }
    /// <summary>How many times this attempt's request was actually written to the wire. The HTTP stack resends a request without a body when the
    /// connection closes before a response, so this can exceed 1; 0 = not transmitted.</summary>
    public int Transmissions { get; init; }
}

public sealed record AssertionDefinition(string Id, AssertionKind Kind, AssertionOperator Operator, string? Target = null, string? Expected = null);

public sealed record AssertionResult(string Id, AssertionKind Kind, AssertionOperator Operator, string? Target, string? Expected, string? Actual, AssertionOutcome Outcome, string Explanation);

/// <summary>Bounded retry: which outcomes are retried, how often, how long to wait. Each retry is a new physical attempt of the same call.</summary>
public sealed record RetryPolicy(int MaxAttempts = 1, List<int>? RetryOnStatus = null, bool RetryOnTransportError = false, int DelayMs = 0);

/// <summary>A planned logical call (REST or GraphQL). Paths are relative to the run's target; headers never carry credentials (authentication
/// comes from the Target Environment's configured credential).</summary>
public sealed record ExecutionCallDefinition
{
    public string Name { get; init; } = "";
    public ExecutionProtocol Protocol { get; init; }
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ContentType { get; init; }
    public string? Body { get; init; }
    public string? GraphQlQuery { get; init; }
    public string? GraphQlVariablesJson { get; init; }
    public string? GraphQlOperationName { get; init; }
    public List<AssertionDefinition> Assertions { get; init; } = [];
    public RetryPolicy Retry { get; init; } = new();
    public int TimeoutMs { get; init; } = 15000;
    /// <summary>Test Data used, as "alias@vN" (the exact version consumed; never re-resolved for history).</summary>
    public List<string> TestDataRefs { get; init; } = [];
    /// <summary>Contract operation identity this call exercises ("contractId#operation"), when known.</summary>
    public string? OperationRef { get; init; }
    public string? DefinitionVersion { get; init; }
    public string? ExpectedOutcome { get; init; }
}

public sealed record ExecutionCallRecord
{
    public Guid CallId { get; init; }
    public int Sequence { get; init; }
    public ExecutionCallDefinition Definition { get; init; } = new();
    public ExecutionState State { get; init; }
    public string? Reason { get; init; }
    public List<ExecutionAttempt> Attempts { get; init; } = [];
    public List<AssertionResult> Assertions { get; init; } = [];
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record ExecutionRunCounts(int Planned, int Executed, int Passed, int Failed, int Errors, int Skipped, int Blocked, int Cancelled, int TimedOut,
    int Interrupted, int NotAssessed, int Attempts);

public sealed record ExecutionRunSummary
{
    public Guid RunId { get; init; }
    public string Name { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string? EnvironmentType { get; init; }
    public string TargetOrigin { get; init; } = "";
    public ExecutionState State { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ExecutionRunCounts Counts { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public CapturePolicy CapturePolicy { get; init; }
    public string RedactionVersion { get; init; } = "";
    public string Initiator { get; init; } = "";
    public string? AuthenticationMode { get; init; }
    /// <summary>Which credential configuration was used ("attended-session:{environment}", "api-credential:{environment}:ClientCredentials:v3") — never a value.</summary>
    public string? AuthenticationReference { get; init; }
    public Guid? SourceSnapshotId { get; init; }
    public List<string> ContractRefs { get; init; } = [];
    public Guid? ReplayOf { get; init; }
    /// <summary>Evidence completeness: calls whose every sent attempt has request and (when received) response evidence.</summary>
    public int CallsWithCompleteEvidence { get; init; }
    /// <summary>Requests actually written to the wire across all attempts (more than the attempts when the HTTP stack resent a request).</summary>
    public int WireTransmissions { get; init; }
}

public sealed record ExecutionRunDetail(ExecutionRunSummary Run, List<ExecutionCallRecord> Calls, int TotalCalls, int Skip, int Take);

/// <summary>A user-initiated run: the target is the selected Target Environment (never a free URL); calls are the planned definitions.</summary>
public sealed record ExecutionRunRequest
{
    public string Name { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public List<ExecutionCallDefinition> Calls { get; init; } = [];
    public CapturePolicy CapturePolicy { get; init; } = CapturePolicy.BoundedBody;
    public Guid? SourceSnapshotId { get; init; }
    public List<string> ContractRefs { get; init; } = [];
    /// <summary>Legacy flag: the attended session credential. <see cref="Authentication"/> is authoritative when set.</summary>
    public bool UseTargetAuthentication { get; init; }
    /// <summary>How the run authenticates; credentials are resolved and applied in the backend only, never part of the request or the evidence.</summary>
    public ExecutionAuthenticationKind Authentication { get; init; }
    /// <summary>The selected Target Environment's URL and classification. Inputs to the backend safety policy, never the decision.</summary>
    public string TargetUrl { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    public bool IsProduction { get; init; }
    public string? ProfileId { get; init; }
    public string? ContextFingerprint { get; init; }
    /// <summary>A mutating call (POST/PUT/PATCH/DELETE or a GraphQL mutation) runs only when the person confirmed it for this run.</summary>
    public bool ConfirmMutatingCalls { get; init; }
}

public sealed record ExecutionRunStartResult(Guid? RunId, ExecutionState State, string? Message, List<string> Blockers);

public sealed record AttemptComparison(Guid RunA, Guid CallA, int AttemptA, Guid RunB, Guid CallB, int AttemptB, List<string> Differences);
