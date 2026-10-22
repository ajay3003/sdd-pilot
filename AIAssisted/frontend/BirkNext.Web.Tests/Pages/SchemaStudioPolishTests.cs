using BirkNext.Schemas;
using BirkNext.Web.Pages;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Schema Studio polish: plain language first with the exact XSD term second, compact tables with full paths as secondary detail, summary-first
/// rules and comparisons, progressive Raw XSD controls, validation history that is not dominated by runs without test cases, and English-only UI
/// copy. Generic fixtures — a reusable library XSD (no top-level element) and a rooted message XSD. Presentation only; schema semantics unchanged.
/// </summary>
public sealed class SchemaStudioPolishTests : BunitContext
{
    private const string LibraryPath = "Package/src/Schemas/lib.v1.1.0.xsd";
    private const string LongDocumentation = "Identifier of a person registered without a national identity number. The value is assigned by the issuing authority and is unique across all registers; see the specification for details on how it is formatted and validated.";
    private static readonly string Library = $$"""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="https://example.org/lib.v1.1.0" xmlns="https://example.org/lib.v1.1.0" version="1.1.0">
          <xs:simpleType name="DufNumberType"><xs:annotation><xs:documentation>{{LongDocumentation}}</xs:documentation></xs:annotation>
            <xs:restriction base="xs:string"><xs:length value="12"/><xs:pattern value="\d{12}"/></xs:restriction></xs:simpleType>
          <xs:complexType name="DistrictInfoType"><xs:sequence>
            <xs:element name="DistrictNumber"><xs:simpleType><xs:restriction base="xs:string"><xs:minLength value="2"/></xs:restriction></xs:simpleType></xs:element>
            <xs:element name="DistrictName" type="xs:string"/>
          </xs:sequence></xs:complexType>
        </xs:schema>
        """;
    private const string Rooted = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:report" xmlns="urn:example:report" elementFormDefault="qualified">
          <xs:element name="Report"><xs:complexType><xs:sequence>
            <xs:element name="Id" type="xs:string"/>
            <xs:element name="Count"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="0"/><xs:maxInclusive value="9"/></xs:restriction></xs:simpleType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    public SchemaStudioPolishTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private SchemaStudioService Studio(params (string Name, string Text)[] files)
    {
        var service = new SchemaStudioService();
        if (files.Length > 0) service.Import(files.ToDictionary(f => f.Name, f => f.Text), files[0].Name);
        Services.AddSingleton(service);
        return service;
    }

    private static void OpenView(IRenderedComponent<SchemaStudio> page, string view)
    {
        page.FindAll("[data-testid=ss-group]").Single(b => b.TextContent == SchemaStudioNavigation.GroupOf(view).Name).Click();
        page.FindAll("[data-testid=ss-view]").Single(b => b.GetAttribute("data-view") == view).Click();
    }

    [Fact]
    public void RootedOverview_NamesTheTopLevelMessage_AndOffersGeneratedTests()
    {
        Studio(("report.xsd", Rooted));
        var page = Render<SchemaStudio>();
        page.Find("[data-testid=ss-root-element]").TextContent.Should().Be("Report");
        page.Find("[data-testid=ss-root-xsd]").TextContent.Should().StartWith("report.xsd");
        page.Find("[data-testid=ss-overview-status]").TextContent.Should().Be("XSD package structurally valid");
        page.Find("[data-testid=ss-meaning]").TextContent.Should().Contain("defines the message Report");
        page.Find("[data-testid=ss-shortcut-tests]").TextContent.Should().Contain("Run generated tests");
        page.Find("[data-testid=ss-overview-dependencies]").TextContent.Should().StartWith("None", "no implementation-style \"0 links\" wording");

        OpenView(page, "Forretning");
        page.Find("[data-testid=sr-business] h4").TextContent.Should().Be("Report", "a rooted schema shows its message hierarchy");
        OpenView(page, "Generated Tests");
        page.Find("[data-testid=ss-run-tests]").HasAttribute("disabled").Should().BeFalse("candidate cases exist");
    }

    [Fact]
    public void Types_AreCompact_PlainLanguageFirst_WithTheXsdBaseAndSourceAsTechnicalDetail()
    {
        Studio((LibraryPath, Library));
        var page = Render<SchemaStudio>();
        OpenView(page, "Types");
        page.FindAll("[data-testid=ss-types] thead th").Select(th => th.TextContent).Should().Equal("Type", "Meaning", "Base", "Rules", "Usage");
        var duf = page.Find("[data-testid=ss-type-row][data-name=DufNumberType]");
        duf.Children[1].TextContent.Should().Be("Text type");
        duf.Children[2].TextContent.Should().Be("Text");
        duf.Children[3].TextContent.Should().Be("12 chars · 12-digit pattern");
        duf.Children[4].TextContent.Should().Be("Not used in this package");
        var preview = duf.QuerySelector(".ss-doc-preview")!.TextContent;
        preview.Should().EndWith("…", "long documentation is a short preview in the row").And.HaveLength(91);
        duf.QuerySelector("details:not([data-testid])")!.HasAttribute("open").Should().BeFalse("the full documentation is behind Show documentation");
        duf.TextContent.Should().Contain("Show documentation");
        var technical = duf.QuerySelector("[data-testid=ss-type-technical]")!;
        technical.HasAttribute("open").Should().BeFalse();
        technical.TextContent.Should().Contain("Simple type").And.Contain("xs:string").And.Contain(LibraryPath, "the full source path is secondary detail");
        page.Find("[data-testid=ss-type-row][data-name=DistrictInfoType]").Children[1].TextContent.Should().Be("Reusable object");
    }

    [Fact]
    public void SchemaTree_UsesPlainLanguageNodes_ShortSourceNames_AndKeepsTechnicalDetailsOnDemand()
    {
        Studio((LibraryPath, Library));
        var page = Render<SchemaStudio>();
        OpenView(page, "Schema Tree");
        var tree = page.Find("[data-testid=ss-schema-tree]");
        tree.QuerySelector("summary strong")!.TextContent.Should().Be("lib.v1.1.0.xsd", "the file is named by its short name");
        tree.QuerySelector("summary strong")!.GetAttribute("title").Should().Be(LibraryPath);
        var district = page.Find("[data-testid=ss-tree-node][data-name=DistrictInfoType]");
        district.QuerySelector("[data-testid=ss-tree-meaning]")!.TextContent.Should().Be("Reusable object");
        district.QuerySelector(".ss-kind")!.TextContent.Should().Be("Complex type");
        district.QuerySelector(".ss-tree-fields")!.TextContent.Should().Contain("DistrictNumber").And.Contain("Text · Required once").And.Contain("Minimum length 2");
        district.QuerySelector("summary")!.TextContent.Should().NotContain(LibraryPath, "paths are not repeated on every node");
        district.QuerySelector("[data-testid=ss-tree-technical]")!.TextContent.Should().Contain("Full path").And.Contain("DistrictNumber 1..1");
    }

    [Fact]
    public void Namespaces_WrapSafely_WithConciseFileNames_AndFullPathsSecondary()
    {
        Studio((LibraryPath, Library));
        var page = Render<SchemaStudio>();
        OpenView(page, "Namespaces");
        page.Find("[data-testid=ss-namespace-uri]").TextContent.Should().Be("https://example.org/lib.v1.1.0");
        page.Find("[data-testid=ss-namespace-uri]").ClassList.Should().Contain("ss-wrap", "long namespaces wrap instead of widening the page");
        page.Find("[data-testid=ss-namespace-files] span").TextContent.Should().Be("lib.v1.1.0.xsd");
        page.Find("[data-testid=ss-namespace-files] span").GetAttribute("title").Should().Be(LibraryPath);
        page.Find("[data-testid=ss-namespace]").TextContent.Should().Contain("View details");
    }

    [Fact]
    public void RawXsd_IsMarkedReadOnlyTechnical_CodeScrollsLocally_FullHeightIsAViewOption()
    {
        Studio((LibraryPath, Library));
        var page = Render<SchemaStudio>();
        OpenView(page, "Raw XSD");
        page.Find("[data-testid=ss-raw-intro]").TextContent.Should().Contain("Read-only").And.Contain("technical inspection");
        page.Find("[data-testid=ss-raw]").ClassList.Should().Contain("ss-code-scroll");
        page.Find("[data-testid=ss-raw]").GetAttribute("aria-label").Should().Contain("read-only");
        page.Find("[data-testid=ss-raw-options-toggle]").GetAttribute("aria-expanded").Should().Be("false", "Show full height is secondary");
        page.Find("[data-testid=ss-raw-expand]").Change(true);
        page.Find("[data-testid=ss-raw]").ClassList.Should().Contain("ss-raw-expanded");
        page.Find("[data-testid=ss-raw-copy]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "navigator.clipboard.writeText" && ((string)i.Arguments[0]!).Contains("DufNumberType"));
    }

    [Fact]
    public void Compare_IsSummaryFirst_WithDetailedChangesCollapsed()
    {
        var service = Studio(("report.xsd", Rooted));
        service.Import(new Dictionary<string, string> { ["report.xsd"] = Rooted.Replace("""<xs:element name="Id" type="xs:string"/>""", """<xs:element name="Id" type="xs:string"/><xs:element name="Extra" type="xs:string"/>""") }, "report.xsd");
        var page = Render<SchemaStudio>();
        OpenView(page, "Compare");
        page.Find("[data-testid=ss-compare-select]").Change(service.Packages.First(p => p.Id != service.Packages[0].Id).Id);
        page.FindAll("[data-testid=ss-compare-summary] dt").Select(dt => dt.TextContent).Should()
            .Equal("Definitions added", "Definitions removed", "Rules changed", "Namespaces changed", "Breaking changes / needs review");
        if (page.FindAll("[data-testid=ss-compare-details-toggle]").Count > 0)
            page.Find("[data-testid=ss-compare-details-toggle]").GetAttribute("aria-expanded").Should().Be("false", "no raw diff by default");
    }

    [Fact]
    public void ValidationHistory_PutsMeaningfulRunsFirst_AndGroupsRunsWithoutTestCases()
    {
        var service = Studio(("report.xsd", Rooted));
        var package = service.Packages[0];
        var history = new SchemaTestRunHistory();
        for (var i = 0; i < 4; i++) history.Add(package, "Generic valid", 1, []);
        history.Add(package, "Generic valid", 1, SchemaStudioService.RunGeneratedTests(package, new Dictionary<string, string>(), 5));
        Services.AddSingleton(history);
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");
        page.Find("[data-testid=ss-history-meaningful]").TextContent.Should().Be("1");
        page.Find("[data-testid=ss-history-noop]").TextContent.Should().Be("4");
        page.FindAll("[data-testid=ss-history-run]").Should().ContainSingle("runs without test cases are counted, not listed as primary history");
        page.Find("[data-testid=ss-history-noop-note]").TextContent.Should().Contain("4 earlier runs had no test cases");
    }

    [Fact]
    public void ALibraryPackage_OffersNoRun_AndSavesNoValidationRun()
    {
        Studio((LibraryPath, Library));
        var history = new SchemaTestRunHistory();
        Services.AddSingleton(history);
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");
        page.FindAll("[data-testid=ss-run-tests]").Should().BeEmpty();
        page.Find("[data-testid=ss-tests-empty]").TextContent.Should().Contain("reusable schema library");
        page.FindAll("[data-testid=ss-history]").Should().BeEmpty();
        history.Runs.Should().BeEmpty("nothing ran, so nothing is saved");
        OpenView(page, "Generation Profile");
        page.Find("[data-testid=ss-generate-library]").TextContent.Should().Contain("no top-level message element");
        page.FindAll("select[aria-label='Generated XML root']").Should().BeEmpty("no empty root selector for a library");
    }

    [Fact]
    public void Search_UsesPlainPlaceholder_AndNamesResultCategories()
    {
        Studio((LibraryPath, Library));
        var page = Render<SchemaStudio>();
        page.Find("[data-testid=ss-search]").GetAttribute("placeholder").Should().Be("Search elements, types, fields, namespaces or files");
        page.Find("[data-testid=ss-search]").Input("Duf");
        page.Find("[data-testid=ss-search-results] .ss-search-category").TextContent.Should().Be("Type");
    }

    [Theory]
    [InlineData(LibraryPath)]
    [InlineData("report.xsd")]
    public void EveryView_UsesEnglishUiCopy(string file)
    {
        Studio((file, file == "report.xsd" ? Rooted : Library));
        var page = Render<SchemaStudio>();
        string[] norwegian = ["Løst", "løst", "Uløst", "uløst", "Kilde", "kilde", "Gyldig", "gyldig", "Mangler", "mangler", "Delvis", "delvis", "Ikke vurdert", "ikke vurdert",
            "Obligatorisk", "Valgfritt", "Tekst", "Felt ", "Regler", "Forekomst", "Rotelement", "Versjon", "Utvid alle", "Skjul alle", "Tillatte verdier"];
        foreach (var view in SchemaStudioNavigation.AllViews)
        {
            OpenView(page, view);
            var text = page.Find("[data-testid=ss-workspace]").TextContent;
            foreach (var word in norwegian) text.Should().NotContain(word, $"{SchemaStudioNavigation.Label(view)} is English UI");
        }
    }
}
