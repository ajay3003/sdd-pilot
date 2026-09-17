namespace BirkNext.Api.Models;

/// <summary>
/// Persisted Integration Quality Review snapshot. Immutable once written: a later review inserts
/// a new row rather than updating an existing one.
///
/// Follows the QaDeltaReview convention — scalar columns for the fields queried on, and a JSON
/// column for the nested per-integration payload, rather than normalising into child tables.
/// </summary>
public class IntegrationQualitySnapshotRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Authoritative environment identity (profile id where available), never a display label.</summary>
    public string EnvironmentId { get; init; } = string.Empty;

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Schema version of the snapshot payload. Stored explicitly, not inferred from CLR shape.</summary>
    public int SnapshotVersion { get; init; }

    /// <summary>Baseline key algorithm version in force when this row was written.</summary>
    public int BaselineIdentityVersion { get; init; }

    public Guid? PreviousSnapshotId { get; init; }

    /// <summary>Whether the originating review was complete. A partial review is not promoted to a complete baseline.</summary>
    public string Completeness { get; init; } = string.Empty;

    /// <summary>Serialised list of IntegrationSnapshotEntry. Contains no credentials.</summary>
    public string IntegrationsJson { get; init; } = string.Empty;
}
