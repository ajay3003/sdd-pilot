namespace BirkNext.Api.Models;

/// <summary>A finished API Quality Review run as recorded (append-only; the report JSON is never changed after insert).</summary>
public sealed class ApiReviewRunRecord
{
    public Guid Id { get; set; }
    public string? ProjectKey { get; set; }
    public string EnvironmentId { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public int Targets { get; set; }
    public int Completed { get; set; }
    public int Blocked { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Info { get; set; }
    public string ReportJson { get; set; } = "{}";
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>A followed-up technical finding (content = the shared <c>TechnicalFinding</c> with its append-only history).</summary>
public sealed class TechnicalFindingRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public string Source { get; set; } = "";
    public string FindingKey { get; set; } = "";
    public string Status { get; set; } = "";
    public string ContentJson { get; set; } = "{}";
    /// <summary>Concurrency token.</summary>
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
