using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;

public sealed class XmlSchemaContractTests
{
    [Fact]
    public void Source_analysis_preserves_xsd_namespace_cardinality_facets_and_snapshot_local_dependencies()
    {
        var result = SourceEvidenceFixtures.Analyze(
            ("contracts/report.xsd", Schema),
            ("contracts/common.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common" />"""));

        var contract = result.Contracts.Contracts.Where(c => c.File == "contracts/report.xsd").Should().ContainSingle().Subject;
        contract.Type.Should().Be(SourceContractType.XmlSchema);
        contract.ParseSupport.Should().Be(DomainSupport.Partial);
        contract.XmlSchema!.TargetNamespace.Should().Be("urn:school:report:v1");
        contract.XmlSchema.ExplicitVersion.Should().Be("1.2");
        contract.XmlSchema.VersionEvidence.Should().Contain("Explicit");
        contract.XmlSchema.Dependencies.Should().ContainSingle().Which.ResolvedInSnapshot.Should().BeTrue();
        contract.XmlSchema.Attributes.Should().ContainSingle(a => a.Path == "Report/@source" && a.Use == "required");

        var id = contract.XmlSchema.Elements.Should().Contain(e => e.Path == "Report/ChildId").Subject;
        id.QualifiedName.Should().Be("{urn:school:report:v1}ChildId");
        id.MinOccurs.Should().Be(1);
        id.MaxOccurs.Should().Be("1");
        id.Nillable.Should().BeFalse();
        id.DefaultValue.Should().Be("[redacted identifier]");

        var status = contract.XmlSchema.Elements.Should().Contain(e => e.Path == "Report/Status").Subject;
        status.MinOccurs.Should().Be(0);
        status.MaxOccurs.Should().Be("unbounded");
        status.Nillable.Should().BeTrue();
        status.Restrictions.Should().Contain(r => r.Facet == "enumeration" && r.Value == "Accepted");
        contract.Limitations.Should().Contain(x => x.Contains("does not validate XML", StringComparison.Ordinal));
    }

    [Fact]
    public void Xsd_snapshot_changes_classify_required_additions_as_potentially_breaking_without_global_breaking_verdict()
    {
        var before = Contract("urn:school:report:v1", [Element("Report/Existing", "{urn:school:report:v1}Existing", 0, "1")]);
        var after = Contract("urn:school:report:v1", [
            Element("Report/Existing", "{urn:school:report:v1}Existing", 0, "1"),
            Element("Report/RequiredValue", "{urn:school:report:v1}RequiredValue", 1, "1")]);
        var changes = SourceEvidenceDiff.Compare(Domains(before), Domains(after));

        changes.Should().ContainSingle(c => c.Area == "Element" && c.CompatibilityConcern == ContractCompatibilityConcern.PotentiallyBreaking);
        changes.Should().NotContain(c => c.Detail.Contains("breaking", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unavailable_external_xsd_import_is_unresolved_evidence_not_invalid_contract()
    {
        var result = SourceEvidenceFixtures.Analyze(("contracts/report.xsd", Schema.Replace("schemaLocation=\"common.xsd\"", "schemaLocation=\"https://schemas.example.test/common.xsd\"")));
        var contract = result.Contracts.Contracts.Should().ContainSingle().Subject;

        contract.ParseSupport.Should().Be(DomainSupport.Partial);
        contract.XmlSchema!.Dependencies.Should().ContainSingle().Which.ResolvedInSnapshot.Should().BeFalse();
        contract.Limitations.Should().Contain(x => x.Contains("unavailable", StringComparison.Ordinal));
        result.Contracts.Status.Should().Be(SourceDomainStatus.Partial);
    }

    [Fact]
    public void Structurally_invalid_xsd_is_reported_as_parse_diagnostic()
    {
        var result = SourceEvidenceFixtures.Analyze(("contracts/broken.xsd", """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:banana /></xs:schema>"""));

        result.Contracts.Contracts.Should().BeEmpty();
        result.Contracts.Status.Should().Be(SourceDomainStatus.Partial);
        result.Contracts.Diagnostics.Should().ContainSingle(d => d.Kind == "Parse error" && d.File == "contracts/broken.xsd");
    }

    private static SourceEvidenceDomainsSnapshot Domains(SourceContract contract) => new()
    {
        Contracts = new ContractEvidence { Contracts = [contract] },
    };

    private static SourceContract Contract(string targetNamespace, List<XmlSchemaElementEvidence> elements) => new()
    {
        Id = "xsd:contracts/report.xsd", Type = SourceContractType.XmlSchema, Name = "report", File = "contracts/report.xsd",
        XmlSchema = new(targetNamespace, "1.2", "Explicit xs:schema version attribute.", elements, [], [], []),
    };

    private static XmlSchemaElementEvidence Element(string path, string qualifiedName, decimal minOccurs, string maxOccurs) =>
        new(path, qualifiedName, "{http://www.w3.org/2001/XMLSchema}string", minOccurs, maxOccurs, false, null, null, null, [], "sequence");

    private const string Schema = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:school:report:v1" xmlns="urn:school:report:v1" elementFormDefault="qualified" version="1.2">
          <xs:import namespace="urn:common" schemaLocation="common.xsd" />
          <xs:element name="Report">
            <xs:annotation><xs:documentation>Inbound report root</xs:documentation></xs:annotation>
            <xs:complexType><xs:sequence>
              <xs:element name="ChildId" type="xs:string" default="01123456789" />
              <xs:element name="Status" minOccurs="0" maxOccurs="unbounded" nillable="true">
                <xs:annotation><xs:documentation>Status entries</xs:documentation></xs:annotation>
                <xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="Accepted" /><xs:enumeration value="Rejected" /></xs:restriction></xs:simpleType>
              </xs:element>
              <xs:choice><xs:element name="Date" type="xs:date" /><xs:element name="DateTime" type="xs:dateTime" /></xs:choice>
            </xs:sequence><xs:attribute name="source" type="xs:string" use="required" /></xs:complexType>
          </xs:element>
          <xs:simpleType name="Period"><xs:restriction base="xs:string"><xs:maxLength value="20" /></xs:restriction></xs:simpleType>
        </xs:schema>
        """;
}
