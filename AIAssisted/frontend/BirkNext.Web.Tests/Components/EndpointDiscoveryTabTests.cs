using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Component = BirkNext.Web.Components.EndpointDiscoveryTab;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The Endpoint Discovery tab is page-oriented: it creates a page per safely correlated application page, renders a stylish endpoint
/// table per page, an application-wide overview, a Shared/background bucket and a Backend integrations table, and supports delete /
/// refresh-analysis / delete-all. Refresh applies to exactly one page. It never shows a credential and never persists one.
/// </summary>
public sealed class EndpointDiscoveryTabTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";

    public EndpointDiscoveryTabTests()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;   // getDiscovery/setDiscovery are best-effort and wrapped in try/catch
    }

    private static ObservedNetworkEndpoint Ep(ObservedTrafficCategory cat, string path, string method = "GET", string? pagePath = "/barn/1", bool auth = true, GraphQlOperationType op = GraphQlOperationType.None, string? host = "api-dev.bufetat.no", string scheme = "https", DateTimeOffset? at = null) =>
        new()
        {
            Category = cat, Scheme = scheme, Host = host!, Port = 443, Path = path, Method = method, AuthObserved = auth, LastStatus = 200,
            Source = EndpointDiscoverySource.AuthenticatedProxyTraffic, Confidence = ObservedEndpointConfidence.Verified, Count = 4,
            FirstObservedAt = at ?? DateTimeOffset.UtcNow, LastObservedAt = at ?? DateTimeOffset.UtcNow, OperationType = op,
            PageOrigin = pagePath is null ? null : Origin, PagePath = pagePath
        };

    private static LocalHttpsProxyStatus Traffic(params ObservedNetworkEndpoint[] endpoints) => new()
    {
        SessionId = "s", State = LocalHttpsProxyState.Ready, AuthenticatedCredentialAvailable = true, ObservedNetworkEndpoints = endpoints
    };

    private IRenderedComponent<Component> Render(FrontendAnalysisProfile profile, LocalHttpsProxyStatus status) =>
        Render<Component>(p => p.Add(x => x.Profile, profile).Add(x => x.ProxyStatus, status));

    private static FrontendAnalysisProfile Dev()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", TargetUrl = Origin + "/" };
        return profile;
    }

    private static string Row(IRenderedComponent<Component> cut, string id) => cut.Find($"[data-testid='{id}']").TextContent;
    private static bool Has(IRenderedComponent<Component> cut, string id) => cut.FindAll($"[data-testid='{id}']").Count > 0;

    [Fact]
    public void EmptyStateInvitesTheUserToGenerateTraffic()
    {
        var cut = Render(Dev(), new LocalHttpsProxyStatus());
        Assert.True(Has(cut, "discovery-empty"));
        Assert.Contains("No endpoint traffic has been observed yet", Row(cut, "discovery-empty"));
    }

    [Fact]
    public void ObservedTrafficCreatesPagesAndAStylishTablePerPage()
    {
        var cut = Render(Dev(), Traffic(
            Ep(ObservedTrafficCategory.GraphQl, "/internal/gql", "POST", pagePath: "/sok", op: GraphQlOperationType.Query),
            Ep(ObservedTrafficCategory.Rest, "/api/roles", pagePath: "/roller"),
            Ep(ObservedTrafficCategory.Rest, "/api/children", pagePath: "/sok")));

        Assert.Equal("2", Row(cut, "discovery-pages-count"));
        cut.Find("[data-testid='discovery-nav-pages']").Click();
        var links = cut.FindAll("[data-testid='discovery-page-link']");
        Assert.Equal(2, links.Count);
        // Select the /sok page: its table shows GraphQL + REST with method, host, path.
        links.Single(l => l.TextContent.Contains("/sok")).Click();
        Assert.True(Has(cut, "discovery-page-table"));
        var table = Row(cut, "discovery-page-table");
        Assert.Contains("GraphQL", table);
        Assert.Contains("REST", table);
        Assert.Contains("/internal/gql", table);
        Assert.Contains("api-dev.bufetat.no", table);
    }

    [Fact]
    public void WebSocketAndCategoriesAreClassifiedDistinctly()
    {
        var cut = Render(Dev(), Traffic(
            Ep(ObservedTrafficCategory.WebSocket, "/notifications", "WS", pagePath: "/brukertilgang", scheme: "wss"),
            Ep(ObservedTrafficCategory.Rest, "/api/audit", pagePath: "/brukertilgang")));
        cut.Find("[data-testid='discovery-nav-pages']").Click();
        cut.FindAll("[data-testid='discovery-page-link']").Single(l => l.TextContent.Contains("/brukertilgang")).Click();
        var table = Row(cut, "discovery-page-table");
        Assert.Contains("WebSocket", table);
        Assert.Contains("Connected", table);
    }

    [Fact]
    public void UncorrelatedTrafficAppearsUnderSharedBackground()
    {
        var cut = Render(Dev(), Traffic(Ep(ObservedTrafficCategory.Rest, "/api/config", pagePath: null)));
        cut.Find("[data-testid='discovery-nav-shared']").Click();
        Assert.True(Has(cut, "discovery-shared-table"));
        Assert.Contains("/api/config", Row(cut, "discovery-shared-table"));
    }

    [Fact]
    public void OverviewDeduplicatesAcrossPagesAndListsBackendIntegrations()
    {
        var profile = Dev();
        profile.Integrations.Add(new IntegrationConfig { Name = "M2LB Events", Type = IntegrationType.RabbitMQ, Resource = "rmq05", Enabled = true });
        var cut = Render(profile, Traffic(
            Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/a", op: GraphQlOperationType.Query),
            Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/b", op: GraphQlOperationType.Query)));
        // Overview is default view.
        var table = Row(cut, "discovery-overview-table");
        Assert.Contains("api-dev.bufetat.no", table);
        Assert.Contains("M2LB Events", table);      // backend integration listed, marked as configuration
        Assert.Contains("AMQP", table);
        // Backend integrations view.
        cut.Find("[data-testid='discovery-nav-integrations']").Click();
        Assert.Contains("Configuration discovery", Row(cut, "discovery-backend-integrations"));
        Assert.Contains("No", Row(cut, "discovery-backend-integrations"));   // runtime observed = No
    }

    [Fact]
    public void DeletePageRemovesOnlyThatPageAndKeepsSharedEndpointEvidenceElsewhere()
    {
        var cut = Render(Dev(), Traffic(
            Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/a", op: GraphQlOperationType.Query),
            Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", pagePath: "/b", op: GraphQlOperationType.Query)));
        cut.Find("[data-testid='discovery-nav-pages']").Click();
        cut.FindAll("[data-testid='discovery-page-link']").Single(l => l.TextContent.Contains("/a")).Click();
        cut.Find("[data-testid='discovery-delete-page']").Click();
        cut.Find("[data-testid='discovery-delete-page-confirm']").Click();
        // Page A gone, page B (and its /gql) remains.
        Assert.Equal("1", Row(cut, "discovery-pages-count"));
        Assert.Single(cut.FindAll("[data-testid='discovery-page-link']"));
        Assert.Contains("/b", cut.FindAll("[data-testid='discovery-page-link']").Single().TextContent);
    }

    [Fact]
    public void RefreshAnalysisKeepsThePageAndResetsItToWaitingForFreshTraffic()
    {
        var cut = Render(Dev(), Traffic(Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a")));
        cut.Find("[data-testid='discovery-nav-pages']").Click();
        cut.Find("[data-testid='discovery-page-link']").Click();
        // The only refresh action is "Refresh analysis"; there is no "Clear endpoints" or bulk re-analyze.
        Assert.Empty(cut.FindAll("[data-testid='discovery-clear']"));
        Assert.Equal("Refresh analysis", cut.Find("[data-testid='discovery-refresh']").TextContent.Trim());

        cut.Find("[data-testid='discovery-refresh']").Click();
        Assert.Equal("1", Row(cut, "discovery-pages-count"));   // page kept, not deleted
        Assert.Contains("Waiting for fresh traffic", cut.Find("[data-testid='discovery-page-link']").TextContent);
        Assert.Contains("Waiting for fresh traffic", Row(cut, "discovery-page-state"));
    }

    [Fact]
    public void RefreshedPageDoesNotRepopulateFromTheStillLiveOldTraffic()
    {
        // The proxy session keeps re-reporting old observations on every re-render. A refreshed page must stay waiting, not repopulate.
        var oldTraffic = Traffic(Ep(ObservedTrafficCategory.Rest, "/api/children", pagePath: "/children", at: DateTimeOffset.UtcNow.AddMinutes(-10)));
        var cut = Render(Dev(), oldTraffic);
        cut.Find("[data-testid='discovery-nav-pages']").Click();
        cut.Find("[data-testid='discovery-page-link']").Click();
        cut.Find("[data-testid='discovery-refresh']").Click();
        Assert.Contains("Waiting for fresh traffic", Row(cut, "discovery-page-state"));

        // A re-render re-runs the merge with the SAME old live traffic; the refreshed page must remain empty.
        cut.Render(p => p.Add(x => x.Profile, Dev()).Add(x => x.ProxyStatus, oldTraffic));
        cut.Find("[data-testid='discovery-nav-pages']").Click();
        Assert.Contains("Waiting for fresh traffic", cut.Find("[data-testid='discovery-page-link']").TextContent);
    }

    [Fact]
    public void RefreshedPageRepopulatesFromFreshCorrelatedTraffic()
    {
        var cut = Render(Dev(), Traffic(Ep(ObservedTrafficCategory.Rest, "/api/children", pagePath: "/children", at: DateTimeOffset.UtcNow.AddMinutes(-10))));
        cut.Find("[data-testid='discovery-nav-pages']").Click();
        cut.Find("[data-testid='discovery-page-link']").Click();
        cut.Find("[data-testid='discovery-refresh']").Click();

        // Fresh traffic for the same page arrives after the refresh boundary.
        cut.Render(p => p.Add(x => x.Profile, Dev())
            .Add(x => x.ProxyStatus, Traffic(Ep(ObservedTrafficCategory.Rest, "/api/children", pagePath: "/children", at: DateTimeOffset.UtcNow.AddMinutes(5)))));
        cut.Find("[data-testid='discovery-nav-pages']").Click();
        Assert.Contains("1 endpoints", cut.Find("[data-testid='discovery-page-link']").TextContent);
    }

    [Fact]
    public void DeleteAllClearsBrowserAnalysesButKeepsBackendIntegrations()
    {
        var profile = Dev();
        profile.Integrations.Add(new IntegrationConfig { Name = "Events", Type = IntegrationType.EventHub, Resource = "eh", Enabled = true });
        var cut = Render(profile, Traffic(Ep(ObservedTrafficCategory.Rest, "/api/a", pagePath: "/a")));
        cut.Find("[data-testid='discovery-delete-all']").Click();
        cut.Find("[data-testid='discovery-delete-all-confirm']").Click();
        Assert.Equal("0", Row(cut, "discovery-pages-count"));
        // Backend integrations remain (computed from configuration).
        cut.Find("[data-testid='discovery-nav-integrations']").Click();
        Assert.Contains("Events", Row(cut, "discovery-backend-integrations"));
    }

    [Fact]
    public void NoCredentialOrSecretIsEverRendered()
    {
        var cut = Render(Dev(), Traffic(Ep(ObservedTrafficCategory.Rest, "/api/children", pagePath: "/barn/1")));
        foreach (var forbidden in new[] { "eyJ", "Bearer ", "Authorization", "Cookie", "Set-Cookie" })
            Assert.DoesNotContain(forbidden, cut.Markup);
    }
}
