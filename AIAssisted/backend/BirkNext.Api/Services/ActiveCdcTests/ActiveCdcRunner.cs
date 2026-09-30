using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveCdcTests;

/// <summary>
/// Executes one prepared run in the background: read-only baseline, a final policy re-check, exactly one send, then bounded read-only
/// observation. Each stage is recorded on its own — an accepted send is not a consumed event, a checkpoint past the event is not a stored
/// Person — and the result is conservative: without a verified Person read, the best outcome is Partial.
/// </summary>
public sealed class ActiveCdcRunner(ActiveCdcPolicy policy, IEventHubTestSender sender, IEventHubMetadataSource metadata, ICheckpointEvidenceSource checkpoints,
    ActiveCdcRunStore store, TimeProvider clock, ILogger<ActiveCdcRunner> logger)
{
    private const string MetadataSource = "Event Hub metadata (read-only properties)";
    private const string CheckpointSource = "Blob checkpoint store (read-only listing)";

    public async Task<ActiveCdcRun> ExecuteAsync(ActiveCdcRun run, IntegrationPlatform platform, string? environmentType, string? targetUrl, SyntheticCdcEvent synthetic, CancellationToken ct)
    {
        try { return await ExecuteCoreAsync(run, platform, environmentType, targetUrl, synthetic, ct); }
        catch (Exception ex)
        {
            // Never leave a run Running because of an unexpected fault; whether the event went out is what the run says it knows.
            var latest = await store.GetAsync(run.RunId, CancellationToken.None) ?? run;
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                return await CompleteAsync(latest with { CancellationRequested = true }, ActiveCdcRunStatus.Cancelled, latest.SendAttempted
                    ? "Cancelled after the send was requested. The event may already be in Event Hub; cancelling does not unsend it."
                    : "Cancelled before the send. Nothing was sent.");
            logger.LogError("Active CDC run {RunId} faulted: {Type}", run.RunId, ex.GetType().Name);
            return await CompleteAsync(latest, latest.CancellationRequested || ex is OperationCanceledException ? ActiveCdcRunStatus.Cancelled : ActiveCdcRunStatus.Inconclusive,
                latest.SendAttempted ? $"The run stopped unexpectedly after the send was requested ({ex.GetType().Name}). The event may have been sent; it was not re-sent."
                    : $"The run stopped unexpectedly before sending ({ex.GetType().Name}). Nothing was sent.");
        }
    }

    private async Task<ActiveCdcRun> ExecuteCoreAsync(ActiveCdcRun run, IntegrationPlatform platform, string? environmentType, string? targetUrl, SyntheticCdcEvent synthetic, CancellationToken ct)
    {
        var hub = run.Destination.EventHub!;
        var group = run.Destination.ConsumerGroup;

        // 1. Read-only baseline (never blocks: unavailable evidence stays unavailable, not zero).
        var before = await metadata.GetHubAsync(platform, hub, ct);
        var baseCheckpoint = group is null ? null : await checkpoints.GetAsync(platform, hub, group, ct);
        run = Add(run, ActiveCdcStepKind.BaselineCaptured, before.IsAvailable ? ActiveCdcEvidenceState.Observed : ActiveCdcEvidenceState.Unavailable,
            (before.IsAvailable ? $"Partitions: {string.Join(", ", before.Value!.Partitions.Select(p => $"{p.PartitionId}@{p.LastEnqueuedSequenceNumber}"))}." : $"Partition positions unavailable: {before.Reason}") +
            (baseCheckpoint is null ? " No consumer group — checkpoints not read." : baseCheckpoint.IsAvailable ? $" Checkpoints read for {group}." : $" Checkpoints unavailable: {baseCheckpoint.Reason}"),
            MetadataSource);
        await store.UpdateAsync(run, CancellationToken.None);

        if (ct.IsCancellationRequested)
            return await CompleteAsync(run with { CancellationRequested = true }, ActiveCdcRunStatus.Cancelled, "Cancelled before the send. Nothing was sent.");

        // 2. Final guard immediately before the send (configuration may have changed since the run started).
        var (approved, reason) = policy.Approve(environmentType, run.Destination.NamespaceFqdn, hub, targetUrl);
        if (approved is null)
            return await CompleteAsync(Add(run, ActiveCdcStepKind.EventHubSend, ActiveCdcEvidenceState.NotAssessed, $"Not sent: {reason}", "Backend policy (re-checked before send)"),
                ActiveCdcRunStatus.Blocked, $"Re-check before sending failed: {reason} Nothing was sent.");

        // 3. Exactly one send. From here on the event may exist in Event Hub — record that before waiting for the answer.
        run = run with { SendAttempted = true };
        await store.UpdateAsync(run, CancellationToken.None);
        var outcome = await sender.SendAsync(approved, synthetic, TimeSpan.FromSeconds(policy.Options.SendTimeoutSeconds), CancellationToken.None);
        var sentAt = clock.GetUtcNow();
        run = Add(run, ActiveCdcStepKind.EventHubSend, outcome.State, outcome.Detail, "Event Hubs producer SDK (one attempt, retries disabled)", sentAt);
        await store.UpdateAsync(run, CancellationToken.None);

        if (outcome.State != ActiveCdcEvidenceState.Observed)
        {
            run = Downstream(run, "Not assessed — the send was not confirmed as accepted.");
            return outcome.State == ActiveCdcEvidenceState.NotAuthorized
                ? await CompleteAsync(run, ActiveCdcRunStatus.Blocked, $"{outcome.Detail}")
                : outcome.Ambiguous
                    ? await CompleteAsync(run, ActiveCdcRunStatus.Inconclusive, $"{outcome.Detail} Downstream evidence was not collected.")
                    : await CompleteAsync(run, ActiveCdcRunStatus.Failed, $"Event Hub did not accept the synthetic event: {outcome.Detail}");
        }

        // 4. Partition position after the send (not attributable to this event alone: other producers may write too).
        var after = await metadata.GetHubAsync(platform, hub, CancellationToken.None);
        var advanced = new Dictionary<string, long>();
        if (before.IsAvailable && after.IsAvailable)
        {
            foreach (var p in after.Value!.Partitions)
            {
                var prior = before.Value!.Partitions.FirstOrDefault(x => x.PartitionId == p.PartitionId);
                if (prior is null || p.LastEnqueuedSequenceNumber > prior.LastEnqueuedSequenceNumber) advanced[p.PartitionId] = p.LastEnqueuedSequenceNumber;
            }
            run = Add(run, ActiveCdcStepKind.PartitionPosition, advanced.Count > 0 ? ActiveCdcEvidenceState.Observed : ActiveCdcEvidenceState.NotObserved,
                advanced.Count > 0 ? $"Advanced on partition {string.Join(", ", advanced.Select(a => $"{a.Key} (to {a.Value})"))}. Other producers may have written in the same interval."
                    : "No partition's last-enqueued position moved yet (metadata can lag the send).", MetadataSource);
        }
        else run = Add(run, ActiveCdcStepKind.PartitionPosition, ActiveCdcEvidenceState.Unavailable, $"Partition positions unavailable: {(before.IsAvailable ? after.Reason : before.Reason)}", MetadataSource);
        await store.UpdateAsync(run, CancellationToken.None);

        // 5. Consumer checkpoint: bounded read-only polling. Observed only when every partition that advanced is checkpointed at or past its post-send position.
        run = await ObserveCheckpointAsync(run, platform, hub, group, advanced, ct);

        run = Add(run, ActiveCdcStepKind.ConsumerTelemetry, ActiveCdcEvidenceState.NotAssessed,
            "Aggregate consumer telemetry cannot be tied to this event: the adapter reads only the event body (the BirkNextRunId property is not propagated) and its per-person success log is Debug level. No telemetry is not a failure.",
            "Application Insights (not queried for this run)");
        run = Downstream(run, null);
        var cancelled = ct.IsCancellationRequested;
        return await CompleteAsync(run with { CancellationRequested = run.CancellationRequested || cancelled },
            cancelled ? ActiveCdcRunStatus.Cancelled : ActiveCdcRunStatus.Partial,
            cancelled ? "Cancelled while observing. The synthetic event had already been accepted by Event Hub; cancelling does not unsend it."
                : "Event Hub accepted the synthetic event. Person persistence, outbox, Service Bus delivery and subscriber processing are not assessed, so the result is Partial.");
    }

    private async Task<ActiveCdcRun> ObserveCheckpointAsync(ActiveCdcRun run, IntegrationPlatform platform, string hub, string? group, IReadOnlyDictionary<string, long> advanced, CancellationToken ct)
    {
        if (group is null)
            return Add(run, ActiveCdcStepKind.ConsumerCheckpoint, ActiveCdcEvidenceState.NotAssessed, "No consumer group is configured or assumed for this integration.", CheckpointSource);
        if (advanced.Count == 0)
            return Add(run, ActiveCdcStepKind.ConsumerCheckpoint, ActiveCdcEvidenceState.NotAssessed, "The event's partition position is unknown, so a checkpoint cannot be compared with it.", CheckpointSource);
        var caveat = run.Destination.ConsumerGroupAssumed ? $" Consumer group {group} is a configured assumption, not confirmed as the adapter's." : "";
        var deadline = clock.GetUtcNow().AddSeconds(policy.Options.ObservationSeconds);
        EvidenceResult<CheckpointEvidence>? last = null;
        while (true)
        {
            last = await checkpoints.GetAsync(platform, hub, group, CancellationToken.None);
            if (!last.IsAvailable)
                return Add(run, ActiveCdcStepKind.ConsumerCheckpoint, last.State == IntegrationEvidenceState.NotAuthorized ? ActiveCdcEvidenceState.NotAuthorized : ActiveCdcEvidenceState.Unavailable,
                    $"Checkpoints unavailable: {last.Reason}{caveat}", CheckpointSource);
            var pending = advanced.Where(a => last.Value!.Partitions.FirstOrDefault(p => p.PartitionId == a.Key)?.SequenceNumber is not { } seq || seq < a.Value).Select(a => a.Key).ToList();
            if (pending.Count == 0)
                return Add(run, ActiveCdcStepKind.ConsumerCheckpoint, ActiveCdcEvidenceState.Observed,
                    $"Consumer group {group} checkpointed at or past the post-send position on partition {string.Join(", ", advanced.Keys)}. The consumer finished with the event — which includes discard and fault-queue outcomes; it does not show a stored Person.{caveat}",
                    CheckpointSource);
            if (ct.IsCancellationRequested || clock.GetUtcNow() >= deadline)
                return Add(run, ActiveCdcStepKind.ConsumerCheckpoint, ct.IsCancellationRequested ? ActiveCdcEvidenceState.NotObserved : ActiveCdcEvidenceState.TimedOut,
                    $"{(ct.IsCancellationRequested ? "Observation cancelled" : $"Not reached within {policy.Options.ObservationSeconds} s")}: partition {string.Join(", ", pending)} not yet checkpointed past the event. Late checkpoints are common (batch/checkpoint intervals); this is not a failure.{caveat}",
                    CheckpointSource);
            try { await Task.Delay(TimeSpan.FromSeconds(policy.Options.PollSeconds), clock, ct); }
            catch (OperationCanceledException) { /* the next iteration records the cancellation */ }
        }
    }

    /// <summary>The stages Phase 1 cannot see. Explicitly Not assessed — never implied by absence, never a failure.</summary>
    private static ActiveCdcRun Downstream(ActiveCdcRun run, string? because)
    {
        string Why(string phase1) => because ?? phase1;
        if (run.Step(ActiveCdcStepKind.PartitionPosition) is null && because is not null)
            run = Add(run, ActiveCdcStepKind.PartitionPosition, ActiveCdcEvidenceState.NotAssessed, because, MetadataSource);
        if (run.Step(ActiveCdcStepKind.ConsumerCheckpoint) is null && because is not null)
            run = Add(run, ActiveCdcStepKind.ConsumerCheckpoint, ActiveCdcEvidenceState.NotAssessed, because, CheckpointSource);
        if (run.Step(ActiveCdcStepKind.ConsumerTelemetry) is null && because is not null)
            run = Add(run, ActiveCdcStepKind.ConsumerTelemetry, ActiveCdcEvidenceState.NotAssessed, because, "Application Insights (not queried for this run)");
        run = Add(run, ActiveCdcStepKind.PersonPersisted, ActiveCdcEvidenceState.NotAssessed,
            Why($"No reliable read-only Person verification exists (no standalone Person read endpoint; the ingestion success log is Debug level). Expected PersonId {run.Fixture?.ExpectedPersonId} is recorded for a later manual check."),
            "Not available in Phase 1");
        run = Add(run, ActiveCdcStepKind.OutboxCreated, ActiveCdcEvidenceState.NotAssessed, Why("BirkNext does not read the Person module database."), "Not available in Phase 1");
        run = Add(run, ActiveCdcStepKind.ServiceBusDelivered, ActiveCdcEvidenceState.NotAssessed, Why("BirkNext does not receive from Service Bus (no data-plane receive)."), "Not available in Phase 1");
        return Add(run, ActiveCdcStepKind.SubscriberProcessed, ActiveCdcEvidenceState.NotAssessed, Why("Subscriber processing is not observable from BirkNext."), "Not available in Phase 1");
    }

    private async Task<ActiveCdcRun> CompleteAsync(ActiveCdcRun run, ActiveCdcRunStatus status, string reason)
    {
        var observed = run.Steps.Where(s => s.State == ActiveCdcEvidenceState.Observed).Select(s => $"{ActiveCdcLabels.Step(s.Kind)}: {s.Detail}").ToList();
        var notAssessed = run.Steps.Where(s => s.State != ActiveCdcEvidenceState.Observed).Select(s => $"{ActiveCdcLabels.Step(s.Kind)} — {ActiveCdcLabels.State(s.State)}: {s.Detail}").ToList();
        var completed = run with
        {
            Status = status, StatusReason = reason, CompletedAt = clock.GetUtcNow(), WhatWasTested = observed,
            WhatWasNotAssessed = [.. notAssessed, "No Service Bus message was sent or received; no checkpoint, consumer group, database or Azure configuration was changed."],
            Limitations =
            [
                .. run.Limitations,
                "Source compatibility is not deployment correlation: the deployed adapter version was not confirmed.",
                .. run.SendAttempted ? new[] { "The synthetic Person (if created) stays in the environment; BirkNext does not delete data." } : [],
            ],
        };
        if (!await store.UpdateAsync(completed, CancellationToken.None))
            completed = completed with { Limitations = [.. completed.Limitations, "The completed run could not be saved to history."] };
        return completed;
    }

    private static ActiveCdcRun Add(ActiveCdcRun run, ActiveCdcStepKind kind, ActiveCdcEvidenceState state, string detail, string source, DateTimeOffset? at = null) =>
        run with { Steps = [.. run.Steps.Where(s => s.Kind != kind), ActiveCdcTestService.Step(kind, state, detail, source, at ?? DateTimeOffset.UtcNow)] };
}
