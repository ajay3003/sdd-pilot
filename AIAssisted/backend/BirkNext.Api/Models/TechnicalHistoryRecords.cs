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

/// <summary>A project's requirement catalog as the server read it from the imported specification files (immutable; one row per content revision).</summary>
public sealed class SpecificationCatalogRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public int Revision { get; set; }
    public string Fingerprint { get; set; } = "";
    public string? ArchiveSha256 { get; set; }
    public Guid? SnapshotId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string DocumentsJson { get; set; } = "[]";
    public string EntriesJson { get; set; } = "[]";
}

/// <summary>A finished specialist technical review stored on the server (Frontend Quality Review, safe fuzzing, authorization checks).
/// Append-only: the report JSON is never changed after insert.</summary>
public sealed class TechnicalReviewRunRecord
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string? ProjectKey { get; set; }
    public string EnvironmentId { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    public string? TargetUrl { get; set; }
    public Guid? SourceSnapshotId { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public string Outcome { get; set; } = "";
    public int Checks { get; set; }
    public int Problems { get; set; }
    public string Summary { get; set; } = "";
    public string ReportSha256 { get; set; } = "";
    public string ReportJson { get; set; } = "{}";
    public DateTimeOffset RecordedAt { get; set; }
}
