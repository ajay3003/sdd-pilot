using BirkNext.Api.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace BirkNext.Api.Tests.Integration;

public class ScenariosMutationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ScenariosMutationTests()
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

    [Fact]
    public async Task CreateScenario_ValidInput_ReturnsScenarioId()
    {
        const string mutation = """
            mutation CreateScenario($input: CreateScenarioInput!) {
              createScenario(input: $input) {
                scenario { id }
                errors { code message field }
              }
            }
            """;

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new
        {
            input = new { title = "Integration test scenario", kind = "REQUIREMENT", projectId = "proj-001" }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var id = doc.RootElement
            .GetProperty("data")
            .GetProperty("createScenario")
            .GetProperty("scenario")
            .GetProperty("id")
            .GetString();

        id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateScenario_MissingTitle_ReturnsTitleRequiredError()
    {
        const string mutation = """
            mutation CreateScenario($input: CreateScenarioInput!) {
              createScenario(input: $input) {
                scenario { id }
                errors { code message field }
              }
            }
            """;

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new
        {
            input = new { title = "", kind = "REQUIREMENT", projectId = "proj-001" }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var firstError = doc.RootElement
            .GetProperty("data")
            .GetProperty("createScenario")
            .GetProperty("errors")[0];

        firstError.GetProperty("code").GetString().Should().Be("TITLE_REQUIRED");
    }

    [Fact]
    public async Task CreateScenario_ValidInput_ReturnsNonEmptyCorrelationId()
    {
        const string mutation = """
            mutation CreateScenario($input: CreateScenarioInput!) {
              createScenario(input: $input) {
                scenario { id }
                errors { code message field }
                correlationId
              }
            }
            """;

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new
        {
            input = new { title = "CorrelationId test scenario", kind = "REQUIREMENT", projectId = "proj-001" }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var correlationId = doc.RootElement
            .GetProperty("data")
            .GetProperty("createScenario")
            .GetProperty("correlationId")
            .GetString();

        correlationId.Should().NotBeNullOrEmpty();
    }

    // ── T039 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateScenario_EmptyTitle_ReturnsFullTitleRequiredError()
    {
        const string mutation = """
            mutation CreateScenario($input: CreateScenarioInput!) {
              createScenario(input: $input) {
                scenario { id }
                errors { code message field }
              }
            }
            """;

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new
        {
            input = new { title = "", kind = "REQUIREMENT", projectId = "proj-001" }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var firstError = doc.RootElement
            .GetProperty("data")
            .GetProperty("createScenario")
            .GetProperty("errors")[0];

        firstError.GetProperty("code").GetString().Should().Be("TITLE_REQUIRED");
        firstError.GetProperty("field").GetString().Should().Be("title");
        firstError.GetProperty("message").GetString().Should().Be("Title is required");
    }

    [Fact]
    public async Task CreateScenario_ValidationError_ReturnsNonEmptyCorrelationId()
    {
        const string mutation = """
            mutation CreateScenario($input: CreateScenarioInput!) {
              createScenario(input: $input) {
                scenario { id }
                errors { code message field }
                correlationId
              }
            }
            """;

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new
        {
            input = new { title = "", kind = "REQUIREMENT", projectId = "proj-001" }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement
            .GetProperty("data")
            .GetProperty("createScenario");

        payload.GetProperty("errors")[0].GetProperty("code").GetString().Should().Be("TITLE_REQUIRED");
        payload.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }
}
