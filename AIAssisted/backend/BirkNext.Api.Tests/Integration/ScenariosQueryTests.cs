using BirkNext.Api.Data;
using BirkNext.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;
using BirkNext.Api.Tests.Utilities;

namespace BirkNext.Api.Tests.Integration;

/// <summary>
/// One PostgreSQL container + API host per test (xUnit creates a class instance per test). Every SQL command the API's
/// <see cref="AppDbContext"/> sends is counted, so query tests can prove how many round-trips a request costs.
/// </summary>
public abstract class ScenariosQueryHost : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    protected readonly ITestOutputHelper Output;
    protected WebApplicationFactory<Program> Factory = null!;
    protected HttpClient Client = null!;
    protected SqlCommandCounter Sql { get; } = new();

    protected ScenariosQueryHost(ITestOutputHelper output)
    {
        Output = output;
        _postgres = new PostgreSqlBuilder("postgres:16")
            .WithDatabase("birknext_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseNpgsql(_postgres.GetConnectionString()).AddInterceptors(Sql));
                }));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        Client = Factory.CreateLocalSessionClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected static StringContent GqlRequest(string query, object? variables = null)
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    protected async Task SeedAsync(params Scenario[] scenarios)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Scenarios.AddRange(scenarios);
        await db.SaveChangesAsync();
    }

    /// <summary>Counts SQL commands (thread-safe). Hosted services share the context registration, so callers filter by table.</summary>
    protected sealed class SqlCommandCounter : DbCommandInterceptor
    {
        private int _scenarioCommands;
        private int _allCommands;
        public int ScenarioCommands => Volatile.Read(ref _scenarioCommands);
        public int AllCommands => Volatile.Read(ref _allCommands);
        public void Reset() { Interlocked.Exchange(ref _scenarioCommands, 0); Interlocked.Exchange(ref _allCommands, 0); }

        private void Count(DbCommand command)
        {
            Interlocked.Increment(ref _allCommands);
            if (ScenariosTableRe.IsMatch(command.CommandText)) Interlocked.Increment(ref _scenarioCommands);
        }

        private static readonly Regex ScenariosTableRe = new(@"\bscenarios\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Count(command); return base.ReaderExecuting(command, eventData, result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count(command); return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Count(command); return base.ScalarExecuting(command, eventData, result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Count(command); return base.ScalarExecutingAsync(command, eventData, result, cancellationToken); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Count(command); return base.NonQueryExecuting(command, eventData, result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Count(command); return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken); }
    }
}

public class ScenariosQueryTests(ITestOutputHelper output) : ScenariosQueryHost(output)
{
    [Fact]
    public async Task Scenarios_ReturnsAllScenariosForProjectId()
    {
        await SeedAsync(
            new Scenario { Title = "First", Kind = ScenarioKind.Requirement, ProjectId = "proj-q01", CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) },
            new Scenario { Title = "Second", Kind = ScenarioKind.Test, ProjectId = "proj-q01", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new Scenario { Title = "Third", Kind = ScenarioKind.Test, ProjectId = "proj-q01", CreatedAt = DateTimeOffset.UtcNow }
        );

        const string query = """
            query GetScenarios($projectId: String!) {
              scenarios(projectId: $projectId) {
                id title kind createdAt
              }
            }
            """;

        var response = await Client.PostAsync("/graphql", GqlRequest(query, new { projectId = "proj-q01" }));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var scenarios = doc.RootElement
            .GetProperty("data")
            .GetProperty("scenarios");

        scenarios.GetArrayLength().Should().Be(3);

        var titles = scenarios.EnumerateArray()
            .Select(s => s.GetProperty("title").GetString())
            .ToList();
        titles.Should().Equal("Third", "Second", "First");
    }

    [Fact]
    public async Task Scenarios_EmptyProject_ReturnsEmptyArray()
    {
        const string query = """
            query GetScenarios($projectId: String!) {
              scenarios(projectId: $projectId) {
                id title
              }
            }
            """;

        var response = await Client.PostAsync("/graphql", GqlRequest(query, new { projectId = "proj-empty" }));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var scenarios = doc.RootElement
            .GetProperty("data")
            .GetProperty("scenarios");

        scenarios.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Scenarios_UnknownProjectId_DoesNotLeakScenariosFromOtherProjects()
    {
        await SeedAsync(
            new Scenario { Title = "Other project scenario", Kind = ScenarioKind.Test, ProjectId = "proj-other-leak" }
        );

        const string query = """
            query GetScenarios($projectId: String!) {
              scenarios(projectId: $projectId) {
                id title
              }
            }
            """;

        var response = await Client.PostAsync("/graphql", GqlRequest(query, new { projectId = "proj-unknown-leak" }));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var scenarios = doc.RootElement
            .GetProperty("data")
            .GetProperty("scenarios");

        scenarios.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Scenarios_MissingProjectId_IsRejectedByGraphQlValidation()
    {
        // Omit the required projectId argument — schema validation must reject this
        // before the resolver is reached, so data must be null and errors non-empty.
        const string query = """
            {
              scenarios {
                id title
              }
            }
            """;

        var response = await Client.PostAsync("/graphql", GqlRequest(query));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("errors", out var errors).Should().BeTrue();
        errors.GetArrayLength().Should().BeGreaterThan(0);

        if (doc.RootElement.TryGetProperty("data", out var data))
            data.ValueKind.Should().Be(JsonValueKind.Null);
    }
}

/// <summary>
/// Timing-sensitive tests run alone: parallelization is disabled for this collection, so xUnit runs it after every parallel collection has
/// finished and with no other test of this assembly running. Otherwise the wall-clock samples measure whichever other test classes are
/// starting containers, API hosts and browsers at the same moment (the 3–4 s outliers seen in full-partition runs), not the query.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScenariosQueryPerformanceCollection
{
    public const string Name = "Scenarios query performance";
}

[Collection(ScenariosQueryPerformanceCollection.Name)]
public class ScenariosQueryPerformanceTests(ITestOutputHelper output) : ScenariosQueryHost(output)
{
    private const int WarmupRequests = 10;
    private const int MeasuredRequests = 60;

    // ── T048 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scenarios_With100Records_ReturnsAllOrderedDescAndWarmedP95CompletesWithinTwoSeconds()
    {
        const string projectId = "proj-perf-100";
        const int count = 100;

        var baseTime = DateTimeOffset.UtcNow.AddDays(-count);
        var scenarios = Enumerable.Range(0, count)
            .Select(i => new Scenario
            {
                Title = $"Scenario {i:D3}",
                Kind = ScenarioKind.Requirement, // uniform kind so CreatedAt DESC ordering applies
                ProjectId = projectId,
                CreatedAt = baseTime.AddMinutes(i)
            })
            .ToArray();

        await SeedAsync(scenarios);

        const string query = """
            query GetScenarios($projectId: String!) {
              scenarios(projectId: $projectId) {
                id title createdAt
              }
            }
            """;

        async Task<string> RequestAsync()
        {
            using var response = await Client.PostAsync("/graphql", GqlRequest(query, new { projectId }));
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        // Defined warmup outside the measurement: schema build and operation cache (GraphQL), EF query compilation, the Npgsql pool's first
        // physical connection, tiered JIT of the request path — and the startup hosted services finishing their first database pass.
        for (var i = 0; i < WarmupRequests; i++)
            await RequestAsync();

        Sql.Reset();
        var durations = new List<double>(MeasuredRequests);
        var json = "";
        for (var i = 0; i < MeasuredRequests; i++)
        {
            // The full request as a client sees it: send, server pipeline + query, and reading the 100-item body.
            var started = Stopwatch.GetTimestamp();
            json = await RequestAsync();
            durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        var scenarioCommands = Sql.ScenarioCommands;

        var sorted = durations.Order().ToArray();
        double p50 = LatencyPercentiles.NearestRank(sorted, 50), p95 = LatencyPercentiles.NearestRank(sorted, 95), p99 = LatencyPercentiles.NearestRank(sorted, 99);
        var summary = $"n={sorted.Length} (after {WarmupRequests} warmup) min={sorted[0]:0.0} p50={p50:0.0} p95={p95:0.0} p99={p99:0.0} max={sorted[^1]:0.0} ms; "
            + $"SQL commands on scenarios={scenarioCommands} (all commands incl. background services={Sql.AllCommands}); "
            + $"samples in order: {string.Join(", ", durations.Select(d => d.ToString("0")))}";
        Output.WriteLine($"Warmed 100-scenario query: {summary}");

        using var doc = JsonDocument.Parse(json);

        var items = doc.RootElement
            .GetProperty("data")
            .GetProperty("scenarios");

        items.GetArrayLength().Should().Be(count);

        var timestamps = items.EnumerateArray()
            .Select(s => DateTimeOffset.Parse(s.GetProperty("createdAt").GetString()!))
            .ToList();

        timestamps.Should().BeInDescendingOrder();

        // One SELECT per request whatever the row count: no N+1 (Scenario has no navigations; adding one must not add a query per row).
        scenarioCommands.Should().Be(MeasuredRequests, "each scenarios request must cost exactly one SQL round-trip; {0}", summary);

        p95.Should().BeLessThan(2000, "the warmed 100-scenario query should complete under two seconds for at least 95% of requests (nearest-rank); {0}", summary);
    }
}

/// <summary>Latency percentiles for timing assertions.</summary>
public static class LatencyPercentiles
{
    /// <summary>Nearest-rank percentile: the smallest sample with at least <paramref name="percent"/>% of the samples at or below it
    /// (rank = ceil(percent·n/100), computed in integers so no floating-point rounding moves the rank).</summary>
    public static double NearestRank(IReadOnlyList<double> sortedAscending, int percent) =>
        sortedAscending[Math.Clamp((percent * sortedAscending.Count + 99) / 100 - 1, 0, sortedAscending.Count - 1)];
}

public class LatencyPercentilesTests
{
    [Fact]
    public void NearestRank_IsTheSmallestSampleCoveringThePercentile()
    {
        var hundred = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        LatencyPercentiles.NearestRank(hundred, 50).Should().Be(50);
        LatencyPercentiles.NearestRank(hundred, 95).Should().Be(95);
        LatencyPercentiles.NearestRank(hundred, 99).Should().Be(99);
        var sixty = Enumerable.Range(1, 60).Select(i => (double)i).ToArray();
        LatencyPercentiles.NearestRank(sixty, 95).Should().Be(57, "with 60 samples the 3 slowest lie above p95");
        LatencyPercentiles.NearestRank(sixty, 99).Should().Be(60);
        var twenty = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        LatencyPercentiles.NearestRank(twenty, 95).Should().Be(19, "with 20 samples p95 is the second-largest sample: two hiccups fail it");
    }
}
