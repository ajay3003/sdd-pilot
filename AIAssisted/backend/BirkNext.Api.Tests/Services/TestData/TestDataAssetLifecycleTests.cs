using BirkNext.Api.Data;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.TestData;
using BirkNext.PerformanceTests;
using BirkNext.Integrations;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// Test Data asset lifecycle: protected defaults (editable value, stable id, never deleted or reset), Delete with usage awareness, Replace
/// references (compatible approved assets only) and Reset Test Data. Uses temporary directories and an in-memory database only; identifiers are
/// generated synthetic values, never real persons.
/// </summary>
public sealed class TestDataAssetLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-td-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed record Bed(TestDataLifecycleService Lifecycle, TestDataCatalog Catalog, TestDataRegistryStore Registry, AltinnSessionStore Session,
        ProtectedTestDataDefaultsStore Defaults, PerformanceTestStore Performance, CriticalE2EStore E2E, AltinnTestingOptions Options);

    private Bed Create(AltinnTestingOptions? options = null)
    {
        Directory.CreateDirectory(_dir);
        var defaults = new ProtectedTestDataDefaultsStore(Path.Combine(_dir, "test-data"), NullLogger<ProtectedTestDataDefaultsStore>.Instance);
        options ??= defaults.Overlay(SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions()));
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "test-data"), NullLogger<TestDataRegistryStore>.Instance);
        var session = new AltinnSessionStore(options, TimeProvider.System);
        var catalog = new TestDataCatalog([new AltinnTestDataProvider(options), new RegisteredTestDataProvider(registry)], TimeProvider.System, registry);
        var performance = new PerformanceTestStore(_db);
        var e2e = new CriticalE2EStore(Path.Combine(_dir, "critical-e2e"), NullLogger<CriticalE2EStore>.Instance);
        var lifecycle = new TestDataLifecycleService(catalog, registry, [new AltinnTestDataEditor(session, defaults, options)],
            [new PerformanceTestDataReferences(performance, TimeProvider.System), new CriticalE2ETestDataReferences(e2e)], TimeProvider.System);
        return new(lifecycle, catalog, registry, session, defaults, performance, e2e, options);
    }

    private const string ChildId = "altinn-local:child:" + SkoleNaervaerTestIdentityDefaults.ChildAlias;
    private const string ReporterId = "altinn-local:reporter:" + SkoleNaervaerTestIdentityDefaults.ReporterAlias;

    private static string NewChild(AltinnTestingOptions options)
    {
        while (true)
        {
            var value = SyntheticPersonIdentifier.Generate();
            if (options.UnattendedIdPortenTest.Reporters.All(r => r.TestIdPersonIdentifier != value)) return value;
        }
    }

    private static RegisteredTestDataAsset Observed(string alias) => new()
    {
        Alias = alias, Label = alias, DataType = TestDataDataType.SimpleValues, EnvironmentId = "development", Fields = ["id"],
        Tags = ["live-discovery", "observed-parameter"], RegisteredAt = DateTimeOffset.UtcNow,
    };

    private async Task MapIntoScenario(Bed bed, string alias, string variable = "brukerId")
    {
        await bed.Performance.SaveDefinitionAsync("dev", new PerformanceTestDefinition
        {
            Id = "def-1", Name = "Baseline check",
            Scenario = new PerformanceScenario
            {
                Steps =
                [
                    new HttpPerformanceStep { Id = "s1", Name = "List", RelativePath = "/api/users" },
                    new HttpPerformanceStep { Id = "s2", Name = "Get user", RelativePath = "/api/users/{" + variable + "}",
                        DataMappings = [new ScenarioDataMapping(variable, [alias], TestDataDataType.SimpleValues, "path")] },
                ],
            },
        }, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Protected_defaults_are_declared_by_ownership_not_by_alias()
    {
        var bed = Create();
        var assets = bed.Catalog.Assets();
        assets.Single(a => a.Id == ChildId).Ownership.Should().Be(TestDataOwnership.ProtectedDefault);
        assets.Single(a => a.Id == ReporterId).Ownership.Should().Be(TestDataOwnership.ProtectedDefault);
        bed.Session.AddIdentity(new AltinnSessionIdentityRequest("child", "session-child", "Session child", NewChild(bed.Options))).Should().BeNull();
        bed.Catalog.Assets().Single(a => a.Alias == "session-child").Ownership.Should().Be(TestDataOwnership.SessionEntered);
    }

    [Fact]
    public async Task Editing_the_child_default_changes_its_value_keeps_its_id_persists_and_survives_a_restart()
    {
        var bed = Create();
        var value = NewChild(bed.Options);

        var (result, _) = await bed.Lifecycle.EditAsync(ChildId, new TestDataEditRequest(null, value), default);

        result.Updated.Should().BeTrue(result.Message);
        result.ValueChanged.Should().BeTrue();
        result.Asset!.Id.Should().Be(ChildId, "the canonical asset id never changes");
        result.Asset.Ownership.Should().Be(TestDataOwnership.ProtectedDefault, "the asset stays protected");
        result.Asset.ValueModified.Should().BeTrue();
        result.Asset.ValueVersion.Should().Be(1);
        bed.Catalog.Reveal(SkoleNaervaerTestIdentityDefaults.ChildAlias).Should().Be(value);
        bed.Catalog.Assets().Single(a => a.Id == ChildId).Capabilities.Should().Contain("child", "it stays available in the child selector");

        var restarted = Create(new ProtectedTestDataDefaultsStore(Path.Combine(_dir, "test-data"), NullLogger<ProtectedTestDataDefaultsStore>.Instance)
            .Overlay(SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions())));
        restarted.Catalog.Reveal(SkoleNaervaerTestIdentityDefaults.ChildAlias).Should().Be(value, "an edited default is persisted");
        restarted.Catalog.Summary(restarted.Catalog.Assets().Single(a => a.Id == ChildId)).ValueVersion.Should().Be(1);
    }

    [Fact]
    public async Task Editing_the_reporter_default_validates_with_the_entry_rules_and_keeps_its_id()
    {
        var bed = Create();
        (await bed.Lifecycle.EditAsync(ReporterId, new TestDataEditRequest(null, "12345678901"), default)).Result.Updated.Should().BeFalse("the TestID rule applies");
        var child = NewChild(bed.Options);
        (await bed.Lifecycle.EditAsync(ChildId, new TestDataEditRequest(null, child), default)).Result.Updated.Should().BeTrue();
        (await bed.Lifecycle.EditAsync(ReporterId, new TestDataEditRequest(null, child), default)).Result.Message.Should().Contain("different persons");

        var value = SyntheticPersonIdentifier.Generate();
        var (result, _) = await bed.Lifecycle.EditAsync(ReporterId, new TestDataEditRequest("Renamed reporter", value), default);

        result.Updated.Should().BeTrue(result.Message);
        result.Asset!.Id.Should().Be(ReporterId);
        result.Asset.Label.Should().Be("Renamed reporter");
        result.Asset.Kind.Should().Be(TestDataKind.EntityReference, "the type never changes");
        result.Asset.Capabilities.Should().Contain("reporter", "the role never changes");
        result.Asset.Status.Should().Be(TestDataAssetStatus.Approved, "an edit does not invent a re-approval requirement");
    }

    [Fact]
    public async Task Edit_view_carries_the_value_only_for_an_editable_value_and_shows_role_and_usages()
    {
        var bed = Create();
        bed.Registry.Register(Observed("observed-id-development"));

        var child = (await bed.Lifecycle.EditViewAsync(ChildId, default))!;
        child.Role.Should().Be("Child");
        child.ValueEditable.Should().BeTrue();
        child.Value.Should().NotBeNullOrEmpty();
        child.CanRestoreDefault.Should().BeTrue();

        var registered = (await bed.Lifecycle.EditViewAsync("registered:observed-id-development", default))!;
        registered.Value.Should().BeNull();
        registered.ValueEditable.Should().BeFalse();
        registered.LabelEditable.Should().BeTrue();
        registered.ValueNote.Should().Contain("metadata and structure only");
    }

    [Fact]
    public async Task Restore_default_value_is_explicit_and_returns_the_seeded_value()
    {
        var bed = Create();
        var seeded = bed.Catalog.Reveal(SkoleNaervaerTestIdentityDefaults.ChildAlias);
        await bed.Lifecycle.EditAsync(ChildId, new TestDataEditRequest(null, NewChild(bed.Options)), default);

        var (result, _) = await bed.Lifecycle.RestoreDefaultAsync(ChildId, default);

        result.Updated.Should().BeTrue();
        bed.Catalog.Reveal(SkoleNaervaerTestIdentityDefaults.ChildAlias).Should().Be(seeded);
        result.Asset!.ValueModified.Should().BeFalse();
        result.Asset.ValueVersion.Should().Be(2, "a restore is a value change too");
    }

    [Fact]
    public async Task Protected_defaults_and_configuration_are_refused_by_delete_server_side()
    {
        var options = SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions());
        options.SyntheticChildren.Add(new SyntheticChildOptions { Alias = "configured-child", Label = "Configured", TestPersonIdentifier = NewChild(options) });
        var bed = Create(options);

        var (child, childStatus) = await bed.Lifecycle.DeleteAsync(ChildId, leaveReferencesUnresolved: true, default);
        childStatus.Should().Be(409);
        child.Message.Should().StartWith("Protected default");
        (await bed.Lifecycle.DeleteAsync(ReporterId, true, default)).Status.Should().Be(409);
        (await bed.Lifecycle.DeleteAsync("altinn-local:child:configured-child", true, default)).Result.Message.Should().Contain("backend's configuration");
        bed.Catalog.Assets().Select(a => a.Alias).Should().Contain([SkoleNaervaerTestIdentityDefaults.ChildAlias, SkoleNaervaerTestIdentityDefaults.ReporterAlias, "configured-child"]);
    }

    [Fact]
    public async Task An_unused_asset_is_deleted_and_nothing_else_changes()
    {
        var bed = Create();
        bed.Registry.Register(Observed("observed-unused"));
        bed.Registry.Register(Observed("observed-kept"));

        var (result, status) = await bed.Lifecycle.DeleteAsync("registered:observed-unused", false, default);

        status.Should().Be(200);
        result.Deleted.Should().BeTrue();
        bed.Catalog.Assets().Select(a => a.Alias).Should().NotContain("observed-unused").And.Contain("observed-kept");
    }

    [Fact]
    public async Task A_used_asset_is_refused_until_the_tester_chooses_to_leave_references_unresolved_and_the_scenario_stays()
    {
        var bed = Create();
        bed.Registry.Register(Observed("observed-id-development"));
        await MapIntoScenario(bed, "observed-id-development");

        var (refused, status) = await bed.Lifecycle.DeleteAsync("registered:observed-id-development", false, default);
        status.Should().Be(409);
        refused.UnresolvedReferences.Should().ContainSingle(u => u.ConsumerType == PerformanceTestDataReferences.Consumer
            && u.ConsumerName == "Baseline check" && u.Location == "Step 2 (Get user) · brukerId");
        bed.Catalog.Assets().Should().Contain(a => a.Alias == "observed-id-development", "nothing is deleted silently");

        var (deleted, okStatus) = await bed.Lifecycle.DeleteAsync("registered:observed-id-development", true, default);
        okStatus.Should().Be(200);
        deleted.UnresolvedReferences.Should().ContainSingle().Which.Unresolved.Should().BeTrue();
        var definition = await bed.Performance.DefinitionAsync("dev", "def-1");
        definition!.Scenario.Steps[1].DataMappings!.Single().AssetAliases.Should().Equal(["observed-id-development"], "the consumer is kept and never re-pointed");
        (await bed.Lifecycle.UsagesAsync(default)).Should().ContainSingle(u => u.Alias == "observed-id-development" && u.Unresolved);
    }

    [Fact]
    public async Task Replace_references_accepts_only_a_compatible_approved_asset_and_updates_the_scenario()
    {
        var bed = Create();
        bed.Registry.Register(Observed("observed-id-development"));
        await MapIntoScenario(bed, "observed-id-development");

        (await bed.Lifecycle.ReplaceAsync("registered:observed-id-development", ChildId, default)).Status.Should().Be(409, "an entity reference is not a simple value");
        bed.Registry.Register(Observed("draft-user"));
        (await bed.Lifecycle.ReplaceAsync("registered:observed-id-development", "registered:draft-user", default)).Result.Message.Should().Contain("Not approved");

        bed.Session.AddIdentity(new AltinnSessionIdentityRequest("child", "session-child", "Session child", NewChild(bed.Options)));
        var (identity, identityStatus) = await bed.Lifecycle.ReplaceAsync(ChildId, "altinn-local:reporter:" + SkoleNaervaerTestIdentityDefaults.ReporterAlias, default);
        identityStatus.Should().Be(409, "a reporter never replaces a child");
        identity.Message.Should().Contain("Different role");
        (await bed.Lifecycle.ReplaceAsync(ChildId, "altinn-local:child:session-child", default)).Status.Should().Be(200, "a child replaces a child");
    }

    [Fact]
    public async Task Replace_re_points_scenario_mappings_by_alias_only()
    {
        var options = SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions());
        var bed = Create(options);
        bed.Session.AddIdentity(new AltinnSessionIdentityRequest("child", "session-child", "Session child", NewChild(options))).Should().BeNull();
        await MapIntoScenario(bed, "session-child", "childId");
        var before = (await bed.Performance.DefinitionAsync("dev", "def-1"))!;

        var (result, status) = await bed.Lifecycle.ReplaceAsync("altinn-local:child:session-child", ChildId, default);

        status.Should().Be(200, result.Message);
        result.ReferencesUpdated.Should().Be(1);
        var after = (await bed.Performance.DefinitionAsync("dev", "def-1"))!;
        after.Scenario.Steps[1].DataMappings!.Single().AssetAliases.Should().Equal([SkoleNaervaerTestIdentityDefaults.ChildAlias]);
        after.Version.Should().Be(before.Version + 1, "a reference change is a new definition version; earlier runs keep theirs");
    }

    [Fact]
    public async Task Reset_preview_and_reset_preserve_edited_protected_defaults_and_remove_every_local_asset()
    {
        var bed = Create();
        var child = NewChild(bed.Options);
        var reporter = SyntheticPersonIdentifier.Generate();
        await bed.Lifecycle.EditAsync(ChildId, new TestDataEditRequest(null, child), default);
        await bed.Lifecycle.EditAsync(ReporterId, new TestDataEditRequest(null, reporter), default);
        bed.Registry.Register(Observed("observed-id-development"));
        bed.Registry.Register(Observed("observed-bruker-id-development"));
        bed.Session.AddIdentity(new AltinnSessionIdentityRequest("child", "session-child", "Session child", NewChild(bed.Options)));
        await MapIntoScenario(bed, "observed-id-development");

        var preview = await bed.Lifecycle.PreviewResetAsync(default);
        preview.ProtectedDefaults.Should().Equal(SkoleNaervaerTestIdentityDefaults.ChildAlias, SkoleNaervaerTestIdentityDefaults.ReporterAlias);
        preview.AssetsToRemove.Should().BeEquivalentTo(["observed-bruker-id-development", "observed-id-development", "session-child"]);
        preview.AffectedReferences.Should().ContainSingle(u => u.ConsumerName == "Baseline check");

        var reset = await bed.Lifecycle.ResetAsync(generatedFixturesRemoved: 1, default);

        reset.Summary.ProtectedDefaultsPreserved.Should().Be(2);
        reset.Summary.AssetsRemoved.Should().Be(4, "two registered, one session identity and one generated fixture");
        reset.Summary.GeneratedFixturesRemoved.Should().Be(1);
        reset.Summary.ReferencesRequiringRemapping.Should().Be(1);
        reset.ReferencesRequiringRemapping.Should().ContainSingle(u => u.Alias == "observed-id-development" && u.Unresolved);
        bed.Catalog.Assets().Select(a => a.Alias).Should().BeEquivalentTo([SkoleNaervaerTestIdentityDefaults.ChildAlias, SkoleNaervaerTestIdentityDefaults.ReporterAlias]);
        bed.Catalog.Reveal(SkoleNaervaerTestIdentityDefaults.ChildAlias).Should().Be(child, "reset preserves the edited value");
        bed.Catalog.Reveal(SkoleNaervaerTestIdentityDefaults.ReporterAlias).Should().Be(reporter);
        (await bed.Performance.DefinitionAsync("dev", "def-1")).Should().NotBeNull("reset of Test Data never deletes consumers");
    }
}
