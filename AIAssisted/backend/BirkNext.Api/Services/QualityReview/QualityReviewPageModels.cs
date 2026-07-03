namespace BirkNext.Api.Services.QualityReview;

/// <summary>Status of a quality review pack or check</summary>
public enum QualityReviewStatus
{
    /// <summary>All prerequisites satisfied; ready to run</summary>
    Available,
    /// <summary>Required input or configuration missing; cannot run</summary>
    Blocked,
    /// <summary>Intentionally not active for this audit</summary>
    Disabled,
    /// <summary>Enabled and selected by user</summary>
    Selected,
    /// <summary>Can run but with reduced/degraded analysis</summary>
    Warning,
    /// <summary>Unrecoverable error; cannot run</summary>
    Fail
}

/// <summary>A single check within a quality review</summary>
public sealed class QualityReviewCheck
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public QualityReviewStatus Status { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>A quality review pack (collection of related checks)</summary>
public sealed class QualityReviewPack
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public QualityReviewStatus Status { get; init; }
    public string Description { get; init; } = string.Empty;
    public List<string> RequiredInputs { get; init; } = [];
    public List<string> MissingInputs { get; init; } = [];
}

/// <summary>A section in the page layout</summary>
public sealed class QualityReviewSection
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<QualityReviewCheck> Checks { get; init; } = [];
}

/// <summary>Summary of page readiness and contents</summary>
public sealed class QualityReviewSummary
{
    public int TotalPacks { get; init; }
    public int AvailablePacks { get; init; }
    public int BlockedPacks { get; init; }
    public int SelectedPacks { get; init; }
    public int TotalChecks { get; init; }
    public bool CanRun { get; init; }
    public string ReadinessMessage { get; init; } = string.Empty;
}

/// <summary>The complete structured model for a Quality Review page</summary>
public sealed class QualityReviewPageModel
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public QualityReviewStatus ReadinessStatus { get; init; }
    public List<QualityReviewSection> Sections { get; init; } = [];
    public List<QualityReviewPack> ReviewPacks { get; init; } = [];
    public List<QualityReviewCheck> Checks { get; init; } = [];
    public List<string> Actions { get; init; } = [];
    public QualityReviewSummary Summary { get; init; } = new();
}
