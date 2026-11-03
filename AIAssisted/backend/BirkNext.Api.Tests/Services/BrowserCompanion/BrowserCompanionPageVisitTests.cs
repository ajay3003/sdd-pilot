using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.BrowserCompanion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.BrowserCompanion;

/// <summary>
/// Page history per session: every page a tab arrives at (opened, reloaded or SPA-navigated) is recorded from live page registration, before and
/// independently of evidence. Navigating on moves the previous page from Live now to historical without a revisit; a page visited but not
/// evidenced says so dimension by dimension (visit observed ≠ fully evidenced).
/// </summary>
public sealed class BrowserCompanionPageVisitTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private readonly TestTime _time = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly BrowserCompanionService _service;
    private string _sessionId = "";

    private sealed class TestTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    public BrowserCompanionPageVisitTests()
    {
        _service = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), _time, NullLogger<BrowserCompanionService>.Instance);
        var challenge = _service.StartPairing(new BrowserCompanionPairingStartRequest("dev", "M2LB DEV", "Development", [Origin]));
        _sessionId = _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).SessionId!;
    }

    private void Beat(params (string Tab, string Route, string Instance)[] pages)
    {
        _service.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", null, null, "0.1.0",
            pages.Select(p => new BrowserCompanionLivePageReport($"t{p.Tab}-{p.Instance}", Origin, p.Route, p.Instance)).ToList()), Extension);
        _time.Advance(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Every_route_a_tab_navigates_through_is_kept_and_the_previous_one_becomes_historical_without_a_revisit()
    {
        foreach (var route in new[] { "/", "/person", "/admin/operations", "/admin/general-roles", "/admin/emergency-access" })
            Beat(("1", route, "a")); // one tab, SPA navigation
        Beat(("1", "/admin/emergency-access", "a")); // a heartbeat on the same route is not a new visit

        var visits = _service.Status("dev").PageVisits;
        visits.Select(v => v.Route).Should().BeEquivalentTo(["/", "/person", "/admin/operations", "/admin/general-roles", "/admin/emergency-access"]);
        visits.Single(v => v.Live).Route.Should().Be("/admin/emergency-access", "only the page open now is live");
        visits.Single(v => v.Route == "/admin/emergency-access").VisitCount.Should().Be(1);
        visits.Should().OnlyContain(v => !v.DomCaptured && !v.AccessibilityCaptured && !v.PerformanceCaptured, "no evidence was sent: visited, not evidenced");
        visits[0].Route.Should().Be("/admin/emergency-access", "newest first");
    }

    [Fact]
    public void Returning_to_a_route_counts_a_new_visit_and_evidence_flags_follow_the_captured_dimensions()
    {
        Beat(("1", "/person", "a"));
        Beat(("1", "/admin/operations", "a"));
        Beat(("1", "/person", "a"));
        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope
        {
            SessionId = _sessionId, ProfileId = "dev", ExtensionVersion = "0.1.0",
            Pages = [new BrowserPageEvidence { ProfileId = "dev", PageOrigin = Origin, PagePath = "/person", VisitStartedAt = _time.GetUtcNow(), CapturedAt = _time.GetUtcNow(),
                Dom = new BrowserDomSummary { NodeCount = 10 } }],
        }, Extension);

        var person = _service.Status("dev").PageVisits.Single(v => v.Route == "/person");
        person.VisitCount.Should().Be(2);
        (person.DomCaptured, person.AccessibilityCaptured, person.PerformanceCaptured).Should().Be((true, false, false));
        _service.Status("dev").PageVisits.Single(v => v.Route == "/admin/operations").Live.Should().BeFalse();
    }

    [Fact]
    public void A_closed_tab_leaves_its_page_in_the_history()
    {
        Beat(("1", "/person", "a"), ("2", "/admin/operations", "b"));
        Beat(("1", "/person", "a")); // tab 2 closed

        _service.Status("dev").PageVisits.Select(v => (v.Route, v.Live)).Should().BeEquivalentTo([("/person", true), ("/admin/operations", false)]);
    }
}
