using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.PipelineReview;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Tests.ProjectImport;
using BirkNext.Integrations;
using BirkNext.ProjectImport;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// Source evidence is not runtime evidence: a Source Analysis snapshot is created without a Target Environment, stays current whatever target
/// is selected, switched or deleted, and is read by the source-only reviews with or without one. Snapshots stored by earlier versions with a
/// target id stay readable. Generic fixtures only.
/// </summary>
public sealed class SourceSnapshotTargetIndependenceTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private static readonly (string, string)[] Pipeline =
        [("shop/.github/workflows/ci.yml", "name: ci\non: [push]\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test\n")];

    private IqrSourceStore Store => new(_db);
    private ReviewSourceEvidenceProvider Provider => new(Store);

    private async Task<IqrSourceSnapshot> AnalyzeWithoutTarget(params (string, string)[] files)
    {
        var bytes = ProjectImportServiceTests.Zip(files);
        var workspace = IqrSourceArchiveReader.ReadDetailed("shop.zip", bytes).Workspace!;
        return await Store.AnalyzeValidatedAsync(null, IqrSourceStore.SourceAnalysisOwner, "shop.zip", bytes, workspace);
    }

    private async Task<IqrSourceSnapshot> InsertLegacy(string environmentId, DateTimeOffset at)
    {
        var snapshot = new IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), IntegrationId = IqrSourceStore.SourceAnalysisOwner, Archive = new SourceArchive("legacy.zip", new string('c', 64), 3),
            AnalyzedAt = at, Status = SourceAnalysisStatus.Partial,
        };
        _db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = snapshot.Id, EnvironmentId = environmentId, IntegrationId = snapshot.IntegrationId,
            AnalyzedAt = at, EvidenceJson = JsonSerializer.Serialize(snapshot, Json) });
        await _db.SaveChangesAsync();
        return snapshot;
    }

    [Fact]
    public async Task ASnapshot_IsCreatedWithoutATargetEnvironment_AndIsCurrent()
    {
        var snapshot = await AnalyzeWithoutTarget(ProjectImportServiceTests.Source);

        var record = await _db.IqrSourceSnapshots.SingleAsync();
        record.EnvironmentId.Should().Be(IqrSourceStore.NoEnvironment);
        (await Store.ListSourceAnalysisAsync(1)).Single().Id.Should().Be(snapshot.Id);
        (await Store.FindSourceAnalysisAsync(snapshot.Id))!.Archive.Sha256.Should().Be(snapshot.Archive.Sha256);
    }

    [Fact]
    public async Task AnActiveTargetAtUpload_DoesNotBindTheSnapshot()
    {
        var bytes = ProjectImportServiceTests.Zip(ProjectImportServiceTests.Source);
        var snapshot = await Store.AnalyzeValidatedAsync("dev", IqrSourceStore.SourceAnalysisOwner, "shop.zip", bytes, IqrSourceArchiveReader.ReadDetailed("shop.zip", bytes).Workspace!);

        (await _db.IqrSourceSnapshots.SingleAsync()).EnvironmentId.Should().Be(IqrSourceStore.NoEnvironment);
        (await Provider.ListAsync("qa")).Single().Id.Should().Be(snapshot.Id, "another target sees the same source snapshot");
    }

    [Fact]
    public async Task LegacyTargetBoundSnapshots_StayReadable_AndKeepTheirHistoryOrder()
    {
        var legacy = await InsertLegacy("qa", DateTimeOffset.UtcNow.AddDays(-1));
        var current = await AnalyzeWithoutTarget(ProjectImportServiceTests.Source);

        (await Store.FindSourceAnalysisAsync(legacy.Id))!.Archive.FileName.Should().Be("legacy.zip");
        (await Provider.ResolveAsync("", legacy.Id)).Should().NotBeNull("no target selected still resolves the exact legacy snapshot");
        (await Provider.ListAsync("")).Select(s => s.Id).Should().Equal(current.Id, legacy.Id);
        (await _db.IqrSourceSnapshots.SingleAsync(r => r.Id == legacy.Id)).EnvironmentId.Should().Be("qa", "history is never rewritten");
    }

    [Fact]
    public async Task TargetAddedLater_SwitchedOrDeleted_NeverChangesTheCurrentSnapshot()
    {
        var snapshot = await AnalyzeWithoutTarget(ProjectImportServiceTests.Source);

        // Target configured later, then switched: the same snapshot, no re-analysis.
        (await Provider.ListAsync("dev"))[0].Id.Should().Be(snapshot.Id);
        (await Provider.ListAsync("qa"))[0].Id.Should().Be(snapshot.Id);
        // Target deleted (a Target Environment is a client-side profile; nothing in the snapshot store refers to it).
        (await Provider.ListAsync(""))[0].Id.Should().Be(snapshot.Id);
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ReimportingTheSameBytes_IsReused_WhetherOrNotATargetIsSelected()
    {
        var bytes = ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source]);
        var staging = new ProjectImportStagingStore();
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(true);
        var service = new ProjectImportService(Store, staging, sources.Object, NullLogger<ProjectImportService>.Instance);

        var first = (await service.CommitAsync(service.Preview("shop.zip", bytes).Preview!.StagingId))!;
        var second = (await service.CommitAsync(service.Preview("shop.zip", bytes).Preview!.StagingId))!;

        first.Source.State.Should().Be(ProjectImportSourceState.Created);
        second.Source.State.Should().Be(ProjectImportSourceState.Reused);
        second.Source.SnapshotId.Should().Be(first.Source.SnapshotId);
        second.Provenance.ImportId.Should().Be(first.Provenance.ImportId, "the import identity is the archive bytes, never the target");
    }

    [Fact]
    public async Task TechnologyCoverage_ReadsTheSnapshot_WithoutATarget()
    {
        var snapshot = await AnalyzeWithoutTarget(ProjectImportServiceTests.Source);
        var catalog = new Mock<IIntegrationCatalogService>(MockBehavior.Strict);

        var result = await new TechnologyCoverageController(Store, catalog.Object).Get(null, CancellationToken.None);

        var coverage = (result.Result as OkObjectResult)?.Value as BirkNext.Technology.ProjectTechnologyCoverage ?? result.Value!;
        coverage.SourceSnapshotId.Should().Be(snapshot.Id);
        coverage.Source.Should().NotBeNull();
        coverage.ConfiguredIntegrations.Should().BeEmpty("configured integrations belong to a target; none is read without one");
    }

    [Fact]
    public async Task PipelineReview_ReviewsTheSnapshot_WithoutATarget()
    {
        var snapshot = await AnalyzeWithoutTarget([.. ProjectImportServiceTests.Source, .. Pipeline]);
        var metadata = new Mock<IPipelineMetadataSource>();
        var service = new PipelineReviewService(Provider, metadata.Object, new MemoryCache(new MemoryCacheOptions()));

        (await service.SourcesAsync("")).Snapshots.Select(s => s.Id).Should().Contain(snapshot.Id);
        (await service.ReviewAsync("", null, includeMetadata: false)).Should().NotBeNull();
    }

    [Fact]
    public async Task DependencyReview_OffersTheSnapshot_WithoutATarget()
    {
        var snapshot = await AnalyzeWithoutTarget(ProjectImportServiceTests.Source);
        var service = new DependencyReviewSourceScopeService(Provider, Mock.Of<IDependencyReviewService>());

        var options = await service.OptionsAsync("", null);

        options.Snapshots.Select(s => s.SnapshotId).Should().Contain(snapshot.Id);
    }
}
