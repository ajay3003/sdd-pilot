using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.LocalHttpsProxy;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Phase 3 Checkpoint 6: mapping observed browser traffic onto runtime evidence.
///
/// Checkpoint 2 defined the evidence model and its factories, but the review flow never populated
/// it, so RuntimeEvidenceSummary was always null and no performance could be derived. This wiring
/// completes that missing path; it was not previously active.
/// </summary>
public class RuntimeObservationMappingTests
{
    private readonly RuntimeEvidencePopulationService _service = new();
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 9, 30, 0, TimeSpan.Zero);

    private static IntegrationConfigDto Rest(string endpoint = "https://api.example.test/orders") =>
        new() { Id = "i1", Name = "Orders", Type = IntegrationType.REST, Endpoint = endpoint, Resource = "orders" };

    private static ObservedNetworkEndpoint Observation(
        string host = "api.example.test",
        string path = "/orders",
        int port = 443,
        string scheme = "https",
        ObservedTrafficCategory category = ObservedTrafficCategory.Rest,
        EndpointDiscoverySource source = EndpointDiscoverySource.AuthenticatedProxyTraffic,
        string? operationName = null,
        params ObservedRequestSample[] samples) =>
        new()
        {
            Scheme = scheme,
            Host = host,
            Port = port,
            Path = path,
            Method = "GET",
            Category = category,
            Source = source,
            OperationName = operationName,
            FirstObservedAt = T0,
            LastObservedAt = T0.AddSeconds(10),
            Samples = samples.ToList()
        };

    private static ObservedRequestSample S(double durationMs, int seconds = 0, int status = 200) =>
        new(T0.AddSeconds(seconds), durationMs, status, null);

    // ── Backward compatibility ───────────────────────────────────────────────

    [Fact]
    public void NoObservations_ProducesNoEvidence()
    {
        Assert.Empty(_service.MapObservations(Rest(), null));
        Assert.Empty(_service.MapObservations(Rest(), []));
    }

    // ── REST mapping ─────────────────────────────────────────────────────────

    [Fact]
    public void RestObservation_MapsToHttpEvidencePerSample()
    {
        var evidence = _service.MapObservations(Rest(),
            [Observation(samples: [S(100), S(150, 1), S(120, 2)])]);

        Assert.Equal(3, evidence.Count);
        Assert.All(evidence, e => Assert.Equal(RuntimeEvidenceType.HttpRequestObserved, e.EvidenceType));
        Assert.All(evidence, e => Assert.Equal(IntegrationType.REST, e.Protocol));
    }

    [Fact]
    public void DurationAndTimestamp_ArePreservedPerSample()
    {
        var evidence = _service.MapObservations(Rest(), [Observation(samples: [S(137, 5)])]);

        var single = Assert.Single(evidence);
        Assert.Equal(137, single.DurationMs);
        Assert.Equal(T0.AddSeconds(5).UtcDateTime, single.ObservedAt);
    }

    [Fact]
    public void SuccessAndErrorOutcomes_AreDerivedFromStatus()
    {
        var evidence = _service.MapObservations(Rest(),
            [Observation(samples: [S(100, 0, 200), S(100, 1, 404), S(100, 2, 500)])]);

        Assert.Equal(1, evidence.Count(e => e.Outcome == RuntimeEvidenceOutcome.Success));
        Assert.Equal(2, evidence.Count(e => e.Outcome == RuntimeEvidenceOutcome.Error));
    }

    [Fact]
    public void StatusCode_IsPreserved()
    {
        var evidence = _service.MapObservations(Rest(), [Observation(samples: [S(100, 0, 503)])]);
        Assert.Equal("503", Assert.Single(evidence).StatusCodeOrOutcome);
    }

    [Fact]
    public void RepeatedCalls_AreRetainedAsDistinctSamples()
    {
        // Repeated requests are legitimate performance samples and must not be collapsed.
        var evidence = _service.MapObservations(Rest(),
            [Observation(samples: [S(100), S(100, 1), S(100, 2), S(100, 3)])]);

        Assert.Equal(4, evidence.Count);
    }

    [Fact]
    public void Evidence_IsOrderedByObservationTime()
    {
        var evidence = _service.MapObservations(Rest(),
            [Observation(samples: [S(100, 5), S(100, 1), S(100, 3)])]);

        Assert.Equal(evidence.OrderBy(e => e.ObservedAt).Select(e => e.ObservedAt), evidence.Select(e => e.ObservedAt));
    }

    // ── GraphQL mapping ──────────────────────────────────────────────────────

    [Fact]
    public void GraphQlObservation_MapsToGraphQlEvidence()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "g1", Name = "Tjeneste GraphQL", Type = IntegrationType.GraphQL,
            Endpoint = "https://api.example.test/graphql"
        };

        var evidence = _service.MapObservations(integration,
            [Observation(path: "/graphql", category: ObservedTrafficCategory.GraphQl,
                         operationName: "HentTjenesterForBarn", samples: [S(220)])]);

        var single = Assert.Single(evidence);
        Assert.Equal(RuntimeEvidenceType.GraphQlOperationObserved, single.EvidenceType);
        Assert.Equal(IntegrationType.GraphQL, single.Protocol);
        Assert.Equal("HentTjenesterForBarn", single.OperationOrMessage);
    }

    [Fact]
    public void GraphQlWithoutOperationName_FallsBackToSanitizedPath()
    {
        var integration = new IntegrationConfigDto
        {
            Id = "g1", Name = "GraphQL", Type = IntegrationType.GraphQL,
            Endpoint = "https://api.example.test/graphql"
        };

        var evidence = _service.MapObservations(integration,
            [Observation(path: "/graphql", category: ObservedTrafficCategory.GraphQl, samples: [S(100)])]);

        Assert.Contains("/graphql", Assert.Single(evidence).OperationOrMessage);
    }

    // ── Provenance and evidence-source discipline ────────────────────────────

    [Fact]
    public void ConfigurationDerivedEndpoints_AreNotEvidence()
    {
        // These describe what exists, not what ran. Treating them as evidence would give a
        // configured-but-never-called endpoint performance metrics it never earned.
        foreach (var source in new[]
                 {
                     EndpointDiscoverySource.PublicConfiguration,
                     EndpointDiscoverySource.ConfigurationDiscovery,
                     EndpointDiscoverySource.ManualConfiguration,
                     EndpointDiscoverySource.Unknown
                 })
        {
            var evidence = _service.MapObservations(Rest(),
                [Observation(source: source, samples: [S(100)])]);

            Assert.Empty(evidence);
        }
    }

    [Fact]
    public void ObservedTraffic_IsAttributedToEndpointDiscovery()
    {
        var evidence = _service.MapObservations(Rest(), [Observation(samples: [S(100)])]);
        Assert.Equal(RuntimeEvidenceSource.EndpointDiscovery, Assert.Single(evidence).Source);
    }

    [Fact]
    public void NonApiTrafficCategories_AreExcluded()
    {
        foreach (var category in new[]
                 {
                     ObservedTrafficCategory.StaticAsset,
                     ObservedTrafficCategory.Telemetry,
                     ObservedTrafficCategory.WebSocket,
                     ObservedTrafficCategory.Authentication,
                     ObservedTrafficCategory.OtherHttp,
                     ObservedTrafficCategory.Unknown
                 })
        {
            Assert.Empty(_service.MapObservations(Rest(),
                [Observation(category: category, samples: [S(100)])]));
        }
    }

    // ── Correlation ──────────────────────────────────────────────────────────

    [Fact]
    public void ObservationOnDifferentHost_IsNotCorrelated() =>
        Assert.Empty(_service.MapObservations(Rest(),
            [Observation(host: "other.example.test", samples: [S(100)])]));

    [Fact]
    public void ObservationOnDifferentPort_IsNotCorrelated() =>
        Assert.Empty(_service.MapObservations(Rest(),
            [Observation(port: 8443, samples: [S(100)])]));

    [Fact]
    public void ObservationOnUnrelatedPath_IsNotCorrelated() =>
        Assert.Empty(_service.MapObservations(Rest(),
            [Observation(path: "/invoices", samples: [S(100)])]));

    [Fact]
    public void SubPathsOfConfiguredEndpoint_AreCorrelated()
    {
        var evidence = _service.MapObservations(Rest(),
            [Observation(path: "/orders/42", samples: [S(100)])]);

        Assert.Single(evidence);
    }

    [Fact]
    public void PathPrefixCollision_IsNotCorrelated()
    {
        // "/ordersearch" must not match an integration configured at "/orders".
        Assert.Empty(_service.MapObservations(Rest(),
            [Observation(path: "/ordersearch", samples: [S(100)])]));
    }

    [Fact]
    public void IntegrationAtOriginRoot_CorrelatesAnyPathOnThatOrigin()
    {
        var evidence = _service.MapObservations(Rest("https://api.example.test"),
            [Observation(path: "/anything", samples: [S(100)])]);

        Assert.Single(evidence);
    }

    [Fact]
    public void CorrelationDoesNotDependOnDisplayName()
    {
        var renamed = Rest();
        renamed.Name = "Completely Different Name";

        Assert.Single(_service.MapObservations(renamed, [Observation(samples: [S(100)])]));
    }

    [Fact]
    public void IntegrationWithoutEndpoint_CorrelatesNothing()
    {
        var noEndpoint = new IntegrationConfigDto
        {
            Id = "m1", Name = "Events", Type = IntegrationType.EventHub, Resource = "hub"
        };

        Assert.Empty(_service.MapObservations(noEndpoint, [Observation(samples: [S(100)])]));
    }

    // ── Messaging ────────────────────────────────────────────────────────────

    [Fact]
    public void MessagingIntegration_GetsNoEvidenceFromBrowserTraffic()
    {
        var eventHub = new IntegrationConfigDto
        {
            Id = "m1", Name = "Placement Events", Type = IntegrationType.EventHub,
            Endpoint = "ns", Resource = "hub"
        };

        Assert.Empty(_service.MapObservations(eventHub, [Observation(samples: [S(100)])]));
    }

    // ── Security ─────────────────────────────────────────────────────────────

    [Fact]
    public void MappedEvidence_CarriesNoCredentialOrQueryData()
    {
        // The shared observation contract strips query and fragment and never carries a header
        // value, cookie or body, so nothing secret can reach evidence through this path.
        var evidence = _service.MapObservations(Rest(), [Observation(samples: [S(100)])]);
        var json = System.Text.Json.JsonSerializer.Serialize(evidence);

        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", json);
    }

    [Fact]
    public void OperationLabel_ContainsNoQueryString()
    {
        var evidence = _service.MapObservations(Rest(), [Observation(samples: [S(100)])]);
        Assert.DoesNotContain("?", Assert.Single(evidence).OperationOrMessage!);
    }

    // ── Evidence summary and performance, end to end ─────────────────────────

    [Fact]
    public void EvidenceSummary_IsPopulatedFromObservations()
    {
        var evidence = _service.MapObservations(Rest(),
            [Observation(samples: [S(100), S(200, 1), S(300, 2)])]);

        var summary = _service.BuildEvidenceSummary("i1", evidence);

        Assert.True(summary.HasRuntimeEvidence);
        Assert.Equal(3, summary.EvidenceCount);
        Assert.Contains(RuntimeEvidenceSource.EndpointDiscovery, summary.Sources);
        Assert.Equal(100, summary.MinDurationMs);
        Assert.Equal(300, summary.MaxDurationMs);
    }

    [Fact]
    public void ObservedTraffic_ProducesRealPerformanceMetrics()
    {
        var analyzer = new IntegrationPerformanceAnalyzer();

        var evidence = _service.MapObservations(Rest(),
            [Observation(samples:
            [
                S(10), S(20, 1), S(30, 2), S(40, 3), S(50, 4),
                S(60, 5), S(70, 6), S(80, 7), S(90, 8), S(100, 9)
            ])]);

        var metrics = analyzer.Analyze(evidence);

        Assert.Equal(PerformanceEvidenceState.Observed, metrics.EvidenceState);
        Assert.Equal(10, metrics.TimedSampleCount);
        Assert.Equal(10, metrics.MinDurationMs);
        Assert.Equal(100, metrics.MaxDurationMs);
        Assert.Equal(55, metrics.AverageDurationMs);
        Assert.Equal(9, metrics.ObservationWindowSeconds);
        Assert.NotNull(metrics.RequestsPerSecond);
    }

    [Fact]
    public void ErrorRate_ComesFromObservedStatuses()
    {
        var analyzer = new IntegrationPerformanceAnalyzer();

        var evidence = _service.MapObservations(Rest(),
            [Observation(samples: [S(10), S(20, 1), S(30, 2), S(40, 3), S(50, 4, 500)])]);

        Assert.Equal(0.2, analyzer.Analyze(evidence).ErrorRate);
    }

    [Fact]
    public void NoObservations_LeavesPerformanceUnavailable()
    {
        var analyzer = new IntegrationPerformanceAnalyzer();
        var metrics = analyzer.Analyze(_service.MapObservations(Rest(), null));

        Assert.Equal(PerformanceEvidenceState.Unavailable, metrics.EvidenceState);
        Assert.Null(metrics.P95DurationMs);
        Assert.Null(metrics.RequestsPerSecond);
        Assert.Null(metrics.ErrorRate);
    }
}
