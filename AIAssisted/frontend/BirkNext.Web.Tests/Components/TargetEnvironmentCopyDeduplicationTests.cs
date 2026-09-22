using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// One state, one explanation, one primary action. These tests hold the page to that rule: an absence or a
/// requirement is stated once in words, and the surfaces around it either stay silent or say something materially
/// different. Model distinctions (connected vs paired-but-not-reporting, no page vs no session) are preserved.
/// </summary>
public sealed class TargetEnvironmentCopyDeduplicationTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public TargetEnvironmentCopyDeduplicationTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"{{Origin}}"}]}
            """);
    }

    private async Task<IRenderedComponent<BrowserDiscoveryTab>> OpenAsync(
        BrowserCompanionState state, string? currentPath = "/dashboard", bool withEvidence = false)
    {
        if (withEvidence)
            Discovery.GetSnapshot("dev").Pages.Add(new()
            {
                PageOrigin = Origin, PagePath = "/dashboard",
                BrowserEvidence = new() { PageOrigin = Origin, PagePath = "/dashboard", CapturedAt = DateTimeOffset.UtcNow, Dom = new() },
            });

        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus
        {
            ProfileId = "dev", State = state, ApprovedOrigins = [Origin],
            CurrentPageOrigin = currentPath is null ? null : Origin,
            CurrentPagePath = currentPath,
        });
        var runtime = new BrowserCompanionRuntime(api.Object, Discovery, Services.GetRequiredService<IJSRuntime>());
        _runtimes.Add(runtime);
        await runtime.FollowAsync(_profile);
        return Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));
    }

    // Runtimes poll in the background; the bUnit context disposes the render tree, and each runtime is stopped
    // when the test class is collected. Holding the references keeps them alive for the duration of the test.
    private readonly List<BrowserCompanionRuntime> _runtimes = [];

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string id) =>
        cut.Find($"[data-testid={id}]").TextContent.Trim();

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    // ── 6. Heading ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OverviewHeadingIsEvidenceOriented()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired);

        cut.Find(".bd-panel-title").TextContent.Trim().Should().Be("Observed browser evidence");
        cut.Markup.Should().NotContain("What browser evidence was observed");
    }

    // ── 7–9. One absence message, one action, no "not paired" chorus ────────

    [Fact]
    public async Task DisconnectedStateStatesTheAbsenceOnceAndOffersOneClearAction()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired, currentPath: null);

        var empty = cut.Find("[data-testid=browser-discovery-empty]");
        empty.TextContent.Should().Contain("No browser evidence yet");
        empty.TextContent.Should().Contain("Pair the managed Edge browser and open an approved application page");

        // Exactly one absence sentence and one Pair action in the empty state.
        Occurrences(cut.Markup, "No browser evidence").Should().Be(1);
        empty.QuerySelectorAll("button").Should().ContainSingle();
        empty.QuerySelector("button")!.TextContent.Trim().Should().Be("Pair Browser Companion");

        // The old chorus of near-synonyms is gone: the state is named once, as a badge.
        Occurrences(cut.Markup, "is not paired").Should().Be(0);
        // The state appears once, as a short label on the Live session strip that owns it. The companion surface
        // is setup now — pairing and connection as its own rows — so there is no second badge repeating it.
        Occurrences(cut.Markup, "Not connected").Should().Be(1);
        Text(cut, "bd-session").Should().Be("Not connected");
        cut.FindAll("[data-testid=browser-companion-state]").Should().BeEmpty();
    }

    [Fact]
    public async Task CompanionCardSaysWhatToDoRatherThanRestatingTheBadge()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired, currentPath: null);

        // Setup states pairing and connection as facts about setup; the session state stays with the Live strip.
        Text(cut, "browser-companion-pairing").Should().Be("Not paired");
        Text(cut, "browser-companion-connection-state").Should().Be("—");
        // The note is an instruction, not the same state in sentence form.
        var note = Text(cut, "browser-companion-connection");
        note.Should().Contain("Pair the managed Edge browser");
        note.Should().NotContain("not paired").And.NotContain("Not connected");
    }

    // ── 10–16. Summary composition ──────────────────────────────────────────

    [Fact]
    public async Task ApprovedOriginsLeavesThePrimarySummaryButStaysDiscoverableInDetails()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected);

        var summary = cut.Find("[data-testid=browser-discovery-summary]");
        summary.TextContent.Should().NotContain("Approved origins", "origins are a security fact, not a review metric");
        cut.FindAll("[data-testid=bd-origins]").Should().BeEmpty();

        // Still discoverable, in a keyboard-operable disclosure.
        var details = cut.Find("[data-testid=browser-companion-details]");
        details.TagName.Should().Be("DETAILS");
        details.QuerySelector("summary")!.TextContent.Trim().Should().Be("Details");
        Text(cut, "browser-companion-origins").Should().Contain(Origin);
    }

    [Fact]
    public async Task SummaryKeepsItsFiveMetricsWithLabelsAssociatedToValues()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected);

        var live = cut.FindAll("[data-testid=browser-discovery-live] .bd-ov-label").Select(l => l.TextContent.Trim()).ToList();
        live.Should().Equal("Target", "Browser Companion", "Live approved pages", "Current page", "Content script", "Live DOM");

        var evidence = cut.FindAll("[data-testid=browser-discovery-summary] .bd-ov-label").Select(l => l.TextContent.Trim()).ToList();
        evidence.Should().Equal("Pages with evidence", "Last evidence", "DOM evidence", "Accessibility evidence", "Performance evidence");

        foreach (var id in new[] { "bd-target", "bd-session", "bd-live-pages", "bd-current-page", "bd-pages-count", "bd-last-evidence" })
            cut.FindAll($"[data-testid={id}]").Should().ContainSingle(id);
    }

    // ── 20. State vocabulary stays distinct where the state is distinct ─────

    [Theory]
    [InlineData(BrowserCompanionState.NotPaired, "Not connected")]
    [InlineData(BrowserCompanionState.Disconnected, "Paired · not reporting")]
    [InlineData(BrowserCompanionState.Connected, "Connected")]
    public async Task SessionStatesKeepDistinctPrimaryWording(BrowserCompanionState state, string expected)
    {
        var cut = await OpenAsync(state);

        // One surface owns the session state, so the canonical wording appears once rather than on two badges.
        Text(cut, "bd-session").Should().Be(expected);
        cut.FindAll("[data-testid=browser-companion-state]").Should().BeEmpty();
        // One canonical vocabulary: the alternatives never appear as a primary state.
        cut.Markup.Should().NotContain("No session").And.NotContain("Disconnected");
    }

    [Fact]
    public async Task ConnectedWithNoCurrentPageIsNotTheSameAsNoSession()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected, currentPath: null);

        Text(cut, "bd-session").Should().Be("Connected", "the session is up");
        Text(cut, "bd-current-page").Should().Be("None", "the live page count beside it already says there is no page open");
        // Still no evidence, and the help reflects that pairing is already done.
        cut.Find("[data-testid=browser-discovery-empty]").TextContent
            .Should().Contain("Open an approved application page to start collecting browser evidence")
            .And.NotContain("Pair Browser Companion and");
    }

    // ── 11, 17. Absence is never a verdict ─────────────────────────────────

    [Fact]
    public async Task NoEvidenceIsNeverPresentedAsAPassingResult()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected, currentPath: null);

        foreach (var verdict in new[] { "0 issues", "Passed", "Compliant", "No accessibility problems", "No performance problems" })
            cut.Markup.Should().NotContain(verdict);
        Text(cut, "bd-pages-count").Should().Be("0");
        Text(cut, "bd-last-evidence").Should().Be("None");
    }

    [Fact]
    public async Task EvidenceAvailableDropsTheEmptyStateAndThePairAction()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected, withEvidence: true);

        cut.FindAll("[data-testid=browser-discovery-empty]").Should().BeEmpty();
        cut.FindAll("[data-testid=browser-discovery-overview-table]").Should().ContainSingle();
        Text(cut, "bd-pages-count").Should().Be("1");
    }
}

/// <summary>The detection area states the verification requirement once, then leads with the action.</summary>
public sealed class DetectionVerificationCopyTests : BunitContext
{
    private const string Url = "https://m2lbdev.example.test/";
    private const string LongPhrase = "Manual authentication verification required";

    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new();

    public DetectionVerificationCopyTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_api.Object);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"dev","profiles":[{"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Url}}"}]}
        """);
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(() => new TargetEnvironmentDetectionResult
        {
            OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable,
            Confidence = DetectionConfidence.VeryHigh,
            DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
            State = DetectionState.ManualAuthenticationVerificationRequired,
            ManualAuthenticationVerificationRequired = true,
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required,
            AuthenticationRequired = true,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        });
    }

    private IRenderedComponent<BirkNext.Web.Components.FrontendAnalysisSettings> Detect()
    {
        var cut = Render<BirkNext.Web.Components.FrontendAnalysisSettings>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        return cut;
    }

    // ── 1–2. One canonical statement, two compact echoes ───────────────────

    [Fact]
    public void TheRequirementIsSpelledOutOnceAndEchoedOnlyAsCompactLabels()
    {
        var cut = Detect();

        // The full sentence exists exactly once: the Detection result heading.
        var full = 0;
        for (var i = cut.Markup.IndexOf(LongPhrase, StringComparison.Ordinal); i >= 0;
             i = cut.Markup.IndexOf(LongPhrase, i + LongPhrase.Length, StringComparison.Ordinal)) full++;
        full.Should().Be(1);
        cut.Find("#detection-result-heading").TextContent.Trim().Should().Be(LongPhrase);

        // The other two surfaces are compact labels, not repeated sentences.
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Needs verification");
        var badge = cut.Find("[data-testid=manual-verification-status]");
        badge.TextContent.Trim().Should().Be("Manual verification required");
        badge.ClassList.Should().Contain("fa-verification-badge", "it is demoted to a badge, not a second prominent line");
    }

    // ── 3–5. Action, workflow and confidence survive ───────────────────────

    [Fact]
    public void VerificationLeadsWithItsActionAndKeepsTheWorkflow()
    {
        var cut = Detect();

        var card = cut.Find("section.fa-verification");
        card.QuerySelector("#manual-verification-heading")!.TextContent.Trim().Should().Be("Verification");
        cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "Open verification instructions");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Open verification instructions").Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Mark verification passed").Click();

        cut.WaitForAssertion(() => _settings.Settings.Profiles.Single(p => p.Id == "dev")
            .ManualVerification!.Result.Should().Be(ManualAuthenticationVerificationStatus.Passed));
        cut.Find("[data-testid=manual-verification-status]").TextContent.Trim().Should().Be("Manual verification passed");
    }

    [Fact]
    public void DetectionConfidenceRemainsVisibleAndReadable()
    {
        var cut = Detect();

        cut.Find("section.fa-result").TextContent.Should().Contain("Very High").And.NotContain("VeryHigh");
    }

    // ── Status is announced, never colour-only ─────────────────────────────

    [Fact]
    public void VerificationStatusIsExposedToAssistiveTechnology()
    {
        var cut = Detect();

        cut.Find("[data-testid=manual-verification-status]").GetAttribute("role").Should().Be("status");
        cut.Find("#manual-verification-heading").TagName.Should().Be("H3");
    }
}
