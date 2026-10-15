using BirkNext.Api.Services.ContractEvolution;
using BirkNext.SourceDomains;
using FluentAssertions;
using Xunit;
using static BirkNext.Api.Tests.Services.ContractEvolution.ContractEvolutionFixtures;

namespace BirkNext.Api.Tests.Services.ContractEvolution;

/// <summary>Contract Evolution on the shared XSD capability: format/restriction changes, inheritance, cross-file types and dependency provenance.</summary>
public sealed class ContractEvolutionSharedSchemaTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static ContractDescriptor Xsd() => new("example:package", SourceContractType.XmlSchema, "Package", null, "test");

    private const string Main = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common" xmlns="urn:main" targetNamespace="urn:main" elementFormDefault="qualified" version="1.0">
          <xs:import namespace="urn:common" schemaLocation="Common.xsd"/>
          <xs:complexType name="BaseType"><xs:sequence><xs:element name="Header" type="xs:string"/></xs:sequence></xs:complexType>
          <xs:element name="Message"><xs:complexType><xs:complexContent><xs:extension base="BaseType"><xs:sequence>
            <xs:element name="Person" type="c:PersonIdType"/>
          </xs:sequence></xs:extension></xs:complexContent></xs:complexType></xs:element>
        </xs:schema>
        """;

    private const string Common = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common" version="3.1">
          <xs:simpleType name="PersonIdType"><xs:restriction base="xs:string"><xs:pattern value="\d{11}"/></xs:restriction></xs:simpleType>
        </xs:schema>
        """;

    private static ContractSnapshot Package(string main, string common) => ContractSnapshots.FromXmlSchemaPackage(Xsd(),
        new Dictionary<string, string> { ["Main.xsd"] = main, ["Common.xsd"] = common }, "Main.xsd", At);

    [Fact]
    public void Package_ExpandsInheritedAndImportedMembersWithProvenance()
    {
        var snapshot = Package(Main, Common);
        snapshot.Nodes.Select(n => n.Path).Should().Contain(["Message", "Message/Header", "Message/Person"]);
        var person = snapshot.Nodes.Single(n => n.Path == "Message/Person");
        person.DataType.Should().Be("{urn:common}PersonIdType");
        person.Restrictions.Should().Equal(@"pattern=\d{11}");
        snapshot.Nodes.Single(n => n.Path == "#type/{urn:common}PersonIdType").DeclaredIn.Should().Be("Common.xsd");
        snapshot.Dependencies.Should().Contain(d => d.File == "Common.xsd" && d.Kind == "imported" && d.Version == "3.1" && d.ReferencedBy == "Main.xsd");
        snapshot.Dependencies.Should().Contain(d => d.File == "Main.xsd" && d.Kind == "root");
        snapshot.Descriptor.Version.Should().Be("1.0");
    }

    [Fact]
    public void FormatChangeInImportedType_IsRestrictionChanged_NeedsReview_WithDependencyProvenance()
    {
        var before = Package(Main, Common);
        var after = Package(Main, Common.Replace(@"\d{11}", @"\d{12}"));
        var changes = ContractDiff.Compare(before, after);
        var restriction = changes.Should().Contain(c => c.Change == ContractChangeClass.RestrictionChanged && c.Path == "Message/Person").Subject;
        restriction.OldValue.Should().Be(@"pattern=\d{11}");
        restriction.NewValue.Should().Be(@"pattern=\d{12}");
        changes.Should().Contain(c => c.Change == ContractChangeClass.ImportChanged && c.Path == "#dependency/Common.xsd" && c.DeclaredIn == "Common.xsd");

        foreach (var perspective in new[] { ContractPerspective.Producer, ContractPerspective.Consumer })
        {
            ContractCompatibilityRules.Structural(restriction, perspective, ["Message/Person"]).Compatibility.Should().Be(ContractCompatibility.NeedsReview,
                "a changed format is never silently compatible for a party that uses the member");
            ContractCompatibilityRules.Structural(restriction, perspective, null).Compatibility.Should().Be(ContractCompatibility.NeedsReview);
        }
    }

    [Fact]
    public void LengthAndBoundChanges_AreRestrictionChanges()
    {
        var baseline = Baseline.Replace("""<xs:element name="Name" type="xs:string"/>""",
            """<xs:element name="Name"><xs:simpleType><xs:restriction base="xs:string"><xs:maxLength value="70"/></xs:restriction></xs:simpleType></xs:element>""");
        var changed = baseline.Replace("""<xs:maxLength value="70"/>""", """<xs:maxLength value="35"/>""");
        ContractDiff.Compare(Snapshot(baseline), Snapshot(changed)).Should().ContainSingle(c => c.Change == ContractChangeClass.RestrictionChanged)
            .Which.Path.Should().Be("Order/Customer/Name");
    }

    [Fact]
    public void InheritedMemberRequiredness_IsDetectedThroughTheBaseType()
    {
        var changes = ContractDiff.Compare(Package(Main, Common),
            Package(Main.Replace("""<xs:element name="Header" type="xs:string"/>""", """<xs:element name="Header" type="xs:string" minOccurs="0"/>"""), Common));
        changes.Should().ContainSingle(c => c.Change == ContractChangeClass.RequirednessChanged && c.Path == "Message/Header")
            .Which.DeclaredIn.Should().Be("Main.xsd");
    }

    [Fact]
    public void FormattingOnlyDependencyChange_IsNotAContractChange()
    {
        var before = Package(Main, Common);
        var after = Package(Main, Common.Replace("<xs:simpleType", "\n\n   <!-- comment -->\n<xs:simpleType"));
        after.Fingerprint.Should().Be(before.Fingerprint, "comments and whitespace in a dependency are not structure");
        ContractDiff.Compare(before, after).Should().BeEmpty();
    }

    [Fact]
    public void XsdFingerprint_UsesItsOwnNormalizerVersion_AndJsonFingerprintsAreUnchanged()
    {
        ContractSnapshots.XmlSchemaNormalizerVersion.Should().Be(2);
        ContractSnapshots.NormalizerVersion.Should().Be(1, "JSON/OpenAPI normalization did not change, so their fingerprints stay stable");
    }
}
