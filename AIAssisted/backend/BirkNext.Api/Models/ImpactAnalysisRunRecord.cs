namespace BirkNext.Api.Models;

/// <summary>Immutable, secret-free result snapshot for one Impact Analysis run.</summary>
public sealed class ImpactAnalysisRunRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = string.Empty;
    public string? ProjectImportId { get; set; }
    public string ProjectDisplayName { get; set; } = string.Empty;
    public Guid BaselineSnapshotId { get; set; }
    public Guid CurrentSnapshotId { get; set; }
    public string BaselineFingerprint { get; set; } = string.Empty;
    public string CurrentFingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ResultJson { get; set; } = string.Empty;
}
