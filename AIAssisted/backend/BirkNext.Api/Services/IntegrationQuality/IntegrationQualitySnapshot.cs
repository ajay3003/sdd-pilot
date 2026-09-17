using System.Text.Json.Serialization;
using BirkNext.Api.Services.ContractAnalysis;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// Phase 3, Checkpoint 5: an immutable record of one completed Integration Quality Review,
/// retained so a later run can be compared against it.
///
/// Snapshots are never mutated. A later run always creates a new snapshot linked to its
/// predecessor. Integrations are keyed by <see cref="IntegrationBaselineIdentity"/>, never by
/// IntegrationId or display name.
/// </summary>
public sealed class IntegrationQualitySnapshot
{
    /// <summary>Schema version of the snapshot itself. Bump when this shape changes.</summary>
    public const int CurrentSnapshotVersion = 1;

    [JsonPropertyName("snapshotId")]
    public Guid SnapshotId { get; init; } = Guid.NewGuid();

    [JsonPropertyName("environmentId")]
    public string EnvironmentId { get; init; } = "";

    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("snapshotVersion")]
    public int SnapshotVersion { get; init; } = CurrentSnapshotVersion;

    /// <summary>
    /// Version of the baseline key algorithm in force when this snapshot was written. Stored so a
    /// future algorithm change is detectable rather than silently producing unmatched keys.
    /// </summary>
    [JsonPropertyName("baselineIdentityVersion")]
    public int BaselineIdentityVersion { get; init; } = IntegrationBaselineIdentity.Version;

    [JsonPropertyName("previousSnapshotId")]
    public Guid? PreviousSnapshotId { get; init; }

    [JsonPropertyName("completeness")]
    public SnapshotCompleteness Completeness { get; init; } = SnapshotCompleteness.Complete;

    [JsonPropertyName("integrations")]
    public List<IntegrationSnapshotEntry> Integrations { get; init; } = [];
}

/// <summary>
/// Whether a snapshot represents a whole review. A partial review is recorded honestly rather
/// than being promoted to a complete baseline.
/// </summary>
public enum SnapshotCompleteness
{
    Complete = 0,
    Partial = 1
}

/// <summary>
/// The authoritative per-integration summary retained for future comparison. Contains no
/// credentials: endpoint values are canonicalised with user info and query stripped before
/// they reach this model.
/// </summary>
public sealed class IntegrationSnapshotEntry
{
    [JsonPropertyName("baselineKey")]
    public string BaselineKey { get; init; } = "";

    /// <summary>Retained for diagnostics only. Never used to resolve a baseline.</summary>
    [JsonPropertyName("integrationId")]
    public string IntegrationId { get; init; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = "";

    [JsonPropertyName("integrationType")]
    public IntegrationType IntegrationType { get; init; }

    // Diagnostic identity: lets an operator see what the opaque key stands for.
    [JsonPropertyName("canonicalEndpoint")]
    public string CanonicalEndpoint { get; init; } = "";

    [JsonPropertyName("canonicalResource")]
    public string CanonicalResource { get; init; } = "";

    // Relationship. Excluded from the baseline key on purpose so changes show up as history.
    [JsonPropertyName("producer")]
    public string? Producer { get; init; }

    [JsonPropertyName("consumer")]
    public string? Consumer { get; init; }

    [JsonPropertyName("relationshipSource")]
    public RelationshipSource RelationshipSource { get; init; }

    // Contract.
    [JsonPropertyName("contractState")]
    public ContractCompatibilityStatus? ContractState { get; init; }

    [JsonPropertyName("contractName")]
    public string? ContractName { get; init; }

    [JsonPropertyName("contractSourceType")]
    public ContractSourceType? ContractSourceType { get; init; }

    [JsonPropertyName("contractSource")]
    public string? ContractSource { get; init; }

    [JsonPropertyName("contractFingerprint")]
    public string? ContractFingerprint { get; init; }

    /// <summary>
    /// The normalized contract, retained so drift can explain what changed. A fingerprint alone
    /// detects "changed" but cannot describe the difference.
    /// </summary>
    [JsonPropertyName("normalizedContract")]
    public NormalizedContract? NormalizedContract { get; init; }

    // Runtime evidence summary.
    [JsonPropertyName("runtimeEvidenceState")]
    public RuntimeEvidenceState RuntimeEvidenceState { get; init; } = RuntimeEvidenceState.Unknown;

    [JsonPropertyName("latestObservedAt")]
    public DateTime? LatestObservedAt { get; init; }

    [JsonPropertyName("evidenceCount")]
    public int EvidenceCount { get; init; }

    [JsonPropertyName("evidenceSources")]
    public List<RuntimeEvidenceSource> EvidenceSources { get; init; } = [];

    // Authentication. Required, capability and execution are three distinct facts.
    [JsonPropertyName("authenticationRequired")]
    public bool? AuthenticationRequired { get; init; }

    [JsonPropertyName("authenticatedCapabilityAvailable")]
    public bool? AuthenticatedCapabilityAvailable { get; init; }

    [JsonPropertyName("authenticatedChecksExecuted")]
    public int AuthenticatedChecksExecuted { get; init; }

    /// <summary>
    /// Populated by Checkpoint 6. Left null rather than filled with fabricated zeros, so
    /// "not measured" stays distinguishable from "measured as zero".
    /// </summary>
    [JsonPropertyName("performance")]
    public IntegrationPerformanceSummary? Performance { get; init; }
}

/// <summary>
/// Whether runtime traffic was observed, kept distinct from reachability.
/// </summary>
public enum RuntimeEvidenceState
{
    Unknown = 0,
    NoEvidence = 1,
    Observed = 2
}

/// <summary>
/// Placeholder shape owned by Checkpoint 6. Declared now so adding performance history does not
/// require a second persistence redesign.
/// </summary>
public sealed class IntegrationPerformanceSummary
{
    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; init; }

    [JsonPropertyName("minDurationMs")]
    public double? MinDurationMs { get; init; }

    [JsonPropertyName("maxDurationMs")]
    public double? MaxDurationMs { get; init; }

    [JsonPropertyName("averageDurationMs")]
    public double? AverageDurationMs { get; init; }

    [JsonPropertyName("p50DurationMs")]
    public double? P50DurationMs { get; init; }

    [JsonPropertyName("p95DurationMs")]
    public double? P95DurationMs { get; init; }

    [JsonPropertyName("p99DurationMs")]
    public double? P99DurationMs { get; init; }

    [JsonPropertyName("errorRate")]
    public double? ErrorRate { get; init; }

    [JsonPropertyName("requestsPerSecond")]
    public double? RequestsPerSecond { get; init; }

    [JsonPropertyName("firstObservedAt")]
    public DateTime? FirstObservedAt { get; init; }

    [JsonPropertyName("lastObservedAt")]
    public DateTime? LastObservedAt { get; init; }

    /// <summary>Carried forward so a later run can tell "measured weakly" from "not measured".</summary>
    [JsonPropertyName("evidenceState")]
    public PerformanceEvidenceState EvidenceState { get; init; } = PerformanceEvidenceState.Unavailable;
}
