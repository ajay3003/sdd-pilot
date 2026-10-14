namespace BirkNext.Api.Models;

/// <summary>Generic integration-journey history row. ResultJson holds the <c>IntegrationJourneyRun</c> contract: no unredacted payload, only redacted payload evidence snapshots.</summary>
public sealed class IntegrationJourneyRunRecord
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string PackId { get; init; } = "";
    public string JourneyId { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string State { get; set; } = "";
    public string ResultJson { get; set; } = "";
}
