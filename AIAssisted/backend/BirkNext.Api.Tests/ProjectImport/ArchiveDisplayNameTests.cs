using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using FluentAssertions;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>The archive display name keeps a readable file name; it is never a path, never carries markup, and never changes identity.</summary>
public sealed class ArchiveDisplayNameTests
{
    [Theory]
    [InlineData("M2LB (2).zip", "M2LB (2).zip")]
    [InlineData(@"C:\Users\someone\Downloads\M2LB (2).zip", "M2LB (2).zip")]
    [InlineData("/home/someone/R&D project #3.zip", "R&D project #3.zip")]
    [InlineData("../../escape.zip", "escape.zip")]
    [InlineData("<script>(1).zip", "_script_(1).zip")]
    [InlineData("   ", "archive.zip")]
    public void KeepsAReadableFileName_WithoutPathsOrMarkup(string uploaded, string expected) =>
        IqrSourceArchiveReader.ArchiveDisplayName(uploaded).Should().StartWith(expected.TrimEnd('_')).And.NotContain("/").And.NotContain("\\").And.NotContain("<");

    [Fact]
    public void TheValidatedWorkspace_UsesTheDisplayName_AndTheRepositoryKeyIsUnchanged()
    {
        var bytes = ProjectImportServiceTests.Zip(ProjectImportServiceTests.Source);

        var workspace = IqrSourceArchiveReader.ReadDetailed("M2LB (2).zip", bytes).Workspace!;

        workspace.Archive.FileName.Should().Be("M2LB (2).zip");
        SourceDependencyEvidenceExtractor.ArchiveRepositoryName(workspace.Archive.FileName).Should().Be("M2LB");
        SourceDependencyEvidenceExtractor.ArchiveRepositoryName("M2LB _2_.zip").Should().Be("M2LB", "snapshots stored with the old label still group with new ones");
    }
}
