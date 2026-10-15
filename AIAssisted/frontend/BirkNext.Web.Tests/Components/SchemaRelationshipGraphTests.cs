using BirkNext.Web.Components;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BirkNext.Web.Tests.Components;

public sealed class SchemaRelationshipGraphTests : BunitContext
{
    private const string Common = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common" xmlns="urn:common">
          <xs:simpleType name="CommonId"><xs:restriction base="xs:string"><xs:minLength value="2"/></xs:restriction></xs:simpleType>
        </xs:schema>
        """;
    private const string Main = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common" targetNamespace="urn:main" xmlns="urn:main" elementFormDefault="qualified">
          <xs:import namespace="urn:common" schemaLocation="common.xsd"/>
          <xs:element name="Record"><xs:complexType><xs:sequence><xs:element name="Id" type="c:CommonId"/></xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    [Fact]
    public async Task GraphShowsResolvedFileAndTypeEdgesAndOpensSourceEvidenceByKeyboard()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string>
        {
            ["main.xsd"] = Main,
            ["common.xsd"] = Common
        }, "linked package");
        string? opened = null;

        var cut = Render<SchemaRelationshipGraph>(parameters => parameters
            .Add(component => component.Package, package)
            .Add(component => component.OnFileSelected, EventCallback.Factory.Create<string>(this, file => opened = file)));

        Assert.Contains("Relationship graph", cut.Markup);
        Assert.Contains("Imported", cut.Markup);
        Assert.Contains("CommonId", cut.Markup);
        Assert.Contains("Namespace: main.xsd to urn:main", cut.Markup);
        Assert.NotEmpty(package.References);
        var mainNode = cut.FindAll("g.ss-graph-node").First(node => node.GetAttribute("aria-label")!.Contains("main.xsd", StringComparison.Ordinal));
        await mainNode.KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        cut.Find(".ss-graph-details button").Click();
        Assert.Equal("main.xsd", opened);
    }

    [Fact]
    public void GraphLabelsUnresolvedImportsAndHasSearchFilterAndZoomControls()
    {
        const string unresolved = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:missing" xmlns="urn:missing">
              <xs:import namespace="urn:external-local" schemaLocation="not-in-package.xsd"/>
              <xs:element name="Record" type="xs:string"/>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["main.xsd"] = unresolved }, "unresolved");
        var cut = Render<SchemaRelationshipGraph>(parameters => parameters.Add(component => component.Package, package));

        Assert.Contains("Unresolved import", cut.Markup);
        Assert.Contains("not-in-package.xsd", cut.Markup);
        Assert.Contains("Search relationship graph", cut.Markup);
        Assert.Contains("Filter graph relationship type", cut.Markup);
        Assert.Contains("Graph zoom", cut.Markup);
    }
}
