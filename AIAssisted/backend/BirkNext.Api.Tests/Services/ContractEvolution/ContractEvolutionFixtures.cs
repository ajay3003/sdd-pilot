using BirkNext.Api.Services.ContractEvolution;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Tests.Services.ContractEvolution;

/// <summary>Reusable XSD contract-evolution fixtures: one neutral baseline and one variant per kind of change. No project names.</summary>
internal static class ContractEvolutionFixtures
{
    public const string ContractId = "example:order";
    public static readonly DateTimeOffset At = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    public const string Baseline = """
        <?xml version="1.0" encoding="utf-8"?>
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:order:v1" xmlns="urn:example:order:v1" elementFormDefault="qualified" version="1.0">
          <xs:element name="Order">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="Id" type="xs:string"/>
                <xs:element name="Customer">
                  <xs:complexType>
                    <xs:sequence>
                      <xs:element name="Name" type="xs:string"/>
                    </xs:sequence>
                  </xs:complexType>
                </xs:element>
                <xs:element name="Quantity" type="xs:int"/>
                <xs:element name="Status">
                  <xs:simpleType>
                    <xs:restriction base="xs:string">
                      <xs:enumeration value="Open"/>
                      <xs:enumeration value="Closed"/>
                    </xs:restriction>
                  </xs:simpleType>
                </xs:element>
                <xs:element name="Note" type="xs:string" minOccurs="0"/>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private const string NoteLine = """<xs:element name="Note" type="xs:string" minOccurs="0"/>""";

    public static string OptionalFieldAdded => Baseline.Replace(NoteLine, NoteLine + """<xs:element name="Comment" type="xs:string" minOccurs="0"/>""");
    public static string RequiredFieldAdded => Baseline.Replace(NoteLine, NoteLine + """<xs:element name="Currency" type="xs:string"/>""");
    public static string FieldRemoved => Baseline.Replace("""<xs:element name="Quantity" type="xs:int"/>""", "");
    public static string TypeChanged => Baseline.Replace("""name="Quantity" type="xs:int""", """name="Quantity" type="xs:decimal""");
    public static string EnumAdded => Baseline.Replace("""<xs:enumeration value="Closed"/>""", """<xs:enumeration value="Closed"/><xs:enumeration value="Cancelled"/>""");
    public static string EnumRemoved => Baseline.Replace("""<xs:enumeration value="Closed"/>""", "");
    public static string NamespaceChanged => Baseline.Replace("urn:example:order:v1", "urn:example:order:v2");
    public static string CardinalityChanged => Baseline.Replace(NoteLine, """<xs:element name="Note" type="xs:string" minOccurs="0" maxOccurs="unbounded"/>""");
    /// <summary>Customer/Name moves to the root as CustomerName: a removal and an addition, never a guessed rename.</summary>
    public static string HierarchyChanged => Baseline
        .Replace("""<xs:element name="Name" type="xs:string"/>""", """<xs:element name="Since" type="xs:date" minOccurs="0"/>""")
        .Replace("""<xs:element name="Quantity" type="xs:int"/>""", """<xs:element name="CustomerName" type="xs:string"/><xs:element name="Quantity" type="xs:int"/>""");
    public static string OrderChanged => Baseline.Replace("""<xs:element name="Id" type="xs:string"/>""", "")
        .Replace(NoteLine, NoteLine + """<xs:element name="Id" type="xs:string"/>""");
    /// <summary>The same contract, reformatted and commented: no structural change.</summary>
    public static string Reformatted => Baseline.Replace("  ", "    ").Replace("<xs:sequence>", "<!-- members --><xs:sequence>").Replace("\n", "\r\n");

    public static ContractDescriptor Descriptor(string? version = null) => new(ContractId, SourceContractType.XmlSchema, "Order", version, "fixture");

    public static ContractSnapshot Snapshot(string xsd) => ContractSnapshots.FromXmlSchemaText(Descriptor(), "contracts/order.xsd", xsd, At);
}
