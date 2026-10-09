using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Integration journeys: a project-neutral model of a business flow that crosses several integration boundaries (submission, transport,
// intake, validation, domain service, publication, downstream consumer). Each step reports its own evidence; a journey is never collapsed into
// one pass because its first boundary accepted a request. Domain packs (project extensions) supply journeys, prerequisites and architecture
// responsibility rules; the generic engine never names a project, system or domain.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Per-step evidence. Configured/Generated/Sent/Accepted/Observed describe what happened; only Verified asserts the expected outcome.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyStepState { Configured, Generated, Sent, Accepted, Observed, Verified, NotVerified, Unavailable, UnexpectedResult, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyReadinessState { Ready, Partial, NotReady, NotConfigured, NotAvailable }

/// <summary>The kind of boundary a step crosses. Transport/technology neutral.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyStepKind
{
    SourceChange, ExternalSubmission, ExternalAcceptance, MessageTransport, PayloadRetrieval, Decryption, StructuralValidation, Mapping,
    DomainIntake, IdentityLookup, DomainState, Publication, BrokerDelivery, ConsumerProcessing, DownstreamVerification,
}

/// <summary>How a journey is executed. Delegated journeys run through another shared engine (e.g. Active Event Testing) and are only summarized here.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyExecutionMode { JourneyRunner, DelegatedToActiveEvent }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyScenarioSupport { Supported, NotAssessed, Unsupported }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyRunState { Running, Completed, Partial, NotVerified, Failed, Blocked, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyPrerequisiteCategory
{
    Integration, TrustedEnvironment, Authentication, Authorization, Contract, SourceEvidence, SyntheticData, Observation, DownstreamVerification, Trigger, Executor,
}

/// <summary>An integration the journey needs, resolved against the Integration Quality Review catalog — never configured by the journey itself.</summary>
public sealed record JourneyIntegrationRequirement(string Key, string Label, IntegrationKind Kind, string Producer, string Consumer, string Purpose);

/// <summary>One step of a journey. <see cref="Owner"/> is the component the architecture assigns the responsibility to.</summary>
public sealed record JourneyStepDefinition(
    string StepId,
    string Label,
    JourneyStepKind Kind,
    string Owner,
    string ExpectedEvidence,
    bool Mandatory = true,
    string? IntegrationKey = null,
    string? FromComponent = null,
    string? ToComponent = null);

public sealed record JourneyScenarioDescriptor(
    string ScenarioId,
    string DisplayName,
    string Description,
    JourneyScenarioSupport Support,
    string? NotAssessedBecause = null,
    bool StateChanging = true);

public sealed record IntegrationJourneyDefinition
{
    public string PackId { get; init; } = "";
    public string JourneyId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public int Order { get; init; }
    public JourneyExecutionMode ExecutionMode { get; init; }
    /// <summary>For delegated journeys: the Active Event provider (extension) id that executes it.</summary>
    public string? DelegatedProviderId { get; init; }
    public IReadOnlyList<JourneyIntegrationRequirement> Integrations { get; init; } = [];
    public IReadOnlyList<JourneyStepDefinition> Steps { get; init; } = [];
    public IReadOnlyList<JourneyScenarioDescriptor> Scenarios { get; init; } = [];
    /// <summary>Where the journey design comes from (specification, documented diagram, source). Evidence maturity, not proof.</summary>
    public string EvidenceBasis { get; init; } = "";
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed record JourneyPrerequisite(string Key, string Label, JourneyPrerequisiteCategory Category, JourneyReadinessState State, string Detail, bool Mandatory = true);

/// <summary>Separate maturity levels: a configured integration is not source verified, executable or runtime verified.</summary>
public sealed record JourneyMaturity(bool Configured, bool SourceVerified, bool Executable, bool RuntimeVerified, string Detail);

public sealed record JourneyMatchedIntegration(string RequirementKey, string? IntegrationId, string? DisplayName, IntegrationKind Kind, bool Enabled, string Detail);

/// <summary>Current per-step status before/without a run: what is configured, what an observer could see, what a verifier could assert.</summary>
public sealed record JourneyStepStatus(string StepId, bool Configured, bool Observable, bool Verifiable, JourneyStepState State, string Limitation);

public sealed record IntegrationJourneyView
{
    public IntegrationJourneyDefinition Journey { get; init; } = new();
    public JourneyReadinessState Readiness { get; init; } = JourneyReadinessState.NotReady;
    public string ReadinessSummary { get; init; } = "";
    public IReadOnlyList<JourneyPrerequisite> Prerequisites { get; init; } = [];
    public IReadOnlyList<JourneyMatchedIntegration> MatchedIntegrations { get; init; } = [];
    public IReadOnlyList<JourneyStepStatus> StepStatus { get; init; } = [];
    public JourneyMaturity Maturity { get; init; } = new(false, false, false, false, "");
    public bool CanRun { get; init; }
    public IntegrationJourneyRunSummary? LastRun { get; init; }
}

public sealed record IntegrationJourneyPackView
{
    public string PackId { get; init; } = "";
    public string PackVersion { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public bool EnvironmentTrusted { get; init; }
    public string EnvironmentDetail { get; init; } = "";
    public IReadOnlyList<IntegrationJourneyView> Journeys { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Journey ids and boundaries other reviews (Test Coverage, Traceability, Impact Analysis) can reference. Relations are design-level
/// (suggested), never confirmed links.</summary>
public sealed record IntegrationJourneyCatalogEntry(string PackId, string JourneyId, string DisplayName, IReadOnlyList<JourneyEdge> Edges);
public sealed record JourneyEdge(string StepId, string Label, JourneyStepKind Kind, string FromComponent, string ToComponent, string Owner);

// ── Runs ────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record IntegrationJourneyRunRequest
{
    public string EnvironmentId { get; init; } = "";
    public string PackId { get; init; } = "";
    public string JourneyId { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public bool Confirmed { get; init; }
}

public sealed record JourneyStepResult
{
    public string StepId { get; init; } = "";
    public string Label { get; init; } = "";
    public string? IntegrationId { get; init; }
    public JourneyStepState State { get; init; } = JourneyStepState.NotAssessed;
    public string Evidence { get; init; } = "";
    public string Verification { get; init; } = "";
    public string Reason { get; init; } = "";
    /// <summary>Safe correlation reference (run id or a returned test reference) — never a payload or personal identifier.</summary>
    public string? Correlation { get; init; }
    public string EvidenceSource { get; init; } = "";
    public DateTimeOffset? CapturedAt { get; init; }
}

public sealed record IntegrationJourneyRun
{
    public Guid RunId { get; init; }
    public string PackId { get; init; } = "";
    public string JourneyId { get; init; } = "";
    public string JourneyName { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public string ScenarioName { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public IReadOnlyList<JourneyStepResult> Steps { get; init; } = [];
    public JourneyRunState OverallState { get; init; } = JourneyRunState.Running;
    public string StateReason { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed record IntegrationJourneyRunSummary(Guid RunId, string PackId, string JourneyId, string ScenarioId, string EnvironmentId, JourneyRunState State,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int VerifiedSteps, int TotalSteps, int LimitationCount);

public sealed record IntegrationJourneyHistoryQuery(string EnvironmentId, string? PackId = null, string? JourneyId = null, string? ScenarioId = null,
    JourneyRunState? State = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

// ── Architecture responsibility rules ───────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchitectureRuleExpectation { MustDependOn, MustNotDependOn }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchitectureRuleOutcome { Conforms, PotentialDeviation, NotAssessed }

/// <summary>
/// A responsibility rule over a Source Analysis architecture snapshot: "component S must / must not depend on component T". Components are
/// selected by the given name tokens (component, logical name or project); dependencies are code evidence (DI-registered/typed clients,
/// HTTP/GraphQL call wiring), never a name similarity between files.
/// </summary>
public sealed record ArchitectureResponsibilityRule(
    string RuleId,
    string Title,
    string SubjectLabel,
    IReadOnlyList<string> SubjectTokens,
    string TargetLabel,
    IReadOnlyList<string> TargetTokens,
    ArchitectureRuleExpectation Expectation,
    string Rationale);

public sealed record ArchitectureRuleEvidenceItem(string File, int Line, string Symbol, string Explanation);

public sealed record ArchitectureRuleFinding(
    string RuleId,
    string Title,
    string SubjectLabel,
    string TargetLabel,
    ArchitectureRuleExpectation Expectation,
    ArchitectureRuleOutcome Outcome,
    string Detail,
    IReadOnlyList<ArchitectureRuleEvidenceItem> Evidence,
    string Provenance);

public sealed record ArchitectureRuleReport(string PackId, Guid? SourceSnapshotId, string? SourceArchive, IReadOnlyList<ArchitectureRuleFinding> Findings, IReadOnlyList<string> Limitations);
