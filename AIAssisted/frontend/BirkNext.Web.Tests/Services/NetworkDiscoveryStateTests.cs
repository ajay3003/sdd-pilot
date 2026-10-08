using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Services;

public class NetworkDiscoveryStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private static ObservedNetworkEndpoint Ep(string path = "/api/roles", string? page = "/admin/operations") => new()
    {
        Host = "app.test", Path = path, PageOrigin = page is null ? null : "https://app.test", PagePath = page,
        Category = ObservedTrafficCategory.Rest, Provenance = RequestProvenance.ApplicationTraffic,
        FirstObservedAt = Now.AddSeconds(-10), LastObservedAt = Now.AddSeconds(-1), Count = 3
    };
    private static LocalHttpsProxyStatus Running(params ObservedNetworkEndpoint[] endpoints) => new()
    {
        ProxyListening = true, RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, RuntimeId = "runtime",
        StartedAt = Now.AddMinutes(-10), ObservedNetworkEndpoints = endpoints
    };

    [Fact]
    public void HistoricalTrafficNeverActivatesAStoppedListener()
    {
        var state = LiveDiscoveryState.From(new() { State = LocalHttpsProxyState.Ready, SessionId = "old", ObservedNetworkEndpoints = [Ep()] }, Now, true);
        Assert.False(state.ObservationActive);
        Assert.Equal("Historical only", state.TrafficFreshness);
        Assert.Null(state.LastTrafficAt);
        Assert.Null(state.ActiveRuntimeId);
    }

    [Fact]
    public void RunningWithoutTrafficIsActiveWithoutEvidence()
    {
        var state = LiveDiscoveryState.From(Running(), Now, false);
        Assert.True(state.ObservationActive);
        Assert.Equal("No recent traffic", state.TrafficFreshness);
        Assert.Equal("Not observed", state.AuthenticatedTrafficState);
        Assert.Equal("Awaiting page evidence", state.PageCorrelationState);
    }

    [Theory]
    [InlineData(-1, "Recent")]
    [InlineData(-120, "Recent")]
    [InlineData(-121, "No recent traffic")]
    [InlineData(1, "No recent traffic")]
    public void FreshnessUsesDocumentedWindow(int seconds, string expected)
    {
        var state = LiveDiscoveryState.From(Running(Ep() with { LastObservedAt = Now.AddSeconds(seconds) }), Now, true);
        Assert.Equal(expected, state.TrafficFreshness);
    }

    [Fact]
    public void CredentialAvailabilityDoesNotProveAuthenticatedTraffic()
    {
        Assert.Equal("Not observed", LiveDiscoveryState.From(Running() with { AuthenticatedCredentialAvailable = true }, Now, false).AuthenticatedTrafficState);
        Assert.Equal("Bearer observed", LiveDiscoveryState.From(Running(Ep() with { AuthObserved = true }), Now, true).AuthenticatedTrafficState);
        Assert.Equal("Not observed", LiveDiscoveryState.From(Running(Ep() with { AuthObserved = true, Provenance = RequestProvenance.DiscoveryProbe }), Now, true).AuthenticatedTrafficState);
    }

    [Fact]
    public void CorrelationRequiresAnApplicationRouteInCurrentRuntime()
    {
        Assert.Equal("Observed", LiveDiscoveryState.From(Running(Ep()), Now, true).PageCorrelationState);
        Assert.Equal("Awaiting page evidence", LiveDiscoveryState.From(Running(Ep(page: "/appsettings.json")), Now, true).PageCorrelationState);
        Assert.Null(LiveDiscoveryState.From(Running(Ep() with { LastObservedAt = Now.AddHours(-1) }), Now, true).LastTrafficAt);
    }

    [Fact]
    public void SummaryCountsHostsNotOriginsAndExcludesTechnicalAndUnknownTraffic()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, [Ep(), Ep("/api/other") with { Host = "APP.TEST", Port = 8443 },
            Ep("/api/autorisasjon/graphql") with { Category = ObservedTrafficCategory.GraphQl },
            Ep("/oauth/token") with { Category = ObservedTrafficCategory.Authentication },
            Ep("/appsettings.json", "/appsettings.json"), Ep("/graphql") with { Provenance = RequestProvenance.DiscoveryProbe },
            Ep("/unknown") with { Provenance = RequestProvenance.Unknown }], Now);
        var summary = ObservedNetworkEvidenceSummary.From(snapshot);
        Assert.Equal(1, summary.BackendHostCount);
        Assert.Equal(1, summary.ApplicationPageCount);
        Assert.Equal(12, summary.TotalObservedRequestCount);
        Assert.Equal(6, summary.RestCallCount);
        Assert.Equal(3, summary.GraphQlCallCount);
        Assert.Equal(3, summary.AuthCallCount);
        Assert.Equal(3, summary.ProbeCount);
        Assert.Equal(Now.AddSeconds(-1), summary.LastCapturedEvidenceAt);
        _ = LiveDiscoveryState.From(new(), Now, true);
        Assert.Equal(summary, ObservedNetworkEvidenceSummary.From(snapshot));
    }

    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/swagger.json")]
    [InlineData("/_framework/app.js")]
    [InlineData("/_content/app.css")]
    [InlineData("/authentication/login-callback")]
    [InlineData("/birknext-unknown-route-probe-12")]
    public void TechnicalPageHistoryIsPreservedOutsideApplicationPages(string route)
    {
        var snapshot = new EndpointDiscoverySnapshot { Pages = [new() { PageOrigin = "https://app.test", PagePath = route, Endpoints = [Ep(route, route) with { Provenance = RequestProvenance.Unknown }] }] };
        EndpointDiscoveryMerge.Reclassify(snapshot);
        Assert.Empty(snapshot.Pages);
        Assert.Single(snapshot.ExcludedPageHistory);
        Assert.Single(snapshot.Shared);
        Assert.False(NetworkEvidencePolicy.IsApiCandidate(snapshot.Shared[0]));
    }

    [Fact]
    public void CapturesConvergeOnRouteAndKeepProvenanceSeparate()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, [Ep(), Ep() with { Count = 5 }], Now);
        Assert.Single(snapshot.Pages);
        Assert.Equal(5, snapshot.Pages[0].Endpoints.Single().Count);
        EndpointDiscoveryMerge.Merge(snapshot, [Ep() with { Provenance = RequestProvenance.DiscoveryProbe }], Now);
        Assert.Single(snapshot.Shared);
        Assert.Equal(5, snapshot.Pages[0].Endpoints.Single().Count);
    }
}
