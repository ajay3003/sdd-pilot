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
/// The transition this whole pipeline exists for: connected on an approved page with nothing observed, then the first
/// evidence arrives. It must land in Browser Discovery through the companion poll alone — reloading BirkNext is not a
/// step in reporting — and a known current page must never have been presented as evidence on the way there.
/// </summary>
public sealed class BrowserDiscoveryEvidenceArrivalTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string Path = "/admin/operations";

    /// <summary>Production environment ids are <c>Guid.NewGuid().ToString("N")</c>: 32 characters, no separator.</summary>
    private readonly string _profileId = Guid.NewGuid().ToString("N");
    private readonly FrontendAnalysisProfile _profile;
    private static readonly DateTimeOffset Visit = new(2026, 9, 21, 9, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Captured = Visit.AddSeconds(4);

    private IEndpointDiscoveryService Discovery => Services.GetRequiredService<IEndpointDiscoveryService>();

    public BrowserDiscoveryEvidenceArrivalTests()
    {
        _profile = new FrontendAnalysisProfile { Id = _profileId, Name = "M2LB DEV", TargetUrl = Origin };
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
    }

    private BrowserCompanionStatus Connected(params BrowserPageEvidence[] pages) => new()
    {
        // The status is only merged when it is the status of the environment being viewed.
        ProfileId = _profileId,
        State = BrowserCompanionState.Connected,
        ApprovedOrigins = [Origin],
        CurrentPageOrigin = Origin,
        CurrentPagePath = Path,
        PagesWithEvidence = pages.Length,
        Pages = [.. pages],
    };

    /// <summary>An assessment that ran and found nothing: evidence exists, no finding does, and no verdict is implied.</summary>
    private BrowserPageEvidence EvidenceWithNoFindings() => new()
    {
        ProfileId = _profileId, PageOrigin = Origin, PagePath = Path,
        VisitStartedAt = Visit, CapturedAt = Captured, SnapshotKind = "initial", SnapshotSequence = 1,
        DocumentTitle = "Operations",
        Dom = new BrowserDomSummary { NodeCount = 1430, InteractiveCount = 52 },
        Accessibility = new BrowserAccessibilitySummary { RulesEvaluated = 24, Findings = [], Checks = [] },
        Performance = new BrowserPerformanceSummary { ObservationType = "initial-load", LcpMs = 2100 },
        Runtime = new BrowserRuntimeSummary(),
    };

    private static string Text(IRenderedComponent<BrowserDiscoveryTab> cut, string id) =>
        cut.Find($"[data-testid={id}]").TextContent.Trim();

    [Fact]
    public async Task FirstEvidenceAppearsThroughTheCompanionPollWithoutReloadingBirkNext()
    {
        var status = Connected();
        var api = new Mock<IBrowserCompanionApiService>();
        // One mutable answer: the backend's status is what changes, not the component.
        api.Setup(a => a.StatusAsync(_profileId, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);

        await using var runtime = new BrowserCompanionRuntime(api.Object, Discovery, Services.GetRequiredService<IJSRuntime>());
        await runtime.FollowAsync(_profile);
        var cut = Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime));

        // Before: connected, the page is known, and none of that is evidence.
        Text(cut, "bd-session").Should().Be("Connected");
        Text(cut, "bd-current-page").Should().Be(Origin + Path);
        Text(cut, "bd-pages-count").Should().Be("0");
        Text(cut, "bd-last-evidence").Should().Be("None");
        Text(cut, "browser-discovery-empty-help")
            .Should().Be("Connected to an approved application page. No browser evidence has been received for it yet.");
        cut.Markup.Should().NotContain("Collecting browser evidence");

        // The companion reports. Nothing re-renders the component by hand and nothing reloads BirkNext.
        status = Connected(EvidenceWithNoFindings());
        await runtime.RefreshAsync();

        cut.WaitForAssertion(() => Text(cut, "bd-pages-count").Should().Be("1"));
        Text(cut, "bd-last-evidence").Should().NotBe("None");
        Text(cut, "bd-current-page").Should().Be(Origin + Path, "the current page survives the arrival of evidence");
        Text(cut, "bd-session").Should().Be("Connected");

        // The evidence is a row, not an empty state, and it is attributed to Browser Companion.
        cut.FindAll("[data-testid=browser-discovery-empty]").Should().BeEmpty();
        var row = cut.Find("[data-testid=browser-discovery-page-row]");
        row.TextContent.Should().Contain(Path).And.Contain("Browser Companion");

        // Zero findings is still evidence, and still never a conformance claim.
        row.TextContent.Should().Contain("Available");
        foreach (var overclaim in new[] { "WCAG compliant", "Conformant", "Passed", "No accessibility issues" })
            cut.Markup.Should().NotContain(overclaim);
    }

    /// <summary>
    /// The backend keys evidence by origin + normalized path, and the environment id it is reported under is the
    /// 32-character id BirkNext issues. Both have to survive the hop into Browser Discovery for the page to appear.
    /// </summary>
    [Fact]
    public async Task EvidenceIsStoredUnderTheAdminOperationsPageIdentity()
    {
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync(_profileId, It.IsAny<CancellationToken>())).ReturnsAsync(Connected(EvidenceWithNoFindings()));

        await using var runtime = new BrowserCompanionRuntime(api.Object, Discovery, Services.GetRequiredService<IJSRuntime>());
        await runtime.FollowAsync(_profile);

        var snapshot = Discovery.GetSnapshot(_profileId);
        var page = snapshot.Pages.Should().ContainSingle().Subject;
        page.Identity.Should().Be(Origin + Path);
        page.BrowserEvidence!.VisitStartedAt.Should().Be(Visit);
        BrowserDiscoveryPresentation.PagesWithEvidence(snapshot).Should().Be(1);
        BrowserDiscoveryPresentation.LastEvidenceAt(snapshot).Should().Be(Captured);
    }
}
