using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// Proposes REST and GraphQL integrations from endpoints seen by Endpoint Discovery, and merges
/// them with integrations already configured.
///
/// Proposing configuration is not the same as having runtime evidence. A proposal records that an
/// application endpoint exists; whether any traffic was actually measured against it remains a
/// separate question answered by RuntimeIntegrationEvidence. An integration can therefore be
/// configured here and still report performance as unavailable.
/// </summary>
public sealed class IntegrationDiscoveryProposalService
{
    /// <summary>
    /// Builds integration proposals from observed endpoints. Only endpoints classified as REST or
    /// GraphQL are considered; static assets, telemetry, authentication and websocket traffic are
    /// infrastructure rather than application integrations.
    ///
    /// When application origins are known, endpoints outside them are skipped, so third-party and
    /// security-infrastructure hosts a browser happens to contact do not become integrations.
    /// </summary>
    public List<IntegrationConfigDto> Propose(
        IReadOnlyList<ObservedNetworkEndpoint>? observations,
        IReadOnlyCollection<string>? applicationOrigins = null)
    {
        if (observations is null || observations.Count == 0)
            return [];

        var proposals = new Dictionary<string, IntegrationConfigDto>(StringComparer.Ordinal);

        foreach (var observation in observations)
        {
            var type = observation.Category switch
            {
                ObservedTrafficCategory.Rest => IntegrationType.REST,
                ObservedTrafficCategory.GraphQl => IntegrationType.GraphQL,
                _ => (IntegrationType?)null
            };

            if (type is null)
                continue;

            if (!IsApplicationOrigin(observation, applicationOrigins))
                continue;

            var endpoint = CanonicalEndpoint(observation);

            if (string.IsNullOrWhiteSpace(endpoint))
                continue;

            // Several observed endpoints can share one integration identity; keep one proposal.
            if (proposals.ContainsKey(endpoint))
                continue;

            proposals[endpoint] = new IntegrationConfigDto
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = ProposedName(type.Value, observation),
                Type = type.Value,
                Endpoint = endpoint,
                ResourceKind = type == IntegrationType.GraphQL
                    ? IntegrationResourceKind.GraphQlEndpoint
                    : IntegrationResourceKind.RestEndpoint,
                ConfigurationSource = IntegrationConfigurationSource.EndpointDiscovery,
                Enabled = true

                // Producer and consumer stay unset. Endpoint Discovery observes that a request was
                // made, not which services sit on either side of it, and the proxy is transparent
                // so it has no route table naming a downstream service. Deriving a consumer from a
                // path such as /api/person/graphql would be inference from a name, not evidence.
            };
        }

        return proposals.Values
            .OrderBy(p => p.Endpoint, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsApplicationOrigin(
        ObservedNetworkEndpoint observation,
        IReadOnlyCollection<string>? applicationOrigins)
    {
        if (applicationOrigins is null || applicationOrigins.Count == 0)
            return true;

        return applicationOrigins.Any(origin =>
            string.Equals(origin?.TrimEnd('/'), observation.Origin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A factual label. It names the protocol and the observed path and makes no claim about which
    /// service answers it.
    /// </summary>
    private static string ProposedName(IntegrationType type, ObservedNetworkEndpoint observation)
    {
        var path = string.IsNullOrWhiteSpace(observation.Path) ? "/" : observation.Path;
        return $"{(type == IntegrationType.GraphQL ? "GraphQL" : "REST")} {observation.Host}{path}";
    }

    /// <summary>
    /// Scheme, host, non-default port and path. The shared observation contract already strips
    /// query and fragment and never carries user info, so nothing secret can reach persisted
    /// configuration through this path.
    /// </summary>
    private static string CanonicalEndpoint(ObservedNetworkEndpoint observation)
    {
        if (string.IsNullOrWhiteSpace(observation.Host))
            return "";

        var scheme = (observation.Scheme ?? "https").ToLowerInvariant();
        var host = observation.Host.ToLowerInvariant();
        var port = observation.Port is 443 or 80 ? "" : $":{observation.Port}";
        var path = (observation.Path ?? "").TrimEnd('/');

        if (path == "/")
            path = "";

        return $"{scheme}://{host}{port}{path}";
    }
}

/// <summary>
/// Merges integrations from different origins into one set.
///
/// Identity is structural, using the same derivation as the historical baseline key, so a
/// renamed integration and a rediscovered one are recognised as the same thing. Display name is
/// never used, because a rename would split one integration into two.
///
/// Merging is field by field rather than record by record. A record-level "stronger source wins"
/// rule would let a rediscovery silently erase a producer somebody typed in.
/// </summary>
public sealed class IntegrationConfigurationMerger
{
    /// <summary>
    /// Configuration authority, strongest first: a value a person entered outranks one discovery
    /// observed, which outranks one suggested from source, which outranks nothing at all.
    /// </summary>
    private static int Authority(IntegrationConfigurationSource source) => source switch
    {
        IntegrationConfigurationSource.Manual => 3,
        IntegrationConfigurationSource.EndpointDiscovery => 2,
        IntegrationConfigurationSource.CodeSuggested => 1,
        _ => 0
    };

    public List<IntegrationConfigDto> Merge(
        string? environmentId,
        IReadOnlyList<IntegrationConfigDto> existing,
        IReadOnlyList<IntegrationConfigDto> incoming)
    {
        var merged = existing.ToList();

        var byIdentity = merged
            .GroupBy(i => IntegrationBaselineIdentity.Compute(environmentId, i), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var candidate in incoming)
        {
            var identity = IntegrationBaselineIdentity.Compute(environmentId, candidate);

            if (byIdentity.TryGetValue(identity, out var current))
            {
                Enrich(current, candidate);
                continue;
            }

            merged.Add(candidate);
            byIdentity[identity] = candidate;
        }

        return merged;
    }

    /// <summary>
    /// Fills gaps on an existing integration from a candidate for the same structural identity.
    ///
    /// Only empty fields are filled, and only by a source with at least as much authority. A
    /// producer or consumer somebody entered therefore survives rediscovery, and a suggestion
    /// never overwrites a typed value.
    /// </summary>
    private static void Enrich(IntegrationConfigDto current, IntegrationConfigDto candidate)
    {
        var currentAuthority = Authority(current.ConfigurationSource);
        var candidateAuthority = Authority(candidate.ConfigurationSource);

        if (candidateAuthority < currentAuthority && currentAuthority == Authority(IntegrationConfigurationSource.Manual))
        {
            // A person owns this record; a weaker source may still confirm it but not change it.
            FillIfEmpty(current, candidate, allowProvenanceUpgrade: false);
            return;
        }

        FillIfEmpty(current, candidate, allowProvenanceUpgrade: true);
    }

    private static void FillIfEmpty(
        IntegrationConfigDto current,
        IntegrationConfigDto candidate,
        bool allowProvenanceUpgrade)
    {
        if (string.IsNullOrWhiteSpace(current.Endpoint))
            current.Endpoint = candidate.Endpoint;

        if (string.IsNullOrWhiteSpace(current.Resource))
            current.Resource = candidate.Resource;

        if (string.IsNullOrWhiteSpace(current.Consumer))
            current.Consumer = candidate.Consumer;

        if (string.IsNullOrWhiteSpace(current.LogicalProducerService))
            current.LogicalProducerService = candidate.LogicalProducerService;

        if (string.IsNullOrWhiteSpace(current.LogicalConsumerService))
            current.LogicalConsumerService = candidate.LogicalConsumerService;

        if (current.ResourceKind == IntegrationResourceKind.Unknown)
            current.ResourceKind = candidate.ResourceKind;

        if (current.AuthType == IntegrationAuthType.None
            && candidate.AuthType != IntegrationAuthType.None)
            current.AuthType = candidate.AuthType;

        // Provenance records how the configuration currently stands. It never participates in
        // structural identity, so changing it cannot detach the integration from its history.
        if (allowProvenanceUpgrade
            && Authority(candidate.ConfigurationSource) > Authority(current.ConfigurationSource))
            current.ConfigurationSource = candidate.ConfigurationSource;
    }
}
