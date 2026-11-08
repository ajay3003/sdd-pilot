using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ContractAnalysis;

/// <summary>
/// Regression tests for the contract import audit (2026-11-08): CI-DEF-1 (<c>$ref</c> request/response schemas were lost, so AQR contract
/// validation silently skipped or reported a false "not documented") and CI-DEF-2 (operations without <c>operationId</c> were all called
/// by their HTTP verb). Synthetic documents only.
/// </summary>
public sealed class OpenApiExtractorDefectTests
{
    private static NormalizedContract Extract(string json)
    {
        var result = new OpenApiExtractor(NullLogger<OpenApiExtractor>.Instance).Extract(json);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Contract!;
    }

    private const string RefDocument = """
        {
          "openapi": "3.0.3", "info": { "title": "Synthetic children", "version": "1" },
          "paths": {
            "/children/{id}": {
              "get": {
                "operationId": "getChild",
                "responses": {
                  "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } },
                  "404": { "content": { "application/json": { "schema": { "type": "object", "properties": { "title": { "type": "string" } } } } } }
                }
              },
              "put": {
                "operationId": "putChild",
                "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } },
                "responses": { "204": { "description": "ok" } }
              }
            },
            "/children": {
              "get": { "responses": { "200": { "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Child" } } } } } } }
            }
          },
          "components": { "schemas": {
            "Child": { "type": "object", "required": ["id", "name"], "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "age": { "type": "integer" } } }
          } }
        }
        """;

    [Fact]
    public void CI_DEF_1_ref_response_and_request_schemas_are_resolved()
    {
        var get = Extract(RefDocument).Operations.Single(o => o.Id == "getChild");
        get.ResponseSchemas.Should().ContainKey("200");
        get.ResponseSchemas["200"].Required.Should().BeEquivalentTo(["id", "name"]);
        get.ResponseSchemas["200"].Properties.Select(p => p.Name).Should().BeEquivalentTo(["id", "name", "age"]);

        var put = Extract(RefDocument).Operations.Single(o => o.Id == "putChild");
        put.RequestSchema.Should().NotBeNull();
        put.RequestSchema!.Required.Should().BeEquivalentTo(["id", "name"]);
    }

    [Fact]
    public void CI_DEF_1_a_ref_success_response_is_validated_not_reported_as_undocumented()
    {
        var get = Extract(RefDocument).Operations.Single(o => o.Id == "getChild");
        // The live body lacks the required "name" and returns "age" as a string.
        JsonShapeEntry[] shape = [new("$", "object", false), new("$.id", "string", false), new("$.age", "string", false)];

        var findings = ContractValidation.ValidateAgainstSchema(get, "200", shape, "t1", "GET /children/1");

        findings.Select(f => f.RuleId).Should().NotContain("contract-undocumented-status");
        findings.Select(f => f.RuleId).Should().Contain("contract-missing-required");
        findings.Select(f => f.RuleId).Should().Contain("contract-type-mismatch");
    }

    [Fact]
    public void CI_DEF_2_operations_without_operationId_get_distinct_method_and_path_ids()
    {
        var contract = Extract("""
            { "openapi": "3.0.3", "info": { "title": "t", "version": "1" }, "paths": {
              "/a": { "get": { "responses": { "200": { "description": "ok" } } }, "post": { "responses": { "201": { "description": "ok" } } } },
              "/b/{id}": { "get": { "responses": { "200": { "description": "ok" } } } },
              "/c": { "get": { "operationId": "dup", "responses": { "200": { "description": "ok" } } } },
              "/d": { "get": { "operationId": "dup", "responses": { "200": { "description": "ok" } } } }
            } }
            """);

        contract.Operations.Select(o => o.Key).Should().BeEquivalentTo(["GET /a", "POST /a", "GET /b/{id}", "GET /c", "GET /d"]);
        contract.Operations.Select(o => o.Id).Should().OnlyHaveUniqueItems();
        contract.Operations.Single(o => o.Key == "GET /a").Id.Should().Be("GET /a");
        // A duplicated operationId cannot identify an operation; both fall back to their method + path key.
        contract.Operations.Single(o => o.Key == "GET /c").Id.Should().Be("GET /c");
        contract.Operations.Single(o => o.Key == "GET /d").Id.Should().Be("GET /d");
    }
}
