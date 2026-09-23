using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Component = BirkNext.Web.Components.EndpointDiscoveryTab;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Endpoint Discovery → Overview is summary-first: live state, retained counts, application backend communication, then a
/// technical/background SUMMARY whose raw rows live one click deeper in Shared / background. The evidence model is not
/// changed here, so these tests also hold the classifications the layout must never blur.
/// </summary>
public sealed class EndpointDiscoveryOverviewTests : BunitContext
{
    private const string Host = "m2lbdev.bufetat.no";

    public EndpointDiscoveryOverviewTests()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ObservedNetworkEndpoint Ep(string path, ObservedTrafficCategory category, int count, string? page = "/admin/roles",
        RequestProvenance provenance = RequestProvenance.BrowserObservedTraffic, bool auth = false, DateTimeOffset? at = null) => new()
    {
        Host = Host, Scheme = "https", Port = 443, Path = path, Method = category == ObservedTrafficCategory.GraphQl ? "POST" : "GET",
        PageOrigin = page is null ? null : $"https://{Host}", PagePath = page, Category = category, Provenance = provenance,
        Source = EndpointDiscoverySource.AuthenticatedProxyTraffic, AuthObserved = auth, Count = count, LastStatus = 200,
        FirstObservedAt = (at ?? DateTimeOffset.UtcNow).AddMinutes(-1), LastObservedAt = at ?? DateTimeOffset.UtcNow,
    };

    /// <summary>The screenshot's shape: REST 16 + GraphQL 31 + Other 3 = 50 application calls, plus technical evidence.</summary>
    private static ObservedNetworkEndpoint[] Sample(bool probe = false) =>
    [
        Ep("/api/roles", ObservedTrafficCategory.Rest, 10, "/admin/roles", auth: true),
        Ep("/api/users", ObservedTrafficCategory.Rest, 6, "/admin/users", auth: true),
        Ep("/graphql", ObservedTrafficCategory.GraphQl, 31, "/admin/operations", auth: true),
        Ep("/admin/export", ObservedTrafficCategory.OtherHttp, 3, "/admin/export"),
        // Technical / background: browser-observed, NOT probes.
        Ep("/appsettings.json", ObservedTrafficCategory.Rest, 4, null),
        Ep("/appsettings.Dev.json", ObservedTrafficCategory.Rest, 2, null),
        Ep("/authentication/login-callback", ObservedTrafficCategory.Authentication, 2, null),
        Ep("/_framework/HotChocolate.dll", ObservedTrafficCategory.StaticAsset, 70, null),
        .. probe ? [Ep("/api/graphql", ObservedTrafficCategory.GraphQl, 1, null, RequestProvenance.DiscoveryProbe)] : Array.Empty<ObservedNetworkEndpoint>(),
    ];

    private IRenderedComponent<Component> Tab(ObservedNetworkEndpoint[] endpoints, bool active = true, DateTimeOffset? started = null) =>
        Render<Component>(p => p
            .Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", TargetUrl = $"https://{Host}" })
            .Add(c => c.ProxyStatus, new LocalHttpsProxyStatus
            {
                ProxyListening = active, RuntimeStatus = active ? LocalHttpsProxyRuntimePhase.Running : LocalHttpsProxyRuntimePhase.Stopped,
                StartedAt = started ?? DateTimeOffset.UtcNow.AddHours(-1), ObservedNetworkEndpoints = endpoints,
            }));

    private static int Index(IRenderedComponent<Component> cut, string testId) =>
        cut.Markup.IndexOf($"data-testid=\"{testId}\"", StringComparison.Ordinal);

    // §41 Summary first.
    [Fact]
    public void OverviewReadsLiveThenRetainedThenBackendThenTechnicalThenMaintenance()
    {
        var cut = Tab(Sample());

        var order = new[] { "discovery-overview", "discovery-evidence-summary", "discovery-overview-table", "discovery-technical-summary", "discovery-manage" }
            .Select(id => Index(cut, id)).ToArray();
        order.Should().OnlyContain(i => i >= 0);
        order.Should().BeInAscendingOrder();
    }

    // §17 / §21 / §40 No raw explorer on Overview; the summary links one click deeper.
    [Fact]
    public void OverviewShowsATechnicalSummaryNotRawRows()
    {
        var cut = Tab(Sample());

        cut.FindAll("[data-testid=discovery-technical-table]").Should().BeEmpty();
        cut.FindAll("[data-testid=discovery-endpoint-row]").Should().BeEmpty("Overview renders no raw evidence rows");
        cut.Find("[data-testid=discovery-technical-total]").TextContent.Should().Be("78");
        var breakdown = cut.Find("[data-testid=discovery-technical-breakdown]").TextContent;
        breakdown.Should().Contain("Static resource").And.Contain("70").And.Contain("Configuration").And.Contain("6")
            .And.Contain("Authentication callback").And.Contain("2");
    }

    // §3 / §42 / §43 The heading names what the rows are; with no probes, nothing says "probe activity".
    [Fact]
    public void TheHeadingDescribesBackgroundEvidenceNotProbeActivity()
    {
        var cut = Tab(Sample());

        cut.Find("[data-testid=discovery-probe-count]").TextContent.Should().Be("0");
        cut.Markup.Should().NotContain("discovery probe activity").And.NotContain("Discovery probe activity");
        cut.Find("#discovery-technical-summary-heading").TextContent.Should().Be("Technical / background evidence");
        cut.FindAll("[data-testid=discovery-show-probes]").Should().BeEmpty("there are no probes to show");

        cut.Find("[data-testid=discovery-nav-shared]").Click();
        cut.Find("[data-testid=discovery-shared-panel]").TextContent.Should().NotContainAny("discovery probe activity", "Discovery probe activity");
    }

    // §17 / §19 / §20 Opening from Overview lands on the Shared explorer, expanded, with its filters; probes one click away.
    [Fact]
    public void ShowTechnicalEvidenceOpensTheExplorerAndProbesAreOneFilterAway()
    {
        var cut = Tab(Sample(probe: true));

        cut.Find("[data-testid=discovery-show-probes]").Click();

        cut.Find("[data-testid=discovery-nav-shared]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=discovery-provenance-filter]").GetAttribute("value").Should().Be("probe");
        var rows = cut.FindAll("[data-testid=discovery-shared-table] [data-testid=discovery-endpoint-row]");
        rows.Should().ContainSingle().Which.TextContent.Should().Contain("Discovery probe");
        foreach (var column in new[] { "Type", "Category / reason", "Method", "Host", "Path", "Auth", "Result", "Calls", "Last seen", "Transport", "Provenance" })
            cut.Find("[data-testid=discovery-shared-table] thead").TextContent.Should().Contain(column);
    }

    // §44 / §45 / §46 / §47 Backend communication is application traffic only, and its calls add up to Application requests.
    [Fact]
    public void BackendCommunicationIsApplicationTrafficOnlyAndAddsUpToApplicationRequests()
    {
        var cut = Tab(Sample(probe: true));

        cut.Find("[data-testid=discovery-app-requests]").TextContent.Should().Be("50");
        var table = cut.Find("[data-testid=discovery-overview-table]");
        var calls = table.QuerySelectorAll("tbody tr").Select(r => int.Parse(r.Children[4].TextContent.Trim())).ToList();
        calls.Sum().Should().Be(50);
        table.QuerySelectorAll("tbody tr").Select(r => r.Children[1].TextContent.Trim()).Should().BeEquivalentTo(["REST", "GraphQL", "Other"]);
        table.TextContent.Should().NotContainAny("appsettings", "_framework", "login-callback", "Discovery probe", "Static");
        cut.Find("[data-testid=discovery-overview-help]").TextContent.Should().Contain("adds up to Application requests (50)");
    }

    // §7 / §12 Bearer stays an observation.
    [Fact]
    public void AuthIsShownAsObservedBearerWithItsCaveat()
    {
        var cut = Tab(Sample());

        var authCells = cut.FindAll("[data-testid=discovery-overview-row]").Select(r => r.Children[3]).ToList();
        authCells.Should().Contain(c => c.TextContent.Trim() == "Bearer observed");
        authCells.Should().NotContain(c => c.TextContent.Trim() == "Bearer");
        authCells.First(c => c.TextContent.Trim() == "Bearer observed").GetAttribute("title")
            .Should().Contain("does not independently verify the browser authentication state");
        cut.Find("[data-testid=discovery-auth-context]").Closest(".ed-ov-item")!.GetAttribute("title")
            .Should().Contain("does not independently verify");
    }

    // §11 Transport and provenance stay separate columns.
    [Fact]
    public void TransportAndProvenanceStaySeparate()
    {
        var row = Tab(Sample()).FindAll("[data-testid=discovery-overview-row]").First();
        row.Children[6].TextContent.Should().Be("Proxy");
        row.Children[7].TextContent.Should().Be("Browser observed traffic");
    }

    // §13 Other is explained.
    [Fact]
    public void OtherTypeIsExplained()
    {
        var other = Tab(Sample()).FindAll("[data-testid=discovery-overview-row]").Single(r => r.Children[1].TextContent.Trim() == "Other");
        other.Children[1].QuerySelector(".ed-badge")!.GetAttribute("title").Should().Contain("not classified as REST, GraphQL or WebSocket");
    }

    // §14 / §49 The page count is a keyboard-reachable button that opens Pages filtered to that host and type.
    [Fact]
    public void PageCountDrillsDownIntoAFilteredPagesTab()
    {
        var cut = Tab(Sample());
        var rest = cut.FindAll("[data-testid=discovery-overview-row]").Single(r => r.Children[1].TextContent.Trim() == "REST");
        var button = rest.QuerySelector("[data-testid=discovery-overview-pages]")!;
        button.TagName.Should().Be("BUTTON", "a real button is focusable and activates with Enter and Space");
        button.TextContent.Trim().Should().Be("2");
        button.GetAttribute("aria-label").Should().Contain("REST").And.Contain(Host);

        button.Click();

        cut.Find("[data-testid=discovery-nav-pages]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[data-testid=discovery-page-filter]").TextContent.Should().Contain("REST").And.Contain(Host);
        cut.FindAll(".ed-pagetitle").Select(e => e.TextContent).Should().BeEquivalentTo(["/admin/roles", "/admin/users"]);
        cut.Find("[data-testid=discovery-page-table]").TextContent.Should().NotContain("/graphql");

        cut.Find("[data-testid=discovery-page-filter-clear]").Click();
        cut.FindAll(".ed-pagetitle").Should().HaveCount(4);
    }

    // §16 / §50 Integrations are linked, never listed.
    [Fact]
    public void IntegrationsAreOnlyLinked()
    {
        var cut = Tab(Sample());
        cut.Find("[data-testid=discovery-open-integrations]").GetAttribute("href").Should().Be(EndpointDiscoveryPresentation.IntegrationsHref);
        cut.Markup.Should().NotContain("Configured integration</th>");
    }

    // §23 / §25 / §51 Maintenance is collapsed, and the destructive action is secondary and confirmed.
    [Fact]
    public void MaintenanceIsDemotedAndDeletionIsConfirmed()
    {
        var cut = Tab(Sample());
        var manage = cut.Find("[data-testid=discovery-manage]");
        manage.TagName.Should().Be("DETAILS");
        manage.HasAttribute("open").Should().BeFalse();
        manage.QuerySelector("summary")!.TextContent.Should().StartWith("Maintenance");

        var delete = cut.Find("[data-testid=discovery-delete-all]");
        delete.ClassList.Should().Contain("btn-secondary").And.NotContain("btn-danger");
        delete.Click();
        cut.Find("[role=alert]").TextContent.Should().Contain("Configured integrations are kept");
        cut.Find("[data-testid=discovery-delete-all-confirm]").Should().NotBeNull();
    }

    // §6 / §37 / §52 Active observation with no recent traffic is a valid, explained state.
    [Fact]
    public void ActiveObservationWithoutRecentTrafficIsExplained()
    {
        var old = DateTimeOffset.UtcNow.AddMinutes(-10);
        var cut = Tab([Ep("/api/roles", ObservedTrafficCategory.Rest, 1, at: old)], started: old.AddMinutes(-1));

        cut.Find("[data-testid=discovery-active]").TextContent.Should().Be("Active");
        cut.Find("[data-testid=discovery-no-recent-traffic]").TextContent.Should().Contain("Observation is active, but no traffic has arrived");
        cut.Find("[data-testid=discovery-active]").Closest(".ed-ov-item")!.GetAttribute("title").Should().Contain("does not mean traffic is flowing");
    }

    // §38 / §52 Empty states explain themselves.
    [Fact]
    public void EmptyStatesSayWhatIsMissing()
    {
        var cut = Tab([]);
        cut.Find("[data-testid=discovery-empty]").TextContent.Should().Contain("No backend communication observed yet");
        cut.Find("[data-testid=discovery-technical-empty]").TextContent.Should().Be("No technical or background evidence retained.");
        cut.FindAll("[data-testid=discovery-overview-table]").Should().BeEmpty();

        var onlyApplication = Tab([Ep("/api/roles", ObservedTrafficCategory.Rest, 1)]);
        onlyApplication.FindAll("[data-testid=discovery-technical-empty]").Should().NotBeEmpty();
    }

    // §48 Provenance is an independent dimension, never derived from category.
    [Fact]
    public void ProvenanceIsNotDerivedFromCategory()
    {
        var cut = Tab(Sample(probe: true));
        cut.Find("[data-testid=discovery-show-technical]").Click();
        var rows = cut.FindAll("[data-testid=discovery-shared-table] [data-testid=discovery-endpoint-row]");
        rows.Single(r => r.TextContent.Contains("/_framework/HotChocolate.dll")).TextContent.Should().Contain("Browser observed traffic").And.Contain("Static resource");
        rows.Single(r => r.TextContent.Contains("/appsettings.json")).TextContent.Should().Contain("Configuration").And.Contain("Browser observed traffic");
        rows.Single(r => r.TextContent.Contains("/api/graphql")).TextContent.Should().Contain("Discovery probe");
        rows.Single(r => r.TextContent.Contains("login-callback")).TextContent.Should().Contain("Authentication callback");
    }
}
