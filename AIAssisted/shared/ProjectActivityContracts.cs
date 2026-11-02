namespace BirkNext.ProjectActivity;

// ── Project Activity: what has happened to this project recently ──────────────────────────────────────────────────────
// Persistent, project-scoped history of meaningful events (imports, completed reviews and runs, meaningful failures, configuration).
// Not the browser-session operation list (Activity Center / Current operations), not an audit log, and never a review result:
// an import is "Imported", never reviewed, tested, passed or ready. Navigation, filtering and viewing never create activity.

/// <summary>What happened. Stored by name, so a value added later never renumbers stored rows.</summary>
public enum ProjectActivityType
{
    ImportCompleted, ImportFailed,
    SourceAnalysisCompleted,
    SpecificationReviewCompleted, DocumentQualityReviewCompleted, ApiQualityReviewCompleted, IntegrationQualityReviewCompleted,
    PerformanceRunCompleted, PerformanceRunFailed,
    ConnectedSystemConfigured, ProjectEvidenceConfigured, TargetEnvironmentConfigured, TestDataChanged,
    DeliverySnapshotImported, DeliveryReportGenerated, BaselineChanged,
    PerformanceValidationFailed, PerformanceTlsTrustRepaired,
}

/// <summary>The badge of an event. Imported is not reviewed; Completed keeps the review's own outcome in <see cref="ProjectActivityItem.Outcome"/>.</summary>
public enum ProjectActivityStatus { Imported, Attention, Completed, Failed, Configured, Generated }

/// <summary>Coarse filter groups (Imports, Reviews, Runs, Configuration, Delivery).</summary>
public enum ProjectActivityCategory { Import, Review, Run, Configuration, Delivery }

/// <summary>
/// One event to record. <see cref="ProjectContextId"/> is the stable logical application/project id (never a display name, route, archive
/// name or tab); <see cref="PackageId"/> optionally names the imported package it concerns. <see cref="IdempotencyKey"/> makes the producer's
/// retries one event (same context + key = the stored event). The server sets the time. Text fields are short facts, never payloads.
/// </summary>
public sealed record ProjectActivityRecordRequest
{
    public string ProjectContextId { get; init; } = "";
    public string? ProjectName { get; init; }
    public ProjectActivityType Type { get; init; }
    public ProjectActivityStatus Status { get; init; }
    /// <summary>The module that produced it ("Import Project", "API Quality Review").</summary>
    public string SourceModule { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>One line ("Supporting/shared source · 4 documents · Source Analysis Partial · 3 items need attention").</summary>
    public string? Summary { get; init; }
    /// <summary>The review's own outcome when it has one ("2 findings", "Blocked") — preserved, never normalized to success.</summary>
    public string? Outcome { get; init; }
    public string? RelatedEntityType { get; init; }
    public string? RelatedEntityId { get; init; }
    public string? PackageId { get; init; }
    public string? PackageName { get; init; }
    public string? EnvironmentId { get; init; }
    public string? EnvironmentName { get; init; }
    /// <summary>Where the event is shown in full (relative app route).</summary>
    public string? Route { get; init; }
    public string IdempotencyKey { get; init; } = "";
}

/// <summary>One stored event as the Dashboard lists it, newest first.</summary>
public sealed record ProjectActivityItem
{
    public Guid Id { get; init; }
    public string ProjectContextId { get; init; } = "";
    public string? ProjectName { get; init; }
    public ProjectActivityType Type { get; init; }
    public ProjectActivityCategory Category { get; init; }
    public ProjectActivityStatus Status { get; init; }
    public string SourceModule { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Summary { get; init; }
    public string? Outcome { get; init; }
    public string? RelatedEntityType { get; init; }
    public string? RelatedEntityId { get; init; }
    public string? PackageId { get; init; }
    public string? PackageName { get; init; }
    public string? EnvironmentId { get; init; }
    public string? EnvironmentName { get; init; }
    public string? Route { get; init; }
    /// <summary>Server time of recording.</summary>
    public DateTimeOffset OccurredAt { get; init; }
}

public static class ProjectActivityCategories
{
    public static ProjectActivityCategory Of(ProjectActivityType type) => type switch
    {
        ProjectActivityType.ImportCompleted or ProjectActivityType.ImportFailed => ProjectActivityCategory.Import,
        ProjectActivityType.PerformanceRunCompleted or ProjectActivityType.PerformanceRunFailed or ProjectActivityType.PerformanceValidationFailed
            or ProjectActivityType.PerformanceTlsTrustRepaired => ProjectActivityCategory.Run,
        ProjectActivityType.ConnectedSystemConfigured or ProjectActivityType.ProjectEvidenceConfigured or ProjectActivityType.TargetEnvironmentConfigured
            or ProjectActivityType.TestDataChanged or ProjectActivityType.BaselineChanged => ProjectActivityCategory.Configuration,
        ProjectActivityType.DeliverySnapshotImported or ProjectActivityType.DeliveryReportGenerated => ProjectActivityCategory.Delivery,
        _ => ProjectActivityCategory.Review,
    };
}
