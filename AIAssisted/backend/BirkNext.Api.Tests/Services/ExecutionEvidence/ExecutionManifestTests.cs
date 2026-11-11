using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.Services.ContractTesting;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.ExecutionEvidence;

/// <summary>
/// Immutable execution manifests and dependency pinning, end to end against a synthetic loopback API: a contract-test run records exactly the
/// contract version, test revision, Test Data version and endpoint binding it executed, with fingerprints computed independently here from the
/// stored records (the oracle is the database and the Test Data store, never the manifest service). Applicability is then evaluated after real
/// changes: a historical PASS stays a PASS and is labelled historical, never current. Caller-declared references never make a result current.
/// </summary>
public sealed class ExecutionManifestTests : IAsyncLifetime
{
    private const string Key = "synthetic-children-api";
    private LoopbackHttpTestServer _server = null!;
    private readonly ConcurrentQueue<(string PathAndQuery, string? Revision)> _seen = new();
    private ServiceProvider _services = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-manifest-" + Guid.NewGuid().ToString("N"));
    private AuthoredTestDataStore _authored = null!;
    private TestDataRegistryStore _registry = null!;
    private TestDataCatalog _catalog = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _server = new LoopbackHttpTestServer(Handle);
        _server.Start();
        _authored = new AuthoredTestDataStore(_dir);
        _registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        _catalog = new TestDataCatalog([new AuthoredTestDataProvider(_authored)], null, _registry);
        var services = new ServiceCollection();
        var db = $"manifest-{Guid.NewGuid():N}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddLogging();
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddScoped(sp => new ExecutionManifestService(sp.GetRequiredService<AppDbContext>(), _authored));
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName).ConfigurePrimaryHttpMessageHandler(ExecutionRunService.CreatePrimaryHandler).AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton<ExecutionRunService>();
        services.AddSingleton<IContractSourceFetcher>(new ContractSourceFetcher(new HttpClient(ContractSourcePolicy.CreateHandler())));
        services.AddSingleton(_authored);
        services.AddSingleton(_catalog);
        services.AddScoped<ContractRegistryService>();
        services.AddScoped<ContractTestService>();
        services.AddScoped<IExecutionContractValidator, ContractCallValidator>();
        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task Handle(HttpListenerContext c)
    {
        _seen.Enqueue((c.Request.Url!.PathAndQuery, c.Request.Headers["X-Test-Revision"]));
        var (status, text) = c.Request.Url.AbsolutePath == "/children/7" ? (200, """{"id":7,"name":"Testbarn"}""") : (404, """{"error":"no route"}""");
        c.Response.StatusCode = status; c.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(text);
        c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes);
        c.Response.Close();
    }

    private T Get<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();
    private ContractTestEnvironment Local => new() { EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local" };
    private static string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private async Task<ExecutionRunDetail> WaitAsync(Guid runId)
    {
        for (var i = 0; i < 600 && ExecutionRunService.IsRunning(runId); i++) await Task.Delay(50);
        using var scope = _services.CreateScope();
        return (await Get<ExecutionEvidenceStore>(scope).GetAsync(runId, new(Take: 200), default))!;
    }

    /// <summary>Imports v1, generates the GET /children/{id} positive test, binds approved Test Data children@v1 (→ test r2).</summary>
    private async Task<ContractTestDefinition> PrepareAsync()
    {
        using var scope = _services.CreateScope();
        (await Get<ContractRegistryService>(scope).ImportAsync(new() { FileName = "children.yaml", Content = ContractTestingTests.V1Yaml }, default)).Imported.Should().BeTrue();
        var tests = Get<ContractTestService>(scope);
        var (created, _) = await tests.GenerateAsync(Key, null, ["GET /children/{id}"], default);
        var test = created.Single(t => t.Kind == ContractTestKind.Positive);
        _authored.Create(new TestDataResourceDraft { Alias = "children", Label = "children",
            Content = new() { Kind = TestDataResourceKind.Dataset, Members = [new("id", null, new("id", TestDataPrimitive.Int32, "7"))] } }).Outcome.Should().Be(AuthoredTestDataStore.Outcome.Saved);
        Approve("children");
        var (bound, error) = await tests.BindAsync(test.Id, new("children", 1), default);
        error.Should().BeNull();
        bound!.Revision.Should().Be(2);
        return bound;
    }

    private void Approve(string alias)
    {
        // With an approved earlier version the catalog shows that version and holds the newest one as its draft: approve the newest.
        var asset = _catalog.Assets().Single(a => a.Alias == alias);
        var newest = asset.Draft ?? asset;
        _registry.SetLifecycle(new TestDataLifecycleRecord { AssetId = asset.Id, ApprovedFingerprint = TestDataCatalog.ApprovalFingerprint(newest), ApprovedAt = DateTimeOffset.UtcNow });
    }

    private async Task<ExecutionRunDetail> RunAsync(Guid testId, Dictionary<Guid, int>? pinned = null, string? basePath = null)
    {
        using var scope = _services.CreateScope();
        var result = await Get<ContractTestService>(scope).RunAsync(new() { TestIds = [testId], Environment = Local, PinnedRevisions = pinned ?? [], BasePath = basePath }, default);
        result.Run.Should().NotBeNull(string.Join("; ", result.NotRun));
        return await WaitAsync(result.Run!.RunId!.Value);
    }

    private async Task<(ExecutionManifest? Manifest, ExecutionApplicability? Applicability)> ReadAsync(Guid runId)
    {
        using var scope = _services.CreateScope();
        var manifests = Get<ExecutionManifestService>(scope);
        return (await manifests.GetAsync(runId, default), await manifests.ApplicabilityAsync(runId, default));
    }

    private async Task<string> ManifestRowJsonAsync(Guid runId)
    {
        using var scope = _services.CreateScope();
        return await Get<AppDbContext>(scope).ExecutionManifests.Where(m => m.RunId == runId).Select(m => m.DocumentJson).SingleAsync();
    }

    [Fact]
    public async Task A_contract_test_run_pins_every_definition_it_executed_with_independently_verified_fingerprints()
    {
        var test = await PrepareAsync();
        var run = await RunAsync(test.Id);
        run.Run.State.Should().Be(ExecutionState.Passed, string.Join("; ", run.Calls.Select(c => $"{c.State} {c.Reason}")));

        var (manifest, applicability) = await ReadAsync(run.Run.RunId);
        manifest.Should().NotBeNull();
        using var scope = _services.CreateScope();
        var db = Get<AppDbContext>(scope);
        var contractSha = await db.ApiContractVersions.Where(v => v.Version == 1).Select(v => v.Sha256).SingleAsync();
        var revisionJson = await db.ContractTestRevisions.Where(r => r.TestId == test.Id && r.Revision == 2).Select(r => r.DocumentJson).SingleAsync();

        manifest!.Dependencies.Should().ContainSingle(d => d.Kind == ManifestDependencyKind.Contract).Which
            .Should().Match<ManifestDependency>(d => d.Ref == "synthetic-children-api@v1" && d.ContentFingerprint == contractSha);
        manifest.Dependencies.Should().ContainSingle(d => d.Kind == ManifestDependencyKind.ContractTest).Which
            .Should().Match<ManifestDependency>(d => d.Ref == ContractIdentity.DefinitionRef(test.Id, 2) && d.Version == 2 && d.ContentFingerprint == Sha(revisionJson));
        manifest.Dependencies.Should().ContainSingle(d => d.Kind == ManifestDependencyKind.TestData).Which
            .Should().Match<ManifestDependency>(d => d.Ref == "children@v1" && d.ContentFingerprint == _authored.Find("children")!.Versions[0].Fingerprint);
        manifest.Dependencies.Should().ContainSingle(d => d.Kind == ManifestDependencyKind.EndpointBinding);
        manifest.Dependencies.Should().OnlyContain(d => d.Origin == ManifestDependencyOrigin.OwnerResolved && d.Resolution == ManifestResolution.Resolved);
        manifest.Environment.Should().Match<ManifestEnvironment>(e => e.EnvironmentId == "local-synthetic" && e.TargetOrigin == new Uri(_server.Url("/")).GetLeftPart(UriPartial.Authority) + "/");
        manifest.Build.SourceCommit.Should().NotBeNullOrWhiteSpace();
        manifest.DependencyFingerprint.Should().HaveLength(64);
        run.Run.DependencyFingerprint.Should().Be(manifest.DependencyFingerprint, "the run summary names its manifest");

        applicability!.Applicability.Should().Be(ResultApplicability.CurrentlyApplicable, applicability.Explanation);
        applicability.ResultState.Should().Be(ExecutionState.Passed);
        applicability.Dependencies.Should().OnlyContain(d => d.State == DependencyCurrencyState.Unchanged);
    }

    [Fact]
    public async Task A_historical_PASS_stays_a_PASS_and_becomes_historical_when_any_pinned_definition_moves()
    {
        var test = await PrepareAsync();
        var run = await RunAsync(test.Id);
        var before = await ManifestRowJsonAsync(run.Run.RunId);

        // 1. A newer contract version.
        using (var scope = _services.CreateScope())
            (await Get<ContractRegistryService>(scope).ImportAsync(new() { ContractKey = Key, FileName = "children-v2.json", Content = ContractTestingTests.V2Json }, default)).Version.Should().Be(2);
        var (_, a1) = await ReadAsync(run.Run.RunId);
        a1!.Applicability.Should().Be(ResultApplicability.Historical);
        a1.ResultState.Should().Be(ExecutionState.Passed, "the historical result itself never changes");
        a1.Dependencies.Single(d => d.Pinned.Kind == ManifestDependencyKind.Contract).Should().Match<DependencyCurrency>(d => d.State == DependencyCurrencyState.Superseded && d.CurrentRef == "synthetic-children-api@v2");
        a1.Explanation.Should().Contain("now v2");

        // 2. A newer Test Data version.
        var draft = new TestDataResourceDraft { Alias = "children", Label = "children", ExpectedVersion = 1,
            Content = new() { Kind = TestDataResourceKind.Dataset, Members = [new("id", null, new("id", TestDataPrimitive.Int32, "8"))] } };
        _authored.AddVersion("children", draft).Outcome.Should().Be(AuthoredTestDataStore.Outcome.Saved);
        (await ReadAsync(run.Run.RunId)).Applicability!.Dependencies.Single(d => d.Pinned.Kind == ManifestDependencyKind.TestData).State.Should().Be(DependencyCurrencyState.Superseded);

        // 3. An edited test (new revision) and a changed endpoint binding.
        using (var scope = _services.CreateScope())
        {
            var tests = Get<ContractTestService>(scope);
            var saved = await tests.UpdateAsync(test.Id, new() { ExpectedRevision = 2, Name = test.Name + " (edited)", Template = test.Template, ReviewState = test.ReviewState }, default);
            saved.Saved.Should().BeTrue(saved.Message);
            (await tests.SaveBindingAsync(Key, new("local-synthetic", "/api", null, ExecutionAuthenticationKind.None), default)).Error.Should().BeNull();
        }
        var (_, a3) = await ReadAsync(run.Run.RunId);
        a3!.Dependencies.Single(d => d.Pinned.Kind == ManifestDependencyKind.ContractTest).Should().Match<DependencyCurrency>(d => d.State == DependencyCurrencyState.Superseded
            && d.CurrentRef == ContractIdentity.DefinitionRef(test.Id, 3));
        a3.Dependencies.Single(d => d.Pinned.Kind == ManifestDependencyKind.EndpointBinding).State.Should().Be(DependencyCurrencyState.Changed);
        a3.ResultState.Should().Be(ExecutionState.Passed);

        (await ManifestRowJsonAsync(run.Run.RunId)).Should().Be(before, "a manifest is never rewritten by later changes");
        (await WaitAsync(run.Run.RunId)).Run.State.Should().Be(ExecutionState.Passed);
    }

    [Fact]
    public async Task Identical_reruns_share_a_dependency_fingerprint_and_a_changed_definition_does_not()
    {
        var test = await PrepareAsync();
        var first = await RunAsync(test.Id);
        var second = await RunAsync(test.Id);
        second.Run.DependencyFingerprint.Should().Be(first.Run.DependencyFingerprint);
        (await ReadAsync(first.Run.RunId)).Manifest!.ManifestId.Should().NotBe((await ReadAsync(second.Run.RunId)).Manifest!.ManifestId);

        _authored.AddVersion("children", new TestDataResourceDraft { Alias = "children", Label = "children", ExpectedVersion = 1,
            Content = new() { Kind = TestDataResourceKind.Dataset, Members = [new("id", null, new("id", TestDataPrimitive.Int32, "7")), new("extra", null, new("extra", TestDataPrimitive.String, "x"))] } }).Outcome.Should().Be(AuthoredTestDataStore.Outcome.Saved);
        Approve("children");
        _catalog.Assets().Single(a => a.Alias == "children").Version.Should().Be(2);
        using (var scope = _services.CreateScope())
        {
            var (_, error) = await Get<ContractTestService>(scope).BindAsync(test.Id, new("children", 2), default);
            error.Should().BeNull(error);
        }
        var third = await RunAsync(test.Id);
        third.Run.DependencyFingerprint.Should().NotBe(first.Run.DependencyFingerprint);
    }

    [Fact]
    public async Task A_pinned_revision_runs_exactly_that_stored_revision_and_the_manifest_names_it()
    {
        var test = await PrepareAsync();
        using (var scope = _services.CreateScope())
        {
            var template = test.Template with { Headers = new(test.Template.Headers, StringComparer.OrdinalIgnoreCase) { ["X-Test-Revision"] = "r3" } };
            (await Get<ContractTestService>(scope).UpdateAsync(test.Id, new() { ExpectedRevision = 2, Name = test.Name, Template = template, ReviewState = test.ReviewState }, default)).Saved.Should().BeTrue();
        }
        _seen.Clear();
        var pinned = await RunAsync(test.Id, new() { [test.Id] = 2 });
        pinned.Calls.Single().Definition.DefinitionVersion.Should().Be(ContractIdentity.DefinitionRef(test.Id, 2));
        _seen.Should().ContainSingle().Which.Revision.Should().BeNull("revision 2 has no X-Test-Revision header; the target is the oracle");
        (await ReadAsync(pinned.Run.RunId)).Manifest!.Dependencies.Single(d => d.Kind == ManifestDependencyKind.ContractTest).Version.Should().Be(2);
        (await ReadAsync(pinned.Run.RunId)).Applicability!.Applicability.Should().Be(ResultApplicability.Historical, "the pinned revision is no longer the test's current one");

        _seen.Clear();
        var current = await RunAsync(test.Id);
        current.Calls.Single().Definition.DefinitionVersion.Should().Be(ContractIdentity.DefinitionRef(test.Id, 3));
        _seen.Should().ContainSingle().Which.Revision.Should().Be("r3");
    }

    [Fact]
    public async Task Caller_declared_references_and_overrides_are_never_currently_applicable()
    {
        var test = await PrepareAsync();
        // A hand-built run naming the same references: nothing proves the stored records were executed.
        using (var scope = _services.CreateScope())
        {
            var started = await Get<ExecutionRunService>(scope).StartAsync(new ExecutionRunRequest
            {
                Name = "hand-built", EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local", ContractRefs = ["synthetic-children-api@v1"],
                Calls = [new() { Name = "get", Path = "/children/7", DefinitionVersion = ContractIdentity.DefinitionRef(test.Id, 2), TestDataRefs = ["children@v1"] }],
            }, "tester", default);
            var run = await WaitAsync(started.RunId!.Value);
            var (manifest, applicability) = await ReadAsync(run.Run.RunId);
            manifest!.Dependencies.Should().OnlyContain(d => d.Origin == ManifestDependencyOrigin.CallerDeclared);
            manifest.Dependencies.Should().OnlyContain(d => d.Resolution == ManifestResolution.Resolved, "the references exist; only the proof is missing");
            applicability!.Applicability.Should().Be(ResultApplicability.Unknown);
        }
        // An explicit base path override: the stored binding is not what ran.
        var overridden = await RunAsync(test.Id, basePath: "/");
        var (m2, a2) = await ReadAsync(overridden.Run.RunId);
        m2!.Dependencies.Single(d => d.Kind == ManifestDependencyKind.EndpointBinding).Origin.Should().Be(ManifestDependencyOrigin.CallerDeclared);
        a2!.Applicability.Should().Be(ResultApplicability.Unknown);

        // Owner-only fields can never be bound from a request body.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonSerializer.Deserialize<ExecutionRunRequest>("""{"ownerAttestedRefs":["x@v1"],"ownerDependencies":[{"ref":"x"}]}""", web)!
            .Should().Match<ExecutionRunRequest>(r => r.OwnerAttestedRefs.Count == 0 && r.OwnerDependencies.Count == 0);
        JsonSerializer.Deserialize<ContractTestRunRequest>("""{"pinnedRevisions":{"00000000-0000-0000-0000-000000000001":1}}""", web)!.PinnedRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_without_a_manifest_is_unknown_and_deleting_a_run_deletes_its_manifest()
    {
        var test = await PrepareAsync();
        var run = await RunAsync(test.Id);
        using (var scope = _services.CreateScope())
        {
            var db = Get<AppDbContext>(scope);
            db.ExecutionManifests.RemoveRange(db.ExecutionManifests);
            await db.SaveChangesAsync();
        }
        (await ReadAsync(run.Run.RunId)).Applicability!.Should().Match<ExecutionApplicability>(a => a.Applicability == ResultApplicability.Unknown && a.ResultState == ExecutionState.Passed);

        var second = await RunAsync(test.Id);
        using (var scope = _services.CreateScope())
        {
            var db = Get<AppDbContext>(scope);
            (await new ExecutionEvidenceManagement(db).DeleteAsync(new() { RunIds = [second.Run.RunId], Confirm = 1 }, "tester", default)).Deleted.Should().Be(1);
            (await db.ExecutionManifests.CountAsync(m => m.RunId == second.Run.RunId)).Should().Be(0);
        }
    }

    [Fact]
    public async Task Rebinding_the_same_version_and_regenerating_identical_content_add_no_revision()
    {
        var test = await PrepareAsync();
        using var scope = _services.CreateScope();
        var tests = Get<ContractTestService>(scope);
        (await tests.BindAsync(test.Id, new("children", 1), default)).Test!.Revision.Should().Be(2);
        (await tests.RegenerateAsync(test.Id, default)).Test!.Revision.Should().Be(2, "the regenerated content is identical");
        (await tests.RevisionsAsync(test.Id, default)).Select(r => r.Revision).Should().BeEquivalentTo([1, 2]);
    }
}

/// <summary>On PostgreSQL (fresh migrated temporary database): the database itself refuses to update a manifest, and deletes it with its run.</summary>
public sealed class ExecutionManifestPostgresTests : IAsyncLifetime
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_manifest");
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

    [Fact]
    public async Task The_database_refuses_to_update_a_manifest_and_deletes_it_with_its_run()
    {
        var runId = Guid.NewGuid();
        await using (var db = Context())
        {
            await new ExecutionEvidenceStore(db).CreateAsync(new ExecutionRunSummary { RunId = runId, Name = "r", EnvironmentId = "dev", State = ExecutionState.Running, StartedAt = DateTimeOffset.UtcNow },
                [new ExecutionCallRecord { CallId = Guid.NewGuid(), Sequence = 1, State = ExecutionState.Passed, Definition = new() { Name = "c", Path = "/x" } }], default,
                new ExecutionManifest { ManifestId = Guid.NewGuid(), Owner = ExecutionManifestSchema.ExecutionEvidenceOwner, RunId = runId, CreatedAt = DateTimeOffset.UtcNow, DependencyFingerprint = new string('a', 64) });
            await new ExecutionEvidenceStore(db).CompleteAsync(runId, null, default);
        }
        await using (var db = Context())
        {
            var update = () => db.Database.ExecuteSqlRawAsync("UPDATE execution_manifests SET \"DocumentJson\" = 'tampered'");
            (await update.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("immutable");
            (await db.ExecutionManifests.SingleAsync()).DependencyFingerprint.Should().Be(new string('a', 64));
            (await new ExecutionEvidenceManagement(db).DeleteAsync(new() { RunIds = [runId], Confirm = 1 }, "tester", default)).Deleted.Should().Be(1);
        }
        await using (var db = Context()) (await db.ExecutionManifests.CountAsync()).Should().Be(0);
    }
}
