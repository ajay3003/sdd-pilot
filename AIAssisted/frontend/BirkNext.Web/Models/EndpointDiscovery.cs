using System.Text.Json.Serialization;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Web.Models;

/// <summary>
/// Safe, per-Target-Environment persistence of page-oriented endpoint discovery. Holds only non-secret network metadata: no bearer
/// token, Authorization value, cookie, request body, response body or sensitive query string, and no transient proxy session id.
/// Persisted in the separate <c>birknext:endpoint-discovery</c> store, never in the Target Environment configuration.
/// </summary>
public sealed class EndpointDiscoverySnapshot
{
    public WcagSettings Wcag { get; set; } = new();
    public List<WcagManualReview> WcagApplicationReviews { get; set; } = [];
    [JsonPropertyName("pages")] public List<PageAnalysis> Pages { get; set; } = [];
    /// <summary>Endpoints that could not be safely correlated to one page (background poll, shared config, telemetry, global auth).</summary>
    [JsonPropertyName("shared")] public List<ObservedNetworkEndpoint> Shared { get; set; } = [];
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One analyzed application page and the endpoints it was observed to communicate with. Relationship-oriented: an endpoint used by two pages is a separate row under each page, so deleting one page never removes the other page's evidence.</summary>
public sealed class PageAnalysis
{
    public List<WcagManualReview> WcagReviews { get; set; } = [];
    [JsonPropertyName("origin")] public string PageOrigin { get; set; } = "";
    [JsonPropertyName("path")] public string PagePath { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("firstObservedAt")] public DateTimeOffset FirstObservedAt { get; set; }
    [JsonPropertyName("lastObservedAt")] public DateTimeOffset LastObservedAt { get; set; }
    [JsonPropertyName("endpoints")] public List<ObservedNetworkEndpoint> Endpoints { get; set; } = [];

    /// <summary>
    /// The current analysis generation. "Refresh analysis" increments it and stamps <see cref="RefreshedAtUtc"/>; only traffic observed at
    /// or after that boundary counts toward the page, so old observations never reappear after a refresh.
    /// </summary>
    [JsonPropertyName("analysisGeneration")] public int AnalysisGeneration { get; set; } = 1;

    /// <summary>UTC boundary of the current analysis generation, set by "Refresh analysis". Null means the page has never been refreshed (all observed traffic counts).</summary>
    [JsonPropertyName("refreshedAtUtc")] public DateTimeOffset? RefreshedAtUtc { get; set; }

    /// <summary>
    /// Latest safe Browser Companion evidence for this page (DOM/accessibility/performance/runtime/Blazor summaries from the user's own
    /// managed Edge session). Same generation rule as endpoints: only a visit that started at or after <see cref="RefreshedAtUtc"/> counts.
    /// Never raw DOM, never a credential.
    /// </summary>
    [JsonPropertyName("browserEvidence")] public BrowserPageEvidence? BrowserEvidence { get; set; }

    /// <summary>
    /// Compact, threshold-independent performance numbers per analysis generation (BirkNext Performance Quality regression history):
    /// the current generation's entry is replaced as evidence arrives; older generations stay for Current vs Previous comparison.
    /// Bounded; raw metrics only, never a URL, header, body or credential.
    /// </summary>
    [JsonPropertyName("performanceHistory")] public List<PagePerformanceHistoryEntry> PerformanceHistory { get; set; } = [];

    /// <summary>Stable identity: scheme+host+normalized path, no query string or credentials.</summary>
    [JsonIgnore] public string Identity => $"{PageOrigin}{PagePath}";
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(DisplayName) ? (PagePath.Length == 0 ? "/" : PagePath) : DisplayName!;
    /// <summary>The page was refreshed and neither traffic nor browser evidence has been observed for the new generation yet.</summary>
    [JsonIgnore] public bool IsWaitingForFreshTraffic => RefreshedAtUtc is not null && Endpoints.Count == 0 && BrowserEvidence is null;
    [JsonIgnore] public bool HasBrowserEvidence => BrowserEvidence is not null;
}
