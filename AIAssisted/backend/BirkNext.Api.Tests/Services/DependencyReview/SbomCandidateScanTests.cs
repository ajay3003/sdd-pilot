using BirkNext.Api.Services.DependencyReview;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.DependencyReview;

/// <summary>
/// Review SBOM first checks the selected Source Analysis snapshots for supported SBOM/lock files by name (snapshots keep paths, not content).
/// Source dependency declarations are never candidates; snapshots without a file inventory are reported as not checkable, not as "no SBOM".
/// </summary>
public sealed class SbomCandidateScanTests
{
    private static IqrSourceSnapshot Snapshot(string archive, bool complete, params string[] files) => new()
    {
        Id = Guid.NewGuid(), Archive = new SourceArchive(archive, new string('a', 64), 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T06:00:00Z"),
        TargetIndex = new SourceTargetIndex { FileInventoryComplete = complete, Files = [.. files.Select(f => new SourceFileTarget(f))] },
    };

    [Fact]
    public void SourceOnlyRepositoriesHaveNoCandidates_AndEveryCheckedSnapshotIsNamed()
    {
        var common = Snapshot("M2LB.Common.zip", true, "Directory.Packages.props", "renovate.json", "src/M2LB.Common/M2LB.Common.csproj", "global.json");
        var app = Snapshot("M2LB.zip", true, "Directory.Packages.props", "renovate.json", "Person/src/Person.Api/Person.Api.csproj", "Web/package.json", "Web/package-lock.json");

        var scan = DependencyReviewSourceScopeService.ScanForSbom([common, app]);

        scan.Candidates.Should().BeEmpty("csproj, Directory.Packages.props, renovate.json and package.json are source declarations, not SBOMs");
        scan.Checked.Select(c => (c.SnapshotId, c.FileInventoryComplete)).Should().Equal((common.Id, true), (app.Id, true));
        scan.Limitations.Should().BeEmpty();
    }

    [Fact]
    public void SupportedFilesAreListedPerSnapshot_InPathOrder()
    {
        var app = Snapshot("App.zip", true, "src/App/packages.lock.json", "artifacts/bom.json", "docs/sbom.spdx.json", "src/App/App.csproj");

        var scan = DependencyReviewSourceScopeService.ScanForSbom([app]);

        scan.Candidates.Select(c => (c.Path, c.Format)).Should().Equal(
            ("artifacts/bom.json", SbomFormat.CycloneDxJson), ("docs/sbom.spdx.json", SbomFormat.SpdxJson), ("src/App/packages.lock.json", SbomFormat.NuGetLockFile));
        scan.Candidates.Should().OnlyContain(c => c.SnapshotId == app.Id);
    }

    [Fact]
    public void MissingOrIncompleteFileInventoryIsALimitation_NotANegativeResult()
    {
        var old = Snapshot("Old.zip", false) with { TargetIndex = null };
        var partial = Snapshot("Partial.zip", false, "a.cs");

        var scan = DependencyReviewSourceScopeService.ScanForSbom([old, partial]);

        scan.Limitations.Should().HaveCount(2);
        scan.Limitations[0].Should().Contain("could not be checked");
        scan.Limitations[1].Should().Contain("incomplete");
    }
}
