using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Every stored source outcome of an imported project maps to one distinct, honest state; nothing without a snapshot claims one.</summary>
public sealed class ProjectImportCurrentSourceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");

    private static SddProjectImportRecord Record(string state, Guid? snapshot = null, Guid? staging = null, DateTimeOffset? until = null, string? message = null) => new()
    {
        ImportId = "import-1", ProjectName = "skole", SourceState = state, SourceSnapshotId = snapshot, SourceStagingId = staging, SourceStagedUntil = until, SourceMessage = message,
    };

    [Fact]
    public void Created_HasTheExactSnapshot()
    {
        var id = Guid.NewGuid();
        var source = ProjectImportPresentation.CurrentSource(Record(nameof(ProjectImportSourceState.Created), id), Now);
        source.SnapshotId.Should().Be(id);
        source.Status.Should().Be($"Snapshot {id.ToString()[..8]}");
        ProjectImportPresentation.SourceAnalysisHref(id).Should().Be($"source-analysis?snapshot={id:D}");
    }

    [Fact]
    public void NotDetected_NoSnapshotAndNoSourceClaim()
    {
        var source = ProjectImportPresentation.CurrentSource(Record(nameof(ProjectImportSourceState.NotDetected)), Now);
        (source.SnapshotId, source.CanRetry, source.SourceDetected).Should().Be((null, false, false));
    }

    [Fact]
    public void NotCreated_UsesTheBackendReason()
    {
        var source = ProjectImportPresentation.CurrentSource(Record(nameof(ProjectImportSourceState.NotCreated), message: "Source Analysis is turned off in Feature Visibility."), Now);
        source.Status.Should().Be("Source detected — no snapshot");
        source.Detail.Should().Contain("turned off");
        source.CanRetry.Should().BeFalse();
    }

    [Fact]
    public void Failed_RetryOnlyWhileStaged()
    {
        var staged = ProjectImportPresentation.CurrentSource(Record(nameof(ProjectImportSourceState.Failed), staging: Guid.NewGuid(), until: Now.AddMinutes(30)), Now);
        staged.CanRetry.Should().BeTrue();
        staged.Detail.Should().Be("Source analysis did not complete, so no snapshot exists.", "the retry button is the action; the reason is said once");
        var expired = ProjectImportPresentation.CurrentSource(Record(nameof(ProjectImportSourceState.Failed), staging: Guid.NewGuid(), until: Now.AddMinutes(-1)), Now);
        expired.CanRetry.Should().BeFalse();
        expired.Detail.Should().Contain("choose the same ZIP again");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Pending")]
    public void UnknownOrLegacyState_NeverClaimsASnapshot(string state)
    {
        var source = ProjectImportPresentation.CurrentSource(Record(state), Now);
        source.SnapshotId.Should().BeNull();
        source.Status.Should().Be("Source state unknown");
    }

    [Fact]
    public void LegacyRecordWithSnapshotButNoState_StillLinksIt()
    {
        var id = Guid.NewGuid();
        ProjectImportPresentation.CurrentSource(Record("", id), Now).SnapshotId.Should().Be(id);
    }

    [Fact]
    public void Preview_NeverPromisesASnapshotWhenSourceAnalysisIsOff_AndNoSourceCopyIsAWholeSentence()
    {
        var detected = new ProjectImportSourceDetection { Detected = true, SourceFiles = 3 };
        ProjectImportPresentation.SourcePreview(detected, sourceAnalysisAvailable: false).Detail.Should().Contain("no source snapshot will be created");
        ProjectImportPresentation.SourcePreview(new ProjectImportSourceDetection()).Detail
            .Should().Be("This archive contains no source files that Source Analysis reads. Project documents can still be imported.");
    }
}
