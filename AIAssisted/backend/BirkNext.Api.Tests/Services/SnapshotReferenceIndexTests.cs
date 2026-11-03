using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Snapshots;
using BirkNext.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

/// <summary>The shared reference index answers "what references this Source Analysis snapshot" from id columns only, and names what it cannot check.</summary>
public sealed class SnapshotReferenceIndexTests
{
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"snapshot-refs-{Guid.NewGuid():N}").Options);

    [Fact]
    public async Task Reviews_comparisons_and_bindings_are_found_with_their_role()
    {
        await using var db = Db();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        db.IqrSourceSnapshots.AddRange(new IqrSourceSnapshotRecord { Id = s1, EvidenceJson = "{}" }, new IqrSourceSnapshotRecord { Id = s2, EvidenceJson = "{}" });
        db.ImpactAnalysisRuns.Add(new ImpactAnalysisRunRecord { BaselineSnapshotId = s1, CurrentSnapshotId = s2, ProjectDisplayName = "M2LB" });
        db.AiCodeReviewRuns.Add(new AiCodeReviewRunRecord { Id = Guid.NewGuid(), CurrentSnapshotId = s1, Label = "CRR", CompletedAt = DateTimeOffset.UtcNow });
        db.IntegrationEvidenceBindings.Add(new IntegrationEvidenceBindingRecord { EnvironmentId = "dev", IntegrationId = "altinn", SnapshotId = s1 });
        await db.SaveChangesAsync();

        var report = await new SnapshotReferenceIndex(db).SourceAnalysisAsync(s1);

        Assert.True(report.Exists);
        Assert.True(report.IsReferenced);
        Assert.Equal(["Code Risk Review", "Impact Analysis", "Integration evidence binding"], report.ReferencedBy.Select(r => r.Owner));
        Assert.Equal(SnapshotReferenceRole.Baseline, report.ReferencedBy.Single(r => r.Owner == "Impact Analysis").Role);
        Assert.Equal(SnapshotReferenceRole.Current, report.ReferencedBy.Single(r => r.Owner == "Code Risk Review").Role);
        Assert.Equal(SnapshotReferenceRole.Bound, report.ReferencedBy.Single(r => r.Owner == "Integration evidence binding").Role);

        var current = await new SnapshotReferenceIndex(db).SourceAnalysisAsync(s2);
        Assert.Equal(SnapshotReferenceRole.Current, Assert.Single(current.ReferencedBy).Role);
    }

    [Fact]
    public async Task Derived_schema_packages_do_not_keep_a_snapshot_alive()
    {
        await using var db = Db();
        var id = Guid.NewGuid();
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = id, EvidenceJson = "{}" });
        db.SourceSchemaArtifacts.Add(new SourceSchemaArtifactRecord { SnapshotId = id, SourceArchiveName = "m2lb.zip", PackageJson = "{}" });
        await db.SaveChangesAsync();

        var report = await new SnapshotReferenceIndex(db).SourceAnalysisAsync(id);

        Assert.Equal(SnapshotReferenceRole.Derived, Assert.Single(report.ReferencedBy).Role);
        Assert.False(report.IsReferenced);
    }

    [Fact]
    public async Task Unchecked_stores_are_always_named_so_no_reference_is_never_claimed_as_unreferenced()
    {
        await using var db = Db();
        var report = await new SnapshotReferenceIndex(db).SourceAnalysisAsync(Guid.NewGuid());

        Assert.False(report.Exists);
        Assert.Empty(report.ReferencedBy);
        Assert.Contains(report.NotChecked, s => s.StartsWith("Performance test runs", StringComparison.Ordinal));
        Assert.Equal(SnapshotReferenceIndex.UncheckedSourceAnalysisReferrers.Count, report.NotChecked.Count);
    }
}
