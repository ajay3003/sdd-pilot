using BirkNext.Schemas;
using BirkNext.Web.Components;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using P = BirkNext.Web.Services.SchemaStudio.SchemaReadablePresentation;

namespace BirkNext.Web.Tests.Components;

/// <summary>Schema Studio's deterministic readable views: wording matrix, source language, no invented rules, roots and dependencies.</summary>
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
    [InlineData("1", "1", "Obligatorisk")]
    [InlineData("0", "1", "Valgfritt")]
    [InlineData("0", "unbounded", "Valgfritt, kan forekomme flere ganger")]
    [InlineData("1", "unbounded", "Minst én, kan forekomme flere ganger")]
    [InlineData("2", "5", "Mellom 2 og 5 ganger")]
    public void Cardinality_IsReadable_AndExactIsPreserved(string min, string max, string expected)
    {
        P.CardinalityLabel(Member(min, max)).Should().Be(expected);
        P.ExactCardinality(Member(min, max)).Should().Be($"{min}..{(max == "unbounded" ? "*" : max)}");
    }

    [Theory]
    [InlineData("xs:string", "Tekst")]
    [InlineData("xs:boolean", "Ja/nei")]
    [InlineData("xs:integer", "Heltall")]
    [InlineData("xs:decimal", "Tall")]
    [InlineData("xs:double", "Tall")]
    [InlineData("xs:date", "Dato")]
    [InlineData("xs:dateTime", "Dato og tidspunkt")]
    public void BuiltInTypes_MapToFixedLabels_AndRawTypeStaysAvailable(string type, string label)
    {
        P.TypeLabel(Member("1", "1", type)).Should().Be(label);
        P.RawType(Member("1", "1", type)).Should().Be(type);
    }

    [Theory]
    [InlineData(@"\d{11}", "Nøyaktig 11 sifre")]
    [InlineData(@"\d{2,4}", "Mellom 2 og 4 sifre")]
    [InlineData(@"[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}", "Formatregel definert")]
    [InlineData(@"(\d+\.)?(\d+\.)?(\*|\d+)", "Formatregel definert")]
    public void Patterns_OnlyObviousDigitCountsAreTranslated(string pattern, string expected) => P.PatternLabel(pattern).Should().Be(expected);

    [Fact]
    public void Rules_LengthAndBounds_AreReadableWithRawFacet()
    {
        P.Rules([new("minLength", "4"), new("maxLength", "4")]).Should().ContainSingle(r => r.Text == "Nøyaktig 4 tegn");
        P.Rules([new("minInclusive", "0"), new("maxExclusive", "10")]).Select(r => r.Text).Should().Equal("Minst 0", "Mindre enn 10");
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
        P.CardinalityLabel(Member("1", "1", compositor: "choice")).Should().Contain("ett av alternativene (xs:choice)");
        P.CardinalityLabel(Member("0", "1") with { Name = "CHOICE_ELEMENT_Fodselsnummer__DUFnummer" }).Should().Be("Valgfritt");
    }

    // ── Real Skolenærvær schema ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Business_ShowsTheRealStructureAndNorwegianDocumentationVerbatim()
    {
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, Import((Skolenaervaer, RealSchema(Skolenaervaer)))).Add(x => x.View, "Forretning"));
        var business = view.Find("[data-testid=sr-business]");
        business.QuerySelector("h4")!.TextContent.Should().Be("Skolenaervaer");
        var topLevel = business.QuerySelectorAll(":scope > ul > li > .sr-name").Select(e => e.TextContent).ToList();
        topLevel.Should().Equal("Meldingshode", "Klient", "Periode", "SkolenaervaerRegistrering");
        view.Markup.Should().Contain("Benyttes kun for kommuner med bydeler").And.NotContain("xs:element");
    }

    [Fact]
    public void FieldsAndRules_ShowTypeFravaerCodesFromTheTypeDocumentation_AndInventNoBusinessRules()
    {
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, Import((Skolenaervaer, RealSchema(Skolenaervaer)))).Add(x => x.View, "Felt og regler"));
        var typeFravaer = view.Find("[data-path='Skolenaervaer/SkolenaervaerRegistrering/FravaerRegistrering/Fravaer/TypeFravaer']");
        typeFravaer.TextContent.Should().Contain("Tillatte verdier").And.Contain("Fra typen:")
            .And.Contain("1 = Lege/tannlege").And.Contain("9 = Annet fravær");
        typeFravaer.QuerySelectorAll(".sr-values li").Select(li => li.TextContent.Trim()).Should().Equal("1", "2", "3", "4", "5", "9");
        view.Find("[data-path='Skolenaervaer/SkolenaervaerRegistrering/FravaerRegistrering/Fravaer']").TextContent
            .Should().Contain("Valgfritt, kan forekomme flere ganger").And.Contain("0..*");
        view.Find("[data-path='Skolenaervaer/Periode/StartDato']").TextContent.Should().Contain("Dato og tidspunkt").And.Contain("Obligatorisk");
        view.Find("[data-path='Skolenaervaer/SkolenaervaerRegistrering/AntallTimerEtterPlanen']").TextContent.Should().Contain("Tall").And.Contain("xs:double");
        view.Find("[data-path='Skolenaervaer/Meldingshode']").TextContent.Should().Contain("arvet fra MeldingType");
        view.Find("[data-path='Skolenaervaer/Klient/Identifikator/Fodselsnummer']").TextContent.Should().Contain("Nøyaktig 11 sifre").And.NotContain("xs:choice");
        view.Find("[data-path='Skolenaervaer/Meldingshode/Id']").TextContent.Should().Contain("Formatregel definert");

        var text = view.Find("[data-testid=sr-fields]").TextContent;
        foreach (var invented in new[] { "SluttDato må", "senere enn", "større enn StartDato", "≥ 0", "Minst 0", "summen", "utelukker" })
            text.Should().NotContain(invented, "rules the XSD does not state are never presented");
    }

    [Fact]
    public void Structure_ShowsProvenanceAndRawTypes()
    {
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, Import((Skolenaervaer, RealSchema(Skolenaervaer)))).Add(x => x.View, "Struktur"));
        view.Find("[data-testid=sr-structure]").TextContent.Should().Contain("rotelement · SkolenaervaerType").And.Contain("arvet fra MeldingType")
            .And.Contain("PeriodeType").And.Contain("i fast rekkefølge (xs:sequence)");
    }

    [Fact]
    public void MultipleRoots_RequireExplicitSelection()
    {
        const string xsd = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="A" type="xs:string"/><xs:element name="B"><xs:complexType><xs:sequence><xs:element name="C" type="xs:int"/></xs:sequence></xs:complexType></xs:element></xs:schema>""";
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, Import(("two.xsd", xsd))).Add(x => x.View, "Felt og regler"));
        view.Markup.Should().Contain("gjetter ikke");
        view.FindAll("[data-testid=sr-fields]").Should().BeEmpty();
        view.Find("select[aria-label=Rotelement]").Change("B");
        view.Find("[data-path='B/C']").TextContent.Should().Contain("Heltall");
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
        problems.Should().OnlyContain(row => row.TextContent.Contains("Avhengighet ikke løst"));
        problems.Select(row => row.TextContent).Should().Contain(text => text.Contains("bufdir.lib.v1.1.0.xsd") && text.Contains("https://bufdir.no/lib.v1.1.0"));
        view.Find("[data-testid=sr-unresolved-references]").TextContent.Should().Contain("{https://bufdir.no/melding.lib.v2}MeldingType");
        var files = view.FindAll("[data-testid=sr-dependency-file]");
        files.Should().HaveCount(2);
        files.Single(row => row.TextContent.Contains("vedlegg.lib")).TextContent.Should().Contain("1.0.0").And.Contain("xs:import fra bufdir.melding.vedleggmelding.v2.0.0.xsd");
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
        view.Markup.Should().Contain("Sirkulær avhengighet oppdaget");
    }
}
