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

    [Fact]
    public async Task An_evidence_file_can_be_withdrawn_but_never_altered()
    {
        var id = Guid.NewGuid();
        await using (var db = Context())
        {
            db.ManualRunAttachments.Add(new ManualRunAttachmentRecord
            {
                Id = id, RunId = Guid.NewGuid(), ProjectKey = "lendo", FileName = "screen.png", OriginalFileName = "screen.png", ContentType = "image/png", Size = 4,
                Sha256 = new string('a', 64), Content = [1, 2, 3, 4], AddedBy = "Kari", AddedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await using var check = Context();
        var swap = () => check.Database.ExecuteSqlRawAsync("UPDATE manual_run_attachments SET \"Content\" = '\\x05'::bytea WHERE \"Id\" = {0}", id);
        await swap.Should().ThrowAsync<PostgresException>().WithMessage("*can only be withdrawn*");
        var rename = () => check.Database.ExecuteSqlRawAsync("UPDATE manual_run_attachments SET \"FileName\" = 'other.png' WHERE \"Id\" = {0}", id);
        await rename.Should().ThrowAsync<PostgresException>();
        var clean = () => check.Database.ExecuteSqlRawAsync("UPDATE manual_run_attachments SET \"ScanStatus\" = 'clean' WHERE \"Id\" = {0}", id);
        await clean.Should().ThrowAsync<PostgresException>("a file is never relabelled clean after the fact");

        await check.Database.ExecuteSqlRawAsync(
            "UPDATE manual_run_attachments SET \"Content\" = ''::bytea, \"WithdrawnAt\" = now(), \"WithdrawnBy\" = 'Kari', \"WithdrawReason\" = 'personal data' WHERE \"Id\" = {0}", id);
        var row = await Context().ManualRunAttachments.AsNoTracking().SingleAsync(a => a.Id == id);
        row.Should().Match<ManualRunAttachmentRecord>(a => a.Content.Length == 0 && a.Sha256 == new string('a', 64) && a.Size == 4 && a.WithdrawReason == "personal data"
            && a.ScanStatus == EvidenceScanStatus.NotScanned);
        var unwithdraw = () => check.Database.ExecuteSqlRawAsync("UPDATE manual_run_attachments SET \"WithdrawnAt\" = NULL WHERE \"Id\" = {0}", id);
        await unwithdraw.Should().ThrowAsync<PostgresException>();
    }
}

/// <summary>The evidence file content policy (no scanner available): passive types only, content checked per type, safe names.</summary>
public sealed class EvidenceFilePolicyTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData(".png", "image/png", true)]
    [InlineData(".txt", "text/plain", true)]
    [InlineData(".json", "application/json", true)]
    [InlineData(".har", "application/json", true)]
    [InlineData(".xml", "application/xml", true)]
    [InlineData(".csv", "text/csv", true)]
    public void Accepted_files_pass(string ext, string type, bool _)
    {
        var content = ext switch
        {
            ".png" => Png, ".txt" => B("Step 3 failed: 500 — æøå ✓"), ".json" => B("""{"status":500}"""), ".har" => B("""{"log":{"entries":[]}}"""),
            ".xml" => B("<r><a>1</a></r>"), _ => B("a;b\n1;2"),
        };
        EvidenceFilePolicy.ContentProblem(ext, type, content).Should().BeNull();
    }

    [Fact]
    public void Spoofed_malformed_and_active_files_are_refused()
    {
        EvidenceFilePolicy.ContentProblem(".png", "image/png", B("<svg onload=alert(1)>")).Should().Contain("does not match", "a renamed SVG is not a PNG");
        EvidenceFilePolicy.ContentProblem(".pdf", "application/pdf", B("%PDF-1.7\n1 0 obj << /OpenAction << /S /JavaScript /JS (app.alert(1)) >> >>")).Should().Contain("active content");
        EvidenceFilePolicy.ContentProblem(".pdf", "application/pdf", B("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n%%EOF")).Should().BeNull();
        EvidenceFilePolicy.ContentProblem(".txt", "text/plain", B("<!DOCTYPE html><html><script>x()</script>")).Should().Contain("web page");
        EvidenceFilePolicy.ContentProblem(".txt", "text/plain", [0x41, 0xC3, 0x28]).Should().Contain("UTF-8");
        EvidenceFilePolicy.ContentProblem(".txt", "text/plain", [0x4D, 0x5A, 0x00, 0x01]).Should().Contain("does not match", "a binary renamed to .txt");
        EvidenceFilePolicy.ContentProblem(".json", "application/json", B("{\"a\":")).Should().Contain("does not parse");
        EvidenceFilePolicy.ContentProblem(".har", "application/json", B("""{"entries":[]}""")).Should().Contain("log");
        EvidenceFilePolicy.ContentProblem(".xml", "application/xml", B("<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><r>&x;</r>")).Should().Contain("does not parse");
    }

    [Theory]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("C:\\Users\\x\\report.pdf", "report.pdf")]
    [InlineData("=HYPERLINK(\"x\").csv", "_=HYPERLINK(_x_).csv")]
    [InlineData("invoice\u202Egnp.txt", "invoicegnp.txt")]
    [InlineData("a\u0000b\u0007.log", "ab.log")]
    [InlineData("rapport æøå.png", "rapport æøå.png")]
    [InlineData("...", "")]
    public void File_names_are_made_safe(string uploaded, string expected) => EvidenceFilePolicy.SafeName(uploaded).Should().Be(expected);
}
