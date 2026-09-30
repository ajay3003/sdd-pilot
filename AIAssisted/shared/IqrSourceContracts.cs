using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceAnalysisStatus { Ready, Partial, Failed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceConfidence { Confirmed, StrongSourceEvidence, Partial, NotResolved }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeveloperTestLayer { Unit, Integration, Component, Architecture, Unknown, Contract }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceCoverageStatus { DeveloperUnitCovered, DeveloperIntegrationCovered, SourceEvidenceOnly, CrossLayerGap, RuntimeGap, E2EGap, ManualVerification, NotAssessable, DeveloperContractCovered }

/// <summary>Evidence only. No source text, configuration values, test bodies or execution results.</summary>
public sealed record IqrSourceSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string IntegrationId { get; init; } = "";
    public SourceArchive Archive { get; init; } = new("", "", 0);
    public string Commit { get; init; } = "Unknown";
    public string? Branch { get; init; }
    public int AnalyzerVersion { get; init; } = 1;
    public DateTimeOffset AnalyzedAt { get; init; }
    public SourceAnalysisStatus Status { get; init; }
    public List<SourceProject> Projects { get; init; } = [];
    public List<SourceConfigurationEvidence> Configurations { get; init; } = [];
    public List<ImplementationRule> Rules { get; init; } = [];
    public List<DeveloperTestEvidence> Tests { get; init; } = [];
    public List<SourceCoverage> Coverage { get; init; } = [];
    public List<SourceDataflow> Dataflows { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public string DeploymentCorrelation { get; init; } = "Deployment/source correlation not established";
    /// <summary>Multi-stage source path (adapter → ingestion → domain → event → outbox → Service Bus) discovered from the same archive. Null in analyzer v1 snapshots.</summary>
    public IntegrationPathEvidence? IntegrationPath { get; init; }
    public BirkNext.DatabaseArchitecture.DatabaseArchitectureSnapshot? DatabaseArchitecture { get; init; }
    /// <summary>Source-derived architecture of the same archive (null in snapshots analyzed before architecture extraction). Not deployed topology.</summary>
    public BirkNext.SourceArchitecture.ArchitectureSnapshot? Architecture { get; init; }
}

// ── Multi-stage integration path (analyzer v2) ────────────────────────────────────────────────────────────────────────
// Discovered from source syntax, never hardcoded: stages are the types a path passes through, hops the source mechanisms between
// them, field traces the per-field route with an explicit transformation. Source-defined ≠ configured ≠ observed ≠ processed.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceStageKind { CdcField, AdapterModel, IngestionRequest, IngestionDto, DomainEntity, DomainEvent, EventEnvelope, Outbox, ServiceBus, Other }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldTransformation { PassThrough, Renamed, Converted, Derived, Reduced, Booleanized, MetadataOnly, FilteredIntentionally, Dropped, Defaulted, Conditional, NotResolved }

/// <summary>One type a path passes through, with the stage it plays. Field names only — never values.</summary>
public sealed record SourceStage(string Id, SourceStageKind Kind, string TypeName, string Project, List<string> Fields, SourceLocation Location, SourceConfidence Confidence, string Evidence);

/// <summary>A source-defined mechanism between two stages, with its developer-test coverage and what it does not prove.</summary>
public sealed record SourceHop
{
    public string Id { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Mechanism { get; init; } = "";
    public SourceConfidence Confidence { get; init; }
    public List<SourceLocation> Locations { get; init; } = [];
    public List<string> DeveloperTestIds { get; init; } = [];
    public List<SourceCoverageStatus> Coverage { get; init; } = [];
    /// <summary>"Source-defined", "Source route verified" … never "delivered".</summary>
    public string SourceState { get; init; } = "Source-defined";
    public string RuntimeState { get; init; } = "Not assessed";
    public string Note { get; init; } = "";
}

public sealed record FieldTraceStep(SourceStageKind Stage, string TypeName, string Field, FieldTransformation Transformation, string Detail, SourceLocation? Location,
    List<string>? DeveloperTestIds = null);

/// <summary>What happens to one origin field across the stages. <see cref="Minimization"/> separates intentional reduction from unexpected loss.</summary>
public sealed record FieldTrace
{
    public string Key { get; init; } = "";
    public string OriginField { get; init; } = "";
    public SourceStageKind OriginStage { get; init; }
    public List<FieldTraceStep> Steps { get; init; } = [];
    public string Outcome { get; init; } = "";
    /// <summary>Sensitive by field-name heuristic only (no value is inspected).</summary>
    public bool Sensitive { get; init; }
    /// <summary>"Intentional reduction / metadata projection", "Retained internally; not emitted", "Potential data-minimization issue", "Not resolved", or "Not sensitive".</summary>
    public string Minimization { get; init; } = "Not sensitive";
    public List<string> DeveloperTestIds { get; init; } = [];
    public List<SourceCoverageStatus> Coverage { get; init; } = [];
    public SourceConfidence Confidence { get; init; }
    public string Gap { get; init; } = "";
}

public sealed record EventFieldEvidence(string Name, string Type, string Source, FieldTransformation Transformation, bool SensitiveName);

/// <summary>An implementation event contract (a class in source). Never called a formal schema unless one is supplied.</summary>
public sealed record EventContractEvidence
{
    public string EventType { get; init; } = "";
    public string Project { get; init; } = "";
    public List<EventFieldEvidence> Fields { get; init; } = [];
    public List<string> Topics { get; init; } = [];
    public List<string> Subjects { get; init; } = [];
    public string SessionId { get; init; } = "Not resolved";
    public string Priority { get; init; } = "Not resolved";
    public List<string> CreatedIn { get; init; } = [];
    public string FormalSchema { get; init; } = "Not available";
    public List<string> DeveloperTestIds { get; init; } = [];
    public List<SourceCoverageStatus> Coverage { get; init; } = [];
    public SourceLocation Location { get; init; } = new("", 1);
}

/// <summary>Which fields a service compares before applying an update, and which it assigns without comparing.</summary>
public sealed record ChangeDetectionEvidence
{
    public string Entity { get; init; } = "";
    public string Input { get; init; } = "";
    public List<string> TrackedFields { get; init; } = [];
    public List<string> AssignedOnUpdate { get; init; } = [];
    public List<string> AssignedNotTracked { get; init; } = [];
    public List<string> AuditMetadataNotTracked { get; init; } = [];
    /// <summary>Whether an empty change set short-circuits before the update is applied.</summary>
    public string Gate { get; init; } = "Not resolved";
    public string Emits { get; init; } = "Not resolved";
    public List<SourceLocation> Locations { get; init; } = [];
    public List<string> DeveloperTestIds { get; init; } = [];
}

public sealed record OutboxEvidence
{
    public string EntityType { get; init; } = "";
    public List<string> Fields { get; init; } = [];
    public string Envelope { get; init; } = "Not resolved";
    public List<string> EnvelopeFields { get; init; } = [];
    public string Serialization { get; init; } = "Not resolved";
    public string Transaction { get; init; } = "Not resolved";
    public string MessageId { get; init; } = "Not resolved";
    public string Dispatcher { get; init; } = "Not resolved";
    public string Retry { get; init; } = "Not resolved";
    public string Ordering { get; init; } = "Not resolved";
    public List<SourceLocation> Locations { get; init; } = [];
    public List<string> DeveloperTestIds { get; init; } = [];
    public List<SourceCoverageStatus> Coverage { get; init; } = [];
}

public sealed record ServiceBusSourcePublication(string Entity, List<string> Subjects, string SessionId, string Priority, List<string> EventTypes, SourceLocation Location);

/// <summary>Service Bus as the source defines it. Source-defined ≠ configured ≠ observed in Azure ≠ processed by a subscriber.</summary>
public sealed record ServiceBusSourceTopology
{
    public List<ServiceBusSourcePublication> Publications { get; init; } = [];
    public string Client { get; init; } = "Not resolved";
    public string Authentication { get; init; } = "Not resolved";
    public List<string> ConfigurationKeys { get; init; } = [];
    public string MessageMapping { get; init; } = "Not resolved";
    public List<SourceLocation> Locations { get; init; } = [];
}

/// <summary>A contract boundary between two components: implementation contract, formal schema and developer contract tests stay separate.</summary>
public sealed record ContractBoundaryEvidence
{
    public string Name { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string ImplementationContract { get; init; } = "Not resolved";
    public string FormalSchema { get; init; } = "Not available";
    public string DeveloperContractTests { get; init; } = "Absent";
    public List<string> DeveloperTestIds { get; init; } = [];
    public List<string> Matched { get; init; } = [];
    public List<string> Mismatches { get; init; } = [];
    public SourceConfidence Confidence { get; init; }
}

public sealed record PathGap(SourceCoverageStatus Kind, string Title, string Detail, SourceConfidence Confidence, List<SourceLocation> Locations);

/// <summary>A source-defined behavior (session id, priority, outbox creation, duplicate handling …) with the developer tests that already cover it.
/// Covered at the same layer → reused as evidence, never duplicated. Runtime/E2E gaps stay regardless of developer coverage.</summary>
public sealed record PathRuleEvidence
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public List<string> DeveloperTestIds { get; init; } = [];
    public List<SourceCoverageStatus> Coverage { get; init; } = [];
    /// <summary>"Covered by developer test — reused as evidence; no duplicate BirkNext test", or what remains.</summary>
    public string BirkNextAction { get; init; } = "";
    public List<SourceLocation> Locations { get; init; } = [];
}

/// <summary>Field names only. No personal value is read, shown or stored.</summary>
public sealed record DataMinimizationSummary
{
    public List<string> SensitiveFieldsEntering { get; init; } = [];
    public List<string> RetainedInternally { get; init; } = [];
    public List<string> EmittedRaw { get; init; } = [];
    public List<string> ReducedMetadata { get; init; } = [];
    public List<string> PotentialExposures { get; init; } = [];
    public List<string> InternalRawCopies { get; init; } = [];
}

public sealed record IntegrationPathEvidence
{
    public List<SourceStage> Stages { get; init; } = [];
    public List<SourceHop> Hops { get; init; } = [];
    public List<FieldTrace> Fields { get; init; } = [];
    public List<EventContractEvidence> Events { get; init; } = [];
    public List<ChangeDetectionEvidence> ChangeDetection { get; init; } = [];
    public OutboxEvidence? Outbox { get; init; }
    public ServiceBusSourceTopology? ServiceBus { get; init; }
    public List<ContractBoundaryEvidence> Boundaries { get; init; } = [];
    public List<PathGap> Gaps { get; init; } = [];
    public List<PathRuleEvidence> Rules { get; init; } = [];
    public DataMinimizationSummary Minimization { get; init; } = new();
    public List<string> Inspected { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record SourceProject(string Name, string Path, bool IsTest, string Classification, SourceConfidence Confidence);
public sealed record SourceConfigurationEvidence(string File, List<string> Keys, SourceConfidence Confidence);
public sealed record ImplementationRule
{
    public string Id { get; init; } = "";
    public string Project { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? Table { get; init; }
    public string Field { get; init; } = "";
    public string? TargetType { get; init; }
    public string Requirement { get; init; } = "Not resolved";
    public string Operation { get; init; } = "Not resolved";
    public string NullBehavior { get; init; } = "Not resolved";
    public string InvalidBehavior { get; init; } = "Not resolved";
    public string Fallback { get; init; } = "Not resolved";
    public string Behavior { get; init; } = "";
    public List<string> RelatedSymbols { get; init; } = [];
    public SourceConfidence Confidence { get; init; }
    public SourceLocation Location { get; init; } = new("", 1);
    public string ConditionFingerprint { get; init; } = "";
    public string AssertionIntent { get; init; } = "Not resolved";
    public bool TestCorrelationResolved { get; init; } = true;
}
public sealed record DeveloperTestEvidence
{
    public string Id { get; init; } = "";
    public string Project { get; init; } = "";
    public string Class { get; init; } = "";
    public string Method { get; init; } = "";
    public string Framework { get; init; } = "";
    public List<string> Categories { get; init; } = [];
    public DeveloperTestLayer Layer { get; init; }
    public SourceConfidence Confidence { get; init; }
    public List<string> ProductionSymbols { get; init; } = [];
    public List<string> Fields { get; init; } = [];
    public string InputCondition { get; init; } = "Not resolved";
    public string AssertionIntent { get; init; } = "Not resolved";
    public SourceLocation Location { get; init; } = new("", 1);
    public string ExecutionResult { get; init; } = "Developer test exists; execution result unavailable";
}
public sealed record SourceCoverage(string RuleId, List<string> DeveloperTestIds, List<SourceCoverageStatus> Statuses, string MatchReason, string Action);
public sealed record SourceDataflow(string Field, List<string> Steps, SourceConfidence Confidence, List<SourceLocation> Locations, string Gap,
    List<string>? DeveloperTestIds = null, string GuardSymbol = "Not resolved");

/// <summary>Explicit selection per integration. Uploading B never replaces A in a recorded run.</summary>
public sealed record IqrSourceSelection(string IntegrationId, Guid SnapshotId);
