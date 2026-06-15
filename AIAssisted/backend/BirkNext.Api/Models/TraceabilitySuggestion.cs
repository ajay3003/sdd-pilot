namespace BirkNext.Api.Models;

public class TraceabilitySuggestion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Test scenario Id (the artifact that covers).</summary>
    public Guid SourceId { get; set; }
    public string SourceKind { get; set; } = TraceLinkArtifactKind.Scenario;

    /// <summary>Requirement scenario Id (the artifact being covered).</summary>
    public Guid TargetId { get; set; }
    public string TargetKind { get; set; } = TraceLinkArtifactKind.Scenario;

    public TraceLinkType LinkType { get; set; } = TraceLinkType.Covers;

    public TraceabilitySuggestionStatus Status { get; set; } = TraceabilitySuggestionStatus.Suggested;

    /// <summary>Confidence score 0.0–1.0.</summary>
    public double Confidence { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>JSON array of matching signal descriptions.</summary>
    public string SignalsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
}
