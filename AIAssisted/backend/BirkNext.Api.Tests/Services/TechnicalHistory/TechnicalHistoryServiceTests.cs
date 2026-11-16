using BirkNext.Api.Data;
using BirkNext.Api.Services.TechnicalHistory;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ApiReview;
using BirkNext.TechnicalHistory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.TechnicalHistory;

/// <summary>
/// Technical run history and finding follow-up on a fresh migrated PostgreSQL database: an API Quality Review run is stored once (idempotent),
/// survives a new context (restart), is listed per environment/project and read back whole; a finding of a stored run is followed up through its
/// lifecycle with an append-only history; "verified resolved" needs a later run of the same environment that reviewed the target and no longer
/// shows the finding; the original run is never changed; projects are isolated; stale changes are refused.
/// </summary>
public sealed class TechnicalHistoryServiceTests : IAsyncLifetime
{
    private const string Project = "import:lendo", Env = "env-qa";
    private readonly string _database = TestPostgres.TemporaryName("birknext_tech");
    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);
    private TechnicalHistoryService History() => new(Context());

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

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static ApiReviewFinding Hsts => new() { Id = "sec-no-hsts:loans", RuleId = "sec-no-hsts", TargetId = "loans", Endpoint = "/loans", Title = "No HSTS header", Severity = ApiReviewSeverity.High };

    private static ApiReviewReport Report(DateTimeOffset at, bool withFinding, ApiReviewTargetStatus targetStatus = ApiReviewTargetStatus.Completed) => new()
    {
        Environment = new() { Name = "QA" }, StartedAt = at.AddMinutes(-1), GeneratedAt = at,
        Targets = [new() { Target = new() { TargetId = "loans", ServiceName = "Loans API" }, Status = targetStatus }],
        Findings = withFinding ? [Hsts] : [],
    };

    [Fact]
    public async Task A_run_is_stored_once_survives_a_restart_and_is_read_back_whole_per_environment_and_project()
    {
        var at = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var first = (await History().StoreApiReviewRunAsync(new(Project, Env, Report(at, true)), default)).Value!;
        var again = (await History().StoreApiReviewRunAsync(new(Project, Env, Report(at, true)), default)).Value!;
        again.Id.Should().Be(first.Id, "the same run (environment + generated time) is stored once");
        first.Should().Match<StoredApiReviewRun>(r => r.High == 1 && r.Targets == 1 && r.Completed == 1 && r.EnvironmentName == "QA");

        (await History().ApiReviewRunsAsync(Env, Project, 10, default)).Should().ContainSingle(r => r.Id == first.Id, "a new context reads it (restart)");
        (await History().ApiReviewRunsAsync(Env, "import:other", 10, default)).Should().BeEmpty();
        (await History().ApiReviewReportAsync(first.Id, default))!.Findings.Should().ContainSingle(f => f.RuleId == "sec-no-hsts");
        (await History().StoreApiReviewRunAsync(new(Project, " ", Report(at, true)), default)).Status.Should().Be(400);
    }

    [Fact]
    public async Task A_finding_is_followed_up_and_only_a_later_run_without_it_verifies_the_fix()
    {
        var t0 = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var original = (await History().StoreApiReviewRunAsync(new(Project, Env, Report(t0, true)), default)).Value!;
        var stillThere = (await History().StoreApiReviewRunAsync(new(Project, Env, Report(t0.AddHours(1), true)), default)).Value!;
        var notReviewed = (await History().StoreApiReviewRunAsync(new(Project, Env, Report(t0.AddHours(2), false, ApiReviewTargetStatus.Blocked)), default)).Value!;
        var fixedRun = (await History().StoreApiReviewRunAsync(new(Project, Env, Report(t0.AddHours(3), false)), default)).Value!;
        var otherEnv = (await History().StoreApiReviewRunAsync(new(Project, "env-prod", Report(t0.AddHours(4), false)), default)).Value!;

        var f = (await History().TrackAsync(new(Project, original.Id, Hsts.Id, "Kari"), "u", default)).Value!;
        f.Should().Match<TechnicalFinding>(x => x.Status == TechnicalFindingStatus.New && x.OriginalRunId == original.Id && x.FindingKey == "sec-no-hsts|loans|/loans");
        (await History().TrackAsync(new(Project, stillThere.Id, Hsts.Id, "Kari"), "u", default)).Value!.Id.Should().Be(f.Id, "the same issue in a later run is the same follow-up");
        (await History().TrackAsync(new("import:other", original.Id, Hsts.Id, null), "u", default)).Status.Should().Be(404, "another project's run is not found");

        async Task<TechnicalHistoryService.Result<TechnicalFinding>> Move(TechnicalFinding x, TechnicalFindingStatus to, Guid? retest = null, string? note = null, string? ext = null) =>
            await History().UpdateAsync(Project, x.Id, new(x.Version, to, note, ext, retest, "Kari"), "u", default);

        (await Move(f, TechnicalFindingStatus.VerifiedResolved)).Status.Should().Be(400, "a new finding cannot jump to verified");
        f = (await Move(f, TechnicalFindingStatus.Confirmed, ext: "4711")).Value!;
        f = (await Move(f, TechnicalFindingStatus.ResolvedPendingRetest, note: "Fixed in 2.1")).Value!;
        (await Move(f, TechnicalFindingStatus.VerifiedResolved)).Error.Should().Contain("Link the retest run");
        (await Move(f, TechnicalFindingStatus.VerifiedResolved, stillThere.Id)).Error.Should().Contain("still shows the finding");
        (await Move(f, TechnicalFindingStatus.VerifiedResolved, notReviewed.Id)).Error.Should().Contain("did not review the finding's target", "absence in a blocked run proves nothing");
        (await Move(f, TechnicalFindingStatus.VerifiedResolved, otherEnv.Id)).Error.Should().Contain("another environment");
        (await Move(f, TechnicalFindingStatus.VerifiedResolved, original.Id)).Error.Should().Contain("later run");
        var verified = (await Move(f, TechnicalFindingStatus.VerifiedResolved, fixedRun.Id)).Value!;

        verified.Status.Should().Be(TechnicalFindingStatus.VerifiedResolved);
        verified.ExternalReference.Should().Be("4711");
        verified.History.Select(h => h.To).Should().Equal(TechnicalFindingStatus.New, TechnicalFindingStatus.Confirmed, TechnicalFindingStatus.ResolvedPendingRetest, TechnicalFindingStatus.VerifiedResolved);
        verified.History[^1].Should().Match<TechnicalFindingEvent>(e => e.RetestRunId == fixedRun.Id && e.RetestOutcome == "Absent");
        (await History().ApiReviewReportAsync(original.Id, default))!.Findings.Should().ContainSingle("the original run is never changed by a retest");
        (await History().UpdateAsync(Project, verified.Id, new(f.Version, TechnicalFindingStatus.Reopened, null, null, null, "Ola"), "u", default)).Status.Should().Be(409, "stale version");
        (await History().UpdateAsync("import:other", verified.Id, new(verified.Version, TechnicalFindingStatus.Reopened, null, null, null, "Ola"), "u", default)).Status.Should().Be(404);
        (await History().FindingsAsync(Project, default)).Should().ContainSingle(x => x.Status == TechnicalFindingStatus.VerifiedResolved);
    }
}
