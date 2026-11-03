using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
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
/// Browser Discovery keeps every page the paired browser visited — not only pages whose full evidence was captured. A visit is persisted the
/// moment it is observed (origin + route, per environment, counted per companion session); evidence dimensions are tracked separately, so a
/// page reads "Visited · evidence pending" until DOM / accessibility / performance arrive. Endpoint Discovery traffic is correlated as
/// supporting evidence only; it does not own the page history.
/// </summary>
public sealed class BrowserDiscoveryPageHistoryTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Dev", TargetUrl = Origin };

    public BrowserDiscoveryPageHistoryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
    }

    private static BrowserPageVisit Visit(string route, int minute, int count = 1, bool live = false) =>
        new() { Origin = Origin, Route = route, FirstSeenAt = T0.AddMinutes(minute), LastSeenAt = T0.AddMinutes(minute), VisitCount = count, Live = live };

    [Fact]
    public void Visits_are_upserted_by_route_counted_per_session_and_never_double_counted()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.MergePageVisits(snapshot, [Visit("/", 0), Visit("/person", 1), Visit("/admin/operations", 2)], "s1", T0).Should().BeTrue();
        EndpointDiscoveryMerge.MergePageVisits(snapshot, [Visit("/", 0), Visit("/person", 1), Visit("/admin/operations", 2)], "s1", T0).Should().BeFalse("the same poll again changes nothing");
        EndpointDiscoveryMerge.MergePageVisits(snapshot, [Visit("/person", 5, count: 2)], "s1", T0).Should().BeTrue();
        EndpointDiscoveryMerge.MergePageVisits(snapshot, [Visit("/person", 30)], "s2", T0).Should().BeTrue("a later companion session adds its own visits");

        snapshot.PageVisits.Select(v => v.Path).Should().BeEquivalentTo(["/", "/person", "/admin/operations"]);
        var person = snapshot.PageVisits.Single(v => v.Path == "/person");
        (person.VisitCount, person.FirstVisitedAt, person.LastVisitedAt).Should().Be((3, T0.AddMinutes(1), T0.AddMinutes(30)));
    }

    [Fact]
    public void Rows_list_visited_pages_beside_evidenced_pages_and_only_evidence_counts_as_captured()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, [new BrowserPageEvidence { ProfileId = "dev", PageOrigin = Origin, PagePath = "/person",
            VisitStartedAt = T0, CapturedAt = T0.AddSeconds(5), Dom = new BrowserDomSummary { NodeCount = 10 } }], T0);
        EndpointDiscoveryMerge.MergePageVisits(snapshot, [Visit("/person", 0), Visit("/admin/operations", 3), Visit("/admin/general-roles", 4)], "s1", T0);

        var rows = BrowserDiscoveryPresentation.Rows(snapshot);
        rows.Select(r => (r.Route, r.EvidenceCaptured)).Should().Equal(("/admin/general-roles", false), ("/admin/operations", false), ("/person", true));
        BrowserDiscoveryPresentation.PagesWithEvidence(snapshot).Should().Be(1, "a visit observed is not a page evidenced");
        BrowserDiscoveryPresentation.EvidenceDimensions(rows[0]).Should().Equal(("Page visited", true), ("DOM", false), ("Accessibility", false), ("Performance", false));
    }

    [Fact]
    public void A_visited_page_without_evidence_is_listed_and_explained_with_its_network_context()
    {
        var discovery = Services.GetRequiredService<IEndpointDiscoveryService>();
        var snapshot = discovery.GetSnapshot("dev");
        EndpointDiscoveryMerge.Merge(snapshot, [new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = "m2lbdev.bufetat.no", Port = 443,
            Path = "/api/operasjoner", Method = "GET", Count = 8, LastStatus = 200, FirstObservedAt = T0, LastObservedAt = T0, PageOrigin = Origin, PagePath = "/admin/operations",
            Confidence = ObservedEndpointConfidence.Verified }], T0);
        EndpointDiscoveryMerge.MergePageVisits(snapshot, [Visit("/admin/operations", 2, count: 2)], "s1", T0);

        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus { ProfileId = "dev", State = BrowserCompanionState.Connected, ApprovedOrigins = [Origin] });
        var runtime = new BrowserCompanionRuntime(api.Object, discovery, Services.GetRequiredService<IJSRuntime>());
        var cut = Render<BrowserDiscoveryTab>(p => p.Add(c => c.Profile, _profile).Add(c => c.Runtime, runtime)).Loaded();

        cut.FindAll("[data-testid=browser-discovery-dimension-pending]").Should().HaveCount(3, "DOM, accessibility and performance are pending, not 'Not captured'");
        cut.Find("[data-testid=browser-discovery-nav-pages]").Click();
        cut.Find("[data-testid=browser-discovery-page-pending]").TextContent.Should().Contain("Visited · evidence pending");
        cut.Find("[data-testid=browser-discovery-selected-page]").TextContent.Should().Be("/admin/operations");
        cut.Find("[data-testid=browser-discovery-visit-dimensions]").TextContent.Should().Contain("Page visited").And.Contain("DOM pending");
        cut.Find("[data-testid=browser-discovery-visit-requests]").TextContent.Should().StartWith("8 API requests observed during this page context");
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
