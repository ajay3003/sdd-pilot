using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Transport-aware integration editing.
///
/// These render the integration editor's decisions rather than the whole settings component:
/// which fields a transport shows, how provenance and incompleteness read, and that the editor
/// never offers a place to type a credential. Field visibility is asserted against
/// IntegrationConfigPresenter, which is the same source the Razor renders from, so a divergence
/// between the two surfaces as a failure here.
/// </summary>
public sealed class IntegrationEditorUITests : BunitContext
{
    private static IntegrationConfig Integration(
        IntegrationType type,
        string name = "Test",
        string? endpoint = null,
        string? resource = null,
        string? consumer = null,
        IntegrationResourceKind kind = IntegrationResourceKind.Unknown,
        IntegrationConfigurationSource source = IntegrationConfigurationSource.Manual) =>
        new()
        {
            Id = "i1", Name = name, Type = type, Endpoint = endpoint, Resource = resource,
            Consumer = consumer, ResourceKind = kind, ConfigurationSource = source
        };

    // ── Which fields each transport shows ────────────────────────────────────

    [Fact]
    public void EventHubShowsConsumerGroupAndNoSubscriptionName()
    {
        var eventHub = Integration(IntegrationType.EventHub, endpoint: "ns", resource: "hub");

        IntegrationConfigPresenter.ShowsConsumerGroup(eventHub).Should().BeTrue();
        IntegrationConfigPresenter.ShowsSubscriptionName(eventHub).Should().BeFalse();
        IntegrationConfigPresenter.ShowsResourceKind(eventHub).Should().BeFalse();
    }

    [Fact]
    public void ServiceBusHidesConsumerGroupAndOffersResourceKind()
    {
        var serviceBus = Integration(IntegrationType.ServiceBus, endpoint: "ns", resource: "leselogg",
            kind: IntegrationResourceKind.ServiceBusQueue);

        // Service Bus has subscriptions, not consumer groups.
        IntegrationConfigPresenter.ShowsConsumerGroup(serviceBus).Should().BeFalse();
        IntegrationConfigPresenter.ShowsResourceKind(serviceBus).Should().BeTrue();
    }

    [Fact]
    public void OnlyAServiceBusSubscriptionAsksForASubscriptionName()
    {
        IntegrationConfigPresenter.ShowsSubscriptionName(
            Integration(IntegrationType.ServiceBus, kind: IntegrationResourceKind.ServiceBusQueue))
            .Should().BeFalse();

        IntegrationConfigPresenter.ShowsSubscriptionName(
            Integration(IntegrationType.ServiceBus, kind: IntegrationResourceKind.ServiceBusTopic))
            .Should().BeFalse();

        IntegrationConfigPresenter.ShowsSubscriptionName(
            Integration(IntegrationType.ServiceBus, kind: IntegrationResourceKind.ServiceBusSubscription))
            .Should().BeTrue();
    }

    [Fact]
    public void KafkaShowsConsumerGroupAndNoServiceBusFields()
    {
        var kafka = Integration(IntegrationType.Kafka, endpoint: "broker:9092", resource: "orders");

        IntegrationConfigPresenter.ShowsConsumerGroup(kafka).Should().BeTrue();
        IntegrationConfigPresenter.ShowsResourceKind(kafka).Should().BeFalse();
        IntegrationConfigPresenter.ShowsSubscriptionName(kafka).Should().BeFalse();
    }

    [Fact]
    public void RabbitMqOffersItsOwnEntityKindsAndNoConsumerGroup()
    {
        var rabbit = Integration(IntegrationType.RabbitMQ, endpoint: "amqp://host", resource: "orders");

        IntegrationConfigPresenter.ShowsResourceKind(rabbit).Should().BeTrue();
        IntegrationConfigPresenter.ShowsConsumerGroup(rabbit).Should().BeFalse();

        IntegrationConfigPresenter.ResourceKindChoices(IntegrationType.RabbitMQ)
            .Should().BeEquivalentTo(new[]
            {
                IntegrationResourceKind.RabbitQueue,
                IntegrationResourceKind.RabbitExchange
            });
    }

    [Fact]
    public void HttpIntegrationsShowNoMessagingFields()
    {
        foreach (var type in new[] { IntegrationType.REST, IntegrationType.GraphQL })
        {
            var http = Integration(type, endpoint: "https://api.example.test/orders");

            IntegrationConfigPresenter.ShowsConsumerGroup(http).Should().BeFalse();
            IntegrationConfigPresenter.ShowsSubscriptionName(http).Should().BeFalse();
            IntegrationConfigPresenter.ShowsResourceKind(http).Should().BeFalse();
        }
    }

    // ── Readiness as the editor presents it ──────────────────────────────────

    [Fact]
    public void IncompleteEventHubNamesTheMissingNamespace()
    {
        var state = IntegrationConfigPresenter.ConfigurationState(
            Integration(IntegrationType.EventHub, resource: "m2lb-cdc-qa.birk.dbo.person"));

        state.Should().Contain("Configuration incomplete");
        state.Should().Contain("Namespace");
    }

    [Fact]
    public void EventHubIsReadyWithoutAnyRelationship()
    {
        // Producer and consumer are optional; an unknown relationship must not block registration.
        var complete = Integration(IntegrationType.EventHub, endpoint: "ns", resource: "hub");

        IntegrationConfigPresenter.IsComplete(complete).Should().BeTrue();
        IntegrationConfigPresenter.ConfigurationState(complete).Should().Be("Configuration complete");
    }

    [Fact]
    public void ServiceBusSubscriptionIsIncompleteWithoutItsSubscriptionName()
    {
        var subscription = Integration(IntegrationType.ServiceBus, endpoint: "ns", resource: "topic",
            kind: IntegrationResourceKind.ServiceBusSubscription);

        IntegrationConfigPresenter.MissingRequiredFields(subscription).Should().Contain("Subscription name");
    }

    [Fact]
    public void IncompletenessIsNeverWordedAsFailure()
    {
        foreach (var integration in new[]
                 {
                     Integration(IntegrationType.EventHub),
                     Integration(IntegrationType.ServiceBus),
                     Integration(IntegrationType.Kafka),
                     Integration(IntegrationType.RabbitMQ),
                     Integration(IntegrationType.REST)
                 })
        {
            var state = IntegrationConfigPresenter.ConfigurationState(integration);

            state.Should().NotContain("Failed");
            state.Should().NotContain("Broken");
            state.Should().NotContain("Unavailable");
            state.Should().NotContain("Error");
        }
    }

    // ── Template application as the editor performs it ───────────────────────

    private static KnownIntegrationTemplate PersonTemplate() => new()
    {
        Id = "qa-eh-person",
        EnvironmentName = "QA",
        DisplayName = "BiRK Person CDC",
        IntegrationType = IntegrationType.EventHub,
        ResourceKind = IntegrationResourceKind.EventHub,
        Resource = "m2lb-cdc-qa.birk.dbo.person",
        SuggestedProducer = "BiRK / Debezium",
        SuggestedConsumer = "PersonBiRKAdapter",
        SuggestedConsumerGroup = "$Default",
        SuggestionOrigin = "Suggested from audited M2LB source"
    };

    [Fact]
    public void ApplyingThePersonTemplateFillsTheEvidencedValues()
    {
        var target = new IntegrationConfig { Id = "i1" };
        IntegrationConfigPresenter.ApplyTemplate(target, PersonTemplate());

        target.Resource.Should().Be("m2lb-cdc-qa.birk.dbo.person");
        target.LogicalProducerService.Should().Be("BiRK / Debezium");
        target.LogicalConsumerService.Should().Be("PersonBiRKAdapter");
        target.Consumer.Should().Be("$Default");
        target.ResourceKind.Should().Be(IntegrationResourceKind.EventHub);
    }

    [Fact]
    public void ThePersonTemplateLeavesTheNamespaceBlankAndSaysSo()
    {
        var target = new IntegrationConfig { Id = "i1" };
        IntegrationConfigPresenter.ApplyTemplate(target, PersonTemplate());

        target.Endpoint.Should().BeNull();
        IntegrationConfigPresenter.ConfigurationState(target).Should().Contain("Namespace");
    }

    [Fact]
    public void TemplateConsumerGroupUsesTheAuditedCasing()
    {
        var target = new IntegrationConfig { Id = "i1" };
        IntegrationConfigPresenter.ApplyTemplate(target, PersonTemplate());

        target.Consumer.Should().Be("$Default");
        target.Consumer.Should().NotBe("$default");
    }

    [Fact]
    public void AnAppliedTemplateReadsAsSuggestedNotVerified()
    {
        var target = new IntegrationConfig { Id = "i1" };
        IntegrationConfigPresenter.ApplyTemplate(target, PersonTemplate());

        var label = IntegrationConfigPresenter.SourceLabel(target.ConfigurationSource);

        label.Should().Be("Suggested from audited M2LB source");
        label.Should().NotContain("Verified");
    }

    [Fact]
    public void EditingAnAppliedTemplateKeepsTheEditedValue()
    {
        var target = new IntegrationConfig { Id = "i1" };
        IntegrationConfigPresenter.ApplyTemplate(target, PersonTemplate());

        target.Endpoint = "chosen-namespace.servicebus.windows.net";
        target.Consumer = "my-own-group";
        target.ConfigurationSource = IntegrationConfigurationSource.Manual;

        // A template initialises; it is never re-applied over an edit.
        target.Endpoint.Should().Be("chosen-namespace.servicebus.windows.net");
        target.Consumer.Should().Be("my-own-group");
        IntegrationConfigPresenter.SourceLabel(target.ConfigurationSource).Should().Be("Manual");
        IntegrationConfigPresenter.IsComplete(target).Should().BeTrue();
    }

    [Fact]
    public void LeseloggTemplateKeepsItsAuditedResourceName()
    {
        var target = new IntegrationConfig { Id = "i1" };

        IntegrationConfigPresenter.ApplyTemplate(target, new KnownIntegrationTemplate
        {
            DisplayName = "Leselogg",
            IntegrationType = IntegrationType.ServiceBus,
            ResourceKind = IntegrationResourceKind.ServiceBusQueue,
            Resource = "leselogg",
            SuggestedConsumer = "Revisjon"
        });

        target.Resource.Should().Be("leselogg");
        target.Resource.Should().NotBe("revisjon.leselogg");

        // Service Bus carries no consumer group.
        target.Consumer.Should().BeNull();
    }

    // ── Grouping ─────────────────────────────────────────────────────────────

    [Fact]
    public void DiscoveredHttpIntegrationsGroupApartFromMessaging()
    {
        var discoveredRest = Integration(IntegrationType.REST,
            endpoint: "https://api.example.test/orders",
            source: IntegrationConfigurationSource.EndpointDiscovery);

        var messaging = Integration(IntegrationType.EventHub, endpoint: "ns", resource: "hub",
            source: IntegrationConfigurationSource.CodeSuggested);

        IntegrationConfigPresenter.IsHttpIntegration(discoveredRest.Type).Should().BeTrue();
        IntegrationConfigPresenter.IsHttpIntegration(messaging.Type).Should().BeFalse();

        IntegrationConfigPresenter.SourceLabel(discoveredRest.ConfigurationSource).Should().Be("Discovered");
        IntegrationConfigPresenter.SourceLabel(messaging.ConfigurationSource)
            .Should().Be("Suggested from audited M2LB source");
    }

    [Fact]
    public void LegacyIntegrationWithoutProvenanceReadsAsUnknown() =>
        IntegrationConfigPresenter.SourceLabel(new IntegrationConfig().ConfigurationSource)
            .Should().Be("Unknown");

    // ── Relationships are never invented ─────────────────────────────────────

    [Fact]
    public void AnUnsetRelationshipStaysUnset()
    {
        var discovered = Integration(IntegrationType.GraphQL,
            endpoint: "https://m2lbqa.example.test/api/person/graphql",
            source: IntegrationConfigurationSource.EndpointDiscovery);

        // Nothing derives "Person" from the path.
        discovered.LogicalProducerService.Should().BeNull();
        discovered.LogicalConsumerService.Should().BeNull();
    }

    // ── Security ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheIntegrationModelHasNoPlaceToPutACredential()
    {
        var properties = typeof(IntegrationConfig).GetProperties().Select(p => p.Name).ToList();

        foreach (var forbidden in new[]
                 {
                     "ConnectionString", "SasKey", "SharedAccessKey", "AccessKey", "Secret",
                     "ClientSecret", "Password", "Token", "Authorization", "Cookie", "Jwt"
                 })
        {
            properties.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"the editor must not offer a field for {forbidden}");
        }
    }

    [Fact]
    public void TemplatesCarryNoCredentialFields()
    {
        var properties = typeof(KnownIntegrationTemplate).GetProperties().Select(p => p.Name).ToList();

        foreach (var forbidden in new[]
                 {
                     "ConnectionString", "SasKey", "SharedAccessKey", "Secret", "Password", "Token"
                 })
        {
            properties.Should().NotContain(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }
}
