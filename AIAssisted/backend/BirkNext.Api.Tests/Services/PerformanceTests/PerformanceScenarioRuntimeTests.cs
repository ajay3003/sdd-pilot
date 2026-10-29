using System.Text.Json;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.TestData;
using BirkNext.PerformanceTests;
using BirkNext.TestData;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Runtime value sources and step dependencies: a {placeholder} is a dynamic parameter that needs a value source (Test Data, previous step
/// output, scenario variable, fixed or generated) — Test Data is one option, never implied. Step order is execution order; a reference to a
/// later, missing or disabled step is invalid and never silently "fixed". The k6 script follows the saved order and reads outputs at run time.
/// </summary>
public sealed class PerformanceScenarioRuntimeTests
{
    private static HttpPerformanceStep Step(string id, string path, params ScenarioDataMapping[] mappings) => new()
    {
        Id = id, Name = id, Method = "GET", RelativePath = path, ExpectedStatusCodes = [200], ThinkTimeMs = 0, DataMappings = mappings.Length == 0 ? null : [.. mappings],
    };

    private static ScenarioDataMapping FromStep(string variable, string stepId, string output) =>
        new(variable, [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.PreviousStep, FromStepId = stepId, Output = output };

    private static PerformanceTestDefinition Definition(params HttpPerformanceStep[] steps) => PerformanceTestReviewTests.Definition(d => d with
    {
        Scenario = d.Scenario with { TestDataProfileId = null, Steps = [.. steps] },
    });

    private static readonly HttpPerformanceStep Producer = Step("create", "/api/users") with { Outputs = [new StepOutput("id", "$.data.items[0].id")] };
    private static readonly HttpPerformanceStep Consumer = Step("get", "/api/users/{id}/barn", FromStep("id", "create", "id"));

    private static List<PerformanceTestSafety.Issue> Issues(PerformanceTestDefinition d) => PerformanceTestSafety.Check(d, null, new PerformanceTestOptions());

    [Fact]
    public void A_dynamic_parameter_needs_a_value_source_and_every_source_kind_resolves()
    {
        Issues(Definition(Step("get", "/api/users/{id}"))).Should().ContainSingle(i => i.Key == "runtime" && i.State == PerformanceReadinessState.NeedsConfiguration)
            .Which.Message.Should().Contain("id: runtime value source not configured").And.NotContain("Test Data missing");
        Issues(Definition(Producer, Consumer)).Should().NotContain(i => new[] { "runtime", "dependency", "testdata", "output" }.Contains(i.Key));
        Issues(Definition(Step("get", "/api/users/{id}", new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.Fixed, FixedValue = "u-1" })))
            .Should().NotContain(i => new[] { "runtime", "testdata" }.Contains(i.Key));
        var withVariable = Definition(Step("get", "/api/users/{id}", new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.ScenarioVariable, ScenarioVariable = "brukerId" }));
        Issues(withVariable).Should().Contain(i => i.Key == "runtime" && i.Message.Contains("'brukerId' is not defined"));
        Issues(withVariable with { Scenario = withVariable.Scenario with { Variables = [new ScenarioVariable("brukerId", RuntimeValueSource.Fixed) { Value = "b-1" }] } })
            .Should().NotContain(i => new[] { "runtime", "variable" }.Contains(i.Key));
        var query = Step("list", "/api/users") with { QueryParameters = [new("correlationId", "{correlationId}")] };
        Issues(Definition(query with { DataMappings = [new ScenarioDataMapping("correlationId", [], TestDataDataType.SimpleValues, "query") { Source = RuntimeValueSource.Generated, Generator = GeneratedValueKind.Uuid }] }))
            .Should().NotContain(i => i.Key == "runtime", "a generated UUID is valid for a technical query value");
        Issues(Definition(Step("get", "/api/users/{brukerId}", new ScenarioDataMapping("brukerId", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.Generated, Generator = GeneratedValueKind.Uuid })))
            .Should().Contain(i => i.Key == "runtime" && i.Message.Contains("generated value is not valid"), "business identifiers are never generated");
    }

    [Fact]
    public void Previous_step_references_are_validated_in_execution_order()
    {
        Issues(Definition(Consumer, Producer)).Should().ContainSingle(i => i.Key == "dependency" && i.State == PerformanceReadinessState.InvalidScenario)
            .Which.Message.Should().Contain("References output from Step 2, which executes later — invalid execution order");
        Issues(Definition(Producer with { Disabled = true }, Consumer)).Should().Contain(i => i.Key == "dependency" && i.Message.Contains("disabled"));
        Issues(Definition(Producer with { Outputs = null }, Consumer)).Should().Contain(i => i.Key == "dependency" && i.Message.Contains("has no output 'id'"));
        Issues(Definition(Consumer)).Should().Contain(i => i.Key == "dependency" && i.Message.Contains("no longer in the scenario"));
        Issues(Definition(Producer with { Outputs = [new StepOutput("id", "id")] }, Consumer)).Should().Contain(i => i.Key == "output", "an output path must be a JSON path such as $.id");
    }

    [Fact]
    public void Reorder_conflicts_dependents_duplicate_and_move_with_dependencies()
    {
        var independent = Step("other", "/api/health-free");
        List<HttpPerformanceStep> steps = [Producer, Consumer, independent];
        PerformanceScenarioRuntime.ReorderConflicts(steps, 1, 0).Should().ContainSingle().Which.Should().Match<StepDependency>(d => d.ConsumerId == "get" && d.ProducerId == "create");
        PerformanceScenarioRuntime.ReorderConflicts(steps, 2, 0).Should().BeEmpty("independent steps move freely");
        PerformanceScenarioRuntime.Moved(steps, 2, 0).Select(s => s.Id).Should().Equal("other", "create", "get");
        PerformanceScenarioRuntime.MoveWithDependencies([independent, Producer, Consumer], 2, 0)!.Select(s => s.Id).Should().Equal("create", "get", "other");
        PerformanceScenarioRuntime.Dependents(steps, "create").Should().ContainSingle(d => d.Variable == "id");
        var copy = PerformanceScenarioRuntime.Duplicate(Consumer with { ThinkTimeMs = 750, Authentication = StepAuthenticationMode.None }, steps.Select(s => s.Name));
        copy.Should().Match<HttpPerformanceStep>(s => s.Id != Consumer.Id && s.Name == "get-copy" && s.ThinkTimeMs == 750 && s.Authentication == StepAuthenticationMode.None);
        copy.DataMappings.Should().BeEquivalentTo(Consumer.DataMappings, "runtime mappings are copied");
        copy.DataMappings.Should().NotBeSameAs(Consumer.DataMappings);
    }

    [Fact]
    public void Step_authentication_inherits_overrides_or_uses_none_and_never_carries_a_token()
    {
        var authenticated = Definition(Step("list", "/api/users")) with { AuthenticationReference = "target-environment" };
        Issues(authenticated).Should().Contain(i => i.Key == "authentication", "an inheriting step uses the default");
        Issues(authenticated with { Scenario = authenticated.Scenario with { Steps = [authenticated.Scenario.Steps[0] with { Authentication = StepAuthenticationMode.None }] } })
            .Should().NotContain(i => i.Key == "authentication", "no executed step is authenticated");
        Issues(Definition(Step("list", "/api/users") with { Authentication = StepAuthenticationMode.Override })).Should().Contain(i => i.Key == "authentication" && i.Message.Contains("names no configuration"));
        PerformanceScenarioRuntime.EffectiveAuthentication(authenticated, Step("x", "/") with { Authentication = StepAuthenticationMode.Override, AuthenticationReference = "other" }).Should().Be("other");
    }

    [Fact]
    public void K6_script_runs_steps_in_saved_order_and_binds_every_value_source()
    {
        var generated = Step("list", "/api/users") with
        {
            QueryParameters = [new("correlationId", "{correlationId}"), new("mode", "{mode}")],
            DataMappings = [new ScenarioDataMapping("correlationId", [], TestDataDataType.SimpleValues, "query") { Source = RuntimeValueSource.Generated, Generator = GeneratedValueKind.Uuid },
                new ScenarioDataMapping("mode", [], TestDataDataType.SimpleValues, "query") { Source = RuntimeValueSource.Fixed, FixedValue = "x\";alert(1);//" }],
        };
        var d = Definition(generated, Producer, Consumer);
        var script = K6ScriptGenerator.Generate(d, null, "/birknext/out/summary.json");
        var json = script[(script.IndexOf("const STEPS = ", StringComparison.Ordinal) + 14)..script.IndexOf(";\nconst ROWS", StringComparison.Ordinal)];
        var steps = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
        steps.Select(s => s.GetProperty("id").GetString()).Should().Equal("list", "create", "get");
        steps[1].GetProperty("outputs")[0].EnumerateArray().Select(e => e.GetString()).Should().Equal("id", "data.items.0.id");
        steps[2].GetProperty("bind").GetProperty("id").GetProperty("k").GetString().Should().Be("step");
        steps[0].GetProperty("bind").GetProperty("mode").GetProperty("v").GetString().Should().Be("x\";alert(1);//", "a fixed value enters only as a JSON literal");
        script.Should().Contain("params.responseType = 'text'").And.Contain("res.json(selector)").And.NotContain("x\";alert", "the quote inside a fixed value stays escaped");

        var reordered = Definition(Producer, Consumer with { }, generated);
        var later = K6ScriptGenerator.Generate(reordered, null, "/out.json");
        later.IndexOf("\"id\":\"create\"", StringComparison.Ordinal).Should().BeLessThan(later.IndexOf("\"id\":\"list\"", StringComparison.Ordinal), "the generated order is the saved order, never sorted by id or name");
    }

    [Fact]
    public void Legacy_mappings_keep_their_exact_json_and_test_data_variables_resolve_for_the_run()
    {
        JsonSerializer.Serialize(new ScenarioDataMapping("paymentId", ["pay-1"], TestDataDataType.SimpleValues, "path"))
            .Should().Be("""{"Variable":"paymentId","AssetAliases":["pay-1"],"DataType":"SimpleValues","In":"path"}""", "fingerprints of existing definitions do not change");
        var plain = JsonSerializer.Serialize(new HttpPerformanceStep { Id = "a", Name = "a" });
        plain.Should().NotContain("Outputs").And.NotContain("Authentication");

        var catalog = new TestDataCatalog([new Provider(new TestDataAsset { Id = "user-01", Alias = "user-01", Kind = TestDataKind.Scalar, Approval = TestDataApproval.Approved, Value = new TestDataValue("U-1"), Source = "Configured" })]);
        var d = Definition(Step("get", "/api/users/{id}", new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.ScenarioVariable, ScenarioVariable = "bruker" }));
        d = d with { Scenario = d.Scenario with { Variables = [new ScenarioVariable("bruker", RuntimeValueSource.TestData) { AssetAlias = "user-01" }] } };
        PerformanceTestSafety.UsesCentralTestData(d).Should().BeTrue();
        PerformanceCentralTestData.Issues(d, catalog, DateTimeOffset.UtcNow).Should().BeEmpty();
        var profile = PerformanceCentralTestData.Resolve(d, catalog)!;
        profile.Columns.Should().Equal("var:bruker");
        profile.Rows.Single().Should().Equal("U-1");
        JsonSerializer.Serialize(d).Should().NotContain("U-1", "the definition keeps the alias, never the value");
        PerformanceCentralTestData.Mappings(Definition(Producer, Consumer)).Should().BeEmpty("previous-step mappings never touch Test Data");
    }

    [Fact]
    public void Status_codes_and_transport_failures_are_counted_without_bodies_and_absent_capture_is_null_not_zero()
    {
        const string summary = """
            { "metrics": {
              "http_reqs": { "values": { "count": 100, "rate": 10 } },
              "http_req_duration": { "values": { "med": 10, "p(95)": 20 } },
              "http_req_failed": { "values": { "rate": 0.1, "passes": 10, "fails": 90 } },
              "birknext_status_200": { "values": { "count": 88 } },
              "birknext_status_429": { "values": { "count": 7 } },
              "birknext_status_4xx_other": { "values": { "count": 2 } },
              "birknext_error_timeout": { "values": { "count": 3 } }
            }, "state": { "testRunDurationMs": 10000 } }
            """;
        var (metrics, error) = K6SummaryParser.Parse(summary, []);
        error.Should().BeNull();
        metrics!.StatusCodes.Should().BeEquivalentTo(new Dictionary<string, long> { ["200"] = 88, ["429"] = 7, ["4xx other"] = 2 });
        metrics.TransportErrors.Should().BeEquivalentTo(new Dictionary<string, long> { ["timeout"] = 3 });

        var older = summary.Replace("birknext_", "unrelated_", StringComparison.Ordinal);
        var (old, _) = K6SummaryParser.Parse(older, []);
        old!.StatusCodes.Should().BeNull("an older script did not count statuses — not captured, never zero");
        old.TransportErrors.Should().BeNull();
        JsonSerializer.Serialize(old).Should().NotContain("StatusCodes", "absent capture adds nothing to stored runs");

        var script = K6ScriptGenerator.Generate(Definition(Step("list", "/api/users")), null, "/out.json");
        script.Should().Contain("new Counter(`birknext_status_${code}`)").And.Contain("count(res);").And.Contain("discardResponseBodies: true", "response bodies are still not retained");
    }

    private sealed class Provider(params TestDataAsset[] assets) : ITestDataProvider
    {
        public string Id => "configured";
        public string Label => "Configured";
        public string Description => "";
        public IReadOnlyList<TestDataAsset> Assets() => assets;
    }
}
