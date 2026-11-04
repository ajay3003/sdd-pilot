using BirkNext.Api.Data;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.TestDataConsumers;
using BirkNext.CriticalE2E;
using BirkNext.PerformanceTests;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// Performance and Critical E2E as Test Data consumers with stable references: alias-only references migrate only when provable, rename keeps
/// every consumer resolving, a deleted-and-reused alias never rebinds a saved or historical consumer, ambiguous and missing legacy aliases are
/// surfaced, consumer counts come from the persisted references, and recording ids never creates a new definition/flow version.
/// </summary>
public sealed class TestDataConsumerReferenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-td-refs-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("td-refs-" + Guid.NewGuid().ToString("N")).Options);
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Values : ITestDataProvider
    {
        public List<TestDataAsset> Items { get; } = [];
        public string Id => "configured";
        public string Label => "Configured";
        public string Description => "In-test values.";
        public IReadOnlyList<TestDataAsset> Assets() => Items;
    }

    private sealed record Bed(TestDataCatalog Catalog, TestDataRegistryStore Registry, PerformanceTestStore Performance, CriticalE2EStore E2E,
        TestDataLifecycleService Lifecycle, TestDataConsumerReferenceMigration Migration, Values Configured);

    private Bed Create()
    {
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "td"), NullLogger<TestDataRegistryStore>.Instance);
        var values = new Values();
        var catalog = new TestDataCatalog([values, new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "td")));
        var performance = new PerformanceTestStore(_db);
        var e2e = new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance);
        var lifecycle = new TestDataLifecycleService(catalog, registry, [], [new PerformanceTestDataReferences(performance, TimeProvider.System), new CriticalE2ETestDataReferences(e2e)], TimeProvider.System);
        return new(catalog, registry, performance, e2e, lifecycle, new TestDataConsumerReferenceMigration(catalog, performance, e2e, TimeProvider.System), values);
    }

    private static RegisteredTestDataAsset Child(string alias) =>
        new() { Alias = alias, Label = "Child", DataType = TestDataDataType.SimpleValues, RegisteredAt = DateTimeOffset.Parse("2026-11-01T08:00:00Z") };

    private static async Task Reference(Bed bed, string alias)
    {
        await bed.Performance.SaveDefinitionAsync("dev", new PerformanceTestDefinition
        {
            Id = "perf-1", Name = "Absence load",
            Scenario = new PerformanceScenario
            {
                Steps = [new HttpPerformanceStep { Id = "s1", Name = "Get child", RelativePath = "/api/child/{childId}",
                    DataMappings = [new ScenarioDataMapping("childId", [alias], TestDataDataType.SimpleValues, "path")] }],
                Variables = [new ScenarioVariable("reporter", RuntimeValueSource.TestData) { AssetAlias = alias }],
            },
        }, DateTimeOffset.UtcNow);
        bed.E2E.Save(new CriticalE2EFlowDefinition
        {
            Id = "flow-1", Name = "Report absence", EnvironmentId = "dev", TestDataAliases = [alias],
            DataMappings = [new CriticalE2EDataMapping("child", [alias], TestDataDataType.SimpleValues, "Step 1")],
        });
    }

    private async Task<(PerformanceTestDefinition Definition, CriticalE2EFlowDefinition Flow)> Stored(Bed bed) =>
        ((await bed.Performance.DefinitionAsync("dev", "perf-1"))!, bed.E2E.Flows("").Single(f => f.Id == "flow-1"));

    [Fact]
    public async Task Unique_legacy_aliases_migrate_to_stable_ids_without_a_new_version_and_idempotently()
    {
        var bed = Create();
        bed.Registry.Register(Child("child-a"));
        await Reference(bed, "child-a");
        var (before, flowBefore) = await Stored(bed);
        var id = bed.Catalog.Assets().Single().AssetId;

        var first = await bed.Migration.MigrateAsync(default);
        var second = await bed.Migration.MigrateAsync(default);

        first.Migrated.Should().Be(4, "step mapping, scenario variable, E2E placeholder and E2E flow alias");
        second.Migrated.Should().Be(0, "idempotent");
        var (after, flow) = await Stored(bed);
        after.Scenario.Steps[0].DataMappings![0].AssetIds.Should().Equal(id);
        after.Scenario.Variables![0].AssetId.Should().Be(id);
        after.Version.Should().Be(before.Version, "recording ids is not a definition change");
        after.ComparisonFingerprint.Should().Be(before.ComparisonFingerprint, "runs stay comparable to their baselines");
        flow.DataMappings![0].AssetIds.Should().Equal(id);
        flow.TestDataAssetIds.Should().Equal(id);
        flow.Version.Should().Be(flowBefore.Version);
    }

    [Fact]
    public async Task Rename_keeps_the_id_every_consumer_resolving_and_the_counts_unchanged()
    {
        var bed = Create();
        bed.Registry.Register(Child("child-a"));
        await Reference(bed, "child-a");
        await bed.Migration.MigrateAsync(default);
        var id = bed.Catalog.Assets().Single().AssetId!;
        var countBefore = (await bed.Lifecycle.UsagesAsync(default)).Count(u => !u.Unresolved);

        bed.Lifecycle.RenameAlias(id, "child-b").Error.Should().BeNull();

        bed.Catalog.Assets().Single().AssetId.Should().Be(id);
        var usages = await bed.Lifecycle.UsagesAsync(default);
        usages.Count(u => !u.Unresolved).Should().Be(countBefore, "consumer counts are unchanged by a rename");
        usages.Should().OnlyContain(u => u.Alias == "child-b" && !u.Unresolved);
        usages.Select(u => u.ConsumerType).Distinct().Should().BeEquivalentTo([PerformanceTestDataReferences.Consumer, CriticalE2ETestDataReferences.Consumer]);
        var (definition, flow) = await Stored(bed);
        TestDataReferenceResolver.Resolve(bed.Catalog, "child-a", definition.Scenario.Steps[0].DataMappings![0].AssetIds![0]).CurrentAlias.Should().Be("child-b");
        CriticalE2ETestData.Issues(flow, bed.Catalog, DateTimeOffset.UtcNow).Should().NotContain(i => i.Contains("no longer exists"));
    }

    [Fact]
    public async Task A_deleted_and_reused_alias_never_rebinds_saved_consumers()
    {
        var bed = Create();
        bed.Registry.Register(Child("child-b"));
        await Reference(bed, "child-b");
        await bed.Migration.MigrateAsync(default);
        var x = bed.Catalog.Assets().Single().AssetId!;
        (await bed.Lifecycle.DeleteAsync(x, leaveReferencesUnresolved: true, default)).Status.Should().Be(200);

        bed.Registry.Register(Child("child-b"));
        var y = bed.Catalog.Assets().Single().AssetId!;
        y.Should().NotBe(x);

        var (definition, flow) = await Stored(bed);
        TestDataReferenceResolver.Resolve(bed.Catalog, "child-b", definition.Scenario.Steps[0].DataMappings![0].AssetIds![0]).State.Should().Be(TestDataReferenceState.Removed);
        PerformanceCentralTestData.Issues(definition, bed.Catalog, DateTimeOffset.UtcNow).Should().Contain(i => i.Contains("no longer exists"), "the saved definition is not attached to Y");
        CriticalE2ETestData.Issues(flow, bed.Catalog, DateTimeOffset.UtcNow).Should().Contain(i => i.Contains("no longer exists"));
        (await bed.Lifecycle.UsagesAsync(default)).Should().OnlyContain(u => u.Unresolved, "the new asset Y is unused");
        (await bed.Migration.MigrateAsync(default)).Migrated.Should().Be(0, "migration never re-points a stored id");
    }

    [Fact]
    public async Task A_legacy_alias_whose_alias_was_reused_is_surfaced_for_review_never_guessed()
    {
        var bed = Create();
        bed.Registry.Register(Child("child-c"));
        await Reference(bed, "child-c"); // alias-only, never migrated (e.g. saved before stable ids)
        var old = bed.Catalog.Assets().Single().AssetId!;
        await bed.Lifecycle.DeleteAsync(old, leaveReferencesUnresolved: true, default);
        bed.Registry.Register(Child("child-c"));

        var result = await bed.Migration.MigrateAsync(default);

        result.NeedsReview.Should().Be(4);
        result.Migrated.Should().Be(0);
        var (definition, flow) = await Stored(bed);
        definition.Scenario.Steps[0].DataMappings![0].AssetIds.Should().BeNull();
        PerformanceCentralTestData.Issues(definition, bed.Catalog, DateTimeOffset.UtcNow).Should().Contain(i => i.Contains("legacy reference that needs review"));
        CriticalE2ETestData.Resolve(flow, bed.Catalog).Should().BeEmpty("no value of the new asset is used by a legacy reference");
        (await bed.Lifecycle.UsagesAsync(default)).Should().OnlyContain(u => u.NeedsReview && u.Unresolved);
    }

    [Fact]
    public async Task A_missing_legacy_alias_is_preserved_as_written_and_unresolved()
    {
        var bed = Create();
        bed.Registry.Register(Child("someone-else"));
        await Reference(bed, "ghost");

        var result = await bed.Migration.MigrateAsync(default);

        result.Unresolved.Should().Be(4);
        var (definition, _) = await Stored(bed);
        definition.Scenario.Steps[0].DataMappings![0].AssetAliases.Should().Equal("ghost");
        definition.Scenario.Steps[0].DataMappings![0].AssetIds.Should().BeNull();
        (await bed.Lifecycle.UsagesAsync(default)).Should().OnlyContain(u => u.Unresolved && !u.NeedsReview);
    }

    [Fact]
    public void Runs_record_the_ids_they_used_and_old_runs_stay_identity_not_recorded()
    {
        var bed = Create();
        bed.Configured.Items.Add(new TestDataAsset { Id = "configured:child-v", Alias = "child-v", Kind = TestDataKind.Scalar, Value = new("12345678901"), Approval = TestDataApproval.Approved });
        var flow = new CriticalE2EFlowDefinition { Id = "flow-2", Name = "Report", EnvironmentId = "dev", TestDataAliases = ["child-v"] };
        var id = bed.Catalog.Assets().Single().AssetId;
        CriticalE2ETestData.AssetIds(flow, bed.Catalog).Should().Equal(id);
        var legacyRun = new CriticalE2ERunResult { RunId = "old", TestDataAliases = ["child-v"] };
        legacyRun.TestDataAssetIds.Should().BeNull();
        TestDataReferenceText.HistoricalIdentityNotRecorded.Should().Be("Historical Test Data identity not recorded");
    }
}
