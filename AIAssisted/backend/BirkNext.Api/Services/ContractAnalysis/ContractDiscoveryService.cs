using BirkNext.Api.Services.IntegrationQuality;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Orchestrates REST/GraphQL contract discovery and compatibility analysis.
/// Coordinates fetching, extraction, normalization, and comparison.
/// Routes by IntegrationType: REST → OpenAPI path, GraphQL → GraphQL path.
/// Reuses normalized contract model and comparison infrastructure.
/// </summary>
public interface IContractDiscoveryService
{
    Task<ContractCompatibilityResult> AnalyzeAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default);
}

public sealed class ContractDiscoveryService : IContractDiscoveryService
{
    private readonly IOpenApiSourceFetcher _openApiFetcher;
    private readonly IOpenApiExtractor _openApiExtractor;
    private readonly IGraphQlSourceFetcher _graphQlFetcher;
    private readonly IGraphQlExtractor _graphQlExtractor;
    private readonly IContractComparer _comparer;
    private readonly ILogger<ContractDiscoveryService> _logger;

    public ContractDiscoveryService(
        IOpenApiSourceFetcher openApiFetcher,
        IOpenApiExtractor openApiExtractor,
        IGraphQlSourceFetcher graphQlFetcher,
        IGraphQlExtractor graphQlExtractor,
        IContractComparer comparer,
        ILogger<ContractDiscoveryService> logger)
    {
        _openApiFetcher = openApiFetcher;
        _openApiExtractor = openApiExtractor;
        _graphQlFetcher = graphQlFetcher;
        _graphQlExtractor = graphQlExtractor;
        _comparer = comparer;
        _logger = logger;
    }

    public async Task<ContractCompatibilityResult> AnalyzeAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default)
    {
        // Route by integration type
        return integration.Type switch
        {
            IntegrationType.REST => await AnalyzeRestAsync(integration, ct),
            IntegrationType.GraphQL => await AnalyzeGraphQlAsync(integration, ct),
            _ => new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.Unsupported,
                AnalysisReadiness = ContractAnalysisReadiness.Unsupported,
                Message = $"Contract analysis not supported for {integration.Type} integrations"
            }
        };
    }

    private async Task<ContractCompatibilityResult> AnalyzeRestAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default)
    {
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

        // Single-service analysis: fetch and validate schema
        var fetchResult = await _openApiFetcher.FetchAsync(source, ct);
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

        var extractResult = _openApiExtractor.Extract(fetchResult.Content!, integration.ContractName);
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

        // Consumer service not configured
        if (string.IsNullOrWhiteSpace(integration.LogicalConsumerService))
        {
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

        // Cross-service comparison not yet implemented
        return new ContractCompatibilityResult
        {
            Status = ContractCompatibilityStatus.Unsupported,
            AnalysisReadiness = ContractAnalysisReadiness.Unsupported,
            Message = "Cross-service contract compatibility analysis will be available in a future phase",
            Producer = integration.LogicalProducerService,
            Consumer = integration.LogicalConsumerService,
            Contract = integration.ContractName ?? "Unknown"
        };
    }

    private async Task<ContractCompatibilityResult> AnalyzeGraphQlAsync(
        IntegrationConfigDto integration,
        CancellationToken ct = default)
    {
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
            ? integration.Endpoint  // For Auto mode, use GraphQL endpoint
            : integration.ContractSourceLocation;

        if (string.IsNullOrWhiteSpace(source))
        {
            return new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.NotReady,
                AnalysisReadiness = ContractAnalysisReadiness.NotReady,
                Message = "GraphQL endpoint or schema source not configured",
                Producer = integration.LogicalProducerService ?? "Unknown",
                Consumer = integration.LogicalConsumerService ?? "Unknown",
                Contract = integration.ContractName ?? "Unknown"
            };
        }

        // Fetch GraphQL schema via introspection
        var fetchResult = await _graphQlFetcher.FetchAsync(source, ct);
        if (!fetchResult.Success)
        {
            // Handle introspection-disabled case specially
            if (fetchResult.Reason == GraphQlFetchFailureReason.IntrospectionDisabled)
            {
                return new ContractCompatibilityResult
                {
                    Status = ContractCompatibilityStatus.NotReady,
                    AnalysisReadiness = ContractAnalysisReadiness.NotReady,
                    Message = "GraphQL schema unavailable: introspection disabled on endpoint. Provide an explicit SDL/schema artifact.",
                    Producer = integration.LogicalProducerService ?? "Unknown",
                    Consumer = integration.LogicalConsumerService ?? "Unknown",
                    Contract = integration.ContractName ?? "Unknown",
                    ProducerSource = RedactUrl(source)
                };
            }

            return new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.Error,
                AnalysisReadiness = ContractAnalysisReadiness.Error,
                Message = $"Failed to fetch GraphQL schema: {fetchResult.FailureMessage}",
                Producer = integration.LogicalProducerService ?? "Unknown",
                Consumer = integration.LogicalConsumerService ?? "Unknown",
                Contract = integration.ContractName ?? "Unknown",
                ProducerSource = RedactUrl(source)
            };
        }

        // Extract and normalize GraphQL schema
        var extractResult = _graphQlExtractor.Extract(fetchResult.SchemaJson!);
        if (!extractResult.Success)
        {
            return new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.Error,
                AnalysisReadiness = ContractAnalysisReadiness.Error,
                Message = $"Failed to parse GraphQL schema: {extractResult.FailureMessage}",
                Producer = integration.LogicalProducerService ?? "Unknown",
                Consumer = integration.LogicalConsumerService ?? "Unknown",
                Contract = integration.ContractName ?? "Unknown",
                ProducerSource = RedactUrl(source)
            };
        }

        // Consumer service not configured
        if (string.IsNullOrWhiteSpace(integration.LogicalConsumerService))
        {
            return new ContractCompatibilityResult
            {
                Status = ContractCompatibilityStatus.NotReady,
                AnalysisReadiness = ContractAnalysisReadiness.NotReady,
                Message = "Consumer service not configured - cross-service comparison requires both producer and consumer sources",
                Producer = integration.LogicalProducerService ?? "Unknown",
                Consumer = "Not configured",
                Contract = integration.ContractName ?? "Unknown",
                ProducerSource = RedactUrl(source)
            };
        }

        // Cross-service comparison not yet implemented
        return new ContractCompatibilityResult
        {
            Status = ContractCompatibilityStatus.Unsupported,
            AnalysisReadiness = ContractAnalysisReadiness.Unsupported,
            Message = "Cross-service GraphQL contract compatibility will be available in a future phase",
            Producer = integration.LogicalProducerService,
            Consumer = integration.LogicalConsumerService,
            Contract = integration.ContractName ?? "Unknown",
            ProducerSource = RedactUrl(source)
        };
    }

    private static string RedactUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return "";

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return "[redacted]";

            if (string.IsNullOrEmpty(uri.Query))
                return uri.GetLeftPart(UriPartial.Path);

            return $"{uri.GetLeftPart(UriPartial.Path)}?[query]";
        }
        catch
        {
            return "[redacted]";
        }
    }
}
