namespace BirkNext.Web.Models;

/// <summary>
/// Shared wording and readiness rules for integration configuration, used by the Target
/// Environment editor and by the Integration Quality Review scope so the two cannot disagree.
///
/// Readiness here is about configuration completeness, not health. An integration missing a
/// namespace is not failing; it is not yet fully described.
/// </summary>
public static class IntegrationConfigPresenter
{
    /// <summary>
    /// Human-readable provenance. CodeSuggested deliberately reads as a source reading rather
    /// than verification: the values came from an audit of the target application's source, not
    /// from a running environment.
    /// </summary>
    public static string SourceLabel(IntegrationConfigurationSource source) => source switch
    {
        IntegrationConfigurationSource.EndpointDiscovery => "Discovered",
        IntegrationConfigurationSource.Manual => "Manual",
        IntegrationConfigurationSource.CodeSuggested => "Suggested from audited M2LB source",
        _ => "Unknown"
    };

    /// <summary>
    /// Integrations whose configuration comes primarily from observed traffic. Used to group the
    /// editor, not to store them differently: there is one integration model.
    /// </summary>
    public static bool IsHttpIntegration(IntegrationType type) =>
        type is IntegrationType.REST or IntegrationType.GraphQL;

    /// <summary>
    /// Fields still required before this integration can be reviewed as configured. Producer and
    /// consumer are never required: an unknown relationship is a legitimate state, and checks
    /// that need one report their own not-ready result.
    /// </summary>
    public static IReadOnlyList<string> MissingRequiredFields(IntegrationConfig integration)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(integration.Name))
            missing.Add("Name");

        switch (integration.Type)
        {
            case IntegrationType.REST:
            case IntegrationType.GraphQL:
                if (string.IsNullOrWhiteSpace(integration.Endpoint))
                    missing.Add("Endpoint");
                break;

            case IntegrationType.EventHub:
                if (string.IsNullOrWhiteSpace(integration.Endpoint))
                    missing.Add("Namespace");
                if (string.IsNullOrWhiteSpace(integration.Resource))
                    missing.Add("Event Hub name");
                break;

            case IntegrationType.ServiceBus:
                if (string.IsNullOrWhiteSpace(integration.Endpoint))
                    missing.Add("Namespace");
                if (integration.ResourceKind == IntegrationResourceKind.Unknown)
                    missing.Add("Resource kind");
                if (string.IsNullOrWhiteSpace(integration.Resource))
                    missing.Add("Entity name");
                if (integration.ResourceKind == IntegrationResourceKind.ServiceBusSubscription
                    && string.IsNullOrWhiteSpace(integration.Consumer))
                    missing.Add("Subscription name");
                break;

            case IntegrationType.Kafka:
                if (string.IsNullOrWhiteSpace(integration.Endpoint))
                    missing.Add("Broker");
                if (string.IsNullOrWhiteSpace(integration.Resource))
                    missing.Add("Topic");
                break;

            case IntegrationType.RabbitMQ:
                if (string.IsNullOrWhiteSpace(integration.Endpoint))
                    missing.Add("Broker");
                if (string.IsNullOrWhiteSpace(integration.Resource))
                    missing.Add("Exchange or queue");
                break;
        }

        return missing;
    }

    public static bool IsComplete(IntegrationConfig integration) =>
        MissingRequiredFields(integration).Count == 0;

    /// <summary>
    /// Configuration state for display. Incomplete configuration is stated as such and never as
    /// a failure or an unavailable runtime.
    /// </summary>
    public static string ConfigurationState(IntegrationConfig integration)
    {
        var missing = MissingRequiredFields(integration);

        return missing.Count == 0
            ? "Configuration complete"
            : $"Configuration incomplete · missing {string.Join(", ", missing)}";
    }

    // ── Field visibility. Only fields the transport actually has are shown. ──

    public static bool ShowsConsumerGroup(IntegrationConfig integration) =>
        integration.Type is IntegrationType.EventHub or IntegrationType.Kafka;

    public static bool ShowsSubscriptionName(IntegrationConfig integration) =>
        integration.Type == IntegrationType.ServiceBus
        && integration.ResourceKind == IntegrationResourceKind.ServiceBusSubscription;

    public static bool ShowsResourceKind(IntegrationConfig integration) =>
        integration.Type is IntegrationType.ServiceBus or IntegrationType.RabbitMQ;

    public static bool ShowsRoutingKey(IntegrationConfig integration) =>
        integration.Type == IntegrationType.RabbitMQ;

    /// <summary>Resource kinds offered for a transport. Empty where the transport has only one.</summary>
    public static IReadOnlyList<IntegrationResourceKind> ResourceKindChoices(IntegrationType type) => type switch
    {
        IntegrationType.ServiceBus =>
        [
            IntegrationResourceKind.ServiceBusQueue,
            IntegrationResourceKind.ServiceBusTopic,
            IntegrationResourceKind.ServiceBusSubscription
        ],
        IntegrationType.RabbitMQ =>
        [
            IntegrationResourceKind.RabbitQueue,
            IntegrationResourceKind.RabbitExchange
        ],
        _ => []
    };

    public static string ResourceKindLabel(IntegrationResourceKind kind) => kind switch
    {
        IntegrationResourceKind.RestEndpoint => "REST endpoint",
        IntegrationResourceKind.GraphQlEndpoint => "GraphQL endpoint",
        IntegrationResourceKind.EventHub => "Event Hub",
        IntegrationResourceKind.ServiceBusQueue => "Queue",
        IntegrationResourceKind.ServiceBusTopic => "Topic",
        IntegrationResourceKind.ServiceBusSubscription => "Subscription",
        IntegrationResourceKind.KafkaTopic => "Topic",
        IntegrationResourceKind.RabbitExchange => "Exchange",
        IntegrationResourceKind.RabbitQueue => "Queue",
        _ => "Unknown"
    };

    /// <summary>
    /// Applies a template as a starting point. Only values the audit established are written, so
    /// fields it could not establish stay empty and the form shows them as still needed.
    /// </summary>
    public static void ApplyTemplate(IntegrationConfig target, KnownIntegrationTemplate template)
    {
        target.Name = template.DisplayName;
        target.Type = template.IntegrationType;
        target.ResourceKind = template.ResourceKind;
        target.Resource = template.Resource;
        target.Endpoint = template.EndpointOrNamespace;
        target.Consumer = template.SuggestedConsumerGroup;
        target.LogicalProducerService = template.SuggestedProducer;
        target.LogicalConsumerService = template.SuggestedConsumer;
        target.ConfigurationSource = IntegrationConfigurationSource.CodeSuggested;
    }
}
