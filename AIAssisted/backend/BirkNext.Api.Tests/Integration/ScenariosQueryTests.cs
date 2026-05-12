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

namespace BirkNext.Api.Tests.Integration;

public class ScenariosQueryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ScenariosQueryTests()
    {
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
    public async Task Scenarios_With100Records_ReturnsAllOrderedDescAndCompletesWithinTwoSeconds()
    {
        const string projectId = "proj-perf-100";
        const int count = 100;

        var baseTime = DateTimeOffset.UtcNow.AddDays(-count);
        var scenarios = Enumerable.Range(0, count)
            .Select(i => new Scenario
            {
                Title = $"Scenario {i:D3}",
                Kind = i % 2 == 0 ? ScenarioKind.Requirement : ScenarioKind.Test,
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

        var sw = Stopwatch.StartNew();
        var response = await _client.PostAsync("/graphql", GqlRequest(query, new { projectId }));
        sw.Stop();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var items = doc.RootElement
            .GetProperty("data")
            .GetProperty("scenarios");

        items.GetArrayLength().Should().Be(count);

        var timestamps = items.EnumerateArray()
            .Select(s => DateTimeOffset.Parse(s.GetProperty("createdAt").GetString()!))
            .ToList();

        timestamps.Should().BeInDescendingOrder();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
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
