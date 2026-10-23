using BirkNext.Schemas;
using BirkNext.Web.Pages;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Schema Studio readable-first pass: plain-language meaning before XSD syntax in every view, a top status that says what is structurally
/// verified and what is not, compact namespaces and dependencies, type-aware designer and profile controls, an explicit generated-test run
/// scope, the profile in use shown where tests run, and package search that opens the matching view with the term applied. Generic
/// fixtures plus the repository's real Skolenærvær XSD. Presentation only; XSD parsing and validation are unchanged.
/// </summary>
public sealed class SchemaStudioReadableFirstTests : BunitContext
{
    private const string Main = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:a="urn:x:shared:types:v1" xmlns:b="urn:x:codes:v2" targetNamespace="urn:x:applikasjonskvittering:v2" xmlns="urn:x:applikasjonskvittering:v2" elementFormDefault="qualified">
          <xs:import namespace="urn:x:shared:types:v1" schemaLocation="shared.types.v1.xsd"/>
          <xs:import namespace="urn:x:codes:v2" schemaLocation="codes.v2.xsd"/>
          <xs:element name="Kvittering"><xs:complexType><xs:sequence>
            <xs:element name="MeldingId" type="a:MeldingIdType"/>
            <xs:element name="Versjon" type="a:VersjonType"/>
            <xs:element name="Resultat" type="b:ResultatType" minOccurs="0"/>
            <xs:element name="Antall" type="xs:integer"/>
            <xs:element name="Dato" type="xs:date"/>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string Shared = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:x:shared:types:v1" xmlns="urn:x:shared:types:v1">
          <xs:simpleType name="MeldingIdType"><xs:restriction base="xs:string"><xs:pattern value="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"/></xs:restriction></xs:simpleType>
          <xs:simpleType name="VersjonType"><xs:restriction base="xs:string"><xs:minLength value="1"/><xs:pattern value="[A-Z]{2}\d+"/></xs:restriction></xs:simpleType>
        </xs:schema>
        """;
    private const string Codes = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:x:codes:v2" xmlns="urn:x:codes:v2">
          <xs:simpleType name="ResultatType"><xs:restriction base="xs:string">
            <xs:enumeration value="A"/><xs:enumeration value="B"/><xs:enumeration value="C"/><xs:enumeration value="D"/><xs:enumeration value="E"/>
            <xs:enumeration value="F"/><xs:enumeration value="G"/><xs:enumeration value="H"/><xs:enumeration value="I"/>
          </xs:restriction></xs:simpleType>
        </xs:schema>
        """;
    private const string LibraryOnly = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:simpleType name="CodeType"><xs:restriction base="xs:string"/></xs:simpleType></xs:schema>""";

    public SchemaStudioReadableFirstTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private SchemaPackage Studio(params (string Name, string Text)[] files)
    {
        var service = new SchemaStudioService();
        var package = service.Import(files.ToDictionary(f => f.Name, f => f.Text), files[0].Name);
        Services.AddSingleton(service);
        return package;
    }

    private IRenderedComponent<SchemaStudio> ThreeFilePackage()
    {
        Studio(("kvittering.v2.xsd", Main), ("shared.types.v1.xsd", Shared), ("codes.v2.xsd", Codes));
        Services.AddSingleton(new SchemaGenerationProfilesService());
        return Render<SchemaStudio>();
    }

    private static void OpenView(IRenderedComponent<SchemaStudio> page, string view)
    {
        page.FindAll("[data-testid=ss-group]").Single(b => b.TextContent == SchemaStudioNavigation.GroupOf(view).Name).Click();
        page.FindAll("[data-testid=ss-view]").Single(b => b.GetAttribute("data-view") == view).Click();
    }

    private static string RealSchema(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AIAssisted", "xsd"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("AIAssisted/xsd"), "AIAssisted", "xsd", name));
    }

    [Fact]
    public void TopStatus_SaysStructurallyValid_AndWhatIsNotVerified()
    {
        var page = ThreeFilePackage();
        page.Find("[data-testid=ss-validity-headline]").TextContent.Should().Be("XSD package structurally valid");
        page.Find("[data-testid=ss-file-count]").TextContent.Should().Be("3 XSD files");
        page.Find("[data-testid=ss-resolution]").TextContent.Should().Be("All imports/includes resolved");
        page.Find("[data-testid=ss-verified]").TextContent.Should().Contain("XSD structure").And.Contain("imports/includes").And.Contain("local XML validation");
        page.Find("[data-testid=ss-not-verified]").TextContent.Should().Contain("business rules").And.Contain("producer/consumer compatibility").And.Contain("live message delivery");
        page.Find("[data-testid=ss-save-help]").TextContent.Should().Contain("never changes XSD files");
    }

    [Fact]
    public void Overview_UsesPackageTerminology_AndKeepsNamespaceUrisSecondary()
    {
        var page = ThreeFilePackage();
        page.Find("[data-testid=ss-root-element]").TextContent.Should().Be("Kvittering");
        page.Find("[data-testid=ss-root-xsd]").TextContent.Should().StartWith("kvittering.v2.xsd");
        page.Find("[data-testid=ss-overview-supporting]").TextContent.Should().StartWith("2");
        var namespaces = page.Find("[data-testid=ss-overview-namespaces]").TextContent;
        namespaces.Should().StartWith("3").And.Contain("Primary: Applikasjonskvittering v2").And.NotContain("urn:x:", "full namespace URIs belong in Schema → Namespaces");
        page.Find("[data-testid=ss-overview-view-namespaces]").Click();
        page.FindAll("[data-testid=ss-namespace]").Should().HaveCount(3, "one compact table row per namespace");
        page.FindAll("[data-testid=ss-namespace-uri]").Select(e => e.TextContent).Should().Contain("urn:x:applikasjonskvittering:v2");
    }

    [Fact]
    public void LibraryOnlyPackage_HasNoInventedMessage_AndNoRunnableTests()
    {
        Studio(("lib.xsd", LibraryOnly));
        var page = Render<SchemaStudio>();
        page.Find("[data-testid=ss-root-element]").TextContent.Should().Be("None");
        page.Find("[data-testid=ss-meaning]").TextContent.Should().Contain("reusable schema library");
        OpenView(page, "Generated Tests");
        page.FindAll("[data-testid=ss-run-tests]").Should().BeEmpty("no candidate cases means no run button");
        page.Find("[data-testid=ss-tests-empty]").TextContent.Should().Contain("does not define a complete message");
    }

    [Fact]
    public void EffectiveStructure_IsReadableByDefault_AndShowsOriginAndTechnicalDetailOnRequest()
    {
        var page = ThreeFilePackage();
        OpenView(page, "Effective Schema");
        var tree = page.Find("[data-testid=ss-effective-tree]").TextContent;
        tree.Should().Contain("MeldingId").And.Contain("Text · Required once · UUID format");
        tree.Should().NotContain("Declared in").And.NotContain("1..1").And.NotContain("MeldingIdType").And.NotContain("xs:sequence");

        page.Find("[data-testid=ss-effective-show-origin]").Change(true);
        page.Find("[data-testid=ss-effective-tree]").TextContent.Should().Contain("Declared in kvittering.v2.xsd");
        page.Find("[data-testid=ss-effective-show-types]").Change(true);
        page.Find("[data-testid=ss-effective-tree]").TextContent.Should().Contain("MeldingIdType");
        page.Find("[data-testid=ss-effective-show-technical]").Change(true);
        page.Find("[data-testid=ss-effective-tree]").TextContent.Should().Contain("1..1");
    }

    [Fact]
    public void FieldsAndRules_PlainOccurrencePatternAndCollapsedAllowedValues_WithTheExactFacetSecond()
    {
        var page = ThreeFilePackage();
        OpenView(page, "Felt og regler");
        var id = page.Find("[data-path='Kvittering/MeldingId']").TextContent;
        id.Should().Contain("Required once").And.Contain("1..1").And.Contain("UUID format").And.Contain("xs:pattern=");
        page.Find("[data-path='Kvittering/Versjon']").TextContent.Should().Contain("Minimum length 1 character").And.Contain("Pattern rule defined", "an unknown pattern is never given a meaning");
        var resultat = page.Find("[data-path='Kvittering/Resultat']");
        resultat.TextContent.Should().Contain("Optional, max once").And.Contain("9 allowed values");
        resultat.QuerySelector("[data-testid=sr-allowed-values]")!.HasAttribute("open").Should().BeFalse("long value lists are collapsed by default");
        resultat.QuerySelectorAll(".sr-values li").Should().HaveCount(9, "every value stays available");
    }

    [Fact]
    public void Constraints_UsePlainRuleNames_AndSayWhatTheAllowedValueCountMeans()
    {
        var page = ThreeFilePackage();
        OpenView(page, "Constraints");
        page.FindAll("[data-testid=ss-rule-category] dt").Select(d => d.TextContent).Should().Contain(["Occurrence rules", "Length rules", "Pattern rules", "Allowed-value entries", "Numeric bounds"]);
        page.Find("[data-testid=ss-constraint-count-note]").TextContent.Should().Contain("each allowed value once");
        var minLength = page.FindAll("[data-testid=ss-constraints] tbody tr").Single(r => r.Children[1].TextContent.StartsWith("Minimum length", StringComparison.Ordinal));
        minLength.Children[1].TextContent.Should().Contain("xs:minLength");
        minLength.Children[2].TextContent.Should().StartWith("1 character");
        var uuid = page.FindAll("[data-testid=ss-constraints] tbody tr").First(r => r.Children[1].TextContent.StartsWith("Pattern", StringComparison.Ordinal) && r.Children[0].TextContent.Contains("MeldingId"));
        uuid.Children[2].TextContent.Should().StartWith("UUID format");
    }

    [Fact]
    public void Dependencies_AreSummaryFirst_WithTheGraphCollapsed_AndReadableNodeLabels()
    {
        var page = ThreeFilePackage();
        OpenView(page, "Schema dependencies");
        var summary = page.Find("[data-testid=ss-dependency-summary]");
        summary.TextContent.Should().Contain("Imports/includes2").And.Contain("Unresolved0");
        page.Find("[data-testid=ss-dependency-scope]").TextContent.Should().StartWith("Resolved within this imported package");
        page.Find("[data-testid=ss-graph-disclosure]").HasAttribute("open").Should().BeFalse("the graph is secondary");
        page.Find("[data-testid=ss-dependency-details]").HasAttribute("open").Should().BeFalse();
        var labels = page.FindAll("[data-testid=ss-graph-node-label]").Select(l => l.TextContent).ToList();
        labels.Should().Contain("Kvittering v2 schema").And.Contain("Shared types v1 schema");
        page.FindAll("g.ss-graph-node title").Select(t => t.TextContent).Should().Contain("kvittering.v2.xsd", "the full identifier stays in the tooltip");
    }

    [Fact]
    public void Designer_ShowsOnlyTheRulesThatApplyToTheSelectedType_AndAPlainDraftState()
    {
        var page = ThreeFilePackage();
        OpenView(page, "Designer");
        page.Find("[data-testid=ss-draft-state]").TextContent.Should().Be("No draft changes yet");
        page.FindAll("[data-testid=ss-designer-save]").Should().BeEmpty("nothing to save before a valid preview");

        page.FindAll("[data-testid=ss-designer-min-length], [data-testid=ss-designer-max-length], [data-testid=ss-designer-pattern], [data-testid=ss-designer-enumeration]").Should().HaveCount(4);
        page.FindAll("[data-testid=ss-designer-minimum], [data-testid=ss-designer-maximum]").Should().BeEmpty("numeric bounds do not apply to text");

        page.Find("[data-testid=ss-designer-type]").Change("xs:integer");
        page.FindAll("[data-testid=ss-designer-minimum], [data-testid=ss-designer-maximum]").Should().HaveCount(2);
        page.FindAll("[data-testid=ss-designer-min-length], [data-testid=ss-designer-pattern], [data-testid=ss-designer-enumeration]").Should().BeEmpty();

        page.Find("[data-testid=ss-designer-type]").Change("xs:boolean");
        page.Find("[data-testid=ss-designer-no-rules]").TextContent.Should().Contain("needs no extra rules");
    }

    [Fact]
    public void Designer_TextRules_BecomeFacetsOfTheDraft_AndTheDraftValidates()
    {
        var page = ThreeFilePackage();
        OpenView(page, "Designer");
        page.Find("[data-testid=ss-designer-name]").Change("Kode");
        page.Find("[data-testid=ss-designer-min-length]").Change("2");
        page.Find("[data-testid=ss-designer-max-length]").Change("4");
        page.Find("[data-testid=ss-designer-pattern]").Change("[A-Z]+");
        page.Find("[data-testid=ss-designer-add]").Click();
        page.Find("[data-testid=ss-draft-state]").TextContent.Should().StartWith("Draft differs from imported schema");
        var draft = page.Find("textarea[aria-label='Edit XSD source']").GetAttribute("value") ?? page.Find("textarea[aria-label='Edit XSD source']").TextContent;
        draft.Should().Contain("minLength").And.Contain("maxLength").And.Contain("pattern");
        page.Find("[data-testid=ss-designer-preview]").Click();
        page.Find("[data-testid=ss-draft-state]").TextContent.Should().StartWith("Draft valid");
        page.Find("[data-testid=ss-designer-save]").ClassList.Should().Contain("btn-primary");
    }

    [Fact]
    public void GeneratedTests_StateTheRunScopeAndTheProfileInUse()
    {
        Studio(("skolenaervaer.xsd", RealSchema("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd")));
        Services.AddSingleton(new SchemaGenerationProfilesService());
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");
        var count = int.Parse(page.Find("[data-testid=ss-test-count]").TextContent);
        page.Find("[data-testid=ss-run-tests]").TextContent.Should().Be(count > 100 ? "Run representative 100" : $"Run all {count} cases", "never \"first 100\" by list order");
        if (count > 100) page.Find("[data-testid=ss-run-scope]").TextContent.Should().StartWith($"100 of {count} cases selected across ");
        page.Find("[data-testid=ss-tests-profile] h4").TextContent.Should().Be("Generation profile");
        var profile = page.Find("[data-testid=ss-gen-profile-name]").TextContent;
        page.Find("[data-testid=ss-gen-profile-source]").TextContent.Should().Be("Built-in");
        page.Find("[data-testid=ss-gen-profile-version]").TextContent.Should().Contain(SchemaTestDesign.GeneratorVersion);
        page.Find("[data-testid=ss-validate-xml] h4").TextContent.Should().Be("Validate an XML document");

        page.Find("[data-testid=ss-tests-open-profile]").Click();
        page.Find("[data-testid=ss-selected-profile]").TextContent.Should().Contain("Used by").And.Contain("Generated tests");
        var name = page.Find("[data-testid=ss-selected-profile] dd").TextContent.Replace(" (built-in)", "");
        profile.Should().Contain(name, "both views name the same profile");
    }

    [Fact]
    public void ProfileFieldOverrides_ShowOnlyControlsForTheChosenFieldType()
    {
        var page = ThreeFilePackage();
        OpenView(page, "Generation Profile");
        page.Find("[data-testid=ss-profile-customize]").Click();
        page.Find("[data-testid=ss-rule-save]").HasAttribute("disabled").Should().BeTrue("no field chosen yet");

        page.Find("[data-testid=ss-rule-field]").Change("/Kvittering/Versjon");
        page.Find("[data-testid=ss-rule-form]").GetAttribute("data-kind").Should().Be("Text");
        page.FindAll("[data-testid=ss-rule-min-length], [data-testid=ss-rule-max-length]").Should().HaveCount(2);
        page.FindAll("[data-testid=ss-rule-minimum], [data-testid=ss-rule-date-from]").Should().BeEmpty();

        page.Find("[data-testid=ss-rule-field]").Change("/Kvittering/Antall");
        page.FindAll("[data-testid=ss-rule-minimum], [data-testid=ss-rule-maximum]").Should().HaveCount(2);
        page.FindAll("[data-testid=ss-rule-min-length], [data-testid=ss-rule-date-from]").Should().BeEmpty();

        page.Find("[data-testid=ss-rule-field]").Change("/Kvittering/Dato");
        page.FindAll("[data-testid=ss-rule-date-from], [data-testid=ss-rule-date-to]").Should().HaveCount(2);
        page.FindAll("[data-testid=ss-rule-minimum], [data-testid=ss-rule-min-length]").Should().BeEmpty();
        page.Find("[data-testid=ss-rule-save]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Search_OpensMatchingViewsWithTheTermApplied()
    {
        var page = ThreeFilePackage();
        page.Find("[data-testid=ss-search]").Input("MeldingId");
        var results = page.FindAll("[data-testid=ss-search-results] li").Select(li => li.TextContent).ToList();
        results.Should().Contain(r => r.Contains("Fields and rules")).And.Contain(r => r.Contains("Effective structure")).And.Contain(r => r.Contains("Raw XSD"))
            .And.Contain(r => r.Contains("Types"));

        page.FindAll("[data-testid=ss-search-results] li").First(li => li.TextContent.Contains("Effective structure")).QuerySelector("button")!.Click();
        page.Find("[data-testid=ss-effective-search]").GetAttribute("value").Should().Be("MeldingId", "the term carries into the opened view");

        page.Find("[data-testid=ss-search]").Input("MeldingId");
        page.FindAll("[data-testid=ss-search-results] li").First(li => li.TextContent.Contains("Raw XSD")).QuerySelector("button")!.Click();
        page.Find("[data-testid=ss-raw-search]").GetAttribute("value").Should().Be("MeldingId");
        page.Find("[data-testid=ss-raw-hits]").TextContent.Should().NotStartWith("0");
    }
}
