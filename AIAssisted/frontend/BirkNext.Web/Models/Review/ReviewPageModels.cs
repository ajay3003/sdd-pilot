using System.Text.Json.Serialization;

namespace BirkNext.Web.Models.Review;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewStatus
{
    Ready = 0,
    Empty = 1,
    Blocked = 2,
    Warning = 3,
    Fail = 4
}

public class ReviewPageModel
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("readinessStatus")]
    public ReviewStatus ReadinessStatus { get; set; }

    [JsonPropertyName("artifactKind")]
    public string? ArtifactKind { get; set; }

    [JsonPropertyName("sections")]
    public List<ReviewSection> Sections { get; set; } = [];

    [JsonPropertyName("results")]
    public List<ReviewResult> Results { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<ReviewAction> Actions { get; set; } = [];

    [JsonPropertyName("requiredInputs")]
    public List<string> RequiredInputs { get; set; } = [];

    [JsonPropertyName("missingInputs")]
    public List<string> MissingInputs { get; set; } = [];

    [JsonPropertyName("summary")]
    public ReviewSummary Summary { get; set; } = new();
}

public class ReviewSection
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("status")]
    public ReviewStatus Status { get; set; }

    [JsonPropertyName("items")]
    public List<ReviewItem> Items { get; set; } = [];
}

public class ReviewItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("status")]
    public ReviewStatus Status { get; set; }
}

public class ReviewResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("status")]
    public ReviewStatus Status { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; set; }

    [JsonPropertyName("relatedArtifacts")]
    public List<string> RelatedArtifacts { get; set; } = [];
}

public class ReviewAction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public ReviewStatus Status { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("expectedEffect")]
    public string? ExpectedEffect { get; set; }
}

public class ReviewSummary
{
    [JsonPropertyName("totalResults")]
    public int TotalResults { get; set; }

    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("infoCount")]
    public int InfoCount { get; set; }

    [JsonPropertyName("statusMessage")]
    public string StatusMessage { get; set; } = string.Empty;

    [JsonPropertyName("hasAvailableActions")]
    public bool HasAvailableActions { get; set; }

    [JsonPropertyName("canRun")]
    public bool CanRun { get; set; }
}
