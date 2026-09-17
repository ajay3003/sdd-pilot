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

        var durations = evidence.Where(e => e.DurationMs.HasValue).Select(e => e.DurationMs.Value).ToList();

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
