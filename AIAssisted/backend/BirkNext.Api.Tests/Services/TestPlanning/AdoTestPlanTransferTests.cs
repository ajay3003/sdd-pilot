using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.DeliveryReporting.AzureDevOps;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using BirkNext.SystemUnderstanding;
using BirkNext.TestCoverage;
using BirkNext.TestData;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Azure DevOps Test Plans (2026-11-25) against an in-memory fake Azure DevOps (no network; a fake token) and a real saved plan on a fresh migrated
/// PostgreSQL database: test plans and suites are created only with confirmation, never twice under the same name, and a lost answer is recorded and
/// checked instead of retried; saving a destination is local; «Send direkte» requires confirmation AND the test lead declaration, links each case to
/// its Azure DevOps test case, updates instead of duplicating, never overwrites a remote edit, reports partial results per case, records the history
/// (the declaration is never a verified approval) and leaves the BirkNext plan unchanged. The file export never calls Azure DevOps.
/// </summary>
public sealed class AdoTestPlanTransferTests : IAsyncLifetime, IDisposable
{
    private const string Pat = "fakepat-transfer-0000000000000000000000000000000000000";
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private const string Project = "import:p1";
    private readonly string _database = TestPostgres.TemporaryName("birknext_adotp");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "adotp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ScenarioDiscoveryCache _discoveries = new();
    private readonly FakeAdo _ado = new();
    private readonly RecordingLogger _log = new();
    private AzureDevOpsConnectionStore _connections = null!;
    private Guid _connection;

    // ── Fake Azure DevOps ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    private sealed class FakeAdo : HttpMessageHandler
    {
        public readonly List<(int Id, string Name, int Root)> Plans = [(70001, "Eksisterende plan", 70002)];
        public readonly List<(int Id, string Name, int? Parent, int Plan)> Suites = [(70002, "Eksisterende plan", null, 70001)];
        public readonly Dictionary<int, (int Rev, string Title, string Type, string Project, string Steps)> Items = [];
        public readonly Dictionary<int, List<int>> SuiteCases = [];
        /// <summary>Last result per test case id: outcome, run, result, completed, tester.</summary>
        public readonly Dictionary<int, (string Outcome, int Run, int Result, DateTimeOffset At, string By)> LastResults = [];
        public readonly List<string> Writes = [];
        public readonly List<string> Calls = [];
        private int _next = 80000;
        /// <summary>The next write whose path contains the key: performed, then the answer is lost (timeout).</summary>
        public string? LoseAnswerAfter;
        /// <summary>The next write whose path contains the key is refused with this status (nothing is done).</summary>
        public (string Path, HttpStatusCode Status)? Refuse;
        public TimeSpan CreateDelay = TimeSpan.Zero;

        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method.Method;
            lock (Calls) Calls.Add($"{method} {path}");
            if (request.Headers.Authorization?.Parameter != Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Pat))) return Json(new { }, HttpStatusCode.Unauthorized);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var isWrite = method is "POST" or "PATCH" && !path.EndsWith("/wiql", StringComparison.Ordinal);
            if (isWrite)
            {
                lock (Writes) Writes.Add($"{method} {path}");
                if (Refuse is { } r && path.Contains(r.Path, StringComparison.Ordinal)) { Refuse = null; return Json(new { message = "refused" }, r.Status); }
                if (CreateDelay > TimeSpan.Zero) await Task.Delay(CreateDelay, ct);
            }
            HttpResponseMessage answer;
            lock (this) answer = Handle(method, path, body);
            if (isWrite && LoseAnswerAfter is { } lose && path.Contains(lose, StringComparison.Ordinal)) { LoseAnswerAfter = null; throw new TaskCanceledException("answer lost"); }
            return answer;
        }

        private HttpResponseMessage Handle(string method, string path, string? body)
        {
            var parts = path.Trim('/').Split('/');
            if (path == "/bufdir/_apis/projects") return Json(new { value = new[] { new { id = "p1", name = "M2LB" }, new { id = "p2", name = "Testdokumentasjon" } } });
            if (path == "/bufdir/M2LB/_apis/testplan/plans" && method == "GET")
                return Json(new { value = Plans.Select(p => new { id = p.Id, name = p.Name, state = "Active", rootSuite = new { id = p.Root } }) });
            if (path == "/bufdir/M2LB/_apis/testplan/plans" && method == "POST")
            {
                var name = JsonNode.Parse(body!)!["name"]!.ToString();
                var (id, root) = (_next++, _next++);
                Plans.Add((id, name, root));
                Suites.Add((root, name, null, id));
                return Json(new { id, name, state = "Active", rootSuite = new { id = root } });
            }
            if (parts.Length == 7 && parts[4] == "Plans" && parts[6] == "suites")
            {
                var plan = int.Parse(parts[5]);
                if (method == "GET") return Json(new { value = Suites.Where(s => s.Plan == plan).Select(s => new { id = s.Id, name = s.Name, suiteType = "staticTestSuite", parentSuite = s.Parent is { } p ? new { id = p } : null }) });
                var node = JsonNode.Parse(body!)!;
                var id = _next++;
                Suites.Add((id, node["name"]!.ToString(), node["parentSuite"]!["id"]!.GetValue<int>(), plan));
                return Json(new { id, name = node["name"]!.ToString(), suiteType = "staticTestSuite", parentSuite = new { id = node["parentSuite"]!["id"]!.GetValue<int>() } });
            }
            if (parts.Length == 9 && parts[6] == "Suites" && parts[8] == "TestPoint" && method == "GET")
            {
                var suite = int.Parse(parts[7]);
                var cases = SuiteCases.TryGetValue(suite, out var l) ? l : [];
                return Json(new { value = cases.Select((id, i) => new
                {
                    id = 60000 + suite % 100 * 100 + i, testCaseReference = new { id, name = Items.TryGetValue(id, out var w) ? w.Title : "" }, configuration = new { name = "Windows 11" },
                    results = LastResults.TryGetValue(id, out var r)
                        ? (object)new { outcome = r.Outcome, lastTestRunId = r.Run, lastResultId = r.Result, lastResultDetails = new { dateCompleted = r.At, runBy = new { displayName = r.By } } }
                        : new { outcome = "Unspecified" },
                }) });
            }
            if (parts.Length == 9 && parts[6] == "Suites" && parts[8] == "TestCase")
            {
                var suite = int.Parse(parts[7]);
                var list = SuiteCases.TryGetValue(suite, out var l) ? l : SuiteCases[suite] = [];
                if (Suites.All(s => s.Id != suite)) return Json(new { }, HttpStatusCode.NotFound);
                if (method == "GET") return Json(new { value = list.Select(i => new { workItem = new { id = i, name = Items.TryGetValue(i, out var w) ? w.Title : "" } }) });
                foreach (var w in JsonNode.Parse(body!)!.AsArray()) { var id = w!["workItem"]!["id"]!.GetValue<int>(); if (!list.Contains(id)) list.Add(id); }
                return Json(new { value = Array.Empty<object>() });
            }
            if (path == "/bufdir/M2LB/_apis/wit/workitems/$Test%20Case" && method == "POST")
            {
                var ops = JsonNode.Parse(body!)!.AsArray();
                string F(string field) => ops.FirstOrDefault(o => o!["path"]!.ToString() == "/fields/" + field)?["value"]?.ToString() ?? "";
                var id = _next++;
                Items[id] = (1, F("System.Title"), "Test Case", "M2LB", F("Microsoft.VSTS.TCM.Steps"));
                return Json(Item(id));
            }
            if (parts.Length == 5 && parts[1] == "_apis" && parts[2] == "wit" && parts[3] == "workitems")
            {
                var id = int.Parse(parts[4]);
                if (!Items.TryGetValue(id, out var w)) return Json(new { }, HttpStatusCode.NotFound);
                if (method == "GET") return Json(Item(id));
                var ops = JsonNode.Parse(body!)!.AsArray();
                var test = ops.First(o => o!["op"]!.ToString() == "test")!["value"]!.GetValue<int>();
                if (test != w.Rev) return Json(new { message = "rev mismatch" }, HttpStatusCode.PreconditionFailed);
                string? F(string field) => ops.FirstOrDefault(o => o!["path"]!.ToString() == "/fields/" + field)?["value"]?.ToString();
                Items[id] = (w.Rev + 1, F("System.Title") ?? w.Title, w.Type, w.Project, F("Microsoft.VSTS.TCM.Steps") ?? w.Steps);
                return Json(Item(id));
            }
            if (path == "/bufdir/M2LB/_apis/wit/wiql")
            {
                var query = JsonNode.Parse(body!)!["query"]!.ToString();
                var matches = Items.Where(i => query.Contains($"[System.Title] = '{i.Value.Title.Replace("'", "''")}'", StringComparison.Ordinal)).Select(i => new { id = i.Key });
                return Json(new { workItems = matches });
            }
            return Json(new { }, HttpStatusCode.NotFound);
        }

        private object Item(int id)
        {
            var w = Items[id];
            return new { id, rev = w.Rev, fields = new Dictionary<string, object> { ["System.Title"] = w.Title, ["System.WorkItemType"] = w.Type, ["System.TeamProject"] = w.Project } };
        }

        /// <summary>Someone edits the test case in Azure DevOps.</summary>
        public void EditRemotely(int id) { lock (this) { var w = Items[id]; Items[id] = w with { Rev = w.Rev + 1, Title = w.Title + " (edited)" }; } }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }

    private sealed class RecordingLogger : ILogger
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Lines) Lines.Add(formatter(state, exception)); }
    }

    private sealed class Logger<T>(RecordingLogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await Execute($"CREATE DATABASE \"{_database}\"");
        await using var db = Context();
        await DatabaseMigrationLock.MigrateAsync(db);
        _connections = new AzureDevOpsConnectionStore(Path.Combine(_dir, "azure-devops"));
        _connection = _connections.Save(null, new("Bufdir", "bufdir", Pat)).Connection!.Id;
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

    private TestPlanSuggestionService Plans(AppDbContext db)
    {
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new NoValues(), new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        return new TestPlanSuggestionService(db, new SystemGuideService(db, [], Array.Empty<IIntegrationJourneyPack>()), new SourceBaselineService(db, new IqrSourceStore(db)), testData,
            Array.Empty<IIntegrationJourneyPack>(), new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance), discoveries: _discoveries);
    }

    private sealed record Sut(AdoTestPlanSettingsService Settings, AdoTestPlanTransferService Transfer, AdoTestPlanExportService Export, AzureDevOpsTestPlanSettingsStore Store)
    {
        public AdoTestResultsService Results { get; init; } = null!;
    }

    private Sut Build(AppDbContext db)
    {
        var git = new AzureDevOpsGitClient(new Factory(_ado), _connections, org => new Uri($"https://ado.fake.test/{org}/"));
        var client = new AzureDevOpsTestPlansClient(git);
        var export = new AdoTestPlanExportService(db, Plans(db));
        var store = new AzureDevOpsTestPlanSettingsStore(_connections.DirectoryPath);
        var settings = new AdoTestPlanSettingsService(_connections, client, store, export, TimeProvider.System, new Logger<AdoTestPlanSettingsService>(_log));
        return new(settings, new AdoTestPlanTransferService(db, export, client, _connections, settings, TimeProvider.System, new Logger<AdoTestPlanTransferService>(_log)), export, store)
        {
            Results = new AdoTestResultsService(db, export, client, _connections, settings, TimeProvider.System, new Logger<AdoTestResultsService>(_log)),
        };
    }

    /// <summary>A saved plan with two scenario cases («Child past compulsory school age», «Valid report»), each with steps and expected results.</summary>
    private async Task<TestPlanDraft> PlanAsync(AppDbContext db)
    {
        var discovery = await new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _discoveries).DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = Project, ProjectName = "SkoleService",
            Workspace = new CoverageWorkspaceEvidence
            {
                SpecificationAvailable = true, SpecificationArtifacts = [new("spec-1", Spec, "SkoleService", "fp")],
                Requirements = [new("FR-001", "The service MUST accept a valid report.", null)],
                AcceptanceScenarios =
                [
                    new QaTestCandidate { Id = "scenario-42", Title = "Child past compulsory school age", Given = "a child past compulsory school age", When = "the period locks", Then = "the child is not expected" },
                    new QaTestCandidate { Id = "scenario-7", Title = "Valid report", Given = "a valid report", When = "the report is submitted", Then = "the report is accepted", RequirementIds = ["FR-001"] },
                ],
            },
        }, default);
        var scenarios = discovery.Candidates.Where(c => c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal)).ToList();
        return await Plans(db).CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = Project, ProjectName = "SkoleService", DiscoveryId = discovery.DiscoveryId,
            CandidateIds = [.. scenarios.Select(c => c.CandidateId)] }, "tester", default);
    }

    private AdoTestPlanDestination Destination(int plan = 70001, int suite = 70002) => new()
    {
        ConnectionId = _connection, Organization = "bufdir", Project = "M2LB", PlanId = plan, PlanName = "Eksisterende plan", SuiteId = suite, SuiteName = "Eksisterende plan",
    };

    private AdoTransferRequest Request(TestPlanDraft plan, bool confirmed = true, bool declared = true, Dictionary<string, AdoTransferAction>? actions = null, AdoTestPlanDestination? destination = null) => new()
    {
        ProjectKey = Project, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true, Destination = destination ?? Destination(), Confirmed = confirmed,
        LeadApprovalDeclared = declared, Actions = actions ?? [],
    };

    /// <summary>The plan with a suite tree (2026-11-26): «Ende til ende / Rapportering» holds the first case, «Integrasjon / Mottak» the second.</summary>
    private async Task<TestPlanDraft> TreeAsync(AppDbContext db)
    {
        var plan = await PlanAsync(db);
        var suites = new List<TestSuiteDraft>
        {
            new("s-e2e", "Ende til ende", "structure", null) { TestLevel = ScenarioTestLevel.EndToEnd }, new("s-rapp", "Rapportering", "structure", null) { ParentId = "s-e2e" },
            new("s-int", "Integrasjon", "structure", null) { Order = 1 }, new("s-mott", "Mottak", "structure", null) { ParentId = "s-int" },
        };
        var target = new[] { "s-rapp", "s-mott" };
        return await Plans(db).SaveAsync(plan.Id, new SaveTestPlanDraftRequest
        {
            BaseRevision = plan.Revision, PlanName = plan.PlanName, ScenarioIds = [.. plan.Cases.Select(c => c.Source.ScenarioId)], Suites = suites,
            Cases = [.. plan.Cases.Select((c, i) => new TestCaseHumanPart { ScenarioId = c.Source.ScenarioId, SuiteId = target[i], Edits = c.Edits, Risk = c.Risk, RequirementLinks = [.. c.RequirementLinks] })],
        }, "tester", default);
    }

    [Fact]
    public async Task A_transfer_with_suite_placement_creates_the_static_suite_tree_parent_first_reuses_it_on_retry_and_never_writes_on_preview()
    {
        await using var db = Context();
        var plan = await TreeAsync(db);
        var sut = Build(db);
        var writes = _ado.Writes.Count;

        var preview = await sut.Transfer.PreviewAsync(Request(plan) with { PreserveSuites = true }, default);
        preview.SuitePlan.Select(i => (i.Path, i.Depth, i.Cases, i.Exists)).Should().Equal(
            ("Ende til ende", 0, 0, false), ("Ende til ende / Rapportering", 1, 1, false), ("Integrasjon", 0, 0, false), ("Integrasjon / Mottak", 1, 1, false));
        _ado.Writes.Count.Should().Be(writes, "a preview never writes");

        var result = await sut.Transfer.TransferAsync(Request(plan) with { PreserveSuites = true }, "tester", default);
        result.Suites.Select(x => (x.Path, x.Outcome)).Should().Equal(("Ende til ende", AdoCreateOutcome.Created), ("Ende til ende / Rapportering", AdoCreateOutcome.Created),
            ("Integrasjon", AdoCreateOutcome.Created), ("Integrasjon / Mottak", AdoCreateOutcome.Created));
        int Remote(string path) => result.Suites.Single(x => x.Path == path).RemoteSuiteId!.Value;
        _ado.Suites.Single(x => x.Id == Remote("Ende til ende / Rapportering")).Parent.Should().Be(Remote("Ende til ende"));
        _ado.Suites.Single(x => x.Id == Remote("Ende til ende")).Parent.Should().Be(70002, "the tree hangs under the chosen destination suite");
        result.Cases.Should().OnlyContain(c => c.Outcome == AdoTransferOutcome.Created);
        _ado.SuiteCases[Remote("Ende til ende / Rapportering")].Should().ContainSingle();
        _ado.SuiteCases[Remote("Integrasjon / Mottak")].Should().ContainSingle();
        _ado.SuiteCases.GetValueOrDefault(70002).Should().BeNullOrEmpty("no case lands in the destination suite itself");

        var suitesBefore = _ado.Suites.Count;
        plan = await Plans(db).DraftAsync(plan.Id, default);
        var again = await sut.Transfer.TransferAsync(Request(plan, actions: plan.Cases.ToDictionary(c => c.Source.ScenarioId, _ => AdoTransferAction.Update)) with { PreserveSuites = true }, "tester", default);
        again.Suites.Should().OnlyContain(x => x.Outcome == AdoCreateOutcome.AlreadyExists, "a retry reuses the suites");
        _ado.Suites.Count.Should().Be(suitesBefore, "no duplicate suite");
        _ado.SuiteCases[Remote("Integrasjon / Mottak")].Should().ContainSingle("no duplicate membership");
    }

    [Fact]
    public async Task A_refused_suite_leaves_its_cases_not_in_a_suite_and_is_reported_as_partial()
    {
        await using var db = Context();
        var plan = await TreeAsync(db);
        var sut = Build(db);
        _ado.Refuse = ("/suites", HttpStatusCode.Forbidden);
        var result = await sut.Transfer.TransferAsync(Request(plan) with { PreserveSuites = true }, "tester", default);
        result.Suites.First().Outcome.Should().Be(AdoCreateOutcome.Failed);
        result.Suites.Single(x => x.Path == "Ende til ende / Rapportering").Outcome.Should().Be(AdoCreateOutcome.Failed, "a child of a failed suite is never created elsewhere");
        result.Cases.Should().Contain(c => c.Outcome == AdoTransferOutcome.NotInSuite && c.TestCaseId > 0, "the test case exists; it is not in its suite yet");
        result.Problems.Should().BeGreaterThan(0);
    }

    // ── Test plans and suites ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_test_plan_is_created_only_with_confirmation_and_an_existing_name_is_offered_instead_of_a_second_plan()
    {
        await using var db = Context();
        var sut = Build(db);

        var unconfirmed = await sut.Settings.CreatePlanAsync(new(_connection, "M2LB", "M2LB – Skolenærvær – Testplan", null, Confirmed: false), "tester", default);
        unconfirmed.Outcome.Should().Be(AdoCreateOutcome.Failed);
        _ado.Writes.Should().BeEmpty("nothing is sent without confirmation");

        var same = await sut.Settings.CreatePlanAsync(new(_connection, "M2LB", "  eksisterende PLAN ", null, Confirmed: true), "tester", default);
        same.Outcome.Should().Be(AdoCreateOutcome.AlreadyExists);
        same.Message.Should().Contain("Det finnes allerede en testplan med navnet «Eksisterende plan»");
        same.Plan!.Id.Should().Be(70001);
        _ado.Writes.Should().BeEmpty("a plan with the same name is never created again or overwritten");

        var created = await sut.Settings.CreatePlanAsync(new(_connection, "M2LB", "M2LB – Skolenærvær – Testplan", "Fra BirkNext", Confirmed: true), "tester", default);
        created.Outcome.Should().Be(AdoCreateOutcome.Created);
        created.Plan!.Id.Should().BeGreaterThan(70001).And.NotBe(50088, "no example id from another project is used");
        created.Plan.RootSuiteId.Should().BeGreaterThan(0);
        created.Plan.WebUrl.Should().Contain("_testPlans/define?planId=" + created.Plan.Id);
        _ado.Writes.Should().ContainSingle(w => w.StartsWith("POST /bufdir/M2LB/_apis/testplan/plans"));
    }

    [Fact]
    public async Task A_lost_answer_is_recorded_and_the_same_plan_is_never_sent_again_before_it_is_checked()
    {
        await using var db = Context();
        var sut = Build(db);
        _ado.LoseAnswerAfter = "/testplan/plans";

        var first = await sut.Settings.CreatePlanAsync(new(_connection, "M2LB", "Ny plan", null, true), "tester", default);
        first.Outcome.Should().Be(AdoCreateOutcome.Uncertain);
        first.Message.Should().Be("Vi vet ikke om testplanen ble opprettet. Kontroller Azure DevOps før du prøver igjen.");
        sut.Store.Pending().Should().ContainSingle(p => p.Name == "Ny plan" && p.Kind == AdoPendingKind.Plan);

        var retry = await sut.Settings.CreatePlanAsync(new(_connection, "M2LB", "Ny plan", null, true), "tester", default);
        retry.Outcome.Should().Be(AdoCreateOutcome.Blocked);
        _ado.Writes.Count(w => w.Contains("/testplan/plans")).Should().Be(1, "a retry never blindly creates a second plan");

        var verified = await sut.Settings.VerifyAsync(first.Pending!.Id, default);
        verified.Outcome.Should().Be(AdoVerifyOutcome.Found);
        verified.Plan!.Name.Should().Be("Ny plan");
        sut.Store.Pending().Should().BeEmpty();
        _ado.Plans.Count(p => p.Name == "Ny plan").Should().Be(1);
    }

    [Fact]
    public async Task Two_simultaneous_clicks_create_one_plan()
    {
        await using var db1 = Context();
        await using var db2 = Context();
        _ado.CreateDelay = TimeSpan.FromMilliseconds(300);
        var results = await Task.WhenAll(
            Build(db1).Settings.CreatePlanAsync(new(_connection, "M2LB", "Dobbeltklikk", null, true), "tester", default),
            Build(db2).Settings.CreatePlanAsync(new(_connection, "M2LB", "Dobbeltklikk", null, true), "tester", default));
        results.Count(r => r.Outcome == AdoCreateOutcome.Created).Should().Be(1);
        results.Count(r => r.Outcome == AdoCreateOutcome.Blocked).Should().Be(1);
        _ado.Plans.Count(p => p.Name == "Dobbeltklikk").Should().Be(1);
    }

    [Fact]
    public async Task Suites_are_created_under_the_chosen_parent_never_twice_and_a_permission_error_is_plain_norwegian()
    {
        await using var db = Context();
        var sut = Build(db);
        var created = await sut.Settings.CreateSuiteAsync(new(_connection, "M2LB", 70001, 70002, "Kritiske brukerreiser", true), "tester", default);
        created.Outcome.Should().Be(AdoCreateOutcome.Created);
        created.Suite!.ParentId.Should().Be(70002);

        var again = await sut.Settings.CreateSuiteAsync(new(_connection, "M2LB", 70001, 70002, "kritiske brukerreiser", true), "tester", default);
        again.Outcome.Should().Be(AdoCreateOutcome.AlreadyExists);
        again.Suite!.Id.Should().Be(created.Suite.Id);

        var child = await sut.Settings.CreateSuiteAsync(new(_connection, "M2LB", 70001, created.Suite.Id, "Kritiske brukerreiser", true), "tester", default);
        child.Outcome.Should().Be(AdoCreateOutcome.Created, "the same name under another parent is a different suite");

        var suites = await sut.Settings.SuitesAsync(_connection, "M2LB", 70001, default);
        suites.Items.Single(s => s.Id == child.Suite!.Id).ParentId.Should().Be(created.Suite.Id, "the hierarchy is kept");

        _ado.Refuse = ("/suites", HttpStatusCode.Forbidden);
        var forbidden = await sut.Settings.CreateSuiteAsync(new(_connection, "M2LB", 70001, 70002, "Sikkerhet", true), "tester", default);
        forbidden.Outcome.Should().Be(AdoCreateOutcome.Failed);
        forbidden.Message.Should().StartWith("Du har ikke tilgang til å opprette testgruppen.");
        sut.Store.Pending().Should().BeEmpty("a refusal is certain: nothing was created");

        var unconfirmed = await sut.Settings.CreateSuiteAsync(new(_connection, "M2LB", 70001, 70002, "Ytelse", false), "tester", default);
        unconfirmed.Outcome.Should().Be(AdoCreateOutcome.Failed);
    }

    [Fact]
    public async Task An_unaccepted_token_and_an_unknown_connection_are_reported_safely()
    {
        await using var db = Context();
        var sut = Build(db);
        var wrong = _connections.Save(null, new("Annen", "bufdir", "wrong-token-000000000000000000000000000000000000000000")).Connection!.Id;
        var check = await sut.Settings.CheckAsync(wrong, null, default);
        check.Success.Should().BeFalse();
        check.Status.Should().Be("AuthenticationFailed");
        check.Message.Should().Be("Azure DevOps godtok ikke tilgangsnøkkelen (PAT). Tilkoblingen må kontrolleres.");
        (await sut.Settings.PlansAsync(Guid.NewGuid(), "M2LB", default)).Error.Should().StartWith("Tilkoblingen mangler");
        (await sut.Settings.CheckAsync(_connection, "M2LB", default)).Message.Should().Be("Tilkoblet. 1 testplan i M2LB.");
    }

    [Fact]
    public async Task Saving_a_default_destination_is_local_the_project_default_wins_and_saving_export_settings_keeps_it()
    {
        await using var db = Context();
        var sut = Build(db);
        await sut.Settings.SaveDefaultAsync(new(AdoDestinationScope.System, null, Destination()), "tester", default);
        (await sut.Settings.EffectiveAsync(Project, default)).Source.Should().Be(AdoDestinationSource.System);

        var projectDestination = Destination() with { SuiteId = 70099, SuiteName = "Prosjektets gruppe" };
        await sut.Settings.SaveDefaultAsync(new(AdoDestinationScope.Project, Project, projectDestination), "tester", default);
        var (effective, source) = await sut.Settings.EffectiveAsync(Project, default);
        source.Should().Be(AdoDestinationSource.Project);
        effective!.SuiteId.Should().Be(70099);

        // A page that loaded the export settings earlier saves them: the project's destination is not lost.
        await sut.Export.SaveProfileAsync(Project, new AdoExportProfile { AreaPath = "M2LB\\Test" }, "tester", default);
        (await sut.Settings.EffectiveAsync(Project, default)).Destination!.SuiteId.Should().Be(70099);
        (await sut.Settings.EffectiveAsync("import:other", default)).Source.Should().Be(AdoDestinationSource.System);

        _ado.Writes.Should().BeEmpty("saving a destination never creates anything in Azure DevOps");
        var incomplete = () => sut.Settings.SaveDefaultAsync(new(AdoDestinationScope.System, null, Destination() with { SuiteId = 0 }), "tester", default);
        await incomplete.Should().ThrowAsync<TestPlanException>().Where(e => e.Code == "destination-incomplete");
    }

    // ── Direct transfer ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_transfer_needs_confirmation_and_the_test_lead_declaration_and_the_file_export_never_calls_azure_devops()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);

        await sut.Export.FileAsync(new AdoExportRequest { ProjectKey = Project, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true,
            Profile = new AdoExportProfile { AreaPath = "M2LB" } }, "tester", default);
        _ado.Calls.Should().BeEmpty("a file download never contacts Azure DevOps");

        var unconfirmed = () => sut.Transfer.TransferAsync(Request(plan, confirmed: false), "tester", default);
        await unconfirmed.Should().ThrowAsync<TestPlanException>().Where(e => e.Code == "confirmation-required" && e.Status == 400);
        var undeclared = () => sut.Transfer.TransferAsync(Request(plan, declared: false), "tester", default);
        await undeclared.Should().ThrowAsync<TestPlanException>().Where(e => e.Code == "lead-approval-required" && e.Message.StartsWith("Godkjenning fra testleder kreves"));

        var preview = await sut.Transfer.PreviewAsync(Request(plan, confirmed: false, declared: false), default);
        preview.ToCreate.Should().Be(2);
        preview.CanTransfer.Should().BeTrue();
        _ado.Writes.Should().BeEmpty("a preview, a missing confirmation and a missing declaration never write");
    }

    [Fact]
    public async Task A_transfer_creates_links_and_records_history_then_a_second_transfer_updates_or_skips_instead_of_duplicating()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);

        var result = await sut.Transfer.TransferAsync(Request(plan), "tester", default);
        result.Cases.Should().HaveCount(2).And.OnlyContain(c => c.Outcome == AdoTransferOutcome.Created && c.TestCaseId > 0 && c.WebUrl!.Contains("_workitems/edit/"));
        result.SuiteUrl.Should().Contain("planId=70001&suiteId=70002");
        _ado.SuiteCases[70002].Should().BeEquivalentTo(result.Cases.Select(c => c.TestCaseId!.Value));
        _ado.Items.Values.Should().OnlyContain(w => w.Steps.StartsWith("<steps id=\"0\"") && w.Steps.Contains("ValidateStep"));

        var history = (await sut.Export.HistoryAsync(Project, default)).Single();
        history.Format.Should().Be("Direct");
        history.Status.Should().Be("Transferred");
        history.Created.Should().Be(2);
        history.LeadApprovalDeclared.Should().BeTrue();
        history.StartedAt.Should().NotBeNull();
        history.CompletedAt.Should().BeOnOrAfter(history.StartedAt!.Value);
        var raw = await db.AdoExports.AsNoTracking().SingleAsync();
        raw.ContentJson.Should().Contain("\"verifiedApproval\":false", "the declaration is never recorded as a verified test lead approval");
        raw.ContentJson.Should().NotContain("passed", "a transfer says nothing about test results").And.NotContain(Pat);

        var unchanged = await sut.Transfer.PreviewAsync(Request(plan), default);
        unchanged.Cases.Should().OnlyContain(c => c.Link.State == AdoLinkState.Linked && c.Suggested == AdoTransferAction.Skip && !c.ChangedSinceTransfer);
        unchanged.CanTransfer.Should().BeFalse("nothing changed, so nothing is suggested");

        var writesBefore = _ado.Writes.Count;
        var update = await sut.Transfer.TransferAsync(Request(plan, actions: unchanged.Cases.ToDictionary(c => c.CaseId, _ => AdoTransferAction.Update)), "tester", default);
        update.Cases.Should().OnlyContain(c => c.Outcome == AdoTransferOutcome.Updated);
        _ado.Items.Should().HaveCount(2, "an update never creates a second test case");
        _ado.Writes.Skip(writesBefore).Should().OnlyContain(w => w.StartsWith("PATCH"), "already in the suite: not added again");
        (await db.TestPlanDrafts.AsNoTracking().SingleAsync()).Revision.Should().Be(plan.Revision, "the BirkNext plan is never changed by a transfer");
    }

    [Fact]
    public async Task A_test_case_edited_in_azure_devops_is_never_overwritten()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);
        var first = await sut.Transfer.TransferAsync(Request(plan), "tester", default);
        var edited = first.Cases[0];
        _ado.EditRemotely(edited.TestCaseId!.Value);

        var patches = _ado.Writes.Count(w => w.StartsWith("PATCH"));
        var result = await sut.Transfer.TransferAsync(Request(plan, actions: first.Cases.ToDictionary(c => c.CaseId, _ => AdoTransferAction.Update)), "tester", default);
        var conflict = result.Cases.Single(c => c.CaseId == edited.CaseId);
        conflict.Outcome.Should().Be(AdoTransferOutcome.NeedsReview);
        conflict.Message.Should().Contain("er endret i Azure DevOps etter siste overføring").And.Contain("ble ikke overskrevet");
        _ado.Items[edited.TestCaseId.Value].Title.Should().EndWith("(edited)");
        result.Cases.Single(c => c.CaseId != edited.CaseId).Outcome.Should().Be(AdoTransferOutcome.Updated);
        _ado.Writes.Count(w => w.StartsWith("PATCH")).Should().Be(patches + 1, "only the unedited case was patched");
        (await sut.Export.HistoryAsync(Project, default)).First().Status.Should().Be("Partial");
    }

    [Fact]
    public async Task A_lost_create_answer_is_never_followed_by_a_blind_second_create_and_is_reconciled_from_azure_devops()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);
        _ado.LoseAnswerAfter = "/wit/workitems/$Test";

        var result = await sut.Transfer.TransferAsync(Request(plan), "tester", default);
        var lost = result.Cases.Single(c => c.Outcome == AdoTransferOutcome.Uncertain);
        lost.Message.Should().Contain("Vi vet ikke om det ble utført");
        result.Cases.Single(c => c.CaseId != lost.CaseId).Outcome.Should().Be(AdoTransferOutcome.Created);
        (await sut.Export.HistoryAsync(Project, default)).Single().Status.Should().Be("Partial", "not reported as full success");

        var preview = await sut.Transfer.PreviewAsync(Request(plan), default);
        var pending = preview.Cases.Single(c => c.CaseId == lost.CaseId);
        pending.Link.State.Should().Be(AdoLinkState.Uncertain);
        pending.Allowed.Should().BeEmpty();
        preview.NeedReview.Should().Be(1);

        var creates = _ado.Writes.Count(w => w.Contains("$Test"));
        var again = await sut.Transfer.TransferAsync(Request(plan, actions: new() { [lost.CaseId] = AdoTransferAction.Create, [result.Cases.Single(c => c.CaseId != lost.CaseId).CaseId] = AdoTransferAction.Update }), "tester", default);
        again.Cases.Single(c => c.CaseId == lost.CaseId).Outcome.Should().Be(AdoTransferOutcome.NeedsReview);
        _ado.Writes.Count(w => w.Contains("$Test")).Should().Be(creates, "an uncertain create is never sent again");

        var reconciled = await sut.Transfer.ReconcileAsync(new(Project, plan.Id, lost.CaseId, Destination()), "tester", default);
        reconciled.Outcome.Should().Be(AdoVerifyOutcome.Found);
        reconciled.Link.State.Should().Be(AdoLinkState.Linked);
        _ado.Items.Should().HaveCount(2, "the test case created before the answer was lost is adopted, not created again");

        var after = await sut.Transfer.PreviewAsync(Request(plan), default);
        after.Cases.Single(c => c.CaseId == lost.CaseId).Suggested.Should().Be(AdoTransferAction.Update, "its text in Azure DevOps is not known for certain");
    }

    [Fact]
    public async Task A_refused_case_is_reported_per_case_and_a_suite_failure_keeps_the_link_so_a_retry_adds_without_creating()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);
        _ado.Refuse = ("/wit/workitems/$Test", HttpStatusCode.BadRequest);
        var partial = await sut.Transfer.TransferAsync(Request(plan), "tester", default);
        partial.Cases.Count(c => c.Outcome == AdoTransferOutcome.Failed).Should().Be(1);
        partial.Cases.Count(c => c.Outcome == AdoTransferOutcome.Created).Should().Be(1);
        partial.Problems.Should().Be(1);
        var failedId = partial.Cases.Single(c => c.Outcome == AdoTransferOutcome.Failed).CaseId;
        (await sut.Transfer.PreviewAsync(Request(plan), default)).Cases.Single(c => c.CaseId == failedId).Link.State.Should().Be(AdoLinkState.None, "a certain failure leaves no link");

        _ado.Refuse = ("/TestCase", HttpStatusCode.InternalServerError);
        var second = await sut.Transfer.TransferAsync(Request(plan, actions: new() { [failedId] = AdoTransferAction.Create }), "tester", default);
        var notInSuite = second.Cases.Single(c => c.CaseId == failedId);
        notInSuite.Outcome.Should().Be(AdoTransferOutcome.NotInSuite);
        notInSuite.Message.Should().Contain("det lages ikke på nytt");

        var items = _ado.Items.Count;
        var retry = await sut.Transfer.TransferAsync(Request(plan, actions: new() { [failedId] = AdoTransferAction.Update }), "tester", default);
        retry.Cases.Single(c => c.CaseId == failedId).Outcome.Should().Be(AdoTransferOutcome.Updated);
        _ado.Items.Count.Should().Be(items);
        _ado.SuiteCases[70002].Should().Contain(notInSuite.TestCaseId!.Value);
        (await sut.Export.HistoryAsync(Project, default)).Select(h => h.Status).Should().Equal(["Transferred", "NeedsReview", "Partial"],
            "newest first; nothing fully succeeded in the second transfer (created, but not in the suite), so it is never reported as transferred");
    }

    [Fact]
    public async Task An_unlinked_case_whose_title_is_already_in_the_suite_is_flagged_and_not_created_by_default()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);
        var title = AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = Project, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true }).Cases[0].Title;
        _ado.Items[90001] = (3, title, "Test Case", "M2LB", "");
        _ado.SuiteCases[70002] = [90001];

        var preview = await sut.Transfer.PreviewAsync(Request(plan), default);
        var flagged = preview.Cases.Single(c => c.Title == title);
        flagged.Suggested.Should().Be(AdoTransferAction.Skip);
        flagged.Note.Should().StartWith("Mulig duplikat");
        flagged.Allowed.Should().Contain(AdoTransferAction.Create, "the tester can still create it explicitly");
        preview.ToCreate.Should().Be(1);
    }

    [Fact]
    public async Task A_refused_transfer_is_recorded_in_the_history_and_a_running_transfer_blocks_a_second()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);
        var missing = () => sut.Transfer.TransferAsync(Request(plan, destination: Destination(suite: 79999)), "tester", default);
        await missing.Should().ThrowAsync<TestPlanException>().Where(e => e.Status == 409);
        var history = (await sut.Export.HistoryAsync(Project, default)).Single();
        history.Status.Should().Be("Failed");
        history.Reason.Should().Contain("Fant ikke testplanen eller testgruppen");

        await using var db2 = Context();
        _ado.CreateDelay = TimeSpan.FromMilliseconds(400);
        var first = sut.Transfer.TransferAsync(Request(plan), "tester", default);
        await Task.Delay(100);
        var second = () => Build(db2).Transfer.TransferAsync(Request(plan), "tester", default);
        await second.Should().ThrowAsync<TestPlanException>().Where(e => e.Code == "transfer-running");
        (await first).Cases.Should().OnlyContain(c => c.Outcome == AdoTransferOutcome.Created);
        _ado.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_token_never_appears_in_logs_results_settings_or_history()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);
        _ado.LoseAnswerAfter = "/testplan/plans";
        var created = await sut.Settings.CreatePlanAsync(new(_connection, "M2LB", "Hemmelig test", null, true), "tester", default);
        await sut.Settings.SaveDefaultAsync(new(AdoDestinationScope.System, null, Destination()), "tester", default);
        var result = await sut.Transfer.TransferAsync(Request(plan), "tester", default);
        var everything = string.Join("\n", _log.Lines) + JsonSerializer.Serialize(created) + JsonSerializer.Serialize(result)
            + JsonSerializer.Serialize(await sut.Settings.ViewAsync(Project, default)) + await File.ReadAllTextAsync(Path.Combine(_connections.DirectoryPath, "test-plans.json"))
            + string.Join("", (await db.AdoExports.AsNoTracking().ToListAsync()).Select(r => r.ContentJson));
        everything.Should().NotContain(Pat).And.NotContain(Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Pat)));
        _log.Lines.Should().Contain(l => l.Contains("Direct Azure DevOps transfer"));
    }

    // ── Phase 2: results fetched back from Azure DevOps ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Results_are_read_only_mapped_by_verified_link_stored_once_and_count_only_for_the_text_that_was_sent()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);
        var sent = await sut.Transfer.TransferAsync(Request(plan), "tester", default);
        var (a, b) = (sent.Cases[0], sent.Cases[1]);
        _ado.Items[90002] = (1, a.Title, "Test Case", "M2LB", "");             // same title, NOT linked: never mapped by title
        _ado.SuiteCases[70002].Add(90002);
        var done = DateTimeOffset.UtcNow.AddMinutes(-5);
        _ado.LastResults[a.TestCaseId!.Value] = ("Passed", 501, 100000, done, "Kari Tester");
        _ado.LastResults[b.TestCaseId!.Value] = ("NotApplicable", 501, 100001, done, "Kari Tester");
        _ado.LastResults[90002] = ("Passed", 501, 100002, done, "Kari Tester");
        var writes = _ado.Writes.Count;

        var fetched = await sut.Results.FetchAsync(new(Project, plan.Id), "tester", default);

        _ado.Writes.Count.Should().Be(writes, "fetching results never writes to Azure DevOps");
        fetched.New.Should().Be(2);
        fetched.NotLinked.Should().Be(1, "a test case with the same title that BirkNext did not send is not mapped");
        fetched.Results.Should().HaveCount(2).And.OnlyContain(r => r.Current && r.WebUrl!.Contains("runId=501"));
        var notApplicable = fetched.Results.Single(r => r.CaseId == b.CaseId);
        notApplicable.Outcome.Should().Be("NotApplicable");
        AdoResultOutcome.Label(notApplicable.Outcome).Should().Be("Ikke relevant", "an outcome that is not Passed is never shown as Bestått");
        (await db.ManualTestRuns.CountAsync()).Should().Be(0, "Azure DevOps results are kept apart from BirkNext runs");

        var again = await sut.Results.FetchAsync(new(Project, plan.Id), "tester", default);
        again.New.Should().Be(0);
        again.AlreadyKnown.Should().Be(2);
        (await db.ExternalTestResults.CountAsync()).Should().Be(2, "fetching again never duplicates a result");

        // The tester edits case A in BirkNext: its Azure DevOps result belongs to the text that was sent.
        var plans = Plans(db);
        var current = await plans.DraftAsync(plan.Id, default);
        await plans.SaveAsync(plan.Id, new SaveTestPlanDraftRequest
        {
            BaseRevision = current.Revision, PlanName = current.PlanName, ScenarioIds = [.. current.Cases.Select(c => c.Source.ScenarioId)], Suites = current.Suites,
            Cases = [.. current.Cases.Select(c => new TestCaseHumanPart
            {
                ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Risk = c.Risk, RequirementLinks = [.. c.RequirementLinks], RejectedSuggestions = c.RejectedSuggestions,
                Translations = c.TranslatedLanguages.ToDictionary(l => l, l => c.Translation(l)!),
                Edits = c.Source.ScenarioId == a.CaseId ? c.Edits with { Steps = ["Lock the period", "Open the report"], StepExpected = ["The period is locked", "The child is not listed"] } : c.Edits,
            })],
            RequirementCatalog = new ProjectRequirementCatalog { ProjectKey = current.ProjectKey!, Requirements = [new("FR-001", "The service MUST accept a valid report.", Spec)] },
        }, "anna", default);
        var after = await sut.Results.ListAsync(Project, plan.Id, default);
        after.Single(r => r.CaseId == a.CaseId).Current.Should().BeFalse("the case changed after it was sent: «Fra en tidligere versjon»");
        after.Single(r => r.CaseId == b.CaseId).Current.Should().BeTrue();
    }

    [Fact]
    public async Task Without_any_link_nothing_is_fetched_and_the_reason_is_plain_norwegian()
    {
        await using var db = Context();
        var plan = await PlanAsync(db);
        var sut = Build(db);
        var fetched = await sut.Results.FetchAsync(new(Project, plan.Id), "tester", default);
        fetched.Problems.Should().ContainSingle().Which.Should().StartWith("Ingen testtilfeller i planen er koblet til Azure DevOps ennå.");
        _ado.Calls.Should().BeEmpty();
    }

    [Fact]
    public void The_steps_field_is_the_azure_devops_xml_with_each_text_html_encoded()
    {
        var xml = AzureDevOpsTestPlansClient.StepsXml([("Åpne <skjema> & send", "Rapporten «lagres»"), ("Lukk", "")]);
        xml.Should().StartWith("<steps id=\"0\" last=\"3\"><step id=\"2\" type=\"ValidateStep\">");
        xml.Should().Contain("&lt;DIV&gt;&lt;P&gt;Åpne &amp;lt;skjema&amp;gt; &amp;amp; send&lt;/P&gt;&lt;/DIV&gt;");
        xml.Should().Contain("<step id=\"3\" type=\"ActionStep\">");
        System.Xml.Linq.XDocument.Parse(xml).Root!.Elements("step").Should().HaveCount(2);
    }

    // ── Test doubles from the planning tests ─────────────────────────────────────────────────────────────────────────────────────────────────
    private sealed class NoValues : ITestDataProvider
    {
        public string Id => "values";
        public string Label => "Values";
        public string Description => "In-test assets.";
        public IReadOnlyList<TestDataAsset> Assets() => [];
        public IReadOnlyList<TestDataSet> Sets() => [];
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
