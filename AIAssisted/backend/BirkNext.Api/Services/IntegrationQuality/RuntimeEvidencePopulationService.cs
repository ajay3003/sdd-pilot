using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// Populates runtime integration evidence from observed traffic and checks (Phase 3, Checkpoint 2).
/// Maps existing observations (HTTP, GraphQL, authenticated checks) to typed RuntimeIntegrationEvidence.
/// Messaging observations are not currently available; they remain as Unknown/unobserved.
/// </summary>
public sealed class RuntimeEvidencePopulationService
{
    /// <summary>
    /// Builds runtime evidence summary for an integration based on available observations.
    /// </summary>
    public RuntimeEvidenceSummary BuildEvidenceSummary(
        string integrationId,
        List<RuntimeIntegrationEvidence> evidence)
    {
        if (!evidence.Any())
            return new RuntimeEvidenceSummary { HasRuntimeEvidence = false, EvidenceCount = 0 };

        var durations = evidence.Select(e => e.DurationMs).OfType<double>().ToList();

        return new RuntimeEvidenceSummary
        {
            HasRuntimeEvidence = true,
            EvidenceCount = evidence.Count,
            Sources = evidence.Select(e => e.Source).Distinct().ToList(),
            LastObservedAt = evidence.Max(e => e.ObservedAt),
            EvidenceTypes = evidence.Select(e => e.EvidenceType).Distinct().ToList(),
            MinDurationMs = durations.Any() ? durations.Min() : null,
            MaxDurationMs = durations.Any() ? durations.Max() : null,
            AvgDurationMs = durations.Any() ? durations.Average() : null
        };
    }

    /// <summary>
    /// Creates runtime evidence from an HTTP/REST observation with timing.
    /// </summary>
    public RuntimeIntegrationEvidence CreateHttpEvidence(
        string integrationId,
        DateTime observedAt,
        double? durationMs,
        string? statusCode = null,
        bool isError = false)
    {
        return new RuntimeIntegrationEvidence
        {
            IntegrationId = integrationId,
            EvidenceType = RuntimeEvidenceType.HttpRequestObserved,
            Source = RuntimeEvidenceSource.EndpointDiscovery,
            Direction = RuntimeEvidenceDirection.Outbound,
            Outcome = isError ? RuntimeEvidenceOutcome.Error : RuntimeEvidenceOutcome.Success,
            ObservedAt = observedAt,
            DurationMs = durationMs,
            Protocol = IntegrationType.REST,
            StatusCodeOrOutcome = statusCode ?? "unknown"
        };
    }

    /// <summary>
    /// Creates runtime evidence from a GraphQL operation observation.
    /// </summary>
    public RuntimeIntegrationEvidence CreateGraphQlEvidence(
        string integrationId,
        string operationName,
        DateTime observedAt,
        double? durationMs,
        bool hasErrors = false)
    {
        return new RuntimeIntegrationEvidence
        {
            IntegrationId = integrationId,
            EvidenceType = RuntimeEvidenceType.GraphQlOperationObserved,
            Source = RuntimeEvidenceSource.EndpointDiscovery,
            Direction = RuntimeEvidenceDirection.Outbound,
            Outcome = hasErrors ? RuntimeEvidenceOutcome.Error : RuntimeEvidenceOutcome.Success,
            ObservedAt = observedAt,
            DurationMs = durationMs,
            Protocol = IntegrationType.GraphQL,
            OperationOrMessage = operationName,
            StatusCodeOrOutcome = hasErrors ? "errors" : "success"
        };
    }

    /// <summary>
    /// Creates runtime evidence from an authenticated check (health/worker URL).
    /// </summary>
    public RuntimeIntegrationEvidence CreateAuthenticatedCheckEvidence(
        string integrationId,
        DateTime observedAt,
        double durationMs,
        int statusCode)
    {
        var isError = statusCode >= 400;
        return new RuntimeIntegrationEvidence
        {
            IntegrationId = integrationId,
            EvidenceType = RuntimeEvidenceType.HttpRequestObserved,
            Source = RuntimeEvidenceSource.AuthenticatedProxy,
            Direction = RuntimeEvidenceDirection.Outbound,
            Outcome = isError ? RuntimeEvidenceOutcome.Error : RuntimeEvidenceOutcome.Success,
            ObservedAt = observedAt,
            DurationMs = durationMs,
            Protocol = IntegrationType.REST,
            StatusCodeOrOutcome = statusCode.ToString()
        };
    }


    /// <summary>
    /// Maps observed browser traffic onto typed runtime evidence for one integration.
    ///
    /// Only traffic the proxy actually intercepted becomes evidence. Endpoints known solely from
    /// configuration describe what exists, not what ran, so they are skipped: otherwise a
    /// configured-but-never-called endpoint would acquire performance metrics it never earned.
    ///
    /// One evidence record is emitted per observed request sample rather than per endpoint, so
    /// latency statistics are computed over real individual exchanges. Repeated calls to the same
    /// endpoint are legitimate samples and are deliberately not collapsed.
    /// </summary>
    public List<RuntimeIntegrationEvidence> MapObservations(
        IntegrationConfigDto integration,
        IReadOnlyList<ObservedNetworkEndpoint>? observations)
    {
        if (observations is null || observations.Count == 0)
            return [];

        var evidence = new List<RuntimeIntegrationEvidence>();

        foreach (var observation in observations.Where(NetworkEvidencePolicy.IsApiCandidate))
        {
            // Configuration-derived entries carry no proof that anything was executed.
            if (observation.Source != EndpointDiscoverySource.AuthenticatedProxyTraffic)
                continue;

            var evidenceType = observation.Category switch
            {
                ObservedTrafficCategory.GraphQl => RuntimeEvidenceType.GraphQlOperationObserved,
                ObservedTrafficCategory.Rest => RuntimeEvidenceType.HttpRequestObserved,
                _ => RuntimeEvidenceType.Unknown
            };

            // Static assets, telemetry, websockets and authentication traffic are not this
            // integration's request/response behaviour.
            if (evidenceType == RuntimeEvidenceType.Unknown)
                continue;

            if (!Correlates(integration, observation))
                continue;

            var protocol = evidenceType == RuntimeEvidenceType.GraphQlOperationObserved
                ? IntegrationType.GraphQL
                : IntegrationType.REST;

            // Path already has query and fragment stripped by the proxy contract.
            var operationLabel = !string.IsNullOrWhiteSpace(observation.OperationName)
                ? observation.OperationName
                : $"{observation.Method} {observation.Path}".Trim();

            foreach (var sample in observation.Samples)
            {
                evidence.Add(new RuntimeIntegrationEvidence
                {
                    IntegrationId = integration.Id,
                    EvidenceType = evidenceType,
                    Source = RuntimeEvidenceSource.EndpointDiscovery,
                    Direction = RuntimeEvidenceDirection.Outbound,
                    Outcome = sample.Status >= 400
                        ? RuntimeEvidenceOutcome.Error
                        : RuntimeEvidenceOutcome.Success,
                    ObservedAt = sample.At.UtcDateTime,
                    DurationMs = sample.DurationMs,
                    Protocol = protocol,
                    OperationOrMessage = operationLabel,
                    StatusCodeOrOutcome = sample.Status.ToString()
                });
            }
        }

        return evidence
            .OrderBy(e => e.ObservedAt)
            .ToList();
    }

    /// <summary>
    /// Associates an observation with an integration by transport identity: same origin, and a
    /// path under the integration's configured path. Display name is never used, because a rename
    /// would silently detach an integration from its own traffic.
    /// </summary>
    private static bool Correlates(IntegrationConfigDto integration, ObservedNetworkEndpoint observation)
    {
        if (string.IsNullOrWhiteSpace(integration.Endpoint))
            return false;

        if (!Uri.TryCreate(integration.Endpoint.Trim(), UriKind.Absolute, out var configured))
            return false;

        var sameOrigin =
            string.Equals(configured.Scheme, observation.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(configured.Host, observation.Host, StringComparison.OrdinalIgnoreCase)
            && configured.Port == observation.Port;

        if (!sameOrigin)
            return false;

        var configuredPath = configured.AbsolutePath.TrimEnd('/');

        // An integration configured at the origin root matches any path on that origin.
        if (configuredPath is "" or "/")
            return true;

        var observedPath = (observation.Path ?? "").TrimEnd('/');

        return observedPath.Equals(configuredPath, StringComparison.Ordinal)
               || observedPath.StartsWith(configuredPath + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Messaging runtime evidence placeholder. Returns empty list; messaging observations not yet implemented.
    /// </summary>
    public List<RuntimeIntegrationEvidence> ExtractMessagingEvidence(
        string integrationId,
        IntegrationType type)
    {
        // Messaging observations (published/consumed messages) are not currently available.
        // This method is a placeholder for when messaging telemetry becomes available.
        return [];
    }
}
