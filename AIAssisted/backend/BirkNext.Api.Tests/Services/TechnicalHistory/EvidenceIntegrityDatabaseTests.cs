using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.TechnicalHistory;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.TechnicalHistory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.TechnicalHistory;

/// <summary>
/// Evidence integrity enforced by PostgreSQL itself (fresh migrated database): a pinned composite run record cannot be rewritten; stored
/// specialist reviews (Frontend Quality Review, safe fuzzing, authorization checks) are append-only, idempotent, survive a restart, are isolated
/// per project and never hold a credential; an evidence file can only be withdrawn (content removed, record kept), never altered.
/// </summary>
public sealed class EvidenceIntegrityDatabaseTests : IAsyncLifetime
{
    private const string Project = "import:lendo", Env = "env-qa";
    private readonly string _database = TestPostgres.TemporaryName("birknext_evid");
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

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task A_pinned_composite_run_record_cannot_be_rewritten_by_anyone()
    {
        var runId = Guid.NewGuid();
        await using (var db = Context())
        {
            db.IntegrationSuiteRuns.Add(new IntegrationSuiteRunRecord
            {
                Id = Guid.NewGuid(), SuiteKey = "skole:project:abc", SuiteId = Guid.NewGuid(), RunId = runId, Kind = "composite-e2e", StartedAt = DateTimeOffset.UtcNow,
                Initiator = "Tester", PlanJson = "{}", DefinitionJson = """{"suiteRevision":1}""",
            });
            await db.SaveChangesAsync();
        }
        await using var check = Context();
        var rewrite = () => check.Database.ExecuteSqlRawAsync("UPDATE integration_suite_runs SET \"DefinitionJson\" = '{{\"suiteRevision\":2}}' WHERE \"RunId\" = {0}", runId);
        await rewrite.Should().ThrowAsync<PostgresException>().WithMessage("*pinned historical run*");
        var relabel = () => check.Database.ExecuteSqlRawAsync("UPDATE integration_suite_runs SET \"Kind\" = 'composite-retest' WHERE \"RunId\" = {0}", runId);
        await relabel.Should().ThrowAsync<PostgresException>();
        var duplicate = async () =>
        {
            await using var db = Context();
            db.IntegrationSuiteRuns.Add(new IntegrationSuiteRunRecord { Id = Guid.NewGuid(), SuiteKey = "x", SuiteId = Guid.NewGuid(), RunId = runId, Kind = "contract-tests", Initiator = "T" });
            await db.SaveChangesAsync();
        };
        await duplicate.Should().ThrowAsync<DbUpdateException>("one execution is recorded once");
    }

    [Fact]
    public async Task Specialist_reviews_are_stored_once_survive_a_restart_stay_in_their_project_and_never_hold_a_credential()
    {
        var at = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var report = Json("""{"environmentId":"env-qa","cases":[{"path":"/loans","body":"{\"note\":\"synthetic\"}","headers":[{"name":"X-Trace","value":"1"}]}],"authorization":"[redacted]"}""");
        var request = new StoreTechnicalReviewRunRequest(TechnicalReviewKinds.SafeFuzzing, Project, Env, "QA", "https://qa.example.test", null, at, "2 problems", 12, 2, "Safe fuzzing: 12 cases", report);
        var first = (await new TechnicalHistoryService(Context()).StoreReviewRunAsync(request, default)).Value!;
        var again = (await new TechnicalHistoryService(Context()).StoreReviewRunAsync(request, default)).Value!;
        again.Id.Should().Be(first.Id, "the same run is stored once");
        first.ReportSha256.Should().HaveLength(64);

        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Task.Run(() => new TechnicalHistoryService(Context()).StoreReviewRunAsync(request with { GeneratedAt = at.AddMinutes(10) }, default))));
        var runs = await new TechnicalHistoryService(Context()).ReviewRunsAsync(TechnicalReviewKinds.SafeFuzzing, Env, Project, 50, default);
        runs.Should().HaveCount(2, "concurrent saves of one run are one record");

        (await new TechnicalHistoryService(Context()).ReviewRunReportAsync(first.Id, Project, default)).Should().Contain("synthetic");
        (await new TechnicalHistoryService(Context()).ReviewRunReportAsync(first.Id, "import:other", default)).Should().BeNull("another project's run is not found");
        (await new TechnicalHistoryService(Context()).ReviewRunsAsync(null, null, "import:other", 50, default)).Should().BeEmpty();

        var withToken = request with { GeneratedAt = at.AddHours(1), Report = Json("""{"identities":[{"alias":"admin","accessToken":"eyJhbGciOi.secret"}]}""") };
        var refused = await new TechnicalHistoryService(Context()).StoreReviewRunAsync(withToken, default);
        refused.Status.Should().Be(400);
        refused.Error.Should().Contain("$.identities[0].accessToken").And.NotContain("eyJ");
        (await new TechnicalHistoryService(Context()).StoreReviewRunAsync(request with { Kind = "made-up" }, default)).Status.Should().Be(400);
        (await new TechnicalHistoryService(Context()).StoreReviewRunAsync(request with { Report = Json("[1]") }, default)).Status.Should().Be(400);

        await using var db = Context();
        var rewrite = () => db.Database.ExecuteSqlRawAsync("UPDATE technical_review_runs SET \"Outcome\" = 'Passed'");
        await rewrite.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
    }
}
