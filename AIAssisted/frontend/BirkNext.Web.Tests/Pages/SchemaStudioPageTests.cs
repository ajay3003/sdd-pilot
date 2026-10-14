using BirkNext.Web.Pages;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public sealed class SchemaStudioPageTests : BunitContext
{
    [Fact]
    public void EmptyStateOffersGenericXsdAndPackageImport()
    {
        Services.AddSingleton(new SchemaStudioService());
        var page = Render<SchemaStudio>();

        Assert.Contains("Schema Studio", page.Markup);
        Assert.Contains("Choose one or more .xsd files", page.Markup);
        Assert.Contains("Choose an XSD ZIP package", page.Markup);
        Assert.Contains("No schemas imported", page.Markup);
        Assert.DoesNotContain("Skolenærvær", page.Markup);
    }

    [Fact]
    public void ExistingSessionPackageShowsOverviewAndDistinctSchemaViews()
    {
        var service = new SchemaStudioService();
        service.Import(new Dictionary<string, string> { ["generic.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"root\" type=\"xs:string\"/></xs:schema>" }, "generic.xsd");
        Services.AddSingleton(service);

        var page = Render<SchemaStudio>();

        Assert.Contains("generic", page.Markup);
        Assert.Contains("Overview", page.Markup);
        Assert.Contains("Linked XSDs", page.Markup);
        Assert.Contains("Effective Schema", page.Markup);
        Assert.Contains("Generated Tests", page.Markup);
    }

    [Fact]
    public void CreateWizardAndStructuredDesignerSaveASeparateVersion()
    {
        var service = new SchemaStudioService();
        Services.AddSingleton(service);
        var page = Render<SchemaStudio>();

        page.Find("button.ss-action").Click();
        page.FindAll("button").Single(b => b.TextContent.Contains("Create schema and open Designer", StringComparison.Ordinal)).Click();
        Assert.Contains("Schema Designer", page.Markup);
        page.Find(".ss-create input").Change("RecordId");
        page.FindAll("button").Single(b => b.TextContent.Contains("Add child element", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(b => b.TextContent.Contains("Validate and save new version", StringComparison.Ordinal)).Click();

        Assert.Equal(2, service.Packages.Count);
        Assert.Contains(service.Packages[0].Files[0].Declarations.SelectMany(d => d.Children), e => e.Name == "RecordId");
        Assert.All(service.Packages, p => Assert.Equal("Valid", p.Validation));
    }

    [Fact]
    public void EffectiveSchemaViewShowsInheritedAndCrossFileProvenance()
    {
        const string xs = "http://www.w3.org/2001/XMLSchema";
        var service = new SchemaStudioService();
        service.Import(new Dictionary<string, string>
        {
            ["main.xsd"] = $"<xs:schema xmlns:xs=\"{xs}\" xmlns:m=\"urn:msg\" targetNamespace=\"urn:main\" xmlns=\"urn:main\"><xs:import namespace=\"urn:msg\" schemaLocation=\"message.xsd\"/><xs:element name=\"Report\" type=\"ReportType\"/><xs:complexType name=\"ReportType\"><xs:complexContent><xs:extension base=\"m:MessageType\"><xs:sequence><xs:element name=\"Period\" type=\"xs:string\"/></xs:sequence></xs:extension></xs:complexContent></xs:complexType></xs:schema>",
            ["message.xsd"] = $"<xs:schema xmlns:xs=\"{xs}\" targetNamespace=\"urn:msg\" xmlns=\"urn:msg\"><xs:complexType name=\"MessageType\"><xs:sequence><xs:element name=\"Id\" type=\"xs:string\"/></xs:sequence></xs:complexType></xs:schema>"
        }, "generic linked contract");
        Services.AddSingleton(service);
        var page = Render<SchemaStudio>();

        page.FindAll(".ss-tabs button").Single(b => b.TextContent == "Effective Schema").Click();

        Assert.Contains("Id", page.Markup);
        Assert.Contains("inherited from MessageType", page.Markup);
        Assert.Contains("message.xsd", page.Markup);
        Assert.Contains("Period", page.Markup);
    }

    [Fact]
    public void DesignerAddsChildInsideImportedBaseExtensionAndSavesValidVersion()
    {
        const string xs = "http://www.w3.org/2001/XMLSchema";
        var service = new SchemaStudioService();
        service.Import(new Dictionary<string, string>
        {
            ["main.xsd"] = $"<xs:schema xmlns:xs=\"{xs}\" xmlns:c=\"urn:common\" xmlns=\"urn:main\" targetNamespace=\"urn:main\"><xs:import namespace=\"urn:common\" schemaLocation=\"common.xsd\"/><xs:element name=\"Root\" type=\"LocalType\"/><xs:complexType name=\"LocalType\"><xs:complexContent><xs:extension base=\"c:BaseType\"><xs:sequence><xs:element name=\"Local\" type=\"xs:string\"/></xs:sequence></xs:extension></xs:complexContent></xs:complexType></xs:schema>",
            ["common.xsd"] = $"<xs:schema xmlns:xs=\"{xs}\" xmlns=\"urn:common\" targetNamespace=\"urn:common\"><xs:complexType name=\"BaseType\"><xs:sequence><xs:element name=\"Base\" type=\"xs:string\"/></xs:sequence></xs:complexType></xs:schema>"
        }, "linked");
        Services.AddSingleton(service);
        var page = Render<SchemaStudio>();

        page.FindAll(".ss-tabs button").Single(b => b.TextContent == "Designer").Click();
        page.FindAll("button").Single(b => b.TextContent.Contains("Add child element", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(b => b.TextContent.Contains("Validate and save new version", StringComparison.Ordinal)).Click();

        Assert.Equal(2, service.Packages.Count);
        Assert.Equal("Valid", service.Packages[0].Validation);
        Assert.Contains(Assert.Single(service.Packages[0].EffectiveSchema).Children, member => member.Name == "NewElement");
    }
}
