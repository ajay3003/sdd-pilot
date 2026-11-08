using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.TestData;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>Dataset rows in Test Data (validation) and typed resolution of lists and nested values (one resolution engine: ContractTestRules).</summary>
public sealed class DatasetRowResolutionTests
{
    private static TestDataResourceValidation Validate(params TestDataDatasetRow[] rows) =>
        TestDataResourceRules.Validate(new TestDataResourceDraft { Alias = "rows", Content = new() { Kind = TestDataResourceKind.Dataset, Rows = [.. rows] } });

    [Fact]
    public void Rows_need_unique_ids_one_type_per_column_and_no_credentials()
    {
        Validate(new("r-1", [new("age", TestDataPrimitive.Int32, "7")]), new("r-2", [new("age", TestDataPrimitive.Null, null)])).Blocking.Should().BeFalse("null is allowed in a typed column");
        Validate(new("r-1", [new("age", TestDataPrimitive.Int32, "7")]), new("r-1", [new("age", TestDataPrimitive.Int32, "8")])).Checks
            .Should().Contain(c => c.State == TestDataCheckState.Failed && c.Message.Contains("Duplicate row id"));
        Validate(new("r-1", [new("age", TestDataPrimitive.Int32, "7")]), new("r-2", [new("age", TestDataPrimitive.String, "seven")])).Checks
            .Should().Contain(c => c.State == TestDataCheckState.Failed && c.Message.Contains("a column has one type"));
        Validate(new TestDataDatasetRow("r-1", [new("age", TestDataPrimitive.Int32, "seven")])).Checks.Should().Contain(c => c.State == TestDataCheckState.Failed && c.Tier == TestDataCheckTiers.Type);
        Validate(new TestDataDatasetRow("r-1", [new("password", TestDataPrimitive.String, "hunter2-secret")])).Checks.Should().Contain(c => c.State == TestDataCheckState.Failed && c.Tier == TestDataCheckTiers.Sensitive);
        Validate(new TestDataDatasetRow("bad id!", [new("age", TestDataPrimitive.Int32, "7")])).Checks.Should().Contain(c => c.State == TestDataCheckState.Failed && c.Message.Contains("an id is required"));
        Validate([.. Enumerable.Range(0, TestDataResourceRules.MaxDatasetRows + 1).Select(i => new TestDataDatasetRow($"r{i}", [new("a", TestDataPrimitive.Int32, "1")]))]).Checks
            .Should().Contain(c => c.State == TestDataCheckState.Failed && c.Message.Contains($"at most {TestDataResourceRules.MaxDatasetRows}"));
    }

    private static ContractTestDefinition GraphQlTest(string variables) => new()
    {
        Id = Guid.NewGuid(), ContractKey = "g", ContractVersion = 1, Operation = "query children", Name = "children", Kind = ContractTestKind.Positive,
        Inputs = [new("ids", "variable", true, "ID"), new("flag", "variable", true, "Boolean"), new("extra", "variable", false, "String")],
        Template = new() { Protocol = ExecutionProtocol.GraphQl, Path = "/graphql", GraphQlQuery = "query($ids:[ID!]!){children(ids:$ids){id}}", GraphQlVariablesJson = variables },
    };

    [Fact]
    public void List_elements_and_nested_objects_keep_their_Test_Data_types_and_unresolved_optional_elements_are_left_out()
    {
        var values = new Dictionary<string, TestDataTypedEntry>
        {
            ["a"] = new("a", TestDataPrimitive.Int32, "7"), ["b"] = new("b", TestDataPrimitive.Boolean, "true"), ["c"] = new("c", TestDataPrimitive.Json, """{"x":[1,2]}"""),
        };
        var test = GraphQlTest("""{"ids":["{{dataset.a}}",["{{dataset.b}}"],{"inner":"{{dataset.c}}"},"{{dataset.extra}}"],"flag":"{{dataset.b}}"}""") with
        {
            Inputs = [new("a", "variable", true, "Int"), new("b", "variable", true, "Boolean"), new("c", "variable", true, "Json"), new("extra", "variable", false, "String")],
        };
        var (call, missing) = ContractTestRules.Materialize(test, values);
        missing.Should().BeEmpty();
        var vars = JsonNode.Parse(call!.GraphQlVariablesJson!)!.AsObject();
        vars.ToJsonString().Should().Be("""{"ids":[7,[true],{"inner":{"x":[1,2]}}],"flag":true}""");
        vars["ids"]![0]!.GetValueKind().Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public void A_required_list_element_without_a_value_is_reported_missing_not_replaced()
    {
        var test = GraphQlTest("""{"ids":["{{dataset.a}}","{{dataset.b}}"]}""") with { Inputs = [new("a", "variable", true, "Int"), new("b", "variable", true, "Int")] };
        var (call, missing) = ContractTestRules.Materialize(test, new Dictionary<string, TestDataTypedEntry> { ["a"] = new("a", TestDataPrimitive.Int32, "1") });
        call.Should().BeNull();
        missing.Should().Equal("b");
    }

    [Fact]
    public void Resolved_values_name_their_type_and_source_and_preview_redacted_text()
    {
        var test = GraphQlTest("""{"ids":["{{dataset.a}}"],"note":"{{dataset.n}}"}""") with { Inputs = [new("a", "variable", true, "Int"), new("n", "variable", true, "String")] };
        var values = new Dictionary<string, TestDataTypedEntry> { ["a"] = new("a", TestDataPrimitive.Int32, "7"), ["n"] = new("n", TestDataPrimitive.String, new string('x', 300)), ["unused"] = new("unused", TestDataPrimitive.Int32, "1") };
        var resolved = ContractTestRules.Resolved(test, values, name => $"row r-1 ({name})");
        resolved.Select(r => r.Name).Should().Equal("a", "n");
        resolved[0].Should().Be(new ResolvedValue("a", "Int32", "row r-1 (a)", "7"));
        resolved[1].Preview!.Length.Should().BeLessThanOrEqualTo(121);
    }
}
