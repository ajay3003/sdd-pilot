using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.Services.Schemas;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// Immutable TestDataAssetId: assigned once (existing assets migrate idempotently), independent of alias and provider label, stable across rename
/// and regeneration; a reused alias never inherits a removed asset's history; shared legacy ids are never merged. Validation trust: only the
/// server's own check against an XSD it holds is server-confirmed; a client-reported "Valid" never is.
/// </summary>
public sealed class TestDataStableIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-td-identity-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Provider(string id = "configured") : ITestDataProvider
    {
        public List<TestDataAsset> Items { get; } = [];
        public string Id => id;
        public string Label { get; set; } = "Configured";
        public string Description => "In-test assets.";
        public IReadOnlyList<TestDataAsset> Assets() => Items;
    }

    private sealed class Schemas(TestDataSchemaPackage? package) : ITestDataSchemaSource
    {
        public Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct) => Task.FromResult(package);
    }

    private (TestDataCatalog Catalog, TestDataRegistryStore Registry, TestDataIdentityStore Identities, Provider Configured, TestDataLifecycleService Lifecycle) Create()
    {
        var registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var identities = new TestDataIdentityStore(_dir);
        var configured = new Provider();
        var catalog = new TestDataCatalog([configured, new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, identities);
        return (catalog, registry, identities, configured, new TestDataLifecycleService(catalog, registry, [], [], TimeProvider.System));
    }

    private static RegisteredTestDataAsset Registered(string alias) =>
        new() { Alias = alias, Label = alias, DataType = TestDataDataType.SimpleValues, RegisteredAt = DateTimeOffset.Parse("2026-11-01T08:00:00Z") };

    private static TestDataAsset Xml(string alias, string xml, string? recorded, TestDataSchemaReference? schema = null) => new()
    {
        Id = $"configured:{alias}", Alias = alias, Kind = TestDataKind.Structured, MediaType = "application/xml", Value = new TestDataValue(xml),
        Schema = schema ?? new TestDataSchemaReference(RealSchemaFixtures.Skolenaervaer200), RecordedValidation = recorded, Approval = TestDataApproval.Unapproved,
    };

    private const string Ns = "https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.0.altinn";
    private static readonly string ValidXml = $"""
        <Skolenaervaer xmlns="{Ns}">
          <Meldingshode><Id>00000000-0000-0000-0000-000000000000</Id><MeldingstypeNmsp>urn:test</MeldingstypeNmsp>
            <FagsystemAvsender><Leverandor>Test</Leverandor><Navn>Test</Navn><Versjon>1</Versjon></FagsystemAvsender>
            <SendtTidspunkt>2024-01-01T00:00:00Z</SendtTidspunkt><Avsender/><Mottaker/></Meldingshode>
          <Klient/><Periode><StartDato>2024-01-01T00:00:00Z</StartDato><SluttDato>2024-02-01T00:00:00Z</SluttDato></Periode>
          <SkolenaervaerRegistrering><AntallTimerEtterPlanen>10</AntallTimerEtterPlanen><AntallTimerFaktiskNaervaer>8</AntallTimerFaktiskNaervaer>
            <FravaerRegistrering><Fravaer><TypeFravaer>1</TypeFravaer><TimerFravaer>2</TimerFravaer></Fravaer></FravaerRegistrering></SkolenaervaerRegistrering>
        </Skolenaervaer>
        """;

    // ---- identity ----

    [Fact]
    public void Existing_assets_migrate_to_stable_ids_idempotently_and_ids_do_not_depend_on_the_provider_label()
    {
        var (catalog, registry, identities, configured, _) = Create();
        registry.Register(Registered("child-default"));
        configured.Items.Add(new TestDataAsset { Id = "configured:reporter", Alias = "reporter", Kind = TestDataKind.Scalar, Value = new("x") });

        var first = catalog.Assets().ToDictionary(a => a.Alias, a => a.AssetId);
        first.Values.Should().OnlyContain(id => id != null && id.StartsWith(TestDataIdentityStore.IdPrefix));
        identities.Identities().Should().OnlyContain(i => i.Origin == "Migrated", "assets that existed before stable ids are recorded as migrated");

        configured.Label = "Renamed provider label";
        var again = new TestDataCatalog([configured, new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(_dir));
        again.Assets().ToDictionary(a => a.Alias, a => a.AssetId).Should().BeEquivalentTo(first, "re-reading (and a new process) finds every id again");
        new TestDataIdentityStore(_dir).Identities().Should().HaveCount(2, "migration is idempotent");
        catalog.View().Assets.Should().OnlyContain(s => s.AssetId != null);
    }

    [Fact]
    public void Scenario_A_rename_keeps_the_stable_id_and_every_reference_resolves_to_the_same_asset()
    {
        var (catalog, registry, identities, _, lifecycle) = Create();
        registry.Register(Registered("child-default"));
        var before = catalog.Assets().Single(a => a.Alias == "child-default");
        registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = before.Id, ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(before), ApprovedAt = DateTimeOffset.UtcNow });
        registry.Bind(new TestDataUsageReference { Id = "u1", AssetId = before.Id, Consumer = new() { ConsumerType = "Performance", ConsumerId = "p1", DisplayName = "p1" }, Usage = "scenario" });

        var (renamed, error) = lifecycle.RenameAlias(before.AssetId!, "child-primary");

        error.Should().BeNull();
        renamed!.AssetId.Should().Be(before.AssetId, "the TestDataAssetId never changes with the alias");
        renamed.PreviousAliases.Should().Equal("child-default");
        renamed.Status.Should().Be(TestDataAssetStatus.Approved, "approval of unchanged content survives a rename");
        registry.Usages().Single().AssetId.Should().Be("registered:child-primary", "id-based references follow the rename");
        catalog.ResolveReference(before.Id)!.AssetId.Should().Be(before.AssetId, "the old legacy id still resolves");
        catalog.ResolveReference("child-default")!.AssetId.Should().Be(before.AssetId, "the old alias resolves while nobody else uses it");
        catalog.ByAlias()["child-default"].AssetId.Should().Be(before.AssetId, "alias-based consumers (Performance, Critical E2E) keep working");
        catalog.ResolveReference(before.AssetId!)!.Alias.Should().Be("child-primary");
        identities.Resolve(before.AssetId!)!.Aliases.Select(a => (a.Alias, a.EndedBy)).Should().Equal([("child-default", "Renamed"), ("child-primary", (string?)null)]);
    }

    [Fact]
    public async Task A_reused_alias_gets_a_new_identity_and_never_inherits_the_removed_assets_history()
    {
        var (catalog, registry, identities, _, lifecycle) = Create();
        registry.Register(Registered("reporter-a"));
        var old = catalog.Assets().Single();
        (await lifecycle.DeleteAsync(old.AssetId!, leaveReferencesUnresolved: true, default)).Status.Should().Be(200);

        registry.Register(Registered("reporter-a"));
        var reused = catalog.Assets().Single();

        reused.AssetId.Should().NotBe(old.AssetId, "a new logical asset gets a new id even with the same alias");
        identities.Resolve(old.AssetId!)!.Status.Should().Be(TestDataIdentityStatus.Removed);
        catalog.ResolveReference(old.AssetId!).Should().BeNull("a reference to the removed asset stays unresolved, never silently re-pointed");
        catalog.View().Assets.Single().PreviousAliases.Should().BeNull("the new asset has no inherited alias history");
    }

    [Fact]
    public void Assets_sharing_one_legacy_id_are_kept_separate_and_flagged_for_review()
    {
        var (catalog, _, identities, configured, _) = Create();
        configured.Items.Add(new TestDataAsset { Id = "configured:dup", Alias = "dup", Kind = TestDataKind.Scalar, Label = "first", Value = new("1") });
        configured.Items.Add(new TestDataAsset { Id = "configured:dup", Alias = "dup", Kind = TestDataKind.Scalar, Label = "second", Value = new("2") });

        var assets = catalog.Assets();
        assets.Select(a => a.AssetId).Distinct().Should().HaveCount(2, "never merged");
        identities.Identities().Should().HaveCount(2).And.OnlyContain(i => i.Review != null);
        catalog.Assets().Select(a => a.AssetId).Should().BeEquivalentTo(assets.Select(a => a.AssetId), "the disambiguated mapping is stable");
        catalog.View().Assets.Should().OnlyContain(s => s.IdentityReview != null);
    }

    [Fact]
    public void Scenario_B_regeneration_adds_a_version_under_the_same_identity_and_keeps_history()
    {
        var (catalog, registry, identities, configured, _) = Create();
        var v1 = Xml("valid-report", ValidXml, "Valid") with { Version = 1, VersionCreatedAt = DateTimeOffset.Parse("2026-11-01T08:00:00Z") };
        configured.Items.Add(v1 with { Versions = [v1] });
        var id = catalog.Assets().Single().AssetId;
        registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = v1.Id, ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(v1), ApprovedAt = DateTimeOffset.UtcNow });

        var v2 = v1 with { Version = 2, Value = new(ValidXml.Replace("<TimerFravaer>2</TimerFravaer>", "<TimerFravaer>3</TimerFravaer>")), VersionCreatedAt = DateTimeOffset.Parse("2026-11-04T08:00:00Z") };
        configured.Items[0] = v2 with { Versions = [v1, v2] };

        var current = catalog.Assets().Single();
        current.AssetId.Should().Be(id, "regeneration of the same logical asset keeps its identity");
        current.Version.Should().Be(1, "the approved (frozen) version stays current until the regenerated one is approved");
        current.Draft!.Version.Should().Be(2);
        identities.Identities().Should().ContainSingle();
    }

    // ---- schema evidence and trust ----

    private static TestDataSchemaAssociationStore Associated(string dir, out TestDataSchemaAssociation association)
    {
        var store = new TestDataSchemaAssociationStore(dir);
        var (created, error) = store.Associate(new(RealSchemaFixtures.Skolenaervaer200,
            [new(RealSchemaFixtures.Skolenaervaer200, RealSchemaFixtures.Read(RealSchemaFixtures.Skolenaervaer200))]), "tester", DateTimeOffset.UtcNow);
        error.Should().BeNull();
        association = created!;
        return store;
    }

    [Fact]
    public async Task Scenario_C_explicit_skolenaervaer_xsd_validates_on_the_server_and_records_schema_identity()
    {
        var (catalog, _, identities, configured, _) = Create();
        configured.Items.Add(Xml("skole-valid", ValidXml, "Valid"));
        configured.Items.Add(Xml("skole-invalid", ValidXml.Replace("<TypeFravaer>1</TypeFravaer>", "<TypeFravaer>6</TypeFravaer>"), "Valid"));
        var associations = Associated(_dir, out var association);
        association.TargetNamespace.Should().Be(Ns);
        association.DeclaredVersion.Should().NotBeNull();
        var package = CompositeTestDataSchemaSource.FromAssociations(associations.All(), new(RealSchemaFixtures.Skolenaervaer200));
        var validation = new TestDataSchemaValidationService(catalog, new Schemas(package));

        var valid = await validation.ValidateAndRecordAsync(catalog.Assets().Single(a => a.Alias == "skole-valid"), TimeProvider.System, default);
        var invalid = await validation.ValidateAndRecordAsync(catalog.Assets().Single(a => a.Alias == "skole-invalid"), TimeProvider.System, default);

        valid!.Trust.Should().Be(TestDataValidationTrust.ServerExplicitSchemaReference, "explicitly associated, never 'verified schema'");
        valid.Result.Should().Be(TestDataSchemaStatus.Validated);
        valid.AssociationId.Should().Be(association.Id);
        valid.SchemaFingerprint.Should().Be(association.PrimaryFingerprint);
        valid.TargetNamespace.Should().Be(Ns);
        valid.Engine.Should().Be(TestDataValidationTrusts.Engine);
        invalid!.Trust.Should().Be(TestDataValidationTrust.ValidationFailed, "6 is not an allowed absence code: the client's 'Valid' claim never wins");
        identities.Identities().Single(i => i.CurrentAlias == "skole-valid").Validations.Should().ContainSingle().Which.Result.Should().Be(TestDataSchemaStatus.Validated);

        var view = await validation.ViewAsync(default);
        view.Assets.Single(a => a.Alias == "skole-valid").ValidationTrust.Should().Be(TestDataValidationTrust.ServerExplicitSchemaReference);
        view.Assets.Single(a => a.Alias == "skole-invalid").ValidationTrust.Should().Be(TestDataValidationTrust.ValidationFailed);
    }

    [Fact]
    public async Task Missing_xsd_is_unavailable_and_a_client_only_claim_is_never_promoted()
    {
        var (catalog, _, _, configured, _) = Create();
        configured.Items.Add(Xml("claimed", ValidXml, "Valid"));
        configured.Items.Add(Xml("unclaimed", ValidXml, null));
        var validation = new TestDataSchemaValidationService(catalog, new Schemas(null));

        (await validation.ValidateAndRecordAsync(catalog.Assets().Single(a => a.Alias == "claimed"), TimeProvider.System, default))!.Trust
            .Should().Be(TestDataValidationTrust.ClientReportedOnly);
        (await validation.ValidateAndRecordAsync(catalog.Assets().Single(a => a.Alias == "unclaimed"), TimeProvider.System, default))!.Trust
            .Should().Be(TestDataValidationTrust.SchemaUnavailable);
        var view = await validation.ViewAsync(default);
        view.Assets.Single(a => a.Alias == "claimed").ValidationTrust.Should().Be(TestDataValidationTrust.ClientReportedOnly);
        view.Assets.Single(a => a.Alias == "claimed").Validation.Should().NotBe(TestDataValidation.Validated, "a client claim is never shown as server-validated");
        TestDataValidationTrusts.ServerConfirmed(TestDataValidationTrust.ClientReportedOnly).Should().BeFalse();
    }

    [Fact]
    public async Task A_snapshot_retained_xsd_is_a_verified_schema_and_a_pinned_reference_is_never_substituted()
    {
        var (catalog, _, _, configured, _) = Create();
        configured.Items.Add(Xml("skole-valid", ValidXml, null));
        var snapshot = Guid.NewGuid();
        var fromSnapshot = new TestDataSchemaPackage(snapshot, new Dictionary<string, string> { [RealSchemaFixtures.Skolenaervaer200] = RealSchemaFixtures.Read(RealSchemaFixtures.Skolenaervaer200) }, true);
        var evidence = await new TestDataSchemaValidationService(catalog, new Schemas(fromSnapshot)).ValidateAndRecordAsync(catalog.Assets().Single(), TimeProvider.System, default);
        evidence!.Trust.Should().Be(TestDataValidationTrust.ServerVerifiedSchema);
        evidence.SourceSnapshotId.Should().Be(snapshot);

        var associations = Associated(_dir, out _);
        CompositeTestDataSchemaSource.FromAssociations(associations.All(), new(RealSchemaFixtures.Skolenaervaer200, SchemaFingerprint: "0000")).Should()
            .BeNull("an association whose content differs from the declared fingerprint is never used");
    }

    [Fact]
    public void Associations_are_validated_and_identical_content_is_not_duplicated()
    {
        var store = Associated(_dir, out var first);
        store.Associate(new(RealSchemaFixtures.Skolenaervaer200, [new(RealSchemaFixtures.Skolenaervaer200, RealSchemaFixtures.Read(RealSchemaFixtures.Skolenaervaer200))]), "tester", DateTimeOffset.UtcNow)
            .Association!.Id.Should().Be(first.Id);
        store.Associate(new("broken.xsd", [new("broken.xsd", "<not-a-schema")]), "tester", DateTimeOffset.UtcNow).Error.Should().NotBeNull();
        new TestDataSchemaAssociationStore(_dir).All().Should().ContainSingle("persisted");
        TestDataSchemaAssociationStore.View(first).Files.Should().Equal(RealSchemaFixtures.Skolenaervaer200);
    }
}
