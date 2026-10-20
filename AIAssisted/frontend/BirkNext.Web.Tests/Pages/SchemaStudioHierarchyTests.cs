using BirkNext.Web.Pages;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Schema Studio second pass: the active package comes first and setup is secondary; structure is reachable from Understand and a library
/// XSD without a global root element is explained (never blank); one provenance disclosure; a compact switcher only for several packages.
/// </summary>
public sealed class SchemaStudioHierarchyTests : BunitContext
{
    private const string Rooted = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:demo:report" xmlns="urn:demo:report" elementFormDefault="qualified">
          <xs:element name="Report"><xs:complexType><xs:sequence>
            <xs:element name="Header"><xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string"/>
              <xs:element name="SentAt" type="xs:dateTime"/>
            </xs:sequence></xs:complexType></xs:element>
            <xs:element name="Absence" type="xs:int" minOccurs="0"/>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private const string Library = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="https://example.test/lib.v1" xmlns="https://example.test/lib.v1">
          <xs:simpleType name="CodeType"><xs:restriction base="xs:string"><xs:maxLength value="10"/></xs:restriction></xs:simpleType>
          <xs:complexType name="PartyType"><xs:sequence><xs:element name="Name" type="xs:string"/><xs:element name="Code" type="CodeType"/></xs:sequence></xs:complexType>
        </xs:schema>
        """;

    private SchemaStudioService Studio(params (string File, string Text)[] packages)
    {
        var service = new SchemaStudioService();
        foreach (var (file, text) in packages) service.Import(new Dictionary<string, string> { [file] = text }, Path.GetFileNameWithoutExtension(file));
        Services.AddSingleton(service);
        JSInterop.Mode = JSRuntimeMode.Loose;
        return service;
    }

    private static void OpenView(IRenderedComponent<SchemaStudio> page, string view)
    {
        page.FindAll("[data-testid=ss-group]").Single(b => b.TextContent == SchemaStudioNavigation.GroupOf(view).Name).Click();
        page.FindAll("[data-testid=ss-view]").Single(b => b.GetAttribute("data-view") == view).Click();
    }

    private static string Current(IRenderedComponent<SchemaStudio> page) => page.Find("[data-testid=ss-view][aria-current=page]").GetAttribute("data-view")!;

    [Fact]
    public void TheActivePackageComesFirst_AndSetupSavedAnalysesAndProvenanceAreSecondary()
    {
        Studio(("report.xsd", Rooted));
        var page = Render<SchemaStudio>();
        var markup = page.Markup;

        markup.IndexOf("data-testid=\"ss-package-header\"", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("data-testid=\"ss-saved-analyses\"", StringComparison.Ordinal));
        page.FindAll("[data-testid=ss-source-setup], [data-testid=ss-import-setup]").Should().BeEmpty("source and import open on request while a package is active");
        page.Find("[data-testid=ss-saved-analyses]").TagName.Should().Be("DETAILS");
        page.Find("[data-testid=ss-saved-analyses]").HasAttribute("open").Should().BeFalse();
        page.Find("[data-testid=ss-provenance]").HasAttribute("open").Should().BeFalse("snapshot, fingerprints and paths stay behind Source provenance");
        page.FindAll("h2").Count(h => h.TextContent.Contains("report", StringComparison.Ordinal)).Should().Be(1, "one current-package identity");
        markup.Should().NotContain("Schema packages and versions");
        page.FindAll("[data-testid=ss-package-switcher]").Should().BeEmpty("one package needs no package list");
        page.Find("[data-testid=ss-validity]").TextContent.Should().Be("Valid");
        page.Find("[data-testid=ss-validity-scope]").TextContent.Should().Be("XSD validation only");
        page.Find("[data-testid=ss-package-actions]").QuerySelectorAll(".ss-action").Select(a => a.TextContent).Should().Equal(["Save analysis"], "one primary action");
        page.Find("[data-testid=ss-export]").QuerySelectorAll("a").Select(a => a.GetAttribute("data-testid")).Should().Equal("ss-export-xsd", "ss-export-zip");

        page.Find("[data-testid=ss-change-source]").Click();
        page.Find("[data-testid=ss-source-setup] h2").TextContent.Should().Be("Change source");
        page.Find("[data-testid=ss-import-another]").Click();
        page.FindAll("[data-testid=ss-source-setup]").Should().BeEmpty();
        page.Find("[data-testid=ss-import-setup] h2").TextContent.Should().Be("Import another schema package");
    }

    [Fact]
    public void WithoutAPackage_SetupIsThePage()
    {
        Studio();
        var page = Render<SchemaStudio>();
        page.Find("[data-testid=ss-start] h2").TextContent.Should().Be("Start with schema evidence");
        page.Find("[data-testid=ss-source-setup] h2").TextContent.Should().Be("Use Source Analysis XSDs");
        page.Find("[data-testid=ss-import-setup]").Should().NotBeNull();
        page.Find("[data-testid=ss-saved-analyses]").TagName.Should().Be("SECTION");
        page.FindAll("[data-testid=ss-package-header]").Should().BeEmpty();
    }

    [Fact]
    public void ARootedSchema_ShowsItsActualHierarchy_InBusinessStructure_AndDeclarationsInTheSchemaTree()
    {
        Studio(("report.xsd", Rooted));
        var page = Render<SchemaStudio>();
        page.Find("[data-testid=ss-shortcut-business]").TextContent.Should().Contain("Understand structure");
        page.Find("[data-testid=ss-shortcut-business]").Click();
        Current(page).Should().Be("Forretning");
        var business = page.Find("[data-testid=sr-business]").TextContent;
        business.Should().Contain("Report").And.Contain("Header").And.Contain("Id").And.Contain("SentAt").And.Contain("Absence");
        page.FindAll("[data-testid=sr-no-root]").Should().BeEmpty();

        page.Find("[data-testid=ss-structure-technical]").Click();
        page.Find("[data-testid=ss-structure-technical]").GetAttribute("aria-pressed").Should().Be("true");
        page.Find("[data-testid=sr-structure]").TextContent.Should().Contain("Header").And.Contain("xs:dateTime");

        OpenView(page, "Schema Tree");
        page.Find("[data-testid=ss-schema-tree]").TextContent.Should().Contain("Report");
        OpenView(page, "Effective Schema");
        page.Find("[data-testid=ss-effective-tree]").TextContent.Should().Contain("Report");
        page.FindAll("[data-testid=ss-effective-no-root]").Should().BeEmpty();
    }

    [Fact]
    public void ALibrarySchemaWithoutARoot_IsExplained_NotBlank_AndLeadsToDeclarationsAndTypes()
    {
        Studio(("lib.xsd", Library));
        var page = Render<SchemaStudio>();
        page.Find("[data-testid=ss-validity]").TextContent.Should().Be("Valid", "no root element never makes an XSD invalid");
        page.Find("[data-testid=ss-root-element]").TextContent.Should().Be("None declared");
        page.Find("[data-testid=ss-root-xsd]").TextContent.Should().Be("Not applicable · no root element declared");

        OpenView(page, "Forretning");
        var empty = page.Find("[data-testid=sr-no-root]").TextContent;
        empty.Should().Contain("No global root element is declared in this XSD").And.Contain("2 global declarations available").And.Contain("does not make the XSD invalid");
        page.FindAll("[data-testid=ss-structure-mode]").Should().BeEmpty("there is no structure to switch");
        page.Find("[data-testid=sr-no-root-declarations]").Click();
        Current(page).Should().Be("Schema Tree");
        page.Find("[data-testid=ss-schema-tree]").TextContent.Should().Contain("PartyType").And.Contain("CodeType");

        OpenView(page, "Forretning");
        page.Find("[data-testid=sr-no-root-types]").Click();
        Current(page).Should().Be("Types");
        page.Find("[data-testid=ss-types]").TextContent.Should().Contain("CodeType");

        OpenView(page, "Effective Schema");
        page.Find("[data-testid=ss-effective-no-root]").TextContent.Should().Contain("No global root element is declared");

        OpenView(page, "Schema dependencies");
        page.Find("[data-testid=ss-dependencies-single]").TextContent.Should().Contain("No cross-file dependency graph is needed");
        page.FindAll("[data-testid=ss-linked-xsds]").Should().BeEmpty("a single XSD gets a compact summary, not an empty graph");
        OpenView(page, "References");
        page.Find("[data-testid=ss-references-empty]").TextContent.Should().Contain("No cross-file references");
    }

    [Fact]
    public void TheFormerStrukturLink_OpensBusinessStructureWithTypesAndOrigin()
    {
        Studio(("report.xsd", Rooted));
        Services.GetRequiredService<NavigationManager>().NavigateTo("schema-studio?view=struktur");
        var page = Render<SchemaStudio>();
        Current(page).Should().Be("Forretning");
        page.Find("[data-testid=ss-structure-technical]").GetAttribute("aria-pressed").Should().Be("true");
        page.Find("[data-testid=sr-structure]").Should().NotBeNull();
    }

    [Fact]
    public void SeveralPackages_GetACompactSwitcher_ThatChangesTheActiveReview()
    {
        Studio(("report.xsd", Rooted), ("lib.xsd", Library));
        var page = Render<SchemaStudio>();
        var switcher = page.Find("[data-testid=ss-package-switcher]");
        switcher.TextContent.Should().Contain("2 packages in session");
        var select = page.Find("[data-testid=ss-package-select]");
        var other = select.QuerySelectorAll("option").First(o => o.GetAttribute("value") != select.GetAttribute("value")).GetAttribute("value")!;
        select.Change(other);
        page.Find("[data-testid=ss-package-select]").GetAttribute("value").Should().Be(other);
        page.Find("#ss-current-title").TextContent.Should().NotBeNullOrWhiteSpace();
    }
}
