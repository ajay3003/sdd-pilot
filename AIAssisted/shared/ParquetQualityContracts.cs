using System.Text.Json.Serialization;

namespace BirkNext.Parquet;

// Parquet Quality Review: a Parquet file validated against a dataset contract found in an imported Source Analysis snapshot. Every rule says
// where it came from (schema, contract, approved business rule, user), every result how it was evaluated (exact full scan, metadata, not
// available), and every requirement whether it was actually executed. File readable, schema compatible, data valid, business rules verified and
// end-to-end evidence are separate dimensions — never one green PASS.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractMatchKind { AuthoritativeContract, CandidateMapping, Ambiguous, Unresolved }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractGovernance { Approved, Draft, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleOrigin { UserAuthored, SchemaDerived, ContractDerived, ApprovedBusinessRule }

/// <summary>Active: runs. NeedsReview: generated but not enabled until a person approves it. NotSupported: a requirement no rule can check
/// (listed, never silently dropped). Rejected: a person decided it does not apply.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleReviewState { Active, NeedsReview, NotSupported, Rejected }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvaluationMode { Exact, Metadata, Sampled, Estimated, NotAvailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SchemaCompatibility { Compatible, CompatibleWithWarnings, Incompatible, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SchemaFindingKind { Match, Missing, Unexpected, PhysicalType, LogicalType, Nullability, DecimalScale, Nested, Mapped }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequirementStatus { CoveredPass, CoveredFail, NotAssessed, Blocked, NotSupported, NotApplicable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorrelationLevel { NotCorrelated, ManualUnverified, Candidate, Confirmed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BaselineClass { Regression, Improvement, ExpectedChange, Incompatible, Inconclusive, NotComparable, Unchanged }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityState { Pass, Fail, Partial, NotAssessed, Blocked }

// ── Dataset contracts (parsed from a retained source document) ──────────────────────────────────────────────────────────

public sealed record DatasetContractColumn
{
    public string Name { get; init; } = "";
    /// <summary>As documented (UTF8, INT32, BYTE_ARRAY (UTF8), FIXED_LEN_BYTE_ARRAY (DECIMAL(6,2)), LIST&lt;…&gt; …).</summary>
    public string DocumentedType { get; init; } = "";
    public string? PhysicalType { get; init; }
    public string? LogicalType { get; init; }
    public bool Nullable { get; init; }
    public bool Nested { get; init; }
    /// <summary>Exact value set when the content cell enumerates values; empty otherwise.</summary>
    public List<string> AllowedValues { get; init; } = [];
    /// <summary>A documented format token (YYYY-MM, YYYY-MM-DD) when the content is exactly one.</summary>
    public string? Format { get; init; }
    public string Content { get; init; } = "";
    public int Line { get; init; }
}

/// <summary>A requirement statement of the contract (a column row, a rule sentence, the manifest, determinism, confidentiality…).</summary>
public sealed record DatasetContractRequirement
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Text { get; init; } = "";
    public int Line { get; init; }
    /// <summary>True when a rule could be generated from it; otherwise <see cref="Reason"/> says why not (NOT SUPPORTED, never dropped).</summary>
    public bool Machine { get; init; }
    public string? Reason { get; init; }
}

public sealed record DatasetRename(string From, string To, string Kind, string Source, int Line);

public sealed record DatasetContract
{
    /// <summary>Stable key: the storage container of the dataset section (e.g. skolenaervaer-dekning), else a slug of the section title.</summary>
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Version { get; init; } = "";
    public ContractGovernance Governance { get; init; }
    public string GovernanceReason { get; init; } = "";
    public string DocumentPath { get; init; } = "";
    public string DocumentSha256 { get; init; } = "";
    public string? Container { get; init; }
    public string? PathPattern { get; init; }
    public List<DatasetContractColumn> Columns { get; init; } = [];
    public List<string> SortKeys { get; init; } = [];
    public List<DatasetContractRequirement> Requirements { get; init; } = [];
    /// <summary>Documented renames from an earlier contract version (column or value), with the document and line that state them.</summary>
    public List<DatasetRename> Renames { get; init; } = [];
    public string? PredecessorVersion { get; init; }
    public List<string> ManifestFields { get; init; } = [];
    public int Line { get; init; }
}

public sealed record DatasetContractSource(string ProjectKey, string ProjectName, Guid SnapshotId, DateTimeOffset AnalyzedAt, string ArchiveName);

public sealed record ColumnMapping(string Actual, string Expected, string Provenance, string? Source = null);

public sealed record ContractCandidate
{
    public DatasetContractSource Source { get; init; } = new("", "", Guid.Empty, default, "");
    public DatasetContract Contract { get; init; } = new();
    public ContractMatchKind Match { get; init; }
    public string Confidence { get; init; } = "Low";
    public List<string> Reasons { get; init; } = [];
    public int MatchedColumns { get; init; }
    public int MappedColumns { get; init; }
    public int ExpectedColumns { get; init; }
    /// <summary>Documented renames that apply to this file (actual name → contract name); never invented.</summary>
    public List<ColumnMapping> DocumentedMappings { get; init; } = [];
    /// <summary>Contract columns no documented rename explains (a person must declare the mapping, or the schema is incompatible).</summary>
    public List<string> UnmappedExpected { get; init; } = [];
}

public sealed record ContractDiscovery
{
    public Guid SessionId { get; init; }
    public ContractMatchKind Outcome { get; init; }
    public List<ContractCandidate> Candidates { get; init; } = [];
    public List<string> Projects { get; init; } = [];
    public string Explanation { get; init; } = "";
    public List<string> Limitations { get; init; } = [];
}

/// <summary>The contract a file is validated against, with the person's review. Mappings: documented (from the source) or declared (by a person).</summary>
public sealed record ContractAssociation
{
    public string ProjectKey { get; init; } = "";
    public Guid SnapshotId { get; init; }
    public string ContractKey { get; init; } = "";
    public string ContractVersion { get; init; } = "";
    public string DocumentPath { get; init; } = "";
    public string DocumentSha256 { get; init; } = "";
    public ContractMatchKind Match { get; init; }
    public string Confidence { get; init; } = "";
    public List<string> Reasons { get; init; } = [];
    /// <summary>Proposed (not reviewed) or Confirmed (a person confirmed the association).</summary>
    public string ReviewStatus { get; init; } = "Proposed";
    public string? ReviewedBy { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public List<ColumnMapping> ColumnMappings { get; init; } = [];
}

// ── Rules, schema, coverage ─────────────────────────────────────────────────────────────────────────────────────────────

public sealed record RuleGeneration
{
    public ContractAssociation Association { get; init; } = new();
    public string RuleSetName { get; init; } = "";
    public string RuleSetRef { get; init; } = "";
    public List<ParquetRule> Rules { get; init; } = [];
    public List<DatasetContractRequirement> Requirements { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record SchemaFinding(string Column, string? ActualColumn, SchemaFindingKind Kind, string Expected, string Actual, string Severity, string Explanation);

public sealed record ParquetSchemaCheck(SchemaCompatibility Compatibility, List<SchemaFinding> Findings, string Summary);

public sealed record RequirementCoverage
{
    public string RequirementId { get; init; } = "";
    public string Category { get; init; } = "";
    public string Text { get; init; } = "";
    public string Source { get; init; } = "";
    public List<string> RuleIds { get; init; } = [];
    public RequirementStatus Status { get; init; }
    public string Reason { get; init; } = "";
    public string Review { get; init; } = "";
}

public sealed record QualityDimension(string Name, QualityState State, string Reason);

public sealed record ParquetManifestSummary(string? Period, string? ContractVersion, string? LockedAt, string? FileName, string? Sha256, long? RowCount, long? Bytes, string Source);

public sealed record ParquetCorrelation
{
    public Guid? RunId { get; init; }
    public string? RunName { get; init; }
    public CorrelationLevel Level { get; init; }
    public List<string> Evidence { get; init; } = [];
    public string Explanation { get; init; } = "";
}

public sealed record BaselineFinding(string Area, string Subject, string Baseline, string Current, BaselineClass Class, string Reason);

public sealed record BaselineAssessment
{
    public string BaselineLabel { get; init; } = "";
    public string BaselineKind { get; init; } = "";
    public Guid? BaselineEvidenceId { get; init; }
    public string? BaselineSha256 { get; init; }
    public bool BaselineApproved { get; init; }
    public bool Comparable { get; init; }
    public List<string> Checks { get; init; } = [];
    public List<BaselineFinding> Findings { get; init; } = [];
    public BaselineClass Overall { get; init; }
    public List<string> Limitations { get; init; } = [];
}

public sealed record CategoryCount(string Value, long Count);

public sealed record ColumnStatistic
{
    public string Column { get; init; } = "";
    public long Rows { get; init; }
    public long Nulls { get; init; }
    public long? Distinct { get; init; }
    /// <summary>Only for numeric/date columns that are not identifier-like; never for text values.</summary>
    public string? Min { get; init; }
    public string? Max { get; init; }
    public EvaluationMode Mode { get; init; }
    /// <summary>Exact category distribution: only values the contract enumerates (allowed values) are kept in durable evidence.</summary>
    public List<CategoryCount> Categories { get; init; } = [];
}

/// <summary>Contract-defined aggregate (e.g. sum of count per category × measure): exact, keyed only by contract-enumerated values.</summary>
public sealed record AggregateCell(string Row, string Column, decimal Value);

public sealed record ParquetStatistics
{
    public long Rows { get; init; }
    public long DuplicateRows { get; init; }
    public List<ColumnStatistic> Columns { get; init; } = [];
    public string? PivotRowColumn { get; init; }
    public string? PivotColumnColumn { get; init; }
    public string? PivotValueColumn { get; init; }
    public List<AggregateCell> Pivot { get; init; } = [];
    public EvaluationMode Mode { get; init; }
    public string Scope { get; init; } = "";
}

public sealed record ParquetQualityRequest
{
    public ContractAssociation? Association { get; init; }
    public ParquetRuleSet Rules { get; init; } = new();
}

public sealed record ParquetQualityResult
{
    public Guid SessionId { get; init; }
    public ContractAssociation? Association { get; init; }
    public ParquetSchemaCheck Schema { get; init; } = new(SchemaCompatibility.NotAssessed, [], "");
    public ParquetValidation? Validation { get; init; }
    public List<RequirementCoverage> Coverage { get; init; } = [];
    public List<QualityDimension> Dimensions { get; init; } = [];
    public ParquetStatistics? Statistics { get; init; }
    public ParquetManifestSummary? Manifest { get; init; }
    public string NextAction { get; init; } = "";
    public List<string> Limitations { get; init; } = [];
}

public sealed record ParquetRuleSetVersion
{
    public Guid Id { get; init; }
    public string ProjectKey { get; init; } = "";
    public string ContractKey { get; init; } = "";
    public string ContractVersion { get; init; } = "";
    public string Name { get; init; } = "";
    public int Version { get; init; }
    public List<ParquetRule> Rules { get; init; } = [];
    public string ApprovedBy { get; init; } = "";
    public DateTimeOffset ApprovedAt { get; init; }
    public string? Note { get; init; }
}

public sealed record ParquetRuleSetApproval(string ProjectKey, string ContractKey, string ContractVersion, string Name, List<ParquetRule> Rules, string? Note);

public sealed record ParquetBaselineRequest
{
    public Guid CurrentSessionId { get; init; }
    public Guid? BaselineSessionId { get; init; }
    public Guid? BaselineEvidenceId { get; init; }
    public string? EnvironmentId { get; init; }
    public List<string> KeyColumns { get; init; } = [];
    public ParquetQualityRequest? Quality { get; init; }
}

public sealed record ParquetBaselineResult(BaselineAssessment Assessment, ParquetComparison? Records);

public sealed record ParquetApprovalRequest(ContractAssociation Association, ParquetRuleSetApproval Approval);
