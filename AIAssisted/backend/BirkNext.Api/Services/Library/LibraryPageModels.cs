using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.Library;

/// <summary>
/// Status of a library page or item.
/// Ready = required data/actions available
/// Empty = no artifacts/scenarios/projects yet, not failure
/// Blocked = required input missing
/// Warning = degraded but usable
/// Fail = actual runtime error only
/// Unavailable = feature cannot run in current environment
/// </summary>
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

/// <summary>
/// Structured model for a library page (QA Artifact Library, Create Test Scenario, Sample Projects).
/// </summary>
public class LibraryPageModel
{
    /// <summary>Page title (e.g., "QA Artifact Library")</summary>
    [JsonPropertyName("title")]
    public required string Title { get; set; }

    /// <summary>Page description/subtitle</summary>
    [JsonPropertyName("description")]
    public required string Description { get; set; }

    /// <summary>Overall readiness status of the page</summary>
    [JsonPropertyName("readinessStatus")]
    public required LibraryStatus ReadinessStatus { get; set; }

    /// <summary>Optional: Library sections/groups (e.g., "Loaded Artifacts", "Available Projects")</summary>
    [JsonPropertyName("sections")]
    public List<LibrarySection> Sections { get; set; } = [];

    /// <summary>Library items (artifacts, scenarios, projects)</summary>
    [JsonPropertyName("items")]
    public List<LibraryItem> Items { get; set; } = [];

    /// <summary>Available actions on this page</summary>
    [JsonPropertyName("actions")]
    public List<LibraryAction> Actions { get; set; } = [];

    /// <summary>Required inputs to run this page (e.g., "Specification")</summary>
    [JsonPropertyName("requiredInputs")]
    public List<string> RequiredInputs { get; set; } = [];

    /// <summary>Which required inputs are missing</summary>
    [JsonPropertyName("missingInputs")]
    public List<string> MissingInputs { get; set; } = [];

    /// <summary>Overall summary and statistics</summary>
    [JsonPropertyName("summary")]
    public required LibrarySummary Summary { get; set; }
}

/// <summary>
/// A section within a library page.
/// </summary>
public class LibrarySection
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("items")]
    public List<LibraryItem> Items { get; set; } = [];
}

/// <summary>
/// A library item (artifact, scenario, project).
/// </summary>
public class LibraryItem
{
    /// <summary>Item identifier/name</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Item type (e.g., "Specification", "Scenario", "Sample Project")</summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>Status of this item (e.g., "Loaded", "Available", "Empty")</summary>
    [JsonPropertyName("status")]
    public required LibraryStatus Status { get; set; }

    /// <summary>Source of item (e.g., "Workspace", "Sample", "Import")</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Brief description</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Related artifact kind if applicable (e.g., "Specification", "Plan")</summary>
    [JsonPropertyName("artifactKind")]
    public string? ArtifactKind { get; set; }

    /// <summary>Last updated timestamp</summary>
    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }

    /// <summary>Available actions on this item</summary>
    [JsonPropertyName("actions")]
    public List<LibraryAction> Actions { get; set; } = [];
}

/// <summary>
/// An action available in the library.
/// </summary>
public class LibraryAction
{
    /// <summary>Action name (e.g., "Load", "Replace", "Export")</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Action status (e.g., "Available", "Disabled")</summary>
    [JsonPropertyName("status")]
    public required LibraryStatus Status { get; set; }

    /// <summary>Is this action enabled?</summary>
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; set; }

    /// <summary>Why action is disabled (if applicable)</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>What will happen if action is executed</summary>
    [JsonPropertyName("expectedEffect")]
    public string? ExpectedEffect { get; set; }
}

/// <summary>
/// Overall summary and statistics for the library page.
/// </summary>
public class LibrarySummary
{
    /// <summary>Total number of items</summary>
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    /// <summary>Number of items with warnings</summary>
    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    /// <summary>Number of items that are empty/missing</summary>
    [JsonPropertyName("emptyCount")]
    public int EmptyCount { get; set; }

    /// <summary>Number of available actions</summary>
    [JsonPropertyName("availableActionsCount")]
    public int AvailableActionsCount { get; set; }

    /// <summary>Human-readable status message</summary>
    [JsonPropertyName("statusMessage")]
    public required string StatusMessage { get; set; }

    /// <summary>Can user perform meaningful actions on this page?</summary>
    [JsonPropertyName("hasAvailableActions")]
    public bool HasAvailableActions { get; set; }
}
