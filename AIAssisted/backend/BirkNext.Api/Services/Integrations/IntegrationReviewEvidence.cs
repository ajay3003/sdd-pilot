using BirkNext.Integrations;
using BirkNext.IntegrationTopology;

namespace BirkNext.Api.Services.Integrations;

/// <summary>The evidence families Integration Quality Review reads besides its own catalog, each read independently: a family that fails is
/// reported as Failed (a limitation), never as "nothing exists" and never as a blocker.</summary>
public interface IIntegrationReviewEvidenceSources
{
    Task<(IntegrationTopologyView? View, string? Error)> TopologyAsync(string environmentId, CancellationToken ct);
    Task<(IReadOnlyList<ExternalIntegration>? Systems, string? Error)> ConnectedSystemsAsync(string environmentId, CancellationToken ct);
}

public sealed class IntegrationReviewEvidenceSources(BirkNext.Api.Services.IntegrationTopology.IntegrationTopologyService topology, BirkNext.Api.Services.ExternalIntegrations.ExternalIntegrationService external,
    ILogger<IntegrationReviewEvidenceSources> logger) : IIntegrationReviewEvidenceSources
{
    public async Task<(IntegrationTopologyView? View, string? Error)> TopologyAsync(string environmentId, CancellationToken ct)
    {
        // Offline only: no OpenAPI discovery and no observations — readiness never makes a network call.
        try { return ((await topology.BuildAsync(environmentId, new TopologyRequest(null), ct)).View, null); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Integration Topology could not be read for {EnvironmentId}; the review continues without it.", environmentId);
            return (null, "Integration Topology could not be read.");
        }
    }

    public async Task<(IReadOnlyList<ExternalIntegration>? Systems, string? Error)> ConnectedSystemsAsync(string environmentId, CancellationToken ct)
    {
        try { return (await external.ListAsync(environmentId, ct), null); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Connected Systems could not be read for {EnvironmentId}; the review continues without them.", environmentId);
            return (null, "Connected Systems could not be read.");
        }
    }
}

/// <summary>What the evidence composition adds to the catalog: the reviewable discovered subjects (transient definitions, never stored), every
/// candidate with its state, and the families it read.</summary>
public sealed record IntegrationReviewEvidenceSet(IReadOnlyList<IntegrationDefinition> Discovered, IReadOnlyList<IntegrationReviewCandidate> Candidates,
    IReadOnlyList<IntegrationEvidenceFamilyStatus> Families)
{
    public static readonly IntegrationReviewEvidenceSet Empty = new([], [], []);

    /// <summary>The catalog the review runs over: the configured integrations plus the reviewable discovered ones.</summary>
    public IntegrationCatalog Effective(IntegrationCatalog catalog) =>
        Discovered.Count == 0 ? catalog : catalog with { Integrations = [.. catalog.Integrations, .. Discovered.Where(d => catalog.Integrations.All(i => i.Id != d.Id))] };
}

/// <summary>
/// One canonical composition of the integrations Integration Quality Review covers, from every evidence family: the Integration Catalog
/// (configured), Integration Topology (connections established by source, configuration and contract evidence) and Connected Systems (systems
/// a person registered). A discovered integration is reviewed when its producer and consumer are established (topology relationship Strongly
/// supported, Resolved or Observed, or a registered Connected System); partial evidence is listed as Needs confirmation and not reviewed; a
/// person's disabled catalog entry is never overridden. Pure and deterministic: the same evidence gives the same candidates and ids.
/// </summary>
public static class IntegrationReviewEvidence
{
    public const string DiscoveredPrefix = "discovered:";
    public const int MaxCandidates = 50;

    private static readonly TopologyRelationshipState[] Established =
        [TopologyRelationshipState.StronglySupported, TopologyRelationshipState.Resolved, TopologyRelationshipState.Observed];

    public static string RelationshipLabel(TopologyRelationshipState state) => state switch
    {
        TopologyRelationshipState.ExpectedUnresolved => "Expected · unresolved",
        TopologyRelationshipState.Candidate => "Candidate",
        TopologyRelationshipState.StronglySupported => "Strongly supported",
        TopologyRelationshipState.Resolved => "Resolved",
        _ => "Observed",
    };

    public static IntegrationReviewEvidenceSet Compose(IntegrationCatalog catalog, IntegrationTopologyView? topology, string? topologyError,
        IReadOnlyList<ExternalIntegration>? connectedSystems, string? connectedError, string? environmentName)
    {
        var candidates = new List<IntegrationReviewCandidate>();
        var discovered = new List<IntegrationDefinition>();
        var connections = topology?.Connections ?? [];
        string Name(string componentId) => topology?.Components.FirstOrDefault(c => c.Id == componentId)?.Name ?? componentId;
        List<string> CatalogIds(TopologyConnection c) => [.. c.Provenance.Where(p => p.Source == TopologySource.IntegrationCatalog).Select(p => p.Reference)];

        // ── Integration Catalog: configured, the person's own decision (a disabled entry stays out of the review) ─────────────
        foreach (var i in catalog.Integrations)
        {
            var inTopology = connections.Any(c => CatalogIds(c).Contains(i.Id));
            candidates.Add(new(i.Id, i.DisplayName, i.Kind, i.Origin, i.Enabled ? IntegrationCandidateState.Reviewed : IntegrationCandidateState.Excluded)
            {
                Producer = i.Producer ?? i.SourceSystem, Consumer = i.Consumer.DisplayName is { Length: > 0 } consumer ? consumer : i.DestinationSystem, EndpointOrTopic = i.EndpointOrTopic,
                Sources = inTopology ? [IntegrationEvidenceFamily.IntegrationCatalog, IntegrationEvidenceFamily.IntegrationTopology] : [IntegrationEvidenceFamily.IntegrationCatalog],
                Reason = i.Enabled ? "Configured in the Integration Catalog" : "Disabled in the Integration Catalog — not reviewed",
            });
        }

        // ── Integration Topology: producer → consumer flows the catalog does not state. A resource (queue, topic, event hub) keeps the chain
        //    resource-centred, so producer → resource → consumer is one integration over that resource. ──────────────────────────────────────
        var resources = topology?.Components.Where(c => c.Kind == TopologyComponentKind.Resource).Select(c => c.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var flows = new List<(string Producer, string Consumer, string? Address, TopologyInterfaceType Type, string Protocol, TopologyRelationshipState Relationship)>();
        foreach (var c in connections.Where(c => CatalogIds(c).Count == 0))
        {
            var fromResource = resources.Contains(c.ProducerComponentId);
            var toResource = resources.Contains(c.ConsumerComponentId);
            if (!fromResource && !toResource) flows.Add((Name(c.ProducerComponentId), Name(c.ConsumerComponentId), c.Address, c.Type, c.Protocol, c.Relationship));
            else if (toResource && !fromResource)
            {
                var outgoing = connections.Where(o => o.ProducerComponentId == c.ConsumerComponentId && !resources.Contains(o.ConsumerComponentId)).ToList();
                // A catalog-stated hop on either side means the catalog already covers this flow.
                if (outgoing.Any(o => CatalogIds(o).Count > 0)) continue;
                if (outgoing.Count == 0) flows.Add((Name(c.ProducerComponentId), "", Name(c.ConsumerComponentId), c.Type, c.Protocol, Weaker(c.Relationship, TopologyRelationshipState.Candidate)));
                foreach (var o in outgoing) flows.Add((Name(c.ProducerComponentId), Name(o.ConsumerComponentId), Name(c.ConsumerComponentId), c.Type, c.Protocol, Weaker(c.Relationship, o.Relationship)));
            }
            else if (fromResource && !toResource && !connections.Any(i => i.ConsumerComponentId == c.ProducerComponentId && !resources.Contains(i.ProducerComponentId)))
                flows.Add(("", Name(c.ConsumerComponentId), Name(c.ProducerComponentId), c.Type, c.Protocol, Weaker(c.Relationship, TopologyRelationshipState.Candidate)));
        }
        foreach (var f in flows.DistinctBy(f => (f.Producer, f.Consumer, f.Address)).OrderBy(f => f.Producer, StringComparer.Ordinal).ThenBy(f => f.Consumer, StringComparer.Ordinal))
        {
            if (candidates.Count >= MaxCandidates) break;
            var kind = KindOf(f.Type, f.Protocol);
            var bothEnds = f.Producer.Length > 0 && f.Consumer.Length > 0;
            var reviewed = bothEnds && Established.Contains(f.Relationship);
            var display = $"{(f.Producer.Length > 0 ? f.Producer : "Unknown producer")} → {(f.Consumer.Length > 0 ? f.Consumer : "Unknown consumer")}";
            var candidate = new IntegrationReviewCandidate(DiscoveredPrefix + Slug($"{f.Producer}|{f.Consumer}|{f.Address}"), display, kind, IntegrationRecordOrigin.Discovered,
                reviewed ? IntegrationCandidateState.Reviewed : IntegrationCandidateState.NeedsConfirmation)
            {
                Producer = NullIfEmpty(f.Producer), Consumer = NullIfEmpty(f.Consumer), EndpointOrTopic = f.Address, Sources = [IntegrationEvidenceFamily.IntegrationTopology],
                Relationship = RelationshipLabel(f.Relationship),
                Reason = reviewed ? $"Discovered in Integration Topology ({RelationshipLabel(f.Relationship)}) — reviewed without a catalog entry"
                    : !bothEnds ? "Discovered in Integration Topology, but one side of the flow is not established — not reviewed until it is"
                    : $"Discovered in Integration Topology ({RelationshipLabel(f.Relationship)}) — the evidence does not establish the producer/consumer yet; not reviewed",
            };
            candidates.Add(candidate);
        }

        // ── Connected Systems: a registered system is an explicit statement that this environment integrates with it ──────────────────────
        var caller = string.IsNullOrWhiteSpace(environmentName) ? "Target application" : environmentName!;
        foreach (var system in (connectedSystems ?? []).Where(s => s.Enabled))
        {
            var index = candidates.FindIndex(c => c.State != IntegrationCandidateState.Excluded && Touches(c, system));
            if (index >= 0)
            {
                candidates[index] = candidates[index] with { Sources = [.. candidates[index].Sources, IntegrationEvidenceFamily.ConnectedSystems] };
                continue;
            }
            if (candidates.Count >= MaxCandidates) break;
            candidates.Add(new(DiscoveredPrefix + "connected-" + Slug(system.Id), $"{caller} → {system.Name}", KindOf(system.ProviderType), IntegrationRecordOrigin.Discovered,
                IntegrationCandidateState.Reviewed)
            {
                Producer = caller, Consumer = system.Name, Sources = [IntegrationEvidenceFamily.ConnectedSystems],
                Reason = "Registered under Connected Systems — reviewed without a catalog entry",
            });
        }

        foreach (var c in candidates.Where(c => c.Origin == IntegrationRecordOrigin.Discovered && c.State == IntegrationCandidateState.Reviewed))
            discovered.Add(new IntegrationDefinition
            {
                Id = c.IntegrationId, EnvironmentId = catalog.EnvironmentId, DisplayName = c.DisplayName, Kind = c.Kind, Enabled = true, SystemName = "Discovered from evidence",
                SourceSystem = c.Producer, DestinationSystem = c.Consumer, Producer = c.Producer, EndpointOrTopic = c.EndpointOrTopic,
                Consumer = new IntegrationConsumer { DisplayName = c.Consumer ?? "" }, Origin = IntegrationRecordOrigin.Discovered,
            });

        var families = new List<IntegrationEvidenceFamilyStatus>
        {
            catalog.Integrations.Count == 0
                ? new(IntegrationEvidenceFamily.IntegrationCatalog, IntegrationEvidenceFamilyState.None, 0, "No integration is defined — not required: integrations are discovered from evidence")
                : new(IntegrationEvidenceFamily.IntegrationCatalog, IntegrationEvidenceFamilyState.Available, catalog.Integrations.Count,
                    $"{catalog.Integrations.Count} defined · {catalog.Integrations.Count(i => i.Enabled)} enabled"),
            topologyError is not null ? new(IntegrationEvidenceFamily.IntegrationTopology, IntegrationEvidenceFamilyState.Failed, 0, topologyError)
                : topology is null ? new(IntegrationEvidenceFamily.IntegrationTopology, IntegrationEvidenceFamilyState.NotConfigured, 0, "Integration Topology is not available in this instance")
                : connections.Count == 0 ? new(IntegrationEvidenceFamily.IntegrationTopology, IntegrationEvidenceFamilyState.None, 0, "No connection is established by the current sources") { CapturedAt = topology.GeneratedAt }
                : new(IntegrationEvidenceFamily.IntegrationTopology, IntegrationEvidenceFamilyState.Available, connections.Count,
                    $"{connections.Count} connection{(connections.Count == 1 ? "" : "s")} · {connections.Count(c => Established.Contains(c.Relationship))} established") { CapturedAt = topology.GeneratedAt },
            connectedError is not null ? new(IntegrationEvidenceFamily.ConnectedSystems, IntegrationEvidenceFamilyState.Failed, 0, connectedError)
                : connectedSystems is null ? new(IntegrationEvidenceFamily.ConnectedSystems, IntegrationEvidenceFamilyState.NotConfigured, 0, "Connected Systems are not available in this instance")
                : connectedSystems.Count(s => s.Enabled) == 0 ? new(IntegrationEvidenceFamily.ConnectedSystems, IntegrationEvidenceFamilyState.None, 0, "No Connected System is registered")
                : new(IntegrationEvidenceFamily.ConnectedSystems, IntegrationEvidenceFamilyState.Available, connectedSystems.Count(s => s.Enabled),
                    string.Join(", ", connectedSystems.Where(s => s.Enabled).Select(s => s.Name))),
        };
        return new(discovered, candidates, families);
    }

    /// <summary>A candidate the Connected System already describes: one of its ends is the system (by name or by provider name).</summary>
    private static bool Touches(IntegrationReviewCandidate candidate, ExternalIntegration system)
    {
        bool Matches(string? end) => !string.IsNullOrWhiteSpace(end) && (string.Equals(end, system.Name, StringComparison.OrdinalIgnoreCase)
            || system.ProviderType is not (ExternalProviderType.Other or ExternalProviderType.GenericHttp or ExternalProviderType.RestApi or ExternalProviderType.GraphQl or ExternalProviderType.Database)
                && end.Contains(system.ProviderType.ToString(), StringComparison.OrdinalIgnoreCase));
        return Matches(candidate.Producer) || Matches(candidate.Consumer);
    }

    private static TopologyRelationshipState Weaker(TopologyRelationshipState a, TopologyRelationshipState b) => a < b ? a : b;

    public static IntegrationKind KindOf(TopologyInterfaceType type, string protocol)
    {
        var p = protocol.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return type switch
        {
            TopologyInterfaceType.Messaging when p.Contains("servicebus") => IntegrationKind.ServiceBus,
            TopologyInterfaceType.Messaging when p.Contains("eventhub") || p.Contains("kafka") => IntegrationKind.EventHub,
            TopologyInterfaceType.Rest or TopologyInterfaceType.GraphQl or TopologyInterfaceType.ExternalApplication => IntegrationKind.HttpApi,
            TopologyInterfaceType.Database => IntegrationKind.Database,
            TopologyInterfaceType.File or TopologyInterfaceType.Storage => IntegrationKind.File,
            _ => IntegrationKind.Other,
        };
    }

    public static IntegrationKind KindOf(ExternalProviderType type) => type switch
    {
        ExternalProviderType.AzureServiceBus => IntegrationKind.ServiceBus,
        ExternalProviderType.AzureEventHub => IntegrationKind.EventHub,
        ExternalProviderType.Database => IntegrationKind.Database,
        ExternalProviderType.FileSftp => IntegrationKind.File,
        ExternalProviderType.Other => IntegrationKind.Other,
        _ => IntegrationKind.HttpApi,
    };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
