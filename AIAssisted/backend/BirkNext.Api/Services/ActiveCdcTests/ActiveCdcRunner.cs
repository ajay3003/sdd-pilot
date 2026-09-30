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

    public async Task<ActiveCdcRun> ExecuteAsync(ActiveCdcRun run, IntegrationPlatform platform, string? environmentType, string? targetUrl, IReadOnlyList<SyntheticCdcEvent> events, CancellationToken ct)
    {
        try
        {
            return events.Count > 1 ? await ExecuteReplayAsync(run, platform, environmentType, targetUrl, events, ct)
                : await ExecuteCoreAsync(run, platform, environmentType, targetUrl, events[0], ct);
        }
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

    // ── Same PersonPK replay ────────────────────────────────────────────────────────────────────────────────────────────
    // Strictly sequential A → A2 → B, one send attempt each. After every send: where it landed (partitions whose last-enqueued position
    // moved) and whether the consumer checkpoint passed it, per partition. Checkpoints are never compared across partitions, and a
    // checkpoint past an event means the adapter finished that flush (ingested, fault-queued or skipped) — never that a row is right.

    private static readonly (ActiveCdcStepKind Send, ActiveCdcStepKind Observe, string Snapshot)[] ReplayStages =
    [
        (ActiveCdcStepKind.SendA, ActiveCdcStepKind.ObserveA, "T1 — after A"),
        (ActiveCdcStepKind.SendReplay, ActiveCdcStepKind.ObserveReplay, "T2 — after A2"),
        (ActiveCdcStepKind.SendControl, ActiveCdcStepKind.ObserveControl, "T3 — after B"),
    ];

    private async Task<ActiveCdcRun> ExecuteReplayAsync(ActiveCdcRun run, IntegrationPlatform platform, string? environmentType, string? targetUrl, IReadOnlyList<SyntheticCdcEvent> events, CancellationToken ct)
    {
        var hub = run.Destination.EventHub!;
        var group = run.Destination.ConsumerGroup;
        var position = await metadata.GetHubAsync(platform, hub, ct);
        var baseline = group is null ? null : await checkpoints.GetAsync(platform, hub, group, ct);
        run = Snapshot(run, "T0 — before A", position, baseline);
        run = Add(run, ActiveCdcStepKind.BaselineCaptured, position.IsAvailable ? ActiveCdcEvidenceState.Observed : ActiveCdcEvidenceState.Unavailable,
            position.IsAvailable ? $"Partitions: {string.Join(", ", position.Value!.Partitions.Select(p => $"{p.PartitionId}@{p.LastEnqueuedSequenceNumber}"))}." : $"Partition positions unavailable: {position.Reason}", MetadataSource);
        await store.UpdateAsync(run, CancellationToken.None);

        for (var i = 0; i < ReplayStages.Length; i++)
        {
            var (sendKind, observeKind, snapshotLabel) = ReplayStages[i];
            var label = run.Messages[i].Label;
            if (ct.IsCancellationRequested)
                return await FinishReplayAsync(run with { CancellationRequested = true }, i, ActiveCdcRunStatus.Cancelled, i == 0 ? "Cancelled before A. Nothing was sent."
                    : $"Cancelled before {label}. {string.Join(", ", run.Messages.Take(i).Select(m => m.Label))} had already been sent and cannot be unsent; {label} and later messages were not sent.");
            var (approved, reason) = policy.Approve(environmentType, run.Destination.NamespaceFqdn, hub, targetUrl);
            if (approved is null)
                return await FinishReplayAsync(run, i, i == 0 ? ActiveCdcRunStatus.Blocked : ActiveCdcRunStatus.Inconclusive,
                    i == 0 ? $"Re-check before sending failed: {reason} Nothing was sent." : $"Re-check before {label} failed: {reason} Earlier messages were sent; the sequence is incomplete.");

            run = run with { SendAttempted = true };
            await store.UpdateAsync(run, CancellationToken.None);
            var outcome = await sender.SendAsync(approved, events[i], TimeSpan.FromSeconds(policy.Options.SendTimeoutSeconds), CancellationToken.None);
            var sentAt = clock.GetUtcNow();
            var after = await metadata.GetHubAsync(platform, hub, CancellationToken.None);
            var advanced = Advanced(position, after);
            run = WithMessage(run, i, m => m with { SendState = outcome.State, SendDetail = outcome.Detail, SentAt = sentAt, AdvancedPartitions = advanced });
            run = Add(run, sendKind, outcome.State, outcome.Detail, "Event Hubs producer SDK (one attempt, retries disabled)", sentAt);
            await store.UpdateAsync(run, CancellationToken.None);
            if (outcome.State != ActiveCdcEvidenceState.Observed)
            {
                var (status, why) = SendFailure(i, outcome);
                return await FinishReplayAsync(run, i + 1, status, why);
            }
            position = after;

            var observed = await CheckpointPastAsync(platform, hub, group, after.IsAvailable ? advanced : null, run.Destination.ConsumerGroupAssumed, ct);
            run = WithMessage(run, i, m => m with { CheckpointState = observed.State, CheckpointDetail = observed.Detail });
            run = Add(run, observeKind, observed.State, observed.Detail, CheckpointSource);
            run = Snapshot(run, snapshotLabel, after, observed.Last);
            await store.UpdateAsync(run, CancellationToken.None);
        }

        if (ct.IsCancellationRequested)
            return await FinishReplayAsync(run with { CancellationRequested = true }, ReplayStages.Length, ActiveCdcRunStatus.Cancelled,
                "Cancelled while observing B. A, A2 and B had been sent and cannot be unsent; following-event progression was not evaluated.");
        var progression = EvaluateProgression(run.Messages[1], run.Messages[2], run.Destination.ConsumerGroupAssumed);
        run = Add(run, ActiveCdcStepKind.FollowingEventProgression, progression.State, progression.Detail, CheckpointSource);
        return await FinishReplayAsync(run, ReplayStages.Length, progression.Status, progression.Reason);
    }

    /// <summary>
    /// Following-event progression from per-message checkpoint evidence. Observed only when the consumer checkpoint passed the replay (A2) on
    /// its partition(s) AND the following valid event (B) on its partition(s). Missing or unreadable evidence is Partial; a stall is Failed
    /// only when it is attributable (confirmed consumer group, each send moved exactly one partition), otherwise Inconclusive.
    /// </summary>
    public static (ActiveCdcEvidenceState State, ActiveCdcRunStatus Status, string Detail, string Reason) EvaluateProgression(ActiveCdcMessageEvidence replay, ActiveCdcMessageEvidence control, bool consumerGroupAssumed)
    {
        var states = new[] { replay.CheckpointState, control.CheckpointState };
        if (states.Any(s => s is ActiveCdcEvidenceState.Unavailable or ActiveCdcEvidenceState.NotAuthorized))
            return (ActiveCdcEvidenceState.Unavailable, ActiveCdcRunStatus.Partial, "Checkpoint evidence could not be read for A2 and/or B, so progression after the replay is not assessable.",
                "All three messages were accepted by Event Hub, but no reliable run-specific consumer-progression evidence was available, so the result is Partial.");
        if (states.Any(s => s is ActiveCdcEvidenceState.NotAssessed))
            return (ActiveCdcEvidenceState.NotAssessed, ActiveCdcRunStatus.Partial, "The partition position of A2 and/or B could not be located, so progression after the replay is not assessable.",
                "All three messages were accepted by Event Hub, but no reliable run-specific consumer-progression evidence was available, so the result is Partial.");
        if (states.All(s => s == ActiveCdcEvidenceState.Observed))
            return (ActiveCdcEvidenceState.Observed, ActiveCdcRunStatus.Passed,
                $"The consumer checkpoint passed the replay A2 (partition {string.Join(", ", replay.AdvancedPartitions.Keys)}) and the following valid event B (partition {string.Join(", ", control.AdvancedPartitions.Keys)}).",
                "All required expectations were met: A, A2 and B were accepted, A2 was an exact replay of A, and the consumer checkpoint passed both the replay and the following valid event.");
        var attributable = !consumerGroupAssumed && replay.AdvancedPartitions.Count == 1 && control.AdvancedPartitions.Count == 1;
        return attributable
            ? (ActiveCdcEvidenceState.NotObserved, ActiveCdcRunStatus.Failed,
                "The consumer checkpoint did not pass the replay and/or the following valid event within the bounded window, on partitions attributable to this run.",
                "Required expectation not met: the following valid event did not progress past the consumer within the bounded window after the replay.")
            : (ActiveCdcEvidenceState.NotObserved, ActiveCdcRunStatus.Inconclusive,
                "The consumer checkpoint did not pass A2 and/or B within the bounded window, but the stall is not attributable to this run (consumer group is a configured assumption, or other traffic moved more than one partition).",
                "Progression after the replay was not observed, but the evidence cannot distinguish a stalled consumer from lag or an unconfirmed consumer group.");
    }

    private static (ActiveCdcRunStatus Status, string Reason) SendFailure(int index, EventHubTestSendOutcome outcome)
    {

        if (outcome.Ambiguous)
            return (ActiveCdcRunStatus.Inconclusive, index switch
            {
                0 => $"A's send outcome is unknown ({outcome.Detail}) — it was not re-sent. The replay count cannot be known, so A2 and B were not sent.",
                1 => $"A2's send outcome is unknown ({outcome.Detail}) — it was not re-sent. Whether the replay happened is unknown, so B was not sent.",
                _ => $"B's send outcome is unknown ({outcome.Detail}) — it was not re-sent. Progression after the replay cannot be claimed.",
            });
        if (index == 0 && outcome.State == ActiveCdcEvidenceState.NotAuthorized) return (ActiveCdcRunStatus.Blocked, $"{outcome.Detail} Nothing was sent.");
        return (ActiveCdcRunStatus.Failed, index switch
        {
            0 => $"A transport failure: Event Hub did not accept A ({outcome.Detail}). A2 and B were not sent.",
            1 => $"Replay transport failure: A2 was not accepted ({outcome.Detail}), so replay processing was never exercised. B was not sent.",
            _ => $"Control transport failure: B was not accepted ({outcome.Detail}). The consumer's state after the replay is not judged.",
        });

    }

    private async Task<(ActiveCdcEvidenceState State, string Detail, EvidenceResult<CheckpointEvidence>? Last)> CheckpointPastAsync(IntegrationPlatform platform, string hub, string? group,
        IReadOnlyDictionary<string, long>? advanced, bool assumed, CancellationToken ct)
    {
        if (group is null) return (ActiveCdcEvidenceState.NotAssessed, "No consumer group is configured or assumed for this integration.", null);
        if (advanced is null) return (ActiveCdcEvidenceState.Unavailable, "Partition positions are unavailable, so the event cannot be located on a partition.", null);
        if (advanced.Count == 0) return (ActiveCdcEvidenceState.NotAssessed, "No partition's last-enqueued position moved across the send (metadata can lag), so the event cannot be located.", null);
        var caveat = assumed ? $" Consumer group {group} is a configured assumption, not confirmed as the adapter's." : "";
        var shared = advanced.Count > 1 ? $" {advanced.Count} partitions moved in the interval (other traffic); all must pass." : "";
        var deadline = clock.GetUtcNow().AddSeconds(policy.Options.ObservationSeconds);
        while (true)
        {
            var last = await checkpoints.GetAsync(platform, hub, group, CancellationToken.None);
            if (!last.IsAvailable)
                return (last.State == IntegrationEvidenceState.NotAuthorized ? ActiveCdcEvidenceState.NotAuthorized : ActiveCdcEvidenceState.Unavailable, $"Checkpoints unavailable: {last.Reason}{caveat}", last);
            var pending = advanced.Where(a => last.Value!.Partitions.FirstOrDefault(p => p.PartitionId == a.Key)?.SequenceNumber is not { } seq || seq < a.Value).Select(a => a.Key).ToList();
            if (pending.Count == 0)
                return (ActiveCdcEvidenceState.Observed, $"Checkpoint of {group} at or past the post-send position on partition {string.Join(", ", advanced.Select(a => $"{a.Key} (≥ {a.Value})"))}. The adapter finished that flush; this does not show what it stored.{shared}{caveat}", last);
            if (ct.IsCancellationRequested || clock.GetUtcNow() >= deadline)
                return (ct.IsCancellationRequested ? ActiveCdcEvidenceState.NotObserved : ActiveCdcEvidenceState.TimedOut,
                    $"{(ct.IsCancellationRequested ? "Observation cancelled" : $"Not reached within {policy.Options.ObservationSeconds} s")}: partition {string.Join(", ", pending)} not yet checkpointed past the event.{shared}{caveat}", last);
            try { await Task.Delay(TimeSpan.FromSeconds(policy.Options.PollSeconds), clock, ct); }
            catch (OperationCanceledException) { /* the next iteration records the cancellation */ }
        }
    }

    /// <summary>Partitions whose last-enqueued position moved from <paramref name="before"/> to <paramref name="after"/>, with the post-send position. Empty when either read is unavailable.</summary>
    internal static Dictionary<string, long> Advanced(EvidenceResult<EventHubRuntimeMetadata> before, EvidenceResult<EventHubRuntimeMetadata> after)
    {
        var moved = new Dictionary<string, long>();
        if (!before.IsAvailable || !after.IsAvailable) return moved;
        foreach (var p in after.Value!.Partitions)
        {
            var prior = before.Value!.Partitions.FirstOrDefault(x => x.PartitionId == p.PartitionId);
            if (prior is null || p.LastEnqueuedSequenceNumber > prior.LastEnqueuedSequenceNumber) moved[p.PartitionId] = p.LastEnqueuedSequenceNumber;
        }
        return moved;
    }

    private static ActiveCdcRun Snapshot(ActiveCdcRun run, string label, EvidenceResult<EventHubRuntimeMetadata> position, EvidenceResult<CheckpointEvidence>? checkpoint)
    {
        var ids = (position.Value?.Partitions.Select(p => p.PartitionId) ?? []).Concat(checkpoint?.Value?.Partitions.Select(p => p.PartitionId) ?? []).Distinct().OrderBy(id => id, StringComparer.Ordinal);
        var snapshot = new ActiveCdcCheckpointSnapshot
        {
            Label = label, CapturedAt = DateTimeOffset.UtcNow,
            State = position.IsAvailable || checkpoint?.IsAvailable == true ? ActiveCdcEvidenceState.Observed : ActiveCdcEvidenceState.Unavailable,
            Detail = $"{(position.IsAvailable ? "Partition positions read" : $"Partition positions unavailable: {position.Reason}")}; " +
                $"{(checkpoint is null ? "checkpoints not read" : checkpoint.IsAvailable ? "checkpoints read" : $"checkpoints unavailable: {checkpoint.Reason}")}.",
            Partitions = ids.Select(id => new ActiveCdcPartitionPosition(id, position.Value?.Partitions.FirstOrDefault(p => p.PartitionId == id)?.LastEnqueuedSequenceNumber,
                checkpoint?.Value?.Partitions.FirstOrDefault(p => p.PartitionId == id)?.SequenceNumber)).ToList(),
        };
        return run with { CheckpointSnapshots = [.. run.CheckpointSnapshots.Where(c => c.Label != label), snapshot] };
    }

    private static ActiveCdcRun WithMessage(ActiveCdcRun run, int index, Func<ActiveCdcMessageEvidence, ActiveCdcMessageEvidence> change) =>
        run with { Messages = run.Messages.Select((m, i) => i == index ? change(m) : m).ToList() };

    /// <summary>Records the stages that were not reached and the domains BirkNext cannot see, then completes the run.</summary>
    private async Task<ActiveCdcRun> FinishReplayAsync(ActiveCdcRun run, int reached, ActiveCdcRunStatus status, string reason)
    {
        for (var i = reached; i < ReplayStages.Length; i++)
        {
            if (run.Step(ReplayStages[i].Send) is null) run = Add(run, ReplayStages[i].Send, ActiveCdcEvidenceState.NotAssessed, $"Not sent — the sequence stopped before {run.Messages[i].Label}.", "Not reached");
            if (run.Step(ReplayStages[i].Observe) is null) run = Add(run, ReplayStages[i].Observe, ActiveCdcEvidenceState.NotAssessed, "Not observed — the message was not sent.", "Not reached");
        }
        for (var i = 0; i < Math.Min(reached, ReplayStages.Length); i++)
            if (run.Step(ReplayStages[i].Observe) is null) run = Add(run, ReplayStages[i].Observe, ActiveCdcEvidenceState.NotAssessed, "Not observed — the sequence stopped after this send.", "Not reached");
        if (run.Step(ActiveCdcStepKind.FollowingEventProgression) is null)
            run = Add(run, ActiveCdcStepKind.FollowingEventProgression, ActiveCdcEvidenceState.NotAssessed, "Not evaluated — the sequence did not complete.", CheckpointSource);
        run = Add(run, ActiveCdcStepKind.CorrelatedReplayError, ActiveCdcEvidenceState.NotAssessed,
            "No run-correlated error source exists: adapter errors are logged with partition and BirkId only (no run id), and its fault queue is in the adapter's own database. No correlated replay error evidence was observed — this is not a statement that no error occurred.",
            "Not available");
        return await CompleteAsync(run, status, reason);
    }

    /// <summary>
    /// Domains no Active CDC scenario can observe. Whatever produced a step for one of these, the stored state is Not assessed (Not tested for
    /// natural-key duplicates): send success and checkpoint progression can never promote them.
    /// </summary>
    internal static readonly IReadOnlyDictionary<ActiveCdcStepKind, (ActiveCdcEvidenceState State, string Detail)> Unobservable = new Dictionary<ActiveCdcStepKind, (ActiveCdcEvidenceState, string)>
    {
        [ActiveCdcStepKind.PersonPersisted] = (ActiveCdcEvidenceState.NotAssessed, "No approved read-only query can see a standalone Person (all Person reads are child-scoped); BirkNext does not read the Person database."),
        [ActiveCdcStepKind.DatabaseIdempotency] = (ActiveCdcEvidenceState.NotAssessed, "Not observable from BirkNext. Developer tests cover same-payload ingestion at database level (see Developer coverage)."),
        [ActiveCdcStepKind.PersonRowCount] = (ActiveCdcEvidenceState.NotAssessed, "Not observable from BirkNext (no Person read path)."),
        [ActiveCdcStepKind.OverwriteBehavior] = (ActiveCdcEvidenceState.NotAssessed, "Not observable from BirkNext (no Person read path)."),
        [ActiveCdcStepKind.OutboxCreated] = (ActiveCdcEvidenceState.NotAssessed, "BirkNext does not read the Person module database."),
        [ActiveCdcStepKind.OutboxDuplication] = (ActiveCdcEvidenceState.NotAssessed, "BirkNext does not read the Person module database."),
        [ActiveCdcStepKind.ServiceBusDelivered] = (ActiveCdcEvidenceState.NotAssessed, "BirkNext does not receive from Service Bus (no data-plane receive)."),
        [ActiveCdcStepKind.SubscriberProcessed] = (ActiveCdcEvidenceState.NotAssessed, "Subscriber processing is not observable from BirkNext."),
        [ActiveCdcStepKind.NaturalKeyDuplicate] = (ActiveCdcEvidenceState.NotTested, "Not tested: this scenario reuses the same PersonPK; it sends no second PersonPK with the same natural identity."),
    };

    /// <summary>Applies <see cref="Unobservable"/>: replay runs get every such step; any run's existing step is forced back to its only allowed state.</summary>
    internal static ActiveCdcRun EnforceBoundaries(ActiveCdcRun run)
    {
        var replay = run.Scenario.Id == ActiveCdcScenarioCatalog.SamePersonPkReplayId;
        foreach (var (kind, (state, detail)) in Unobservable)
        {
            var step = run.Step(kind);
            if (step is null && !replay) continue;
            if (step is { } existing && existing.State == state) continue;
            run = Add(run, kind, state, step is null ? detail : step.Detail, step?.Source is { Length: > 0 } source ? source : "Not available", step?.CapturedAt);
        }
        return run;
    }

    private async Task<ActiveCdcRun> CompleteAsync(ActiveCdcRun run, ActiveCdcRunStatus status, string reason)
    {
        run = EnforceBoundaries(run);
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
