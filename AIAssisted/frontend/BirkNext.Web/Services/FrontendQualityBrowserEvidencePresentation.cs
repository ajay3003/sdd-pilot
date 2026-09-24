using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// The Frontend Quality Review's view of the browser evidence it CONSUMES.
///
/// Browser Discovery owns the Browser Companion: pairing, approved origins, the extension, and the raw evidence itself.
/// This review reads that state and says what it means for the next run — nothing here pairs, unpairs, approves an
/// origin or installs anything, and every value is delegated to <see cref="BrowserDiscoveryLive"/> and
/// <see cref="BrowserDiscoveryPresentation"/> rather than derived a second time.
///
/// The split that the whole record exists to preserve: <b>live</b> facts (is the companion reporting, which page is
/// open right now) and <b>historical</b> facts (what was captured, and when). "Connected" never implies fresh evidence,
/// and captured evidence never implies an open browser.
/// </summary>
public static class FrontendQualityBrowserEvidencePresentation
{
    /// <summary>Browser Discovery, where the evidence and the companion session actually belong.</summary>
    public const string BrowserDiscoveryHref = FrontendQualityTargetAccess.TargetEnvironmentsHref + "&tab=browser";

    public const string OwnershipNote =
        "Browser evidence is collected by Browser Companion and owned by Browser Discovery. This review reads it.";

    public static FrontendQualityBrowserEvidenceModel Build(BrowserCompanionStatus? status, EndpointDiscoverySnapshot? snapshot)
    {
        var session = status?.State ?? BrowserCompanionState.NotPaired;
        var live = status?.EffectiveLive ?? BrowserCompanionLiveSession.Disconnected;
        var connected = session == BrowserCompanionState.Connected;
        var totals = BrowserDiscoveryLive.Totals(status, snapshot);

        return new FrontendQualityBrowserEvidenceModel(
            LiveStatus: BrowserDiscoveryStates.SessionLabel(session),
            LiveConnected: connected,
            // Only the live page, and only while the session is live. A route from stored evidence would be a claim
            // about a browser that may have been closed hours ago.
            // The ROUTE, because that is what identifies a page to a reviewer. The full identity (origin + route) is
            // unchanged and goes to technical details; this is a reading of it, not a second identity.
            CurrentPage: connected ? BrowserDiscoveryLive.CurrentRouteLabel(live) : "None",
            CurrentPageIdentity: connected ? BrowserDiscoveryLive.CurrentPageLabel(live) : "None",
            HasLivePage: connected && live.CurrentPage is not null,
            LiveDomAvailable: connected && live.LiveDomAvailable,
            PagesCaptured: totals.Pages,
            DomEvidence: BrowserDiscoveryLive.Captured(totals.DomPages),
            AccessibilityEvidence: BrowserDiscoveryLive.Captured(totals.AccessibilityPages),
            PerformanceEvidence: BrowserDiscoveryLive.Captured(totals.PerformancePages),
            LastCapturedAt: totals.LastCapturedAt,
            ApprovedOrigins: status?.ApprovedOrigins ?? []);
    }

    /// <summary>
    /// The collapsed row: one live fact and one historical fact, joined but never merged. History is stated even when
    /// nothing is connected — evidence captured yesterday is still the evidence this review will read.
    /// </summary>
    public static string Collapsed(FrontendQualityBrowserEvidenceModel model) =>
        $"Live: {model.LiveStatus} · Historical: {(model.PagesCaptured == 0 ? "no pages captured" : $"{model.PagesCaptured} page{(model.PagesCaptured == 1 ? "" : "s")} captured")}";
}
