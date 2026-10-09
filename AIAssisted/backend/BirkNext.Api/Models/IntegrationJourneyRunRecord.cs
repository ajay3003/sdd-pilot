namespace BirkNext.Api.Models;

/// <summary>Generic integration-journey history row. ResultJson holds the payload-free <c>IntegrationJourneyRun</c> contract.</summary>
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
