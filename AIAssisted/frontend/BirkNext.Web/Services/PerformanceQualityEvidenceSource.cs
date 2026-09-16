using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// Supplies the BirkNext Performance Quality engine with evidence: the Browser Companion status (page/runtime/resource/Blazor metrics from
/// the user's own managed Edge) and the Local HTTPS proxy status (API/network samples), both already folded into the page-oriented Endpoint
/// Discovery store per page identity and generation. No Playwright, no CDP, no Lighthouse, no token: the engine only reads recorded evidence.
/// Evaluation is deterministic (<see cref="PerformanceQualityRules"/>); coverage is reported per layer, so missing evidence is never a pass.
/// </summary>
public interface IPerformanceQualityEvidenceSource
{
    Task<PerformanceQualityReviewResult> CollectAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default);
}

public sealed class PerformanceQualityEvidenceSource(BrowserCompanionRuntime companion, LocalHttpsProxyRuntime? proxy, IEndpointDiscoveryService discovery, IJSRuntime js) : IPerformanceQualityEvidenceSource
{
    public const string NoEvidenceMessage = "No performance evidence collected for this Target Environment. Pair the Browser Companion and/or start the Local HTTPS proxy, sign in with your normal managed Edge and visit application pages.";

    public async Task<PerformanceQualityReviewResult> CollectAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default)
    {
        var profile = context.ActiveProfile;
        var profileId = profile.Id;
        await companion.FollowAsync(profile);
        await companion.RefreshAsync();
        var companionStatus = companion.For(profileId);
        if (companionStatus.Pages.Count > 0)
            await discovery.MergeBrowserEvidenceAsync(js, profileId, companionStatus.Pages);

        LocalHttpsProxyState? proxyState = null;
        if (proxy is not null)
        {
            var proxyStatus = proxy.ForFingerprint(context.ManualVerificationFingerprint);
            if (proxyStatus.SessionId is null) proxyStatus = proxy.For(profile);
            proxyState = proxyStatus.State;
            if (proxyStatus.ObservedNetworkEndpoints.Count > 0)
                await discovery.MergeObservedAsync(js, profileId, proxyStatus.ObservedNetworkEndpoints);
        }

        var approved = BrowserCompanionScope.ApprovedOrigins(profile);
        var snapshot = discovery.GetSnapshot(profileId);
        var pages = snapshot.Pages
            .Where(p => approved.Count == 0 || approved.Contains(p.PageOrigin, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(p => p.LastObservedAt)
            .Select(p => PerformanceQualityRules.Evaluate(p, context.PerformanceThresholds, context.CoreWebVitalsThresholds))
            .Where(p => p.HasEvidence)
            .ToList();
        var proxyEvidence = pages.Any(p => p.Api.Available);
        var browserEvidence = pages.Any(p => p.Coverage.Browser == PerformanceCoverageState.Complete);
        var coverage = PerformanceCoverage.Merge(pages.Select(p => p.Coverage));
        var thresholds = PerformanceQualityRules.ResolveThresholds(context.PerformanceThresholds, context.CoreWebVitalsThresholds).All.ToList();

        var limitations = new List<string>
        {
            "BirkNext Performance Quality observes pages the user visited manually in the paired browser; it does not crawl and issues no request to the target.",
            "Browser metrics are field measurements from the user's browser (PerformanceObserver); they are not Lighthouse lab scores and use different observation windows.",
            "LCP/CLS/Navigation Timing are measured for the initial document load only; SPA navigations report BirkNext Page Stabilization Time instead. Reload the target page for a fresh initial-load measurement.",
            "INP is published only from real Event Timing interactions above the sample minimum; otherwise it is reported as not measured.",
            "API latency is the proxy-observed duration (request head received → response relayed); p50/p95 are published only with at least " + PerformanceLatencyStatistics.MinimumSamplesForPercentiles + " samples.",
            "No configured polling classification exists; polling-like cadence is a heuristic that lowers confidence and never hides evidence.",
        };
        if (!browserEvidence) limitations.Add("Browser performance: not assessed — Browser Companion not connected or no page visited; only network/API evidence is available.");
        if (!proxyEvidence) limitations.Add("Network/API correlation unavailable: no Local HTTPS proxy traffic was recorded for these pages (proxy not running or not configured in the browser).");

        var message = pages.Count == 0 ? NoEvidenceMessage
            : $"{pages.Count} page(s) with performance evidence ({coverage.OverallLabel}); browser evidence {(browserEvidence ? "available" : "not available")}, API evidence {(proxyEvidence ? "available" : "not available")}.";

        return new PerformanceQualityReviewResult
        {
            CompanionState = companionStatus.State, CompanionMessage = message, ProxyState = proxyState, ProxyEvidenceAvailable = proxyEvidence,
            Pages = pages, Coverage = coverage, Limitations = limitations, EvaluatedAt = DateTimeOffset.UtcNow,
            BrowserName = pages.Select(p => p.BrowserName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)), Thresholds = thresholds,
        };
    }
}
