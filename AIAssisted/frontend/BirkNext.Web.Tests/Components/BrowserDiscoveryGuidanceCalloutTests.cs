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
/// "Paired · not reporting" is expected and recoverable, and its next step — reload the page in the paired
/// browser — was a sentence buried in body text. It is now a callout: a short title, one sentence and a
/// decorative icon, above the technical details.
///
/// Info, never warning. A state the reader can fix in one action is not a failure, so the callout borrows none
/// of the warning palette and is not announced as an alert. And it belongs to exactly one state: nothing tells a
/// reporting session to resume, and nothing tells an unpaired one to reload.
/// </summary>
public sealed class BrowserDiscoveryGuidanceCalloutTests : BunitContext
{
    private const string Origin = "https://application.example.test";
    private const string Title = "Resume browser reporting";
    private const string Guidance = "Open or refresh an approved application page in your managed Edge browser.";

    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };
    private readonly List<BrowserCompanionRuntime> _runtimes = [];
    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public BrowserDiscoveryGuidanceCalloutTests()
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

    /// <summary>The primary surface: everything the reader sees without opening a collapsed block.</summary>
    private static string PrimaryText(IRenderedComponent<BrowserDiscoveryTab> cut)
    {
        var clone = (IElement)cut.Find("[data-testid=browser-discovery]").Clone(true);
        foreach (var collapsed in clone.QuerySelectorAll("details:not([open]), [hidden]").ToList()) collapsed.Remove();
        return clone.TextContent;
    }

    // ── §15. 1, 2, 3 ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PairedNotReportingRendersTheGuidanceCallout()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        All(cut, "browser-companion-guidance").Should().ContainSingle();
        Text(cut, "browser-companion-guidance-title").Should().Be(Title);
        Text(cut, "browser-companion-guidance-text").Should().Be(Guidance);
    }

    // 4, 5. The old sentence is gone, and the callout precedes the technical details.
    [Fact]
    public async Task TheCalloutReplacesTheOldSentenceAndComesBeforeDetails()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        cut.Markup.Should().NotContain("Open the application in your managed Edge to resume reporting");
        All(cut, "browser-companion-connection").Should().BeEmpty("the callout carries the next step for this state");

        cut.Markup.IndexOf("browser-companion-guidance", StringComparison.Ordinal)
            .Should().BeLessThan(cut.Markup.IndexOf("browser-companion-details", StringComparison.Ordinal));
    }

    // 6, 7. Informational, not a failure, and the icon adds nothing a reader must read.
    [Fact]
    public async Task TheCalloutIsInformationalAndItsIconIsDecorative()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        var callout = cut.Find("[data-testid=browser-companion-guidance]");
        callout.GetAttribute("data-kind").Should().Be("Info");
        callout.ClassList.Should().Contain("callout-info");
        foreach (var tone in new[] { "warn", "warning", "error", "danger" })
            callout.ClassList.Should().NotContain(c => c.Contains(tone, StringComparison.OrdinalIgnoreCase));
        // Not announced as an alert: this is a state the reader fixes, not one that went wrong.
        callout.HasAttribute("role").Should().BeFalse();

        var icon = callout.QuerySelector(".callout-icon")!;
        icon.GetAttribute("aria-hidden").Should().Be("true");
        icon.TextContent.Trim().Should().NotBeEmpty();
        // The message survives without it.
        Text(cut, "browser-companion-guidance-title").Should().Be(Title);
    }

    // ── §15. 8, 9, 10, 11 — the callout belongs to exactly one state ─────────────────────────

    [Fact]
    public async Task NotConnectedKeepsItsPairActionAndShowsNoResumeGuidance()
    {
        var cut = await OpenAsync(BrowserCompanionState.NotPaired);

        All(cut, "browser-companion-guidance").Should().BeEmpty();
        PrimaryText(cut).Should().NotContain(Title);
        All(cut, "browser-discovery-pair").Should().ContainSingle("pairing, not reloading, is the next step here");
    }

    [Fact]
    public async Task AReportingSessionIsNeverToldToResume()
    {
        var cut = await OpenAsync(BrowserCompanionState.Connected, currentPath: "/dashboard");

        All(cut, "browser-companion-guidance").Should().BeEmpty();
        PrimaryText(cut).Should().NotContain(Title).And.NotContain("Open or refresh");
    }

    // 11. Evidence outranks the session state: a stale session with evidence shows the evidence, not the callout.
    [Fact]
    public async Task EvidenceAvailableLeavesNoStaleCallout()
    {
        SeedEvidence();
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        All(cut, "browser-companion-guidance").Should().BeEmpty();
        All(cut, "browser-discovery-overview-table").Should().ContainSingle();
        PrimaryText(cut).Should().NotContain(Title);
        // The session state is still reported truthfully alongside the evidence.
        Text(cut, "bd-session").Should().Be("Paired · not reporting");
    }

    // ── §16. One primary instruction ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyOneInstructionIsVisibleForThisState()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);
        var primary = PrimaryText(cut);

        // The superseded phrasings are gone from the visible surface.
        foreach (var old in new[]
                 {
                     "Open the application in your managed Edge to resume reporting",
                     "Browser Companion paired but not reporting",
                     "Browser Companion is paired but is not currently reporting",
                 })
            primary.Should().NotContain(old);

        // The empty state explains the absence; the callout gives the action. Two surfaces, one instruction each.
        All(cut, "browser-companion-guidance-text").Should().ContainSingle();
        All(cut, "browser-discovery-empty-help").Should().ContainSingle();
    }

    // §6. The technical state stays available in Details, reduced to a statement of fact.
    [Fact]
    public async Task DetailsKeepTheTechnicalStateWithoutRepeatingTheAction()
    {
        var cut = await OpenAsync(BrowserCompanionState.Disconnected);

        All(cut, "browser-companion-details").Should().ContainSingle();
        BrowserDiscoveryStates.SessionLabel(BrowserCompanionState.Disconnected).Should().Be("Paired · not reporting");
        // The card's own connection sentence, where it still renders, states the situation and not the fix.
        var connected = await OpenAsync(BrowserCompanionState.Connected, currentPath: "/dashboard");
        Text(connected, "browser-companion-connection").Should().NotContain("Open or refresh");
    }

    // ── §14. One source for the copy ─────────────────────────────────────────────────────────

    [Fact]
    public void TheGuidanceCopyLivesInThePresenterAndBelongsToOneState()
    {
        BrowserDiscoveryStates.GuidanceTitle.Should().Be(Title);
        BrowserDiscoveryStates.GuidanceText.Should().Be(Guidance);

        BrowserDiscoveryStates.ShowsGuidance(BrowserDiscoveryState.PairedNotReporting).Should().BeTrue();
        foreach (var other in new[] { BrowserDiscoveryState.NotConnected, BrowserDiscoveryState.Pairing,
                                      BrowserDiscoveryState.ConnectedWithoutEvidence, BrowserDiscoveryState.EvidenceAvailable })
            BrowserDiscoveryStates.ShowsGuidance(other).Should().BeFalse();

        // Guidance and the Pair action never appear for the same state.
        BrowserDiscoveryStates.ShowsPairAction(BrowserDiscoveryState.PairedNotReporting).Should().BeFalse();
    }
}
