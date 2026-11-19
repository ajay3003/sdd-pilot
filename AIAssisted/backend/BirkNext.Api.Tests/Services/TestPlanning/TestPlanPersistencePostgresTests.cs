using BirkNext.Api.Data;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Saved test plans on a real PostgreSQL database: every saved revision is kept and a restore is a new revision; the last scenario discovery survives
/// a restart. (Moved from the retired manual run tests on 2026-11-19: manual execution is owned by Azure DevOps Test Plans, plan persistence is not.)
/// </summary>
public sealed class TestPlanPersistencePostgresTests : IAsyncLifetime, IDisposable
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_tpp");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtr-" + Guid.NewGuid().ToString("N")[..8]);
    private const string Spec = "specs/001-library/spec.md";
    private ScenarioDiscoveryCache _cache = new();

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    private TestPlanSuggestionService Plans(AppDbContext db)
    {
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        var guides = new SystemGuideService(db, [], Array.Empty<IIntegrationJourneyPack>());
        return new TestPlanSuggestionService(db, guides, new SourceBaselineService(db, new IqrSourceStore(db)), testData, Array.Empty<IIntegrationJourneyPack>(),
            new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance), discoveries: _cache, discoveryStore: new ScenarioDiscoveryStore(db, _cache));
    }

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

    private async Task<ScenarioDiscoveryResult> Discover(AppDbContext db, string project = "library") =>
        await new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _cache, store: new ScenarioDiscoveryStore(db, _cache)).DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = project, ProjectName = "Library",
            Workspace = new BirkNext.TestCoverage.CoverageWorkspaceEvidence
            {
                SpecificationAvailable = true, SpecificationArtifacts = [new("spec-1", Spec, null, "fp")],
                Requirements = [new("FR-001", "System MUST let a member reserve a book.", null), new("FR-002", "System MUST email a confirmation.", null)],
                AcceptanceScenarios = [new BirkNext.TestCoverage.QaTestCandidate { Id = "US1-1", Title = "Reserve a book", Given = "a member is signed in", When = "the member reserves the book", Then = "the book is reserved", RequirementIds = ["FR-001"] }],
            },
        }, default);

    /// <summary>A project plan with the specification scenario case (one step with its expected result) and the FR-001 requirement case.</summary>
    private async Task<(TestPlanDraft Plan, TestCaseDraft Scenario)> PlanAsync(AppDbContext db, string project = "library")
    {
        var discovery = await Discover(db, project);
        var plan = await Plans(db).CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = project, ProjectName = "Library", DiscoveryId = discovery.DiscoveryId,
            CandidateIds = [.. discovery.Candidates.Select(c => c.CandidateId)] }, "tester", default);
        return (plan, plan.Cases.First(c => c.Generated.Evidence.Any(e => e.Kind == "specification")));
    }

    private static SaveTestPlanDraftRequest Rename(TestPlanDraft plan, string name) => new()
    {
        BaseRevision = plan.Revision, PlanName = name, ScenarioIds = [.. plan.Cases.Select(c => c.Source.ScenarioId)], Suites = plan.Suites,
        Cases = [.. plan.Cases.Select(c => new TestCaseHumanPart { ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = c.Edits })],
    };

    [Fact]
    public async Task Every_saved_plan_revision_is_kept_and_a_restore_is_a_new_revision_that_keeps_history()
    {
        await using var db = Context();
        var (plan, _) = await PlanAsync(db);
        var saved = await Plans(db).SaveAsync(plan.Id, Rename(plan, "Library — sprint 2"), "anna", default);

        var history = await Plans(Context()).RevisionsAsync(plan.Id, default);
        history.Select(h => (h.Revision, h.Reason, h.Current)).Should().Equal((saved.Revision, "Saved", true), (plan.Revision, "Created", false));
        (await Plans(Context()).RevisionAsync(plan.Id, plan.Revision, default)).PlanName.Should().Be(plan.PlanName, "an earlier revision reads as it was");

        var restored = await Plans(Context()).RestoreRevisionAsync(plan.Id, new RestoreTestPlanRevisionRequest(saved.Revision, plan.Revision), "bob", default);
        restored.Revision.Should().Be(saved.Revision + 1, "a restore is a new revision, never a rewrite");
        restored.PlanName.Should().Be(plan.PlanName);
        restored.Cases.Select(c => c.Source.ScenarioId).Should().Equal(plan.Cases.Select(c => c.Source.ScenarioId));
        (await Plans(Context()).RevisionsAsync(plan.Id, default)).Select(h => h.Reason).Should().Equal($"Restored from revision {plan.Revision}", "Saved", "Created");

        var stale = () => Plans(Context()).RestoreRevisionAsync(plan.Id, new RestoreTestPlanRevisionRequest(saved.Revision, plan.Revision), "bob", default);
        (await stale.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("draft-changed", "a restore from an outdated view never overwrites newer work");
        var current = () => Plans(Context()).RestoreRevisionAsync(plan.Id, new RestoreTestPlanRevisionRequest(restored.Revision, restored.Revision), "bob", default);
        (await current.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("revision-current");
        var missing = () => Plans(Context()).RevisionAsync(plan.Id, 99, default);
        (await missing.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("revision-not-found");
    }

    [Fact]
    public async Task A_discovery_result_survives_a_restart_and_a_plan_can_still_be_prepared_from_it()
    {
        await using var db = Context();
        var discovery = await Discover(db);
        (await db.ScenarioDiscoveryResults.CountAsync()).Should().Be(1);

        // Restart: a new, empty cache. The last result comes back from storage; nothing is re-run.
        _cache = new ScenarioDiscoveryCache();
        var service = new TestScenarioDiscoveryService(new IqrSourceStore(null!), [], _cache, store: new ScenarioDiscoveryStore(Context(), _cache));
        service.Last("library", null).Should().BeNull("the cache is empty after a restart");
        var restored = await service.LastAsync("library", null, default);
        restored!.DiscoveryId.Should().Be(discovery.DiscoveryId);
        restored.Candidates.Select(c => c.CandidateId).Should().Equal(discovery.Candidates.Select(c => c.CandidateId));
        (await service.LastAsync("other-project", null, default)).Should().BeNull("another project's result is never returned");

        _cache = new ScenarioDiscoveryCache();
        var plan = await Plans(Context()).CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "library", DiscoveryId = discovery.DiscoveryId,
            CandidateIds = [discovery.Candidates[0].CandidateId] }, "tester", default);
        plan.Cases.Should().ContainSingle("the stored result is found by id after a restart, so preparing does not need discovery again");
        var foreign = () => Plans(Context()).CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "other-project", DiscoveryId = discovery.DiscoveryId,
            CandidateIds = [discovery.Candidates[0].CandidateId] }, "tester", default);
        (await foreign.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("discovery-missing");
    }
}
