using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Collects the configuration values the domain analyzers of one snapshot declare under generic semantic keys: the OpenAPI values the contract
/// analyzer read, and messaging names Infrastructure as Code declares as safe literals (Service Bus / Event Hubs namespaces, topics,
/// subscriptions, queues, hubs, consumer groups — per environment when an environment file changes the name). A computed or unresolved name
/// is not a value. Declared in source only: a declaration does not prove the resource exists, is reachable or is used.
/// </summary>
/// <summary>A provider adapter that recognizes provider-specific values in already analyzed evidence (it reads the domains; it parses no file
/// of its own and contacts nothing). Registered with the analyzers; the collector itself names no provider.</summary>
internal interface ISourceConfigurationValueProducer
{
    string Name { get; }
    IEnumerable<SourceConfigurationValue> Produce(ConfigurationEvidence? configuration);
}

internal static class SourceConfigurationValueCollector
{
    private static readonly Dictionary<string, string> Messaging = new(StringComparer.Ordinal)
    {
        ["Service Bus namespace"] = SourceConfigurationKeys.MessagingServiceBusNamespace, ["Service Bus topic"] = SourceConfigurationKeys.MessagingServiceBusTopic,
        ["Service Bus subscription"] = SourceConfigurationKeys.MessagingServiceBusSubscription, ["Service Bus queue"] = SourceConfigurationKeys.MessagingServiceBusQueue,
        ["Event Hubs namespace"] = SourceConfigurationKeys.MessagingEventHubNamespace, ["Event Hub"] = SourceConfigurationKeys.MessagingEventHubName,
        ["Event Hub consumer group"] = SourceConfigurationKeys.MessagingEventHubConsumerGroup,
    };

    public static SourceConfigurationValuesEvidence Collect(SourceEvidenceContext context, IReadOnlyList<ISourceConfigurationValueProducer>? producers = null)
    {
        producers ??= [];
        var values = new List<SourceConfigurationValue>(context.DeclaredConfiguration);
        foreach (var producer in producers)
        {
            try { values.AddRange(producer.Produce(context.Configuration)); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { }
        }
        foreach (var resource in context.Infrastructure?.Resources ?? [])
        {
            if (resource.Kind != "resource" || !Messaging.TryGetValue(resource.CategoryDetail, out var key)) continue;
            SourceConfigurationValue Value(string name, SourceEnvironmentRole environment, string basis) => new()
            {
                SemanticKey = key, Value = SourceEvidenceRedaction.SafePath(name), Group = SourceConfigurationGroup.Messaging, Environment = environment,
                EvidenceKind = SourceConfigurationEvidenceKind.InfrastructureAsCode, Artifact = SourceEvidenceRedaction.SafePath(resource.File),
                Location = $"{SourceEvidenceRedaction.SafePath(resource.ResourceType)}.{SourceEvidenceRedaction.SafePath(resource.LogicalName)} (line {resource.Line})", Basis = basis,
                Confidence = resource.EvidenceState, Attributes = new() { ["ResourceType"] = SourceEvidenceRedaction.SafePath(resource.ResourceType), ["Format"] = resource.Format.ToString() },
            };
            if (resource.EnvironmentNames.Count > 0)
                values.AddRange(resource.EnvironmentNames.Select(item => Value(item.Name, Role(item.Environment), $"{resource.CategoryDetail} name for {item.Environment.Raw}: {item.Basis}")));
            else if (resource.DeclaredNameResolved && resource.DeclaredName is { Length: > 0 } declared)
                values.Add(Value(declared, Role(resource.Environment), $"{resource.CategoryDetail} declared name"));
        }
        return new SourceConfigurationValuesEvidence
        {
            Values = values,
            Producers = ["OpenAPI / Swagger: info, servers (by description), path route, endpoint roles, security schemes, token providers, request schemas, data-type and process-task tables",
                "Infrastructure as Code: messaging resource names declared as safe literals (Terraform, Bicep, ARM)", .. producers.Select(producer => producer.Name)],
            Limitations = ["Declared in source: not configured, not reachable, not runtime verified.", "Examples in API documents are documentation and are never read as configuration.",
                "Application configuration files (appsettings, Helm values …) are listed under Configuration; a test pack maps a key to a semantic value only by declaring it."],
        };
    }

    /// <summary>The values of a stored snapshot: what was collected at analysis, plus what the registered producers recognize in its stored
    /// configuration entries (so a snapshot analyzed before a producer existed is read the same way; nothing is re-analyzed).</summary>
    public static IReadOnlyList<SourceConfigurationValue> Values(SourceEvidenceDomainsSnapshot? domains)
    {
        if (domains is null) return [];
        var values = new List<SourceConfigurationValue>(domains.ConfigurationValues?.Values ?? []);
        foreach (var producer in SourceEvidenceAnalyzer.SourceConfigurationValueProducers)
        {
            try { values.AddRange(producer.Produce(domains.Configuration)); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { }
        }
        return values.DistinctBy(value => (value.SemanticKey, value.Value, value.Artifact, value.Location, value.Scope)).ToArray();
    }

    public static SourceEnvironmentRole Role(SourceEnvironmentLabel? label) => label?.Kind switch
    {
        null or SourceEnvironmentKind.Default => SourceEnvironmentRole.Any,
        SourceEnvironmentKind.Development or SourceEnvironmentKind.Local => SourceEnvironmentRole.Local,
        SourceEnvironmentKind.Test or SourceEnvironmentKind.QA or SourceEnvironmentKind.Staging => SourceEnvironmentRole.Test,
        SourceEnvironmentKind.Production => SourceEnvironmentRole.Production,
        _ => SourceEnvironmentRole.Unclassified,
    };
}
