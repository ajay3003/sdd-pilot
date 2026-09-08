using BirkNext.Api.Services.IntegrationQuality;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Orchestrates REST/OpenAPI contract discovery and compatibility analysis.
/// Coordinates fetching, extraction, normalization, and comparison.
/// </summary>
public interface IContractDiscoveryService
{
    Task<ContractCompatibilityResult> AnalyzeAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default);
}

public sealed class ContractDiscoveryService : IContractDiscoveryService
{
    private readonly IOpenApiSourceFetcher _fetcher;
    private readonly IOpenApiExtractor _extractor;
    private readonly IContractComparer _comparer;
    private readonly ILogger<ContractDiscoveryService> _logger;

    public ContractDiscoveryService(
        IOpenApiSourceFetcher fetcher,
        IOpenApiExtractor extractor,
        IContractComparer comparer,
        ILogger<ContractDiscoveryService> logger)
    {
        _fetcher = fetcher;
        _extractor = extractor;
        _comparer = comparer;
        _logger = logger;
    }

    public async Task<ContractCompatibilityResult> AnalyzeAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default)
    {
        // Only REST in Phase 3
        if (integration.Type != IntegrationType.REST)
        {
            return new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.Unsupported,
                AnalysisReadiness = ContractAnalysisReadiness.Unsupported,
                Message = "Contract analysis is only available for REST integrations in Phase 3"
            };
        }

        // Verify metadata readiness
        integration.ComputeReadiness();
        if (integration.ContractMetadataReadiness != ContractMetadataReadiness.Ready)
        {
            return new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.NotReady,
                AnalysisReadiness = ContractAnalysisReadiness.NotReady,
                Message = $"Contract metadata not ready: {integration.ContractMetadataReadiness}",
                Producer = integration.LogicalProducerService ?? "Unknown",
                Consumer = integration.LogicalConsumerService ?? "Unknown",
                Contract = integration.ContractName ?? "Unknown"
            };
        }

        // Determine source (explicit or Auto mode)
        var source = integration.ContractSourceType == ContractSourceType.Auto
            ? integration.Endpoint  // For Auto mode, use REST endpoint as OpenAPI location
            : integration.ContractSourceLocation;

        if (string.IsNullOrWhiteSpace(source))
        {
            return new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.NotReady,
                AnalysisReadiness = ContractAnalysisReadiness.NotReady,
                Message = "Contract source location not configured",
                Producer = integration.LogicalProducerService ?? "Unknown",
                Consumer = integration.LogicalConsumerService ?? "Unknown",
                Contract = integration.ContractName ?? "Unknown"
            };
        }

        // For Phase 3, we have single source - cannot do cross-service comparison
        // Return informational result about insufficient sources
        if (string.IsNullOrWhiteSpace(integration.LogicalConsumerService))
        {
            _logger.LogInformation(
                "Integration {IntegrationId}: Consumer service not configured for cross-service comparison",
                integration.Id);

            // Single-service analysis: fetch and validate schema
            var fetchResult = await _fetcher.FetchAsync(source, ct);
            if (!fetchResult.Success)
            {
                return new ContractCompatibilityResult
                {
                    Status = ContractCompatibilityStatus.Error,
                    AnalysisReadiness = ContractAnalysisReadiness.Error,
                    Message = $"Failed to fetch OpenAPI: {fetchResult.FailureMessage}",
                    Producer = integration.LogicalProducerService ?? "Unknown",
                    Consumer = integration.LogicalConsumerService ?? "Unknown",
                    Contract = integration.ContractName ?? "Unknown",
                    ProducerSource = fetchResult.SafeUrl
                };
            }

            var extractResult = _extractor.Extract(fetchResult.Content!, integration.ContractName);
            if (!extractResult.Success)
            {
                return new ContractCompatibilityResult
                {
                    Status = ContractCompatibilityStatus.Error,
                    AnalysisReadiness = ContractAnalysisReadiness.Error,
                    Message = $"Failed to parse OpenAPI: {extractResult.ErrorMessage}",
                    Producer = integration.LogicalProducerService ?? "Unknown",
                    Contract = integration.ContractName ?? "Unknown",
                    ProducerSource = fetchResult.SafeUrl
                };
            }

            // Single source - cannot compare
            return new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.NotReady,
                AnalysisReadiness = ContractAnalysisReadiness.NotReady,
                Message = "Consumer service not configured - cross-service comparison requires both producer and consumer sources",
                Producer = integration.LogicalProducerService ?? "Unknown",
                Consumer = "Not configured",
                Contract = integration.ContractName ?? "Unknown",
                ProducerSource = fetchResult.SafeUrl
            };
        }

        // Cross-service comparison not supported yet in Phase 3
        // Phase 3 only does single-service schema validation
        return new ContractCompatibilityResult
        {
            Status = ContractCompatibilityStatus.Unsupported,
            AnalysisReadiness = ContractAnalysisReadiness.Unsupported,
            Message = "Cross-service contract compatibility analysis is planned for Phase 4",
            Producer = integration.LogicalProducerService,
            Consumer = integration.LogicalConsumerService,
            Contract = integration.ContractName ?? "Unknown"
        };
    }
}
