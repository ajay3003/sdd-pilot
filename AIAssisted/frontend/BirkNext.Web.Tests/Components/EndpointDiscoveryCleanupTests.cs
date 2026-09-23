using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Component = BirkNext.Web.Components.EndpointDiscoveryTab;

namespace BirkNext.Web.Tests.Components;

public class EndpointDiscoveryCleanupTests : BunitContext
{
    public EndpointDiscoveryCleanupTests()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
    private static ObservedNetworkEndpoint Ep(string path, string? page = "/admin/operations", RequestProvenance provenance = RequestProvenance.ApplicationTraffic) => new()
    {
        Host = "app.test", Path = path, PageOrigin = page is null ? null : "https://app.test", PagePath = page,
        Category = path.EndsWith("graphql") ? ObservedTrafficCategory.GraphQl : ObservedTrafficCategory.Rest,
        Provenance = provenance, Count = 2, LastObservedAt = DateTimeOffset.UtcNow, FirstObservedAt = DateTimeOffset.UtcNow, Method = "GET"
    };
    private IRenderedComponent<Component> Page(params ObservedNetworkEndpoint[] endpoints) => Render<Component>(p => p
        .Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://app.test" })
        .Add(c => c.ProxyStatus, new LocalHttpsProxyStatus { ProxyListening = true, RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, ObservedNetworkEndpoints = endpoints }));

    [Fact]
    public void RoutesLeadAndDuplicateTitlesDoNotHideIdentity()
    {
        var cut = Page(Ep("/api/a"), Ep("/api/b", "/admin/general-roles"), Ep("/appsettings.json", "/appsettings.json"));
        var snapshot = Services.GetRequiredService<IEndpointDiscoveryService>().GetSnapshot("dev");
        foreach (var page in snapshot.Pages) page.DisplayName = "M2LB.Frontend.Web";
        cut.Find("[data-testid=discovery-nav-pages]").Click();
        Assert.Equal(2, cut.FindAll(".ed-pagetitle").Count);
        Assert.All(cut.FindAll(".ed-pagetitle"), e => Assert.StartsWith("/admin/", e.TextContent));
        Assert.DoesNotContain("appsettings", cut.Find(".ed-pagelist").TextContent);
        cut.FindAll("[data-testid=discovery-page-link]").Single(e => e.TextContent.Contains("/admin/general-roles")).Click();
        Assert.Contains("/api/b", cut.Find("[data-testid=discovery-page-table]").TextContent);
        Assert.DoesNotContain("/api/a", cut.Find("[data-testid=discovery-page-table]").TextContent);
    }

    [Fact]
    public void ProbesRemainInspectableOutsideApplicationOverview()
    {
        var cut = Page(Ep("/api/autorisasjon/graphql"), Ep("/graphql", provenance: RequestProvenance.DiscoveryProbe));
        Assert.DoesNotContain("Discovery probe", cut.Find("[data-testid=discovery-overview-table]").TextContent);
        cut.Find("[data-testid=discovery-nav-shared]").Click();
        var technical = cut.Find("[data-testid=discovery-technical]");
        Assert.False(technical.HasAttribute("open"));
        Assert.Contains("Discovery probe", technical.TextContent);
        Assert.Contains("Proxy", technical.TextContent);
        cut.Find("[data-testid=discovery-technical] [data-testid=discovery-provenance-filter]").Change("probe");
        Assert.Single(cut.FindAll("[data-testid=discovery-technical-table] [data-testid=discovery-endpoint-row]"));
        cut.Find("[data-testid=discovery-technical] [data-testid=discovery-provenance-filter]").Change("app");
        Assert.Empty(cut.FindAll("[data-testid=discovery-technical-table]"));
    }

    [Fact]
    public void SharedCategoryAndSearchAndProtocolFiltersWork()
    {
        var cut = Page(Ep("/api/a", null), Ep("/api/graphql", null), Ep("/appsettings.json", null));
        cut.Find("[data-testid=discovery-nav-shared]").Click();
        Assert.Contains("Configuration", cut.Find("[data-testid=discovery-shared-table]").TextContent);
        Assert.Contains("Uncorrelated", cut.Find("[data-testid=discovery-shared-table]").TextContent);
        cut.Find("#discovery-evidence-panel [data-testid=discovery-type-filter]").Change("graphql");
        Assert.Single(cut.FindAll("[data-testid=discovery-shared-table] [data-testid=discovery-endpoint-row]"));
        cut.Find("#discovery-evidence-panel [data-testid=discovery-search]").Input("missing");
        Assert.Empty(cut.FindAll("[data-testid=discovery-shared-table]"));
    }

    [Fact]
    public void RefreshArchivesCaptureAndDeleteRequiresConfirmationInsideDisclosure()
    {
        var cut = Page(Ep("/api/a"));
        cut.Find("[data-testid=discovery-nav-pages]").Click();
        Assert.Empty(cut.FindAll("[data-testid=discovery-manage]"));
        cut.Find("[data-testid=discovery-nav-overview]").Click();
        Assert.NotNull(cut.Find("[data-testid=discovery-delete-page]").Closest("details"));
        Assert.Empty(cut.FindAll("[data-testid=discovery-delete-page-confirm]"));
        cut.Find("[data-testid=discovery-refresh]").Click();
        var snapshot = Services.GetRequiredService<IEndpointDiscoveryService>().GetSnapshot("dev");
        Assert.Single(snapshot.Pages.Single().NetworkHistory);
        Assert.Contains("/api/a", cut.Find("[data-testid=discovery-capture-table]").TextContent);
        cut.Find("[data-testid=discovery-delete-page]").Click();
        Assert.Contains("this page only", cut.Find("[data-testid=discovery-delete-page-note]").TextContent);
        Assert.Single(snapshot.Pages);
        cut.Find("[data-testid=discovery-delete-page-confirm]").Click();
        Assert.Empty(snapshot.Pages);
    }

    [Fact]
    public void KeyboardNavigationAndIntegrationOwnershipAreExplicit()
    {
        var cut = Page();
        cut.Find("[data-testid=discovery-nav-overview]").KeyDown("ArrowRight");
        Assert.Equal("true", cut.Find("[data-testid=discovery-nav-pages]").GetAttribute("aria-selected"));
        Assert.Empty(cut.FindAll("[data-testid=discovery-nav-integrations]"));
        Assert.Equal(EndpointDiscoveryPresentation.IntegrationsHref, cut.Find("[data-testid=discovery-open-integrations]").GetAttribute("href"));
        Assert.Contains("No application pages have produced correlated network evidence yet", cut.Markup);
    }
}
