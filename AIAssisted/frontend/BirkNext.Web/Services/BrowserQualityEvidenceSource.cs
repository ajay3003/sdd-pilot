using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// Supplies the BirkNext Browser Quality engine with evidence: the Browser Companion status for the environment under review plus the
/// per-page evidence already merged into Endpoint Discovery (companion DOM/accessibility/performance/runtime/Blazor summaries and, when the
/// Local HTTPS proxy ran, network evidence for the same pages). No Playwright, no CDP, no token: the user's own managed Edge session is the
/// browser. Findings are derived deterministically by <see cref="BrowserQualityRules"/>.
/// </summary>
public interface IBrowserQualityEvidenceSource
{
    Task<BrowserQualityReviewResult> CollectAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default);
}

public sealed class BrowserQualityEvidenceSource(BrowserCompanionRuntime companion, IEndpointDiscoveryService discovery, IJSRuntime js) : IBrowserQualityEvidenceSource
{
    public const string NotConnectedMessage = "Browser Companion not connected. Pair the BirkNext Browser Companion in the Target Environment and open the application in your managed Edge.";
    public const string NoEvidenceMessage = "Browser Companion is connected but no page of this Target Environment has been visited yet. Navigate the application in your managed Edge.";

    public async Task<BrowserQualityReviewResult> CollectAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default)
    {
        var profileId = context.ActiveProfile.Id;
        await companion.FollowAsync(context.ActiveProfile);
        await companion.RefreshAsync();
        var status = companion.For(profileId);

        // Fold the latest companion evidence into the page-oriented store first so the review and Endpoint Discovery agree.
        if (status.Pages.Count > 0)
            await discovery.MergeBrowserEvidenceAsync(js, profileId, status.Pages);

        var approved = BrowserCompanionScope.ApprovedOrigins(context.ActiveProfile);
        var pages = discovery.GetSnapshot(profileId).Pages
            .Where(p => p.BrowserEvidence is not null && approved.Contains(p.PageOrigin, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(p => p.LastObservedAt)
            .ToList();
        var proxyEvidence = pages.Any(p => p.Endpoints.Count > 0);
        var findings = pages.SelectMany(p => BrowserQualityRules.Evaluate(p, context.PerformanceThresholds, context.CoreWebVitalsThresholds)).ToList();

        var limitations = new List<string>
        {
            "BirkNext Browser Quality evaluates pages the user visited manually in the paired browser; it does not crawl.",
            "BirkNext Accessibility Checks are a conservative native rule set, not full axe coverage and not WCAG conformance.",
            "Browser performance metrics are field measurements from the user's browser, not Lighthouse lab scores.",
            "Console output is not intercepted; runtime evidence comes from window error, unhandledrejection and resource error events.",
            proxyEvidence ? "Network evidence for these pages comes from the Local HTTPS proxy (Endpoint Discovery) and is correlated by page identity, not by causality."
                          : "Network correlation unavailable: no Local HTTPS proxy traffic was recorded for these pages (proxy not running or not configured in the browser).",
        };

        var message = status.State switch
        {
            BrowserCompanionState.Connected when pages.Count == 0 => NoEvidenceMessage,
            BrowserCompanionState.Connected => $"Browser Companion connected; {pages.Count} page(s) with evidence.",
            BrowserCompanionState.Disconnected when pages.Count > 0 => $"Browser Companion paired but not currently reporting; using the {pages.Count} page(s) already collected.",
            BrowserCompanionState.PairingPending => "Browser Companion pairing pending. Enter the pairing code in the extension.",
            BrowserCompanionState.Expired => "Browser Companion session expired. Pair again.",
            _ => NotConnectedMessage,
        };

        return new BrowserQualityReviewResult
        {
            CompanionState = status.State, CompanionMessage = message, ProxyEvidenceAvailable = proxyEvidence,
            PagesWithEvidence = pages.Count, PageIdentities = pages.Select(p => p.Identity).ToList(), Findings = findings,
            Limitations = limitations, EvaluatedAt = DateTimeOffset.UtcNow, BrowserName = pages.Select(p => p.BrowserEvidence!.BrowserName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
        };
    }
}
