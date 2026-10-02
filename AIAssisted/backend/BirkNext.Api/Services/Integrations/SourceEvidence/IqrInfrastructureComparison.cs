using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;


/// <summary>
/// Integration Quality Review's use of Source Analysis Infrastructure evidence: each CONFIGURED value of the Integrations catalog (namespace,
/// Event Hub, consumer group, checkpoint storage, Application Insights, Service Bus topics/queues/subscriptions) is compared with what the
/// selected snapshot DECLARES, through the shared comparer — IQR never parses Terraform and never re-derives names or environments.
/// The configured catalog stays the expected scope: a comparison never writes a value, and runtime (Observed) evidence stays separate.
/// The target environment is matched to source environments only through the shared environment normalization (inferred from its name).
/// </summary>
public static class IqrInfrastructureComparison
{
    public const string Boundary = SourceInfrastructureComparer.Boundary;

    public static List<ConfiguredSourceComparison> Compare(IntegrationCatalog catalog, IReadOnlyList<IqrSourceSnapshot> snapshots, string? environmentName)
    {
        var source = snapshots.FirstOrDefault(s => s.EvidenceDomains?.Infrastructure.Resources.Count > 0) ?? snapshots.FirstOrDefault(s => s.EvidenceDomains is not null);
        if (source?.EvidenceDomains is not { } evidence) return [];
        var infra = evidence.Infrastructure;
        var environment = SourceEnvironments.FromName(environmentName ?? catalog.EnvironmentId)?.Kind;
        var result = new List<ConfiguredSourceComparison>();
        void Add(IntegrationPlatform p, string? integration, string field, string? configured, InfrastructureResourceKind kind, string? parent = null)
        {
            var comparison = SourceInfrastructureComparer.Compare(field, configured, infra, source.Id, kind, environment, parent);
            // Neither configured nor declared: nothing to say for this field.
            if (comparison.State == SourceComparisonState.NoSourceDeclaration && string.IsNullOrWhiteSpace(configured)) return;
            result.Add(new(p.Id, p.Name, integration, environment is null ? comparison : comparison with { EnvironmentBasis = $"{comparison.EnvironmentBasis}; target environment from its name (inferred)" }));
        }
        foreach (var p in catalog.Platforms.Where(p => p.Enabled))
        {
            var ns = p.Namespace ?? p.NamespaceFqdn;
            switch (p.Kind)
            {
                case IntegrationKind.EventHub:
                    Add(p, null, "Event Hubs namespace", ns, InfrastructureResourceKind.EventHubNamespace);
                    foreach (var i in catalog.Integrations.Where(i => i.PlatformId == p.Id && i.Kind == IntegrationKind.EventHub))
                    {
                        Add(p, i.Id, "Event Hub", i.EndpointOrTopic, InfrastructureResourceKind.EventHub, ns);
                        Add(p, i.Id, "Consumer group", i.ConsumerGroup ?? p.RuntimeEvidence?.ExpectedConsumerGroup, InfrastructureResourceKind.ConsumerGroup, i.EndpointOrTopic);
                    }
                    if (p.RuntimeEvidence is { } r)
                    {
                        Add(p, null, "Checkpoint storage account", r.CheckpointBlobEndpoint ?? r.CheckpointContainerUrl, InfrastructureResourceKind.StorageAccount);
                        Add(p, null, "Checkpoint container", r.CheckpointContainerName, InfrastructureResourceKind.BlobContainer);
                        Add(p, null, "Application Insights", r.ApplicationInsightsResourceName, InfrastructureResourceKind.TelemetryComponent);
                    }
                    break;
                case IntegrationKind.ServiceBus:
                    Add(p, null, "Service Bus namespace", ns, InfrastructureResourceKind.ServiceBusNamespace);
                    foreach (var e in p.ServiceBusTopology?.Entities ?? [])
                        Add(p, null, $"Service Bus {e.EntityType.ToString().ToLowerInvariant()} {e.Name}", e.Name, e.EntityType switch
                        {
                            ServiceBusEntityType.Topic => InfrastructureResourceKind.ServiceBusTopic,
                            ServiceBusEntityType.Queue => InfrastructureResourceKind.ServiceBusQueue,
                            _ => InfrastructureResourceKind.ServiceBusSubscription,
                        }, e.EntityType == ServiceBusEntityType.Subscription ? e.Topic : ns);
                    break;
            }
        }
        return result;
    }

    /// <summary>Domain lines for the review result: counts per comparison state, never Pass/Fail.</summary>
    public static (List<string> Observed, List<string> Missing) Domain(IntegrationReviewDomain domain, IReadOnlyList<ConfiguredSourceComparison> comparisons)
    {
        if (domain is not (IntegrationReviewDomain.Configuration or IntegrationReviewDomain.Connectivity) || comparisons.Count == 0) return ([], []);
        if (domain == IntegrationReviewDomain.Connectivity) return ([], ["A namespace declared in source does not prove the namespace exists or is reachable at runtime"]);
        var first = comparisons[0].Comparison;
        var at = $"source snapshot {(first.SourceFingerprint ?? "")[..Math.Min(8, first.SourceFingerprint?.Length ?? 0)]}, Infrastructure v{first.AnalyzerVersion}";
        var summary = string.Join(", ", comparisons.GroupBy(c => c.Comparison.State).OrderBy(g => g.Key).Select(g => $"{g.Count()} {SourceInfrastructureComparer.Label(g.Key).ToLowerInvariant()}"));
        return ([$"Configured vs. declared in source ({at}): {summary}"], ["Source declarations are not deployed state; configured values are kept as configured (nothing was changed from source)"]);
    }
}
