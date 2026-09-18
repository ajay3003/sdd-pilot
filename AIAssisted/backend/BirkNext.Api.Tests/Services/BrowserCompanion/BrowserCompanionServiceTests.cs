using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.BrowserCompanion;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.BrowserCompanion;

/// <summary>
/// Browser Companion pairing, session binding, evidence validation and sanitization. The channel is loopback-only, bound to one
/// Target Environment and one extension origin, single-use pairing codes, bounded payloads, and never stores anything credential-shaped.
/// </summary>
public sealed class BrowserCompanionServiceTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string OtherExtension = "chrome-extension://zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
    private readonly TestTime _time = new(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));

    private sealed class TestTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
    private readonly BrowserCompanionService _service;

    public BrowserCompanionServiceTests()
    {
        _service = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), _time, NullLogger<BrowserCompanionService>.Instance);
    }

    private BrowserCompanionPairingChallenge Start(string profile = "dev", params string[] origins) =>
        _service.StartPairing(new BrowserCompanionPairingStartRequest(profile, "M2LB DEV", "Development", origins.Length == 0 ? ["https://m2lbdev.bufetat.no/"] : origins));

    private BrowserCompanionPairResult Pair(string profile = "dev")
    {
        var challenge = Start(profile);
        return _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension);
    }

    private static BrowserPageEvidence Page(string profile, string origin, string path, DateTimeOffset visit, int seq = 1) => new()
    {
        ProfileId = profile, PageOrigin = origin, PagePath = path, VisitStartedAt = visit, CapturedAt = visit.AddSeconds(seq), SnapshotSequence = seq,
        DocumentTitle = "Children search", Dom = new BrowserDomSummary { NodeCount = 1200 }, Performance = new BrowserPerformanceSummary { LcpMs = 2100 },
    };

    // ── Pairing ────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidPairing_CreatesSessionBoundToEnvironmentAndExtension()
    {
        var challenge = Start();
        challenge.PairingCode.Should().HaveLength(BrowserCompanionLimits.PairingCodeLength).And.MatchRegex("^[A-Z2-9]+$");
        _service.Status("dev").State.Should().Be(BrowserCompanionState.PairingPending);
        _service.Status("dev").PairingCode.Should().Be(challenge.PairingCode);

        var result = _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode.ToLowerInvariant(), "0.1.0"), Extension);

        result.Accepted.Should().BeTrue();
        result.SessionId.Should().HaveLength(64);
        result.ProfileId.Should().Be("dev");
        result.ApprovedOrigins.Should().BeEquivalentTo(["https://m2lbdev.bufetat.no"]);
        var status = _service.Status("dev");
        status.State.Should().Be(BrowserCompanionState.Connected);
        status.PairingCode.Should().BeNull("the code is consumed and never shown again");
        status.ApprovedOrigins.Should().BeEquivalentTo(["https://m2lbdev.bufetat.no"]);
    }

    [Fact]
    public void PairThenApprovedM2lbPage_HeartbeatsKeepReportingAndOnlyEvidenceIncrementsPages()
    {
        var paired = Pair();
        const string origin = "https://m2lbdev.bufetat.no";
        _service.Status("dev").State.Should().Be(BrowserCompanionState.Connected);
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", origin, "/", "0.1.0"), Extension)
                .Accepted.Should().BeTrue();
            var reporting = _service.Status("dev");
            reporting.State.Should().Be(BrowserCompanionState.Connected);
            reporting.CurrentPageOrigin.Should().Be(origin);
            reporting.CurrentPagePath.Should().Be("/");
            reporting.PagesWithEvidence.Should().Be(0);
        }
        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope
        {
            SessionId = paired.SessionId!, ProfileId = "dev",
            Pages = [Page("dev", origin, "/", _time.GetUtcNow())]
        }, Extension).Accepted.Should().BeTrue();
        _service.Status("dev").PagesWithEvidence.Should().Be(1);
        _time.Advance(TimeSpan.FromSeconds(30));
        _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", origin, "/", "0.1.0"), Extension);
        _service.Status("dev").State.Should().Be(BrowserCompanionState.Connected);
        _service.Status("dev").PagesWithEvidence.Should().Be(1);
    }

    [Fact]
    public void InvalidNonce_Rejected()
    {
        Start();
        _service.CompletePairing(new BrowserCompanionPairRequest("NOPE1234", "0.1.0"), Extension).Accepted.Should().BeFalse();
        _service.Status("dev").State.Should().Be(BrowserCompanionState.PairingPending, "a wrong code does not consume the challenge");
    }

    [Fact]
    public void ExpiredNonce_Rejected()
    {
        var challenge = Start();
        _time.Advance(BrowserCompanionLimits.PairingCodeLifetime + TimeSpan.FromSeconds(1));

        _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).Accepted.Should().BeFalse();
        _service.Status("dev").State.Should().Be(BrowserCompanionState.NotPaired);
    }

    [Fact]
    public void ReplayedNonce_Rejected()
    {
        var challenge = Start();
        _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).Accepted.Should().BeTrue();

        var replay = _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), OtherExtension);

        replay.Accepted.Should().BeFalse();
        _service.Status("dev").State.Should().Be(BrowserCompanionState.Connected, "the first pairing stays intact");
    }

    [Fact]
    public void WebPageOrigin_CannotPair()
    {
        var challenge = Start();
        _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), "http://localhost:5173").Accepted.Should().BeFalse();
        _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), "").Accepted.Should().BeFalse();
        _service.Status("dev").State.Should().Be(BrowserCompanionState.PairingPending);
    }

    [Fact]
    public void PairingAgain_InvalidatesPreviousSession()
    {
        var first = Pair();
        Start();
        var heartbeat = _service.Heartbeat(new BrowserCompanionHeartbeat(first.SessionId!, "dev", null, null, "0.1.0"), Extension);
        heartbeat.Accepted.Should().BeFalse();
        _service.Status("dev").State.Should().Be(BrowserCompanionState.PairingPending);
    }

    // ── Evidence validation ────────────────────────────────────────────────────

    [Fact]
    public void Evidence_ApprovedOrigin_Accepted_AndExposedInStatus()
    {
        var paired = Pair();
        var visit = _time.GetUtcNow();

        var result = _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", ExtensionVersion = "0.1.0",
            Pages = [Page("dev", "https://m2lbdev.bufetat.no", "/children/search", visit)] }, Extension);

        result.Accepted.Should().BeTrue();
        result.AcceptedPages.Should().Be(1);
        var status = _service.Status("dev");
        status.PagesWithEvidence.Should().Be(1);
        status.CurrentPagePath.Should().Be("/children/search");
        status.Pages.Single().Identity.Should().Be("https://m2lbdev.bufetat.no/children/search");
    }

    [Fact]
    public void Evidence_WrongEnvironment_Rejected()
    {
        var dev = Pair("dev");
        Pair("qa");

        var crossEnvironment = _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = dev.SessionId!, ProfileId = "qa",
            Pages = [Page("qa", "https://m2lbdev.bufetat.no", "/children", _time.GetUtcNow())] }, Extension);

        crossEnvironment.Accepted.Should().BeFalse("a DEV session cannot report into the QA context");
        _service.Status("qa").PagesWithEvidence.Should().Be(0);
        _service.Status("dev").PagesWithEvidence.Should().Be(0);
    }

    [Fact]
    public void Evidence_UnapprovedOrigin_Rejected_OtherPagesStillAccepted()
    {
        var paired = Pair();
        var visit = _time.GetUtcNow();

        var result = _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev",
            Pages = [Page("dev", "https://login.microsoftonline.com", "/common/oauth2", visit), Page("dev", "https://m2lbdev.bufetat.no", "/dashboard", visit)] }, Extension);

        result.AcceptedPages.Should().Be(1);
        result.RejectedPages.Should().Be(1);
        _service.Status("dev").Pages.Should().ContainSingle(p => p.PagePath == "/dashboard");
    }

    [Fact]
    public void Evidence_WrongSessionOrDifferentExtension_Rejected()
    {
        var paired = Pair();
        var page = Page("dev", "https://m2lbdev.bufetat.no", "/dashboard", _time.GetUtcNow());

        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = new string('A', 64), ProfileId = "dev", Pages = [page] }, Extension).Accepted.Should().BeFalse();
        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", Pages = [page] }, OtherExtension).Accepted.Should().BeFalse();
        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", Pages = [page] }, "https://m2lbdev.bufetat.no").Accepted.Should().BeFalse();
        _service.Status("dev").PagesWithEvidence.Should().Be(0);
    }

    [Fact]
    public void Evidence_StaleSession_Rejected_StatusExpired()
    {
        var paired = Pair();
        _time.Advance(BrowserCompanionLimits.SessionIdleLifetime + TimeSpan.FromMinutes(1));

        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev",
            Pages = [Page("dev", "https://m2lbdev.bufetat.no", "/dashboard", _time.GetUtcNow())] }, Extension).Accepted.Should().BeFalse();
        _service.Status("dev").State.Should().Be(BrowserCompanionState.Expired);
    }

    [Fact]
    public void Evidence_RateLimited_AndPagesPerEnvelopeBounded()
    {
        var paired = Pair();
        var visit = _time.GetUtcNow();
        var first = _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", Pages = [Page("dev", "https://m2lbdev.bufetat.no", "/a", visit)] }, Extension);
        var flood = _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", Pages = [Page("dev", "https://m2lbdev.bufetat.no", "/b", visit)] }, Extension);
        first.Accepted.Should().BeTrue();
        flood.Accepted.Should().BeFalse();
        flood.Message.Should().Contain("Rate limited");

        _time.Advance(TimeSpan.FromSeconds(1));
        var tooMany = _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev",
            Pages = Enumerable.Range(0, BrowserCompanionLimits.MaxPagesPerEnvelope + 1).Select(i => Page("dev", "https://m2lbdev.bufetat.no", $"/p{i}", visit)).ToList() }, Extension);
        tooMany.Accepted.Should().BeFalse();
    }

    [Fact]
    public void Evidence_LatestVisitWins_OlderVisitNeverOverwrites()
    {
        var paired = Pair();
        var older = _time.GetUtcNow();
        var newer = older.AddMinutes(5);
        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", Pages = [Page("dev", "https://m2lbdev.bufetat.no", "/x", newer, 3)] }, Extension);
        _time.Advance(TimeSpan.FromSeconds(1));
        var stale = _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", Pages = [Page("dev", "https://m2lbdev.bufetat.no", "/x", older, 9)] }, Extension);

        stale.RejectedPages.Should().Be(1);
        _service.Status("dev").Pages.Single().VisitStartedAt.Should().Be(newer);
    }

    [Fact]
    public void Heartbeat_TracksConnectionAndCurrentApprovedPageOnly()
    {
        var paired = Pair();
        _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", "https://m2lbdev.bufetat.no", "/children/search?child=123", "0.1.0"), Extension).Accepted.Should().BeTrue();
        _service.Status("dev").CurrentPagePath.Should().Be("/children/search", "query strings never enter the status");

        _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", "https://news.example.com", "/", "0.1.0"), Extension);
        _service.Status("dev").CurrentPageOrigin.Should().BeNull("unrelated tabs are not inspected");

        _time.Advance(BrowserCompanionLimits.ConnectedWindow + TimeSpan.FromSeconds(1));
        _service.Status("dev").State.Should().Be(BrowserCompanionState.Disconnected);
    }

    [Fact]
    public void Unpair_ClearsSessionAndEvidence()
    {
        var paired = Pair();
        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", Pages = [Page("dev", "https://m2lbdev.bufetat.no", "/a", _time.GetUtcNow())] }, Extension);

        _service.Unpair("dev").State.Should().Be(BrowserCompanionState.NotPaired);
        _service.Status("dev").PagesWithEvidence.Should().Be(0);
        _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", null, null, "0.1.0"), Extension).Accepted.Should().BeFalse();
    }

    // ── Credential safety ──────────────────────────────────────────────────────

    [Fact]
    public void Evidence_CredentialShapedValues_RedactedBeforeExposure()
    {
        var paired = Pair();
        var visit = _time.GetUtcNow();
        const string jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJVadQssw5c";
        var evidence = new BrowserPageEvidence
        {
            ProfileId = "dev", PageOrigin = "https://m2lbdev.bufetat.no", PagePath = "/children/search?access_token=SECRETQUERY#frag", VisitStartedAt = visit, CapturedAt = visit,
            DocumentTitle = $"Search for ola.nordmann@bufetat.no Bearer {jwt}",
            Runtime = new BrowserRuntimeSummary { ErrorCount = 1, Errors = [new BrowserRuntimeError { Kind = "error", Message = $"Failed: Authorization: Bearer {jwt} token=abc123def456 user=kari@bufetat.no", Source = "https://m2lbdev.bufetat.no/app.js?code=SECRETCODE", FirstAt = visit, LastAt = visit }] },
            Accessibility = new BrowserAccessibilitySummary { RulesEvaluated = 14, Findings = [new BrowserAccessibilityRuleResult { RuleId = "a11y-button-name", Severity = "High", Title = "Button without accessible name", Count = 2,
                Selectors = ["button.sp-btn", "input[value='SECRETVALUE']", "div#user-1234567", "a[href=\"mailto:kari@bufetat.no\"]"] }] },
            Performance = new BrowserPerformanceSummary { LongestResources = [new BrowserResourceEntry { Url = "https://api.example.test/children?token=SECRETQUERY", Kind = "api", DurationMs = 900 }] },
        };

        _service.AcceptEvidence(new BrowserCompanionEvidenceEnvelope { SessionId = paired.SessionId!, ProfileId = "dev", Pages = [evidence] }, Extension).AcceptedPages.Should().Be(1);

        var json = System.Text.Json.JsonSerializer.Serialize(_service.Status("dev"));
        json.Should().NotContainAny("SECRETQUERY", "SECRETCODE", "SECRETVALUE", jwt, "ola.nordmann@bufetat.no", "kari@bufetat.no", "Bearer ey", "#frag");
        var page = _service.Status("dev").Pages.Single();
        page.PagePath.Should().Be("/children/search");
        page.Runtime!.Errors.Single().Source.Should().Be("https://m2lbdev.bufetat.no/app.js");
        page.Accessibility!.Findings.Single().Selectors.Should().BeEquivalentTo(["button.sp-btn"], "only structural selectors survive");
        page.Performance!.LongestResources.Single().Url.Should().Be("https://api.example.test/children");
    }

    [Fact]
    public void Sanitizer_BoundsEveryList()
    {
        var sanitizer = new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer());
        var evidence = new BrowserPageEvidence
        {
            PageOrigin = "https://m2lbdev.bufetat.no", PagePath = "/", VisitStartedAt = DateTimeOffset.UtcNow,
            Runtime = new BrowserRuntimeSummary { Errors = Enumerable.Range(0, 500).Select(i => new BrowserRuntimeError { Message = $"error {i} " + new string('x', 5000) }).ToList() },
            Accessibility = new BrowserAccessibilitySummary { Findings = Enumerable.Range(0, 100).Select(i => new BrowserAccessibilityRuleResult { RuleId = $"a11y-rule-{i}", Selectors = Enumerable.Range(0, 50).Select(j => $"div.c{j}").ToList() }).ToList() },
            Performance = new BrowserPerformanceSummary { LongestResources = Enumerable.Range(0, 100).Select(i => new BrowserResourceEntry { Url = $"https://h/{i}" }).ToList() },
        };

        var s = sanitizer.Sanitize(evidence);

        s.Runtime!.Errors.Should().HaveCount(BrowserCompanionLimits.MaxRuntimeErrorsPerPage);
        s.Runtime.Errors[0].Message.Length.Should().BeLessThanOrEqualTo(BrowserCompanionLimits.MaxStringLength);
        s.Accessibility!.Findings.Should().HaveCount(BrowserCompanionLimits.MaxAccessibilityRulesPerPage);
        s.Accessibility.Findings[0].Selectors.Should().HaveCount(BrowserCompanionLimits.MaxSelectorsPerRule);
        s.Performance!.LongestResources.Should().HaveCount(BrowserCompanionLimits.MaxResourcesPerList);
    }

    // ── Session lifetime: the backend must not outlive the companion that holds the session ─────

    /// <summary>
    /// The reported contradiction: the companion popup said "Not paired" while BirkNext still said
    /// "Paired · not reporting". The backend kept a session alive long after the last heartbeat, so it went on
    /// describing a live pairing that the extension no longer held. The idle window is tied to the heartbeat.
    /// </summary>
    [Fact]
    public void SessionIdleLifetimeIsASmallMultipleOfTheHeartbeat()
    {
        BrowserCompanionLimits.SessionIdleLifetime.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5),
            "a companion heartbeats every 30 seconds; tolerating far longer leaves BirkNext claiming a pairing nothing holds");
        BrowserCompanionLimits.SessionIdleLifetime.Should().BeGreaterThan(BrowserCompanionLimits.ConnectedWindow,
            "losing one heartbeat means not reporting, not session loss");
    }

    [Fact]
    public void RegularHeartbeatsKeepTheSessionAliveIndefinitely()
    {
        var paired = Pair();

        // Ten minutes of ordinary 30-second heartbeats — well past the idle window if it were not being renewed.
        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", null, null, "0.1.0"), Extension)
                .Accepted.Should().BeTrue();
            _service.Status("dev").State.Should().Be(BrowserCompanionState.Connected);
        }
    }

    [Fact]
    public void ATransientGapIsNotReportingButIsStillAValidSession()
    {
        var paired = Pair();
        _time.Advance(BrowserCompanionLimits.ConnectedWindow + TimeSpan.FromSeconds(5));

        _service.Status("dev").State.Should().Be(BrowserCompanionState.Disconnected,
            "one missed heartbeat means the companion is not reporting, not that the pairing is gone");
        // And it recovers without re-pairing, which is what makes the tolerance worth having.
        _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", null, null, "0.1.0"), Extension)
            .Accepted.Should().BeTrue();
        _service.Status("dev").State.Should().Be(BrowserCompanionState.Connected);
    }

    [Fact]
    public void AnExpiredSessionStopsBeingReportedAsPaired()
    {
        var paired = Pair();

        _time.Advance(BrowserCompanionLimits.SessionIdleLifetime + TimeSpan.FromSeconds(1));

        var status = _service.Status("dev");
        status.State.Should().Be(BrowserCompanionState.Expired);
        status.State.Should().NotBe(BrowserCompanionState.Disconnected,
            "Disconnected is what BirkNext renders as \"Paired · not reporting\"; an expired session is not paired at all");
        // The session is gone, so its own heartbeat no longer resurrects it.
        _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", null, null, "0.1.0"), Extension)
            .Accepted.Should().BeFalse();
    }

    [Fact]
    public void ARejectedHeartbeatSaysThePairingMustBeRepeated()
    {
        var paired = Pair();
        _time.Advance(BrowserCompanionLimits.SessionIdleLifetime + TimeSpan.FromSeconds(1));
        _service.Status("dev");

        var result = _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, "dev", null, null, "0.1.0"), Extension);

        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("Pair again");
    }

    [Fact]
    public void ValidatingDoesNotByItselfKeepASessionAlive()
    {
        var paired = Pair();
        _time.Advance(BrowserCompanionLimits.SessionIdleLifetime - TimeSpan.FromSeconds(10));

        // A popup asking "is this still valid?" is not evidence that the companion is running.
        _service.ValidateSession(paired.SessionId!, "dev", Extension).Accepted.Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(11));

        _service.Status("dev").State.Should().Be(BrowserCompanionState.Expired);
    }

    [Fact]
    public void RepairingReplacesTheSessionAndTheOldOneIsRefused()
    {
        var first = Pair();
        var second = Pair();

        second.SessionId.Should().NotBe(first.SessionId);
        _service.Heartbeat(new BrowserCompanionHeartbeat(first.SessionId!, "dev", null, null, "0.1.0"), Extension)
            .Accepted.Should().BeFalse("the previous session stopped being trusted the moment pairing restarted");
        _service.Heartbeat(new BrowserCompanionHeartbeat(second.SessionId!, "dev", null, null, "0.1.0"), Extension)
            .Accepted.Should().BeTrue();
        _service.Status("dev").State.Should().Be(BrowserCompanionState.Connected);
    }

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop", true)]
    [InlineData("moz-extension://3f5c2a1e-1234-4bcd-9abc-1234567890ab", true)]
    [InlineData("http://localhost:5173", false)]
    [InlineData("https://m2lbdev.bufetat.no", false)]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/popup.html", false)]
    [InlineData("", false)]
    public void ExtensionOriginGate(string origin, bool expected) => BrowserCompanionService.IsExtensionOrigin(origin).Should().Be(expected);
}
