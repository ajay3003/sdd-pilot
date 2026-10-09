using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting.Observation;

/// <summary>
/// Consumer continuity for Event Hub targets, read-only: partition last-enqueued positions and the consumer group's blob checkpoints before
/// the first send (baseline), positions after the sends, then bounded polling until every partition that advanced is checkpointed at or past
/// its post-send position. A checkpoint past the events shows the consumer moved on — including discards and fault outcomes — never that an
/// event was handled or stored, and other producers on the same hub can advance positions too (attribution is stated, not assumed).
/// </summary>
public sealed class EventHubCheckpointContinuityProvider(
    IEventHubMetadataSource metadata, ICheckpointEvidenceSource checkpoints, ActiveEventPolicy policy, TimeProvider clock) : IActiveEventContinuityProvider
{
    private const string Source = "Event Hub partition metadata + blob checkpoint store (read-only)";

    private sealed record Baseline(IReadOnlyDictionary<string, long> Positions, IReadOnlyDictionary<string, long?> Checkpoints);

    public bool CanObserve(ActiveEventObservationContext context, out string reason)
    {
        if (!string.Equals(context.Target.TransportType, "EventHub", StringComparison.OrdinalIgnoreCase)) { reason = "Not an Event Hub target."; return false; }
        if (context.Platform is null || string.IsNullOrWhiteSpace(context.Target.Resource)) { reason = "The integration has no Event Hubs platform/hub."; return false; }
        if (string.IsNullOrWhiteSpace(context.Target.Consumer)) { reason = "No consumer group is configured or assumed, so checkpoints cannot be read."; return false; }
        var positions = metadata.Describe(context.Platform);
        if (positions.State != IntegrationEvidenceState.Available) { reason = $"Partition positions: {positions.Reason}"; return false; }
        var checkpointStatus = checkpoints.Describe(context.Platform);
        if (checkpointStatus.State != IntegrationEvidenceState.Available) { reason = $"Checkpoints: {checkpointStatus.Reason}"; return false; }
        reason = "";
        return true;
    }

    public async Task<ActiveEventContinuityBaseline> CaptureBaselineAsync(ActiveEventObservationContext context, CancellationToken ct)
    {
        var platform = context.Platform!;
        var hub = context.Target.Resource!;
        var before = await metadata.GetHubAsync(platform, hub, ct);
        var checkpoint = await checkpoints.GetAsync(platform, hub, context.Target.Consumer!, ct);
        if (!before.IsAvailable)
            return new(false, $"Partition positions unavailable before the send: {before.Reason}", clock.GetUtcNow());
        var state = new Baseline(
            before.Value!.Partitions.ToDictionary(p => p.PartitionId, p => p.LastEnqueuedSequenceNumber, StringComparer.Ordinal),
            checkpoint.IsAvailable ? checkpoint.Value!.Partitions.ToDictionary(p => p.PartitionId, p => p.SequenceNumber, StringComparer.Ordinal) : new Dictionary<string, long?>());
        return new(true, $"Baseline: partitions {string.Join(", ", state.Positions.Select(p => $"{p.Key}@{p.Value}"))}; " +
            (checkpoint.IsAvailable ? $"checkpoints read for {context.Target.Consumer}." : $"checkpoints unavailable ({checkpoint.Reason})."), clock.GetUtcNow(), state);
    }

    public async Task<ActiveEventStageEvidence> ObserveAfterSendAsync(ActiveEventObservationContext context, ActiveEventContinuityBaseline baseline,
        IReadOnlyList<ActiveEventCorrelation> sent, TimeSpan window, CancellationToken ct)
    {
        var platform = context.Platform!;
        var hub = context.Target.Resource!;
        var group = context.Target.Consumer!;
        var caveat = context.Target.ConsumerAssumed ? $" Consumer group {group} is a configured assumption, not confirmed as the consumer's." : "";
        if (!baseline.Available || baseline.State is not Baseline before)
            return Evidence(ActiveEventEvidenceStatus.Unavailable, $"{baseline.Detail}{caveat}", ActiveEventCorrelationQuality.Unavailable);

        var after = await metadata.GetHubAsync(platform, hub, CancellationToken.None);
        if (!after.IsAvailable)
            return Evidence(ActiveEventEvidenceStatus.Unavailable, $"Partition positions unavailable after the send: {after.Reason}{caveat}", ActiveEventCorrelationQuality.Unavailable);
        var advanced = after.Value!.Partitions
            .Where(p => !before.Positions.TryGetValue(p.PartitionId, out var prior) || p.LastEnqueuedSequenceNumber > prior)
            .ToDictionary(p => p.PartitionId, p => p.LastEnqueuedSequenceNumber, StringComparer.Ordinal);
        if (advanced.Count == 0)
            return Evidence(ActiveEventEvidenceStatus.NotObserved, $"No partition's last-enqueued position moved after the {sent.Count} send(s) yet (metadata can lag), so continuity cannot be compared.{caveat}", ActiveEventCorrelationQuality.Unavailable);
        // Attribution: one advanced partition per sent event at most is consistent with this run alone; more means other traffic moved too.
        var quality = advanced.Count <= sent.Count ? ActiveEventCorrelationQuality.Moderate : ActiveEventCorrelationQuality.Weak;
        var attribution = quality == ActiveEventCorrelationQuality.Moderate ? "" : " More partitions advanced than events were sent: other producers wrote in the same interval.";

        var deadline = clock.GetUtcNow() + window;
        while (true)
        {
            var current = await checkpoints.GetAsync(platform, hub, group, CancellationToken.None);
            if (!current.IsAvailable)
                return Evidence(ActiveEventEvidenceStatus.Unavailable, $"Checkpoints unavailable: {current.Reason}{caveat}", ActiveEventCorrelationQuality.Unavailable);
            var pending = advanced.Where(a => current.Value!.Partitions.FirstOrDefault(p => p.PartitionId == a.Key)?.SequenceNumber is not { } seq || seq < a.Value).Select(a => a.Key).ToList();
            if (pending.Count == 0)
                return Evidence(ActiveEventEvidenceStatus.Observed,
                    $"{baseline.Detail} Consumer group {group} checkpointed at or past the post-send position on partition {string.Join(", ", advanced.Select(a => $"{a.Key} (≥ {a.Value})"))}. " +
                    $"Consumer progression only: it does not show that any event was handled successfully or stored.{attribution}{caveat}", quality);
            if (ct.IsCancellationRequested || clock.GetUtcNow() >= deadline)
                return Evidence(ActiveEventEvidenceStatus.NotObserved,
                    $"{(ct.IsCancellationRequested ? "Observation cancelled" : $"Not reached within {window.TotalSeconds:0} s")}: partition {string.Join(", ", pending)} not yet checkpointed past the run's events. " +
                    $"Late checkpoints are common (batch/checkpoint intervals); this is not a failure.{attribution}{caveat}", quality);
            try { await Task.Delay(TimeSpan.FromSeconds(policy.Options.PollSeconds), clock, ct); }
            catch (OperationCanceledException) { /* the next iteration records the cancellation */ }
        }

        ActiveEventStageEvidence Evidence(ActiveEventEvidenceStatus status, string detail, ActiveEventCorrelationQuality correlation) => new()
        {
            Stage = ActiveEventEvidenceStage.ConsumerContinuityObserved, Status = status, Detail = detail, EvidenceSource = Source,
            CorrelationQuality = correlation, CapturedAt = clock.GetUtcNow(),
        };
    }
}
