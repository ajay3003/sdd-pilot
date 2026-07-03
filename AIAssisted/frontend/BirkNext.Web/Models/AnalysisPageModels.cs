using System.Text.Json.Serialization;

namespace BirkNext.Web.Models.Analysis;

/// <summary>
/// Analysis page status enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisStatus
{
    Ready = 0,
    Blocked = 1,
    Warning = 2,
    Fail = 3,
    Empty = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public class AnalysisPageModel
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("readinessStatus")]
    public AnalysisStatus ReadinessStatus { get; set; }

    [JsonPropertyName("requiredInputs")]
    public List<string> RequiredInputs { get; set; } = [];

    [JsonPropertyName("missingInputs")]
    public List<string> MissingInputs { get; set; } = [];

    [JsonPropertyName("sections")]
    public List<AnalysisSection> Sections { get; set; } = [];

    [JsonPropertyName("results")]
    public List<AnalysisResult> Results { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<AnalysisAction> Actions { get; set; } = [];

    [JsonPropertyName("summary")]
    public AnalysisSummary Summary { get; set; } = new();
}

public class AnalysisSection
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("items")]
    public List<string> Items { get; set; } = [];
}

public class AnalysisResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public AnalysisStatus Status { get; set; }

    [JsonPropertyName("severity")]
    public AnalysisSeverity Severity { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; set; }

    [JsonPropertyName("relatedArtifacts")]
    public List<string> RelatedArtifacts { get; set; } = [];
}

public class AnalysisAction
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("navigationUrl")]
    public string? NavigationUrl { get; set; }

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; set; }
}

public class AnalysisSummary
{
    [JsonPropertyName("canRun")]
    public bool CanRun { get; set; }

    [JsonPropertyName("readinessMessage")]
    public string ReadinessMessage { get; set; } = string.Empty;

    [JsonPropertyName("totalResults")]
    public int TotalResults { get; set; }

    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("highCount")]
    public int HighCount { get; set; }

    [JsonPropertyName("mediumCount")]
    public int MediumCount { get; set; }

    [JsonPropertyName("lowCount")]
    public int LowCount { get; set; }

    [JsonPropertyName("healthPercent")]
    public int HealthPercent { get; set; }
}
