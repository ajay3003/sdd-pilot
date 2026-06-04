namespace BirkNext.Api.Models;

public class TraceLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>The artifact that covers or links (v1: a Test Scenario's Id).</summary>
    public Guid SourceId { get; set; }

    /// <summary>Discriminates the source artifact type. See <see cref="TraceLinkArtifactKind"/>.</summary>
    public string SourceKind { get; set; } = TraceLinkArtifactKind.Scenario;

    /// <summary>The artifact being covered or linked (v1: a Requirement Scenario's Id).</summary>
    public Guid TargetId { get; set; }

    /// <summary>Discriminates the target artifact type. See <see cref="TraceLinkArtifactKind"/>.</summary>
    public string TargetKind { get; set; } = TraceLinkArtifactKind.Scenario;

    public TraceLinkType LinkType { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public string? Notes { get; set; }
}
