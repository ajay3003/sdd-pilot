using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractAnalysis;

/// <summary>
/// Phase 3 Checkpoint 3: Messaging Contract/Schema Extraction
/// Tests schema extraction from EventHub, ServiceBus, Kafka, RabbitMQ.
/// Verifies: schema discovery, normalization, producer/consumer preservation,
/// transport/schema separation, malformed handling, unsupported graceful failure.
/// </summary>
public class MessageSchemaDiscoveryTests
{
    private readonly FakeAssemblyInspector _assemblyInspector = new();
    private readonly MessageSchemaDiscoveryService _service;

    public MessageSchemaDiscoveryTests()
    {
        _service = new MessageSchemaDiscoveryService(_assemblyInspector, NullLogger<MessageSchemaDiscoveryService>.Instance);
    }

    #region Schema Available - Assembly Source

    [Fact]
    public async Task EventHub_WithAssemblySchema_NormalizesSuccessfully()
    {
        // Arrange
        var integration = new IntegrationConfigDto
        {
            Id = "eh-1",
            Name = "Placement Events",
            Type = IntegrationType.EventHub,
            Endpoint = "my-namespace",
            Resource = "placement-hub",
            ContractName = "PlacementUpdated",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/contracts/MyApp.Events.dll",
            LogicalProducerService = "PlacementService",
            LogicalConsumerService = "NotificationService"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema
        {
            Name = "PlacementUpdated",
            Type = "object",
            Properties = new()
            {
                new NormalizedProperty { Name = "placementId", Type = "string", Required = true, Nullable = false },
                new NormalizedProperty { Name = "status", Type = "string", Required = true, Nullable = false },
                new NormalizedProperty { Name = "notes", Type = "string", Required = false, Nullable = true }
            },
            Required = new() { "placementId", "status" }
        });

        // Act
        var result = await _service.ExtractSchemaAsync(integration);

        // Assert
        Assert.Equal(MessageSchemaState.Available, result.State);
        Assert.NotNull(result.NormalizedContract);
        Assert.Equal("PlacementUpdated", result.NormalizedContract!.Name);
        Assert.Single(result.NormalizedContract.Schemas);
        Assert.Equal(3, result.NormalizedContract.Schemas[0].Properties.Count);
        Assert.Equal("PlacementService", result.ProducerService);
        Assert.Equal("NotificationService", result.ConsumerService);
        Assert.Equal(ContractSourceType.Assembly, result.SourceType);
    }

    [Fact]
    public async Task ServiceBus_WithAssemblySchema_Normalizes()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "sb-1",
            Type = IntegrationType.ServiceBus,
            Endpoint = "my-namespace.servicebus.windows.net",
            Resource = "payment-topic",
            Consumer = "payment-subscription",
            ContractName = "PaymentProcessed",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/contracts/MyApp.Payments.dll",
            LogicalProducerService = "PaymentService",
            LogicalConsumerService = "ReportingService"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema { Name = "PaymentProcessed", Type = "object" });

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Available, result.State);
        Assert.NotNull(result.NormalizedContract);
        Assert.Equal("PaymentProcessed", result.NormalizedContract!.Name);
    }

    [Fact]
    public async Task Kafka_WithAssemblySchema_Normalizes()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "kafka-1",
            Type = IntegrationType.Kafka,
            Endpoint = "kafka-broker:9092",
            Resource = "user-events-topic",
            Consumer = "user-service-consumer",
            ContractName = "UserCreated",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/contracts/UserEvents.dll",
            LogicalConsumerService = "UserService"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema { Name = "UserCreated", Type = "object" });

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Available, result.State);
        Assert.Equal("UserService", result.ConsumerService);
        Assert.Null(result.ProducerService);  // Not configured
    }

    [Fact]
    public async Task RabbitMQ_WithAssemblySchema_Normalizes()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "rmq-1",
            Type = IntegrationType.RabbitMQ,
            Endpoint = "amqp://rabbitmq-host",
            Resource = "order-queue",
            Consumer = "order-processor",
            ContractName = "OrderCreated",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/contracts/OrderEvents.dll",
            LogicalProducerService = "OrderService",
            LogicalConsumerService = "FulfillmentService"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema { Name = "OrderCreated", Type = "object" });

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Available, result.State);
    }

    #endregion

    #region No Schema - Transport Metadata Only

    [Fact]
    public async Task EventHub_TransportMetadataOnly_ReturnsNotAvailable()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "eh-2",
            Type = IntegrationType.EventHub,
            Endpoint = "my-namespace",
            Resource = "my-hub",
            Consumer = "my-group"
            // No ContractName or ContractSourceType
        };

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.NotAvailable, result.State);
        Assert.Contains("transport metadata", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceBus_TransportMetadataOnly_ReturnsNotAvailable()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "sb-2",
            Type = IntegrationType.ServiceBus,
            Endpoint = "my-namespace.servicebus.windows.net",
            Resource = "my-topic",
            Consumer = "my-subscription"
            // No schema metadata
        };

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.NotAvailable, result.State);
    }

    [Fact]
    public async Task Kafka_TransportMetadataOnly_ReturnsNotAvailable()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "kafka-2",
            Type = IntegrationType.Kafka,
            Endpoint = "kafka-broker:9092",
            Resource = "topic-name",
            Consumer = "consumer-group"
        };

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.NotAvailable, result.State);
    }

    [Fact]
    public async Task RabbitMQ_TransportMetadataOnly_ReturnsNotAvailable()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "rmq-2",
            Type = IntegrationType.RabbitMQ,
            Endpoint = "amqp://host",
            Resource = "queue-name"
        };

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.NotAvailable, result.State);
    }

    #endregion

    #region Malformed Schema

    [Fact]
    public async Task EventHub_AssemblyNotFound_ReturnsMalformed()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "eh-3",
            Type = IntegrationType.EventHub,
            ContractName = "MyEvent",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/nonexistent/assembly.dll"
        };

        _assemblyInspector.SetupFailure("ArtifactNotFound", "File not found");

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Malformed, result.State);
        Assert.Contains("File not found", result.ErrorMessage!);
    }

    [Fact]
    public async Task EventHub_AssemblyInvalid_ReturnsMalformed()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "eh-4",
            Type = IntegrationType.EventHub,
            ContractName = "MyEvent",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/corrupted.dll"
        };

        _assemblyInspector.SetupFailure("AssemblyInvalid", "Not a valid PE assembly");

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Malformed, result.State);
    }

    #endregion

    #region Schema File (Not Yet Implemented)

    [Fact]
    public async Task EventHub_SchemaFilePath_ReturnsUnsupported()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "eh-5",
            Type = IntegrationType.EventHub,
            ContractName = "MyEvent",
            ContractSourceType = ContractSourceType.SchemaFile,
            ContractSourceLocation = "/schemas/myevent.json"
        };

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Unsupported, result.State);
        Assert.Contains("not yet implemented", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Remote Endpoint (Not Yet Implemented)

    [Fact]
    public async Task EventHub_RemoteEndpoint_ReturnsUnsupported()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "eh-6",
            Type = IntegrationType.EventHub,
            ContractName = "MyEvent",
            ContractSourceType = ContractSourceType.Endpoint,
            ContractSourceLocation = "https://schema-registry.example.com/MyEvent"
        };

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Unsupported, result.State);
    }

    #endregion

    #region Producer/Consumer Preservation

    [Fact]
    public async Task Producer_IsPreserved()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-1",
            Type = IntegrationType.EventHub,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/test.dll",
            LogicalProducerService = "MyProducer"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema { Name = "Event" });

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal("MyProducer", result.ProducerService);
    }

    [Fact]
    public async Task Consumer_IsPreserved()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-2",
            Type = IntegrationType.EventHub,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/test.dll",
            LogicalConsumerService = "MyConsumer"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema { Name = "Event" });

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal("MyConsumer", result.ConsumerService);
    }

    [Fact]
    public async Task ProducerAndConsumer_BothPreserved()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-3",
            Type = IntegrationType.EventHub,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/test.dll",
            LogicalProducerService = "Producer",
            LogicalConsumerService = "Consumer"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema { Name = "Event" });

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal("Producer", result.ProducerService);
        Assert.Equal("Consumer", result.ConsumerService);
    }

    #endregion

    #region Property Normalization

    [Fact]
    public async Task RequiredProperty_IsNormalized()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-4",
            Type = IntegrationType.EventHub,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/test.dll"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema
        {
            Name = "Event",
            Properties = new() { new NormalizedProperty { Name = "id", Type = "string", Required = true } },
            Required = new() { "id" }
        });

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Available, result.State);
        var prop = result.NormalizedContract!.Schemas[0].Properties[0];
        Assert.True(prop.Required);
        Assert.Contains("id", result.NormalizedContract.Schemas[0].Required);
    }

    [Fact]
    public async Task OptionalProperty_IsNormalized()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-5",
            Type = IntegrationType.EventHub,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/test.dll"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema
        {
            Name = "Event",
            Properties = new() { new NormalizedProperty { Name = "notes", Type = "string", Required = false } },
            Required = new()
        });

        var result = await _service.ExtractSchemaAsync(integration);

        var prop = result.NormalizedContract!.Schemas[0].Properties[0];
        Assert.False(prop.Required);
        Assert.DoesNotContain("notes", result.NormalizedContract.Schemas[0].Required);
    }

    [Fact]
    public async Task NullableProperty_IsNormalized()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-6",
            Type = IntegrationType.EventHub,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/test.dll"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema
        {
            Name = "Event",
            Properties = new() { new NormalizedProperty { Name = "notes", Type = "string", Nullable = true } },
            Required = new()
        });

        var result = await _service.ExtractSchemaAsync(integration);

        var prop = result.NormalizedContract!.Schemas[0].Properties[0];
        Assert.True(prop.Nullable);
    }

    #endregion

    #region Type Normalization

    [Fact]
    public async Task TypeInfo_IsPreserved()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-7",
            Type = IntegrationType.EventHub,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/test.dll"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema
        {
            Name = "Event",
            Type = "object",
            Properties = new()
            {
                new NormalizedProperty { Name = "id", Type = "string" },
                new NormalizedProperty { Name = "count", Type = "integer" },
                new NormalizedProperty { Name = "active", Type = "boolean" }
            },
            Required = new()
        });

        var result = await _service.ExtractSchemaAsync(integration);

        var schema = result.NormalizedContract!.Schemas[0];
        Assert.Equal("object", schema.Type);
        Assert.Equal("string", schema.Properties[0].Type);
        Assert.Equal("integer", schema.Properties[1].Type);
        Assert.Equal("boolean", schema.Properties[2].Type);
    }

    #endregion

    #region No Schema Configured

    [Fact]
    public async Task NoSchemaConfigured_ReturnsNotAvailable()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-8",
            Type = IntegrationType.EventHub
            // No schema metadata at all
        };

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.NotAvailable, result.State);
        Assert.Contains("not configured", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Unsupported Integration Type

    [Fact]
    public async Task NonMessagingType_ReturnsUnsupported()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-9",
            Type = IntegrationType.REST,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/test.dll"
        };

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(MessageSchemaState.Unsupported, result.State);
    }

    #endregion

    #region Contract Source Tracking

    [Fact]
    public async Task ContractSource_IsTracked()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "test-10",
            Type = IntegrationType.EventHub,
            ContractName = "Event",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/path/to/assembly.dll"
        };

        _assemblyInspector.SetupSuccess(new NormalizedSchema { Name = "Event" });

        var result = await _service.ExtractSchemaAsync(integration);

        Assert.Equal(ContractSourceType.Assembly, result.SourceType);
        Assert.Equal("/path/to/assembly.dll", result.SourceLocation);
        Assert.NotNull(result.NormalizedContract!.Source);
        Assert.Equal(ContractSourceType.Assembly, result.NormalizedContract.Source.Type);
        Assert.Equal("/path/to/assembly.dll", result.NormalizedContract.Source.Location);
    }

    #endregion

    #region Contract Comparer Compatibility

    [Fact]
    public async Task ExtractedContract_CanBeUsedByContractComparer()
    {
        // Verify extracted contracts can be directly consumed by ContractComparer
        // without creating a parallel comparison path

        var producerIntegration = new IntegrationConfigDto
        {
            Id = "producer",
            Type = IntegrationType.EventHub,
            ContractName = "OrderPlaced",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/producer.dll",
            LogicalProducerService = "OrderService"
        };

        var consumerIntegration = new IntegrationConfigDto
        {
            Id = "consumer",
            Type = IntegrationType.EventHub,
            ContractName = "OrderPlaced",
            ContractSourceType = ContractSourceType.Assembly,
            ContractSourceLocation = "/consumer.dll",
            LogicalConsumerService = "NotificationService"
        };

        var schema = new NormalizedSchema
        {
            Name = "OrderPlaced",
            Type = "object",
            Properties = new()
            {
                new NormalizedProperty { Name = "orderId", Type = "string", Required = true },
                new NormalizedProperty { Name = "customerId", Type = "string", Required = true }
            },
            Required = new() { "orderId", "customerId" }
        };

        _assemblyInspector.SetupSuccess(schema);

        var producerResult = await _service.ExtractSchemaAsync(producerIntegration);
        var consumerResult = await _service.ExtractSchemaAsync(consumerIntegration);

        // Contracts should be ready for direct consumption by ContractComparer
        Assert.Equal(MessageSchemaState.Available, producerResult.State);
        Assert.Equal(MessageSchemaState.Available, consumerResult.State);
        Assert.NotNull(producerResult.NormalizedContract);
        Assert.NotNull(consumerResult.NormalizedContract);

        // ContractComparer should be able to compare them
        var comparer = new ContractComparer();
        var comparison = comparer.Compare(
            producerResult.NormalizedContract!,
            consumerResult.NormalizedContract!,
            producerResult.ProducerService ?? "Unknown",
            consumerResult.ConsumerService ?? "Unknown",
            "OrderPlaced",
            producerResult.SourceLocation,
            consumerResult.SourceLocation);

        // Both compatible (same schema)
        Assert.True(comparison.Compatible);
    }

    #endregion

    // Fake implementation for testing
    private sealed class FakeAssemblyInspector : IAssemblyMetadataInspector
    {
        private NormalizedSchema? _successSchema;
        private string? _failureType;
        private string? _failureMessage;

        public void SetupSuccess(NormalizedSchema schema) => _successSchema = schema;
        public void SetupFailure(string failureType, string failureMessage)
        {
            _failureType = failureType;
            _failureMessage = failureMessage;
        }

        public Task<AssemblyContractExtractionResult> ExtractContractAsync(
            string assemblyPath,
            string contractTypeName,
            CancellationToken ct = default)
        {
            if (_failureType != null)
            {
                return Task.FromResult(new AssemblyContractExtractionResult
                {
                    Success = false,
                    FailureType = _failureType,
                    FailureMessage = _failureMessage
                });
            }

            return Task.FromResult(new AssemblyContractExtractionResult
            {
                Success = true,
                Schema = _successSchema ?? new NormalizedSchema { Name = contractTypeName },
                Source = new ContractSource
                {
                    Type = ContractSourceType.Assembly,
                    Location = assemblyPath,
                    FetchedAt = DateTime.UtcNow
                }
            });
        }
    }
}
