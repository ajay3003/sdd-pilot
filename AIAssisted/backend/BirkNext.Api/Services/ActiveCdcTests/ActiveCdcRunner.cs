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
            return events.Count > 1 ? await ExecuteSequenceAsync(run, platform, environmentType, targetUrl, events, ct)
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

    // ── Multi-message sequences (Same PersonPK replay, Invalid Person → valid Person) ────────────────────────────────────
    // Strictly sequential, one send attempt each. After every send: where it landed (partitions whose last-enqueued position moved) and
    // whether the consumer checkpoint passed it, per partition. Checkpoints are never compared across partitions, and a checkpoint past an
    // event means the adapter finished that flush (ingested, fault-queued, discarded or skipped) — never that the event was handled well.

    private sealed record SequencePlan(
        string BaselineLabel,
        (ActiveCdcStepKind Send, ActiveCdcStepKind Observe, string Snapshot)[] Stages,
        ActiveCdcStepKind Evaluation,
        Func<int, EventHubTestSendOutcome, (ActiveCdcRunStatus Status, string Reason)> SendFailure,
        Func<ActiveCdcRun, (ActiveCdcEvidenceState State, ActiveCdcRunStatus Status, string Detail, string Reason)> Evaluate,
        string CancelledAfterLast);

    private static readonly SequencePlan ReplayPlan = new(
        "T0 — before A",
        [
            (ActiveCdcStepKind.SendA, ActiveCdcStepKind.ObserveA, "T1 — after A"),
            (ActiveCdcStepKind.SendReplay, ActiveCdcStepKind.ObserveReplay, "T2 — after A2"),
            (ActiveCdcStepKind.SendControl, ActiveCdcStepKind.ObserveControl, "T3 — after B"),
        ],
        ActiveCdcStepKind.FollowingEventProgression, ReplaySendFailure,
        run => EvaluateProgression(run.Messages[1], run.Messages[2], run.Destination.ConsumerGroupAssumed),
        "Cancelled while observing B. A, A2 and B had been sent and cannot be unsent; consumer continuity was not evaluated.");

    private static readonly SequencePlan InvalidThenValidPlan = new(
        "T0 — before I",
        [
            (ActiveCdcStepKind.SendInvalid, ActiveCdcStepKind.ObserveInvalid, "T1 — after invalid I"),
            (ActiveCdcStepKind.SendValidControl, ActiveCdcStepKind.ObserveValidControl, "T2 — after valid V"),
        ],
        ActiveCdcStepKind.ConsumerContinuity, InvalidSendFailure,
        run => EvaluateContinuity(run.Messages[0], run.Messages[1], run.Destination.ConsumerGroupAssumed),
        "Cancelled while observing V. I and V had been sent and cannot be unsent; consumer continuity was not evaluated.");

    private static SequencePlan PlanFor(ActiveCdcRun run) => run.Scenario.Id == ActiveCdcScenarioCatalog.InvalidThenValidId ? InvalidThenValidPlan : ReplayPlan;

    private async Task<ActiveCdcRun> ExecuteSequenceAsync(ActiveCdcRun run, IntegrationPlatform platform, string? environmentType, string? targetUrl, IReadOnlyList<SyntheticCdcEvent> events, CancellationToken ct)
    {
        var plan = PlanFor(run);
        var hub = run.Destination.EventHub!;
        var group = run.Destination.ConsumerGroup;
        var position = await metadata.GetHubAsync(platform, hub, ct);
        var baseline = group is null ? null : await checkpoints.GetAsync(platform, hub, group, ct);
        run = Snapshot(run, plan.BaselineLabel, position, baseline);
        run = Add(run, ActiveCdcStepKind.BaselineCaptured, position.IsAvailable ? ActiveCdcEvidenceState.Observed : ActiveCdcEvidenceState.Unavailable,
            position.IsAvailable ? $"Partitions: {string.Join(", ", position.Value!.Partitions.Select(p => $"{p.PartitionId}@{p.LastEnqueuedSequenceNumber}"))}." : $"Partition positions unavailable: {position.Reason}", MetadataSource);
        await store.UpdateAsync(run, CancellationToken.None);

        for (var i = 0; i < plan.Stages.Length; i++)
        {
            var (sendKind, observeKind, snapshotLabel) = plan.Stages[i];
            var label = run.Messages[i].Label;
            if (ct.IsCancellationRequested)
                return await FinishSequenceAsync(run with { CancellationRequested = true }, plan, i, ActiveCdcRunStatus.Cancelled, i == 0 ? $"Cancelled before {label}. Nothing was sent."
                    : $"Cancelled before {label}. {string.Join(", ", run.Messages.Take(i).Select(m => m.Label))} had already been sent and cannot be unsent; {label} and later messages were not sent.");
            var (approved, reason) = policy.Approve(environmentType, run.Destination.NamespaceFqdn, hub, targetUrl);
            if (approved is null)
                return await FinishSequenceAsync(run, plan, i, i == 0 ? ActiveCdcRunStatus.Blocked : ActiveCdcRunStatus.Inconclusive,
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
                var (status, why) = plan.SendFailure(i, outcome);
                return await FinishSequenceAsync(run, plan, i + 1, status, why);
            }
            position = after;

            var observed = await CheckpointPastAsync(platform, hub, group, after.IsAvailable ? advanced : null, run.Destination.ConsumerGroupAssumed, ct);
            run = WithMessage(run, i, m => m with { CheckpointState = observed.State, CheckpointDetail = observed.Detail });
            run = Add(run, observeKind, observed.State, observed.Detail, CheckpointSource);
            run = Snapshot(run, snapshotLabel, after, observed.Last);
            await store.UpdateAsync(run, CancellationToken.None);
        }

        if (ct.IsCancellationRequested)
            return await FinishSequenceAsync(run with { CancellationRequested = true }, plan, plan.Stages.Length, ActiveCdcRunStatus.Cancelled, plan.CancelledAfterLast);
        var result = plan.Evaluate(run);
        run = Add(run, plan.Evaluation, result.State, result.Detail, CheckpointSource);
        return await FinishSequenceAsync(run, plan, plan.Stages.Length, result.Status, result.Reason);
    }

    /// <summary>The wording of one continuity evaluation; the rules are shared.</summary>
    private sealed record ContinuityTexts(string Unreadable, string Unlocated, string FirstUnlocated, string Observed, string PassReason, string Stall, string StallReason, string Unattributable, string UnattributableReason);

    /// <summary>
    /// Continuity from per-message checkpoint evidence of the event under test (<paramref name="first"/>: A2 or I) and the control after it
    /// (<paramref name="second"/>: B or V). Observed only when the consumer advanced past both, each on its own partition(s). Unreadable or
    /// unlocatable evidence is Partial. A stall is Failed only when attributable (confirmed consumer group, each send moved exactly one
    /// partition); otherwise Inconclusive — the <c>$Default</c> assumption can never produce a Failed.
    /// </summary>
    private static (ActiveCdcEvidenceState State, ActiveCdcRunStatus Status, string Detail, string Reason) Continuity(ActiveCdcMessageEvidence first, ActiveCdcMessageEvidence second, bool consumerGroupAssumed, ContinuityTexts t)
    {
        var states = new[] { first.CheckpointState, second.CheckpointState };
        const string partial = "no reliable run-specific consumer-progression evidence was available, so the result is Partial.";
        if (states.Any(s => s is ActiveCdcEvidenceState.Unavailable or ActiveCdcEvidenceState.NotAuthorized))
            return (ActiveCdcEvidenceState.Unavailable, ActiveCdcRunStatus.Partial, t.Unreadable, $"All messages were accepted by Event Hub, but {partial}");
        if (second.CheckpointState == ActiveCdcEvidenceState.NotAssessed)
            return (ActiveCdcEvidenceState.NotAssessed, ActiveCdcRunStatus.Partial, t.Unlocated, $"All messages were accepted by Event Hub, but {partial}");
        if (first.CheckpointState == ActiveCdcEvidenceState.NotAssessed)
            return (ActiveCdcEvidenceState.NotAssessed, ActiveCdcRunStatus.Partial, t.FirstUnlocated, $"All messages were accepted by Event Hub, but {partial}");
        if (states.All(s => s == ActiveCdcEvidenceState.Observed))
            return (ActiveCdcEvidenceState.Observed, ActiveCdcRunStatus.Passed,
                string.Format(t.Observed, string.Join(", ", first.AdvancedPartitions.Keys), string.Join(", ", second.AdvancedPartitions.Keys)), t.PassReason);
        var attributable = !consumerGroupAssumed && first.AdvancedPartitions.Count == 1 && second.AdvancedPartitions.Count == 1;
        return attributable ? (ActiveCdcEvidenceState.NotObserved, ActiveCdcRunStatus.Failed, t.Stall, t.StallReason)
            : (ActiveCdcEvidenceState.NotObserved, ActiveCdcRunStatus.Inconclusive, t.Unattributable, t.UnattributableReason);
    }

    private static readonly ContinuityTexts ReplayTexts = new(
        "Checkpoint evidence could not be read for A2 and/or B, so progression after the replay is not assessable.",
        "The partition position of A2 and/or B could not be located, so progression after the replay is not assessable.",
        "The partition position of A2 and/or B could not be located, so progression after the replay is not assessable.",
        "The consumer advanced past the replay A2 position (partition {0}) and the control B position (partition {1}). Checkpoint progression only — not evidence that either event was handled successfully.",
        "All required expectations were met: A, A2 and B were accepted, A2 was an exact replay of A, and the consumer advanced past both the replay and the control event positions.",
        "The consumer did not advance past the replay and/or the control event position within the bounded window, on partitions attributable to this run.",
        "Required expectation not met: the consumer did not advance past the control event position within the bounded window after the replay.",
        "The consumer checkpoint did not pass A2 and/or B within the bounded window, but the stall is not attributable to this run (consumer group is a configured assumption, or other traffic moved more than one partition).",
        "Progression after the replay was not observed, but the evidence cannot distinguish a stalled consumer from lag or an unconfirmed consumer group.");

    private static readonly ContinuityTexts InvalidTexts = new(
        "Checkpoint evidence could not be read for I and/or V, so consumer continuity after the invalid input is not assessable.",
        "The valid control's partition position could not be located, so consumer continuity after the invalid input is not assessable.",
        "The consumer advanced past V, but the invalid event's partition could not be located, so continuity on I's partition after the invalid input is not assessable.",
        "The consumer advanced past the invalid event position (partition {0}) and the valid control position (partition {1}). Consumer progression only — not evidence that I was rejected for the right reason or that V was handled.",
        "All required expectations were met: the reviewed invalid event and the valid control were accepted, and the consumer advanced past both positions.",
        "The consumer did not advance past the invalid event and/or the valid control position within the bounded window, on partitions attributable to this run.",
        "Required expectation not met: after the invalid event, the consumer did not advance past the valid control position within the bounded window.",
        "The consumer checkpoint did not pass I and/or V within the bounded window, but the stall is not attributable to this run (consumer group is a configured assumption, or other traffic moved more than one partition).",
        "Consumer continuity after the invalid input was not observed, but the evidence cannot distinguish a stalled consumer from lag or an unconfirmed consumer group.");

    /// <summary>Same PersonPK replay: the consumer must advance past A2 and B, each on its own partition(s).</summary>
    public static (ActiveCdcEvidenceState State, ActiveCdcRunStatus Status, string Detail, string Reason) EvaluateProgression(ActiveCdcMessageEvidence replay, ActiveCdcMessageEvidence control, bool consumerGroupAssumed) =>
        Continuity(replay, control, consumerGroupAssumed, ReplayTexts);

    /// <summary>
    /// Invalid Person → valid Person: the consumer must advance past I on I's partition(s) and past V on V's. When they share a partition,
    /// passing V also passes I. When I could not be located, V alone says nothing about I's partition, so continuity is not assessable (Partial).
    /// </summary>
    public static (ActiveCdcEvidenceState State, ActiveCdcRunStatus Status, string Detail, string Reason) EvaluateContinuity(ActiveCdcMessageEvidence invalid, ActiveCdcMessageEvidence valid, bool consumerGroupAssumed) =>
        Continuity(invalid, valid, consumerGroupAssumed, InvalidTexts);

    private static (ActiveCdcRunStatus Status, string Reason) ReplaySendFailure(int index, EventHubTestSendOutcome outcome)
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

    private static (ActiveCdcRunStatus Status, string Reason) InvalidSendFailure(int index, EventHubTestSendOutcome outcome)
    {
        if (outcome.Ambiguous)
            return (ActiveCdcRunStatus.Inconclusive, index == 0
                ? $"The invalid event's send outcome is unknown ({outcome.Detail}) — it was not re-sent, so the valid control was not sent."
                : $"The valid control's send outcome is unknown ({outcome.Detail}) — it was not re-sent. Consumer continuity cannot be claimed.");
        if (index == 0 && outcome.State == ActiveCdcEvidenceState.NotAuthorized) return (ActiveCdcRunStatus.Blocked, $"{outcome.Detail} Nothing was sent.");
        return (ActiveCdcRunStatus.Failed, index == 0
            ? $"Invalid event transport failure: Event Hub did not accept I ({outcome.Detail}), so the fault scenario never entered the consumer path. V was not sent."
            : $"Control transport failure: V was not accepted ({outcome.Detail}). The consumer's state after the invalid event is not judged.");
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
                return (ActiveCdcEvidenceState.Observed, $"Checkpoint of {group} at or past the post-send position on partition {string.Join(", ", advanced.Select(a => $"{a.Key} (≥ {a.Value})"))}. Consumer progression only: the adapter checkpoints per flush and skips events it cannot handle, so this does not show the event was handled or stored.{shared}{caveat}", last);
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
    private async Task<ActiveCdcRun> FinishSequenceAsync(ActiveCdcRun run, SequencePlan plan, int reached, ActiveCdcRunStatus status, string reason)
    {
        for (var i = reached; i < plan.Stages.Length; i++)
        {
            if (run.Step(plan.Stages[i].Send) is null) run = Add(run, plan.Stages[i].Send, ActiveCdcEvidenceState.NotAssessed, $"Not sent — the sequence stopped before {run.Messages[i].Label}.", "Not reached");
            if (run.Step(plan.Stages[i].Observe) is null) run = Add(run, plan.Stages[i].Observe, ActiveCdcEvidenceState.NotAssessed, "Not observed — the message was not sent.", "Not reached");
        }
        for (var i = 0; i < Math.Min(reached, plan.Stages.Length); i++)
            if (run.Step(plan.Stages[i].Observe) is null) run = Add(run, plan.Stages[i].Observe, ActiveCdcEvidenceState.NotAssessed, "Not observed — the sequence stopped after this send.", "Not reached");
        if (run.Step(plan.Evaluation) is null)
            run = Add(run, plan.Evaluation, ActiveCdcEvidenceState.NotAssessed, "Not evaluated — the sequence did not complete.", CheckpointSource);
        if (plan == ReplayPlan)
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
        [ActiveCdcStepKind.ReplayHandled] = (ActiveCdcEvidenceState.NotAssessed, "Not assessed: a checkpoint past A2 shows consumer progression only — the adapter checkpoints per flush and logs and skips events it cannot handle."),
        [ActiveCdcStepKind.ControlHandled] = (ActiveCdcEvidenceState.NotAssessed, "Not assessed: a checkpoint past B shows consumer progression only, not that B was ingested or stored."),
        [ActiveCdcStepKind.InvalidHandledCorrectly] = (ActiveCdcEvidenceState.NotAssessed, "Not assessed: the source-reviewed path is \"PersonMapper returned null → discarded\", but nothing observable ties that outcome to this event; a checkpoint past I shows consumer progression only."),
        [ActiveCdcStepKind.InvalidDiagnostic] = (ActiveCdcEvidenceState.NotAssessed, "Not assessed: the adapter logs the discard with table and operation only (no PersonPK, message id or run id), so no log line can be tied to this run. An expected invalid-input warning would not be a failure; no correlated error evidence is available."),
        [ActiveCdcStepKind.FaultQueueOutcome] = (ActiveCdcEvidenceState.NotAssessed, "Not assessed: the adapter's fault queue is in its own database, which BirkNext does not read. The reviewed discard path writes no fault entry, but that is not observed."),
        [ActiveCdcStepKind.ConsumerRetry] = (ActiveCdcEvidenceState.NotAssessed, "Not assessed: consumer and application retries are not observable from BirkNext. The producer made one attempt per message; no correlated consumer retry evidence is available."),
        [ActiveCdcStepKind.DatabaseEffects] = (ActiveCdcEvidenceState.NotAssessed, "Not assessed: BirkNext does not read the Person or adapter databases."),
        [ActiveCdcStepKind.ValidControlHandled] = (ActiveCdcEvidenceState.NotAssessed, "Not assessed: a checkpoint past V shows consumer progression only, not that V was ingested or stored."),
    };

    private static readonly ActiveCdcStepKind[] SequenceCommon =
        [ActiveCdcStepKind.PersonPersisted, ActiveCdcStepKind.OutboxCreated, ActiveCdcStepKind.ServiceBusDelivered, ActiveCdcStepKind.SubscriberProcessed];

    /// <summary>The unobservable domains a scenario records explicitly (Normal Person keeps its Phase 1 set, which its runner already writes).</summary>
    internal static IReadOnlyCollection<ActiveCdcStepKind> RecordedUnobservable(string scenarioId) => scenarioId switch
    {
        ActiveCdcScenarioCatalog.SamePersonPkReplayId =>
            [.. SequenceCommon, ActiveCdcStepKind.DatabaseIdempotency, ActiveCdcStepKind.PersonRowCount, ActiveCdcStepKind.OverwriteBehavior, ActiveCdcStepKind.OutboxDuplication,
             ActiveCdcStepKind.NaturalKeyDuplicate, ActiveCdcStepKind.ReplayHandled, ActiveCdcStepKind.ControlHandled],
        ActiveCdcScenarioCatalog.InvalidThenValidId =>
            [.. SequenceCommon, ActiveCdcStepKind.InvalidHandledCorrectly, ActiveCdcStepKind.InvalidDiagnostic, ActiveCdcStepKind.FaultQueueOutcome, ActiveCdcStepKind.ConsumerRetry,
             ActiveCdcStepKind.DatabaseEffects, ActiveCdcStepKind.ValidControlHandled],
        _ => [],
    };

    /// <summary>Applies <see cref="Unobservable"/>: the scenario's own set is always recorded; any run's existing step for one of these kinds is forced back to its only allowed state.</summary>
    internal static ActiveCdcRun EnforceBoundaries(ActiveCdcRun run)
    {
        var recorded = RecordedUnobservable(run.Scenario.Id);
        foreach (var (kind, (state, detail)) in Unobservable)
        {
            var step = run.Step(kind);
            if (step is null && !recorded.Contains(kind)) continue;
            if (step is { } existing && existing.State == state) continue;
            // A step that claimed more than BirkNext can see gets the canonical Not assessed text, not its claim.
            run = Add(run, kind, state, detail, step?.Source is { Length: > 0 } source ? source : "Not available", step?.CapturedAt);
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
