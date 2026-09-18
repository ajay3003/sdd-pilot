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
    /// <summary>Transport name as a user reads it, for grouping and preview headings.</summary>
    public static string TypeLabel(IntegrationType type) => type switch
    {
        IntegrationType.EventHub => "Event Hub",
        IntegrationType.ServiceBus => "Service Bus",
        IntegrationType.GraphQL => "GraphQL",
        IntegrationType.RabbitMQ => "RabbitMQ",
        IntegrationType.SOAP => "SOAP",
        _ => type.ToString()
    };

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
    /// Applies a template as a starting point. The template supplies the reusable knowledge —
    /// name, type, producer, consumer — and <paramref name="values"/> supplies everything that is
    /// specific to this environment. Nothing structural is ever taken from the template itself, so
    /// a QA hub name cannot leak into a Development integration.
    /// </summary>
    /// <param name="values">
    /// The environment's binding, plus anything the user typed for fields it had no value for. A
    /// field that is still empty here stays empty, and the form shows it as still needed.
    /// </param>
    /// <param name="fillOnly">
    /// Applying to an integration that already exists. Only empty fields are filled and the provenance
    /// is left alone, mirroring the backend merger: a suggestion never overwrites a value a person
    /// entered, and re-applying a template never downgrades a Manual record to CodeSuggested.
    /// </param>
    public static void ApplyTemplate(
        IntegrationConfig target,
        KnownIntegrationTemplate template,
        IntegrationEnvironmentValues values,
        bool fillOnly = false)
    {
        if (!fillOnly)
        {
            target.Name = template.DisplayName;
            target.Type = template.IntegrationType;
            target.ResourceKind = template.ResourceKind;
            // Structural values come from the environment, never from the reusable template.
            target.Resource = values.Resource;
            target.Endpoint = values.EndpointOrNamespace;
            target.Consumer = values.ConsumerGroup;
            target.LogicalProducerService = template.SuggestedProducer;
            target.LogicalConsumerService = template.SuggestedConsumer;
            target.ConfigurationSource = IntegrationConfigurationSource.CodeSuggested;
            return;
        }

        if (string.IsNullOrWhiteSpace(target.Name)) target.Name = template.DisplayName;
        if (target.ResourceKind == IntegrationResourceKind.Unknown) target.ResourceKind = template.ResourceKind;
        if (string.IsNullOrWhiteSpace(target.Resource)) target.Resource = values.Resource;
        if (string.IsNullOrWhiteSpace(target.Endpoint)) target.Endpoint = values.EndpointOrNamespace;
        if (string.IsNullOrWhiteSpace(target.Consumer)) target.Consumer = values.ConsumerGroup;
        if (string.IsNullOrWhiteSpace(target.LogicalProducerService)) target.LogicalProducerService = template.SuggestedProducer;
        if (string.IsNullOrWhiteSpace(target.LogicalConsumerService)) target.LogicalConsumerService = template.SuggestedConsumer;
        // Provenance records the strongest authority behind the record; only an absent one is filled.
        if (target.ConfigurationSource == IntegrationConfigurationSource.Unknown)
            target.ConfigurationSource = IntegrationConfigurationSource.CodeSuggested;
    }
}

/// <summary>
/// The environment-specific structural values a template is accepted with: whatever the environment's
/// binding provided, overlaid with whatever the user supplied for the fields it could not.
///
/// This type exists so the reusable template and the environment values it is combined with stay two
/// separate things all the way to the point of acceptance.
/// </summary>
public sealed record IntegrationEnvironmentValues(string? Resource, string? EndpointOrNamespace, string? ConsumerGroup)
{
    public static readonly IntegrationEnvironmentValues None = new(null, null, null);
}

/// <summary>
/// The structural field names the backend reports as required for a known template. They are
/// compared as strings across the wire, so they are named in one place on this side too.
/// </summary>
public static class KnownIntegrationFields
{
    public const string Resource = "Resource";
    public const string Endpoint = "Endpoint";
}
