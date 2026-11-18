using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Source-aware XSD choice (2026-11-18): Schema Studio used to pre-select the first file by path (a referral schema in a Tiltak 2-style archive).
/// A schema is pre-selected only when the source evidence makes it the single best candidate; ties and no evidence are explained, not guessed.
/// Generic synthetic names; nothing product-specific is encoded.
/// </summary>
public sealed class SchemaSourceSelectionTests
{
    private static SourceSchemaFile File(string path, string tier = SchemaSourceSelection.DeclaredTier, string? version = null, params (string Kind, string File)[] refs) =>
        new(path, "<xs:schema />", "f", new SourceSchemaUsage("https://example.test/" + System.IO.Path.GetFileNameWithoutExtension(path), version, tier,
            refs.Length == 0 ? "Present in the source archive; nothing in the source names it" : $"{refs[0].Kind}: {refs[0].File}:1",
            [.. refs.Select(r => new SourceSchemaReference(r.File, 1, r.Kind))], false, "Not verified at runtime: no test run in BirkNext records which schema was applied to a message."));

    private static readonly SourceSchemaFile Referral = File("xsds/a.referral.v2.0.0.xsd");
    private static readonly SourceSchemaFile ReportFlat = File("xsds/example.report.v2.0.0.flat.xsd", SchemaSourceSelection.ValidationTier, "2.0.0",
        ("Validator (schema registry)", "Hub/XmlValidatorClient.cs"), ("Test code reference", "Hub.Tests/InboundTests.cs"), ("Schema registry entry", "xsds/registry.json"));
    private static readonly SourceSchemaFile ReportStandard = File("xsds/example.report.v2.0.0.xsd", SchemaSourceSelection.ValidationTier, "2.0.0",
        ("Validator (schema registry)", "Hub/XmlValidatorClient.cs"), ("Schema registry entry", "xsds/registry.json"));
    private static readonly SourceSchemaFile ReportOld = File("xsds/example.report.v0.8.2.flat.xsd", SchemaSourceSelection.ValidationTier, "0.8.2",
        ("Validator (schema registry)", "Hub/XmlValidatorClient.cs"), ("Schema registry entry", "xsds/registry.json"));

    [Fact]
    public void The_single_best_evidenced_schema_is_pre_selected_never_the_first_file_by_path()
    {
        var r = SchemaSourceSelection.Recommend([Referral, ReportOld, ReportStandard, ReportFlat]);
        r.Selected.Should().Be(ReportFlat, "registered for validation and named by a test beats registered only");
        r.Ambiguous.Should().BeFalse();
        r.Message.Should().StartWith("Pre-selected example.report.v2.0.0.flat.xsd (version 2.0.0): referenced by validation code")
            .And.Contain("Also referenced by validation code: example.report.v2.0.0.xsd, example.report.v0.8.2.flat.xsd")
            .And.EndWith("Not verified at runtime: no test run in BirkNext records which schema was applied to a message.");
    }

    [Fact]
    public void A_validator_naming_the_file_beats_a_registry_and_a_registry_beats_configuration_and_tests()
    {
        var literal = File("x/a.xsd", SchemaSourceSelection.ValidationTier, null, ("Validator", "A.cs"));
        var registry = File("x/b.xsd", SchemaSourceSelection.ValidationTier, null, ("Validator (schema registry)", "B.cs"));
        var config = File("x/c.xsd", SchemaSourceSelection.SourceTier, null, ("Configuration reference", "appsettings.json"), ("Test code reference", "T.cs"));
        SchemaSourceSelection.Rank([config, registry, literal]).Select(x => x.File).Should().Equal(literal, registry, config);
    }

    [Fact]
    public void Equally_evidenced_schemas_are_not_guessed()
    {
        var r = SchemaSourceSelection.Recommend([Referral, ReportOld, ReportStandard]);
        r.Selected.Should().BeNull();
        r.Ambiguous.Should().BeTrue();
        r.Tied.Should().BeEquivalentTo([ReportOld, ReportStandard]);
        r.Message.Should().StartWith("2 schemas have equally strong source evidence (referenced by validation code), so none is pre-selected");
    }

    [Fact]
    public void Without_source_evidence_nothing_is_pre_selected_and_the_reason_is_given()
    {
        var r = SchemaSourceSelection.Recommend([Referral, File("xsds/b.other.v1.xsd")]);
        r.Selected.Should().BeNull();
        r.Ambiguous.Should().BeFalse();
        r.Message.Should().Contain("No XSD in this snapshot is named by validation code, configuration or tests");
        SchemaSourceSelection.Recommend([]).Message.Should().Be("This snapshot contains no XSD files.");
    }

    [Theory]
    [InlineData("xsds/bufdir.barnevern.tiltak.skolenaervaer.v2.0.0.flat.xsd", "bufdir.barnevern.tiltak.skolenaervaer", "flat")]
    [InlineData("xsds/bufdir.barnevern.tiltak.skolenaervaer.v2.0.0.xsd", "bufdir.barnevern.tiltak.skolenaervaer", "standard")]
    [InlineData("xsds/bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd", "bufdir_barnevern_tiltak_skolenaervaer", "altinn")]
    public void Family_and_variant_tell_flat_and_non_flat_versions_apart(string path, string family, string variant)
    {
        SchemaSourceSelection.Family(path).Should().Be(family);
        SchemaSourceSelection.Variant(path).Should().Be(variant);
    }

    [Fact]
    public void Equal_evidence_is_ordered_by_family_then_newest_version()
    {
        SchemaSourceSelection.Rank([ReportOld, ReportStandard]).Select(x => SchemaSourceSelection.VersionOf(x.File)).Should().Equal("2.0.0", "0.8.2");
    }

    [Fact]
    public void A_large_list_is_ranked_quickly_and_completely()
    {
        var many = Enumerable.Range(0, 2000).Select(i => File($"xsds/family{i % 40}.v{i % 7}.{i % 3}.0.xsd")).Append(ReportFlat).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = SchemaSourceSelection.Recommend(many);
        watch.Stop();
        r.Selected.Should().Be(ReportFlat);
        SchemaSourceSelection.Rank(many).Should().HaveCount(2001);
        watch.ElapsedMilliseconds.Should().BeLessThan(2000);
    }
}
