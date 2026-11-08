using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// GAP-CDT-7 / GAP-CDT-8: contracts Source Analysis already holds are imported without re-upload (snapshot provenance, no duplicates, newer
/// snapshot = next version, impact across snapshots, structural validation of source-backed versions), plus OpenAPI reader diagnostics
/// (malformed JSON with a position, YAML anchors named, Swagger 2 bodies and responses validated).
/// </summary>
public sealed class SourceContractImportTests
{
    private sealed class Sources(params IqrSourceSnapshot[] snapshots) : IReviewSourceEvidenceProvider
    {
        public List<IqrSourceSnapshot> All { get; } = [.. snapshots];
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([.. All.OrderByDescending(s => s.AnalyzedAt)]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(All.FirstOrDefault(s => s.Id == snapshotId));
    }

    private static IqrSourceSnapshot Snapshot(string file, string content, DateTimeOffset at, string commit)
    {
        var contract = ContractAnalyzer.ReadOpenApiDocument(file, content)! with { Id = $"openapi:{file}", File = file };
        return new IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), AnalyzedAt = at, Commit = commit, Archive = new("children-source.zip", "abc123", 12),
            EvidenceDomains = new SourceEvidenceDomainsSnapshot { Contracts = new() { Domain = SourceEvidenceDomain.Contracts, Contracts = [contract] } },
        };
    }

    private static (AppDbContext Db, ContractRegistryService Registry) Registry(IReviewSourceEvidenceProvider sources)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"src-{Guid.NewGuid():N}").Options);
        return (db, new ContractRegistryService(db, new ContractSourceFetcher(new HttpClient()), null, sources));
    }

    [Fact]
    public async Task A_source_contract_is_imported_with_provenance_without_duplicates_and_a_newer_snapshot_is_the_next_version()
    {
        var first = Snapshot("api/children.yaml", ContractTestingTests.V1Yaml, DateTimeOffset.UtcNow.AddHours(-2), "c0ffee1");
        var sources = new Sources(first);
        var (db, registry) = Registry(sources);
        var candidates = await registry.SourceCandidatesAsync(default);
        candidates.Should().ContainSingle().Which.Should().Match<SourceContractCandidate>(c => c.Type == "OpenApi" && c.OperationCount == 4 && c.ImportedAs == null && c.Archive == "children-source.zip");

        var imported = await registry.ImportFromSourceAsync(new(first.Id, "openapi:api/children.yaml", null, null), default);
        imported.Imported.Should().BeTrue(imported.Message);
        var detail = (await registry.GetAsync(imported.Key!, null, default))!;
        var info = detail.Versions.Single();
        info.Should().Match<ApiContractVersionInfo>(v => v.Source == ContractVersionSource.SourceSnapshot && v.SourceSnapshotId == first.Id && v.SourcePath == "openapi:api/children.yaml"
            && !v.HasDocument && v.SourceLabel.Contains("children-source.zip") && v.SourceLabel.Contains("c0ffee1") && v.Sha256.Length == 64);
        detail.Document.Operations.Should().HaveCount(4);
        (await registry.ImportFromSourceAsync(new(first.Id, "openapi:api/children.yaml", null, null), default)).Unchanged.Should().BeTrue("the same snapshot evidence is never stored twice");
        (await registry.SourceCandidatesAsync(default)).Single().ImportedAs.Should().Be($"{imported.Key}@v1");

        var second = Snapshot("api/children.json", ContractTestingTests.V2Json, DateTimeOffset.UtcNow, "c0ffee2");
        sources.All.Add(second);
        var next = await registry.ImportFromSourceAsync(new(second.Id, "openapi:api/children.json", imported.Key, null), default);
        next.Version.Should().Be(2, next.Message);
        var changes = ContractRegistryService.Changes((await registry.DocumentAsync(imported.Key!, 1, default))!, (await registry.DocumentAsync(imported.Key!, 2, default))!);
        changes.Select(c => c.ContractChange).Should().Contain([ContractChangeClass.OperationRemoved, ContractChangeClass.ParameterAddedRequired]);
        db.ApiContractVersions.Count().Should().Be(2);
        (await registry.ImportFromSourceAsync(new(second.Id, "openapi:api/children.json", null, "Other"), default)).Unchanged.Should().BeTrue();
    }

    [Fact]
    public async Task Source_backed_versions_get_structural_validation_and_say_what_is_not_evaluated()
    {
        var snapshot = Snapshot("api/children.yaml", ContractTestingTests.V1Yaml, DateTimeOffset.UtcNow, "c1");
        var (_, registry) = Registry(new Sources(snapshot));
        var key = (await registry.ImportFromSourceAsync(new(snapshot.Id, "openapi:api/children.yaml", null, null), default)).Key!;
        var validator = new ContractCallValidator(registry);
        var call = new ExecutionCallDefinition { Method = "GET", Path = "/children/7", OperationRef = $"{key}@v1#GET /children/{{id}}" };
        var bad = await validator.ResponseAsync(call, new CapturedResponse { StatusCode = 200, Headers = [new("Content-Type", "application/json")] }, """{"id":"seven"}"""u8.ToArray(), default);
        bad!.State.Should().Be(ContractValidationState.Invalid);
        bad.Findings.Select(f => (f.InstancePath, f.Keyword)).Should().BeEquivalentTo([("$.id", "type"), ("$.name", "required")]);
        bad.Unsupported.Should().ContainSingle(u => u.Contains("source-backed version"));
        var good = await validator.ResponseAsync(call, new CapturedResponse { StatusCode = 200 }, """{"id":7,"name":"Testbarn"}"""u8.ToArray(), default);
        good!.State.Should().Be(ContractValidationState.Valid);
        good.Unsupported.Should().NotBeEmpty("a structural check is never presented as a full schema pass");
        (await validator.ResponseAsync(call, new CapturedResponse { StatusCode = 500 }, "{}"u8.ToArray(), default))!.Findings.Should().ContainSingle(f => f.Message.Contains("HTTP 500 is not a documented response"));
    }

    [Fact]
    public async Task Protocols_are_kept_apart_and_unknown_sources_are_refused()
    {
        var snapshot = Snapshot("api/children.yaml", ContractTestingTests.V1Yaml, DateTimeOffset.UtcNow, "c1");
        var (_, registry) = Registry(new Sources(snapshot));
        var gql = await registry.ImportAsync(new() { Name = "Gql", FileName = "g.graphql", Content = ContractTestingTests.GraphQlV1 }, default);
        (await registry.ImportFromSourceAsync(new(snapshot.Id, "openapi:api/children.yaml", gql.Key, null), default)).Problems.Should().ContainSingle(p => p.Contains("GraphQl contract"));
        (await registry.ImportFromSourceAsync(new(Guid.NewGuid(), "x", null, null), default)).Problems.Should().ContainSingle(p => p.Contains("does not exist"));
        (await registry.ImportFromSourceAsync(new(snapshot.Id, "openapi:missing.yaml", null, null), default)).Problems.Should().ContainSingle(p => p.Contains("no such contract"));
    }

    [Fact]
    public async Task Malformed_documents_get_positioned_diagnostics_and_YAML_limits_are_named()
    {
        var (_, registry) = Registry(new Sources());
        (await registry.ImportAsync(new() { FileName = "bad.json", Content = "{ \"openapi\": \"3.0.3\",\n  \"paths\": { \"/a\": { } \n" }, default)).Message.Should().MatchRegex(@"not valid at line \d+, position \d+");
        var yaml = ContractTestingTests.V1Yaml.Replace("info:\n  title", "x-shared: &shared\n  a: 1\ninfo:\n  title", StringComparison.Ordinal);
        var result = await registry.ImportAsync(new() { FileName = "anchors.yaml", Content = yaml }, default);
        result.Imported.Should().BeTrue(result.Message);
        (await registry.GetAsync(result.Key!, null, default))!.Versions.Single().Limitations.Should().Contain(l => l.Contains("anchors/aliases"));
    }

    [Fact]
    public void Swagger_2_body_and_response_schemas_are_validated_from_the_document()
    {
        var v = new OpenApiSchemaValidator(JsonNode.Parse("""
            { "swagger": "2.0", "basePath": "/api", "paths": { "/children": { "post": {
                "parameters": [ { "name": "body", "in": "body", "required": true, "schema": { "$ref": "#/definitions/NewChild" } } ],
                "responses": { "201": { "description": "ok", "schema": { "$ref": "#/definitions/NewChild" } } } } } },
              "definitions": { "NewChild": { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" }, "age": { "type": "integer" } } } } }
            """)!);
        var request = v.RequestSchema("POST", "/children", "application/json");
        request.Pointer.Should().Be("#/paths/~1children/post/parameters/0/schema");
        v.Validate(JsonNode.Parse("""{"age":"x"}"""), request.Schema!, request.Pointer!, ValidationDirection.Request, new HashSet<string>()).Select(f => f.Keyword).Should().BeEquivalentTo(["required", "type"]);
        v.ResponseSchema("POST", "/children", 201, null).Schema.Should().NotBeNull();
        var swagger = ContractAnalyzer.ReadOpenApiDocument("s.json", """{ "swagger": "2.0", "host": "api.example.test", "basePath": "/api", "schemes": ["https"], "paths": {} }""")!;
        swagger.Servers.Should().Equal("https://api.example.test/api");
    }
}
