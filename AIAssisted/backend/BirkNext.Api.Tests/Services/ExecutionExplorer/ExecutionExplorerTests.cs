using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.ExecutionExplorer;
using BirkNext.CriticalE2E;
using BirkNext.ExecutionEvidence;
using BirkNext.ExecutionExplorer;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.Parquet;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ExecutionExplorer;

/// <summary>
/// Test Execution &amp; Evidence: the read-only projection over the evidence owners. Every expected value below comes from the seeded owner records
/// (the independent oracle), never from the explorer's own summaries: status normalisation never upgrades, static assessments stay apart, paging and
/// filters are server-side, a project filter never claims unattributed or foreign runs, an unavailable owner never hides the others, details and
/// reports read stored evidence only, comparison refuses unrelated runs.
/// </summary>
public sealed class ExecutionExplorerTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-explorer-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppDbContext _db;
    private readonly CriticalE2EStore _e2e;
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    public ExecutionExplorerTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("explorer-" + Guid.NewGuid()).Options);
        Directory.CreateDirectory(_dir);
        _e2e = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ExecutionExplorerService Service(params IExplorerProvider[] extra) => new(
    [
        new ExecutionEvidenceExplorerProvider(_db, new ExecutionEvidenceStore(_db)), new JourneyExplorerProvider(_db), new BrowserE2EExplorerProvider(_e2e),
        new PerformanceExplorerProvider(_db), new ParquetExplorerProvider(_db), new SecurityReviewExplorerProvider(_db), .. extra,
    ], NullLogger<ExecutionExplorerService>.Instance);

    // ── Seeding helpers (owner records exactly as the owners store them) ─────────────────────────────────────────────────

    private static ExecutionRunCounts Counts(int planned, int passed, int failed, int notAssessed = 0) => new(planned, passed + failed + notAssessed, passed, failed, 0, 0, 0, 0, 0, 0, notAssessed, planned);

    private Guid ExecRun(string name, string env, DateTimeOffset at, ExecutionState state, ExecutionRunCounts counts, string[]? contracts = null, string? chain = null, bool isProtected = false,
        params (string Def, string State, int? Http, long Ms, AssertionOutcome[] Assertions)[] calls)
    {
        var id = Guid.NewGuid();
        var summary = new ExecutionRunSummary
        {
            RunId = id, Name = name, EnvironmentId = env, State = state, StartedAt = at, CompletedAt = at.AddSeconds(2), Counts = counts, ContractRefs = [.. contracts ?? []], ChainRef = chain,
            CallsWithCompleteEvidence = counts.Executed, TargetOrigin = "http://127.0.0.1:5299",
        };
        _db.ExecutionRuns.Add(new ExecutionRunRow { Id = id, EnvironmentId = env, Name = name, State = state.ToString(), StartedAt = at, CompletedAt = at.AddSeconds(2), Protected = isProtected,
            SummaryJson = JsonSerializer.Serialize(summary, ExecutionEvidenceStore.Json) });
        var seq = 0;
        foreach (var c in calls)
        {
            seq++;
            var callId = Guid.NewGuid();
            var record = new ExecutionCallRecord
            {
                CallId = callId, Sequence = seq, Definition = new ExecutionCallDefinition { Name = c.Def, Method = "GET", Path = "/x", TestDataRefs = ["children@v3"], OperationRef = "synthetic-person@v1#Lookup" },
                State = Enum.Parse<ExecutionState>(c.State), StartedAt = at.AddMilliseconds(seq * 100), CompletedAt = at.AddMilliseconds(seq * 100 + c.Ms),
                Attempts = [new ExecutionAttempt { AttemptNumber = 1, StartedAt = at.AddMilliseconds(seq * 100), CompletedAt = at.AddMilliseconds(seq * 100 + c.Ms), Transport = TransportResult.ResponseReceived,
                    Transmissions = 1, Response = new CapturedResponse { StatusCode = c.Http ?? 0, Body = new CapturedBody { Text = "{\"secret\":\"BODY-TEXT-NEVER-IN-REPORT\"}" } } }],
                Assertions = [.. c.Assertions.Select((o, i) => new AssertionResult($"a{i}", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200", "200", o, "explained"))],
            };
            _db.ExecutionCalls.Add(new ExecutionCallRow { Id = callId, RunId = id, Sequence = seq, Name = c.Def, Protocol = "Rest", Method = "GET", State = c.State, HttpStatus = c.Http, DurationMs = c.Ms,
                AttemptCount = 1, DocumentJson = JsonSerializer.Serialize(record, ExecutionEvidenceStore.Json), DefinitionRef = $"contract-test:{c.Def}@r1" });
        }
        _db.SaveChanges();
        return id;
    }

    private void Contract(string key, string? project)
    {
        _db.ApiContracts.Add(new ApiContractRow { Id = Guid.NewGuid(), Key = key, Name = key, Protocol = "OpenApi", ProjectKey = project, CreatedAt = T0, UpdatedAt = T0 });
        _db.SaveChanges();
    }

    private Guid Journey(DateTimeOffset at, JourneyRunState state, params JourneyStepState[] steps)
    {
        var id = Guid.NewGuid();
        var run = new IntegrationJourneyRun
        {
            RunId = id, PackId = "skolenaervaer", JourneyId = "report", JourneyName = "Skolenærvær report", ScenarioId = "s1", ScenarioName = "Synthetic child", EnvironmentId = "env-a",
            StartedAt = at, CompletedAt = at.AddSeconds(30), OverallState = state, StateReason = state.ToString(),
            Steps = [.. steps.Select((s, i) => new JourneyStepResult { StepId = $"step-{i}", Label = $"Step {i}", State = s, Evidence = s == JourneyStepState.Verified ? "observed" : "",
                CapturedAt = s == JourneyStepState.Verified ? at.AddSeconds(i) : null })],
        };
        _db.IntegrationJourneyRuns.Add(new IntegrationJourneyRunRecord { Id = id, EnvironmentId = "env-a", PackId = "skolenaervaer", JourneyId = "report", ScenarioId = "s1", StartedAt = at,
            CompletedAt = at.AddSeconds(30), State = state.ToString(), ResultJson = JsonSerializer.Serialize(run, Web) });
        _db.SaveChanges();
        return id;
    }

    private Guid Performance(DateTimeOffset at, PerformanceRunState state, PerformanceQualityVerdict verdict, string? project = null)
    {
        var run = new PerformanceTestRun { EnvironmentId = "env-a", ProjectId = project, DefinitionId = "load-1", DefinitionVersion = 2, CreatedAt = at, StartedAt = at, FinishedAt = at.AddMinutes(1),
            State = state, Verdict = verdict, DefinitionSnapshot = new PerformanceTestDefinition { Name = "Lookup load" },
            ThresholdResults = [new PerformanceThresholdResult { ThresholdId = "p95", Outcome = verdict == PerformanceQualityVerdict.Pass ? BirkNext.Applicability.CheckOutcome.Pass : BirkNext.Applicability.CheckOutcome.NotAssessed }] };
        _db.PerformanceTestRuns.Add(new PerformanceTestRunRecord { Id = run.RunId, EnvironmentId = "env-a", DefinitionId = "load-1", State = state.ToString(), CreatedAt = at, DocumentJson = JsonSerializer.Serialize(run, Web) });
        _db.SaveChanges();
        return run.RunId;
    }

    private Guid Parquet(DateTimeOffset at, params ParquetRuleOutcome[] outcomes)
    {
        var e = new ParquetInspectionEvidence
        {
            EnvironmentId = "env-a", FileName = "delivery.parquet", Sha256 = new string('c', 64), SchemaFingerprint = "fp", Rows = 10, SavedAt = at, SavedBy = "Local session",
            Association = new ContractAssociation { ProjectKey = "synth.delivery", ContractKey = "bufdata", ContractVersion = "1.0" },
            Validation = new ParquetValidation("Rules", 1, [.. outcomes.Select((o, i) => new ParquetRuleResult($"r{i}", ParquetRuleKind.NotNull, "c", o, "x", "y", "Exact", 10, [], "why") { RequirementIds = ["REQ-7"] })], 10, "Exact"),
        };
        _db.ParquetInspections.Add(new ParquetInspectionRecord { Id = e.Id, EnvironmentId = "env-a", FileName = e.FileName, SavedAt = at, DocumentJson = JsonSerializer.Serialize(e, Web) });
        _db.SaveChanges();
        return e.Id;
    }

    private Guid Security(DateTimeOffset at, string kind, ClassificationOverall overall)
    {
        var r = new ClassificationReviewResult { RunId = Guid.NewGuid(), ReviewKind = kind, EnvironmentId = "env-a", StartedAt = at, CompletedAt = at.AddSeconds(1), Overall = overall };
        _db.SecurityClassificationEvidence.Add(new SecurityClassificationEvidenceRecord { Id = r.RunId, EnvironmentId = "env-a", Kind = "run", CreatedAt = at, Json = JsonSerializer.Serialize(r, Web) });
        _db.SaveChanges();
        return r.RunId;
    }

    // ── Tests ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Status_normalisation_never_upgrades_a_result()
    {
        ExplorerStatus.Execution(ExecutionState.Passed, Counts(3, 0, 0, 3)).Outcome.Should().Be(ExplorerOutcome.NotAssessed, "transport success without an assessed assertion is not a pass");
        ExplorerStatus.Execution(ExecutionState.Passed, Counts(3, 3, 0)).Outcome.Should().Be(ExplorerOutcome.Passed);
        ExplorerStatus.Execution(ExecutionState.Interrupted, Counts(3, 1, 0)).Outcome.Should().Be(ExplorerOutcome.Interrupted);
        ExplorerStatus.Composite(CompositeRunVerdict.SubflowVerified, "").Outcome.Should().Be(ExplorerOutcome.Partial, "a subflow is never a full end-to-end pass");
        ExplorerStatus.Composite(CompositeRunVerdict.Incomplete, "").Outcome.Should().Be(ExplorerOutcome.Partial);
        ExplorerStatus.Composite(CompositeRunVerdict.WrongCorrelation, "").Outcome.Should().Be(ExplorerOutcome.Failed);
        ExplorerStatus.Composite(CompositeRunVerdict.UnknownOutcome, "").Outcome.Should().Be(ExplorerOutcome.UnknownOutcome);
        ExplorerStatus.Performance(PerformanceRunState.Completed, PerformanceQualityVerdict.NotAssessed, null).Outcome.Should().Be(ExplorerOutcome.NotAssessed, "k6 exiting is not a pass");
        ExplorerStatus.Performance(PerformanceRunState.Completed, PerformanceQualityVerdict.Warning, null).Outcome.Should().Be(ExplorerOutcome.Partial);
        ExplorerStatus.BrowserE2E(CriticalE2EStatus.Inconclusive).Should().Be(ExplorerOutcome.NotAssessed);
        ExplorerStatus.Journey(JourneyRunState.NotVerified, "").Should().Be(ExplorerOutcome.NotAssessed);
        ExplorerStatus.Journey(JourneyRunState.Cancelled, JourneyRunStore_Interrupted).Should().Be(ExplorerOutcome.Interrupted);
        ExplorerStatus.Parquet(0, 0, 2).Should().Be(ExplorerOutcome.NotAssessed);
        ExplorerStatus.Parquet(3, 0, 1).Should().Be(ExplorerOutcome.Partial);
        ExplorerStatus.Security(ClassificationOverall.NotTestable).Should().Be(ExplorerOutcome.NotAssessed);
        ExplorerStatus.IsExecution(ExplorerCategory.StaticAssessment).Should().BeFalse();
        ExplorerStatus.IsExecution(ExplorerCategory.DataValidation).Should().BeFalse();
    }

    private const string JourneyRunStore_Interrupted = "Interrupted: the backend stopped before this run finished.";

    [Fact]
    public async Task Every_owner_is_listed_newest_first_with_server_side_paging_and_static_assessments_kept_apart()
    {
        Contract("synthetic-person", "synth.skole");
        var rest = ExecRun("Contract tests · Person", "env-a", T0.AddMinutes(1), ExecutionState.Passed, Counts(2, 2, 0), ["synthetic-person@v1"]);
        var journey = Journey(T0.AddMinutes(2), JourneyRunState.Partial, JourneyStepState.Verified, JourneyStepState.NotVerified);
        var perf = Performance(T0.AddMinutes(3), PerformanceRunState.Completed, PerformanceQualityVerdict.Pass);
        var parquet = Parquet(T0.AddMinutes(4), ParquetRuleOutcome.Pass, ParquetRuleOutcome.Fail);
        var staticReview = Security(T0.AddMinutes(5), ClassificationReviewKinds.Static, ClassificationOverall.Verified);
        var runtimeReview = Security(T0.AddMinutes(6), ClassificationReviewKinds.Runtime, ClassificationOverall.IssueDetected);
        _e2e.Record(new CriticalE2ERunResult { RunId = "e2e-1", FlowId = "flow-1", FlowName = "Login", EnvironmentId = "env-a", StartedAt = T0.AddMinutes(7), Status = CriticalE2EStatus.Inconclusive,
            StepResults = [new CriticalE2EStepResult { StepId = "s1", Description = "Open", Status = CriticalE2EStatus.Passed, StartedAt = T0.AddMinutes(7) }] });

        var svc = Service();
        var all = await svc.ListAsync(new ExplorerQuery { Take = 100 }, default);
        all.Runs.Select(r => r.SourceRunId).Should().Equal("e2e-1", runtimeReview.ToString("D"), parquet.ToString("D"), perf.ToString("D"), journey.ToString("D"), rest.ToString("D"));
        all.Runs.Should().NotContain(r => r.SourceRunId == staticReview.ToString("D"), "a static review is listed only on request");
        all.Runs.Single(r => r.Source == ExplorerSource.ContractTests).ProjectKey.Should().Be("synth.skole", "derived from the recorded contract reference");
        all.Runs.Single(r => r.Source == ExplorerSource.BrowserE2E).Outcome.Should().Be(ExplorerOutcome.NotAssessed);
        all.Runs.Single(r => r.Source == ExplorerSource.ParquetValidation).Should().Match<ExplorerRun>(r => r.Outcome == ExplorerOutcome.Failed && r.Category == ExplorerCategory.DataValidation);
        all.Runs.Single(r => r.Source == ExplorerSource.IntegrationJourney).Should().Match<ExplorerRun>(r => r.Outcome == ExplorerOutcome.Partial && r.Passed == 1 && r.NotAssessed == 1);

        var withStatic = await svc.ListAsync(new ExplorerQuery { IncludeStatic = true, Take = 100 }, default);
        withStatic.Runs.Single(r => r.SourceRunId == staticReview.ToString("D")).Category.Should().Be(ExplorerCategory.StaticAssessment);

        var page2 = await svc.ListAsync(new ExplorerQuery { Skip = 2, Take = 2 }, default);
        page2.Total.Should().Be(6);
        page2.Runs.Select(r => r.SourceRunId).Should().Equal(parquet.ToString("D"), perf.ToString("D"));
        (await svc.ListAsync(new ExplorerQuery { Take = 1000 }, default)).Take.Should().Be(ExecutionExplorerService.MaxTake, "the page size is bounded");

        var failed = await svc.ListAsync(new ExplorerQuery { Outcomes = [ExplorerOutcome.Failed], Take = 100 }, default);
        failed.Runs.Select(r => r.Source).Should().BeEquivalentTo([ExplorerSource.ParquetValidation, ExplorerSource.SecurityReview]);
        (await svc.ListAsync(new ExplorerQuery { Sources = [ExplorerSource.Performance] }, default)).Runs.Should().ContainSingle(r => r.SourceRunId == perf.ToString("D"));
        (await svc.ListAsync(new ExplorerQuery { Search = "Person" }, default)).Runs.Should().ContainSingle(r => r.SourceRunId == rest.ToString("D"));

        var overview = await svc.OverviewAsync(new ExplorerQuery(), default);
        overview.TotalRuns.Should().Be(5, "contract run, journey, performance, runtime review and browser flow are executions; the Parquet validation and the static review are not");
        overview.StaticAssessments.Should().Be(2);
    }

    [Fact]
    public async Task Overview_counts_only_executions_and_tests_actually_assessed()
    {
        ExecRun("A", "env-a", T0, ExecutionState.Passed, Counts(3, 3, 0));
        ExecRun("B", "env-a", T0.AddMinutes(1), ExecutionState.Failed, Counts(4, 1, 2, 1));
        ExecRun("C", "env-a", T0.AddMinutes(2), ExecutionState.Passed, Counts(2, 0, 0, 2));
        Parquet(T0.AddMinutes(3), ParquetRuleOutcome.Pass);
        Security(T0.AddMinutes(4), ClassificationReviewKinds.Static, ClassificationOverall.Verified);
        var o = await Service().OverviewAsync(new ExplorerQuery(), default);
        o.TotalRuns.Should().Be(3);
        (o.PassedRuns, o.FailedRuns, o.NotAssessedRuns).Should().Be((1, 1, 1));
        o.TestsExecuted.Should().Be(3 + 1 + 2, "passed + failed calls only; not-assessed calls are not counted as executed tests");
        o.StaticAssessments.Should().Be(2);
        o.Latest!.Name.Should().Be("C");
        o.Attention.Should().ContainSingle(r => r.Name == "B");
        o.Coverage.Single(c => c.Category == ExplorerCategory.FunctionalTest).Denominator.Should().Be("of 3 contract test run(s)");
        o.Coverage.Single(c => c.Category == ExplorerCategory.FunctionalTest).Missing.Should().Contain("1 not assessed");
    }

    [Fact]
    public async Task A_project_filter_never_claims_unattributed_runs_and_never_shows_another_projects_runs()
    {
        Contract("synthetic-person", "synth.skole");
        Contract("other-api", "other.project");
        Contract("orphan-api", null);
        var mine = ExecRun("Mine", "env-a", T0, ExecutionState.Passed, Counts(1, 1, 0), ["synthetic-person@v1"]);
        var theirs = ExecRun("Theirs", "env-a", T0.AddMinutes(1), ExecutionState.Passed, Counts(1, 1, 0), ["other-api@v2"]);
        var orphan = ExecRun("Orphan", "env-a", T0.AddMinutes(2), ExecutionState.Passed, Counts(1, 1, 0), ["orphan-api@v1"]);
        var mixed = ExecRun("Mixed", "env-a", T0.AddMinutes(3), ExecutionState.Passed, Counts(2, 2, 0), ["synthetic-person@v1", "other-api@v2"]);
        var svc = Service();
        var scoped = await svc.ListAsync(new ExplorerQuery { ProjectKey = "synth.skole" }, default);
        scoped.Runs.Select(r => r.SourceRunId).Should().Equal(mine.ToString("D"));
        var withUnattributed = await svc.ListAsync(new ExplorerQuery { ProjectKey = "synth.skole", IncludeUnattributed = true }, default);
        withUnattributed.Runs.Select(r => r.SourceRunId).Should().BeEquivalentTo([mine.ToString("D"), orphan.ToString("D"), mixed.ToString("D")]);
        withUnattributed.Runs.Should().NotContain(r => r.SourceRunId == theirs.ToString("D"), "another project's run is never shown, even with unattributed runs");
        withUnattributed.Runs.Where(r => r.SourceRunId != mine.ToString("D")).Should().OnlyContain(r => r.ProjectKey == null, "never attributed by guess");
        (await svc.ListAsync(new ExplorerQuery { EnvironmentId = "env-b" }, default)).Total.Should().Be(0);
    }

    private sealed class BrokenProvider : IExplorerProvider
    {
        public IReadOnlyList<ExplorerSource> Sources { get; } = [ExplorerSource.Performance];
        public Task<List<ExplorerRun>> RecentAsync(ExplorerQuery query, int window, CancellationToken ct) => throw new InvalidOperationException("store offline");
        public Task<ExplorerRunDetail?> DetailAsync(ExplorerSource source, string id, CancellationToken ct) => throw new InvalidOperationException("store offline");
    }

    [Fact]
    public async Task One_unavailable_owner_is_reported_and_never_hides_the_others()
    {
        ExecRun("Still here", "env-a", T0, ExecutionState.Failed, Counts(1, 0, 1));
        var svc = new ExecutionExplorerService([new ExecutionEvidenceExplorerProvider(_db, new ExecutionEvidenceStore(_db)), new BrokenProvider()], NullLogger<ExecutionExplorerService>.Instance);
        var page = await svc.ListAsync(new ExplorerQuery(), default);
        page.Runs.Should().ContainSingle(r => r.Name == "Still here");
        page.Providers.Should().Contain(p => p.Source == ExplorerSource.Performance && p.State == ProviderState.Unavailable);
        page.Limitations.Should().Contain(l => l.Contains("could not be read"));
        var o = await svc.OverviewAsync(new ExplorerQuery(), default);
        o.FailedRuns.Should().Be(1, "a failing owner never turns into false zeros for the others");
    }

    [Fact]
    public async Task Details_and_reports_read_stored_evidence_only_with_recorded_timeline_and_traceability()
    {
        Contract("synthetic-person", "synth.skole");
        var id = ExecRun("Contract tests · Person", "env-a", T0, ExecutionState.Failed, Counts(2, 1, 1), ["synthetic-person@v1"], null, false,
            ("lookup", "Passed", 200, 40, [AssertionOutcome.Passed]), ("lookup-404", "Failed", 500, 60, [AssertionOutcome.Failed]));
        var svc = Service();
        var d = (await svc.DetailAsync(ExplorerSource.ContractTests, id.ToString("D"), default))!;
        d.ExecutionRunId.Should().Be(id);
        d.Items.Select(i => i.Outcome).Should().Equal("Passed", "Failed");
        d.Timeline.Select(t => t.At).Should().BeInAscendingOrder();
        d.Timeline.Should().OnlyContain(t => t.Correlation.StartsWith("X-BirkNext-Execution-Id " + id.ToString("N")));
        d.Traceability.Should().Contain(t => t.Kind == "Contract version" && t.Reference == "synthetic-person@v1");
        d.Traceability.Should().Contain(t => t.Kind == "Test Data version" && t.Reference == "children@v3");
        (await svc.DetailAsync(ExplorerSource.Performance, id.ToString("D"), default)).Should().BeNull("a reference is resolved by its owner only");
        (await svc.DetailAsync(ExplorerSource.ContractChain, id.ToString("D"), default)).Should().BeNull("the source must match the run's real kind");

        var journey = Journey(T0, JourneyRunState.Partial, JourneyStepState.Verified, JourneyStepState.NotVerified);
        var jd = (await svc.DetailAsync(ExplorerSource.IntegrationJourney, journey.ToString("D"), default))!;
        jd.Timeline.Select(t => t.At is null).Should().Equal([false, true], "an unobserved step keeps no time — none is invented");

        var report = (await svc.ReportAsync(ExplorerSource.ContractTests, id.ToString("D"), default))!;
        report.FileName.Should().MatchRegex("^execution-report-contracttests-[0-9a-f]{8}\\.md$");
        report.Markdown.Should().Contain("**FAILED**").And.Contain("synthetic-person@v1").And.Contain("Nothing was re-executed");
        report.Markdown.Should().NotContain("BODY-TEXT-NEVER-IN-REPORT", "bodies are never part of a report");
        ExecutionExplorerService.Clean("a|b\nc<script>`x`").Should().Be("a/b c‹script›'x'");
        _db.ExecutionRuns.Count().Should().Be(1, "reading added nothing");
    }

    [Fact]
    public async Task Comparison_detects_regressions_and_refuses_unrelated_runs()
    {
        var before = ExecRun("Chain", "env-a", T0, ExecutionState.Passed, Counts(3, 3, 0), null, "contract-chain:abc@r1", false,
            ("create", "Passed", 201, 40, [AssertionOutcome.Passed]), ("read", "Passed", 200, 50, [AssertionOutcome.Passed, AssertionOutcome.Passed]), ("lock", "Failed", 409, 30, [AssertionOutcome.Failed]));
        var after = ExecRun("Chain", "env-a", T0.AddHours(1), ExecutionState.Failed, Counts(3, 1, 2), null, "contract-chain:abc@r2", false,
            ("create", "Failed", 500, 40, [AssertionOutcome.Failed]), ("read", "Passed", 200, 900, [AssertionOutcome.Passed, AssertionOutcome.Passed]), ("lock", "Passed", 200, 30, [AssertionOutcome.Passed]));
        var elsewhere = ExecRun("Chain", "env-b", T0.AddHours(2), ExecutionState.Passed, Counts(3, 3, 0), null, "contract-chain:abc@r2", false, ("create", "Passed", 201, 40, [AssertionOutcome.Passed]));
        var svc = Service();
        var cmp = await svc.CompareAsync(ExplorerSource.ContractChain, before.ToString("D"), after.ToString("D"), default);
        cmp.Comparable.Should().BeTrue(string.Join(" ", cmp.Reasons));
        cmp.Rows.Single(r => r.Name == "create").Change.Should().Be(ComparisonChange.NewFailure);
        cmp.Rows.Single(r => r.Name == "lock").Change.Should().Be(ComparisonChange.ResolvedFailure);
        cmp.Rows.Single(r => r.Name == "read").Change.Should().Be(ComparisonChange.LatencyChanged);
        cmp.Limitations.Should().Contain(l => l.Contains("Chain revisions differ"));
        var refused = await svc.CompareAsync(ExplorerSource.ContractChain, before.ToString("D"), elsewhere.ToString("D"), default);
        refused.Comparable.Should().BeFalse();
        refused.Reasons.Should().Contain(r => r.Contains("Target Environments"));
        (await svc.CompareAsync(ExplorerSource.Performance, "x", "y", default)).Comparable.Should().BeFalse();
    }
}
