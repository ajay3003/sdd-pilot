using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.Schemas;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>Generic Test Data validates XML fixtures through the shared XSD validator; approval and schema validity stay independent.</summary>
public sealed class TestDataSchemaValidationTests
{
    private const string Xsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns="urn:fixture" targetNamespace="urn:fixture" elementFormDefault="qualified" version="2.0.0">
          <xs:element name="Report"><xs:complexType><xs:sequence>
            <xs:element name="StartDato" type="xs:dateTime"/>
            <xs:element name="Code"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="1"/></xs:restriction></xs:simpleType></xs:element>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string Valid = """<Report xmlns="urn:fixture"><StartDato>2024-01-01T00:00:00Z</StartDato><Code>1</Code></Report>""";
    private const string MissingStart = """<Report xmlns="urn:fixture"><Code>1</Code></Report>""";
    private static readonly Guid SnapshotId = Guid.Parse("7d5e2c58-2d0c-4a6c-9c76-0d3c2f1b5a11");

    private static TestDataAsset Xml(string alias, string value, TestDataApproval approval = TestDataApproval.Approved,
        TestDataSensitivity sensitivity = TestDataSensitivity.Public, TestDataSchemaReference? schema = null) => new()
    {
        Id = "fixtures:" + alias, Alias = alias, Kind = TestDataKind.Structured, MediaType = "application/xml", Approval = approval,
        Sensitivity = sensitivity, Source = "Fixtures", Value = new TestDataValue(value),
        Schema = schema ?? new TestDataSchemaReference("schemas/report.xsd", SnapshotId, Label: "Report 2.0.0"),
    };

    private static TestDataSchemaPackage Package(params (string Path, string Text)[] files) =>
        new(SnapshotId, files.ToDictionary(f => f.Path, f => f.Text, StringComparer.OrdinalIgnoreCase), true);

    private static readonly TestDataSchemaPackage Report = Package(("schemas/report.xsd", Xsd));

    [Fact]
    public void ValidXml_IsValidated_WithSchemaIdentity()
    {
        var check = TestDataSchemaValidator.Check(Xml("valid-report", Valid), Report)!;
        check.Status.Should().Be(TestDataSchemaStatus.Validated);
        check.SourceSnapshotId.Should().Be(SnapshotId);
        check.SchemaFingerprint.Should().Be(XmlSchemaPackageAnalyzer.Fingerprint(Xsd));
        check.SchemaVersion.Should().Be("2.0.0");
    }

    [Fact]
    public void ApprovedNegativeFixture_StaysApproved_AndIsInvalid()
    {
        var catalog = new TestDataCatalog([new StaticProvider(Xml("missing-required-field", MissingStart))]);
        var asset = catalog.Assets().Single();
        var summary = TestDataSchemaValidator.Apply(catalog.Summary(asset), TestDataSchemaValidator.Check(asset, Report));
        summary.Approval.Should().Be(TestDataApproval.Approved, "approval is a separate decision from schema validity");
        summary.Validation.Should().Be(TestDataValidation.Invalid);
        summary.SchemaCheck!.Status.Should().Be(TestDataSchemaStatus.Invalid);
        summary.SchemaCheck.Issues.Should().ContainSingle().Which.Should().Contain("StartDato");
        catalog.Assets().Should().ContainSingle("an invalid fixture is never removed or disabled");
    }

    [Fact]
    public void MissingSchema_IsSchemaUnavailable_AndValidationNeedsValidation()
    {
        var asset = Xml("valid-report", Valid);
        var check = TestDataSchemaValidator.Check(asset, null)!;
        check.Status.Should().Be(TestDataSchemaStatus.SchemaUnavailable);
        var summary = TestDataSchemaValidator.Apply(new TestDataCatalog([new StaticProvider(asset)]).View().Assets.Single(), check);
        summary.Validation.Should().Be(TestDataValidation.NeedsValidation, "well-formed XML whose schema is unavailable is not claimed valid");
    }

    [Fact]
    public void UnresolvedDependency_IsDependencyUnresolved()
    {
        var withImport = Xsd.Replace("<xs:element name=\"Report\">", "<xs:import namespace=\"urn:common\" schemaLocation=\"common.xsd\"/><xs:element name=\"Report\">");
        TestDataSchemaValidator.Check(Xml("valid-report", Valid), Package(("schemas/report.xsd", withImport)))!.Status
            .Should().Be(TestDataSchemaStatus.DependencyUnresolved);
    }

    [Fact]
    public void FingerprintMismatch_IsSchemaUnavailable()
    {
        var asset = Xml("valid-report", Valid, schema: new("schemas/report.xsd", SnapshotId, SchemaFingerprint: new string('a', 64)));
        TestDataSchemaValidator.Check(asset, Report)!.Status.Should().Be(TestDataSchemaStatus.SchemaUnavailable);
    }

    [Fact]
    public void WrongRoot_IsInvalid()
    {
        var asset = Xml("valid-report", Valid, schema: new("schemas/report.xsd", SnapshotId, RootElement: "Other"));
        TestDataSchemaValidator.Check(asset, Report)!.Status.Should().Be(TestDataSchemaStatus.Invalid);
    }

    [Fact]
    public void SensitiveXml_NeverLeaksIntoTheCheck()
    {
        const string secret = "28117512345";
        var xml = Valid.Replace("<Code>1</Code>", $"<Code>{secret}</Code>");
        var check = TestDataSchemaValidator.Check(Xml("sensitive", xml, sensitivity: TestDataSensitivity.Sensitive), Report)!;
        check.Status.Should().Be(TestDataSchemaStatus.Invalid);
        JsonSerializer.Serialize(check).Should().NotContain(secret);
    }

    [Fact]
    public void NonXmlAndNoSchema_AreNotEvaluatedOrUnchanged()
    {
        var json = Xml("json", "{}") with { MediaType = "application/json" };
        TestDataSchemaValidator.Check(json, Report)!.Status.Should().Be(TestDataSchemaStatus.NotEvaluated);
        TestDataSchemaValidator.Check(Xml("plain", Valid) with { Schema = null }, Report).Should().BeNull();
    }

    [Fact]
    public async Task SourceAnalysisSchemaSource_ResolvesExactSnapshot_AndNewestSnapshotContainingThePath()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var older = Guid.NewGuid();
        Seed(db, older, DateTimeOffset.UtcNow.AddDays(-1), ("schemas/report.xsd", Xsd));
        Seed(db, SnapshotId, DateTimeOffset.UtcNow, ("schemas/other.xsd", Xsd));
        await db.SaveChangesAsync();
        var source = new SourceAnalysisTestDataSchemaSource(new IqrSourceStore(db));

        (await source.FindAsync(new("schemas/report.xsd", older), default))!.SnapshotId.Should().Be(older);
        (await source.FindAsync(new("schemas/report.xsd", SnapshotId), default)).Should().BeNull("the exact snapshot does not retain that path; no other snapshot is substituted");
        (await source.FindAsync(new("schemas/report.xsd"), default))!.SnapshotId.Should().Be(older, "the newest snapshot that contains the path");

        var service = new TestDataSchemaValidationService(new TestDataCatalog([new StaticProvider(Xml("valid-report", Valid, schema: new("schemas/report.xsd")))]), source);
        var view = await new TestDataController(service).Catalog(default);
        var summary = ((Microsoft.AspNetCore.Mvc.OkObjectResult)view.Result!).Value.Should().BeOfType<TestDataCatalogView>().Which.Assets.Single();
        summary.SchemaCheck!.Status.Should().Be(TestDataSchemaStatus.Validated);
        summary.SchemaCheck.SourceSnapshotId.Should().Be(older);
        summary.Validation.Should().Be(TestDataValidation.Validated);
    }

    private static void Seed(AppDbContext db, Guid id, DateTimeOffset at, params (string Path, string Text)[] files)
    {
        var snapshot = new IqrSourceSnapshot { Id = id, IntegrationId = IqrSourceStore.SourceAnalysisOwner, Archive = new("schemas.zip", "fp-" + id.ToString("N")[..8], files.Length), AnalyzedAt = at };
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = id, IntegrationId = IqrSourceStore.SourceAnalysisOwner, EvidenceJson = JsonSerializer.Serialize(snapshot), AnalyzedAt = at });
        db.SourceSchemaArtifacts.Add(new SourceSchemaArtifactRecord
        {
            SnapshotId = id, SnapshotFingerprint = snapshot.Archive.Sha256, SourceArchiveName = "schemas.zip", CapturedAt = at, IsComplete = true,
            PackageJson = JsonSerializer.Serialize(files.Select(f => new { path = f.Path, text = f.Text, fingerprint = XmlSchemaPackageAnalyzer.Fingerprint(f.Text) })),
        });
    }

    private sealed class StaticProvider(params TestDataAsset[] assets) : ITestDataProvider
    {
        public string Id => "fixtures";
        public string Label => "Fixtures";
        public string Description => "Static XML fixtures";
        public IReadOnlyList<TestDataAsset> Assets() => assets;
        public IReadOnlyList<TestDataSet> Sets() => [];
    }
}
