using System.IO.Compression;
using System.Text;
using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

public sealed class SchemaStudioServiceTests
{
    private const string Ns = "http://www.w3.org/2001/XMLSchema";
    private const string Basic = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:generic" xmlns="urn:generic" elementFormDefault="qualified">
          <xs:element name="Envelope"><xs:complexType><xs:sequence><xs:element name="Code" minOccurs="1"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="A"/><xs:enumeration value="B"/></xs:restriction></xs:simpleType></xs:element><xs:element name="Optional" minOccurs="0" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    [Fact]
    public void ImportsGenericXsdAndExtractsNamespaceConstraintsAndOccurrences()
    {
        var studio = new SchemaStudioService();
        var package = studio.Import(new Dictionary<string, string> { ["contract.xsd"] = Basic }, "contract.xsd");

        Assert.Equal("urn:generic", Assert.Single(package.Files).TargetNamespace);
        Assert.Equal("Valid", package.Validation);
        Assert.Contains(package.Files[0].Declarations.SelectMany(d => d.Facets), f => f.Kind == "enumeration" && f.Value == "A");
        Assert.Contains(package.Files[0].Declarations.SelectMany(d => d.Children), e => e.Name == "Optional" && e.MinOccurs == "0");
    }

    [Fact]
    public void ResolvesPackageImportButKeepsMissingImportPartial()
    {
        var root = $"<xs:schema xmlns:xs=\"{Ns}\" targetNamespace=\"urn:root\"><xs:import namespace=\"urn:types\" schemaLocation=\"types.xsd\"/><xs:element name=\"root\" type=\"t:Shared\" xmlns:t=\"urn:types\"/></xs:schema>";
        var types = $"<xs:schema xmlns:xs=\"{Ns}\" targetNamespace=\"urn:types\" xmlns=\"urn:types\"><xs:complexType name=\"Shared\"><xs:sequence><xs:element name=\"id\" type=\"xs:string\"/></xs:sequence></xs:complexType></xs:schema>";
        var studio = new SchemaStudioService();
        var resolved = studio.Import(new Dictionary<string, string> { ["main.xsd"] = root, ["types.xsd"] = types }, "linked");
        var missing = new SchemaStudioService().Import(new Dictionary<string, string> { ["main.xsd"] = root }, "missing");

        Assert.Equal("Valid", resolved.Validation);
        Assert.Equal("types.xsd", Assert.Single(resolved.Files[0].Links).ResolvedFile);
        Assert.Equal("Partial", missing.Validation);
    }

    [Fact]
    public void BuildsCrossFileEffectiveMembersInheritanceUsagesAndConstraintCases()
    {
        var main = $"""
            <xs:schema xmlns:xs="{Ns}" xmlns:msg="urn:messaging" xmlns:common="urn:common" targetNamespace="urn:main" xmlns="urn:main" elementFormDefault="qualified">
              <xs:import namespace="urn:messaging" schemaLocation="messaging.xsd"/>
              <xs:import namespace="urn:common" schemaLocation="common.xsd"/>
              <xs:element name="Report" type="ReportType"/>
              <xs:complexType name="ReportType"><xs:complexContent><xs:extension base="msg:MeldingType"><xs:sequence>
                <xs:element name="Client" type="common:ClientType"/><xs:element name="Period" type="xs:string"/>
              </xs:sequence></xs:extension></xs:complexContent></xs:complexType>
            </xs:schema>
            """;
        var messaging = $"""
            <xs:schema xmlns:xs="{Ns}" xmlns:tns="urn:messaging" targetNamespace="urn:messaging" xmlns="urn:messaging" elementFormDefault="qualified">
              <xs:complexType name="MeldingType"><xs:sequence><xs:element name="Id" type="xs:string"/><xs:element name="MessageTypeNamespace" type="xs:string"/></xs:sequence></xs:complexType>
            </xs:schema>
            """;
        var common = $"""
            <xs:schema xmlns:xs="{Ns}" xmlns:tns="urn:common" targetNamespace="urn:common" xmlns="urn:common" elementFormDefault="qualified">
              <xs:simpleType name="Code"><xs:restriction base="xs:string"><xs:enumeration value="A"/><xs:enumeration value="B"/></xs:restriction></xs:simpleType>
              <xs:complexType name="ClientType"><xs:sequence><xs:element name="Identifier" type="xs:string"/><xs:element name="Code" type="tns:Code"/><xs:element name="Comment"><xs:simpleType><xs:restriction base="xs:string"><xs:maxLength value="16"/></xs:restriction></xs:simpleType></xs:element></xs:sequence></xs:complexType>
            </xs:schema>
            """;

        var package = new SchemaStudioService().Import(new Dictionary<string, string>
        {
            ["main.xsd"] = main, ["messaging.xsd"] = messaging, ["common.xsd"] = common
        }, "report");

        Assert.Equal("Valid", package.Validation);
        Assert.Equal(2, package.Files[0].Links.Count);
        Assert.Contains(package.References, r => r.Kind == "extends" && r.Name == "MeldingType" && r.TargetFile == "messaging.xsd");
        Assert.Contains(package.References, r => r.Kind == "uses type" && r.Name == "ClientType" && r.TargetFile == "common.xsd");
        var report = Assert.Single(package.EffectiveSchema);
        Assert.Contains(report.Children, m => m.Name == "Id" && m.InheritedFrom == "MeldingType" && m.DeclaredIn == "messaging.xsd");
        Assert.Contains(report.Children, m => m.Name == "Client" && m.DeclaredIn == "main.xsd" && m.TypeDeclaredIn == "common.xsd");
        Assert.Contains(package.GeneratedTests, t => t.Path.EndsWith("Identifier", StringComparison.Ordinal) && t.Rule == "missing required element");
        Assert.Contains(package.GeneratedTests, t => t.Path.EndsWith("Code", StringComparison.Ordinal) && t.Rule == "known enumeration" && t.Value == "A");
        Assert.Contains(package.GeneratedTests, t => t.Rule == "above maxLength" && t.Value.Length == 17);
    }

    [Fact]
    public void GeneratedLengthBoundaryCasesUseExactValuesAndDoNotInventAnImpossibleBelowZeroCase()
    {
        var minimumSchema = $$"""
            <xs:schema xmlns:xs="{{Ns}}" targetNamespace="urn:length" xmlns="urn:length" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence><xs:element name="Value"><xs:simpleType><xs:restriction base="xs:string"><xs:minLength value="600"/></xs:restriction></xs:simpleType></xs:element></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var minimumPackage = new SchemaStudioService().Import(new Dictionary<string, string> { ["minimum.xsd"] = minimumSchema }, "minimum");
        var minimumBoundary = Assert.Single(minimumPackage.GeneratedTests, test => test.Rule == "minLength boundary");
        Assert.Equal(600, minimumBoundary.Value.Length);
        Assert.True(minimumBoundary.ExpectedValid);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(minimumPackage, "minimum.xsd",
            SchemaStudioService.GenerateTestXml(minimumPackage, minimumBoundary)));
        var belowMinimum = Assert.Single(minimumPackage.GeneratedTests, test => test.Rule == "below minLength");
        Assert.Equal(599, belowMinimum.Value.Length);
        Assert.False(belowMinimum.ExpectedValid);
        Assert.StartsWith("Invalid against", SchemaStudioService.ValidateXml(minimumPackage, "minimum.xsd",
            SchemaStudioService.GenerateTestXml(minimumPackage, belowMinimum)));

        var maximumSchema = $$"""
            <xs:schema xmlns:xs="{{Ns}}" targetNamespace="urn:length" xmlns="urn:length" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence><xs:element name="Value"><xs:simpleType><xs:restriction base="xs:string"><xs:maxLength value="900"/></xs:restriction></xs:simpleType></xs:element></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var maximumPackage = new SchemaStudioService().Import(new Dictionary<string, string> { ["maximum.xsd"] = maximumSchema }, "maximum");
        var maximumBoundary = Assert.Single(maximumPackage.GeneratedTests, test => test.Rule == "maxLength boundary");
        Assert.Equal(900, maximumBoundary.Value.Length);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(maximumPackage, "maximum.xsd",
            SchemaStudioService.GenerateTestXml(maximumPackage, maximumBoundary)));
        var aboveMaximum = Assert.Single(maximumPackage.GeneratedTests, test => test.Rule == "above maxLength");
        Assert.Equal(901, aboveMaximum.Value.Length);
        Assert.StartsWith("Invalid against", SchemaStudioService.ValidateXml(maximumPackage, "maximum.xsd",
            SchemaStudioService.GenerateTestXml(maximumPackage, aboveMaximum)));

        var zeroMinimumSchema = $$"""
            <xs:schema xmlns:xs="{{Ns}}"><xs:element name="Value"><xs:simpleType><xs:restriction base="xs:string"><xs:minLength value="0"/></xs:restriction></xs:simpleType></xs:element></xs:schema>
            """;
        var zeroMinimumPackage = new SchemaStudioService().Import(new Dictionary<string, string> { ["zero.xsd"] = zeroMinimumSchema }, "zero");
        Assert.DoesNotContain(zeroMinimumPackage.GeneratedTests, test => test.Rule == "below minLength");
    }

    [Fact]
    public void SavedTestHistoryDoesNotExposeEnumerationLiteralsThroughGeneratedTestIds()
    {
        const string literal = "sensitive-enumeration-fixture-7f1c";
        var schema = $$"""
            <xs:schema xmlns:xs="{{Ns}}"><xs:element name="Record"><xs:complexType><xs:sequence>
              <xs:element name="Code"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="{{literal}}"/></xs:restriction></xs:simpleType></xs:element>
            </xs:sequence></xs:complexType></xs:element></xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["private-enum.xsd"] = schema }, "private-enum");
        var test = Assert.Single(package.GeneratedTests, item => item.Rule == "known enumeration");
        Assert.Equal(literal, test.Value);
        Assert.DoesNotContain(literal, test.Id, StringComparison.Ordinal);

        var history = new SchemaTestRunHistory();
        history.Add(package, "Generic valid", 1, SchemaStudioService.RunGeneratedTests(package));
        Assert.DoesNotContain(literal, history.SnapshotJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void CapsCrossFileRelationshipsAndMarksTheResultAsTruncated()
    {
        var elements = string.Concat(Enumerable.Range(0, 1_005).Select(i => $"<xs:element name=\"Field{i}\" type=\"t:Shared\"/>"));
        var package = new SchemaStudioService().Import(new Dictionary<string, string>
        {
            ["main.xsd"] = $"<xs:schema xmlns:xs=\"{Ns}\" xmlns:t=\"urn:types\" targetNamespace=\"urn:main\"><xs:import namespace=\"urn:types\" schemaLocation=\"types.xsd\"/><xs:element name=\"Root\" type=\"xs:string\"/><xs:complexType name=\"Unused\"><xs:sequence>{elements}</xs:sequence></xs:complexType></xs:schema>",
            ["types.xsd"] = $"<xs:schema xmlns:xs=\"{Ns}\" targetNamespace=\"urn:types\"><xs:simpleType name=\"Shared\"><xs:restriction base=\"xs:string\"/></xs:simpleType></xs:schema>"
        }, "large");

        Assert.Equal(SchemaStudioService.MaxRelationships, package.References.Count);
        Assert.True(package.ReferencesTruncated);
    }

    [Fact]
    public void CapsGeneratedTestCandidatesDuringDerivationAndMarksTheSetAsTruncated()
    {
        var elements = string.Join("", Enumerable.Range(0, 1600).Select(index =>
            $"<xs:element name=\"Field{index}\" type=\"xs:integer\"/>"));
        var schema = $"<xs:schema xmlns:xs=\"{Ns}\" targetNamespace=\"urn:bounded-tests\" xmlns=\"urn:bounded-tests\" elementFormDefault=\"qualified\"><xs:element name=\"Record\"><xs:complexType><xs:sequence>{elements}</xs:sequence></xs:complexType></xs:element></xs:schema>";
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["bounded.xsd"] = schema }, "bounded");

        Assert.Equal(5000, package.GeneratedTests.Count);
        Assert.True(package.GeneratedTestsTruncated);
    }

    [Fact]
    public void GeneratesProfiledXmlMutationsAndComparesExpectedWithActualValidation()
    {
        var schema = $$"""
            <xs:schema xmlns:xs="{{Ns}}" targetNamespace="urn:profile" xmlns="urn:profile" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence>
                <xs:element name="Code"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="A"/><xs:enumeration value="B"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Comment" minOccurs="0"><xs:simpleType><xs:restriction base="xs:string"><xs:maxLength value="3"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Count"><xs:simpleType><xs:restriction base="xs:decimal"><xs:minExclusive value="0"/><xs:maxInclusive value="9"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="PatternCode"><xs:simpleType><xs:restriction base="xs:string"><xs:pattern value="[A-Z]{2}"/></xs:restriction></xs:simpleType></xs:element>
                <xs:choice><xs:element name="Alpha" type="xs:string"/><xs:element name="Beta" type="xs:string"/></xs:choice>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["profile.xsd"] = schema }, "profile");
        Assert.Contains(Assert.Single(package.EffectiveSchema).Children.Single(m => m.Name == "PatternCode").Facets, f => f.Kind == "pattern" && f.Value == "[A-Z]{2}");
        var valid = Assert.Single(package.GeneratedTests.Where(t => t.Rule == "known enumeration" && t.Value == "A"));
        var validXml = SchemaStudioService.GenerateTestXml(package, valid);
        Assert.Contains(">A</Code>", validXml);
        var validResult = SchemaStudioService.ValidateXml(package, "profile.xsd", validXml);
        Assert.True(validResult.StartsWith("Valid against", StringComparison.Ordinal), validResult + "\n" + validXml);

        var invalidEnum = Assert.Single(package.GeneratedTests.Where(t => t.Rule == "unknown enumeration"));
        var invalidXml = SchemaStudioService.GenerateTestXml(package, invalidEnum);
        Assert.StartsWith("Invalid against", SchemaStudioService.ValidateXml(package, "profile.xsd", invalidXml));

        var profile = new Dictionary<string, string> { ["/Record/Code"] = "B" };
        var unrelatedMutation = Assert.Single(package.GeneratedTests.Where(t => t.Rule == "optional element omitted"));
        Assert.Contains(">B</Code>", SchemaStudioService.GenerateTestXml(package, unrelatedMutation, profile));

        var outcomes = SchemaStudioService.RunGeneratedTests(package, maxTests: 100);
        Assert.Contains(outcomes, r => r.Rule == "known enumeration" && r.ExpectedValid && r.Actual == "Valid" && r.Outcome == "Pass");
        Assert.Contains(outcomes, r => r.Rule == "unknown enumeration" && !r.ExpectedValid && r.Actual == "Invalid" && r.Outcome == "Pass");
        var invalidEnumOutcome = Assert.Single(outcomes.Where(r => r.Rule == "unknown enumeration"));
        var recordedBaseline = Assert.Single(outcomes.Where(result => result.BaselineXml.Length > 0));
        Assert.Equal(recordedBaseline.BaselineId, invalidEnumOutcome.BaselineId);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "profile.xsd", recordedBaseline.BaselineXml));
        Assert.Contains("__unknown_value__", invalidEnumOutcome.Xml);
        Assert.Contains(outcomes, r => r.Rule == "optional element omitted" && r.ExpectedValid && r.Actual == "Valid");
        Assert.Contains(outcomes, r => r.Rule == "pattern match" && r.Actual == "Valid" && r.Outcome == "Pass");
        Assert.Contains(outcomes, r => r.Rule == "pattern non-match" && r.Actual == "Invalid" && r.Outcome == "Pass");
        Assert.Contains(outcomes, r => r.Rule == "wrong namespace" && r.Actual == "Invalid" && r.Outcome == "Pass");
        Assert.Contains(outcomes, r => r.Rule == "wrong namespace prefix binding" && r.Actual == "Invalid" && r.Outcome == "Pass");
        var wrongPrefixBinding = Assert.Single(package.GeneratedTests.Where(test => test.Rule == "wrong namespace prefix binding"));
        var wrongPrefixXml = SchemaStudioService.GenerateTestXml(package, wrongPrefixBinding);
        Assert.Contains("ss:Record", wrongPrefixXml);
        Assert.Contains("xmlns:ss=\"urn:schema-studio:wrong-prefix-binding\"", wrongPrefixXml);
        Assert.StartsWith("Invalid against", SchemaStudioService.ValidateXml(package, "profile.xsd", wrongPrefixXml));
        Assert.True(outcomes.Any(r => r.Rule == "wrong datatype" && r.Actual == "Invalid" && r.Outcome == "Pass"), string.Join("\n", outcomes.Where(r => r.Rule == "wrong datatype")));
        Assert.Contains(outcomes, r => r.Rule == "choice with multiple branches" && r.Actual == "Invalid" && r.Outcome == "Pass");
        var invalidProfileOutcomes = SchemaStudioService.RunGeneratedTests(package, new Dictionary<string, string> { ["/Record/Code"] = "outside-enum" }, maxTests: 2);
        Assert.All(invalidProfileOutcomes, result => Assert.Equal("Not evaluated", result.Outcome));
    }

    [Fact]
    public void ResolvesRequiredAttributesAndGeneratesProfiledAndMissingAttributeCases()
    {
        var schema = $$"""
            <xs:schema xmlns:xs="{{Ns}}" targetNamespace="urn:attribute" xmlns="urn:attribute" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:attribute name="code" type="xs:integer" use="required"/><xs:attribute name="label" type="xs:string"/></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["attributes.xsd"] = schema }, "attributes");
        var root = Assert.Single(package.EffectiveSchema);
        var required = Assert.Single(root.Attributes!, attribute => attribute.Name == "code");
        Assert.Equal("attributes.xsd", required.DeclaredIn);
        Assert.Equal("required", required.Use);

        var missing = Assert.Single(package.GeneratedTests.Where(test => test.Rule == "missing required attribute"));
        var missingXml = SchemaStudioService.GenerateTestXml(package, missing);
        Assert.DoesNotContain("code=", missingXml);
        Assert.StartsWith("Invalid against", SchemaStudioService.ValidateXml(package, "attributes.xsd", missingXml));
        var result = Assert.Single(SchemaStudioService.RunGeneratedTests(package).Where(test => test.Rule == "missing required attribute"));
        Assert.Equal("Pass", result.Outcome);
        var wrongType = Assert.Single(package.GeneratedTests.Where(test => test.Rule == "wrong datatype" && test.Path == "/Record/@code"));
        var wrongTypeXml = SchemaStudioService.GenerateTestXml(package, wrongType);
        Assert.StartsWith("Invalid against", SchemaStudioService.ValidateXml(package, "attributes.xsd", wrongTypeXml));
        Assert.Contains(SchemaStudioService.RunGeneratedTests(package), test => test.Path == "/Record/@code"
            && test.Rule == "wrong datatype" && test.Actual == "Invalid" && test.Outcome == "Pass");

        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("integer attributes", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 42);
        profiles.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed,
            [new SchemaGenerationRule("/Record/@code", SchemaGenerationValueMode.Synthetic, Minimum: 17, Maximum: 17)]);
        var savedProfile = profiles.Profiles.Single();
        var values = profiles.GenerateValues(package, savedProfile);
        var xml = SchemaStudioService.GenerateProfileXml(package, "Record", values, savedProfile);
        Assert.Contains("code=\"17\"", xml);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "attributes.xsd", xml));
    }

    [Fact]
    public void WrongDatatypeCandidatesCoverSignedUnsignedAndFloatingPointXsdTypes()
    {
        var schema = $$"""
            <xs:schema xmlns:xs="{{Ns}}" targetNamespace="urn:datatype-mutations" xmlns="urn:datatype-mutations" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence>
                <xs:element name="Negative" type="xs:negativeInteger"/>
                <xs:element name="NonPositive" type="xs:nonPositiveInteger"/>
                <xs:element name="UnsignedByte" type="xs:unsignedByte"/>
                <xs:element name="UnsignedShort" type="xs:unsignedShort"/>
                <xs:element name="UnsignedInt" type="xs:unsignedInt"/>
                <xs:element name="UnsignedLong" type="xs:unsignedLong"/>
                <xs:element name="FloatValue" type="xs:float"/>
                <xs:element name="DoubleValue" type="xs:double"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["types.xsd"] = schema }, "types.xsd");
        var expectedPaths = new[]
        {
            "/Record/Negative", "/Record/NonPositive", "/Record/UnsignedByte", "/Record/UnsignedShort",
            "/Record/UnsignedInt", "/Record/UnsignedLong", "/Record/FloatValue", "/Record/DoubleValue"
        };

        var mutations = package.GeneratedTests.Where(test => test.Rule == "wrong datatype" && expectedPaths.Contains(test.Path)).ToArray();
        Assert.Equal(expectedPaths.Length, mutations.Length);
        var results = SchemaStudioService.RunGeneratedTests(package, maxTests: 100);
        foreach (var path in expectedPaths)
            Assert.Contains(results, result => result.Path == path && result.Rule == "wrong datatype"
                && !result.ExpectedValid && result.Actual == "Invalid" && result.Outcome == "Pass");
    }

    [Fact]
    public void RejectsDtdAndExternalEntityPayload()
    {
        var malicious = "<!DOCTYPE x [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:annotation><xs:documentation>&xxe;</xs:documentation></xs:annotation></xs:schema>";
        Assert.ThrowsAny<Exception>(() => new SchemaStudioService().Import(new Dictionary<string, string> { ["bad.xsd"] = malicious }, "bad"));
    }

    [Fact]
    public void XMLValidationProhibitsDtdAndEnforcesNodeAndNestingLimits()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string>
        {
            ["instance.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Root\" type=\"xs:string\"/></xs:schema>"
        }, "instance");

        var dtd = "<!DOCTYPE Root [<!ENTITY xxe SYSTEM 'file:///not-read'>]><Root>&xxe;</Root>";
        Assert.StartsWith("Invalid against", SchemaStudioService.ValidateXml(package, "instance.xsd", dtd));

        var deep = "<Root>" + string.Concat(Enumerable.Repeat("<x>", SchemaStudioService.MaxNestingDepth))
            + "v" + string.Concat(Enumerable.Repeat("</x>", SchemaStudioService.MaxNestingDepth)) + "</Root>";
        Assert.Contains("nesting limit", SchemaStudioService.ValidateXml(package, "instance.xsd", deep));

        var manyNodes = "<Root>" + string.Concat(Enumerable.Repeat("<x/>", SchemaStudioService.MaxXmlNodes)) + "</Root>";
        Assert.Contains("node validation limit", SchemaStudioService.ValidateXml(package, "instance.xsd", manyNodes));
    }

    [Fact]
    public void XMLGenerationStopsAtTheBoundedNodeLimitAndReportsMutationAsNotEvaluated()
    {
        var schema = $"""
            <xs:schema xmlns:xs="{Ns}">
              <xs:element name="Root"><xs:complexType><xs:sequence>
                <xs:element name="A" minOccurs="100" maxOccurs="100"><xs:complexType><xs:sequence>
                  <xs:element name="B" minOccurs="100" maxOccurs="100"><xs:complexType><xs:sequence>
                    <xs:element name="C" minOccurs="100" maxOccurs="100"><xs:complexType><xs:sequence>
                      <xs:element name="Value" type="xs:string" minOccurs="100" maxOccurs="100"/>
                    </xs:sequence></xs:complexType></xs:element>
                  </xs:sequence></xs:complexType></xs:element>
                </xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["large-output.xsd"] = schema }, "large-output.xsd");
        Assert.Equal("Valid", package.Validation);

        var results = SchemaStudioService.RunGeneratedTests(package, maxTests: 1);

        var result = Assert.Single(results);
        Assert.Equal("Not evaluated", result.Actual);
        Assert.Equal("Not evaluated", result.Outcome);
        Assert.Contains("node generation limit", result.Diagnostic);
        Assert.Empty(result.Xml);
    }

    [Fact]
    public void ZipImportRejectsTraversalAndReadsLinkedFilesWithoutExtraction()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../evil.xsd").Open()); writer.Write(Basic);
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => SchemaStudioService.ReadZip(stream));
    }

    [Fact]
    public void GeneratedXmlIsValidatedLocallyAndVersionsRemainSeparate()
    {
        var studio = new SchemaStudioService();
        var first = studio.Import(new Dictionary<string, string> { ["contract.xsd"] = Basic }, "contract.xsd");
        var xml = SchemaStudioService.GenerateMinimumXml(first.Files[0]);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(first, "contract.xsd", xml));
        var second = studio.Import(new Dictionary<string, string> { ["contract.xsd"] = Basic.Replace("value=\"B\"", "value=\"C\"", StringComparison.Ordinal) }, "contract.xsd");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, studio.Packages.Count);
    }
}
