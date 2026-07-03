namespace BirkNext.Web.Services;

/// <summary>
/// Coordinates workspace mutations and publishes a single ArtifactsChanged event per logical update.
/// Supports batching multiple mutations with BeginUpdate/EndUpdate.
/// Pages explicitly call NotifyMutation() after calling Repository.Set().
///
/// Usage:
/// - Single mutation: Repository.Set(...); Updates.NotifyMutation();
/// - Batch: Updates.BeginUpdate(); Repository.Set(...); Updates.NotifyMutation(); ... Updates.EndUpdate();
/// </summary>
public interface IWorkspaceUpdateCoordinator
{
    /// <summary>
    /// Begin a batched update. Supports nesting.
    /// </summary>
    void BeginUpdate();

    /// <summary>
    /// End a batched update.
    /// Fires ArtifactsChanged once if mutations occurred.
    /// </summary>
    void EndUpdate();

    /// <summary>
    /// Signal that a mutation occurred (Repository.Set/Clear was called).
    /// If in batch: marks dirty, no event.
    /// If not in batch: fires ArtifactsChanged immediately.
    /// </summary>
    void NotifyMutation();

    /// <summary>
    /// Fired when workspace mutations complete and should trigger subscribers.
    /// Single-artifact update: fires immediately after NotifyMutation.
    /// Batch update: fires once when EndUpdate completes with mutations.
    /// Subscribers: AutoSaveService, WorkflowReadinessService, Dashboard, etc.
    /// </summary>
    event EventHandler? ArtifactsChanged;
}
