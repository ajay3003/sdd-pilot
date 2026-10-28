using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.Services.Schemas;
using BirkNext.Schemas;
using BirkNext.TestData;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// Test Data → Create XML fixture, backend side: Test Data values are placed into generated XML by alias (approved assets with a value only, at
/// the element path the test pack declares — never created, never an arbitrary element), and a saved fixture keeps its contract, role aliases and
/// root element. The real Skolenærvær v2.0.0 XSD validates the bound result.
/// </summary>
public sealed class TestDataXmlFixtureTests
{
    private const string Ns = "https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.0.altinn";
    private const string ChildPath = "Klient.Identifikator.Fodselsnummer";

    /// <summary>A minimum Skolenærvær instance with the child's element present (as Test Data's generation produces it) holding a placeholder.</summary>
    internal static string Skeleton(string fodselsnummer = "00000000000") => $"""
        <?xml version="1.0" encoding="utf-8"?><Skolenaervaer xmlns="{Ns}"><Meldingshode><Id>00000000-0000-0000-0000-000000000000</Id><MeldingstypeNmsp>urn:test</MeldingstypeNmsp><FagsystemAvsender><Leverandor>Test</Leverandor><Navn>Test</Navn><Versjon>1</Versjon></FagsystemAvsender><SendtTidspunkt>2024-01-01T00:00:00Z</SendtTidspunkt><Avsender/><Mottaker/></Meldingshode><Klient><Identifikator><Fodselsnummer>{fodselsnummer}</Fodselsnummer></Identifikator></Klient><Periode><StartDato>2024-01-01T00:00:00Z</StartDato><SluttDato>2024-02-01T00:00:00Z</SluttDato></Periode><SkolenaervaerRegistrering><AntallTimerEtterPlanen>10</AntallTimerEtterPlanen><AntallTimerFaktiskNaervaer>9</AntallTimerFaktiskNaervaer></SkolenaervaerRegistrering></Skolenaervaer>
        """.Trim();

    private static TestDataAsset Child(string alias = "skolenaervaer-child-default", TestDataApproval approval = TestDataApproval.Approved, string? value = "04061076692") => new()
    {
        Id = $"altinn-local:child:{alias}", Alias = alias, Kind = TestDataKind.EntityReference, Capabilities = ["person", "child"], Approval = approval,
        Value = value is null ? null : new TestDataValue(value),
    };

    [Fact]
    public void The_child_is_resolved_by_alias_into_its_element_and_the_result_is_valid_against_the_real_XSD()
    {
        var result = TestDataXmlBinder.Bind(new(Skeleton(), [new("skolenaervaer-child-default", ChildPath)]), [Child()]);

        result.Problems.Should().BeEmpty();
        result.Xml.Should().Contain("<Fodselsnummer>04061076692</Fodselsnummer>").And.NotContain("<Fodselsnummer>00000000000</Fodselsnummer>");
        var files = new Dictionary<string, string> { [RealSchemaFixtures.Skolenaervaer200] = RealSchemaFixtures.Read(RealSchemaFixtures.Skolenaervaer200) };
        XmlSchemaPackageAnalyzer.ValidateXml(files, result.Xml!).Status.Should().Be(XmlSchemaValidationStatus.Valid, "a bound fixture stays schema-valid");
    }

    [Fact]
    public void Nothing_is_bound_from_an_unknown_unapproved_or_value_less_asset_or_into_an_absent_element()
    {
        TestDataXmlBinder.Bind(new(Skeleton(), [new("child-x", ChildPath)]), [Child()]).Should().Match<TestDataXmlBindResult>(r => r.Xml == null && r.Problems.Single().Contains("no asset child-x"));
        TestDataXmlBinder.Bind(new(Skeleton(), [new("skolenaervaer-child-default", ChildPath)]), [Child(approval: TestDataApproval.Unapproved)]).Problems.Single().Should().Contain("not approved");
        TestDataXmlBinder.Bind(new(Skeleton(), [new("skolenaervaer-child-default", ChildPath)]), [Child(value: null)]).Problems.Single().Should().Contain("no value");
        TestDataXmlBinder.Bind(new(Skeleton(), [new("skolenaervaer-child-default", "Klient.Ukjent")]), [Child()]).Problems.Single().Should().Contain("has no element Klient.Ukjent", "an element is never invented");
        TestDataXmlBinder.Bind(new(Skeleton(), [new("skolenaervaer-child-default", "../x")]), [Child()]).Problems.Single().Should().Contain("not valid");
        TestDataXmlBinder.Bind(new("<!DOCTYPE x [<!ENTITY e 'v'>]><x>&e;</x>", []), []).Problems.Single().Should().Be("The XML could not be read.", "no DTD processing");
    }

    [Fact]
    public void A_saved_fixture_keeps_its_contract_role_aliases_and_root_element()
    {
        var json = SavedXmlDocument(approved: true, xml: Skeleton("04061076692"));
        var asset = new SchemaStudioTestDataProvider(() => [json]).Assets().Single();

        asset.Alias.Should().Be("skolenaervaer-valid-report");
        asset.MediaType.Should().Be("application/xml");
        asset.Schema!.RootElement.Should().Be("Skolenaervaer", "validation also checks the message root");
        asset.Capabilities.Should().BeEquivalentTo(["schema-derived", "skolenaervaer-report"],
            "the pack-declared capability makes it the journey's valid payload; malformed tokens and negative-test are never taken from facts");
        asset.Provenance.Should().Contain(p => p.Label == "Contract" && p.Value == "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn")
            .And.Contain(p => p.Label == "Test Data roles" && p.Value.Contains("Child: skolenaervaer-child-default"));
    }

    internal const string SchemaPath = "xsd/bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd";

    /// <summary>Schema Studio's saved-XML document as Test Data's XML fixture flow writes it (workspace persistence).</summary>
    internal static string SavedXmlDocument(bool approved, string xml, Guid? snapshot = null) => System.Text.Json.JsonSerializer.Serialize(new
    {
        Version = 1,
        Items = new[]
        {
            new
            {
                Id = Guid.NewGuid(), FileName = SchemaPath, ProfileName = "Test Data XML fixture", Xml = xml, ProjectScope = (string?)null,
                Provenance = new { SourceSnapshotId = snapshot ?? Guid.Parse("11111111-1111-1111-1111-111111111111"), SelectedContractPath = SchemaPath, ContractFingerprint = (string?)null },
                TestData = new
                {
                    Alias = "skolenaervaer-valid-report", Label = "Skolenærvær valid report", Intent = "positive", Approved = approved, RegisteredAt = DateTimeOffset.UnixEpoch,
                    Facts = new
                    {
                        ExpectedResult = "Valid", ValidationStatus = "Valid", RootElement = "Skolenaervaer", GeneratorVersion = "Test Data XML fixture v1",
                        Contract = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn",
                        TestDataBindings = "Child: skolenaervaer-child-default; Reporter (not in the XML): skolenaervaer-reporter-default",
                        Capabilities = new[] { "skolenaervaer-report", "Bad Token", "negative-test" },
                    },
                },
            },
        },
    });
}
