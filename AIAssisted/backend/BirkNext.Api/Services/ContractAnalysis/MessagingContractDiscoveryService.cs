using BirkNext.Api.Services.IntegrationQuality;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Discovers and compares message contracts for EventHub, ServiceBus, Kafka, RabbitMQ.
/// Requires independently resolvable producer and consumer contract sources.
/// Reuses normalized contract model and directional comparison logic from Phase 3/4.
/// </summary>
public interface IMessagingContractDiscoveryService
{
    Task<ContractCompatibilityResult> AnalyzeEventHubAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default);
}

public sealed class MessagingContractDiscoveryService : IMessagingContractDiscoveryService
{
    private readonly IAssemblyMetadataInspector _assemblyInspector;
    private readonly IContractComparer _comparer;
    private readonly ILogger<MessagingContractDiscoveryService> _logger;

    public MessagingContractDiscoveryService(
        IAssemblyMetadataInspector assemblyInspector,
        IContractComparer comparer,
        ILogger<MessagingContractDiscoveryService> logger)
    {
        _assemblyInspector = assemblyInspector;
        _comparer = comparer;
        _logger = logger;
    }

    public async Task<ContractCompatibilityResult> AnalyzeEventHubAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default)
    {
        // Verify producer and consumer are configured
        if (string.IsNullOrWhiteSpace(integration.LogicalProducerService))
        {
            return NotReady(
                integration,
                "Producer service not configured");
        }

        if (string.IsNullOrWhiteSpace(integration.LogicalConsumerService))
        {
            return NotReady(
                integration,
                "Consumer service not configured");
        }

        if (string.IsNullOrWhiteSpace(integration.ContractName))
        {
            return NotReady(
                integration,
                "Message contract name not configured");
        }

        // Verify producer contract source is configured
        if (!integration.ProducerContractSourceType.HasValue ||
            integration.ProducerContractSourceType == ContractSourceType.Unknown ||
            string.IsNullOrWhiteSpace(integration.ProducerContractSourceLocation))
        {
            return NotReady(
                integration,
                "Producer contract source not configured");
        }

        // Verify consumer contract source is configured
        if (!integration.ConsumerContractSourceType.HasValue ||
            integration.ConsumerContractSourceType == ContractSourceType.Unknown ||
            string.IsNullOrWhiteSpace(integration.ConsumerContractSourceLocation))
        {
            return NotReady(
                integration,
                "Consumer contract source not configured");
        }

        // Extract producer contract
        var producerResult = await ExtractContractAsync(
            integration.ProducerContractSourceType.Value,
            integration.ProducerContractSourceLocation,
            integration.ContractName,
            ct);

        if (!producerResult.Success)
        {
            return Error(
                integration,
                $"Failed to extract producer contract: {producerResult.FailureMessage}",
                null,
                integration.ProducerContractSourceLocation);
        }

        // Extract consumer contract
        var consumerResult = await ExtractContractAsync(
            integration.ConsumerContractSourceType.Value,
            integration.ConsumerContractSourceLocation,
            integration.ContractName,
            ct);

        if (!consumerResult.Success)
        {
            return Error(
                integration,
                $"Failed to extract consumer contract: {consumerResult.FailureMessage}",
                integration.ProducerContractSourceLocation,
                null);
        }

        // Compare contracts
        return _comparer.Compare(
            producerResult.Schema!,
            consumerResult.Schema!,
            integration.LogicalProducerService,
            integration.LogicalConsumerService,
            integration.ContractName,
            integration.ProducerContractSourceLocation,
            integration.ConsumerContractSourceLocation);
    }

    private async Task<(bool Success, NormalizedContract? Schema, string? FailureMessage)> ExtractContractAsync(
        ContractSourceType sourceType,
        string sourceLocation,
        string contractName,
        CancellationToken ct)
    {
        switch (sourceType)
        {
            case ContractSourceType.Assembly:
                return await ExtractFromAssemblyAsync(sourceLocation, contractName, ct);

            case ContractSourceType.SchemaFile:
                return await ExtractFromSchemaFileAsync(sourceLocation, contractName, ct);

            default:
                return (false, null, $"Contract source type '{sourceType}' is not supported in Phase 5");
        }
    }

    private async Task<(bool Success, NormalizedContract? Schema, string? FailureMessage)> ExtractFromAssemblyAsync(
        string assemblyPath,
        string contractTypeName,
        CancellationToken ct)
    {
        var result = await _assemblyInspector.ExtractContractAsync(assemblyPath, contractTypeName, ct);

        if (!result.Success)
            return (false, null, result.FailureMessage);

        var contract = new NormalizedContract
        {
            Name = contractTypeName,
            Source = result.Source!,
            Schemas = new() { result.Schema! }
        };

        return (true, contract, null);
    }

    private async Task<(bool Success, NormalizedContract? Schema, string? FailureMessage)> ExtractFromSchemaFileAsync(
        string schemaFilePath,
        string contractName,
        CancellationToken ct)
    {
        // TODO: Implement schema file parsing in future phase
        return (false, null, "Schema file extraction not implemented in Phase 5");
    }

    private static ContractCompatibilityResult NotReady(IntegrationConfigDto integration, string reason)
    {
        return new ContractCompatibilityResult
        {
            Status = ContractCompatibilityStatus.NotReady,
            AnalysisReadiness = ContractAnalysisReadiness.NotReady,
            Message = reason,
            Producer = integration.LogicalProducerService ?? "Unknown",
            Consumer = integration.LogicalConsumerService ?? "Unknown",
            Contract = integration.ContractName ?? "Unknown",
            ReadyReason = reason
        };
    }

    private static ContractCompatibilityResult Error(
        IntegrationConfigDto integration,
        string message,
        string? producerSource,
        string? consumerSource)
    {
        return new ContractCompatibilityResult
        {
            Status = ContractCompatibilityStatus.Error,
            AnalysisReadiness = ContractAnalysisReadiness.Error,
            Message = message,
            Producer = integration.LogicalProducerService ?? "Unknown",
            Consumer = integration.LogicalConsumerService ?? "Unknown",
            Contract = integration.ContractName ?? "Unknown",
            ProducerSource = producerSource,
            ConsumerSource = consumerSource
        };
    }
}
