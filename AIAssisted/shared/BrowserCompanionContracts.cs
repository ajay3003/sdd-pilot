using System.Text.Json.Serialization;

namespace BirkNext.BrowserCompanion;

/// <summary>
/// Contracts shared by the BirkNext backend, the Blazor frontend and (as JSON) the BirkNext Browser Companion extension.
/// The companion runs inside the user's normal managed Edge session (no Playwright, no CDP) and reports safe page evidence
/// for approved Target Environment origins over the loopback backend. Nothing in these contracts carries a credential:
/// no token, cookie, storage value, request/response body, raw DOM, form value or sensitive query string.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserCompanionState
{
    /// <summary>No companion session exists for this Target Environment.</summary>
    NotPaired,
    /// <summary>BirkNext issued a pairing code and waits for the extension to present it.</summary>
    PairingPending,
    /// <summary>A paired extension reported a heartbeat or evidence recently.</summary>
    Connected,
    /// <summary>A paired extension has not reported for a while (browser closed, extension disabled, or blocked by policy).</summary>
    Disconnected,
    /// <summary>The session expired and must be paired again.</summary>
    Expired,
}

/// <summary>UI → backend: begin pairing for the active Target Environment. Approved origins come from the saved environment configuration.</summary>
public sealed record BrowserCompanionPairingStartRequest(string ProfileId, string EnvironmentName, string? EnvironmentType, IReadOnlyList<string> ApprovedOrigins);

public sealed record BrowserCompanionPairingChallenge
{
    public string PairingCode { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public string ProfileId { get; init; } = "";
}

/// <summary>Extension → backend: present the short-lived pairing code shown in BirkNext.</summary>
public sealed record BrowserCompanionPairRequest(string PairingCode, string ExtensionVersion);

public sealed record BrowserCompanionPairResult
{
    public string? EnvironmentType { get; init; }
    public bool Accepted { get; init; }
    /// <summary>Random session identifier bound to one Target Environment and one extension origin. Not a credential for anything else.</summary>
    public string? SessionId { get; init; }
    public string? ProfileId { get; init; }
    public string? EnvironmentName { get; init; }
    public IReadOnlyList<string> ApprovedOrigins { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>Extension → backend: liveness plus the current approved page (identity only).</summary>
public sealed record BrowserCompanionHeartbeat(string SessionId, string ProfileId, string? CurrentPageOrigin, string? CurrentPagePath, string ExtensionVersion);

/// <summary>Extension → backend: one or more page evidence snapshots (batched; never one message per DOM node or entry).</summary>
public sealed record BrowserCompanionEvidenceEnvelope
{
    public string SessionId { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string ExtensionVersion { get; init; } = "";
    public List<BrowserPageEvidence> Pages { get; init; } = [];
}

public sealed record BrowserCompanionAcceptResult
{
    public bool Accepted { get; init; }
    public int AcceptedPages { get; init; }
    public int RejectedPages { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>UI → backend status/unpair for one Target Environment.</summary>
public sealed record BrowserCompanionStatusRequest(string ProfileId);

public sealed record BrowserCompanionStatus
{
    public BrowserCompanionState State { get; init; } = BrowserCompanionState.NotPaired;
    public string? ProfileId { get; init; }
    public string? EnvironmentName { get; init; }
    /// <summary>Present only while pairing is pending, so the UI can show it to the user.</summary>
    public string? PairingCode { get; init; }
    public DateTimeOffset? PairingExpiresAt { get; init; }
    public DateTimeOffset? PairedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public string? ExtensionVersion { get; init; }
    public IReadOnlyList<string> ApprovedOrigins { get; init; } = [];
    public string? CurrentPageOrigin { get; init; }
    public string? CurrentPagePath { get; init; }
    public int PagesWithEvidence { get; init; }
    public int RejectedMessages { get; init; }
    /// <summary>Latest safe evidence per page (identity, metrics, rule ids, sanitized selectors). Memory-only on the backend.</summary>
    public List<BrowserPageEvidence> Pages { get; init; } = [];
    public string Message { get; init; } = "";
    public bool Connected => State == BrowserCompanionState.Connected;
}

// ── Evidence model ─────────────────────────────────────────────────────────────

/// <summary>Safe browser-side evidence for exactly one page (origin + normalized path — the Endpoint Discovery page identity).</summary>
public sealed record BrowserPageEvidence
{
    public string ProfileId { get; init; } = "";
    /// <summary>scheme://host[:port] without default ports.</summary>
    public string PageOrigin { get; init; } = "";
    /// <summary>Normalized pathname: no query, no fragment, trailing slash trimmed, "/" when empty.</summary>
    public string PagePath { get; init; } = "";
    /// <summary>When the current visit of this page started (navigation or SPA route change). Drives the refresh-generation boundary.</summary>
    public DateTimeOffset VisitStartedAt { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    /// <summary>"initial" | "update" | "final".</summary>
    public string SnapshotKind { get; init; } = "initial";
    public int SnapshotSequence { get; init; }
    public string? DocumentTitle { get; init; }
    public string? BrowserName { get; init; }
    public BrowserDomSummary? Dom { get; init; }
    public BrowserAccessibilitySummary? Accessibility { get; init; }
    public BrowserPerformanceSummary? Performance { get; init; }
    public BrowserRuntimeSummary? Runtime { get; init; }
    public BrowserBlazorSummary? Blazor { get; init; }
    public string Identity => $"{PageOrigin}{PagePath}";
}

public sealed record BrowserDomSummary
{
    public int NodeCount { get; init; }
    public int MaxDepth { get; init; }
    public int InteractiveCount { get; init; }
    public int FormControlCount { get; init; }
    public int IframeCount { get; init; }
    public int ImageCount { get; init; }
    public Dictionary<string, int> HeadingCounts { get; init; } = new();
    public List<int> HeadingOrder { get; init; } = [];
    public Dictionary<string, int> Landmarks { get; init; } = new();
    public int DuplicateIdCount { get; init; }
    public int HiddenFocusableCount { get; init; }
    public int DialogCount { get; init; }
    public int PositiveTabIndexCount { get; init; }
}

public sealed record BrowserAccessibilityRuleResult
{
    public string RuleId { get; init; } = "";
    public string Severity { get; init; } = "Low";
    public string? Wcag { get; init; }
    public string Title { get; init; } = "";
    public string Guidance { get; init; } = "";
    public int Count { get; init; }
    /// <summary>Structural selectors only (tag/id/class/role/nth-of-type), sanitized on both sides.</summary>
    public List<string> Selectors { get; init; } = [];
}

public sealed record BrowserAccessibilitySummary
{
    public string Engine { get; init; } = "BirkNext Accessibility Checks";
    public int RulesEvaluated { get; init; }
    public List<BrowserAccessibilityRuleResult> Findings { get; init; } = [];
    /// <summary>Explicit check execution records. Missing evidence is never a pass.</summary>
    public List<BrowserWcagCheck> Checks { get; init; } = [];
    public int? VideoCount { get; init; }
    public int? AudioCount { get; init; }
    /// <summary>Embedded/custom media prevents proving absence from native media counts.</summary>
    public bool MediaScopeComplete { get; init; }
    public List<int> NavigationStructure { get; init; } = [];
    public List<int> ComponentStructure { get; init; } = [];
}

/// <summary>Only fixed ids, enum-like outcomes, counts and structural selectors cross the boundary.</summary>
public sealed record BrowserWcagCheck
{
    public string CheckId { get; init; } = "";
    public string Outcome { get; init; } = "NotTested";
    public int Tested { get; init; }
    public int Failed { get; init; }
    public int Uncertain { get; init; }
    public List<string> Selectors { get; init; } = [];
}

public sealed record BrowserResourceEntry
{
    /// <summary>Scheme, host and path only; query string and fragment removed.</summary>
    public string Url { get; init; } = "";
    public string Kind { get; init; } = "other";
    public double? DurationMs { get; init; }
    public long? TransferBytes { get; init; }
    public int? Status { get; init; }
    public int Count { get; init; } = 1;
}

public sealed record BrowserPerformanceSummary
{
    public double? TtfbMs { get; init; }
    public double? DomContentLoadedMs { get; init; }
    public double? LoadEventMs { get; init; }
    public string? NavigationType { get; init; }
    public double? FirstContentfulPaintMs { get; init; }
    public double? LcpMs { get; init; }
    public double? Cls { get; init; }
    public int LongTaskCount { get; init; }
    public double? LongTaskTotalMs { get; init; }
    public double? LongestTaskMs { get; init; }
    public int ResourceCount { get; init; }
    public long TransferredBytes { get; init; }
    public long JsBytes { get; init; }
    public long CssBytes { get; init; }
    public long ImageBytes { get; init; }
    public long WasmBytes { get; init; }
    public long FontBytes { get; init; }
    public long ApiBytes { get; init; }
    public long FrameworkDataBytes { get; init; }
    public long OtherBytes { get; init; }
    public int DuplicateFetchCount { get; init; }
    public List<BrowserResourceEntry> DuplicateResources { get; init; } = [];
    public int FailedResourceCount { get; init; }
    public List<BrowserResourceEntry> FailedResources { get; init; } = [];
    public List<BrowserResourceEntry> LongestResources { get; init; } = [];
    /// <summary>Metrics the browser did not report stay null; this lists what was supported so the UI never fabricates a value.</summary>
    public List<string> UnsupportedMetrics { get; init; } = [];
}

public sealed record BrowserRuntimeError
{
    /// <summary>"error" | "unhandledrejection" | "resource".</summary>
    public string Kind { get; init; } = "error";
    /// <summary>Sanitized and length-capped; never a form value, token or body.</summary>
    public string Message { get; init; } = "";
    /// <summary>Sanitized script/resource URL (no query).</summary>
    public string? Source { get; init; }
    public int Count { get; init; } = 1;
    public DateTimeOffset FirstAt { get; init; }
    public DateTimeOffset LastAt { get; init; }
}

public sealed record BrowserRuntimeSummary
{
    public int ErrorCount { get; init; }
    public int RejectionCount { get; init; }
    public int ResourceFailureCount { get; init; }
    public List<BrowserRuntimeError> Errors { get; init; } = [];
    /// <summary>Console output is not intercepted by the companion (only error events); this documents that limitation in the evidence.</summary>
    public bool ConsoleCaptured { get; init; }
}

public sealed record BrowserBlazorSummary
{
    public bool Detected { get; init; }
    public bool BlazorScriptPresent { get; init; }
    public bool BootManifestObserved { get; init; }
    public bool BootManifestFailed { get; init; }
    public int FrameworkResourceCount { get; init; }
    public long FrameworkBytes { get; init; }
    public long WasmBytes { get; init; }
    public List<BrowserResourceEntry> FrameworkFailures { get; init; } = [];
    public int RepeatedFrameworkDownloads { get; init; }
    /// <summary>The <c>#blazor-error-ui</c> element was displayed (unhandled .NET runtime error).</summary>
    public bool ErrorUiVisible { get; init; }
}

/// <summary>Safety limits enforced by the backend on every companion message (and mirrored by the extension).</summary>
public static class BrowserCompanionLimits
{
    public const int MaxPagesPerEnvelope = 20;
    public const int MaxPagesPerEnvironment = 200;
    public const int MaxRuntimeErrorsPerPage = 50;
    public const int MaxAccessibilityRulesPerPage = 40;
    public const int MaxSelectorsPerRule = 5;
    public const int MaxResourcesPerList = 20;
    public const int MaxStringLength = 300;
    public const int MaxSelectorLength = 160;
    public const int MaxEnvelopeBytes = 512 * 1024;
    public const int MinMillisecondsBetweenEnvelopes = 200;
    public const int PairingCodeLength = 8;
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan SessionIdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SessionAbsoluteLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan ConnectedWindow = TimeSpan.FromSeconds(45);
}
