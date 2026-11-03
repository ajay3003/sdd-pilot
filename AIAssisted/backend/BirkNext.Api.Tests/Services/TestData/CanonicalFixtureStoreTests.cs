using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.TestData;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// The canonical Skolenærvær valid report is kept by Test Data itself (backend canonical fixture store), not in a browser workspace artifact that a
/// workspace save, a new project or a reset removes: one stable asset (schema-studio:skolenaervaer-valid-report) whose content evolves through
/// append-only versions, protected, surviving restart and Reset Test Data, and bound to the consumer that uses it — by stable id.
/// </summary>
public sealed class CanonicalFixtureStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-td-canonical-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _workspace = [];
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string ValidReport = SkoleNaervaerCanonicalFixtures.ValidReportAlias;
    private static readonly IReadOnlyList<TestDataCanonicalFixture> Fixtures = new SkoleNaervaerCanonicalFixtures().Fixtures;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonObject Item(string xml, string alias = ValidReport, string intent = "positive", string validation = "Valid", string[]? capabilities = null, string scope = "import:m2lb") =>
        JsonSerializer.SerializeToNode(new
        {
            id = Guid.NewGuid(), fileName = "contracts/skolenaervaer.xsd", profileName = "Valid", xml, projectScope = scope,
            provenance = new { sourceSnapshotId = Guid.NewGuid(), selectedContractPath = "contracts/skolenaervaer.xsd", contractFingerprint = new string('b', 64) },
            testData = new { alias, intent, approved = true, registeredAt = DateTimeOffset.UtcNow,
                facts = new { expectedResult = "Valid", validationStatus = validation, capabilities = capabilities ?? [SkoleNaervaerCanonicalFixtures.ReportCapability],
                    testDataValueVersions = "skolenaervaer-child-default@v1" } },
        }, Web)!.AsObject();

    private CanonicalFixtureStore Store() => new(Path.Combine(_dir, "test-data"), NullLogger<CanonicalFixtureStore>.Instance);

    private (TestDataCatalog Catalog, TestDataRegistryStore Registry, TestDataLifecycleService Lifecycle) Catalog(CanonicalFixtureStore store)
    {
        var defaults = new ProtectedTestDataDefaultsStore(Path.Combine(_dir, "test-data"), NullLogger<ProtectedTestDataDefaultsStore>.Instance);
        var options = defaults.Overlay(SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions()));
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "test-data"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new AltinnTestDataProvider(options), new SchemaStudioTestDataProvider(() => _workspace, () => { store.Adopt(_workspace, Fixtures); return store.Document(); }),
            new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, [new SkoleNaervaerCanonicalFixtures()]);
        var lifecycle = new TestDataLifecycleService(catalog, registry, [new AltinnTestDataEditor(new AltinnSessionStore(options, TimeProvider.System), defaults, options)], [], TimeProvider.System);
        return (catalog, registry, lifecycle);
    }

    [Fact]
    public void Saving_creates_one_canonical_asset_and_regenerating_adds_a_version_of_the_same_asset()
    {
        var store = Store();

        store.Add(Item("<Report><Child>1</Child></Report>"), Fixtures, DateTimeOffset.UtcNow).Should().Be(((int?)1, (string?)null));
        store.Add(Item("<Report><Child>2</Child></Report>"), Fixtures, DateTimeOffset.UtcNow).Version.Should().Be(2);

        var asset = Catalog(store).Catalog.Assets().Where(a => a.Alias == ValidReport).Should().ContainSingle("versions never create a second asset").Subject;
        asset.Id.Should().Be($"{SchemaStudioTestDataProvider.ProviderId}:{ValidReport}");
        asset.Versions.Should().HaveCount(2);
        asset.Approval.Should().Be(TestDataApproval.Unapproved, "a stored version is never approved by saving — approval is a Test Data decision");
        asset.ProtectionKind.Should().Be(TestDataProtectionKinds.CanonicalFixture);
        asset.Provenance.Should().Contain(f => f.Label == "Saved in" && f.Value == "Test Data (canonical fixture store)");
    }

    [Theory]
    [InlineData("other-report", "positive", "Valid", true, "not a canonical fixture")]
    [InlineData(ValidReport, "negative-test", "Valid", true, "positive")]
    [InlineData(ValidReport, "positive", "Invalid", true, "validated")]
    [InlineData(ValidReport, "positive", "Valid", false, "not created for")]
    public void Only_a_valid_positive_fixture_created_for_the_declared_capability_is_canonical(string alias, string intent, string validation, bool capability, string reason)
    {
        var store = Store();
        var (version, error) = store.Add(Item("<Report/>", alias, intent, validation, capability ? null : ["schema-derived"]), Fixtures, DateTimeOffset.UtcNow);
        version.Should().BeNull();
        error.Should().Contain(reason);
        store.Versions().Should().BeEmpty();
    }

    [Fact]
    public void The_fixture_survives_a_restart_and_keeps_its_id_and_protection()
    {
        Store().Add(Item("<Report><Child>1</Child></Report>"), Fixtures, DateTimeOffset.UtcNow);
        var (first, registry, _) = Catalog(Store());
        var id = first.Assets().Single(a => a.Alias == ValidReport).Id;

        var (restarted, _, _) = Catalog(Store()); // new store and registry instances over the same files = API restart
        var asset = restarted.Assets().Single(a => a.Alias == ValidReport);
        asset.Id.Should().Be(id);
        asset.IsProtected.Should().BeTrue();
        registry.ProtectedAssets().Should().ContainSingle(p => p.AssetId == id);
    }

    [Fact]
    public void A_workspace_copy_does_not_replace_or_duplicate_the_canonical_fixture_and_its_removal_does_not_remove_it()
    {
        var store = Store();
        store.Add(Item("<Report><Child>canonical</Child></Report>"), Fixtures, DateTimeOffset.UtcNow);
        _workspace.Add(JsonSerializer.Serialize(new { version = 1, items = new[] { Item("<Report><Child>stale</Child></Report>") } }, Web));
        var (catalog, _, _) = Catalog(store);

        catalog.Assets().Where(a => a.Alias == ValidReport).Should().ContainSingle().Which.Value!.Reveal().Should().Contain("canonical");
        _workspace.Clear(); // the workspace artifact is replaced by an auto-save from a session without it
        catalog.Assets().Should().ContainSingle(a => a.Alias == ValidReport);
    }

    [Fact]
    public void An_existing_workspace_fixture_is_adopted_once_and_an_ambiguous_one_is_never_merged()
    {
        _workspace.Add(JsonSerializer.Serialize(new { version = 1, items = new[] { Item("<Report><Child>legacy</Child></Report>") } }, Web));
        var store = Store();
        store.Adopt(_workspace, Fixtures).Should().Equal(ValidReport);
        store.Adopt(_workspace, Fixtures).Should().BeEmpty("adoption is idempotent");
        store.Versions().Should().ContainSingle();

        var ambiguous = new CanonicalFixtureStore(Path.Combine(_dir, "other"), NullLogger<CanonicalFixtureStore>.Instance);
        var twoProjects = JsonSerializer.Serialize(new { version = 1, items = new[] { Item("<A/>", scope: "import:a"), Item("<B/>", scope: "import:b") } }, Web);
        ambiguous.Adopt([twoProjects], Fixtures).Should().BeEmpty("two projects' fixtures are not one canonical asset — nothing is guessed");
        ambiguous.Versions().Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_Test_Data_keeps_the_fixture_its_content_and_protection_and_delete_is_refused()
    {
        var store = Store();
        store.Add(Item("<Report><Child>1</Child></Report>"), Fixtures, DateTimeOffset.UtcNow);
        var (catalog, registry, lifecycle) = Catalog(store);
        var before = catalog.Assets().Single(a => a.Alias == ValidReport);

        await lifecycle.ResetAsync(generatedFixturesRemoved: 0, default);

        var after = Catalog(Store()).Catalog.Assets().Single(a => a.Alias == ValidReport);
        (after.Id, after.Value!.Reveal(), after.IsProtected).Should().Be((before.Id, before.Value!.Reveal(), true));
        foreach (var alias in new[] { ValidReport, SkoleNaervaerTestIdentityDefaults.ChildAlias, SkoleNaervaerTestIdentityDefaults.ReporterAlias })
        {
            var (_, status) = await lifecycle.DeleteAsync(catalog.Assets().Single(a => a.Alias == alias).Id, leaveReferencesUnresolved: true, default);
            status.Should().Be(409, $"{alias} is protected: the backend refuses delete");
        }
        catalog.Assets().Should().Contain(a => a.Alias == ValidReport);
    }

    [Fact]
    public void The_consumer_persists_what_it_consumes_by_stable_id_once_and_never_rebinds()
    {
        var (catalog, registry, _) = Catalog(Store());
        var consumers = new[] { new SkoleNaervaerTestDataConsumers() };

        TestDataConsumerBindings.Ensure(registry, catalog, consumers, DateTimeOffset.UtcNow).Should().BeTrue();
        TestDataConsumerBindings.Ensure(registry, catalog, consumers, DateTimeOffset.UtcNow).Should().BeFalse("idempotent");

        registry.Requirements().Select(r => (r.Usage, r.Required)).Should().BeEquivalentTo(new[]
        {
            ("Child · XML child field", true), ("Reporter · submitting Altinn actor (not in the XML)", true), ("Valid report payload · XML", false),
        });
        var assets = catalog.Assets();
        registry.Usages().Select(u => u.AssetId).Should().BeEquivalentTo(
            [assets.Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias).Id, assets.Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ReporterAlias).Id],
            "the child and reporter the consumer resolves are bound by stable id; no XML fixture exists, so none is bound");
        registry.Usages().Should().OnlyContain(u => u.Consumer.ConsumerId == "skolenaervaer-report-intake" && u.Mapping == TestDataConsumerBindings.ResolvedByConsumer);
    }

    [Fact]
    public void A_bound_asset_that_disappears_leaves_an_unresolved_reference_not_a_ready_requirement()
    {
        var (catalog, registry, _) = Catalog(Store());
        TestDataConsumerBindings.Ensure(registry, catalog, [new SkoleNaervaerTestDataConsumers()], DateTimeOffset.UtcNow);
        var childUsage = registry.Usages().Single(u => u.Usage.StartsWith("Child", StringComparison.Ordinal));
        var requirement = registry.Requirements().Single(r => r.Id == childUsage.RequirementId);

        var (status, reason) = TestDataCatalogRules.UsageStatus(childUsage, null); // the bound asset no longer resolves
        status.Should().Be(TestDataUsageStatus.NeedsReview);
        reason.Should().Contain("no longer exists");
        TestDataCatalogRules.RequirementStatus(requirement, [childUsage with { Status = status }]).Should().Be(TestDataRequirementStatus.NeedsReview, "never Ready");
    }
}
