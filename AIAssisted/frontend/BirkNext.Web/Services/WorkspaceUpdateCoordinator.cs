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
        System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] BeginUpdate depth={_updateBatchDepth}");
    }

    public void EndUpdate()
    {
        System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] EndUpdate before decrement depth={_updateBatchDepth}, hasMutations={_batchHasMutations}");
        if (_updateBatchDepth > 0)
            _updateBatchDepth--;

        // Fire event only when exiting outermost batch with mutations
        if (_updateBatchDepth == 0 && _batchHasMutations)
        {
            _batchHasMutations = false;
            System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] EndUpdate firing ArtifactsChanged (subscriber count={ArtifactsChanged?.GetInvocationList().Length ?? 0})");
            OnArtifactsChanged();
            System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] ArtifactsChanged fired");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] EndUpdate NOT firing (depth={_updateBatchDepth}, mutations={_batchHasMutations})");
        }
    }

    public void NotifyMutation()
    {
        System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] NotifyMutation called, batchDepth={_updateBatchDepth}");
        if (_updateBatchDepth > 0)
        {
            // In batch: mark dirty, don't fire yet
            _batchHasMutations = true;
            System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] NotifyMutation marked _batchHasMutations=true");
        }
        else
        {
            // Outside batch: fire immediately
            System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] NotifyMutation firing immediately (not in batch)");
            OnArtifactsChanged();
        }
    }

    private void OnArtifactsChanged()
    {
        System.Diagnostics.Debug.WriteLine($"DIAG: [Coordinator] OnArtifactsChanged invoking subscribers");
        ArtifactsChanged?.Invoke(this, EventArgs.Empty);
    }
}
