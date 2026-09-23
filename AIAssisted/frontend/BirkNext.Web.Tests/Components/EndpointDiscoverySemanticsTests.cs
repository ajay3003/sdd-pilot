using AngleSharp.Dom;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.EndpointDiscoveryTab;
using Settings = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Endpoint Discovery answers one question: what network communication have we observed? It is an evidence
/// surface, not a configuration page and not a review.
///
/// Four words are used deliberately here and these tests hold them apart. OBSERVED is runtime evidence.
/// CORRELATED is evidence associated across network, page and service data — association, never ownership.
/// CONFIGURED is what someone saved under Integrations, and is not evidence of anything running. And no
/// verdict — passed, compatible, no drift — belongs on this page at all; the reviews interpret the evidence.
/// </summary>
public sealed class EndpointDiscoverySemanticsTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";

    private readonly FrontendAnalysisSettingsService _settings = new();

    public EndpointDiscoverySemanticsTests()
    {
        // Every service is registered up front: bunit seals the provider as soon as one is resolved.
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ObservedNetworkEndpoint Ep(
        ObservedTrafficCategory cat, string path, string method = "GET", string? pagePath = "/barn/1",
        bool auth = true, GraphQlOperationType op = GraphQlOperationType.None, string host = "api-dev.bufetat.no") =>
        new()
        {
            Provenance = RequestProvenance.ApplicationTraffic, Category = cat, Scheme = "https", Host = host, Port = 443, Path = path, Method = method,
            AuthObserved = auth, LastStatus = 200, Source = EndpointDiscoverySource.AuthenticatedProxyTraffic,
            Confidence = ObservedEndpointConfidence.Verified, Count = 4,
            FirstObservedAt = DateTimeOffset.UtcNow, LastObservedAt = DateTimeOffset.UtcNow, OperationType = op,
            PageOrigin = pagePath is null ? null : Origin, PagePath = pagePath,
        };

    private static LocalHttpsProxyStatus Traffic(params ObservedNetworkEndpoint[] endpoints) => new()
    {
        ProxyListening = true, RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, SessionId = "s", State = LocalHttpsProxyState.Ready,
        AuthenticatedCredentialAvailable = true, ObservedNetworkEndpoints = endpoints,
    };

    private static LocalHttpsProxyStatus Stopped() => new() { State = LocalHttpsProxyState.Stopped };

    private static FrontendAnalysisProfile Dev(params IntegrationConfig[] integrations)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", TargetUrl = Origin + "/" };
        profile.Integrations.AddRange(integrations);
        return profile;
    }

    private static IntegrationConfig EventHub() => new()
    {
        Name = "M2LB Events", Type = IntegrationType.EventHub, Resource = "m2lb-cdc-qa.birk.dbo.person", Enabled = true,
    };

    private IRenderedComponent<Component> Tab(FrontendAnalysisProfile profile, LocalHttpsProxyStatus status) =>
        Render<Component>(p => p.Add(x => x.Profile, profile).Add(x => x.ProxyStatus, status));

    /// <summary>The whole mixed scenario: observed REST, GraphQL, auth and other traffic plus one configured integration.</summary>
    private IRenderedComponent<Component> MixedTab() => Tab(Dev(EventHub()), Traffic(
        Ep(ObservedTrafficCategory.Rest, "/api/barn"),
        Ep(ObservedTrafficCategory.GraphQl, "/gql", "POST", op: GraphQlOperationType.Query),
        Ep(ObservedTrafficCategory.Authentication, "/oauth2/token", "POST", host: "login.microsoftonline.com"),
        Ep(ObservedTrafficCategory.OtherHttp, "/assets/app.js", auth: false, host: "cdn.example.test")));

    private static string Text(IRenderedComponent<Component> cut, string id) => cut.Find($"[data-testid='{id}']").TextContent;
    private static bool Has(IRenderedComponent<Component> cut, string id) => cut.FindAll($"[data-testid='{id}']").Count > 0;

    // ── §37. The primary summary ─────────────────────────────────────────────────────────────

    // 1, 2, 3, 4, 5, 6.
    [Fact]
    public void TheSummaryLeadsWithSessionCorrelationEvidenceAndObservedCounts()
    {
        var cut = MixedTab();

        var order = cut.Find("[data-testid='discovery-overview']").QuerySelectorAll(".ed-ov-label")
            .Select(l => l.TextContent.Trim()).ToList();
        order.Should().Equal("Observation", "Authenticated traffic", "Page correlation", "Last traffic", "Freshness");

        Text(cut, "discovery-active").Should().Be("Active");
        Text(cut, "discovery-network").Should().Be("Observed");
        Text(cut, "discovery-auth-context").Should().Be("Bearer observed");
        Text(cut, "discovery-pages-count").Should().Be("1");
        Text(cut, "discovery-hosts-count").Should().Be("2", "technical resources do not count as backend hosts");

        // 6. "Saved application analyses" is no longer a primary runtime status.
        cut.Markup.Should().NotContain("Saved application analyses");
    }

    // §5. One vocabulary for the session; "live" is not a second status system.
    [Fact]
    public void TheSessionStateUsesOneVocabulary()
    {
        Text(Tab(Dev(), Stopped()), "discovery-active").Should().Be("Inactive");
        Text(MixedTab(), "discovery-active").Should().Be("Active");

        Tab(Dev(), Stopped()).Markup.Should().NotContain("Discovery session (live)");
    }

    // §6, §25. Authenticated context is reported here and owned by Authentication.
    [Fact]
    public void AuthenticatedContextIsReportedOnceWithOneActionAndNoSetupInstructions()
    {
        var cut = Tab(Dev(), Stopped());

        Text(cut, "discovery-auth-context").Should().Be("Unavailable");
        Has(cut, "discovery-open-authentication").Should().BeFalse("no host is wired up in this render");

        // None of the Authentication tab.s setup procedure leaks into this surface.
        cut.Markup.Should().NotContainAny("Start the Local HTTPS Proxy from", "Trust the certificate", "Step 1", "Step 4");
    }

    // ── §38. Observed versus configured ──────────────────────────────────────────────────────

    // 7, 8. The view is named for what it holds.
    [Fact]
    public void TheBackendViewIsNamedConfiguredNotObserved()
    {
        var cut = MixedTab();
        Has(cut, "discovery-nav-integrations").Should().BeFalse();
        cut.Find("[data-testid='discovery-open-integrations']").Should().NotBeNull();
    }

    // 9. A zero here is about configuration, because that is what the view holds.
    [Fact]
    public void AnEmptyConfiguredViewTalksAboutConfigurationNotObservation()
    {
        var cut = Tab(Dev(), Traffic());
        Has(cut, "discovery-backend-integrations").Should().BeFalse();
        Text(cut, "discovery-configured-pointer").Should().Contain("managed in Integrations");
    }

    // §9, §11. A configured integration is never rendered as observed traffic.
    [Fact]
    public void AConfiguredIntegrationNeverAppearsInTheObservedTrafficTable()
    {
        var cut = MixedTab();
        Text(cut, "discovery-overview-table").Should().NotContain("M2LB Events");
        Text(cut, "discovery-configured-pointer").Should().Contain("managed in Integrations");
        Has(cut, "discovery-backend-integrations").Should().BeFalse();
    }

    // 10, 11. Configured integrations stay owned by the Integrations tab, and the link points there.
    [Fact]
    public void TheConfiguredViewLinksToTheTabThatOwnsThem()
    {
        var cut = MixedTab();
        cut.Find("[data-testid='discovery-open-integrations']").GetAttribute("href").Should().Be(EndpointDiscoveryPresentation.IntegrationsHref);
        Has(cut, "discovery-backend-integrations").Should().BeFalse();
    }

    // ── §39. The communication table ─────────────────────────────────────────────────────────

    // 12, 13, 14, 15, 16, 17, 18, 19.
    [Fact]
    public void TheCommunicationTableRemainsPrimaryAndKeepsItsEvidenceColumns()
    {
        var cut = MixedTab();

        var table = cut.Find("[data-testid='discovery-overview-table']");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Service / Host", "Type", "Pages", "Auth", "Calls", "Last seen", "Transport", "Provenance");

        var body = table.TextContent;
        body.Should().Contain("api-dev.bufetat.no").And.Contain("login.microsoftonline.com").And.NotContain("cdn.example.test");
        // 17. Source stays evidence provenance.
        table.QuerySelectorAll("tbody .ed-source").Should().OnlyContain(s => s.TextContent.Trim() == "Proxy");
        // 18, 19. Counts and timestamps survive.
        body.Should().Contain("4");
        table.QuerySelectorAll("tbody tr").Should().NotBeEmpty();
        table.QuerySelectorAll("tbody td.ed-mono").Select(c => c.TextContent).Should().NotContain("0");
    }

    // §14. An observed bearer token is an observation about a request, not a policy statement.
    [Fact]
    public void ObservedAuthIsAMechanismNotASignInPolicy()
    {
        var cut = MixedTab();

        var table = Text(cut, "discovery-overview-table");
        table.Should().Contain("Bearer");
        table.Should().NotContainAny("Authentication required", "Sign-in required", "Requires authentication");
        EndpointDiscoveryPresentation.ObservedAuthLabel(false).Should().Be("—", "an absent observation is a dash, not a claim");
    }

    // ── §40. Copy semantics ──────────────────────────────────────────────────────────────────

    // 20, 21, 23.
    [Fact]
    public void ThePageUsesEvidenceWordsAndRendersNoReviewVerdict()
    {
        var cut = MixedTab();

        // 23. No interpretation: the reviews do that.
        foreach (var verdict in new[] { "Passed", "Failed", "Compatible", "No drift", "Healthy", "Secure", "Compliant" })
            cut.Markup.Should().NotContain(verdict);

        // 21. Correlation is association, not verified ownership. The hint says so in as many words, so the
        // claim to look for is the affirmative one, not the denial that contains the same substring.
        cut.Markup.Should().NotContainAny("relationship verified", "ownership verified", "is a verified integration");
        EndpointDiscoveryPresentation.CorrelationHint.Should().Contain("not verified integration ownership");

        // 20. The observed surface never calls its rows configured.
        Text(cut, "discovery-overview-table").Should().NotContain("Configuration discovery");
    }

    // §3, §42. 28, 29, 30.
    [Fact]
    public void TheExplanatoryStripIsNeutralAndDescribesObservedCommunication()
    {
        EndpointDiscoveryPresentation.Introduction.Should().Be("Observed network communication for this Target Environment.");
        EndpointDiscoveryPresentation.Introduction.Should().NotContain("configured");
        EndpointDiscoveryPresentation.IntroductionDetail.Should().Contain("observed evidence");

        var settings = RenderSettings();
        var strip = settings.Find("[data-testid='discovery-introduction']");
        strip.TextContent.Should().Contain("Observed network communication");
        // 29. Informational prose is not announced or styled as a warning.
        strip.ClassList.Should().Contain("fa-info-note").And.NotContain("fa-section-note");
        strip.HasAttribute("role").Should().BeFalse();
    }

    // ── §41. Action placement ────────────────────────────────────────────────────────────────

    // 24, 25.
    [Fact]
    public void DeletingEveryAnalysisIsSecondaryManagementNotNavigation()
    {
        var cut = MixedTab();

        // 24. It is not among the view-navigation controls.
        cut.Find(".ed-nav").QuerySelectorAll("[data-testid='discovery-delete-all']").Should().BeEmpty();
        cut.Find("[data-testid='discovery-manage']").QuerySelector("[data-testid='discovery-delete-all']").Should().NotBeNull();

        // 25. And it still works, behind its confirmation.
        cut.Find("[data-testid='discovery-delete-all']").Click();
        cut.Find("[data-testid='discovery-delete-all-confirm']").Should().NotBeNull();
    }

    // 26, 27. Resetting a profile belongs to General, and no other pane offers it.
    [Fact]
    public void ResettingTheProfileIsNotAnEndpointDiscoveryAction()
    {
        MixedTab().Markup.Should().NotContain("Reset Profile");

        var settings = RenderSettings();
        settings.FindAll("[data-testid=profile-advanced]").Should().BeEmpty("this tab owns no destructive action");
        settings.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Reset Profile");

        // And it is still there, on the tab that owns it.
        settings.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "General").Click();
        var reset = settings.FindAll("button").Single(b => b.TextContent.Trim() == "Reset Profile");
        reset.Closest("[data-testid='endpoint-discovery']").Should().BeNull();
        reset.Closest(".fa-profile-reset-zone").Should().NotBeNull();
    }

    // ── §43. The semantic separations ────────────────────────────────────────────────────────

    // 31, 32, 33, 34, 35.
    [Fact]
    public void EvidenceIsNeverConfigurationOwnershipOrPolicy()
    {
        var cut = MixedTab();

        // 31. Configured integrations are excluded from the observed evidence.
        EndpointDiscoveryPresentation.ObservedHostCount(
            [Ep(ObservedTrafficCategory.Rest, "/api/barn")]).Should().Be(1, "counted from observations only");

        // 33. Correlation availability follows the capture session, not any integration conclusion.
        EndpointDiscoveryPresentation.CorrelationLabel(Stopped()).Should().Be("Unavailable");
        EndpointDiscoveryPresentation.CorrelationLabel(Traffic()).Should().Be("Awaiting page evidence");

        // 34. Authenticated context is about capture, and says nothing about the application's policy.
        EndpointDiscoveryPresentation.AuthenticatedContextLabel(Stopped()).Should().Be("Unavailable");
        cut.Markup.Should().NotContain("Sign-in required");

        // 35. Page attribution here is network traffic, not DOM or browser evidence.
        foreach (var browser in new[] { "WCAG", "Browser Companion", "DOM evidence", "Core Web Vitals" })
            cut.Markup.Should().NotContain(browser);
    }

    // ── §35. Accessibility of the restructured tab ───────────────────────────────────────────

    [Fact]
    public void TabsAndTablesKeepTheirSemantics()
    {
        var cut = MixedTab();

        cut.Find(".ed-nav").GetAttribute("role").Should().Be("tablist");
        cut.Find("[data-testid='discovery-overview-table']").QuerySelectorAll("thead th").Should().NotBeEmpty();
        // Named for what it deletes: all retained discovery evidence, not only "analyses".
        cut.Find("[data-testid='discovery-delete-all']").TextContent.Trim().Should().Be("Delete all retained evidence…");
        // Every summary value is text; none of them depends on colour alone.
        cut.Find("[data-testid='discovery-overview']").QuerySelectorAll(".ed-ov-value")
            .Should().OnlyContain(v => v.TextContent.Trim().Length > 0);
    }

    // ── Host harness: the tab inside the settings component, for the strip and profile-level actions ──

    private IRenderedComponent<Settings> RenderSettings()
    {
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"dev","profiles":[
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"{{Origin}}/"}
        ]}
        """);

        var cut = Render<Settings>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Endpoint Discovery").Click();
        return cut;
    }
}
