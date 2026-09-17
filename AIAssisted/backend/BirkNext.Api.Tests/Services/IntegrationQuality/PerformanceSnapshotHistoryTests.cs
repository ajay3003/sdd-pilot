using System.Net;
using System.Text.Json;
using BirkNext.Api.Services;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationQuality;

/// <summary>
/// Phase 3 Checkpoint 6 closure: performance survives snapshot persistence and is compared across
/// successive reviews through the real orchestration path, not just the analyzer in isolation.
/// </summary>
public class PerformanceSnapshotHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 9, 30, 0, TimeSpan.Zero);

    private static IntegrationConfigDto Rest() => new()
    {
        Id = "i1", Name = "Orders", Type = IntegrationType.REST, Enabled = true,
        Endpoint = "https://api.example.test/orders", Resource = "orders"
    };

    private static ObservedNetworkEndpoint Traffic(params ObservedRequestSample[] samples) => new()
    {
        Scheme = "https", Host = "api.example.test", Port = 443, Path = "/orders", Method = "GET",
        Category = ObservedTrafficCategory.Rest,
        Source = EndpointDiscoverySource.AuthenticatedProxyTraffic,
        FirstObservedAt = T0, LastObservedAt = T0.AddSeconds(10),
        Samples = samples.ToList()
    };

    private static ObservedRequestSample S(double durationMs, int seconds, int status = 200) =>
        new(T0.AddSeconds(seconds), durationMs, status, null);

    /// <summary>Ten samples at a fixed latency, one per second, so percentiles are unambiguous.</summary>
    private static ObservedNetworkEndpoint SteadyTraffic(double latencyMs, int errorCount = 0)
    {
        var samples = Enumerable.Range(0, 10)
            .Select(i => S(latencyMs, i, i < errorCount ? 500 : 200))
            .ToArray();

        return Traffic(samples);
    }

    private static IntegrationQualityRequest Request(
        string environment, params ObservedNetworkEndpoint[] observations) =>
        new()
        {
            EnvironmentName = environment,
            Integrations = [Rest()],
            RuntimeObservations = observations.Length > 0 ? observations.ToList() : null
        };

    private static IntegrationQualityReviewService Service(IIntegrationQualitySnapshotRepository repository) =>
        new(new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://unused.example.test/") },
            NullLogger<IntegrationQualityReviewService>.Instance,
            new FakeGateway(),
            new IntegrationRelationshipPopulationService(),
            null, null, null, repository);

    // ── Run 1: measured, but nothing to compare against ──────────────────────

    [Fact]
    public async Task FirstRun_MeasuresPerformanceWithoutComparison()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var report = await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));

        var status = report.Statuses.Single(s => s.IntegrationId == "i1");

        Assert.Equal(PerformanceEvidenceState.Observed, status.Performance!.EvidenceState);
        Assert.Equal(100, status.Performance.P95DurationMs);
        Assert.Empty(status.PerformanceChanges);
    }

    [Fact]
    public async Task FirstRun_PersistsPerformanceSummary()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));

        var snapshot = await repository.GetLatestAsync("Dev");
        var entry = snapshot!.Integrations.Single();

        Assert.NotNull(entry.Performance);
        Assert.Equal(100, entry.Performance!.P95DurationMs);
        Assert.Equal(PerformanceEvidenceState.Observed, entry.Performance.EvidenceState);
    }

    // ── Run 2 and 3: comparison uses the immediately preceding snapshot ───────

    [Fact]
    public async Task SecondRun_ComparesAgainstFirst()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));
        var second = await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(150)));

        var change = second.Statuses.Single().PerformanceChanges.Single(c => c.Metric == "p95 latency");

        Assert.Equal(100, change.PreviousValue);
        Assert.Equal(150, change.CurrentValue);
        Assert.Equal(PerformanceChangeState.Regressed, change.ChangeState);
        Assert.Equal(50, change.PercentageChange);
    }

    [Fact]
    public async Task ThirdRun_ComparesAgainstSecondNotFirst()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));
        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(200)));
        var third = await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(210)));

        var change = third.Statuses.Single().PerformanceChanges.Single(c => c.Metric == "p95 latency");

        Assert.Equal(200, change.PreviousValue);
        Assert.NotEqual(100, change.PreviousValue);
    }

    [Fact]
    public async Task LatencyDecrease_IsImprovement()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(200)));
        var second = await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));

        Assert.Equal(PerformanceChangeState.Improved,
            second.Statuses.Single().PerformanceChanges
                .Single(c => c.Metric == "p95 latency").ChangeState);
    }

    [Fact]
    public async Task ErrorRateIncrease_IsRegression()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));
        var second = await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100, errorCount: 3)));

        var change = second.Statuses.Single().PerformanceChanges.Single(c => c.Metric == "error rate");

        Assert.Equal(PerformanceChangeState.Regressed, change.ChangeState);
        Assert.Equal(0.3, change.CurrentValue);
    }

    [Fact]
    public async Task ThroughputChange_IsReportedWithoutJudgement()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));
        var second = await Service(repository).AnalyzeAsync(
            Request("Dev", Traffic(S(100, 0), S(100, 20))));

        var change = second.Statuses.Single().PerformanceChanges
            .SingleOrDefault(c => c.Metric == "throughput");

        if (change is not null && change.ChangeState != PerformanceChangeState.Unchanged)
            Assert.Equal(PerformanceChangeState.Changed, change.ChangeState);
    }

    // ── Missing evidence on either side ──────────────────────────────────────

    [Fact]
    public async Task PreviousRunWithoutEvidence_ProducesNoFabricatedComparison()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        // Run 1 observed nothing, so it stored no performance summary.
        await Service(repository).AnalyzeAsync(Request("Dev"));
        var second = await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));

        Assert.Empty(second.Statuses.Single().PerformanceChanges);
    }

    [Fact]
    public async Task CurrentRunWithoutEvidence_IsUnavailableNotRegression()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();

        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));
        var second = await Service(repository).AnalyzeAsync(Request("Dev"));

        var status = second.Statuses.Single();

        Assert.Equal(PerformanceEvidenceState.Unavailable, status.Performance!.EvidenceState);
        Assert.DoesNotContain(status.PerformanceChanges,
            c => c.ChangeState == PerformanceChangeState.Regressed);
    }

    [Fact]
    public async Task NoObservations_LeavesPerformanceUnavailableAndUnpersisted()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        var report = await Service(repository).AnalyzeAsync(Request("Dev"));

        Assert.Equal(PerformanceEvidenceState.Unavailable,
            report.Statuses.Single().Performance!.EvidenceState);

        var snapshot = await repository.GetLatestAsync("Dev");
        Assert.Null(snapshot!.Integrations.Single().Performance);
    }

    [Fact]
    public async Task InsufficientSampleState_SurvivesIntoHistory()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        await Service(repository).AnalyzeAsync(Request("Dev", Traffic(S(100, 0), S(110, 1))));

        var entry = (await repository.GetLatestAsync("Dev"))!.Integrations.Single();

        // Two samples cannot support a distribution claim, and that weakness is carried forward
        // rather than being smoothed into a confident-looking number.
        Assert.Equal(PerformanceEvidenceState.InsufficientSamples, entry.Performance!.EvidenceState);
    }

    // ── Snapshot round-trip ──────────────────────────────────────────────────

    [Fact]
    public void PerformanceSummary_RoundTripsThroughJson()
    {
        var original = new IntegrationQualitySnapshot
        {
            EnvironmentId = "Dev",
            Integrations =
            [
                new IntegrationSnapshotEntry
                {
                    BaselineKey = "key-1",
                    Performance = new IntegrationPerformanceSummary
                    {
                        SampleCount = 842,
                        MinDurationMs = 41,
                        MaxDurationMs = 980,
                        AverageDurationMs = 88.5,
                        P50DurationMs = 72,
                        P95DurationMs = 141,
                        P99DurationMs = 260,
                        ErrorRate = 0.0036,
                        RequestsPerSecond = 4.8,
                        FirstObservedAt = new DateTime(2026, 9, 17, 9, 30, 0, DateTimeKind.Utc),
                        LastObservedAt = new DateTime(2026, 9, 17, 9, 33, 0, DateTimeKind.Utc),
                        EvidenceState = PerformanceEvidenceState.Observed
                    }
                }
            ]
        };

        var round = JsonSerializer.Deserialize<IntegrationQualitySnapshot>(
            JsonSerializer.Serialize(original));

        var performance = round!.Integrations.Single().Performance;

        Assert.NotNull(performance);
        Assert.Equal(842, performance!.SampleCount);
        Assert.Equal(41, performance.MinDurationMs);
        Assert.Equal(980, performance.MaxDurationMs);
        Assert.Equal(88.5, performance.AverageDurationMs);
        Assert.Equal(72, performance.P50DurationMs);
        Assert.Equal(141, performance.P95DurationMs);
        Assert.Equal(260, performance.P99DurationMs);
        Assert.Equal(0.0036, performance.ErrorRate);
        Assert.Equal(4.8, performance.RequestsPerSecond);
        Assert.Equal(original.Integrations[0].Performance!.FirstObservedAt, performance.FirstObservedAt);
        Assert.Equal(original.Integrations[0].Performance!.LastObservedAt, performance.LastObservedAt);
        Assert.Equal(PerformanceEvidenceState.Observed, performance.EvidenceState);
    }

    [Fact]
    public void UnmeasuredValues_RemainNullThroughRoundTrip()
    {
        var original = new IntegrationQualitySnapshot
        {
            EnvironmentId = "Dev",
            Integrations =
            [
                new IntegrationSnapshotEntry
                {
                    BaselineKey = "key-1",
                    Performance = new IntegrationPerformanceSummary
                    {
                        SampleCount = 1,
                        P50DurationMs = 100,
                        EvidenceState = PerformanceEvidenceState.InsufficientSamples
                        // throughput, error rate and window deliberately unmeasured
                    }
                }
            ]
        };

        var performance = JsonSerializer.Deserialize<IntegrationQualitySnapshot>(
            JsonSerializer.Serialize(original))!.Integrations.Single().Performance!;

        // A zero here would read as a measurement of zero rather than an absent measurement.
        Assert.Null(performance.RequestsPerSecond);
        Assert.Null(performance.ErrorRate);
        Assert.Null(performance.FirstObservedAt);
        Assert.Null(performance.LastObservedAt);
        Assert.Null(performance.MaxDurationMs);
    }

    // ── Legacy compatibility ─────────────────────────────────────────────────

    [Fact]
    public void LegacySnapshotWithoutPerformance_DeserializesSafely()
    {
        const string legacy = """
            {
              "snapshotId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
              "environmentId": "Dev",
              "capturedAt": "2026-09-16T14:32:00+00:00",
              "snapshotVersion": 1,
              "baselineIdentityVersion": 1,
              "completeness": 0,
              "integrations": [
                { "baselineKey": "key-1", "displayName": "Orders", "integrationType": 0 }
              ]
            }
            """;

        var snapshot = JsonSerializer.Deserialize<IntegrationQualitySnapshot>(legacy);

        Assert.NotNull(snapshot);
        var entry = snapshot!.Integrations.Single();

        Assert.Null(entry.Performance);
        Assert.Equal("key-1", entry.BaselineKey);
    }

    [Fact]
    public void LegacyEntryWithoutPerformance_ProducesNoComparison()
    {
        var analyzer = new IntegrationPerformanceAnalyzer();
        var current = analyzer.Analyze(
        [
            new RuntimeIntegrationEvidence
            {
                EvidenceType = RuntimeEvidenceType.HttpRequestObserved,
                Source = RuntimeEvidenceSource.EndpointDiscovery,
                Outcome = RuntimeEvidenceOutcome.Success,
                ObservedAt = DateTime.UtcNow,
                DurationMs = 100
            }
        ]);

        // No fabricated history from a snapshot that predates performance capture.
        Assert.Empty(analyzer.Compare(current, null));
    }

    // ── Persistence security ─────────────────────────────────────────────────

    [Fact]
    public async Task PersistedSnapshot_ContainsNoTrafficPayloadOrCredentials()
    {
        var repository = new InMemoryIntegrationQualitySnapshotRepository();
        await Service(repository).AnalyzeAsync(Request("Dev", SteadyTraffic(100)));

        var json = JsonSerializer.Serialize(await repository.GetLatestAsync("Dev"));

        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", json);

        // Only the derived summary persists, never the individual observed exchanges.
        Assert.DoesNotContain("samples", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("responseBytes", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) =>
            new() { Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.NotApplicable, PublicApi = true, Reason = "Public only" };

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(
            AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedReviewExecutionOutcome { Status = AuthenticatedExecutionStatus.NoContext });

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(
            AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedReviewExecutionOutcome { Status = AuthenticatedExecutionStatus.NoContext });

        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(
            AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedGraphQlSchemaOutcome { Status = AuthenticatedExecutionStatus.NoContext });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
    }
}
