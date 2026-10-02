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
        foreach (var f in Fields(catalog))
        {
            var comparison = SourceInfrastructureComparer.Compare(f.Field, f.Configured, infra, source.Id, f.Kind, environment, f.Parent);
            // Neither configured nor declared: nothing to say for this field.
            if (comparison.State == SourceComparisonState.NoSourceDeclaration && string.IsNullOrWhiteSpace(f.Configured)) continue;
            result.Add(new(f.Platform.Id, f.Platform.Name, f.IntegrationId, environment is null ? comparison : comparison with { EnvironmentBasis = $"{comparison.EnvironmentBasis}; target environment from its name (inferred)" }));
        }
        return result;
    }

    /// <summary>A configured catalog value that names an infrastructure resource (one enumeration for the declared and the observed comparison).</summary>
    public sealed record ConfiguredField(IntegrationPlatform Platform, string? IntegrationId, string Field, string? Configured, InfrastructureResourceKind Kind, string? Parent);

    public static IEnumerable<ConfiguredField> Fields(IntegrationCatalog catalog)
    {
        foreach (var p in catalog.Platforms.Where(p => p.Enabled))
        {
            var ns = p.Namespace ?? p.NamespaceFqdn;
            switch (p.Kind)
            {
                case IntegrationKind.EventHub:
                    yield return new(p, null, "Event Hubs namespace", ns, InfrastructureResourceKind.EventHubNamespace, null);
                    foreach (var i in catalog.Integrations.Where(i => i.PlatformId == p.Id && i.Kind == IntegrationKind.EventHub))
                    {
                        yield return new(p, i.Id, "Event Hub", i.EndpointOrTopic, InfrastructureResourceKind.EventHub, ns);
                        yield return new(p, i.Id, "Consumer group", i.ConsumerGroup ?? p.RuntimeEvidence?.ExpectedConsumerGroup, InfrastructureResourceKind.ConsumerGroup, i.EndpointOrTopic);
                    }
                    if (p.RuntimeEvidence is { } r)
                    {
                        yield return new(p, null, "Checkpoint storage account", r.CheckpointBlobEndpoint ?? r.CheckpointContainerUrl, InfrastructureResourceKind.StorageAccount, null);
                        yield return new(p, null, "Checkpoint container", r.CheckpointContainerName, InfrastructureResourceKind.BlobContainer, null);
                        yield return new(p, null, "Application Insights", r.ApplicationInsightsResourceName, InfrastructureResourceKind.TelemetryComponent, null);
                    }
                    break;
                case IntegrationKind.ServiceBus:
                    yield return new(p, null, "Service Bus namespace", ns, InfrastructureResourceKind.ServiceBusNamespace, null);
                    foreach (var e in p.ServiceBusTopology?.Entities ?? [])
                        yield return new(p, null, $"Service Bus {e.EntityType.ToString().ToLowerInvariant()} {e.Name}", e.Name, e.EntityType switch
                        {
                            ServiceBusEntityType.Topic => InfrastructureResourceKind.ServiceBusTopic,
                            ServiceBusEntityType.Queue => InfrastructureResourceKind.ServiceBusQueue,
                            _ => InfrastructureResourceKind.ServiceBusSubscription,
                        }, e.EntityType == ServiceBusEntityType.Subscription ? e.Topic : ns);
                    break;
            }
        }
    }

    /// <summary>
    /// Configured values looked up in ONE Azure Environment Analysis snapshot (Observed: control-plane inventory read earlier with the person's
    /// own permissions). Read from the stored snapshot — the review never calls Azure for this. Observed ≠ verified: nothing here is Pass/Fail.
    /// </summary>
    public static List<BirkNext.AzureEnvironment.ConfiguredObservedComparison> CompareObserved(IntegrationCatalog catalog, BirkNext.AzureEnvironment.AzureEnvironmentSnapshot? snapshot) =>
        snapshot is null ? [] : Fields(catalog).Where(f => !string.IsNullOrWhiteSpace(f.Configured))
            .Select(f => new BirkNext.AzureEnvironment.ConfiguredObservedComparison(f.Platform.Id, f.Platform.Name, f.IntegrationId, f.Field, f.Configured,
                BirkNext.Api.Services.AzureEnvironment.AzureEnvironmentEvidenceProvider.Lookup(snapshot, f.Kind, f.Configured, f.Parent) with { Observations = [], Relationships = [] }))
            .ToList();

    /// <summary>Domain lines for the observed comparison: counts per lookup state with the snapshot time, never Pass/Fail.</summary>
    public static (List<string> Observed, List<string> Missing) ObservedDomain(IntegrationReviewDomain domain, IReadOnlyList<BirkNext.AzureEnvironment.ConfiguredObservedComparison> comparisons)
    {
        if (domain is not (IntegrationReviewDomain.Configuration or IntegrationReviewDomain.Connectivity) || comparisons.Count == 0) return ([], []);
        var at = comparisons[0].Lookup.CapturedAt is { } t ? $"{t:yyyy-MM-dd HH:mm} UTC" : "unknown time";
        var summary = string.Join(", ", comparisons.GroupBy(c => c.Lookup.State).OrderBy(g => g.Key).Select(g => $"{g.Count()} {Observed(g.Key)}"));
        return ([$"Configured vs. observed in Azure (snapshot {at}): {summary}"],
            [domain == IntegrationReviewDomain.Connectivity ? "Observed in the Azure control plane does not prove the integration can connect or authenticate at runtime"
                : "The Azure snapshot is not re-read by this run; re-analyze the Azure environment for current state"]);
    }

    private static string Observed(BirkNext.AzureEnvironment.ObservedLookupState state) => state switch
    {
        BirkNext.AzureEnvironment.ObservedLookupState.Observed => "observed",
        BirkNext.AzureEnvironment.ObservedLookupState.NotObserved => "not observed in the analyzed scope",
        BirkNext.AzureEnvironment.ObservedLookupState.MultipleObserved => "observed more than once",
        _ => "unable to verify",
    };

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
