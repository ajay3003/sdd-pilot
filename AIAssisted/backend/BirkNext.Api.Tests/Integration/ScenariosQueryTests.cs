using BirkNext.Api.Data;
using BirkNext.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Integration;

public class ScenariosQueryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private readonly ITestOutputHelper _output;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ScenariosQueryTests(ITestOutputHelper output)
    {
        _output = output;
        _postgres = new PostgreSqlBuilder("postgres:16")
            .WithDatabase("birknext_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseNpgsql(_postgres.GetConnectionString()));
                }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static StringContent GqlRequest(string query, object? variables = null)
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private async Task SeedAsync(params Scenario[] scenarios)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Scenarios.AddRange(scenarios);
        await db.SaveChangesAsync();
    }

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

        var response = await _client.PostAsync("/graphql", GqlRequest(query, new { projectId = "proj-q01" }));
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

        var response = await _client.PostAsync("/graphql", GqlRequest(query, new { projectId = "proj-empty" }));
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

        var response = await _client.PostAsync("/graphql", GqlRequest(query, new { projectId = "proj-unknown-leak" }));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var scenarios = doc.RootElement
            .GetProperty("data")
            .GetProperty("scenarios");

        scenarios.GetArrayLength().Should().Be(0);
    }

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

        // The first GraphQL operation initializes the schema and pipeline. Keep that
        // cold-start work outside this steady-state query performance assertion.
        using (var warmup = await _client.PostAsync("/graphql", GqlRequest(query, new { projectId })))
            warmup.EnsureSuccessStatusCode();

        const int measuredRequests = 20;
        var durations = new List<long>(measuredRequests);
        string json = "";
        for (var i = 0; i < measuredRequests; i++)
        {
            var sw = Stopwatch.StartNew();
            using var response = await _client.PostAsync("/graphql", GqlRequest(query, new { projectId }));
            sw.Stop();
            response.EnsureSuccessStatusCode();
            durations.Add(sw.ElapsedMilliseconds);
            json = await response.Content.ReadAsStringAsync();
        }

        var orderedDurations = durations.Order().ToArray();
        var p95Index = (int)Math.Ceiling(orderedDurations.Length * 0.95) - 1;
        var p95 = orderedDurations[p95Index];
        _output.WriteLine($"Warmed 100-scenario query timings (ms): min={orderedDurations[0]}, median={orderedDurations[orderedDurations.Length / 2]}, p95={p95}, max={orderedDurations[^1]}");

        using var doc = JsonDocument.Parse(json);

        var items = doc.RootElement
            .GetProperty("data")
            .GetProperty("scenarios");

        items.GetArrayLength().Should().Be(count);

        var timestamps = items.EnumerateArray()
            .Select(s => DateTimeOffset.Parse(s.GetProperty("createdAt").GetString()!))
            .ToList();

        timestamps.Should().BeInDescendingOrder();

        p95.Should().BeLessThan(2000, "the warmed 100-scenario query should complete under two seconds for at least 95% of requests; timings were {0}", string.Join(", ", durations));
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

        var response = await _client.PostAsync("/graphql", GqlRequest(query));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("errors", out var errors).Should().BeTrue();
        errors.GetArrayLength().Should().BeGreaterThan(0);

        if (doc.RootElement.TryGetProperty("data", out var data))
            data.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
