namespace BirkNext.Web.Services;

/// <summary>
/// Single root state manager for workspace.
/// All other state derives from workspace.
/// When workspace changes, all dependent state is invalidated.
/// </summary>
public interface IWorkspaceStateManager
{
    /// <summary>
    /// Get current workspace ID (null if no workspace loaded).
    /// </summary>
    Guid? CurrentWorkspaceId { get; }

    /// <summary>
    /// Notify that workspace has changed. Clears all dependent state.
    /// </summary>
    void NotifyWorkspaceChanged(Guid? newWorkspaceId);

    /// <summary>
    /// Check if a cached result is still valid for current workspace.
    /// </summary>
    bool IsValidForCurrentWorkspace(Guid? cachedWorkspaceId);

    /// <summary>
    /// Event fired when workspace changes. All consumers must clear their state.
    /// </summary>
    event Action<Guid?>? WorkspaceChanged;
}

public sealed class WorkspaceStateManager : IWorkspaceStateManager
{
    private Guid? _currentWorkspaceId;

    public Guid? CurrentWorkspaceId => _currentWorkspaceId;

    public event Action<Guid?>? WorkspaceChanged;

    public void NotifyWorkspaceChanged(Guid? newWorkspaceId)
    {
        if (_currentWorkspaceId == newWorkspaceId)
            return;

        _currentWorkspaceId = newWorkspaceId;
        WorkspaceChanged?.Invoke(newWorkspaceId);
    }

    public bool IsValidForCurrentWorkspace(Guid? cachedWorkspaceId)
    {
        // Null workspace ID means workspace was cleared
        if (_currentWorkspaceId == null)
            return false;

        // If cache is from a different workspace, it's stale
        if (cachedWorkspaceId != _currentWorkspaceId)
            return false;

        return true;
    }
}
