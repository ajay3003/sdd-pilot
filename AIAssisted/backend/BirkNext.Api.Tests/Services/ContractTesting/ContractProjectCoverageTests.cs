using System.Net;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.SourceEvidence;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// CTH-F05: project-wide coverage aggregates the per-contract coverage. Definition rises with tests; execution and verification only with a real
/// (loopback) call; each contract counts once at its latest version even when several snapshots hold it; a newer version starts its own current
/// coverage while the older version's execution is history; another project's contracts never count; nothing executed is NOT ASSESSED.
/// </summary>
public sealed class ContractProjectCoverageTests : IAsyncLifetime
{
    private sealed class Sources : IReviewSourceEvidenceProvider
    {
        public List<IqrSourceSnapshot> All { get; } = [];
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([.. All]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(All.FirstOrDefault(s => s.Id == snapshotId));
    }

    private const string Pets = """{ "openapi": "3.0.3", "info": { "title": "Pets", "version": "1" }, "paths": { "/pets": { "get": { "responses": { "200": { "description": "ok" } } } } } }""";
    private static readonly string OrdersV2 = ContractTestDataTests.Orders.Replace("\"version\": \"1\"", "\"version\": \"2\"", StringComparison.Ordinal)
        .Replace("\"required\": [\"quantity\", \"lines\", \"delivery\"]", "\"required\": [\"quantity\", \"lines\", \"delivery\", \"status\"]", StringComparison.Ordinal);

    private readonly Sources _sources = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-cpc-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;
    private LoopbackHttpTestServer _server = null!;
    private int _requests;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _server = new LoopbackHttpTestServer(async c =>
        {
            Interlocked.Increment(ref _requests);
            using (var r = new StreamReader(c.Request.InputStream)) await r.ReadToEndAsync();
            c.Response.StatusCode = c.Request.HttpMethod == "POST" ? 201 : 200; c.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(c.Request.HttpMethod == "POST" ? """{"id":"1"}""" : "[]");
            c.Response.ContentLength64 = bytes.Length; await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close();
        });
        _server.Start();
        var authored = new AuthoredTestDataStore(_dir);
        var registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var services = new ServiceCollection();
        var db = $"cpc-{Guid.NewGuid():N}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddLogging();
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton<ExecutionRunService>();
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName).ConfigurePrimaryHttpMessageHandler(ExecutionRunService.CreatePrimaryHandler).AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddSingleton<IContractSourceFetcher>(new ContractSourceFetcher(new HttpClient()));
        services.AddSingleton<IReviewSourceEvidenceProvider>(_sources);
        services.AddSingleton(authored);
        services.AddSingleton(new TestDataCatalog([new AuthoredTestDataProvider(authored)], null, registry));
        services.AddSingleton(registry);
        services.AddScoped<TestDataResourceService>();
        services.AddScoped<ContractRegistryService>();
        services.AddScoped<ContractTestService>();
        services.AddScoped<IExecutionContractValidator, ContractCallValidator>();
        services.AddScoped<ContractTestDataService>();
        services.AddScoped<ContractProjectCoverageService>();
        services.AddScoped<ContractChainSuggestionService>();
        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private IqrSourceSnapshot Snapshot(string project, params (string File, string Text)[] files)
    {
        var s = new IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(_sources.All.Count), Archive = new($"{project}.zip", "abc", 1), Repository = new SourceRepositoryIdentity(project, project, "test"),
            EvidenceDomains = new SourceEvidenceDomainsSnapshot { Contracts = new() { Domain = SourceEvidenceDomain.Contracts,
                Contracts = [.. files.Select(f => ContractAnalyzer.ReadOpenApiDocument(f.File, f.Text)! with { Id = $"openapi:{f.File}", File = f.File })] } },
        };
        _sources.All.Add(s);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (file, text) in files)
            db.SourceContractDocuments.Add(new SourceContractDocumentRecord { Id = Guid.NewGuid(), SnapshotId = s.Id, Path = file, Text = text, CapturedAt = s.AnalyzedAt,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() });
        db.SaveChanges();
        return s;
    }

    private async Task<string> ImportGenerate(IqrSourceSnapshot s, string file)
    {
        using var scope = _services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<ContractRegistryService>().ImportFromSourceAsync(new(s.Id, $"openapi:{file}", null, null), default);
        await scope.ServiceProvider.GetRequiredService<ContractTestService>().GenerateAsync(r.Key!, r.Version, null, default);
        await scope.ServiceProvider.GetRequiredService<ContractTestDataService>().GenerateAsync(r.Key!, new(r.Version), default);
        return r.Key!;
    }

    private async Task<ProjectCoverageReport> Coverage(string project = "p", ContractTestEnvironment? env = null)
    {
        using var scope = _services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ContractProjectCoverageService>().CoverageAsync(project, env, default))!;
    }

    private static ProjectCoverageTotal T(ProjectCoverageReport r, string id) => r.Totals.Single(t => t.Id == id);

    [Fact]
    public async Task Project_coverage_counts_each_contract_once_separates_definition_execution_and_verification_and_keeps_history_apart()
    {
        var s1 = Snapshot("p", ("Orders/openapi.json", ContractTestDataTests.Orders), ("Pets/openapi.json", Pets));
        var orders = await ImportGenerate(s1, "Orders/openapi.json");
        await ImportGenerate(s1, "Pets/openapi.json");
        var q = Snapshot("q", ("Other/openapi.json", Pets));
        await ImportGenerate(q, "Other/openapi.json");

        var before = await Coverage();
        before.Contracts.Should().HaveCount(2, "project q's contract is not part of project p");
        T(before, "operations-with-tests").Should().Match<ProjectCoverageTotal>(t => t.Numerator == 4 && t.Denominator == 4 && t.Assessed);
        T(before, "operations-executed").Should().Match<ProjectCoverageTotal>(t => t.Numerator == 0 && t.NotAssessed == 4 && !t.Assessed);
        T(before, "operations-verified").Assessed.Should().BeFalse();

        // A second snapshot holding the same contracts: still counted once.
        var s2 = Snapshot("p", ("Orders/openapi.json", ContractTestDataTests.Orders), ("Pets/openapi.json", Pets));
        using (var scope = _services.CreateScope())
        {
            var reg = scope.ServiceProvider.GetRequiredService<ContractRegistryService>();
            (await reg.ImportFromSourceAsync(new(s2.Id, "openapi:Orders/openapi.json", null, null), default)).Unchanged.Should().BeTrue();
        }
        (await Coverage()).Contracts.Should().HaveCount(2);

        // One authorised loopback run of the bound POST /orders positive test.
        var env = new ContractTestEnvironment { EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local" };
        using (var scope = _services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ContractTestService>();
            var post = (await svc.ListAsync(orders, env, default)).Single(t => t.Test.Operation == "POST /orders" && t.Test.Kind == ContractTestKind.Positive);
            var run = await svc.RunAsync(new() { TestIds = [post.Test.Id], Environment = env, ConfirmMutatingCalls = true }, default);
            run.Run!.RunId.Should().NotBeNull(string.Join(" ", run.NotRun));
            for (var i = 0; i < 600 && ExecutionRunService.IsRunning(run.Run.RunId!.Value); i++) await Task.Delay(50);
        }
        _requests.Should().Be(1);
        var after = await Coverage(env: env);
        T(after, "operations-executed").Should().Match<ProjectCoverageTotal>(t => t.Numerator == 1 && t.Denominator == 4 && t.NotAssessed == 3);
        T(after, "operations-verified").Numerator.Should().Be(1, "the call passed with assessed assertions");
        T(after, "operations-with-tests").Numerator.Should().Be(4, "definition coverage is unchanged by execution");
        var row = after.Contracts.Single(c => c.Key == orders);
        row.Should().Match<ProjectCoverageContract>(c => c.ReadinessAssessed && c.Module == "Orders" && c.Coverage.LatestExecutionAt != null);

        // A changed contract: the new version's current coverage starts from its own evidence; v1's execution is history.
        var s3 = Snapshot("p", ("Orders/openapi.json", OrdersV2));
        using (var scope = _services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<ContractRegistryService>().ImportFromSourceAsync(new(s3.Id, "openapi:Orders/openapi.json", null, null), default)).Version.Should().Be(2);
        var changed = (await Coverage()).Contracts.Single(c => c.Key == orders);
        changed.Version.Should().Be(2);
        changed.Coverage.Metrics.Single(m => m.Id == "operations-executed").Numerator.Should().Be(0, "a v1 execution never counts for v2");
        changed.History.Should().ContainSingle().Which.Should().Match<HistoricalCoverage>(h => h.Version == 1 && h.Executed == 1);
        changed.TestsOnOlderVersions.Should().BeGreaterThan(0);
    }

    private const string Shop = """
        { "openapi": "3.0.3", "info": { "title": "Shop", "version": "1" },
          "paths": {
            "/orders": { "post": { "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/NewOrder" } } } },
                                   "responses": { "201": { "description": "created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Order" } } } } } },
                         "get": { "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Order" } } } } } } } },
            "/orders/{orderId}": { "get": { "parameters": [ { "name": "orderId", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "200": { "description": "ok" } } },
                                   "put": { "parameters": [ { "name": "orderId", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "200": { "description": "ok" } } },
                                   "delete": { "parameters": [ { "name": "orderId", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "204": { "description": "gone" } } } },
            "/notes": { "post": { "responses": { "201": { "description": "created", "content": { "application/json": { "schema": { "type": "object", "properties": { "id": { "type": "string" } } } } } } } } },
            "/notes/{noteId}": { "get": { "parameters": [ { "name": "noteId", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "200": { "description": "ok" } } } },
            "/tags": { "post": { "responses": { "201": { "description": "created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Tag" } } } } } } },
            "/tags/{tagId}": { "get": { "parameters": [ { "name": "tagId", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "200": { "description": "ok" } } } } },
          "components": { "schemas": {
            "NewOrder": { "type": "object", "required": ["item"], "properties": { "item": { "type": "string", "enum": ["book"] } } },
            "Order": { "type": "object", "required": ["id"], "properties": { "id": { "type": "string" }, "item": { "type": "string" } } },
            "Tag": { "type": "object", "properties": { "uuid": { "type": "string", "format": "uuid" } } } } } }
        """;

    [Fact]
    public async Task Chain_suggestions_need_a_typed_response_field_never_add_delete_and_are_accepted_only_as_drafts_that_cannot_run()
    {
        using var scope = _services.CreateScope();
        var reg = scope.ServiceProvider.GetRequiredService<ContractRegistryService>();
        var svc = scope.ServiceProvider.GetRequiredService<ContractTestService>();
        var suggest = scope.ServiceProvider.GetRequiredService<ContractChainSuggestionService>();
        var key = (await reg.ImportAsync(new() { FileName = "shop.json", Content = Shop }, default)).Key!;
        await svc.GenerateAsync(key, 1, null, default);

        var list = (await suggest.SuggestAsync(key, null, default))!;
        list.Select(s => s.Purpose).Should().BeEquivalentTo(["Create, update, read: POST /orders → PUT /orders/{orderId} → GET /orders/{orderId}", "Search then details: GET /orders → GET /orders/{orderId}"],
            "an untyped response (notes) and an unrelated uuid field (tags) never make a chain");
        list.Should().NotContain(s => s.Steps.Any(st => st.Operation.StartsWith("DELETE", StringComparison.Ordinal)), "no automatic destructive step");
        var create = list.Single(s => s.Purpose.StartsWith("Create", StringComparison.Ordinal));
        create.Should().Match<ChainSuggestion>(s => s.Mutating && s.Confidence == "Medium" && s.Evidence.Any(e => e.Contains("type Order with field \"id\"")));
        create.Steps[0].Extract.Single().Should().Be(new OutputExtraction("id", OutputSource.JsonBody, "$.id", "String"));
        create.Steps[2].Inputs.Single().Should().Be(new ContractChainInput("orderId", "step1", "id"));
        list.Single(s => s.Purpose.StartsWith("Search", StringComparison.Ordinal)).Steps[0].Extract.Single().Path.Should().Be("$[0].id");

        var accepted = await suggest.AcceptAsync(key, create.Id, default);
        accepted.Chain.Should().Match<ContractTestChain>(c => c.State == ContractChainState.Draft && c.SuggestionId == create.Id && c.Steps.Count == 3);
        (await suggest.AcceptAsync(key, create.Id, default)).Chain!.Id.Should().Be(accepted.Chain!.Id, "accepting again creates no duplicate");
        (await suggest.SuggestAsync(key, null, default))!.Single(s => s.Id == create.Id).AcceptedChainId.Should().Be(accepted.Chain.Id.ToString("D"));
        var env = new ContractTestEnvironment { EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local" };
        (await svc.RunChainAsync(accepted.Chain.Id, new() { Environment = env, ConfirmMutatingCalls = true }, default)).NotRun.Should().ContainSingle(n => n.Contains("draft"));
        _requests.Should().Be(0, "a suggestion or a draft never sends anything");

        var search = list.Single(s => s.Purpose.StartsWith("Search", StringComparison.Ordinal));
        await suggest.RejectAsync(key, search.Id, "reviewer", default);
        (await suggest.SuggestAsync(key, null, default))!.Should().NotContain(s => s.Id == search.Id);
    }
}
