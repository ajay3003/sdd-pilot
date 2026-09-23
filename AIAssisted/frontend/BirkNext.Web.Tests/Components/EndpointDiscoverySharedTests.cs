using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Component = BirkNext.Web.Components.EndpointDiscoveryTab;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Endpoint Discovery → Shared / background: one explorer over everything the tab owns, a summary in the column's own
/// words, and an expanded row that adds evidence instead of repeating the row. The classification itself is unchanged.
/// </summary>
public sealed class EndpointDiscoverySharedTests : BunitContext
{
    private const string Host = "m2lbdev.bufetat.no";
    private const string LongPath = "/_content/Microsoft.AspNetCore.Components.WebAssembly.Authentication/AuthenticationService.js";

    public EndpointDiscoverySharedTests()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ObservedNetworkEndpoint Ep(string path, ObservedTrafficCategory category, int count, string? page = null,
        RequestProvenance provenance = RequestProvenance.BrowserObservedTraffic, bool auth = false, int status = 200,
        ObservedEndpointConfidence confidence = ObservedEndpointConfidence.Verified) => new()
    {
        Host = Host, Scheme = "https", Port = 443, Path = path, Method = "GET",
        PageOrigin = page is null ? null : $"https://{Host}", PagePath = page, Category = category, Confidence = confidence,
        Provenance = provenance, Source = EndpointDiscoverySource.AuthenticatedProxyTraffic, AuthObserved = auth, Count = count,
        LastStatus = status, FirstObservedAt = DateTimeOffset.UtcNow.AddMinutes(-5), LastObservedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Static 83, Configuration 6, Authentication callback 2, one uncorrelated application call, one probe.</summary>
    private static ObservedNetworkEndpoint[] Sample() =>
    [
        Ep("/_framework/HotChocolate.dll", ObservedTrafficCategory.StaticAsset, 70),
        Ep(LongPath, ObservedTrafficCategory.StaticAsset, 13, status: 304),
        Ep("/appsettings.json", ObservedTrafficCategory.OtherHttp, 4),
        Ep("/appsettings.Dev.json", ObservedTrafficCategory.OtherHttp, 2),
        Ep("/authentication/login-callback", ObservedTrafficCategory.Authentication, 2, confidence: ObservedEndpointConfidence.Candidate),
        Ep("/api/ping", ObservedTrafficCategory.Rest, 1, auth: true),
        Ep("/birknext-unknown-route-probe-1", ObservedTrafficCategory.OtherHttp, 1, provenance: RequestProvenance.DiscoveryProbe),
        // Page-correlated application traffic belongs to Pages, never here.
        Ep("/api/roles", ObservedTrafficCategory.Rest, 10, page: "/admin/roles", auth: true),
    ];

    private IRenderedComponent<Component> Shared(ObservedNetworkEndpoint[]? endpoints = null)
    {
        var cut = Render<Component>(p => p
            .Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", TargetUrl = $"https://{Host}" })
            .Add(c => c.ProxyStatus, new LocalHttpsProxyStatus
            {
                ProxyListening = true, RuntimeStatus = LocalHttpsProxyRuntimePhase.Running,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-1), ObservedNetworkEndpoints = endpoints ?? Sample(),
            }));
        cut.Find("[data-testid=discovery-nav-shared]").Click();
        return cut;
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Rows(IRenderedComponent<Component> cut) =>
        cut.FindAll("[data-testid=discovery-shared-table] [data-testid=discovery-endpoint-row]");

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Component> cut, string path) =>
        Rows(cut).Single(r => r.QuerySelector(".ed-path")!.GetAttribute("title") == path);

    // §31 One canonical explorer; the second table is gone and nothing was lost from the union.
    [Fact]
    public void SharedRendersOneExplorerOverEverythingTheTabOwns()
    {
        var cut = Shared();

        cut.FindAll("[data-testid=discovery-shared-panel] table").Should().ContainSingle();
        cut.FindAll("[data-testid=discovery-technical], [data-testid=discovery-technical-table]").Should().BeEmpty();
        var paths = Rows(cut).Select(r => r.QuerySelector(".ed-path")!.GetAttribute("title")).ToList();
        paths.Should().OnlyHaveUniqueItems("each retained record appears once");
        paths.Should().Contain(["/_framework/HotChocolate.dll", "/appsettings.json", "/authentication/login-callback", "/api/ping", "/birknext-unknown-route-probe-1"]);
        paths.Should().NotContain("/api/roles", "page-correlated application traffic is the Pages tab's");
    }

    // §32 The summary is the real per-reason tally, above the table.
    [Fact]
    public void SummaryCountsMatchTheRows()
    {
        var cut = Shared();

        cut.Find("[data-testid=discovery-shared-total]").TextContent.Should().Be("93");
        var breakdown = cut.FindAll("[data-testid=discovery-shared-breakdown] li").Select(li => li.TextContent.Replace(" ", "")).ToList();
        breakdown.Should().Equal("Staticresource83", "Configuration6", "Authenticationcallback2", "Discoveryprobe1", "Uncorrelated(background/shared)1");
        // 93 includes the probe; the Overview's Shared / background count (92) excludes it, and the tab says why.
        cut.Find("[data-testid=discovery-shared-count-note]").TextContent.Should().Contain("(92)").And.Contain("discovery probes");
        var markup = cut.Find("[data-testid=discovery-shared-panel]").InnerHtml;
        markup.IndexOf("discovery-shared-summary", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("discovery-shared-table", StringComparison.Ordinal));
    }

    // §33 / §7 The auth filter names what it filters on, and says what it does not mean.
    [Fact]
    public void AuthFilterMeansAuthEvidenceObservedNotAVerifiedSession()
    {
        var cut = Shared();
        var filter = cut.Find("[data-testid=discovery-auth-evidence-filter]");
        filter.Closest("label")!.TextContent.Trim().Should().Be("Auth evidence only");
        filter.Closest("label")!.TextContent.Should().NotContain("Authenticated only");
        var help = cut.Find("#" + filter.GetAttribute("aria-describedby")).TextContent;
        help.Should().Contain("authentication evidence (a Bearer credential) was observed")
            .And.Contain("does not mean the browser session was independently verified");

        filter.Change(true);
        Rows(cut).Select(r => r.QuerySelector(".ed-path")!.GetAttribute("title")).Should().Equal("/api/ping");
    }

    // §34 / §9 Provenance filters independently; probes isolate on their own.
    [Fact]
    public void ProvenanceFiltersBrowserTrafficAndProbesIndependently()
    {
        var cut = Shared();
        var provenance = cut.Find("[data-testid=discovery-provenance-filter]");

        provenance.Change("probe");
        Rows(cut).Should().ContainSingle().Which.TextContent.Should().Contain("Discovery probe");

        cut.Find("[data-testid=discovery-provenance-filter]").Change("shared");
        Rows(cut).Should().NotBeEmpty().And.OnlyContain(r => r.TextContent.Contains("Browser observed traffic"));
    }

    // §35 / §36 / §37 Classifications stay as they are.
    [Fact]
    public void ConfigurationCallbackAndStaticKeepTheirClassification()
    {
        var cut = Shared();

        var config = Row(cut, "/appsettings.json").TextContent;
        config.Should().Contain("Configuration").And.Contain("Other").And.NotContain("REST");
        var callback = Row(cut, "/authentication/login-callback").TextContent;
        callback.Should().Contain("Auth").And.Contain("Authentication callback");
        Row(cut, "/_framework/HotChocolate.dll").TextContent.Should().Contain("Static").And.Contain("Static resource");
        Row(cut, LongPath).TextContent.Should().Contain("Static");
    }

    // §38 / §39 / §11 Classification and confidence are separate, readable fields; the row is not repeated.
    [Fact]
    public void ExpandedRowAddsClassificationConfidenceAndEvidenceWithoutRepeatingTheRow()
    {
        var cut = Shared();

        Row(cut, "/appsettings.json").QuerySelector(".ed-expand")!.Click();
        var detail = cut.Find("[data-testid=discovery-endpoint-detail]");
        cut.Find("[data-testid=discovery-detail-classification]").TextContent.Should().Be("Other HTTP");
        cut.Find("[data-testid=discovery-detail-confidence]").TextContent.Should().Be("Verified");
        cut.Find("[data-testid=discovery-detail-confidence]").GetAttribute("title").Should().Contain("direct signal").And.NotContainAny("pass", "fail", "Pass", "Fail");
        var labels = detail.QuerySelectorAll("dt").Select(d => d.TextContent.Trim()).ToList();
        labels.Should().Contain(["Full path", "Classification", "Confidence", "Origin", "First observed", "Authentication evidence"]);
        labels.Should().NotContain(["Category / reason", "Transport", "Provenance", "Calls", "Last status", "Last observed", "Host"],
            "each is already a column of this row");
        detail.TextContent.Should().NotContain("OtherHttp (Verified)");
        detail.TextContent.Should().NotContain("may include page navigation", "Configuration already explains this Other row");

        cut.Find(".ed-expand[aria-expanded=true]").Click();
        Row(cut, "/authentication/login-callback").QuerySelector(".ed-expand")!.Click();
        cut.Find("[data-testid=discovery-detail-classification]").TextContent.Should().Be("Authentication");
        cut.Find("[data-testid=discovery-detail-confidence]").TextContent.Should().Be("Candidate");
        cut.Find("[data-testid=discovery-detail-confidence]").GetAttribute("title").Should().Contain("inferred").And.Contain("not fully confirmed");
    }

    // §40 / §15 A long path stays inspectable: title on the cell, and in full in the detail.
    [Fact]
    public void LongPathsStayInspectable()
    {
        var cut = Shared();
        var row = Row(cut, LongPath);
        row.QuerySelector(".ed-path")!.GetAttribute("title").Should().Be(LongPath);

        var expand = row.QuerySelector(".ed-expand")!;
        expand.TagName.Should().Be("BUTTON");
        expand.GetAttribute("aria-label").Should().Contain(LongPath);
        expand.Click();
        cut.Find("[data-testid=discovery-detail-path]").TextContent.Should().Be(LongPath);
    }

    // §41 / §19 304 stays numeric and is explained as a cache revalidation, never an error.
    [Fact]
    public void NotModifiedIsExplainedAsCacheReuse()
    {
        var cut = Shared();
        var result = Row(cut, LongPath).QuerySelector("td[data-label=Result]")!;
        result.TextContent.Trim().Should().Be("304");
        result.GetAttribute("title").Should().Be("304 Not Modified — cached content was reused.");
        result.GetAttribute("title").Should().NotContainAny("error", "Error", "fail");
        Row(cut, "/appsettings.json").QuerySelector("td[data-label=Result]")!.HasAttribute("title").Should().BeFalse();
    }

    // §42 / §43 Contained table, wrapping filters, scoped headers, labelled filters.
    [Fact]
    public void TableAndFiltersStayContainedLabelledAndKeyboardReachable()
    {
        var cut = Shared();
        cut.Find("[data-testid=discovery-shared-table]").ParentElement!.ClassList.Should().Contain("ed-tablewrap");
        cut.Find("[data-testid=discovery-shared-panel] .ed-toolbar").Should().NotBeNull();
        cut.FindAll("[data-testid=discovery-shared-table] thead th").Should().OnlyContain(h => h.GetAttribute("scope") == "col");
        foreach (var testId in new[] { "discovery-search", "discovery-type-filter", "discovery-provenance-filter", "discovery-auth-evidence-filter" })
            cut.Find($"[data-testid={testId}]").Closest("label").Should().NotBeNull($"{testId} is labelled");
        cut.FindAll(".ed-expand").Should().OnlyContain(b => b.TagName == "BUTTON" && b.HasAttribute("aria-expanded"));
    }

    [Fact]
    public void EmptySharedSaysSo()
    {
        var cut = Shared([Ep("/api/roles", ObservedTrafficCategory.Rest, 1, page: "/admin/roles")]);
        cut.FindAll("[data-testid=discovery-shared-summary]").Should().BeEmpty();
        cut.Find("[data-testid=discovery-shared-panel] .ed-empty").TextContent.Should().Be("No shared, background or technical evidence retained yet.");
    }
}
