using System.Text.Json;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// XML payload versions: a regenerated fixture registered as the next version of the same alias is the same Test Data asset (stable id). The
/// approved version stays current — and is what consumers resolve — until the draft is explicitly approved; an invalid draft cannot be approved;
/// approving makes it current under the same id, so a Connected System binding follows without rebinding. The canonical fixture stays protected.
/// </summary>
public sealed class TestDataXmlVersionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-td-version-" + Guid.NewGuid().ToString("N"));
    private readonly List<object> _items = [];
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Alias = SkoleNaervaerCanonicalFixtures.ValidReportAlias;

    private object Version(string xml, int? version, bool legacyApproved = false, string? validation = "Valid", string? valueVersions = null) => new
    {
        id = Guid.NewGuid(), fileName = "contracts/skolenaervaer.xsd", profileName = "Valid", xml, projectScope = "import:m2lb",
        provenance = new { sourceSnapshotId = Guid.NewGuid(), selectedContractPath = "contracts/skolenaervaer.xsd", contractFingerprint = new string('b', 64) },
        testData = new
        {
            alias = Alias, intent = "positive", approved = legacyApproved, registeredAt = DateTimeOffset.UtcNow, version,
            facts = new { expectedResult = "Valid", validationStatus = validation, capabilities = new[] { SkoleNaervaerCanonicalFixtures.ReportCapability },
                testDataValueVersions = valueVersions ?? "skolenaervaer-child-default@v0" },
        },
    };

    private sealed class NoSchemas : ITestDataSchemaSource
    {
        public Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct) => Task.FromResult<TestDataSchemaPackage?>(null);
    }

    private sealed class Sources : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private (TestDataCatalog Catalog, TestDataCatalogService Service, TestDataRegistryStore Registry) Create()
    {
        Directory.CreateDirectory(_dir);
        var defaults = new ProtectedTestDataDefaultsStore(Path.Combine(_dir, "test-data"), NullLogger<ProtectedTestDataDefaultsStore>.Instance);
        var options = defaults.Overlay(SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions()));
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "test-data"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new AltinnTestDataProvider(options),
            new SchemaStudioTestDataProvider(() => [JsonSerializer.Serialize(new { version = 1, items = _items }, new JsonSerializerOptions(JsonSerializerDefaults.Web))]),
            new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, [new SkoleNaervaerCanonicalFixtures()]);
        var sources = new Sources();
        var service = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry),
            sources, TimeProvider.System);
        return (catalog, service, registry);
    }

    private static readonly ConnectedSystemTestDataRequirement Payload = new("report-payload", "Valid report payload", TestDataSemanticRoles.ReportXml, TestDataDataType.XmlPayload, true,
        [TestDataContexts.SkolenaervaerReport], "");

    private static ConnectedSystemTestDataRow PayloadRow(TestDataCatalog catalog) =>
        ConnectedSystemTestDataMatcher.View(new ExternalIntegration
        {
            Id = "env:altinn", TargetEnvironmentId = "env", Name = "Altinn — buf/skolenaervaer", EnvironmentName = "TT02",
            TestDataBindings = [new("report-payload", Alias) { AssetId = "schema-studio:" + Alias }],
        }, [Payload], catalog.View().Assets).Rows.Single();

    [Fact]
    public async Task A_regenerated_draft_never_replaces_the_approved_version_until_it_is_approved_under_the_same_id()
    {
        _items.Add(Version("<Report>v1</Report>", null, legacyApproved: true));
        _items.Add(Version("<Report>v2</Report>", 2));
        var (catalog, service, _) = Create();

        var current = catalog.Assets().Single(a => a.Alias == Alias);
        (current.Id, current.Version, current.Draft?.Version, current.Value!.Reveal()).Should().Be(("schema-studio:" + Alias, 1, 2, "<Report>v1</Report>"),
            "the approved content stays current and untouched");
        current.ProtectionKind.Should().Be(TestDataProtectionKinds.CanonicalFixture);
        var summary = catalog.View().Assets.Single(a => a.Alias == Alias);
        (summary.Version, summary.DraftVersion, summary.DraftValidation, summary.VersionCount, summary.Approval).Should().Be((1, 2, "Valid", 2, TestDataApproval.Approved));
        (summary.SemanticRole, summary.AppliesTo).Should().Be((TestDataSemanticRoles.ReportXml, summary.AppliesTo), "declared fixture metadata makes Role / Applies to filters find it");
        summary.AppliesTo.Should().Contain([TestDataContexts.AltinnIntake, TestDataContexts.SkolenaervaerReport]);

        var before = PayloadRow(catalog);
        (before.State, before.BoundAssetId).Should().Be((ConnectedSystemTestDataState.Bound, "schema-studio:" + Alias));
        before.DraftNote.Should().Contain("Draft update available (version 2)");

        (await service.ApproveAsync(current.Id, 2, CancellationToken.None)).Should().BeNull();
        var promoted = catalog.Assets().Single(a => a.Alias == Alias);
        (promoted.Id, promoted.Version, promoted.Draft, promoted.Value!.Reveal(), promoted.Approval).Should().Be(("schema-studio:" + Alias, 2, null, "<Report>v2</Report>", TestDataApproval.Approved));
        var after = PayloadRow(catalog);
        (after.State, after.DraftNote).Should().Be((ConnectedSystemTestDataState.Bound, null), "the binding follows the stable id — no rebinding");
    }

    [Fact]
    public async Task An_invalid_draft_cannot_be_approved_and_the_approved_version_stays_current()
    {
        _items.Add(Version("<Report>v1</Report>", null, legacyApproved: true));
        _items.Add(Version("<Report><Broken/></Report>", 2, validation: "Invalid"));
        var (catalog, service, _) = Create();
        var id = catalog.Assets().Single(a => a.Alias == Alias).Id;

        (await service.ApproveAsync(id, 2, CancellationToken.None)).Should().Contain("invalid XML cannot be approved");
        catalog.Assets().Single(a => a.Alias == Alias).Value!.Reveal().Should().Be("<Report>v1</Report>");
        (await service.ApproveAsync(id, 7, CancellationToken.None)).Should().Contain("has no version 7");
    }

    [Fact]
    public void Same_alias_without_distinct_versions_stays_ambiguous_and_is_never_merged()
    {
        _items.Add(Version("<Report>a</Report>", null));
        _items.Add(Version("<Report>b</Report>", null));
        var (catalog, _, _) = Create();
        catalog.Assets().Where(a => a.Alias == Alias).Should().OnlyContain(a => a.Versions == null);
    }

    [Fact]
    public void A_changed_mapped_input_marks_the_payload_stale_and_reporter_changes_do_not()
    {
        _items.Add(Version("<Report>v1</Report>", null, legacyApproved: true, valueVersions: "skolenaervaer-child-default@v0"));
        var (catalog, _, _) = Create();
        var assets = catalog.View().Assets;
        TestDataValueVersions.Changed(assets.Single(a => a.Alias == Alias), assets).Should().BeEmpty();
        var childChanged = assets.Select(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias ? a with { ValueVersion = 1 } : a).ToList();
        TestDataValueVersions.Changed(childChanged.Single(a => a.Alias == Alias), childChanged).Should().ContainSingle(c => c.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias);
        var reporterChanged = assets.Select(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ReporterAlias ? a with { ValueVersion = 3 } : a).ToList();
        TestDataValueVersions.Changed(reporterChanged.Single(a => a.Alias == Alias), reporterChanged).Should().BeEmpty("the reporter is submission context, not mapped into the XML");
    }

    [Fact]
    public async Task A_version_nobody_validated_cannot_be_approved()
    {
        _items.Add(Version("<Report>v1</Report>", null, legacyApproved: true));
        _items.Add(Version("<Report>v2</Report>", 2, validation: null));
        var (catalog, service, _) = Create();
        var id = catalog.Assets().Single(a => a.Alias == Alias).Id;

        (await service.ApproveAsync(id, 2, CancellationToken.None)).Should().Contain("has not been validated against its XSD", "existing is not valid");
        catalog.Assets().Single(a => a.Alias == Alias).Value!.Reveal().Should().Be("<Report>v1</Report>");
    }

    [Fact]
    public void A_duplicated_alias_is_reported_by_consumers_never_thrown_and_never_revealed()
    {
        _items.Add(Version("<Report>a</Report>", null));
        _items.Add(Version("<Report>b</Report>", null));
        var (catalog, _, _) = Create();

        var byAlias = catalog.ByAlias();
        byAlias[Alias].Approval.Should().Be(TestDataApproval.Disabled, "every copy of a duplicated alias is disabled until it is unique");
        catalog.Reveal(Alias).Should().BeNull("a disabled duplicate's value is never handed to a run");
    }

    [Fact]
    public void An_approved_fixture_built_from_changed_Test_Data_needs_regeneration_and_is_never_ready()
    {
        var stale = new TestDataXmlFixtureFacts(Alias, Approved: true, TestDataSchemaStatus.Validated, null, InputsChanged: true);
        TestDataXmlReadiness.Evaluate([stale]).State.Should().Be(TestDataXmlReadinessState.RegenerationRequired);
        TestDataXmlReadiness.Evaluate([stale, stale with { Alias = "fresh", InputsChanged = false }]).Should().Be((TestDataXmlReadinessState.Ready, stale with { Alias = "fresh", InputsChanged = false }));
    }
}
