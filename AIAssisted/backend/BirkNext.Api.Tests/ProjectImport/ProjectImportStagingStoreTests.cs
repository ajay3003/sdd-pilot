using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.ProjectImport;
using FluentAssertions;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>Durable Project Import staging: restart recovery, fingerprint check, bounded retention and cleanup. Generic fixtures only.</summary>
public sealed class ProjectImportStagingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "birknext-tests", "staging-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private ProjectImportStagingStore Store() => new(_clock, _directory);

    private static (byte[] Bytes, IqrSourceArchiveReader.Workspace Workspace) Archive(string marker = "")
    {
        var bytes = ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, ("shop/src/Marker.cs", $"class Marker {{ /* {marker} */ }}")]);
        return (bytes, IqrSourceArchiveReader.ReadDetailed("shop.zip", bytes, captureDocuments: true).Workspace!);
    }

    private static StagedProjectImport Stage(ProjectImportStagingStore store, string marker = "")
    {
        var (bytes, workspace) = Archive(marker);
        return store.Add(ProjectImportService.ImportIdFor(workspace.Archive.Sha256), "shop.zip", bytes, workspace, new ProjectImportSourceDetection { Detected = true, SourceFiles = 1 });
    }

    [Fact]
    public void AStage_IsRestoredByANewStore_WithTheSameBytesAndAFreshlyValidatedWorkspace()
    {
        var staged = Stage(Store());

        var restored = Store().Find(staged.StagingId);

        restored.Should().NotBeNull();
        restored!.Bytes.Should().Equal(staged.Bytes);
        restored.ImportId.Should().Be(staged.ImportId);
        restored.Workspace.Archive.Sha256.Should().Be(staged.Workspace.Archive.Sha256);
        restored.Workspace.DocumentFiles!.Select(d => d.Path).Should().BeEquivalentTo(staged.Workspace.DocumentFiles!.Select(d => d.Path));
        restored.Source.Detected.Should().BeTrue();
        restored.ExpiresAt.Should().Be(staged.ExpiresAt, "a restart never extends the retention");
    }

    [Fact]
    public void ATamperedArchive_IsDiscarded_NotCommitted()
    {
        var staged = Stage(Store());
        File.WriteAllBytes(Path.Combine(_directory, staged.StagingId.ToString("N") + ".zip"), Archive("changed").Bytes);

        Store().Find(staged.StagingId).Should().BeNull("the staged bytes no longer match the recorded fingerprint");
        Directory.EnumerateFiles(_directory).Should().BeEmpty();
    }

    [Fact]
    public void ExpiredStages_AreRemovedFromDisk()
    {
        var store = Store();
        var staged = Stage(store);
        _clock.Advance(ProjectImportStagingStore.TimeToLive);

        store.Find(staged.StagingId).Should().BeNull();
        Directory.EnumerateFiles(_directory).Should().BeEmpty();
        Stage(store);
        Store().Count.Should().Be(1);
    }

    [Fact]
    public void StartupCleanup_DeletesExpiredAndOrphanFiles_AndKeepsLiveStages()
    {
        var live = Stage(Store());
        File.WriteAllBytes(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".zip"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(_directory, "not-a-stage.json"), "{}");

        var store = Store();

        Directory.EnumerateFiles(_directory).Select(Path.GetFileName).Should().BeEquivalentTo(
            [live.StagingId.ToString("N") + ".json", live.StagingId.ToString("N") + ".zip"]);
        store.Find(live.StagingId).Should().NotBeNull();
    }

    [Fact]
    public void ConcurrentStages_AreSeparate_AndEvictionIsBoundedOnDisk()
    {
        var store = Store();
        var ids = Enumerable.Range(0, ProjectImportStagingStore.MaxStaged + 1).Select(i => { _clock.Advance(TimeSpan.FromSeconds(1)); return Stage(store, i.ToString()).StagingId; }).ToList();

        ids.Should().OnlyHaveUniqueItems();
        Store().Count.Should().Be(ProjectImportStagingStore.MaxStaged);
        Store().Find(ids[0]).Should().BeNull("the oldest stage is evicted, on disk too");
        Directory.EnumerateFiles(_directory, "*.zip").Should().HaveCount(ProjectImportStagingStore.MaxStaged);
    }

    [Fact]
    public void ASettledStage_KeepsOnlyItsResult_AcrossARestart()
    {
        var store = Store();
        var staged = Stage(store);
        var result = new ProjectImportCommitResult { StagingId = staged.StagingId, Source = new ProjectImportSourceResult { State = ProjectImportSourceState.Created, SnapshotId = Guid.NewGuid() } };

        store.MarkCommitted(staged, result);

        Directory.EnumerateFiles(_directory, "*.zip").Should().BeEmpty();
        Store().Find(staged.StagingId)!.Committed!.Source.SnapshotId.Should().Be(result.Source.SnapshotId);
    }

    [Fact]
    public void Clear_DeletesEveryStagedFile()
    {
        var store = Store();
        Stage(store, "a");
        Stage(store, "b");

        store.Clear();

        Directory.EnumerateFiles(_directory).Should().BeEmpty();
        Store().Count.Should().Be(0);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
