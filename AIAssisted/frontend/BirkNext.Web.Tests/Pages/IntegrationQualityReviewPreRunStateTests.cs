using AngleSharp.Dom;
using BirkNext.LocalHttpsProxy;
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
/// Integration Quality Review pre-run: configured scope → evidence availability → the exact blocker or limitation → Run.
/// The one Run prerequisite is an ENABLED integration; every review domain is judged by its own prerequisite, using the
/// same rules the backend review applies.
/// </summary>
public sealed class IntegrationQualityReviewPreRunStateTests : BunitContext
{
    private const string ApiHost = "api.example.test";
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private const string IntegrationsHref = "/admin/system-settings?section=target-environments&tab=integrations&profile=dev";

    private readonly Mock<IIntegrationQualityReviewService> _review = new();
    private readonly RuntimeReviewSessionService _session = new();
    private readonly Mock<IEndpointDiscoveryService> _discovery = new();

    private static IntegrationConfig Rest(string id = "rest-1", bool enabled = true, bool contract = false, bool relationship = true) => new()
    {
        Id = id, Name = "Orders API " + id, Type = IntegrationType.REST, Enabled = enabled,
        Endpoint = $"https://{ApiHost}/api/orders",
        LogicalProducerService = relationship ? "Orders" : null, LogicalConsumerService = relationship ? "Frontend" : null,
        ContractName = contract ? "Orders" : null,
        ContractSourceType = contract ? ContractSourceType.OpenApi : ContractSourceType.Unknown,
        ContractSourceLocation = contract ? $"https://{ApiHost}/swagger/v1/swagger.json" : null,
    };

    private static IntegrationConfig EventHub(bool schema = false) => new()
    {
        Id = "eh-1", Name = "Person CDC", Type = IntegrationType.EventHub, Enabled = true,
        Endpoint = "ns.servicebus.windows.net", Resource = "person", Consumer = "$Default",
        LogicalProducerService = "BiRK", LogicalConsumerService = "Adapter",
        ContractName = schema ? "PersonChanged" : null,
        ContractSourceType = schema ? ContractSourceType.Auto : ContractSourceType.Unknown,
    };

    private static ObservedNetworkEndpoint Observed(bool timed) => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Source = EndpointDiscoverySource.AuthenticatedProxyTraffic,
        Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = ApiHost, Port = 443, Path = "/api/orders/42", Method = "GET",
        AuthObserved = true, LastStatus = 200, Count = 2, FirstObservedAt = T0, LastObservedAt = T0,
        Confidence = ObservedEndpointConfidence.Verified, PageOrigin = "https://app.example.test", PagePath = "/",
        Samples = timed ? [new ObservedRequestSample(T0, 120, 200, 512), new ObservedRequestSample(T0.AddSeconds(5), 140, 200, 512)] : [],
    };

    private IRenderedComponent<IntegrationQualityReview> Landing(IntegrationConfig[] integrations, ObservedNetworkEndpoint[]? observed = null)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://app.example.test/" };
        profile.Integrations.AddRange(integrations);
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = profile.TargetUrl, Integrations = integrations.ToList(),
        });
        _discovery.Setup(d => d.LoadAsync(It.IsAny<IJSRuntime>())).Returns(Task.CompletedTask);
        _discovery.Setup(d => d.GetSnapshot("dev")).Returns(new EndpointDiscoverySnapshot { Shared = (observed ?? []).ToList() });
        _review.Setup(r => r.AnalyzeAsync(It.IsAny<IntegrationQualityRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new IntegrationQualityReport { EnvironmentName = "Dev", GeneratedAt = T0.UtcDateTime }, (string?)null));

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(_review.Object);
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(_session);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(_discovery.Object);
        var page = Render<IntegrationQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=iqr-decide]"));
        return page;
    }

    private static IElement Run(IRenderedComponent<IntegrationQualityReview> page) => page.Find("[data-testid=iqr-run-review]");
    private static IElement Domain(IRenderedComponent<IntegrationQualityReview> page, string key) => page.Find($"[data-testid=iqr-domain][data-domain='{key}']");
    private static string State(IRenderedComponent<IntegrationQualityReview> page, string key) => Domain(page, key).QuerySelector("[data-testid=iqr-domain-state]")!.TextContent.Trim();
    private static string? Limitation(IRenderedComponent<IntegrationQualityReview> page, string key) => Domain(page, key).QuerySelector("[data-testid=iqr-domain-limitation]")?.TextContent;

    // 48. Nothing configured.
    [Fact]
    public void NothingConfigured_BlocksWithOneActionTowardIntegrations_AndNoDomainIsExcluded()
    {
        var page = Landing([]);

        page.Find("[data-testid=iqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("#iqr-readiness-heading").TextContent.Should().Be("Review cannot start");
        page.Find("[data-testid=iqr-readiness-message]").TextContent.Should().Be(
            "No integrations are configured for this Target Environment. Configure one and enable it for review.");
        Run(page).HasAttribute("disabled").Should().BeTrue();
        Run(page).GetAttribute("aria-describedby").Should().Be("iqr-readiness-message");

        var cta = page.Find("[data-testid=iqr-go-configure]");
        cta.TagName.Should().Be("A");
        cta.TextContent.Should().Be("Configure integrations");
        cta.GetAttribute("href").Should().Be(IntegrationsHref);
        page.FindAll("[data-testid=iqr-decide] a").Count(a => a.GetAttribute("href")!.Contains("tab=integrations"))
            .Should().Be(1, "one action, not competing ones");

        // Each card owns one fact.
        page.Find("[data-testid=iqr-integration-count]").TextContent.Should().Be("0");
        page.Find("[data-testid=iqr-scope-headline]").TextContent.Should().Be("No integrations enabled for review");
        page.FindAll("[data-testid=iqr-scope-enabled]").Should().BeEmpty();

        // Runtime evidence is not a second failure.
        page.Find("[data-testid=iqr-runtime-headline]").TextContent.Should().Be("Not assessed yet");
        page.Find("[data-testid=iqr-runtime-missing]").TextContent.Should().Be("No enabled integration to observe yet.");
        page.FindAll("[data-testid=iqr-runtime-messaging]").Should().BeEmpty();
        page.Find("[data-testid=iqr-decide]").TextContent.Should().NotContain("telemetry unavailable");

        // Neutral empty-state domains: no badges, least of all six "Not included".
        page.Find("#iqr-domains-heading").TextContent.Should().Be("What Integration Quality Review can assess");
        page.FindAll("[data-testid=iqr-domain]").Should().HaveCount(6).And.OnlyContain(d => d.GetAttribute("data-state") == "AwaitingIntegration");
        page.FindAll("[data-testid=iqr-domain-state]").Should().BeEmpty();
        page.Find("[data-testid=iqr-domains]").TextContent.Should().NotContain("Not included");
    }

    // 49. Configured, none enabled.
    [Fact]
    public void ConfiguredButNoneEnabled_SaysSo_AndNeverClaimsNothingIsConfigured()
    {
        var page = Landing([Rest("a", enabled: false), Rest("b", enabled: false), Rest("c", enabled: false)]);

        page.Find("[data-testid=iqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("[data-testid=iqr-readiness-message]").TextContent.Should().Be("None of the 3 configured integrations is enabled for review.");
        page.Find("[data-testid=iqr-integration-count]").TextContent.Should().Be("3");
        page.Find("[data-testid=iqr-scope-enabled]").TextContent.Should().Be("3 configured, none enabled");
        page.Find("[data-testid=iqr-decide]").TextContent.Should().NotContain("No integrations are configured");
        Run(page).HasAttribute("disabled").Should().BeTrue();
    }

    // 50, 57. Enabled, no contract, no runtime — Run starts; each domain says what it lacks.
    [Fact]
    public void AnEnabledIntegrationIsTheOnlyPrerequisite_MissingEvidenceLimitsButNeverBlocks()
    {
        var page = Landing([Rest()]);

        Run(page).HasAttribute("disabled").Should().BeFalse("contracts and runtime evidence are not prerequisites");
        Run(page).HasAttribute("aria-describedby").Should().BeFalse();
        page.Find("[data-testid=iqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited");
        page.Find("#iqr-readiness-heading").TextContent.Should().Be("Review can run with limitations");
        page.Find("[data-testid=iqr-readiness-action]").GetAttribute("href").Should().Be(IntegrationsHref);

        State(page, "relationships").Should().Be("Included", "producer and consumer are configuration, not runtime evidence");
        State(page, "contracts").Should().Be("Limited");
        Limitation(page, "contracts").Should().Be("No contract source is configured. Transport configuration is not a schema.");
        State(page, "compatibility").Should().Be("Not assessed");
        Limitation(page, "compatibility").Should().StartWith("Insufficient contract evidence");
        State(page, "runtime").Should().Be("Not assessed");
        State(page, "performance").Should().Be("Not assessed");
        Limitation(page, "performance").Should().StartWith("No timing evidence");
        State(page, "drift").Should().Be("Unavailable");
        Limitation(page, "drift").Should().Contain("REST and GraphQL drift is not assessed in this build");

        page.FindAll("[data-testid=iqr-domain-state]").Select(s => s.TextContent).Should().NotContain("Not included");
        page.Find("[data-testid=iqr-domains]").TextContent.Should().NotContainAny("Failed", "Pass", "0 ms", "No drift detected");
    }

    // 51. Contract evidence, no baseline known.
    [Fact]
    public void WithAContractSource_CompatibilityIsEligible_AndDriftNeverReadsNoDrift()
    {
        var page = Landing([EventHub(schema: true)]);

        State(page, "contracts").Should().Be("Included");
        State(page, "compatibility").Should().Be("Included");
        Limitation(page, "compatibility").Should().Contain("A configured contract is not a compatibility result");
        State(page, "drift").Should().Be("Limited");
        Limitation(page, "drift").Should().Contain("a first review records the baseline, which is not the same as no drift");
        Domain(page, "drift").TextContent.Should().NotContain("No drift detected");
        State(page, "runtime").Should().Be("Unavailable", "messaging runtime evidence is not collected in this build");
        page.Find("[data-testid=iqr-runtime-messaging]").TextContent.Should().Be("Messaging runtime evidence is not collected in this build (1 messaging integration)");
    }

    // 52. Runtime observed, no timing samples.
    [Fact]
    public void ObservedTrafficWithoutTimingNeverBecomesZeroMilliseconds()
    {
        var page = Landing([Rest()], [Observed(timed: false)]);

        page.Find("[data-testid=iqr-runtime-headline]").TextContent.Should().Be("1 of 1 observed");
        State(page, "runtime").Should().Be("Included");
        State(page, "performance").Should().Be("Not assessed");
        Limitation(page, "performance").Should().Be("Runtime traffic was observed, but no timing samples were recorded.");
        page.Markup.Should().NotContain("0 ms");
    }

    // 53. Full HTTP evidence — each domain reaches its own state; drift stays honest about this build.
    [Fact]
    public void FullHttpEvidence_EachDomainUsesItsOwnPrerequisite()
    {
        var page = Landing([Rest(contract: true)], [Observed(timed: true)]);

        page.Find("[data-testid=iqr-readiness]").GetAttribute("data-readiness").Should().Be("Ready");
        foreach (var key in new[] { "relationships", "runtime", "contracts", "compatibility", "performance" })
            State(page, key).Should().Be("Included", key);
        State(page, "drift").Should().Be("Unavailable", "this build resolves drift for messaging schemas only");
        page.Find("[data-testid=iqr-domains]").TextContent.Should().NotContainAny("Healthy", "Compatible.", "Fully compatible");
    }

    // Runtime correlation matches the backend: proxy-observed traffic on the same origin, under the path.
    [Fact]
    public void RuntimeEvidenceUsesTheBackendsCorrelationRule()
    {
        var other = Observed(timed: true) with { };
        var sameHostOtherPath = new ObservedNetworkEndpoint
        {
            Provenance = RequestProvenance.ApplicationTraffic, Source = EndpointDiscoverySource.AuthenticatedProxyTraffic,
            Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = ApiHost, Port = 443, Path = "/api/customers", Method = "GET",
            Confidence = ObservedEndpointConfidence.Verified, FirstObservedAt = T0, LastObservedAt = T0,
        };
        var configurationOnly = new ObservedNetworkEndpoint
        {
            Provenance = RequestProvenance.ApplicationTraffic, Source = EndpointDiscoverySource.PublicConfiguration,
            Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = ApiHost, Port = 443, Path = "/api/orders", Method = "GET",
            Confidence = ObservedEndpointConfidence.Verified, FirstObservedAt = T0, LastObservedAt = T0,
        };

        IntegrationReviewPresentation.Evidence([Rest()], [sameHostOtherPath, configurationOnly]).Observed
            .Should().Be(0, "a host match is not enough, and configuration-derived entries prove nothing ran");
        IntegrationReviewPresentation.Evidence([Rest()], [other]).Observed.Should().Be(1);
    }

    // A direct visit loads the persisted discovery store itself instead of relying on another page having done so.
    [Fact]
    public void TheDiscoveryStoreIsLoadedBeforeEvidenceIsCounted()
    {
        Landing([Rest()], [Observed(timed: true)]);

        _discovery.Verify(d => d.LoadAsync(It.IsAny<IJSRuntime>()), Times.Once);
    }

    // 55. The action only navigates.
    [Fact]
    public void ConfigureIntegrationsLinksToTheSameEnvironmentsIntegrationsTab()
    {
        IntegrationReviewPresentation.IntegrationsHref("dev").Should().Be(IntegrationsHref);
        IntegrationReviewPresentation.IntegrationsHref("qa env").Should().EndWith("&profile=qa%20env");

        var page = Landing([]);
        var cta = page.Find("[data-testid=iqr-go-configure]");
        cta.HasAttribute("blazor:onclick").Should().BeFalse("a link, not a control with a side effect");
        page.Find("[data-testid=iqr-manage-scope]").GetAttribute("href").Should().Be(IntegrationsHref, "one destination for both links");
        page.Find("[data-testid=iqr-manage-scope]").TextContent.Should().Be("Open Integrations configuration");
        _review.Verify(r => r.AnalyzeAsync(It.IsAny<IntegrationQualityRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // 56. Details start collapsed and keep the reader's choice across re-renders.
    [Fact]
    public void ReviewDetailsStartCollapsed_AndAnExpansionSurvivesARerender()
    {
        var page = Landing([Rest()]);

        foreach (var id in new[] { "iqr-scope-disclosure", "iqr-runtime-disclosure" })
        {
            page.Find($"[data-testid={id}-toggle]").GetAttribute("aria-expanded").Should().Be("false", id);
            page.Find($"[data-testid={id}-body]").HasAttribute("hidden").Should().BeTrue(id);
        }

        page.Find("[data-testid=iqr-runtime-disclosure-toggle]").Click();
        page.Find("[data-testid=iqr-target-details-toggle]").Click();   // an unrelated interaction re-renders the page
        page.Render();

        page.Find("[data-testid=iqr-runtime-disclosure-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.Find("[data-testid=iqr-runtime-disclosure-toggle]").TextContent.Should().Contain("No runtime evidence observed");
    }

    // 27–30. Configured scope and evidence availability are separate lists; unobserved is never a zero.
    [Fact]
    public void RuntimeObservationsSeparateConfiguredScopeFromEvidence()
    {
        var page = Landing([Rest()]);
        page.Find("[data-testid=iqr-runtime-disclosure-toggle]").Click();

        page.Find("[data-testid=iqr-evidence-configured]").TextContent.Should().Be("1");
        page.Find("[data-testid=iqr-evidence-enabled]").TextContent.Should().Be("1");
        page.Find("[data-testid=iqr-evidence-contracts]").TextContent.Should().Be("None configured");
        page.Find("[data-testid=iqr-evidence-runtime]").TextContent.Should().Be("Not observed");
        page.Find("[data-testid=iqr-evidence-timing]").TextContent.Should().Be("Not observed");
        page.Find("[data-testid=iqr-evidence-history]").TextContent.Should().Be("Resolved when the review runs");
        page.Markup.Should().NotContain("Depends on environment activity");
    }

    [Fact]
    public void WithNothingEnabled_TheRuntimeDetailsSayThereIsNothingToObserve()
    {
        var page = Landing([]);

        page.Find("[data-testid=iqr-runtime-disclosure-toggle]").TextContent.Should().Contain("No enabled integrations to observe");
        page.Find("[data-testid=iqr-evidence-empty]").TextContent.Should().Contain("once an integration is enabled");
        page.FindAll("[data-testid=iqr-evidence-contracts]").Should().BeEmpty("a contract count of 0 with no scope is not a problem to show");
    }

    // The run receives the contract and relationship metadata the domain cards promise.
    [Fact]
    public void TheReviewRequestCarriesContractAndRelationshipMetadata()
    {
        var page = Landing([Rest(contract: true)]);

        Run(page).Click();

        _review.Verify(r => r.AnalyzeAsync(It.Is<IntegrationQualityRequest>(q =>
            q.Integrations.Single().ContractSourceType == ContractSourceType.OpenApi
            && q.Integrations.Single().ContractSourceLocation == $"https://{ApiHost}/swagger/v1/swagger.json"
            && q.Integrations.Single().ContractName == "Orders"
            && q.Integrations.Single().LogicalProducerService == "Orders"
            && q.Integrations.Single().LogicalConsumerService == "Frontend"), It.IsAny<CancellationToken>()), Times.Once);
    }
}

/// <summary>Following "Configure integrations" opens the same environment's Integrations tab without changing anything.</summary>
public sealed class IntegrationQualityReviewIntegrationsDeepLinkTests : BunitContext
{
    [Fact]
    public void TheLinkOpensIntegrationsForTheEnvironmentWithoutActivatingOrSavingIt()
    {
        var settings = new FrontendAnalysisSettingsService();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[
              {"id":"dev","name":"Dev target","environmentType":"Development","targetUrl":"https://dev.example.test"},
              {"id":"qa","name":"QA target","environmentType":"QA","targetUrl":"https://qa.example.test"}]}
            """);

        var cut = Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p => p.Add(c => c.InitialTab, "integrations").Add(c => c.InitialProfileId, "qa"));

        cut.Find("#target-tab-integrations").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("#target-tab-general").GetAttribute("aria-selected").Should().Be("false");
        cut.Markup.Should().Contain("QA target");
        settings.Settings.ActiveProfileId.Should().Be("dev", "navigation selects the environment for viewing; it does not activate it");
        JSInterop.Invocations.Where(i => i.Identifier.Contains("setItem", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
    }
}
