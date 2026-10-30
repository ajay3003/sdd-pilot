using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.DeliveryReporting.AzureDevOps;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Delivery Reporting → Import snapshot from Azure DevOps against a local fake Azure DevOps REST server (loopback only, a fake token): the PAT is
/// stored encrypted and never returned, authentication failures are safe, projects/branches follow continuation tokens, the commit is resolved at
/// or before the requested time (never a later one), and the commit tree goes through the same parser as an uploaded ZIP.
/// </summary>
public sealed class DeliveryAzureDevOpsTests : IAsyncLifetime
{
    private const string FakePat = "fakepat0000000000000000000000000000000000000000000000";
    private static readonly DateTimeOffset Mon0700 = new(2026, 10, 26, 7, 0, 0, TimeSpan.Zero);

    private sealed record FakeCommit(string Id, DateTimeOffset At, string Branch, byte[] Zip);

    private static readonly FakeCommit[] Commits =
    [
        new("a".PadRight(40, '1'), Mon0700, "main", Zip(ChildSearch(8, 3))),
        new("b".PadRight(40, '2'), Mon0700.AddHours(2), "main", Zip(ChildSearch(8, 5))),
        new("c".PadRight(40, '3'), Mon0700.AddDays(1).AddHours(5), "main", Zip(ChildSearch(8, 7))),
        new("a".PadRight(40, '1'), Mon0700, "develop", Zip(ChildSearch(8, 3))),
        new("d".PadRight(40, '4'), Mon0700.AddHours(1).AddMinutes(30), "develop", Zip(ChildSearch(8, 4))),
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "birknext-ado-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string?> _authorizations = [];
    private LoopbackHttpTestServer _server = null!;
    private AzureDevOpsConnectionStore _store = null!;

    private sealed class Clients : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    public Task InitializeAsync()
    {
        _server = new LoopbackHttpTestServer(Handle);
        _server.Start();
        _store = new AzureDevOpsConnectionStore(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private AzureDevOpsGitClient Git() => new(new Clients(), _store, org => new Uri(_server.Url($"/{org}/")));

    private Guid Connection(string pat = FakePat) => _store.Save(null, new("Bufdir", "https://dev.azure.com/bufdir", pat)).Connection!.Id;

    private async Task Handle(HttpListenerContext c)
    {
        lock (_authorizations) _authorizations.Add(c.Request.Headers["Authorization"]);
        async Task Send(int status, string type, byte[] bytes, string? continuation = null)
        {
            c.Response.StatusCode = status;
            c.Response.ContentType = type;
            if (continuation is not null) c.Response.Headers["x-ms-continuationtoken"] = continuation;
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        Task Json(object value, string? continuation = null) => Send(200, "application/json", JsonSerializer.SerializeToUtf8Bytes(value), continuation);
        var expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + FakePat));
        if (c.Request.Headers["Authorization"] != expected) { await Send(401, "application/json", "{}"u8.ToArray()); return; }
        var path = c.Request.Url!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(c.Request.Url.Query);
        if (query["api-version"] != AzureDevOpsGitClient.ApiVersion) { await Send(400, "application/json", "{}"u8.ToArray()); return; }
        switch (path)
        {
            case "/bufdir/_apis/projects" when query["continuationToken"] is null:
                await Json(new { value = new[] { new { id = "p1", name = "M2LB" } } }, query["$top"] == "1" ? null : "page2"); break;
            case "/bufdir/_apis/projects" when query["continuationToken"] == "page2":
                await Json(new { value = new[] { new { id = "p2", name = "Birk" } } }); break;
            case "/bufdir/M2LB/_apis/git/repositories":
                await Json(new { value = new[] { new { id = "r2", name = "M2LB.Common", defaultBranch = "refs/heads/main" }, new { id = "r1", name = "M2LB", defaultBranch = "refs/heads/main" } } }); break;
            case "/bufdir/M2LB/_apis/git/repositories/M2LB/refs" when query["continuationToken"] is null:
                await Json(new { value = new[] { new { name = "refs/heads/release/1.2" }, new { name = "refs/heads/develop" } } }, "refs2"); break;
            case "/bufdir/M2LB/_apis/git/repositories/M2LB/refs":
                await Json(new { value = new[] { new { name = "refs/heads/main" } } }); break;
            case "/bufdir/M2LB/_apis/git/repositories/M2LB/commits":
            {
                var branch = query["searchCriteria.itemVersion.version"];
                var to = DateTimeOffset.Parse(query["searchCriteria.toDate"]!);
                var match = Commits.Where(x => x.Branch == branch && x.At <= to).OrderByDescending(x => x.At).Take(1)
                    .Select(x => new { commitId = x.Id, comment = "Work", author = new { name = "Dev", date = x.At }, committer = new { date = x.At } });
                await Json(new { value = match.ToArray() }); break;
            }
            case "/bufdir/M2LB/_apis/git/repositories/M2LB/items" when query["$format"] == "zip":
            {
                var commit = Commits.First(x => x.Id == query["versionDescriptor.version"]);
                await Send(200, "application/zip", commit.Zip); break;
            }
            default: await Send(404, "application/json", "{}"u8.ToArray()); break;
        }
    }

    [Fact]
    public void Store_EncryptsThePat_NeverReturnsIt_AndEditKeepsIt()
    {
        var id = Connection();
        File.ReadAllText(Path.Combine(_directory, "connections.json")).Should().NotContain(FakePat, "the token is stored encrypted");
        var view = _store.List().Single();
        view.CredentialConfigured.Should().BeTrue();
        view.Organization.Should().Be("bufdir");
        JsonSerializer.Serialize(view).Should().NotContain(FakePat);

        _store.Save(id, new("Bufdir renamed", "bufdir", null)).Connection!.CredentialConfigured.Should().BeTrue();
        _store.Credential(id)!.Value.Pat.Should().Be(FakePat, "an empty token field keeps the stored token");
        _store.Save(null, new("No token", "bufdir", "")).Error.Should().Contain("personal access token");
        _store.Save(null, new("Custom host", "https://evil.example/bufdir", FakePat)).Error.Should().Contain("organization");
    }

    [Fact]
    public async Task Validate_WrongToken_IsSafeAuthenticationFailure_RightTokenConnects()
    {
        var wrong = Connection("wrongpat111111111111111111111111");
        var failed = await Git().ValidateAsync(wrong, default);
        failed.Success.Should().BeFalse();
        failed.Status.Should().Be("AuthenticationFailed");
        failed.Message.Should().NotContain("wrongpat").And.NotContain("Basic");

        var ok = await Git().ValidateAsync(Connection(), default);
        ok.Success.Should().BeTrue(ok.Message);
        ok.Status.Should().Be("Connected");
    }

    [Fact]
    public async Task Projects_Repositories_Branches_FollowContinuationAndUseFriendlyNames()
    {
        var id = Connection();
        (await Git().ProjectsAsync(id, default)).Select(p => p.Name).Should().Equal("Birk", "M2LB");
        var repositories = await Git().RepositoriesAsync(id, "M2LB", default);
        repositories.Select(r => r.Name).Should().Equal("M2LB", "M2LB.Common");
        repositories[0].DefaultBranch.Should().Be("main");
        (await Git().BranchesAsync(id, "M2LB", "M2LB", default)).Select(b => b.Name).Should().Equal("main", "develop", "release/1.2");
    }

    [Theory]
    [InlineData("main", 60, "a")]      // Mon 08:00 → A (07:00)
    [InlineData("main", 180, "b")]     // Mon 10:00 → B (09:00), never C (Tue)
    [InlineData("develop", 180, "d")]  // develop Mon 10:00 → D
    [InlineData("main", 0, "a")]       // exactly at A
    public async Task Resolve_PicksTheLatestCommitAtOrBeforeTheTime(string branch, int minutesAfterMon0700, string expected)
    {
        var service = Service(Mon0700.AddDays(3));
        var result = await service.ResolveAsync(new(Connection(), "M2LB", "M2LB", branch, Mon0700.AddMinutes(minutesAfterMon0700)), default);
        result.Error.Should().BeNull();
        result.Commit!.CommitId.Should().StartWith(expected);
        result.Commit.CommitAt.Should().BeOnOrBefore(Mon0700.AddMinutes(minutesAfterMon0700));
        result.Commit.Branch.Should().Be(branch);
    }

    [Fact]
    public async Task Resolve_BeforeFirstCommit_Future_AndLongGap()
    {
        var id = Connection();
        var service = Service(Mon0700.AddDays(5));
        (await service.ResolveAsync(new(id, "M2LB", "M2LB", "main", Mon0700.AddMinutes(-1)), default)).Error.Should().Contain("No commit found");
        (await service.ResolveAsync(new(id, "M2LB", "M2LB", "main", Mon0700.AddDays(6)), default)).Error.Should().Contain("future");
        var latest = await service.ResolveAsync(new(id, "M2LB", "M2LB", "main", null), default);
        latest.Commit!.CommitId.Should().StartWith("c", "Current/latest is the branch head now");
        latest.Note.Should().Contain("day(s) earlier");
    }

    [Fact]
    public async Task Preview_CommitTree_ParsesLikeTheSameZipUploaded()
    {
        var service = Service(Mon0700.AddDays(3));
        var (preview, _, error) = await service.PreviewAsync(new(Connection(), "M2LB", "M2LB", "main", Mon0700.AddHours(3)), default);
        error.Should().BeNull();
        var manual = (await Imports(null!).PreviewAsync(null, "m2lb.zip", Commits[1].Zip, default)).Preview!;
        preview!.SourceType.Should().Be(DeliverySourceType.AzureDevOps);
        preview.GitCommit.Should().Be(Commits[1].Id);
        preview.AzureDevOps!.Repository.Should().Be("M2LB");
        preview.SuggestedEffectiveAt.Should().Be(Mon0700.AddHours(3), "the requested state time, not the commit time");
        preview.Fingerprint.Should().Be(manual.Fingerprint);
        (preview.TaskTotal, preview.TaskCompleted, preview.Features.Count, preview.CriteriaCount, preview.Coverage)
            .Should().Be((manual.TaskTotal, manual.TaskCompleted, manual.Features.Count, manual.CriteriaCount, manual.Coverage));
        lock (_authorizations) _authorizations.Should().OnlyContain(a => a == "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + FakePat)));
    }

    [Fact]
    public async Task Import_StoresProvenanceWithoutTheToken_AndDetectsTheDuplicateCommit()
    {
        await WithDatabase(async target =>
        {
            var options = new DbContextOptionsBuilder<DeliveryReportingDbContext>();
            DeliveryReportingDbContext.Configure(options, target);
            await using var db = new DeliveryReportingDbContext(options.Options);
            await DatabaseMigrationLock.MigrateAsync(db);
            var clock = new Clock(Mon0700.AddDays(3));
            var project = (await new DeliverySnapshotStore(db, clock).CreateProjectAsync(new("M2LB", null), default)).Project!.Id;
            var service = Service(clock.GetUtcNow(), db);
            var request = new AzureDevOpsResolveRequest(Connection(), "M2LB", "M2LB", "main", Mon0700.AddHours(3)) { ProjectId = project };
            var (preview, _, _) = await service.PreviewAsync(request, default);
            preview!.DuplicateWarning.Should().BeNull();
            var outcome = await Imports(db, clock).CommitAsync(preview.StagingId, new DeliveryImportCommitRequest
                { ProjectId = project, EffectiveAt = Mon0700.AddHours(3), Role = DeliverySnapshotRole.Baseline, Label = "Monday" }, default);
            outcome.StatusCode.Should().Be(200, outcome.Result.Error);
            outcome.Result.Snapshot!.AzureDevOps!.ShortCommit.Should().Be(Commits[1].Id[..7]);

            var source = await db.ReportSources.AsNoTracking().SingleAsync();
            source.SourceType.Should().Be(nameof(DeliverySourceType.AzureDevOps));
            source.GitCommit.Should().Be(Commits[1].Id);
            source.GitBranch.Should().Be("main");
            source.MetadataJson.Should().Contain("\"repository\": \"M2LB\"").And.NotContain(FakePat);
            (await db.Database.SqlQueryRaw<string>("SELECT t::text AS \"Value\" FROM delivery_reporting.report_source t").ToListAsync())
                .Should().OnlyContain(m => !m.Contains(FakePat));

            var (again, _, _) = await service.PreviewAsync(request, default);
            again!.DuplicateWarning.Should().NotBeNull("the same commit is already imported into this project");
        }, Mon0700);
    }

    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "BirkNext.Api";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("Development", "http://127.0.0.1:5297/", true)]
    [InlineData("Production", "http://127.0.0.1:5297/", false)]
    [InlineData("Development", "https://dev.example.com/", false)]
    [InlineData("Development", null, false)]
    public void DevelopmentBaseUrl_OnlyLoopbackInDevelopment(string environment, string? url, bool honoured)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DeliveryReporting:AzureDevOps:DevelopmentBaseUrl"] = url }).Build();
        var seam = AzureDevOpsGitClient.DevelopmentBaseUri(new Env(environment), configuration);
        (seam is not null).Should().Be(honoured);
        seam?.Invoke("bufdir").ToString().Should().Be("http://127.0.0.1:5297/bufdir/");
    }

    private static readonly DeliveryImportStagingStore Staging = new(TimeProvider.System);

    private static DeliveryReportingImportService Imports(DeliveryReportingDbContext db, TimeProvider? clock = null) => new(db, Staging, clock ?? TimeProvider.System);

    private DeliveryAzureDevOpsImportService Service(DateTimeOffset now, DeliveryReportingDbContext? db = null) =>
        new(Git(), Imports(db!, new Clock(now)), new Clock(now), NullLogger<DeliveryAzureDevOpsImportService>.Instance);

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=birknext";

    private static async Task WithDatabase(Func<string, Task> body, DateTimeOffset _)
    {
        var database = "birknext_delivery_reporting_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var target = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = database, Pooling = false }.ConnectionString;
        await Execute(admin, $"CREATE DATABASE \"{database}\"");
        try { await body(target); }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Execute(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
