using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Duplicate import: binary identity (same archive bytes) and content identity (same files in a different archive) are both detected and told
/// apart; a materially different archive is not a duplicate. Real case: "M2LB (3).zip" and "M2LB.zip" differ in bytes but hold identical files.</summary>
public sealed class ProjectImportContentDuplicateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Existing = Guid.NewGuid();
    private static readonly SddProjectImportRecord Record = new() { ImportId = "import-m2lb", ProjectName = "M2LB", ArchiveFileName = "M2LB.zip", ArchiveSha256 = "aaa", ImportedAt = T0, SourceSnapshotId = Existing };
    private static readonly SourceSetupView Setup = new() { Snapshots = [new SourceSetupSnapshot(Existing, "M2LB.zip", "aaa", T0, "Partial") { ContentFingerprint = "content-1", ImportId = "import-m2lb" }] };

    [Fact]
    public void The_same_bytes_are_an_exact_duplicate()
    {
        var d = ProjectImportHistory.Duplicate("aaa", [Record], null, Setup, "content-1")!;
        d.SameContentDifferentArchive.Should().BeFalse();
        d.ImportId.Should().Be("import-m2lb");
    }

    [Fact]
    public void A_different_archive_with_the_same_files_is_detected_as_same_content()
    {
        var d = ProjectImportHistory.Duplicate("bbb", [Record], null, Setup, "content-1")!;
        d.SameContentDifferentArchive.Should().BeTrue();
        d.ArchiveFileName.Should().Be("M2LB.zip");
        d.SnapshotId.Should().Be(Existing);
        d.ImportId.Should().Be("import-m2lb", "the existing import can be reused");
    }

    [Fact]
    public void Different_content_is_not_a_duplicate()
    {
        ProjectImportHistory.Duplicate("ccc", [Record], null, Setup, "content-2").Should().BeNull();
        ProjectImportHistory.Duplicate("ccc", [Record], null, Setup, null).Should().BeNull("an older backend without content fingerprints never guesses");
    }
}
