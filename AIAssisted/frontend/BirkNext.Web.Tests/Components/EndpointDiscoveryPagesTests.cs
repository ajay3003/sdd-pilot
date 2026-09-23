using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Component = BirkNext.Web.Components.EndpointDiscoveryTab;

namespace BirkNext.Web.Tests.Components;

public sealed class EndpointDiscoveryPagesTests : BunitContext
{
    public EndpointDiscoveryPagesTests()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
    private static ObservedNetworkEndpoint Ep(string path, string host = "api.test") => new()
    {
        Host = host, Scheme = "https", Port = 443, Path = path, Method = "GET",
        PageOrigin = "https://app.test", PagePath = "/roles", Category = ObservedTrafficCategory.Rest,
        Provenance = RequestProvenance.ApplicationTraffic, AuthObserved = true, Count = 12, LastStatus = 200,
        FirstObservedAt = DateTimeOffset.UtcNow.AddMinutes(-3), LastObservedAt = DateTimeOffset.UtcNow
    };
    private IRenderedComponent<Component> Page(params ObservedNetworkEndpoint[] endpoints)
    {
        var cut = Render<Component>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://app.test" })
            .Add(c => c.ProxyStatus, new LocalHttpsProxyStatus { ObservedNetworkEndpoints = endpoints }));
        cut.Find("[data-testid=discovery-nav-pages]").Click();
        return cut;
    }
    [Fact]
    public void RailSummaryAndDetailsExplainActualEvidence()
    {
        var path = "/api/autorisasjon/v1/very-long-full-path/roles";
        var cut = Page(Ep(path));
        var rail = cut.Find("[data-testid=discovery-page-link]");
        Assert.Contains("1 capture", rail.TextContent);
        Assert.DoesNotContain("1 captures", rail.TextContent);
        Assert.DoesNotContain("?", rail.TextContent);
        Assert.Contains("1 correlated request group", rail.TextContent);
        Assert.Equal("page", rail.GetAttribute("aria-current"));
        Assert.Contains("https://app.test/roles", cut.Find(".ed-page-sub").TextContent);
        Assert.Contains("Bearer observed", cut.Find("[data-testid=discovery-page-summary]").TextContent);
        Assert.Contains("Last observed", cut.Find("[data-testid=discovery-page-summary]").TextContent);
        var headers = cut.FindAll("[data-testid=discovery-page-table] th").Select(e => e.TextContent).ToArray();
        Assert.Equal(new[] { "Details", "Type", "Method", "Path", "Auth", "Result", "Calls", "Last seen" }, headers);
        Assert.Equal(path, cut.Find(".ed-path").GetAttribute("title"));
        var expand = cut.Find(".ed-expand");
        Assert.Equal("false", expand.GetAttribute("aria-expanded"));
        expand.Click();
        Assert.Equal("true", cut.Find(".ed-expand").GetAttribute("aria-expanded"));
        var details = cut.Find(".ed-detailrow").TextContent;
        foreach (var label in new[] { path, "api.test", "Transport", "Proxy", "Provenance", "Category / reason" }) Assert.Contains(label, details);
        Assert.Contains("12", cut.Find("td[data-label=Calls]").TextContent);
        Assert.Contains("200", cut.Find("td[data-label=Result]").TextContent);
    }
    [Fact]
    public void SharedLinkResetsFiltersAndPagesNeverRenderTechnicalOrMaintenanceTables()
    {
        var cut = Page(Ep("/api/roles"), Ep("/appsettings.json"), Ep("/_framework/System.dll"), Ep("/authentication/login-callback"));
        Assert.Empty(cut.FindAll("[data-testid=discovery-technical], [data-testid=discovery-manage], [data-testid=discovery-delete-all]"));
        foreach (var path in new[] { "/appsettings.json", "/_framework/System.dll", "/authentication/login-callback" }) Assert.DoesNotContain(path, cut.Find("[data-testid=discovery-page-table]").TextContent);
        cut.Find("[data-testid=discovery-search]").Input("missing");
        cut.Find("[data-testid=discovery-open-shared]").Click();
        Assert.Equal("true", cut.Find("[data-testid=discovery-nav-shared]").GetAttribute("aria-selected"));
        Assert.Contains("/appsettings.json", cut.Markup);
        Assert.Equal("", cut.Find("[data-testid=discovery-search]").GetAttribute("value"));
    }
    [Fact]
    public void CapturesAreGenerationsAndArchivedCallsAreNotSummedIntoCurrentEvidence()
    {
        var cut = Page(Ep("/api/roles"));
        cut.Find("[data-testid=discovery-nav-overview]").Click();
        cut.Find("[data-testid=discovery-refresh]").Click();
        Assert.Contains("/api/roles", cut.Find("[data-testid=discovery-capture-table]").TextContent);
        cut.Find("[data-testid=discovery-nav-pages]").Click();
        Assert.Contains("2 captures", cut.Find(".ed-pagelist").TextContent);
        Assert.Contains("0 correlated request groups", cut.Find(".ed-pagelist").TextContent);
        Assert.Contains("No page-correlated application communication observed", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid=discovery-page-table]"));
        Assert.NotNull(cut.Find("[data-testid=discovery-open-shared]"));
    }
    [Fact]
    public void DistinctOperationsAndPathsRemainSeparateAndDifferentHostsRemainVisible()
    {
        var cut = Page(Ep("/api/graphql") with { Category = ObservedTrafficCategory.GraphQl, OperationName = "GetRoles" },
            Ep("/api/graphql") with { Category = ObservedTrafficCategory.GraphQl, OperationName = "GetUsers" }, Ep("/api/another-long-path", "other.test"));
        Assert.Equal(3, cut.FindAll("[data-testid=discovery-endpoint-row]").Count);
        Assert.Contains("Host", cut.Find("thead").TextContent);
        Assert.Contains("GetRoles", cut.Find("tbody").TextContent);
        Assert.Contains("GetUsers", cut.Find("tbody").TextContent);
    }
    [Fact]
    public void OtherIsExplainedWithoutReclassification()
    {
        var cut = Page(Ep("/document") with { Category = ObservedTrafficCategory.OtherHttp });
        Assert.Contains("not classified", cut.Find(".ed-badge-other").GetAttribute("title"));
        cut.Find(".ed-expand").Click();
        Assert.Contains("may include page navigation", cut.Find(".ed-detailrow").TextContent);
    }
    [Fact]
    public void RepeatedDocumentTitlesAreHiddenAndDistinctTitlesAndOriginsDisambiguate()
    {
        var cut = Page(Ep("/api/roles"), Ep("/api/users") with { PagePath = "/users" });
        var pages = Services.GetRequiredService<IEndpointDiscoveryService>().GetSnapshot("dev").Pages;
        foreach (var page in pages) page.DisplayName = "Same app";
        cut.Find("[data-testid=discovery-nav-pages]").Click();
        Assert.DoesNotContain("Same app", cut.Find(".ed-pagelist").TextContent);
        pages[1].DisplayName = "Other title";
        pages[1].PageOrigin = "https://other.test";
        cut.Find("[data-testid=discovery-nav-pages]").Click();
        Assert.Contains("Other title", cut.Find(".ed-pagelist").TextContent);
        Assert.Contains("https://other.test", cut.Find(".ed-pagelist").TextContent);
    }
}
