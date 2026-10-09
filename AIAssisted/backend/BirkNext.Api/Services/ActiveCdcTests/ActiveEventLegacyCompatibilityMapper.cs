using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveCdcTests;

/// <summary>Compatibility adapter for existing Active CDC HTTP/UI contracts; generic execution and durable history never depend on these DTOs.</summary>
public static class ActiveEventLegacyCompatibilityMapper
{
    public static ActiveCdcScenario Scenario(ActiveEventScenarioDescriptor descriptor)
    {
        var existing = ActiveCdcScenarioCatalog.Find(descriptor.ScenarioId);
        return existing ?? new ActiveCdcScenario
        {
            Id = descriptor.ScenarioId,
            Version = descriptor.ScenarioVersion,
            Name = descriptor.DisplayName,
            Description = descriptor.Description,
            Operation = string.Join(", ", descriptor.SupportedOperations),
            ReplayKind = descriptor.ReplayKind,
            Category = descriptor.Category,
            MessageCount = descriptor.ExpectedEventCount,
        };
    }

    public static ActiveCdcReadiness Readiness(ActiveEventReadiness readiness) => new()
    {
        EnvironmentId = readiness.TargetEnvironmentId,
        IntegrationId = readiness.IntegrationId,
        Scenario = Scenario(readiness.Scenario),
        Manifest = new ActiveCdcContractManifest
        {
            ScenarioId = readiness.Scenario.ScenarioId,
            ScenarioVersion = readiness.SourceContract.ScenarioVersion,
            SourceSnapshotId = readiness.SourceContract.SourceSnapshotId,
            Fingerprint = readiness.SourceContract.ContractFingerprint ?? "",
            Status = string.IsNullOrWhiteSpace(readiness.SourceContract.ContractFingerprint) ? ActiveCdcContractStatus.NoSourceSnapshot : ActiveCdcContractStatus.Compatible,
            Detail = "Scenario prerequisites were evaluated by the registered extension provider.",
        },
        Destination = Destination(readiness.Target),
        Checks = readiness.Checks.Select(check => new ActiveCdcReadinessCheck(check.Key, check.Label, check.State switch
        {
            ActiveEventReadinessState.Ready => ActiveCdcReadinessState.Ready,
            ActiveEventReadinessState.Blocked => ActiveCdcReadinessState.Blocked,
            ActiveEventReadinessState.Unknown => ActiveCdcReadinessState.Unknown,
            _ => ActiveCdcReadinessState.Optional,
        }, check.Detail)).ToList(),
        CanRun = readiness.CanRun,
        SendTimeoutSeconds = readiness.SendTimeoutSeconds,
        ObservationSeconds = readiness.ObservationTimeoutSeconds,
    };

    public static ActiveCdcRun Run(ActiveEventRunResult result) => new()
    {
        RunId = result.RunId,
        EnvironmentId = result.Target.TargetEnvironmentId,
        EnvironmentName = result.Target.SafeMetadata.GetValueOrDefault("displayName", result.Target.TargetEnvironmentId),
        EnvironmentType = result.Target.EnvironmentType,
        IntegrationId = result.Target.IntegrationId,
        IntegrationName = result.Target.IntegrationId,
        Scenario = Scenario(result.Scenario),
        Manifest = new ActiveCdcContractManifest
        {
            ScenarioId = result.Scenario.ScenarioId,
            ScenarioVersion = result.SourceContract.ScenarioVersion,
            SourceSnapshotId = result.SourceContract.SourceSnapshotId,
            Fingerprint = result.SourceContract.ContractFingerprint ?? "",
            Status = string.IsNullOrWhiteSpace(result.SourceContract.ContractFingerprint) ? ActiveCdcContractStatus.NoSourceSnapshot : ActiveCdcContractStatus.Compatible,
            Detail = "Source contract reference recorded with the generic execution result.",
        },
        Destination = Destination(result.Target),
        Status = result.Status switch
        {
            ActiveEventRunStatus.Running => ActiveCdcRunStatus.Running,
            ActiveEventRunStatus.Completed => ActiveCdcRunStatus.Passed,
            ActiveEventRunStatus.CompletedWithLimitedEvidence => ActiveCdcRunStatus.Partial,
            ActiveEventRunStatus.Failed => ActiveCdcRunStatus.Failed,
            ActiveEventRunStatus.SafetyBlocked => ActiveCdcRunStatus.Blocked,
            ActiveEventRunStatus.Cancelled => ActiveCdcRunStatus.Cancelled,
            _ => ActiveCdcRunStatus.Inconclusive,
        },
        StatusReason = result.Limitations.FirstOrDefault() ?? $"Run status: {result.Status}.",
        StartedAt = result.StartedAt,
        CompletedAt = result.CompletedAt,
        SendAttempted = result.Evidence.Any(item => item.Stage == ActiveEventEvidenceStage.SendAttempted),
        Fixture = Fixture(result.Events),
        Messages = result.Events.Count > 1 ? result.Events.OrderBy(item => item.SequenceIndex).Select(Message).ToList() : [],
        Steps = result.Evidence.Select(Step).ToList(),
        WhatWasTested = ["Generic active event lifecycle evidence"],
        WhatWasNotAssessed = result.Evidence.Where(item => item.Status is ActiveEventEvidenceStatus.NotVerified or ActiveEventEvidenceStatus.Unavailable or ActiveEventEvidenceStatus.NotObserved)
            .Select(item => $"{item.Stage}: {item.Detail}").Distinct().ToList(),
        Limitations = [.. result.Limitations],
    };

    public static ActiveCdcRunSummary Summary(ActiveEventRunResult result)
    {
        var legacy = Run(result);
        return new(legacy.RunId, legacy.IntegrationId, legacy.Scenario.Id, legacy.Status, legacy.StartedAt, legacy.CompletedAt, legacy.SendAttempted, legacy.Manifest.Fingerprint);
    }

    private static ActiveCdcDestination Destination(ActiveEventTrustedTarget? target) => new()
    {
        EnvironmentId = target?.TargetEnvironmentId ?? "",
        IntegrationId = target?.IntegrationId ?? "",
        NamespaceFqdn = target?.Endpoint,
        EventHub = target?.Resource,
        ConsumerGroup = target?.Consumer,
        Approved = target?.SafeMetadata.GetValueOrDefault("destinationApproval") == "approved",
    };

    private static ActiveCdcFixtureSummary? Fixture(IReadOnlyList<ActiveEventGeneratedSummary> events)
    {
        var item = events.FirstOrDefault(eventItem => eventItem.SafeMetadata.GetValueOrDefault("syntheticPersonPk") is { Length: > 0 });
        if (item is null) return null;
        _ = int.TryParse(item.SafeMetadata.GetValueOrDefault("syntheticPersonPk"), out var key);
        _ = Guid.TryParse(item.SafeMetadata.GetValueOrDefault("expectedPersonId"), out var personId);
        _ = DateOnly.TryParse(item.SafeMetadata.GetValueOrDefault("syntheticBirthDate"), out var birthDate);
        return new() { SyntheticPersonPk = key, ExpectedPersonId = personId, Marker = item.SafeMetadata.GetValueOrDefault("marker", ""),
            SyntheticBirthDate = birthDate, Fields = item.SafeMetadata.GetValueOrDefault("fields", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            PayloadSha256 = item.BodySha256, PayloadBytes = item.BodyBytes, Notes = ["Generic history stores body hash and safe extension metadata only."] };
    }

    private static ActiveCdcMessageEvidence Message(ActiveEventGeneratedSummary item)
    {
        _ = int.TryParse(item.SafeMetadata.GetValueOrDefault("syntheticPersonPk"), out var key);
        _ = Guid.TryParse(item.SafeMetadata.GetValueOrDefault("expectedPersonId"), out var personId);
        return new() { Label = item.SafeMetadata.GetValueOrDefault("sequenceLabel", item.SequenceIndex.ToString()),
            Role = item.SafeMetadata.GetValueOrDefault("role", "Generated event"), SyntheticPersonPk = key == 0 ? null : key,
            ExpectedPersonId = personId, Marker = item.SafeMetadata.GetValueOrDefault("marker", ""), PayloadSha256 = item.BodySha256,
            PayloadBytes = item.BodyBytes, SendState = ActiveCdcEvidenceState.NotAssessed };
    }

    private static ActiveCdcStep Step(ActiveEventStageEvidence item) => new()
    {
        Kind = item.Stage switch
        {
            ActiveEventEvidenceStage.Generated => ActiveCdcStepKind.FixtureGenerated,
            ActiveEventEvidenceStage.SendAttempted => ActiveCdcStepKind.IntentRecorded,
            ActiveEventEvidenceStage.TransportAccepted => ActiveCdcStepKind.EventHubSend,
            ActiveEventEvidenceStage.ConsumerActivityObserved => ActiveCdcStepKind.ConsumerTelemetry,
            ActiveEventEvidenceStage.ConsumerContinuityObserved => ActiveCdcStepKind.ConsumerCheckpoint,
            _ => ActiveCdcStepKind.PersonPersisted,
        },
        State = item.Status switch
        {
            ActiveEventEvidenceStatus.Observed => ActiveCdcEvidenceState.Observed,
            ActiveEventEvidenceStatus.NotObserved => ActiveCdcEvidenceState.NotObserved,
            ActiveEventEvidenceStatus.Unavailable or ActiveEventEvidenceStatus.NotVerified => ActiveCdcEvidenceState.NotAssessed,
            ActiveEventEvidenceStatus.Ambiguous => ActiveCdcEvidenceState.Ambiguous,
            ActiveEventEvidenceStatus.Failed => ActiveCdcEvidenceState.Error,
            ActiveEventEvidenceStatus.SafetyBlocked => ActiveCdcEvidenceState.NotAssessed,
            _ => ActiveCdcEvidenceState.NotAssessed,
        },
        Detail = item.Detail,
        Source = item.EvidenceSource,
        CapturedAt = item.CapturedAt,
    };
}
