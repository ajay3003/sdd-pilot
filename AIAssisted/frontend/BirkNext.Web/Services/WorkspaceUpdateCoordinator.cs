namespace BirkNext.Web.Services;

/// <summary>
/// Implements batched workspace mutation coordination and event publishing.
/// Stateless: tracks only batch depth and mutation flag.
/// Does not depend on any other services.
/// </summary>
public sealed class WorkspaceUpdateCoordinator : IWorkspaceUpdateCoordinator
{
    private int _updateBatchDepth = 0;
    private bool _batchHasMutations = false;

    public event EventHandler? ArtifactsChanged;

    public void BeginUpdate()
    {
        _updateBatchDepth++;
    }

    public void EndUpdate()
    {
        if (_updateBatchDepth > 0)
            _updateBatchDepth--;

        // Fire event only when exiting outermost batch with mutations
        if (_updateBatchDepth == 0 && _batchHasMutations)
        {
            _batchHasMutations = false;
            OnArtifactsChanged();
        }
    }

    public void NotifyMutation()
    {
        if (_updateBatchDepth > 0)
        {
            // In batch: mark dirty, don't fire yet
            _batchHasMutations = true;
        }
        else
        {
            // Outside batch: fire immediately
            OnArtifactsChanged();
        }
    }

    private void OnArtifactsChanged()
    {
        ArtifactsChanged?.Invoke(this, EventArgs.Empty);
    }
}
