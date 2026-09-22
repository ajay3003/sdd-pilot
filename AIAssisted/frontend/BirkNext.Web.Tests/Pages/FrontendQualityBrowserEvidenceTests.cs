using AngleSharp.Dom;
using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Frontend Quality Review CONSUMES browser evidence. Browser Discovery owns it.
///
/// The pre-run page used to embed the whole Browser Companion card: Unpair, Pair again, "Install the extension (DEV)",
/// the approved-origin list, and a second sentence restating the connection the badge above it had already stated.
/// That is Browser Discovery's surface, and having two pages that can both pair and unpair one session is how the two
/// end up disagreeing about it.
///
/// The other thing these tests hold in place is the live/historical split. "Connected" is a fact about a browser right
/// now and says nothing about how fresh the stored evidence is; "5 pages captured" is a fact about the past and says
/// nothing about whether a browser is open. Neither may be derived from the other.
/// </summary>
public sealed class FrontendQualityBrowserEvidenceTests : BunitContext
{
    private const string Origin = "https://m2lbdev.example.test";
    private static readonly DateTimeOffset Captured = new(2026, 9, 22, 8, 6, 10, TimeSpan.Zero);

    private readonly FrontendAnalysisProfile _profile = new()
    {
        Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin,
        Features = { EnableBrowserQualityEngine = true, EnablePerformanceQualityEngine = true },
    };

    /// <summary>Runtimes poll in the background; holding this keeps it alive for the test's duration.</summary>
    private readonly BrowserCompanionRuntime _runtime;
    private readonly EndpointDiscoveryService _discovery;

    /// <summary>
    /// The session the fake companion API reports, set by each test before the page renders. bUnit freezes its service
    /// provider the first time anything resolves from it, so every registration happens here, once, and the per-test
    /// variation goes through these fields instead.
    /// </summary>
    private BrowserCompanionState _session = BrowserCompanionState.NotPaired;
    private string? _currentPath;

    private IEndpointDiscoveryService Discovery => _discovery;

    public FrontendQualityBrowserEvidenceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(() => new BrowserCompanionStatus
        {
            ProfileId = "dev", State = _session, ApprovedOrigins = [Origin],
            CurrentPageOrigin = _currentPath is null ? null : Origin, CurrentPagePath = _currentPath,
        });
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(Context);
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto());

        // Constructed here rather than by the container: the runtime is IAsyncDisposable only, and a container that
        // created it would try to dispose it synchronously when the test ends. A service the container did not create
        // is one it does not dispose.
        _discovery = new EndpointDiscoveryService();
        _runtime = new BrowserCompanionRuntime(api.Object, _discovery, JSInterop.JSRuntime);

        Services.AddSingleton<IEndpointDiscoveryService>(_discovery);
        Services.AddSingleton(api.Object);
        Services.AddSingleton(_runtime);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(status.Object);
        Services.AddSingleton(Mock.Of<IFrontendQualityReviewOrchestrator>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
    }

    /// <summary>Only the two Browser Companion engines matter here; the rest stay off so the page is minimal.</summary>
    private FrontendAnalysisContext Context()
    {
        _profile.Features.EnableBrowserRuntimeEngine = false;
        _profile.Features.EnableAccessibilityEngine = false;
        _profile.Features.EnableLighthouseEngine = false;
        _profile.Features.EnablePassiveSecurityEngine = false;
        return new FrontendAnalysisContext
        {
            ActiveProfile = _profile, TargetUrl = Origin, FeatureToggles = _profile.Features,
            EngineRequirements = _profile.EngineRequirements, ReviewEngineSelection = _profile.ReviewEngineSelection,
        };
    }

    private void SeedCapturedPages(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var path = $"/admin/page-{i}";
            Discovery.GetSnapshot("dev").Pages.Add(new()
            {
                PageOrigin = Origin, PagePath = path,
                BrowserEvidence = new()
                {
                    PageOrigin = Origin, PagePath = path, CapturedAt = Captured.AddMinutes(-i),
                    Dom = new() { NodeCount = 812 },
                    Performance = new() { ObservationType = "initial-load", LcpMs = 1234 },
                    Accessibility = new() { Checks = [new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 }] },
                },
            });
        }
    }

    private async Task<IRenderedComponent<FrontendQualityReview>> PageAsync(BrowserCompanionState state, string? currentPath = null)
    {
        _session = state;
        _currentPath = currentPath;
        await _runtime.FollowAsync(_profile);
        return Render<FrontendQualityReview>();
    }

    private static IElement Evidence(IRenderedComponent<FrontendQualityReview> page) => page.Find("[data-testid=fqr-browser-evidence]");
    private static string Value(IRenderedComponent<FrontendQualityReview> page, string id) => page.Find($"[data-testid={id}]").TextContent.Trim();
    private static string Hint(IRenderedComponent<FrontendQualityReview> page) =>
        page.Find("[data-testid=fqr-companion-toggle] .disclosure-hint").TextContent.Trim();

    // ── §41. Ownership ──────────────────────────────────────────────────────────────────────────────────────────────

    // 47, 48, 49, 50. The review reads the evidence; it never administers the session that produces it.
    [Fact]
    public async Task ThePreRunPageOffersNoBrowserCompanionSetupOrPairingControl()
    {
        SeedCapturedPages(5);
        var page = await PageAsync(BrowserCompanionState.Connected, "/admin/user-access");

        var markup = page.Markup;
        markup.Should().NotContain("Unpair");
        markup.Should().NotContain("Pair again");
        markup.Should().NotContain("Install the BirkNext Browser Companion extension");
        markup.Should().NotContain("Developer extension setup");
        markup.Should().NotContain("edge://extensions");
        page.FindAll("[data-testid=browser-companion-panel]").Should().BeEmpty("the whole card belongs to Browser Discovery");
        page.FindAll("[data-testid=browser-companion-unpair]").Should().BeEmpty();
        page.FindAll("[data-testid=browser-companion-repair]").Should().BeEmpty();
        page.FindAll("[data-testid=browser-companion-install]").Should().BeEmpty();

        // 50. What replaces them: a way to reach the page that does own all of it.
        var open = page.Find("[data-testid=fqr-evidence-open-discovery]");
        open.TextContent.Trim().Should().Be("Open Browser Discovery");
        open.GetAttribute("href").Should().Be(FrontendQualityBrowserEvidencePresentation.BrowserDiscoveryHref);
        open.GetAttribute("href").Should().Contain("tab=browser");
    }

    // 29. Approved origins are Browser Discovery / Target Environment context, not a pre-run decision input.
    [Fact]
    public async Task ApprovedOriginsAreTechnicalContextOnly()
    {
        SeedCapturedPages(2);
        var page = await PageAsync(BrowserCompanionState.Connected, "/admin/user-access");

        Evidence(page).TextContent.Should().NotContain("Approved origins");
        page.Find("[data-testid=fqr-technical-evidence]").TextContent.Should().Contain("Approved origins").And.Contain(Origin);
    }

    // 51. One statement of the connection, not a badge and two sentences repeating it.
    [Fact]
    public async Task TheConnectionIsStatedOnce()
    {
        SeedCapturedPages(3);
        var page = await PageAsync(BrowserCompanionState.Connected, "/admin/user-access");

        var text = Evidence(page).TextContent;
        Occurrences(text, "Connected").Should().Be(1);
        text.Should().NotContain("Browser Companion is connected.");
        text.Should().NotContain("Browser Companion connected.");
    }

    // ── §42. Live versus historical ─────────────────────────────────────────────────────────────────────────────────

    // 39, 40, 41, 42. Case A: connected, on a page, with history.
    [Fact]
    public async Task ConnectedWithALivePageAndHistoryReportsBothWithoutMergingThem()
    {
        SeedCapturedPages(5);
        var page = await PageAsync(BrowserCompanionState.Connected, "/admin/user-access");

        Value(page, "fqr-evidence-session").Should().Contain("Connected");
        // 27. The route identifies the page to a reviewer; the full URL is technical detail.
        Value(page, "fqr-evidence-current-page").Should().Be("/admin/user-access");
        Value(page, "fqr-evidence-pages").Should().Be("5");
        Hint(page).Should().Be("Connected · 5 pages captured");
    }

    // Case B: disconnected, with history. The history must NOT disappear — it is what the next run will read.
    [Fact]
    public async Task DisconnectedStillReportsEveryCapturedPage()
    {
        SeedCapturedPages(5);
        var page = await PageAsync(BrowserCompanionState.Disconnected);

        Value(page, "fqr-evidence-session").Should().NotContain("Connected");
        Value(page, "fqr-evidence-current-page").Should().Be("None");
        Value(page, "fqr-evidence-pages").Should().Be("5");
        Value(page, "fqr-evidence-dom").Should().Be("5 pages captured");
        Hint(page).Should().Contain("5 pages captured");
    }

    // Case C: connected with no live page. "Connected" must not imply a page is open, and history must not supply one.
    [Fact]
    public async Task ConnectedWithoutALivePageNeverBorrowsARouteFromStoredEvidence()
    {
        SeedCapturedPages(5);
        var page = await PageAsync(BrowserCompanionState.Connected);

        Value(page, "fqr-evidence-session").Should().Contain("Connected");
        Value(page, "fqr-evidence-current-page").Should().Be("None");
        Value(page, "fqr-evidence-current-page").Should().NotContain("/admin/page-");
        Value(page, "fqr-evidence-pages").Should().Be("5", "the history is unaffected by there being no page open");
    }

    // 25, 30, 43. Live capability is "available now"; history is "captured", and the timestamp says which it is.
    [Fact]
    public async Task LiveCapabilityAndCapturedEvidenceUseDifferentWords()
    {
        SeedCapturedPages(5);
        var page = await PageAsync(BrowserCompanionState.Connected, "/admin/user-access");

        var live = page.Find("[data-testid=fqr-evidence-live]").TextContent;
        var history = page.Find("[data-testid=fqr-evidence-history]").TextContent;

        live.Should().Contain("Live browser");
        history.Should().Contain("Captured evidence");
        // 43. "Available" alone reads as "available now", which is the live session's word.
        Value(page, "fqr-evidence-dom").Should().Be("5 pages captured");
        Value(page, "fqr-evidence-accessibility").Should().Be("5 pages captured");
        Value(page, "fqr-evidence-performance").Should().Be("5 pages captured");
        // 30. A historical timestamp, labelled as one — never as a live heartbeat.
        history.Should().Contain("Last captured evidence");
        Value(page, "fqr-evidence-last").Should().Be(Captured.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        history.Should().NotContainAny("heartbeat", "Last seen", "now");
    }

    // 26, 43, 44. "DOM Quality" was an assessment word on a page that has assessed nothing yet.
    [Fact]
    public async Task EvidenceIsNamedAsEvidence_NeverAsQualityOrAnOutcome()
    {
        SeedCapturedPages(5);
        var page = await PageAsync(BrowserCompanionState.Connected, "/admin/user-access");

        var text = Evidence(page).TextContent;
        text.Should().Contain("DOM evidence").And.Contain("Accessibility evidence").And.Contain("Performance evidence");
        text.Should().NotContain("DOM quality").And.NotContain("DOM Quality");
        text.Should().NotContainAny("Passed", "Failed", "conformant", "violation");
        // 24. The ownership boundary is stated, so "this review reads it" is not left to be inferred.
        text.Should().Contain(FrontendQualityBrowserEvidencePresentation.OwnershipNote);
    }

    // No Browser Companion engine enabled: nothing to consume, so the section is absent rather than empty.
    [Fact]
    public async Task NoBrowserEngineEnabledMeansNoBrowserEvidenceSection()
    {
        _profile.Features.EnableBrowserQualityEngine = false;
        _profile.Features.EnablePerformanceQualityEngine = false;
        var page = await PageAsync(BrowserCompanionState.Connected, "/admin/user-access");

        page.FindAll("[data-testid=fqr-browser-evidence]").Should().BeEmpty();
        page.FindAll("[data-testid=fqr-companion-toggle]").Should().BeEmpty();
    }

    // ── §48. Accessible without colour ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryEvidenceFactIsReadableAsText()
    {
        SeedCapturedPages(5);
        var page = await PageAsync(BrowserCompanionState.Connected, "/admin/user-access");

        var toggle = page.Find("[data-testid=fqr-companion-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TagName.Should().Be("BUTTON", "a disclosure has to be reachable and operable from the keyboard");

        foreach (var group in page.FindAll("[data-testid=fqr-browser-evidence] section"))
            group.QuerySelector("h3").Should().NotBeNull("each group is announced by a heading");
        foreach (var pill in page.FindAll("[data-testid=fqr-browser-evidence] .fqr-pill"))
            pill.TextContent.Trim().Should().NotBeEmpty("state is never carried by colour alone");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
