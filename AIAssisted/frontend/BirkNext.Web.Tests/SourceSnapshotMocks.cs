using BirkNext.Dependencies;
using BirkNext.Integrations;
using BirkNext.Web.Services;
using Moq;

namespace BirkNext.Web.Tests;

/// <summary>
/// Stored Source Analysis snapshots as the API serves them: the list as summaries, one snapshot by id, and the dependency evidence of those
/// that have it — all from the same snapshots, so a test states its fixtures once.
/// </summary>
public static class SourceSnapshotMocks
{
    public static void SetupSourceSnapshots(this Mock<IIntegrationCatalogApiService> api, Func<IEnumerable<IqrSourceSnapshot>> snapshots, string? environmentId = null)
    {
        // A Moq matcher cannot sit inside ??: an exact environment and "any environment" are separate setups.
        var summaries = environmentId is null
            ? api.Setup(a => a.ListSourceSnapshotSummariesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            : api.Setup(a => a.ListSourceSnapshotSummariesAsync(environmentId, It.IsAny<CancellationToken>()));
        summaries.ReturnsAsync(() => snapshots().Select(IqrSourceSnapshotSummary.From).ToList());
        api.Setup(a => a.GetSourceSnapshotAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => snapshots().FirstOrDefault(s => s.Id == id));
        api.Setup(a => a.ListDependencySourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => snapshots().Where(s => s.DependencyEvidence is not null)
                .Select(s => new DependencySourceEvidence(s.Id, s.Archive.FileName, s.AnalyzedAt, s.DependencyEvidence!, s.Archive.Sha256)).ToList());
    }

    public static void SetupSourceSnapshots(this Mock<IIntegrationCatalogApiService> api, IReadOnlyList<IqrSourceSnapshot> snapshots, string? environmentId = null) =>
        api.SetupSourceSnapshots(() => snapshots, environmentId);

    public static void SetupSourceSnapshots(this Mock<ISddEvidenceApiService> api, IReadOnlyList<IqrSourceSnapshot> snapshots, string? environmentId = null)
    {
        var summaries = environmentId is null
            ? api.Setup(a => a.SourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            : api.Setup(a => a.SourceSnapshotsAsync(environmentId, It.IsAny<CancellationToken>()));
        summaries.ReturnsAsync(() => snapshots.Select(IqrSourceSnapshotSummary.From).ToList());
        api.Setup(a => a.SourceSnapshotAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => snapshots.FirstOrDefault(s => s.Id == id));
    }
}
