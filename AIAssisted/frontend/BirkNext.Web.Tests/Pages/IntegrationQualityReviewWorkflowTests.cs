using AngleSharp.Dom;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Integration Quality Review as a review workflow. Before a run the page answers whether to run:
/// target, scope, runtime evidence, readiness, run action, and what will be reviewed. After a run
/// the findings lead and the inventory moves below them.
///
/// Three separations these tests police. CONFIGURED is not OBSERVED — a saved integration is intent,
/// and only a runtime observation is evidence that it ran. Transport metadata is not a schema.
/// And an absent value is unknown, never zero.
/// </summary>
public sealed class IntegrationQualityReviewWorkflowTests : BunitContext
{
    private readonly Mock<IIntegrationQualityReviewService> _review = new();
    private readonly Mock<IFrontendAnalysisContextFactory> _contextFactory = new();

    private static IntegrationConfig Rest(bool enabled = true) => new()
    {
        Id = "rest-1", Name = "Orders API", Type = IntegrationType.REST, Enabled = enabled,
        Endpoint = "https://application-qa.example.test/api/orders",
        LogicalProducerService = "Orders", LogicalConsumerService = "Frontend",
        ConfigurationSource = IntegrationConfigurationSource.EndpointDiscovery,
        ResourceKind = IntegrationResourceKind.RestEndpoint,
    };

    private static IntegrationConfig EventHub(bool enabled = true) => new()
    {
        Id = "eh-1", Name = "Person CDC", Type = IntegrationType.EventHub, Enabled = enabled,
        Resource = "m2lb-cdc-qa.birk.dbo.person", Consumer = "$Default",
        LogicalProducerService = "BiRK / Debezium", LogicalConsumerService = "PersonBiRKAdapter",
        ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
        ResourceKind = IntegrationResourceKind.EventHub,
    };

    /// <summary>A queue whose producing service the audit never established.</summary>
    private static IntegrationConfig ServiceBus() => new()
    {
        Id = "sb-1", Name = "Leselogg", Type = IntegrationType.ServiceBus, Enabled = true,
        Endpoint = "ns.servicebus.windows.net", Resource = "leselogg",
        LogicalConsumerService = "Revisjon",
        ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
        ResourceKind = IntegrationResourceKind.ServiceBusQueue,
    };

    private IRenderedComponent<IntegrationQualityReview> Landing(params IntegrationConfig[] integrations)
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "qa", Name = "M2LB QA", EnvironmentType = FrontendEnvironmentType.QA,
            TargetUrl = "https://application-qa.example.test/",
        };
        profile.Integrations.AddRange(integrations);

        _contextFactory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = profile.TargetUrl, Integrations = integrations.ToList(),
        });

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_contextFactory.Object);
        Services.AddSingleton(_review.Object);
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IEndpointDiscoveryService>());

        var page = Render<IntegrationQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=iqr-decide]"));
        return page;
    }

    private static IReadOnlyList<string> PreRunOrder(IRenderedComponent<IntegrationQualityReview> page) =>
        page.FindAll("[data-testid=iqr-target], [data-testid=iqr-scope], [data-testid=iqr-evidence], [data-testid=iqr-readiness], [data-testid=iqr-run-row], [data-testid=iqr-domains], [data-testid=iqr-details]")
            .Select(e => e.GetAttribute("data-testid")!).ToList();

    private static IElement Domain(IRenderedComponent<IntegrationQualityReview> page, string key) =>
        page.Find($"[data-testid=iqr-domain][data-domain='{key}']");

    private static string DomainState(IRenderedComponent<IntegrationQualityReview> page, string key) =>
        Domain(page, key).QuerySelector("[data-testid=iqr-domain-state]")!.TextContent.Trim();

    /// <summary>Text the reader can actually see: a collapsed disclosure body is hidden and does not count.</summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var hidden in clone.QuerySelectorAll("[hidden]").ToList()) hidden.Remove();
        return clone.TextContent;
    }

    private static bool Collapsed(IRenderedComponent<IntegrationQualityReview> page, string id) =>
        page.Find($"[data-testid={id}-toggle]").GetAttribute("aria-expanded") == "false"
        && page.Find($"[data-testid={id}-body]").HasAttribute("hidden");

    // ── §3, §19. The pre-run decision area ───────────────────────────────────────────────────

    [Fact]
    public void PreRunReadsTargetThenScopeThenRuntimeEvidenceThenReadinessThenRun()
    {
        var page = Landing(Rest(), EventHub());

        PreRunOrder(page).Should().Equal(
            "iqr-target", "iqr-scope", "iqr-evidence", "iqr-readiness", "iqr-run-row", "iqr-domains", "iqr-details");

        var run = page.Find("[data-testid=iqr-run-row] [data-testid=iqr-run-review]");
        run.TextContent.Trim().Should().Be("Run Integration Quality Review");
        run.Closest(".disclosure-body").Should().BeNull("Run is reachable without expanding anything");
    }

    // §4. The Target card no longer repeats the review state.
    [Fact]
    public void TargetCardCarriesNoReviewStatus()
    {
        var page = Landing(Rest());

        page.FindAll("[data-testid=iqr-readiness-status]").Should().BeEmpty();
        page.FindAll("[data-testid=iqr-readiness]").Should().ContainSingle();
        VisibleText(page.Find("[data-testid=iqr-target]")).Should().NotContainAny("Ready to review", "Ready with limitations");
        // One way to the Target Environment from the decision area, not three.
        page.Find("[data-testid=iqr-configure-target]").TextContent.Should().Be("Open Target Environment");
    }

    // §5. Scope counts come from the configured integrations.
    [Fact]
    public void ReviewScopeCountsTheConfiguredIntegrationsByTransport()
    {
        var page = Landing(Rest(), EventHub(), ServiceBus(), EventHub(enabled: false));

        page.Find("[data-testid=iqr-scope-headline]").TextContent.Should().Be("4 integrations configured");
        page.Find("[data-testid=iqr-scope-enabled]").TextContent.Should().Be("3 enabled for review");
        page.Find("[data-testid=iqr-scope-transports]").TextContent
            .Should().Contain("2 Event Hub").And.Contain("1 REST").And.Contain("1 Service Bus");
    }

    // ── §7, §8, §9. Configured is not observed ───────────────────────────────────────────────

    [Fact]
    public void ConfiguredIntegrationsAreNeverCountedAsRuntimeEvidence()
    {
        var page = Landing(Rest(), EventHub(), ServiceBus());

        // Three enabled integrations are configured; none was observed running.
        page.Find("[data-testid=iqr-runtime-headline]").TextContent.Should().Be("No runtime evidence observed");
        page.Find("[data-testid=iqr-runtime-missing]").TextContent.Should().Be("3 enabled integrations have no runtime evidence");
        page.Find("[data-testid=iqr-runtime-messaging]").TextContent.Should().Be("Messaging telemetry unavailable");

        var evidence = VisibleText(page.Find("[data-testid=iqr-evidence]"));
        evidence.Should().NotContainAny("Connected", "Active", "Observed running");
    }

    // §8. Reachability and health are not runtime evidence, so the placeholder that implied them is gone.
    [Fact]
    public void RuntimeEvidenceIsNotInferredFromReachabilityOrConfiguration()
    {
        var page = Landing(Rest());

        VisibleText(page.Find("[data-testid=iqr-decide]")).Should().NotContain("Depends on environment activity");
        IntegrationReviewPresentation
            .RuntimeEvidence([Rest()], observations: null).Observed
            .Should().Be(0, "a configured endpoint is intent, not evidence");
    }

    // ── §10, §11. Readiness ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadinessIsStatedOnceWithItsLimitationsCollapsed()
    {
        var page = Landing(Rest(), EventHub());

        page.Find("[data-testid=iqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited");
        page.Find("#iqr-readiness-heading").TextContent.Should().Be("Ready with limitations");
        Collapsed(page, "iqr-readiness-limitations").Should().BeTrue();
        page.Find("[data-testid=iqr-run-review]").HasAttribute("disabled").Should().BeFalse();

        page.Find("[data-testid=iqr-readiness-limitations-toggle]").Click();
        var body = page.Find("[data-testid=iqr-readiness-limitations-body]");
        body.TextContent.Should().Contain("no runtime evidence").And.Contain("Messaging runtime telemetry is unavailable");
        // None of it reads as a failure.
        body.TextContent.Should().NotContainAny("failed", "error", "broken");
    }

    [Fact]
    public void NoEnabledIntegrationBlocksTheReviewAndKeepsItsReasonVisible()
    {
        var page = Landing(EventHub(enabled: false));

        page.Find("[data-testid=iqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("[data-testid=iqr-readiness]").GetAttribute("role").Should().Be("alert");
        page.Find("[data-testid=iqr-run-review]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=iqr-readiness-items]").HasAttribute("hidden").Should().BeFalse();
        page.Find("[data-testid=iqr-go-configure]").Should().NotBeNull();
    }

    // ── §12–§18. The six review domains ──────────────────────────────────────────────────────

    [Fact]
    public void AllSixDomainsRenderWithScopeVocabularyOnly()
    {
        var page = Landing(Rest(), EventHub(), ServiceBus());

        page.FindAll("[data-testid=iqr-domain]").Select(d => d.GetAttribute("data-domain"))
            .Should().Equal("relationships", "runtime", "contracts", "compatibility", "drift", "performance");
        page.Find("#iqr-domains-heading").TextContent.Should().Be("What will be reviewed");

        page.FindAll("[data-testid=iqr-domain-state]").Select(s => s.TextContent.Trim())
            .Should().OnlyContain(s => s == "Included" || s == "Limited" || s == "Partial evidence" || s == "Not included");
        // Configuration and connection words are never a domain state.
        page.FindAll("[data-testid=iqr-domain-state]").Select(s => s.TextContent.Trim())
            .Should().NotIntersectWith(["Configured", "Connected", "Enabled", "Available"]);
    }

    // §13. Unknown ownership limits the domain and is never inferred.
    [Fact]
    public void RelationshipsReportsWhatIsRecordedAndInfersNothing()
    {
        var page = Landing(Rest(), ServiceBus());

        DomainState(page, "relationships").Should().Be("Limited");
        var limitation = Domain(page, "relationships").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent;
        limitation.Should().Contain("1 of 2 enabled integrations have both producer and consumer recorded");
        limitation.Should().Contain("not inferred from names, paths or resources");
    }

    [Fact]
    public void FullyRecordedRelationshipsAreIncluded()
    {
        var page = Landing(Rest(), EventHub());

        DomainState(page, "relationships").Should().Be("Included");
    }

    // §14. Runtime evidence reports what was observed, and says why messaging cannot be judged.
    [Fact]
    public void RuntimeEvidenceDomainReportsPartialEvidenceRatherThanZero()
    {
        var page = Landing(Rest(), EventHub());

        DomainState(page, "runtime").Should().Be("Partial evidence");
        var limitation = Domain(page, "runtime").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent;
        limitation.Should().Contain("no runtime evidence");
        limitation.Should().Contain("no evidence is not the same as no traffic");
    }

    // §15. Transport metadata is not a schema.
    [Fact]
    public void ContractsSeparatesTransportConfigurationFromSchemaEvidence()
    {
        var page = Landing(Rest(), EventHub());

        DomainState(page, "contracts").Should().Be("Limited");
        Domain(page, "contracts").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent
            .Should().Contain("it is not a schema");
    }

    // §16. Compatibility never claims compatibility without comparable evidence.
    [Fact]
    public void CompatibilityNeverClaimsCompatibilityWithoutComparableEvidence()
    {
        var page = Landing(Rest(), EventHub());

        var compatibility = Domain(page, "compatibility").TextContent;
        compatibility.Should().Contain("not comparable");
        compatibility.Should().NotContainAny("Fully compatible", "Compatible.");
    }

    // §17. A first review is not "no drift".
    [Fact]
    public void DriftDistinguishesAFirstBaselineFromNoDrift()
    {
        var page = Landing(Rest());

        Domain(page, "drift").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent
            .Should().Contain("records the baseline").And.Contain("not the same as no drift");
    }

    // §18. Performance is derived from observation only.
    [Fact]
    public void PerformanceIsNeverDerivedFromConfiguration()
    {
        var page = Landing(Rest(), EventHub());

        DomainState(page, "performance").Should().Be("Partial evidence");
        var limitation = Domain(page, "performance").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent;
        limitation.Should().Contain("No runtime timing was observed");
        limitation.Should().Contain("Configured timeouts and reachability are not performance evidence");
        // No fabricated measurement anywhere in the domain cards.
        page.Find("[data-testid=iqr-domains]").TextContent.Should().NotContainAny("0 ms", "0 req/s", "0 requests");
    }

    // ── §6, §20. The inventory is supporting detail ──────────────────────────────────────────

    [Fact]
    public void ConfiguredIntegrationsAndRuntimeObservationsAreCollapsedBelowTheDomains()
    {
        var page = Landing(Rest(), EventHub());

        var details = page.Find("[data-testid=iqr-details]");
        details.QuerySelector("[data-testid=iqr-scope-disclosure]").Should().NotBeNull();
        details.QuerySelector("[data-testid=iqr-runtime-disclosure]").Should().NotBeNull();

        Collapsed(page, "iqr-scope-disclosure").Should().BeTrue();
        Collapsed(page, "iqr-runtime-disclosure").Should().BeTrue();
        page.Find("[data-testid=iqr-scope-disclosure-toggle]").TextContent
            .Should().Contain("Configured integrations").And.Contain("2 configured").And.Contain("2 enabled");

        page.Find("[data-testid=iqr-scope-disclosure-toggle]").Click();
        page.Find("[data-testid=iqr-scope-disclosure-body]").QuerySelectorAll("[data-testid=iqr-integration-item]")
            .Should().HaveCount(2, "the inventory is moved, not removed");
    }

    // ── §42. Accessibility of the restructured page ──────────────────────────────────────────

    [Fact]
    public void HeadingsAreHierarchicalAndDisclosuresExposeTheirState()
    {
        var page = Landing(Rest(), EventHub());

        page.FindAll("h1").Should().ContainSingle();
        var h2 = page.FindAll("h2").Select(h => h.TextContent.Trim()).ToList();
        h2.Should().Contain(["Target", "Review scope", "Runtime evidence", "What will be reviewed", "Review details"]);
        h2.Should().OnlyHaveUniqueItems();

        foreach (var toggle in page.FindAll(".disclosure-toggle"))
        {
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("aria-expanded").Should().BeOneOf("true", "false");
            page.Find($"#{toggle.GetAttribute("aria-controls")}").Should().NotBeNull();
            toggle.TextContent.Trim().Should().NotBeEmpty();
        }
        page.FindAll(".iqr-pill").Should().OnlyContain(p => p.TextContent.Trim().Length > 0, "state chips carry text");
    }
}
