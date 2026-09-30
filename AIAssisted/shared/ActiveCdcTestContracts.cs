using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// Active CDC tests (Phase 1): BirkNext sends ONE synthetic Debezium Person event to an approved DEV/QA Event Hub and records what
// it could observe afterwards. Each stage is its own evidence: generated ≠ sent ≠ accepted by Event Hub ≠ consumer checkpoint ≠
// Person persisted ≠ outbox ≠ Service Bus delivered ≠ subscriber processed. Not observed / unavailable / not assessed are never a
// failure and never zero. Nothing here carries the payload, a credential, a connection string or a personal value.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveCdcRunStatus { Running, Passed, Failed, Partial, Blocked, Inconclusive, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveCdcEvidenceState { Observed, NotObserved, NotAssessed, Unavailable, NotAuthorized, TimedOut, Error, Ambiguous, NotTested }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveCdcStepKind
{
    EnvironmentGuard, Destination, SourceContract, FixtureGenerated, BaselineCaptured, IntentRecorded,
    EventHubSend, PartitionPosition, ConsumerCheckpoint, ConsumerTelemetry, PersonPersisted, OutboxCreated, ServiceBusDelivered, SubscriberProcessed,
    // Same PersonPK replay (appended: stored Phase 1 runs keep their meaning).
    IdentitiesAllocated, SendA, ObserveA, ReplayEquivalence, SendReplay, ObserveReplay, SendControl, ObserveControl, FollowingEventProgression,
    CorrelatedReplayError, DatabaseIdempotency, PersonRowCount, OverwriteBehavior, OutboxDuplication, NaturalKeyDuplicate,
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
    /// <summary>"Transport" or "Runtime resilience". Empty in runs recorded before categories existed.</summary>
    public string Category { get; init; } = "";
    /// <summary>Events one run sends (1 for Normal Person, 3 for Same PersonPK replay).</summary>
    public int MessageCount { get; init; } = 1;
    /// <summary>The limitation shown prominently beside the scenario — never only in help text.</summary>
    public string Limitation { get; init; } = "";
    /// <summary>What a Passed result means, exactly, and what it does not.</summary>
    public string PassMeaning { get; init; } = "";
    public string PassDoesNotMean { get; init; } = "";
}

/// <summary>One sent message of a multi-message scenario (A, A2, B): synthetic identity, hash, send outcome and where it landed. Never the payload.</summary>
public sealed record ActiveCdcMessageEvidence
{
    public string Label { get; init; } = "";
    public string Role { get; init; } = "";
    public int SyntheticPersonPk { get; init; }
    public Guid ExpectedPersonId { get; init; }
    public string Marker { get; init; } = "";
    public string PayloadSha256 { get; init; } = "";
    public int PayloadBytes { get; init; }
    public ActiveCdcEvidenceState SendState { get; init; } = ActiveCdcEvidenceState.NotAssessed;
    public string SendDetail { get; init; } = "";
    public DateTimeOffset? SentAt { get; init; }
    /// <summary>Partitions whose last-enqueued position moved across this send, with the post-send position. More than one = other traffic in the interval.</summary>
    public Dictionary<string, long> AdvancedPartitions { get; init; } = [];
    public ActiveCdcEvidenceState CheckpointState { get; init; } = ActiveCdcEvidenceState.NotAssessed;
    public string CheckpointDetail { get; init; } = "";
}

/// <summary>Read-only partition/checkpoint snapshot at one point of the sequence (T0 before A … T3 after B).</summary>
public sealed record ActiveCdcCheckpointSnapshot
{
    public string Label { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public ActiveCdcEvidenceState State { get; init; } = ActiveCdcEvidenceState.NotAssessed;
    public string Detail { get; init; } = "";
    public List<ActiveCdcPartitionPosition> Partitions { get; init; } = [];
}

public sealed record ActiveCdcPartitionPosition(string PartitionId, long? LastEnqueued, long? Checkpointed);

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
    /// <summary>Developer tests in the bound snapshot that cover the scenario's domain behavior (discovered, not executed). Shown apart from Active CDC evidence.</summary>
    public List<string> DeveloperCoverage { get; init; } = [];
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
    /// <summary>Multi-message scenarios only (A, A2, B), in send order. Empty for Normal Person.</summary>
    public List<ActiveCdcMessageEvidence> Messages { get; init; } = [];
    public List<ActiveCdcCheckpointSnapshot> CheckpointSnapshots { get; init; } = [];
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
        ActiveCdcEvidenceState.NotTested => "Not tested",
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
        ActiveCdcStepKind.IdentitiesAllocated => "Allocate synthetic identities (X, Y)",
        ActiveCdcStepKind.SendA => "A — send (PersonPK X)",
        ActiveCdcStepKind.ObserveA => "Observe A (consumer checkpoint)",
        ActiveCdcStepKind.ReplayEquivalence => "A2 is an exact replay of A",
        ActiveCdcStepKind.SendReplay => "A2 — replay send (same PersonPK X)",
        ActiveCdcStepKind.ObserveReplay => "Observe replay (consumer checkpoint)",
        ActiveCdcStepKind.SendControl => "B — control send (different PersonPK Y)",
        ActiveCdcStepKind.ObserveControl => "Observe B (consumer checkpoint)",
        ActiveCdcStepKind.FollowingEventProgression => "Following valid event progression",
        ActiveCdcStepKind.CorrelatedReplayError => "Correlated replay error",
        ActiveCdcStepKind.DatabaseIdempotency => "Database idempotency",
        ActiveCdcStepKind.PersonRowCount => "Person row count",
        ActiveCdcStepKind.OverwriteBehavior => "Overwrite behavior",
        ActiveCdcStepKind.OutboxDuplication => "Outbox duplication",
        ActiveCdcStepKind.NaturalKeyDuplicate => "Natural-key duplicate",
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
