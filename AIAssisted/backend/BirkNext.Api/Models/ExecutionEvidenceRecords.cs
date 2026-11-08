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
