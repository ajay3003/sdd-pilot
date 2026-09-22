using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed record LiveDiscoveryState(bool ObservationActive, string ObservationSource,
    string AuthenticatedTrafficState, string PageCorrelationState, DateTimeOffset? LastTrafficAt,
    string TrafficFreshness, string? ActiveRuntimeId)
{
    // Presentation rule, not a runtime timeout: "Recent" means captured in the last two minutes
    // of the current running listener. No request frequency or authentication success is inferred.
    public static readonly TimeSpan RecentTrafficWindow = TimeSpan.FromMinutes(2);
    public static LiveDiscoveryState From(LocalHttpsProxyStatus status, DateTimeOffset now, bool hasHistory)
    {
        var active = status.ProxyListening && status.RuntimeStatus == LocalHttpsProxyRuntimePhase.Running;
        var live = status.ObservedNetworkEndpoints.Where(e => status.StartedAt is not { } start || e.LastObservedAt >= start).ToList();
        DateTimeOffset? last = active && live.Count > 0 ? live.Max(e => e.LastObservedAt) : null;
        var auth = !active ? "Unavailable" : live.Any(e => NetworkEvidencePolicy.IsApplicationTraffic(e) && e.AuthObserved)
            ? "Bearer observed" : "Not observed";
        var correlation = !active ? "Unavailable" : live.Any(e => NetworkEvidencePolicy.IsApplicationTraffic(e)
            && e.PageOrigin is not null && NetworkEvidencePolicy.IsApplicationPage(e.PagePath)) ? "Observed" : "Awaiting page evidence";
        var freshness = !active ? (hasHistory ? "Historical only" : "Unknown")
            : last is { } at && at <= now && now - at <= RecentTrafficWindow ? "Recent" : "No recent traffic";
        return new(active, "Local HTTPS Proxy", auth, correlation, last, freshness, active ? status.RuntimeId : null);
    }
}

public sealed record ObservedNetworkEvidenceSummary(int ApplicationPageCount, int BackendHostCount,
    int TotalObservedRequestCount, int RestCallCount, int GraphQlCallCount, int AuthCallCount,
    int OtherCallCount, int SharedBackgroundCount, int ProbeCount, DateTimeOffset? LastCapturedEvidenceAt)
{
    public static ObservedNetworkEvidenceSummary From(EndpointDiscoverySnapshot snapshot)
    {
        var all = snapshot.Pages.SelectMany(p => p.Endpoints).Concat(snapshot.Shared).ToList();
        var app = all.Where(NetworkEvidencePolicy.IsApplicationTraffic).ToList();
        int Calls(ObservedTrafficCategory c) => app.Where(e => e.Category == c).Sum(e => e.Count);
        return new(snapshot.Pages.Where(p => NetworkEvidencePolicy.IsApplicationPage(p.PagePath)).Select(p => p.Identity).Distinct().Count(),
            app.Select(e => e.Host).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            app.Sum(e => e.Count), Calls(ObservedTrafficCategory.Rest), Calls(ObservedTrafficCategory.GraphQl), Calls(ObservedTrafficCategory.Authentication),
            app.Where(e => e.Category is not (ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl or ObservedTrafficCategory.Authentication)).Sum(e => e.Count),
            snapshot.Shared.Where(e => NetworkEvidencePolicy.ProvenanceOf(e) is not (RequestProvenance.DiscoveryProbe or RequestProvenance.BirkNextDiagnostic)).Sum(e => e.Count),
            all.Where(e => NetworkEvidencePolicy.ProvenanceOf(e) == RequestProvenance.DiscoveryProbe).Sum(e => e.Count),
            all.Count == 0 ? null : all.Max(e => e.LastObservedAt));
    }
}

public sealed record HistoricalAnalysisState(int RetainedPageAnalyses, int ExcludedTechnicalAnalyses,
    IReadOnlyList<PageAnalysis> Routes)
{
    public static HistoricalAnalysisState From(EndpointDiscoverySnapshot snapshot) =>
        new(snapshot.Pages.Count, snapshot.ExcludedPageHistory.Count, snapshot.Pages.OrderBy(p => p.Identity).ToList());
}
