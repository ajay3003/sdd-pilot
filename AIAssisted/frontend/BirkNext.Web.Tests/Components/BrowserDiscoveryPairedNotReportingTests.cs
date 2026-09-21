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

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// "Paired · not reporting" is a correct, ordinary state: pairing does not reach a page that was already open,
/// because the content script starts with a page load. The user's next step is therefore a reload — and these
/// tests hold the page to saying so, rather than leaving a state that reads like an unexplained failure.
/// </summary>
public sealed class BrowserDiscoveryPairedNotReportingTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private const string NextStep = "Open or refresh an approved application page to start collecting browser evidence.";

    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private readonly List<BrowserCompanionRuntime> _runtimes = [];
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public BrowserDiscoveryPairedNotReportingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"{{Origin}}"}]}
            """);
    }

    private void SeedEvidence() => Discovery.GetSnapshot("dev").Pages.Add(new()
    {
        PageOrigin = Origin, PagePath = "/dashboard",
        BrowserEvidence = new()
        {
            PageOrigin = Origin, PagePath = "/dashboard",
            CapturedAt = new DateTimeOffset(2026, 9, 18, 13, 42, 10, TimeSpan.Zero),
            Dom = new() { NodeCount = 812 },
        },
    });

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

    /// <summary>The primary surface: the summary strip and the empty state, excluding collapsed detail.</summary>
    private static string PrimaryText(IRenderedComponent<BrowserDiscoveryTab> cut)
    {
        var clone = (IElement)cut.Find("[data-testid=browser-discovery]").Clone(true);
        foreach (var collapsed in clone.QuerySelectorAll("details:not([open]), [hidden]").ToList()) collapsed.Remove();
        return clone.TextContent;
    }

    // ── §10. 1, 2, 3, 4 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PairedNotReportingTellsTheUserExactlyWhatToDoNext()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        // 1. A distinct primary state, in its own words.
        Text(cut, "bd-session").Should().Be("Paired · not reporting");

        // 2. The explanation is the next step, verbatim.
        Text(cut, "browser-discovery-empty-help").Should().Be(NextStep);

        // 3. Already paired, so nothing asks for pairing again.
        All(cut, "browser-discovery-pair").Should().BeEmpty();
        All(cut, "browser-companion-pair").Should().BeEmpty();

        // 4. A valid intermediate state is never dressed as a failure.
        foreach (var failure in new[] { "Failed", "Error", "Pairing failed", "Disconnected" })
            PrimaryText(cut).Should().NotContain(failure);
    }

    // 5. The disconnected state keeps its own, different next step.
    [Fact]
    public async Task NotConnectedStillAsksForPairing()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired);

        Text(cut, "bd-session").Should().Be("Not connected");
        All(cut, "browser-discovery-pair").Should().ContainSingle();
        Text(cut, "browser-discovery-empty-help").Should().NotBe(NextStep);
        Text(cut, "browser-discovery-empty-help").Should().StartWith("Pair the managed Edge browser");
    }

    // 6. Once a session is reporting, the refresh guidance is gone.
    [Fact]
    public async Task ConnectedRemovesTheRefreshGuidance()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected, currentPath: "/dashboard");

        Text(cut, "bd-session").Should().Be("Connected");
        // The approved page is open and reported, so the remaining gap is the evidence, not the page.
        Text(cut, "browser-discovery-empty-help")
            .Should().Be("Connected to an approved application page. No browser evidence has been received for it yet.");
        PrimaryText(cut).Should().NotContain("Open or refresh");
    }

    // 7. And once evidence exists, no empty-state copy survives at all.
    [Fact]
    public async Task EvidenceAvailableLeavesNoStalePairedNotReportingCopy()
    {
        SeedEvidence();
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        All(cut, "browser-discovery-empty").Should().BeEmpty();
        All(cut, "browser-discovery-empty-help").Should().BeEmpty();
        PrimaryText(cut).Should().NotContain("Open or refresh");
        // The session state itself is still reported truthfully.
        Text(cut, "bd-session").Should().Be("Paired · not reporting");
    }

    // 8. One instruction on the primary surface, and none of the state's near-synonyms.
    [Fact]
    public async Task TheStateIsNotRestatedInFourDifferentWays()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        var primary = PrimaryText(cut);
        foreach (var synonym in new[] { "No approved page open", "Browser Companion not reporting", "No page reporting", "Waiting for evidence" })
            primary.Should().NotContain(synonym);

        // The next step is stated once, by the surface that owns the absence.
        All(cut, "browser-discovery-empty-help").Should().ContainSingle();
    }

    // ── §6. Summary strip ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSummaryStripKeepsTheStateAndTheCanonicalEmptyValues()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        Text(cut, "bd-session").Should().Be("Paired · not reporting");
        Text(cut, "bd-current-page").Should().Be("—", "the canonical empty value this strip already uses");
        Text(cut, "bd-pages-count").Should().Be("0");
        Text(cut, "bd-last-evidence").Should().Be("None");
        // The full next-action sentence belongs to the empty state, not the strip.
        cut.Find("[data-testid=browser-discovery-summary]").TextContent.Should().NotContain("Open or refresh");
    }

    // ── §4, §9. One mapping, one next action per state ───────────────────────────────────────

    [Fact]
    public void EachStateMapsToExactlyOneNextAction()
    {
        BrowserDiscoveryStates.NextAction(BrowserDiscoveryState.NotConnected).Should().Be(BrowserDiscoveryNextAction.Pair);
        BrowserDiscoveryStates.NextAction(BrowserDiscoveryState.Pairing).Should().Be(BrowserDiscoveryNextAction.EnterPairingCode);
        BrowserDiscoveryStates.NextAction(BrowserDiscoveryState.PairedNotReporting).Should().Be(BrowserDiscoveryNextAction.OpenOrRefreshApprovedPage);
        BrowserDiscoveryStates.NextAction(BrowserDiscoveryState.ConnectedWithoutEvidence).Should().Be(BrowserDiscoveryNextAction.OpenApprovedPage);
        BrowserDiscoveryStates.NextAction(BrowserDiscoveryState.EvidenceAvailable).Should().Be(BrowserDiscoveryNextAction.None);

        // Paired · not reporting is never collapsed into Not connected, in either direction.
        BrowserDiscoveryStates.Of(BrowserCompanionState.Disconnected, hasEvidence: false)
            .Should().Be(BrowserDiscoveryState.PairedNotReporting);
        BrowserDiscoveryStates.SessionLabel(BrowserCompanionState.Disconnected).Should().NotBe("Not connected");
        BrowserDiscoveryStates.Explanation(BrowserDiscoveryState.PairedNotReporting).Should().Be(NextStep);
    }

    // ── §11. Accessibility ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheExplanationIsAssociatedWithTheAbsenceItExplains()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        var empty = cut.Find("[data-testid=browser-discovery-empty]");
        cut.Find($"#{empty.GetAttribute("aria-labelledby")}").TextContent.Should().Be("No browser evidence yet");
        cut.Find($"#{empty.GetAttribute("aria-describedby")}").TextContent.Should().Be(NextStep);

        // The state is readable text, never colour alone.
        Text(cut, "bd-session").Should().Be("Paired · not reporting");
    }
}
