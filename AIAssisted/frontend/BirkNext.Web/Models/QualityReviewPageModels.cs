using System.Text.Json.Serialization;

namespace BirkNext.Web.Models.QualityReview;

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
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public QualityReviewStatus Status { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}

/// <summary>A quality review pack (collection of related checks)</summary>
public sealed class QualityReviewPack
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public QualityReviewStatus Status { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("requiredInputs")]
    public List<string> RequiredInputs { get; init; } = [];

    [JsonPropertyName("missingInputs")]
    public List<string> MissingInputs { get; init; } = [];
}

/// <summary>A section in the page layout</summary>
public sealed class QualityReviewSection
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("checks")]
    public List<QualityReviewCheck> Checks { get; init; } = [];
}

/// <summary>Summary of page readiness and contents</summary>
public sealed class QualityReviewSummary
{
    [JsonPropertyName("totalPacks")]
    public int TotalPacks { get; init; }

    [JsonPropertyName("availablePacks")]
    public int AvailablePacks { get; init; }

    [JsonPropertyName("blockedPacks")]
    public int BlockedPacks { get; init; }

    [JsonPropertyName("selectedPacks")]
    public int SelectedPacks { get; init; }

    [JsonPropertyName("totalChecks")]
    public int TotalChecks { get; init; }

    [JsonPropertyName("canRun")]
    public bool CanRun { get; init; }

    [JsonPropertyName("readinessMessage")]
    public string ReadinessMessage { get; init; } = string.Empty;
}

/// <summary>The complete structured model for a Quality Review page</summary>
public sealed class QualityReviewPageModel
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    [JsonPropertyName("readinessStatus")]
    public QualityReviewStatus ReadinessStatus { get; init; }

    [JsonPropertyName("sections")]
    public List<QualityReviewSection> Sections { get; init; } = [];

    [JsonPropertyName("reviewPacks")]
    public List<QualityReviewPack> ReviewPacks { get; init; } = [];

    [JsonPropertyName("checks")]
    public List<QualityReviewCheck> Checks { get; init; } = [];

    [JsonPropertyName("actions")]
    public List<string> Actions { get; init; } = [];

    [JsonPropertyName("summary")]
    public QualityReviewSummary Summary { get; init; } = new();
}
