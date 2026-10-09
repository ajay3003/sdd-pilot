using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveCdcTests;

/// <summary>
/// LEGACY, read-only: runs recorded by the retired Active CDC runner, projected into the generic <see cref="ActiveEventRunResult"/> so the
/// shared history shows old and new runs together. There is no start path here — every new run executes through the generic Active Event
/// lifecycle. A row a stopped process left Running is completed as Inconclusive (whether the event went out is exactly what is unknown).
/// </summary>
public sealed class LegacyActiveCdcHistory(ActiveCdcRunStore store, TimeProvider clock)
{
    public const string LegacyExtensionId = "legacy.active-cdc";

    public async Task<ActiveEventRunResult?> GetAsync(Guid runId, CancellationToken ct)
    {
        var run = await store.GetAsync(runId, ct);
        return run is null ? null : Project(await RecoverAsync(run, ct));
    }

    public async Task<IReadOnlyList<ActiveEventRunResult>> ListAsync(string environmentId, string? integrationId, int take, CancellationToken ct)
    {
        var runs = new List<ActiveEventRunResult>();
        foreach (var run in await store.ListAsync(environmentId, integrationId, take, ct))
            runs.Add(Project(await RecoverAsync(run, ct)));
        return runs;
    }

    private async Task<ActiveCdcRun> RecoverAsync(ActiveCdcRun run, CancellationToken ct)
    {
        if (run.Status != ActiveCdcRunStatus.Running) return run;
        var recovered = run with
        {
            Status = ActiveCdcRunStatus.Inconclusive, CompletedAt = clock.GetUtcNow(),
            StatusReason = run.SendAttempted ? "Interrupted after the send was requested (BirkNext stopped). The event may have been sent; it was not re-sent."
                : "Interrupted before the send (BirkNext stopped). Nothing was sent by this run.",
            WhatWasNotAssessed = [.. run.WhatWasNotAssessed, "Run interrupted — no evidence was captured after the interruption."],
        };
        return await store.UpdateAsync(recovered, ct) ? recovered : await store.GetAsync(run.RunId, ct) ?? recovered;
    }

    /// <summary>Read-only projection: legacy steps become generic stages where a generic stage exists; everything else stays in Limitations.</summary>
    public static ActiveEventRunResult Project(ActiveCdcRun run)
    {
        var evidence = new List<ActiveEventStageEvidence>();
        foreach (var step in run.Steps)
        {
            ActiveEventEvidenceStage? stage = step.Kind switch
            {
                ActiveCdcStepKind.FixtureGenerated => ActiveEventEvidenceStage.Generated,
                ActiveCdcStepKind.IntentRecorded when run.SendAttempted => ActiveEventEvidenceStage.SendAttempted,
                ActiveCdcStepKind.EventHubSend or ActiveCdcStepKind.SendA or ActiveCdcStepKind.SendReplay or ActiveCdcStepKind.SendControl
                    or ActiveCdcStepKind.SendInvalid or ActiveCdcStepKind.SendValidControl => ActiveEventEvidenceStage.TransportAccepted,
                ActiveCdcStepKind.ConsumerTelemetry => ActiveEventEvidenceStage.ConsumerActivityObserved,
                ActiveCdcStepKind.ConsumerCheckpoint or ActiveCdcStepKind.FollowingEventProgression or ActiveCdcStepKind.ConsumerContinuity => ActiveEventEvidenceStage.ConsumerContinuityObserved,
                ActiveCdcStepKind.PersonPersisted => ActiveEventEvidenceStage.DownstreamVerified,
                _ => null,
            };
            if (stage is null) continue;
            evidence.Add(new ActiveEventStageEvidence
            {
                Stage = stage.Value, Status = Status(step.State), Detail = step.Detail, EvidenceSource = $"Legacy Active CDC · {step.Source}", CapturedAt = step.CapturedAt,
            });
        }
        return new ActiveEventRunResult
        {
            RunId = run.RunId,
            Legacy = true,
            Scenario = new ActiveEventScenarioDescriptor
            {
                ExtensionId = LegacyExtensionId, ProviderDisplayName = "Active CDC (legacy history)", ScenarioId = run.Scenario.Id,
                ScenarioVersion = run.Scenario.Version, DisplayName = run.Scenario.Name, Description = run.Scenario.Description, Category = run.Scenario.Category,
                ResourceLabel = run.Destination.SourceTable is { Length: > 0 } table ? $"{run.Destination.SourceSchema}.{table}" : "",
                RequiredIntegrationType = "EventHub", RequiredTransportType = "EventHub", ExpectedEventCount = run.Scenario.MessageCount,
                ReplayKind = run.Scenario.ReplayKind, ResultDoesNotMean = run.Scenario.PassDoesNotMean,
            },
            Target = new ActiveEventTrustedTarget
            {
                TargetEnvironmentId = run.EnvironmentId, EnvironmentDisplayName = run.EnvironmentName, EnvironmentType = run.EnvironmentType,
                IntegrationId = run.IntegrationId, IntegrationDisplayName = run.IntegrationName, IntegrationType = "EventHub", TransportType = "EventHub",
                Endpoint = run.Destination.NamespaceFqdn, Resource = run.Destination.EventHub, Consumer = run.Destination.ConsumerGroup,
            },
            SourceContract = new ActiveEventSourceContractReference
            {
                SourceSnapshotId = run.Manifest.SourceSnapshotId, ContractFingerprint = run.Manifest.Fingerprint, ContractStatus = run.Manifest.Status.ToString(),
                ScenarioVersion = run.Scenario.Version,
            },
            Status = run.Status switch
            {
                ActiveCdcRunStatus.Running => ActiveEventRunStatus.Running,
                ActiveCdcRunStatus.Passed => ActiveEventRunStatus.Completed,
                ActiveCdcRunStatus.Partial => ActiveEventRunStatus.CompletedWithLimitedEvidence,
                ActiveCdcRunStatus.Failed => ActiveEventRunStatus.Failed,
                ActiveCdcRunStatus.Blocked => ActiveEventRunStatus.SafetyBlocked,
                ActiveCdcRunStatus.Cancelled => ActiveEventRunStatus.Cancelled,
                _ => ActiveEventRunStatus.Inconclusive,
            },
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Events = (run.Messages.Count > 0 ? run.Messages.Select((m, i) => (Index: i, Label: m.Label, Sha: m.PayloadSha256, Bytes: m.PayloadBytes))
                    : run.Fixture is { } f ? [(Index: 0, Label: "", Sha: f.PayloadSha256, Bytes: f.PayloadBytes)] : [])
                .Select(item => new ActiveEventGeneratedSummary
                {
                    EventId = item.Label.Length == 0 ? run.RunId.ToString("N") : $"{run.RunId:N}-{item.Label}", ExtensionId = LegacyExtensionId,
                    ScenarioId = run.Scenario.Id, SequenceIndex = item.Index, Operation = ActiveEventOperation.Create, ContentType = "application/json",
                    BodySha256 = item.Sha, BodyBytes = item.Bytes, SafeMetadata = new Dictionary<string, string> { ["sequenceLabel"] = item.Label },
                }).ToArray(),
            Evidence = evidence,
            Limitations = [.. run.Limitations, .. run.WhatWasNotAssessed, .. run.StatusReason.Length > 0 ? new[] { run.StatusReason } : []],
        };
    }

    private static ActiveEventEvidenceStatus Status(ActiveCdcEvidenceState state) => state switch
    {
        ActiveCdcEvidenceState.Observed => ActiveEventEvidenceStatus.Observed,
        ActiveCdcEvidenceState.NotObserved or ActiveCdcEvidenceState.TimedOut => ActiveEventEvidenceStatus.NotObserved,
        ActiveCdcEvidenceState.Unavailable or ActiveCdcEvidenceState.NotAuthorized => ActiveEventEvidenceStatus.Unavailable,
        ActiveCdcEvidenceState.Ambiguous => ActiveEventEvidenceStatus.Ambiguous,
        ActiveCdcEvidenceState.Error => ActiveEventEvidenceStatus.Failed,
        _ => ActiveEventEvidenceStatus.NotVerified,
    };
}
