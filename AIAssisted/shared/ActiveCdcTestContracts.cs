using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// Active CDC tests (Phase 1): BirkNext sends ONE synthetic Debezium Person event to an approved DEV/QA Event Hub and records what
// it could observe afterwards. Each stage is its own evidence: generated ≠ sent ≠ accepted by Event Hub ≠ consumer checkpoint ≠
// Person persisted ≠ outbox ≠ Service Bus delivered ≠ subscriber processed. Not observed / unavailable / not assessed are never a
// failure and never zero. Nothing here carries the payload, a credential, a connection string or a personal value.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveCdcRunStatus { Running, Passed, Failed, Partial, Blocked, Inconclusive, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveCdcEvidenceState { Observed, NotObserved, NotAssessed, Unavailable, NotAuthorized, TimedOut, Error }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveCdcStepKind
{
    EnvironmentGuard, Destination, SourceContract, FixtureGenerated, BaselineCaptured, IntentRecorded,
    EventHubSend, PartitionPosition, ConsumerCheckpoint, ConsumerTelemetry, PersonPersisted, OutboxCreated, ServiceBusDelivered, SubscriberProcessed,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveCdcReadinessState { Ready, Blocked, Unknown, Optional }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveCdcContractStatus { Compatible, Incompatible, Outdated, NoSourceSnapshot }

/// <summary>A reviewed, built-in scenario. There is no user-supplied payload: the fixture is produced by the backend from this definition.</summary>
public sealed record ActiveCdcScenario
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Debezium source table the scenario writes (payload <c>source.table</c>).</summary>
    public string Table { get; init; } = "";
    public string Operation { get; init; } = "";
    /// <summary>Payload fields the fixture sends (names only), each of which the bound source snapshot must show the adapter reading.</summary>
    public List<string> Fields { get; init; } = [];
    /// <summary>What a Passed result would require — not reachable in Phase 1 (no reliable read-only Person verification exists).</summary>
    public string PassCriterion { get; init; } = "";
}

/// <summary>Scenario ↔ source snapshot binding. Recomputed per run and stored with it; a newer snapshot makes it Outdated (blocked) until re-bound.</summary>
public sealed record ActiveCdcContractManifest
{
    public string ScenarioId { get; init; } = "";
    public string ScenarioVersion { get; init; } = "";
    public int FixtureSchemaVersion { get; init; }
    public Guid? SourceSnapshotId { get; init; }
    public string? ArchiveSha256 { get; init; }
    public string? SourceCommit { get; init; }
    public int? AnalyzerVersion { get; init; }
    public List<string> RequiredFields { get; init; } = [];
    public List<string> ConfirmedFields { get; init; } = [];
    public List<string> MissingFields { get; init; } = [];
    public ActiveCdcContractStatus Status { get; init; } = ActiveCdcContractStatus.NoSourceSnapshot;
    public string Detail { get; init; } = "";
    /// <summary>SHA-256 over scenario, fixture schema, snapshot and confirmed fields. Two runs with the same fingerprint used the same binding.</summary>
    public string Fingerprint { get; init; } = "";
}

/// <summary>Where the event goes: derived from the configured integration and platform, then approved by backend policy. Identifiers only.</summary>
public sealed record ActiveCdcDestination
{
    public string EnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public string? NamespaceFqdn { get; init; }
    public string? EventHub { get; init; }
    public string? ConsumerGroup { get; init; }
    public bool ConsumerGroupAssumed { get; init; }
    public string? SourceDatabase { get; init; }
    public string? SourceSchema { get; init; }
    public string? SourceTable { get; init; }
    public bool Approved { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>What was generated — synthetic identifiers, field names and a hash. The payload itself is never stored.</summary>
public sealed record ActiveCdcFixtureSummary
{
    public int SyntheticPersonPk { get; init; }
    /// <summary>The PersonId the adapter derives from PersonPK (SHA-256 of its decimal text, first 16 bytes) — what a later Person check would look for.</summary>
    public Guid ExpectedPersonId { get; init; }
    public string Marker { get; init; } = "";
    public DateOnly SyntheticBirthDate { get; init; }
    public int AgeYears { get; init; }
    public List<string> Fields { get; init; } = [];
    public string PayloadSha256 { get; init; } = "";
    public int PayloadBytes { get; init; }
    public List<string> Notes { get; init; } = [];
}

public sealed record ActiveCdcStep
{
    public ActiveCdcStepKind Kind { get; init; }
    public ActiveCdcEvidenceState State { get; init; } = ActiveCdcEvidenceState.NotAssessed;
    public string Detail { get; init; } = "";
    /// <summary>Where the evidence came from ("Backend policy", "Event Hubs producer SDK", "Blob checkpoint store (read-only)").</summary>
    public string Source { get; init; } = "";
    public DateTimeOffset? CapturedAt { get; init; }
}

public sealed record ActiveCdcRun
{
    public Guid RunId { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public string IntegrationName { get; init; } = "";
    public ActiveCdcScenario Scenario { get; init; } = new();
    public ActiveCdcContractManifest Manifest { get; init; } = new();
    public ActiveCdcDestination Destination { get; init; } = new();
    public ActiveCdcFixtureSummary? Fixture { get; init; }
    public ActiveCdcRunStatus Status { get; init; } = ActiveCdcRunStatus.Running;
    public string StatusReason { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>True once the send was requested. From then on the event may exist in Event Hub: cancellation cannot unsend it.</summary>
    public bool SendAttempted { get; init; }
    public bool CancellationRequested { get; init; }
    public List<ActiveCdcStep> Steps { get; init; } = [];
    public List<string> WhatWasTested { get; init; } = [];
    public List<string> WhatWasNotAssessed { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    [JsonIgnore] public bool Completed => Status != ActiveCdcRunStatus.Running;
    public ActiveCdcStep? Step(ActiveCdcStepKind kind) => Steps.FirstOrDefault(s => s.Kind == kind);
}

public sealed record ActiveCdcRunSummary(Guid RunId, string IntegrationId, string ScenarioId, ActiveCdcRunStatus Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    bool SendAttempted, string ManifestFingerprint);

public sealed record ActiveCdcReadinessCheck(string Key, string Label, ActiveCdcReadinessState State, string Detail);

public sealed record ActiveCdcReadiness
{
    public string EnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public ActiveCdcScenario Scenario { get; init; } = new();
    public ActiveCdcContractManifest Manifest { get; init; } = new();
    public ActiveCdcDestination Destination { get; init; } = new();
    public List<ActiveCdcReadinessCheck> Checks { get; init; } = [];
    public bool CanRun { get; init; }
    public Guid? RunningRunId { get; init; }
    /// <summary>Bounds the UI states: one event per run, the observation window, and that cancelling cannot unsend.</summary>
    public int ObservationSeconds { get; init; }
    public int SendTimeoutSeconds { get; init; }
}

public sealed record ActiveCdcRunRequest
{
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public Guid? SourceSnapshotId { get; init; }
    /// <summary>Must be true: the person confirmed that one synthetic event is sent to the named non-production destination.</summary>
    public bool ConfirmedSend { get; init; }
    /// <summary>The destination the person saw and confirmed. A mismatch with what the backend resolves blocks the run.</summary>
    public string? ConfirmedEventHub { get; init; }
}

public static class ActiveCdcLabels
{
    public const string ActiveTestNotice = "Active test — sends synthetic data to the selected non-production environment.";
    public const string CancelNotice = "Cancelling stops waiting for evidence. It cannot unsend an event that was already sent.";

    public static string Status(ActiveCdcRunStatus status) => status switch
    {
        ActiveCdcRunStatus.Running => "Running",
        ActiveCdcRunStatus.Partial => "Partial",
        ActiveCdcRunStatus.Inconclusive => "Inconclusive",
        _ => status.ToString(),
    };

    public static string State(ActiveCdcEvidenceState state) => state switch
    {
        ActiveCdcEvidenceState.NotObserved => "Not observed",
        ActiveCdcEvidenceState.NotAssessed => "Not assessed",
        ActiveCdcEvidenceState.NotAuthorized => "Not authorized",
        ActiveCdcEvidenceState.TimedOut => "Timed out",
        _ => state.ToString(),
    };

    public static string Step(ActiveCdcStepKind kind) => kind switch
    {
        ActiveCdcStepKind.EnvironmentGuard => "Environment guard (DEV/QA only)",
        ActiveCdcStepKind.Destination => "Destination resolved and approved",
        ActiveCdcStepKind.SourceContract => "Source contract compatible",
        ActiveCdcStepKind.FixtureGenerated => "Synthetic fixture generated",
        ActiveCdcStepKind.BaselineCaptured => "Pre-send baseline (read-only)",
        ActiveCdcStepKind.IntentRecorded => "Run intent recorded before send",
        ActiveCdcStepKind.EventHubSend => "Event Hub accepted the event",
        ActiveCdcStepKind.PartitionPosition => "Partition position advanced",
        ActiveCdcStepKind.ConsumerCheckpoint => "Consumer checkpoint passed the event",
        ActiveCdcStepKind.ConsumerTelemetry => "Consumer telemetry for this event",
        ActiveCdcStepKind.PersonPersisted => "Person persisted",
        ActiveCdcStepKind.OutboxCreated => "Outbox message created",
        ActiveCdcStepKind.ServiceBusDelivered => "Service Bus delivered",
        ActiveCdcStepKind.SubscriberProcessed => "Subscriber processed",
        _ => kind.ToString(),
    };

    public static string Readiness(ActiveCdcReadinessState state) => state switch
    {
        ActiveCdcReadinessState.Optional => "Optional — not available",
        _ => state.ToString(),
    };

    public static string Contract(ActiveCdcContractStatus status) => status switch
    {
        ActiveCdcContractStatus.NoSourceSnapshot => "No source snapshot",
        _ => status.ToString(),
    };
}
