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
    public ActiveEventRunResult ToHistoryResult(ActiveEventSourceContractReference sourceContract)
    {
        var status = Evidence.Any(item => item.Status == ActiveEventEvidenceStatus.SafetyBlocked)
            ? ActiveEventRunStatus.SafetyBlocked
            : Evidence.Any(item => item.Status == ActiveEventEvidenceStatus.Failed)
                ? ActiveEventRunStatus.Failed
                : Evidence.Any(item => item.Status is ActiveEventEvidenceStatus.Ambiguous or ActiveEventEvidenceStatus.Unavailable)
                    ? ActiveEventRunStatus.Inconclusive
                    : Evidence.Where(item => item.Stage == ActiveEventEvidenceStage.DownstreamPersistenceVerified)
                        .All(item => item.Status == ActiveEventEvidenceStatus.Observed) && Evidence.Any(item => item.Stage == ActiveEventEvidenceStage.DownstreamPersistenceVerified)
                        ? ActiveEventRunStatus.Completed
                        : ActiveEventRunStatus.CompletedWithLimitedEvidence;
        return new ActiveEventRunResult
        {
            RunId = RunId,
            Scenario = Scenario,
            Target = Target,
            SourceContract = sourceContract,
            Status = status,
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
                Correlation = item.Correlation,
                SafeMetadata = item.SafeDisplayMetadata,
            }).ToArray(),
            Evidence = Evidence,
            Limitations = Limitations,
        };
    }
}

/// <summary>Transport-neutral event orchestration. Scenario bodies stay opaque; the runner owns only safety-neutral sequence validation and evidence stages.</summary>
public sealed class ActiveEventExecutionRunner(
    IActiveEventTransportRegistry transports,
    IEnumerable<IActiveEventConsumerEvidenceProvider> consumerProviders,
    IEnumerable<IConsumerContinuityEvidenceProvider> continuityProviders,
    IEnumerable<IActiveEventDownstreamVerifier> downstreamVerifiers,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<ActiveEventStageEvidence>> ExecuteGeneratedAsync(
        ActiveEventTrustedTarget target, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken cancellationToken,
        Func<ActiveEventStageEvidence, CancellationToken, Task>? onSendAttempted = null)
    {
        var transport = transports.Resolve(target.TransportType);
        if (transport is null)
            return [new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Unavailable,
                Detail = $"No active transport provider is registered for {target.TransportType}.", EvidenceSource = "Transport registry", CapturedAt = clock.GetUtcNow() }];
        if (!transport.CanSend(target, out var reason))
            return [new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.SafetyBlocked,
                Detail = reason, EvidenceSource = "Transport provider readiness", CapturedAt = clock.GetUtcNow() }];

        var attempted = new ActiveEventStageEvidence { Stage = ActiveEventEvidenceStage.SendAttempted, Status = ActiveEventEvidenceStatus.Observed,
            Detail = $"Send requested for event {activeEvent.EventId}.", EvidenceSource = "Active Event runner", CapturedAt = clock.GetUtcNow() };
        if (onSendAttempted is not null) await onSendAttempted(attempted, cancellationToken);
        try
        {
            var result = await transport.SendAsync(target, activeEvent, timeout, cancellationToken);
            return [attempted, result with { Stage = ActiveEventEvidenceStage.TransportAccepted }];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return [attempted, new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Ambiguous,
                Detail = "Transport timed out. Acceptance is unknown and the event was not resent.", EvidenceSource = target.TransportType, CapturedAt = clock.GetUtcNow() }];
        }
    }

    public async Task<ActiveEventExecutionResult> ExecuteAsync(
        IActiveEventScenarioProvider scenarioProvider,
        ActiveEventScenarioDescriptor scenario,
        ActiveEventScenarioPreparation preparation,
        ActiveEventTrustedTarget target,
        ActiveEventSourceContractReference sourceContract,
        Guid runId,
        TimeSpan sendTimeout,
        TimeSpan observationWindow,
        CancellationToken cancellationToken,
        Func<ActiveEventRunResult, CancellationToken, Task>? persistProgress = null)
    {
        var startedAt = clock.GetUtcNow();
        if (!string.Equals(scenarioProvider.ExtensionId, scenario.ExtensionId, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected scenario does not belong to the resolved extension provider.");
        if (!preparation.Compatible)
            return Result([], [Evidence(ActiveEventEvidenceStage.Generated, ActiveEventEvidenceStatus.SafetyBlocked,
                preparation.Detail.Length == 0 ? "Scenario prerequisites were not satisfied." : preparation.Detail, "Scenario provider eligibility")], ["Scenario prerequisites were not satisfied."]);
        if (!string.Equals(target.TransportType, scenario.RequiredTransportType, StringComparison.OrdinalIgnoreCase))
            return Result([], [Evidence(ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.Unavailable, "The scenario's required transport does not match the trusted target.", "Active Event runner")], ["Required transport does not match the trusted target."]);

        cancellationToken.ThrowIfCancellationRequested();
        var generation = await scenarioProvider.GenerateAsync(scenario.ScenarioId, runId, target, sourceContract, cancellationToken);
        var events = generation.Events;
        if (events.Count == 0) throw new InvalidOperationException("The scenario provider generated no events.");
        if (events.Count != scenario.ExpectedEventCount) throw new InvalidOperationException("The scenario provider generated an event count different from its descriptor.");
        if (events.Select(item => item.SequenceIndex).Distinct().Count() != events.Count)
            throw new InvalidOperationException("Generated event sequence indexes must be unique within a run.");

        var evidence = new List<ActiveEventStageEvidence>();
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateGeneratedEvent(item, scenario, runId);
            evidence.Add(Evidence(ActiveEventEvidenceStage.Generated, ActiveEventEvidenceStatus.Observed,
                $"Generated event {item.EventId} ({item.BodyBytes} bytes, SHA-256 {item.BodySha256[..16]}…).", "Scenario provider"));
        }

        await PersistProgressAsync();

        var transport = transports.Resolve(target.TransportType);
        if (transport is null)
        {
            evidence.Add(Evidence(ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.Unavailable,
                $"No active transport provider is registered for {target.TransportType}.", "Transport registry"));
            return Result(events, evidence, [.. generation.Limitations, $"No active transport provider is registered for {target.TransportType}."]);
        }
        if (!transport.CanSend(target, out var readiness))
        {
            evidence.Add(Evidence(ActiveEventEvidenceStage.TransportAccepted, ActiveEventEvidenceStatus.SafetyBlocked, readiness, "Transport provider readiness"));
            return Result(events, evidence, [.. generation.Limitations, readiness]);
        }

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
                break; // An ambiguous send is never retried or followed by a control send.
        }

        if (evidence.Any(item => item.Stage == ActiveEventEvidenceStage.TransportAccepted && item.Status != ActiveEventEvidenceStatus.Observed))
            return Result(events, evidence, [.. generation.Limitations, "Transport did not confirm every event; no event was automatically resent."]);

        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observer = consumerProviders.FirstOrDefault(provider => provider.CanObserve(target, item.Correlation, out _));
            if (observer is null)
            {
                evidence.Add(Evidence(ActiveEventEvidenceStage.ConsumerActivityObserved, ActiveEventEvidenceStatus.NotVerified,
                    "No event-specific consumer evidence provider is available.", "Observation provider registry"));
            }
            else
            {
                evidence.Add(await observer.ObserveAsync(target, item.Correlation, observationWindow, cancellationToken));
            }
            await PersistProgressAsync();

            var continuity = continuityProviders.FirstOrDefault(provider => provider.CanObserve(target, out _));
            evidence.Add(continuity is null
                ? Evidence(ActiveEventEvidenceStage.ConsumerContinuityObserved, ActiveEventEvidenceStatus.NotVerified,
                    "No consumer continuity provider is available.", "Continuity provider registry")
                : await continuity.ObserveAsync(target, item.Correlation, clock.GetUtcNow(), observationWindow, cancellationToken));
            await PersistProgressAsync();

            var verifier = downstreamVerifiers.FirstOrDefault(provider => provider.CanVerify(target, scenario, out _));
            evidence.Add(verifier is null
                ? Evidence(ActiveEventEvidenceStage.DownstreamPersistenceVerified, ActiveEventEvidenceStatus.NotVerified,
                    "No downstream verifier is available for this scenario.", "Downstream verifier registry")
                : await verifier.VerifyAsync(target, scenario, item.Correlation, cancellationToken));
            await PersistProgressAsync();
        }

        return Result(events, evidence, generation.Limitations);

        ActiveEventExecutionResult Result(IReadOnlyList<GeneratedActiveEvent> generated, IReadOnlyList<ActiveEventStageEvidence> stages, IReadOnlyList<string> limitations) =>
            new(runId, scenario, target, generated, stages, limitations, startedAt, clock.GetUtcNow());

        ActiveEventStageEvidence Evidence(ActiveEventEvidenceStage stage, ActiveEventEvidenceStatus status, string detail, string source) =>
            new() { Stage = stage, Status = status, Detail = detail, EvidenceSource = source, CapturedAt = clock.GetUtcNow() };

        async Task PersistProgressAsync()
        {
            if (persistProgress is null) return;
            var snapshot = Result(events, evidence, generation.Limitations).ToHistoryResult(sourceContract) with
            { Status = ActiveEventRunStatus.Running, CompletedAt = null };
            await persistProgress(snapshot, cancellationToken);
        }
    }

    private static void ValidateGeneratedEvent(GeneratedActiveEvent item, ActiveEventScenarioDescriptor scenario, Guid runId)
    {
        if (string.IsNullOrWhiteSpace(item.EventId) || item.Correlation.RunId != runId || item.Correlation.EventId != item.EventId)
            throw new InvalidOperationException("Generated event identity/correlation is invalid.");
        if (!string.Equals(item.ExtensionId, scenario.ExtensionId, StringComparison.Ordinal) || !string.Equals(item.ScenarioId, scenario.ScenarioId, StringComparison.Ordinal))
            throw new InvalidOperationException("Generated event does not belong to the selected scenario.");
        if (!scenario.SupportedOperations.Contains(item.Operation)) throw new InvalidOperationException("Generated event operation is not declared by the selected scenario.");
        if (item.Body.Length == 0 || item.Body.Length != item.BodyBytes) throw new InvalidOperationException("Generated event body size metadata is invalid.");
        var hash = Convert.ToHexString(SHA256.HashData(item.Body)).ToLowerInvariant();
        if (!string.Equals(hash, item.BodySha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(hash, item.Correlation.EventFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Generated event fingerprint does not match its body.");
    }
}
