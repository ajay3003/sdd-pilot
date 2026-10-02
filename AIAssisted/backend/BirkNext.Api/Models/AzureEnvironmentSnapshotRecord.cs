namespace BirkNext.Api.Models;

/// <summary>An immutable Azure Environment Analysis snapshot (observed inventory, topology, capabilities). Holds no token, credential or secret value.</summary>
public sealed class AzureEnvironmentSnapshotRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public string Status { get; init; } = "";
    public string SummaryJson { get; init; } = "";
    public string SnapshotJson { get; init; } = "";
}
