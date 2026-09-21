using System.Text.Json.Serialization;

namespace BirkNext.CriticalE2E;

/// <summary>
/// How a critical flow reaches the system under test.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EExecutionMode
{
    /// <summary>
    /// Attended browser automation. The user signs in and completes MFA in their normal Edge session; from there the
    /// Browser Companion executes the business flow automatically and asserts the visible result. The login is manual —
    /// the flow is not.
    /// </summary>
    CompanionBrowser,
    /// <summary>REST / GraphQL / Event Hub action plus downstream verification. No browser identity, so a pipeline can run it.</summary>
    AutomatedIntegration,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EAuthenticationRequirement
{
    None,
    /// <summary>Reuses an API context BirkNext already holds (Target Environment authentication). Never re-entered here.</summary>
    ExistingApiContext,
    /// <summary>A human must complete sign-in and MFA in the paired browser before the flow can run.</summary>
    ManualBrowserLogin,
}

/// <summary>
/// The outcome of a flow, a step or a single browser command. The distinction that matters most is Failed vs Blocked:
/// Failed means the flow ran and the business result was wrong, Blocked means it never got the chance to be wrong.
/// Reporting a missing prerequisite as a failure is how a release gate loses its meaning.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EStatus
{
    NotRun,
    Running,
    /// <summary>The flow's final business assertion succeeded. Executing every step is not enough.</summary>
    Passed,
    /// <summary>The flow executed and the expected business result was wrong or missing.</summary>
    Failed,
    /// <summary>A prerequisite was unavailable: pairing, page, session, configuration, integration, transport or environment.</summary>
    Blocked,
    Cancelled,
}

// ── Browser automation vocabulary ──────────────────────────────────────────────

/// <summary>
/// Every browser action BirkNext may ask the companion to perform. This is a closed set on purpose: it is a vocabulary,
/// not a scripting language, and nothing in a command is ever executable.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompanionActionKind
{
    Navigate,
    Click,
    Fill,
    Select,
    WaitForVisible,
    WaitForText,
    WaitForRoute,
    AssertVisible,
    AssertHidden,
    AssertText,
    AssertValue,
    AssertRoute,
    ReadValue,
}

/// <summary>
/// How a step names the element it acts on, in the order a flow author should prefer. A test id is stable across copy
/// changes and across the Norwegian/English switch; text is not.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompanionSelectorKind
{
    TestId,
    Role,
    Label,
    Text,
    Css,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompanionTextMatch
{
    Equals,
    Contains,
}

/// <summary>A described element. Never a code fragment, and never an index into a node list.</summary>
public sealed record CompanionSelector
{
    public CompanionSelectorKind Kind { get; init; } = CompanionSelectorKind.TestId;
    /// <summary>The test id, label, text or CSS, depending on <see cref="Kind"/>. Unused for <see cref="CompanionSelectorKind.Role"/>.</summary>
    public string Value { get; init; } = "";
    /// <summary>ARIA role, for <see cref="CompanionSelectorKind.Role"/>.</summary>
    public string? Role { get; init; }
    /// <summary>Accessible name, for <see cref="CompanionSelectorKind.Role"/>.</summary>
    public string? Name { get; init; }

    public string Describe() => Kind switch
    {
        CompanionSelectorKind.Role => $"role={Role}{(string.IsNullOrWhiteSpace(Name) ? "" : $" name=\"{Name}\"")}",
        _ => $"{Kind.ToString().ToLowerInvariant()}=\"{Value}\"",
    };
}

/// <summary>
/// One command's life. The states exist so a heartbeat retry can never turn into a second click: a command leaves
/// Pending exactly once, and every later request for it is answered with the state it already has.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompanionCommandState
{
    /// <summary>Queued by BirkNext, not yet handed to the extension.</summary>
    Pending,
    /// <summary>Handed to the extension worker. It will never be handed out again.</summary>
    Claimed,
    /// <summary>The content script reported that it started.</summary>
    Running,
    Passed,
    Failed,
    Blocked,
    /// <summary>The deadline passed before the extension claimed or completed it. A stale click is never executed.</summary>
    Expired,
    Cancelled,
}

/// <summary>
/// BirkNext → extension. Carries an action NAME and a described element. There is deliberately no field that could hold
/// JavaScript, a URL to fetch, or anything else the page would execute.
/// </summary>
public sealed record CompanionAutomationCommand
{
    public string CommandId { get; init; } = "";
    public string RunId { get; init; } = "";
    public string FlowId { get; init; } = "";
    public string StepId { get; init; } = "";
    /// <summary>The Target Environment this command belongs to. Must equal the paired session's profile.</summary>
    public string ProfileId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    /// <summary>Canonical origin the page must still be on when the command executes.</summary>
    public string TargetOrigin { get; init; } = "";
    /// <summary>Optional page identity (origin + normalized path) when the flow targets one specific page.</summary>
    public string? PageId { get; init; }
    public CompanionActionKind Action { get; init; }
    public CompanionSelector? Selector { get; init; }
    /// <summary>Input for Fill/Select, or the route for Navigate. Never code.</summary>
    public string? Value { get; init; }
    /// <summary>Expected text, value or route for a wait or an assertion.</summary>
    public string? Expected { get; init; }
    public CompanionTextMatch Match { get; init; } = CompanionTextMatch.Equals;
    public int TimeoutMs { get; init; } = 8000;
}

/// <summary>Extension → BirkNext. Observations and an outcome; no page text beyond a short accessible name, no payloads.</summary>
public sealed record CompanionAutomationResult
{
    public string CommandId { get; init; } = "";
    public string StepId { get; init; } = "";
    public CriticalE2EStatus Status { get; init; } = CriticalE2EStatus.NotRun;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public double DurationMs { get; init; }
    /// <summary>Normalized path the page was on when the command finished.</summary>
    public string? ObservedRoute { get; init; }
    /// <summary>For ReadValue and AssertValue: the observed control value, sanitized and length-capped.</summary>
    public string? ObservedValue { get; init; }
    /// <summary>Null when the command performed an action rather than an assertion.</summary>
    public bool? AssertionResult { get; init; }
    /// <summary>Page identity whose Browser Companion evidence covers this step, when one is available.</summary>
    public string? EvidenceReference { get; init; }
    public string? SafeSummary { get; init; }
    public string? SanitizedError { get; init; }
}

/// <summary>Extension → backend envelope for a command outcome, carrying the same session proof as a heartbeat.</summary>
public sealed record CompanionAutomationResultEnvelope
{
    public string SessionId { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string ExtensionVersion { get; init; } = "";
    public CompanionAutomationResult Result { get; init; } = new();
}

/// <summary>Why a command could not be queued. A queue refusal is a Blocked step, never a Failed one.</summary>
public sealed record CompanionCommandDispatchResult
{
    public bool Accepted { get; init; }
    public string CommandId { get; init; } = "";
    public CompanionCommandState State { get; init; } = CompanionCommandState.Pending;
    public string Message { get; init; } = "";
}

// ── Flow definition ────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EIntegrationKind
{
    Http,
    GraphQl,
    EventHub,
    /// <summary>Poll a REST or GraphQL endpoint until the expectation holds. This is where an integration flow earns its PASS.</summary>
    Poll,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EComparison
{
    Equals,
    Contains,
    Exists,
    NotExists,
}

/// <summary>
/// What an integration step observes.
///
/// Deliberately not a JSON path. BirkNext reaches authenticated APIs through the authenticated review gateway, and that
/// gateway does not hand response bodies back — by design, because a body from a target environment is the one thing
/// most likely to carry personal data. Rather than widen that boundary for a test runner, a flow asserts over what the
/// gateway does expose, and the way to assert on business state is to write a query that returns data only when the
/// state holds: a GraphQL query filtered by the run correlation id answers "did it propagate?" with
/// <see cref="CriticalE2EObservable.GraphQlHasData"/> and nothing leaves the environment.
/// </summary>
public sealed record CriticalE2EExpectation
{
    public CriticalE2EObservable Observable { get; init; } = CriticalE2EObservable.StatusCode;
    public CriticalE2EComparison Comparison { get; init; } = CriticalE2EComparison.Equals;
    public string ExpectedValue { get; init; } = "200";

    public string Describe() => $"{Observable} {Comparison} {ExpectedValue}";
}

/// <summary>The facts an authenticated execution exposes to a flow. No body, no headers beyond the safe allow-list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EObservable
{
    StatusCode,
    /// <summary>True when the GraphQL response carried a data payload — the workhorse for "has it arrived downstream?".</summary>
    GraphQlHasData,
    GraphQlErrorCount,
}

public sealed record CriticalE2EStepDefinition
{
    public string StepId { get; init; } = "";
    public string Description { get; init; } = "";

    // Browser steps
    public CompanionActionKind? BrowserAction { get; init; }
    public CompanionSelector? Selector { get; init; }
    /// <summary>Fill/Select input, or the route for Navigate. Supports ${RunId}, ${CorrelationId}, ${Timestamp}, ${RandomGuid}.</summary>
    public string? Value { get; init; }
    public string? Expected { get; init; }
    public CompanionTextMatch Match { get; init; } = CompanionTextMatch.Equals;

    // Integration steps
    public CriticalE2EIntegrationKind? IntegrationAction { get; init; }
    /// <summary>Which configured integration / API target this step uses. BirkNext never re-enters a URL here.</summary>
    public string? IntegrationId { get; init; }
    public string? Method { get; init; }
    /// <summary>Request path, GraphQL operation name, or Event Hub name — resolved against the configured integration.</summary>
    public string? PathOrOperation { get; init; }
    public string? Body { get; init; }
    public CriticalE2EExpectation? Expect { get; init; }

    public int TimeoutMs { get; init; } = 10_000;
    public int? PollIntervalMs { get; init; }

    /// <summary>
    /// The business assertion this flow exists to make. A flow with no final assertion can never pass, because "all the
    /// clicks executed" is not a statement about the system under test.
    /// </summary>
    public bool IsFinalAssertion { get; init; }

    public bool IsBrowserStep => BrowserAction.HasValue;
    public bool IsIntegrationStep => IntegrationAction.HasValue;
}

public sealed record CriticalE2EFlowDefinition
{
    public string Id { get; init; } = "";
    /// <summary>The delivery module this flow covers. Data-driven, so a new module needs no code change.</summary>
    public string Module { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public CriticalE2EExecutionMode Mode { get; init; }
    public string ProfileId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public bool Enabled { get; init; } = true;
    /// <summary>Counts toward the delivery requirement and can block a release.</summary>
    public bool RequiredForRelease { get; init; }
    /// <summary>
    /// Plain statement of where automation starts and stops, e.g. "Event Hub → M2LB; BiRK → Debezium is not automated".
    /// Shown with every result, because a green flow that silently skips half the chain is worse than no flow.
    /// </summary>
    public string AutomationBoundary { get; init; } = "";
    public CriticalE2EAuthenticationRequirement AuthenticationRequirement { get; init; }
    public int TimeoutMs { get; init; } = 120_000;
    public int PollingIntervalMs { get; init; } = 1_000;
    public List<CriticalE2EStepDefinition> Steps { get; init; } = [];
    /// <summary>How the flow obtains and disposes of test data. Synthetic only.</summary>
    public string TestDataPolicy { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public int Version { get; init; } = 1;

    public bool HasFinalAssertion => Steps.Any(s => s.IsFinalAssertion);

    /// <summary>
    /// Why this flow cannot run, or null when it can. Lives on the definition rather than in either the backend or the
    /// editor, because the two answering differently is how a flow gets saved that can never pass.
    /// </summary>
    public string? ConfigurationProblem()
    {
        if (string.IsNullOrWhiteSpace(Module)) return "No module is assigned.";
        if (string.IsNullOrWhiteSpace(Name)) return "The flow has no name.";
        if (Steps.Count == 0) return "The flow has no steps.";
        // Without this, a flow can only ever report that its clicks executed — which is not a statement about M2LB.
        if (!HasFinalAssertion) return "The flow has no final business assertion.";
        if (string.IsNullOrWhiteSpace(ProfileId)) return "No Target Environment is selected.";
        if (Mode == CriticalE2EExecutionMode.CompanionBrowser && Steps.Any(s => s.IsIntegrationStep))
            return "A companion browser flow cannot contain integration steps.";
        if (Mode == CriticalE2EExecutionMode.AutomatedIntegration && Steps.Any(s => s.IsBrowserStep))
            return "An automated integration flow cannot contain browser steps.";
        if (Steps.Any(s => !s.IsBrowserStep && !s.IsIntegrationStep)) return "A step has no action.";
        if (Steps.Any(s => s.IsBrowserStep && s.BrowserAction != CompanionActionKind.Navigate
                && s.BrowserAction != CompanionActionKind.AssertRoute && s.BrowserAction != CompanionActionKind.WaitForRoute
                && string.IsNullOrWhiteSpace(s.Selector?.Value) && string.IsNullOrWhiteSpace(s.Selector?.Role)))
            return "A browser step has no element to act on.";
        return null;
    }
}

// ── Results ────────────────────────────────────────────────────────────────────

public sealed record CriticalE2EStepResult
{
    public string StepId { get; init; } = "";
    public string Description { get; init; } = "";
    public CriticalE2EStatus Status { get; init; } = CriticalE2EStatus.NotRun;
    public bool IsFinalAssertion { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public double DurationMs { get; init; }
    public string? SafeSummary { get; init; }
    public string? SanitizedError { get; init; }
    public string? ObservedRoute { get; init; }
    public string? ObservedValue { get; init; }
    public string? EvidenceReference { get; init; }
}

public sealed record CriticalE2ERunResult
{
    public string RunId { get; init; } = "";
    public string FlowId { get; init; } = "";
    public string FlowName { get; init; } = "";
    public string Module { get; init; } = "";
    public CriticalE2EExecutionMode Mode { get; init; }
    public string ProfileId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double DurationMs { get; init; }
    public CriticalE2EStatus Status { get; init; } = CriticalE2EStatus.NotRun;
    public string CorrelationId { get; init; } = "";
    public List<CriticalE2EStepResult> StepResults { get; init; } = [];
    /// <summary>One sentence, already sanitized. Never a stack trace, a payload or a token.</summary>
    public string? FailureReason { get; init; }
    public string AutomationBoundary { get; init; } = "";
    public bool RequiredForRelease { get; init; }
    public string? BuildId { get; init; }
    public string? ReleaseId { get; init; }
    public string? CommitSha { get; init; }
    public List<string> EvidenceReferences { get; init; } = [];
    public Dictionary<string, string> SafeDiagnosticMetadata { get; init; } = new();

    public int PassedSteps => StepResults.Count(s => s.Status == CriticalE2EStatus.Passed);
    public int TotalSteps => StepResults.Count;
}

/// <summary>Correlation ids are generated centrally so every flow and every log line spells them the same way.</summary>
public static class CriticalE2ECorrelation
{
    public const string Prefix = "M2LB-E2E";
    /// <summary>Also the required prefix for synthetic test data, so a stray record is recognisable at a glance.</summary>
    public const string TestDataPrefix = "M2LB-E2E-";

    public static string New(DateTimeOffset now) => $"{Prefix}-{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
}

/// <summary>
/// Environments a critical flow may be executed against. Production is absent, and that is the point: browser
/// automation against production is out of scope for V1 and the gate is enforced in the backend, the extension worker
/// and the content script rather than by hiding a button.
/// </summary>
public static class CriticalE2EEnvironmentPolicy
{
    private static readonly string[] Automatable = ["Local", "Development", "QA", "Test", "RC"];

    public static bool AllowsAutomation(string? environmentType) =>
        environmentType is not null && Automatable.Contains(environmentType.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>An unknown environment is treated as production. Guessing in the permissive direction is how a probe ends up clicking in production.</summary>
    public static string BlockedReason(string? environmentType) =>
        string.IsNullOrWhiteSpace(environmentType)
            ? "Environment type is unknown, so automation is not permitted."
            : $"Automation is not permitted against a {environmentType} environment.";
}

// ── Coverage and release aggregation ───────────────────────────────────────────

public sealed record CriticalE2EModuleCoverage
{
    public string Module { get; init; } = "";
    public List<CriticalE2EFlowSummary> Flows { get; init; } = [];
    public bool Covered => Flows.Any(f => f.RequiredForRelease && f.Enabled);
}

public sealed record CriticalE2EFlowSummary
{
    public string FlowId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Module { get; init; } = "";
    public CriticalE2EExecutionMode Mode { get; init; }
    public bool Enabled { get; init; }
    public bool RequiredForRelease { get; init; }
    public bool Configured { get; init; }
    /// <summary>Why the flow is not configured, when it is not. Empty otherwise.</summary>
    public string? ConfigurationProblem { get; init; }
    public CriticalE2EStatus LastStatus { get; init; } = CriticalE2EStatus.NotRun;
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastBuildId { get; init; }
    /// <summary>False when the most recent result belongs to a different build than the one being validated.</summary>
    public bool LastResultMatchesRelease { get; init; }
}

/// <summary>The release verdict. Incomplete and Blocked are different answers and must not be merged into "not ready".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EReleaseDisposition
{
    /// <summary>Every required flow passed against this build.</summary>
    Ready,
    /// <summary>A required flow has not run against this build yet. Nothing is known to be wrong.</summary>
    Incomplete,
    /// <summary>A required flow failed, or a prerequisite prevented one from running.</summary>
    Blocked,
    /// <summary>No required flows are configured, so the gate says nothing at all.</summary>
    NotConfigured,
}

public sealed record CriticalE2EReleaseStatus
{
    public string? BuildId { get; init; }
    public string? ReleaseId { get; init; }
    public string EnvironmentId { get; init; } = "";
    public CriticalE2EReleaseDisposition Disposition { get; init; } = CriticalE2EReleaseDisposition.NotConfigured;
    /// <summary>Modules with at least one enabled, required flow — the delivery requirement's definition coverage.</summary>
    public int ModulesCovered { get; init; }
    public int ModulesTotal { get; init; }
    public int RequiredFlowsPassed { get; init; }
    public int RequiredFlowsTotal { get; init; }
    public int RequiredFlowsPending { get; init; }
    public int RequiredFlowsFailed { get; init; }
    public int RequiredFlowsBlocked { get; init; }
    public string Summary { get; init; } = "";
}

// ── Page contracts (BirkNext UI ⇄ backend) ─────────────────────────────────────

/// <summary>
/// Whether a transport can run something right now. Deliberately separate from whether a flow passed: a ready engine
/// with no flows says nothing about quality, and a blocked engine says nothing about the application.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EEngineState
{
    Ready,
    /// <summary>The companion is not paired, not connected, or not on an approved page. The user can fix this; nothing is broken.</summary>
    RequiresBrowserSession,
    /// <summary>Nothing has been configured for this transport yet.</summary>
    NotConfigured,
    /// <summary>Configured, but this environment cannot be automated — production, or an unknown environment type.</summary>
    Unavailable,
}

public sealed record CriticalE2EEngineStatus
{
    public CriticalE2EEngineState State { get; init; } = CriticalE2EEngineState.NotConfigured;
    public string Message { get; init; } = "";
    /// <summary>What the user should do next, when there is something to do. Empty when the engine is ready.</summary>
    public string? Action { get; init; }
    public bool Ready => State == CriticalE2EEngineState.Ready;
}

/// <summary>UI → backend. Everything the backend needs that lives in the frontend's environment configuration.</summary>
public sealed record CriticalE2EOverviewRequest
{
    public string ProfileId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public string? EnvironmentType { get; init; }
    public string? BuildId { get; init; }
    public string? ReleaseId { get; init; }
    public string? CommitSha { get; init; }
    /// <summary>Delivery modules this environment is expected to cover. Data-driven so a new module needs no code change.</summary>
    public List<string> Modules { get; init; } = [];
    /// <summary>How BirkNext reaches authenticated APIs for this environment. Never a credential.</summary>
    public string? AuthenticationMethod { get; init; }
    public string? ContextFingerprint { get; init; }
}

public sealed record CriticalE2EOverview
{
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public CriticalE2EReleaseStatus Release { get; init; } = new();
    public List<CriticalE2EModuleCoverage> Modules { get; init; } = [];
    public List<CriticalE2EFlowSummary> Flows { get; init; } = [];
    public CriticalE2EEngineStatus BrowserEngine { get; init; } = new();
    public CriticalE2EEngineStatus IntegrationEngine { get; init; } = new();
    public List<CriticalE2ERunResult> History { get; init; } = [];
}

public sealed record CriticalE2ERunFlowRequest
{
    public CriticalE2EOverviewRequest Context { get; init; } = new();
    /// <summary>Run one flow. Empty runs every enabled flow of <see cref="Mode"/>.</summary>
    public string? FlowId { get; init; }
    public CriticalE2EExecutionMode? Mode { get; init; }
}

public sealed record CriticalE2ERunBatchResult
{
    public List<CriticalE2ERunResult> Runs { get; init; } = [];
    public CriticalE2EOverview Overview { get; init; } = new();
    public string Message { get; init; } = "";
}
