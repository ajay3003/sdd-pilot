using System.Text.Json.Serialization;

namespace BirkNext.Web.Models.Library;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LibraryStatus
{
    Ready = 0,
    Empty = 1,
    Blocked = 2,
    Warning = 3,
    Fail = 4,
    Unavailable = 5
}

public class LibraryPageModel
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("readinessStatus")]
    public LibraryStatus ReadinessStatus { get; set; }

    [JsonPropertyName("sections")]
    public List<LibrarySection> Sections { get; set; } = [];

    [JsonPropertyName("items")]
    public List<LibraryItem> Items { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<LibraryAction> Actions { get; set; } = [];

    [JsonPropertyName("requiredInputs")]
    public List<string> RequiredInputs { get; set; } = [];

    [JsonPropertyName("missingInputs")]
    public List<string> MissingInputs { get; set; } = [];

    [JsonPropertyName("summary")]
    public LibrarySummary Summary { get; set; } = new();
}

public class LibrarySection
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("items")]
    public List<LibraryItem> Items { get; set; } = [];
}

public class LibraryItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public LibraryStatus Status { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("artifactKind")]
    public string? ArtifactKind { get; set; }

    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }

    [JsonPropertyName("actions")]
    public List<LibraryAction> Actions { get; set; } = [];
}

public class LibraryAction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public LibraryStatus Status { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("expectedEffect")]
    public string? ExpectedEffect { get; set; }
}

public class LibrarySummary
{
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("emptyCount")]
    public int EmptyCount { get; set; }

    [JsonPropertyName("availableActionsCount")]
    public int AvailableActionsCount { get; set; }

    [JsonPropertyName("statusMessage")]
    public string StatusMessage { get; set; } = string.Empty;

    [JsonPropertyName("hasAvailableActions")]
    public bool HasAvailableActions { get; set; }
}
