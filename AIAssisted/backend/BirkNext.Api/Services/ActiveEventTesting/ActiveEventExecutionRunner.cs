using System.Security.Cryptography;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

public sealed record ActiveEventExecutionResult(
    Guid RunId,
    ActiveEventScenarioDescriptor Scenario,
    ActiveEventTrustedTarget Target,
    IReadOnlyList<GeneratedActiveEvent> Events,
    IReadOnlyList<ActiveEventStageEvidence> Evidence,
    IReadOnlyList<string> Limitations,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public bool TransportAccepted => Events.Count > 0 && Evidence.Count(item => item.Stage == ActiveEventEvidenceStage.TransportAccepted && item.Status == ActiveEventEvidenceStatus.Observed) == Events.Count;

    /// <summary>Creates the durable, body-free lifecycle result shared by all project extensions.</summary>
    public ActiveEventRunResult ToHistoryResult(ActiveEventSourceContractReference sourceContract) => new()
    {
        RunId = RunId,
        Scenario = Scenario,
        Target = Target,
        SourceContract = sourceContract,
        Status = ActiveEventRunOutcome.Status(Scenario, Events.Count, Evidence),
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        Events = Events.OrderBy(item => item.SequenceIndex).Select(item => new ActiveEventGeneratedSummary
        {
            EventId = item.EventId,
            ExtensionId = item.ExtensionId,
            ScenarioId = item.ScenarioId,
            SequenceIndex = item.SequenceIndex,
            Operation = item.Operation,
            ContentType = item.ContentType,
            BodySha256 = item.BodySha256,
            BodyBytes = item.BodyBytes,
            EventKey = item.EventKey,
            PartitionKey = item.PartitionKey,
            Correlation = item.Correlation,
            SafeMetadata = item.SafeDisplayMetadata,
        }).ToArray(),
        Evidence = Evidence,
        Limitations = Limitations,
    };
}

/// <summary>
/// Final status from stage evidence. Transport acceptance is never Completed on its own: Completed needs every event accepted, plus
/// observed continuity when the scenario requires it, plus a verified downstream result for every event when the scenario requires it.
/// </summary>
public static class ActiveEventRunOutcome
{
    public static ActiveEventRunStatus Status(ActiveEventScenarioDescriptor scenario, int eventCount, IReadOnlyList<ActiveEventStageEvidence> evidence)
    {
        if (evidence.Any(item => item.Status == ActiveEventEvidenceStatus.SafetyBlocked)) return ActiveEventRunStatus.SafetyBlocked;
        var transport = evidence.Where(item => item.Stage == ActiveEventEvidenceStage.TransportAccepted).ToList();
        if (transport.Any(item => item.Status == ActiveEventEvidenceStatus.Failed)) return ActiveEventRunStatus.Failed;
        if (eventCount == 0 || transport.Count(item => item.Status == ActiveEventEvidenceStatus.Observed) != eventCount) return ActiveEventRunStatus.Inconclusive;

        var downstream = evidence.Where(item => item.Stage == ActiveEventEvidenceStage.DownstreamVerified).ToList();
        if (downstream.Any(item => item.Status is ActiveEventEvidenceStatus.UnexpectedResult or ActiveEventEvidenceStatus.Failed)) return ActiveEventRunStatus.Failed;
        if (downstream.Any(item => item.Status == ActiveEventEvidenceStatus.Ambiguous)) return ActiveEventRunStatus.Inconclusive;

        var continuityOk = !scenario.RequiresContinuity ||
            evidence.Any(item => item.Stage == ActiveEventEvidenceStage.ConsumerContinuityObserved && item.Status == ActiveEventEvidenceStatus.Observed);
        var downstreamOk = !scenario.RequiresDownstreamVerification ||
            (downstream.Count >= eventCount && downstream.All(item => item.Status == ActiveEventEvidenceStatus.Observed));
        return continuityOk && downstreamOk ? ActiveEventRunStatus.Completed : ActiveEventRunStatus.CompletedWithLimitedEvidence;
    }
}

/// <summary>
/// The single production runner for every scenario provider. Scenario bodies stay opaque; the runner owns sequence validation, the send
/// loop (one attempt per event, never resent), and the evidence stages: consumer activity, continuity (baseline before the first send) and
/// downstream verification — each only as strong as its provider says.
/// </summary>
public sealed class ActiveEventExecutionRunner(
    IActiveEventTransportRegistry transports,
    IEnumerable<IActiveEventConsumerActivityProvider> activityProviders,
    IEnumerable<IActiveEventContinuityProvider> continuityProviders,
    IEnumerable<IActiveEventDownstreamVerifier> downstreamVerifiers,
    TimeProvider clock,
    int maxEventsPerRun = 10)
{
    public async Task<IReadOnlyList<ActiveEventStageEvidence>> ExecuteGeneratedAsync(
        ActiveEventTrustedTarget target, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken cancellationToken,
        Func<ActiveEventStageEvidence, CancellationToken, Task>? onSendAttempted = null)
    {
        var transport = transports.Resolve(target.TransportType);
        if (transport is null)
            return [new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Unavailable, EventId = activeEvent.EventId,
                Detail = $"No active transport provider is registered for {target.TransportType}.", EvidenceSource = "Transport registry", CapturedAt = clock.GetUtcNow() }];
        if (!transport.CanSend(target, out var reason))
            return [new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.SafetyBlocked, EventId = activeEvent.EventId,
                Detail = reason, EvidenceSource = "Transport provider readiness", CapturedAt = clock.GetUtcNow() }];

        var attempted = new ActiveEventStageEvidence { Stage = ActiveEventEvidenceStage.SendAttempted, Status = ActiveEventEvidenceStatus.Observed, EventId = activeEvent.EventId,
            Detail = $"Send requested for event {activeEvent.EventId}.", EvidenceSource = "Active Event runner", CapturedAt = clock.GetUtcNow() };
        if (onSendAttempted is not null) await onSendAttempted(attempted, cancellationToken);
        try
        {
            var result = await transport.SendAsync(target, activeEvent, timeout, cancellationToken);
            return [attempted, result with { Stage = ActiveEventEvidenceStage.TransportAccepted, EventId = activeEvent.EventId }];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return [attempted, new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Ambiguous, EventId = activeEvent.EventId,
                Detail = "Transport timed out. Acceptance is unknown and the event was not resent.", EvidenceSource = target.TransportType, CapturedAt = clock.GetUtcNow() }];
        }
    }

    public async Task<ActiveEventExecutionResult> ExecuteAsync(
        IActiveEventScenarioProvider scenarioProvider,
        ActiveEventScenarioDescriptor scenario,
        ActiveEventScenarioPreparation preparation,
        ActiveEventObservationContext context,
        ActiveEventSourceContractReference sourceContract,
        Guid runId,
        TimeSpan sendTimeout,
        TimeSpan observationWindow,
        CancellationToken cancellationToken,
        Func<ActiveEventRunResult, CancellationToken, Task>? persistProgress = null)
    {
        var target = context.Target;
        var startedAt = clock.GetUtcNow();
        IReadOnlyList<GeneratedActiveEvent> events = [];
        var evidence = new List<ActiveEventStageEvidence>();
        IReadOnlyList<string> limitations = [];
        if (!string.Equals(scenarioProvider.ExtensionId, scenario.ExtensionId, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected scenario does not belong to the resolved extension provider.");
        if (scenario.Support != ActiveEventScenarioSupport.Supported)
            return Blocked(ActiveEventEvidenceStage.Generated, scenario.SupportDetail.Length == 0 ? "This scenario is declared but not assessed; it cannot run." : scenario.SupportDetail, "Scenario support");
        if (!preparation.Compatible)
            return Blocked(ActiveEventEvidenceStage.Generated, preparation.Detail.Length == 0 ? "Scenario prerequisites were not satisfied." : preparation.Detail, "Scenario provider eligibility");
        if (!string.Equals(target.TransportType, scenario.RequiredTransportType, StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(Evidence(ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.Unavailable, "The scenario's required transport does not match the trusted target.", "Active Event runner"));
            return Result(["Required transport does not match the trusted target."]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var generation = await scenarioProvider.GenerateAsync(scenario.ScenarioId, runId, target, sourceContract, cancellationToken);
        events = generation.Events;
        limitations = generation.Limitations;
        if (events.Count == 0) throw new InvalidOperationException("The scenario provider generated no events.");
        if (events.Count != scenario.ExpectedEventCount) throw new InvalidOperationException("The scenario provider generated an event count different from its descriptor.");
        if (events.Count > maxEventsPerRun) throw new InvalidOperationException($"The scenario provider generated more than the {maxEventsPerRun} events one run may send.");
        if (events.Select(item => item.SequenceIndex).Distinct().Count() != events.Count)
            throw new InvalidOperationException("Generated event sequence indexes must be unique within a run.");

        foreach (var item in events.OrderBy(item => item.SequenceIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateGeneratedEvent(item, scenario, runId);
            evidence.Add(Evidence(ActiveEventEvidenceStage.Generated, ActiveEventEvidenceStatus.Observed,
                item.Operation == ActiveEventOperation.Tombstone
                    ? $"Generated tombstone {item.EventId} (no body; key recorded)."
                    : $"Generated event {item.EventId} ({item.Operation}, {item.BodyBytes} bytes, SHA-256 {item.BodySha256[..16]}…).", "Scenario provider", item.EventId));
        }

        await PersistProgressAsync();

        var transport = transports.Resolve(target.TransportType);
        if (transport is null)
        {
            evidence.Add(Evidence(ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.Unavailable,
                $"No active transport provider is registered for {target.TransportType}.", "Transport registry"));
            return Result([.. limitations, $"No active transport provider is registered for {target.TransportType}."]);
        }
        if (!transport.CanSend(target, out var readiness))
        {
            evidence.Add(Evidence(ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.SafetyBlocked, readiness, "Transport provider readiness"));
            return Result([.. limitations, readiness]);
        }

        // Continuity needs the consumer state BEFORE the first send; capture failure leaves continuity unavailable, it never blocks the run.
        var continuity = continuityProviders.FirstOrDefault(provider => provider.CanObserve(context, out _));
        var baseline = continuity is null ? null : await continuity.CaptureBaselineAsync(context, cancellationToken);
        var sentFrom = clock.GetUtcNow();

        foreach (var item in events.OrderBy(item => item.SequenceIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sendEvidence = await ExecuteGeneratedAsync(target, item, sendTimeout, cancellationToken, async (attempted, _) =>
            {
                evidence.Add(attempted);
                await PersistProgressAsync();
            });
            evidence.AddRange(sendEvidence.Where(stage => stage.Stage != ActiveEventEvidenceStage.SendAttempted));
            await PersistProgressAsync();
            if (sendEvidence.Any(stage => stage.Stage == ActiveEventEvidenceStage.TransportAccepted && stage.Status != ActiveEventEvidenceStatus.Observed))
                break; // An unconfirmed send is never retried or followed by a control send.
        }

        if (evidence.Count(item => item.Stage == ActiveEventEvidenceStage.TransportAccepted && item.Status == ActiveEventEvidenceStatus.Observed) != events.Count)
            return Result([.. limitations, "Transport did not confirm every event; no event was automatically resent and no downstream evidence was collected."]);

        // Both observations share one window measured from the first send: continuity polls until progression or the deadline, then
        // aggregate activity is read once at the end of the window.
        evidence.Add(continuity is null || baseline is null
            ? Evidence(ActiveEventEvidenceStage.ConsumerContinuityObserved, ActiveEventEvidenceStatus.NotVerified, ContinuityUnavailable(context), "Continuity provider registry")
            : await continuity.ObserveAfterSendAsync(context, baseline, events.Select(item => item.Correlation).ToArray(), observationWindow, cancellationToken));
        await PersistProgressAsync();

        var activity = activityProviders.FirstOrDefault(provider => provider.CanObserve(context, out _));
        evidence.Add(activity is null
            ? Evidence(ActiveEventEvidenceStage.ConsumerActivityObserved, ActiveEventEvidenceStatus.NotVerified, ActivityUnavailable(context), "Observation provider registry")
            : await activity.ObserveAsync(context, sentFrom, observationWindow, cancellationToken));
        await PersistProgressAsync();

        foreach (var item in events.OrderBy(item => item.SequenceIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verifier = downstreamVerifiers.FirstOrDefault(provider => provider.CanVerify(target, scenario, out _));
            if (verifier is null)
            {
                evidence.Add(Evidence(ActiveEventEvidenceStage.DownstreamVerified, ActiveEventEvidenceStatus.NotVerified,
                    "No downstream verifier is available for this scenario; the downstream result is not verified.", "Downstream verifier registry", item.EventId));
            }
            else
            {
                var result = await verifier.VerifyAsync(target, scenario, item.Correlation, observationWindow, cancellationToken);
                evidence.Add(new ActiveEventStageEvidence
                {
                    Stage = ActiveEventEvidenceStage.DownstreamVerified, Status = DownstreamStatus(result.Outcome), EventId = item.EventId,
                    Detail = result.Reason, EvidenceSource = result.EvidenceSummary.Length == 0 ? "Downstream verifier" : result.EvidenceSummary,
                    CorrelationQuality = result.Outcome == ActiveEventDownstreamOutcome.Verified ? ActiveEventCorrelationQuality.Exact : ActiveEventCorrelationQuality.Unavailable,
                    CapturedAt = clock.GetUtcNow(), Downstream = result,
                });
            }
            await PersistProgressAsync();
        }

        return Result(limitations);

        ActiveEventExecutionResult Blocked(ActiveEventEvidenceStage stage, string detail, string source)
        {
            evidence.Add(Evidence(stage, ActiveEventEvidenceStatus.SafetyBlocked, detail, source));
            return Result([detail]);
        }

        ActiveEventExecutionResult Result(IReadOnlyList<string> resultLimitations) =>
            new(runId, scenario, target, events, evidence.ToArray(), resultLimitations, startedAt, clock.GetUtcNow());

        ActiveEventStageEvidence Evidence(ActiveEventEvidenceStage stage, ActiveEventEvidenceStatus status, string detail, string source, string? eventId = null) =>
            new() { Stage = stage, Status = status, Detail = detail, EvidenceSource = source, CapturedAt = clock.GetUtcNow(), EventId = eventId };

        async Task PersistProgressAsync()
        {
            if (persistProgress is null) return;
            var snapshot = Result(limitations).ToHistoryResult(sourceContract) with { Status = ActiveEventRunStatus.Running, CompletedAt = null };
            await persistProgress(snapshot, cancellationToken);
        }
    }

    private string ActivityUnavailable(ActiveEventObservationContext context)
    {
        var reasons = activityProviders.Select(provider => provider.CanObserve(context, out var reason) ? "" : reason).Where(reason => reason.Length > 0).ToArray();
        return reasons.Length == 0 ? "No consumer activity provider is registered." : $"Consumer activity is not observable: {string.Join(" ", reasons)}";
    }

    private string ContinuityUnavailable(ActiveEventObservationContext context)
    {
        var reasons = continuityProviders.Select(provider => provider.CanObserve(context, out var reason) ? "" : reason).Where(reason => reason.Length > 0).ToArray();
        return reasons.Length == 0 ? "No consumer continuity provider is registered." : $"Consumer continuity is not observable: {string.Join(" ", reasons)}";
    }

    private static ActiveEventEvidenceStatus DownstreamStatus(ActiveEventDownstreamOutcome outcome) => outcome switch
    {
        ActiveEventDownstreamOutcome.Verified => ActiveEventEvidenceStatus.Observed,
        ActiveEventDownstreamOutcome.Unavailable => ActiveEventEvidenceStatus.Unavailable,
        ActiveEventDownstreamOutcome.UnexpectedResult => ActiveEventEvidenceStatus.UnexpectedResult,
        ActiveEventDownstreamOutcome.Ambiguous => ActiveEventEvidenceStatus.Ambiguous,
        _ => ActiveEventEvidenceStatus.NotVerified,
    };

    /// <summary>Generic structural validation of provider output. A tombstone is the only event without a body, and it must carry a key.</summary>
    public static void ValidateGeneratedEvent(GeneratedActiveEvent item, ActiveEventScenarioDescriptor scenario, Guid runId)
    {
        if (string.IsNullOrWhiteSpace(item.EventId) || item.Correlation.RunId != runId || item.Correlation.EventId != item.EventId)
            throw new InvalidOperationException("Generated event identity/correlation is invalid.");
        if (!string.Equals(item.ExtensionId, scenario.ExtensionId, StringComparison.Ordinal) || !string.Equals(item.ScenarioId, scenario.ScenarioId, StringComparison.Ordinal))
            throw new InvalidOperationException("Generated event does not belong to the selected scenario.");
        if (!scenario.SupportedOperations.Contains(item.Operation)) throw new InvalidOperationException("Generated event operation is not declared by the selected scenario.");
        if (item.Operation == ActiveEventOperation.Tombstone)
        {
            if (item.Body.Length != 0 || item.BodyBytes != 0) throw new InvalidOperationException("A tombstone carries no body.");
            if (string.IsNullOrWhiteSpace(item.EventKey)) throw new InvalidOperationException("A tombstone must carry the record key.");
        }
        else if (item.Body.Length == 0 || item.Body.Length != item.BodyBytes) throw new InvalidOperationException("Generated event body size metadata is invalid.");
        var hash = Convert.ToHexString(SHA256.HashData(item.Body)).ToLowerInvariant();
        if (!string.Equals(hash, item.BodySha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(hash, item.Correlation.EventFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Generated event fingerprint does not match its body.");
    }
}
