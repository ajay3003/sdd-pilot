using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Rendered scope of the Integration Quality Review.
///
/// The review reports on configuration; it does not edit it. These assert the rendered page shows
/// what will be reviewed and where it came from, offers a way to go and change it, and contains
/// no control for changing it in place.
/// </summary>
public sealed class IntegrationReviewScopeDomTests : BunitContext
{
    private readonly Mock<IIntegrationQualityReviewService> _review = new();
    private readonly Mock<IFrontendAnalysisContextFactory> _contextFactory = new();
    private readonly Mock<IReportExportService> _export = new();

    private static IntegrationConfig Rest() => new()
    {
        Id = "rest-1", Name = "Orders API", Type = IntegrationType.REST, Enabled = true,
        Endpoint = "https://application-qa.example.test/api/orders",
        ConfigurationSource = IntegrationConfigurationSource.EndpointDiscovery,
        ResourceKind = IntegrationResourceKind.RestEndpoint
    };

    private static IntegrationConfig GraphQl() => new()
    {
        Id = "gql-1", Name = "Person GraphQL", Type = IntegrationType.GraphQL, Enabled = true,
        Endpoint = "https://application-qa.example.test/api/person/graphql",
        ConfigurationSource = IntegrationConfigurationSource.EndpointDiscovery,
        ResourceKind = IntegrationResourceKind.GraphQlEndpoint
    };

    /// <summary>An accepted template whose namespace the audit never established.</summary>
    private static IntegrationConfig EventHub() => new()
    {
        Id = "eh-1", Name = "BiRK Person CDC", Type = IntegrationType.EventHub, Enabled = true,
        Resource = "m2lb-cdc-qa.birk.dbo.person", Consumer = "$Default",
        LogicalProducerService = "BiRK / Debezium",
        LogicalConsumerService = "PersonBiRKAdapter",
        ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
        ResourceKind = IntegrationResourceKind.EventHub
    };

    private static IntegrationConfig ServiceBus() => new()
    {
        Id = "sb-1", Name = "Leselogg", Type = IntegrationType.ServiceBus, Enabled = true,
        Endpoint = "ns.servicebus.windows.net", Resource = "leselogg",
        LogicalConsumerService = "Revisjon",
        ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
        ResourceKind = IntegrationResourceKind.ServiceBusQueue
    };

    private IRenderedComponent<IntegrationQualityReview> RenderScope(params IntegrationConfig[] integrations)
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "qa", Name = "QA", EnvironmentType = FrontendEnvironmentType.QA,
            TargetUrl = "https://application-qa.example.test/"
        };
        profile.Integrations.AddRange(integrations);

        _contextFactory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = profile,
            TargetUrl = profile.TargetUrl,
            Integrations = integrations.ToList()
        });

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_contextFactory.Object);
        Services.AddSingleton(_review.Object);
        Services.AddSingleton(_export.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IEndpointDiscoveryService>());

        return Render<IntegrationQualityReview>();
    }

    // ── Scope renders what will be reviewed ──────────────────────────────────

    [Fact]
    public void ScopeSectionRendersOnce()
    {
        var cut = RenderScope(Rest(), EventHub());

        // One scope surface: a second element sharing this id would make every selector ambiguous.
        cut.FindAll("[data-testid=iqr-scope]").Should().HaveCount(1);
    }

    [Fact]
    public void DiscoveredHttpIntegrationsAppear()
    {
        var cut = RenderScope(Rest(), GraphQl());

        cut.Markup.Should().Contain("Orders API");
        cut.Markup.Should().Contain("Person GraphQL");
        cut.FindAll("[data-testid=iqr-type-group]").Should().NotBeEmpty();
    }

    [Fact]
    public void MessagingIntegrationsAppear()
    {
        var cut = RenderScope(EventHub(), ServiceBus());

        cut.Markup.Should().Contain("BiRK Person CDC");
        cut.Markup.Should().Contain("Leselogg");
    }

    [Fact]
    public void GroupsAreSeparatedByTransport()
    {
        var cut = RenderScope(Rest(), GraphQl(), EventHub(), ServiceBus());

        var types = cut.FindAll("[data-testid=iqr-type-group]")
            .Select(g => g.GetAttribute("data-type")).ToList();

        types.Should().Contain("REST");
        types.Should().Contain("GraphQL");
        types.Should().Contain("EventHub");
        types.Should().Contain("ServiceBus");
    }

    // ── Provenance ───────────────────────────────────────────────────────────

    [Fact]
    public void DiscoveredIntegrationRendersAsDiscovered()
    {
        var cut = RenderScope(Rest());

        cut.FindAll("[data-testid=iqr-scope-source]")
            .Should().Contain(e => e.TextContent.Contains("Discovered"));
    }

    [Fact]
    public void SuggestedIntegrationNamesTheAuditedSourceAndNotVerification()
    {
        var cut = RenderScope(EventHub());

        var source = cut.Find("[data-testid=iqr-scope-source]").TextContent;

        source.Should().Contain("Suggested from audited M2LB source");
        source.Should().NotContain("Verified");
    }

    [Fact]
    public void LegacyIntegrationWithoutProvenanceRendersAsUnknown()
    {
        var legacy = Rest();
        legacy.ConfigurationSource = IntegrationConfigurationSource.Unknown;

        var cut = RenderScope(legacy);

        cut.Find("[data-testid=iqr-scope-source]").TextContent.Should().Contain("Unknown");
    }

    // ── Incomplete configuration is not failure ──────────────────────────────

    [Fact]
    public void IncompleteIntegrationNamesItsMissingFields()
    {
        // The Event Hub template carries no namespace; the audit never established one.
        var cut = RenderScope(EventHub());

        var incomplete = cut.Find("[data-testid=iqr-scope-incomplete]").TextContent;

        incomplete.Should().Contain("Configuration incomplete");
        incomplete.Should().Contain("Namespace");
    }

    [Fact]
    public void IncompleteConfigurationIsNeverDescribedAsFailure()
    {
        var cut = RenderScope(EventHub());

        var incomplete = cut.Find("[data-testid=iqr-scope-incomplete]").TextContent;

        incomplete.Should().NotContain("Failed");
        incomplete.Should().NotContain("Broken");
        incomplete.Should().NotContain("Unavailable");
        incomplete.Should().NotContain("Error");
    }

    [Fact]
    public void CompleteIntegrationShowsNoIncompleteMessage()
    {
        var cut = RenderScope(ServiceBus());

        cut.FindAll("[data-testid=iqr-scope-incomplete]")
            .Should().BeEmpty("the Service Bus entry has namespace, kind and entity");
    }

    [Fact]
    public void AWayToEditTargetEnvironmentIsOffered()
    {
        var cut = RenderScope(EventHub());

        var link = cut.Find("[data-testid=iqr-manage-scope]");

        link.TextContent.Should().Be("Open Integrations configuration");
        link.GetAttribute("href").Should().Contain("section=target-environments&tab=integrations");
    }

    // ── Read-only ────────────────────────────────────────────────────────────

    [Fact]
    public void ScopeContainsNoConfigurationEditingControls()
    {
        var cut = RenderScope(Rest(), GraphQl(), EventHub(), ServiceBus());

        var scope = cut.Find("[data-testid=iqr-scope]");

        scope.QuerySelectorAll("input").Should().BeEmpty("the review does not edit configuration");
        scope.QuerySelectorAll("select").Should().BeEmpty();
        scope.QuerySelectorAll("textarea").Should().BeEmpty();
    }

    [Fact]
    public void ScopeOffersNoSaveAction()
    {
        var cut = RenderScope(EventHub());

        var scope = cut.Find("[data-testid=iqr-scope]");

        scope.QuerySelectorAll("button")
            .Should().NotContain(b => b.TextContent.Contains("Save", StringComparison.OrdinalIgnoreCase));
    }

    // ── Relationships are never invented ─────────────────────────────────────

    [Fact]
    public void UnknownRelationshipsAreNotFilledIn()
    {
        // A discovered GraphQL endpoint at /api/person/graphql must not acquire "Person".
        var cut = RenderScope(GraphQl());

        var scope = cut.Find("[data-testid=iqr-scope]").TextContent;

        scope.Should().NotContain("PersonBiRKAdapter");
        scope.Should().NotMatch("*Consumer: Person*");
    }

    [Fact]
    public void NoConsumerIsDerivedFromAMessagingResourceName()
    {
        var hub = EventHub();
        hub.LogicalProducerService = null;
        hub.LogicalConsumerService = null;

        var cut = RenderScope(hub);
        var scope = cut.Find("[data-testid=iqr-scope]").TextContent;

        // "m2lb-cdc-qa.birk.dbo.person" must not yield a "Person" consumer.
        scope.Should().NotContain("PersonBiRKAdapter");
        scope.Should().NotMatch("*Consumer: Person*");
    }

    [Fact]
    public void RecordedRelationshipsAreShownAsRecorded()
    {
        var cut = RenderScope(EventHub());
        // The inventory is supporting detail now; the summary card above it only counts.
        var scope = cut.Find("[data-testid=iqr-scope-disclosure-body]").TextContent;

        // These were established by the audit, so they may appear.
        scope.Should().Contain("m2lb-cdc-qa.birk.dbo.person");
    }

    // ── Configuration is not evidence of runtime ─────────────────────────────

    [Fact]
    public void AConfiguredTemplateRendersNoPerformanceNumbers()
    {
        var cut = RenderScope(EventHub(), ServiceBus());
        var scope = cut.Find("[data-testid=iqr-scope]").TextContent;

        // No review has run, so nothing was measured. Configuration, a consumer group and a
        // resource name are not evidence that traffic was observed.
        scope.Should().NotContain("p95");
        scope.Should().NotContain("req/s");
        scope.Should().NotContain("ms");
        scope.Should().NotContain("0%");
    }

    [Fact]
    public void ScopeRendersBeforeAnyReviewHasRun()
    {
        // Scope answers "what will be reviewed", so it must not depend on a report existing.
        var cut = RenderScope(Rest(), EventHub());

        cut.FindAll("[data-testid=iqr-scope]").Should().NotBeEmpty();
        cut.FindAll("[data-testid=iqr-results]").Should().BeEmpty();
    }
}
