using BirkNext.Api.Services;
using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Tests for Phase 3 Checkpoint 1: Producer/Consumer Relationship Population
/// Verifies that relationships are populated from authoritative sources only,
/// never from display names, and source tracking is maintained.
/// </summary>
public class IntegrationRelationshipPopulationTests
{
    private readonly IntegrationRelationshipPopulationService _service = new();

    [Fact]
    public void ConfiguredRelationship_PopulatesWithConfiguredSource()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-1",
            Name = "Test Integration",
            Type = IntegrationType.EventHub,
            LogicalProducerService = "ProducerService",
            LogicalConsumerService = "ConsumerService"
        };

        _service.PopulateRelationship(integration);

        Assert.Equal("ProducerService", integration.LogicalProducerService);
        Assert.Equal("ConsumerService", integration.LogicalConsumerService);
        Assert.Equal(RelationshipSource.Configured, integration.ProducerConsumerSource);
    }

    [Fact]
    public void PartiallyConfiguredRelationship_MarksAsConfigured()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-2",
            Name = "Partial Config",
            Type = IntegrationType.EventHub,
            LogicalProducerService = "ProducerOnly"
            // Consumer not set
        };

        _service.PopulateRelationship(integration);

        Assert.Equal("ProducerOnly", integration.LogicalProducerService);
        Assert.Equal(RelationshipSource.Configured, integration.ProducerConsumerSource);
    }

    [Fact]
    public void MessagingIntegrationWithConsumer_InfersConsumerFromMetadata()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-3",
            Name = "EventHub With Consumer",
            Type = IntegrationType.EventHub,
            Endpoint = "mynamespace",
            Resource = "my-hub",
            Consumer = "my-consumer-group"
            // No LogicalConsumerService configured
        };

        _service.PopulateRelationship(integration);

        Assert.Null(integration.LogicalProducerService);  // Producer not inferred
        Assert.Equal("my-consumer-group", integration.LogicalConsumerService);  // Consumer inferred
        Assert.Equal(RelationshipSource.MessagingMetadata, integration.ProducerConsumerSource);
    }

    [Fact]
    public void MessagingIntegrationWithoutConsumer_RemainsUnknown()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-4",
            Name = "EventHub Without Consumer",
            Type = IntegrationType.EventHub,
            Endpoint = "mynamespace",
            Resource = "my-hub"
            // No Consumer configured
        };

        _service.PopulateRelationship(integration);

        Assert.Null(integration.LogicalProducerService);
        Assert.Null(integration.LogicalConsumerService);
        Assert.Equal(RelationshipSource.Unknown, integration.ProducerConsumerSource);
    }

    [Fact]
    public void RestIntegrationWithoutExplicitRelationship_RemainsUnknown()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-5",
            Name = "REST API",
            Type = IntegrationType.REST,
            Endpoint = "https://api.example.com"
        };

        _service.PopulateRelationship(integration);

        Assert.Null(integration.LogicalProducerService);
        Assert.Null(integration.LogicalConsumerService);
        Assert.Equal(RelationshipSource.Unknown, integration.ProducerConsumerSource);
    }

    [Fact]
    public void DisplayNameNotParsedAsRelationship()
    {
        // Display name "BIRK → M2LB" must never be parsed
        var integration = new IntegrationConfigDto
        {
            Id = "test-6",
            Name = "BIRK → M2LB",  // Suggestive display name only
            Type = IntegrationType.REST,
            Endpoint = "https://api.example.com"
            // No explicit LogicalProducerService/Consumer
        };

        _service.PopulateRelationship(integration);

        Assert.Null(integration.LogicalProducerService);  // Not parsed from name
        Assert.Null(integration.LogicalConsumerService);  // Not parsed from name
        Assert.Equal(RelationshipSource.Unknown, integration.ProducerConsumerSource);
    }

    [Fact]
    public void KafkaWithConsumer_InfersConsumerFromMetadata()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-7",
            Name = "Kafka Topic",
            Type = IntegrationType.Kafka,
            Endpoint = "kafka-broker:9092",
            Resource = "my-topic",
            Consumer = "my-consumer-group"
        };

        _service.PopulateRelationship(integration);

        Assert.Null(integration.LogicalProducerService);
        Assert.Equal("my-consumer-group", integration.LogicalConsumerService);
        Assert.Equal(RelationshipSource.MessagingMetadata, integration.ProducerConsumerSource);
    }

    [Fact]
    public void ServiceBusWithConsumer_InfersConsumerFromMetadata()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-8",
            Name = "Service Bus",
            Type = IntegrationType.ServiceBus,
            Endpoint = "mynamespace.servicebus.windows.net",
            Resource = "my-topic",
            Consumer = "my-subscription"
        };

        _service.PopulateRelationship(integration);

        Assert.Null(integration.LogicalProducerService);
        Assert.Equal("my-subscription", integration.LogicalConsumerService);
        Assert.Equal(RelationshipSource.MessagingMetadata, integration.ProducerConsumerSource);
    }

    [Fact]
    public void PopulateMultipleIntegrations()
    {
        var integrations = new List<IntegrationConfigDto>
        {
            new()
            {
                Id = "int-1",
                Name = "Configured",
                Type = IntegrationType.EventHub,
                LogicalProducerService = "Producer1",
                LogicalConsumerService = "Consumer1"
            },
            new()
            {
                Id = "int-2",
                Name = "REST",
                Type = IntegrationType.REST,
                Endpoint = "https://api.example.com"
            },
            new()
            {
                Id = "int-3",
                Name = "Kafka",
                Type = IntegrationType.Kafka,
                Consumer = "group1"
            }
        };

        _service.PopulateRelationships(integrations);

        Assert.Equal(RelationshipSource.Configured, integrations[0].ProducerConsumerSource);
        Assert.Equal(RelationshipSource.Unknown, integrations[1].ProducerConsumerSource);
        Assert.Equal(RelationshipSource.MessagingMetadata, integrations[2].ProducerConsumerSource);
    }

    [Fact]
    public void ExplicitConfigurationWinsOverMessagingMetadata()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-9",
            Name = "EventHub",
            Type = IntegrationType.EventHub,
            LogicalConsumerService = "ExplicitConsumer",  // Explicitly configured
            Consumer = "different-group"  // Would infer differently
        };

        _service.PopulateRelationship(integration);

        // Explicit configuration wins
        Assert.Equal("ExplicitConsumer", integration.LogicalConsumerService);
        Assert.Equal(RelationshipSource.Configured, integration.ProducerConsumerSource);
    }

    [Fact]
    public void RabbitMQWithConsumer_InfersConsumerFromMetadata()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-10",
            Name = "RabbitMQ",
            Type = IntegrationType.RabbitMQ,
            Endpoint = "amqp://rabbitmq-host",
            Resource = "my-queue",
            Consumer = "consumer-app"
        };

        _service.PopulateRelationship(integration);

        Assert.Null(integration.LogicalProducerService);
        Assert.Equal("consumer-app", integration.LogicalConsumerService);
        Assert.Equal(RelationshipSource.MessagingMetadata, integration.ProducerConsumerSource);
    }
}
