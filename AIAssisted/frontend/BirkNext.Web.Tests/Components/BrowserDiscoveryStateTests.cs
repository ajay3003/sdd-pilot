using AngleSharp.Dom;
using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Settings = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Browser Discovery states one situation once, explains it once, and offers one action.
///
/// Four situations are genuinely different and stay apart: nothing is paired; pairing is under way; a session
/// exists but nothing is reporting; and a session is connected with nothing observed yet. None of them is a
/// verdict — an absence of browser evidence says nothing about quality, which Frontend Quality Review decides.
/// </summary>
public sealed class BrowserDiscoveryStateTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public BrowserDiscoveryStateTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"{{Origin}}"}]}
            """);
    }

    private static readonly DateTimeOffset Observed = new(2026, 9, 18, 13, 42, 10, TimeSpan.Zero);

    private void SeedEvidence(string path = "/dashboard") => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = path,
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = path, CapturedAt = Observed,
            Dom = new() { NodeCount = 812 },
            Performance = new() { ObservationType = "initial-load", LcpMs = 1234 },
        },
    });

    /// <summary>Runtimes poll in the background; holding them keeps them alive for the test's duration.</summary>
    private readonly List<BrowserCompanionRuntime> _runtimes = [];

    private async Task<IRenderedComponent<BrowserDiscoveryTab>> OpenAsync(BrowserCompanionState state, string? currentPath = null)
    {
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus
        {
            ProfileId = "dev", State = state, ApprovedOrigins = [Origin],
            CurrentPageOrigin = currentPath is null ? null : Origin, CurrentPagePath = currentPath,
        });
        var runtime = new BrowserCompanionRuntime(api.Object, Discovery, Services.GetRequiredService<IJSRuntime>());
        _runtimes.Add(runtime);
        await runtime.FollowAsync(_profile);
        return Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));
    }

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string id) =>
        cut.Find($"[data-testid={id}]").TextContent.Trim();

    private static IReadOnlyList<IElement> All(IRenderedComponent<BrowserDiscoveryTab> cut, string id) =>
        cut.FindAll($"[data-testid={id}]");

    /// <summary>
    /// Every control that asks the user to PAIR, whichever surface owns it. "Pair again" is excluded on purpose:
    /// it re-pairs an existing session and has no counterpart elsewhere, so it is not part of this duplication.
    /// </summary>
    private static IReadOnlyList<IElement> PairActions(IRenderedComponent<BrowserDiscoveryTab> cut) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim()
            .StartsWith("Pair Browser Companion", StringComparison.OrdinalIgnoreCase)
            || b.TextContent.Trim().Equals("Pair browser companion", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Text the reader can actually see: a collapsed details block is not part of the primary surface.</summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var collapsed in clone.QuerySelectorAll("details:not([open]), [hidden]").ToList()) collapsed.Remove();
        return clone.TextContent;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    // ── §31. Disconnected ────────────────────────────────────────────────────────────────────

    // 1, 2, 3, 4, 5, 6, 7.
    [Fact]
    public async Task DisconnectedShowsOneStateOneExplanationAndOnePairAction()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired);

        Text(cut, "bd-session").Should().Be("Not connected");
        Text(cut, "browser-discovery-empty").Should().Contain("No browser evidence yet");
        Text(cut, "browser-discovery-empty-help")
            .Should().Be("Pair the managed Edge browser and open an approved application page to start collecting browser evidence.");

        // 3. Exactly one Pair control on the whole page, and it belongs to the empty state.
        PairActions(cut).Should().ContainSingle();
        All(cut, "browser-discovery-pair").Should().ContainSingle();
        All(cut, "browser-companion-pair").Should().BeEmpty();

        // 4. The pairing sentence is not repeated beside a badge that already says Not connected.
        Occurrences(cut.Markup, "is not paired").Should().Be(0);

        // 5, 6, 7. Connection details exist and own the origins; the summary strip does not.
        All(cut, "browser-companion-details").Should().ContainSingle();
        All(cut, "bd-origins").Should().BeEmpty();
        Text(cut, "browser-companion-origins").Should().Contain(Origin);
    }

    // ── §32. Connected with no evidence ──────────────────────────────────────────────────────

    // 8, 9, 10, 11, 12.
    [Fact]
    public async Task ConnectedWithoutEvidenceNeverAsksTheUserToPairAgain()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected, currentPath: "/dashboard");

        Text(cut, "bd-session").Should().Be("Connected");
        Text(cut, "browser-discovery-empty").Should().Contain("No browser evidence yet");
        Text(cut, "browser-discovery-empty-help").Should().Be("Open an approved application page to start collecting browser evidence.");

        // 10. Pairing is done, so nothing asks for it.
        PairActions(cut).Should().BeEmpty();

        // 12. An absence of evidence is never a verdict.
        foreach (var verdict in new[] { "Passed", "No accessibility issues", "No performance issues", "Compliant", "No issues" })
            cut.Markup.Should().NotContain(verdict);
    }

    // ── §33. Paired but not reporting ────────────────────────────────────────────────────────

    // 13, 14, 15, 16.
    [Fact]
    public async Task PairedNotReportingStaysDistinctFromNotConnected()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        Text(cut, "bd-session").Should().Be("Paired · not reporting");
        Text(cut, "bd-session").Should().NotBe("Not connected");
        Text(cut, "browser-discovery-empty-help").Should().Contain("Open or refresh an approved application page");

        // 15. Already paired, so no Pair control — but the session actions that act on it remain.
        All(cut, "browser-discovery-pair").Should().BeEmpty();
        All(cut, "browser-companion-pair").Should().BeEmpty();
        All(cut, "browser-companion-repair").Should().ContainSingle("re-pairing an existing session has no counterpart elsewhere");
    }

    // ── §34. Evidence available ──────────────────────────────────────────────────────────────

    // 17, 18, 19, 20.
    [Fact]
    public async Task EvidenceReplacesTheEmptyStateAndTheDiagnosticsStaySecondary()
    {
        SeedEvidence();
        var cut = await OpenAsync(BrowserCompanionState.Connected, currentPath: "/dashboard");

        All(cut, "browser-discovery-empty").Should().BeEmpty();
        All(cut, "browser-discovery-overview-table").Should().ContainSingle();
        Text(cut, "bd-pages-count").Should().Be("1");
        Text(cut, "bd-session").Should().Be("Connected");

        // 20. The companion card is present but the evidence table precedes it.
        cut.Markup.IndexOf("browser-discovery-overview-table", StringComparison.Ordinal)
            .Should().BeLessThan(cut.Markup.IndexOf("browser-companion-panel", StringComparison.Ordinal));
        // Approved origins and pairing internals remain behind the details disclosure.
        cut.Find("[data-testid=browser-companion-origins]").Closest("[data-testid=browser-companion-details]").Should().NotBeNull();
    }

    // ── §35. Copy deduplication ──────────────────────────────────────────────────────────────

    // 21, 22, 23, 24, 25.
    [Fact]
    public async Task OneDisconnectedStateIsNotSaidInFourWays()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired);

        // 23. "Not connected" is the primary wording, as a short label on the two surfaces that own a state.
        Occurrences(cut.Markup, "Not connected").Should().Be(2);
        Text(cut, "bd-session").Should().Be("Not connected");
        Text(cut, "browser-companion-state").Should().Be("Not connected");

        // 21, 24. Its near-synonyms are not on the primary surface. The developer install aside quotes the
        // extension.s own "Not paired" wording inside a collapsed details block; that is the extension speaking,
        // not this page restating the state, so the visible surface is what is scanned.
        var visible = VisibleText(cut.Find("[data-testid=browser-discovery]"));
        foreach (var synonym in new[] { "Not paired", "No session", "Disconnected", "not currently connected" })
            visible.Should().NotContain(synonym);

        // 25. One absence sentence, and the companion card says what to do rather than restating the badge.
        Occurrences(cut.Markup, "No browser evidence").Should().Be(1);
        Text(cut, "browser-companion-connection").Should().Contain("Pair the managed Edge browser to collect evidence");
    }

    // ── §37. Semantic separations ────────────────────────────────────────────────────────────

    // 29, 30, 31, 32, 33.
    [Fact]
    public void TheFourSituationsAreDerivedOnceAndNeverCollapsed()
    {
        // 29. Connected is a session state; it is not evidence.
        BrowserDiscoveryStates.Of(BrowserCompanionState.Connected, hasEvidence: false)
            .Should().Be(BrowserDiscoveryState.ConnectedWithoutEvidence);
        BrowserDiscoveryStates.Of(BrowserCompanionState.Connected, hasEvidence: true)
            .Should().Be(BrowserDiscoveryState.EvidenceAvailable);

        // 30. Paired is not reporting.
        BrowserDiscoveryStates.Of(BrowserCompanionState.Disconnected, hasEvidence: false)
            .Should().Be(BrowserDiscoveryState.PairedNotReporting);
        BrowserDiscoveryStates.SessionLabel(BrowserCompanionState.Disconnected).Should().Be("Paired · not reporting");

        // 31. No session is not a verdict about the application.
        BrowserDiscoveryStates.Of(BrowserCompanionState.NotPaired, hasEvidence: false)
            .Should().Be(BrowserDiscoveryState.NotConnected);

        // Evidence outranks every session state: a stale session with stored evidence still shows the evidence.
        BrowserDiscoveryStates.Of(BrowserCompanionState.NotPaired, hasEvidence: true)
            .Should().Be(BrowserDiscoveryState.EvidenceAvailable);

        // The Pair action belongs to exactly one situation.
        BrowserDiscoveryStates.ShowsPairAction(BrowserDiscoveryState.NotConnected).Should().BeTrue();
        foreach (var other in new[] { BrowserDiscoveryState.Pairing, BrowserDiscoveryState.PairedNotReporting,
                                      BrowserDiscoveryState.ConnectedWithoutEvidence, BrowserDiscoveryState.EvidenceAvailable })
            BrowserDiscoveryStates.ShowsPairAction(other).Should().BeFalse();
    }

    // 33. Approved origins is a security fact, never a count of observed pages.
    [Fact]
    public async Task ApprovedOriginCountIsNotAnEvidenceCount()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected, currentPath: "/dashboard");

        Text(cut, "bd-pages-count").Should().Be("0");
        Text(cut, "browser-companion-origins").Should().Contain(Origin, "one approved origin, and still no evidence");
    }

    // ── §38. The tabs are unchanged ──────────────────────────────────────────────────────────

    // 34, 35, 36, 37.
    [Fact]
    public async Task OverviewPagesAndEvidenceRemainEvenAtZero()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired);

        cut.FindAll(".bd-nav [role=tab]").Select(t => t.TextContent.Trim())
            .Should().Equal("Overview", "Pages (0)", "Evidence");
        foreach (var absent in new[] { "wcag", "performance" })
            All(cut, $"browser-discovery-nav-{absent}").Should().BeEmpty(absent);
    }

    // ── §36. Reset Profile is a profile action ───────────────────────────────────────────────

    // 26, 27, 28.
    [Fact]
    public async Task ResettingTheProfileIsNotABrowserDiscoveryAction()
    {
        (await OpenAsync(BrowserCompanionState.NotPaired)).Markup.Should().NotContain("Reset Profile");

        // It lives at profile level, outside every tab panel, and stays reachable there.
        var settings = Render<Settings>();
        settings.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Dev")).Click();
        settings.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Browser Discovery").Click();

        var reset = settings.FindAll("button").Single(b => b.TextContent.Trim() == "Reset Profile");
        reset.Closest("[data-testid=browser-discovery]").Should().BeNull();
        reset.Closest(".fa-profile-reset-zone").Should().NotBeNull();
    }

    // ── §30. Accessibility of the consolidated state ─────────────────────────────────────────

    [Fact]
    public async Task StatesAreTextAndTheDisclosureIsSemantic()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired);

        Text(cut, "bd-session").Should().NotBeNullOrWhiteSpace();
        Text(cut, "browser-companion-state").Should().NotBeNullOrWhiteSpace();

        var details = cut.Find("[data-testid=browser-companion-details]");
        details.TagName.Should().Be("DETAILS");
        details.QuerySelector("summary").Should().NotBeNull();

        // No two simultaneously rendered controls share an accessible name.
        var names = cut.FindAll("button").Select(b => b.TextContent.Trim()).Where(n => n.Length > 0).ToList();
        names.Should().OnlyHaveUniqueItems();

        cut.FindAll(".bd-nav [role=tab]").Should().OnlyContain(t => t.HasAttribute("aria-selected") && t.HasAttribute("aria-controls"));
    }
}
