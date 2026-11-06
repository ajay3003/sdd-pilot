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
    Fail,
    /// <summary>No evidence to decide on here: neither available nor blocked (appended — existing values keep their numbers)</summary>
    NotAssessed
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
    /// <summary>What the readiness is derived from (for example "workspace artifacts"), or "none" when this model holds no evidence and its status
    /// is Not assessed. Null on models built before the field existed.</summary>
    public string? EvidenceAuthority { get; init; }
}

/// <summary>
/// The API / Integration / Frontend page-model builders have no configuration evidence (their detectors are placeholders that always answer
/// "not configured"), and no page reads them: the review pages evaluate readiness with their own engines from the Target Environment. Their
/// answer is therefore Not assessed — never a "Blocked: configure an endpoint" that would read as an authoritative finding.
/// </summary>
public static class PlaceholderPageModel
{
    public static QualityReviewPageModel NotAssessed(QualityReviewPageModel model, string ownerPage) => new()
    {
        Title = model.Title, Description = model.Description, Target = model.Target, Actions = model.Actions,
        ReadinessStatus = QualityReviewStatus.NotAssessed,
        ReviewPacks = [.. model.ReviewPacks.Select(p => new QualityReviewPack
            { Name = p.Name, Category = p.Category, Description = p.Description, RequiredInputs = p.RequiredInputs, MissingInputs = [], Status = QualityReviewStatus.NotAssessed })],
        Checks = [.. model.Checks.Select(Check)],
        Sections = [.. model.Sections.Select(s => new QualityReviewSection { Title = s.Title, Description = s.Description, Checks = [.. s.Checks.Select(Check)] })],
        Summary = new()
        {
            TotalPacks = model.Summary.TotalPacks, TotalChecks = model.Summary.TotalChecks, AvailablePacks = 0, BlockedPacks = 0, CanRun = false,
            ReadinessMessage = $"Not assessed here: this page model holds no configuration evidence. {ownerPage} evaluates readiness from the active Target Environment.",
        },
        EvidenceAuthority = "none",
    };

    private static QualityReviewCheck Check(QualityReviewCheck c) =>
        new() { Name = c.Name, Category = c.Category, Description = c.Description, Status = QualityReviewStatus.NotAssessed };
}
