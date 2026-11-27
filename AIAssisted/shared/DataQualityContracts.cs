using System.Text.Json.Serialization;

namespace BirkNext.DataQuality;

/// <summary>What a data-quality run checks (System Settings → Data Quality, 2026-11-27). Read-only audits; nothing in the business domain is written.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataQualityCategory
{
    /// <summary>Dashboard figures: stored records → API (server) and API → UI (browser), each against an independent reference calculation.</summary>
    Dashboard,
    /// <summary>Requirement identities: repeated display ids, shared v1 source identities, ambiguous links, and a simulated (never written) v2 resolution.</summary>
    RequirementIdentity,
    /// <summary>Source and import: the stored source text against the stored parse, read again by an independent reference parser.</summary>
    SourceImport,
}

/// <summary>A check's outcome. A check that could not run is never <see cref="Pass"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataQualityStatus { Pass, Fail, Warning, Inconclusive, NotVerified }

/// <summary>Why a check did not pass: which stage is wrong, as far as the evidence shows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataQualityCause
{
    None, IncorrectImport, IncorrectAggregation, IncorrectFrontendDisplay, StaleData, WrongScopeOrBaseline, AmbiguousIdentity, UnsupportedSource,
    MismatchedDrilldown, SourceUnavailable, ParserRuleDifference, ConcurrentChange,
}

/// <summary>The stage a check compares: the import (source text → stored parse), the server (stored records → API) or the browser (API → UI).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataQualityStage { SourceToStored, StoredToApi, ApiToUi, Identity }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataQualityRunState { Running, Completed, Cancelled, Failed }

/// <summary>A simulated v2 resolution of one stored relationship (never written).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdentityResolution { Unique, Ambiguous, Unresolved, NotAffected }

/// <summary>One record behind a discrepancy: what kind of difference, its key, where it comes from and a short text.</summary>
public sealed record DataQualityRecord(string Kind, string Key, string? Path = null, string? Text = null);

/// <summary>One check. <see cref="Expected"/> is the independent reference value; <see cref="Actual"/> the production value (null when not available).</summary>
public sealed record DataQualityCheck
{
    public string Id { get; init; } = "";
    public DataQualityCategory Category { get; init; }
    public DataQualityStage Stage { get; init; }
    public string Metric { get; init; } = "";
    public string Scope { get; init; } = "";
    /// <summary>The counting contract: entity, identity, scope, denominator and inclusion rules.</summary>
    public string Contract { get; init; } = "";
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    public string? Difference { get; init; }
    public DataQualityStatus Status { get; init; }
    public DataQualityCause Cause { get; init; }
    public string Explanation { get; init; } = "";
    public List<DataQualityRecord> Records { get; init; } = [];
    /// <summary>More records existed than are kept in the report.</summary>
    public int RecordsOmitted { get; init; }
}

/// <summary>One requirement identity of the dry run (simulated; nothing is migrated or relinked).</summary>
public sealed record IdentityDryRunRow
{
    public string V1 { get; init; } = "";
    public string V2 { get; init; } = "";
    public string RequirementId { get; init; } = "";
    public string SpecificationPath { get; init; } = "";
    public string? Application { get; init; }
    public string? SourceVersion { get; init; }
    /// <summary>How many documents of the baseline carry the same v1 identity.</summary>
    public int DocumentsWithSameV1 { get; init; }
    public List<string> TestCases { get; init; } = [];
    public List<string> PlanItems { get; init; } = [];
    public List<string> ReadinessDecisions { get; init; } = [];
    public List<string> Approvals { get; init; } = [];
    public List<string> Executions { get; init; } = [];
    public List<string> Coverage { get; init; } = [];
    public IdentityResolution Resolution { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>An expected dashboard figure for the browser stage: the browser computes the production value from the same API response and compares.</summary>
public sealed record DataQualityKpiExpectation(string Id, string Metric, string Scope, int? Expected, string Contract, List<string> Members);

/// <summary>The source baseline a run used (the specification catalog behind the dashboard) and whether a newer one of the same application exists.</summary>
public sealed record DataQualityBaseline
{
    public Guid? CatalogId { get; init; }
    public string? CatalogProjectKey { get; init; }
    public int? Revision { get; init; }
    public string? ArchiveSha256 { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public int Documents { get; init; }
    public string? Application { get; init; }
    public string Label { get; init; } = "";
}

public sealed record DataQualityRunRequest(string ProjectKey, List<DataQualityCategory> Categories);

/// <summary>The browser stage's comparison for one figure, sent back to be kept with the run's report (diagnostics store only).</summary>
public sealed record DataQualityUiValue(string Id, int? Actual);

public sealed record DataQualityProgress
{
    public string Phase { get; init; } = "";
    public int ChecksCompleted { get; init; }
    public int ChecksFailed { get; init; }
    public List<string> PhasesDone { get; init; } = [];
}

/// <summary>A run and, once finished, its report.</summary>
public sealed record DataQualityRun
{
    public string RunId { get; init; } = "";
    public string ProjectKey { get; init; } = "";
    public List<DataQualityCategory> Categories { get; init; } = [];
    public DataQualityRunState State { get; init; }
    public DataQualityProgress Progress { get; init; } = new();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string StartedBy { get; init; } = "";
    public string? Error { get; init; }
    public DataQualityReport? Report { get; init; }
}

public sealed record DataQualityReport
{
    public const string SchemaVersion = "birknext-data-quality/1";
    public string Schema { get; init; } = SchemaVersion;
    public string RunId { get; init; } = "";
    public string ProjectKey { get; init; } = "";
    public DataQualityBaseline Baseline { get; init; } = new();
    /// <summary>The consistency marker of the read-only snapshot (row counts and newest change times), before and after the run.</summary>
    public string SnapshotMarker { get; init; } = "";
    /// <summary>False when the data changed while the run read it: the run is INCONCLUSIVE, never PASS.</summary>
    public bool Consistent { get; init; } = true;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public List<DataQualityCategory> Categories { get; init; } = [];
    public List<DataQualityCheck> Checks { get; init; } = [];
    public List<IdentityDryRunRow> Identities { get; init; } = [];
    public List<DataQualityKpiExpectation> Kpis { get; init; } = [];
    public List<DataQualityUiValue> UiValues { get; init; } = [];
    public List<string> Skipped { get; init; } = [];
    public List<string> FollowUps { get; init; } = [];

    public int Count(DataQualityStatus status) => Checks.Count(c => c.Status == status);
}

public sealed record DataQualityRunSummary(string RunId, string ProjectKey, DataQualityRunState State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int Pass, int Fail, int Warning, int Inconclusive, int NotVerified, string Baseline);

public sealed record DataQualityProjectOption(string ProjectKey, string Name, List<DataQualityBaseline> Baselines);
