using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// Approval of XML Test Data: the server's XSD validation is authoritative. Schema-backed XML is approved only when the server validated it;
/// a browser-only "Valid" may be saved and kept as a draft but never approved; non-schema XML is not blocked for lacking an XSD.
/// </summary>
public sealed class TestDataXmlApprovalTrustTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-td-approval-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Xsd = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="Report"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="ok" /></xs:restriction></xs:simpleType></xs:element></xs:schema>""";

    private sealed class Provider : ITestDataProvider
    {
        public List<TestDataAsset> Items { get; } = [];
        public string Id => "configured";
        public string Label => "Configured";
        public string Description => "In-test assets.";
        public IReadOnlyList<TestDataAsset> Assets() => Items;
    }

    private sealed class Schemas(bool available) : ITestDataSchemaSource
    {
        public Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct) =>
            Task.FromResult(available ? new TestDataSchemaPackage(Guid.NewGuid(), new Dictionary<string, string> { [reference.SchemaPath] = Xsd }, true) : null);
    }

    private sealed class Sources : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private (TestDataCatalogService Service, TestDataCatalog Catalog) Create(Provider provider, bool xsdOnServer)
    {
        var registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([provider], TimeProvider.System, registry, null, new TestDataIdentityStore(_dir));
        var sources = new Sources();
        return (new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new Schemas(xsdOnServer)), catalog, registry, new TestDataCandidateService(sources, registry),
            sources, TimeProvider.System), catalog);
    }

    private static TestDataAsset Xml(string alias, string xml, string? recorded, bool schemaBacked = true) => new()
    {
        Id = $"configured:{alias}", Alias = alias, Kind = TestDataKind.Structured, MediaType = "application/xml", Value = new TestDataValue(xml), RecordedValidation = recorded,
        Schema = schemaBacked ? new TestDataSchemaReference("report.xsd") : null,
    };

    [Fact]
    public async Task Schema_backed_xml_with_only_a_browser_valid_claim_cannot_be_approved()
    {
        var provider = new Provider();
        provider.Items.Add(Xml("claimed", "<Report>ok</Report>", "Valid"));
        var (service, catalog) = Create(provider, xsdOnServer: false);

        var error = await service.ApproveAsync(catalog.Assets().Single().Id, null, default);

        error.Should().StartWith("Approval blocked — server validation required").And.Contain("only client-reported as valid").And.Contain("not available to the server")
            .And.Contain("keep it as a draft");
        catalog.View().Assets.Single().Status.Should().NotBe(TestDataAssetStatus.Approved, "the asset stays a draft / needs validation");
    }

    [Fact]
    public async Task Server_validation_approves_and_outranks_the_browser_claim_both_ways()
    {
        var provider = new Provider();
        provider.Items.Add(Xml("server-valid-browser-invalid", "<Report>ok</Report>", "Valid"));
        provider.Items.Add(Xml("server-invalid-browser-valid", "<Report>nope</Report>", "Valid"));
        var (service, catalog) = Create(provider, xsdOnServer: true);

        (await service.ApproveAsync("configured:server-valid-browser-invalid", null, default)).Should().BeNull("the server validated it against the XSD it holds");
        (await service.ApproveAsync("configured:server-invalid-browser-valid", null, default)).Should().StartWith("Validation failed", "the server's Invalid outranks a browser Valid");
        catalog.Identities!.Identities().Single(i => i.CurrentAlias == "server-invalid-browser-valid").Validations.Single().Trust.Should().Be(TestDataValidationTrust.ValidationFailed);
    }

    [Fact]
    public async Task Non_schema_xml_is_not_blocked_for_lacking_an_xsd()
    {
        var provider = new Provider();
        provider.Items.Add(Xml("free-form", "<Note>hello</Note>", null, schemaBacked: false));
        var (service, catalog) = Create(provider, xsdOnServer: false);

        (await service.ApproveAsync(catalog.Assets().Single().Id, null, default)).Should().BeNull("no schema association is expected for this asset");
    }
}
