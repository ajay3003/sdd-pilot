using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Api.Services.TestData;
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
/// CTH-F03: one Target Environment configured for several contracts of a project. Endpoint evidence (saved binding authoritative, declared servers
/// candidates, several ambiguous, none unresolved), authentication fit with the operations' declared security (never downgraded to None), the
/// environment's credential reference (never a copied secret), the environment gate (production refused), explicit approval, overwrite and review
/// confirmation, concurrency, per-row results and an audit record. Nothing is sent.
/// </summary>
public sealed class ContractBulkBindingTests : IAsyncLifetime
{
    private sealed class Sources : IReviewSourceEvidenceProvider
    {
        public List<IqrSourceSnapshot> All { get; } = [];
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([.. All]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(All.FirstOrDefault(s => s.Id == snapshotId));
    }

    private static string Api(string title, string servers, bool secured) => $$"""
        { "openapi": "3.0.3", "info": { "title": "{{title}}", "version": "1" }, "servers": [{{servers}}],
          {{(secured ? "\"security\": [ { \"bearer\": [] } ], \"components\": { \"securitySchemes\": { \"bearer\": { \"type\": \"http\", \"scheme\": \"bearer\" } } }," : "")}}
          "paths": { "/items": { "get": { "responses": { "200": { "description": "ok" } } } } } }
        """;

    private readonly Sources _sources = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-cbb-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;
    private ApiServiceCredentialStore _credentials = null!;
    private readonly Dictionary<string, string> _keys = [];
    private static readonly ContractTestEnvironment Local = new() { EnvironmentId = "local-synthetic", TargetUrl = "http://127.0.0.1:5099/", EnvironmentType = "Local" };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var authored = new AuthoredTestDataStore(_dir);
        var registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        _credentials = new ApiServiceCredentialStore(Path.Combine(_dir, "credentials"));
        var services = new ServiceCollection();
        var db = $"cbb-{Guid.NewGuid():N}";
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
        services.AddSingleton(_credentials);
        services.AddScoped<TestDataResourceService>();
        services.AddScoped<ContractRegistryService>();
        services.AddScoped<ContractTestService>();
        services.AddScoped<ContractBulkBindingService>();
        _services = services.BuildServiceProvider();

        Add("p", ("One/openapi.json", Api("One", "{ \"url\": \"https://api.example.test/v1\" }", true)), ("Two/openapi.json", Api("Two", "{ \"url\": \"https://a.example.test/v1\" }, { \"url\": \"https://b.example.test/v2\" }", true)),
            ("Plain/openapi.json", Api("Plain", "", false)));
        Add("q", ("One/openapi.json", Api("Other", "{ \"url\": \"https://api.example.test/v1\" }", true)));
        using var scope = _services.CreateScope();
        var reg = scope.ServiceProvider.GetRequiredService<ContractRegistryService>();
        foreach (var s in _sources.All)
            foreach (var c in s.EvidenceDomains!.Contracts.Contracts)
                _keys[$"{ContractRegistryService.ProjectKeyOf(s)}:{c.File}"] = (await reg.ImportFromSourceAsync(new(s.Id, c.Id, null, null), default)).Key!;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Add(string project, params (string File, string Text)[] files) => _sources.All.Add(new IqrSourceSnapshot
    {
        Id = Guid.NewGuid(), AnalyzedAt = DateTimeOffset.UtcNow, Archive = new($"{project}.zip", "abc", 1), Repository = new SourceRepositoryIdentity(project, project, "test"),
        EvidenceDomains = new SourceEvidenceDomainsSnapshot { Contracts = new() { Domain = SourceEvidenceDomain.Contracts,
            Contracts = [.. files.Select(f => ContractAnalyzer.ReadOpenApiDocument(f.File, f.Text)! with { Id = $"openapi:{f.File}", File = f.File })] } },
    });

    private string K(string id) => _keys[id];

    private async Task<BulkBindingPreview> Preview(ExecutionAuthenticationKind auth, ContractTestEnvironment? env = null, BulkBasePathMode mode = BulkBasePathMode.KeepExisting, string? path = null)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ContractBulkBindingService>().PreviewAsync(new()
        {
            ProjectKey = "p", Environment = env ?? Local, Authentication = auth, BasePathMode = mode, BasePath = path,
            ContractKeys = [K("p:One/openapi.json"), K("p:Two/openapi.json"), K("p:Plain/openapi.json"), K("q:One/openapi.json")],
        }, default);
    }

    private async Task<BulkBindingApplyResult> Apply(bool confirm, params BulkBindingApplyItem[] items)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ContractBulkBindingService>()
            .ApplyAsync(new() { ProjectKey = "p", Environment = Local, ConfirmEnvironment = confirm, Items = [.. items] }, "tester@example.test", default);
    }

    private static BulkBindingApplyItem Item(BulkBindingPreviewRow r, bool confirm = false) =>
        new(r.Key, r.ProposedBasePath, r.ProposedGraphQlPath, r.ProposedAuthentication, r.Existing?.UpdatedAt, confirm);

    [Fact]
    public async Task Preview_classifies_endpoint_evidence_and_never_downgrades_declared_authentication()
    {
        var none = await Preview(ExecutionAuthenticationKind.None);
        var rows = none.Rows.ToDictionary(r => r.Key);
        rows[K("p:One/openapi.json")].Should().Match<BulkBindingPreviewRow>(r => r.Endpoint == EndpointEvidenceState.Candidate && r.ProposedBasePath == "/v1" && r.Status == BulkBindingStatus.Blocked
            && r.RequiredSchemes.SequenceEqual(new[] { "bearer" }) && r.Reasons.Any(x => x.Contains("never sent anonymously")));
        rows[K("p:Two/openapi.json")].Endpoint.Should().Be(EndpointEvidenceState.Ambiguous);
        rows[K("p:Plain/openapi.json")].Should().Match<BulkBindingPreviewRow>(r => r.Endpoint == EndpointEvidenceState.Unresolved && r.Status == BulkBindingStatus.NeedsReview);
        rows[K("q:One/openapi.json")].Should().Match<BulkBindingPreviewRow>(r => r.Status == BulkBindingStatus.Blocked && r.Reasons.Any(x => x.Contains("project q")));

        var cc = await Preview(ExecutionAuthenticationKind.ClientCredentials);
        cc.Rows.Single(r => r.Key == K("p:One/openapi.json")).Should().Match<BulkBindingPreviewRow>(r => r.Status == BulkBindingStatus.Blocked && r.Credential == "missing");
        _credentials.Save(new ApiServiceCredentialSave { EnvironmentId = "local-synthetic", Kind = ExecutionAuthenticationKind.ClientCredentials, TokenUrl = "http://127.0.0.1:5099/token",
            ClientId = "synthetic-client", Scope = "api.read", Secret = "synthetic-secret-value" }, DateTimeOffset.UtcNow);
        var ready = await Preview(ExecutionAuthenticationKind.ClientCredentials);
        var one = ready.Rows.Single(r => r.Key == K("p:One/openapi.json"));
        one.Should().Match<BulkBindingPreviewRow>(r => r.Status == BulkBindingStatus.Applicable && r.Credential.Contains("api-credential:local-synthetic") && r.Credential.Contains("scope api.read"));
        ready.Rows.Single(r => r.Key == K("p:Two/openapi.json")).Status.Should().Be(BulkBindingStatus.NeedsReview, "two declared server paths: the tester chooses");
        ready.Limitations.Should().Contain(l => l.Contains("audience"));
        JsonSerializer.Serialize(ready).Should().NotContain("synthetic-secret-value", "a credential is referenced, never copied");

        var explicitPath = await Preview(ExecutionAuthenticationKind.ClientCredentials, mode: BulkBasePathMode.Explicit, path: "https://evil.example.test/x");
        explicitPath.Rows.Should().Contain(r => r.Key == K("p:One/openapi.json") && r.Status == BulkBindingStatus.Blocked && r.Reasons.Any(x => x.Contains("no host")));

        var production = await Preview(ExecutionAuthenticationKind.ClientCredentials, Local with { EnvironmentType = "Production", IsProduction = true, TargetUrl = "https://prod.example.test/" });
        production.EnvironmentAllowed.Should().BeFalse();
        production.Rows.Should().OnlyContain(r => r.Status == BulkBindingStatus.Blocked);
    }

    [Fact]
    public async Task Apply_needs_the_environment_confirmation_applies_approved_rows_reports_each_and_keeps_an_audit_record()
    {
        _credentials.Save(new ApiServiceCredentialSave { EnvironmentId = "local-synthetic", Kind = ExecutionAuthenticationKind.ClientCredentials, TokenUrl = "http://127.0.0.1:5099/token",
            ClientId = "synthetic-client", Secret = "synthetic-secret-value" }, DateTimeOffset.UtcNow);
        var preview = (await Preview(ExecutionAuthenticationKind.ClientCredentials)).Rows.ToDictionary(r => r.Key);
        var one = preview[K("p:One/openapi.json")]; var two = preview[K("p:Two/openapi.json")]; var other = preview[K("q:One/openapi.json")];

        (await Apply(false, Item(one))).Rows.Single().Status.Should().Be(BulkBindingStatus.Skipped, "the approved environment must be confirmed");
        var applied = await Apply(true, Item(one), Item(two), Item(other));
        applied.Rows.Select(r => (r.Key, r.Status)).Should().Equal((one.Key, BulkBindingStatus.Applied), (two.Key, BulkBindingStatus.NeedsReview), (other.Key, BulkBindingStatus.Blocked));
        applied.Applied.Should().Be(1);
        applied.NotApplied.Should().Be(2, "a partial apply is reported as partial");
        applied.Rows[0].Binding.Should().Match<ContractEndpointBinding>(b => b.BasePath == "/v1" && b.Authentication == ExecutionAuthenticationKind.ClientCredentials);

        // Review rows only with explicit confirmation; then the saved binding is authoritative and repeating changes nothing.
        (await Apply(true, Item(two) with { BasePath = "/v2", ConfirmOverwrite = true })).Rows.Single().Status.Should().Be(BulkBindingStatus.Applied);
        var again = (await Preview(ExecutionAuthenticationKind.ClientCredentials)).Rows.Single(r => r.Key == one.Key);
        again.Should().Match<BulkBindingPreviewRow>(r => r.Status == BulkBindingStatus.Unchanged && r.Endpoint == EndpointEvidenceState.Authoritative);

        // Changing a saved binding needs the overwrite confirmation; a stale preview is refused.
        var attended = (await Preview(ExecutionAuthenticationKind.AttendedSession)).Rows.Single(r => r.Key == one.Key);
        attended.Should().Match<BulkBindingPreviewRow>(r => r.Overwrites && r.Status == BulkBindingStatus.NeedsReview);
        (await Apply(true, Item(attended))).Rows.Single().Status.Should().Be(BulkBindingStatus.NeedsReview);
        (await Apply(true, Item(attended, true) with { ExpectedUpdatedAt = DateTimeOffset.UnixEpoch })).Rows.Single().Message.Should().Contain("changed since the preview");
        (await Apply(true, Item(attended, true))).Rows.Single().Status.Should().Be(BulkBindingStatus.Applied);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ContractReviewDecisions.Where(d => d.Kind == "binding").Should().HaveCount(3).And.OnlyContain(d => d.DecidedBy == "tester@example.test" && d.Detail.Contains("before"));
        db.ContractReviewDecisions.Where(d => d.Kind == "binding").Should().NotContain(d => d.Detail.Contains("synthetic-secret-value"));
    }
}
