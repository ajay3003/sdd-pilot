using BirkNext.Api.Services.IntegrationQuality;

namespace BirkNext.Api.Services;

/// <summary>
/// Populates and tracks authoritative producer/consumer relationships for integrations.
/// Uses explicit configuration as the primary source, with fallbacks to messaging metadata.
/// Never derives relationships from display names.
/// </summary>
public sealed class IntegrationRelationshipPopulationService
{
    /// <summary>
    /// Populates producer/consumer relationships with source tracking.
    /// Respects existing configured relationships and only fills missing ones.
    /// </summary>
    public void PopulateRelationships(List<IntegrationConfigDto> integrations)
    {
        foreach (var integration in integrations)
        {
            PopulateRelationship(integration);
        }
    }

    /// <summary>
    /// Populates a single integration's producer/consumer relationship.
    /// Uses this precedence:
    /// 1. Explicitly configured (LogicalProducerService/Consumer) → Configured
    /// 2. Inferred from messaging metadata (Consumer + type) → MessagingMetadata
    /// 3. Discovered from runtime (future phase) → DiscoveredTraffic
    /// 4. Unknown → Unknown
    /// </summary>
    public void PopulateRelationship(IntegrationConfigDto integration)
    {
        // Priority 1: Explicitly configured
        if (!string.IsNullOrWhiteSpace(integration.LogicalProducerService) ||
            !string.IsNullOrWhiteSpace(integration.LogicalConsumerService))
        {
            integration.ProducerConsumerSource = RelationshipSource.Configured;
            return;
        }

        // Priority 2: Infer from messaging metadata
        if (TryPopulateFromMessagingMetadata(integration))
        {
            integration.ProducerConsumerSource = RelationshipSource.MessagingMetadata;
            return;
        }

        // Priority 3: Discovered traffic (placeholder for future)
        // TryPopulateFromDiscoveredTraffic(integration)

        // Otherwise unknown
        integration.ProducerConsumerSource = RelationshipSource.Unknown;
    }

    /// <summary>
    /// Attempts to infer producer/consumer from messaging integration metadata.
    /// For messaging integrations, the Consumer field identifies the consumer service.
    /// The producer must be explicitly configured or discovered separately.
    /// </summary>
    private static bool TryPopulateFromMessagingMetadata(IntegrationConfigDto integration)
    {
        // Messaging integrations have a Consumer field
        var isMessagingIntegration = integration.Type is IntegrationType.EventHub or
                                      IntegrationType.ServiceBus or
                                      IntegrationType.Kafka or
                                      IntegrationType.RabbitMQ;

        if (!isMessagingIntegration)
            return false;

        // Only populate if Consumer is configured (identifies the consumer service)
        if (string.IsNullOrWhiteSpace(integration.Consumer))
            return false;

        // If Consumer is set, use it as the consumer service
        // Producer must be configured separately (explicit LogicalProducerService)
        if (string.IsNullOrWhiteSpace(integration.LogicalConsumerService))
        {
            // Infer consumer service name from consumer group/subscription name
            // For now, mark the consumer as populated from messaging metadata
            integration.LogicalConsumerService = NormalizeConsumerServiceName(integration.Consumer, integration.Type);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a consumer group/subscription name to a service name.
    /// Uses heuristics for common naming patterns; otherwise preserves the name.
    /// </summary>
    private static string NormalizeConsumerServiceName(string consumerIdentifier, IntegrationType type)
    {
        // For now, return the identifier as-is
        // In the future, could apply pattern-based normalization
        // e.g., "subscription-xyz" → "XyzService"
        return consumerIdentifier;
    }
}
