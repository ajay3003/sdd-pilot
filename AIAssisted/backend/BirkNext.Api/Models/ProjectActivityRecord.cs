namespace BirkNext.Api.Models;

/// <summary>
/// One persisted project activity event (table <c>project_activity</c>). Scoped by the stable logical project/application id; an imported
/// package is only a related reference. Insert-only: an event is never rewritten. Unique per (project context, idempotency key).
/// </summary>
public sealed class ProjectActivityRecord
{
    public Guid Id { get; set; }
    public string ProjectContextId { get; set; } = "";
    public string? ProjectName { get; set; }
    public string ActivityType { get; set; } = "";
    public string Status { get; set; } = "";
    public string SourceModule { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public string? Outcome { get; set; }
    public string? RelatedEntityType { get; set; }
    public string? RelatedEntityId { get; set; }
    public string? PackageId { get; set; }
    public string? PackageName { get; set; }
    public string? EnvironmentId { get; set; }
    public string? EnvironmentName { get; set; }
    public string? Route { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}
