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
using BirkNext.ContractTesting;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// Contract discovery and bulk import from Source Analysis: one row per source contract (newest snapshot, re-analysis never lists it twice) with
/// its registry state; a bulk import goes through the one registry import, test generator and test-data generator; a newer snapshot of the same
/// file is the next version of the same contract (never a duplicate contract, also for a single import without a target); repeating changes nothing.
/// </summary>
public sealed class ContractBulkImportTests : IAsyncLifetime
{
    private sealed class Sources : IReviewSourceEvidenceProvider
    {
        public List<IqrSourceSnapshot> All { get; } = [];
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([.. All]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(All.FirstOrDefault(s => s.Id == snapshotId));
    }

    private const string Pets = """
        { "openapi": "3.0.3", "info": { "title": "Synthetic Pet API", "version": "1" },
          "paths": { "/pets": { "get": { "responses": { "200": { "description": "ok" } } } } } }
        """;

    private static readonly string OrdersV2 = ContractTestDataTests.Orders.Replace("\"version\": \"1\"", "\"version\": \"2\"", StringComparison.Ordinal)
        .Replace("\"required\": [\"quantity\", \"lines\", \"delivery\"]", "\"required\": [\"quantity\", \"lines\", \"delivery\", \"status\"]", StringComparison.Ordinal);

    private readonly Sources _sources = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-cbi-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var authored = new AuthoredTestDataStore(_dir);
        var registry = new TestDataRegistryStore(_dir, NullLogger<TestDataRegistryStore>.Instance);
        var services = new ServiceCollection();
        var db = $"cbi-{Guid.NewGuid():N}";
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
        services.AddScoped<ContractBulkImportService>();
        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A snapshot holding the given OpenAPI files, with their document texts retained (as Source Analysis does at snapshot save).</summary>
    private Task<IqrSourceSnapshot> AddSnapshotAsync(DateTimeOffset at, params (string File, string Text)[] files) => AddSnapshotAsync(at, c => c, files);

    private async Task<IqrSourceSnapshot> AddSnapshotAsync(DateTimeOffset at, Func<SourceContract, SourceContract> tweak, params (string File, string Text)[] files)
    {
        var contracts = files.Select(f => tweak(ContractAnalyzer.ReadOpenApiDocument(f.File, f.Text)! with { Id = $"openapi:{f.File}", File = f.File })).ToList();
        var snapshot = new IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), AnalyzedAt = at, Commit = "c0ffee", Archive = new("synthetic-source.zip", "abc123", 12),
            EvidenceDomains = new SourceEvidenceDomainsSnapshot { Contracts = new() { Domain = SourceEvidenceDomain.Contracts, Contracts = contracts } },
        };
        _sources.All.Add(snapshot);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (file, text) in files)
            db.SourceContractDocuments.Add(new SourceContractDocumentRecord
            {
                Id = Guid.NewGuid(), SnapshotId = snapshot.Id, Path = file, Text = text, CapturedAt = at,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            });
        await db.SaveChangesAsync();
        return snapshot;
    }

    private async Task<IReadOnlyList<SourceContractCandidate>> CandidatesAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ContractRegistryService>().SourceCandidatesAsync(default);
    }

    private async Task<SourceContractBulkImportResult> BulkAsync(IEnumerable<SourceContractCandidate> candidates)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ContractBulkImportService>()
            .ImportAsync(new() { Items = [.. candidates.Select(c => new SourceContractRef(c.SnapshotId, c.ContractId))] }, default);
    }

    [Fact]
    public async Task Discovery_lists_each_source_contract_once_and_bulk_import_generates_tests_and_data_without_duplicates()
    {
        var now = DateTimeOffset.UtcNow;
        await AddSnapshotAsync(now.AddHours(-3), ("Orders/openapi.json", ContractTestDataTests.Orders));
        var newer = await AddSnapshotAsync(now.AddHours(-2), ("Orders/openapi.json", ContractTestDataTests.Orders), ("Pets/openapi.json", Pets));

        var candidates = await CandidatesAsync();
        candidates.Select(c => (c.Module, c.State, c.Snapshots, c.SnapshotId)).Should().Equal(
            ("Orders", SourceContractCandidateState.New, 2, newer.Id), ("Pets", SourceContractCandidateState.New, 1, newer.Id));
        candidates.Should().OnlyContain(c => c.HasDocument && c.Protocol == ApiContractProtocol.Rest && c.ContractKey == null);

        var first = await BulkAsync(candidates);
        first.Imported.Should().Be(2);
        var orders = first.Items.Single(i => i.File == "Orders/openapi.json");
        orders.Should().Match<SourceContractBulkItem>(i => i.Outcome == BulkImportOutcome.Imported && i.Version == 1 && i.TestsCreated >= 3 && i.Unsupported == 0);
        orders.DataBound.Should().Be(1, "POST /orders: every required value comes from the contract and validates");
        (orders.DataNeedsReview + orders.DataUnresolved).Should().Be(2, "a record id and a complex pattern need a human; nothing is invented");
        first.Items.Single(i => i.File == "Pets/openapi.json").DataNoInputs.Should().Be(1);

        var after = await CandidatesAsync();
        after.Should().OnlyContain(c => c.State == SourceContractCandidateState.UpToDate && c.ContractVersion == 1);
        after.Single(c => c.Module == "Orders").ImportedAs.Should().Be($"{orders.Key}@v1");

        var again = await BulkAsync(after);
        again.Items.Should().OnlyContain(i => i.Outcome == BulkImportOutcome.Unchanged && i.TestsCreated == 0);
        again.Items.Single(i => i.Key == orders.Key).DataBound.Should().Be(1, "the pinned set is reported as already bound, never re-created");

        // A newer analysis of the same project with a changed Orders contract: the next version of the same contract.
        var changed = await AddSnapshotAsync(now.AddHours(-1), ("Orders/openapi.json", OrdersV2), ("Pets/openapi.json", Pets));
        var third = await CandidatesAsync();
        third.Single(c => c.Module == "Orders").Should().Match<SourceContractCandidate>(c => c.State == SourceContractCandidateState.Changed && c.ContractKey == orders.Key
            && c.SnapshotId == changed.Id && c.Snapshots == 3);
        third.Single(c => c.Module == "Pets").State.Should().Be(SourceContractCandidateState.UpToDate, "identical evidence in a newer snapshot is not a change");

        var next = await BulkAsync(third.Where(c => c.State == SourceContractCandidateState.Changed));
        next.Items.Single().Should().Match<SourceContractBulkItem>(i => i.Outcome == BulkImportOutcome.NewVersion && i.Key == orders.Key && i.Version == 2);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ApiContracts.Count().Should().Be(2, "re-analysis never creates a second contract for the same source file");
        db.ApiContractVersions.Count(v => v.ContractId == db.ApiContracts.Single(c => c.Key == orders.Key).Id).Should().Be(2);
    }

    [Fact]
    public async Task A_single_import_without_a_target_continues_the_contract_imported_from_an_earlier_snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var older = await AddSnapshotAsync(now.AddHours(-2), ("Orders/openapi.json", ContractTestDataTests.Orders));
        var newer = await AddSnapshotAsync(now.AddHours(-1), ("Orders/openapi.json", OrdersV2));
        using var scope = _services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ContractRegistryService>();
        var v1 = await registry.ImportFromSourceAsync(new(older.Id, "openapi:Orders/openapi.json", null, null), default);
        var v2 = await registry.ImportFromSourceAsync(new(newer.Id, "openapi:Orders/openapi.json", null, null), default);
        v2.Should().Match<ContractImportResult>(r => r.Imported && r.Key == v1.Key && r.Version == 2);
        // An explicit new name still creates a separate contract (the tester's choice).
        var named = await registry.ImportFromSourceAsync(new(newer.Id, "openapi:Orders/openapi.json", null, "Orders copy"), default);
        named.Unchanged.Should().BeTrue("this snapshot's evidence is already stored; nothing is stored twice");
    }

    [Fact]
    public async Task The_same_document_in_another_archive_is_the_same_version_even_when_the_analysers_surroundings_differ()
    {
        var now = DateTimeOffset.UtcNow;
        var first = await AddSnapshotAsync(now.AddHours(-2), ("Orders/openapi.json", ContractTestDataTests.Orders));
        using (var scope = _services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<ContractRegistryService>().ImportFromSourceAsync(new(first.Id, "openapi:Orders/openapi.json", null, null), default)).Imported.Should().BeTrue();
        // Another archive of the project: the same document, but the analyser saw other code references and another line.
        await AddSnapshotAsync(now.AddHours(-1), c => c with { Line = c.Line + 7, ConsumerHints = ["Web"] }, ("Orders/openapi.json", ContractTestDataTests.Orders));
        var candidate = (await CandidatesAsync()).Single();
        candidate.State.Should().Be(SourceContractCandidateState.UpToDate, "the contract document is identical");
        (await BulkAsync([candidate])).Items.Single().Outcome.Should().Be(BulkImportOutcome.Unchanged);
    }
}
