namespace BirkNext.Api.Models;

/// <summary>Immutable source discovery JSON, with independent review decisions. Approved Target Environment settings stay in their existing store.</summary>
public sealed class SecurityExpectationDiscoveryRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public Guid SourceSnapshotId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string EvidenceJson { get; init; } = "";
    public string DecisionsJson { get; set; } = "[]";
    public int Revision { get; set; }
}
