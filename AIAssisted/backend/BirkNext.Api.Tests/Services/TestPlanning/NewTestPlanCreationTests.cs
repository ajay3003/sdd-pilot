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
/// «+ Ny testplan» (2026-11-25) on a fresh migrated PostgreSQL database: an empty test plan with the tester's name (a name is required), its own
/// id every time (also within the same second), nothing of an earlier plan (cases, revision, Azure DevOps links), and one plan per creation
/// request — a double click, a retry after a lost answer and two simultaneous requests never create a second test plan.
/// </summary>
public sealed class NewTestPlanCreationTests : IAsyncLifetime, IDisposable
{
    private const string Project = "import:p1";
    private readonly string _database = TestPostgres.TemporaryName("birknext_ntp");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ntp-" + Guid.NewGuid().ToString("N")[..8]);

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    private TestPlanSuggestionService Service(AppDbContext db)
    {
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        return new TestPlanSuggestionService(db, new SystemGuideService(db, [], Array.Empty<IIntegrationJourneyPack>()), new SourceBaselineService(db, new IqrSourceStore(db)), testData,
            Array.Empty<IIntegrationJourneyPack>(), new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance), discoveries: new ScenarioDiscoveryCache());
    }

    private static CreateProjectTestPlanRequest New(string name, string? requestId = null) => new()
    {
        ProjectKey = Project, ProjectName = "SkoleService", PlanName = name, AllowEmpty = true, CreationRequestId = requestId ?? Guid.NewGuid().ToString("N"),
    };

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

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task An_empty_test_plan_is_created_with_the_testers_name_and_nothing_else()
    {
        await using var db = Context();
        var plan = await Service(db).CreateProjectAsync(New("Testplan 2026-11-25 10-15-30"), "tester", default);

        plan.PlanName.Should().Be("Testplan 2026-11-25 10-15-30");
        plan.Cases.Should().BeEmpty();
        plan.Revision.Should().Be(1);
        plan.ProjectKey.Should().Be(Project);
        plan.CreationRequestId.Should().NotBeNullOrWhiteSpace();
        (await db.TestPlanDrafts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_empty_test_plan_needs_a_name()
    {
        await using var db = Context();
        var act = () => Service(db).CreateProjectAsync(New("  "), "tester", default);
        (await act.Should().ThrowAsync<TestPlanException>()).Which.Message.Should().Be("Gi testplanen et navn.");
    }

    [Fact]
    public async Task Without_AllowEmpty_a_plan_still_needs_scenarios()
    {
        await using var db = Context();
        var act = () => Service(db).CreateProjectAsync(New("x") with { AllowEmpty = false }, "tester", default);
        await act.Should().ThrowAsync<TestPlanException>().WithMessage("Select at least one scenario*");
    }

    [Fact]
    public async Task Two_test_plans_with_the_same_name_in_the_same_second_are_two_plans()
    {
        await using var db = Context();
        var service = Service(db);
        var first = await service.CreateProjectAsync(New("Testplan 2026-11-25 10-15-30"), "tester", default);
        var second = await service.CreateProjectAsync(New("Testplan 2026-11-25 10-15-30"), "tester", default);

        second.Id.Should().NotBe(first.Id);
        (await db.TestPlanDrafts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_new_test_plan_inherits_nothing_from_an_earlier_one()
    {
        await using var db = Context();
        var service = Service(db);
        var earlier = await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = Project, PlanName = "Earlier", ManualCaseTitle = "Log in" }, "tester", default);
        earlier.Cases.Should().ContainSingle();

        var fresh = await service.CreateProjectAsync(New("Fresh"), "tester", default);

        fresh.Cases.Should().BeEmpty("cases belong to the plan they were made in");
        fresh.Revision.Should().Be(1);
        fresh.Id.Should().NotBe(earlier.Id);
        fresh.PlanName.Should().Be("Fresh");
    }

    [Fact]
    public async Task The_same_creation_request_again_returns_the_plan_already_created()
    {
        await using var db = Context();
        var service = Service(db);
        var request = New("Testplan 2026-11-25 10-15-30", "req-1");
        var first = await service.CreateProjectAsync(request, "tester", default);
        await using var other = Context();
        var retry = await Service(other).CreateProjectAsync(request, "tester", default);

        retry.Id.Should().Be(first.Id, "a retry after a lost answer is the same test plan");
        (await db.TestPlanDrafts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Simultaneous_requests_for_one_draft_create_one_test_plan()
    {
        var request = New("Testplan 2026-11-25 10-15-30", "req-concurrent");
        async Task<Guid?> Create()
        {
            await using var db = Context();
            try { return (await Service(db).CreateProjectAsync(request, "tester", default)).Id; }
            catch (TestPlanException e) when (e.Message.Contains("opprettes allerede")) { return null; }
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Create)));

        results.OfType<Guid>().Distinct().Should().ContainSingle("every answer names the same test plan or says it is being created");
        await using var check = Context();
        (await check.TestPlanDrafts.CountAsync()).Should().Be(1);
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
