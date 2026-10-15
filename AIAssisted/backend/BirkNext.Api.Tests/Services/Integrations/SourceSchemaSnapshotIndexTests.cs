using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.Services.Integrations;

public sealed class SourceSchemaSnapshotIndexTests
{
    [Fact]
    public async Task SchemaIndexReturnsOnlyBoundedMetadataAndMarksLegacySnapshotsUnavailable()
    {
        using var db = NewDb();
        var availableId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        db.IqrSourceSnapshots.AddRange(
            new IqrSourceSnapshotRecord { Id = availableId, IntegrationId = IqrSourceStore.SourceAnalysisOwner,
                AnalyzedAt = DateTimeOffset.UtcNow, EvidenceJson = "large evidence payload is not read by this endpoint" },
            new IqrSourceSnapshotRecord { Id = legacyId, IntegrationId = IqrSourceStore.SourceAnalysisOwner,
                AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(-1), EvidenceJson = "not even valid JSON" },
            new IqrSourceSnapshotRecord { Id = Guid.NewGuid(), IntegrationId = "legacy-integration",
                AnalyzedAt = DateTimeOffset.UtcNow, EvidenceJson = "{}" });
        db.SourceSchemaArtifacts.Add(new SourceSchemaArtifactRecord
        {
            SnapshotId = availableId, SnapshotFingerprint = new string('a', 64), SourceArchiveName = "contracts.zip",
            ProjectImportId = "import-abc-123", CapturedAt = DateTimeOffset.UtcNow, IsComplete = false, OmittedFiles = 2, PackageJson = "[]",
        });
        await db.SaveChangesAsync();

        var controller = Controller(db);
        var response = await controller.SchemaIndex(default);
        var index = Assert.IsType<SourceSchemaSnapshotIndexDto>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response.Result).Value);

        Assert.False(index.IsTruncated);
        Assert.Equal(2, index.Snapshots.Count);
        var available = Assert.Single(index.Snapshots, item => item.Id == availableId);
        Assert.Equal("contracts.zip", available.ArchiveName);
        Assert.Equal("import-abc-123", available.ProjectImportId);
        Assert.Equal(new string('a', 64), available.Fingerprint);
        Assert.True(available.HasSchemaArtifact);
        Assert.False(available.IsComplete);
        Assert.Equal(2, available.OmittedFiles);
        var legacy = Assert.Single(index.Snapshots, item => item.Id == legacyId);
        Assert.False(legacy.HasSchemaArtifact);
        Assert.Null(legacy.Fingerprint);
    }

    [Fact]
    public async Task SchemaIndexDisclosesWhenTheNewestSnapshotListIsCapped()
    {
        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 501; i++)
            db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord
            {
                Id = Guid.NewGuid(), IntegrationId = IqrSourceStore.SourceAnalysisOwner,
                AnalyzedAt = now.AddSeconds(-i), EvidenceJson = "{}",
            });
        await db.SaveChangesAsync();

        var response = await Controller(db).SchemaIndex(default);
        var index = Assert.IsType<SourceSchemaSnapshotIndexDto>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response.Result).Value);

        Assert.Equal(500, index.Snapshots.Count);
        Assert.True(index.IsTruncated);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IqrSourceEvidenceController Controller(AppDbContext db) => new(new IqrSourceStore(db),
        Mock.Of<BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider>(),
        NullLogger<IqrSourceEvidenceController>.Instance);
}
