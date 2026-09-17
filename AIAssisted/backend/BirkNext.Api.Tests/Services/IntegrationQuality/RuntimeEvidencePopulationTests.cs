using BirkNext.Api.Services.IntegrationQuality;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Tests for Phase 3 Checkpoint 2: Runtime Integration Evidence Population
/// Verifies that runtime observations (HTTP, GraphQL, authenticated checks) are converted
/// to typed RuntimeIntegrationEvidence distinct from reachability probes.
/// </summary>
public class RuntimeEvidencePopulationTests
{
    private readonly RuntimeEvidencePopulationService _service = new();

    [Fact]
    public void HttpEvidenceCreation_PopulatesCorrectFields()
    {
        var evidence = _service.CreateHttpEvidence(
            integrationId: "rest-1",
            observedAt: new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            durationMs: 42.5,
            statusCode: "200",
            isError: false);

        Assert.Equal("rest-1", evidence.IntegrationId);
        Assert.Equal(RuntimeEvidenceType.HttpRequestObserved, evidence.EvidenceType);
        Assert.Equal(RuntimeEvidenceSource.EndpointDiscovery, evidence.Source);
        Assert.Equal(RuntimeEvidenceDirection.Outbound, evidence.Direction);
        Assert.Equal(RuntimeEvidenceOutcome.Success, evidence.Outcome);
        Assert.Equal(42.5, evidence.DurationMs);
        Assert.Equal("200", evidence.StatusCodeOrOutcome);
        Assert.Equal(IntegrationType.REST, evidence.Protocol);
    }

    [Fact]
    public void HttpEvidenceWithErrorStatus_MarksAsError()
    {
        var evidence = _service.CreateHttpEvidence(
            integrationId: "rest-2",
            observedAt: DateTime.UtcNow,
            durationMs: 100,
            statusCode: "500",
            isError: true);

        Assert.Equal(RuntimeEvidenceOutcome.Error, evidence.Outcome);
        Assert.Equal("500", evidence.StatusCodeOrOutcome);
    }

    [Fact]
    public void GraphQlEvidenceCreation_IncludesOperationName()
    {
        var evidence = _service.CreateGraphQlEvidence(
            integrationId: "gql-1",
            operationName: "GetUserQuery",
            observedAt: new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            durationMs: 125.3,
            hasErrors: false);

        Assert.Equal("gql-1", evidence.IntegrationId);
        Assert.Equal(RuntimeEvidenceType.GraphQlOperationObserved, evidence.EvidenceType);
        Assert.Equal(RuntimeEvidenceSource.EndpointDiscovery, evidence.Source);
        Assert.Equal("GetUserQuery", evidence.OperationOrMessage);
        Assert.Equal(125.3, evidence.DurationMs);
        Assert.Equal(RuntimeEvidenceOutcome.Success, evidence.Outcome);
        Assert.Equal(IntegrationType.GraphQL, evidence.Protocol);
    }

    [Fact]
    public void GraphQlEvidenceWithErrors_MarksAsError()
    {
        var evidence = _service.CreateGraphQlEvidence(
            integrationId: "gql-2",
            operationName: "GetUserMutation",
            observedAt: DateTime.UtcNow,
            durationMs: 50,
            hasErrors: true);

        Assert.Equal(RuntimeEvidenceOutcome.Error, evidence.Outcome);
        Assert.Equal("errors", evidence.StatusCodeOrOutcome);
    }

    [Fact]
    public void AuthenticatedCheckEvidence_UsesAuthenticatedProxySource()
    {
        var evidence = _service.CreateAuthenticatedCheckEvidence(
            integrationId: "auth-1",
            observedAt: new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            durationMs: 35.2,
            statusCode: 200);

        Assert.Equal("auth-1", evidence.IntegrationId);
        Assert.Equal(RuntimeEvidenceType.HttpRequestObserved, evidence.EvidenceType);
        Assert.Equal(RuntimeEvidenceSource.AuthenticatedProxy, evidence.Source);
        Assert.Equal(RuntimeEvidenceDirection.Outbound, evidence.Direction);
        Assert.Equal(RuntimeEvidenceOutcome.Success, evidence.Outcome);
        Assert.Equal(35.2, evidence.DurationMs);
        Assert.Equal("200", evidence.StatusCodeOrOutcome);
    }

    [Fact]
    public void AuthenticatedCheckWithError_MarksClientErrorAsError()
    {
        var evidence = _service.CreateAuthenticatedCheckEvidence(
            integrationId: "auth-2",
            observedAt: DateTime.UtcNow,
            durationMs: 45,
            statusCode: 404);

        Assert.Equal(RuntimeEvidenceOutcome.Error, evidence.Outcome);
        Assert.Equal("404", evidence.StatusCodeOrOutcome);
    }

    [Fact]
    public void EvidenceSummary_WithNoEvidence_ReturnsFalse()
    {
        var summary = _service.BuildEvidenceSummary("int-1", []);

        Assert.False(summary.HasRuntimeEvidence);
        Assert.Equal(0, summary.EvidenceCount);
        Assert.Empty(summary.Sources);
        Assert.Empty(summary.EvidenceTypes);
        Assert.Null(summary.LastObservedAt);
        Assert.Null(summary.MinDurationMs);
        Assert.Null(summary.MaxDurationMs);
        Assert.Null(summary.AvgDurationMs);
    }

    [Fact]
    public void EvidenceSummary_WithMultipleEvidence_AggregatesDurations()
    {
        var evidence = new List<RuntimeIntegrationEvidence>
        {
            _service.CreateHttpEvidence("int-1", DateTime.UtcNow.AddSeconds(-10), 50),
            _service.CreateHttpEvidence("int-1", DateTime.UtcNow.AddSeconds(-5), 100),
            _service.CreateHttpEvidence("int-1", DateTime.UtcNow, 75),
            _service.CreateGraphQlEvidence("int-1", "Query", DateTime.UtcNow.AddSeconds(-3), 120)
        };

        var summary = _service.BuildEvidenceSummary("int-1", evidence);

        Assert.True(summary.HasRuntimeEvidence);
        Assert.Equal(4, summary.EvidenceCount);
        Assert.Contains(RuntimeEvidenceSource.EndpointDiscovery, summary.Sources);
        Assert.Contains(RuntimeEvidenceType.HttpRequestObserved, summary.EvidenceTypes);
        Assert.Contains(RuntimeEvidenceType.GraphQlOperationObserved, summary.EvidenceTypes);
        Assert.Equal(50, summary.MinDurationMs);
        Assert.Equal(120, summary.MaxDurationMs);
        Assert.Equal(86.25, summary.AvgDurationMs);  // (50 + 100 + 75 + 120) / 4
    }

    [Fact]
    public void EvidenceSummary_LastObservedAt_IsMaximum()
    {
        var time1 = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var time2 = new DateTime(2024, 1, 15, 10, 5, 0, DateTimeKind.Utc);
        var time3 = new DateTime(2024, 1, 15, 10, 3, 0, DateTimeKind.Utc);

        var evidence = new List<RuntimeIntegrationEvidence>
        {
            _service.CreateHttpEvidence("int-1", time1, 50),
            _service.CreateHttpEvidence("int-1", time2, 75),
            _service.CreateHttpEvidence("int-1", time3, 60)
        };

        var summary = _service.BuildEvidenceSummary("int-1", evidence);

        Assert.Equal(time2, summary.LastObservedAt);
    }

    [Fact]
    public void EvidenceSummary_WithoutDurations_SkipsAggregation()
    {
        var evidence = new List<RuntimeIntegrationEvidence>
        {
            new RuntimeIntegrationEvidence
            {
                IntegrationId = "int-1",
                EvidenceType = RuntimeEvidenceType.MessagePublished,
                Source = RuntimeEvidenceSource.MessagingAdapter,
                Direction = RuntimeEvidenceDirection.Outbound,
                Outcome = RuntimeEvidenceOutcome.Success,
                ObservedAt = DateTime.UtcNow,
                DurationMs = null  // No duration
            }
        };

        var summary = _service.BuildEvidenceSummary("int-1", evidence);

        Assert.True(summary.HasRuntimeEvidence);
        Assert.Equal(1, summary.EvidenceCount);
        Assert.Null(summary.MinDurationMs);
        Assert.Null(summary.MaxDurationMs);
        Assert.Null(summary.AvgDurationMs);
    }

    [Fact]
    public void MessagingEvidence_ReturnsEmptyList()
    {
        var evidence = _service.ExtractMessagingEvidence("int-1", IntegrationType.EventHub);
        Assert.Empty(evidence);

        evidence = _service.ExtractMessagingEvidence("int-1", IntegrationType.Kafka);
        Assert.Empty(evidence);

        evidence = _service.ExtractMessagingEvidence("int-1", IntegrationType.RabbitMQ);
        Assert.Empty(evidence);
    }

    [Fact]
    public void EvidenceSummary_TracksDistinctSources()
    {
        var evidence = new List<RuntimeIntegrationEvidence>
        {
            _service.CreateHttpEvidence("int-1", DateTime.UtcNow, 50),
            _service.CreateAuthenticatedCheckEvidence("int-1", DateTime.UtcNow, 60, 200)
        };

        var summary = _service.BuildEvidenceSummary("int-1", evidence);

        Assert.Equal(2, summary.Sources.Count);
        Assert.Contains(RuntimeEvidenceSource.EndpointDiscovery, summary.Sources);
        Assert.Contains(RuntimeEvidenceSource.AuthenticatedProxy, summary.Sources);
    }

    [Fact]
    public void EvidenceSummary_TracksDistinctEvidenceTypes()
    {
        var evidence = new List<RuntimeIntegrationEvidence>
        {
            _service.CreateHttpEvidence("int-1", DateTime.UtcNow, 50),
            _service.CreateGraphQlEvidence("int-1", "Query", DateTime.UtcNow, 75)
        };

        var summary = _service.BuildEvidenceSummary("int-1", evidence);

        Assert.Equal(2, summary.EvidenceTypes.Count);
        Assert.Contains(RuntimeEvidenceType.HttpRequestObserved, summary.EvidenceTypes);
        Assert.Contains(RuntimeEvidenceType.GraphQlOperationObserved, summary.EvidenceTypes);
    }

    [Fact]
    public void HttpEvidenceWithoutStatus_DefaultsToUnknown()
    {
        var evidence = _service.CreateHttpEvidence(
            integrationId: "rest-3",
            observedAt: DateTime.UtcNow,
            durationMs: 50,
            statusCode: null);

        Assert.Equal("unknown", evidence.StatusCodeOrOutcome);
    }

    [Fact]
    public void EvidenceSeparatesFromReachability()
    {
        // Key principle: RuntimeEvidenceType does NOT include reachability probes.
        // Reachability (health/worker URLs) remains separate from runtime observations.
        var httpEvidence = _service.CreateHttpEvidence("int-1", DateTime.UtcNow, 50, "200");
        var authenticatedCheckEvidence = _service.CreateAuthenticatedCheckEvidence("int-1", DateTime.UtcNow, 45, 200);

        // Both are runtime evidence (actual traffic), not reachability probes
        Assert.Equal(RuntimeEvidenceType.HttpRequestObserved, httpEvidence.EvidenceType);
        Assert.Equal(RuntimeEvidenceType.HttpRequestObserved, authenticatedCheckEvidence.EvidenceType);

        // But sources differ to indicate provenance
        Assert.Equal(RuntimeEvidenceSource.EndpointDiscovery, httpEvidence.Source);
        Assert.Equal(RuntimeEvidenceSource.AuthenticatedProxy, authenticatedCheckEvidence.Source);
    }
}
