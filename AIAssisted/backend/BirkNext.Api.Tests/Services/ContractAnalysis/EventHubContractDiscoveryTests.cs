using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Tests.Services.ContractAnalysis;

/// <summary>
/// Tests for EventHub messaging contract discovery (Phase 5).
/// Verifies: independent producer/consumer sources, NotReady states, compatibility analysis.
/// </summary>
public class EventHubContractDiscoveryTests
{
    private readonly Mock<IAssemblyMetadataInspector> _assemblyInspectorMock;
    private readonly Mock<IContractComparer> _comparerMock;
    private readonly Mock<ILogger<MessagingContractDiscoveryService>> _loggerMock;
    private readonly MessagingContractDiscoveryService _service;

    public EventHubContractDiscoveryTests()
    {
        _assemblyInspectorMock = new Mock<IAssemblyMetadataInspector>();
        _comparerMock = new Mock<IContractComparer>();
        _loggerMock = new Mock<ILogger<MessagingContractDiscoveryService>>();
        _service = new MessagingContractDiscoveryService(
            _assemblyInspectorMock.Object,
            _comparerMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ProducerNotConfigured_ReturnsNotReady()
    {
        // Arrange
        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = null, // Missing producer
            LogicalConsumerService = "PlacementService",
            ContractName = "ChildUpdated",
            ProducerContractSourceType = ContractSourceType.Assembly,
            ProducerContractSourceLocation = "/path/to/producer.dll",
            ConsumerContractSourceType = ContractSourceType.Assembly,
            ConsumerContractSourceLocation = "/path/to/consumer.dll"
        };

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.NotReady, result.Status);
        Assert.Equal(ContractAnalysisReadiness.NotReady, result.AnalysisReadiness);
        Assert.Contains("Producer service not configured", result.Message);
    }

    [Fact]
    public async Task ConsumerNotConfigured_ReturnsNotReady()
    {
        // Arrange
        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = "ChildService",
            LogicalConsumerService = null, // Missing consumer
            ContractName = "ChildUpdated",
            ProducerContractSourceType = ContractSourceType.Assembly,
            ProducerContractSourceLocation = "/path/to/producer.dll",
            ConsumerContractSourceType = ContractSourceType.Assembly,
            ConsumerContractSourceLocation = "/path/to/consumer.dll"
        };

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.NotReady, result.Status);
        Assert.Contains("Consumer service not configured", result.Message);
    }

    [Fact]
    public async Task ContractNameNotConfigured_ReturnsNotReady()
    {
        // Arrange
        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = "ChildService",
            LogicalConsumerService = "PlacementService",
            ContractName = null, // Missing contract name
            ProducerContractSourceType = ContractSourceType.Assembly,
            ProducerContractSourceLocation = "/path/to/producer.dll",
            ConsumerContractSourceType = ContractSourceType.Assembly,
            ConsumerContractSourceLocation = "/path/to/consumer.dll"
        };

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.NotReady, result.Status);
        Assert.Contains("contract name not configured", result.Message);
    }

    [Fact]
    public async Task ProducerSourceNotConfigured_ReturnsNotReady()
    {
        // Arrange
        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = "ChildService",
            LogicalConsumerService = "PlacementService",
            ContractName = "ChildUpdated",
            ProducerContractSourceType = null, // Missing producer source type
            ProducerContractSourceLocation = null,
            ConsumerContractSourceType = ContractSourceType.Assembly,
            ConsumerContractSourceLocation = "/path/to/consumer.dll"
        };

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.NotReady, result.Status);
        Assert.Contains("Producer contract source not configured", result.Message);
    }

    [Fact]
    public async Task ConsumerSourceNotConfigured_ReturnsNotReady()
    {
        // Arrange
        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = "ChildService",
            LogicalConsumerService = "PlacementService",
            ContractName = "ChildUpdated",
            ProducerContractSourceType = ContractSourceType.Assembly,
            ProducerContractSourceLocation = "/path/to/producer.dll",
            ConsumerContractSourceType = null, // Missing consumer source type
            ConsumerContractSourceLocation = null
        };

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.NotReady, result.Status);
        Assert.Contains("Consumer contract source not configured", result.Message);
    }

    [Fact]
    public async Task ProducerExtractionFails_ReturnsError()
    {
        // Arrange
        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = "ChildService",
            LogicalConsumerService = "PlacementService",
            ContractName = "ChildUpdated",
            ProducerContractSourceType = ContractSourceType.Assembly,
            ProducerContractSourceLocation = "/path/to/producer.dll",
            ConsumerContractSourceType = ContractSourceType.Assembly,
            ConsumerContractSourceLocation = "/path/to/consumer.dll"
        };

        _assemblyInspectorMock
            .Setup(x => x.ExtractContractAsync("/path/to/producer.dll", "ChildUpdated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssemblyContractExtractionResult
            {
                Success = false,
                FailureType = "ArtifactNotFound",
                FailureMessage = "Assembly not found"
            });

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.Error, result.Status);
        Assert.Contains("Failed to extract producer contract", result.Message);
    }

    [Fact]
    public async Task ConsumerExtractionFails_ReturnsError()
    {
        // Arrange
        var producerSchema = new NormalizedSchema { Name = "ChildUpdated", Type = "object" };
        var producerResult = new AssemblyContractExtractionResult
        {
            Success = true,
            Schema = producerSchema,
            Source = new ContractSource { Type = ContractSourceType.Assembly }
        };

        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = "ChildService",
            LogicalConsumerService = "PlacementService",
            ContractName = "ChildUpdated",
            ProducerContractSourceType = ContractSourceType.Assembly,
            ProducerContractSourceLocation = "/path/to/producer.dll",
            ConsumerContractSourceType = ContractSourceType.Assembly,
            ConsumerContractSourceLocation = "/path/to/consumer.dll"
        };

        _assemblyInspectorMock
            .Setup(x => x.ExtractContractAsync("/path/to/producer.dll", "ChildUpdated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(producerResult);

        _assemblyInspectorMock
            .Setup(x => x.ExtractContractAsync("/path/to/consumer.dll", "ChildUpdated", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssemblyContractExtractionResult
            {
                Success = false,
                FailureType = "ContractTypeNotFound",
                FailureMessage = "Type 'ChildUpdated' not found"
            });

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.Error, result.Status);
        Assert.Contains("Failed to extract consumer contract", result.Message);
    }

    [Fact]
    public async Task CompatibleContracts_ReturnsPass()
    {
        // Arrange
        var schema = new NormalizedSchema
        {
            Name = "ChildUpdated",
            Type = "object",
            Properties = new()
            {
                new NormalizedProperty { Name = "id", Type = "string", Required = true }
            },
            Required = new() { "id" }
        };

        var producerResult = new AssemblyContractExtractionResult
        {
            Success = true,
            Schema = schema,
            Source = new ContractSource { Type = ContractSourceType.Assembly }
        };

        var consumerResult = new AssemblyContractExtractionResult
        {
            Success = true,
            Schema = schema,
            Source = new ContractSource { Type = ContractSourceType.Assembly }
        };

        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = "ChildService",
            LogicalConsumerService = "PlacementService",
            ContractName = "ChildUpdated",
            ProducerContractSourceType = ContractSourceType.Assembly,
            ProducerContractSourceLocation = "/path/to/producer.dll",
            ConsumerContractSourceType = ContractSourceType.Assembly,
            ConsumerContractSourceLocation = "/path/to/consumer.dll"
        };

        _assemblyInspectorMock
            .Setup(x => x.ExtractContractAsync(It.IsAny<string>(), "ChildUpdated", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, string type, CancellationToken ct) =>
                path.Contains("producer") ? producerResult : consumerResult);

        var compatibleResult = new ContractCompatibilityResult
        {
            Compatible = true,
            Status = ContractCompatibilityStatus.Compatible,
            Producer = "ChildService",
            Consumer = "PlacementService",
            Contract = "ChildUpdated"
        };

        _comparerMock
            .Setup(x => x.Compare(It.IsAny<NormalizedContract>(), It.IsAny<NormalizedContract>(),
                "ChildService", "PlacementService", "ChildUpdated", It.IsAny<string>(), It.IsAny<string>()))
            .Returns(compatibleResult);

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.Compatible, result.Status);
        Assert.True(result.Compatible);
    }

    [Fact]
    public async Task UnsupportedSourceType_ReturnsUnsupported()
    {
        // Arrange
        var integration = new IntegrationConfigDto
        {
            Type = IntegrationType.EventHub,
            Name = "Child Events",
            LogicalProducerService = "ChildService",
            LogicalConsumerService = "PlacementService",
            ContractName = "ChildUpdated",
            ProducerContractSourceType = ContractSourceType.Endpoint, // Not yet supported
            ProducerContractSourceLocation = "https://api.example.com/schema",
            ConsumerContractSourceType = ContractSourceType.Assembly,
            ConsumerContractSourceLocation = "/path/to/consumer.dll"
        };

        // Act
        var result = await _service.AnalyzeEventHubAsync(integration);

        // Assert
        Assert.Equal(ContractCompatibilityStatus.Error, result.Status);
        Assert.Contains("not supported", result.Message.ToLower());
    }
}
