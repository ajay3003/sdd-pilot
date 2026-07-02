namespace BirkNext.Web.Services;

public sealed record WorkspaceArtifactStatus(
    bool HasConstitution,
    bool HasSpecification,
    bool HasPlan,
    bool HasTasks,
    bool HasDataModel,
    int ArtifactCount,
    string? ActiveProjectName)
{
    public IReadOnlyDictionary<WorkspaceArtifactKind, bool> Availability =>
        new Dictionary<WorkspaceArtifactKind, bool>
        {
            [WorkspaceArtifactKind.Constitution] = HasConstitution,
            [WorkspaceArtifactKind.Specification] = HasSpecification,
            [WorkspaceArtifactKind.Plan] = HasPlan,
            [WorkspaceArtifactKind.Tasks] = HasTasks,
            [WorkspaceArtifactKind.DataModel] = HasDataModel
        };

    public bool IsFullyLoaded => ArtifactCount == 5;
    public bool IsPartiallyLoaded => ArtifactCount > 0 && ArtifactCount < 5;
    public bool IsEmpty => ArtifactCount == 0;
}

public interface IWorkspaceArtifactStatusService
{
    WorkspaceArtifactStatus GetStatus();
    event Action? StatusChanged;
}
