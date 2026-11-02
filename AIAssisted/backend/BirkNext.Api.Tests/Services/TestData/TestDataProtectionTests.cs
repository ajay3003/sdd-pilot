using System.Text.Json;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.TestData;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// Generic, persisted Test Data protection: the canonical Skolenærvær valid report fixture (alias + the pack's report capability + positive intent)
/// is protected by its stable asset id — like the child/reporter protected defaults — so Delete refuses it and Reset Test Data keeps it with its
/// current content, approval and bindings. Approved is not protected: a user's approved XML, or the same alias without the pack capability, is not.
/// </summary>
public sealed class TestDataProtectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-td-protect-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _saved = [];
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Document(params object[] items) => JsonSerializer.Serialize(new { version = 1, items }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static object Saved(string xml, string alias, bool approved, string[]? capabilities, string intent = "positive") => new
    {
        id = Guid.NewGuid(), fileName = "contracts/skolenaervaer.xsd", profileName = "Valid", xml, projectScope = "import:m2lb",
        provenance = new { sourceSnapshotId = Guid.NewGuid(), selectedContractPath = "contracts/skolenaervaer.xsd", contractFingerprint = new string('b', 64) },
        testData = new { alias, intent, approved, registeredAt = DateTimeOffset.UtcNow,
            facts = new { expectedResult = "Valid", validationStatus = "Valid", capabilities, testDataValueVersions = "skolenaervaer-child-default@v1" } },
    };

    private (TestDataCatalog Catalog, TestDataRegistryStore Registry, TestDataLifecycleService Lifecycle) Create()
    {
        Directory.CreateDirectory(_dir);
        var defaults = new ProtectedTestDataDefaultsStore(Path.Combine(_dir, "test-data"), NullLogger<ProtectedTestDataDefaultsStore>.Instance);
        var options = defaults.Overlay(SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions()));
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "test-data"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new AltinnTestDataProvider(options), new SchemaStudioTestDataProvider(() => _saved), new RegisteredTestDataProvider(registry)],
            TimeProvider.System, registry, [new SkoleNaervaerCanonicalFixtures()]);
        var lifecycle = new TestDataLifecycleService(catalog, registry, [new AltinnTestDataEditor(new AltinnSessionStore(options, TimeProvider.System), defaults, options)], [], TimeProvider.System);
        return (catalog, registry, lifecycle);
    }

    private const string ValidReport = SkoleNaervaerCanonicalFixtures.ValidReportAlias;
    private static readonly string[] ReportCapability = [SkoleNaervaerCanonicalFixtures.ReportCapability];

    [Fact]
    public void TheCanonicalValidReport_IsProtectedByItsStableId_Once_AlongsideTheProtectedDefaults()
    {
        _saved.Add(Document(Saved("<Report><Child>x</Child></Report>", ValidReport, true, ReportCapability)));
        var (catalog, registry, _) = Create();

        var assets = catalog.Assets();
        var report = assets.Single(a => a.Alias == ValidReport);
        report.ProtectionKind.Should().Be(TestDataProtectionKinds.CanonicalFixture);
        report.IsProtected.Should().BeTrue();
        report.ProtectionReason.Should().Contain("Required baseline data for Skolenærvær");
        assets.Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias).ProtectionKind.Should().Be(TestDataProtectionKinds.ProtectedDefault);
        assets.Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ReporterAlias).ProtectionKind.Should().Be(TestDataProtectionKinds.ProtectedDefault);

        catalog.Assets(); catalog.Assets();
        registry.ProtectedAssets().Should().ContainSingle("seeding/migration is idempotent").Which.AssetId.Should().Be(report.Id);
        var summary = catalog.Summary(report);
        summary.ProtectionLabel.Should().Be("Protected fixture");
        summary.Deletable.Should().BeFalse();
        summary.Approval.Should().Be(TestDataApproval.Approved, "protection does not change approval");
    }

    [Fact]
    public void ApprovedIsNotProtected_AndTheSameAliasWithoutThePackCapabilityIsNotTheCanonicalFixture()
    {
        _saved.Add(Document(
            Saved("<Report/>", "my-valid-report", true, ReportCapability),
            Saved("<Report/>", ValidReport, true, null)));
        var (catalog, registry, _) = Create();
        var assets = catalog.Assets();
        assets.Single(a => a.Alias == "my-valid-report").IsProtected.Should().BeFalse("a user's approved XML is not protected");
        assets.Single(a => a.Alias == ValidReport).IsProtected.Should().BeFalse("same alias, but not created for the pack's report capability");
        registry.ProtectedAssets().Should().BeEmpty();

        _saved.Clear();
        _saved.Add(Document(Saved("<Report/>", ValidReport, true, ReportCapability, intent: TestDataProtection.NegativeTest)));
        catalog.Assets().Single(a => a.Alias == ValidReport).IsProtected.Should().BeFalse("a negative-test variant is never the valid fixture");
    }

    [Fact]
    public async Task DirectDeleteOfTheProtectedFixture_IsRefusedWithADomainError()
    {
        _saved.Add(Document(Saved("<Report/>", ValidReport, true, ReportCapability)));
        var (catalog, _, lifecycle) = Create();
        var id = catalog.Assets().Single(a => a.Alias == ValidReport).Id;

        var (result, status) = await lifecycle.DeleteAsync(id, leaveReferencesUnresolved: true, default);

        status.Should().Be(409);
        result.Deleted.Should().BeFalse();
        result.Message.Should().StartWith("This Test Data asset is protected and cannot be deleted");
    }

    [Fact]
    public async Task ResetTestData_KeepsProtectedAssets_TheirApprovalAndBindings_AndRemovesTheRest()
    {
        _saved.Add(Document(Saved("<Report><Child>x</Child></Report>", ValidReport, true, ReportCapability)));
        var (catalog, registry, lifecycle) = Create();
        var report = catalog.Assets().Single(a => a.Alias == ValidReport);
        registry.Register(new RegisteredTestDataAsset { Alias = "observed-user-id", Label = "observed", DataType = TestDataDataType.SimpleValues, RegisteredAt = DateTimeOffset.UtcNow });
        registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = report.Id, ApprovedFingerprint = "fp", ApprovedAt = DateTimeOffset.UtcNow });
        registry.Bind(new TestDataUsageReference { Id = "u1", AssetId = report.Id, Usage = "Valid report payload", Consumer = new TestDataConsumer { ConsumerType = "ConnectedSystem", ConsumerId = "altinn", DisplayName = "Altinn — buf/skolenaervaer" } });
        registry.Bind(new TestDataUsageReference { Id = "u2", AssetId = "registered:observed-user-id", Usage = "path", Consumer = new TestDataConsumer { ConsumerType = "ConnectedSystem", ConsumerId = "other", DisplayName = "Other" } });

        var preview = await lifecycle.PreviewResetAsync(default);
        preview.ProtectedFixtures.Should().Equal(ValidReport);
        preview.ProtectedDefaults.Should().Contain([SkoleNaervaerTestIdentityDefaults.ChildAlias, SkoleNaervaerTestIdentityDefaults.ReporterAlias]);
        preview.AssetsToRemove.Should().Contain("observed-user-id").And.NotContain(ValidReport);

        var result = await lifecycle.ResetAsync(generatedFixturesRemoved: 0, default);

        result.Summary.ProtectedFixturesPreserved.Should().Be(1);
        result.Summary.ProtectedDefaultsPreserved.Should().Be(2);
        var after = catalog.Assets().Single(a => a.Alias == ValidReport);
        after.Id.Should().Be(report.Id, "stable id");
        after.Value!.Reveal().Should().Be("<Report><Child>x</Child></Report>", "content is never restored or regenerated");
        registry.Lifecycle(report.Id).Should().NotBeNull("its approval decision is kept");
        registry.Usages().Should().ContainSingle(u => u.AssetId == report.Id, "its consumer binding is kept").And.NotContain(u => u.Id == "u2");
        catalog.Assets().Should().NotContain(a => a.Alias == "observed-user-id");
        registry.ProtectedAssets().Should().ContainSingle();
    }
}
