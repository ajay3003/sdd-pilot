using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>Live: open in the paired browser right now with evidence. Recent / Historical / Stale: captured earlier (≤ 24 h / ≤ 30 days / older).
/// Historical is usable evidence with a limitation — not "bad"; only a review that requires live evidence treats it as insufficient.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PageEvidenceFreshness { Live, Recent, Historical, Stale }

/// <summary>One page's browser evidence as Frontend Quality Review reads it: liveness, freshness, which evidence kinds exist, and a fingerprint
/// per evidence kind so a later capture can be recognised as newer evidence for the same page.</summary>
public sealed record BrowserEvidencePage(
    string Identity, string Route, bool Live, PageEvidenceFreshness Freshness, DateTimeOffset? CapturedAt,
    bool Dom, bool Accessibility, bool Performance, string Fingerprint, string? DomVersion, string? AccessibilityVersion, string? PerformanceVersion);

/// <summary>
/// The exact browser evidence a Frontend Quality Review used (frozen in its report): page, route, capture time, live/historical, and the
/// per-kind evidence versions. Later Browser Discovery captures never rewrite it; they are compared against it ("Newer browser evidence available").
/// </summary>
public sealed record BrowserEvidenceUsed
{
    public string Identity { get; init; } = "";
    public string Route { get; init; } = "";
    public DateTimeOffset? CapturedAt { get; init; }
    public bool Live { get; init; }
    public PageEvidenceFreshness Freshness { get; init; }
    public string Fingerprint { get; init; } = "";
    public string? DomVersion { get; init; }
    public string? AccessibilityVersion { get; init; }
    public string? PerformanceVersion { get; init; }
}

public sealed record BrowserEvidenceChange(string Identity, string Route, DateTimeOffset? UsedCapturedAt, DateTimeOffset? LatestCapturedAt, IReadOnlyList<string> ChangedKinds);

/// <summary>All browser evidence of one Target Environment (approved origins), live pages first, then most recent.</summary>
public sealed record FrontendBrowserEvidence(IReadOnlyList<BrowserEvidencePage> Pages, BrowserCompanionState? CompanionState, int LiveWithoutEvidence, DateTimeOffset EvaluatedAt)
{
    public static readonly FrontendBrowserEvidence Empty = new([], null, 0, DateTimeOffset.MinValue);
    public int LivePages => Pages.Count(p => p.Live);
    public int HistoricalPages => Pages.Count(p => !p.Live);
    public int StalePages => Pages.Count(p => p.Freshness == PageEvidenceFreshness.Stale);
    public int DomPages => Pages.Count(p => p.Dom);
    public int AccessibilityPages => Pages.Count(p => p.Accessibility);
    public int PerformancePages => Pages.Count(p => p.Performance);

    /// <summary>Limitations (never blockers): historical pages are usable with a limitation; stale ones need review.</summary>
    public IReadOnlyList<string> Limitations
    {
        get
        {
            var list = new List<string>();
            var historical = Pages.Count(p => p.Freshness is PageEvidenceFreshness.Recent or PageEvidenceFreshness.Historical);
            if (historical > 0) list.Add($"{historical} page{(historical == 1 ? " uses" : "s use")} historical evidence — not revalidated in the current session");
            if (StalePages > 0) list.Add($"{StalePages} page{(StalePages == 1 ? " has" : "s have")} stale evidence (older than 30 days) — review before relying on it");
            if (LiveWithoutEvidence > 0) list.Add($"{LiveWithoutEvidence} live page{(LiveWithoutEvidence == 1 ? "" : "s")} without evidence yet");
            return list;
        }
    }

    /// <summary>The page "Review current page only" reviews: the one page open in the paired browser with evidence. Null when none is open,
    /// or when several are (which one is "current" is then not known — never guessed).</summary>
    public BrowserEvidencePage? CurrentPage => Pages.Where(p => p.Live).ToList() is [var only] ? only : null;

    /// <summary>Why "Review current page only" is unavailable, or null when it is available.</summary>
    public string? CurrentPageUnavailable => CurrentPage is not null ? null
        : LivePages > 1 ? $"{LivePages} pages are open in the paired browser; keep only the page to review open."
        : "No page is open in the paired browser. Open the page in your managed Edge to review it alone.";

    /// <summary>In "current runtime required" mode, pages without live evidence cannot be reviewed as current: that is the only blocker.</summary>
    public string? LiveRequiredBlocker => Pages.Count == 0 ? null
        : HistoricalPages > 0 ? $"Current-runtime review requires live evidence: {HistoricalPages} of {Pages.Count} page{(Pages.Count == 1 ? "" : "s")} have historical evidence only. Open them in the paired browser, or turn off \"Require live evidence\"."
        : null;
}

/// <summary>
/// Loads Frontend Quality Review's browser evidence itself — the per-environment Endpoint Discovery store (persisted in this browser) plus the
/// Browser Companion's accepted pages, merged with the same rule Browser Discovery uses — so FQR never depends on Browser Discovery having been
/// opened. Browser Discovery stays a capture/viewer surface. Nothing here judges quality.
/// </summary>
public static class FrontendBrowserEvidences
{
    public static async Task<FrontendBrowserEvidence> LoadAsync(IEndpointDiscoveryService discovery, IJSRuntime js, BrowserCompanionRuntime? companion,
        FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        await discovery.LoadAsync(js);
        ct.ThrowIfCancellationRequested();
        discovery.ConfigureTarget(profile);
        BrowserCompanionStatus? status = null;
        if (companion is not null)
        {
            await companion.FollowAsync(profile);
            status = companion.For(profile.Id);
            if (status.Pages.Count > 0) await discovery.MergeBrowserEvidenceAsync(js, profile.Id, status.Pages);
        }
        ct.ThrowIfCancellationRequested();
        return From(discovery.GetSnapshot(profile.Id), status, BrowserCompanionScope.ApprovedOrigins(profile), DateTimeOffset.UtcNow);
    }

    public static FrontendBrowserEvidence From(EndpointDiscoverySnapshot snapshot, BrowserCompanionStatus? status, IReadOnlyList<string> approvedOrigins, DateTimeOffset now)
    {
        var live = status?.EffectiveLive.LivePages.Select(p => p.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var pages = snapshot.Pages
            .Where(p => p.BrowserEvidence is not null && (approvedOrigins.Count == 0 || approvedOrigins.Contains(p.PageOrigin, StringComparer.OrdinalIgnoreCase)))
            .Select(p =>
            {
                var e = p.BrowserEvidence!;
                var isLive = live.Contains(p.Identity);
                var at = e.CapturedAt == default ? p.LastObservedAt : e.CapturedAt;
                return new BrowserEvidencePage(p.Identity, p.PagePath.Length == 0 ? "/" : p.PagePath, isLive, Freshness(isLive, at, now), at,
                    e.Dom is not null, e.Accessibility is not null, e.Performance is not null, Fingerprint(e), Version(e.Dom), Version(e.Accessibility), Version(e.Performance));
            })
            .OrderByDescending(p => p.Live).ThenByDescending(p => p.CapturedAt).ToList();
        var known = pages.Select(p => p.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(pages, status?.State, live.Count(id => !known.Contains(id)), now);
    }

    public static PageEvidenceFreshness Freshness(bool live, DateTimeOffset? at, DateTimeOffset now) => live ? PageEvidenceFreshness.Live
        : at is not { } t ? PageEvidenceFreshness.Stale
        : now - t <= ReviewDecisions.RecentWindow ? PageEvidenceFreshness.Recent
        : now - t <= ReviewDecisions.HistoricalWindow ? PageEvidenceFreshness.Historical
        : PageEvidenceFreshness.Stale;

    public static string FreshnessLabel(BrowserEvidencePage page) => page.Freshness switch
    {
        PageEvidenceFreshness.Live => "Live evidence",
        PageEvidenceFreshness.Stale => $"Stale evidence · last observed {Time(page.CapturedAt)}",
        _ => $"Historical evidence · last observed {Time(page.CapturedAt)}",
    };

    private static string Time(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "unknown";

    /// <summary>The frozen record of what a review used.</summary>
    public static List<BrowserEvidenceUsed> Used(FrontendBrowserEvidence inventory) =>
        [.. inventory.Pages.Select(p => new BrowserEvidenceUsed
        {
            Identity = p.Identity, Route = p.Route, CapturedAt = p.CapturedAt, Live = p.Live, Freshness = p.Freshness, Fingerprint = p.Fingerprint,
            DomVersion = p.DomVersion, AccessibilityVersion = p.AccessibilityVersion, PerformanceVersion = p.PerformanceVersion,
        })];

    /// <summary>Pages whose current evidence differs from what a review used (by fingerprint per evidence kind), never by timestamp alone.</summary>
    public static IReadOnlyList<BrowserEvidenceChange> NewerThan(IReadOnlyList<BrowserEvidenceUsed> used, FrontendBrowserEvidence current)
    {
        var changes = new List<BrowserEvidenceChange>();
        foreach (var u in used)
        {
            if (current.Pages.FirstOrDefault(p => string.Equals(p.Identity, u.Identity, StringComparison.OrdinalIgnoreCase)) is not { } now || now.Fingerprint == u.Fingerprint) continue;
            var kinds = new List<string>();
            if (now.DomVersion != u.DomVersion) kinds.Add("DOM");
            if (now.AccessibilityVersion != u.AccessibilityVersion) kinds.Add("accessibility");
            if (now.PerformanceVersion != u.PerformanceVersion) kinds.Add("performance");
            if (kinds.Count == 0) kinds.Add("capture");
            changes.Add(new(u.Identity, u.Route, u.CapturedAt, now.CapturedAt, kinds));
        }
        return changes;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Capture identity + per-kind evidence content (FNV-1a over the canonical JSON; no value leaves this process).</summary>
    public static string Fingerprint(BrowserPageEvidence e) =>
        Hash($"{e.CapturedAt:O}|{e.SnapshotKind}|{e.SnapshotSequence}|{Version(e.Dom)}|{Version(e.Accessibility)}|{Version(e.Performance)}|{Version(e.Runtime)}");

    private static string? Version<T>(T? evidence) where T : class => evidence is null ? null : Hash(JsonSerializer.Serialize(evidence, Json));

    private static string Hash(string text)
    {
        ulong hash = 14695981039346656037;
        foreach (var b in Encoding.UTF8.GetBytes(text)) { hash ^= b; hash *= 1099511628211; }
        return hash.ToString("x16");
    }
}
