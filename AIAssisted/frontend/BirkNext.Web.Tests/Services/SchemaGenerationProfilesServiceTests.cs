using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

public sealed class SchemaGenerationProfilesServiceTests
{
    private const string Xsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:profiles" xmlns="urn:profiles" elementFormDefault="qualified">
          <xs:element name="Record"><xs:complexType><xs:sequence>
            <xs:element name="Code"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="A"/><xs:enumeration value="B"/></xs:restriction></xs:simpleType></xs:element>
            <xs:element name="Pattern"><xs:simpleType><xs:restriction base="xs:string"><xs:pattern value="[A-Z]{2}"/><xs:minLength value="2"/><xs:maxLength value="4"/></xs:restriction></xs:simpleType></xs:element>
            <xs:element name="Count"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="2"/><xs:maxInclusive value="9"/></xs:restriction></xs:simpleType></xs:element>
            <xs:element name="When" type="xs:date"/>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    private static SchemaPackage Package() => new SchemaStudioService().Import(new Dictionary<string, string> { ["profiles.xsd"] = Xsd }, "profiles.xsd");

    [Fact]
    public void ProvidesFiveStableSchemaBoundBuiltInProfiles()
    {
        var first = SchemaGenerationProfilesService.BuiltIns("abc");
        var reopened = SchemaGenerationProfilesService.BuiltIns("abc");
        Assert.Equal(5, first.Count);
        Assert.Equal(first.Select(p => p.Id), reopened.Select(p => p.Id));
        Assert.NotEqual(first[0].Id, SchemaGenerationProfilesService.BuiltIns("different")[0].Id);
        Assert.All(first, p => Assert.True(p.IsBuiltIn));
    }

    [Fact]
    public void SupportsCreateEditCloneDeleteAndProtectsBuiltIns()
    {
        var service = new SchemaGenerationProfilesService();
        var profile = service.Create("Profile A", Package().Fingerprint, "import:one", SchemaGenerationProfileKind.GenericValid, 12);
        var edited = service.Edit(profile.Id, "Profile B", SchemaGenerationProfileKind.Boundary, 14,
            [new("/Record/Count", SchemaGenerationValueMode.Boundary, Minimum: 2, Maximum: 9)]);
        Assert.Equal("Profile B", edited.Name);
        Assert.Single(edited.Rules);
        var clone = service.Clone(edited.Id, "Clone");
        Assert.NotEqual(edited.Id, clone.Id);
        Assert.Equal(edited.Rules, clone.Rules);
        Assert.True(service.Delete(clone.Id));
        Assert.False(service.Delete(SchemaGenerationProfilesService.BuiltIns(Package().Fingerprint)[0].Id));
        Assert.Throws<InvalidOperationException>(() => service.Edit(SchemaGenerationProfilesService.BuiltIns(Package().Fingerprint)[0].Id, "x", SchemaGenerationProfileKind.Minimal, 1, []));
    }

    [Fact]
    public void GenerationIsReproducibleAndConformsToTheSelectedSchema()
    {
        var service = new SchemaGenerationProfilesService();
        var package = Package();
        var profile = service.Create("Stable", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 9876);
        var first = service.GenerateValues(package, profile);
        var second = service.GenerateValues(package, profile);
        Assert.Equal(first, second);
        var xml = SchemaStudioService.GenerateProfileXml(package, "Record", first);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "profiles.xsd", xml));
        Assert.Contains("<When>20", xml);
        Assert.DoesNotContain("123456789", xml);
    }

    [Fact]
    public void MinimalProfileOmitsOptionalBranchesUnlessAProfileRuleRequestsThem()
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:optional" xmlns="urn:optional" elementFormDefault="qualified">
              <xs:element name="Root"><xs:complexType><xs:sequence>
                <xs:element name="Required" type="xs:string"/>
                <xs:element name="OptionalGroup" minOccurs="0"><xs:complexType><xs:sequence><xs:element name="Value" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["optional.xsd"] = schema }, "optional.xsd");
        var profiles = new SchemaGenerationProfilesService();
        var minimal = profiles.Create("Minimal", package.Fingerprint, null, SchemaGenerationProfileKind.Minimal, 19);

        var withoutOptional = SchemaStudioService.GenerateProfileXml(package, "Root", profiles.GenerateValues(package, minimal), minimal);
        Assert.DoesNotContain("OptionalGroup", withoutOptional);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "optional.xsd", withoutOptional));

        var configured = profiles.Edit(minimal.Id, minimal.Name, minimal.Kind, minimal.Seed,
            [new("/Root/OptionalGroup/Value", SchemaGenerationValueMode.Synthetic)]);
        var values = profiles.GenerateValues(package, configured);
        var withOptional = SchemaStudioService.GenerateProfileXml(package, "Root", values, configured);
        Assert.Contains("OptionalGroup", withOptional);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "optional.xsd", withOptional));
    }

    [Fact]
    public void ProfileRuleSelectsTheConfiguredBranchOfAnXsdChoice()
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:choice" xmlns="urn:choice" elementFormDefault="qualified">
              <xs:element name="Root"><xs:complexType><xs:choice><xs:element name="First" type="xs:string"/><xs:element name="Second" type="xs:string"/></xs:choice></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["choice.xsd"] = schema }, "choice.xsd");
        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("Choose second", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 7);
        profile = profiles.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed,
            [new("/Root/Second", SchemaGenerationValueMode.Synthetic)]);

        var xml = SchemaStudioService.GenerateProfileXml(package, "Root", profiles.GenerateValues(package, profile), profile);

        Assert.Contains("<Second", xml);
        Assert.DoesNotContain("<First", xml);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "choice.xsd", xml));
    }

    [Fact]
    public void ProfileRulesGenerateDeterministicDateRangesAndBoundaries()
    {
        var service = new SchemaGenerationProfilesService();
        var package = Package();
        var profile = service.Create("Constrained", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 9);
        service.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed,
        [
            new("/Record/Count", SchemaGenerationValueMode.Boundary, Minimum: 2, Maximum: 9),
            new("/Record/When", SchemaGenerationValueMode.Synthetic, DateFrom: new DateOnly(2024, 1, 1), DateTo: new DateOnly(2024, 1, 1))
        ]);
        var values = service.GenerateValues(package, service.Profiles.Single());
        Assert.Contains(values["/Record/Count"], new[] { "2", "9" });
        Assert.Equal("2024-01-01", values["/Record/When"]);
    }

    [Fact]
    public void ProfileOccurrenceRulesGenerateTheRequestedBoundedRepeatCount()
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:repeat" xmlns="urn:repeat" elementFormDefault="qualified">
              <xs:element name="Root"><xs:complexType><xs:sequence>
                <xs:element name="Items"><xs:complexType><xs:sequence>
                  <xs:element name="Item" type="xs:string" minOccurs="1" maxOccurs="5"/>
                </xs:sequence></xs:complexType></xs:element>
                <xs:element name="Optional" type="xs:string" minOccurs="0" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["repeat.xsd"] = schema }, "repeat.xsd");
        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("Repeat three", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 27);
        profile = profiles.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed,
        [
            new("/Root/Items/Item", SchemaGenerationValueMode.Synthetic, OccurrenceCount: 3),
            new("/Root/Optional", SchemaGenerationValueMode.Synthetic, OccurrenceCount: 0)
        ]);

        var values = profiles.GenerateValues(package, profile);
        var xml = SchemaStudioService.GenerateProfileXml(package, "Root", values, profile);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "urn:repeat";
        Assert.Equal(3, document.Root!.Element(ns + "Items")!.Elements(ns + "Item").Count());
        Assert.Empty(document.Root.Elements(ns + "Optional"));
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "repeat.xsd", xml));

        var reopenedCatalog = new SchemaGenerationProfilesService();
        reopenedCatalog.RestoreJson(profiles.SnapshotJson());
        var reopenedProfile = reopenedCatalog.Profiles.Single(p => p.Id == profile.Id);
        var reopenedValues = reopenedCatalog.GenerateValues(package, reopenedProfile);
        var reopenedXml = SchemaStudioService.GenerateProfileXml(package, "Root", reopenedValues, reopenedProfile);
        Assert.Equal(xml, reopenedXml);

        var invalid = profiles.Create("Too many", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 28);
        profiles.Edit(invalid.Id, invalid.Name, invalid.Kind, invalid.Seed,
            [new("/Root/Items/Item", SchemaGenerationValueMode.Synthetic, OccurrenceCount: 6)]);
        Assert.Throws<InvalidOperationException>(() => profiles.GenerateValues(package,
            profiles.Profiles.Single(p => p.Id == invalid.Id)));
    }

    [Theory]
    [InlineData("[A-Z]{5}[0-9]{3}", 8)]
    [InlineData("(AB|CD)-\\d{2}", 5)]
    [InlineData("^[A-Z]{2}$", 2)]
    public void SynthesizesDeterministicValuesForSupportedXsdPatternSyntax(string pattern, int expectedLength)
    {
        var schema = $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:pattern" xmlns="urn:pattern" elementFormDefault="qualified">
              <xs:element name="Root"><xs:complexType><xs:sequence><xs:element name="Value"><xs:simpleType><xs:restriction base="xs:string"><xs:pattern value="{pattern}"/><xs:length value="{expectedLength}"/></xs:restriction></xs:simpleType></xs:element></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["pattern.xsd"] = schema }, "pattern.xsd");
        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("Pattern", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 2741);

        var first = profiles.GenerateValues(package, profile)["/Root/Value"];
        var second = profiles.GenerateValues(package, profile)["/Root/Value"];
        var xml = SchemaStudioService.GenerateProfileXml(package, "Root", new Dictionary<string, string> { ["/Root/Value"] = first }, profile);

        Assert.Equal(expectedLength, first.Length);
        Assert.Equal(first, second);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "pattern.xsd", xml));
    }

    [Fact]
    public void UnsupportedXsdPatternSyntaxFailsExplicitlyInsteadOfReturningAFalseValidValue()
    {
        const string schema = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Value\"><xs:simpleType><xs:restriction base=\"xs:string\"><xs:pattern value=\"\\i[a-z]*\"/></xs:restriction></xs:simpleType></xs:element></xs:schema>";
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["unsupported.xsd"] = schema }, "unsupported.xsd");
        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("Unsupported pattern", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 1);

        var error = Assert.Throws<InvalidOperationException>(() => profiles.GenerateValues(package, profile));

        Assert.Contains("unsupported XML Schema regex syntax", error.Message);
    }

    [Fact]
    public void ProfilesRespectFixedAndDefaultValuesAndCanNilOnlyNillableElements()
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:nullable" xmlns="urn:nullable" xmlns:t="urn:nullable" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence>
                <xs:element name="Defaulted" type="xs:string" default="from-schema"/>
                <xs:element name="Fixed" type="xs:string" fixed="locked"/>
                <xs:element name="Optional" type="xs:string" minOccurs="0" nillable="true"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["nullable.xsd"] = schema }, "nullable");
        var members = package.EffectiveSchema.Single().Children.ToDictionary(m => m.Name);
        Assert.Equal("from-schema", members["Defaulted"].DefaultValue);
        Assert.Equal("locked", members["Fixed"].FixedValue);
        Assert.True(members["Optional"].Nillable);

        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("Nullable", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 41);
        profile = profiles.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed,
        [
            new("/Record/Defaulted", SchemaGenerationValueMode.SchemaDefault),
            new("/Record/Optional", SchemaGenerationValueMode.Nil)
        ]);
        var values = profiles.GenerateValues(package, profile);
        Assert.Equal("from-schema", values["/Record/Defaulted"]);
        Assert.Equal("locked", values["/Record/Fixed"]);
        var xml = SchemaStudioService.GenerateProfileXml(package, "Record", values, profile);
        var generated = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("true", generated.Root!.Element(System.Xml.Linq.XName.Get("Optional", "urn:nullable"))!
            .Attribute(System.Xml.Linq.XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"))!.Value);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "nullable.xsd", xml));

        var notNillable = profiles.Create("Invalid nil", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 42);
        profiles.Edit(notNillable.Id, notNillable.Name, notNillable.Kind, notNillable.Seed,
            [new("/Record/Fixed", SchemaGenerationValueMode.Nil)]);
        Assert.Throws<InvalidOperationException>(() => profiles.GenerateValues(package, profiles.Profiles.Single(p => p.Id == notNillable.Id)));
    }

    [Fact]
    public void BoundaryGenerationRespectsExclusiveAndIntegerNumericLimits()
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:numbers" xmlns="urn:numbers" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence>
                <xs:element name="Above"><xs:simpleType><xs:restriction base="xs:integer"><xs:minExclusive value="2"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Below"><xs:simpleType><xs:restriction base="xs:decimal"><xs:maxExclusive value="5"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Narrow"><xs:simpleType><xs:restriction base="xs:decimal"><xs:minInclusive value="0.2"/><xs:maxExclusive value="0.3"/><xs:fractionDigits value="2"/></xs:restriction></xs:simpleType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["numbers.xsd"] = schema }, "numbers.xsd");
        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("Numeric boundaries", package.Fingerprint, null, SchemaGenerationProfileKind.Boundary, 123);
        var values = profiles.GenerateValues(package, profile);
        Assert.True(decimal.Parse(values["/Record/Above"], System.Globalization.CultureInfo.InvariantCulture) > 2m);
        Assert.True(decimal.Parse(values["/Record/Below"], System.Globalization.CultureInfo.InvariantCulture) < 5m);
        var narrow = decimal.Parse(values["/Record/Narrow"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(narrow, 0.2m, 0.29m);
        var xml = SchemaStudioService.GenerateProfileXml(package, "Record", values, profile);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "numbers.xsd", xml));
    }

    [Fact]
    public void GenerationHonorsIntrinsicNumericTypesAndRejectsIncompatibleProfileRules()
    {
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:intrinsic" xmlns="urn:intrinsic" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence>
                <xs:element name="Amount" type="xs:integer"/>
                <xs:element name="Positive" type="xs:positiveInteger"/>
                <xs:element name="Label"><xs:simpleType><xs:restriction base="xs:string"><xs:minLength value="3"/></xs:restriction></xs:simpleType></xs:element>
                <xs:element name="Moment"><xs:simpleType><xs:restriction base="xs:dateTime"><xs:minInclusive value="2026-01-01T18:00:00Z"/><xs:maxInclusive value="2026-01-03T11:00:00Z"/></xs:restriction></xs:simpleType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["intrinsic.xsd"] = schema }, "intrinsic.xsd");
        var moment = package.EffectiveSchema.Single().Children.Single(member => member.Name == "Moment");
        Assert.Equal("xs:dateTime", moment.Type);
        Assert.Contains(moment.Facets, facet => facet.Kind == "maxInclusive" && facet.Value == "2026-01-03T11:00:00Z");
        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("Intrinsic types", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 88);
        var values = profiles.GenerateValues(package, profile);
        Assert.True(int.TryParse(values["/Record/Amount"], out _));
        Assert.True(int.Parse(values["/Record/Positive"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.True(values["/Record/Label"].Length >= 3);
        Assert.StartsWith("2026-01-", values["/Record/Moment"]);
        var generatedXml = SchemaStudioService.GenerateProfileXml(package, "Record", values, profile);
        var generatedValidation = SchemaStudioService.ValidateXml(package, "intrinsic.xsd", generatedXml);
        Assert.True(generatedValidation.StartsWith("Valid against", StringComparison.Ordinal), generatedValidation + "\n" + generatedXml);

        profiles.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed,
            [new("/Record/Label", SchemaGenerationValueMode.Synthetic, MinimumLength: 1, MaximumLength: 2)]);
        Assert.Throws<InvalidOperationException>(() => profiles.GenerateValues(package, profiles.Profiles.Single()));

        profiles.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed,
            [new("/Record/Label", SchemaGenerationValueMode.Synthetic, Minimum: 1)]);
        Assert.Throws<InvalidOperationException>(() => profiles.GenerateValues(package, profiles.Profiles.Single()));
    }

    [Theory]
    [InlineData("positiveInteger")]
    [InlineData("nonNegativeInteger")]
    [InlineData("negativeInteger")]
    [InlineData("nonPositiveInteger")]
    [InlineData("unsignedByte")]
    [InlineData("unsignedShort")]
    [InlineData("unsignedInt")]
    [InlineData("unsignedLong")]
    [InlineData("byte")]
    [InlineData("short")]
    [InlineData("int")]
    [InlineData("long")]
    public void BuiltInNumericTypesGenerateValidValuesForEveryProfile(string type)
    {
        var schema = $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:numeric-{type}" xmlns="urn:numeric-{type}" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence>
                <xs:element name="Value" type="xs:{type}"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["numeric.xsd"] = schema }, "numeric.xsd");
        var service = new SchemaGenerationProfilesService();

        foreach (var profile in SchemaGenerationProfilesService.BuiltIns(package.Fingerprint))
        {
            var values = service.GenerateValues(package, profile);
            var xml = SchemaStudioService.GenerateProfileXml(package, "Record", values, profile);
            var result = SchemaStudioService.ValidateXml(package, "numeric.xsd", xml);
            var expectedInvalid = profile.Kind == SchemaGenerationProfileKind.ControlledInvalid;
            Assert.Equal(expectedInvalid, result.StartsWith("Invalid against", StringComparison.Ordinal));
            if (!expectedInvalid)
                Assert.StartsWith("Valid against", result);
        }
    }

    [Fact]
    public void RejectsProfilesForDifferentSchemaOrProjectScope()
    {
        var service = new SchemaGenerationProfilesService();
        var package = Package();
        var profile = service.Create("Scoped", package.Fingerprint, "import:one", SchemaGenerationProfileKind.GenericValid, 1);
        Assert.False(service.IsCompatible(profile, package, "import:two"));
        Assert.Throws<InvalidOperationException>(() => service.GenerateValues(package, profile, "import:two"));
        var otherSchema = new SchemaStudioService().Import(new Dictionary<string, string> { ["other.xsd"] = Xsd.Replace("profiles", "another", StringComparison.Ordinal) }, "other");
        Assert.False(service.IsCompatible(profile with { ProjectScope = null }, otherSchema));
    }

    [Fact]
    public void ProfileExportImportAndWorkspaceSnapshotRestorePreserveOnlyRulesAndSettings()
    {
        var source = new SchemaGenerationProfilesService();
        var package = Package();
        var profile = source.Create("Safe profile", package.Fingerprint, null, SchemaGenerationProfileKind.Boundary, 42);
        source.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed, [new("/Record/Pattern", SchemaGenerationValueMode.Boundary, MinimumLength: 2, MaximumLength: 4)]);
        var exported = source.ExportJson(source.Profiles.Single());
        Assert.DoesNotContain("real identifier", exported, StringComparison.OrdinalIgnoreCase);
        var imported = new SchemaGenerationProfilesService();
        var newProfile = imported.ImportJson(exported, "import:target");
        Assert.NotEqual(profile.Id, newProfile.Id);
        Assert.Equal(profile.SchemaFingerprint, newProfile.SchemaFingerprint);
        Assert.Equal("import:target", newProfile.ProjectScope);
        var reopened = new SchemaGenerationProfilesService();
        reopened.RestoreJson(imported.SnapshotJson());
        Assert.Equal(newProfile.Rules, Assert.Single(reopened.Profiles).Rules);
    }

    [Fact]
    public void ImportingPortableProfileBindsItToSelectedSchemaAndRejectsUnboundRestore()
    {
        var package = Package();
        var portable = SchemaGenerationProfilesService.BuiltIns("*").Single(profile => profile.Kind == SchemaGenerationProfileKind.GenericValid);
        var imported = new SchemaGenerationProfilesService().ImportJson(
            new SchemaGenerationProfilesService().ExportJson(portable), "import:current", package.Fingerprint);

        Assert.Equal(package.Fingerprint, imported.SchemaFingerprint);
        Assert.False(imported.IsBuiltIn);
        Assert.True(new SchemaGenerationProfilesService().IsCompatible(imported, package, "import:current"));
        var otherSchema = new SchemaStudioService().Import(new Dictionary<string, string>
            { ["other.xsd"] = Xsd.Replace("profiles", "other-profiles", StringComparison.Ordinal) }, "other");
        Assert.False(new SchemaGenerationProfilesService().IsCompatible(imported, otherSchema, "import:current"));

        var unbound = imported with { SchemaFingerprint = "*" };
        var json = System.Text.Json.JsonSerializer.Serialize(unbound, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var legacyRestore = new SchemaGenerationProfilesService();
        legacyRestore.RestoreJson("[" + json + "]");
        Assert.Equal(1, legacyRestore.RejectedUnboundLegacyProfiles);
        Assert.Empty(legacyRestore.Profiles);
        Assert.Throws<InvalidDataException>(() => new SchemaGenerationProfilesService().ImportJson(json, "import:current"));

        var valid = new SchemaGenerationProfilesService().Create("Still usable", package.Fingerprint, "import:current", SchemaGenerationProfileKind.GenericValid, 7);
        var mixed = "[" + json + "," + new SchemaGenerationProfilesService().ExportJson(valid) + "]";
        var restored = new SchemaGenerationProfilesService();
        restored.RestoreJson(mixed);
        Assert.Equal(1, restored.RejectedUnboundLegacyProfiles);
        Assert.Equal("Still usable", Assert.Single(restored.Profiles).Name);
    }

    [Fact]
    public void WorkspaceProfileSnapshotExcludesProfilesFromOtherProjects()
    {
        var service = new SchemaGenerationProfilesService();
        var fingerprint = Package().Fingerprint;
        service.Create("Project A", fingerprint, "import:a", SchemaGenerationProfileKind.GenericValid, 1);
        service.Create("Project B", fingerprint, "import:b", SchemaGenerationProfileKind.GenericValid, 2);

        var reopened = new SchemaGenerationProfilesService();
        reopened.RestoreJson(service.SnapshotJson("import:a"));

        Assert.Equal("Project A", Assert.Single(reopened.Profiles).Name);
    }

    [Fact]
    public void ProfileCompatibilityRequiresExplicitWorkspaceAssignmentExceptForBuiltIns()
    {
        var service = new SchemaGenerationProfilesService();
        var package = Package();
        var unassigned = service.Create("Unassigned", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 1);

        Assert.False(service.IsCompatible(unassigned, package, "import:project"));
        Assert.True(service.IsCompatible(SchemaGenerationProfilesService.BuiltIns(package.Fingerprint)[0], package, "import:project"));
    }

    [Fact]
    public void WorkspaceSnapshotEnforcesTheProfileDocumentSizeLimit()
    {
        var service = new SchemaGenerationProfilesService();
        var fingerprint = Package().Fingerprint;
        var rules = Enumerable.Range(0, SchemaGenerationProfilesService.MaxRulesPerProfile)
            .Select(i => new SchemaGenerationRule("/" + i.ToString("D3") + new string('x', 1000), SchemaGenerationValueMode.Synthetic))
            .ToArray();
        for (var i = 0; i < 11; i++)
        {
            var profile = service.Create("Large profile " + i, fingerprint, null, SchemaGenerationProfileKind.GenericValid, i);
            service.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed, rules);
        }

        Assert.Throws<InvalidDataException>(() => service.SnapshotJson());
        Assert.Throws<InvalidDataException>(() => service.SnapshotJson(null));
    }

    [Theory]
    [InlineData("../Record/Secret")]
    [InlineData("/" + "x" + "/" + ".." + "/x")]
    public void RejectsUnsafeRulePaths(string path)
    {
        var service = new SchemaGenerationProfilesService();
        var profile = service.Create("Profile", Package().Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 1);
        Assert.Throws<InvalidDataException>(() => service.Edit(profile.Id, "Profile", profile.Kind, profile.Seed,
            [new(path, SchemaGenerationValueMode.Synthetic)]));
    }

    [Fact]
    public void RejectsMalformedSavedProfilesAndUnsupportedKinds()
    {
        var service = new SchemaGenerationProfilesService();
        Assert.Throws<InvalidDataException>(() => service.RestoreJson("[null]"));

        var valid = service.Create("Valid", Package().Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 1);
        var invalidKind = service.ExportJson(valid).Replace("\"kind\":0", "\"kind\":99", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => new SchemaGenerationProfilesService().ImportJson(invalidKind));
    }

    [Fact]
    public void ResolvesImportedSimpleTypeBaseForEffectiveSchemaProfilesAndWrongDatatypeCases()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string>
        {
            ["main.xsd"] = """
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:common" targetNamespace="urn:main" xmlns="urn:main" elementFormDefault="qualified">
                  <xs:import namespace="urn:common" schemaLocation="common.xsd"/>
                  <xs:element name="Record"><xs:complexType><xs:sequence><xs:element name="Amount" type="c:AmountValue"/></xs:sequence></xs:complexType></xs:element>
                </xs:schema>
                """,
            ["common.xsd"] = """
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:common" xmlns="urn:common">
                  <xs:simpleType name="AmountValue"><xs:restriction base="xs:integer"><xs:minInclusive value="3"/></xs:restriction></xs:simpleType>
                </xs:schema>
                """
        }, "imported simple type");
        var amount = package.EffectiveSchema.Single().Children.Single();
        Assert.Equal("c:AmountValue", amount.Type);
        Assert.Equal("common.xsd", amount.TypeDeclaredIn);
        Assert.Equal("xs:integer", amount.BaseType);

        var profileService = new SchemaGenerationProfilesService();
        var profile = profileService.Create("Imported amount", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 17);
        var generated = profileService.GenerateValues(package, profile);
        Assert.True(int.Parse(generated["/Record/Amount"], System.Globalization.CultureInfo.InvariantCulture) >= 3);
        var xml = SchemaStudioService.GenerateProfileXml(package, "Record", generated, profile);
        Assert.StartsWith("Valid against", SchemaStudioService.ValidateXml(package, "main.xsd", xml));

        var wrongType = Assert.Single(package.GeneratedTests, test => test.Rule == "wrong datatype" && test.Path == "/Record/Amount");
        Assert.StartsWith("Invalid against", SchemaStudioService.ValidateXml(package, "main.xsd", SchemaStudioService.GenerateTestXml(package, wrongType)));
    }
}
