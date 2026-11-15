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
/// The export service on a fresh migrated PostgreSQL database: it reads the saved plan at the reviewed revision only, never another project's,
/// never changes the plan, refuses a blocked file, records which file was created (case revisions, not an import) and keeps export settings per
/// project without credentials.
/// </summary>
public sealed class AdoTestPlanExportServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_adox");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "adox-" + Guid.NewGuid().ToString("N")[..8]);

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    private TestPlanSuggestionService Plans(AppDbContext db)
    {
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        return new TestPlanSuggestionService(db, new SystemGuideService(db, [], Array.Empty<IIntegrationJourneyPack>()), new SourceBaselineService(db, new IqrSourceStore(db)), testData,
            Array.Empty<IIntegrationJourneyPack>(), new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance));
    }

    private AdoTestPlanExportService Export(AppDbContext db) => new(db, Plans(db));

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

    /// <summary>A project plan with one hand-written case given two steps with expected results and approved.</summary>
    private async Task<TestPlanDraft> PlanAsync(AppDbContext db, string project = "library")
    {
        var plans = Plans(db);
        var plan = await plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = project, ManualCaseTitle = "Låne en bok" }, "tester", default);
        var c = plan.Cases.Single();
        plan = await plans.SaveAsync(plan.Id, new SaveTestPlanDraftRequest
        {
            BaseRevision = plan.Revision, PlanName = plan.PlanName, ScenarioIds = [c.Source.ScenarioId], Suites = plan.Suites,
            Cases = [new TestCaseHumanPart { ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = new TestCaseEdits { Steps = ["Åpne siden", "Registrer lånet"], StepExpected = ["Siden vises", "Lånet er registrert"], ExpectedResult = "Lånet er registrert" } }],
        }, "tester", default);
        return await plans.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, c.Source.ScenarioId, TestCaseReviewState.Accepted, null), "lead", default);
    }

    private static AdoExportRequest Request(TestPlanDraft plan, string project = "library") => new()
    {
        ProjectKey = project, DraftId = plan.Id, PlanRevision = plan.Revision, Profile = new AdoExportProfile { AreaPath = "Library\\Loans", State = "Design" },
    };

    [Fact]
    public async Task A_file_is_built_from_the_saved_revision_records_what_was_exported_and_never_changes_the_plan()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var before = await db.TestPlanDrafts.AsNoTracking().Where(d => d.Id == plan.Id).Select(d => new { d.Revision, d.ContentJson }).SingleAsync();

        var file = await Export(db).FileAsync(Request(plan), "anna", default);
        file.Should().Match<AdoExportFile>(f => f.Cases == 1 && f.Rows == 2 && f.FileName.EndsWith($"-r{plan.Revision}.xlsx") && f.ContentType.Contains("spreadsheetml"));
        var csv = await Export(db).FileAsync(Request(plan) with { Profile = Request(plan).Profile with { Format = AdoExportFormat.Csv } }, "anna", default);
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(csv.Base64)).Should().Contain("\"Låne en bok\"").And.Contain("\"Lånet er registrert\"");

        var after = await Context().TestPlanDrafts.AsNoTracking().Where(d => d.Id == plan.Id).Select(d => new { d.Revision, d.ContentJson }).SingleAsync();
        after.Should().Be(before, "exporting reads the plan; it never saves it");
        var history = await Export(Context()).HistoryAsync("library", default);
        history.Should().HaveCount(2);
        history.Should().OnlyContain(h => h.PlanRevision == plan.Revision && h.Cases == 1 && h.Rows == 2 && h.ExportedBy == "anna" && h.CaseRevisions.Count == 1
            && h.CaseRevisions[0].StartsWith(plan.Cases.Single().Source.ScenarioId + "@"));
        history.Select(h => h.Format).Should().BeEquivalentTo(["Xlsx", "Csv"]);
    }

    [Fact]
    public async Task A_newer_saved_revision_another_projects_plan_and_a_blocked_export_are_refused()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);

        var stale = () => Export(db).PreviewAsync(Request(plan) with { PlanRevision = plan.Revision - 1 }, default);
        (await stale.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("plan-changed");
        var foreign = () => Export(db).PreviewAsync(Request(plan, "other-project"), default);
        (await foreign.Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(404);
        var blocked = () => Export(db).FileAsync(Request(plan) with { Profile = new AdoExportProfile { AreaPath = null } }, "anna", default);
        (await blocked.Should().ThrowAsync<TestPlanException>()).Which.Should().Match<TestPlanException>(e => e.Code == "export-blocked" && e.Message.Contains("Area Path is missing"));
        (await Export(Context()).HistoryAsync("library", default)).Should().BeEmpty("a refused file is not recorded");
    }

    [Fact]
    public async Task Export_settings_are_kept_per_project_without_credentials()
    {
        await using var db = Context();
        var export = Export(db);
        (await export.ProfileAsync("library", default)).Should().Be(new AdoExportProfile(), "defaults: Services, Design, Excel, preconditions in the report");

        await export.SaveProfileAsync("library", new AdoExportProfile { DestinationLabel = " contoso / Library ", AreaPath = "Library\\Loans", State = "Design", Format = AdoExportFormat.Csv }, "anna", default);
        var stored = await Export(Context()).ProfileAsync("library", default);
        stored.Should().Match<AdoExportProfile>(p => p.DestinationLabel == "contoso / Library" && p.AreaPath == "Library\\Loans" && p.Format == AdoExportFormat.Csv);
        (await Export(Context()).ProfileAsync("other-project", default)).AreaPath.Should().BeNull("settings never leak to another project");
        (await db.AdoExportProfiles.AsNoTracking().SingleAsync()).ProfileJson.Should().NotContainAny("password", "token", "secret");
    }
}
