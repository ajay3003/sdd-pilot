using System.Diagnostics;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Tests.Data;

/// <summary>
/// Manual run persistence on real PostgreSQL (a temporary database per test class, migrated with the production migrations; never a person's
/// database). Audit 2026-11-15 PERF-01 / TEST-01 / SEC-01: the traceability read is bounded by cases, not by history; the run list pages with a
/// stable order and an exact total; concurrent saves at one revision let exactly one through; a completed run is final; another project's run is
/// never read or changed; and one case never has two runs in progress. The expected answers come from the seed data, not from the code under test.
/// </summary>
public sealed class ManualRunPersistencePostgresTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_manual_runs");
    private string Connection => TestPostgres.Temporary(_database).ConnectionString;

    public async Task InitializeAsync()
    {
        await Execute(TestPostgres.Admin().ConnectionString, $"CREATE DATABASE \"{_database}\"");
        await using var db = Db();
        await DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute(TestPostgres.Admin().ConnectionString, $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Connection).Options);
    private ManualTestRunService Service(AppDbContext db) => new(db, null!);

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>One seeded run as the service stores it (the row columns mirror the JSON, as StartAsync/WriteAsync write them).</summary>
    private sealed record Seed(Guid Id, string Project, Guid Draft, string Case, ManualRunState State, ManualRunOutcome Outcome, DateTimeOffset Started, DateTimeOffset? Completed, int Revision = 1);

    private static ManualTestRunRecord Row(Seed s, int padding = 0)
    {
        var run = new ManualTestRun
        {
            Id = s.Id, ProjectKey = s.Project, DraftId = s.Draft, CaseId = s.Case, CaseKey = $"{s.Draft:N}/{s.Case}", PlanName = "Plan", PlanRevision = 1, CaseFingerprint = "fp",
            Title = $"Case {s.Case}", Tester = "anna", StartedAt = s.Started, CompletedAt = s.Completed, State = s.State, Outcome = s.Outcome, Revision = s.Revision,
            Notes = padding > 0 ? new string('x', padding) : null,
            Steps = [new ManualRunStep { Number = 1, Action = "Do it", Expected = "Done", Outcome = s.State == ManualRunState.Completed ? ManualStepOutcome.Pass : null }],
            UpdatedBy = "anna", UpdatedAt = s.Completed ?? s.Started,
        };
        return new ManualTestRunRecord
        {
            Id = s.Id, ProjectKey = s.Project, DraftId = s.Draft, CaseId = s.Case, State = s.State.ToString(), Outcome = s.Outcome.ToString(), Revision = s.Revision,
            ContentJson = JsonSerializer.Serialize(run, E.Json), Tester = "anna", StartedAt = s.Started, CompletedAt = s.Completed, UpdatedAt = s.Completed ?? s.Started,
        };
    }

    private async Task Insert(IEnumerable<Seed> seeds, int padding = 0)
    {
        foreach (var chunk in seeds.Chunk(500))
        {
            await using var db = Db();
            db.ManualTestRuns.AddRange(chunk.Select(s => Row(s, padding)));
            await db.SaveChangesAsync();
        }
    }

    /// <summary>A history: per case several completed runs (outcomes vary), stopped runs, and at most one run in progress; plus another project.</summary>
    private static List<Seed> History(string project, int cases, int runsPerCase, int seed)
    {
        var random = new Random(seed);
        var draft = Guid.NewGuid();
        var list = new List<Seed>();
        for (var c = 0; c < cases; c++)
        {
            var start = T0.AddMinutes(c);
            for (var r = 0; r < runsPerCase; r++)
            {
                var started = start.AddHours(r);
                var kind = random.Next(10);
                list.Add(kind switch
                {
                    < 6 => new(Guid.NewGuid(), project, draft, $"case-{c}", ManualRunState.Completed, random.Next(2) == 0 ? ManualRunOutcome.Passed : ManualRunOutcome.Failed, started, started.AddMinutes(20 + random.Next(60))),
                    < 8 => new(Guid.NewGuid(), project, draft, $"case-{c}", ManualRunState.Abandoned, ManualRunOutcome.NotDecided, started, started.AddMinutes(5)),
                    _ => new(Guid.NewGuid(), project, draft, $"case-{c}", ManualRunState.Completed, ManualRunOutcome.Blocked, started, started.AddMinutes(10)),
                });
            }
            if (random.Next(4) == 0) list.Add(new(Guid.NewGuid(), project, draft, $"case-{c}", ManualRunState.InProgress, ManualRunOutcome.NotDecided, start.AddHours(runsPerCase), null));
        }
        return list;
    }

    // ---- PERF-01: bounded traceability read ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Traceability_reads_the_latest_completed_run_and_in_progress_state_per_case_and_nothing_of_another_project()
    {
        var mine = History("library", cases: 40, runsPerCase: 12, seed: 7);
        var other = History("payroll", cases: 10, runsPerCase: 5, seed: 8);
        await Insert(mine.Concat(other));

        await using var db = Db();
        var bounded = await ManualTestRunService.ReadTraceabilityRunsAsync(db, "library", CancellationToken.None);

        // Oracle from the seed: the latest completed run per case (completion, then start time) and whether a run is in progress.
        var expectedLatest = mine.Where(s => s.State == ManualRunState.Completed).GroupBy(s => s.Case)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Completed).ThenByDescending(s => s.Started).First().Id);
        var expectedInProgress = mine.Where(s => s.State == ManualRunState.InProgress).Select(s => s.Case).ToHashSet();

        bounded.Should().OnlyContain(r => r.ProjectKey == "library", "another project's runs never enter this project's traceability");
        bounded.Where(r => r.State == ManualRunState.Completed).GroupBy(r => r.CaseId).Should().OnlyContain(g => g.Count() == 1, "one completed run per case is enough");
        bounded.Where(r => r.State == ManualRunState.Completed).ToDictionary(r => r.CaseId, r => r.Id).Should().BeEquivalentTo(expectedLatest);
        bounded.Where(r => r.State == ManualRunState.InProgress).Select(r => r.CaseId).Should().BeEquivalentTo(expectedInProgress);
        bounded.Should().NotContain(r => r.State == ManualRunState.Abandoned, "a stopped run never counts as an execution");
        bounded.Count.Should().BeLessThanOrEqualTo(2 * 40, "bounded by cases, not by history (480 runs were stored)");
    }

    [Fact]
    public async Task Traceability_gives_the_same_per_case_answer_as_reading_every_run()
    {
        var mine = History("library", cases: 25, runsPerCase: 9, seed: 21);
        await Insert(mine);
        await using var db = Db();
        var all = await ManualTestRunService.ReadProjectRunsAsync(db, "library", CancellationToken.None);
        var bounded = await ManualTestRunService.ReadTraceabilityRunsAsync(db, "library", CancellationToken.None);

        static Dictionary<string, (Guid? Latest, bool InProgress)> PerCase(IEnumerable<ManualTestRun> runs) => runs.GroupBy(r => r.CaseKey).ToDictionary(g => g.Key, g => (
            g.Where(r => r.State == ManualRunState.Completed).OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefault(),
            g.Any(r => r.State == ManualRunState.InProgress)));
        // Cases with only stopped runs give no outcome and no "in progress" either way; compare the cases that have something to show.
        var expected = PerCase(all).Where(kv => kv.Value.Latest is not null || kv.Value.InProgress).ToDictionary(kv => kv.Key, kv => kv.Value);
        PerCase(bounded).Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task Traceability_read_cost_follows_cases_not_history(int runs)
    {
        const int cases = 100;
        await Insert(History("library", cases, runsPerCase: runs / cases, seed: runs), padding: 1500);

        await using var db = Db();
        // Warm the connection and the query plans once, then measure each read.
        await ManualTestRunService.ReadTraceabilityRunsAsync(db, "library", CancellationToken.None);
        await ManualTestRunService.ReadProjectRunsAsync(db, "library", CancellationToken.None);

        var (fullMs, fullBytes, fullCount) = await Measure(() => ManualTestRunService.ReadProjectRunsAsync(db, "library", CancellationToken.None));
        var (boundedMs, boundedBytes, boundedCount) = await Measure(() => ManualTestRunService.ReadTraceabilityRunsAsync(db, "library", CancellationToken.None));
        output.WriteLine($"PERF-01 {runs} stored runs, {cases} cases: read-every-run {fullMs} ms, {fullBytes / 1024} KiB allocated, {fullCount} runs deserialized; " +
                         $"bounded {boundedMs} ms, {boundedBytes / 1024} KiB allocated, {boundedCount} runs deserialized");
        boundedCount.Should().BeLessThanOrEqualTo(2 * cases);
        fullCount.Should().BeGreaterThan(runs - 1);
        boundedBytes.Should().BeLessThan(fullBytes, "the bounded read deserializes at most two runs per case");
    }

    private static async Task<(long Ms, long Bytes, int Count)> Measure(Func<Task<List<ManualTestRun>>> read)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var result = await read();
        watch.Stop();
        return (watch.ElapsedMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before, result.Count);
    }

    // ---- PERF-01: paged run list ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_run_list_pages_newest_first_with_a_stable_order_and_an_exact_total()
    {
        var draft = Guid.NewGuid();
        // 1,203 runs; groups share a start time so the order needs the id tie-breaker to be stable across pages.
        var seeds = Enumerable.Range(0, 1203).Select(i => new Seed(Guid.NewGuid(), "library", draft, $"case-{i % 7}", ManualRunState.Completed, ManualRunOutcome.Passed,
            T0.AddMinutes(i / 3), T0.AddMinutes(i / 3 + 1))).ToList();
        await Insert(seeds.Concat(History("payroll", 3, 4, 3)));
        var expected = seeds.OrderByDescending(s => s.Started).ThenByDescending(s => s.Id).Select(s => s.Id).ToList();

        var seen = new List<Guid>();
        for (var skip = 0; ; skip += 250)
        {
            await using var db = Db();
            var (page, total) = await Service(db).ListPageAsync("library", null, null, skip, 250, CancellationToken.None);
            total.Should().Be(1203, "the total is exact, never the page size");
            if (page.Count == 0) break;
            seen.AddRange(page.Select(r => r.Id));
        }
        seen.Should().Equal(expected, "every run appears exactly once, newest first, across pages");

        await using (var db = Db())
        {
            var (deep, total) = await Service(db).ListPageAsync("library", null, null, 1200, 250, CancellationToken.None);
            deep.Select(r => r.Id).Should().Equal(expected.Skip(1200));
            (await Service(db).ListPageAsync("library", null, "case-3", 0, 500, CancellationToken.None)).Total.Should().Be(seeds.Count(s => s.Case == "case-3"));
            var compatible = await Service(db).ListAsync("library", null, null, CancellationToken.None);
            compatible.Should().HaveCount(ManualTestRunService.MaxPage, "the unpaged list keeps its documented first page");
            compatible.Select(r => r.Id).Should().Equal(expected.Take(ManualTestRunService.MaxPage));
        }
    }

    [Fact]
    public async Task Paging_arguments_are_bounded()
    {
        await using var db = Db();
        var service = Service(db);
        await service.Invoking(s => s.ListPageAsync("library", null, null, -1, 10, CancellationToken.None)).Should().ThrowAsync<TestPlanException>().Where(e => e.Status == 400);
        await service.Invoking(s => s.ListPageAsync("library", null, null, 0, 0, CancellationToken.None)).Should().ThrowAsync<TestPlanException>().Where(e => e.Status == 400);
        await service.Invoking(s => s.ListPageAsync("library", null, null, 0, ManualTestRunService.MaxPage + 1, CancellationToken.None)).Should().ThrowAsync<TestPlanException>().Where(e => e.Status == 400);
    }

    // ---- TEST-01: concurrency, immutability, isolation on PostgreSQL ------------------------------------------------------------------

    [Fact]
    public async Task Two_saves_at_the_same_revision_let_exactly_one_through()
    {
        var id = Guid.NewGuid();
        await Insert([new Seed(id, "library", Guid.NewGuid(), "case-1", ManualRunState.InProgress, ManualRunOutcome.NotDecided, T0, null)]);
        var save = new SaveManualRunRequest { BaseRevision = 1, Steps = [new ManualStepInput(1, ManualStepOutcome.Pass, "ok")] };

        await using var a = Db();
        await using var b = Db();
        var results = await Task.WhenAll(Attempt(Service(a)), Attempt(Service(b)));
        results.Count(r => r is null).Should().Be(1, "one writer wins");
        results.Single(r => r is not null)!.Status.Should().Be(409);

        await using var check = Db();
        (await check.ManualTestRuns.SingleAsync(r => r.Id == id)).Revision.Should().Be(2, "exactly one save was stored");

        async Task<TestPlanException?> Attempt(ManualTestRunService s)
        {
            try { await s.SaveAsync("library", id, save, "anna", CancellationToken.None); return null; }
            catch (TestPlanException e) { return e; }
        }
    }

    [Fact]
    public async Task A_completed_or_stopped_run_is_final()
    {
        var done = Guid.NewGuid();
        var stopped = Guid.NewGuid();
        await Insert([
            new Seed(done, "library", Guid.NewGuid(), "case-1", ManualRunState.Completed, ManualRunOutcome.Passed, T0, T0.AddMinutes(5), Revision: 3),
            new Seed(stopped, "library", Guid.NewGuid(), "case-2", ManualRunState.Abandoned, ManualRunOutcome.NotDecided, T0, T0.AddMinutes(5), Revision: 2)]);
        await using var db = Db();
        var service = Service(db);
        foreach (var (id, revision) in new[] { (done, 3), (stopped, 2) })
        {
            (await service.Invoking(s => s.SaveAsync("library", id, new SaveManualRunRequest { BaseRevision = revision }, "anna", CancellationToken.None))
                .Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-final");
            (await service.Invoking(s => s.CompleteAsync("library", id, new CompleteManualRunRequest(revision), "anna", CancellationToken.None))
                .Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("run-final");
        }
        await using var check = Db();
        (await check.ManualTestRuns.SingleAsync(r => r.Id == done)).Revision.Should().Be(3);
    }

    [Fact]
    public async Task The_database_refuses_to_rewrite_a_final_run_but_allows_progress_and_reset()
    {
        var done = Guid.NewGuid();
        var open = Guid.NewGuid();
        await Insert([
            new Seed(done, "library", Guid.NewGuid(), "case-1", ManualRunState.Completed, ManualRunOutcome.Failed, T0, T0.AddMinutes(5), Revision: 3),
            new Seed(open, "library", Guid.NewGuid(), "case-2", ManualRunState.InProgress, ManualRunOutcome.NotDecided, T0, null)]);
        await using var db = Db();
        var rewrite = () => db.Database.ExecuteSqlRawAsync("UPDATE manual_test_runs SET \"Outcome\" = 'Passed' WHERE \"Id\" = {0}", done);
        (await rewrite.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("final", "a recorded result is never rewritten, not even outside the service");
        (await db.Database.ExecuteSqlRawAsync("UPDATE manual_test_runs SET \"Revision\" = 2 WHERE \"Id\" = {0}", open)).Should().Be(1, "a run in progress is still saved");
        (await db.Database.ExecuteSqlRawAsync("DELETE FROM manual_test_runs WHERE \"Id\" = {0}", done)).Should().Be(1, "Local Data Reset can still remove it");
    }

    [Fact]
    public async Task Another_projects_run_is_never_read_listed_or_changed()
    {
        var theirs = Guid.NewGuid();
        await Insert([new Seed(theirs, "payroll", Guid.NewGuid(), "case-1", ManualRunState.InProgress, ManualRunOutcome.NotDecided, T0, null)]);
        await using var db = Db();
        var service = Service(db);
        (await service.Invoking(s => s.GetAsync("library", theirs, CancellationToken.None)).Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(404);
        (await service.Invoking(s => s.SaveAsync("library", theirs, new SaveManualRunRequest { BaseRevision = 1, Notes = "changed" }, "anna", CancellationToken.None))
            .Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(404);
        (await service.Invoking(s => s.AbandonAsync("library", theirs, new AbandonManualRunRequest(1, "no"), "anna", CancellationToken.None))
            .Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(404);
        (await service.ListAsync("library", null, null, CancellationToken.None)).Should().BeEmpty();
        (await ManualTestRunService.ReadTraceabilityRunsAsync(db, "library", CancellationToken.None)).Should().BeEmpty();
        await using var check = Db();
        (await check.ManualTestRuns.SingleAsync(r => r.Id == theirs)).Revision.Should().Be(1, "nothing of the other project changed");
    }

    [Fact]
    public async Task One_case_never_has_two_runs_in_progress()
    {
        var draft = Guid.NewGuid();
        await Insert([new Seed(Guid.NewGuid(), "library", draft, "case-1", ManualRunState.InProgress, ManualRunOutcome.NotDecided, T0, null)]);
        var second = () => Insert([new Seed(Guid.NewGuid(), "library", draft, "case-1", ManualRunState.InProgress, ManualRunOutcome.NotDecided, T0.AddMinutes(1), null)]);
        await second.Should().ThrowAsync<DbUpdateException>("the database refuses a second run in progress for the same case");
        // Completed and stopped runs of the case, and an in-progress run of another case or project, are unaffected.
        await Insert([
            new Seed(Guid.NewGuid(), "library", draft, "case-1", ManualRunState.Completed, ManualRunOutcome.Passed, T0.AddMinutes(2), T0.AddMinutes(3)),
            new Seed(Guid.NewGuid(), "library", draft, "case-2", ManualRunState.InProgress, ManualRunOutcome.NotDecided, T0, null),
            new Seed(Guid.NewGuid(), "payroll", draft, "case-1", ManualRunState.InProgress, ManualRunOutcome.NotDecided, T0, null)]);
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
