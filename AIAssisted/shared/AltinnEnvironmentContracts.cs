using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

/// <summary>
/// The provenance-carrying state of one Altinn environment value. These are never collapsed into a generic "available": a configured value
/// is not detected, a detected value is not observed, an observation is not a verification.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AltinnSettingState
{
    /// <summary>Explicitly saved on the Target Environment (or the backend's own configuration).</summary>
    Configured,
    /// <summary>Found by deterministic discovery (Source Analysis, stored public metadata, IQR binding) — a proposal until applied.</summary>
    Detected,
    /// <summary>Seen in runtime evidence (a browser login, a manual run) — evidence, never configuration.</summary>
    Observed,
    /// <summary>Stated by a test pack or reference default — a suggestion, never an override.</summary>
    Declared,
    /// <summary>A deliberate human confirmation.</summary>
    Confirmed,
    /// <summary>Proven by a BirkNext runtime verification.</summary>
    Verified,
    Missing,
    /// <summary>Sources disagree; nothing is selected silently.</summary>
    Conflict,
    Unknown,
    /// <summary>A secret the backend's own process configuration supplies (value never returned).</summary>
    AvailableToBackend,
    /// <summary>A secret entered in BirkNext for this backend session only (memory, never persisted; value never returned).</summary>
    AvailableForSession,
}

/// <summary>Where a value comes from, highest precedence first (non-secret values).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AltinnSettingSource
{
    TargetEnvironment,
    Session,
    BackendConfiguration,
    IntegrationBinding,
    SourceAnalysis,
    PublicMetadata,
    RuntimeObservation,
    TestPack,
    None,
}

/// <summary>Who needs a value: manual testing, automated BirkNext execution only, or nobody (informational).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AltinnRequirement { Manual, Automated, Optional }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AltinnReadinessState { Ready, Partial, Blocked, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AltinnRuntimeObservationKind { ReporterLogin, IdPortenAuthentication, AppOpened, FormCompleted, Submission, Receipt }

/// <summary>
/// A Target Environment's Altinn configuration: non-secret values only. A client secret and synthetic identifiers are not representable here;
/// they live in backend memory for the session (or the backend's own process configuration). Every field is optional: absent means
/// "not configured on the Target Environment", never "empty is intended".
/// </summary>
public sealed record AltinnEnvironmentSettings
{
    public ExternalTestEnvironment? Environment { get; init; }
    /// <summary>"org/app", e.g. buf/skolenaervaer.</summary>
    public string? AppIdentifier { get; init; }
    public string? AppBaseUrl { get; init; }
    public string? FormDataType { get; init; }
    public string? ContentType { get; init; }
    public AltinnUnattendedClientSettings Unattended { get; init; } = new();
    /// <summary>Observed browser (ID-porten) login of Altinn's own portal client. Evidence only: never the unattended client.</summary>
    public AltinnBrowserAuthenticationObservation? BrowserAuthentication { get; init; }
    public List<AltinnRuntimeObservation> RuntimeObservations { get; init; } = [];
    /// <summary>The canonical IQR integration this environment's Altinn app feeds (referenced, never copied).</summary>
    public string? IntegrationId { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>The registered ID-porten TEST client BirkNext itself uses for unattended execution. No secret.</summary>
public sealed record AltinnUnattendedClientSettings
{
    public string? ClientId { get; init; }
    public string? RedirectUri { get; init; }
    public List<string> Scopes { get; init; } = [];
    /// <summary>Automated execution enabled for this environment (an explicit choice; execution policy and trust still apply).</summary>
    public bool? Enabled { get; init; }
}

/// <summary>What a browser login to Altinn showed (authorization request parameters only — never a code, state, nonce or token).</summary>
public sealed record AltinnBrowserAuthenticationObservation
{
    public string? Issuer { get; init; }
    public string? AuthorizationEndpoint { get; init; }
    public string? AuthenticationLevel { get; init; }
    public string? ClientId { get; init; }
    public string? RedirectUri { get; init; }
    public List<string> Scopes { get; init; } = [];
    public string? PkceMethod { get; init; }
    public string EvidenceSource { get; init; } = "";
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>A recorded runtime observation (e.g. a manual TT02 submission). Evidence with provenance; no transient identifiers.</summary>
public sealed record AltinnRuntimeObservation
{
    public AltinnRuntimeObservationKind Kind { get; init; }
    public bool Succeeded { get; init; }
    public string? Note { get; init; }
    public string EvidenceSource { get; init; } = "Manual observation";
    public string RecordedBy { get; init; } = "";
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>A candidate value from one source (shown when sources disagree or a detected value can be applied).</summary>
public sealed record AltinnSettingCandidate(string? Value, AltinnSettingState State, AltinnSettingSource Source, string SourceLabel);

/// <summary>One resolved Altinn value with its state, provenance, who needs it and — when sources disagree — every candidate.</summary>
public sealed record AltinnSettingField
{
    public string Key { get; init; } = "";
    public string Group { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>Null for secrets and identities (never returned) and for unresolved values.</summary>
    public string? Value { get; init; }
    public AltinnSettingState State { get; init; }
    public AltinnSettingSource Source { get; init; }
    public string SourceLabel { get; init; } = "";
    public string Detail { get; init; } = "";
    public AltinnRequirement Requirement { get; init; }
    public IReadOnlyList<AltinnSettingCandidate> Candidates { get; init; } = [];
    /// <summary>A detected/declared value that differs from (or fills) the saved one and can be applied deliberately.</summary>
    public AltinnSettingCandidate? Proposal { get; init; }
}

/// <summary>One readiness dimension of the Altinn environment, with the exact reason.</summary>
public sealed record AltinnReadinessItem(string Key, string Label, AltinnReadinessState State, string Reason);

public sealed record AltinnIdentitySummary(string Role, string Alias, string Label, AltinnSettingState State, string Source, string Detail);

public sealed record AltinnDetectionSummary(int Configured, int Detected, int Observed, int Missing, int Conflicts, IReadOnlyList<string> ActionableGaps);

/// <summary>The Target Environment → Altinn view. Never contains a secret, a token or a raw synthetic identifier.</summary>
public sealed record AltinnEnvironmentView
{
    public string EnvironmentId { get; init; } = "";
    public AltinnEnvironmentSettings Saved { get; init; } = new();
    public IReadOnlyList<AltinnSettingField> Fields { get; init; } = [];
    public IReadOnlyList<AltinnIdentitySummary> Identities { get; init; } = [];
    public IReadOnlyList<AltinnReadinessItem> Summary { get; init; } = [];
    public AltinnReadinessItem ManualReadiness { get; init; } = new("manual", "Manual test readiness", AltinnReadinessState.NotAssessed, "");
    public AltinnReadinessItem AutomatedReadiness { get; init; } = new("automated", "Automated BirkNext execution", AltinnReadinessState.NotAssessed, "");
    public IReadOnlyList<string> AutomatedBlockers { get; init; } = [];
    public AltinnDetectionSummary Detection { get; init; } = new(0, 0, 0, 0, 0, []);
    /// <summary>True when this view includes a Detect run (public metadata read in memory); false for an ordinary read.</summary>
    public bool Detected { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Values to save on the Target Environment. Null leaves a value unchanged; an empty string clears it. No secret field exists.</summary>
public sealed record AltinnEnvironmentSettingsUpdate
{
    public string? Environment { get; init; }
    public string? AppIdentifier { get; init; }
    public string? AppBaseUrl { get; init; }
    public string? FormDataType { get; init; }
    public string? ContentType { get; init; }
    public string? ClientId { get; init; }
    public string? RedirectUri { get; init; }
    public List<string>? Scopes { get; init; }
    public bool? Enabled { get; init; }
    public string? IntegrationId { get; init; }
}

/// <summary>A session secret (POST body only). Never echoed, persisted, logged or exported.</summary>
public sealed record AltinnSessionSecretRequest(string Secret);

/// <summary>A session synthetic identity (POST body only). The identifier is never echoed, persisted, logged or exported.</summary>
public sealed record AltinnSessionIdentityRequest(string Role, string Alias, string Label, string Identifier, string? ConfirmProfileAlias = null, bool KnownToPerson = true);

public sealed record AltinnRuntimeObservationRequest(AltinnRuntimeObservationKind Kind, bool Succeeded, string? Note);

public sealed record AltinnBrowserObservationRequest(string? Issuer, string? AuthorizationEndpoint, string? AuthenticationLevel, string? ClientId, string? RedirectUri,
    List<string>? Scopes, string? PkceMethod, string? EvidenceSource);
