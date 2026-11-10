namespace BirkNext.Api.Models;

/// <summary>One execution run (indexed summary columns + the run summary document). Owned by AppDbContext, like every other execution run.</summary>
public sealed class ExecutionRunRow
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Planned { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Errors { get; set; }
    public int Attempts { get; set; }
    public string SummaryJson { get; set; } = "";
    /// <summary>Kept by bulk deletion and retention; removed only by an explicit unprotect.</summary>
    public bool Protected { get; set; }
    public DateTimeOffset? ProtectedAt { get; set; }
}

/// <summary>Audit trail of evidence management (who removed or exported what, and how much). Holds counts and references, never content.</summary>
public sealed class ExecutionEvidenceAuditRow
{
    public Guid Id { get; init; }
    public DateTimeOffset At { get; init; }
    public string Action { get; init; } = "";
    public string Scope { get; init; } = "";
    public int Runs { get; init; }
    public int Calls { get; init; }
    public long Bytes { get; init; }
    public string Initiator { get; init; } = "";
    public string? Detail { get; init; }
}

/// <summary>An imported, sanitized message observation set (Service Bus peek export or Wolverine envelope export). Content was redacted before it
/// was stored; the document never holds an unredacted body or an envelope body.</summary>
public sealed class MessageObservationSetRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset ImportedAt { get; init; }
    public string DocumentJson { get; set; } = "";
}

/// <summary>Saved Parquet inspection evidence: metadata, fingerprints, rule results and comparison counts — never row values.</summary>
public sealed class ParquetInspectionRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string FileName { get; set; } = "";
    public DateTimeOffset SavedAt { get; init; }
    public string DocumentJson { get; set; } = "";
}

/// <summary>An approved Parquet rule-set version for one project + dataset contract + contract version. Insert only: a new approval is a new
/// version; earlier versions stay as they were (results saved against them keep their reference).</summary>
public sealed class ParquetRuleSetRecord
{
    public Guid Id { get; init; }
    public string ProjectKey { get; init; } = "";
    public string ContractKey { get; init; } = "";
    public string ContractVersion { get; init; } = "";
    public int Version { get; init; }
    public string Name { get; init; } = "";
    public string RulesJson { get; init; } = "";
    public string ApprovedBy { get; init; } = "";
    public DateTimeOffset ApprovedAt { get; init; }
    public string? Note { get; init; }
}

/// <summary>One revision of a generated integration suite: a manifest of references (never copies of contracts, tests, data or evidence).</summary>
public sealed class IntegrationSuiteRecord
{
    public Guid Id { get; init; }
    public string SuiteKey { get; init; } = "";
    public int Revision { get; init; }
    public Guid SnapshotId { get; init; }
    public string ScopeId { get; init; } = "";
    public string? EnvironmentId { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public string Fingerprint { get; init; } = "";
    public string DefinitionJson { get; init; } = "";
}

/// <summary>Link from a suite to an execution run it started (the run and its calls stay in Execution Evidence).</summary>
public sealed class IntegrationSuiteRunRecord
{
    public Guid Id { get; init; }
    public string SuiteKey { get; init; } = "";
    public Guid SuiteId { get; init; }
    public Guid RunId { get; init; }
    public string Kind { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public string Initiator { get; init; } = "";
    public string TestIdsJson { get; init; } = "[]";
}

/// <summary>One implementation-gap evaluation: the extracted SDD inputs (never document text) and the report, per scope (e.g. a project import).</summary>
public sealed class ImplementationGapEvaluationRecord
{
    public Guid Id { get; init; }
    public string ScopeKey { get; init; } = "";
    public string? ProjectImportId { get; init; }
    public Guid? SnapshotId { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; }
    public string InputsJson { get; init; } = "";
    public string ReportJson { get; init; } = "";
}

/// <summary>Saved Blob observation: store, blob identity, properties, redacted metadata, check results, correlation and a hash of the bytes read — never content.</summary>
public sealed class BlobObservationRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string BlobName { get; set; } = "";
    public DateTimeOffset ObservedAt { get; init; }
    public string DocumentJson { get; set; } = "";
}

/// <summary>The one retention setting of execution evidence (row id 1). Absent = Disabled.</summary>
public sealed class ExecutionRetentionSettingsRow
{
    public int Id { get; init; }
    public string Mode { get; set; } = "Disabled";
    public int? Days { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>One logical call of a run: searchable columns plus the redacted call document (definition, attempts, captures, assertions).</summary>
public sealed class ExecutionCallRow
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public int Sequence { get; init; }
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Method { get; init; } = "";
    public string State { get; set; } = "";
    public int? HttpStatus { get; set; }
    public long? DurationMs { get; set; }
    public int AttemptCount { get; set; }
    public string DocumentJson { get; set; } = "";
    /// <summary>The definition this call ran (e.g. a contract test revision, "contract-test:{id}@r2"), for "latest result of a test" lookups.</summary>
    public string? DefinitionRef { get; init; }
}
