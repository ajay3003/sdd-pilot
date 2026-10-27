using BirkNext.Schemas;
using BirkNext.Web.Pages;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Schema Studio overhaul: Business structure is a readable functional view and Effective structure the resolved technical one; the schema
/// tree is a chip-labelled explorer; Save is a narrow workspace action whose origin is explicit; empty states say why; Validate &amp; Test states
/// that it validates schema-derived local cases only. Generic fixtures, no project knowledge.
/// </summary>
public sealed class SchemaStudioOverhaulTests : BunitContext
{
    private const string Order = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:order" xmlns="urn:example:order" elementFormDefault="qualified">
          <xs:simpleType name="CodeType"><xs:restriction base="xs:string"><xs:maxLength value="4"/><xs:pattern value="[A-Z]+"/></xs:restriction></xs:simpleType>
          <xs:complexType name="LineType"><xs:sequence><xs:element name="Code" type="CodeType"/></xs:sequence></xs:complexType>
          <xs:element name="Order">
            <xs:complexType><xs:sequence>
              <xs:element name="OrderId" type="xs:string"><xs:annotation><xs:documentation>Unique order reference.</xs:documentation></xs:annotation></xs:element>
              <xs:element name="Line" type="LineType" maxOccurs="unbounded"/>
              <xs:element name="Country" minOccurs="0"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="NO"/><xs:enumeration value="SE"/></xs:restriction></xs:simpleType></xs:element>
            </xs:sequence></xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private IRenderedComponent<SchemaStudio> Open(out SchemaPackage package)
    {
        var service = new SchemaStudioService();
        package = service.Import(new Dictionary<string, string> { ["order.xsd"] = Order }, "order.xsd");
        Services.AddSingleton(service);
        return Render<SchemaStudio>();
    }

    private static void OpenView(IRenderedComponent<SchemaStudio> page, string view)
    {
        page.FindAll("[data-testid=ss-group]").Single(b => b.TextContent == SchemaStudioNavigation.GroupOf(view).Name).Click();
        page.FindAll("[data-testid=ss-view]").Single(b => b.GetAttribute("data-view") == view).Click();
    }

    [Fact]
    public void Business_structure_is_readable_sections_and_effective_structure_is_technical()
    {
        var page = Open(out _);
        OpenView(page, "Forretning");
        var business = page.Find("[data-testid=sr-business]");
        business.QuerySelectorAll(".sr-section-card").Should().NotBeEmpty("groups are section cards");
        business.QuerySelectorAll(".sr-field-row").Should().NotBeEmpty("fields are readable rows");
        business.QuerySelectorAll(".sr-chip-repeating").Select(c => c.TextContent).Should().Contain("Repeating", "Line repeats");
        business.QuerySelectorAll("[data-testid=sr-occurrence]").Select(c => c.TextContent).Should().Contain(["Required once", "Optional, max once"]);
        business.QuerySelector("[data-testid=sr-plain-rules]")!.TextContent.Should().NotBeNullOrWhiteSpace("key rules in plain language");
        business.TextContent.Should().NotContain("xs:", "no XSD syntax in the business view").And.NotContain("declared in");

        OpenView(page, "Effective Schema");
        var effective = page.Find("[data-testid=ss-effective-tree]");
        effective.QuerySelectorAll(".sr-section-card").Should().BeEmpty("the technical view is not the business layout");
        page.Find("[data-testid=ss-effective-show-types]").Should().NotBeNull("types, origin and exact occurrence are toggles here");
    }

    [Fact]
    public void Schema_tree_is_an_explorer_with_labelled_chips()
    {
        var page = Open(out _);
        OpenView(page, "Schema Tree");
        var code = page.Find("[data-testid=ss-tree-node][data-name=CodeType]");
        code.QuerySelector(".ss-kind")!.TextContent.Should().Be("Simple type");
        code.QuerySelectorAll(".ss-chip").Select(c => c.TextContent).Should().Contain(["Text type", "Pattern", "Length rule", "Used 1×"]);
        var line = page.Find("[data-testid=ss-tree-node][data-name=LineType]");
        line.QuerySelectorAll(".ss-chip").Select(c => c.TextContent).Should().Contain(["Complex type", "Reusable object"]);
        page.Find("[data-testid=ss-schema-tree]").QuerySelectorAll(".ss-def-card").Should().NotBeEmpty("definitions are styled cards, not bullets");

        page.Find("[data-testid=ss-tree-compact]").Change(true);
        page.Find("[data-testid=ss-schema-tree]").ClassList.Should().Contain("is-compact");
    }

    [Fact]
    public void Session_import_says_where_it_comes_from_and_save_is_named_for_what_it_does()
    {
        var page = Open(out _);
        page.Find("[data-testid=ss-evidence-origin-label]").TextContent.Should().Be("Session import");
        page.Find("[data-testid=ss-save]").TextContent.Should().Be(SchemaStudioVisuals.SaveLabel);
        page.Find("[data-testid=ss-save-help]").TextContent.Should().Contain("never changes XSD files");
        page.Markup.Should().NotContain("Save analysis");
    }

    [Fact]
    public void Validate_and_test_summary_states_scope_counts_and_mismatch_policy()
    {
        var page = Open(out var package);
        OpenView(page, "Generated Tests");
        page.Find("[data-testid=ss-test-scope]").TextContent.Should().StartWith("Schema-derived local cases.");
        page.Find("[data-testid=ss-overview-run]").TextContent.Should().Be($"0 of {package.GeneratedTests.Count}");
        page.Find("[data-testid=ss-overview-files]").TextContent.Should().Be("1");
        page.Find("[data-testid=ss-overview-policy]").TextContent.Should().Contain("The XSD is the oracle").And.Contain("Neither is a schema or application defect");
        page.Find("[data-testid=ss-test-overview]").TextContent.Should().NotContainEquivalentOf("runtime-valid").And.NotContainEquivalentOf("integration-valid");
    }

    [Fact]
    public void Visual_helpers_are_facts_only()
    {
        SchemaStudioVisuals.OccurrenceChips("1", "1").Select(c => c.Label).Should().Equal("Required");
        SchemaStudioVisuals.OccurrenceChips("0", "unbounded").Select(c => c.Label).Should().Equal("Optional", "Repeating");
        SchemaStudioVisuals.OccurrenceChips("1", "3").Select(c => c.Label).Should().Equal("Required", "Repeating · up to 3");
        SchemaStudioVisuals.FacetChips([new("minInclusive", "1")]).Select(c => c.Label).Should().Equal("Numeric bounds");
        SchemaStudioVisuals.FacetChips([]).Should().BeEmpty();
    }
}
