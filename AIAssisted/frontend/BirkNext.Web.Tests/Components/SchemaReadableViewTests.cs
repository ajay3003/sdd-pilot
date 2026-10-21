using BirkNext.Schemas;
using BirkNext.Web.Components;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using P = BirkNext.Web.Services.SchemaStudio.SchemaReadablePresentation;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Schema Studio's deterministic readable views: English wording matrix (the UI is English; the schema's own documentation stays verbatim in its
/// source language), no invented rules, explicit roots, dependencies with English status labels only.
/// </summary>
public sealed class SchemaReadableViewTests : BunitContext
{
    private static string RealSchema(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AIAssisted", "xsd"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("AIAssisted/xsd"), "AIAssisted", "xsd", name));
    }

    private const string Skolenaervaer = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd";

    private static SchemaPackage Import(params (string Name, string Text)[] files) =>
        new SchemaStudioService().Import(files.ToDictionary(f => f.Name, f => f.Text), "package");

    private static EffectiveMember Member(string min, string max, string? type = "xs:string", string? compositor = "sequence", string? group = "/R/sequence#1") =>
        new("Felt", type, min, max, "a.xsd", null, null, compositor, [], [], "", group, BaseType: type);

    // ── Wording matrix ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1", "1", "Required")]
    [InlineData("0", "1", "Optional")]
    [InlineData("0", "unbounded", "Optional, can repeat")]
    [InlineData("1", "unbounded", "At least one, can repeat")]
    [InlineData("2", "5", "Between 2 and 5 times")]
    public void Cardinality_IsReadable_AndExactIsPreserved(string min, string max, string expected)
    {
        P.CardinalityLabel(Member(min, max)).Should().Be(expected);
        P.ExactCardinality(Member(min, max)).Should().Be($"{min}..{(max == "unbounded" ? "*" : max)}");
    }

    [Theory]
    [InlineData("xs:string", "Text")]
    [InlineData("xs:boolean", "True/false")]
    [InlineData("xs:integer", "Whole number")]
    [InlineData("xs:decimal", "Number")]
    [InlineData("xs:double", "Number")]
    [InlineData("xs:date", "Date")]
    [InlineData("xs:dateTime", "Date and time")]
    public void BuiltInTypes_MapToFixedLabels_AndRawTypeStaysAvailable(string type, string label)
    {
        P.TypeLabel(Member("1", "1", type)).Should().Be(label);
        P.RawType(Member("1", "1", type)).Should().Be(type);
    }

    [Theory]
    [InlineData(@"\d{11}", "Exactly 11 digits")]
    [InlineData(@"\d{2,4}", "Between 2 and 4 digits")]
    [InlineData(@"[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}", "Pattern rule defined")]
    [InlineData(@"(\d+\.)?(\d+\.)?(\*|\d+)", "Pattern rule defined")]
    public void Patterns_OnlyObviousDigitCountsAreTranslated(string pattern, string expected) => P.PatternLabel(pattern).Should().Be(expected);

    [Fact]
    public void Rules_LengthAndBounds_AreReadableWithRawFacet()
    {
        P.Rules([new("minLength", "4"), new("maxLength", "4")]).Should().ContainSingle(r => r.Text == "Exactly 4 characters" && r.Raw == "minLength=4, maxLength=4");
        P.Rules([new("minInclusive", "0"), new("maxExclusive", "10")]).Select(r => r.Text).Should().Equal("Minimum value 0", "Less than 10");
    }

    [Fact]
    public void EnumValues_UseOnlyPerValueDocumentation_NeverInventedLabels()
    {
        P.AllowedValues([new("enumeration", "1", "Lege/tannlege"), new("enumeration", "2")])
            .Should().Equal(("1", "Lege/tannlege"), ("2", (string?)null));
    }

    [Fact]
    public void ChoiceWording_OnlyForRealXsChoice()
    {
        P.CardinalityLabel(Member("1", "1", compositor: "choice")).Should().Contain("one of the alternatives (xs:choice)");
        P.CardinalityLabel(Member("0", "1") with { Name = "CHOICE_ELEMENT_Fodselsnummer__DUFnummer" }).Should().Be("Optional", "naming conventions never imply xs:choice");
    }

    // ── Real Skolenærvær schema ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Business_ShowsTheRealStructureAndNorwegianDocumentationVerbatim()
    {
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, Import((Skolenaervaer, RealSchema(Skolenaervaer)))).Add(x => x.View, "Forretning"));
        var business = view.Find("[data-testid=sr-business]");
        business.QuerySelector("h4")!.TextContent.Should().Be("Skolenaervaer");
        var topLevel = business.QuerySelectorAll(":scope > ul > li").Select(li => li.QuerySelector(".sr-name")!.TextContent).ToList();
        topLevel.Should().Equal("Meldingshode", "Klient", "Periode", "SkolenaervaerRegistrering");
        view.Markup.Should().Contain("Benyttes kun for kommuner med bydeler").And.NotContain("xs:element");
    }

    [Fact]
    public void FieldsAndRules_ShowTypeFravaerCodesFromTheTypeDocumentation_AndInventNoBusinessRules()
    {
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, Import((Skolenaervaer, RealSchema(Skolenaervaer)))).Add(x => x.View, "Felt og regler"));
        var typeFravaer = view.Find("[data-path='Skolenaervaer/SkolenaervaerRegistrering/FravaerRegistrering/Fravaer/TypeFravaer']");
        typeFravaer.TextContent.Should().Contain("Allowed values").And.Contain("From the type:")
            .And.Contain("1 = Lege/tannlege").And.Contain("9 = Annet fravær");
        typeFravaer.QuerySelectorAll(".sr-values li").Select(li => li.TextContent.Trim()).Should().Equal("1", "2", "3", "4", "5", "9");
        view.Find("[data-path='Skolenaervaer/SkolenaervaerRegistrering/FravaerRegistrering/Fravaer']").TextContent
            .Should().Contain("Optional, can repeat").And.Contain("0..*");
        view.Find("[data-path='Skolenaervaer/Periode/StartDato']").TextContent.Should().Contain("Date and time").And.Contain("Required");
        view.Find("[data-path='Skolenaervaer/SkolenaervaerRegistrering/AntallTimerEtterPlanen']").TextContent.Should().Contain("Number").And.Contain("xs:double");
        view.Find("[data-path='Skolenaervaer/Meldingshode']").TextContent.Should().Contain("Inherited from MeldingType");
        view.Find("[data-path='Skolenaervaer/Klient/Identifikator/Fodselsnummer']").TextContent.Should().Contain("Exactly 11 digits").And.NotContain("xs:choice");
        view.Find("[data-path='Skolenaervaer/Meldingshode/Id']").TextContent.Should().Contain("Pattern rule defined");

        var text = view.Find("[data-testid=sr-fields]").TextContent;
        foreach (var invented in new[] { "SluttDato må", "later than", "greater than StartDato", "≥ 0", "Minimum value 0", "sum of", "excludes" })
            text.Should().NotContain(invented, "rules the XSD does not state are never presented");
        // UI copy is English; the schema's documentation (Norwegian here) is the only non-English text and is shown verbatim.
        view.FindAll("[data-testid=sr-fields] thead th").Select(th => th.TextContent).Should().Equal("Field", "Occurrence", "Type", "Rules", "Documentation", "Source");
    }

    [Fact]
    public void Structure_ShowsProvenanceAndRawTypes()
    {
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, Import((Skolenaervaer, RealSchema(Skolenaervaer)))).Add(x => x.View, "Struktur"));
        view.Find("[data-testid=sr-structure]").TextContent.Should().Contain("top-level element · SkolenaervaerType").And.Contain("inherited from MeldingType")
            .And.Contain("PeriodeType").And.Contain("in fixed order (xs:sequence)");
    }

    [Fact]
    public void MultipleRoots_RequireExplicitSelection()
    {
        const string xsd = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="A" type="xs:string"/><xs:element name="B"><xs:complexType><xs:sequence><xs:element name="C" type="xs:int"/></xs:sequence></xs:complexType></xs:element></xs:schema>""";
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, Import(("two.xsd", xsd))).Add(x => x.View, "Felt og regler"));
        view.Markup.Should().Contain("does not guess which one is the message");
        view.FindAll("[data-testid=sr-fields]").Should().BeEmpty();
        view.Find("select[aria-label='Top-level element']").Change("B");
        view.Find("[data-path='B/C']").TextContent.Should().Contain("Whole number");
    }

    [Fact]
    public void Dependencies_ShowUnresolvedImportsExplicitlyAndOpenRawXsd()
    {
        var opened = "";
        var package = Import(("bufdir.melding.vedleggmelding.v2.0.0.xsd", RealSchema("bufdir.melding.vedleggmelding.v2.0.0.xsd")),
            ("bufdir.melding.vedlegg.lib.v1.0.0.xsd", RealSchema("bufdir.melding.vedlegg.lib.v1.0.0.xsd")));
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, package).Add(x => x.View, "Schema dependencies")
            .Add(x => x.OnFileSelected, (string file) => opened = file));
        var problems = view.FindAll("[data-testid=sr-dependency-problem]");
        problems.Should().HaveCount(2);
        problems.Should().OnlyContain(row => row.TextContent.Contains("Unresolved dependency"));
        problems.Select(row => row.TextContent).Should().Contain(text => text.Contains("bufdir.lib.v1.1.0.xsd") && text.Contains("https://bufdir.no/lib.v1.1.0"));
        view.Find("[data-testid=sr-unresolved-references]").TextContent.Should().Contain("{https://bufdir.no/melding.lib.v2}MeldingType");
        var files = view.FindAll("[data-testid=sr-dependency-file]");
        files.Should().HaveCount(2);
        files.Single(row => row.TextContent.Contains("vedlegg.lib")).TextContent.Should().Contain("1.0.0").And.Contain("Imported schema (xs:import) from bufdir.melding.vedleggmelding.v2.0.0.xsd");
        view.FindAll("[data-testid=sr-dependencies] thead th").Select(th => th.TextContent).Should().Equal("Schema file", "Namespace", "Version", "Dependency status");
        view.Find("[data-testid=sr-dependency-provenance-toggle]").GetAttribute("aria-expanded").Should().Be("false", "fingerprints and source are provenance, not default columns");
        files[0].QuerySelector("button")!.Click();
        opened.Should().EndWith(".xsd");
    }

    [Fact]
    public void Dependencies_CircularIsShownWithoutCrashing()
    {
        var package = Import(
            ("a.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:a"><xs:import namespace="urn:b" schemaLocation="b.xsd"/><xs:element name="A" type="xs:string"/></xs:schema>"""),
            ("b.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:b"><xs:import namespace="urn:a" schemaLocation="a.xsd"/></xs:schema>"""));
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, package).Add(x => x.View, "Schema dependencies"));
        view.Markup.Should().Contain("Circular dependency detected");
    }

    [Fact]
    public void Dependencies_SingleFile_IsConcise_AndUsesEnglishStatusLabelsOnly()
    {
        var package = Import(("bufdir.lib.v1.1.0.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="https://bufdir.no/lib.v1.1.0" version="1.1.0"><xs:simpleType name="DUFNummerType"><xs:restriction base="xs:string"><xs:pattern value="\d{12}"/></xs:restriction></xs:simpleType></xs:schema>"""));
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, package).Add(x => x.View, "Schema dependencies"));
        view.Find("[data-testid=sr-dependency-status]").TextContent.Should().Be("No imports/includes");
        view.FindAll("[data-testid=sr-dependency-problem]").Should().BeEmpty();
        var text = view.Find("section").TextContent;
        foreach (var norwegian in new[] { "Løst", "løst", "Uløst", "uløst", "Kilde", "kilde", "Versjon", "Ikke oppgitt", "Lokal import", "fra " })
            text.Should().NotContain(norwegian, "Schema Studio UI is English");
    }

    [Fact]
    public void LibraryXsd_IsExplainedInPlainLanguage_TechnicalPhraseSecondary()
    {
        var package = Import(("bufdir.lib.v1.1.0.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:lib"><xs:simpleType name="A"><xs:restriction base="xs:string"/></xs:simpleType><xs:complexType name="B"><xs:sequence><xs:element name="C" type="xs:string"/></xs:sequence></xs:complexType></xs:schema>"""));
        var navigated = "";
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, package).Add(x => x.View, "Forretning").Add(x => x.OnNavigate, (string v) => navigated = v));
        var state = view.Find("[data-testid=sr-no-root]");
        state.QuerySelector("strong")!.TextContent.Should().Be(SchemaStudioPresentation.LibraryStructureHeadline);
        state.TextContent.Should().Contain("2 reusable schema definitions");
        state.QuerySelector(".empty-state-note")!.TextContent.Should().Be(SchemaStudioPresentation.LibraryTechnical, "the technical phrase is secondary detail");
        state.QuerySelectorAll("button").Select(b => b.TextContent).Should().Equal("View reusable definitions", "View types", "View references");
        view.Find("[data-testid=sr-no-root-types]").Click();
        navigated.Should().Be("Types");
    }
}
