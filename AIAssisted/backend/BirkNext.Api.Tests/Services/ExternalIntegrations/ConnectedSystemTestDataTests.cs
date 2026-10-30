using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.ExternalIntegrations;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.ExternalIntegrations;

/// <summary>
/// Connected Systems ↔ Test Data: provider-declared requirements, deterministic suggestions from semantic role / data type / Applies to / scope /
/// approval (a single strong match is preselected but never persisted), explicit bindings storing aliases only, consumer tracking through the
/// usage registry, and delete/reset/replace that never silently re-bind. Local in-memory data only.
/// </summary>
public sealed class ConnectedSystemTestDataTests : IDisposable
{
    private const string Env = "tt02-env";
    private const string SystemId = Env + ":other:skolenaervaer-altinn-intake";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-cstd-" + Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"cstd-{Guid.NewGuid()}").Options);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Handler(ExternalProviderType type, Func<IReadOnlyList<ConnectedSystemTestDataRequirement>> requirements) : IExternalProviderHandler
    {
        public ExternalProviderType Type => type;
        public Task<Dictionary<string, string>> CurrentSettingsAsync(ExternalIntegration integration, CancellationToken ct) => Task.FromResult(new Dictionary<string, string>());
        public Task<ExternalIntegrationView> AssessAsync(ExternalIntegration integration, bool detect, CancellationToken ct) => Task.FromResult(new ExternalIntegrationView { Integration = integration });
        public Task<IReadOnlyList<SetupDependency>> SetupRequirementsAsync(ExternalIntegration integration, IReadOnlyDictionary<string, string> settings, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SetupDependency>>([]);
        public Task<IReadOnlyList<ConnectedSystemTestDataRequirement>> TestDataRequirementsAsync(ExternalIntegration integration, CancellationToken ct) => Task.FromResult(requirements());
    }

    private sealed class Sources : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private sealed class NoSchemas : ITestDataSchemaSource
    {
        public Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct) => Task.FromResult<TestDataSchemaPackage?>(null);
    }

    private sealed record Bed(ConnectedSystemTestDataService Service, ExternalIntegrationService Integrations, TestDataLifecycleService Lifecycle, TestDataCatalogService Catalog,
        TestDataRegistryStore Registry, AltinnSessionStore Session, AltinnTestingOptions Options);

    private async Task<Bed> CreateAsync(bool withRequirements = true)
    {
        Directory.CreateDirectory(_dir);
        var defaults = new ProtectedTestDataDefaultsStore(Path.Combine(_dir, "test-data"), NullLogger<ProtectedTestDataDefaultsStore>.Instance);
        var options = defaults.Overlay(SkoleNaervaerTestIdentityDefaults.Apply(new AltinnTestingOptions()));
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "test-data"), NullLogger<TestDataRegistryStore>.Instance);
        var session = new AltinnSessionStore(options, TimeProvider.System);
        var catalog = new TestDataCatalog([new AltinnTestDataProvider(options), new RegisteredTestDataProvider(registry)], TimeProvider.System, registry);
        var lifecycle = new TestDataLifecycleService(catalog, registry, [new AltinnTestDataEditor(session, defaults, options)], [new ConnectedSystemTestDataReferences(_db)], TimeProvider.System);
        var sources = new Sources();
        var catalogService = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry),
            sources, TimeProvider.System, lifecycle);
        var integrationCatalog = new Mock<IIntegrationCatalogService>();
        var handler = new Handler(ExternalProviderType.Other, () => withRequirements ? AltinnTestDataRequirements.For(new Dictionary<string, string>()) : []);
        var integrations = new ExternalIntegrationService(_db, [handler], integrationCatalog.Object, session, new AltinnEnvironmentSettingsCache(), TimeProvider.System);
        await integrations.PersistAsync(new ExternalIntegration
        {
            Id = SystemId, TargetEnvironmentId = Env, Name = "Skolenærvær – Altinn intake", ProviderType = ExternalProviderType.Other, EnvironmentName = "TT02",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return new(new ConnectedSystemTestDataService(integrations, catalogService, TimeProvider.System), integrations, lifecycle, catalogService, registry, session, options);
    }

    private static ConnectedSystemTestDataRow Row(ConnectedSystemTestDataView view, string id) => view.Rows.Single(r => r.Requirement.Id == id);

    private Task<ConnectedSystemTestDataView?> ViewAsync(Bed bed) => bed.Service.ViewAsync(Env, SystemId, CancellationToken.None);

    private Task<(ConnectedSystemTestDataView? View, string? Problem)> BindAsync(Bed bed, params (string Requirement, string? Alias)[] changes) =>
        bed.Service.UpdateAsync(Env, SystemId, new([.. changes.Select(c => new ConnectedSystemTestDataBindingChange(c.Requirement, c.Alias))]), CancellationToken.None);

    private void AddSessionReporter(Bed bed, string alias = "reporter-a") =>
        bed.Session.AddIdentity(new("reporter", alias, "Reporter A", AltinnTestKit.ReporterIdentifier, alias)).Should().BeNull();

    [Fact]
    public async Task Protected_defaults_declare_their_semantic_role_and_contexts()
    {
        var bed = await CreateAsync();
        var assets = (await bed.Catalog.ViewAsync(CancellationToken.None)).Assets;
        assets.Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ReporterAlias).Should().Match<TestDataAssetSummary>(a =>
            a.SemanticRole == TestDataSemanticRoles.Reporter && a.AppliesTo!.SequenceEqual(new[] { TestDataContexts.AltinnIntake, TestDataContexts.Skolenaervaer }) && a.MetadataFixed && a.Environment == "TT02");
        assets.Single(a => a.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias).Should().Match<TestDataAssetSummary>(a =>
            a.SemanticRole == TestDataSemanticRoles.Child && a.AppliesTo!.Contains(TestDataContexts.SkolenaervaerReport) && a.Environment == null);
        assets.Should().OnlyContain(a => a.Consumers == 0, "Applies to is metadata, never a consumer");
    }

    [Fact]
    public async Task Strong_unique_matches_are_suggested_without_swapping_and_nothing_is_persisted_until_apply()
    {
        var bed = await CreateAsync();
        var view = (await ViewAsync(bed))!;
        Row(view, AltinnTestDataRequirements.Reporter).Should().Match<ConnectedSystemTestDataRow>(r => r.State == ConnectedSystemTestDataState.Suggested && r.Suggested == SkoleNaervaerTestIdentityDefaults.ReporterAlias);
        Row(view, AltinnTestDataRequirements.Child).Should().Match<ConnectedSystemTestDataRow>(r => r.State == ConnectedSystemTestDataState.Suggested && r.Suggested == SkoleNaervaerTestIdentityDefaults.ChildAlias);
        Row(view, AltinnTestDataRequirements.Reporter).Candidates.Should().NotContain(c => c.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias, "a Reporter requirement never offers a Child");
        view.StatusLabel.Should().Be("Partial");
        (await bed.Integrations.GetAsync(Env, SystemId, CancellationToken.None))!.TestDataBindings.Should().BeNull("a suggestion is never a binding");
    }

    [Fact]
    public async Task Apply_stores_aliases_only_and_consumers_show_the_connected_system()
    {
        var bed = await CreateAsync();
        var (view, problem) = await BindAsync(bed, (AltinnTestDataRequirements.Reporter, SkoleNaervaerTestIdentityDefaults.ReporterAlias), (AltinnTestDataRequirements.Child, SkoleNaervaerTestIdentityDefaults.ChildAlias));
        problem.Should().BeNull();
        view!.Should().Match<ConnectedSystemTestDataView>(v => v.StatusLabel == "Ready" && v.Readiness.State == SetupReadinessState.Ready && v.Readiness.Reason == "2/2 required assets bound.");
        var json = (await _db.ExternalIntegrations.SingleAsync(r => r.Id == SystemId)).DocumentJson;
        var reporterValue = bed.Options.UnattendedIdPortenTest.Reporters.Single(r => r.Alias == SkoleNaervaerTestIdentityDefaults.ReporterAlias).TestIdPersonIdentifier!;
        var childValue = bed.Options.SyntheticChildren.Single(c => c.Alias == SkoleNaervaerTestIdentityDefaults.ChildAlias).TestPersonIdentifier!;
        json.Should().Contain(SkoleNaervaerTestIdentityDefaults.ReporterAlias).And.NotContain(reporterValue).And.NotContain(childValue, "the binding is a reference; the value stays in Test Data");

        var usages = await bed.Lifecycle.UsagesAsync(SkoleNaervaerTestIdentityDefaults.ReporterAlias, CancellationToken.None);
        usages.Should().ContainSingle().Which.Should().Match<TestDataAssetUsage>(u => u.ConsumerType == "Connected system" && u.ConsumerName == "Skolenærvær – Altinn intake"
            && u.Location == "Reporter identity" && u.EnvironmentId == Env);
        var assets = (await bed.Catalog.ViewAsync(CancellationToken.None)).Assets;
        assets.Where(a => a.Alias is SkoleNaervaerTestIdentityDefaults.ReporterAlias or SkoleNaervaerTestIdentityDefaults.ChildAlias).Should().OnlyContain(a => a.Consumers == 1);
    }

    [Fact]
    public async Task Editing_a_bound_protected_value_keeps_the_binding_without_rebinding()
    {
        var bed = await CreateAsync();
        await BindAsync(bed, (AltinnTestDataRequirements.Child, SkoleNaervaerTestIdentityDefaults.ChildAlias));
        string next;
        do next = SyntheticPersonIdentifier.Generate(); while (bed.Options.UnattendedIdPortenTest.Reporters.Any(r => r.TestIdPersonIdentifier == next));
        var (edit, _) = await bed.Lifecycle.EditAsync("altinn-local:child:" + SkoleNaervaerTestIdentityDefaults.ChildAlias, new TestDataEditRequest(null, next), CancellationToken.None);
        edit.Updated.Should().BeTrue();
        edit.AffectedUsages.Should().ContainSingle(u => u.ConsumerType == "Connected system", "the impact names the bound system");
        Row((await ViewAsync(bed))!, AltinnTestDataRequirements.Child).Should().Match<ConnectedSystemTestDataRow>(r => r.State == ConnectedSystemTestDataState.Bound && r.BoundAlias == SkoleNaervaerTestIdentityDefaults.ChildAlias);
    }

    [Fact]
    public async Task A_second_compatible_reporter_needs_selection_and_never_changes_an_existing_binding()
    {
        var bed = await CreateAsync();
        AddSessionReporter(bed);
        var view = (await ViewAsync(bed))!;
        Row(view, AltinnTestDataRequirements.Reporter).Should().Match<ConnectedSystemTestDataRow>(r => r.State == ConnectedSystemTestDataState.NeedsSelection && r.Suggested == null
            && r.Detail.StartsWith("2 compatible assets found — needs selection."));
        await BindAsync(bed, (AltinnTestDataRequirements.Reporter, "reporter-a"));
        Row((await ViewAsync(bed))!, AltinnTestDataRequirements.Reporter).Should().Match<ConnectedSystemTestDataRow>(r => r.State == ConnectedSystemTestDataState.Bound && r.BoundAlias == "reporter-a",
            "an explicit binding wins over the default and every later suggestion");
    }

    [Fact]
    public async Task Wrong_type_or_role_is_never_suggested_and_is_refused_when_bound()
    {
        var bed = await CreateAsync();
        bed.Registry.Register(new RegisteredTestDataAsset { Alias = "reporter-xml", DataType = TestDataDataType.XmlPayload, SemanticRole = TestDataSemanticRoles.Reporter,
            AppliesTo = [TestDataContexts.AltinnIntake], RegisteredAt = DateTimeOffset.UtcNow });
        bed.Catalog.Approve("registered:reporter-xml").Should().BeNull();
        Row((await ViewAsync(bed))!, AltinnTestDataRequirements.Reporter).Candidates.Should().NotContain(c => c.Alias == "reporter-xml", "an XML fixture is not a scalar Reporter identity");
        (await BindAsync(bed, (AltinnTestDataRequirements.Reporter, "reporter-xml"))).Problem.Should().StartWith("reporter-xml is not compatible with Reporter identity");
        (await BindAsync(bed, (AltinnTestDataRequirements.Reporter, SkoleNaervaerTestIdentityDefaults.ChildAlias))).Problem.Should().StartWith("skolenaervaer-child-default is not compatible");
    }

    [Fact]
    public async Task Unrelated_context_is_a_weak_candidate_explicit_binding_works_and_later_metadata_change_needs_review()
    {
        var bed = await CreateAsync();
        bed.Registry.Register(new RegisteredTestDataAsset { Alias = "person-api-reporter", DataType = TestDataDataType.SimpleValues, SemanticRole = TestDataSemanticRoles.Reporter,
            AppliesTo = [TestDataContexts.PersonApi], RegisteredAt = DateTimeOffset.UtcNow });
        bed.Catalog.Approve("registered:person-api-reporter").Should().BeNull();
        var reporter = Row((await ViewAsync(bed))!, AltinnTestDataRequirements.Reporter);
        reporter.State.Should().Be(ConnectedSystemTestDataState.Suggested, "the default stays the only strong match");
        reporter.Candidates.Single(c => c.Alias == "person-api-reporter").Strength.Should().Be(TestDataMatchStrength.Weak);
        await BindAsync(bed, (AltinnTestDataRequirements.Reporter, "person-api-reporter"));
        Row((await ViewAsync(bed))!, AltinnTestDataRequirements.Reporter).State.Should().Be(ConnectedSystemTestDataState.Bound, "an explicit binding works without the matching Applies to");

        // A binding made while the context matched turns Needs review (still bound) when the metadata later stops matching.
        bed.Registry.Register(new RegisteredTestDataAsset { Alias = "intake-reporter", DataType = TestDataDataType.SimpleValues, SemanticRole = TestDataSemanticRoles.Reporter,
            AppliesTo = [TestDataContexts.AltinnIntake], RegisteredAt = DateTimeOffset.UtcNow });
        bed.Catalog.Approve("registered:intake-reporter").Should().BeNull();
        await BindAsync(bed, (AltinnTestDataRequirements.Reporter, "intake-reporter"));
        (await bed.Lifecycle.EditAsync("registered:intake-reporter", new TestDataEditRequest(null, null) { AppliesTo = [TestDataContexts.PersonApi] }, CancellationToken.None)).Result.Updated.Should().BeTrue();
        Row((await ViewAsync(bed))!, AltinnTestDataRequirements.Reporter).Should().Match<ConnectedSystemTestDataRow>(r => r.State == ConnectedSystemTestDataState.NeedsReview && r.BoundAlias == "intake-reporter"
            && r.Detail.Contains("context metadata no longer matches this consumer"));

        // A role change that affects a bound consumer is shown first and needs confirmation.
        var (blocked, _) = await bed.Lifecycle.EditAsync("registered:intake-reporter", new TestDataEditRequest(null, null) { SemanticRole = TestDataSemanticRoles.PersonId }, CancellationToken.None);
        blocked.Should().Match<TestDataEditResult>(r => !r.Updated && r.Message.StartsWith("Changing the role affects 1 bound consumer(s): Skolenærvær – Altinn intake · Reporter identity"));
        (await bed.Lifecycle.EditAsync("registered:intake-reporter", new TestDataEditRequest(null, null) { SemanticRole = TestDataSemanticRoles.PersonId, ConfirmRoleImpact = true }, CancellationToken.None))
            .Result.Updated.Should().BeTrue();
        Row((await ViewAsync(bed))!, AltinnTestDataRequirements.Reporter).State.Should().Be(ConnectedSystemTestDataState.NeedsReview, "never silently unbound");
        (await bed.Lifecycle.EditAsync("altinn-local:child:" + SkoleNaervaerTestIdentityDefaults.ChildAlias, new TestDataEditRequest(null, null) { SemanticRole = "Reporter" }, CancellationToken.None))
            .Result.Message.Should().Contain("declared by its source", "a protected default's role is fixed");
    }

    [Fact]
    public async Task Deleting_or_resetting_a_bound_custom_asset_leaves_a_missing_reference_and_never_auto_binds()
    {
        var bed = await CreateAsync();
        AddSessionReporter(bed);
        await BindAsync(bed, (AltinnTestDataRequirements.Reporter, "reporter-a"));
        var (refused, status) = await bed.Lifecycle.DeleteAsync("altinn-local:reporter:reporter-a", leaveReferencesUnresolved: false, CancellationToken.None);
        (refused.Deleted, status).Should().Be((false, 409));
        refused.UnresolvedReferences.Should().ContainSingle(u => u.ConsumerType == "Connected system" && u.Location == "Reporter identity");
        (await bed.Lifecycle.ResetAsync(0, CancellationToken.None)).Should().NotBeNull();
        var reporter = Row((await ViewAsync(bed))!, AltinnTestDataRequirements.Reporter);
        reporter.Should().Match<ConnectedSystemTestDataRow>(r => r.State == ConnectedSystemTestDataState.MissingReference && r.BoundAlias == "reporter-a"
            && r.Suggested == SkoleNaervaerTestIdentityDefaults.ReporterAlias && r.Detail.StartsWith("Previously bound Test Data (reporter-a) no longer exists."));
        (await bed.Integrations.GetAsync(Env, SystemId, CancellationToken.None))!.TestDataBindings!.Single().AssetAlias.Should().Be("reporter-a", "reset never re-binds to a replacement");
        (await bed.Lifecycle.UsagesAsync("reporter-a", CancellationToken.None)).Should().ContainSingle(u => u.Unresolved);
    }

    [Fact]
    public async Task Optional_requirements_never_block_and_a_system_without_requirements_is_not_required()
    {
        var bed = await CreateAsync();
        var optional = new ConnectedSystemTestDataRequirement("report-payload", "Report payload (XML fixture)", TestDataSemanticRoles.ReportXml, TestDataDataType.XmlPayload, false,
            [TestDataContexts.SkolenaervaerReport], "Optional fixture");
        var integration = (await bed.Integrations.GetAsync(Env, SystemId, CancellationToken.None))! with
        {
            TestDataBindings = [new(AltinnTestDataRequirements.Reporter, SkoleNaervaerTestIdentityDefaults.ReporterAlias), new(AltinnTestDataRequirements.Child, SkoleNaervaerTestIdentityDefaults.ChildAlias)],
        };
        var assets = (await bed.Catalog.ViewAsync(CancellationToken.None)).Assets;
        var view = ConnectedSystemTestDataMatcher.View(integration, [.. AltinnTestDataRequirements.For(new Dictionary<string, string>()), optional], assets);
        view.StatusLabel.Should().Be("Ready");
        Row(view, "report-payload").State.Should().Be(ConnectedSystemTestDataState.OptionalMissing);

        var generic = await CreateAsync(withRequirements: false);
        var none = (await ViewAsync(generic))!;
        (none.StatusLabel, none.Readiness.State, none.Rows.Count).Should().Be(("Not required", SetupReadinessState.NotApplicable, 0));
    }

    [Fact]
    public void Scope_matches_the_environment_by_id_name_or_canonical_name_in_parentheses_never_by_partial_text()
    {
        var tt02 = new TestDataAssetSummary { Alias = "r", Environment = "TT02" };
        ConnectedSystemTestDataMatcher.ScopeCompatible(tt02, "tt02acc", "Test (TT02)").Should().BeTrue("Altinn shows TT02 as \"Test (TT02)\"");
        ConnectedSystemTestDataMatcher.ScopeCompatible(tt02, "tt02acc", "TT02").Should().BeTrue();
        ConnectedSystemTestDataMatcher.ScopeCompatible(tt02, "dev", "Development").Should().BeFalse();
        ConnectedSystemTestDataMatcher.ScopeCompatible(new TestDataAssetSummary { Alias = "r", Environment = "TT" }, "x", "Test (TT02)").Should().BeFalse("never a partial-text match");
        ConnectedSystemTestDataMatcher.ScopeCompatible(new TestDataAssetSummary { Alias = "c" }, "dev", "Development").Should().BeTrue("Shared applies everywhere");
    }

    [Fact]
    public void Altinn_intake_declares_reporter_and_child_and_the_payload_only_with_a_form_contract()
    {
        AltinnTestDataRequirements.For(new Dictionary<string, string>()).Select(r => (r.Id, r.SemanticRole, r.Required)).Should().Equal(
            (AltinnTestDataRequirements.Reporter, TestDataSemanticRoles.Reporter, true), (AltinnTestDataRequirements.Child, TestDataSemanticRoles.Child, true));
        AltinnTestDataRequirements.For(new Dictionary<string, string> { ["formDataType"] = "skolenaervaer" }).Last()
            .Should().Match<ConnectedSystemTestDataRequirement>(r => r.Id == AltinnTestDataRequirements.ReportPayload && !r.Required && r.DataType == TestDataDataType.XmlPayload);
        JsonSerializer.Serialize(new ExternalIntegration()).Should().NotContain("TestDataBindings", "older documents serialize unchanged until a binding exists");
    }
}
