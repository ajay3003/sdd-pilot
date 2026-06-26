using BirkNext.Api.Data;
using FluentAssertions;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Snapshooter.Xunit;

namespace BirkNext.Api.Tests.Contract;

public class ScenariosSchemaTests
{
    [Fact]
    public async Task Schema_MatchesSnapshot()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseInMemoryDatabase("schema-test"));
                }));

        var resolver = factory.Services.GetRequiredService<IRequestExecutorResolver>();
        var executor = await resolver.GetRequestExecutorAsync();

        executor.Schema.ToString().MatchSnapshot();
    }

    [Fact]
    public async Task ScenariosQuery_ShapeMatchesSnapshot()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseInMemoryDatabase("schema-test-scenarios-query"));
                }));

        var resolver = factory.Services.GetRequiredService<IRequestExecutorResolver>();
        var executor = await resolver.GetRequestExecutorAsync();

        executor.Schema.ToString()
            .Split('\n')
            .First(l => l.TrimStart().StartsWith("scenarios("))
            .Trim()
            .MatchSnapshot();
    }

    // ── T090 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractionMetadataInput_HasExactlyFourFieldsWithCorrectTypes()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseInMemoryDatabase("schema-test-t090"));
                }));

        var resolver = factory.Services.GetRequiredService<IRequestExecutorResolver>();
        var executor = await resolver.GetRequestExecutorAsync();
        var schema = executor.Schema.ToString();

        // Extract the ExtractionMetadataInput block
        var lines = schema.Split('\n');
        var startIndex = Array.FindIndex(lines, l => l.TrimStart().StartsWith("input ExtractionMetadataInput"));
        startIndex.Should().BeGreaterThanOrEqualTo(0, "ExtractionMetadataInput must be defined in the schema");

        var blockLines = new List<string>();
        for (var i = startIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "}") break;
            if (trimmed.Length > 0)
                blockLines.Add(trimmed);
        }

        // Exactly 4 fields — no additions permitted (contract lock)
        blockLines.Should().HaveCount(4, "ExtractionMetadataInput must have exactly 4 fields");

        blockLines.Should().Contain(l => l.StartsWith("totalExtracted") && l.Contains("Int!"));
        blockLines.Should().Contain(l => l.StartsWith("selectedCount") && l.Contains("Int!"));
        blockLines.Should().Contain(l => l.StartsWith("extractionDurationMs") && l.Contains("Int!"));
        blockLines.Should().Contain(l => l.StartsWith("sessionId") && l.Contains("String!"));
    }

    // ── T094 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_US2Types_FieldsMatchContractsSchemaGraphql()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseInMemoryDatabase("schema-test-t094"));
                }));

        var resolver = factory.Services.GetRequiredService<IRequestExecutorResolver>();
        var executor = await resolver.GetRequestExecutorAsync();
        var schema = executor.Schema.ToString();

        // CreateScenariosPayload fields
        var payloadBlock = ExtractTypeBlock(schema, "type CreateScenariosPayload");
        payloadBlock.Should().Contain(f => f.StartsWith("results") && f.Contains("CreateScenarioResult"));
        payloadBlock.Should().Contain(f => f.StartsWith("successCount") && f.Contains("Int!"));
        payloadBlock.Should().Contain(f => f.StartsWith("failureCount") && f.Contains("Int!"));
        payloadBlock.Should().Contain(f => f.StartsWith("correlationId") && f.Contains("String!"));

        // CreateScenarioSuccess fields
        var successBlock = ExtractTypeBlock(schema, "type CreateScenarioSuccess");
        successBlock.Should().Contain(f => f.StartsWith("scenario") && f.Contains("Scenario!"));

        // CreateScenarioError fields
        var errorBlock = ExtractTypeBlock(schema, "type CreateScenarioError");
        errorBlock.Should().Contain(f => f.StartsWith("code") && f.Contains("String!"));
        errorBlock.Should().Contain(f => f.StartsWith("message") && f.Contains("String!"));
        errorBlock.Should().Contain(f => f.StartsWith("field"));

        // CreateScenarioResult union
        schema.Should().Contain("union CreateScenarioResult");
        schema.Should().MatchRegex(@"union CreateScenarioResult\s*=\s*CreateScenarioSuccess\s*\|\s*CreateScenarioError|union CreateScenarioResult\s*=\s*CreateScenarioError\s*\|\s*CreateScenarioSuccess");
    }

    [Fact]
    public async Task Schema_US1Types_UnchangedFromContracts()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseInMemoryDatabase("schema-test-t094-us1"));
                }));

        var resolver = factory.Services.GetRequiredService<IRequestExecutorResolver>();
        var executor = await resolver.GetRequestExecutorAsync();
        var schema = executor.Schema.ToString();

        // Scenario type fields (US1 entity — must not change)
        var scenarioBlock = ExtractTypeBlock(schema, "type Scenario");
        scenarioBlock.Should().Contain(f => f.StartsWith("id") && f.Contains("ID!"));
        scenarioBlock.Should().Contain(f => f.StartsWith("title") && f.Contains("String!"));
        scenarioBlock.Should().Contain(f => f.StartsWith("description"));
        scenarioBlock.Should().Contain(f => f.StartsWith("kind") && f.Contains("ScenarioKind!"));
        scenarioBlock.Should().Contain(f => f.StartsWith("projectId") && f.Contains("String!"));
        scenarioBlock.Should().Contain(f => f.StartsWith("createdAt"));

        // CreateScenarioPayload fields (US1 payload — must not change)
        var us1PayloadBlock = ExtractTypeBlock(schema, "type CreateScenarioPayload");
        us1PayloadBlock.Should().Contain(f => f.StartsWith("scenario"));
        us1PayloadBlock.Should().Contain(f => f.StartsWith("errors"));
        us1PayloadBlock.Should().Contain(f => f.StartsWith("correlationId") && f.Contains("String!"));

        // UserError fields (US1 error type — must not change)
        var userErrorBlock = ExtractTypeBlock(schema, "type UserError");
        userErrorBlock.Should().Contain(f => f.StartsWith("field"));
        userErrorBlock.Should().Contain(f => f.StartsWith("message") && f.Contains("String!"));
        userErrorBlock.Should().Contain(f => f.StartsWith("code") && f.Contains("String!"));

        // ScenarioKind enum values
        schema.Should().Contain("REQUIREMENT");
        schema.Should().Contain("TEST");
        schema.Should().Contain("NEEDS_CLARIFICATION");
    }

    private static List<string> ExtractTypeBlock(string schema, string typeDeclaration)
    {
        var lines = schema.Split('\n');
        var startIndex = Array.FindIndex(lines, l => l.TrimStart().StartsWith(typeDeclaration));
        if (startIndex < 0)
            return [];

        var fields = new List<string>();
        for (var i = startIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "}") break;
            if (trimmed.Length > 0 && !trimmed.StartsWith("#") && !trimmed.StartsWith("\"\"\""))
                fields.Add(trimmed);
        }
        return fields;
    }

    // ── T075 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_ContainsBatchMutationAndUS1Unchanged()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseInMemoryDatabase("schema-test-t075"));
                }));

        var resolver = factory.Services.GetRequiredService<IRequestExecutorResolver>();
        var executor = await resolver.GetRequestExecutorAsync();
        var schema = executor.Schema.ToString();

        // US2 batch mutation present
        schema.Should().Contain("createScenarios(");
        schema.Should().Contain("CreateScenariosInput");
        schema.Should().Contain("ExtractionMetadataInput");
        schema.Should().Contain("CreateScenariosPayload");
        schema.Should().Contain("CreateScenarioResult");
        schema.Should().Contain("CreateScenarioSuccess");
        schema.Should().Contain("CreateScenarioError");

        // US1 types still present and unchanged
        schema.Should().Contain("createScenario(");
        schema.Should().Contain("CreateScenarioInput");
        schema.Should().Contain("CreateScenarioPayload");
        schema.Should().Contain("UserError");
        schema.Should().Contain("ScenarioKind");
    }
}
