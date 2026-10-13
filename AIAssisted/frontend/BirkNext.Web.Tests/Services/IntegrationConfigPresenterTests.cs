using BirkNext.Web.Models;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Configuration provenance wording, per-transport readiness and field visibility.
///
/// IntegrationConfigPresenter is the single source of this wording for both the Target
/// Environment editor and the Integration Quality Review scope, so asserting on it covers both.
/// </summary>
public class IntegrationConfigPresenterTests
{
    private static IntegrationConfig Config(
        IntegrationType type,
        string? endpoint = null,
        string? resource = null,
        string? consumer = null,
        IntegrationResourceKind kind = IntegrationResourceKind.Unknown,
        IntegrationConfigurationSource source = IntegrationConfigurationSource.Manual) =>
        new()
        {
            Id = "i1", Name = "Test", Type = type, Endpoint = endpoint, Resource = resource,
            Consumer = consumer, ResourceKind = kind, ConfigurationSource = source
        };

    // ── Source labels ────────────────────────────────────────────────────────

    [Fact]
    public void SourceLabels_AreHumanReadable()
    {
        IntegrationConfigPresenter.SourceLabel(IntegrationConfigurationSource.EndpointDiscovery)
            .Should().Be("Discovered");
        IntegrationConfigPresenter.SourceLabel(IntegrationConfigurationSource.Manual)
            .Should().Be("Manual");
        IntegrationConfigPresenter.SourceLabel(IntegrationConfigurationSource.CodeSuggested)
            .Should().Be("Suggested from audited M2LB source");
        IntegrationConfigPresenter.SourceLabel(IntegrationConfigurationSource.Unknown)
            .Should().Be("Unknown");
    }

    [Fact]
    public void SuggestedSource_IsNeverPresentedAsVerified()
    {
        var label = IntegrationConfigPresenter.SourceLabel(IntegrationConfigurationSource.CodeSuggested);

        label.Should().NotContain("Verified");
        label.Should().NotContain("verified");
        label.Should().NotContain("this repository");
    }

    [Fact]
    public void LegacyConfigWithoutProvenance_ReadsAsUnknownNotManual() =>
        IntegrationConfigPresenter.SourceLabel(new IntegrationConfig().ConfigurationSource)
            .Should().Be("Unknown");

    // ── Readiness per transport ──────────────────────────────────────────────

    [Fact]
    public void RestRequiresOnlyAnEndpoint()
    {
        IntegrationConfigPresenter.MissingRequiredFields(
            Config(IntegrationType.REST, endpoint: "https://api.example.test/orders"))
            .Should().BeEmpty();

        IntegrationConfigPresenter.MissingRequiredFields(Config(IntegrationType.REST))
            .Should().Contain("Endpoint");
    }

    [Fact]
    public void GraphQlRequiresOnlyAnEndpoint() =>
        IntegrationConfigPresenter.MissingRequiredFields(
            Config(IntegrationType.GraphQL, endpoint: "https://api.example.test/graphql"))
            .Should().BeEmpty();

    [Fact]
    public void EventHubRequiresNamespaceAndResource()
    {
        var missing = IntegrationConfigPresenter.MissingRequiredFields(
            Config(IntegrationType.EventHub, resource: "m2lb-cdc-qa.birk.dbo.person"));

        missing.Should().Contain("Namespace");
        missing.Should().NotContain("Event Hub name");
    }

    [Fact]
    public void ServiceBusRequiresNamespaceKindAndEntity()
    {
        var missing = IntegrationConfigPresenter.MissingRequiredFields(
            Config(IntegrationType.ServiceBus, resource: "leselogg"));

        missing.Should().Contain("Namespace");
        missing.Should().Contain("Resource kind");
    }

    [Fact]
    public void ServiceBusSubscriptionAlsoRequiresASubscriptionName()
    {
        var missing = IntegrationConfigPresenter.MissingRequiredFields(
            Config(IntegrationType.ServiceBus, endpoint: "ns", resource: "topic",
                   kind: IntegrationResourceKind.ServiceBusSubscription));

        missing.Should().Contain("Subscription name");
    }

    [Fact]
    public void ServiceBusQueueDoesNotRequireASubscriptionName() =>
        IntegrationConfigPresenter.MissingRequiredFields(
            Config(IntegrationType.ServiceBus, endpoint: "ns", resource: "leselogg",
                   kind: IntegrationResourceKind.ServiceBusQueue))
            .Should().BeEmpty();

    [Fact]
    public void KafkaRequiresBrokerAndTopic()
    {
        var missing = IntegrationConfigPresenter.MissingRequiredFields(Config(IntegrationType.Kafka));
        missing.Should().Contain("Broker");
        missing.Should().Contain("Topic");
    }

    [Fact]
    public void RabbitMqRequiresBrokerAndEntity()
    {
        var missing = IntegrationConfigPresenter.MissingRequiredFields(Config(IntegrationType.RabbitMQ));
        missing.Should().Contain("Broker");
        missing.Should().Contain("Exchange or queue");
    }

    [Fact]
    public void ProducerAndConsumerAreNeverRequired()
    {
        // An unknown relationship is a legitimate state; checks that need one report their own
        // not-ready result rather than blocking configuration.
        var complete = Config(IntegrationType.EventHub, endpoint: "ns", resource: "hub");

        IntegrationConfigPresenter.MissingRequiredFields(complete).Should().BeEmpty();
        IntegrationConfigPresenter.IsComplete(complete).Should().BeTrue();
    }

    [Fact]
    public void IncompleteConfiguration_IsNotDescribedAsFailure()
    {
        var state = IntegrationConfigPresenter.ConfigurationState(Config(IntegrationType.EventHub));

        state.Should().Contain("Configuration incomplete");
        state.Should().Contain("Namespace");
        state.Should().NotContain("Failed");
        state.Should().NotContain("Unavailable");
        state.Should().NotContain("Broken");
    }

    // ── Field visibility ─────────────────────────────────────────────────────

    [Fact]
    public void ConsumerGroupShowsOnlyWhereTheTransportHasOne()
    {
        IntegrationConfigPresenter.ShowsConsumerGroup(Config(IntegrationType.EventHub)).Should().BeTrue();
        IntegrationConfigPresenter.ShowsConsumerGroup(Config(IntegrationType.Kafka)).Should().BeTrue();
        IntegrationConfigPresenter.ShowsConsumerGroup(Config(IntegrationType.ServiceBus)).Should().BeFalse();
        IntegrationConfigPresenter.ShowsConsumerGroup(Config(IntegrationType.REST)).Should().BeFalse();
    }

    [Fact]
    public void SubscriptionNameShowsOnlyForAServiceBusSubscription()
    {
        IntegrationConfigPresenter.ShowsSubscriptionName(
            Config(IntegrationType.ServiceBus, kind: IntegrationResourceKind.ServiceBusSubscription))
            .Should().BeTrue();

        IntegrationConfigPresenter.ShowsSubscriptionName(
            Config(IntegrationType.ServiceBus, kind: IntegrationResourceKind.ServiceBusQueue))
            .Should().BeFalse();

        IntegrationConfigPresenter.ShowsSubscriptionName(Config(IntegrationType.EventHub))
            .Should().BeFalse();
    }

    [Fact]
    public void RoutingKeyIsNotOffered()
    {
        // IntegrationConfig has no routing-key field, so the editor must not imply one exists.
        // A helper that claimed otherwise was removed rather than left describing a capability
        // the model does not have.
        typeof(IntegrationConfig).GetProperties().Select(p => p.Name)
            .Should().NotContain(name => name.Contains("RoutingKey", StringComparison.OrdinalIgnoreCase));

        typeof(IntegrationConfigPresenter).GetMethods().Select(m => m.Name)
            .Should().NotContain("ShowsRoutingKey");
    }

    [Fact]
    public void ResourceKindChoices_MatchTheTransport()
    {
        IntegrationConfigPresenter.ResourceKindChoices(IntegrationType.ServiceBus)
            .Should().BeEquivalentTo(new[]
            {
                IntegrationResourceKind.ServiceBusQueue,
                IntegrationResourceKind.ServiceBusTopic,
                IntegrationResourceKind.ServiceBusSubscription
            });

        IntegrationConfigPresenter.ResourceKindChoices(IntegrationType.RabbitMQ)
            .Should().BeEquivalentTo(new[]
            {
                IntegrationResourceKind.RabbitQueue,
                IntegrationResourceKind.RabbitExchange
            });

        IntegrationConfigPresenter.ResourceKindChoices(IntegrationType.EventHub).Should().BeEmpty();
        IntegrationConfigPresenter.ResourceKindChoices(IntegrationType.REST).Should().BeEmpty();
    }

    // ── Grouping ─────────────────────────────────────────────────────────────

    [Fact]
    public void HttpIntegrationsGroupSeparatelyFromMessaging()
    {
        IntegrationConfigPresenter.IsHttpIntegration(IntegrationType.REST).Should().BeTrue();
        IntegrationConfigPresenter.IsHttpIntegration(IntegrationType.GraphQL).Should().BeTrue();
        IntegrationConfigPresenter.IsHttpIntegration(IntegrationType.EventHub).Should().BeFalse();
        IntegrationConfigPresenter.IsHttpIntegration(IntegrationType.ServiceBus).Should().BeFalse();
    }

}
