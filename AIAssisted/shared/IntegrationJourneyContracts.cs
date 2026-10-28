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
    SourceChange, ExternalAuthentication, ExternalSubmission, ExternalAcceptance, MessageTransport, PayloadRetrieval, Decryption, StructuralValidation, Mapping,
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
public sealed record JourneyIntegrationRequirement(string Key, string Label, IntegrationKind Kind, string Producer, string Consumer, string Purpose)
{
    /// <summary>The canonical identity the requirement resolves through (logical/technical names, aliases, channel, source resource, contract
    /// identity). Null for packs that still match by their own rule only.</summary>
    public IntegrationRequirementIdentity? Identity { get; init; }
}

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
    bool StateChanging = true,
    IReadOnlyList<JourneyScenarioInput>? Inputs = null,
    IReadOnlyList<string>? ExecutionEffects = null,
    JourneyScenarioCapabilityProfile? Capability = null);

/// <summary>
/// Whether a scenario can be executed now, separately from whether its result can be verified. Runnable = every applicable mandatory
/// prerequisite is ready; ConditionallyRunnable = only configuration is missing; OperatorRequired = a human operator must act in the target
/// system (BirkNext never does it); NotAssessed = the scenario is not supported yet; Blocked = a non-configuration prerequisite is not met.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyScenarioCapabilityStatus { Runnable, ConditionallyRunnable, OperatorRequired, NotAssessed, Blocked }

/// <summary>How far BirkNext can verify a scenario's result once it ran. Independent of whether it can run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyResultVerification { Verifiable, Partial, NotVerifiable }

/// <summary>What a scenario needs and who can close its gaps, as declared by the pack. Text only: never identifiers, secrets or endpoints with credentials.</summary>
public sealed record JourneyScenarioCapabilityProfile(
    string System,
    string ExecutionMethod,
    string RequiredConfiguration,
    string RequiredTestData,
    string RequiredAccess,
    JourneyResultVerification Verification,
    string VerificationDetail,
    string MissingEvidence,
    string ResponsibleTeam,
    string NextAction,
    bool OperatorRequired = false);

/// <summary>One scenario's capability in this environment: its status from current readiness plus the pack's declared profile.</summary>
public sealed record JourneyScenarioCapability(
    string ScenarioId,
    string DisplayName,
    string? Resource,
    JourneyScenarioCapabilityStatus Status,
    string StatusDetail,
    IReadOnlyList<string> Blockers,
    JourneyScenarioCapabilityProfile? Profile);

/// <summary>A pack-level evidence limitation shown next to the scenarios (e.g. a missing source repository). Never a pass.</summary>
public sealed record JourneyEvidenceLimitation(string Key, string Title, string Status, string Detail, string Owner);

/// <summary>Kinds of typed scenario input. There is deliberately no free-form payload, URL or endpoint kind.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyScenarioInputKind { Choice, Date, Number, SyntheticPersonIdentifier, Reference }

public sealed record JourneyScenarioInputOption(string Value, string Label);

/// <summary>
/// One typed input a scenario asks the tester for. The pack validates and normalizes every value server-side before anything runs; values
/// are held in memory for the run only and never persisted — history keeps the pack's redacted summary instead. <c>Default</c> is a value
/// the pack resolved from an approved source (one of the options, e.g. the only approved reporter alias); the tester can still change it.
/// </summary>
public sealed record JourneyScenarioInput(
    string Key,
    string Label,
    JourneyScenarioInputKind Kind,
    bool Required,
    string Help = "",
    IReadOnlyList<JourneyScenarioInputOption>? Options = null,
    string? Default = null);

public sealed record IntegrationJourneyDefinition
{
    public string PackId { get; init; } = "";
    public string JourneyId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>The journey's documented component chain for display (e.g. "A → B → C"); null derives it from the steps' From/To components.</summary>
    public string? Flow { get; init; }
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

/// <summary>A readiness condition. <c>Scenarios</c> limits it to those scenario ids (e.g. only state-changing submissions need a
/// complete submission contract); null applies it to every scenario of the journey.</summary>
public sealed record JourneyPrerequisite(string Key, string Label, JourneyPrerequisiteCategory Category, JourneyReadinessState State, string Detail, bool Mandatory = true,
    IReadOnlyList<string>? Scenarios = null)
{
    public bool AppliesTo(string scenarioId) => Scenarios is null || Scenarios.Contains(scenarioId, StringComparer.Ordinal);
    /// <summary>Where the value was resolved from (e.g. "Target Environment", "Integration Quality Review", "Local execution config",
    /// "Approved test data", "Live Altinn metadata"). A source, never a value: secrets and identities are not named here.</summary>
    public string? Source { get; init; }
    /// <summary>Safe diagnostic facts behind the state (identities compared, candidates, the exact reason), shown on request — never secrets.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>Separate maturity levels: a configured integration is not source verified, executable or runtime verified.</summary>
public sealed record JourneyMaturity(bool Configured, bool SourceVerified, bool Executable, bool RuntimeVerified, string Detail);

public sealed record JourneyMatchedIntegration(string RequirementKey, string? IntegrationId, string? DisplayName, IntegrationKind Kind, bool Enabled, string Detail)
{
    /// <summary>Matched, or found with a mapping still incomplete ("Integration found · mapping incomplete") — never "missing" when found.</summary>
    public IntegrationMatchState MatchState { get; init; }
    public IReadOnlyList<string> MatchedOn { get; init; } = [];
    public IReadOnlyList<string> Unresolved { get; init; } = [];
}

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
    /// <summary>Supported scenarios whose applicable mandatory prerequisites are all ready.</summary>
    public IReadOnlyList<string> RunnableScenarios { get; init; } = [];
    /// <summary>Every scenario with its execution status and declared verification capability (for a delegated journey, its provider's catalog).</summary>
    public IReadOnlyList<JourneyScenarioCapability> ScenarioCapabilities { get; init; } = [];
    public IntegrationJourneyRunSummary? LastRun { get; init; }
    /// <summary>Separate readiness per dimension (integration, contracts, source bindings, infrastructure, environment, Test Data, test evidence,
    /// execution, verification, optional delivery), each composed from the shared evidence owner it names — never collapsed into one score.</summary>
    public IReadOnlyList<JourneyReadinessDimension> Dimensions { get; init; } = [];
    /// <summary>What the last run established, kept apart from readiness: executed ≠ verified.</summary>
    public JourneyAssessmentState Assessment { get; init; } = JourneyAssessmentState.NotRun;
    /// <summary>Archives of the source snapshots IQR binds to this journey's integrations (identity only), so Impact Analysis can name
    /// journeys whose canonical source changed without owning journey logic.</summary>
    public IReadOnlyList<string> SourceArchives { get; init; } = [];
}

/// <summary>The readiness dimensions of a journey. Generic: a pack declares requirements; the journey service composes shared evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyDimension { Integration, Contracts, SourceBindings, Infrastructure, EnvironmentConfiguration, TestData, TestEvidence, Execution, Verification, Delivery }

/// <summary>One dimension's state. Declared/available evidence is never "Ready" for a runtime fact: infrastructure declared in source is Partial.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyDimensionState { Ready, Partial, ReviewRequired, Missing, Blocked, NotAssessed }

/// <summary>The last run's outcome as an assessment: not run, executed but not verified, partially verified, verified, failed or blocked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JourneyAssessmentState { NotRun, ExecutedNotVerified, Partial, Verified, Failed, Blocked }

/// <summary>
/// One readiness dimension of a journey: its state, the evidence it was composed from (counts and states, never values), and the feature that
/// owns the fix (<see cref="OwnerKey"/>: integrations, execution-settings, iqr-evidence, source-contracts, source-infrastructure, test-data,
/// test-coverage, pipeline-review, verify, local-execution). An optional dimension never blocks a journey.
/// </summary>
public sealed record JourneyReadinessDimension(JourneyDimension Dimension, string Label, JourneyDimensionState State, string Evidence, string Owner, string OwnerKey, bool Optional = false)
{
    /// <summary>Supporting facts (one line each), e.g. "Producer contract: Available", "Runtime validation: Not assessed".</summary>
    public IReadOnlyList<string> Items { get; init; } = [];
    /// <summary>The shared evidence family the dimension was read from ("Contract & Schema Evidence", "Test Data catalog" …).</summary>
    public string Provenance { get; init; } = "";
}

/// <summary>A source snapshot a run's readiness was assessed against (identity only).</summary>
public sealed record JourneyRunSourceReference(Guid SnapshotId, string Archive, DateTimeOffset AnalyzedAt);

/// <summary>The exact evidence context a run started with — kept immutable with the run, so history never re-reads current evidence.</summary>
public sealed record JourneyRunEvidenceContext
{
    public string PackVersion { get; init; } = "";
    public IReadOnlyList<JourneyRunSourceReference> SourceSnapshots { get; init; } = [];
    /// <summary>Test Data versions (alias + fingerprint) the run could only have used — deterministic resolutions; never a value.</summary>
    public IReadOnlyList<BirkNext.TestData.TestDataUsage> TestData { get; init; } = [];
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
    /// <summary>Evidence limitations the pack derives from current readiness and its declared knowledge (shown with the scenario capabilities).</summary>
    public IReadOnlyList<JourneyEvidenceLimitation> EvidenceLimitations { get; init; } = [];
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
    /// <summary>Typed scenario inputs by <see cref="JourneyScenarioInput.Key"/>. Unknown keys are refused; nothing here is persisted.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();
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
    /// <summary>Safe, labelled facts behind the evidence (e.g. a task id or a masked reference) — never payloads or personal identifiers.</summary>
    public IReadOnlyList<JourneyEvidenceFact> Facts { get; init; } = [];
    /// <summary>A redacted payload evidence snapshot of what this step generated/sent — never the unredacted payload (optional; runs recorded
    /// before it existed have none).</summary>
    public JourneyPayloadCapture? Payload { get; init; }
}

public sealed record JourneyEvidenceFact(string Label, string Value);

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
    /// <summary>Redacted description of the run's inputs (e.g. a hashed synthetic identifier and the period) — never the values themselves.</summary>
    public IReadOnlyList<string> InputSummary { get; init; } = [];
    /// <summary>The BirkNext execution context that started the run: <c>local-session:loopback</c> or <c>authenticated-user:&lt;hash&gt;</c>.
    /// Never an e-mail, name, raw subject, token or the synthetic TestID reporter (who is an Altinn actor, not a BirkNext user).</summary>
    public string? InitiatedBy { get; init; }
    /// <summary>For a read-only message verification run: its sanitized evidence, correlations and assertions (null for every other run).</summary>
    public MessageVerificationRecord? MessageVerification { get; init; }
    /// <summary>The source snapshots and Test Data versions the run started with (null for runs recorded before this was kept).</summary>
    public JourneyRunEvidenceContext? EvidenceContext { get; init; }
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
    string Rationale)
{
    /// <summary>The logical source (a pack source role key) whose canonical binding the rule is assessed against. Null: any bound source of the pack.</summary>
    public string? SourceRole { get; init; }
}

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

public sealed record ArchitectureRuleReport(string PackId, Guid? SourceSnapshotId, string? SourceArchive, IReadOnlyList<ArchitectureRuleFinding> Findings, IReadOnlyList<string> Limitations)
{
    /// <summary>Per rule: the logical source it needs and what is bound/available for it (rules may need different sources).</summary>
    public IReadOnlyList<ArchitectureRuleSource> RuleSources { get; init; } = [];
}

/// <summary>The source a rule was (or could not be) assessed against: its required logical source, the bound snapshot used, and an available
/// but unbound snapshot that would provide it (never used).</summary>
public sealed record ArchitectureRuleSource(string RuleId, string? SourceRole, string? SourceRoleLabel, string? BoundArchive, string? AvailableArchive, TestPackSourceState State);

// ── Logical source roles ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A logical source a test pack needs (a repository / application / system), identified by key — never by a file name. Its canonical evidence is
/// the IQR binding of the pack integrations listed in <see cref="IntegrationRequirementKeys"/> (empty: any binding of the pack). A snapshot is
/// recognized as this source deterministically by <see cref="ComponentTokens"/> (component, project or API scope names) and, for an API-described
/// source, by an API document. Archive names are provenance only.
/// </summary>
public sealed record TestPackSourceRole(string Key, string Label, string Description)
{
    public IReadOnlyList<string> IntegrationRequirementKeys { get; init; } = [];
    public IReadOnlyList<string> ComponentTokens { get; init; } = [];
    /// <summary>Component/project words that prove a snapshot is ANOTHER source (e.g. a receiving service whose adapter project carries the
    /// sender's name): a snapshot containing one is never recognized as this source.</summary>
    public IReadOnlyList<string> ExcludedComponentTokens { get; init; } = [];
    /// <summary>True when the source is recognized by an API document (OpenAPI / application metadata) rather than by components.</summary>
    public bool RequiresApiDocument { get; init; }
    /// <summary>For an API-described source: semantic keys of which the named document must declare at least one (what makes it THAT kind of
    /// application, e.g. an app route or process task) — a document that only mentions a token in its title is not the source. Empty: any.</summary>
    public IReadOnlyList<string> ApiDocumentKeys { get; init; } = [];
    /// <summary>What a tester would import ("Repository source archive", "OpenAPI / Swagger document").</summary>
    public string ExpectedArtifact { get; init; } = "Source archive";
    /// <summary>Plain-language capabilities ("Meldingsutvekslermottak", "Altinn webhook") shown as "Used for".</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> Journeys { get; init; } = [];
    /// <summary>Whether the pack cannot be tested without this source (Required), can be (Optional), or needs it only under a stated condition.</summary>
    public TestPackSourceRequirement Requirement { get; init; } = TestPackSourceRequirement.Required;
    /// <summary>For Conditional: when the source is needed. For Required/Optional: an optional plain-language scope ("for application metadata").</summary>
    public string? RequirementCondition { get; init; }
    /// <summary>True when the source is recognized by Infrastructure as Code evidence (any format — Terraform, Bicep, ARM, Kubernetes, Helm),
    /// optionally narrowed by <see cref="ComponentTokens"/>. The logical capability is "Infrastructure as Code", never one technology.</summary>
    public bool RequiresInfrastructureAsCode { get; init; }
    /// <summary>False when the source is read as workspace evidence directly: no IQR binding is needed for it to count.</summary>
    public bool BindingRequired { get; init; } = true;
    /// <summary>Who provides the source: an imported Source Analysis snapshot (default), or reference metadata the test pack ships with BirkNext.
    /// A Test Pack reference is never imported, never a snapshot and never an IQR binding; its states are its own (available / unavailable).</summary>
    public TestPackSourceProvider Provider { get; init; } = TestPackSourceProvider.ImportedSource;
}

/// <summary>ImportedSource: a Source Analysis snapshot the tester imports (matched by content, bound in IQR). TestPackReference: metadata the test
/// pack provides itself (e.g. a built-in OpenAPI reference) — supports configuration and test planning, never canonical evidence or runtime truth.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestPackSourceProvider { ImportedSource, TestPackReference }

/// <summary>One fact a Test Pack reference declares: a label, the value and the generic semantic key it supplies (when it supplies one).</summary>
public sealed record TestPackReferenceFact(string Label, string Value)
{
    public string? SemanticKey { get; init; }
    /// <summary>For a fact composed from other facts of the same document (e.g. a base URL = a server + the app route): their semantic keys, in
    /// order. Empty: the document declares the fact itself.</summary>
    public IReadOnlyList<string> DerivedFrom { get; init; } = [];
}

/// <summary>A Test Pack reference as loaded: what it is, the safe facts it declares, its provenance and what it does not prove. When it cannot
/// be loaded, <see cref="Diagnostics"/> says why (never a file-system path in the normal view).</summary>
public sealed record TestPackReferenceView(string Kind, string Title)
{
    public IReadOnlyList<TestPackReferenceFact> Facts { get; init; } = [];
    /// <summary>Document version facts (e.g. "OpenAPI 3.0.4", "info.version 8.12.7.264"), shown as provenance only.</summary>
    public IReadOnlyList<string> Provenance { get; init; } = [];
    public string? Fingerprint { get; init; }
    public string Limitation { get; init; } = "";
    /// <summary>What the reference does not prove (deployment, runtime health, credentials, reporter, execution …).</summary>
    public IReadOnlyList<string> NotProven { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>Required: the pack's journeys need it. Optional: adds evidence, never blocks. Conditional: needed only when <see cref="TestPackSourceRole.RequirementCondition"/> applies.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestPackSourceRequirement { Required, Optional, Conditional }

/// <summary>ReferenceAvailable / ReferenceUnavailable = a Test Pack reference loaded / could not be loaded (never an imported-source state).
/// Bound = a canonical binding provides the source; AvailableNotBound = a stored snapshot is recognized as the source but no binding
/// uses it (never authoritative); Missing = no snapshot is recognized as a required source; ReviewRequired = the bound snapshot is not recognized
/// as this source (wrong source context) or several snapshots of different content match and none is bound; Available = recognized, and the
/// source needs no binding; NotProvided = an optional or conditional source that no snapshot provides (never a failure).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestPackSourceState { Bound, AvailableNotBound, Missing, ReviewRequired, Available, NotProvided, ReferenceAvailable, ReferenceUnavailable }

/// <summary>One stored snapshot recognized as a logical source. Archive name and fingerprint are provenance; the snapshot id is the identity.</summary>
public sealed record TestPackSourceCandidate(Guid SnapshotId, string ArchiveFileName, string Fingerprint, DateTimeOffset AnalyzedAt)
{
    /// <summary>True when this snapshot is the canonical IQR binding that makes the source authoritative.</summary>
    public bool Bound { get; init; }
    /// <summary>The Project Import that created the snapshot, if any.</summary>
    public string? ImportId { get; init; }
    /// <summary>What recognized it, e.g. "Infrastructure as Code: Terraform" (technologies only, never source text).</summary>
    public IReadOnlyList<string> Technologies { get; init; } = [];
    /// <summary>Why it was recognized: the component, project, configuration or API names that matched the role (names only, bounded).</summary>
    public IReadOnlyList<string> MatchedOn { get; init; } = [];
    /// <summary>The snapshot's Source Analysis status ("Ready", "Partial" …); provenance only.</summary>
    public string? AnalysisStatus { get; init; }
}

/// <summary>A stored snapshot whose name or repository suggests the role (a hint only) but whose content is not recognized as it — shown so
/// "Missing" can say the package was seen and why it does not count. Never counted as the source.</summary>
public sealed record TestPackSourceNearMiss(Guid SnapshotId, string ArchiveFileName, string Reason);

public sealed record TestPackSourceStatus(TestPackSourceRole Role, TestPackSourceState State)
{
    public IReadOnlyList<string> BoundArchives { get; init; } = [];
    public IReadOnlyList<string> AvailableArchives { get; init; } = [];
    /// <summary>Configured integrations whose binding supplies (or would supply) this source.</summary>
    public IReadOnlyList<string> Integrations { get; init; } = [];
    public string Detail { get; init; } = "";
    public string NextAction { get; init; } = "";
    /// <summary>Where the next action is done: "source-analysis", "iqr-evidence", "target-environment-integrations", "target-environment" or "import-project".</summary>
    public string? NextActionTarget { get; init; }
    /// <summary>Every stored snapshot recognized as this source (bound first, then newest first). Never chosen from: only a binding is authoritative.</summary>
    public IReadOnlyList<TestPackSourceCandidate> Candidates { get; init; } = [];
    /// <summary>Snapshots named like the source that are not recognized as it, with the reason (explanation only; never affects the state).</summary>
    public IReadOnlyList<TestPackSourceNearMiss> NearMisses { get; init; } = [];
    /// <summary>The Test Pack reference (provider TestPackReference only); imported snapshots recognized as the same source stay in Candidates as
    /// additional imported evidence and never replace it.</summary>
    public TestPackReferenceView? Reference { get; init; }
}

/// <summary>A test pack's logical sources and their state in one environment — what Import Project shows before asking for another upload.</summary>
public sealed record TestPackSourceSetup(string PackId, string DisplayName, IReadOnlyList<TestPackSourceStatus> Sources)
{
    /// <summary>Journey id → display name, so a source can say which journeys use it ("Used by: Rapportmottak").</summary>
    public IReadOnlyDictionary<string, string> JourneyNames { get; init; } = new Dictionary<string, string>();
}

/// <summary>One stored Source Analysis snapshot as the source setup sees it: provenance, extraction status and the IQR integrations bound to it
/// in the requested environment. Read-only metadata; never source content.</summary>
public sealed record SourceSetupSnapshot(Guid SnapshotId, string ArchiveFileName, string Fingerprint, DateTimeOffset AnalyzedAt, string Status)
{
    public string? ImportId { get; init; }
    public string? Repository { get; init; }
    public IReadOnlyList<string> BoundIntegrations { get; init; } = [];
    public IReadOnlyList<string> InfrastructureFormats { get; init; } = [];
    /// <summary>Infrastructure automation in the same source package ("Python, Shell scripts · 6 source checks"), read statically — kept apart from
    /// <see cref="InfrastructureFormats"/>, which is what an IaC requirement matches. Null when there is none.</summary>
    public string? InfrastructureAutomation { get; init; }
    /// <summary>The configuration environments the snapshot declares (Development, QA, Local …). A source label, never a deployed environment.</summary>
    public IReadOnlyList<string> ConfigurationEnvironments { get; init; } = [];
    /// <summary>The snapshot's own per-area analysis outcomes (application code, architecture, evidence domains), copied from the stored models —
    /// so a snapshot whose code analysis found no production project still shows the infrastructure, configuration or contract evidence it holds.
    /// Empty from older backends.</summary>
    public IReadOnlyList<SourceSetupArea> Areas { get; init; } = [];
}

/// <summary>One Source Analysis area of a stored snapshot and its own outcome: Complete, Partial, NotDetected, Unsupported, Failed or NotAnalyzed
/// (the snapshot predates the area). Not a review result.</summary>
public sealed record SourceSetupArea(string Area, string Status);

/// <summary>Source setup across every registered pack that declares logical sources, plus the stored snapshots they were matched against.
/// Bindings are per target environment: without one, nothing reads as bound.</summary>
public sealed record SourceSetupView
{
    public string EnvironmentId { get; init; } = "";
    public bool BindingsRead { get; init; }
    public IReadOnlyList<TestPackSourceSetup> Packs { get; init; } = [];
    public IReadOnlyList<SourceSetupSnapshot> Snapshots { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

// ── Execution access ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How this caller may execute active tests: an authenticated principal holding ActiveEventExecute, or the explicitly enabled local
/// trusted execution of a loopback-only development backend. None means execution is refused.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionAccessMode { None, Authenticated, LocalTrusted }

/// <summary>What the backend says about the current caller's execution permission. No identity, claim or token is included.</summary>
public sealed record ExecutionAccessView(ExecutionAccessMode Mode, bool CanExecute, string Detail);

// ── Run validation (dry run) ────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One check of a dry run. A non-blocking check is a stated risk (e.g. a schema field the app is expected to fill), not a pass.</summary>
public sealed record JourneyValidationCheck(string Key, string Label, bool Passed, string Detail, bool Blocking = true);

/// <summary>
/// The result of validating a run WITHOUT executing it: no external call, no token, no instance, no history record. The preview holds only
/// safe, redacted facts (structure, masked values) — never a payload, identifier, token or secret.
/// </summary>
public sealed record JourneyRunValidation
{
    public string PackId { get; init; } = "";
    public string JourneyId { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    /// <summary>True when every blocking check passed. Valid is not executed, and not a prediction that the external system accepts it.</summary>
    public bool Valid { get; init; }
    public IReadOnlyList<JourneyValidationCheck> Checks { get; init; } = [];
    public IReadOnlyList<JourneyEvidenceFact> Preview { get; init; } = [];
    public IReadOnlyList<string> InputSummary { get; init; } = [];
    /// <summary>The pack's Payload &amp; Mapping Flow for this dry run (generated and expected only — nothing is observed in a dry run).</summary>
    public PayloadFlowView? PayloadFlow { get; init; }
    public string Note { get; init; } = "Validated without any external call: nothing was authenticated, created, sent or recorded.";
}

// ── Test packs ──────────────────────────────────────────────────────────────────────────────────────────────────────
// A test pack is a registered journey pack seen as a whole: its journeys, verification views, rules, required capabilities and history,
// described from the pack's own metadata. The descriptor is generic: a page renders any pack from it, and the engine never names a pack.

[JsonConverter(typeof(JsonStringEnumConverter))]
/// <summary>Security: the pack's projection of the shared Security Classification capability (consumer id = pack id); read-only.</summary>
public enum TestPackViewKind { Overview, Journey, Verification, Rules, History, Security }

/// <summary>One top-level view of a test pack. <c>ViewId</c> is the stable route key; for a journey view it equals the journey id.</summary>
public sealed record TestPackView(string ViewId, string DisplayName, TestPackViewKind Kind, string? JourneyId = null, string? ScenarioId = null, string Description = "");

/// <summary>A navigation group of a test pack (e.g. "Journeys", "Verify"); presentation order only.</summary>
public sealed record TestPackNavigationGroup(string Label, IReadOnlyList<TestPackView> Views);

/// <summary>A capability the pack needs from a shared owner (Integration Quality Review, Source Analysis, Test Data). Declared, never satisfied here.</summary>
public sealed record TestPackCapabilityRequirement(string Key, string Label, string Owner, string Detail = "");

/// <summary>The source guidance of one pack in one environment: per declared configuration requirement, what the IQR-bound source declares.</summary>
public sealed record TestPackSourceGuidance(string PackId, IReadOnlyList<BirkNext.SourceDomains.SourceConfigurationSuggestion> Suggestions)
{
    public IReadOnlyList<string> Limitations { get; init; } = [];
    /// <summary>The pack's logical sources and their current state (bound / available but not bound / missing / review required).</summary>
    public IReadOnlyList<TestPackSourceStatus> Sources { get; init; } = [];
}

/// <summary>One journey as the pack declares it: its flow (component chain from the declared steps) and who executes it.</summary>
public sealed record TestPackJourney(string JourneyId, string DisplayName, string Flow, JourneyExecutionMode ExecutionMode, string? ExecutionProvider);

public sealed record TestPackDescriptor
{
    public string PackId { get; init; } = "";
    public string PackVersion { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<TestPackJourney> Journeys { get; init; } = [];
    public IReadOnlyList<TestPackView> VerificationViews { get; init; } = [];
    public IReadOnlyList<string> RuleIds { get; init; } = [];
    public IReadOnlyList<TestPackCapabilityRequirement> RequiredIntegrationCapabilities { get; init; } = [];
    public IReadOnlyList<TestPackCapabilityRequirement> RequiredEvidenceCapabilities { get; init; } = [];
    public IReadOnlyList<TestPackCapabilityRequirement> RequiredTestDataCapabilities { get; init; } = [];
    public IReadOnlyList<BirkNext.SourceDomains.SourceConfigurationRequirement> RequiredConfiguration { get; init; } = [];
    public IReadOnlyList<TestPackSourceRole> SourceRoles { get; init; } = [];
    public IReadOnlyList<string> ExecutionProviders { get; init; } = [];
    public bool SupportsHistory { get; init; }
    public IReadOnlyList<TestPackNavigationGroup> Navigation { get; init; } = [];
    /// <summary>Authorization expectations the pack knows for its APIs (generic selectors; never credentials).</summary>
    public IReadOnlyList<TestPackAuthorizationExpectation> AuthorizationExpectations { get; init; } = [];
}

/// <summary>
/// One authorization expectation a Test Pack declares. <see cref="ApiSelector"/> matches an API target whose name or base path contains it
/// (case-insensitive); REST operations are selected by method and path template, GraphQL by a query document. An empty <see cref="Role"/> means
/// an unauthenticated request. <see cref="Confirmed"/> is the pack's own statement that the expectation is authoritative.
/// </summary>
public sealed record TestPackAuthorizationExpectation(string ApiSelector, string Role, BirkNext.RuntimeSecurity.AuthorizationExpectation Expected)
{
    public string Method { get; init; } = "GET";
    public string? Path { get; init; }
    public string? GraphQlQuery { get; init; }
    public bool Confirmed { get; init; } = true;
    public string Note { get; init; } = "";
}

// ── Integration templates (Add integration guidance) ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// Safe, deterministic setup metadata a test pack offers for one of its integrations, so "Add integration" can prefill what the pack already
/// knows (type, system, producer, consumer, known endpoint) instead of asking the tester. Never secrets, credentials, connection strings or
/// personal data, and never trust, runtime or test state. An endpoint is only offered where the pack knows it; environment-specific values
/// come from <see cref="EndpointByEnvironment"/> (an unknown environment stays empty — never guessed) and a template value is never offered
/// for a production environment.
/// </summary>
/// <summary>
/// Where a Test Data role goes in the message the template's contract describes: an element path under the root ("Klient.Identifikator.Fodselsnummer",
/// local names) or none when the role is not part of the message (e.g. the Altinn actor). Declared by the test pack; values are resolved from Test
/// Data by alias at fixture creation, never stored here.
/// </summary>
public sealed record TestDataFixtureBinding(string Role, string Capability, string? ElementPath, string Note);

public sealed record IntegrationTemplateDefinition(string Key, string DisplayName, string Description, BirkNext.Integrations.IntegrationKind Kind)
{
    /// <summary>Test Data roles of a message fixture for this template's contract (Test Data → Create XML fixture).</summary>
    public IReadOnlyList<TestDataFixtureBinding> FixtureBindings { get; init; } = [];
    /// <summary>Test Data capabilities a valid fixture for <see cref="SchemaName"/> satisfies (e.g. the pack's payload role), recorded when it is saved.</summary>
    public IReadOnlyList<string> FixtureCapabilities { get; init; } = [];
    /// <summary>The pack integration requirement the template fulfils (also how an existing record is recognized as already configured).</summary>
    public string? RequirementKey { get; init; }
    public string? SystemName { get; init; }
    public string? Producer { get; init; }
    public string? Consumer { get; init; }
    public string? SourceSystem { get; init; }
    public string? DestinationSystem { get; init; }
    /// <summary>A known, non-secret endpoint/channel valid for every non-production environment (e.g. a fixed topic name).</summary>
    public string? EndpointOrTopic { get; init; }
    /// <summary>Environment-specific non-secret endpoints by environment kind name ("Development", "QA"); wins over <see cref="EndpointOrTopic"/>.</summary>
    public IReadOnlyDictionary<string, string> EndpointByEnvironment { get; init; } = new Dictionary<string, string>();
    public string? SourceResource { get; init; }
    /// <summary>The pack source role whose snapshot is this integration's canonical evidence (e.g. the repository that implements it).</summary>
    public string? EvidenceSourceRoleKey { get; init; }
    /// <summary>Which side of the integration the evidence source implements (e.g. the receiving service). Unknown: contract discovery lists
    /// source candidates for both sides and asks for confirmation.</summary>
    public BirkNext.Integrations.IntegrationSourceSide EvidenceSourceSide { get; init; }
    /// <summary>Which facts of a Test Pack reference describe this integration's authentication and submission contract (null: none).</summary>
    public IntegrationTemplateReferenceUse? Reference { get; init; }
}

/// <summary>
/// How a template reads a pack's Test Pack reference: the role that provides it, the facts (by label) that describe authentication and the
/// submission contract, and the fact (by semantic key) whose value names the schema to look for in the evidence source. Reference facts are
/// known metadata for display only: never saved on the integration, never runtime configuration, never verified.
/// </summary>
public sealed record IntegrationTemplateReferenceUse(string RoleKey)
{
    public IReadOnlyList<string> AuthenticationFacts { get; init; } = [];
    public IReadOnlyList<string> ContractFacts { get; init; } = [];
    public string? SchemaFactKey { get; init; }
    /// <summary>Which side the reference describes (e.g. the sending application): its declared data type is that side's contract metadata.</summary>
    public BirkNext.Integrations.IntegrationSourceSide Side { get; init; }
}

/// <summary>An XML schema in a snapshot recognized as the template's evidence source whose name equals the reference's schema fact exactly. A
/// candidate only: what the source contains, not what the deployed receiver validates against.</summary>
public sealed record IntegrationTemplateSchemaCandidate(Guid SnapshotId, string ArchiveFileName, string SchemaName, string File)
{
    public string? Version { get; init; }
}

/// <summary>A template value and where it came from ("Template", "Template · Development").</summary>
public sealed record IntegrationTemplateValue(string Value, string Provenance);

/// <summary>A template resolved for one environment: the prefill values with provenance, the fields left for the tester, the evidence source
/// state, and whether the pack integration is already configured. Read-only guidance.</summary>
public sealed record IntegrationTemplateView(string Key, string DisplayName, string Description, BirkNext.Integrations.IntegrationKind Kind, string PackId, string PackName)
{
    /// <summary>Field name (IntegrationDefinition property) → value + provenance. Only fields the template knows.</summary>
    public IReadOnlyDictionary<string, IntegrationTemplateValue> Values { get; init; } = new Dictionary<string, IntegrationTemplateValue>();
    /// <summary>Fields the template cannot fill for this environment (e.g. an endpoint not known for it): "Needs configuration".</summary>
    public IReadOnlyList<string> NeedsConfiguration { get; init; } = [];
    public IReadOnlyList<string> Journeys { get; init; } = [];
    /// <summary>The evidence source requirement and its current state (null: the template needs no source evidence).</summary>
    public TestPackSourceStatus? EvidenceSource { get; init; }
    /// <summary>Configured integrations that already fulfil the template's requirement (adding another is usually a duplicate).</summary>
    public IReadOnlyList<string> AlreadyConfigured { get; init; } = [];
    /// <summary>The Test Pack reference the template reads and its state (null: the template uses none).</summary>
    public TestPackSourceStatus? ReferenceSource { get; init; }
    /// <summary>Reference facts describing authentication (scheme, token providers …) — known metadata, never run configuration.</summary>
    public IReadOnlyList<TestPackReferenceFact> AuthenticationReference { get; init; } = [];
    /// <summary>Reference facts describing the submission contract (data type, content type, task …).</summary>
    public IReadOnlyList<TestPackReferenceFact> ContractReference { get; init; } = [];
    /// <summary>The schema name the reference declares (e.g. the form data type), looked up in the evidence source; null when none.</summary>
    public string? SchemaName { get; init; }
    /// <summary>Schemas named exactly <see cref="SchemaName"/> in the snapshots recognized as the evidence source (never chosen automatically).</summary>
    public IReadOnlyList<IntegrationTemplateSchemaCandidate> SchemaCandidates { get; init; } = [];
    /// <summary>Test Data roles of a message fixture for <see cref="SchemaName"/> (see <see cref="TestDataFixtureBinding"/>).</summary>
    public IReadOnlyList<TestDataFixtureBinding> FixtureBindings { get; init; } = [];
    /// <summary>Test Data capabilities a valid fixture for <see cref="SchemaName"/> satisfies (e.g. the pack's payload role), recorded when it is saved.</summary>
    public IReadOnlyList<string> FixtureCapabilities { get; init; } = [];
    /// <summary>The integration requirement's producer → consumer ("Altinn → MU"), from the pack definition.</summary>
    public string? Flow { get; init; }
}
