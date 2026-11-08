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
public enum CaptureCompleteness { ExactRedacted, Truncated, MetadataOnly, Empty, Unavailable,
    /// <summary>The stored body was removed later by its owner; size and SHA-256 of what was stored are kept.</summary>
    Removed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapturePolicy { MetadataOnly, BoundedBody, FullPermittedBody }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssertionKind { StatusCode, Header, JsonPath, ResponseTime, GraphQlErrorCount, GraphQlErrorCode, GraphQlErrorPath, GraphQlDataPath, GraphQlHasData,
    /// <summary>The response matches the contract (REST: the documented schema of its status; GraphQL: the schema's data shape). Expected: "valid".</summary>
    ContractSchema,
    /// <summary>The response body text (Equals / Contains / NotEquals).</summary>
    BodyText,
    /// <summary>An XPath 1.0 expression over an XML response (DTDs prohibited). Target = the expression.</summary>
    XPath }

/// <summary>Contract checks of one call. Enforce: an invalid request is not sent. Record: validated and recorded only (deliberately invalid
/// negative tests). None: no contract check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractCheckMode { None, Enforce, Record }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractValidationState { Valid, Invalid, NotAssessed }

/// <summary>One finding. Tier: Syntax, Type, Schema or Operation (kept apart, never merged into one "valid"). InstancePath points into the
/// request/response ($.items[0].age); SchemaPath into the contract (#/components/schemas/Child/properties/age).</summary>
public sealed record ContractValidationFinding(string Tier, string InstancePath, string SchemaPath, string Keyword, string Message);

/// <summary>The contract check of a request or response against the exact contract version of the call. Unsupported lists schema keywords
/// met but not evaluated, so a pass is never claimed for them.</summary>
public sealed record ContractValidationReport(string Direction, ContractValidationState State, string? ContractRef, string? SchemaRef, string Summary,
    List<ContractValidationFinding> Findings, List<string> Unsupported);

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

/// <summary>
/// What authentication did for one logical call. NotApplicable: the run is anonymous. Authenticated: the run's credential was applied and the
/// target did not reject it. AnonymousByDesign: a negative test sent without the credential on purpose. BlockedForAuthentication: not sent because
/// the credential could not be applied (expired session not renewed in time, identity changed, token refused) — never sent anonymously instead.
/// FailedAuthentication: sent with the credential and the target answered 401 (or GraphQL UNAUTHENTICATED). AuthorizationDenied: 403 (or FORBIDDEN).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CallAuthenticationOutcome { NotApplicable, Authenticated, AnonymousByDesign, BlockedForAuthentication, FailedAuthentication, AuthorizationDenied }

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
    /// <summary>Which credential instance was applied ("attended-session:{env}#g2", "api-credential:{env}:ClientCredentials:v1#t3") — never a value.</summary>
    public string? AuthenticationSession { get; init; }
}

public sealed record AssertionDefinition(string Id, AssertionKind Kind, AssertionOperator Operator, string? Target = null, string? Expected = null);

/// <summary>Where a chained call's output is read: a JSON path into the response body, a path into the GraphQL <c>data</c> object, or a
/// (non-sensitive) response header name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputSource { JsonBody, GraphQlData, Header }

/// <summary>An output a later step may use: <see cref="Name"/> is referenced as <c>{{steps.&lt;step&gt;.&lt;name&gt;}}</c>. <see cref="Type"/> is the
/// expected JSON kind (String, Integer, Number, Boolean, Object, Array) or null for any.</summary>
public sealed record OutputExtraction(string Name, OutputSource Source, string Path, string? Type = null);

/// <summary>An output as extracted from a received response: found or not (with the reason), its kind and a redacted, bounded preview.</summary>
public sealed record ExtractedOutput(string Name, OutputSource Source, string Path, string? Type, bool Found, string? Preview, string? Problem);

/// <summary>
/// One input value as it was resolved for a call: its name, exact Test Data type, where it came from ("row r-007 of children@v3", "member of
/// children@v3", "step create → childId") and a redacted, bounded preview. Recorded so the evidence shows which value and type were sent.
/// </summary>
public sealed record ResolvedValue(string Name, string Type, string Source, string? Preview);

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
    /// <summary>Contract validation of the request (before sending) and the response, against <see cref="OperationRef"/>'s contract version.</summary>
    public ContractCheckMode ContractCheck { get; init; }
    /// <summary>Sent without the run's credential on purpose (a negative "missing authentication" test). Never the reverse: a call cannot add a credential.</summary>
    public bool WithoutAuthentication { get; init; }
    /// <summary>The dataset row this logical call executes ("alias@vN#rowId"); null when the test is not data-driven.</summary>
    public string? DatasetRow { get; init; }
    /// <summary>The input values resolved for this call (typed, with their source); previews are redacted and bounded.</summary>
    public List<ResolvedValue> ResolvedValues { get; init; } = [];
    /// <summary>Chained runs: this call's step id (unique in the run), the steps it depends on (all earlier in the run) and the outputs it provides.</summary>
    public string? StepId { get; init; }
    public List<string> DependsOn { get; init; } = [];
    public List<OutputExtraction> Extract { get; init; } = [];
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
    /// <summary>Request and response contract checks (findings with source paths); empty when the call had no contract check.</summary>
    public List<ContractValidationReport> ContractValidation { get; init; } = [];
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public CallAuthenticationOutcome Authentication { get; init; }
    /// <summary>Outputs extracted from the response (chained steps); a required output that is missing fails the call.</summary>
    public List<ExtractedOutput> Outputs { get; init; } = [];
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
    /// <summary>The credential instances used by the run, in first-use order (a renewal adds the next generation; one entry = one session for every call).</summary>
    public List<string> AuthenticationSessions { get; init; } = [];
    /// <summary>How long the run would wait for an expired attended session to be renewed by signing in again (0 = no wait).</summary>
    public int ReauthenticationWaitSeconds { get; init; }
    /// <summary>Distinct test definitions (revisions) the run executed; with <see cref="DatasetRows"/> this tells definitions, variants and logical calls apart.</summary>
    public int Definitions { get; init; }
    /// <summary>Logical calls that executed one dataset row each.</summary>
    public int DatasetRows { get; init; }
    /// <summary>The run stops at the first failed, erroneous or blocked call; the remaining calls are Skipped (never sent).</summary>
    public bool StopOnFirstFailure { get; init; }
    /// <summary>The chain definition revision the run executed ("contract-chain:{id}@r{rev}"), when it is a chained run.</summary>
    public string? ChainRef { get; init; }
    /// <summary>Protected runs are never deleted by bulk deletion or retention; deleting one needs its protection removed first.</summary>
    public bool Protected { get; init; }
}

/// <summary>
/// Local storage of execution evidence. Everything lives in PostgreSQL (AppDbContext tables execution_runs and execution_calls; request and response
/// bodies inside the call documents, bounded by the capture policy); no evidence files exist and exports are generated on demand, never stored.
/// <see cref="StoredBytes"/> is measured (UTF-8 bytes of the stored JSON); <see cref="DatabaseTableBytes"/> is the tables' size on disk as PostgreSQL
/// reports it (with indexes and TOAST), null when not available.
/// </summary>
public sealed record ExecutionStorageUsage(int Runs, int LogicalCalls, int Attempts, long StoredBytes, long? DatabaseTableBytes, int ProtectedRuns,
    DateTimeOffset? Oldest, DateTimeOffset? Newest, int RetentionCandidates, long RetentionCandidateBytes, string Location, string Note);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RetentionMode { Disabled, Days }

/// <summary>Retention of execution evidence. Default Disabled: nothing is ever deleted automatically; with Days, runs older than the period are
/// candidates that a person previews and then deletes. Protected and running runs are never candidates.</summary>
public sealed record ExecutionRetentionSettings(RetentionMode Mode, int? Days, DateTimeOffset? UpdatedAt = null, string? UpdatedBy = null);

/// <summary>A dry run: exactly which runs applying retention now would delete, what they hold, and what is kept.</summary>
public sealed record ExecutionRetentionPreview(ExecutionRetentionSettings Settings, DateTimeOffset? Cutoff, int Runs, int Calls, long Bytes, int ProtectedKept, int RunningKept,
    List<Guid> RunIds, string Explanation);

/// <summary>Bulk deletion: the listed runs, or every eligible (finished, unprotected) run of an environment. <see cref="Confirm"/> must equal the
/// number of runs the person was shown; a different number means the set changed and nothing is deleted.</summary>
public sealed record ExecutionDeletionRequest
{
    public List<Guid> RunIds { get; init; } = [];
    public string? EnvironmentId { get; init; }
    public bool AllEligible { get; init; }
    public int Confirm { get; init; }
}

public sealed record ExecutionDeletionSkip(Guid RunId, string Reason);

public sealed record ExecutionDeletionResult(int Deleted, int Calls, long Bytes, List<ExecutionDeletionSkip> Skipped, string Message);

/// <summary>One entry of the evidence audit trail (deletion, body removal, protection, retention change or apply, export). Never evidence content.</summary>
public sealed record ExecutionAuditEntry(Guid Id, DateTimeOffset At, string Action, string Scope, int Runs, int Calls, long Bytes, string Initiator, string? Detail);

/// <summary>Stored bodies re-hashed against the SHA-256 recorded when they were captured; removed bodies are counted, not checked.</summary>
public sealed record ExecutionIntegrityReport(Guid RunId, int Calls, int BodiesChecked, int BodiesRemoved, List<string> Mismatches)
{
    public bool Intact => Mismatches.Count == 0;
}

public sealed record ExecutionExportRequest
{
    public List<Guid> RunIds { get; init; } = [];
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
    /// <summary>Attended runs: when the session expires mid-run, wait up to this long (0–900 s, once per run) for the tester to sign in again;
    /// the waiting call is then sent with the renewed credential of the same identity. 0 = the remaining calls are Blocked for authentication.</summary>
    public int ReauthenticationWaitSeconds { get; init; }
    /// <summary>Stop at the first failed, erroneous or blocked call; the remaining calls are Skipped and never sent. Default: continue.</summary>
    public bool StopOnFirstFailure { get; init; }
    /// <summary>The chain definition revision, for a chained run (see <see cref="ExecutionRunSummary.ChainRef"/>).</summary>
    public string? ChainRef { get; init; }
}

public sealed record ExecutionRunStartResult(Guid? RunId, ExecutionState State, string? Message, List<string> Blockers);

public sealed record AttemptComparison(Guid RunA, Guid CallA, int AttemptA, Guid RunB, Guid CallB, int AttemptB, List<string> Differences);
