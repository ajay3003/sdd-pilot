namespace BirkNext.Api.Models;

/// <summary>Generic active-event history row. ResultJson contains the body-free ActiveEventRunResult contract.</summary>
public sealed class ActiveEventRunRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "";
    public string ResultJson { get; set; } = "";
}
