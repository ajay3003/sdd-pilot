using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting.Observation;

/// <summary>
/// Aggregate consumer activity from the integration's configured telemetry (Log Analytics / Application Insights, bounded aggregate
/// query for the consumer role). Generated events are not propagated into consumer telemetry (transport properties such as the run id are
/// not known to survive), so this is AggregateOnly: activity after the send shows the consumer was working, never that an event was handled.
/// </summary>
public sealed class TelemetryConsumerActivityProvider(ITelemetryEvidenceSource telemetry, TimeProvider clock) : IActiveEventConsumerActivityProvider
{
    private const string Source = "Consumer telemetry (aggregate query, read-only)";

    public bool CanObserve(ActiveEventObservationContext context, out string reason)
    {
        if (context.Platform is null) { reason = "The integration has no platform with monitoring references."; return false; }
        if (string.IsNullOrWhiteSpace(context.Target.ConsumerRole)) { reason = "The integration names no consumer role (container app) to query telemetry for."; return false; }
        var status = telemetry.Describe(context.Platform);
        if (status.State != IntegrationEvidenceState.Available) { reason = $"Consumer telemetry: {status.Reason}"; return false; }
        reason = "";
        return true;
    }

    public async Task<ActiveEventStageEvidence> ObserveAsync(ActiveEventObservationContext context, DateTimeOffset sentFrom, TimeSpan window, CancellationToken ct)
    {
        // Telemetry ingestion lags; the query is made once, at the end of the observation window, over the smallest supported window (1 h).
        var remaining = sentFrom + window - clock.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            try { await Task.Delay(remaining, clock, ct); }
            catch (OperationCanceledException) { /* observe what is there now */ }
        }
        var result = await telemetry.GetConsumerAsync(context.Platform!, context.Target.ConsumerRole!, 1, CancellationToken.None);
        if (!result.IsAvailable)
            return Evidence(ActiveEventEvidenceStatus.Unavailable, $"Consumer telemetry unavailable: {result.Reason}");
        var value = result.Value!;
        return value.LastActivity is { } last && last >= sentFrom
            ? Evidence(ActiveEventEvidenceStatus.Observed,
                $"Aggregate consumer activity for {context.Target.ConsumerRole} after the send (last activity {last:u}; {value.ProcessingTraces} processing trace(s), {value.Exceptions} exception(s) in the last hour). " +
                "Aggregate only: this is not evidence that a specific generated event was processed.")
            : Evidence(ActiveEventEvidenceStatus.NotObserved,
                $"No consumer activity for {context.Target.ConsumerRole} was recorded after the send (last activity {(value.LastActivity is { } at ? at.ToString("u") : "none in the last hour")}). Telemetry can lag; this is not a failure.");

        ActiveEventStageEvidence Evidence(ActiveEventEvidenceStatus status, string detail) => new()
        {
            Stage = ActiveEventEvidenceStage.ConsumerActivityObserved, Status = status, Detail = detail, EvidenceSource = Source,
            CorrelationQuality = status == ActiveEventEvidenceStatus.Observed ? ActiveEventCorrelationQuality.AggregateOnly : ActiveEventCorrelationQuality.Unavailable,
            CapturedAt = clock.GetUtcNow(),
        };
    }
}
