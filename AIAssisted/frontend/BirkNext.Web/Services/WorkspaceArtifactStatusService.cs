namespace BirkNext.Web.Services;

public sealed class WorkspaceArtifactStatusService : IWorkspaceArtifactStatusService, IDisposable
{
    private readonly IWorkspaceSessionService _workspace;
    private WorkspaceArtifactStatus? _cachedStatus;
    private string? _cachedProjectName;

    public event Action? StatusChanged;

    public WorkspaceArtifactStatusService(IWorkspaceSessionService workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public WorkspaceArtifactStatus GetStatus()
    {
        var hasConstitution = _workspace.Has(WorkspaceArtifactKind.Constitution);
        var hasSpecification = _workspace.Has(WorkspaceArtifactKind.Specification);
        var hasPlan = _workspace.Has(WorkspaceArtifactKind.Plan);
        var hasTasks = _workspace.Has(WorkspaceArtifactKind.Tasks);
        var hasDataModel = _workspace.Has(WorkspaceArtifactKind.DataModel);

        var artifactCount = 0;
        if (hasConstitution) artifactCount++;
        if (hasSpecification) artifactCount++;
        if (hasPlan) artifactCount++;
        if (hasTasks) artifactCount++;
        if (hasDataModel) artifactCount++;

        var projectName = _workspace.CurrentProject;

        var newStatus = new WorkspaceArtifactStatus(
            HasConstitution: hasConstitution,
            HasSpecification: hasSpecification,
            HasPlan: hasPlan,
            HasTasks: hasTasks,
            HasDataModel: hasDataModel,
            ArtifactCount: artifactCount,
            ActiveProjectName: projectName
        );

        // Only notify listeners if this is not the first call and status changed
        if (_cachedStatus != null && StatusHasChanged(newStatus))
        {
            _cachedStatus = newStatus;
            _cachedProjectName = projectName;
            StatusChanged?.Invoke();
        }
        else if (_cachedStatus == null)
        {
            // First call: cache without notifying
            _cachedStatus = newStatus;
            _cachedProjectName = projectName;
        }

        return newStatus;
    }

    private bool StatusHasChanged(WorkspaceArtifactStatus newStatus)
    {
        return _cachedStatus!.HasConstitution != newStatus.HasConstitution ||
               _cachedStatus.HasSpecification != newStatus.HasSpecification ||
               _cachedStatus.HasPlan != newStatus.HasPlan ||
               _cachedStatus.HasTasks != newStatus.HasTasks ||
               _cachedStatus.HasDataModel != newStatus.HasDataModel ||
               _cachedProjectName != newStatus.ActiveProjectName;
    }

    public void Dispose()
    {
        StatusChanged = null;
    }
}
