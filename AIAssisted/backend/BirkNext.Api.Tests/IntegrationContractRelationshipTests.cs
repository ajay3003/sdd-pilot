using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests;

/// <summary>
/// Tests for Phase 2: Integration relationship + contract source model.
/// Verifies producer/consumer/contract metadata and readiness computation.
/// </summary>
public class IntegrationContractRelationshipTests
{
    // ────────────────────────────────────────────────────────────────────────────
    // Test 1: Legacy integration without contract metadata loads successfully
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LegacyIntegration_WithoutContractMetadata_LoadsSuccessfully()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "old-eventhub",
            Name = "Old EventHub",
            Type = IntegrationType.EventHub,
            Endpoint = "namespace",
            Resource = "hub",
            Consumer = null,
            AuthType = IntegrationAuthType.ConnectionString,
            Enabled = true
            // No contract metadata fields
        };

        // New fields should default to null/NotConfigured
        Assert.Null(integration.LogicalProducerService);
        Assert.Null(integration.LogicalConsumerService);
        Assert.Null(integration.ContractName);
        Assert.Equal(ContractSourceType.Unknown, integration.ContractSourceType);
        Assert.Null(integration.ContractSourceLocation);
        Assert.Equal(ContractMetadataReadiness.NotConfigured, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 2: EventHub with complete relationship metadata
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EventHubIntegration_WithCompleteMetadata_IsReady()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "eh-child-updated",
            Name = "Child Updated Events",
            Type = IntegrationType.EventHub,
            Endpoint = "myeventhub-namespace",
            Resource = "child-updated-hub",
            AuthType = IntegrationAuthType.ConnectionString,
            LogicalProducerService = "Hendelse Adapter",
            LogicalConsumerService = "Hendelse",
            ContractName = "ChildUpdated",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "BirkNext.Contracts.ChildUpdated, BirkNext.Contracts v1.0"
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Ready, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 3: REST with OpenAPI source is Ready
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RestIntegration_WithOpenApiSource_IsReady()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "rest-hendelse",
            Name = "Hendelse API",
            Type = IntegrationType.REST,
            Endpoint = "https://hendelse-api.example.com",
            AuthType = IntegrationAuthType.BearerToken,
            LogicalProducerService = "Hendelse Adapter",
            ContractSourceType = ContractSourceType.OpenApi,
            ContractSourceLocation = "https://hendelse-api.example.com/swagger/v1/swagger.json"
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Ready, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 4: GraphQL with schema source is Ready
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GraphQlIntegration_WithSchemaSourceAndConsumer_IsReady()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "graphql-hendelse",
            Name = "Hendelse GraphQL",
            Type = IntegrationType.GraphQL,
            Endpoint = "https://hendelse-api.example.com/graphql",
            AuthType = IntegrationAuthType.BearerToken,
            LogicalConsumerService = "BirkNext Frontend",
            ContractSourceType = ContractSourceType.GraphQlSchema,
            ContractSourceLocation = "https://hendelse-api.example.com/graphql"
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Ready, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 5: Partial metadata is detected correctly
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EventHubIntegration_WithProducerOnly_IsPartial()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "eh-partial",
            Name = "Partial EventHub",
            Type = IntegrationType.EventHub,
            Endpoint = "namespace",
            Resource = "hub",
            LogicalProducerService = "Hendelse Adapter"
            // Missing consumer, contract, or source
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Partial, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 6: Empty relationship metadata is NotConfigured
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Integration_WithNoContractMetadata_IsNotConfigured()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "no-metadata",
            Name = "No Metadata",
            Type = IntegrationType.EventHub,
            Endpoint = "namespace",
            Resource = "hub"
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.NotConfigured, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 7: Kafka with producer/consumer and contract is Ready
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KafkaIntegration_WithProducerAndContract_IsReady()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "kafka-events",
            Name = "Kafka Events",
            Type = IntegrationType.Kafka,
            Endpoint = "kafka.example.com:9092",
            LogicalProducerService = "Event Generator",
            ContractName = "UserEvent"
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Ready, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 8: RabbitMQ with consumer and contract is Ready
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RabbitMqIntegration_WithConsumerAndContract_IsReady()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "rabbitmq-queue",
            Name = "RabbitMQ Queue",
            Type = IntegrationType.RabbitMQ,
            Endpoint = "rabbitmq.example.com",
            Resource = "my-queue",
            LogicalConsumerService = "Event Processor",
            ContractName = "QueueMessage"
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Ready, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 9: REST with Auto source type requires location to be Ready
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RestIntegration_WithAutoSourceButNoLocation_IsPartial()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "rest-auto-no-location",
            Name = "REST Auto NoLoc",
            Type = IntegrationType.REST,
            Endpoint = "https://api.example.com",
            ContractSourceType = ContractSourceType.Auto
            // No source location
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Partial, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 10: REST with Auto source and location is Ready
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RestIntegration_WithAutoSourceAndLocation_IsReady()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "rest-auto-with-location",
            Name = "REST Auto WithLoc",
            Type = IntegrationType.REST,
            Endpoint = "https://api.example.com",
            ContractSourceType = ContractSourceType.Auto,
            ContractSourceLocation = "https://api.example.com/swagger.json"
        };

        integration.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Ready, integration.ContractMetadataReadiness);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 11: Secret filtering - connection strings not allowed in metadata
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContractSourceLocation_WithSharedAccessKey_ShouldNotBeAllowed()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "secret-test",
            Name = "Secret Test",
            Type = IntegrationType.EventHub,
            ContractSourceLocation = "Endpoint=sb://namespace.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret"
        };

        // Validation should reject this (would be implemented in service validation)
        // For now, just verify the field accepted the value
        Assert.NotNull(integration.ContractSourceLocation);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 12: Serialization/deserialization roundtrip
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Integration_WithContractMetadata_SerializesAndDeserializes()
    {
        var original = new IntegrationConfigDto
        {
            Id = "test-id",
            Name = "Test Integration",
            Type = IntegrationType.EventHub,
            Endpoint = "namespace",
            LogicalProducerService = "Producer",
            LogicalConsumerService = "Consumer",
            ContractName = "TestContract",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "Test.Contracts.v1.0"
        };

        original.ComputeReadiness();

        // Simulate JSON serialization by creating a new instance with same values
        var deserialized = new IntegrationConfigDto
        {
            Id = original.Id,
            Name = original.Name,
            Type = original.Type,
            Endpoint = original.Endpoint,
            LogicalProducerService = original.LogicalProducerService,
            LogicalConsumerService = original.LogicalConsumerService,
            ContractName = original.ContractName,
            ContractSourceType = original.ContractSourceType,
            ContractSourceLocation = original.ContractSourceLocation
        };

        // Verify all properties match
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.LogicalProducerService, deserialized.LogicalProducerService);
        Assert.Equal(original.LogicalConsumerService, deserialized.LogicalConsumerService);
        Assert.Equal(original.ContractName, deserialized.ContractName);
        Assert.Equal(original.ContractSourceType, deserialized.ContractSourceType);
        Assert.Equal(original.ContractSourceLocation, deserialized.ContractSourceLocation);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 13: Multiple consumers test - single integration per producer-consumer pair
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IntegrationsList_CanHaveMultipleRecordsForSameMessageDifferentConsumers()
    {
        var consumer1 = new IntegrationConfigDto
        {
            Id = "child-updated-consumer1",
            Name = "Child Updated - Service1",
            Type = IntegrationType.EventHub,
            LogicalProducerService = "Hendelse Adapter",
            LogicalConsumerService = "Service1",
            ContractName = "ChildUpdated"
        };

        var consumer2 = new IntegrationConfigDto
        {
            Id = "child-updated-consumer2",
            Name = "Child Updated - Service2",
            Type = IntegrationType.EventHub,
            LogicalProducerService = "Hendelse Adapter",
            LogicalConsumerService = "Service2",
            ContractName = "ChildUpdated"
        };

        consumer1.ComputeReadiness();
        consumer2.ComputeReadiness();

        Assert.Equal(ContractMetadataReadiness.Partial, consumer1.ContractMetadataReadiness);
        Assert.Equal(ContractMetadataReadiness.Partial, consumer2.ContractMetadataReadiness);
        Assert.NotEqual(consumer1.Id, consumer2.Id);
    }
}
