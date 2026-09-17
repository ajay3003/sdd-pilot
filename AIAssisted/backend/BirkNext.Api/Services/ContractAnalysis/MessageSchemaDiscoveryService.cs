using BirkNext.Api.Services.IntegrationQuality;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Phase 3 Checkpoint 3: Messaging Schema Discovery and Normalization
/// Extracts schema contracts from EventHub, ServiceBus, Kafka, RabbitMQ integrations.
/// Maps to existing NormalizedContract model. Preserves transport metadata separate from schema.
/// Handles: configured schemas, schema files, assembly inspection, remote endpoints.
/// Explicitly handles: no schema, malformed schema, unsupported schema.
/// </summary>
public interface IMessageSchemaDiscoveryService
{
    /// <summary>
    /// Extracts and normalizes message schema for a messaging integration.
    /// Does not require producer/consumer configuration.
    /// Returns explicit schema availability state.
    /// </summary>
    Task<MessageSchemaExtractionResult> ExtractSchemaAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default);
}

public sealed class MessageSchemaDiscoveryService : IMessageSchemaDiscoveryService
{
    private readonly IAssemblyMetadataInspector _assemblyInspector;
    private readonly ILogger<MessageSchemaDiscoveryService> _logger;

    public MessageSchemaDiscoveryService(
        IAssemblyMetadataInspector assemblyInspector,
        ILogger<MessageSchemaDiscoveryService> logger)
    {
        _assemblyInspector = assemblyInspector;
        _logger = logger;
    }

    public async Task<MessageSchemaExtractionResult> ExtractSchemaAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default)
    {
        // Validate this is a messaging type
        if (!IsSupportedMessagingType(integration.Type))
        {
            return MessageSchemaExtractionResult.Unsupported(
                integration.Id,
                integration.ContractName ?? "Unknown",
                $"{integration.Type} is not a supported messaging type");
        }

        // Transport metadata only (not a schema)
        // Explicitly distinguish: Endpoint (namespace), Resource (topic/queue), Consumer (group)
        // These are transport topology, NOT message contract
        if (IsTransportMetadataOnly(integration))
        {
            return MessageSchemaExtractionResult.NoSchemaAvailable(
                integration.Id,
                integration.ContractName ?? integration.Resource ?? "Unknown",
                "Only transport metadata provided. No message schema available.");
        }

        // No schema metadata configured
        if (string.IsNullOrWhiteSpace(integration.ContractName) &&
            integration.ContractSourceType == ContractSourceType.Unknown)
        {
            return MessageSchemaExtractionResult.NoSchemaAvailable(
                integration.Id,
                integration.Resource ?? "Unknown",
                "No schema configured. Configure ContractName and ContractSourceType to enable schema extraction.");
        }

        // Extract schema from configured source
        return await ExtractFromSourceAsync(integration, ct);
    }

    private async Task<MessageSchemaExtractionResult> ExtractFromSourceAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default)
    {
        var contractName = integration.ContractName ?? "UnknownMessage";

        return integration.ContractSourceType switch
        {
            ContractSourceType.Assembly =>
                await ExtractFromAssemblyAsync(integration, contractName, ct),

            ContractSourceType.SchemaFile =>
                ExtractFromSchemaFile(integration, contractName),

            ContractSourceType.Endpoint =>
                ExtractFromEndpoint(integration, contractName),

            ContractSourceType.Unknown =>
                MessageSchemaExtractionResult.NoSchemaAvailable(
                    integration.Id,
                    contractName,
                    "Contract source type not configured."),

            _ =>
                MessageSchemaExtractionResult.Unsupported(
                    integration.Id,
                    contractName,
                    $"Contract source type '{integration.ContractSourceType}' is not yet supported for messaging.")
        };
    }

    private async Task<MessageSchemaExtractionResult> ExtractFromAssemblyAsync(
        IntegrationConfigDto integration,
        string contractName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(integration.ContractSourceLocation))
        {
            return MessageSchemaExtractionResult.Malformed(
                integration.Id,
                contractName,
                "Assembly path not configured.");
        }

        try
        {
            var result = await _assemblyInspector.ExtractContractAsync(
                integration.ContractSourceLocation,
                contractName,
                ct);

            if (!result.Success)
            {
                return MessageSchemaExtractionResult.Malformed(
                    integration.Id,
                    contractName,
                    result.FailureMessage ?? "Assembly inspection failed.");
            }

            // Wrap assembly schema in NormalizedContract with producer/consumer from Checkpoint 1
            var contract = new NormalizedContract
            {
                Name = contractName,
                Source = new ContractSource
                {
                    Type = ContractSourceType.Assembly,
                    Location = integration.ContractSourceLocation,
                    FetchedAt = DateTime.UtcNow
                },
                Schemas = new() { result.Schema! }
            };

            return MessageSchemaExtractionResult.Success(
                integration.Id,
                contractName,
                contract,
                ContractSourceType.Assembly,
                integration.ContractSourceLocation,
                integration.LogicalProducerService,
                integration.LogicalConsumerService);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting assembly schema for {ContractName}", contractName);
            return MessageSchemaExtractionResult.Malformed(
                integration.Id,
                contractName,
                $"Assembly inspection failed: {ex.Message}");
        }
    }

    private MessageSchemaExtractionResult ExtractFromSchemaFile(
        IntegrationConfigDto integration,
        string contractName)
    {
        // Schema file parsing not yet implemented
        // Placeholder for future JSON/YAML/Protobuf schema file support
        return MessageSchemaExtractionResult.Unsupported(
            integration.Id,
            contractName,
            "Schema file extraction is not yet implemented.");
    }

    private MessageSchemaExtractionResult ExtractFromEndpoint(
        IntegrationConfigDto integration,
        string contractName)
    {
        // Remote endpoint schema discovery not yet implemented
        // Placeholder for future schema registry/remote endpoint support
        return MessageSchemaExtractionResult.Unsupported(
            integration.Id,
            contractName,
            "Remote endpoint schema extraction is not yet implemented.");
    }

    private static bool IsSupportedMessagingType(IntegrationType type)
    {
        return type switch
        {
            IntegrationType.EventHub => true,
            IntegrationType.ServiceBus => true,
            IntegrationType.Kafka => true,
            IntegrationType.RabbitMQ => true,
            _ => false
        };
    }

    private static bool IsTransportMetadataOnly(IntegrationConfigDto integration)
    {
        // Transport metadata present but no actual schema metadata
        var hasTransportMetadata = !string.IsNullOrWhiteSpace(integration.Endpoint) ||
                                   !string.IsNullOrWhiteSpace(integration.Resource) ||
                                   !string.IsNullOrWhiteSpace(integration.Consumer);

        var hasSchemaMetadata = !string.IsNullOrWhiteSpace(integration.ContractName) &&
                                (integration.ContractSourceType != ContractSourceType.Unknown ||
                                 !string.IsNullOrWhiteSpace(integration.ContractSourceLocation));

        return hasTransportMetadata && !hasSchemaMetadata;
    }
}

/// <summary>
/// Result of messaging schema extraction attempt.
/// Explicitly tracks: available, unavailable, malformed, unsupported.
/// </summary>
public sealed class MessageSchemaExtractionResult
{
    public string IntegrationId { get; init; } = "";
    public string ContractName { get; init; } = "";
    public MessageSchemaState State { get; init; }
    public NormalizedContract? NormalizedContract { get; init; }
    public ContractSourceType? SourceType { get; init; }
    public string? SourceLocation { get; init; }
    public string? ProducerService { get; init; }
    public string? ConsumerService { get; init; }
    public string? ErrorMessage { get; init; }

    public static MessageSchemaExtractionResult Success(
        string integrationId,
        string contractName,
        NormalizedContract contract,
        ContractSourceType sourceType,
        string sourceLocation,
        string? producer = null,
        string? consumer = null)
    {
        return new MessageSchemaExtractionResult
        {
            IntegrationId = integrationId,
            ContractName = contractName,
            State = MessageSchemaState.Available,
            NormalizedContract = contract,
            SourceType = sourceType,
            SourceLocation = sourceLocation,
            ProducerService = producer,
            ConsumerService = consumer
        };
    }

    public static MessageSchemaExtractionResult NoSchemaAvailable(
        string integrationId,
        string contractName,
        string reason)
    {
        return new MessageSchemaExtractionResult
        {
            IntegrationId = integrationId,
            ContractName = contractName,
            State = MessageSchemaState.NotAvailable,
            ErrorMessage = reason
        };
    }

    public static MessageSchemaExtractionResult Malformed(
        string integrationId,
        string contractName,
        string reason)
    {
        return new MessageSchemaExtractionResult
        {
            IntegrationId = integrationId,
            ContractName = contractName,
            State = MessageSchemaState.Malformed,
            ErrorMessage = reason
        };
    }

    public static MessageSchemaExtractionResult Unsupported(
        string integrationId,
        string contractName,
        string reason)
    {
        return new MessageSchemaExtractionResult
        {
            IntegrationId = integrationId,
            ContractName = contractName,
            State = MessageSchemaState.Unsupported,
            ErrorMessage = reason
        };
    }
}

public enum MessageSchemaState
{
    Available = 0,     // Schema extracted successfully
    NotAvailable = 1,  // No schema configured/available
    Malformed = 2,     // Schema exists but cannot be parsed
    Unsupported = 3    // Schema format or integration type not supported
}
