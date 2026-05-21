using BirkNext.Api.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace BirkNext.Api.Tests.Integration;

public class ScenariosBatchMutationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ScenariosBatchMutationTests()
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

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }

    private const string BatchMutation = """
        mutation CreateScenarios($input: CreateScenariosInput!) {
          createScenarios(input: $input) {
            successCount
            failureCount
            correlationId
            results {
              __typename
              ... on CreateScenarioSuccess {
                scenario { id title kind }
              }
              ... on CreateScenarioError {
                code
                message
                field
              }
            }
          }
        }
        """;

    [Fact]
    public async Task CreateScenarios_AllValid_ReturnsAllSuccesses()
    {
        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = "Scenario A", kind = "REQUIREMENT", projectId = "proj-batch-01" },
                    new { title = "Scenario B", kind = "TEST", projectId = "proj-batch-01" },
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement.GetProperty("data").GetProperty("createScenarios");
        payload.GetProperty("successCount").GetInt32().Should().Be(2);
        payload.GetProperty("failureCount").GetInt32().Should().Be(0);
        payload.GetProperty("results").GetArrayLength().Should().Be(2);
        payload.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateScenarios_OneEmptyTitle_ReturnsErrorPlusOtherSucceeds()
    {
        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = "", kind = "REQUIREMENT", projectId = "proj-batch-02" },
                    new { title = "Valid Scenario", kind = "REQUIREMENT", projectId = "proj-batch-02" },
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement.GetProperty("data").GetProperty("createScenarios");
        payload.GetProperty("successCount").GetInt32().Should().Be(1);
        payload.GetProperty("failureCount").GetInt32().Should().Be(1);

        var results = payload.GetProperty("results");
        results[0].GetProperty("__typename").GetString().Should().Be("CreateScenarioError");
        results[0].GetProperty("code").GetString().Should().Be("TITLE_REQUIRED");
        results[1].GetProperty("__typename").GetString().Should().Be("CreateScenarioSuccess");
    }

    [Fact]
    public async Task CreateScenarios_TitleTooLong_ReturnsTitleTooLongError()
    {
        var longTitle = new string('x', 501);

        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = longTitle, kind = "REQUIREMENT", projectId = "proj-batch-03" }
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement.GetProperty("data").GetProperty("createScenarios");
        payload.GetProperty("failureCount").GetInt32().Should().Be(1);
        payload.GetProperty("successCount").GetInt32().Should().Be(0);

        payload.GetProperty("results")[0]
            .GetProperty("code").GetString().Should().Be("TITLE_TOO_LONG");
    }

    [Fact]
    public async Task CreateScenarios_EmptyItemsArray_ReturnsTopLevelError()
    {
        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new { items = Array.Empty<object>() }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("errors", out var errors).Should().BeTrue();
        errors.GetArrayLength().Should().BeGreaterThan(0);
        errors[0].GetProperty("extensions").GetProperty("code").GetString().Should().Be("ITEMS_EMPTY");
    }

    [Fact]
    public async Task CreateScenarios_NoExtractionMetadata_Succeeds()
    {
        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = "No metadata scenario", kind = "REQUIREMENT", projectId = "proj-batch-05" }
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement.GetProperty("data").GetProperty("createScenarios");
        payload.GetProperty("successCount").GetInt32().Should().Be(1);
        payload.GetProperty("failureCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task CreateScenarios_WithExtractionMetadata_LogsContainCandidateReviewSavedWithTotalExtracted()
    {
        var logFactory = new CapturingLoggerFactory();

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseNpgsql(_postgres.GetConnectionString()));
                });
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<ILoggerFactory>(logFactory));
            });

        var client = factory.CreateClient();

        var response = await client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = "Metadata log test", kind = "REQUIREMENT", projectId = "proj-batch-06" }
                },
                extractionMetadata = new
                {
                    totalExtracted = 42,
                    selectedCount = 1,
                    extractionDurationMs = 150,
                    sessionId = "session-abc"
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("data").GetProperty("createScenarios")
            .GetProperty("successCount").GetInt32().Should().Be(1);

        logFactory.Messages.Should().Contain(m =>
            m.Contains("CandidateReviewSaved") &&
            m.Contains("totalExtracted=42") &&
            m.Contains("selectedCount=1") &&
            m.Contains("scenariosCreated=1") &&
            m.Contains("failedCount=0"));
    }

    private const string ScenariosQuery = """
        query GetScenarios($projectId: String!) {
          scenarios(projectId: $projectId) {
            id
            title
          }
        }
        """;

    [Fact]
    public async Task CreateScenarios_TitleTooLong_ErrorIncludesTitleField()
    {
        var longTitle = new string('x', 501);

        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = longTitle, kind = "REQUIREMENT", projectId = "proj-t089a" }
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var result = doc.RootElement
            .GetProperty("data").GetProperty("createScenarios")
            .GetProperty("results")[0];

        result.GetProperty("__typename").GetString().Should().Be("CreateScenarioError");
        result.GetProperty("code").GetString().Should().Be("TITLE_TOO_LONG");
        result.GetProperty("field").GetString().Should().Be("title");
    }

    [Fact]
    public async Task CreateScenarios_EmptyProjectId_ReturnsProjectIdRequiredError()
    {
        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = "Valid title", kind = "REQUIREMENT", projectId = "" }
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var result = doc.RootElement
            .GetProperty("data").GetProperty("createScenarios")
            .GetProperty("results")[0];

        result.GetProperty("__typename").GetString().Should().Be("CreateScenarioError");
        result.GetProperty("code").GetString().Should().Be("PROJECT_ID_REQUIRED");
        result.GetProperty("field").GetString().Should().Be("projectId");
    }

    [Fact]
    public async Task CreateScenarios_AllItemsInvalid_NoRowsInsertedInDb()
    {
        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = "", kind = "REQUIREMENT", projectId = "proj-t089c" },
                    new { title = new string('y', 501), kind = "TEST", projectId = "proj-t089c" },
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement.GetProperty("data").GetProperty("createScenarios");
        payload.GetProperty("successCount").GetInt32().Should().Be(0);
        payload.GetProperty("failureCount").GetInt32().Should().Be(2);

        // Verify no rows were persisted for this project
        var queryResponse = await _client.PostAsync("/graphql", GqlRequest(ScenariosQuery, new
        {
            projectId = "proj-t089c"
        }));
        var queryJson = await queryResponse.Content.ReadAsStringAsync();
        using var queryDoc = JsonDocument.Parse(queryJson);
        queryDoc.RootElement.GetProperty("data").GetProperty("scenarios").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CreateScenarios_MixedBatch_ValidItemsPersistedInvalidRejected()
    {
        var response = await _client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = "Valid scenario D1", kind = "REQUIREMENT", projectId = "proj-t089d" },
                    new { title = "", kind = "TEST", projectId = "proj-t089d" },
                    new { title = "Valid scenario D2", kind = "TEST", projectId = "proj-t089d" },
                }
            }
        }));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var payload = doc.RootElement.GetProperty("data").GetProperty("createScenarios");
        payload.GetProperty("successCount").GetInt32().Should().Be(2);
        payload.GetProperty("failureCount").GetInt32().Should().Be(1);

        var results = payload.GetProperty("results");
        results[0].GetProperty("__typename").GetString().Should().Be("CreateScenarioSuccess");
        results[1].GetProperty("__typename").GetString().Should().Be("CreateScenarioError");
        results[2].GetProperty("__typename").GetString().Should().Be("CreateScenarioSuccess");

        // Verify valid items were persisted
        var queryResponse = await _client.PostAsync("/graphql", GqlRequest(ScenariosQuery, new
        {
            projectId = "proj-t089d"
        }));
        var queryJson = await queryResponse.Content.ReadAsStringAsync();
        using var queryDoc = JsonDocument.Parse(queryJson);
        queryDoc.RootElement.GetProperty("data").GetProperty("scenarios").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task CreateScenarios_WithExtractionMetadata_CandidateReviewSaved_HasNonNegativeDurationMs()
    {
        var logFactory = new CapturingLoggerFactory();

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseNpgsql(_postgres.GetConnectionString()));
                });
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<ILoggerFactory>(logFactory));
            });

        var client = factory.CreateClient();

        await client.PostAsync("/graphql", GqlRequest(BatchMutation, new
        {
            input = new
            {
                items = new[]
                {
                    new { title = "Duration test scenario", kind = "TEST", projectId = "proj-batch-07" }
                },
                extractionMetadata = new
                {
                    totalExtracted = 5,
                    selectedCount = 1,
                    extractionDurationMs = 88,
                    sessionId = "session-dur-test"
                }
            }
        }));

        var match = logFactory.Messages
            .Select(m => System.Text.RegularExpressions.Regex.Match(m, @"durationMs=(\d+)"))
            .FirstOrDefault(m => m.Success);

        match.Should().NotBeNull("CandidateReviewSaved must include a durationMs field");
        var durationMs = long.Parse(match!.Groups[1].Value);
        // With a real PostgreSQL container, the operation takes measurable time.
        // We assert >= 0 (field is a valid measurement); in practice it is always > 0.
        durationMs.Should().BeGreaterThanOrEqualTo(0);
    }
}
