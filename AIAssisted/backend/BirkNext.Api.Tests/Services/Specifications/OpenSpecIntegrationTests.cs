using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.Specifications.OpenSpec;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using BirkNext.SourceEvidence;
using BirkNext.Specifications;
using BirkNext.TestCoverage;
using BirkNext.TestData;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.Specifications;

/// <summary>
/// The OpenSpec route end to end on a fresh, temporary, migrated PostgreSQL database (created and dropped by the test; never the normal user data):
/// routed import capture → discovery basis → Finn og forstå candidates → plan preparation → Testoversikt → source changes → reconciliation, and the
/// Spec Kit route through the same router producing exactly what it produced before.
/// </summary>
public sealed class OpenSpecIntegrationTests : IAsyncLifetime, IDisposable
{
    private const string OpenApp = "repo:openspec-demo";
    private const string KitApp = "repo:speckit-demo";
    private readonly string _database = TestPostgres.TemporaryName("birknext_osr");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osr-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ScenarioDiscoveryCache _discoveries = new();

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await Execute($"CREATE DATABASE \"{_database}\"");
        await using var db = Context();
        await DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private SpecificationFormatSettingsStore Settings => new(Path.Combine(_dir, "format"));
    private OpenSpecSourceModelStore Models => new(Path.Combine(_dir, "models"));

    private sealed record Stack(TestScenarioDiscoveryService Discovery, TestPlanSuggestionService Plans, SpecificationProcessingRouter Router, OpenSpecStatusService Status,
        SpecificationCatalogService Catalogs);

    private Stack Services(AppDbContext db, bool withRouter = true)
    {
        var store = new IqrSourceStore(db);
        var baselines = new SourceBaselineService(db, store);
        var specs = new SpecificationCatalogService(db);
        var router = new SpecificationProcessingRouter(Settings, specs, new OpenSpecProcessingRoute(specs, Models));
        var guides = new SystemGuideService(db, [], Array.Empty<IIntegrationJourneyPack>());
        var resolver = new ScenarioSourceBasisResolver(baselines, store, specs, withRouter ? router : null);
        List<ITestScenarioCandidateProvider> providers = [new SpecificationScenarioProvider()];
        var discoveryStore = new ScenarioDiscoveryStore(db, _discoveries);
        var discovery = new TestScenarioDiscoveryService(store, providers, _discoveries, store: discoveryStore, basisResolver: resolver);
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var testDataCatalog = new TestDataCatalog([new NoValues(), new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(testDataCatalog, new NoSchemas()), testDataCatalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        var plans = new TestPlanSuggestionService(db, guides, baselines, testData, Array.Empty<IIntegrationJourneyPack>(), new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance),
            discoveries: _discoveries, discoveryStore: discoveryStore, specificationCatalogs: specs, basisResolver: resolver);
        return new Stack(discovery, plans, router, new OpenSpecStatusService(specs, Models, Settings, db, baselines), specs);
    }

    /// <summary>A Source Analysis snapshot of <paramref name="repository"/> plus the routed specification capture (as Project Import does).</summary>
    private async Task<(Guid Snapshot, SpecificationRouteOutcome Outcome)> ImportAsync(string repository, string importId, IReadOnlyList<SpecificationCatalogService.Document> documents,
        DateTimeOffset at, bool route = true)
    {
        var id = Guid.NewGuid();
        await using var db = Context();
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord
        {
            Id = id, EnvironmentId = "", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = at,
            EvidenceJson = JsonSerializer.Serialize(new IqrSourceSnapshot
            {
                Id = id, Archive = new SourceArchive(importId + ".zip", new string('c', 63) + (importId.Length % 10), 10, "content-" + importId), AnalyzedAt = at,
                Repository = new SourceRepositoryIdentity(repository, repository, "Test"),
                ProjectImport = new BirkNext.ProjectImport.ProjectImportProvenance { ImportId = importId, ArchiveFileName = importId + ".zip", ArchiveSha256 = "sha-" + importId, ImportedAt = at },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        await db.SaveChangesAsync();
        var x = Services(db);
        var outcome = route
            ? await x.Router.CaptureAsync(new SpecificationRouteRequest(repository, importId, "sha-" + importId, id, documents.Where(d => d.Path.EndsWith(".md")).ToList(),
                documents.Where(d => !d.Path.EndsWith(".md")).ToList()), default)
            : new SpecificationRouteOutcome(SpecificationFormat.SpecKit, await x.Catalogs.CaptureAsync(importId, "sha-" + importId, id, documents, default));
        return (id, outcome);
    }

    private static List<DiscoveredScenario> Spec(ScenarioDiscoveryResult r) => [.. r.Candidates.Where(c => c.CapabilityId == D.Specification)];

    [Fact]
    public async Task C01_C02_C03_C04_format_is_per_application_defaults_to_SpecKit_and_persists()
    {
        Settings.FormatOf(OpenApp).Format.Should().Be(SpecificationFormat.SpecKit, "C01: nothing chosen = Spec Kit");
        Settings.Save(OpenApp, SpecificationFormat.OpenSpec, "admin", DateTimeOffset.UtcNow);
        new SpecificationFormatSettingsStore(Path.Combine(_dir, "format")).FormatOf(OpenApp).Format.Should().Be(SpecificationFormat.OpenSpec, "C03: a new store instance (reload) reads it");
        Settings.FormatOf(KitApp).Format.Should().Be(SpecificationFormat.SpecKit, "C04: another application keeps its own format");
        Settings.Read().History.Should().ContainSingle(h => h.ApplicationKey == OpenApp && h.From == SpecificationFormat.SpecKit && h.To == SpecificationFormat.OpenSpec && h.By == "admin");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task C05_changing_a_format_requires_the_system_configuration_permission()
    {
        var save = typeof(BirkNext.Api.Controllers.SpecificationFormatController).GetMethod(nameof(BirkNext.Api.Controllers.SpecificationFormatController.Save))!;
        save.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Should().ContainSingle(a => a.Policy == BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.Policy);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task S01_the_SpecKit_route_through_the_router_equals_the_direct_capture_and_discovery_is_identical()
    {
        var docs = SpecKitRegressionBaselineTests.Documents();
        var (direct, viaDirect) = await ImportAsync(KitApp, "imp-direct", docs, DateTimeOffset.UtcNow.AddMinutes(-5), route: false);
        var (routed, viaRouter) = await ImportAsync(KitApp + "-b", "imp-routed", docs, DateTimeOffset.UtcNow);
        viaRouter.Format.Should().Be(SpecificationFormat.SpecKit);
        viaRouter.Catalog!.Fingerprint.Should().Be(viaDirect.Catalog!.Fingerprint, "the same Spec Kit catalog content");
        viaRouter.Catalog.Requirements.Should().BeEquivalentTo(viaDirect.Catalog.Requirements, o => o.WithStrictOrdering());
        SpecificationFormatMarker.IsOpenSpec(viaRouter.Catalog.Fingerprint).Should().BeFalse();
        await using var db = Context();
        var a = await Services(db, withRouter: false).Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp-direct", SnapshotId = direct, MemberSnapshotIds = [direct] }, default);
        var b = await Services(db).Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp-direct", SnapshotId = direct, MemberSnapshotIds = [direct] }, default);
        Spec(b).Select(c => (c.CandidateId, c.SourceIdentity, c.Fingerprint)).Should().Equal(Spec(a).Select(c => (c.CandidateId, c.SourceIdentity, c.Fingerprint)),
            "S02/S03: identical Spec Kit candidates with and without the router");
        b.Limitations.Should().Equal(a.Limitations);
        var overviewA = await Services(db, withRouter: false).Plans.OverviewAsync("import:imp-direct", default);
        var overviewB = await Services(db).Plans.OverviewAsync("import:imp-direct", default);
        JsonSerializer.Serialize(overviewB.Specifications).Should().Be(JsonSerializer.Serialize(overviewA.Specifications), "S04/S05: Testoversikt grouping and KPI populations unchanged");
        overviewB.Specifications.Select(s => s.Module).Should().Contain("SkoleService").And.Contain(ModuleStructure.ProjectWide);
    }

    [Fact]
    public async Task M01_to_M08_and_D08_OpenSpec_flows_through_the_existing_Manual_Testing_services()
    {
        Settings.Save(OpenApp, SpecificationFormat.OpenSpec, "admin", DateTimeOffset.UtcNow);
        var (s1, outcome) = await ImportAsync(OpenApp, "imp-os1", OpenSpecFixture.Documents(), DateTimeOffset.UtcNow.AddMinutes(-10));
        outcome.Format.Should().Be(SpecificationFormat.OpenSpec);
        outcome.Catalog.Should().NotBeNull();
        SpecificationFormatMarker.IsOpenSpec(outcome.Catalog!.Fingerprint).Should().BeTrue();
        outcome.Catalog.Requirements.Should().HaveCount(5);

        Guid planId; string validCaseId; string validIdentity; List<string> validSteps;
        await using (var db = Context())
        {
            var x = Services(db);
            // M01: Finn og forstå — OpenSpec scenarios, no user story, generated requirement keys; proposals never appear.
            var d = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp-os1", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
            d.Basis!.SpecificationOrigin.Should().Be(SpecificationOrigin.ImportedSnapshot);
            d.Limitations.Should().Contain(l => l.Contains("Kildeformat OpenSpec"));
            var spec = Spec(d);
            spec.Should().HaveCount(6, "5 scenarios + 1 requirement without scenarios");
            spec.Should().NotContain(c => c.Title.Contains("OTP") || c.Title.Contains("Idle session"), "M07: proposals are not current scenarios");
            spec.Should().OnlyContain(c => c.Story == null && c.RequirementIds.All(SpecificationFormatMarker.IsGeneratedOpenSpecKey));
            var valid = spec.Single(c => c.Title == "Valid credentials");
            // M02: exact navigation by identity.
            D.Pick(d, [valid.SourceIdentity]).Should().ContainSingle(c => c.CandidateId == valid.CandidateId);
            validIdentity = valid.SourceIdentity;

            // M03: plan preparation keeps the source evidence and steps exactly as written (none fabricated).
            var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest
            {
                ProjectKey = "import:imp-os1", ProjectName = "OpenSpec demo", DiscoveryId = d.DiscoveryId, CandidateIds = [.. spec.Select(c => c.CandidateId)],
            }, "tester", default);
            var validCase = plan.Cases.Single(c => c.Generated.SourceIdentity == valid.SourceIdentity);
            validCase.Generated.Evidence.Should().Contain(e => e.Reference.StartsWith("openspec/specs/auth/spec.md#os:auth/user-authentication/valid-credentials"));
            validCase.Generated.Steps.Should().ContainSingle().Which.Should().ContainEquivalentOf("a user submits a valid username and password", "the source step (the plan engine only capitalises and punctuates)");
            validSteps = validCase.Generated.Steps;
            var requirementOnly = plan.Cases.Single(c => c.Generated.Requirements.Any(r => r.RuleId == "os:auth/session-timeout"));
            requirementOnly.Generated.Steps.Should().BeEmpty("no step is invented for a requirement without scenarios");
            planId = plan.Id; validCaseId = validCase.Source.ScenarioId;

            // M05/M07: Testoversikt counts the current baseline only; OpenSpec capabilities are not modules.
            var overview = await x.Plans.OverviewAsync("import:imp-os1", default);
            overview.Specifications.Sum(s => s.Scenarios.Count).Should().Be(6);
            overview.Specifications.Should().OnlyContain(s => s.Module == ModuleStructure.Unassigned);
            overview.Cases.Should().OnlyContain(c => c.Module == ModuleStructure.Unassigned);
            overview.Specifications.SelectMany(s => s.Scenarios).Count(s => s.RequirementOnly).Should().Be(1);

            // M06/D08: the reconciliation over the stored catalog passes, and Testoversikt reads this catalog.
            var report = await x.Status.ReconcileAsync("import:imp-os1", default);
            report.Checks.Where(c => c.Status != ReconciliationStatus.Pass).Should().BeEmpty(string.Join("; ", report.Checks.Where(c => c.Status != ReconciliationStatus.Pass).Select(c => $"{c.Id} {c.Observed}")));
            var status = await x.Status.StatusAsync("import:imp-os1", default);
            (status.CatalogFormat, status.ConfiguredFormat, status.ModelAvailable, status.CurrentRequirements, status.CurrentScenarios, status.ActiveChanges.Count, status.ArchivedChanges.Count)
                .Should().Be((SpecificationFormat.OpenSpec, SpecificationFormat.OpenSpec, true, 5, 5, 2, 1));

            // M08: prospective impact of proposals is separate and maps saved cases only through identities.
            var changes = await x.Status.SourceChangesAsync("import:imp-os1", default);
            changes.BaselineChanges.Should().BeEmpty();
            changes.ProposedImpact.Should().OnlyContain(c => c.Prospective);
            var rename = changes.ProposedImpact.Single(c => c.Kind == OpenSpecChangeKind.RenamedRequirement);
            rename.AffectedCases.Should().Contain(c => c.CaseId == validCaseId, "the case's requirement is renamed by the proposal (flag for review, not rewritten)");
        }

        // M04 + O09 + L07: an unchanged re-import keeps every case link; an accepted baseline change is an actual source change with affected cases.
        var (s2, again) = await ImportAsync(OpenApp, "imp-os1", OpenSpecFixture.Documents(), DateTimeOffset.UtcNow.AddMinutes(-5));
        again.Catalog!.Revision.Should().Be(outcome.Catalog.Revision, "same content = same revision");
        var changedAuth = OpenSpecFixture.Auth.Replace("- **THEN** a session token is returned", "- **THEN** a signed session token is returned");
        var (s3, changed) = await ImportAsync(OpenApp, "imp-os1", OpenSpecFixture.Documents(auth: changedAuth), DateTimeOffset.UtcNow);
        changed.Catalog!.Revision.Should().Be(outcome.Catalog.Revision + 1);
        await using (var db = Context())
        {
            var x = Services(db);
            var report = await x.Status.SourceChangesAsync("import:imp-os1", default);
            var modified = report.BaselineChanges.Single(c => c.Kind == OpenSpecChangeKind.ModifiedScenario);
            modified.Prospective.Should().BeFalse();
            modified.AffectedCases.Should().ContainSingle(c => c.CaseId == validCaseId && c.Evidence.Contains(validIdentity));
            var plan = await x.Plans.DraftAsync(planId, default);
            plan.Cases.Single(c => c.Source.ScenarioId == validCaseId).Generated.Steps.Should().Equal(validSteps, "M09: saved case content is never rewritten");
            var d = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp-os1", SnapshotId = s3, MemberSnapshotIds = [s3] }, default);
            Spec(d).Single(c => c.Title == "Valid credentials").SourceIdentity.Should().Be(validIdentity, "the wording edit keeps the logical identity");
        }
        _ = s2;
    }

    [Fact]
    public async Task C06_C07_switching_format_never_rewrites_history_and_an_unusable_OpenSpec_route_never_falls_back()
    {
        var docs = SpecKitRegressionBaselineTests.Documents();
        var (s1, kit) = await ImportAsync(KitApp, "imp-kit", docs, DateTimeOffset.UtcNow.AddMinutes(-10));
        kit.Format.Should().Be(SpecificationFormat.SpecKit);
        Guid planId; int revision; string json;
        await using (var db = Context())
        {
            var x = Services(db);
            var d = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp-kit", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
            var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:imp-kit", DiscoveryId = d.DiscoveryId, CandidateIds = [.. Spec(d).Where(c => c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal)).Take(5).Select(c => c.CandidateId)] }, "t", default);
            var approvable = plan.Cases.First(c => TestPlanSuggestionService.ApproveBlocker(c) is null);
            plan = await x.Plans.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, approvable.Source.ScenarioId, TestCaseReviewState.Accepted, null), "lead", default);
            (planId, revision) = (plan.Id, plan.Revision);
            json = (await db.TestPlanDrafts.AsNoTracking().SingleAsync(r => r.Id == planId)).ContentJson;
        }
        Settings.Save(KitApp, SpecificationFormat.OpenSpec, "admin", DateTimeOffset.UtcNow);
        // C07: the same Spec Kit archive imported again as OpenSpec has no openspec/ folder: nothing is captured, the problem is kept, no fallback.
        var (_, refused) = await ImportAsync(KitApp, "imp-kit", docs, DateTimeOffset.UtcNow);
        refused.Format.Should().Be(SpecificationFormat.OpenSpec);
        refused.Catalog.Should().BeNull();
        refused.ProblemCode.Should().Be("OPENSPEC_ROOT_MISSING");
        await using (var db = Context())
        {
            var x = Services(db);
            (await db.SpecificationCatalogs.CountAsync(c => c.ProjectKey == "import:imp-kit")).Should().Be(1, "C06: the Spec Kit catalog is kept, nothing added");
            (await x.Catalogs.CurrentAsync("import:imp-kit", default))!.Fingerprint.Should().Be(kit.Catalog!.Fingerprint);
            var row = await db.TestPlanDrafts.AsNoTracking().SingleAsync(r => r.Id == planId);
            (row.Revision, row.ContentJson).Should().Be((revision, json), "C06/S06/S08: plan, cases and approvals untouched by the switch");
            var status = await x.Status.StatusAsync("import:imp-kit", default);
            status.CatalogFormat.Should().Be(SpecificationFormat.SpecKit);
            status.ConfiguredFormat.Should().Be(SpecificationFormat.OpenSpec);
            status.RouteProblem.Should().Contain("ingen openspec/-mappe");
            status.Notes.Should().Contain(n => n.Contains("Formatvalget gjelder fremtidige importer"));
            // C08: discovery for the switched application states that its current basis is a Spec Kit catalog and refuses a Spec Kit browser draft.
            var d = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp-kit", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
            d.Limitations.Should().Contain(l => l.Contains("behandlet som Spec Kit"));
            var draft = () => x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest
            {
                ProjectKey = "import:imp-kit", SnapshotId = s1, MemberSnapshotIds = [s1], UseWorkingSpecification = true,
                Workspace = new CoverageWorkspaceEvidence { SpecificationAvailable = true, AcceptanceScenarios = [new QaTestCandidate { Id = "x", Given = "a", When = "b", Then = "c" }] },
            }, default);
            (await draft.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("openspec-working-draft-unsupported");
            (await x.Status.ReconcileAsync("import:imp-kit", default)).Overall.Should().Be(ReconciliationStatus.NotApplicable, "Spec Kit data quality is untouched");
        }
    }

    [Fact]
    public async Task C08_an_OpenSpec_application_without_imported_evidence_never_uses_the_browsers_SpecKit_reading()
    {
        Settings.Save(OpenApp, SpecificationFormat.OpenSpec, "admin", DateTimeOffset.UtcNow);
        var (s1, _) = await ImportAsync(OpenApp, "imp-none", [new("README.md", "# nothing")], DateTimeOffset.UtcNow);
        await using var db = Context();
        var d = await Services(db).Discovery.DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = "import:imp-none", SnapshotId = s1, MemberSnapshotIds = [s1],
            Workspace = new CoverageWorkspaceEvidence { SpecificationAvailable = true, AcceptanceScenarios = [new QaTestCandidate { Id = "scenario-1", Given = "a", When = "b", Then = "c" }] },
        }, default);
        Spec(d).Should().BeEmpty("no Spec Kit fallback");
        d.Basis!.SpecificationOrigin.Should().Be(SpecificationOrigin.None);
        d.Limitations.Should().Contain(l => l.Contains("brukes ikke som reserve"));
    }

    [Fact]
    public async Task ADO_transfer_is_preview_only_for_an_OpenSpec_project()
    {
        Settings.Save(OpenApp, SpecificationFormat.OpenSpec, "admin", DateTimeOffset.UtcNow);
        await ImportAsync(OpenApp, "imp-ado", OpenSpecFixture.Documents(), DateTimeOffset.UtcNow);
        await using var db = Context();
        var latest = await db.SpecificationCatalogs.Where(c => c.ProjectKey == "import:imp-ado").OrderByDescending(c => c.Revision).Select(c => c.Fingerprint).FirstAsync();
        SpecificationFormatMarker.IsOpenSpec(latest).Should().BeTrue("the guard reads this marker (AdoTestPlanTransferService.OpenSpecProjectAsync)");
        AdoTestPlanTransferService.OpenSpecPreviewOnly.Should().Contain("sperret");
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class NoValues : ITestDataProvider
    {
        public string Id => "values";
        public string Label => "Values";
        public string Description => "In-test assets.";
        public IReadOnlyList<TestDataAsset> Assets() => [];
        public IReadOnlyList<TestDataSet> Sets() => [];
    }

    private sealed class NoSources : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => false;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private sealed class NoSchemas : ITestDataSchemaSource
    {
        public Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct) => Task.FromResult<TestDataSchemaPackage?>(null);
    }
}
