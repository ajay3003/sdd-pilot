using System.Net;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>GAP-CDT-9 / GAP-CDT-10: API Quality Review uses a stored contract version from the contract store (the same evidence contract tests
/// use) instead of fetching a URL; the stored identity is the contract source shown; an unavailable stored version is reported, never replaced.</summary>
public sealed partial class ApiReviewEngineTests
{
    private sealed class Stored(params StoredContract[] contracts) : IStoredContractSource
    {
        public Task<StoredContract?> GetAsync(string contractRef, CancellationToken ct) => Task.FromResult(contracts.FirstOrDefault(c => $"{c.Key}@v{c.Version}" == contractRef));
    }

    private const string StoredOpenApi = """
        { "openapi": "3.0.3", "info": { "title": "Children", "version": "2" }, "paths": { "/api/children": { "get": { "operationId": "listChildren",
          "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Page" } } } } } } } },
          "components": { "schemas": { "Page": { "type": "object", "required": ["items", "total"], "properties": { "items": { "type": "array" }, "total": { "type": "integer" } } } } } }
        """;

    private static ApiReviewEngine EngineWith(Fixture fixture, IStoredContractSource stored) =>
        new(new HttpClient(fixture), new FakeGateway(false), new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance), new GraphQlExtractor(NullLogger<GraphQlExtractor>.Instance),
            NullLogger<ApiReviewEngine>.Instance, null, null, null, stored);

    [Fact]
    public async Task A_stored_contract_version_is_used_instead_of_the_URL_and_shown_as_the_contract_source()
    {
        var fixture = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath == "/api/children" && req.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, """{"items":[]}""") : Json(HttpStatusCode.NotFound, "{}") };
        var target = Rest(contract: "https://api-dev.example.test/swagger.json") with { ContractRef = "children@v2" };
        var report = await EngineWith(fixture, new Stored(new StoredContract("children", 2, "Rest", StoredOpenApi, StoredOpenApi))).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, target));

        var result = Assert.Single(report.Targets);
        Assert.Equal("Stored contract children@v2", result.Contract!.Source);
        Assert.True(result.Contract.Available);
        Assert.DoesNotContain(fixture.Requests, r => r.RequestUri!.AbsolutePath == "/swagger.json");
        // The stored schema is the one validated against: the live response lacks the required total.
        Assert.Contains(report.Findings, f => f.RuleId == "contract-missing-required" && f.Title.Contains("$.total", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing@v1", "was not found", ApiReviewCheckResult.Fail)]
    [InlineData("schema@v1", "is a GraphQL schema", ApiReviewCheckResult.Fail)]
    [InlineData("from-source@v1", "without the document text", ApiReviewCheckResult.NotTested)]
    public async Task An_unavailable_stored_version_is_reported_never_silently_replaced(string reference, string note, ApiReviewCheckResult status)
    {
        var fixture = new Fixture { Respond = (_, _) => Json(HttpStatusCode.OK, """{"items":[]}""") };
        var stored = new Stored(new StoredContract("schema", 1, "GraphQl", "type Query { a: Int }", null), new StoredContract("from-source", 1, "Rest", "", null));
        var report = await EngineWith(fixture, stored).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest(contract: "https://api-dev.example.test/swagger.json") with { ContractRef = reference }));
        var contract = Assert.Single(report.Targets).Contract!;
        Assert.Equal(status, contract.Status);
        Assert.Contains(note, contract.Note);
        Assert.DoesNotContain(fixture.Requests, r => r.RequestUri!.AbsolutePath == "/swagger.json");
    }
}
