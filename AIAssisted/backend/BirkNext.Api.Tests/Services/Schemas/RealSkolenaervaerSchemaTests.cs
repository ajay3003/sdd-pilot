using BirkNext.Api.Services.ContractEvolution;
using BirkNext.Schemas;
using BirkNext.SourceDomains;
using FluentAssertions;
using Xunit;
using static BirkNext.Api.Tests.Services.Schemas.RealSchemaFixtures;

namespace BirkNext.Api.Tests.Services.Schemas;

/// <summary>Acceptance on the real Skolenærvær and vedlegg schemas: structure, inheritance, enumerations, dependencies and versions.</summary>
public sealed class RealSkolenaervaerSchemaTests
{
    private static XmlSchemaAnalysis Analyze(params string[] names) =>
        XmlSchemaPackageAnalyzer.Analyze(names.ToDictionary(name => name, Read, StringComparer.OrdinalIgnoreCase));

    private static EffectiveMember Member(EffectiveMember root, string path)
    {
        var current = root;
        foreach (var part in path.Split('/')) current = current.Children.Single(child => child.Name == part);
        return current;
    }

    [Fact]
    public void Skolenaervaer200_ParsesRootInheritanceStructureAndOccurrence()
    {
        var analysis = Analyze(Skolenaervaer200);
        analysis.Validation.Should().Be("Valid");
        analysis.Identity.Version.Should().Be("2.0.0");
        analysis.Identity.TargetNamespace.Should().Be("https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.0.altinn");
        analysis.Files.Single().Links.Should().BeEmpty("the flat Altinn schema has no xs:import/include; source comments are not dependencies");

        var root = analysis.EffectiveSchema.Should().ContainSingle().Subject;
        root.Name.Should().Be("Skolenaervaer");
        root.Children.Select(c => c.Name).Should().Equal("Meldingshode", "Klient", "Periode", "SkolenaervaerRegistrering");
        root.Children[0].InheritedFrom.Should().Be("MeldingType", "Meldingshode comes from MeldingType through xs:extension");
        root.Children.Skip(1).Should().OnlyContain(member => member.InheritedFrom == null);

        var fravaer = Member(root, "SkolenaervaerRegistrering/FravaerRegistrering/Fravaer");
        (fravaer.MinOccurs, fravaer.MaxOccurs).Should().Be(("0", "unbounded"));
        fravaer.Children.Select(c => c.Name).Should().Equal("TypeFravaer", "TimerFravaer");
        Member(fravaer, "TimerFravaer").BaseType.Should().Be("xs:double");
        Member(root, "Periode/StartDato").BaseType.Should().Be("xs:dateTime");

        var type = Member(fravaer, "TypeFravaer");
        type.Facets.Where(f => f.Kind == "enumeration").Select(f => f.Value).Should().Equal("1", "2", "3", "4", "5", "9");
        type.Facets.Should().OnlyContain(f => f.Documentation == null, "the schema documents the codes on the type, not per value");
        type.TypeDocumentation.Should().Contain("1 = Lege/tannlege").And.Contain("9 = Annet fravær", "Norwegian documentation is kept verbatim");
    }

    [Fact]
    public void Skolenaervaer200_DoesNotInventBusinessRules()
    {
        var root = Analyze(Skolenaervaer200).EffectiveSchema.Single();
        var periode = Member(root, "Periode");
        periode.Children.Should().OnlyContain(member => member.Facets.Count == 0, "no date-order rule exists in the XSD");
        Member(root, "SkolenaervaerRegistrering/AntallTimerFaktiskNaervaer").Facets.Should().BeEmpty("no numeric bound or sum exists in the XSD");
        Member(root, "SkolenaervaerRegistrering/FravaerRegistrering/Fravaer/TimerFravaer").Facets.Should().BeEmpty("no >= 0 rule exists in the XSD");
        var identifikator = Member(root, "Klient/Identifikator");
        identifikator.Children.Should().OnlyContain(member => member.MinOccurs == "0" && !member.InChoice,
            "FNR/DUF exclusivity is a naming convention here, not an xs:choice");
    }

    [Fact]
    public void Skolenaervaer_ValidAndInvalidInstances()
    {
        var files = new Dictionary<string, string> { [Skolenaervaer200] = Read(Skolenaervaer200) };
        const string ns = "https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.0.altinn";
        var valid = $"""
            <Skolenaervaer xmlns="{ns}">
              <Meldingshode><Id>00000000-0000-0000-0000-000000000000</Id><MeldingstypeNmsp>urn:test</MeldingstypeNmsp>
                <FagsystemAvsender><Leverandor>Test</Leverandor><Navn>Test</Navn><Versjon>1</Versjon></FagsystemAvsender>
                <SendtTidspunkt>2024-01-01T00:00:00Z</SendtTidspunkt><Avsender/><Mottaker/></Meldingshode>
              <Klient/><Periode><StartDato>2024-01-01T00:00:00Z</StartDato><SluttDato>2023-01-01T00:00:00Z</SluttDato></Periode>
              <SkolenaervaerRegistrering><AntallTimerEtterPlanen>10</AntallTimerEtterPlanen><AntallTimerFaktiskNaervaer>20</AntallTimerFaktiskNaervaer>
                <FravaerRegistrering><Fravaer><TypeFravaer>1</TypeFravaer><TimerFravaer>-1</TimerFravaer></Fravaer></FravaerRegistrering></SkolenaervaerRegistrering>
            </Skolenaervaer>
            """;
        XmlSchemaPackageAnalyzer.ValidateXml(files, valid).Status.Should().Be(XmlSchemaValidationStatus.Valid,
            "end before start, more attendance than planned and negative hours are business rules the XSD does not enforce");
        XmlSchemaPackageAnalyzer.ValidateXml(files, valid.Replace("<TypeFravaer>1</TypeFravaer>", "<TypeFravaer>6</TypeFravaer>")).Status
            .Should().Be(XmlSchemaValidationStatus.Invalid, "6 is not an allowed absence code in v2.0.0");
        XmlSchemaPackageAnalyzer.ValidateXml(files, valid.Replace("<Klient/>", "")).Status.Should().Be(XmlSchemaValidationStatus.Invalid);
    }

    [Fact]
    public void VedleggMelding_UnresolvedImportsAreExplicitAndInheritedBaseIsReported()
    {
        var analysis = Analyze(VedleggMelding, VedleggLib);
        analysis.Validation.Should().Be("Partial");
        var links = analysis.Files.Single(f => f.Name == VedleggMelding).Links;
        links.Should().HaveCount(3);
        links.Single(l => l.Location == VedleggLib).ResolvedFile.Should().Be(VedleggLib);
        links.Where(l => l.ResolvedFile is null).Select(l => l.Location).Should().BeEquivalentTo(["bufdir.lib.v1.1.0.xsd", "bufdir.melding.lib.v2.xsd"]);
        analysis.UnresolvedReferences.Should().Contain(r => r.QualifiedName == "{https://bufdir.no/melding.lib.v2}MeldingType" && r.Kind == "base type");

        var root = analysis.EffectiveSchema.Single();
        root.Name.Should().Be("Vedlegg");
        root.Children.Select(c => c.Name).Should().Equal(["Vedlegg"], "only members declared in available files are shown; nothing from the missing base is guessed");
        var vedlegg = root.Children.Single();
        vedlegg.TypeDeclaredIn.Should().Be(VedleggLib);
        vedlegg.Children.Select(c => c.Name).Should().Equal("Filnavn", "VedleggType", "Beskrivelse");
        vedlegg.Children[2].Facets.Should().BeEmpty("xs:appinfo <enable> is not an XSD rule");
    }

    [Fact]
    public void ContractEvolution_V082ToV200_DetectsNamespaceVersionEnumRequirednessAndRootTypeChanges()
    {
        var descriptor = new ContractDescriptor("skolenaervaer:altinn", SourceContractType.XmlSchema, "Skolenærvær", null, "test");
        var at = DateTimeOffset.UnixEpoch;
        var before = ContractSnapshots.FromXmlSchemaText(descriptor, Skolenaervaer082, Read(Skolenaervaer082), at);
        var after = ContractSnapshots.FromXmlSchemaText(descriptor, Skolenaervaer200, Read(Skolenaervaer200), at);
        before.Descriptor.Version.Should().Be("0.8.2");
        after.Descriptor.Version.Should().Be("2.0.0");

        var changes = ContractDiff.Compare(before, after);
        changes.Should().Contain(c => c.Change == ContractChangeClass.VersionChanged);
        changes.Should().Contain(c => c.Change == ContractChangeClass.NamespaceChanged && c.Path == "");
        changes.Should().Contain(c => c.Change == ContractChangeClass.RequirednessChanged && c.Path == "Skolenaervaer/Klient/Identifikator" && c.NewValue == "optional");
        changes.Should().Contain(c => c.Change == ContractChangeClass.TypeChanged && c.Path == "Skolenaervaer", "the root type was renamed RapportSkolenaervaerType → SkolenaervaerType");
        var fravaer = "Skolenaervaer/SkolenaervaerRegistrering/FravaerRegistrering/Fravaer/TypeFravaer";
        changes.Where(c => c.Path == fravaer && c.Change == ContractChangeClass.EnumValueRemoved).Select(c => c.OldValue).Should().BeEquivalentTo(["6", "7", "99"]);
        changes.Should().Contain(c => c.Path == fravaer && c.Change == ContractChangeClass.EnumValueAdded && c.NewValue == "9");
        changes.Should().OnlyContain(c => c.Change != ContractChangeClass.None);
        changes.Where(c => c.Path.StartsWith("Skolenaervaer/", StringComparison.Ordinal)).Should().OnlyContain(c => c.DeclaredIn != null, "every member change carries its declaring file");
    }
}
