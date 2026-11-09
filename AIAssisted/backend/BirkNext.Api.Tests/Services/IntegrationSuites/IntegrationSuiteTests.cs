using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationSuites;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ManualReview;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.ProjectImport;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationSuites;

/// <summary>
/// Integration suite generation over the real owners (Source Analysis snapshot, Contract Registry, Contract Testing, Test Data, manual review,
/// Execution Evidence): explicit snapshot identity, idempotent generation, drift on a new snapshot, constraint-only synthetic Test Data,
/// declared-server/auth prefill without credentials, XSD dependency checks, journey-pack stages with real executor/observer coverage,
/// Kode 6/7 reuse of the M01 requirements, reviews, no network during generation, authorised execution only, and no false PASS.
/// Synthetic project and a loopback target with an independent request ledger.
/// </summary>
public sealed class IntegrationSuiteTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-suites-" + Guid.NewGuid().ToString("N"));
    private LoopbackHttpTestServer _server = null!;
    private ServiceProvider _services = null!;
    private readonly ConcurrentQueue<(string Method, string Path)> _ledger = new();
    private readonly string _db = $"suites-{Guid.NewGuid():N}";

    private const string OpenApiV1 = """
        { "openapi": "3.0.3", "info": { "title": "Ward API", "version": "1.0" }, "servers": [ { "url": "https://ward.example.test/api" } ],
          "components": { "securitySchemes": { "bearer": { "type": "http", "scheme": "bearer" } },
            "schemas": { "Health": { "type": "object", "required": ["status"], "properties": { "status": { "type": "string" } } },
                         "Child": { "type": "object", "required": ["id"], "properties": { "id": { "type": "integer" } } } } },
          "paths": {
            "/health": { "get": { "operationId": "health", "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Health" } } } } } } },
            "/children": { "get": { "security": [ { "bearer": [] } ], "parameters": [
                { "name": "status", "in": "query", "required": true, "schema": { "type": "string", "enum": ["active", "inactive"] } },
                { "name": "fodselsnummer", "in": "query", "required": false, "schema": { "type": "string" } },
                { "name": "fromDate", "in": "query", "required": true, "schema": { "type": "string", "format": "date" } } ],
              "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Child" } } } } } } } } } }
        """;

    private static readonly string OpenApiV2 = OpenApiV1.Replace("\"version\": \"1.0\"", "\"version\": \"2.0\"", StringComparison.Ordinal)
        .Replace("\"/health\": {", "\"/ping\": { \"get\": { \"responses\": { \"200\": { \"description\": \"ok\" } } } }, \"/health\": {", StringComparison.Ordinal);

    private const string Graphql = "type Query { child(id: ID!): Child }\ntype Child { id: ID! name: String }\n";
    private const string Xsd = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:ward:report" xmlns="urn:ward:report" elementFormDefault="qualified"><xs:element name="Report"><xs:complexType><xs:sequence><xs:element name="Period" type="xs:string"/><xs:element name="Hours" type="xs:decimal"/></xs:sequence></xs:complexType></xs:element></xs:schema>""";
    private const string BrokenXsd = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:lib="urn:ward:lib" targetNamespace="urn:ward:msg" xmlns="urn:ward:msg"><xs:import namespace="urn:ward:lib" schemaLocation="lib.xsd"/><xs:element name="Message" type="lib:Envelope"/></xs:schema>""";

    private static (string, string)[] Project(string openApi) =>
    [
        ("ward/src/Ward.Api/Ward.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ("ward/src/Ward.Api/Program.cs", "var app = WebApplication.Create(); app.MapGet(\"/health\", () => new { status = \"ok\" }); app.Run();"),
        ("ward/src/Ward.Api/openapi.json", openApi),
        ("ward/src/Ward.Api/schema.graphql", Graphql),
        ("ward/schemas/report.xsd", Xsd),
        ("ward/schemas/message.xsd", BrokenXsd),
        ("other/src/Other.Api/openapi.json", OpenApiV1.Replace("Ward API", "Other API", StringComparison.Ordinal)),
    ];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _server = new LoopbackHttpTestServer(async c =>
        {
            _ledger.Enqueue((c.Request.HttpMethod, c.Request.Url!.AbsolutePath));
            var body = c.Request.Url.AbsolutePath == "/health" ? """{"status":"ok"}""" : "[]";
            c.Response.StatusCode = 200; c.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        });
        _server.Start();
        var authored = new AuthoredTestDataStore(_dir);
        var registryStore = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new AuthoredTestDataProvider(authored)], null, registryStore);
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_db));
        services.AddLogging();
        services.AddScoped<IqrSourceStore>();
        services.AddScoped<IReviewSourceEvidenceProvider>(sp => new ReviewSourceEvidenceProvider(sp.GetRequiredService<IqrSourceStore>()));
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName).ConfigurePrimaryHttpMessageHandler(ExecutionRunService.CreatePrimaryHandler).AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton<ExecutionRunService>();
        services.AddSingleton<IContractSourceFetcher>(new ContractSourceFetcher(new HttpClient(ContractSourcePolicy.CreateHandler())));
        services.AddSingleton(authored);
        services.AddSingleton(catalog);
        services.AddScoped<TestDataResourceService>();
        services.AddScoped<ContractRegistryService>();
        services.AddScoped<ContractTestService>();
        services.AddScoped<IExecutionContractValidator, ContractCallValidator>();
        services.AddScoped<ManualReviewDecisionStore>();
        services.AddSingleton<IIntegrationJourneyPackRegistry>(new IntegrationJourneyPackRegistry([new FakeSkolePack()]));
        services.AddSingleton(new IntegrationJourneyRunner([new FakeAltinnExecutor()], [new FakeTelemetryObserver()], TimeProvider.System));
        services.AddScoped<IntegrationSuiteService>();
        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<Guid> Snapshot(string openApi, string archive = "ward.zip")
    {
        using var scope = _services.CreateScope();
        var bytes = ProjectImportServiceTests.Zip(Project(openApi));
        var ws = IqrSourceArchiveReader.ReadDetailed(archive, bytes, default).Workspace!;
        return (await scope.ServiceProvider.GetRequiredService<IqrSourceStore>().AnalyzeValidatedAsync(null, IqrSourceStore.SourceAnalysisOwner, archive, bytes, ws, default)).Id;
    }

    private async Task<IntegrationSuiteGenerateResult> Generate(IntegrationSuiteGenerateRequest r)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IntegrationSuiteService>().GenerateAsync(r, "Tester", default);
    }

    private T With<T>(Func<IntegrationSuiteService, T> f)
    {
        using var scope = _services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<IntegrationSuiteService>());
    }

    private static IntegrationSuiteGenerateRequest Api(Guid snapshot) => new() { SnapshotId = snapshot, ScopeId = IntegrationSuiteComposer.ApiScope, Modules = ["ward"] };

    // ── ATG-12 snapshot identity ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_suite_needs_an_explicit_existing_snapshot_and_records_its_identity()
    {
        (await Generate(Api(Guid.Empty))).Error.Should().Contain("Choose an analysed source snapshot");
        (await Generate(Api(Guid.NewGuid()))).Error.Should().Contain("not found");
        var snap = await Snapshot(OpenApiV1);
        var suite = (await Generate(Api(snap))).Suite!;
        suite.Snapshot.Should().Match<SuiteSnapshotProvenance>(p => p.SnapshotId == snap && p.ArchiveFileName == "ward.zip" && p.ArchiveSha256.Length == 64 && p.EvidenceDomains.Contains("Contracts"));
        suite.Snapshot.Modules.Should().Contain(["ward", "other"]);
        (await Generate(new() { SnapshotId = snap, ScopeId = IntegrationSuiteComposer.SkoleNaervaerScope })).Suite!.Snapshot.MissingSourceAreas
            .Should().Contain("SkoleService/ is not in this archive", "the scope's missing modules are shown, never assumed present");
    }

    // ── ATG-01/02/05/07/08 generation over existing owners ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generation_reuses_registry_and_contract_testing_and_prefills_configuration_without_credentials()
    {
        var snap = await Snapshot(OpenApiV1);
        var suite = (await Generate(Api(snap))).Suite!;
        suite.Contracts.Should().HaveCount(2, "the ward module's OpenAPI and GraphQL — never the other module's").And.OnlyContain(c => c.ContractKey != null && c.ContractVersion == 1);
        suite.Contracts.Should().NotContain(c => c.SourceFile.StartsWith("other/", StringComparison.Ordinal));
        suite.Tests.Where(t => t.Kind == "Positive").Select(t => t.Operation).Should().Contain(["GET /health", "GET /children"]);
        suite.Tests.Should().Contain(t => t.Kind != "Positive", "contract-derived negative proposals are offered through the existing rules");
        suite.Tests.Should().OnlyContain(t => t.ContractRef.Contains("@v1"));
        var config = suite.Configuration;
        config.Single(c => c.Field == "Declared server").Should().Match<SuiteConfigurationValue>(c => c.Value == "https://ward.example.test/api" && c.Origin == SuiteValueOrigin.Inferred && c.NeedsReview);
        config.Single(c => c.Field == "Authentication requirement" && c.Key.Contains("ward-api", StringComparison.OrdinalIgnoreCase)).Should().Match<SuiteConfigurationValue>(c => c.Value!.Contains("bearer") && c.Origin == SuiteValueOrigin.Discovered);
        System.Text.Json.JsonSerializer.Serialize(suite).Should().NotContainAny("password", "client_secret", "Bearer ey", "token=");
        suite.Review.Should().Contain(r => r.Area == "Target environment");
        _ledger.Should().BeEmpty("generation never sends a request");
    }

    [Fact]
    public async Task Regeneration_is_idempotent_and_a_new_snapshot_becomes_a_new_revision_with_drift()
    {
        var snap = await Snapshot(OpenApiV1);
        var first = await Generate(Api(snap));
        var again = await Generate(Api(snap));
        again.Reused.Should().BeTrue();
        again.Suite!.Id.Should().Be(first.Suite!.Id);
        again.Suite.Tests.Select(t => t.TestId).Should().BeEquivalentTo(first.Suite.Tests.Select(t => t.TestId), "no duplicate definitions");
        using (var scope = _services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().ContractTests.CountAsync()).Should().Be(first.Suite.Tests.Count);

        var snap2 = await Snapshot(OpenApiV2, "ward.zip");
        var next = (await Generate(Api(snap2))).Suite!;
        next.SuiteKey.Should().Be(first.Suite.SuiteKey, "the same project and scope");
        next.Revision.Should().Be(2);
        next.Drift.Should().Contain(d => d.Contains("v1 → v2") && d.Contains("stay on v1"), string.Join(" | ", next.Drift.Concat(first.Suite.Contracts.Concat(next.Contracts).Select(c => $"{c.SourceFile} {c.ContractKey} v{c.ContractVersion} {c.ImportOutcome}")))).And.Contain(d => d.StartsWith("Source snapshot changed", StringComparison.Ordinal));
        next.Tests.Where(t => t.Operation == "GET /health").Should().OnlyContain(t => t.ContractRef.EndsWith("@v1", StringComparison.Ordinal), "an approved test is never silently rebound to v2");
        (await With(s => s.GetAsync(first.Suite.Id, default)))!.Revision.Should().Be(1, "history is kept");
    }

    [Fact]
    public async Task Concurrent_generation_of_one_suite_stores_one_revision()
    {
        var snap = await Snapshot(OpenApiV1);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Generate(Api(snap))));
        results.Select(r => r.Suite!.Id).Distinct().Should().ContainSingle();
        using var scope = _services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().IntegrationSuites.CountAsync()).Should().Be(1);
    }

    // ── ATG-06 synthetic Test Data ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Synthetic_test_data_comes_only_from_declared_constraints_is_seeded_unapproved_and_never_overwritten()
    {
        var snap = await Snapshot(OpenApiV1);
        var suite = (await Generate(Api(snap))).Suite!;
        var item = suite.TestData.Single(d => d.Role.Contains("GET /children"));
        item.Generated.Should().BeTrue();
        item.Approved.Should().BeFalse("generated values are drafts until a tester approves them");
        item.Values.Single(v => v.Name == "status").Should().Match<SuiteGeneratedValue>(v => v.Value == "active" && v.Rule == "first declared enum value");
        item.Values.Single(v => v.Name == "fromDate").Value.Should().MatchRegex(@"^2026-\d\d-\d\d$");
        item.Values.Should().NotContain(v => v.Name == "fodselsnummer");
        item.Prerequisite.Should().Contain("fodselsnummer: person identity");
        suite.Review.Should().Contain(r => r.Area == "Test Data" && r.Question.Contains(item.Asset!));

        var (again, _) = IntegrationSuiteComposer.Synthesize([new ContractParameter("fromDate", "query", true, "string(date)")], 20261109);
        var (again2, _) = IntegrationSuiteComposer.Synthesize([new ContractParameter("fromDate", "query", true, "string(date)")], 20261109);
        again.Single().Value.Should().Be(again2.Single().Value, "seeded: the same seed gives the same value");
        var regenerated = (await Generate(Api(snap) with { Seed = 1 })).Suite!;
        regenerated.TestData.Single(d => d.Role.Contains("GET /children")).State.Should().Be("Existing draft kept (not overwritten)");
    }

    [Theory]
    [InlineData("Authorization", "header", "string")]
    [InlineData("fnr", "query", "string")]
    [InlineData("duf_nummer", "query", "string")]
    [InlineData("birkId", "path", "string")]
    public void Credentials_and_identities_are_never_synthesised(string name, string location, string type)
    {
        var (values, _) = IntegrationSuiteComposer.Synthesize([new ContractParameter(name, location, true, type)], 7);
        values.Should().BeEmpty();
    }

    // ── XSD / journey stages / Kode 6/7 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Xsd_references_report_unresolved_dependencies_and_block_schema_readiness()
    {
        var snap = await Snapshot(OpenApiV1);
        var suite = (await Generate(Api(snap))).Suite!;
        suite.XmlSchemas.Single(x => x.File.EndsWith("report.xsd")).Should().Match<SuiteXmlSchemaRef>(x => x.DependenciesResolved && x.RootElements.Contains("Report") && x.TargetNamespace == "urn:ward:report");
        suite.XmlSchemas.Single(x => x.File.EndsWith("message.xsd")).Should().Match<SuiteXmlSchemaRef>(x => !x.DependenciesResolved && x.State.StartsWith("Blocked"));
        suite.Readiness.Single(d => d.Name == "Schemas").State.Should().Be(SuiteItemReadiness.Blocked);
    }

    [Fact]
    public async Task Journey_stages_follow_the_pack_and_unsupported_stages_never_become_ready()
    {
        var snap = await Snapshot(OpenApiV1);
        var suite = (await Generate(new() { SnapshotId = snap, ScopeId = IntegrationSuiteComposer.SkoleNaervaerScope })).Suite!;
        suite.Stages.Select(s => s.StageId).Should().Equal("altinn", "mu", "mm", "skole-intake", "person-tjeneste", "period-lock", "bufdata");
        suite.Stages.Single(s => s.StageId == "altinn").Coverage.Should().Be(SuiteStageCoverage.Executable);
        suite.Stages.Single(s => s.StageId == "altinn").Blockers.Should().Contain("No Target Environment selected");
        suite.Stages.Single(s => s.StageId == "mu").Should().Match<SuiteStage>(s => s.Coverage == SuiteStageCoverage.Unsupported && s.Readiness == SuiteItemReadiness.NotSupported && s.ExecutorObserver.Contains("missing adapter"));
        suite.Stages.Single(s => s.StageId == "skole-intake").Coverage.Should().Be(SuiteStageCoverage.Observable, "telemetry observes, it does not cause");
        suite.Stages.Should().NotContain(s => s.Readiness == SuiteItemReadiness.Ready);
        suite.Status.Should().NotBe(SuiteStatus.ReadyToExecute);
        suite.Readiness.Single(d => d.Name == "Correlation").State.Should().Be(SuiteItemReadiness.Blocked);
        suite.Security.Should().Contain(s => s.ScenarioId == "K67-05" && s.Triage == SuiteTriage.NeedsDomainDecision && s.OpenQuestion!.Contains("aggregate"));
        suite.Security.Should().Contain(s => s.ScenarioId == "K67-03" && s.Triage == SuiteTriage.NotSupported);
    }

    [Fact]
    public void Kode67_reuses_the_M01_requirements_and_only_becomes_runnable_with_identities_executor_observer_and_an_approved_rule()
    {
        var runId = Guid.NewGuid();
        var consumer = new SecurityClassificationConsumerAssessment("skolenaervaer", "Skolenærvær", "ctx", runId, DateTimeOffset.UnixEpoch,
        [
            new("UnauthorizedRuntimeDenial", "Unauthorized identity gets no graded child", SecurityRequirementState.NotTested, "", "Run the runtime verification", "Security Classification review"),
            new("AuthorizedRuntimeAccess", "Authorized identity sees the child", SecurityRequirementState.Verified, "", "", "Security Classification review"),
            new("bufdata-exclusion", "Bufdata dataset never holds fødselsnummer or DUF", SecurityRequirementState.NotTested, "", "", "Skolenærvær") { DomainSpecific = true },
        ], "summary");
        IntegrationSuiteComposer.SecurityInputs Inputs(bool ids, bool observer, Dictionary<string, string?> decisions) =>
            new(consumer, ids, ids, true, AltinnSubmissionExecutable: true, MuMmObservable: false, IntakeObservedPerMessage: observer, BufdataVerifierRegistered: false, [], decisions);

        var none = IntegrationSuiteComposer.Security(Inputs(false, false, []));
        none.Where(s => s.ScenarioId.StartsWith("M01:")).Select(s => s.ReviewRunId).Should().OnlyContain(id => id == runId, "existing coverage is referenced by its review run, never copied");
        none.Single(s => s.ScenarioId == "M01:AuthorizedRuntimeAccess").Triage.Should().Be(SuiteTriage.CanRunNow);
        var k67_01 = none.Single(s => s.ScenarioId == "K67-01");
        k67_01.Triage.Should().Be(SuiteTriage.NeedsDomainDecision, "the intake rule for a Kode 6/7 report is not approved");
        k67_01.Prerequisites.Should().Contain(p => p.Contains("Kode 6 child")).And.Contain(p => p.Contains("observer"));
        none.Single(s => s.ScenarioId == "K67-02").Levels.Should().Equal("Kode 7");
        none.Single(s => s.ScenarioId == "K67-06").Prerequisites.Should().Contain(p => p.StartsWith("M01 runtime probe"));
        none.Single(s => s.ScenarioId == "K67-07").Triage.Should().Be(SuiteTriage.WaitingForPrerequisite, "identities are still missing");

        var decided = IntegrationSuiteComposer.Security(Inputs(true, false, new() { ["security:intake-rule"] = "Acceptable" }));
        decided.Single(s => s.ScenarioId == "K67-01").Should().Match<SuiteSecurityScenario>(s => s.Triage == SuiteTriage.WaitingForPrerequisite && s.Basis == SuiteScenarioBasis.ReviewedBusinessRule,
            "without a per-message observer a reviewed rule still cannot be verified");
        var full = IntegrationSuiteComposer.Security(Inputs(true, true, new() { ["security:intake-rule"] = "Acceptable" }));
        full.Single(s => s.ScenarioId == "K67-01").Triage.Should().Be(SuiteTriage.CanRunNow);
        full.Single(s => s.ScenarioId == "K67-07").Triage.Should().Be(SuiteTriage.CanRunNow);
    }

    // ── Review, execution, evidence ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reviews_are_recorded_against_the_suite_and_survive_regeneration()
    {
        var snap = await Snapshot(OpenApiV1);
        var suite = (await Generate(Api(snap))).Suite!;
        var item = suite.Review.First(r => r.Area == "Configuration");
        var (decision, error) = await With(s => s.ReviewAsync(suite.Id, new(item.Key, "Acceptable", "Matched to the local profile"), "Tester", default));
        error.Should().BeNull();
        decision!.Current.Should().BeTrue();
        (await With(s => s.GetAsync(suite.Id, default)))!.Review.Single(r => r.Key == item.Key).Should().Match<SuiteReviewItem>(r => r.Decision == "Acceptable" && r.DecidedBy == "Tester");
        (await Generate(Api(snap) with { IncludeNegatives = false })).Suite!.Review.Should().NotContain(r => r.Decision == "Acceptable", "other option sets are other suites");
        (await With(s => s.ReviewAsync(suite.Id, new("nope", "Acceptable", null), "Tester", default))).Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Execution_needs_authorisation_runs_only_suite_tests_and_links_evidence_without_false_pass()
    {
        var snap = await Snapshot(OpenApiV1);
        var suite = (await Generate(Api(snap))).Suite!;
        var health = suite.Tests.Single(t => t.Operation == "GET /health" && t.Kind == "Positive");
        var env = new IntegrationSuiteExecuteRequest { TestIds = [health.TestId], EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local" };

        (await With(s => s.ExecuteAsync(suite.Id, env, "Tester", default))).State.Should().Be("NotAuthorized");
        (await With(s => s.ExecuteAsync(suite.Id, env with { TestIds = [Guid.NewGuid()], Authorized = true }, "Tester", default))).State.Should().Be("Refused");
        _ledger.Should().BeEmpty("nothing is sent without authorisation");

        var result = await With(s => s.ExecuteAsync(suite.Id, env with { Authorized = true }, "Tester", default));
        result.RunId.Should().NotBeNull(result.Message);
        await WaitForRunAsync(result.RunId!.Value);
        _ledger.Should().ContainSingle().Which.Should().Be(("GET", "/health"), "exactly the authorised call reached the target");
        var read = (await With(s => s.GetAsync(suite.Id, default)))!;
        var calls = (await _services.CreateScope().ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().GetAsync(result.RunId.Value, new ExecutionCallQuery(0, 50), default))!.Calls;
        // The source-backed contract cannot evaluate every schema keyword, so the engine reports NotAssessed — never a pass by default.
        read.Runs.Should().ContainSingle(r => r.RunId == result.RunId && r.State == "1 notassessed of 1",
            string.Join(" | ", calls.SelectMany(c => c.Assertions).Select(a => $"{a.Kind} {a.Target} {a.Outcome} {a.Explanation}")));
        read.Outcome.Should().Be(SuiteExecutionOutcome.Executed, "an executed call whose assertions are not all assessed is not verification");
    }

    private async Task WaitForRunAsync(Guid runId)
    {
        for (var i = 0; i < 100; i++)
        {
            using var scope = _services.CreateScope();
            var detail = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().GetAsync(runId, new ExecutionCallQuery(), default);
            if (detail is not null && detail.Calls.Count > 0 && detail.Calls.All(c => c.State is not (ExecutionState.Planned or ExecutionState.Queued or ExecutionState.Running))) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("run did not finish");
    }

    // ── Fakes for the journey pack and its registered executor/observer (the real pack is exercised in acceptance) ───────────

    private sealed class FakeSkolePack : IIntegrationJourneyPack
    {
        private static JourneyStepDefinition S(string id, JourneyStepKind kind) => new(id, id, kind, "x", "x", Mandatory: true);
        public string PackId => "skolenaervaer";
        public string PackVersion => "test";
        public string DisplayName => "Skolenærvær (test)";
        public string Description => "";
        public IReadOnlyList<ArchitectureResponsibilityRule> Rules => [];
        public IReadOnlyList<string> Limitations => [];
        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => false;
        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>([]);
        public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } =
        [
            new() { PackId = "skolenaervaer", JourneyId = "rapportmottak", DisplayName = "Rapportmottak", Steps =
                [.. new[] { "altinn-auth", "altinn-instance", "altinn-data", "altinn-validate", "altinn-submit", "altinn-receipt" }.Select(id => S(id, JourneyStepKind.ExternalSubmission)),
                 S("mu-receive", JourneyStepKind.MessageTransport), S("mu-queue", JourneyStepKind.MessageTransport), S("mm-retrieve", JourneyStepKind.PayloadRetrieval),
                 S("mm-decrypt", JourneyStepKind.Decryption), S("mm-xsd", JourneyStepKind.StructuralValidation), S("mm-map", JourneyStepKind.Mapping),
                 S("report-receive", JourneyStepKind.DomainIntake), S("report-person", JourneyStepKind.IdentityLookup), S("report-state", JourneyStepKind.DomainState)] },
            new() { PackId = "skolenaervaer", JourneyId = "periodeavslutning", DisplayName = "Periodeavslutning", Steps =
                [S("period-close", JourneyStepKind.DomainState), S("dataset-prepare", JourneyStepKind.DomainState), S("publish", JourneyStepKind.Publication),
                 S("broker", JourneyStepKind.BrokerDelivery), S("bufdata-activity", JourneyStepKind.ConsumerProcessing), S("bufdata-processed", JourneyStepKind.ConsumerProcessing),
                 S("dataset-verify", JourneyStepKind.DownstreamVerification), S("dataset-pii", JourneyStepKind.DownstreamVerification)] },
        ];
    }

    private sealed class FakeAltinnExecutor : IJourneyStepExecutor
    {
        public bool CanExecute(JourneyStepContext context, out string reason) { reason = ""; return context.Step.StepId.StartsWith("altinn-", StringComparison.Ordinal); }
        public Task<JourneyStepResult> ExecuteAsync(JourneyStepContext context, CancellationToken ct) => throw new InvalidOperationException("never executed by generation");
    }

    private sealed class FakeTelemetryObserver : IJourneyStepObserver
    {
        public bool CanObserve(JourneyStepContext context, out string reason) { reason = ""; return context.Step.StepId is "report-receive" or "report-state"; }
        public Task<JourneyStepResult> ObserveAsync(JourneyStepContext context, CancellationToken ct) => throw new InvalidOperationException("never observed by generation");
    }
}
