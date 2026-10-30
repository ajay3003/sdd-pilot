using System.Text.Json;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Test Data is optional: a dynamic parameter needs a runtime value, not a Test Data asset. A deterministic contract default/example pre-fills the
/// value (auto-filled, with provenance), valid for the declared type; an example that Live Discovery may contradict needs review; Test Data is used
/// only when chosen; credential-like or identity-like declared values are never kept. Local fixtures only.
/// </summary>
public sealed class PerformanceRuntimeValueSourceTests
{
    private const string Target = "https://tilgang.example.test";

    private const string TilgangOpenApi = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Tilgang API", "version": "1.0.0" },
          "paths": {
            "/tilgang/{id}": {
              "get": { "operationId": "getTilgang", "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer", "format": "int64" }, "example": 12345 } ],
                       "responses": { "200": { "description": "ok" } } }
            },
            "/tilgang": {
              "get": { "operationId": "listTilgang", "parameters": [
                  { "name": "status", "in": "query", "required": true, "schema": { "type": "string", "enum": ["AKTIV", "INAKTIV"], "default": "AKTIV" } },
                  { "name": "page", "in": "query", "schema": { "type": "integer" }, "example": 1 } ],
                "responses": { "200": { "description": "ok" } } }
            },
            "/tilgang/{id}/logg/{fra}": {
              "get": { "operationId": "getLogg", "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" }, "example": "abc" },
                  { "name": "fra", "in": "path", "required": true, "schema": { "type": "string", "format": "date" } } ],
                "responses": { "200": { "description": "ok" } } }
            },
            "/person/{fnr}": {
              "get": { "operationId": "getPerson", "parameters": [ { "name": "fnr", "in": "path", "required": true, "schema": { "type": "string" }, "example": "01019912345" } ],
                       "responses": { "200": { "description": "ok" } } }
            },
            "/session": {
              "get": { "operationId": "getSession", "parameters": [ { "name": "token", "in": "query", "required": true, "schema": { "type": "string" }, "example": "abc123" } ],
                       "responses": { "200": { "description": "ok" } } }
            }
          }
        }
        """;

    private static SourceContract Tilgang() => new()
    {
        Id = "tilgang", Type = SourceContractType.OpenApi, Name = "Tilgang API", File = "Tilgang/openapi.json", Operations = ContractAnalyzer.ReadOpenApiOperations("openapi.json", TilgangOpenApi),
    };

    private static ObservedNetworkEndpoint Seen(string path) => new()
    {
        Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = "tilgang.example.test", Port = 443, Method = "GET", Path = path, Count = 5, LastStatus = 200,
        LastObservedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero),
    };

    private static ScenarioOperation Op(string operationId, IReadOnlyList<ObservedNetworkEndpoint>? seen = null, IReadOnlyList<TestDataAssetSummary>? assets = null) =>
        PerformanceScenarioEngine.Build([Tilgang()], null, "Tilgang.zip", Target, "Development", seen ?? [], assets ?? [], ["tilgang"], true).Operations.Single(o => o.OperationId == operationId);

    private static PerformanceTestDefinition Definition(params HttpPerformanceStep[] steps) => PerformanceTestReviewTests.Definition(d => d with
    {
        Scenario = d.Scenario with { TestDataProfileId = null, Steps = [.. steps] },
    });

    private static List<PerformanceTestSafety.Issue> Issues(PerformanceTestDefinition d) => PerformanceTestSafety.Check(d, null, new PerformanceTestOptions());

    [Fact]
    public void Contract_keeps_short_safe_declared_values_and_never_credential_or_identity_values()
    {
        var ops = Tilgang().Operations;
        ops.Single(o => o.OperationId == "getTilgang").ParameterDetails.Single().Should().Match<ContractParameter>(p => p.Example == "12345" && p.Type == "integer:int64");
        var status = ops.Single(o => o.OperationId == "listTilgang").ParameterDetails.Single(p => p.Name == "status");
        status.Default.Should().Be("AKTIV");
        status.Enum.Should().Equal("AKTIV", "INAKTIV");
        ops.Single(o => o.OperationId == "getPerson").ParameterDetails.Single().Example.Should().BeNull("an identity-number-like parameter/value is never kept");
        ops.Single(o => o.OperationId == "getSession").ParameterDetails.Single().Example.Should().BeNull("a credential-like parameter's value is never kept");
        ops.Single(o => o.OperationId == "getSession").ParameterDetails.Single().HasExample.Should().BeTrue("that one is declared is still recorded");
    }

    [Fact]
    public void Tilgang_id_example_is_auto_filled_and_the_step_is_Ready_without_Test_Data()
    {
        var op = Op("getTilgang");
        op.TestDataState.Should().Be(ScenarioTestDataStates.Optional);
        op.Level.Should().NotBe(ScenarioRecommendationLevel.ReviewRequired, "a dynamic parameter never makes a candidate review-required by itself");
        op.Reasons.Should().Contain("suggested value available for {id}");
        var step = PerformanceScenarioEngine.ToStep(op);
        step.DataMappings.Should().ContainSingle().Which.Should().Match<ScenarioDataMapping>(m => m.Source == RuntimeValueSource.AutoFilled && m.FixedValue == "12345"
            && m.ValueOrigin == RuntimeValueOrigins.OpenApiExample && m.DeclaredType == "integer:int64" && !m.NeedsReview && m.AssetAliases.Count == 0);
        var d = Definition(step);
        PerformanceScenarioRuntime.Readiness(d, 0).Status.Should().Be(StepStatus.Ready);
        var row = PerformanceScenarioRuntime.Rows(d.Scenario, 0).Single();
        (row.SourceLabel, row.ValueText).Should().Be(("Auto-filled · OpenAPI example", "12345"));
        PerformanceScenarioRuntime.ValuesSummary(d.Scenario).Should().Be((1, 1, 0));
        Issues(d).Should().NotContain(i => new[] { "runtime", "testdata", "placeholder" }.Contains(i.Key));
    }

    [Fact]
    public void Contract_default_wins_over_example_enum_is_kept_and_optional_query_is_not_a_requirement()
    {
        var step = PerformanceScenarioEngine.ToStep(Op("listTilgang"));
        step.QueryParameters.Should().ContainSingle().Which.Name.Should().Be("status", "an optional query parameter is not added and never blocks");
        step.DataMappings.Should().ContainSingle().Which.Should().Match<ScenarioDataMapping>(m => m.FixedValue == "AKTIV" && m.ValueOrigin == RuntimeValueOrigins.ContractDefault
            && m.AllowedValues!.SequenceEqual(new[] { "AKTIV", "INAKTIV" }));
        PerformanceScenarioRuntime.Readiness(Definition(step), 0).Status.Should().Be(StepStatus.Ready);
    }

    [Fact]
    public void An_example_invalid_for_the_declared_type_is_not_used_and_the_value_is_missing_not_Test_Data_missing()
    {
        var step = PerformanceScenarioEngine.ToStep(Op("getLogg"));
        step.DataMappings.Should().BeNull("'abc' is not an integer and {fra} declares nothing");
        var readiness = PerformanceScenarioRuntime.Readiness(Definition(step), 0);
        readiness.Status.Should().Be(StepStatus.NeedsConfiguration);
        readiness.Reasons.Should().Contain("Step 1 · id: Runtime value missing — choose a value source").And.NotContain(r => r.Contains("Test Data"));
        step.Source!.Parameters.Should().BeEquivalentTo([new ScenarioParameterShape("id", "integer"), new ScenarioParameterShape("fra", "string:date")],
            "the declared shape is kept even without a suggested value");
        var typed = step with { DataMappings = [new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.Fixed, FixedValue = "abc" }] };
        PerformanceScenarioRuntime.Rows(Definition(typed).Scenario, 0).Single(r => r.Parameter.Variable == "id").Problem.Should().Be("'abc' is not a valid integer");
    }

    [Fact]
    public void Example_with_Live_Discovery_values_needs_review_and_is_never_chosen_silently()
    {
        var op = Op("getTilgang", [Seen("/tilgang/777")]);
        op.Needs.Single().ObservedValueAvailable.Should().BeTrue();
        var mapping = PerformanceScenarioEngine.ToStep(op).DataMappings!.Single();
        mapping.NeedsReview.Should().BeTrue();
        var d = Definition(PerformanceScenarioEngine.ToStep(op));
        PerformanceScenarioRuntime.Rows(d.Scenario, 0).Single().Problem.Should().StartWith("Needs review: the OpenAPI example value and values observed by Live Discovery may differ");
        Issues(d).Should().Contain(i => i.Key == "runtime" && i.State == PerformanceReadinessState.NeedsConfiguration && i.Message.Contains("Needs review"));
        var confirmed = d with { Scenario = d.Scenario with { Steps = [d.Scenario.Steps[0] with { DataMappings = [mapping with { NeedsReview = false }] }] } };
        PerformanceScenarioRuntime.Readiness(confirmed, 0).Status.Should().Be(StepStatus.Ready);
    }

    [Fact]
    public void Explicitly_chosen_Test_Data_wins_and_stays_Test_Data()
    {
        var step = PerformanceScenarioEngine.ToStep(Op("getTilgang"), new Dictionary<string, string> { ["id"] = "tilgang-id" });
        step.DataMappings.Should().ContainSingle().Which.Should().Match<ScenarioDataMapping>(m => m.Source == RuntimeValueSource.TestData && m.AssetAliases.Single() == "tilgang-id" && m.FixedValue == null);
        var missing = Definition(step with { DataMappings = [new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path")] });
        Issues(missing).Should().ContainSingle(i => i.Key == "testdata").Which.Message.Should().StartWith("Test Data missing for {id}", "named only when Test Data is the chosen source");
    }

    [Theory]
    [InlineData("12345", "integer", null)]
    [InlineData("abc", "integer:int64", "'abc' is not a valid integer")]
    [InlineData("1.5", "number", null)]
    [InlineData("yes", "boolean", "'yes' is not a valid boolean (true/false)")]
    [InlineData("2026-10-30", "string:date", null)]
    [InlineData("30.10.2026", "string:date", "'30.10.2026' is not a valid date (YYYY-MM-DD)")]
    [InlineData("not-a-guid", "string:uuid", "'not-a-guid' is not a valid UUID")]
    [InlineData("42", "Int!", null)]
    [InlineData("anything", "string", null)]
    [InlineData("anything", null, null)]
    public void Fixed_and_auto_filled_values_are_validated_against_the_declared_type(string value, string? type, string? problem) =>
        PerformanceScenarioRuntime.TypeProblem(value, type).Should().Be(problem);

    [Fact]
    public void Enum_values_and_invalid_fixed_values_block_with_a_precise_reason()
    {
        PerformanceScenarioRuntime.TypeProblem("SLETTET", "string", ["AKTIV", "INAKTIV"]).Should().Be("'SLETTET' is not an allowed value (AKTIV, INAKTIV)");
        var fixedStep = new HttpPerformanceStep
        {
            Id = "s1", Name = "get", Method = "GET", RelativePath = "/tilgang/{id}", ExpectedStatusCodes = [200],
            DataMappings = [new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.Fixed, FixedValue = "abc", DeclaredType = "integer" }],
        };
        Issues(Definition(fixedStep)).Should().Contain(i => i.Key == "runtime" && i.Message.Contains("'abc' is not a valid integer"));
    }

    [Fact]
    public void K6_binds_an_auto_filled_value_as_a_JSON_literal()
    {
        var script = K6ScriptGenerator.Generate(Definition(PerformanceScenarioEngine.ToStep(Op("getTilgang")) with { Id = "s1" }), null, "/out.json");
        var json = script[(script.IndexOf("const STEPS = ", StringComparison.Ordinal) + 14)..script.IndexOf(";\nconst ROWS", StringComparison.Ordinal)];
        var bind = JsonDocument.Parse(json).RootElement[0].GetProperty("bind").GetProperty("id");
        (bind.GetProperty("k").GetString(), bind.GetProperty("v").GetString()).Should().Be(("fixed", "12345"));
    }

    [Fact]
    public void Stored_mappings_stay_compatible_new_fields_are_omitted_when_unset()
    {
        var legacy = JsonSerializer.Deserialize<ScenarioDataMapping>("""{"Variable":"id","AssetAliases":["tilgang-id"],"DataType":"SimpleValues","In":"path"}""")!;
        legacy.Source.Should().Be(RuntimeValueSource.TestData, "a stored mapping without a source is a Test Data mapping, as before");
        JsonSerializer.Serialize(legacy).Should().NotContain("ValueOrigin").And.NotContain("DeclaredType").And.NotContain("NeedsReview").And.NotContain("AllowedValues");
        var auto = JsonSerializer.Deserialize<ScenarioDataMapping>(JsonSerializer.Serialize(PerformanceScenarioEngine.ToStep(Op("getTilgang")).DataMappings!.Single()))!;
        auto.Should().Match<ScenarioDataMapping>(m => m.Source == RuntimeValueSource.AutoFilled && m.FixedValue == "12345" && m.ValueOrigin == RuntimeValueOrigins.OpenApiExample);
    }
}
