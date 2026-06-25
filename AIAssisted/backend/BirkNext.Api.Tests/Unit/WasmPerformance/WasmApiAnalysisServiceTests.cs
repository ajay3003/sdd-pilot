using BirkNext.Api.Services.WasmPerformance;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit.WasmPerformance;

public class WasmApiAnalysisServiceTests
{
    // ── IsGraphQLResponse ─────────────────────────────────────────────────────

    [Fact]
    public void IsGraphQLResponse_WithDataKey_ReturnsTrue()
    {
        const string body = """{"data":{"__typename":"Query"}}""";
        WasmApiAnalysisService.IsGraphQLResponse(body).Should().BeTrue();
    }

    [Fact]
    public void IsGraphQLResponse_WithErrorsKey_ReturnsTrue()
    {
        const string body = """{"errors":[{"message":"Unauthorized"}]}""";
        WasmApiAnalysisService.IsGraphQLResponse(body).Should().BeTrue();
    }

    [Fact]
    public void IsGraphQLResponse_WithBothDataAndErrors_ReturnsTrue()
    {
        const string body = """{"data":{"user":null},"errors":[{"message":"Not found"}]}""";
        WasmApiAnalysisService.IsGraphQLResponse(body).Should().BeTrue();
    }

    [Fact]
    public void IsGraphQLResponse_PlainJson_ReturnsFalse()
    {
        const string body = """{"message":"Hello","status":200}""";
        WasmApiAnalysisService.IsGraphQLResponse(body).Should().BeFalse();
    }

    [Fact]
    public void IsGraphQLResponse_EmptyString_ReturnsFalse()
    {
        WasmApiAnalysisService.IsGraphQLResponse("").Should().BeFalse();
    }

    [Fact]
    public void IsGraphQLResponse_InvalidJson_ReturnsFalse()
    {
        WasmApiAnalysisService.IsGraphQLResponse("not { valid json").Should().BeFalse();
    }

    [Fact]
    public void IsGraphQLResponse_HtmlResponse_ReturnsFalse()
    {
        WasmApiAnalysisService.IsGraphQLResponse("<!DOCTYPE html><html></html>").Should().BeFalse();
    }

    [Fact]
    public void IsGraphQLResponse_JsonArray_ReturnsFalse()
    {
        WasmApiAnalysisService.IsGraphQLResponse("""[{"id":1},{"id":2}]""").Should().BeFalse();
    }

    // ── HasGraphQLErrors ──────────────────────────────────────────────────────

    [Fact]
    public void HasGraphQLErrors_WithNonEmptyErrors_ReturnsTrue()
    {
        const string body = """{"errors":[{"message":"Field not found","path":["user"]}]}""";
        WasmApiAnalysisService.HasGraphQLErrors(body).Should().BeTrue();
    }

    [Fact]
    public void HasGraphQLErrors_WithEmptyErrorsArray_ReturnsFalse()
    {
        const string body = """{"data":{"user":{"id":1}},"errors":[]}""";
        WasmApiAnalysisService.HasGraphQLErrors(body).Should().BeFalse();
    }

    [Fact]
    public void HasGraphQLErrors_WithNoErrorsKey_ReturnsFalse()
    {
        const string body = """{"data":{"user":{"id":1}}}""";
        WasmApiAnalysisService.HasGraphQLErrors(body).Should().BeFalse();
    }

    [Fact]
    public void HasGraphQLErrors_EmptyBody_ReturnsFalse()
    {
        WasmApiAnalysisService.HasGraphQLErrors("").Should().BeFalse();
    }

    // ── IsIntrospectionEnabled ────────────────────────────────────────────────

    [Fact]
    public void IsIntrospectionEnabled_WithSchemaData_ReturnsTrue()
    {
        const string body = """
            {
              "data": {
                "__schema": {
                  "queryType": { "name": "Query" },
                  "mutationType": null,
                  "subscriptionType": null
                }
              }
            }
            """;
        WasmApiAnalysisService.IsIntrospectionEnabled(body).Should().BeTrue();
    }

    [Fact]
    public void IsIntrospectionEnabled_WithErrorsOnly_ReturnsFalse()
    {
        const string body = """{"errors":[{"message":"Introspection is disabled"}]}""";
        WasmApiAnalysisService.IsIntrospectionEnabled(body).Should().BeFalse();
    }

    [Fact]
    public void IsIntrospectionEnabled_WithDataButNoSchema_ReturnsFalse()
    {
        const string body = """{"data":{"__typename":"Query"}}""";
        WasmApiAnalysisService.IsIntrospectionEnabled(body).Should().BeFalse();
    }

    [Fact]
    public void IsIntrospectionEnabled_EmptyBody_ReturnsFalse()
    {
        WasmApiAnalysisService.IsIntrospectionEnabled("").Should().BeFalse();
    }

    // ── ParseOpenApiEndpoints ─────────────────────────────────────────────────

    [Fact]
    public void ParseOpenApiEndpoints_ValidSpec_ReturnsEndpoints()
    {
        const string json = """
            {
              "openapi": "3.0.1",
              "info": { "title": "Test API", "version": "v1" },
              "paths": {
                "/api/users": {
                  "get":  { "summary": "Get all users", "operationId": "Users_GetAll" },
                  "post": { "summary": "Create user",   "operationId": "Users_Create" }
                },
                "/api/users/{id}": {
                  "get":    { "operationId": "Users_GetById" },
                  "delete": { "operationId": "Users_Delete" }
                }
              }
            }
            """;

        var results = WasmApiAnalysisService.ParseOpenApiEndpoints(json);

        results.Should().HaveCount(4);
        results.Should().Contain(e => e.Path == "/api/users" && e.Method == "GET");
        results.Should().Contain(e => e.Path == "/api/users" && e.Method == "POST");
        results.Should().Contain(e => e.Path == "/api/users/{id}" && e.Method == "GET");
        results.Should().Contain(e => e.Path == "/api/users/{id}" && e.Method == "DELETE");
    }

    [Fact]
    public void ParseOpenApiEndpoints_WithSecurity_SetsHasAuthRequirement()
    {
        const string json = """
            {
              "paths": {
                "/api/secure": {
                  "get": {
                    "summary": "Secure endpoint",
                    "security": [{"Bearer": []}]
                  }
                },
                "/api/public": {
                  "get": {
                    "summary": "Public endpoint",
                    "security": []
                  }
                }
              }
            }
            """;

        var results = WasmApiAnalysisService.ParseOpenApiEndpoints(json);

        results.Single(e => e.Path == "/api/secure").HasAuthRequirement.Should().BeTrue();
        results.Single(e => e.Path == "/api/public").HasAuthRequirement.Should().BeFalse();
    }

    [Fact]
    public void ParseOpenApiEndpoints_SummaryFallsBackToOperationId()
    {
        const string json = """
            {
              "paths": {
                "/api/things": {
                  "get": { "operationId": "Things_GetAll" }
                }
              }
            }
            """;

        var results = WasmApiAnalysisService.ParseOpenApiEndpoints(json);

        results.Single().Summary.Should().Be("Things_GetAll");
    }

    [Fact]
    public void ParseOpenApiEndpoints_NoPaths_ReturnsEmpty()
    {
        const string json = """{"openapi":"3.0.1","info":{"title":"Empty","version":"v1"}}""";

        WasmApiAnalysisService.ParseOpenApiEndpoints(json).Should().BeEmpty();
    }

    [Fact]
    public void ParseOpenApiEndpoints_InvalidJson_ReturnsEmpty()
    {
        WasmApiAnalysisService.ParseOpenApiEndpoints("{ broken json [").Should().BeEmpty();
    }

    [Fact]
    public void ParseOpenApiEndpoints_EmptyString_ReturnsEmpty()
    {
        WasmApiAnalysisService.ParseOpenApiEndpoints("").Should().BeEmpty();
    }

    [Fact]
    public void ParseOpenApiEndpoints_HttpMethodsExtracted_AreUpperCase()
    {
        const string json = """
            {
              "paths": {
                "/api/items": {
                  "get":   { "summary": "Get" },
                  "post":  { "summary": "Create" },
                  "patch": { "summary": "Patch" }
                }
              }
            }
            """;

        var results = WasmApiAnalysisService.ParseOpenApiEndpoints(json);

        results.Should().AllSatisfy(e => e.Method.Should().Be(e.Method.ToUpperInvariant()));
    }

    // ── GenerateApiFindings ───────────────────────────────────────────────────

    [Fact]
    public void GenerateApiFindings_AllGood_ReturnsOnlyR001()
    {
        var input = new ApiProbeInput
        {
            GraphQLDetected      = true,
            IntrospectionEnabled = false,
            GraphQLCompressed    = true,
            GraphQLLatencyMs     = 50,
            GraphQLResponseBytes = 100,
            HasGraphQLErrors     = false,
            HasOpenApi           = true  // has openapi → no API-R001
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, new ApiAnalysisThresholds());

        findings.Should().BeEmpty();
    }

    [Fact]
    public void GenerateApiFindings_IntrospectionEnabled_GeneratesAPIIG001()
    {
        var input = new ApiProbeInput
        {
            GraphQLDetected      = true,
            IntrospectionEnabled = true,
            GraphQLCompressed    = true,
            HasOpenApi           = true
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, new ApiAnalysisThresholds());

        findings.Should().Contain(f => f.Id == "API-G001");
        findings.Single(f => f.Id == "API-G001").Severity.Should().Be(PerformanceSeverity.Medium);
    }

    [Fact]
    public void GenerateApiFindings_Uncompressed_GeneratesAPIIG002()
    {
        var input = new ApiProbeInput
        {
            GraphQLDetected   = true,
            GraphQLCompressed = false,
            HasOpenApi        = true
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, new ApiAnalysisThresholds());

        findings.Should().Contain(f => f.Id == "API-G002");
    }

    [Fact]
    public void GenerateApiFindings_LargeResponse_GeneratesAPIIG003()
    {
        var t = new ApiAnalysisThresholds { MaxGraphQLResponseKB = 10.0 }; // 10 KB threshold
        var input = new ApiProbeInput
        {
            GraphQLDetected      = true,
            GraphQLCompressed    = true,
            GraphQLResponseBytes = 50_000, // 50 KB — exceeds threshold
            HasOpenApi           = true
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, t);

        findings.Should().Contain(f => f.Id == "API-G003");
    }

    [Fact]
    public void GenerateApiFindings_SlowLatency_GeneratesAPIIG004()
    {
        var t = new ApiAnalysisThresholds { MaxApiLatencyMs = 100.0 };
        var input = new ApiProbeInput
        {
            GraphQLDetected   = true,
            GraphQLCompressed = true,
            GraphQLLatencyMs  = 500.0, // exceeds threshold
            HasOpenApi        = true
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, t);

        findings.Should().Contain(f => f.Id == "API-G004");
    }

    [Fact]
    public void GenerateApiFindings_GraphQLErrors_GeneratesAPIIG005()
    {
        var input = new ApiProbeInput
        {
            GraphQLDetected   = true,
            GraphQLCompressed = true,
            HasGraphQLErrors  = true,
            HasOpenApi        = true
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, new ApiAnalysisThresholds());

        findings.Should().Contain(f => f.Id == "API-G005");
        findings.Single(f => f.Id == "API-G005").Severity.Should().Be(PerformanceSeverity.Info);
    }

    [Fact]
    public void GenerateApiFindings_NoOpenApi_GeneratesAPIIR001()
    {
        var input = new ApiProbeInput
        {
            GraphQLDetected = false,
            HasOpenApi      = false
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, new ApiAnalysisThresholds());

        findings.Should().Contain(f => f.Id == "API-R001");
        findings.Single(f => f.Id == "API-R001").Severity.Should().Be(PerformanceSeverity.Info);
    }

    [Fact]
    public void GenerateApiFindings_GraphQLNotDetected_NoGraphQLFindings()
    {
        var input = new ApiProbeInput
        {
            GraphQLDetected      = false,
            IntrospectionEnabled = true, // would generate finding if detected
            GraphQLCompressed    = false,
            HasOpenApi           = true
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, new ApiAnalysisThresholds());

        findings.Should().NotContain(f => f.Id.StartsWith("API-G"));
    }

    [Fact]
    public void GenerateApiFindings_MultipleIssues_AllGenerated()
    {
        var t = new ApiAnalysisThresholds { MaxGraphQLResponseKB = 1.0, MaxApiLatencyMs = 10.0 };
        var input = new ApiProbeInput
        {
            GraphQLDetected      = true,
            IntrospectionEnabled = true,
            GraphQLCompressed    = false,
            GraphQLLatencyMs     = 3000,
            GraphQLResponseBytes = 5_000,
            HasGraphQLErrors     = true,
            HasOpenApi           = false
        };

        var findings = WasmApiAnalysisService.GenerateApiFindings(input, t);

        findings.Select(f => f.Id).Should().Contain(["API-G001", "API-G002", "API-G003", "API-G004", "API-G005", "API-R001"]);
    }

    // ── GenerateApiRecommendations ────────────────────────────────────────────

    [Fact]
    public void GenerateApiRecommendations_GraphQLDetected_AlwaysIncludesOperationNameRec()
    {
        var recs = WasmApiAnalysisService.GenerateApiRecommendations(graphqlDetected: true, []);

        recs.Should().Contain(r => r.Title.Contains("operationName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateApiRecommendations_GraphQLNotDetected_NoGraphQLRecs()
    {
        var recs = WasmApiAnalysisService.GenerateApiRecommendations(graphqlDetected: false, []);

        recs.Should().NotContain(r => r.Title.Contains("GraphQL", StringComparison.OrdinalIgnoreCase));
        recs.Should().NotContain(r => r.Title.Contains("operationName", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateApiRecommendations_IntrospectionFinding_IncludesDisableRec()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "API-G001", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.ApiCalls, Description = "", Recommendation = "" }
        };

        var recs = WasmApiAnalysisService.GenerateApiRecommendations(graphqlDetected: true, findings);

        recs.Should().Contain(r => r.Title.Contains("introspection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateApiRecommendations_CompressionFinding_IncludesCompressionRec()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "API-G002", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.ApiCalls, Description = "", Recommendation = "" }
        };

        var recs = WasmApiAnalysisService.GenerateApiRecommendations(graphqlDetected: true, findings);

        recs.Should().Contain(r => r.Title.Contains("compression", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateApiRecommendations_PrioritiesAreSequential()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "API-G001", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.ApiCalls, Description = "", Recommendation = "" },
            new PerformanceFinding { Id = "API-G002", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.ApiCalls, Description = "", Recommendation = "" }
        };

        var recs = WasmApiAnalysisService.GenerateApiRecommendations(graphqlDetected: true, findings);

        recs.Should().BeInAscendingOrder(r => r.Priority);
        recs.Select(r => r.Priority).Should().OnlyHaveUniqueItems();
    }
}
