using System.Xml.Linq;
using BirkNext.Schemas;
using BirkNext.Web.Services.SchemaStudio;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Schema Studio → Validate &amp; Test: the minimum example is generated recursively from the effective schema and self-validated with the same
/// local validator (never root-only, never "valid" without validating); every expected-valid case validates and every deterministic mutation
/// is rejected; cases group into canonical rule families with exact totals; the representative sample is deterministic and diverse; coverage
/// keeps discovered / generated / executed separate; registration never approves and never accepts invalid XML as a positive fixture.
/// </summary>
public sealed class SchemaValidateAndTestTests
{
    private const string Skolenaervaer = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd";
    private const string Ns = "http://www.w3.org/2001/XMLSchema";

    private static string RealSchema(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AIAssisted", "xsd"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("AIAssisted/xsd"), "AIAssisted", "xsd", name));
    }

    /// <summary>Nested named types across an import, a sequence, a required choice, occurrences, attributes, enum, length, numeric and date facets.</summary>
    private static readonly Dictionary<string, string> Fixture = new()
    {
        ["main.xsd"] = $"""
            <xs:schema xmlns:xs="{Ns}" xmlns:c="urn:common" targetNamespace="urn:main" xmlns="urn:main" elementFormDefault="qualified">
              <xs:import namespace="urn:common" schemaLocation="common.xsd"/>
              <xs:element name="Report" type="ReportType"/>
              <xs:complexType name="ReportType">
                <xs:sequence>
                  <xs:element name="Header" type="HeaderType"/>
                  <xs:choice>
                    <xs:element name="Person" type="c:PartyType"/>
                    <xs:element name="Organisation" type="c:PartyType"/>
                  </xs:choice>
                  <xs:element name="Line" type="LineType" minOccurs="2" maxOccurs="4"/>
                  <xs:element name="Note" type="xs:string" minOccurs="0"/>
                </xs:sequence>
                <xs:attribute name="version" use="required"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="2.0"/></xs:restriction></xs:simpleType></xs:attribute>
                <xs:attribute name="channel" type="xs:string" use="optional"/>
              </xs:complexType>
              <xs:complexType name="HeaderType"><xs:sequence>
                <xs:element name="Sent" type="xs:dateTime"/>
                <xs:element name="Period" type="xs:date"/>
                <xs:element name="Code"><xs:simpleType><xs:restriction base="xs:string"><xs:minLength value="4"/><xs:maxLength value="8"/></xs:restriction></xs:simpleType></xs:element>
              </xs:sequence></xs:complexType>
              <xs:complexType name="LineType"><xs:sequence>
                <xs:element name="Amount"><xs:simpleType><xs:restriction base="xs:decimal"><xs:minInclusive value="1"/><xs:maxInclusive value="100"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Kind"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="A"/><xs:enumeration value="B"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Count" type="xs:integer"/>
                <xs:element name="Active" type="xs:boolean"/>
              </xs:sequence></xs:complexType>
            </xs:schema>
            """,
        ["common.xsd"] = $$"""
            <xs:schema xmlns:xs="{{Ns}}" targetNamespace="urn:common" xmlns="urn:common" elementFormDefault="qualified">
              <xs:complexType name="PartyType"><xs:sequence>
                <xs:element name="Name"><xs:simpleType><xs:restriction base="xs:string"><xs:length value="3"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Municipality"><xs:simpleType><xs:restriction base="xs:string"><xs:pattern value="[0-9]{4}"/></xs:restriction></xs:simpleType></xs:element>
              </xs:sequence></xs:complexType>
            </xs:schema>
            """,
    };

    private static SchemaPackage Package(IReadOnlyDictionary<string, string> files) => new SchemaStudioService().Import(files, "fixture");

    private static IEnumerable<string> RequiredDescendants(EffectiveMember member)
    {
        foreach (var child in member.Children.Where(c => c.MinOccurs != "0" && c.Compositor != "choice"))
        {
            yield return child.Name;
            foreach (var nested in RequiredDescendants(child)) yield return nested;
        }
    }

    // ── SCHEMA-BLOCKER-01 ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Skolenaervaer_minimum_example_is_recursive_self_validated_and_bound_to_the_exact_schema()
    {
        var package = Package(new Dictionary<string, string> { [Skolenaervaer] = RealSchema(Skolenaervaer) });
        var result = SchemaStudioService.GenerateMinimumValid(package, Skolenaervaer);

        result.State.Should().Be(SchemaGenerationState.SchemaValid, string.Join(" | ", result.Reasons));
        SchemaStudioService.Validate(package, Skolenaervaer, result.Xml).Status.Should().Be(XmlSchemaValidationStatus.Valid, "the same local validator agrees");
        var document = XDocument.Parse(result.Xml);
        document.Root!.Name.LocalName.Should().Be("Skolenaervaer");
        document.Root.Elements().Should().NotBeEmpty("the minimum example is never root-only");
        var root = package.EffectiveSchema.Single(r => r.Name == "Skolenaervaer");
        var required = RequiredDescendants(root).Distinct().ToList();
        required.Should().NotBeEmpty();
        var generated = document.Descendants().Select(e => e.Name.LocalName).ToHashSet();
        required.Should().OnlyContain(name => generated.Contains(name), "every required descendant (derived from the parsed model) is generated");
        result.Xml.Should().NotContain("Henvisning", "only the selected schema and its dependencies are used");
        result.Provenance.RootElement.Should().Be("Skolenaervaer");
        result.Provenance.SourceXsd.Should().Be(Skolenaervaer);
        result.Provenance.SchemaFingerprint.Should().Be(package.Files.Single().Fingerprint);
        result.Provenance.PackageFingerprint.Should().Be(package.Fingerprint);
        result.Provenance.GeneratorVersion.Should().Be(SchemaTestDesign.GeneratorVersion);
        result.Heading.Should().Be("Schema-valid");
    }

    [Fact]
    public void Skolenaervaer_cases_stay_in_that_schema_and_every_case_behaves_as_the_schema_expects()
    {
        var package = Package(new Dictionary<string, string> { [Skolenaervaer] = RealSchema(Skolenaervaer) });
        package.GeneratedTests.Should().OnlyContain(t => t.SourceFile == Skolenaervaer && t.Path.StartsWith("/Skolenaervaer", StringComparison.Ordinal));

        var results = SchemaStudioService.RunCases(package, package.GeneratedTests);

        results.Where(r => r.ExpectedValid).Should().OnlyContain(r => r.Outcome == "Pass", "every generated expected-valid case must pass the local validator");
        results.Where(r => SchemaTestDesign.Classify(r) is SchemaResultClass.GeneratorDefect or SchemaResultClass.MutationDefect)
            .Select(r => $"{r.Rule} @ {r.Path} = {package.GeneratedTests.Single(t => t.Id == r.TestId).Value}").Should().BeEmpty();
    }

    [Fact]
    public void Minimum_example_covers_nested_named_types_imports_sequence_choice_occurrences_attributes_and_facets()
    {
        var package = Package(Fixture);
        var result = SchemaStudioService.GenerateMinimumValid(package, "main.xsd");

        result.State.Should().Be(SchemaGenerationState.SchemaValid, string.Join(" | ", result.Reasons));
        var root = XDocument.Parse(result.Xml).Root!;
        XNamespace main = "urn:main", common = "urn:common";
        root.Name.Should().Be(main + "Report");
        root.Elements().Select(e => e.Name.LocalName).Should().Equal(["Header", "Person", "Line", "Line"], "sequence order, one deterministic choice branch, minOccurs=2, optional Note omitted");
        root.Attribute("version")!.Value.Should().Be("2.0", "required attribute with its enumeration");
        root.Attribute("channel").Should().BeNull("optional attributes are omitted");
        root.Element(main + "Header")!.Element(main + "Code")!.Value.Length.Should().BeInRange(4, 8);
        root.Element(main + "Person")!.Element(common + "Name")!.Value.Should().HaveLength(3, "the imported type's facets apply");
        root.Element(main + "Person")!.Element(common + "Municipality")!.Value.Should().MatchRegex("^[0-9]{4}$");
        result.Choices.Should().ContainSingle(c => c.Contains("/Report/Person") && c.Contains("first declared of Person | Organisation"));
    }

    [Fact]
    public void An_unresolved_dependency_blocks_generation_instead_of_emitting_a_supposedly_valid_document()
    {
        var package = Package(new Dictionary<string, string> { ["main.xsd"] = Fixture["main.xsd"] });
        var result = SchemaStudioService.GenerateMinimumValid(package, "main.xsd");

        result.State.Should().Be(SchemaGenerationState.Blocked);
        result.IsSchemaValid.Should().BeFalse();
        result.Xml.Should().BeEmpty();
        result.Reasons[0].Should().Be("Generation blocked · required schema dependency unresolved");
        result.Heading.Should().Be("Generation blocked");
    }

    [Fact]
    public void A_library_without_a_message_root_is_blocked_with_a_reason()
    {
        var package = Package(new Dictionary<string, string> { ["common.xsd"] = Fixture["common.xsd"] });
        var result = SchemaStudioService.GenerateMinimumValid(package, "common.xsd");
        result.State.Should().Be(SchemaGenerationState.Blocked);
        result.Reasons.Should().ContainSingle(r => r.Contains("no top-level message element"));
    }

    [Fact]
    public void Generated_xml_that_fails_validation_is_never_labeled_minimum_valid()
    {
        var failed = new SchemaGenerationResult(SchemaGenerationState.FailedValidation, "<Report/>", ["Generation failed validation", "missing Header"], [],
            new("p", "f", null, null, "main.xsd", null, "Report", "id", "Minimum valid", "built-in", SchemaTestDesign.GeneratorVersion, null, true, DateTimeOffset.UnixEpoch, "Invalid"));
        failed.IsSchemaValid.Should().BeFalse();
        failed.Heading.Should().Be("Could not generate a valid example");
        failed.XmlLabel.Should().Be("Partial generated XML · not schema-valid");
    }

    // ── Invariants over fixture schemas ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_expected_valid_case_validates_and_every_mutation_is_rejected()
    {
        var package = Package(Fixture);
        package.GeneratedTests.Should().NotBeEmpty();
        var results = SchemaStudioService.RunCases(package, package.GeneratedTests);

        results.Should().HaveCount(package.GeneratedTests.Count);
        results.Where(r => r.ExpectedValid).Should().OnlyContain(r => r.Outcome == "Pass", "expected-valid generated XML must pass the local validator");
        results.Where(r => !r.ExpectedValid).Should().OnlyContain(r => r.Outcome == "Pass", "a deterministic mutation must be rejected");
        results.Where(r => !r.ExpectedValid).Should().OnlyContain(r => r.IssueCount >= 1);
    }

    // ── Grouping, sampling, coverage ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cases_group_into_canonical_families_with_exact_totals()
    {
        var package = Package(new Dictionary<string, string> { [Skolenaervaer] = RealSchema(Skolenaervaer) });
        var groups = package.GeneratedTests.GroupBy(SchemaRuleFamilies.Of).ToList();
        groups.Sum(g => g.Count()).Should().Be(package.GeneratedTests.Count, "grouping never loses cases");
        groups.Should().NotContain(g => g.Key == SchemaRuleFamily.Other, "every generated rule key maps to a canonical family");
        groups.Select(g => g.Key).Should().Contain([SchemaRuleFamily.Namespace, SchemaRuleFamily.RequiredOptional, SchemaRuleFamily.LengthBoundaries]);
        SchemaRuleFamilies.Of(new("x", "/R/@a", "wrong datatype", "v", false, "a.xsd")).Should().Be(SchemaRuleFamily.Attributes);
        SchemaRuleFamilies.Of(new("x", "/R/A", "wrong datatype", "v", false, "a.xsd")).Should().Be(SchemaRuleFamily.Datatype);
        SchemaRuleFamilies.Of(new("x", "/R/A", "minInclusive outside boundary", "0", false, "a.xsd")).Should().Be(SchemaRuleFamily.NumericBoundaries);
    }

    [Fact]
    public void Representative_sample_is_deterministic_stratified_and_not_the_first_cases()
    {
        var package = Package(new Dictionary<string, string> { [Skolenaervaer] = RealSchema(Skolenaervaer) });
        package.GeneratedTests.Count.Should().BeGreaterThan(SchemaTestDesign.RepresentativeSize);
        var key = SchemaTestDesign.SampleKey(package, null, SchemaTestDesign.RepresentativeSize);

        var first = SchemaTestDesign.Representative(package.GeneratedTests, SchemaTestDesign.RepresentativeSize, key);
        var second = SchemaTestDesign.Representative(package.GeneratedTests, SchemaTestDesign.RepresentativeSize, key);

        first.Select(t => t.Id).Should().Equal(second.Select(t => t.Id), "same schema, profile, size and generator → same sample");
        first.Should().HaveCount(SchemaTestDesign.RepresentativeSize).And.OnlyHaveUniqueItems();
        first.Select(SchemaRuleFamilies.Of).Distinct().Should().BeEquivalentTo(package.GeneratedTests.Select(SchemaRuleFamilies.Of).Distinct(), "every rule family is represented");
        first.Should().Contain(t => t.ExpectedValid).And.Contain(t => !t.ExpectedValid);
        first.Select(t => t.Id).Should().NotEqual(package.GeneratedTests.Take(SchemaTestDesign.RepresentativeSize).Select(t => t.Id), "never list order");
        var largest = package.GeneratedTests.GroupBy(SchemaRuleFamilies.Of).MaxBy(g => g.Count())!;
        first.Count(t => SchemaRuleFamilies.Of(t) == largest.Key).Should().BeLessThan(largest.Count(), "one category cannot take the slots because it is large or first");
        SchemaTestDesign.SampleSummary(first, package.GeneratedTests.Count).Should().StartWith($"100 of {package.GeneratedTests.Count} cases selected across ");
    }

    [Fact]
    public void Run_modes_select_from_the_same_case_model()
    {
        var package = Package(Fixture);
        var key = SchemaTestDesign.SampleKey(package, null, SchemaTestDesign.RepresentativeSize);
        SchemaTestDesign.Select(package, SchemaRunMode.All, key).Should().HaveCount(package.GeneratedTests.Count);
        SchemaTestDesign.Select(package, SchemaRunMode.Families, key, [SchemaRuleFamily.Enumeration]).Should().NotBeEmpty()
            .And.OnlyContain(t => SchemaRuleFamilies.Of(t) == SchemaRuleFamily.Enumeration);
        var failedId = package.GeneratedTests[3].Id;
        SchemaTestDesign.Select(package, SchemaRunMode.Failed, key, previous: [new(failedId, "/x", "r", true, "Invalid", "Fail", "", "")])
            .Select(t => t.Id).Should().Equal(failedId);
    }

    [Fact]
    public void Coverage_keeps_discovered_generated_executed_and_confirmed_separate()
    {
        var package = Package(Fixture);
        var before = SchemaTestDesign.Coverage(package, []);
        before.Sum(c => c.Generated).Should().Be(package.GeneratedTests.Count);
        before.Should().OnlyContain(c => c.Executed == 0 && c.Passed == 0, "generated-only is never executed coverage");
        before.Single(c => c.Family == SchemaRuleFamily.Pattern).Discovered.Should().Be(2, "Municipality under both choice branches");
        before.Single(c => c.Family == SchemaRuleFamily.Attributes).Discovered.Should().BeGreaterThanOrEqualTo(1);

        var enumeration = package.GeneratedTests.Where(t => SchemaRuleFamilies.Of(t) == SchemaRuleFamily.Enumeration).ToList();
        var after = SchemaTestDesign.Coverage(package, SchemaStudioService.RunCases(package, enumeration));
        var row = after.Single(c => c.Family == SchemaRuleFamily.Enumeration);
        row.Executed.Should().Be(enumeration.Count);
        row.Passed.Should().Be(enumeration.Count);
        after.Where(c => c.Family != SchemaRuleFamily.Enumeration).Should().OnlyContain(c => c.Executed == 0);
    }

    [Fact]
    public void An_unsupported_pattern_is_detected_and_reported_as_a_generation_gap()
    {
        // A character-class subtraction the bounded generator cannot construct: detected, never silently "valid".
        var xsd = $$"""
            <xs:schema xmlns:xs="{{Ns}}" targetNamespace="urn:p" xmlns="urn:p" elementFormDefault="qualified">
              <xs:element name="R"><xs:complexType><xs:sequence><xs:element name="Code"><xs:simpleType><xs:restriction base="xs:string"><xs:pattern value="[a-z-[aeiou]]{3}"/></xs:restriction></xs:simpleType></xs:element></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = Package(new Dictionary<string, string> { ["p.xsd"] = xsd });
        var minimum = SchemaStudioService.GenerateMinimumValid(package, "p.xsd");
        if (minimum.IsSchemaValid) SchemaStudioService.Validate(package, "p.xsd", minimum.Xml).Status.Should().Be(XmlSchemaValidationStatus.Valid);
        else minimum.Reasons.Should().NotBeEmpty();

        var pattern = SchemaTestDesign.Inventory(package).Single(r => r.Family == SchemaRuleFamily.Pattern);
        if (!package.GeneratedTests.Any(t => t.Rule == "pattern match"))
        {
            pattern.Covered.Should().BeFalse();
            pattern.NotCoveredReason.Should().Contain("regex generation unsupported");
            SchemaTestDesign.Coverage(package, []).Single(c => c.Family == SchemaRuleFamily.Pattern).Unsupported.Should().Be(1);
        }
    }

    // ── Result semantics ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, "Valid", "Pass", 0, SchemaResultClass.Pass)]
    [InlineData(false, "Invalid", "Pass", 1, SchemaResultClass.Pass)]
    [InlineData(false, "Invalid", "Pass", 3, SchemaResultClass.PassWithAdditionalViolations)]
    [InlineData(true, "Invalid", "Fail", 1, SchemaResultClass.GeneratorDefect)]
    [InlineData(false, "Valid", "Fail", 0, SchemaResultClass.MutationDefect)]
    [InlineData(true, "Not evaluated", "Not evaluated", 0, SchemaResultClass.NotEvaluated)]
    public void Results_are_classified_with_the_schema_as_oracle(bool expectedValid, string actual, string outcome, int issues, SchemaResultClass expected)
    {
        var result = new SchemaTestResult("t", "/R", "rule", expectedValid, actual, outcome, "", "", IssueCount: issues);
        SchemaTestDesign.Classify(result).Should().Be(expected);
        SchemaTestDesign.Actual(result).Should().Be(actual switch { "Valid" => "Accepted", "Invalid" => "Rejected", _ => "Not evaluated" });
    }

    [Fact]
    public void Local_validation_never_claims_runtime_or_compatibility()
    {
        var package = Package(Fixture);
        var result = SchemaStudioService.GenerateMinimumValid(package, "main.xsd");
        var text = string.Join(" ", result.Heading, result.XmlLabel, result.Provenance.Validation);
        text.Should().NotContainAny("runtime verified", "Runtime verified", "compatible", "deployed", "delivery");
        SchemaTestDesign.SchemaOnlyNotice.Should().Be("Schema validity does not prove business validity or runtime acceptance.");
    }

    // ── Save / Register / Export ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_does_not_register_and_registration_never_approves_or_accepts_invalid_positive_xml()
    {
        var package = Package(Fixture);
        var minimum = SchemaStudioService.GenerateMinimumValid(package, "main.xsd");
        var store = new SchemaGeneratedXmlStore();
        var facts = new SchemaXmlRegistrationFacts(SchemaXmlRegistrationFacts.Valid, SchemaXmlRegistrationFacts.Valid, null, "Minimum schema-valid example", null,
            minimum.Provenance.ProfileId, "built-in", SchemaTestDesign.GeneratorVersion, "Report");
        var saved = store.Save(package, "main.xsd", "Minimum valid", null, minimum.Xml, "import:a", generation: facts);
        saved.TestData.Should().BeNull("saving to the workspace never registers Test Data");
        saved.Generation.Should().Be(facts);

        var registered = store.Register(saved.Id, "import:a", "report-minimum", SchemaXmlTestDataRegistration.Positive, facts: facts with { EnvironmentId = "env-qa" });
        registered.TestData!.Approved.Should().BeFalse("Schema Studio never approves");
        registered.TestData.Facts!.ExpectedResult.Should().Be("Valid");
        registered.TestData.Facts.EnvironmentId.Should().Be("env-qa");

        var broken = store.Save(package, "main.xsd", "User XML", null, "<Report xmlns=\"urn:main\"/>", "import:a");
        var invalidFacts = new SchemaXmlRegistrationFacts(SchemaXmlRegistrationFacts.Valid, SchemaXmlRegistrationFacts.Invalid);
        FluentActions.Invoking(() => store.Register(broken.Id, "import:a", "broken", SchemaXmlTestDataRegistration.Positive, facts: invalidFacts))
            .Should().Throw<InvalidDataException>().WithMessage("*Only XML that passed local validation*");
        FluentActions.Invoking(() => store.Register(broken.Id, "import:a", "broken", SchemaXmlTestDataRegistration.Positive))
            .Should().Throw<InvalidDataException>("without a validation result nothing becomes a positive fixture");
        var negative = store.Register(broken.Id, "import:a", "missing-header", SchemaXmlTestDataRegistration.NegativeTest,
            facts: invalidFacts with { ExpectedResult = SchemaXmlRegistrationFacts.Invalid, Mutation = "missing required element", TargetRule = "Required / optional: missing required element", Path = "/Report/Header" });
        negative.TestData!.Facts!.Mutation.Should().Be("missing required element");
        negative.TestData.Approved.Should().BeFalse();
        FluentActions.Invoking(() => store.Register(saved.Id, "import:a", "not-negative", SchemaXmlTestDataRegistration.NegativeTest, facts: facts))
            .Should().Throw<InvalidDataException>().WithMessage("*cannot be a negative fixture*");

        var restored = new SchemaGeneratedXmlStore();
        restored.RestoreJson(store.SnapshotJson("import:a"));
        restored.Documents.Single(d => d.Id == saved.Id).TestData!.Facts!.ProfileVersion.Should().Be("built-in");
    }

    [Theory]
    [InlineData("The 'Kode' element is invalid - The value 'ABC' is invalid according to its datatype.", "The 'Kode' element is invalid - The value is invalid according to its datatype.")]
    [InlineData("The Value   'x y' failed.", "The value failed.")]
    [InlineData("Identifier 12345678901 and code 12345 remain safe.", "Identifier [number] and code 12345 remain safe.")]
    [InlineData("invalue 'kept' because not a word boundary", "invalue 'kept' because not a word boundary")]
    public void Validator_messages_are_redacted_deterministically_without_timed_regexes(string message, string expected) =>
        XmlSchemaPackageAnalyzer.SafeIssue(message).Should().Be(expected);

    [Fact]
    public void Synthetic_identifiers_only()
    {
        var package = Package(new Dictionary<string, string> { [Skolenaervaer] = RealSchema(Skolenaervaer) });
        var xml = SchemaStudioService.GenerateMinimumValid(package, Skolenaervaer).Xml;
        foreach (System.Text.RegularExpressions.Match digits in System.Text.RegularExpressions.Regex.Matches(xml, @"(?<![\d.-])\d{9,12}(?![\d.-])"))
            digits.Value.Should().MatchRegex("^[01]0*$", "identifier-shaped values are synthetic zeros, never real-looking");
    }
}
