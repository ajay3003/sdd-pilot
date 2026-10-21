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
        page.Find("[data-testid=ss-saved-analyses-toggle]").TextContent.Should().Contain("Saved analyses (0)", "a compact disclosure with its count");
        page.Find("[data-testid=ss-saved-analyses-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=ss-provenance-toggle]").GetAttribute("aria-expanded").Should().Be("false", "snapshot, fingerprints and paths stay behind Source provenance");
        page.FindAll("h2").Count(h => h.TextContent.Contains("report", StringComparison.Ordinal)).Should().Be(1, "one current-package identity");
        markup.Should().NotContain("Schema packages and versions");
        page.FindAll("[data-testid=ss-package-switcher]").Should().BeEmpty("one package needs no package list");
        page.Find("[data-testid=ss-validity]").TextContent.Should().Be("Valid");
        page.Find("[data-testid=ss-validity-headline]").TextContent.Should().Be("Schema valid");
        page.Find("[data-testid=ss-validity-scope]").TextContent.Should().Be(SchemaStudioPresentation.ValidityScope, "the scope says the integration is not verified");
        page.Find("[data-testid=ss-source-kind]").TextContent.Should().Be("Source: imported XSD files", "the archive and identifiers are provenance, not the primary line");
        var actions = page.Find("[data-testid=ss-package-actions]");
        actions.QuerySelectorAll(".btn-primary").Select(a => a.TextContent).Should().Equal(["Save analysis"], "one primary action");
        actions.QuerySelectorAll(":scope > .btn-secondary").Select(a => a.TextContent).Should().Equal("Change source", "Import another package");
        page.Find("[data-testid=ss-more-toggle]").TextContent.Should().Contain("More");
        page.Find("[data-testid=ss-more-toggle]").GetAttribute("aria-expanded").Should().Be("false", "export and removal never compete with Save");
        page.Find("[data-testid=ss-more]").QuerySelectorAll("a").Select(a => a.GetAttribute("data-testid")).Should().Equal("ss-more-export-xsd", "ss-more-export-zip");
        page.Find("[data-testid=ss-more-remove]").TextContent.Should().Be("Remove from session");

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
        page.Find("[data-testid=ss-root-element]").TextContent.Should().Be("None");
        page.Find("[data-testid=ss-root-xsd]").TextContent.Should().StartWith("Not defined");
        page.Find("[data-testid=ss-overview-definitions]").TextContent.Should().StartWith("2");
        page.Find("[data-testid=ss-overview-dependencies]").TextContent.Should().StartWith("None");
        var meaning = page.Find("[data-testid=ss-meaning]");
        meaning.TextContent.Should().Contain(SchemaStudioPresentation.LibraryHeadline).And.Contain("not a complete message");
        meaning.QuerySelector("strong")!.TextContent.Should().Be(SchemaStudioPresentation.LibraryHeadline, "plain language first");
        meaning.QuerySelector(".ss-tech-term")!.TextContent.Should().Contain("no global root element", "the technical phrase is secondary");
        page.FindAll("[data-testid=ss-shortcut-tests]").Should().BeEmpty("no Run generated tests card when there is nothing to run");
        page.Find("[data-testid=ss-shortcut-fields]").TextContent.Should().Contain("Review reusable types");

        OpenView(page, "Forretning");
        var empty = page.Find("[data-testid=sr-no-root]").TextContent;
        empty.Should().Contain(SchemaStudioPresentation.LibraryStructureHeadline).And.Contain("2 reusable schema definitions").And.Contain("no global root element is declared");
        page.FindAll("[data-testid=ss-structure-mode]").Should().BeEmpty("there is no structure to switch");
        page.Find("[data-testid=sr-no-root-declarations]").Click();
        Current(page).Should().Be("Schema Tree");
        page.Find("[data-testid=ss-schema-tree]").TextContent.Should().Contain("PartyType").And.Contain("CodeType");

        OpenView(page, "Forretning");
        page.Find("[data-testid=sr-no-root-types]").Click();
        Current(page).Should().Be("Types");
        page.Find("[data-testid=ss-types]").TextContent.Should().Contain("CodeType");

        OpenView(page, "Effective Schema");
        page.Find("[data-testid=ss-effective-no-root]").TextContent.Should().Contain("No complete message structure").And.Contain("reusable definitions only");
        page.FindAll("[data-testid=ss-effective-toolbar]").Should().BeEmpty("no structure filters or expand controls without a structure");

        OpenView(page, "Felt og regler");
        page.Find("[data-testid=sr-no-root]").TextContent.Should().Contain(SchemaStudioPresentation.LibraryStructureHeadline, "one shared library explanation");

        OpenView(page, "Generated Tests");
        page.Find("[data-testid=ss-tests-empty]").TextContent.Should().Contain("No generated test cases are available").And.Contain("reusable schema library");
        page.FindAll("[data-testid=ss-run-tests]").Should().BeEmpty("no run action with zero cases");
        page.Find("[data-testid=ss-xml-generate]").HasAttribute("disabled").Should().BeTrue("a minimum example needs a top-level message element");
        page.FindAll("[data-testid=ss-xml-editor]").Should().BeEmpty("the XML editor opens on request");

        OpenView(page, "Schema dependencies");
        page.Find("[data-testid=ss-dependencies-single]").TextContent.Should().Contain("no dependency graph to show");
        page.FindAll("[data-testid=ss-linked-xsds]").Should().BeEmpty("a single XSD gets a compact summary, not an empty graph");
        OpenView(page, "References");
        page.Find("[data-testid=ss-references-empty]").TextContent.Should().Contain("Cross-file references: none").And.Contain("one XSD file");
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
