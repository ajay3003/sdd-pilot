namespace BirkNext.Api.Models;

/// <summary>
/// One active CDC test run. Written as Running BEFORE the send (durable intent), then completed once; a completed row is never changed.
/// <see cref="SyntheticPersonPk"/> is unique per environment so a reserved synthetic key is never reused. No payload or credential is stored.
/// </summary>
public sealed class ActiveCdcRunRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public int? SyntheticPersonPk { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "";
    public string ResultJson { get; set; } = "";
}
