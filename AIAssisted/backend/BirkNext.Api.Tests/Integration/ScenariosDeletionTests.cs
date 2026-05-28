using BirkNext.Api.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace BirkNext.Api.Tests.Integration;

public class ScenariosDeletionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ScenariosDeletionTests()
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

    private async Task<string> CreateScenarioAsync(string title = "Scenario to delete")
    {
        const string mutation = """
            mutation CreateScenario($input: CreateScenarioInput!) {
              createScenario(input: $input) {
                scenario { id }
              }
            }
            """;

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new
        {
            input = new { title, kind = "REQUIREMENT", projectId = "proj-delete-tests" }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("createScenario")
            .GetProperty("scenario")
            .GetProperty("id")
            .GetString()!;
    }

    [Fact]
    public async Task DeleteScenario_ExistingId_ReturnsSuccessAndDeletedId()
    {
        var id = await CreateScenarioAsync("Delete success test");

        const string mutation = """
            mutation DeleteScenario($id: ID!) {
              deleteScenario(id: $id) {
                deletedId
                success
                errors { code message }
                correlationId
              }
            }
            """;

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new { id }));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement
            .GetProperty("data")
            .GetProperty("deleteScenario");

        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("deletedId").GetString().Should().Be(id);
        payload.GetProperty("errors").GetArrayLength().Should().Be(0);
        payload.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DeleteScenario_ExistingId_RemovesScenarioFromDatabase()
    {
        var id = await CreateScenarioAsync("Row removal test");

        const string mutation = """
            mutation DeleteScenario($id: ID!) {
              deleteScenario(id: $id) { success }
            }
            """;

        await _client.PostAsync("/graphql", GqlRequest(mutation, new { id }));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Scenarios.Any(s => s.Id == Guid.Parse(id)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteScenario_NonExistentId_ReturnsNotFoundError()
    {
        const string mutation = """
            mutation DeleteScenario($id: ID!) {
              deleteScenario(id: $id) {
                deletedId
                success
                errors { code message }
              }
            }
            """;

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new { id = "non-existent-id" }));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement
            .GetProperty("data")
            .GetProperty("deleteScenario");

        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("deletedId").ValueKind.Should().Be(JsonValueKind.Null);
        payload.GetProperty("errors")[0].GetProperty("code").GetString().Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task DeleteScenario_AlreadyDeletedId_ReturnsNotFoundError()
    {
        var id = await CreateScenarioAsync("Double delete test");

        const string mutation = """
            mutation DeleteScenario($id: ID!) {
              deleteScenario(id: $id) {
                success
                errors { code }
              }
            }
            """;

        await _client.PostAsync("/graphql", GqlRequest(mutation, new { id }));

        var response = await _client.PostAsync("/graphql", GqlRequest(mutation, new { id }));
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement
            .GetProperty("data")
            .GetProperty("deleteScenario");

        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errors")[0].GetProperty("code").GetString().Should().Be("NOT_FOUND");
    }
}
