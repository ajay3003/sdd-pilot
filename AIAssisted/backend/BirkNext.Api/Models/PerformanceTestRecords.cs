namespace BirkNext.Api.Models;

/// <summary>Current version of a performance test definition (JSON document). Runs keep their own immutable snapshot.</summary>
public class PerformanceTestDefinitionRecord
{
    public string Id { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool Archived { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string DocumentJson { get; set; } = string.Empty;
}

/// <summary>One performance test run (immutable once finished): definition snapshot, normalized metrics, threshold and drift results.</summary>
public class PerformanceTestRunRecord
{
    public Guid Id { get; set; }
    public string EnvironmentId { get; set; } = string.Empty;
    public string DefinitionId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string DocumentJson { get; set; } = string.Empty;
}

/// <summary>A versioned, explicitly selected baseline. Superseded baselines are kept; nothing is hard-deleted.</summary>
public class PerformanceBaselineRecord
{
    public string Id { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public string DefinitionId { get; set; } = string.Empty;
    public string ComparisonFingerprint { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string DocumentJson { get; set; } = string.Empty;
}

/// <summary>Approved synthetic test data referenced by performance scenarios.</summary>
public class PerformanceTestDataProfileRecord
{
    public string Id { get; set; } = string.Empty;
    public string EnvironmentId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string DocumentJson { get; set; } = string.Empty;
}
