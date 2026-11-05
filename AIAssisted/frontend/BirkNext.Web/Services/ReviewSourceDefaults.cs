using BirkNext.SourceEvidence;
using BirkNext.Web.Services.ProjectEvidence;

namespace BirkNext.Web.Services;

/// <summary>
/// The default current snapshot of a source review: the Target Application's main package (Project Evidence) when it is listed and holds the
/// review's evidence, otherwise the newest snapshot. A default only — an explicit selection always wins, and the main package never decides a
/// baseline. Never simply the package imported last (a shared library or an infrastructure repository).
/// </summary>
public static class ReviewSourceDefaults
{
    public static Guid? Current(IServiceProvider services, IReadOnlyList<ReviewSourceSnapshot> snapshots)
    {
        Guid? main = null;
        try { main = (services.GetService(typeof(IProjectEvidenceService)) as IProjectEvidenceService)?.Peek().Main?.SnapshotId; }
        catch (InvalidOperationException) { main = null; }
        return snapshots.FirstOrDefault(s => s.SnapshotId == main && s.HasConsumerEvidence)?.SnapshotId ?? snapshots.FirstOrDefault()?.SnapshotId;
    }
}
