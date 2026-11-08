using System.Text.Json.Nodes;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// GAP-CDT-2: contract validation. OpenAPI schema keywords (nested $ref, required, primitive types, integer vs number, arrays, enum/const, string
/// and numeric constraints, additionalProperties, allOf/anyOf/oneOf/not, 3.0 nullable vs 3.1 type arrays, readOnly/writeOnly by direction),
/// unsupported keywords reported, response lookup by status (documented, range, default, undocumented), YAML documents, and GraphQL document,
/// variable and data-shape checks. Synthetic schemas only.
/// </summary>
public sealed class ContractValidationTests
{
    private const string Doc30 = """
        { "openapi": "3.0.3", "info": { "title": "t", "version": "1" },
          "paths": { "/children/{id}": { "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer", "minimum": 1 } } ],
              "get": { "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } }, "4XX": { "description": "client" } } },
              "put": { "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } }, "responses": { "default": { "description": "any" } } } } },
          "components": { "schemas": {
            "Child": { "type": "object", "required": ["id", "name", "school"], "additionalProperties": false, "properties": {
              "id": { "type": "integer", "readOnly": true }, "name": { "type": "string", "minLength": 2, "maxLength": 20, "pattern": "^[A-Z]" },
              "age": { "type": "integer", "minimum": 0, "maximum": 18, "nullable": true }, "status": { "type": "string", "enum": ["ACTIVE", "INACTIVE"] },
              "secret": { "type": "string", "writeOnly": true }, "tags": { "type": "array", "items": { "type": "string" }, "maxItems": 2, "uniqueItems": true },
              "school": { "$ref": "#/components/schemas/School" }, "score": { "type": "number", "multipleOf": 0.5 },
              "contact": { "oneOf": [ { "$ref": "#/components/schemas/Email" }, { "$ref": "#/components/schemas/Phone" } ] },
              "kind": { "allOf": [ { "type": "string" }, { "not": { "const": "forbidden" } } ] },
              "variant": { "anyOf": [ { "type": "integer" }, { "type": "string", "format": "uuid" } ] },
              "extra": { "type": "object", "patternProperties": { "^x-": { "type": "string" } } } } },
            "School": { "type": "object", "required": ["code"], "properties": { "code": { "type": "string" }, "address": { "$ref": "#/components/schemas/Address" } } },
            "Address": { "type": "object", "required": ["city"], "properties": { "city": { "type": "string" } } },
            "Email": { "type": "object", "required": ["email"], "properties": { "email": { "type": "string", "format": "email" } } },
            "Phone": { "type": "object", "required": ["phone"], "properties": { "phone": { "type": "string" } } } } } }
        """;

    private static (OpenApiSchemaValidator Validator, OpenApiSchemaValidator.SchemaLookup Lookup) Response(string doc, int status = 200)
    {
        var v = new OpenApiSchemaValidator(OpenApiSchemaValidator.Parse(doc)!);
        return (v, v.ResponseSchema("GET", "/children/{id}", status, "application/json"));
    }

    private static List<ContractValidationFinding> ValidateResponse(string json, out HashSet<string> unsupported)
    {
        var (v, lookup) = Response(Doc30);
        unsupported = [];
        return v.Validate(JsonNode.Parse(json), lookup.Schema!, lookup.Pointer!, ValidationDirection.Response, unsupported);
    }

    [Fact]
    public void A_valid_instance_through_nested_refs_has_no_findings()
    {
        var findings = ValidateResponse("""{"id":7,"name":"Testbarn","age":null,"status":"ACTIVE","tags":["a"],"school":{"code":"S1","address":{"city":"Testby"}},"score":2.5,"contact":{"email":"a@example.test"},"kind":"normal","variant":3}""", out var unsupported);
        findings.Should().BeEmpty();
        unsupported.Should().BeEmpty("no unsupported keyword was met on this instance's path");
    }

    [Fact]
    public void Violations_are_reported_with_instance_and_schema_paths()
    {
        var findings = ValidateResponse("""{"id":7.5,"name":"testbarn-with-a-very-long-name","age":19,"status":"GONE","tags":["a","a","b"],"school":{"address":{}},"score":2.3,"contact":{"email":"a@example.test","phone":"1"},"kind":"forbidden","variant":true,"unknown":1,"secret":"x"}""", out _);
        string Kw(string path) => string.Join(",", findings.Where(f => f.InstancePath == path).Select(f => f.Keyword).Order());
        Kw("$.id").Should().Be("type", "7.5 is a number, not an integer");
        Kw("$.name").Should().Be("maxLength,pattern");
        Kw("$.age").Should().Be("maximum");
        Kw("$.status").Should().Be("enum");
        Kw("$.tags").Should().Be("maxItems,uniqueItems");
        findings.Should().Contain(f => f.InstancePath == "$.school.code" && f.Keyword == "required" && f.SchemaPath == "#/components/schemas/School/required");
        findings.Should().Contain(f => f.InstancePath == "$.school.address.city" && f.SchemaPath == "#/components/schemas/Address/required", "nested $ref is followed, never skipped");
        Kw("$.score").Should().Be("multipleOf");
        Kw("$.contact").Should().Be("oneOf");
        Kw("$.kind").Should().Be("not");
        Kw("$.variant").Should().Be("anyOf");
        Kw("$.unknown").Should().Be("additionalProperties");
        Kw("$.secret").Should().Be("writeOnly");
    }

    [Fact]
    public void Nullability_follows_OpenAPI_3_0_nullable_and_3_1_type_arrays()
    {
        ValidateResponse("""{"id":1,"name":"Ab","school":{"code":"S"},"age":null}""", out _).Should().BeEmpty("3.0 nullable: true allows null");
        ValidateResponse("""{"id":1,"name":null,"school":{"code":"S"}}""", out _).Should().ContainSingle(f => f.InstancePath == "$.name" && f.Message.Contains("nullable: true"));
        var v31 = new OpenApiSchemaValidator(JsonNode.Parse("""{ "openapi": "3.1.0", "paths": {}, "components": { "schemas": { "A": { "type": ["string", "null"], "exclusiveMinimum": 3 } } } }""")!);
        var schema = JsonNode.Parse("""{ "$ref": "#/components/schemas/A" }""")!;
        v31.Validate(null, schema, "#", ValidationDirection.Response, new HashSet<string>()).Should().BeEmpty();
        v31.Validate(JsonValue.Create(5), schema, "#", ValidationDirection.Response, new HashSet<string>()).Should().ContainSingle(f => f.Keyword == "type");
        var numeric = JsonNode.Parse("""{ "type": "number", "exclusiveMinimum": 3 }""")!;
        v31.Validate(JsonNode.Parse("3"), numeric, "#", ValidationDirection.Response, new HashSet<string>()).Should().ContainSingle(f => f.Keyword == "exclusiveMinimum", "3.1 exclusiveMinimum is a number");
    }

    [Fact]
    public void Direction_matters_readOnly_is_refused_in_requests_and_not_required_there()
    {
        var v = new OpenApiSchemaValidator(OpenApiSchemaValidator.Parse(Doc30)!);
        var request = v.RequestSchema("PUT", "/children/{id}", "application/json");
        request.Schema.Should().NotBeNull();
        v.Validate(JsonNode.Parse("""{"name":"Ab","school":{"code":"S"}}"""), request.Schema!, request.Pointer!, ValidationDirection.Request, new HashSet<string>())
            .Should().BeEmpty("the required id is readOnly, so a request need not send it");
        v.Validate(JsonNode.Parse("""{"id":1,"name":"Ab","school":{"code":"S"}}"""), request.Schema!, request.Pointer!, ValidationDirection.Request, new HashSet<string>())
            .Should().ContainSingle(f => f.Keyword == "readOnly" && f.InstancePath == "$.id");
        v.Validate(JsonNode.Parse("""{"name":"Ab","school":{"code":"S"}}"""), Response(Doc30).Lookup.Schema!, "#", ValidationDirection.Response, new HashSet<string>())
            .Should().ContainSingle(f => f.Keyword == "required" && f.InstancePath == "$.id", "a response must return the readOnly id");
    }

    [Fact]
    public void Unsupported_keywords_are_reported_never_silently_passed()
    {
        ValidateResponse("""{"id":1,"name":"Ab","school":{"code":"S"},"extra":{"x-a":"1"}}""", out var unsupported).Should().BeEmpty();
        unsupported.Should().Contain("patternProperties");
    }

    [Fact]
    public void Response_lookup_distinguishes_documented_ranged_default_and_undocumented_statuses()
    {
        Response(Doc30, 200).Lookup.StatusKey.Should().Be("200");
        Response(Doc30, 404).Lookup.Should().Match<OpenApiSchemaValidator.SchemaLookup>(l => l.StatusKey == "4XX" && l.StatusDocumented && l.Schema == null);
        Response(Doc30, 500).Lookup.Should().Match<OpenApiSchemaValidator.SchemaLookup>(l => !l.StatusDocumented && l.Note!.Contains("HTTP 500 is not a documented response"));
        var v = new OpenApiSchemaValidator(OpenApiSchemaValidator.Parse(Doc30)!);
        v.ResponseSchema("PUT", "/children/{id}", 503, null).StatusKey.Should().Be("default");
        v.ParameterSchema("GET", "/children/{id}", "id", "path")!.Value.Pointer.Should().Be("#/paths/~1children~1{id}/parameters/0/schema", "path-level parameters apply to the operation");
    }

    [Fact]
    public void YAML_documents_validate_like_JSON_with_typed_numbers()
    {
        const string yaml = """
            openapi: 3.0.3
            paths:
              /children/{id}:
                get:
                  responses:
                    "200":
                      content:
                        application/json:
                          schema:
                            type: object
                            required:
                              - age
                            properties:
                              age:
                                type: integer
                                maximum: 18
            """;
        var (v, lookup) = Response(yaml);
        lookup.Schema.Should().NotBeNull();
        v.Validate(JsonNode.Parse("""{"age":20}"""), lookup.Schema!, lookup.Pointer!, ValidationDirection.Response, new HashSet<string>()).Should().ContainSingle(f => f.Keyword == "maximum");
        v.Validate(JsonNode.Parse("""{"age":5}"""), lookup.Schema!, lookup.Pointer!, ValidationDirection.Response, new HashSet<string>()).Should().BeEmpty();
    }

    [Fact]
    public void Self_referencing_schemas_terminate()
    {
        var v = new OpenApiSchemaValidator(JsonNode.Parse("""{ "openapi": "3.0.3", "paths": {}, "components": { "schemas": { "Node": { "type": "object", "properties": { "next": { "$ref": "#/components/schemas/Node" } } } } } }""")!);
        v.Validate(JsonNode.Parse("""{"next":{"next":{"next":{"next":null}}}}"""), JsonNode.Parse("""{ "$ref": "#/components/schemas/Node" }""")!, "#", ValidationDirection.Response, new HashSet<string>())
            .Should().ContainSingle(f => f.InstancePath == "$.next.next.next.next" && f.Keyword == "type", "null is not an object (no nullable)");
    }

    private static readonly SourceContract GraphQlSchema = new()
    {
        Type = SourceContractType.GraphQlSchema,
        Operations = [new("query child", "query", null, ["id"]) { OperationId = "child", ParameterDetails = [new("id", "argument", true, "ID!")], ResultType = "Child", Detailed = true },
                      new("mutation createChild", "mutation", null, ["input"]) { OperationId = "createChild", ParameterDetails = [new("input", "argument", true, "NewChildInput!")], ResultType = "Child!", Detailed = true }],
        Types = [new("Child", "object", [new("id", "ID!", true), new("name", "String!", true), new("status", "Status", false), new("friends", "[Child!]", false)]),
                 new("Status", "enum", []) { Values = ["ACTIVE", "INACTIVE"] },
                 new("NewChildInput", "input_object", [new("name", "String!", true), new("age", "Int!", true)])],
    };

    [Fact]
    public void GraphQL_documents_and_variables_are_validated_against_the_schema()
    {
        GraphQlContractValidator.ValidateRequest(GraphQlSchema, "query C($id: ID!) { child(id: $id) { id name status friends { id } } }", """{"id":"7"}""").Findings.Should().BeEmpty();
        var bad = GraphQlContractValidator.ValidateRequest(GraphQlSchema, "query C($id: ID!) { child(id: $id, extra: 1) { id nickname friends } }", "{}").Findings;
        bad.Select(f => f.Keyword).Should().Contain(["required", "argument", "field", "selection"]);
        GraphQlContractValidator.ValidateRequest(GraphQlSchema, "query { child(id: \"7\") { id ", null).Findings.Should().ContainSingle(f => f.Tier == "Syntax");
        var input = GraphQlContractValidator.ValidateRequest(GraphQlSchema, "mutation M($input: NewChildInput!) { createChild(input: $input) { id } }", """{"input":{"name":"A","age":"seven","shoe":1}}""").Findings;
        input.Should().Contain(f => f.InstancePath == "$.variables.input" && f.Message.Contains("must be an Int"));
        input.Should().Contain(f => f.InstancePath == "$.variables.input.shoe" && f.Keyword == "inputField");
        GraphQlContractValidator.ValidateRequest(GraphQlSchema, "mutation M($input: NewChildInput!) { createChild(input: $input) { id } }", """{"input":null}""").Findings
            .Should().Contain(f => f.Message.Contains("non-null"));
        GraphQlContractValidator.ValidateRequest(GraphQlSchema, "query { unknownRoot { id } }", null).Findings.Should().ContainSingle(f => f.Message.Contains("query unknownRoot is not in the schema"));
    }

    [Fact]
    public void GraphQL_data_shapes_are_checked_and_errors_with_HTTP_200_are_judged_on_their_own()
    {
        const string query = "query C($id: ID!) { child(id: $id) { id name status friends { id } } }";
        GraphQlContractValidator.ValidateResponse(GraphQlSchema, query, JsonNode.Parse("""{"data":{"child":{"id":"7","name":"A","status":"ACTIVE","friends":[{"id":"8"}]}}}""")).Should().BeEmpty();
        var bad = GraphQlContractValidator.ValidateResponse(GraphQlSchema, query, JsonNode.Parse("""{"data":{"child":{"id":"7","name":null,"status":"GONE","friends":{"id":"8"}}}}"""));
        bad.Select(f => (f.InstancePath, f.Keyword)).Should().BeEquivalentTo([("$.data.child.name", "nullability"), ("$.data.child.status", "enum"), ("$.data.child.friends", "list")]);
        GraphQlContractValidator.ValidateResponse(GraphQlSchema, query, JsonNode.Parse("""{"data":{"child":{"id":"7","name":null,"status":null,"friends":null}},"errors":[{"message":"x","path":["child","name"]}]}"""))
            .Should().BeEmpty("a null in a non-null position is consistent when the errors array reports that path");
        GraphQlContractValidator.ValidateResponse(GraphQlSchema, query, JsonNode.Parse("""{"hello":1}""")).Should().ContainSingle(f => f.Keyword == "envelope");
    }

    [Fact]
    public void Hostile_nesting_is_capped_as_not_evaluated_never_as_a_violation_or_a_crash()
    {
        var levels = GraphQlContractValidator.MaxNesting + 10;
        var query = "query C($id: ID!) { child(id: $id) { " + string.Concat(Enumerable.Repeat("friends { ", levels)) + "id" + new string('}', levels) + " } }";
        var request = GraphQlContractValidator.ValidateRequest(GraphQlSchema, query, """{"id":"7"}""");
        request.Findings.Should().BeEmpty();
        request.Unsupported.Should().ContainSingle(u => u.Contains("selection nesting"));

        var deep = "{\"id\":\"1\"}";
        for (var i = 0; i < GraphQlContractValidator.MaxNesting * 2 + 10; i++) deep = "{\"id\":\"1\",\"friends\":[" + deep + "]}";
        var response = GraphQlContractValidator.ValidateResponse(GraphQlSchema, query, JsonNode.Parse("{\"data\":{\"child\":" + deep + "}}", null, new() { MaxDepth = 1000 }));
        response.Should().OnlyContain(f => f.Tier == "Unsupported" && f.Keyword == "depth");

        var yaml = "openapi: 3.0.3\n" + string.Concat(Enumerable.Range(0, 200).Select(i => new string(' ', i * 2) + "k:\n")) + new string(' ', 400) + "v: 1\n";
        var parse = () => OpenApiSchemaValidator.Parse(yaml);
        parse.Should().Throw<FormatException>().WithMessage("*nested too deeply*");
    }
}
