namespace BirkNext.Api.Services.Review;

/// <summary>
/// Status of a review page.
/// Ready = required artifact available and analysis complete
/// Empty = no artifact loaded yet, not failure
/// Blocked = required artifact missing from workspace
/// Warning = degraded but usable
/// Fail = actual analysis/runtime error only
/// </summary>
public enum ReviewStatus
{
    Ready = 0,
    Empty = 1,
    Blocked = 2,
    Warning = 3,
    Fail = 4
}

/// <summary>
/// Structured model for a Review page (Dashboard, Explorer pages).
/// </summary>
public class ReviewPageModel
{
    /// <summary>Page title</summary>
    public required string Title { get; set; }

    /// <summary>Page description/subtitle</summary>
    public required string Description { get; set; }

    /// <summary>Overall readiness status of the page</summary>
    public required ReviewStatus ReadinessStatus { get; set; }

    /// <summary>Artifact kind this page reviews (e.g., "Specification", "Constitution")</summary>
    public string? ArtifactKind { get; set; }

    /// <summary>Optional: Sections of review content</summary>
    public List<ReviewSection> Sections { get; set; } = [];

    /// <summary>Analysis/review results</summary>
    public List<ReviewResult> Results { get; set; } = [];

    /// <summary>Available actions on this page</summary>
    public List<ReviewAction> Actions { get; set; } = [];

    /// <summary>Required inputs to run this page</summary>
    public List<string> RequiredInputs { get; set; } = [];

    /// <summary>Which required inputs are missing</summary>
    public List<string> MissingInputs { get; set; } = [];

    /// <summary>Overall summary and statistics</summary>
    public required ReviewSummary Summary { get; set; }
}

/// <summary>
/// A section within a review page.
/// </summary>
public class ReviewSection
{
    public required string Title { get; set; }
    public string? Description { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.Ready;
    public List<ReviewItem> Items { get; set; } = [];
}

/// <summary>
/// An item within a review section.
/// </summary>
public class ReviewItem
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.Ready;
}

/// <summary>
/// A review/analysis result.
/// </summary>
public class ReviewResult
{
    /// <summary>Result name or title</summary>
    public required string Name { get; set; }

    /// <summary>Category (e.g., "Requirements", "Gaps", "Scenarios", "Validation")</summary>
    public string? Category { get; set; }

    /// <summary>Status of this result</summary>
    public ReviewStatus Status { get; set; } = ReviewStatus.Ready;

    /// <summary>Severity (e.g., "Critical", "Warning", "Info")</summary>
    public string? Severity { get; set; }

    /// <summary>Brief summary</summary>
    public string? Summary { get; set; }

    /// <summary>Detailed findings/information</summary>
    public string? Details { get; set; }

    /// <summary>Recommended action</summary>
    public string? Recommendation { get; set; }

    /// <summary>Related artifacts that this result concerns</summary>
    public List<string> RelatedArtifacts { get; set; } = [];
}

/// <summary>
/// An action available in the review page.
/// </summary>
public class ReviewAction
{
    /// <summary>Action name (e.g., "Upload", "Clear", "Analyze")</summary>
    public required string Name { get; set; }

    /// <summary>Action status</summary>
    public ReviewStatus Status { get; set; } = ReviewStatus.Ready;

    /// <summary>Is this action enabled?</summary>
    public required bool Enabled { get; set; }

    /// <summary>Why action is disabled (if applicable)</summary>
    public string? Reason { get; set; }

    /// <summary>What will happen if action is executed</summary>
    public string? ExpectedEffect { get; set; }
}

/// <summary>
/// Overall summary and statistics for the review page.
/// </summary>
public class ReviewSummary
{
    /// <summary>Total number of results/findings</summary>
    public int TotalResults { get; set; }

    /// <summary>Number of critical findings</summary>
    public int CriticalCount { get; set; }

    /// <summary>Number of warnings</summary>
    public int WarningCount { get; set; }

    /// <summary>Number of info-level findings</summary>
    public int InfoCount { get; set; }

    /// <summary>Human-readable status message</summary>
    public required string StatusMessage { get; set; }

    /// <summary>Can user perform meaningful actions on this page?</summary>
    public bool HasAvailableActions { get; set; }

    /// <summary>Can the analysis/review run given current artifact state?</summary>
    public bool CanRun { get; set; }
}
