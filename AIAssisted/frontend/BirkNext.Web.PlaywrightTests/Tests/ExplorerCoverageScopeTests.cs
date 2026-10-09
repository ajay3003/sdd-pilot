using System.Text.Json;
using BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

namespace BirkNext.Web.PlaywrightTests.Tests;

public sealed class ExplorerCoverageScopeTests
{
    [Fact]
    public void BaselineAggregationUsesOnlyConfiguredArchiveDocuments()
    {
        using var json = JsonDocument.Parse("""
            {
              "documents": [
                { "sourceOrigin": "BuiltInFixture", "sourceBlockCount": 99, "representedDirectlyCount": 99 },
                { "sourceOrigin": "ConfiguredArchive", "sourceBlockCount": 12, "representedDirectlyCount": 4, "representedStructurallyCount": 6, "intentionallyIgnoredCount": 2, "unsupportedCount": 0, "missingCount": 0 },
                { "sourceBlockCount": 500, "representedDirectlyCount": 500 }
              ]
            }
            """);

        var archiveDocuments = ExplorerCoverageScope.ArchiveOwnedDocuments(json.RootElement);
        var totals = ExplorerCoverageScope.Aggregate(archiveDocuments);

        Assert.Single(archiveDocuments);
        Assert.Equal(1, totals["explorer-coverage.documents"]);
        Assert.Equal(12, totals["explorer-coverage.blocks"]);
        Assert.Equal(4, totals["explorer-coverage.direct"]);
        Assert.Equal(6, totals["explorer-coverage.structured"]);
        Assert.Equal(2, totals["explorer-coverage.ignored"]);
        Assert.Equal(0, totals["explorer-coverage.unsupported"]);
        Assert.Equal(0, totals["explorer-coverage.missing"]);
    }
}
