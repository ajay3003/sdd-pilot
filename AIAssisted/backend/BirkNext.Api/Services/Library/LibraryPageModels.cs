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
    public required string Title { get; set; }

    /// <summary>Page description/subtitle</summary>
    public required string Description { get; set; }

    /// <summary>Overall readiness status of the page</summary>
    public required LibraryStatus ReadinessStatus { get; set; }

    /// <summary>Optional: Library sections/groups (e.g., "Loaded Artifacts", "Available Projects")</summary>
    public List<LibrarySection> Sections { get; set; } = [];

    /// <summary>Library items (artifacts, scenarios, projects)</summary>
    public List<LibraryItem> Items { get; set; } = [];

    /// <summary>Available actions on this page</summary>
    public List<LibraryAction> Actions { get; set; } = [];

    /// <summary>Required inputs to run this page (e.g., "Specification")</summary>
    public List<string> RequiredInputs { get; set; } = [];

    /// <summary>Which required inputs are missing</summary>
    public List<string> MissingInputs { get; set; } = [];

    /// <summary>Overall summary and statistics</summary>
    public required LibrarySummary Summary { get; set; }
}

/// <summary>
/// A section within a library page.
/// </summary>
public class LibrarySection
{
    public required string Name { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public List<LibraryItem> Items { get; set; } = [];
}

/// <summary>
/// A library item (artifact, scenario, project).
/// </summary>
public class LibraryItem
{
    /// <summary>Item identifier/name</summary>
    public required string Name { get; set; }

    /// <summary>Item type (e.g., "Specification", "Scenario", "Sample Project")</summary>
    public required string Type { get; set; }

    /// <summary>Status of this item (e.g., "Loaded", "Available", "Empty")</summary>
    public required LibraryStatus Status { get; set; }

    /// <summary>Source of item (e.g., "Workspace", "Sample", "Import")</summary>
    public string? Source { get; set; }

    /// <summary>Brief description</summary>
    public string? Description { get; set; }

    /// <summary>Related artifact kind if applicable (e.g., "Specification", "Plan")</summary>
    public string? ArtifactKind { get; set; }

    /// <summary>Last updated timestamp</summary>
    public DateTimeOffset? LastUpdated { get; set; }

    /// <summary>Available actions on this item</summary>
    public List<LibraryAction> Actions { get; set; } = [];
}

/// <summary>
/// An action available in the library.
/// </summary>
public class LibraryAction
{
    /// <summary>Action name (e.g., "Load", "Replace", "Export")</summary>
    public required string Name { get; set; }

    /// <summary>Action status (e.g., "Available", "Disabled")</summary>
    public required LibraryStatus Status { get; set; }

    /// <summary>Is this action enabled?</summary>
    public required bool Enabled { get; set; }

    /// <summary>Why action is disabled (if applicable)</summary>
    public string? Reason { get; set; }

    /// <summary>What will happen if action is executed</summary>
    public string? ExpectedEffect { get; set; }
}

/// <summary>
/// Overall summary and statistics for the library page.
/// </summary>
public class LibrarySummary
{
    /// <summary>Total number of items</summary>
    public int TotalItems { get; set; }

    /// <summary>Number of items with warnings</summary>
    public int WarningCount { get; set; }

    /// <summary>Number of items that are empty/missing</summary>
    public int EmptyCount { get; set; }

    /// <summary>Number of available actions</summary>
    public int AvailableActionsCount { get; set; }

    /// <summary>Human-readable status message</summary>
    public required string StatusMessage { get; set; }

    /// <summary>Can user perform meaningful actions on this page?</summary>
    public bool HasAvailableActions { get; set; }
}
