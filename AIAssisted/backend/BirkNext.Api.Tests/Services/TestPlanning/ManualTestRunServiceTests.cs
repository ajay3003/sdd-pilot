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
    public async Task A_run_started_after_a_failed_run_is_a_retest_of_it_and_carries_its_specification_origin()
    {
        await using var db = Context();
        var (plan, scenario) = await PlanAsync(db);
        var runs = Runs(db);
        var first = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default);
        first.RetestOf.Should().BeNull("the first run retests nothing");
        first.Origin.Should().NotBeNull("every new run pins where its case came from");
        first.Origin!.Requirements.Select(r => r.RequirementId).Should().Equal(first.RequirementIds);
        first = await runs.SaveAsync("library", first.Id, Save(first, (1, ManualStepOutcome.Fail, "Error")), "anna", default);
        first = await runs.CompleteAsync("library", first.Id, new CompleteManualRunRequest(first.Revision), "anna", default);

        var retest = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "bob", default);
        retest.RetestOf.Should().Be(first.Id, "the case's latest completed run failed");
        ManualRunRules.Summary(retest).RetestOf.Should().Be(first.Id);
        retest = await runs.SaveAsync("library", retest.Id, Save(retest, (1, ManualStepOutcome.Pass, null)), "bob", default);
        await runs.CompleteAsync("library", retest.Id, new CompleteManualRunRequest(retest.Revision), "bob", default);

        (await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default)).RetestOf
            .Should().BeNull("after a passing run there is nothing to retest");
    }

    // ── Plan version history ──────────────────────────────────────────────────────────────────────────────────────────

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

    // ── Defect lifecycle after completion ───────────────────────────────────────────────────────────────────────────

    private async Task<(TestPlanDraft Plan, TestCaseDraft Scenario, ManualTestRun Run)> FailedRunWithDefectAsync(AppDbContext db)
    {
        var (plan, scenario) = await PlanAsync(db);
        var runs = Runs(db);
        var run = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default);
        run = await runs.SaveAsync("library", run.Id, Save(run, (1, ManualStepOutcome.Fail, "Error 500")) with
        {
            Defects = [new ManualRunDefect { Title = "Reservation fails with 500", Severity = "High", Status = ManualDefectStatus.Open, Step = 1 }],
            Evidence = [new ManualRunEvidence { Kind = ManualEvidenceKind.Link, Label = "Screenshot", Reference = "https://share.example.test/s1.png" }],
        }, "anna", default);
        run = await runs.CompleteAsync("library", run.Id, new CompleteManualRunRequest(run.Revision), "anna", default);
        return (plan, scenario, run);
    }

    [Fact]
    public async Task A_defect_is_followed_up_after_completion_with_history_while_results_and_evidence_stay_as_recorded()
    {
        await using var db = Context();
        var (plan, scenario, run) = await FailedRunWithDefectAsync(db);
        var defect = run.Defects.Single();
        var runs = Runs(Context());

        var fixedRun = await runs.UpdateDefectAsync("library", run.Id, defect.Id, new UpdateManualDefectRequest(run.Revision, ManualDefectStatus.ReadyForRetest, "Fixed in build 1.4", "4711", null), "dev", default);
        fixedRun.Defects.Single().Should().Match<ManualRunDefect>(d => d.Status == ManualDefectStatus.ReadyForRetest && d.ExternalReference == "4711");
        fixedRun.Defects.Single().History!.Single().Should().Match<ManualDefectEvent>(e => e.From == ManualDefectStatus.Open && e.To == ManualDefectStatus.ReadyForRetest
            && e.By == "dev" && e.Note == "Fixed in build 1.4" && e.ExternalReference == "4711");
        fixedRun.Steps.Should().BeEquivalentTo(run.Steps, "a follow-up never changes the recorded results");
        (fixedRun.State, fixedRun.Outcome, fixedRun.CompletedAt).Should().Be((run.State, run.Outcome, run.CompletedAt));
        fixedRun.Evidence.Should().BeEquivalentTo(run.Evidence, "the original evidence is kept as recorded");

        // Retest: a new run of the same case verifies the fix; the defect is closed with that run as its proof.
        var retest = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default);
        retest.RetestOf.Should().Be(run.Id);
        var inProgress = () => Runs(Context()).UpdateDefectAsync("library", retest.Id, defect.Id, new UpdateManualDefectRequest(retest.Revision, ManualDefectStatus.Closed, null, null, null), "anna", default);
        (await inProgress.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-in-progress");
        retest = await runs.SaveAsync("library", retest.Id, Save(retest, (1, ManualStepOutcome.Pass, null)), "anna", default);
        retest = await runs.CompleteAsync("library", retest.Id, new CompleteManualRunRequest(retest.Revision), "anna", default);
        var closed = await Runs(Context()).UpdateDefectAsync("library", run.Id, defect.Id, new UpdateManualDefectRequest(fixedRun.Revision, ManualDefectStatus.Closed, "Retest passed", null, retest.Id), "anna", default);
        closed.Defects.Single().History!.Select(e => e.To).Should().Equal(ManualDefectStatus.ReadyForRetest, ManualDefectStatus.Closed);
        closed.Defects.Single().History![1].RetestRunId.Should().Be(retest.Id);
        ManualRunRules.Summary(closed).OpenDefects.Should().Be(0);
        // The database itself still refuses a rewrite of the recorded results, also with a correct revision step (only follow-up is allowed).
        await using (var raw = Context())
        {
            var rewrite = () => raw.Database.ExecuteSqlRawAsync(
                "UPDATE manual_test_runs SET \"ContentJson\" = replace(\"ContentJson\", 'Error 500', 'All fine'), \"Revision\" = \"Revision\" + 1 WHERE \"Id\" = {0}", run.Id);
            await rewrite.Should().ThrowAsync<Exception>("step results of a completed run are final in the database");
        }

        var stale = () => Runs(Context()).UpdateDefectAsync("library", run.Id, defect.Id, new UpdateManualDefectRequest(fixedRun.Revision, ManualDefectStatus.Open, "reopen", null, null), "anna", default);
        (await stale.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-changed");
        var unchanged = () => Runs(Context()).UpdateDefectAsync("library", run.Id, defect.Id, new UpdateManualDefectRequest(closed.Revision, ManualDefectStatus.Closed, null, null, null), "anna", default);
        (await unchanged.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("defect-unchanged");
        var foreignRetest = () => Runs(Context()).UpdateDefectAsync("library", run.Id, defect.Id, new UpdateManualDefectRequest(closed.Revision, ManualDefectStatus.Open, "x", null, Guid.NewGuid()), "anna", default);
        (await foreignRetest.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("retest-run-unknown");

        var list = await Runs(Context()).DefectsAsync("library", default);
        list.Should().ContainSingle().Which.Should().Match<ManualDefectListItem>(i => i.RunId == run.Id && i.Defect.Status == ManualDefectStatus.Closed && i.RunTitle == run.Title);
        (await Runs(Context()).DefectsAsync("another-project", default)).Should().BeEmpty("never another project's defects");
    }

    // ── Attachments ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    [Fact]
    public async Task Files_are_attached_while_running_and_after_completion_typed_and_checked_and_never_cross_projects()
    {
        await using var db = Context();
        var (plan, scenario) = await PlanAsync(db);
        var runs = Runs(db);
        var run = await runs.StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default);

        run = await runs.AttachAsync("library", run.Id, run.Revision, 1, "Error dialog", "C:\\Users\\anna\\error.png", Png, "anna", default);
        var file = run.Evidence.Single();
        file.Should().Match<ManualRunEvidence>(e => e.Kind == ManualEvidenceKind.Attachment && e.FileName == "error.png" && e.ContentType == "image/png" && e.Size == Png.Length
            && e.Step == 1 && e.Label == "Error dialog" && !e.AddedAfterCompletion);
        var stored = await Runs(Context()).AttachmentAsync("library", run.Id, Guid.Parse(file.Reference), default);
        stored.Content.Should().Equal(Png);
        stored.FileName.Should().Be("error.png", "a path from the browser is reduced to the file name");

        async Task Refused(string name, byte[] content, string code)
        {
            var current = await Runs(Context()).GetAsync("library", run.Id, default);
            var attach = () => Runs(Context()).AttachAsync("library", run.Id, current.Revision, null, null, name, content, "anna", default);
            (await attach.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be(code, name);
        }
        await Refused("page.html", "<script>alert(1)</script>"u8.ToArray(), "attachment-type-refused");
        await Refused("drawing.svg", "<svg/>"u8.ToArray(), "attachment-type-refused");
        await Refused("fake.png", "not a png"u8.ToArray(), "attachment-content-mismatch");
        await Refused("binary.txt", [0x41, 0x00, 0x42], "attachment-content-mismatch");
        await Refused("empty.log", [], "attachment-empty");
        await Refused("huge.pdf", [.. "%PDF"u8, .. new byte[ManualTestRunService.MaxAttachmentBytes]], "attachment-too-large");

        // A save keeps the attachment and may relabel it, but cannot invent one or point it at another file.
        var forged = () => Runs(Context()).SaveAsync("library", run.Id, Save(run) with
            { Evidence = [.. run.Evidence, new ManualRunEvidence { Kind = ManualEvidenceKind.Attachment, Label = "x", Reference = Guid.NewGuid().ToString("N") }] }, "anna", default);
        (await forged.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("attachment-unknown");
        run = await Runs(Context()).SaveAsync("library", run.Id, Save(run, (1, ManualStepOutcome.Pass, null)) with
            { Evidence = [file with { Label = "Error dialog (renamed)", Reference = "elsewhere" }] }, "anna", default);
        run.Evidence.Single().Should().Match<ManualRunEvidence>(e => e.Label == "Error dialog (renamed)" && e.Reference == file.Reference);

        run = await Runs(Context()).CompleteAsync("library", run.Id, new CompleteManualRunRequest(run.Revision), "anna", default);
        run = await Runs(Context()).AttachAsync("library", run.Id, run.Revision, null, "Log after the fix", "server.log", "ok"u8.ToArray(), "anna", default);
        run.Evidence.Should().HaveCount(2);
        run.Evidence[1].AddedAfterCompletion.Should().BeTrue("follow-up evidence is marked, the original stays first");
        run.Outcome.Should().Be(ManualRunOutcome.Passed);

        var other = () => Runs(Context()).AttachmentAsync("another-project", run.Id, Guid.Parse(file.Reference), default);
        (await other.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("attachment-not-found");
    }

    [Fact]
    public async Task A_file_is_not_attached_to_a_stopped_run_or_from_a_stale_revision()
    {
        await using var db = Context();
        var (plan, scenario) = await PlanAsync(db);
        var run = await Runs(db).StartAsync(new StartManualRunRequest("library", plan.Id, scenario.Source.ScenarioId, null), "anna", default);

        var stale = () => Runs(Context()).AttachAsync("library", run.Id, run.Revision - 1, null, null, "shot.png", Png, "anna", default);
        (await stale.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-changed", "a tab that missed a save must reload first");

        run = await Runs(Context()).AbandonAsync("library", run.Id, new AbandonManualRunRequest(run.Revision, "Environment down"), "anna", default);
        var stopped = () => Runs(Context()).AttachAsync("library", run.Id, run.Revision, null, null, "shot.png", Png, "anna", default);
        (await stopped.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-final");
        (await Context().ManualRunAttachments.CountAsync(a => a.RunId == run.Id)).Should().Be(0, "nothing is stored for a refused file");
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

    // 2026-11-25: execution in Requirements Traceability / Test Coverage is not restored with optional execution (they show test design only;
    // see docs/manual-test-execution-plan-2026-11-25.md). The test of that display was removed with it.
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
