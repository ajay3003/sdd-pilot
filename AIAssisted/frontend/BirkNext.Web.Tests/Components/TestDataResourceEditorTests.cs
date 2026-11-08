using BirkNext.SourceDomains;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>Add data / Edit data: kind → configure → validate → save as Draft, type-aware controls, inline errors, no credentials. Synthetic values only.</summary>
public sealed class TestDataResourceEditorTests : BunitContext
{
    private sealed class Api : ITestDataApiService
    {
        public List<(string? Alias, TestDataResourceDraft Draft)> Saved { get; } = [];
        public IReadOnlyList<TestDataOperationOption> Operations { get; set; } = [];
        public Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default) => Task.FromResult<(TestDataCatalogView?, string?)>((new([], [], []), null));
        public Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default) => Task.FromResult<(TestDataAssetDetail?, string?)>((null, null));
        public Task<(TestDataResourceValidation? Validation, string? Error)> ValidateResourceAsync(TestDataResourceDraft draft, CancellationToken ct = default) =>
            Task.FromResult<(TestDataResourceValidation?, string?)>((TestDataResourceRules.Validate(draft, new() { KnownAliases = ["child-age"] }), null));
        public Task<(TestDataResourceSaveResult? Result, string? Error)> SaveResourceAsync(string? alias, TestDataResourceDraft draft, CancellationToken ct = default)
        {
            Saved.Add((alias, draft));
            return Task.FromResult<(TestDataResourceSaveResult?, string?)>((new(true, new TestDataResourceView { Alias = draft.Alias, Content = draft.Content, Version = (draft.ExpectedVersion ?? 0) + 1 }, new([]), "Saved"), null));
        }
        public Task<(IReadOnlyList<TestDataOperationOption>? Operations, string? Error)> ResourceOperationsAsync(string kind, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<TestDataOperationOption>?, string?)>((Operations, null));
    }

    private readonly Api _api = new();
    private string? _closed = "(open)";
    private bool _xml;

    public TestDataResourceEditorTests() => Services.AddSingleton<ITestDataApiService>(_api);

    private IRenderedComponent<TestDataResourceEditor> Editor(TestDataResourceView? existing = null) => Render<TestDataResourceEditor>(p => p
        .Add(x => x.Existing, existing).Add(x => x.KnownAliases, ["child-age"]).Add(x => x.Environments, ["dev"])
        .Add(x => x.OnClose, EventCallback.Factory.Create<string?>(this, a => _closed = a))
        .Add(x => x.OnXmlRequested, EventCallback.Factory.Create(this, () => _xml = true)));

    private static void Choose(IRenderedComponent<TestDataResourceEditor> c, TestDataResourceKind kind)
    {
        c.Find($"[data-testid=tdx-kind][data-kind={kind}]").Click();
        c.Find("[data-testid=tdx-next]").Click();
        c.WaitForAssertion(() => c.Find("[data-testid=tdx]").GetAttribute("data-step").Should().Be("2"));
    }

    [Fact]
    public void Kinds_are_offered_as_a_compact_choice_and_XML_opens_the_existing_fixture_editor()
    {
        var c = Editor();
        c.FindAll("[data-testid=tdx-kind]").Select(b => b.GetAttribute("data-kind")).Should().Equal(Enum.GetNames<TestDataResourceKind>());
        c.Find("[data-testid=tdx-kind-xml]").Click();
        _xml.Should().BeTrue("XML is never edited in a second editor");
    }

    [Fact]
    public void An_integer_is_validated_inline_and_saved_as_a_typed_draft()
    {
        var c = Editor();
        Choose(c, TestDataResourceKind.SingleValue);
        c.FindAll("[data-testid=tdx-inline-error]").Should().BeEmpty("an untouched form does not open with errors");
        c.Find("[data-testid=tdx-alias]").Input("child-years");
        c.Find("[data-testid=tdx-type]").Change(TestDataPrimitive.Int32.ToString());
        c.Find("[data-testid=tdx-value]").Input("4.2");
        c.Find("[data-testid=tdx-inline-error]").TextContent.Should().Contain("32-bit integer");
        c.Find("[data-testid=tdx-validate]").Click();
        c.WaitForAssertion(() => c.Find("[data-testid=tdx-tier][data-tier=Type]").GetAttribute("data-state").Should().Be("Failed"));
        c.Find("[data-testid=tdx-save]").HasAttribute("disabled").Should().BeTrue();

        c.Find("[data-testid=tdx-back]").Click();
        c.Find("[data-testid=tdx-value]").Input("42");
        c.Find("[data-testid=tdx-validate]").Click();
        c.WaitForAssertion(() => c.Find("[data-testid=tdx-save]").HasAttribute("disabled").Should().BeFalse());
        c.Find("[data-testid=tdx-tier][data-tier=Contract]").Should().NotBeNull();
        c.Find("[data-testid=tdx-save]").Click();
        c.WaitForAssertion(() => _closed.Should().Be("child-years"));
        var (alias, draft) = _api.Saved.Single();
        alias.Should().BeNull("a new resource is created, not a version");
        draft.Content.Value.Should().Be(new TestDataTypedEntry("", TestDataPrimitive.Int32, "42"));
        TestDataResourceRules.Payload(draft.Content).Should().Be("42", "an integer is never stored as text");
    }

    [Fact]
    public void Dataset_rows_are_imported_from_CSV_typed_per_column_and_saved_as_typed_rows()
    {
        var c = Editor();
        Choose(c, TestDataResourceKind.Dataset);
        c.Find("[data-testid=tdx-alias]").Input("children-rows");
        c.Find("[data-testid=tdx-csv]").Input("id,name,age\nr-1,Barn 1,7\nr-2,\"Barn, 2\",null\n");
        c.Find("[data-testid=tdx-csv-import]").Click();
        c.Find("[data-testid=tdx-csv-message]").TextContent.Should().Contain("2 row(s) and 2 column(s)");
        c.FindAll("[data-testid=tdx-row]").Should().HaveCount(2);
        c.FindAll("[data-testid=tdx-col-type]").Select(s => s.GetAttribute("value")).Should().Equal("String", "Int32");

        // An added empty row is refused (a row needs a value), so nothing incomplete is saved.
        c.Find("[data-testid=tdx-add-row]").Click();
        c.FindAll("[data-testid=tdx-row-id]")[2].GetAttribute("value").Should().Be("r-003");
        c.Find("[data-testid=tdx-validate]").Click();
        c.WaitForAssertion(() => c.Find("[data-testid=tdx-save]").HasAttribute("disabled").Should().BeTrue());
        c.Find("[data-testid=tdx-back]").Click();
        c.FindAll("[aria-label='Delete row 3']").Single().Click();
        c.Find("[data-testid=tdx-validate]").Click();
        c.WaitForAssertion(() => c.Find("[data-testid=tdx-save]").HasAttribute("disabled").Should().BeFalse());
        c.Find("[data-testid=tdx-save]").Click();
        c.WaitForAssertion(() => _closed.Should().Be("children-rows"));
        var rows = _api.Saved.Single().Draft.Content.Rows;
        rows.Select(r => r.Id).Should().Equal("r-1", "r-2");
        rows[0].Values.Should().Equal(new TestDataTypedEntry("name", TestDataPrimitive.String, "Barn 1"), new TestDataTypedEntry("age", TestDataPrimitive.Int32, "7"));
        rows[1].Values.Should().Equal(new TestDataTypedEntry("name", TestDataPrimitive.String, "Barn, 2"), new TestDataTypedEntry("age", TestDataPrimitive.Null, null));
        _api.Saved.Single().Draft.Content.Members.Should().BeEmpty("the untouched empty member line is not a member");
    }

    [Fact]
    public void Booleans_use_a_toggle_and_null_is_an_explicit_marker()
    {
        var c = Editor();
        Choose(c, TestDataResourceKind.SingleValue);
        c.Find("[data-testid=tdx-type]").Change(TestDataPrimitive.Boolean.ToString());
        c.Find("[data-testid=tdx-value]").GetAttribute("role").Should().Be("switch");
        c.Find("[data-testid=tdx-value]").Change(true);
        c.Find("[data-testid=tdx-type]").Change(TestDataPrimitive.Null.ToString());
        c.Find("[data-testid=tdx-value]").GetAttribute("data-null").Should().Be("true");
    }

    [Fact]
    public void Key_value_rows_can_be_added_duplicated_and_removed_and_duplicate_keys_are_refused()
    {
        var c = Editor();
        Choose(c, TestDataResourceKind.KeyValue);
        c.Find("[data-testid=tdx-row-name]").Input("age");
        c.Find("[data-testid=tdx-row-duplicate]").Click();
        c.FindAll("[data-testid=tdx-row]").Should().HaveCount(2);
        c.FindAll("[data-testid=tdx-row-name]")[1].Input("age");
        c.FindAll("[data-testid=tdx-inline-error]").Select(e => e.TextContent).Should().Contain(t => t.Contains("Duplicate key"));
        c.FindAll("[data-testid=tdx-row-delete]")[1].Click();
        c.FindAll("[data-testid=tdx-row]").Should().HaveCount(1);
    }

    [Fact]
    public void Json_errors_name_the_line_and_the_structure_keeps_types()
    {
        var c = Editor();
        Choose(c, TestDataResourceKind.Json);
        c.Find("[data-testid=tdx-json]").Input("{\n \"a\": 1,\n}");
        c.Find("[data-testid=tdx-json-error]").TextContent.Should().Contain("line");
        c.Find("[data-testid=tdx-json]").Input("""{"children":[{"age":7,"active":false,"note":null}]}""");
        c.Find("[data-testid=tdx-json-ok]").Should().NotBeNull();
        c.FindAll("[data-testid=tdx-tree-leaf]").Select(l => l.GetAttribute("data-type")).Should().Contain(["Number", "False", "Null"]);
    }

    [Fact]
    public async Task GraphQl_variables_use_the_discovered_operation_and_report_missing_required_variables()
    {
        _api.Operations =
        [
            new(Guid.NewGuid(), "graphqlschema:schema.graphql", "schema", "GraphQlSchema",
                new ContractOperation("mutation registerChild", "mutation", null, ["input"]) { Detailed = true, ParameterDetails = [new("input", "argument", true, "ChildInput!")] },
                [new("ChildInput", "input_object", [new("name", "String!", true), new("age", "Int!", true)])]),
        ];
        var c = Editor();
        Choose(c, TestDataResourceKind.GraphQlVariables);
        c.WaitForAssertion(() => c.Find("[data-testid=tdx-operation]"));
        c.Find("[data-testid=tdx-operation]").Change("graphqlschema:schema.graphql|mutation registerChild");
        c.Find("[data-testid=tdx-variable-defs]").TextContent.Should().Contain("input: ChildInput!");
        c.Find("[data-testid=tdx-template]").Click();
        c.Find("[data-testid=tdx-json]").GetAttribute("value").Should().Contain("\"age\": 0").And.Contain("\"name\": \"\"");
        c.Find("[data-testid=tdx-json]").Input("{}");
        c.FindAll("[data-testid=tdx-inline-error]").Select(e => e.TextContent).Should().Contain(t => t.Contains("Missing required variable $input"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Rest_templates_refuse_an_authorization_header()
    {
        var c = Editor();
        Choose(c, TestDataResourceKind.RestRequest);
        c.Find("[data-testid=tdx-path]").Input("/children");
        c.Find("[data-testid=tdx-add-row]").Click();
        c.Find("[data-testid=tdx-row-name]").Input("Authorization");
        c.Find("[data-testid=tdx-row-in]").Change("header");
        c.Find("[data-testid=tdx-row-value]").Input("Bearer abcdefghijklmnop");
        c.Find("[data-testid=tdx-validate]").Click();
        c.WaitForAssertion(() => c.Find("[data-testid=tdx-tier][data-tier='Sensitive data']").GetAttribute("data-state").Should().Be("Failed"));
        c.Find("[data-testid=tdx-save]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Editing_starts_at_configure_keeps_the_name_and_saves_a_version_from_the_loaded_one()
    {
        var existing = new TestDataResourceView
        {
            Alias = "child-age", Version = 3, Content = new() { Kind = TestDataResourceKind.SingleValue, Value = new("", TestDataPrimitive.Int32, "7") },
        };
        var c = Editor(existing);
        c.Find("[data-testid=tdx]").GetAttribute("data-step").Should().Be("2");
        c.Find("[data-testid=tdx-alias]").HasAttribute("disabled").Should().BeTrue();
        c.Find("[data-testid=tdx-value]").GetAttribute("value").Should().Be("7");
        c.Find("[data-testid=tdx-value]").Input("8");
        c.Find("[data-testid=tdx-validate]").Click();
        c.WaitForAssertion(() => c.Find("[data-testid=tdx-save]").TextContent.Should().Be("Save new version"));
        c.Find("[data-testid=tdx-save]").Click();
        c.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        _api.Saved[0].Alias.Should().Be("child-age");
        _api.Saved[0].Draft.ExpectedVersion.Should().Be(3, "a concurrent edit is detected by the backend");
    }
}
