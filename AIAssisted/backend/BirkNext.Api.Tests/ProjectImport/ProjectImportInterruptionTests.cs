using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.ProjectImport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static BirkNext.Api.Tests.ProjectImport.ProjectImportServiceTests;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>
/// Interrupted Project Imports (BQA-007). The source snapshot and its schema artifacts are saved in one SaveChanges, and the Snapshot Registry
/// lists source snapshots from that store (no separate pointer to lose), so the interruption points are: during analysis (nothing saved), the save
/// itself, and after the save but before the staged import is marked committed (a crash or lost response). Each must leave no false completed
/// import, no partial snapshot, the previous valid snapshot current, the error retained, and a retry that never duplicates.
/// </summary>
public sealed class ProjectImportInterruptionTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly string _stagingDirectory = Path.Combine(Path.GetTempPath(), "birknext-import-interruption-" + Guid.NewGuid().ToString("N"));
    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_dbName).Options);

    public void Dispose()
    {
        try { Directory.Delete(_stagingDirectory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static ProjectImportService Service(AppDbContext db, ProjectImportStagingStore staging)
    {
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(true);
        return new ProjectImportService(new IqrSourceStore(db), staging, sources.Object, NullLogger<ProjectImportService>.Instance);
    }

    private async Task<int> SnapshotCount() { await using var db = Db(); return await db.IqrSourceSnapshots.CountAsync(); }

    [Fact]
    public async Task Cancelled_during_analysis_saves_nothing_keeps_the_stage_across_a_restart_and_a_retry_creates_exactly_one_snapshot()
    {
        var staging = new ProjectImportStagingStore(directory: _stagingDirectory);
        Guid stagingId;
        await using (var db = Db())
        {
            var service = Service(db, staging);
            stagingId = service.Preview("shop.zip", Zip([.. Documents, .. Source])).Preview!.StagingId;
            using var cts = new CancellationTokenSource();
            var commit = () => service.CommitAsync(stagingId, cts.Token, e =>
            {
                if (e.Stage == "Discovering test definitions") cts.Cancel();
                return Task.CompletedTask;
            });
            await commit.Should().ThrowAsync<OperationCanceledException>("a cancelled import is never reported as completed");
        }
        (await SnapshotCount()).Should().Be(0, "nothing is saved before the single snapshot save");
        staging.Find(stagingId)!.Committed.Should().BeNull();

        // Backend restart: a new staging store over the same folder still holds the archive, so the retry needs no new upload.
        var restarted = new ProjectImportStagingStore(directory: _stagingDirectory);
        restarted.Find(stagingId).Should().NotBeNull("stages survive a restart");
        await using (var db = Db())
        {
            var retry = (await Service(db, restarted).CommitAsync(stagingId))!;
            retry.Source.State.Should().Be(ProjectImportSourceState.Created);
            (await Service(db, restarted).CommitAsync(stagingId))!.Source.SnapshotId.Should().Be(retry.Source.SnapshotId, "a repeated commit returns the settled result");
        }
        (await SnapshotCount()).Should().Be(1);
    }

    [Fact]
    public async Task Interrupted_after_the_save_before_the_import_is_marked_committed_the_retry_reuses_the_snapshot_and_never_duplicates_it()
    {
        var staging = new ProjectImportStagingStore();
        using var cts = new CancellationTokenSource();
        Guid stagingId;
        await using (var crashing = new CrashAfterSaveDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_dbName).Options, cts))
        {
            var service = Service(crashing, staging);
            stagingId = service.Preview("shop.zip", Zip([.. Documents, .. Source])).Preview!.StagingId;
            var commit = () => service.CommitAsync(stagingId, cts.Token);
            await commit.Should().ThrowAsync<OperationCanceledException>();
        }
        (await SnapshotCount()).Should().Be(1, "the save completed before the interruption");
        staging.Find(stagingId)!.Committed.Should().BeNull("the interrupted commit never settled the stage");

        await using var db = Db();
        var retry = (await Service(db, staging).CommitAsync(stagingId))!;
        retry.Source.State.Should().Be(ProjectImportSourceState.Reused, "the snapshot of these exact bytes is already current");
        (await SnapshotCount()).Should().Be(1, "the retry never creates a second snapshot of the same import");
        (await new IqrSourceStore(db).FindSourceAnalysisAsync(retry.Source.SnapshotId!.Value))!.ProjectImport!.ImportId.Should().Be(retry.Provenance.ImportId);
    }

    [Fact]
    public async Task A_failed_save_of_a_new_version_keeps_the_previous_snapshot_current_retains_the_error_and_a_retry_succeeds_once()
    {
        var staging = new ProjectImportStagingStore();
        Guid v1;
        await using (var db = Db())
            v1 = (await Service(db, staging).CommitAsync(Service(db, staging).Preview("shop.zip", Zip([.. Documents, .. Source])).Preview!.StagingId))!.Source.SnapshotId!.Value;

        var v2Bytes = Zip([.. Documents, .. Source, ("shop/src/Shop.Api/Orders.cs", "namespace Shop.Api; public sealed class Orders { }")]);
        Guid v2Stage;
        await using (var failingOnce = new FailOnceDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_dbName).Options))
        {
            var service = Service(failingOnce, staging);
            v2Stage = service.Preview("shop.zip", v2Bytes).Preview!.StagingId;
            var failed = (await service.CommitAsync(v2Stage))!;
            failed.Source.State.Should().Be(ProjectImportSourceState.Failed);
            failed.Source.Code.Should().Be("SOURCE_SNAPSHOT_SAVE_FAILED");
            failed.Source.Message.Should().Contain("could not be saved", "the reason is kept for the user");
            failed.Source.CanRetry.Should().BeTrue();
            failed.Source.SnapshotId.Should().BeNull();
            staging.Find(v2Stage)!.Committed.Should().BeNull("a failure is not a settled import");

            await using var read = Db();
            (await new IqrSourceStore(read).ListSourceAnalysisAsync(1)).Single().Id.Should().Be(v1, "the previous valid snapshot stays current");

            var retried = (await service.CommitAsync(v2Stage))!;
            retried.Source.State.Should().Be(ProjectImportSourceState.Created);
        }
        await using (var db = Db())
        {
            var list = await new IqrSourceStore(db).ListSourceAnalysisAsync();
            list.Should().HaveCount(2, "one snapshot per version: the failure left no partial row and the retry added exactly one");
            list.Select(s => s.Id).Should().Contain(v1, "history is kept");
        }
    }

    [Fact]
    public async Task Two_concurrent_commits_of_one_stage_create_one_snapshot_and_both_get_the_same_result()
    {
        var staging = new ProjectImportStagingStore();
        await using var a = Db();
        await using var b = Db();
        var stagingId = Service(a, staging).Preview("shop.zip", Zip([.. Documents, .. Source])).Preview!.StagingId;

        var results = await Task.WhenAll(Service(a, staging).CommitAsync(stagingId), Service(b, staging).CommitAsync(stagingId));

        results.Select(r => r!.Source.SnapshotId).Distinct().Should().ContainSingle();
        (await SnapshotCount()).Should().Be(1);
    }

    /// <summary>Saves, then reports an interruption (process stop / lost response) before the commit can settle the stage.</summary>
    private sealed class CrashAfterSaveDbContext(DbContextOptions<AppDbContext> options, CancellationTokenSource interruption) : AppDbContext(options)
    {
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await base.SaveChangesAsync(cancellationToken);
            interruption.Cancel();
            throw new OperationCanceledException(interruption.Token);
        }
    }

    /// <summary>The first save fails (database unavailable); later saves succeed.</summary>
    private sealed class FailOnceDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        private bool _failed;
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_failed) return base.SaveChangesAsync(cancellationToken);
            _failed = true;
            throw new DbUpdateException("simulated save failure");
        }
    }
}
