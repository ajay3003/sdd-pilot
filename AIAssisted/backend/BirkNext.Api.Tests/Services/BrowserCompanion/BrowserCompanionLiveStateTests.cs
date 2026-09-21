using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.BrowserCompanion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.BrowserCompanion;

/// <summary>
/// Live browser state versus captured evidence.
///
/// These were one model, and that is how BirkNext could report "Connected · 4 pages with evidence · DOM available" for
/// a browser with no application page open at all: an arriving evidence envelope set the current page. Every test here
/// pins one direction of the separation — live state never comes from evidence, and evidence never disappears because
/// a tab closed.
/// </summary>
public sealed class BrowserCompanionLiveStateTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private readonly TestTime _time = new(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly BrowserCompanionService _service;
    private string _sessionId = "";

    private sealed class TestTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    public BrowserCompanionLiveStateTests() =>
        _service = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), _time, NullLogger<BrowserCompanionService>.Instance);

    private void Pair(string profile = "dev")
    {
        var challenge = _service.StartPairing(new BrowserCompanionPairingStartRequest(profile, "M2LB DEV", "Development", [Origin]));
        _sessionId = _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).SessionId!;
    }

    /// <summary>A heartbeat carrying the live pages the extension currently has content scripts on.</summary>
    private BrowserCompanionAcceptResult Beat(params (string Tab, string Route, string Instance)[] pages) =>
        _service.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", null, null, "0.1.0",
            pages.Select(p => new BrowserCompanionLivePageReport($"t{p.Tab}-{p.Instance}", Origin, p.Route, p.Instance)).ToList()), Extension);

    private void SendEvidence(string path = "/saker")
    {
        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope
        {
            SessionId = _sessionId, ProfileId = "dev", ExtensionVersion = "0.1.0",
            Pages = [new BrowserPageEvidence
            {
                ProfileId = "dev", PageOrigin = Origin, PagePath = path, VisitStartedAt = _time.GetUtcNow(),
                CapturedAt = _time.GetUtcNow(), Dom = new BrowserDomSummary { NodeCount = 10 },
                Accessibility = new BrowserAccessibilitySummary(), Performance = new BrowserPerformanceSummary(),
            }],
        }, Extension);
        // The envelope rate limit is per session, so a second one in the same instant would be refused.
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    private BrowserCompanionStatus Status() => _service.Status("dev");

    // ── Session versus page ───────────────────────────────────────────────────

    [Fact]
    public void AHeartbeatMakesTheExtensionConnectedAndNothingElse()
    {
        Pair();
        Beat();
        var status = Status();
        status.State.Should().Be(BrowserCompanionState.Connected);
        status.Live.ExtensionConnected.Should().BeTrue();
        status.Live.LiveApprovedPageCount.Should().Be(0, "a connected extension is not an open application page");
        status.Live.CurrentPage.Should().BeNull();
        status.Live.ContentScriptAlive.Should().BeFalse();
        status.Live.LiveDomAvailable.Should().BeFalse();
        status.Live.AutomationAvailable.Should().BeFalse();
    }

    // ── Live page without evidence ────────────────────────────────────────────

    [Fact]
    public void AContentScriptRegistrationCreatesALivePageBeforeAnyEvidenceExists()
    {
        Pair();
        Beat(("1", "/admin/operations", "abc"));

        var status = Status();
        status.Live.LiveApprovedPageCount.Should().Be(1);
        status.Live.CurrentRoute.Should().Be("/admin/operations");
        status.Live.ContentScriptAlive.Should().BeTrue();
        status.Live.LiveDomAvailable.Should().BeTrue();
        status.Live.AutomationAvailable.Should().BeTrue();
        status.Evidence.PagesWithEvidence.Should().Be(0, "nothing has been captured yet, and that is a valid state");
    }

    // ── Historical evidence without a live page ───────────────────────────────

    [Fact]
    public void EvidenceNeverMakesAPageLive()
    {
        Pair();
        Beat();              // connected, nothing open
        SendEvidence();      // a late snapshot from a tab that has already gone

        var status = Status();
        status.Evidence.PagesWithEvidence.Should().Be(1);
        status.Live.LiveApprovedPageCount.Should().Be(0, "an evidence envelope is not a browser");
        status.Live.CurrentPage.Should().BeNull();
        status.CurrentPageOrigin.Should().BeNull();
        status.Live.LiveDomAvailable.Should().BeFalse();
    }

    [Fact]
    public void ClosingEveryTabKeepsTheEvidenceAndClearsTheLiveState()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        SendEvidence();
        Status().Evidence.PagesWithEvidence.Should().Be(1);

        Beat();   // the tab closed; the heartbeat no longer mentions it

        var status = Status();
        status.State.Should().Be(BrowserCompanionState.Connected, "the extension is still there");
        status.Live.LiveApprovedPageCount.Should().Be(0);
        status.Live.CurrentPage.Should().BeNull();
        status.Live.ContentScriptAlive.Should().BeFalse();
        status.Evidence.PagesWithEvidence.Should().Be(1, "evidence is not deleted because a tab closed");
        status.Evidence.DomEvidencePageCount.Should().Be(1);
        status.Evidence.LastEvidenceAt.Should().NotBeNull();
    }

    // ── Mixed state ───────────────────────────────────────────────────────────

    [Fact]
    public void TheCurrentPageIsTheLiveOneEvenWhenEvidenceExistsForAnother()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        SendEvidence("/saker");
        Beat(("1", "/arkiv", "abc"));   // same page, the user navigated

        var status = Status();
        status.Live.CurrentRoute.Should().Be("/arkiv");
        status.Evidence.HistoricalRoutes.Should().Contain("/saker");
        status.Evidence.PagesWithEvidence.Should().Be(1);
    }

    // ── SPA transition ────────────────────────────────────────────────────────

    [Fact]
    public void ARouteChangeMovesTheRouteAndNeverDropsThePage()
    {
        Pair();
        Beat(("1", "/admin/operations", "abc"));
        var before = Status().Live.CurrentPageId;

        foreach (var route in new[] { "/admin/general-roles", "/admin/operations", "/saker" })
        {
            Beat(("1", route, "abc"));
            var live = Status().Live;
            live.LiveApprovedPageCount.Should().Be(1, $"the page stays alive across {route}");
            live.CurrentRoute.Should().Be(route);
            live.CurrentPageId.Should().Be(before, "a route change is not a new page");
        }
    }

    // ── Reload ────────────────────────────────────────────────────────────────

    [Fact]
    public void AReloadReplacesTheLivePageRatherThanAddingOne()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        Beat(("1", "/saker", "xyz"));   // same tab, new content script instance

        var live = Status().Live;
        live.LiveApprovedPageCount.Should().Be(1, "a reload is the same tab, not a second one");
        live.LivePages.Single().ContentScriptInstanceId.Should().Be("xyz");
    }

    // ── Multiple tabs ─────────────────────────────────────────────────────────

    [Fact]
    public void TwoOpenPagesMeanThereIsNoCurrentPageToPick()
    {
        Pair();
        Beat(("1", "/saker", "abc"), ("2", "/arkiv", "def"));

        var live = Status().Live;
        live.LiveApprovedPageCount.Should().Be(2);
        live.CurrentPage.Should().BeNull("picking one would silently aim a command at whichever registered first");
        live.AutomationAvailable.Should().BeFalse();
        live.ContentScriptAlive.Should().BeTrue();
        live.LiveDomAvailable.Should().BeTrue();
    }

    // ── Expiry ────────────────────────────────────────────────────────────────

    [Fact]
    public void ALivePageExpiresWhenTheContentScriptStopsSayingItIsThere()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        _time.Advance(BrowserCompanionService.LivePageLifetime + TimeSpan.FromSeconds(5));
        // The extension is still alive; only the page stopped reporting.
        _service.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", null, null, "0.1.0", []), Extension);

        Status().Live.LiveApprovedPageCount.Should().Be(0);
    }

    [Fact]
    public void ADisconnectedSessionHasNoLivePagesWhateverItLastReported()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        // Past the connected window but inside the session lifetime: paired, not reporting.
        _time.Advance(BrowserCompanionLimits.ConnectedWindow + TimeSpan.FromSeconds(5));

        var status = Status();
        status.State.Should().Be(BrowserCompanionState.Disconnected);
        status.Live.ExtensionConnected.Should().BeFalse();
        status.Live.LiveApprovedPageCount.Should().Be(0, "liveness that outlives its own evidence of life is not liveness");
    }

    // ── Origin boundary ───────────────────────────────────────────────────────

    [Fact]
    public void APageOnAnUnapprovedOriginIsNeverLive()
    {
        Pair();
        _service.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", null, null, "0.1.0",
            [new BrowserCompanionLivePageReport("t1-abc", "https://elsewhere.example", "/x", "abc")]), Extension);

        Status().Live.LiveApprovedPageCount.Should().Be(0);
    }

    [Fact]
    public void AnOlderExtensionThatReportsOnlyOneCurrentPageStillCounts()
    {
        Pair();
        // No livePages field at all — the compatibility path, which can describe one page and never two.
        _service.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", Origin, "/saker", "0.1.0"), Extension);

        var live = Status().Live;
        live.LiveApprovedPageCount.Should().Be(1);
        live.CurrentRoute.Should().Be("/saker");
    }

    // ── Unpair ────────────────────────────────────────────────────────────────

    [Fact]
    public void UnpairingClearsLiveStateForThatSession()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        _service.Unpair("dev");

        var status = Status();
        status.State.Should().Be(BrowserCompanionState.NotPaired);
        status.Live.LiveApprovedPageCount.Should().Be(0);
        status.Live.ExtensionConnected.Should().BeFalse();
    }

    // ── Evidence freshness ────────────────────────────────────────────────────

    [Fact]
    public void EvidenceFromBeforeARunIsNeverThatRunsOwnEvidence()
    {
        var sessionStart = _time.GetUtcNow();
        var runStart = sessionStart.AddMinutes(5);

        BrowserEvidenceFreshnessPolicy.Classify(sessionStart.AddMinutes(-60), sessionStart, runStart, "p", "p")
            .Should().Be(BrowserEvidenceFreshness.Historical);
        BrowserEvidenceFreshnessPolicy.Classify(sessionStart.AddMinutes(1), sessionStart, runStart, "p", "p")
            .Should().Be(BrowserEvidenceFreshness.CurrentSession, "captured this session, but before the run that wants to cite it");
        BrowserEvidenceFreshnessPolicy.Classify(runStart.AddSeconds(30), sessionStart, runStart, "p", "p")
            .Should().Be(BrowserEvidenceFreshness.CurrentRun);
    }

    [Fact]
    public void EvidenceFromAnotherPageIsNotThisRunsEvidenceHoweverRecentItIs()
    {
        var sessionStart = _time.GetUtcNow();
        var runStart = sessionStart.AddMinutes(5);
        BrowserEvidenceFreshnessPolicy.Classify(runStart.AddSeconds(30), sessionStart, runStart, "https://m2lbdev.bufetat.no/other", "https://m2lbdev.bufetat.no/saker")
            .Should().Be(BrowserEvidenceFreshness.CurrentSession, "a snapshot from a different tab cannot be the run's outcome");
    }
}
