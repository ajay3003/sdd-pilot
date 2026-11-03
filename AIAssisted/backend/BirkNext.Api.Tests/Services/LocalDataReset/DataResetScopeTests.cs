using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.LocalDataReset;
using BirkNext.Api.Services.TestData;
using BirkNext.Maintenance;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.LocalDataReset;

/// <summary>
/// One reset entry point with explicit scopes. "Reset everything" is the only path that overrides Test Data protection (after typed confirmation);
/// "Reset everything except protected data" keeps protected assets unchanged with their ids; Delivery Management owns and deletes only its schema.
/// Ordinary deletes of protected assets stay refused.
/// </summary>
public sealed class DataResetScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-reset-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed record Harness(TestDataCatalog Catalog, TestDataRegistryStore Registry, TestDataLifecycleService Lifecycle, CanonicalFixtureStore Store, AltinnTestingOptions Options);

    private Harness Build()
    {
        var dir = Path.Combine(_dir, "test-data");
        var store = new CanonicalFixtureStore(dir, NullLogger<CanonicalFixtureStore>.Instance);
        var defaults = new ProtectedTestDataDefaultsStore(dir, NullLogger<ProtectedTestDataDefaultsStore>.Instance);
        var options = defaults.Overlay(SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions()));
        var registry = new TestDataRegistryStore(dir, NullLogger<TestDataRegistryStore>.Instance);
        var fixtures = new SkoleNaervaerCanonicalFixtures();
        var catalog = new TestDataCatalog([new AltinnTestDataProvider(options), new SchemaStudioTestDataProvider(() => [], () => store.Document()), new RegisteredTestDataProvider(registry)],
            TimeProvider.System, registry, [fixtures]);
        var lifecycle = new TestDataLifecycleService(catalog, registry, [new AltinnTestDataEditor(new AltinnSessionStore(options, TimeProvider.System), defaults, options)], [], TimeProvider.System, store);
        return new(catalog, registry, lifecycle, store, options);
    }

    private static JsonObject Fixture() => JsonSerializer.SerializeToNode(new
    {
        id = Guid.NewGuid(), fileName = "contracts/skolenaervaer.xsd", profileName = "Valid", xml = "<Report><Child>1</Child></Report>", projectScope = "import:m2lb",
        provenance = new { sourceSnapshotId = Guid.NewGuid(), selectedContractPath = "contracts/skolenaervaer.xsd", contractFingerprint = new string('b', 64) },
        testData = new { alias = SkoleNaervaerCanonicalFixtures.ValidReportAlias, intent = "positive", approved = true, registeredAt = DateTimeOffset.UtcNow,
            facts = new { expectedResult = "Valid", validationStatus = "Valid", capabilities = new[] { SkoleNaervaerCanonicalFixtures.ReportCapability } } },
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();

    private static string ChildValue(Harness h) => h.Catalog.Assets().Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias).Value!.Reveal();

    [Fact]
    public async Task Reset_everything_removes_the_canonical_fixture_and_returns_protected_defaults_to_product_values()
    {
        var h = Build();
        h.Store.Add(Fixture(), new SkoleNaervaerCanonicalFixtures().Fixtures, DateTimeOffset.UtcNow);
        var child = h.Catalog.Assets().Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias);
        var seed = ChildValue(h);
        (await h.Lifecycle.EditAsync(child.Id, new TestDataEditRequest("Edited child", null), default)).Result.Updated.Should().BeTrue();
        File.Exists(Path.Combine(_dir, "test-data", "protected-defaults.json")).Should().BeTrue("the edit is stored as an overlay");
        var preview = await h.Lifecycle.PreviewResetAsync(default);

        var summary = await h.Lifecycle.CompleteFullResetIncludingProtectedAsync(preview, default);

        h.Store.Versions().Should().BeEmpty("the canonical fixture is genuinely removed");
        h.Catalog.Assets().Should().NotContain(a => a.Alias == SkoleNaervaerCanonicalFixtures.ValidReportAlias);
        ChildValue(h).Should().Be(seed, "the product default value applies again");
        h.Catalog.Assets().Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias).Label.Should().Be(SkoleNaervaerTestIdentityDefaults.ChildLabel, "the label returns to the product label too");
        File.Exists(Path.Combine(_dir, "test-data", "protected-defaults.json")).Should().BeFalse("stored edits are deleted, so a restart does not bring them back");
        summary.ProtectedDefaultsResetToProduct.Should().Be(1);
        summary.ProtectedFixturesRemoved.Should().Be(1);
        summary.SystemDefaultsRecreated.Should().Contain([SkoleNaervaerTestIdentityDefaults.ChildAlias, SkoleNaervaerTestIdentityDefaults.ReporterAlias]);
        summary.ProtectedDefaultsPreserved.Should().Be(0);

        h.Registry.ProtectedAssets().Should().BeEmpty("no stale protection record keyed on an alias-derived id survives");
        h.Store.Add(Fixture(), new SkoleNaervaerCanonicalFixtures().Fixtures, DateTimeOffset.UtcNow);
        h.Catalog.Assets().Single(a => a.Alias == SkoleNaervaerCanonicalFixtures.ValidReportAlias).ProtectionKind
            .Should().Be(TestDataProtectionKinds.CanonicalFixture, "a re-created canonical fixture is protected again by its declaration");
    }

    [Fact]
    public async Task Reset_except_protected_keeps_the_fixture_and_its_id_and_ordinary_delete_stays_refused()
    {
        var h = Build();
        h.Store.Add(Fixture(), new SkoleNaervaerCanonicalFixtures().Fixtures, DateTimeOffset.UtcNow);
        var before = h.Catalog.Assets().Single(a => a.Alias == SkoleNaervaerCanonicalFixtures.ValidReportAlias);

        await h.Lifecycle.CompleteFullResetAsync(await h.Lifecycle.PreviewResetAsync(default), default);

        var after = h.Catalog.Assets().Single(a => a.Alias == SkoleNaervaerCanonicalFixtures.ValidReportAlias);
        (after.Id, after.Value!.Reveal()).Should().Be((before.Id, before.Value!.Reveal()), "current content and stable id survive; nothing is restored");
        var (_, status) = await h.Lifecycle.DeleteAsync(after.Id, leaveReferencesUnresolved: true, default);
        status.Should().Be(409, "protection is overridden only by the confirmed Reset everything scope");
    }

    [Fact]
    public void Delivery_tables_are_deleted_children_first_and_only_from_the_delivery_schema()
    {
        using var db = new DeliveryReportingDbContext(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseNpgsql("Host=localhost;Database=none").Options);
        var ordered = DeliveryReportingDataReset.ChildFirst(db);
        ordered.Should().OnlyContain(t => t.Schema == "delivery_reporting");
        for (var i = 0; i < ordered.Count; i++)
            foreach (var fk in ordered[i].ForeignKeyConstraints)
                ordered.ToList().IndexOf(fk.PrincipalTable).Should().BeGreaterThan(i, $"{ordered[i].Name} references {fk.PrincipalTable.Name}, which must be deleted after it");
        ordered.Select(t => t.Name).Should().Contain(["project", "report_snapshot", "task_snapshot", "weekly_report"]);
    }

    private sealed class Guard(string? refusal) : ILocalDatabaseReset
    {
        public Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync() => throw new InvalidOperationException("must not run");
        public string? ResetRefusal() => refusal;
    }

    [Theory]
    [InlineData(DataResetMode.FullIncludingProtected, "RESET", null, "Type RESET EVERYTHING")]
    [InlineData(DataResetMode.FullPreserveProtected, null, null, "not confirmed")]
    [InlineData(DataResetMode.DeliveryManagement, "CONFIRM", "Reset is available only for a Local database on a non-production backend.", "Local database")]
    public async Task Scope_confirmation_and_the_local_database_guard_are_enforced_by_the_backend(DataResetMode mode, string? confirmation, string? refusal, string message)
    {
        var service = new DataResetService(new Guard(refusal), null!, null!, NullLogger<DataResetService>.Instance);
        var result = await service.ResetAsync(new DataResetRequest(mode, confirmation), default);
        result.Status.Should().Be("Refused");
        result.Success.Should().BeFalse();
        result.Message.Should().Contain(message);
    }
}
