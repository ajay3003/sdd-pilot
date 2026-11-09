using System.Text.Json.Serialization;

namespace BirkNext.IntegrationSuites;

// ── Generate Integration Test Suite from Source ──────────────────────────────────────────────────────────────────────────────
// A thin orchestration layer. A suite is a versioned MANIFEST OF REFERENCES to records owned elsewhere — Contract Registry versions,
// contract-test definitions (id@revision), Test Data assets (alias@version), the Skolenærvær journey pack's journeys/steps, the M01 Security
// Classification review (Kode 6/7), topology connections and execution runs. It never copies a contract, a test body, a value or evidence.
// Generation is draft-only and makes no network call; execution is a separate, explicitly authorised action through the existing run path.
// Three outcomes are kept apart for every generated item: it can run now / it is generated but waits for a named prerequisite / it needs a
// domain decision. Readiness and execution outcome are separate dimensions.

/// <summary>Where a generated value came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuiteValueOrigin { Discovered, Inferred, UserConfirmed, RuntimeVerified, Unresolved }

/// <summary>Topology association class (never a merge by similar names).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuiteMappingClass { Confirmed, Candidate, Ambiguous, Unresolved }

/// <summary>What the registered executors/observers can do for a stage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuiteStageCoverage { Executable, Observable, ExecutableAndObservable, Manual, Unsupported, Blocked }

/// <summary>Readiness of one item (test, stage, security scenario).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuiteItemReadiness { Ready, NeedsReview, Blocked, PartiallyExecutable, NotSupported, NotAssessed }

/// <summary>The tester-facing triage of an item: run now / generated, waiting for a named prerequisite / needs a domain decision / not supported.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuiteTriage { CanRunNow, WaitingForPrerequisite, NeedsDomainDecision, NotSupported }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuiteStatus { Draft, NeedsReview, Blocked, ReadyToExecute, PartiallyExecutable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuiteExecutionOutcome { NotExecuted, Executed, PartiallyVerified, Verified, Failed }

/// <summary>How an expected outcome or scenario was established.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SuiteScenarioBasis { ContractDerived, SpecificationDerived, ReviewedBusinessRule, ManuallyAuthored, ExistingCapability, Candidate }

public sealed record SuiteSourceRef(string Path, int? Line = null, string? Note = null);

public sealed record SuiteSnapshotProvenance
{
    public Guid SnapshotId { get; init; }
    public string? ProjectImportId { get; init; }
    public string ArchiveFileName { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public DateTimeOffset? ImportedAt { get; init; }
    public string Status { get; init; } = "";
    public List<string> EvidenceDomains { get; init; } = [];
    public List<string> Modules { get; init; } = [];
    public bool FileInventoryComplete { get; init; }
    public List<string> MissingSourceAreas { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record IntegrationSuiteScope
{
    public string ScopeId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Top-level source modules whose contracts and schemas belong to the scope (e.g. SkoleService, MeldingsutvekslerReceiver).</summary>
    public List<string> Modules { get; init; } = [];
    /// <summary>The journey pack whose journeys define the ordered stages (null = an API-only scope: one stage per operation).</summary>
    public string? JourneyPackId { get; init; }
    public List<string> JourneyIds { get; init; } = [];
    public List<SuiteStageTemplate> Stages { get; init; } = [];
    public bool IncludesSecurityClassification { get; init; }
}

/// <summary>An ordered stage of the scope's integration chain, mapped to the journey pack's step ids that realise it.</summary>
public sealed record SuiteStageTemplate(string StageId, string Label, string Component, IReadOnlyList<string> JourneySteps);

public sealed record IntegrationSuiteGenerateRequest
{
    public Guid SnapshotId { get; init; }
    public string ScopeId { get; init; } = "";
    /// <summary>Modules to include for an API-only scope (ignored when the scope fixes its modules).</summary>
    public List<string> Modules { get; init; } = [];
    /// <summary>An existing Target Environment profile id (optional; never created here).</summary>
    public string? EnvironmentId { get; init; }
    public bool IncludeNegatives { get; init; } = true;
    public bool IncludeSecurity { get; init; } = true;
    public bool GenerateTestData { get; init; } = true;
    /// <summary>Seed of the synthetic Test Data generator: the same seed gives the same values.</summary>
    public int Seed { get; init; } = 20261109;
}

public sealed record SuiteContractRef
{
    public string SourceContractId { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string? ContractKey { get; init; }
    public int? ContractVersion { get; init; }
    public string ImportOutcome { get; init; } = "";
    public List<string> Servers { get; init; } = [];
    public int Operations { get; init; }
}

public sealed record SuiteTestRef
{
    public Guid TestId { get; init; }
    public int Revision { get; init; }
    public string ContractRef { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? Variant { get; init; }
    public string ReviewState { get; init; } = "";
    public string Readiness { get; init; } = "";
    public List<string> ReadinessGaps { get; init; } = [];
    public bool Mutating { get; init; }
    public string? TestDataBinding { get; init; }
    public string? SuggestedTestData { get; init; }
    public SuiteTriage Triage { get; init; }
}

public sealed record SuiteUnsupportedOperation(string ContractRef, string Operation, string Reason);

/// <summary>A generated configuration value with its origin and evidence — credentials are never values here.</summary>
public sealed record SuiteConfigurationValue
{
    public string Key { get; init; } = "";
    public string Field { get; init; } = "";
    public string? Value { get; init; }
    public SuiteValueOrigin Origin { get; init; }
    public string Confidence { get; init; } = "";
    public string Binding { get; init; } = "";
    public List<SuiteSourceRef> Evidence { get; init; } = [];
    public bool NeedsReview { get; init; }
    public string Note { get; init; } = "";
}

public sealed record SuiteGeneratedValue(string Name, string Value, string Rule, string Constraint);

public sealed record SuiteTestDataItem
{
    public string Role { get; init; } = "";
    public string Purpose { get; init; } = "";
    /// <summary>"alias@vN" of an existing approved asset, or of a generated draft.</summary>
    public string? Asset { get; init; }
    public bool Generated { get; init; }
    public bool Approved { get; init; }
    public List<SuiteGeneratedValue> Values { get; init; } = [];
    public string State { get; init; } = "";
    public string? Prerequisite { get; init; }
}

public sealed record SuiteXmlSchemaRef
{
    public string File { get; init; } = "";
    public string? TargetNamespace { get; init; }
    public List<string> RootElements { get; init; } = [];
    public bool DependenciesResolved { get; init; }
    public List<string> Unresolved { get; init; } = [];
    public string State { get; init; } = "";
}

public sealed record SuiteTopologyMapping
{
    public string Key { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Relationship { get; init; } = "";
    public SuiteMappingClass Class { get; init; }
    public string Confidence { get; init; } = "";
    public List<SuiteSourceRef> Evidence { get; init; } = [];
    public bool RequiresReview { get; init; }
}

public sealed record SuiteStage
{
    public int Order { get; init; }
    public string StageId { get; init; } = "";
    public string Label { get; init; } = "";
    public string Component { get; init; } = "";
    public List<string> JourneySteps { get; init; } = [];
    public List<string> ContractTests { get; init; } = [];
    public SuiteStageCoverage Coverage { get; init; }
    public SuiteItemReadiness Readiness { get; init; }
    public SuiteTriage Triage { get; init; }
    public string ExecutorObserver { get; init; } = "";
    public List<string> Blockers { get; init; } = [];
}

/// <summary>A Kode 6/7 scenario: an existing capability reused by id, or a candidate variant. Never duplicates the M01 review.</summary>
public sealed record SuiteSecurityScenario
{
    public string ScenarioId { get; init; } = "";
    public string Title { get; init; } = "";
    public SuiteScenarioBasis Basis { get; init; }
    /// <summary>The existing owner this scenario reuses (e.g. "Security Classification review · UnauthorizedRuntimeDenial").</summary>
    public string? Reuses { get; init; }
    public string? ExistingState { get; init; }
    public Guid? ReviewRunId { get; init; }
    public List<string> Levels { get; init; } = [];
    public SuiteItemReadiness Readiness { get; init; }
    public SuiteTriage Triage { get; init; }
    public List<string> Prerequisites { get; init; } = [];
    public string? OpenQuestion { get; init; }
    public List<SuiteSourceRef> Evidence { get; init; } = [];
    public List<string> References { get; init; } = [];
}

public sealed record SuiteReviewItem
{
    public string Key { get; init; } = "";
    public string Area { get; init; } = "";
    public string Question { get; init; } = "";
    public string Why { get; init; } = "";
    public List<SuiteSourceRef> Evidence { get; init; } = [];
    /// <summary>Current decision from the manual review store (append-only), or null.</summary>
    public string? Decision { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
}

public sealed record SuiteReadinessDimension(string Name, SuiteItemReadiness State, string Detail);

public sealed record SuiteRunLink(Guid RunId, string Kind, DateTimeOffset StartedAt, string Initiator, string State, List<Guid> TestIds);

public sealed record IntegrationSuite
{
    public Guid Id { get; init; }
    /// <summary>Stable identity of snapshot + scope + options + environment: regeneration with the same key adds revisions, never a second suite.</summary>
    public string SuiteKey { get; init; } = "";
    public int Revision { get; init; }
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; }
    public string GeneratedBy { get; init; } = "";
    public SuiteSnapshotProvenance Snapshot { get; init; } = new();
    public IntegrationSuiteScope Scope { get; init; } = new();
    public string? EnvironmentId { get; init; }
    public List<SuiteContractRef> Contracts { get; init; } = [];
    public List<SuiteTestRef> Tests { get; init; } = [];
    public List<SuiteUnsupportedOperation> Unsupported { get; init; } = [];
    public List<SuiteConfigurationValue> Configuration { get; init; } = [];
    public List<SuiteTestDataItem> TestData { get; init; } = [];
    public List<SuiteXmlSchemaRef> XmlSchemas { get; init; } = [];
    public List<SuiteTopologyMapping> Topology { get; init; } = [];
    public List<SuiteStage> Stages { get; init; } = [];
    public List<SuiteSecurityScenario> Security { get; init; } = [];
    public List<SuiteReviewItem> Review { get; init; } = [];
    public List<SuiteReadinessDimension> Readiness { get; init; } = [];
    public SuiteStatus Status { get; init; }
    public string StatusReason { get; init; } = "";
    public SuiteExecutionOutcome Outcome { get; init; }
    public List<SuiteRunLink> Runs { get; init; } = [];
    /// <summary>Changes since the previous revision (contract versions, tests, schemas) — drift is shown, never silently rebound.</summary>
    public List<string> Drift { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record IntegrationSuiteSummary(Guid Id, string SuiteKey, int Revision, string ScopeId, string ScopeName, Guid SnapshotId, string ArchiveFileName,
    DateTimeOffset GeneratedAt, SuiteStatus Status, int Tests, int Stages, int ReviewOpen);

public sealed record IntegrationSuiteGenerateResult(IntegrationSuite? Suite, bool Reused, string? Error);

public sealed record IntegrationSuiteReviewRequest(string Key, string Decision, string? Note);

public sealed record IntegrationSuiteExecuteRequest
{
    public List<Guid> TestIds { get; init; } = [];
    public string EnvironmentId { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    public bool IsProduction { get; init; }
    public string? ProfileId { get; init; }
    public string? ContextFingerprint { get; init; }
    public bool ConfirmMutatingCalls { get; init; }
    /// <summary>The tester's explicit authorisation of this run. Without it nothing is sent.</summary>
    public bool Authorized { get; init; }
}

public sealed record IntegrationSuiteExecuteResult(Guid? RunId, string State, string Message, List<string> NotRun);

public static class IntegrationSuiteRules
{
    public const string ReviewModule = "integration-suites";
    public const string RuleSetVersion = "suite-rules/1";
}
