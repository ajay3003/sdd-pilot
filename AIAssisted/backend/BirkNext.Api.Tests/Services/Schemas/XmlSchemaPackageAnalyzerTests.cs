using BirkNext.Schemas;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.Schemas;

/// <summary>The shared deterministic XSD capability: parser/effective model, validator, dependency resolution, provenance and safety.</summary>
public sealed class XmlSchemaPackageAnalyzerTests
{
    private const string Ns = "urn:example:matrix:v1";

    private const string Matrix = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns="urn:example:matrix:v1" targetNamespace="urn:example:matrix:v1" elementFormDefault="qualified" version="1.4.0">
          <xs:simpleType name="CodeType">
            <xs:annotation><xs:documentation>Kode fra kodeverket</xs:documentation></xs:annotation>
            <xs:restriction base="xs:string">
              <xs:enumeration value="A"><xs:annotation><xs:documentation>Alfa</xs:documentation></xs:annotation></xs:enumeration>
              <xs:enumeration value="B"/>
            </xs:restriction>
          </xs:simpleType>
          <xs:simpleType name="ElevenDigits"><xs:restriction base="xs:string"><xs:pattern value="\d{11}"/></xs:restriction></xs:simpleType>
          <xs:complexType name="BaseType">
            <xs:sequence><xs:element name="Id" type="xs:string"/></xs:sequence>
          </xs:complexType>
          <xs:complexType name="DerivedType">
            <xs:complexContent><xs:extension base="BaseType"><xs:sequence>
              <xs:element name="Name"><xs:simpleType><xs:restriction base="xs:string"><xs:minLength value="2"/><xs:maxLength value="70"/></xs:restriction></xs:simpleType></xs:element>
            </xs:sequence></xs:extension></xs:complexContent>
          </xs:complexType>
          <xs:complexType name="RestrictedType">
            <xs:complexContent><xs:restriction base="BaseType"><xs:sequence><xs:element name="Id" type="xs:string"/></xs:sequence></xs:restriction></xs:complexContent>
          </xs:complexType>
          <xs:element name="Order">
            <xs:annotation><xs:documentation>Bestilling</xs:documentation></xs:annotation>
            <xs:complexType>
              <xs:sequence>
                <xs:element name="Party" type="DerivedType"/>
                <xs:element name="Restricted" type="RestrictedType" minOccurs="0"/>
                <xs:element name="Count" type="xs:integer"/>
                <xs:element name="Amount" type="xs:decimal" minOccurs="0"/>
                <xs:element name="Hours" type="xs:double"/>
                <xs:element name="Active" type="xs:boolean"/>
                <xs:element name="Day" type="xs:date"/>
                <xs:element name="At" type="xs:dateTime"/>
                <xs:element name="Code" type="CodeType"/>
                <xs:element name="Identifier" type="ElevenDigits" minOccurs="0"/>
                <xs:element name="Quantity"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="1"/><xs:maxInclusive value="10"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Line" minOccurs="0" maxOccurs="unbounded">
                  <xs:complexType><xs:sequence><xs:element name="Text" type="xs:string"/></xs:sequence></xs:complexType>
                </xs:element>
                <xs:choice>
                  <xs:element name="Email" type="xs:string"/>
                  <xs:element name="Phone" type="xs:string"/>
                </xs:choice>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
          <xs:element name="Receipt" type="BaseType"/>
        </xs:schema>
        """;

    private const string ValidOrder = """
        <Order xmlns="urn:example:matrix:v1">
          <Party><Id>p1</Id><Name>Ola</Name></Party><Count>3</Count><Hours>1.5</Hours><Active>true</Active><Day>2024-01-01</Day>
          <At>2024-01-01T08:00:00Z</At><Code>A</Code><Quantity>5</Quantity><Email>x</Email>
        </Order>
        """;

    private static XmlSchemaAnalysis Analyze(params (string Name, string Text)[] files) =>
        XmlSchemaPackageAnalyzer.Analyze(files.ToDictionary(f => f.Name, f => f.Text, StringComparer.OrdinalIgnoreCase));

    private static EffectiveMember Member(EffectiveMember root, string path)
    {
        var current = root;
        foreach (var part in path.Split('/')) current = current.Children.Single(child => child.Name == part);
        return current;
    }

    // ── Parser / effective model matrix ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EffectiveModel_ResolvesTypesOccurrenceFacetsInheritanceChoiceRootsNamespaceVersionAndProvenance()
    {
        var analysis = Analyze(("matrix.xsd", Matrix));
        analysis.Validation.Should().Be("Valid");
        analysis.EffectiveSchema.Select(r => r.Name).Should().Equal("Order", "Receipt"); // global elements; multiple roots kept, none guessed
        var order = analysis.EffectiveSchema[0];
        order.NamespaceUri.Should().Be(Ns);
        order.Documentation.Should().Be("Bestilling");
        analysis.Identity.Version.Should().Be("1.4.0");
        analysis.Identity.TargetNamespace.Should().Be(Ns);
        analysis.Files.Single().Version.Should().Be("1.4.0");

        Member(order, "Count").MinOccurs.Should().Be("1");                 // required
        Member(order, "Amount").MinOccurs.Should().Be("0");                // optional
        Member(order, "Line").MaxOccurs.Should().Be("unbounded");          // repeated
        Member(order, "Line/Text").Type.Should().Be("xs:string");          // nested inline type
        Member(order, "Count").BaseType.Should().Be("xs:integer");
        Member(order, "Amount").BaseType.Should().Be("xs:decimal");
        Member(order, "Hours").BaseType.Should().Be("xs:double");
        Member(order, "Active").BaseType.Should().Be("xs:boolean");
        Member(order, "Day").BaseType.Should().Be("xs:date");
        Member(order, "At").BaseType.Should().Be("xs:dateTime");

        var code = Member(order, "Code");
        code.QualifiedType.Should().Be($"{{{Ns}}}CodeType");               // type reference, prefix-independent
        code.BaseType.Should().Be("xs:string");                             // simple type resolved to its built-in base
        code.Facets.Where(f => f.Kind == "enumeration").Select(f => (f.Value, f.Documentation)).Should().Equal(("A", "Alfa"), ("B", null));
        code.TypeDocumentation.Should().Be("Kode fra kodeverket");          // annotation preserved verbatim
        Member(order, "Identifier").Facets.Should().ContainSingle(f => f.Kind == "pattern" && f.Value == @"\d{11}");
        Member(order, "Party/Name").Facets.Select(f => f.Kind).Should().BeEquivalentTo(["minLength", "maxLength"]);
        Member(order, "Quantity").Facets.Select(f => $"{f.Kind}={f.Value}").Should().BeEquivalentTo(["minInclusive=1", "maxInclusive=10"]);

        var party = Member(order, "Party");
        party.Children.Select(c => c.Name).Should().Equal("Id", "Name");   // xs:extension: inherited members first
        party.Children[0].InheritedFrom.Should().Be("BaseType");
        party.Children[1].InheritedFrom.Should().BeNull();
        Member(order, "Restricted").Children.Select(c => c.Name).Should().Equal("Id"); // xs:restriction keeps the restricted content

        Member(order, "Email").InChoice.Should().BeTrue();                  // real xs:choice
        Member(order, "Count").InChoice.Should().BeFalse();
        order.Children.Should().OnlyContain(member => member.DeclaredIn == "matrix.xsd");
    }

    [Fact]
    public void ParentFacetsAreNotCopiedFromNestedChildren()
    {
        var order = Analyze(("matrix.xsd", Matrix)).EffectiveSchema[0];
        order.Facets.Should().BeEmpty("the root's own type has no restriction; children's enumerations belong to the children");
        Member(order, "Line").Facets.Should().BeEmpty();
    }

    [Fact]
    public void GeneratedChoiceNamingConvention_IsNotTreatedAsXsChoice()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Klient"><xs:complexType><xs:sequence>
                <xs:element name="CHOICE_ELEMENT_Fodselsnummer__DUFnummer" type="xs:string" minOccurs="0"/>
                <xs:element name="Fodselsnummer" type="xs:string" minOccurs="0"/>
                <xs:element name="DUFnummer" type="xs:string" minOccurs="0"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var root = Analyze(("klient.xsd", xsd)).EffectiveSchema.Single();
        root.Children.Should().OnlyContain(member => !member.InChoice, "exclusivity is never inferred from a CHOICE_ELEMENT_* name");
    }

    // ── Validator matrix ─────────────────────────────────────────────────────────────────────────────────────────

    private static XmlSchemaValidationResult Validate(string xml, string? root = null) =>
        XmlSchemaPackageAnalyzer.ValidateXml(new Dictionary<string, string> { ["matrix.xsd"] = Matrix }, xml, root);

    [Fact]
    public void Validator_ValidXml_IsValid() => Validate(ValidOrder).Status.Should().Be(XmlSchemaValidationStatus.Valid);

    [Fact]
    public void Validator_MissingRequiredElement_IsInvalid() =>
        Validate(ValidOrder.Replace("<Count>3</Count>", "")).Status.Should().Be(XmlSchemaValidationStatus.Invalid);

    [Fact]
    public void Validator_WrongEnumeration_IsInvalid() =>
        Validate(ValidOrder.Replace("<Code>A</Code>", "<Code>Z</Code>")).Status.Should().Be(XmlSchemaValidationStatus.Invalid);

    [Fact]
    public void Validator_WrongPattern_IsInvalid_AndIssueDoesNotEchoTheValue()
    {
        var result = Validate(ValidOrder.Replace("<Quantity>", "<Identifier>12345</Identifier><Quantity>"));
        result.Status.Should().Be(XmlSchemaValidationStatus.Invalid);
        string.Join(" ", result.Issues).Should().NotContain("12345", "validation issues never carry instance values");
    }

    [Fact]
    public void Validator_WrongNamespace_IsInvalid() =>
        Validate(ValidOrder.Replace("urn:example:matrix:v1", "urn:example:other")).Status.Should().Be(XmlSchemaValidationStatus.Invalid);

    [Fact]
    public void Validator_WrongRoot_IsInvalid_BothUndeclaredAndNotSelected()
    {
        Validate("<Unknown xmlns=\"urn:example:matrix:v1\"/>").Status.Should().Be(XmlSchemaValidationStatus.Invalid);
        Validate(ValidOrder, root: "Receipt").Message.Should().Contain("not the selected root 'Receipt'");
    }

    [Fact]
    public void Validator_MalformedXml_IsInvalidWithSafeMessage() =>
        Validate("<Order xmlns=\"urn:example:matrix:v1\"><Count>").Status.Should().Be(XmlSchemaValidationStatus.Invalid);

    [Fact]
    public void Validator_RefusesDtdAndExternalEntities()
    {
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE Order [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><Order xmlns=\"urn:example:matrix:v1\">&x;</Order>";
        var result = Validate(xxe);
        result.Status.Should().Be(XmlSchemaValidationStatus.Invalid);
        result.Message.Should().Contain("not well formed");
        result.Message.Should().NotContain("[fonts]", "no external entity is ever expanded");
        var dtdSchema = "<!DOCTYPE xs:schema [<!ENTITY e \"x\">]><xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"/>";
        var act = () => XmlSchemaPackageAnalyzer.Analyze(new Dictionary<string, string> { ["dtd.xsd"] = dtdSchema });
        act.Should().Throw<System.Xml.XmlException>("DTDs are prohibited in schemas too");
    }

    [Fact]
    public void Validator_OversizedAndDeepInputs_AreNotEvaluated()
    {
        var huge = "<Order xmlns=\"urn:example:matrix:v1\">" + new string('x', XmlSchemaPackageAnalyzer.MaxPackageBytes + 10) + "</Order>";
        Validate(huge).Status.Should().Be(XmlSchemaValidationStatus.NotEvaluated);
        var deep = string.Concat(Enumerable.Repeat("<a>", 80)) + string.Concat(Enumerable.Repeat("</a>", 80));
        Validate(deep).Status.Should().Be(XmlSchemaValidationStatus.NotEvaluated);
    }

    [Fact]
    public void Validator_ImportedAndIncludedTypes_AreResolvedInsideThePackage()
    {
        var files = new Dictionary<string, string>
        {
            ["main.xsd"] = """
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common" xmlns="urn:main" targetNamespace="urn:main" elementFormDefault="qualified">
                  <xs:import namespace="urn:common" schemaLocation="lib/common.xsd"/>
                  <xs:include schemaLocation="parts.xsd"/>
                  <xs:element name="Doc"><xs:complexType><xs:sequence>
                    <xs:element name="When" type="c:StampType"/><xs:element name="Part" type="PartType"/>
                  </xs:sequence></xs:complexType></xs:element>
                </xs:schema>
                """,
            ["parts.xsd"] = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:main" elementFormDefault="qualified"><xs:simpleType name="PartType"><xs:restriction base="xs:string"><xs:enumeration value="P"/></xs:restriction></xs:simpleType></xs:schema>""",
            ["lib/common.xsd"] = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common"><xs:simpleType name="StampType"><xs:restriction base="xs:dateTime"/></xs:simpleType></xs:schema>""",
        };
        XmlSchemaPackageAnalyzer.ValidateXml(files, "<Doc xmlns=\"urn:main\"><When>2024-01-01T00:00:00Z</When><Part>P</Part></Doc>").Status
            .Should().Be(XmlSchemaValidationStatus.Valid);
        XmlSchemaPackageAnalyzer.ValidateXml(files, "<Doc xmlns=\"urn:main\"><When>not a date</When><Part>P</Part></Doc>").Status
            .Should().Be(XmlSchemaValidationStatus.Invalid);

        var analysis = XmlSchemaPackageAnalyzer.Analyze(files);
        var doc = analysis.EffectiveSchema.Single();
        doc.Children[0].TypeDeclaredIn.Should().Be("lib/common.xsd");       // provenance across xs:import
        doc.Children[0].BaseType.Should().Be("xs:dateTime");
        doc.Children[1].TypeDeclaredIn.Should().Be("parts.xsd");            // provenance across xs:include (same namespace)
        doc.Children[1].Facets.Should().ContainSingle(f => f.Value == "P");
        analysis.Files.Single(f => f.Name == "main.xsd").Links.Should().OnlyContain(l => l.EffectiveStatus == SchemaDependencyStatus.Resolved);
        analysis.Files.Single(f => f.Name == "lib/common.xsd").TargetNamespace.Should().Be("urn:common", "imported namespaces are kept separate");
    }

    [Fact]
    public void Validator_UnresolvedImport_IsDependencyUnresolved_NeverGuessed()
    {
        var files = new Dictionary<string, string>
        {
            ["main.xsd"] = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common" targetNamespace="urn:main"><xs:import namespace="urn:common" schemaLocation="missing.xsd"/><xs:element name="Doc" type="c:T"/></xs:schema>""",
        };
        var result = XmlSchemaPackageAnalyzer.ValidateXml(files, "<Doc xmlns=\"urn:main\"/>");
        result.Status.Should().Be(XmlSchemaValidationStatus.DependencyUnresolved);
        result.Issues.Should().ContainSingle().Which.Should().Contain("missing.xsd");
        var analysis = XmlSchemaPackageAnalyzer.Analyze(files);
        analysis.Validation.Should().Be("Partial");
        analysis.UnresolvedReferences.Should().ContainSingle(r => r.QualifiedName == "{urn:common}T");
    }

    // ── Dependency matrix ───────────────────────────────────────────────────────────────────────────────────────

    private static string Schema(string targetNamespace, string body) =>
        $"<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"{targetNamespace}\">{body}</xs:schema>";

    [Fact]
    public void Dependencies_NestedAndDuplicateImports_ResolveEachFileOnceAndKeepEveryEdge()
    {
        var analysis = Analyze(
            ("a.xsd", Schema("urn:a", "<xs:import namespace=\"urn:b\" schemaLocation=\"b.xsd\"/><xs:import namespace=\"urn:c\" schemaLocation=\"c.xsd\"/>")),
            ("b.xsd", Schema("urn:b", "<xs:import namespace=\"urn:c\" schemaLocation=\"c.xsd\"/>")),
            ("c.xsd", Schema("urn:c", "")));
        analysis.Files.Should().HaveCount(3, "a schema reached twice is still one canonical file");
        analysis.Files.SelectMany(f => f.Links).Count(l => l.ResolvedFile == "c.xsd").Should().Be(2, "both incoming edges are preserved");
        analysis.Validation.Should().Be("Valid");
    }

    [Fact]
    public void Dependencies_Circular_IsDetectedWithoutInfiniteRecursion()
    {
        var analysis = Analyze(
            ("a.xsd", Schema("urn:a", "<xs:import namespace=\"urn:b\" schemaLocation=\"b.xsd\"/>")),
            ("b.xsd", Schema("urn:b", "<xs:import namespace=\"urn:a\" schemaLocation=\"a.xsd\"/>")));
        analysis.Files.SelectMany(f => f.Links).Should().ContainSingle(l => l.EffectiveStatus == SchemaDependencyStatus.Circular)
            .Which.Reason.Should().Contain("Circular dependency detected");
        analysis.Files.SelectMany(f => f.Links).Should().OnlyContain(l => l.ResolvedFile != null, "the cyclic edge is kept, not dropped");
    }

    [Fact]
    public void Dependencies_MissingLocationMissingFileNamespaceMismatchAndRedefine_AreExplicit()
    {
        var analysis = Analyze(
            ("main.xsd", Schema("urn:main",
                "<xs:import namespace=\"urn:nolocation\"/>" +
                "<xs:import namespace=\"urn:missing\" schemaLocation=\"missing.xsd\"/>" +
                "<xs:import namespace=\"urn:expected\" schemaLocation=\"other.xsd\"/>" +
                "<xs:redefine schemaLocation=\"same.xsd\"/>")),
            ("other.xsd", Schema("urn:actual", "")),
            ("same.xsd", Schema("urn:main", "")));
        var links = analysis.Files.Single(f => f.Name == "main.xsd").Links;
        links[0].EffectiveStatus.Should().Be(SchemaDependencyStatus.Unresolved);
        links[0].Reason.Should().Contain("No schemaLocation");
        links[1].EffectiveStatus.Should().Be(SchemaDependencyStatus.Unresolved);
        links[1].Reason.Should().Contain("not in this package");
        links[2].EffectiveStatus.Should().Be(SchemaDependencyStatus.NamespaceMismatch);
        links[3].EffectiveStatus.Should().Be(SchemaDependencyStatus.Unsupported, "xs:redefine is not faked in the readable model");
        analysis.Validation.Should().Be("Partial");
    }

    [Theory]
    [InlineData("sub/../types.xsd", true)]
    [InlineData("./types.xsd", true)]
    [InlineData("../../etc/passwd.xsd", false)]
    [InlineData("/etc/types.xsd", false)]
    [InlineData("C:\\windows\\types.xsd", false)]
    public void Dependencies_RelativePathsResolveAndTraversalIsRefused(string location, bool resolves)
    {
        var analysis = Analyze(("main.xsd", Schema("urn:main", $"<xs:include schemaLocation=\"{location}\"/>")), ("types.xsd", Schema("urn:main", "")));
        analysis.Files.Single(f => f.Name == "main.xsd").Links.Single().ResolvedFile.Should().Be(resolves ? "types.xsd" : null);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/schema.xsd")]
    [InlineData("https://localhost:5001/schema.xsd")]
    [InlineData("http://10.0.0.5/schema.xsd")]
    [InlineData("file:///c:/schemas/schema.xsd")]
    [InlineData("ftp://example.invalid/schema.xsd")]
    public void Dependencies_ExternalLocationsAreNeverFetched(string location)
    {
        var files = new Dictionary<string, string> { ["main.xsd"] = Schema("urn:main", $"<xs:import namespace=\"urn:x\" schemaLocation=\"{location}\"/><xs:element name=\"Doc\" type=\"xs:string\"/>") };
        var analysis = XmlSchemaPackageAnalyzer.Analyze(files);
        analysis.Files.Single().Links.Single().EffectiveStatus.Should().Be(SchemaDependencyStatus.External);
        analysis.Validation.Should().Be("Partial");
        XmlSchemaPackageAnalyzer.ValidateXml(files, "<Doc xmlns=\"urn:main\">x</Doc>").Status.Should().Be(XmlSchemaValidationStatus.DependencyUnresolved,
            "validation is refused rather than resolving anything outside the package (no SSRF, no file access)");
    }

    [Fact]
    public void Identity_FingerprintIsStableAcrossOrderAndChangesWithContent()
    {
        var a = Analyze(("a.xsd", Schema("urn:a", "")), ("b.xsd", Schema("urn:b", "")));
        var b = Analyze(("b.xsd", Schema("urn:b", "")), ("a.xsd", Schema("urn:a", "")));
        a.Fingerprint.Should().Be(b.Fingerprint);
        Analyze(("a.xsd", Schema("urn:a", "<xs:element name=\"X\" type=\"xs:string\"/>")), ("b.xsd", Schema("urn:b", ""))).Fingerprint.Should().NotBe(a.Fingerprint);
        Analyze(("a.xsd", Schema("urn:a", ""))).Identity.PrimaryFile.Should().Be("a.xsd");
    }

    [Fact]
    public void Identity_VersionIsFirstClass()
    {
        var v1 = Analyze(("s.xsd", Matrix));
        var v2 = Analyze(("s.xsd", Matrix.Replace("version=\"1.4.0\"", "version=\"2.0.0\"")));
        v1.Identity.Version.Should().Be("1.4.0");
        v2.Identity.Version.Should().Be("2.0.0");
        v2.Fingerprint.Should().NotBe(v1.Fingerprint, "a version-only change is a different schema identity");
    }

    [Fact]
    public void Analyze_RefusesUnsafeNamesAndOversizedPackages()
    {
        var unsafeName = () => XmlSchemaPackageAnalyzer.Analyze(new Dictionary<string, string> { ["../x.xsd"] = Schema("urn:a", "") });
        unsafeName.Should().Throw<InvalidDataException>();
        var big = () => XmlSchemaPackageAnalyzer.Analyze(new Dictionary<string, string> { ["x.xsd"] = new string(' ', XmlSchemaPackageAnalyzer.MaxFileBytes + 1) });
        big.Should().Throw<InvalidDataException>();
    }
}
