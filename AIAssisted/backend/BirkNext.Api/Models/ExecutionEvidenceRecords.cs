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
}
