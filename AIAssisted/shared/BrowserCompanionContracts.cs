using System.Text.Json.Serialization;

namespace BirkNext.BrowserCompanion;

/// <summary>Page ownership is exact application origin membership, never redirect permission or a resource host.</summary>
public static class ApplicationPagePolicy
{
    public static bool IsInfrastructureHost(string host) => new[]
    {
        "access.mcas.ms", "mcas.ms", "microsoftonline.com", "microsoftonline-p.com",
        "msauth.net", "msftauth.net", "login.live.com", "login.windows.net"
    }.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The canonical origin identity of a page: scheme, host and non-default port, and nothing else. This is the ONE
    /// canonicalization for every origin in the Browser Companion channel — the approved list, the evidence a page
    /// reports and the heartbeat's current page all pass through here, so both sides of every comparison are produced
    /// the same way.
    ///
    /// It is deliberately not a sanitizer. An origin is an identity that is about to be matched against origins the
    /// user explicitly approved, and free-text credential redaction rewrites anything token-shaped: a hostname with a
    /// 32-character label, or one containing a word the generic patterns treat as sensitive, would be redacted into a
    /// value that can never match the approval it was checked against. A hostname is not a secret, and an origin that
    /// does not match an approved one is rejected regardless of what it spells.
    ///
    /// <see cref="Uri"/> supplies the normalization: scheme and host are lower-cased, the default port for the scheme
    /// is dropped, and path, query and fragment are discarded, so <c>https://APP.test:443/x?q#f</c> and
    /// <c>https://app.test</c> are one identity. Returns null for anything that is not an absolute http/https URI, and
    /// for a value carrying userinfo — an origin never carries credentials, and accepting one would mean putting a
    /// password into an identity field.
    /// </summary>
    public static string? CanonicalOrigin(string? value)
    {
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Host.Length == 0) return null;
        return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    /// <summary>
    /// Exact canonical-origin membership. Never a wildcard, a suffix or a prefix: an approved origin admits that origin
    /// and nothing else, so a subdomain, another scheme or another port is a different application.
    /// </summary>
    public static bool IsApplicationOrigin(string? origin, IReadOnlyList<string>? applicationOrigins = null)
    {
        if (CanonicalOrigin(origin) is not { } canonical) return false;
        // CanonicalOrigin has already proved this parses as an absolute http/https URI, so the host read cannot throw.
        return !IsInfrastructureHost(new Uri(canonical).Host) &&
               (applicationOrigins is null || applicationOrigins.Contains(canonical, StringComparer.OrdinalIgnoreCase));
    }
}

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

/// <summary>
/// Extension → backend: liveness, plus every approved page the companion currently has a content script on.
///
/// <paramref name="LivePages"/> is the live truth. The single-page fields are a compatibility fallback for an older
/// extension build and are used only when no live pages are reported — they cannot describe two open tabs.
/// </summary>
public sealed record BrowserCompanionHeartbeat(
    string SessionId, string ProfileId, string? CurrentPageOrigin, string? CurrentPagePath, string ExtensionVersion,
    List<BrowserCompanionLivePageReport>? LivePages = null);

/// <summary>One live page as the extension reports it. Small on purpose: this rides on every heartbeat.</summary>
public sealed record BrowserCompanionLivePageReport(string PageId, string Origin, string Route, string ContentScriptInstanceId);

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
    /// <summary>
    /// A Critical E2E browser command for the companion to execute, carried on the heartbeat the extension was already
    /// making. It is handed out exactly once: a retried heartbeat receives null, which is what stops a retry becoming a
    /// second click. Null on every ordinary heartbeat.
    /// </summary>
    public CriticalE2E.CompanionAutomationCommand? PendingCommand { get; init; }
    /// <summary>
    /// How soon BirkNext would like the next heartbeat, in milliseconds. Set only while a Critical E2E run window is
    /// open, so a flow advances at step speed instead of at the 30-second liveness cadence. Null the rest of the time:
    /// the companion is an observer, and an observer that polls constantly is a cost with no reader.
    /// </summary>
    public int? NextHeartbeatMs { get; init; }
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
    /// <summary>Live: the origin of the single open approved page, or null. Never set from stored evidence.</summary>
    public string? CurrentPageOrigin { get; init; }
    public string? CurrentPagePath { get; init; }
    /// <summary>Historical. A count of pages we have captured evidence for, which says nothing about what is open.</summary>
    public int PagesWithEvidence { get; init; }
    public int RejectedMessages { get; init; }
    /// <summary>Latest safe evidence per page (identity, metrics, rule ids, sanitized selectors). Memory-only on the backend.</summary>
    public List<BrowserPageEvidence> Pages { get; init; } = [];
    public string Message { get; init; } = "";

    /// <summary>What is true in the browser right now. Never derived from <see cref="Pages"/>.</summary>
    public BrowserCompanionLiveSession Live { get; init; } = BrowserCompanionLiveSession.Disconnected;
    /// <summary>What was captured before. Survives every tab closing.</summary>
    public BrowserCompanionEvidenceSummary Evidence { get; init; } = BrowserCompanionEvidenceSummary.Empty;

    /// <summary>
    /// The extension is talking to BirkNext. It does NOT mean an approved page is open, a content script is alive, a
    /// DOM is readable, or that browser automation can run — read <see cref="Live"/> for any of those.
    /// </summary>
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
    public BrowserAxeEvidence? Axe { get; init; }
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

/// <summary>Bounded axe rule evidence only. No HTML, text, node attributes or raw axe result is transported.</summary>
public sealed record BrowserAxeEvidence
{
    public string State { get; init; } = "Unavailable";
    public string? Version { get; init; }
    public string? EvidenceVersion { get; init; }
    public List<BrowserAxeRule> Rules { get; init; } = [];
}

public sealed record BrowserAxeRule
{
    public string RuleId { get; init; } = "";
    public string Outcome { get; init; } = "NotTested";
    public List<string> CriterionIds { get; init; } = [];
    public int Count { get; init; }
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
    /// <summary>How many of <see cref="Count"/> fetches actually transferred bytes over the network (the rest were browser cache hits). Null when unknown.</summary>
    public int? NetworkCount { get; init; }
    /// <summary>Start of the fetch relative to the visit start (initial load: relative to navigation start), for the lightweight timeline.</summary>
    public double? StartMs { get; init; }
    /// <summary>Decoded body size when exposed by the browser (same-origin or Timing-Allow-Origin).</summary>
    public long? DecodedBytes { get; init; }
    /// <summary>"cache" when the browser served the resource from its HTTP cache without a network transfer; "network" when bytes were transferred; null when not inferable.</summary>
    public string? Delivery { get; init; }
}

/// <summary>Per-category resource totals (JavaScript, CSS, WASM, image, font, fetch/XHR, document, framework data, other).</summary>
public sealed record BrowserResourceCategorySummary
{
    public string Kind { get; init; } = "other";
    public int Count { get; init; }
    public long TransferBytes { get; init; }
    public int CachedCount { get; init; }
    public BrowserResourceEntry? Largest { get; init; }
    public BrowserResourceEntry? Slowest { get; init; }
}

/// <summary>
/// Interaction to Next Paint evidence. INP is reported ONLY when the browser exposes Event Timing with interaction ids and enough
/// distinct interactions were observed; otherwise <see cref="Status"/> says why and <see cref="InpMs"/> stays null. Never approximated.
/// </summary>
public sealed record BrowserInteractionSummary
{
    /// <summary>"measured" | "insufficient-samples" | "not-measured" | "not-supported".</summary>
    public string Status { get; init; } = "not-measured";
    public int InteractionCount { get; init; }
    /// <summary>Minimum distinct interactions BirkNext requires before publishing an INP value.</summary>
    public int MinimumInteractions { get; init; }
    public double? InpMs { get; init; }
    /// <summary>Longest single interaction latency observed (informational even below the sample minimum).</summary>
    public double? LongestInteractionMs { get; init; }
    public double? FirstInputDelayMs { get; init; }
}

/// <summary>DOM mutation activity after the route change, as counted by the stabilization tracker (batch counts only, never node content).</summary>
public sealed record BrowserDomMutationSummary
{
    public int BatchCount { get; init; }
    public int MutationCount { get; init; }
    public int LargestBatch { get; init; }
    /// <summary>Milliseconds from visit start to the last mutation observed before stabilization.</summary>
    public double? LastMutationMs { get; init; }
    /// <summary>Large mutation batches (≥ 50 records) observed after the page had already stabilized — a sustained-churn indicator.</summary>
    public int LargeBatchesAfterStabilization { get; init; }
}

/// <summary>Cost of the collector itself, so the observer can never silently distort the page it measures.</summary>
public sealed record BrowserCollectorSummary
{
    /// <summary>Wall time spent building this snapshot (DOM summary + performance aggregation).</summary>
    public double? SnapshotBuildMs { get; init; }
    /// <summary>PerformanceObserver / MutationObserver callbacks handled during this visit.</summary>
    public int ObserverCallbacks { get; init; }
    /// <summary>Snapshots (initial + updates + final) sent for this visit so far.</summary>
    public int SnapshotsSent { get; init; }
    /// <summary>Approximate serialized size of this snapshot in bytes.</summary>
    public int PayloadBytes { get; init; }
    /// <summary>Resource timing entries examined for this snapshot.</summary>
    public int EntriesExamined { get; init; }
}

public sealed record BrowserPerformanceSummary
{
    /// <summary>"initial-load" (full document navigation: framework bootstrap included) or "spa-navigation" (client-side route change).</summary>
    public string ObservationType { get; init; } = "initial-load";
    public double? TtfbMs { get; init; }
    public double? DomContentLoadedMs { get; init; }
    public double? LoadEventMs { get; init; }
    public string? NavigationType { get; init; }
    public double? FirstContentfulPaintMs { get; init; }
    public double? LcpMs { get; init; }
    public double? Cls { get; init; }
    /// <summary>BirkNext Page Stabilization Time: route change → DOM/network quiet (bounded by the tracker's max wait). Not LCP.</summary>
    public double? StabilizationMs { get; init; }
    /// <summary>"quiet" when the quiet window was reached, "max-wait" when the bounded timeout ended the observation instead.</summary>
    public string? StabilizedBy { get; init; }
    public BrowserInteractionSummary? Interaction { get; init; }
    public int LongTaskCount { get; init; }
    public double? LongTaskTotalMs { get; init; }
    public double? LongestTaskMs { get; init; }
    /// <summary>BirkNext main-thread blocking time: Σ max(0, duration − 50 ms) over observed long tasks during this visit. Not Lighthouse TBT (different window).</summary>
    public double? MainThreadBlockingMs { get; init; }
    /// <summary>Long tasks observed after the page had stabilized (runtime phase) vs. during load/navigation.</summary>
    public int LongTasksAfterStabilization { get; init; }
    public BrowserDomMutationSummary? Mutations { get; init; }
    /// <summary>Informational JS heap usage where the browser exposes it (Chromium performance.memory); null = not measured.</summary>
    public long? JsHeapUsedBytes { get; init; }
    public int ResourceCount { get; init; }
    public int CachedResourceCount { get; init; }
    public long DecodedBytes { get; init; }
    public List<BrowserResourceCategorySummary> Categories { get; init; } = [];
    public BrowserResourceEntry? LargestResource { get; init; }
    public BrowserResourceEntry? SlowestResource { get; init; }
    /// <summary>Chronological, bounded list of the visit's resource fetches (sanitized URL, kind, start, duration, bytes) for the lightweight timeline.</summary>
    public List<BrowserResourceEntry> Timeline { get; init; } = [];
    public BrowserCollectorSummary? Collector { get; init; }
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
    /// <summary>Error/rejection events recorded between the route change and page stabilization (correlation only, never causality).</summary>
    public int ErrorsBeforeStabilization { get; init; }
}

public sealed record BrowserBlazorSummary
{
    public bool Detected { get; init; }
    public bool BlazorScriptPresent { get; init; }
    public bool BootManifestObserved { get; init; }
    public bool BootManifestFailed { get; init; }
    public double? BootManifestMs { get; init; }
    public int FrameworkResourceCount { get; init; }
    public long FrameworkBytes { get; init; }
    public long WasmBytes { get; init; }
    /// <summary>dotnet runtime resources (dotnet*.js, dotnet.native.*, dotnet.wasm / dotnet.native.wasm).</summary>
    public int RuntimeResourceCount { get; init; }
    public long RuntimeBytes { get; init; }
    /// <summary>Managed assemblies (.dll or .wasm assemblies under _framework, excluding the runtime).</summary>
    public int AssemblyCount { get; init; }
    public long AssemblyBytes { get; init; }
    /// <summary>ICU culture data (icudt*.dat) and timezone data (dotnet.timezones.blat) resources.</summary>
    public int CultureResourceCount { get; init; }
    public long CultureBytes { get; init; }
    public bool TimezoneDataObserved { get; init; }
    /// <summary>Framework JavaScript (blazor.webassembly.js, dotnet*.js).</summary>
    public int FrameworkJsCount { get; init; }
    /// <summary>Framework resources served from the browser cache (no bytes transferred) in this visit.</summary>
    public int CachedFrameworkResourceCount { get; init; }
    /// <summary>"cold" (framework transferred over the network), "warm" (framework from cache), "mixed", or "none" (no framework resources in this visit).</summary>
    public string LoadKind { get; init; } = "none";
    /// <summary>Framework download window relative to navigation start: first framework request → last framework response end.</summary>
    public double? FrameworkLoadStartMs { get; init; }
    public double? FrameworkLoadEndMs { get; init; }
    public List<BrowserResourceEntry> FrameworkFailures { get; init; } = [];
    public BrowserResourceEntry? SlowestFrameworkResource { get; init; }
    public int RepeatedFrameworkDownloads { get; init; }
    /// <summary>The <c>#blazor-error-ui</c> element was displayed (unhandled .NET runtime error).</summary>
    public bool ErrorUiVisible { get; init; }
}

/// <summary>Safety limits enforced by the backend on every companion message (and mirrored by the extension).</summary>
public static class BrowserCompanionLimits
{
    public const int MaxPagesPerEnvelope = 20;
    /// <summary>Live approved pages tracked per session. A person does not have fifty M2LB tabs open; a loop might.</summary>
    public const int MaxLivePages = 20;
    public const int MaxPagesPerEnvironment = 200;
    public const int MaxRuntimeErrorsPerPage = 50;
    public const int MaxAccessibilityRulesPerPage = 40;
    public const int MaxSelectorsPerRule = 5;
    public const int MaxResourcesPerList = 20;
    /// <summary>Chronological resource timeline entries kept per page snapshot (lightweight waterfall, not a DevTools clone).</summary>
    public const int MaxTimelineEntries = 60;
    public const int MaxStringLength = 300;
    public const int MaxSelectorLength = 160;
    public const int MaxEnvelopeBytes = 512 * 1024;
    public const int MinMillisecondsBetweenEnvelopes = 200;
    public const int PairingCodeLength = 8;
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(3);
    /// <summary>
    /// How long a session survives without a heartbeat. The companion heartbeats every 30 seconds, so this
    /// tolerates six consecutive misses — enough for a suspended worker or a brief network fault, and short
    /// enough that a companion which has actually gone away stops being reported as a live pairing. A window
    /// far longer than the heartbeat would leave BirkNext claiming "Paired · not reporting" for an environment
    /// whose extension no longer holds the session at all.
    /// </summary>
    public static readonly TimeSpan SessionIdleLifetime = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan SessionAbsoluteLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan ConnectedWindow = TimeSpan.FromSeconds(45);
}

// ── Live session state ─────────────────────────────────────────────────────────
//
// Everything below answers "what is true in the browser right now". It is deliberately a separate model from the
// evidence summary further down, which answers "what did we capture before". The two were one object, and that is how
// a closed tab could still report a current page: an evidence envelope arriving late set it.

/// <summary>
/// One approved application page that is open in the paired browser right now.
///
/// Liveness is a property of the PAGE, not of the evidence visit inside it. An SPA route change ends a visit and starts
/// another; the page does not stop existing in between, and a model that conflated the two made the browser appear to
/// vanish mid-navigation.
/// </summary>
public sealed record BrowserCompanionLivePage
{
    /// <summary>
    /// Stable for as long as this content script instance lives: tab plus instance. A URL is not an identity — two tabs
    /// can show the same route — and a route is mutable, so neither can be used here.
    /// </summary>
    public string PageId { get; init; } = "";
    public string Origin { get; init; } = "";
    /// <summary>Normalized path. Mutable: it changes as the user navigates within the same live page.</summary>
    public string Route { get; init; } = "";
    /// <summary>Changes on every full page load, which is how a reload replaces a live page instead of duplicating it.</summary>
    public string ContentScriptInstanceId { get; init; } = "";
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public string Identity => $"{Origin}{Route}";
}

/// <summary>
/// What BirkNext currently knows about the paired browser. Nothing here is derived from stored evidence.
/// </summary>
public sealed record BrowserCompanionLiveSession
{
    public string? ProfileId { get; init; }
    /// <summary>The extension is talking to BirkNext. It says nothing about whether an application page is open.</summary>
    public bool ExtensionConnected { get; init; }
    public DateTimeOffset? LastExtensionHeartbeatAt { get; init; }
    public List<BrowserCompanionLivePage> LivePages { get; init; } = [];
    public DateTimeOffset? LastContentScriptHeartbeatAt { get; init; }

    public int LiveApprovedPageCount => LivePages.Count;
    /// <summary>A content script is running somewhere we can reach. Without one there is no DOM to read or act on.</summary>
    public bool ContentScriptAlive => LivePages.Count > 0;

    /// <summary>
    /// The one live page, when there is exactly one. With several open there is no current page: picking the first
    /// would silently aim a command at whichever happened to register first.
    /// </summary>
    public BrowserCompanionLivePage? CurrentPage => LivePages.Count == 1 ? LivePages[0] : null;
    public string? CurrentPageId => CurrentPage?.PageId;
    public string? CurrentOrigin => CurrentPage?.Origin;
    public string? CurrentRoute => CurrentPage?.Route;

    /// <summary>
    /// A live DOM exists to read and act on. True only with a live page and a live content script — never because a DOM
    /// was captured earlier.
    /// </summary>
    public bool LiveDomAvailable => ExtensionConnected && ContentScriptAlive;

    /// <summary>Typed commands can be delivered: connected, exactly one live page, and a content script on it.</summary>
    public bool AutomationAvailable => ExtensionConnected && CurrentPage is not null;

    public static readonly BrowserCompanionLiveSession Disconnected = new();
}

// ── Historical evidence state ──────────────────────────────────────────────────

/// <summary>
/// What the companion captured previously. It survives tabs closing, the browser closing and the session expiring, and
/// it proves nothing about the present.
/// </summary>
public sealed record BrowserCompanionEvidenceSummary
{
    public int PagesWithEvidence { get; init; }
    public DateTimeOffset? LastEvidenceAt { get; init; }
    public int DomEvidencePageCount { get; init; }
    public int AccessibilityEvidencePageCount { get; init; }
    public int PerformanceEvidencePageCount { get; init; }
    /// <summary>Routes evidence was captured on. Not routes that are open.</summary>
    public List<string> HistoricalRoutes { get; init; } = [];

    public bool Any => PagesWithEvidence > 0;
    public static readonly BrowserCompanionEvidenceSummary Empty = new();
}

/// <summary>
/// How current a piece of evidence is relative to what is being claimed with it. Browser Discovery does not care;
/// Critical E2E does, because evidence captured yesterday must never stand in as proof of today's run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserEvidenceFreshness
{
    /// <summary>Captured before the current browser session. Diagnostics only.</summary>
    Historical,
    /// <summary>Captured during the current browser session, but before the run that is citing it.</summary>
    CurrentSession,
    /// <summary>Captured after the run started, on the page the run is bound to. The only kind that can back a result.</summary>
    CurrentRun,
}

public static class BrowserEvidenceFreshnessPolicy
{
    /// <summary>
    /// Freshness is a fact about time and page, not a judgement. Evidence only counts as a run's own when it was
    /// captured after the run started AND on the page the run is bound to — either condition alone lets a snapshot from
    /// a different tab, or from before the first click, be cited as the outcome.
    /// </summary>
    public static BrowserEvidenceFreshness Classify(
        DateTimeOffset capturedAt, DateTimeOffset? sessionStartedAt, DateTimeOffset? runStartedAt,
        string? evidenceIdentity = null, string? runPageIdentity = null)
    {
        var samePage = runPageIdentity is null || string.Equals(evidenceIdentity, runPageIdentity, StringComparison.OrdinalIgnoreCase);
        if (runStartedAt is { } run && capturedAt >= run && samePage) return BrowserEvidenceFreshness.CurrentRun;
        if (sessionStartedAt is { } session && capturedAt >= session) return BrowserEvidenceFreshness.CurrentSession;
        return BrowserEvidenceFreshness.Historical;
    }
}
