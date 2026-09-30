namespace BirkNext.Api.Models;

/// <summary>Insert-only implementation evidence. Neither upload bytes nor source contents are retained.</summary>
public sealed class IqrSourceSnapshotRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public string EvidenceJson { get; init; } = "";
}
