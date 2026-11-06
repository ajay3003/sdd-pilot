using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// NEXT58A: the required-field table tells apart a required field without a value, an optional field left out, a valid mapping, a stale mapping
/// (the asset changed after the saved fixture was generated), an incompatible asset (removed, not approved or of another type) and a mapping to an
/// element the selected XSD does not declare — and the eight Test Data readiness dimensions carry the evidence facts behind them.
/// </summary>
public sealed class XmlFieldMappingPresentationTests
{
    private const string Contract = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn";
    private const string XsdFile = "xsd/" + Contract + ".xsd";
    private static readonly Guid Snapshot = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static XmlParameter P(string path, bool required, string? type = "xs:string") =>
        new(path, path.Replace(".", " → "), "/Melding/" + path.Replace('.', '/'), type, null) { Required = required };

    private static TestDataAssetSummary A(string alias, TestDataApproval approval = TestDataApproval.Approved, TestDataValueType type = TestDataValueType.String,
        string capability = "value", int valueVersion = 0) => new()
    {
        Id = "x:" + alias, Alias = alias, Label = alias, Kind = TestDataKind.EntityReference, Capabilities = [capability], Approval = approval, ValueType = type,
        ValueVersion = valueVersion, IsDefault = capability == "child",
    };

    private static readonly TestDataFixtureBinding ChildBinding = new("Child", "child", "Klient.Fodselsnummer", "The child.");

    [Fact]
    public void Each_field_state_is_distinct_and_attention_rows_come_first()
    {
        var child = A("child-default", capability: "child");
        var parameters = new[]
        {
            P("Klient.Fodselsnummer", true), P("Melding.Dato", true, "xs:date"), P("Melding.Id", true), P("Melding.Kommentar", false),
            P("Melding.Kode", true), P("Melding.Ref", true), P("Melding.Navn", true), P("Melding.Utkast", true),
        };
        var mappings = new List<XmlValueMapping>
        {
            new("Melding.Dato", XmlValueSourceKind.TestDataAsset, "flag"),
            new("Melding.Id", XmlValueSourceKind.TestDataAsset),
            new("Melding.Ref", XmlValueSourceKind.TestDataAsset, "removed"),
            new("Melding.Navn", XmlValueSourceKind.TestDataAsset, "name"),
            new("Melding.Utkast", XmlValueSourceKind.TestDataAsset, "draft"),
            new("Old.Element", XmlValueSourceKind.Fixed, FixedValue: "x"),
        };
        var assets = new[] { child, A("flag", type: TestDataValueType.Boolean), A("name", valueVersion: 2), A("draft", TestDataApproval.Unapproved) };
        var rows = XmlFieldMappingPresentation.Rows(parameters, mappings, [ChildBinding], _ => child, assets, ["name"],
            ["The element 'Kode' has an invalid value according to its data type."]).ToDictionary(r => r.Path);

        rows["Klient.Fodselsnummer"].State.Should().Be(XmlFieldState.Valid);
        rows["Klient.Fodselsnummer"].Alias.Should().Be("child-default");
        rows["Klient.Fodselsnummer"].Status.Should().Be("Role mapped");
        rows["Melding.Dato"].State.Should().Be(XmlFieldState.IncompatibleAsset);
        rows["Melding.Dato"].Guidance.Should().Contain("Boolean").And.Contain("xs:date");
        rows["Melding.Dato"].ExpectedType.Should().Be("xs:date");
        rows["Melding.Id"].State.Should().Be(XmlFieldState.RequiredMissing);
        rows["Melding.Kommentar"].State.Should().Be(XmlFieldState.OptionalUnmapped);
        rows["Melding.Kode"].State.Should().Be(XmlFieldState.Synthetic, "a required field without a mapping gets a schema-valid placeholder");
        rows["Melding.Kode"].Issue.Should().Contain("'Kode'");
        rows["Melding.Kode"].NeedsAttention.Should().BeTrue("a validation issue on the field needs attention");
        rows["Melding.Ref"].State.Should().Be(XmlFieldState.IncompatibleAsset);
        rows["Melding.Ref"].Guidance.Should().Contain("no longer exists");
        rows["Melding.Navn"].State.Should().Be(XmlFieldState.Stale, "the asset's value changed after the saved fixture was generated");
        rows["Melding.Utkast"].State.Should().Be(XmlFieldState.IncompatibleAsset);
        rows["Melding.Utkast"].Guidance.Should().Contain("not approved");
        rows["Old.Element"].State.Should().Be(XmlFieldState.Unresolved, "the selected XSD does not declare the element");

        var ordered = XmlFieldMappingPresentation.Rows(parameters, mappings, [ChildBinding], _ => child, assets, ["name"], []);
        ordered.TakeWhile(r => r.NeedsAttention).Should().HaveCount(ordered.Count(r => r.NeedsAttention), "fields needing attention are listed first");
        ordered.Select(r => r.State).Distinct().Should().HaveCountGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void A_role_without_an_approved_identity_is_a_required_missing_mapping_never_valid()
    {
        var unapproved = A("child-b", TestDataApproval.Unapproved, capability: "child");
        var missing = XmlFieldMappingPresentation.Rows([P("Klient.Fodselsnummer", true)], [], [ChildBinding], _ => null, [], [], []).Single();
        missing.State.Should().Be(XmlFieldState.RequiredMissing);
        missing.Guidance.Should().Contain("approved child");
        var notApproved = XmlFieldMappingPresentation.Rows([P("Klient.Fodselsnummer", true)], [], [ChildBinding], _ => unapproved, [unapproved], [], []).Single();
        notApproved.State.Should().Be(XmlFieldState.IncompatibleAsset);
    }

    [Theory]
    [InlineData("xs:date", TestDataValueType.Boolean, false)]
    [InlineData("xs:date", TestDataValueType.Date, true)]
    [InlineData("xs:string", TestDataValueType.Integer, true)]
    [InlineData("xs:int", TestDataValueType.String, true)]
    [InlineData("xs:int", TestDataValueType.Integer, true)]
    [InlineData("xs:int", TestDataValueType.Date, false)]
    [InlineData("xs:boolean", TestDataValueType.Integer, false)]
    [InlineData("xs:decimal", TestDataValueType.Integer, true)]
    [InlineData(null, TestDataValueType.Boolean, true)]
    [InlineData("tns:FodselsnummerType", TestDataValueType.Integer, true)]
    public void Only_a_specific_type_contradicting_a_specific_xsd_type_is_incompatible(string? xsd, TestDataValueType type, bool compatible) =>
        XmlFieldMappingPresentation.TypeCompatible(xsd, type).Should().Be(compatible);

    // ── readiness: evidence mapped onto the existing eight dimensions ──────────────────────────────────────────────────────────────

    private static IntegrationTemplateView Context() =>
        new("skolenaervaer-report-intake", "Report intake", "", IntegrationKind.HttpApi, "skolenaervaer", "Skolenærvær Testing")
        {
            SchemaName = Contract, SchemaCandidates = [new(Snapshot, "skolenaervaer.zip", Contract, XsdFile) { MatchesContract = true }],
            FixtureBindings = [ChildBinding, new("Reporter", "reporter", null, "Submits.")],
        };

    private static TestDataAssetSummary Fixture(TestDataApproval approval, string? valueVersions = null, int? draft = null) => new()
    {
        Id = "schema-studio:report", Alias = "report", Kind = TestDataKind.Structured, MediaType = "application/xml", Approval = approval, Version = 1, DraftVersion = draft,
        Schema = new TestDataSchemaReference(XsdFile, Snapshot), SchemaCheck = new TestDataSchemaCheck(TestDataSchemaStatus.Validated, XsdFile, "", []),
        Provenance = valueVersions is null ? [] : [new TestDataFact(TestDataValueVersions.Label, valueVersions)],
    };

    private static IReadOnlyList<TestDataReadinessDimension> Dimensions(params TestDataAssetSummary[] assets) =>
        TestDataReadinessModel.Dimensions(XmlFixturePresentation.Tester(assets, Context()), Context(), assets, "Dev", "Development");

    private static readonly TestDataAssetSummary[] Identities = [A("child-default", capability: "child"), A("reporter-default", capability: "reporter") with { IsDefault = true }];

    [Fact]
    public void The_eight_evidence_facts_map_onto_the_existing_eight_dimensions_without_adding_any()
    {
        var dims = Dimensions([.. Identities, Fixture(TestDataApproval.Approved)]);
        dims.Select(d => d.Key).Should().Equal(["identities", "contract", "mapping", "fixture", "xsd", "consumers", "environment", "runtime"]);
        var evidence = dims.SelectMany(d => d.Evidence.Select(e => (Dimension: d.Key, e.Key))).ToList();
        evidence.Should().BeEquivalentTo(new[]
        {
            ("identities", "approved-test-data"), ("contract", "source"), ("contract", "xsd-resolved"), ("mapping", "required-mappings"),
            ("fixture", "xml-generated"), ("fixture", "fixture-saved"), ("fixture", "fixture-approved"), ("xsd", "schema-valid"),
        });
        dims.SelectMany(d => d.Evidence).Should().OnlyContain(e => e.State == TestDataDimensionState.Ready);
        dims.Single(d => d.Key == "runtime").State.Should().Be(TestDataDimensionState.NotAssessed, "Test Data never assesses runtime");
    }

    [Fact]
    public void No_fixture_is_not_assessed_or_missing_never_ready_and_a_draft_is_never_approved()
    {
        var none = Dimensions(Identities).SelectMany(d => d.Evidence).ToDictionary(e => e.Key);
        none["xml-generated"].State.Should().Be(TestDataDimensionState.NotAssessed, "an editor preview is not evidence");
        none["fixture-saved"].State.Should().Be(TestDataDimensionState.Blocked);
        none["fixture-approved"].State.Should().Be(TestDataDimensionState.NotAssessed);
        none["schema-valid"].State.Should().Be(TestDataDimensionState.NotAssessed);

        var draft = Dimensions([.. Identities, Fixture(TestDataApproval.Unapproved)]);
        draft.Single(d => d.Key == "fixture").State.Should().Be(TestDataDimensionState.NeedsAttention);
        draft.SelectMany(d => d.Evidence).Single(e => e.Key == "fixture-approved").Should().Match<TestDataReadinessEvidence>(e =>
            e.State == TestDataDimensionState.NeedsAttention && e.Detail.Contains("never treated as approved"));

        var pending = Dimensions([.. Identities, Fixture(TestDataApproval.Approved, draft: 2)]).SelectMany(d => d.Evidence).Single(e => e.Key == "fixture-approved");
        pending.Detail.Should().Contain("draft v2 is not approved");
    }

    [Fact]
    public void A_mapped_asset_changed_or_removed_after_generation_makes_the_mapping_stale_or_blocked_not_ready()
    {
        var changed = Dimensions([A("child-default", capability: "child", valueVersion: 3), Identities[1], Fixture(TestDataApproval.Approved, "child-default@v1")]);
        changed.Single(d => d.Key == "mapping").Should().Match<TestDataReadinessDimension>(d => d.State == TestDataDimensionState.NeedsAttention && d.Detail.Contains("child-default"));
        var removed = Dimensions([.. Identities, Fixture(TestDataApproval.Approved, "child-default@v0; old-child@v1")]);
        removed.Single(d => d.Key == "mapping").Should().Match<TestDataReadinessDimension>(d => d.State == TestDataDimensionState.Blocked && d.Detail.Contains("old-child (no longer in Test Data)"));
    }
}
