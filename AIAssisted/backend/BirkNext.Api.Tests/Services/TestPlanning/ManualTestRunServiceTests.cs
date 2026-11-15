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
/// Manual test runs on a fresh migrated PostgreSQL database: a run pins the saved case (later edits never change it), resumes instead of
/// duplicating, records only the tester's decisions, rolls up the outcome by the documented rule, is final once completed or stopped, validates
/// evidence references and defect references, never crosses projects, and feeds Test Coverage / Requirements Traceability through the shared
/// traceability rule. Discovery results survive a restart (stored, restored into a fresh cache).
/// </summary>
public sealed class ManualTestRunServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_mtr");
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

    private ManualTestRunService Runs(AppDbContext db) => new(db, Plans(db));

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

    private static SaveManualRunRequest Save(ManualTestRun run, params (int Step, ManualStepOutcome? Outcome, string? Actual)[] steps) => new()
    {
        BaseRevision = run.Revision, Steps = [.. steps.Select(s => new ManualStepInput(s.Step, s.Outcome, s.Actual))], Evidence = [.. run.Evidence], Defects = [.. run.Defects],
    };

    [Fact]
    public async Task A_run_pins_the_saved_case_resumes_instead_of_duplicating_and_survives_a_restart()
    {
        await using var db = Context();
        var (plan, scenario) = await PlanAsync(db);
        var run = await Runs(db).StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, "QA"), "anna", default);

        run.Should().Match<ManualTestRun>(r => r.State == ManualRunState.InProgress && r.Outcome == ManualRunOutcome.NotDecided && r.Tester == "anna" && r.Environment == "QA"
            && r.PlanRevision == plan.Revision && r.CaseKey == $"{plan.Id:N}/{scenario.Source.ScenarioId}" && r.CaseFingerprint == ManualTestRunService.Fingerprint(scenario));
        run.Steps.Should().ContainSingle().Which.Should().Match<ManualRunStep>(s => s.Number == 1 && s.Expected == "the book is reserved" && s.Outcome == null);
        run.RequirementIds.Should().Equal("FR-001");

        var again = await Runs(Context()).StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "bob", default);
        again.Id.Should().Be(run.Id, "a case has at most one run in progress: starting again resumes it");

        // The case is edited after the run started: the run keeps what was executed.
        var edited = await Plans(db).SaveAsync(plan.Id, new SaveTestPlanDraftRequest
        {
            BaseRevision = plan.Revision, PlanName = plan.PlanName, ScenarioIds = [.. plan.Cases.Select(c => c.Source.ScenarioId)], Suites = plan.Suites,
            Cases = [.. plan.Cases.Select(c => new TestCaseHumanPart { ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId,
                Edits = c.Source.ScenarioId == scenario.Source.ScenarioId ? c.Edits with { Steps = ["Open the catalogue", "Reserve the book"] } : c.Edits })],
        }, "lead", default);
        var reread = await Runs(Context()).GetAsync("library", run.Id, default);
        reread.Steps.Select(s => s.Action).Should().Equal(run.Steps.Select(s => s.Action), "a past run is never rewritten by a later case edit");
        reread.CaseFingerprint.Should().NotBe(ManualTestRunService.Fingerprint(edited.Cases.Single(c => c.Source.ScenarioId == scenario.Source.ScenarioId)));
    }

    [Fact]
    public async Task Results_are_the_testers_decisions_the_outcome_rolls_up_by_the_rule_and_a_completed_run_is_final()
    {
        await using var db = Context();
        var (plan, scenario) = await PlanAsync(db);
        var runs = Runs(db);
        var run = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default);

        var complete = () => runs.CompleteAsync("library", run.Id, new CompleteManualRunRequest(run.Revision), "anna", default);
        (await complete.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("steps-undecided", "a run is never completed with a step nobody judged");

        run = await runs.SaveAsync("library", run.Id, Save(run, (1, ManualStepOutcome.Fail, "Error 500 shown")), "anna", default);
        run.Steps.Single().Should().Match<ManualRunStep>(s => s.Outcome == ManualStepOutcome.Fail && s.Actual == "Error 500 shown" && s.RecordedAt != null);
        run.Outcome.Should().Be(ManualRunOutcome.NotDecided, "no overall result until the tester completes the run");
        var stale = () => runs.SaveAsync("library", run.Id, Save(run with { Revision = run.Revision - 1 }, (1, ManualStepOutcome.Pass, null)), "bob", default);
        (await stale.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-changed");

        run = await runs.CompleteAsync("library", run.Id, new CompleteManualRunRequest(run.Revision), "anna", default);
        run.Should().Match<ManualTestRun>(r => r.State == ManualRunState.Completed && r.Outcome == ManualRunOutcome.Failed && r.CompletedAt != null);
        var change = () => runs.SaveAsync("library", run.Id, Save(run, (1, ManualStepOutcome.Pass, null)), "anna", default);
        (await change.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-final");
        (await Runs(Context()).GetAsync("library", run.Id, default)).Outcome.Should().Be(ManualRunOutcome.Failed);
    }

    [Fact]
    public void The_rollup_rule_is_fail_then_blocked_then_not_run_then_passed_and_undecided_stays_undecided()
    {
        static List<ManualRunStep> S(params ManualStepOutcome?[] o) => [.. o.Select((x, i) => new ManualRunStep { Number = i + 1, Action = "a", Outcome = x })];
        ManualRunRules.Rollup(S(ManualStepOutcome.Pass, ManualStepOutcome.Pass)).Should().Be(ManualRunOutcome.Passed);
        ManualRunRules.Rollup(S(ManualStepOutcome.Pass, ManualStepOutcome.Fail, ManualStepOutcome.Blocked)).Should().Be(ManualRunOutcome.Failed);
        ManualRunRules.Rollup(S(ManualStepOutcome.Pass, ManualStepOutcome.Blocked, ManualStepOutcome.NotRun)).Should().Be(ManualRunOutcome.Blocked);
        ManualRunRules.Rollup(S(ManualStepOutcome.Pass, ManualStepOutcome.NotRun)).Should().Be(ManualRunOutcome.NotRun);
        ManualRunRules.Rollup(S(ManualStepOutcome.Pass, null)).Should().Be(ManualRunOutcome.NotDecided);
        ManualRunRules.Rollup([]).Should().Be(ManualRunOutcome.NotDecided);
        ManualRunRules.Executed(ManualRunState.Completed, ManualRunOutcome.Blocked).Should().BeFalse("blocked is not an execution of the case");
        ManualRunRules.Executed(ManualRunState.Abandoned, ManualRunOutcome.Failed).Should().BeFalse();
        ManualRunRules.Executed(ManualRunState.Completed, ManualRunOutcome.Failed).Should().BeTrue();
    }

    [Fact]
    public async Task Evidence_is_a_validated_reference_and_defects_name_only_a_work_item_number_or_an_https_link()
    {
        await using var db = Context();
        var (plan, scenario) = await PlanAsync(db);
        var runs = Runs(db);
        var run = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default);

        foreach (var bad in new[] { "javascript:alert(1)", "file:///C:/secret.txt", "data:text/html,x", "screenshots/run.png" })
        {
            var save = () => runs.SaveAsync("library", run.Id, Save(run) with { Evidence = [new() { Kind = ManualEvidenceKind.Link, Label = "Shot", Reference = bad }] }, "anna", default);
            (await save.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("evidence-link-invalid", bad);
        }
        var unknownStep = () => runs.SaveAsync("library", run.Id, Save(run) with { Evidence = [new() { Kind = ManualEvidenceKind.Note, Label = "x", Reference = "y", Step = 9 }] }, "anna", default);
        (await unknownStep.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("step-unknown");
        var badDefect = () => runs.SaveAsync("library", run.Id, Save(run) with { Defects = [new() { Title = "Crash", ExternalReference = "javascript:x" }] }, "anna", default);
        (await badDefect.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("defect-reference-invalid");
        var badSeverity = () => runs.SaveAsync("library", run.Id, Save(run) with { Defects = [new() { Title = "Crash", Severity = "Blocker!" }] }, "anna", default);
        (await badSeverity.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("defect-severity-invalid");

        run = await runs.SaveAsync("library", run.Id, Save(run, (1, ManualStepOutcome.Fail, "Error")) with
        {
            Evidence = [new() { Kind = ManualEvidenceKind.Link, Label = "Screenshot", Reference = "https://share.example/run-1.png", Step = 1 },
                new() { Kind = ManualEvidenceKind.Note, Label = "Log", Reference = "Team share: qa/logs/run-1.txt" }],
            Defects = [new() { Title = "Reservation fails", Severity = "High", Step = 1, ExternalReference = "4711", Actual = "Error" }],
        }, "anna", default);
        run.Evidence.Should().HaveCount(2).And.OnlyContain(e => e.Id.StartsWith("ev-") && e.AddedBy == "anna");
        run.Defects.Single().Should().Match<ManualRunDefect>(d => d.Id.StartsWith("df-") && d.RecordedBy == "anna" && d.Status == ManualDefectStatus.Open && d.ExternalReference == "4711");

        // Saved again by someone else: who added what is kept; closing a defect keeps its identity.
        var closed = run.Defects.Single() with { Status = ManualDefectStatus.Closed };
        run = await runs.SaveAsync("library", run.Id, Save(run) with { Steps = [], Defects = [closed] }, "bob", default);
        run.Evidence.Should().OnlyContain(e => e.AddedBy == "anna");
        run.Defects.Single().Should().Match<ManualRunDefect>(d => d.Id == closed.Id && d.RecordedBy == "anna" && d.Status == ManualDefectStatus.Closed);
        run.Steps.Single().Outcome.Should().Be(ManualStepOutcome.Fail, "a save that names no step leaves the step results as they are");
    }

    [Fact]
    public async Task A_run_is_never_read_or_changed_through_another_project_and_cases_that_cannot_run_are_refused()
    {
        await using var db = Context();
        var (plan, scenario) = await PlanAsync(db);
        var runs = Runs(db);
        var foreign = () => runs.StartAsync(new StartManualRunRequest("other-project", plan.Id, scenario.Source.ScenarioId, null), "x", default);
        (await foreign.Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(404);
        var run = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default);
        var read = () => runs.GetAsync("other-project", run.Id, default);
        (await read.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-not-found");
        var write = () => runs.SaveAsync("other-project", run.Id, Save(run, (1, ManualStepOutcome.Pass, null)), "x", default);
        (await write.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-not-found");
        (await runs.ListAsync("other-project", null, null, default)).Should().BeEmpty();
        (await runs.ListAsync("library", plan.Id, null, default)).Should().ContainSingle();

        var manual = await Plans(db).CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "library", ManualCaseTitle = "Log in" }, "tester", default);
        var noSteps = () => runs.StartAsync(new StartManualRunRequest("library", manual.Id, manual.Cases.Single().Source.ScenarioId, null), "anna", default);
        (await noSteps.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("case-has-no-steps");
        var requirementCase = plan.Cases.First(c => c.Source.ScenarioId != scenario.Source.ScenarioId);
        plan = await Plans(db).ReviewAsync(plan.Id, new ReviewTestCaseRequest((await Plans(db).DraftAsync(plan.Id, default)).Revision, requirementCase.Source.ScenarioId, TestCaseReviewState.Excluded, "Out of scope"), "lead", default);
        var excluded = () => runs.StartAsync(new StartManualRunRequest("library", plan.Id, requirementCase.Source.ScenarioId, null), "anna", default);
        (await excluded.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("case-not-runnable");
    }

    [Fact]
    public async Task Test_Coverage_traceability_counts_only_completed_runs_as_executions_and_says_when_a_run_was_of_an_earlier_revision()
    {
        await using var db = Context();
        var (plan, scenario) = await PlanAsync(db);
        var runs = Runs(db);
        var trace = async () => await Plans(Context()).PreparedCaseTraceAsync("library", [Spec], ["FR-001", "FR-002"], default);

        var before = await trace();
        before.ExecutedCases.Should().Be(0);
        before.CaseList.Should().OnlyContain(c => c.Execution == PreparedCaseTraceView.NotExecuted);

        var run = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, "QA"), "anna", default);
        (await trace()).CaseList.Single(c => c.CaseId == scenario.Source.ScenarioId).Should().Match<PreparedCaseTrace>(c => c.RunInProgress && !c.Executed && c.Execution.StartsWith("Run in progress"));

        run = await runs.SaveAsync("library", run.Id, Save(run, (1, ManualStepOutcome.Pass, "Reserved")), "anna", default);
        await runs.CompleteAsync("library", run.Id, new CompleteManualRunRequest(run.Revision), "anna", default);
        var after = await trace();
        var traced = after.CaseList.Single(c => c.CaseId == scenario.Source.ScenarioId);
        traced.Should().Match<PreparedCaseTrace>(c => c.Executed && c.Passed && c.LatestRunCurrent && c.Execution.StartsWith("Passed — manual run"));
        after.ExecutedCases.Should().Be(1);
        after.PassedCases.Should().Be(1);
        var fr1 = after.Requirements.Single(r => r.RequirementId == "FR-001");
        fr1.Should().Match<PreparedRequirementTrace>(r => r.ExecutedCases == 1 && r.PassedCases == 1 && r.Execution.StartsWith("Executed: 1 case"));
        after.RequirementsExecuted.Should().Be(1);
        after.CaseList.Single(c => c.CaseId != scenario.Source.ScenarioId).Execution.Should().Be(PreparedCaseTraceView.NotExecuted, "a run of one case never executes another");

        // An abandoned later run does not replace the completed result.
        var second = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "bob", default);
        await runs.AbandonAsync("library", second.Id, new AbandonManualRunRequest(second.Revision, "Environment down"), "bob", default);
        (await trace()).CaseList.Single(c => c.CaseId == scenario.Source.ScenarioId).Passed.Should().BeTrue();
        var noReason = () => runs.AbandonAsync("library", second.Id, new AbandonManualRunRequest(second.Revision, " "), "bob", default);
        (await noReason.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("reason-required");
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
