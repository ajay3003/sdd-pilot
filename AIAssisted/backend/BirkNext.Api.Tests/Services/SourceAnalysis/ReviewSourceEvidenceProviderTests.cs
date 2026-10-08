using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// The shared source-evidence provider: Source Analysis snapshots are listed deterministically, resolved exactly (never the latest instead),
/// validated as one primary plus related snapshots of other repositories, newer snapshots are offered and never substituted, the scope a run
/// keeps names every snapshot separately (no merged snapshot), and hiding Source Analysis blocks selection without deleting anything.
/// </summary>
public sealed class ReviewSourceEvidenceProviderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IqrSourceSnapshot Snapshot(string archive, DateTimeOffset at, string owner = IqrSourceStore.SourceAnalysisOwner, char fill = 'a') => new()
    {
        Id = Guid.NewGuid(), IntegrationId = owner, Archive = new SourceArchive(archive, new string(fill, 64), 10), AnalyzedAt = at, Status = SourceAnalysisStatus.Partial,
    };

    private static async Task<IqrSourceSnapshot> Insert(AppDbContext db, IqrSourceSnapshot s, string env = "dev")
    {
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = s.Id, EnvironmentId = env, IntegrationId = s.IntegrationId, AnalyzedAt = s.AnalyzedAt, EvidenceJson = JsonSerializer.Serialize(s, Json) });
        await db.SaveChangesAsync();
        return s;
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T08:00:00Z");

    [Fact]
    public async Task ListsSourceAnalysisSnapshotsDeterministicallyAndResolvesExactlyNeverTheLatest()
    {
        await using var db = Db();
        var a = await Insert(db, Snapshot("AppRepo.zip", T0));
        var b = await Insert(db, Snapshot("AppRepo.zip", T0.AddHours(1), fill: 'b'));
        var legacy = await Insert(db, Snapshot("AppRepo.zip", T0.AddHours(2), owner: "person-adapter", fill: 'c'));
        var otherTarget = await Insert(db, Snapshot("Other.zip", T0.AddMinutes(30), fill: 'd'), env: "qa");
        var provider = new ReviewSourceEvidenceProvider(new IqrSourceStore(db));

        (await provider.ListAsync("dev")).Select(s => s.Id).Should().Equal(b.Id, otherTarget.Id, a.Id);
        (await provider.ResolveAsync("dev", a.Id))!.Archive.Sha256.Should().Be(a.Archive.Sha256, "the exact snapshot, although a newer one exists");
        (await provider.ResolveAsync("dev", Guid.NewGuid())).Should().BeNull("an unknown id is never substituted");
        (await provider.ResolveAsync("dev", legacy.Id)).Should().BeNull("a snapshot an earlier version uploaded per integration is not offered for new scopes");
        (await provider.ResolveAsync("qa", a.Id))!.Id.Should().Be(a.Id, "a Target Environment never scopes source snapshots");
        (await provider.ListAsync("")).Select(s => s.Id).Should().Equal(new[] { b.Id, otherTarget.Id, a.Id }, "no target selected lists the same history");
    }

    [Fact]
    public void ScopeValidationIsSharedAndNeverSubstitutes()
    {
        var app = Snapshot("AppRepo.zip", T0);
        var appNewer = Snapshot("AppRepo.zip", T0.AddHours(1), fill: 'b');
        var shared = Snapshot("Shared.Common.zip", T0, fill: 'c');
        IReadOnlyList<IqrSourceSnapshot> all = [app, appNewer, shared];

        ReviewSourceEvidenceProvider.Validate(new(), all).Error.Should().Be("Choose a primary source snapshot.");
        ReviewSourceEvidenceProvider.Validate(new() { PrimarySnapshotId = Guid.NewGuid() }, all).Error.Should().Contain("nothing is substituted");
        ReviewSourceEvidenceProvider.Validate(new() { PrimarySnapshotId = app.Id, RelatedSnapshotIds = [app.Id] }, all).Error.Should().Contain("more than once");
        ReviewSourceEvidenceProvider.Validate(new() { PrimarySnapshotId = app.Id, RelatedSnapshotIds = [appNewer.Id] }, all).Error.Should().Contain("one snapshot per repository");
        ReviewSourceEvidenceProvider.Validate(new() { PrimarySnapshotId = app.Id }, all, _ => "no evidence for this review").Error.Should().Contain("AppRepo").And.Contain("no evidence for this review");
        ReviewSourceEvidenceProvider.Validate(new() { PrimarySnapshotId = app.Id }, all, sourceAnalysisEnabled: false).Error.Should().Be(ReviewSourceEvidenceProvider.Disabled);
        var (selected, error) = ReviewSourceEvidenceProvider.Validate(new() { PrimarySnapshotId = app.Id, RelatedSnapshotIds = [shared.Id] }, all);
        error.Should().BeNull();
        selected!.Select(s => s.Id).Should().Equal(app.Id, shared.Id);
    }

    [Fact]
    public void NewerSnapshotIsOfferedAndTheScopeKeepsEverySnapshotSeparately()
    {
        var a = Snapshot("AppRepo.zip", T0);
        var b = Snapshot("AppRepo.zip", T0.AddHours(1), fill: 'b');
        var shared = Snapshot("Shared.Common.zip", T0, fill: 'c');
        IReadOnlyList<IqrSourceSnapshot> all = [a, b, shared];
        var candidate = new RelatedSourceCandidate { RepositoryKey = "other", Repository = "Other", State = RelatedSourceState.SnapshotUnavailable };

        ReviewSourceEvidenceProvider.Newer([a, shared], all).Should().ContainSingle(n => n.Id == b.Id);
        ReviewSourceEvidenceProvider.Newer([b, shared], all).Should().BeEmpty();
        var options = ReviewSourceEvidenceProvider.Options(true, all, _ => new(true), new() { PrimarySnapshotId = a.Id, RelatedSnapshotIds = [shared.Id] }, _ => [candidate]);
        options.Scope!.Primary.SnapshotId.Should().Be(a.Id, "the newer snapshot is only offered");
        options.Newer.Should().ContainSingle(n => n.SnapshotId == b.Id && n.Latest);
        options.Scope.Related.Should().ContainSingle(r => r.SnapshotId == shared.Id && r.Fingerprint == shared.Archive.Sha256);
        options.Scope.Primary.Fingerprint.Should().NotBe(options.Scope.Related[0].Fingerprint, "two snapshots, two entries — never one merged snapshot");
        options.Scope.Limitations.Should().ContainSingle(l => l.Contains("Related source detected but not included") && l.Contains("no analyzed snapshot"));
        options.Snapshots.Select(s => s.Repository).Should().Equal("AppRepo", "AppRepo", "Shared.Common");
        options.Snapshots.Should().OnlyContain(s => s.SourceStatus == "Partial", "the Source Analysis status is shown as is — not a review result");
    }

    [Fact]
    public async Task DisabledSourceAnalysisOffersNothingAndDeletesNothing()
    {
        await using var db = Db();
        var a = await Insert(db, Snapshot("AppRepo.zip", T0));
        var provider = new ReviewSourceEvidenceProvider(new IqrSourceStore(db), sourceAnalysisEnabled: false);

        var options = ReviewSourceEvidenceProvider.Options(provider.SourceAnalysisEnabled, [], _ => new(true), new() { PrimarySnapshotId = a.Id }, _ => []);

        options.SourceAnalysisEnabled.Should().BeFalse();
        options.Snapshots.Should().BeEmpty();
        options.Error.Should().Be(ReviewSourceEvidenceProvider.Disabled);
        db.IqrSourceSnapshots.Should().ContainSingle("stored snapshots are kept");
        ReviewSourceScopes.Gate(false, 1, true, false, 0, 0).Should().Be(ReviewSourceGateState.SourceAnalysisDisabled);
    }

    [Fact]
    public void GateAndOptionalStatesFollowTheSharedRules()
    {
        ReviewSourceScopes.Gate(true, 0, false, false, 0, 0).Should().Be(ReviewSourceGateState.NoSnapshotAvailable);
        ReviewSourceScopes.Gate(true, 2, false, false, 0, 0).Should().Be(ReviewSourceGateState.SnapshotAvailableNotSelected);
        ReviewSourceScopes.Gate(true, 2, true, true, 0, 0).Should().Be(ReviewSourceGateState.SelectedSnapshotUnavailable);
        ReviewSourceScopes.Gate(true, 2, true, false, 1, 1).Should().Be(ReviewSourceGateState.NewerSnapshotAvailable);
        ReviewSourceScopes.Gate(true, 2, true, false, 0, 1).Should().Be(ReviewSourceGateState.RelatedSourceSuggested);
        ReviewSourceScopes.Gate(true, 2, true, false, 0, 0, requiredRelatedUnavailable: true).Should().Be(ReviewSourceGateState.RequiredRelatedSourceUnavailable);
        ReviewSourceScopes.Gate(true, 2, true, false, 0, 0).Should().Be(ReviewSourceGateState.Selected);
        ReviewSourceScopes.Optional(true, 0, false).Should().Be(ReviewSourceOptionalState.NotSelected);
        ReviewSourceScopes.Optional(true, 3, false).Should().Be(ReviewSourceOptionalState.Available);
        ReviewSourceScopes.Optional(true, 3, true).Should().Be(ReviewSourceOptionalState.Selected);
        ReviewSourceScopes.Optional(false, 3, false).Should().Be(ReviewSourceOptionalState.Unavailable);
    }

    [Fact]
    public void ReviewsUploadNoSourceArchives()
    {
        // Source Analysis is the one source-ingestion entry point; the other remaining multipart endpoint is the SBOM import (not source).
        static IEnumerable<string?> Posts(Type controller) => controller.GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Cast<Microsoft.AspNetCore.Mvc.HttpPostAttribute>()).Select(a => a.Template);
        Posts(typeof(BirkNext.Api.Controllers.IqrSourceEvidenceController)).Should().Equal(new[] { "snapshots" }, "no per-integration upload");
        Posts(typeof(BirkNext.Api.Controllers.SecurityClassificationController)).Should().NotContain("source");
        Posts(typeof(BirkNext.Api.Controllers.DependencyReviewController)).Should().NotContain("runs");
        Posts(typeof(BirkNext.Api.Controllers.DependencyReviewController)).Should().Contain("inventories/import", "the SBOM import is not source ingestion");
    }
}
